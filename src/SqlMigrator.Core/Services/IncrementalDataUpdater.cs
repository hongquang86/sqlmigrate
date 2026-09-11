using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using SqlMigrator.Core.Interfaces;
using SqlMigrator.Core.Models;

namespace SqlMigrator.Core.Services
{
    /// <summary>
    /// Thực thi đồng bộ tăng dần dữ liệu nguồn → đích:
    ///  - <see cref="SyncKind.AppendNew"/>: chỉ chép các bản ghi được THÊM MỚI
    ///    (lọc theo MAX(identity) hoặc MAX(rowversion) của mốc baseline gần nhất).
    ///  - <see cref="SyncKind.Full"/>: làm mới từng bảng — xóa dữ liệu bảng đích rồi
    ///    chép lại toàn bộ từ nguồn (bao gồm bản ghi mới + bản ghi đã bị sửa).
    /// Sau khi xong, lưu baseline mới (giữ nguyên mốc cũ cho các bảng thất bại).
    /// </summary>
    public sealed class IncrementalDataUpdater
    {
        private readonly MigrationOptions _options;
        private readonly ILogger _logger;
        private readonly SyncBaselineStore _store;
        private readonly IConstraintManager _constraints;

        public IncrementalDataUpdater(MigrationOptions options, ILogger logger,
            SyncBaselineStore store, IConstraintManager constraints)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _constraints = constraints ?? throw new ArgumentNullException(nameof(constraints));
        }

        public async Task<SyncResult> UpdateAsync(SyncKind kind, CancellationToken ct = default,
            IProgress<MigrationProgress>? progress = null)
        {
            var sw = Stopwatch.StartNew();
            var result = new SyncResult();

            var baseline = await _store.LoadAsync(
                _options.SourceConnectionString, _options.DestinationConnectionString, ct).ConfigureAwait(false);
            if (baseline == null || baseline.Tables.Count == 0)
            {
                result.AddError("Chưa có mốc đồng bộ. Hãy chạy một lần 'Bắt đầu di chuyển' trước.");
                return result;
            }

            var excluded = new HashSet<string>(_options.ExcludeTables, StringComparer.OrdinalIgnoreCase);

            IReadOnlyList<CatalogTable> catalog;
            try
            {
                catalog = await SyncBaselineStore.QueryCatalogAsync(_options.SourceConnectionString, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                result.AddError("Không đọc được catalog nguồn: " + ex.Message);
                return result;
            }

            var catalogLookup = catalog.ToDictionary(t => t.PlainName, StringComparer.OrdinalIgnoreCase);

            // Chọn danh sách bảng xử lý trong đợt này.
            var tableResults = new List<SyncTableResult>();
            var constraintTables = new List<TableSchema>();
            var failedTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var processedBaselines = new Dictionary<string, TableBaseline>(StringComparer.OrdinalIgnoreCase);

            foreach (var pair in baseline.Tables)
            {
                if (ct.IsCancellationRequested) break;
                if (excluded.Contains(pair.Key)) continue;
                if (!catalogLookup.TryGetValue(pair.Key, out var current)) continue;

                var needed = kind == SyncKind.Full
                    ? true
                    : await HasNewRowsAsync(pair.Value, current, ct).ConfigureAwait(false);
                if (!needed)
                    continue;

                constraintTables.Add(new TableSchema { Schema = current.Schema, Name = current.Name });
                processedBaselines[pair.Key] = pair.Value;
            }

            if (constraintTables.Count == 0)
            {
                result.AddError(kind == SyncKind.AppendNew
                    ? "Không có bảng nào có dữ liệu mới để cập nhật (chọn nút này khi nguồn có phát sinh dữ liệu)."
                    : "Không có bảng nào để đồng bộ đầy đủ.");
                return result;
            }

            // Tắt ràng buộc/trigger/index theo tùy chọn, giống luồng di chuyển chính.
            var shouldDefer = _options.DisableForeignKeyConstraints || _options.DisableCheckConstraints;
            try
            {
                if (shouldDefer)
                {
                    progress?.Report(new MigrationProgress(5, "Đang vô hiệu hóa ràng buộc trên đích..."));
                    await _constraints.DisableConstraintsAsync(constraintTables, ct).ConfigureAwait(false);
                }
                if (_options.DisableTriggers)
                {
                    progress?.Report(new MigrationProgress(8, "Đang vô hiệu hóa trigger trên đích..."));
                    await _constraints.DisableTriggersAsync(constraintTables, ct).ConfigureAwait(false);
                }
                if (_options.DisableIndexesDuringLoad)
                {
                    progress?.Report(new MigrationProgress(10, "Đang vô hiệu hóa index phụ..."));
                    await _constraints.DisableSecondaryIndexesAsync(constraintTables, ct).ConfigureAwait(false);
                }

                var done = 0;
                foreach (var pair in processedBaselines)
                {
                    if (ct.IsCancellationRequested) break;
                    var current = catalogLookup[pair.Key];

                    try
                    {
                        var rows = await SyncTableAsync(kind, pair.Value, current, ct).ConfigureAwait(false);
                        result.RowsSynced += rows;
                        result.Tables = result.Tables.Concat(new[]
                        {
                            new SyncTableResult { PlainName = pair.Key, RowsSynced = rows }
                        }).ToArray();
                        _logger.LogInformation("Đồng bộ {Kind} '{T}': {Rows:n0} dòng.",
                            kind == SyncKind.AppendNew ? "bản ghi mới" : "đầy đủ", pair.Key, rows);
                    }
                    catch (Exception ex)
                    {
                        failedTables.Add(pair.Key);
                        var message = $"{pair.Key}: {ex.Message}";
                        result.Tables = result.Tables.Concat(new[]
                        {
                            new SyncTableResult { PlainName = pair.Key, Success = false, Error = ex.Message }
                        }).ToArray();
                        _logger.LogError(ex, "Đồng bộ thất bại cho {T}: {Message}", pair.Key, ex.Message);
                        if (_options.FailFast || !_options.ContinueOnNonCriticalErrors)
                            throw new InvalidOperationException($"Sync failed at {pair.Key}: {ex.Message}", ex);
                        result.AddError(message);
                    }

                    var currentIndex = Math.Min(90, 15 + (int)(70.0 * (++done) / processedBaselines.Count));
                    progress?.Report(new MigrationProgress(currentIndex, $"Đã xử lý {done}/{processedBaselines.Count} bảng…"));
                }
            }
            finally
            {
                try
                {
                    if (_options.DisableIndexesDuringLoad)
                    {
                        progress?.Report(new MigrationProgress(92, "Đang xây dựng lại index phụ..."));
                        await _constraints.RebuildSecondaryIndexesAsync(constraintTables, ct).ConfigureAwait(false);
                    }
                    if (_options.DisableTriggers)
                    {
                        progress?.Report(new MigrationProgress(95, "Đang bật lại trigger..."));
                        await _constraints.EnableTriggersAsync(constraintTables, ct).ConfigureAwait(false);
                    }
                    if (shouldDefer)
                    {
                        progress?.Report(new MigrationProgress(97, "Đang bật lại ràng buộc..."));
                        await _constraints.EnableConstraintsAsync(constraintTables, _options.CheckDataAfterLoad, ct)
                            .ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Khôi phục lại ràng buộc sau đồng bộ gặp lỗi: {Message}", ex.Message);
                }
            }

            // Lưu baseline mới; các bảng thất bại giữ nguyên mốc cũ để lần sau dò lại chính xác.
            var snapshot = await _store.CaptureAsync(_options, ct).ConfigureAwait(false);
            if (failedTables.Count > 0)
            {
                var fixedTables = new Dictionary<string, TableBaseline>(snapshot.Tables, StringComparer.OrdinalIgnoreCase);
                foreach (var failed in failedTables)
                {
                    if (baseline.Tables.TryGetValue(failed, out var old))
                        fixedTables[failed] = old;
                }
                snapshot.Tables = fixedTables;
            }

            await _store.SaveAsync(snapshot, _options.SourceConnectionString, _options.DestinationConnectionString, ct)
                .ConfigureAwait(false);

            sw.Stop();
            result.Elapsed = sw.Elapsed;
            progress?.Report(new MigrationProgress(100, "Hoàn tất đồng bộ dữ liệu."));
            return result;
        }

        private async Task<bool> HasNewRowsAsync(TableBaseline baseTable, CatalogTable current, CancellationToken ct)
        {
            var meter = await SyncBaselineStore.ReadMeterAsync(_options.SourceConnectionString, current, ct).ConfigureAwait(false);

            if (baseTable.MaxRowVersion != null && meter.MaxRowVersion > baseTable.MaxRowVersion)
                return true;

            if ((meter.MaxIdentity ?? 0L) > (baseTable.MaxIdentity ?? 0L))
                return true;

            return meter.Count != baseTable.RowCount;
        }

        private async Task<long> SyncTableAsync(SyncKind kind, TableBaseline baseTable, CatalogTable current,
            CancellationToken ct)
        {
            using var source = new SqlConnection(_options.SourceConnectionString);
            await source.OpenAsync(ct).ConfigureAwait(false);
            using var dest = new SqlConnection(_options.DestinationConnectionString);
            await dest.OpenAsync(ct).ConfigureAwait(false);

            var projection = BuildProjection(current, _options.PreserveIdentity);
            if (projection.Count == 0)
                return 0;

            var where = kind == SyncKind.AppendNew ? BuildAppendFilter(baseTable) : null;

            if (kind == SyncKind.Full)
            {
                using (var clear = new SqlCommand($"DELETE FROM {current.QualifiedName};", dest)
                {
                    CommandTimeout = _options.CommandTimeoutSeconds
                })
                {
                    await clear.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
                _logger.LogInformation("Đã xóa dữ liệu bảng đích '{T}' trước khi đồng bộ đầy đủ.", current.PlainName);
            }

            var select = "SELECT " + string.Join(", ", projection.Select(c => Quoting.QuoteIdentifier(c.Name))) +
                         " FROM " + current.QualifiedName;
            if (where != null)
                select += " WHERE " + where;

            using var cmd = new SqlCommand(select, source) { CommandTimeout = _options.CommandTimeoutSeconds };
            if (where != null)
            {
                var baselineValue = baseTable.RowVersionColumn != null && baseTable.MaxRowVersion != null
                    ? baseTable.MaxRowVersion.Value
                    : baseTable.MaxIdentity ?? 0L;
                cmd.Parameters.AddWithValue("@baseline", baselineValue);
            }

            using var reader = await cmd.ExecuteReaderAsync(System.Data.CommandBehavior.SequentialAccess, ct).ConfigureAwait(false);
            using var bulk = new SqlBulkCopy(dest, BuildBulkCopyOptions(), null)
            {
                DestinationTableName = current.QualifiedName,
                BatchSize = _options.BatchSize,
                EnableStreaming = _options.EnableStreaming
            };

            if (_options.BulkCopyTimeoutSeconds > 0)
                bulk.BulkCopyTimeout = _options.BulkCopyTimeoutSeconds;

            foreach (var column in projection)
                bulk.ColumnMappings.Add(column.Name, column.Name);

            // Identity insert chỉ bật khi thực sự chép cả cột identity (giữ nguyên ID khóa chính).
            var identityInsertOn = _options.PreserveIdentity && current.IdentityColumn != null &&
                                   projection.Any(c => c.IsIdentity);

            try
            {
                if (identityInsertOn)
                    await SetIdentityInsertAsync(dest, current, true, ct).ConfigureAwait(false);

                await bulk.WriteToServerAsync(reader, ct).ConfigureAwait(false);
                return bulk.RowsCopied;
            }
            catch (Exception ex)
            {
                // Dọn phần dữ liệu đổ dở nếu được phép (bảng đã trống sau khi clear ở chế độ Full).
                if (_options.CleanupPartialTableOnError)
                {
                    try
                    {
                        using var cleanup = new SqlCommand($"DELETE FROM {current.QualifiedName};", dest)
                        {
                            CommandTimeout = _options.CommandTimeoutSeconds
                        };
                        await cleanup.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                        _logger.LogWarning("Đã dọn dữ liệu đổ dở của '{T}' sau lỗi: {Message}",
                            current.PlainName, ex.Message);
                    }
                    catch (Exception cleanupEx)
                    {
                        _logger.LogWarning("Không dọn được '{T}' sau lỗi: {Message}", current.PlainName, cleanupEx.Message);
                    }
                }
                throw;
            }
            finally
            {
                if (identityInsertOn)
                    await SetIdentityInsertAsync(dest, current, false, ct).ConfigureAwait(false);
            }
        }

        private static async Task SetIdentityInsertAsync(SqlConnection conn, CatalogTable table, bool on, CancellationToken ct)
        {
            using var cmd = new SqlCommand(
                $"SET IDENTITY_INSERT {table.QualifiedName} {(on ? "ON" : "OFF")};", conn);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        /// <summary>Các cột được chép: bỏ computed và rowversion; bỏ identity nếu không giữ giá trị gốc.</summary>
        private static List<CatalogColumn> BuildProjection(CatalogTable table, bool preserveIdentity)
        {
            var result = new List<CatalogColumn>();
            foreach (var column in table.Columns)
            {
                if (column.IsComputed || column.IsRowVersion)
                    continue;
                if (column.IsIdentity && !preserveIdentity)
                    continue;
                result.Add(column);
            }
            return result;
        }

        /// <summary>Bộ lọc append-only: rowversion ưu tiên (bao trùm cả bản ghi thêm mới), nếu không có thì dùng identity.</summary>
        private string? BuildAppendFilter(TableBaseline baseTable)
        {
            if (baseTable.RowVersionColumn != null && baseTable.MaxRowVersion != null)
                return Quoting.QuoteIdentifier(baseTable.RowVersionColumn) + " > @baseline";
            if (baseTable.IdentityColumn != null && baseTable.MaxIdentity != null)
                return Quoting.QuoteIdentifier(baseTable.IdentityColumn) + " > @baseline";
            return null;
        }

        private SqlBulkCopyOptions BuildBulkCopyOptions()
        {
            var options = SqlBulkCopyOptions.TableLock;
            if (_options.UseInternalTransaction)
                options |= SqlBulkCopyOptions.UseInternalTransaction;
            if (_options.PreserveIdentity)
                options |= SqlBulkCopyOptions.KeepIdentity;
            if (!_options.DisableTriggers)
                options |= SqlBulkCopyOptions.FireTriggers;
            return options;
        }
    }
}