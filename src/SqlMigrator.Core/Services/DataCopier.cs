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
    /// Đổ dữ liệu từ nguồn sang đích bằng engine chunk/parallel/resumable:
    ///  - bảng có khóa chunk được: đọc theo chunk keyset (WHERE key &gt; mốc, TOP n) nên
    ///    không giữ khóa đọc dài trên nguồn và có thể hồi tục sau khi bị đứt;
    ///  - nhiều bảng độc lập được sao chép song song (tối đa MaxParallelism luồng);
    ///  - bộ nhớ bị khóa cứng bởi MaxBufferMB, chia cho từng worker theo bề rộng dòng;
    ///  - mốc chunk được ghi vào file checkpoint để lần chạy sau tiếp tục từ nơi dừng.
    /// Bảng không có khóa dùng được → scan toàn bộ 1 luồng (vẫn streaming, có cảnh báo).
    /// </summary>
    public sealed class DataCopier : IDataCopier
    {
        private readonly MigrationOptions _options;
        private readonly ILogger _logger;
        private readonly TransferCheckpointStore _checkpointStore;
        private readonly SqlEnvironmentManager _env;

        public DataCopier(MigrationOptions options, ILogger logger)
        {
            _options = options;
            _logger = logger;
            _checkpointStore = new TransferCheckpointStore(logger);
            _env = new SqlEnvironmentManager(options, logger);
        }

        public async Task<DataCopyResult> CopyAsync(SchemaModel schema, CancellationToken ct = default,
            IProgress<MigrationProgress>? progress = null)
        {
            var sw = Stopwatch.StartNew();
            var result = new DataCopyResult();

            // Kiểm tra trạng thái RCSI nguồn (chỉ đọc) để ghi log; không đổi isolation gì thêm.
            // Trước đây từng SET TRANSACTION ISOLATION LEVEL SNAPSHOT ở đây nhưng đã bỏ
            // vì RCSI bật là đủ (READ COMMITTED tự dùng row versioning), còn SNAPSHOT
            // isolation đòi ALLOW_SNAPSHOT_ISOLATION riêng và làm sập cả đọc lẫn verify.
            await _env.EnsureReadCommittedSnapshotOnSourceAsync(ct).ConfigureAwait(false);
            await _env.PrepareDestinationRecoveryAsync(ct).ConfigureAwait(false);
            await _env.CheckDestinationLogUsageAsync(ct).ConfigureAwait(false);

            try
            {
                var copyable = schema.Tables.Where(t => !t.IsSkipped && !t.IsExternal).ToList();
                var estimatedRows = await ReadEstimatedRowsAsync(ct).ConfigureAwait(false);

                var jobs = new List<(TableSchema Table, TransferTablePlan Plan)>();
                foreach (var table in copyable)
                {
                    if (BuildProjection(table).Count == 0)
                    {
                        result.TablesSkipped++;
                        _logger.LogInformation("Bảng {T} không có cột chép được; bỏ qua.", table.PlainName);
                        continue;
                    }

                    estimatedRows.TryGetValue(table.PlainName, out var rows);
                    var plan = TransferChunker.BuildPlan(table, _options.PreserveIdentity,
                        _options.UseKeysetChunking, rows);
                    jobs.Add((table, plan));
                }

                if (jobs.Count == 0)
                {
                    sw.Stop();
                    result.Elapsed = sw.Elapsed;
                    return result;
                }

                TransferCheckpointFile? checkFile = null;
                if (_options.EnableTransferCheckpoint)
                {
                    checkFile = await _checkpointStore.LoadAsync(
                        _options.SourceConnectionString, _options.DestinationConnectionString, ct)
                        .ConfigureAwait(false)
                        ?? new TransferCheckpointFile
                        {
                            Key = TransferCheckpointStore.KeyOf(
                                _options.SourceConnectionString, _options.DestinationConnectionString)
                        };
                }

                var scheduler = new TransferScheduler(this, jobs, _options, _logger, checkFile,
                    progress, ct);
                var tableResults = scheduler.CopyAll();

                foreach (var r in tableResults)
                {
                    if (r.Skipped)
                    {
                        result.TablesSkipped++;
                    }
                    else if (r.Success)
                    {
                        result.TablesCopied++;
                        result.RowsCopied += r.RowsCopied;
                        result.BytesCopied += r.BytesCopied;
                    }
                    else
                    {
                        var message = r.PlainName + ": " + r.Error;
                        result.Errors = result.Errors.Concat(new[] { message }).ToArray();
                    }
                }

                sw.Stop();
                result.Elapsed = sw.Elapsed;
                if (result.Elapsed.TotalSeconds > 0)
                    result.BytesPerSecond = result.BytesCopied / result.Elapsed.TotalSeconds;
                else
                    result.BytesPerSecond = 0;

                if (result.Errors.Count > 0)
                    _logger.LogWarning("Sao chép dữ liệu có {Count} bảng lỗi; các bảng còn lại vẫn được xử lý.",
                        result.Errors.Count);

                _logger.LogInformation("Sao chép dữ liệu xong: {Copied} bảng, {Rows:n0} dòng ({Bytes:n0} KB), trong {Elapsed}.",
                    result.TablesCopied, result.RowsCopied, result.BytesCopied / 1024, sw.Elapsed);
                return result;
            }
            finally
            {
                // Luôn khôi phục recovery model gốc của đích, kể cả khi gặp lỗi.
                await _env.RestoreDestinationRecoveryAsync(ct).ConfigureAwait(false);
            }
        }

        /// <summary>Đọc ước lượng số dòng tất cả bảng trong một truy vấn (rẻ hơn COUNT_BIG từng bảng).</summary>
        private async Task<Dictionary<string, long>> ReadEstimatedRowsAsync(CancellationToken ct)
        {
            var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            if (!_options.UseEstimatedRowCounts)
                return result;

            try
            {
                return await SqlRetry.WithRetryAsync(async token =>
                {
                    using var conn = new SqlConnection(_options.SourceConnectionString);
                    await conn.OpenAsync(token).ConfigureAwait(false);
                    using var cmd = new SqlCommand(
                        "SELECT s.name, t.name, COALESCE(SUM(p.rows), 0) " +
                        "FROM sys.tables t " +
                        "JOIN sys.schemas s ON t.schema_id = s.schema_id " +
                        "JOIN sys.partitions p ON p.object_id = t.object_id AND p.index_id IN (0, 1) " +
                        "GROUP BY s.name, t.name;", conn)
                    { CommandTimeout = 120 };

                    using var reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
                    var map = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                    while (await reader.ReadAsync(token).ConfigureAwait(false))
                    {
                        var schema = reader.GetString(0);
                        var name = reader.GetString(1);
                        var rows = reader.GetInt64(2);
                        map[schema + "." + name] = rows;
                    }

                    return map;
                }, _logger, "Ước lượng số dòng các bảng", ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Không ước lượng được số dòng ({Message}); ETA sẽ không chính xác.", ex.Message);
                return result;
            }
        }

        // ----------------------------------------------------------------------------
        // Phần chép một bảng
        // ----------------------------------------------------------------------------

        private async Task<TransferTableResult> CopyTableAsync(TableSchema table, TransferTablePlan plan,
            TransferCheckpointFile? checkFile, CancellationToken ct)
        {
            var result = new TransferTableResult { PlainName = table.PlainName };

            var entry = checkFile?.Find(table.Schema, table.Name);

            // Resume an toàn: nếu chúng ta đang giữa chừng (chưa Completed), dữ liệu các
            // chunk trước đã nằm yên trên đích → KHÔNG xóa nữa, tiếp tục từ mốc LastKey.
            var resuming = entry != null && !entry.Completed && entry.Strategy == TransferStrategy.KeysetChunked;

            using var source = new SqlConnection(_options.SourceConnectionString);
            await source.OpenAsync(ct).ConfigureAwait(false);

            using var dest = new SqlConnection(_options.DestinationConnectionString);
            await dest.OpenAsync(ct).ConfigureAwait(false);

            // Xóa dữ liệu cũ trên đích (chỉ khi chạy mới, không trong lúc resume giữa chừng).
            if (!resuming)
            {
                if (_options.TruncateDestinationTables)
                    await ClearDestinationAsync(dest, table, ct).ConfigureAwait(false);
            }
            else
            {
                _logger.LogInformation("Tiếp tục bảng {T} từ mốc keyset {Key}; giữ nguyên dữ liệu đã chép.",
                    table.PlainName, entry!.LastKey ?? "đầu bảng");
            }

            var destRowsBefore = await ReadExistingRowCountAsync(dest, table, ct).ConfigureAwait(false);

            try
            {
                if (plan.Strategy != TransferStrategy.KeysetChunked)
                {
                    if (plan.Columns.Count == 0)
                    {
                        result.Success = true;
                        return result;
                    }
                    await CopyFullScanAsync(source, dest, table, plan, ct).ConfigureAwait(false);
                    result.Success = true;
                }
                else
                {
                    await CopyKeysetAsync(source, dest, table, plan, checkFile, entry, ct).ConfigureAwait(false);
                    result.Success = true;
                }

                var destRowsAfter = await ReadExistingRowCountAsync(dest, table, ct).ConfigureAwait(false);
                var copiedNow = Math.Max(0, destRowsAfter - (resuming ? destRowsBefore : 0));
                if (!resuming) copiedNow = destRowsAfter; // bảng trống trước khi đổ
                result.RowsCopied = copiedNow;
                result.BytesCopied = copiedNow * Math.Max(32, plan.EstimatedRowWidthBytes);
                _logger.LogInformation("Bảng {T}: đã có {Rows:n0} dòng trên đích.", table.PlainName, destRowsAfter);
                return result;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Error = ex.Message;

                // Dọn dữ liệu đổ dở — chỉ an toàn khi đích trống ban đầu hoặc ta đã xóa chủ động.
                var cleaned = false;
                if (_options.CleanupPartialTableOnError && (destRowsBefore == 0 || _options.TruncateDestinationTables || resuming))
                {
                    cleaned = (await TryCleanupPartialAsync(table, ct).ConfigureAwait(false)) >= 0;
                }

                // Nếu dọn sạch → bỏ mốc cũ để lần chạy sau chép lại từ đầu (không bỏ sót dòng).
                if (cleaned && checkFile != null && plan.Strategy == TransferStrategy.KeysetChunked)
                {
                    checkFile.Remove(table.Schema, table.Name);
                    await _checkpointStore.SaveAsync(checkFile, ct).ConfigureAwait(false);
                }

                _logger.LogWarning(
                    "Sao chép bảng {T} thất bại{Cleanup}: {Message}",
                    table.PlainName, cleaned ? " (đã dọn dữ liệu đổ dở)" : " (chưa dọn được dữ liệu đổ dở)", ex.Message);
                return result;
            }
        }

        private async Task CopyKeysetAsync(SqlConnection source, SqlConnection dest, TableSchema table,
            TransferTablePlan plan, TransferCheckpointFile? checkFile, TransferCheckpointEntry? entry,
            CancellationToken ct)
        {
            var chunkRows = ComputeChunkRows(plan);
            var lastKey = entry?.LastKey;
            var chunksDone = entry?.ChunksCompleted ?? 0;
            var rowsThisRun = 0L;
            var saveEvery = Math.Max(1, chunkRows >> 16) /* mỗi ~64k dòng ghi checkpoint */;
            var throttle = Math.Max(1, saveEvery / Math.Max(1, chunkRows));
            var lastSaveUtc = DateTime.UtcNow;
            var more = true;

            while (more)
            {
                if (ct.IsCancellationRequested)
                    throw new OperationCanceledException(ct);

                // 1) Đọc khóa đóng dải tiếp theo (seek trên index, rất rẻ).
                var keysSql = TransferChunker.BuildChunkKeysSql(plan, chunkRows, lastKey, out var keyParams);
                IReadOnlyList<string> keys;
                try
                {
                    keys = await SqlRetry.WithRetryAsync(async token =>
                    {
                        using var cmd = new SqlCommand(keysSql, source)
                        {
                            CommandTimeout = _options.CommandTimeoutSeconds
                        };
                        cmd.Parameters.AddRange(keyParams);
                        using var reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
                        var list = new List<string>(chunkRows);
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            list.Add(TransferChunker.SerializeKey(plan.KeyKind, reader.GetValue(0)));
                        return list;
                    }, _logger, "Đọc khóa chunk " + plan.PlainName, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Không đọc được khóa chunk bảng {T}: {Message}", plan.PlainName, ex.Message);
                    throw;
                }

                if (keys.Count == 0)
                {
                    var doneEntry = MarkCompleted(entry, plan, rowsThisRun, chunksDone);
                    if (checkFile != null)
                        await _checkpointStore.SaveAsync(checkFile, ct).ConfigureAwait(false);
                    break;
                }

                var isLast = keys.Count < chunkRows;
                var endKey = keys[keys.Count - 1];

                // 2) Đổ đúng dải (lastKey, endKey] — một batch, hồi tục theo endKey.
                var dataSql = TransferChunker.BuildChunkDataSql(plan, lastKey, endKey, out var dataParams);
                await SqlRetry.WithRetryAsync(async token =>
                {
                    using var cmd = new SqlCommand(dataSql, source)
                    {
                        CommandTimeout = _options.CommandTimeoutSeconds
                    };
                    cmd.Parameters.AddRange(dataParams);

                    using var reader = await cmd.ExecuteReaderAsync(System.Data.CommandBehavior.SequentialAccess, token)
                        .ConfigureAwait(false);
                    using var bulk = new SqlBulkCopy(dest, BuildCopyOptions(), null)
                    {
                        DestinationTableName = table.QualifiedName,
                        BatchSize = chunkRows,
                        EnableStreaming = _options.EnableStreaming,
                        // 0 = không giới hạn (theo docs SqlBulkCopy).
                        BulkCopyTimeout = _options.BulkCopyTimeoutSeconds
                    };
                    foreach (var column in plan.Columns)
                        bulk.ColumnMappings.Add(column, column);

                    var identityOn = table.HasIdentity && _options.PreserveIdentity;
                    if (identityOn)
                        await SetIdentityInsertAsync(dest, table, true, token).ConfigureAwait(false);
                    try
                    {
                        await bulk.WriteToServerAsync(reader, token).ConfigureAwait(false);
                    }
                    finally
                    {
                        if (identityOn)
                            await SetIdentityInsertAsync(dest, table, false, token).ConfigureAwait(false);
                    }
                }, _logger, "Đổ chunk dữ liệu " + plan.PlainName, ct).ConfigureAwait(false);

                rowsThisRun += keys.Count;
                chunksDone++;

                // 3) Ghi mốc checkpoint (có throttling để tránh churn đĩa với bảng quá lớn).
                if (checkFile != null)
                {
                    var doneEntry = EnsureEntry(entry, plan, rowsThisRun, chunksDone);
                    doneEntry.LastKey = endKey;
                    doneEntry.ChunksCompleted = chunksDone;
                    doneEntry.RowsCopied = rowsThisRun;

                    if (isLast)
                        doneEntry.Completed = true;

                    var now = DateTime.UtcNow;
                    if (isLast || chunksDone % throttle == 0 || (now - lastSaveUtc).TotalSeconds >= 2)
                    {
                        await _checkpointStore.SaveAsync(checkFile, ct).ConfigureAwait(false);
                        lastSaveUtc = now;
                    }
                }

                lastKey = endKey;
                if (isLast)
                {
                    if (checkFile != null)
                        await _checkpointStore.SaveAsync(checkFile, ct).ConfigureAwait(false);
                    break;
                }
            }
        }

        // Thêm biến phụ để tránh phải viết lại model — engine nội bộ đánh dấu chunk cuối.
        private static TransferCheckpointEntry EnsureEntry(TransferCheckpointEntry? entry,
            TransferTablePlan plan, long rowsThisRun, int chunksDone)
        {
            if (entry == null)
            {
                entry = new TransferCheckpointEntry
                {
                    Schema = plan.Schema,
                    Name = plan.Name,
                    Strategy = plan.Strategy,
                    KeyKind = plan.KeyKind,
                    ChunksCompleted = chunksDone,
                    RowsCopied = rowsThisRun
                };
            }

            return entry;
        }

        private TransferCheckpointEntry MarkCompleted(TransferCheckpointEntry? entry,
            TransferTablePlan plan, long rowsThisRun, int chunksDone)
        {
            var done = EnsureEntry(entry, plan, rowsThisRun, chunksDone);
            done.Completed = true;
            done.RowsCopied = rowsThisRun;
            done.ChunksCompleted = chunksDone;
            return done;
        }

        private async Task CopyFullScanAsync(SqlConnection source, SqlConnection dest, TableSchema table,
            TransferTablePlan plan, CancellationToken ct)
        {
            if (plan.Columns.Count == 0)
                return;

            _logger.LogWarning(
                "Bảng {T} không có khóa chunk được; sẽ scan toàn bộ 1 luồng (dữ liệu lớn có thể giữ khóa nguồn lâu).",
                plan.PlainName);

            var select = TransferChunker.BuildFullScanSql(plan);
            await SqlRetry.WithRetryAsync(async token =>
            {
                using var cmd = new SqlCommand(select, source)
                {
                    CommandTimeout = _options.CommandTimeoutSeconds
                };
                using var reader = await cmd.ExecuteReaderAsync(System.Data.CommandBehavior.SequentialAccess, token)
                    .ConfigureAwait(false);
                using var bulk = new SqlBulkCopy(dest, BuildCopyOptions(), null)
                {
                    DestinationTableName = table.QualifiedName,
                    BatchSize = Math.Max(1, OptionsBatchSize()),
                    EnableStreaming = _options.EnableStreaming,
                    // 0 = không giới hạn (theo docs SqlBulkCopy).
                    BulkCopyTimeout = _options.BulkCopyTimeoutSeconds
                };
                foreach (var column in plan.Columns)
                    bulk.ColumnMappings.Add(column, column);

                var identityOn = table.HasIdentity && _options.PreserveIdentity;
                if (identityOn)
                    await SetIdentityInsertAsync(dest, table, true, token).ConfigureAwait(false);
                try
                {
                    await bulk.WriteToServerAsync(reader, token).ConfigureAwait(false);
                }
                finally
                {
                    if (identityOn)
                        await SetIdentityInsertAsync(dest, table, false, token).ConfigureAwait(false);
                }
            }, _logger, "Scan toàn bộ bảng " + plan.PlainName, ct).ConfigureAwait(false);
        }

        /// <summary>Số dòng mỗi chunk = ngân sách RAM của worker chia bề rộng dòng (luôn bounded).</summary>
        private int ComputeChunkRows(TransferTablePlan plan)
        {
            var workerBudgetBytes = _options.MaxBufferMB * 1024L * 1024L / Math.Max(1, _options.EffectiveParallelism);
            var byBudget = workerBudgetBytes / Math.Max(32, plan.EstimatedRowWidthBytes);
            var cap = _options.ChunkRowCount > 0
                ? (long)_options.ChunkRowCount
                : 100_000L;
            return (int)Math.Clamp(byBudget > 0 ? Math.Min(byBudget, cap) : cap, 1000, 1_000_000);
        }

        private int OptionsBatchSize() => _options.BatchSize > 0 ? _options.BatchSize : 10000;

        private async Task<long> TryCleanupPartialAsync(TableSchema table, CancellationToken ct)
        {
            try
            {
                using var conn = new SqlConnection(_options.DestinationConnectionString);
                await conn.OpenAsync(ct).ConfigureAwait(false);
                using var cmd = new SqlCommand($"DELETE FROM {table.QualifiedName};", conn)
                {
                    CommandTimeout = _options.CommandTimeoutSeconds
                };
                var affected = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                _logger.LogInformation("Đã dọn {Deleted:n0} dòng đổ dở của bảng {T}.", affected, table.PlainName);
                return affected;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Không dọn được bảng {T} sau lỗi đổ dữ liệu: {Message}.", table.PlainName, ex.Message);
                return -1;
            }
        }

        private async Task ClearDestinationAsync(SqlConnection dest, TableSchema table, CancellationToken ct)
        {
            using var cmd = new SqlCommand($"DELETE FROM {table.QualifiedName};", dest)
            {
                CommandTimeout = _options.CommandTimeoutSeconds
            };
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            _logger.LogInformation("Đã xóa dữ liệu cũ khỏi {T} (data_only + truncate).", table.PlainName);
        }

        private static async Task SetIdentityInsertAsync(SqlConnection conn, TableSchema table, bool on, CancellationToken ct)
        {
            using var cmd = new SqlCommand(
                $"SET IDENTITY_INSERT {table.QualifiedName} {(on ? "ON" : "OFF")};", conn);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        private static async Task<long> ReadExistingRowCountAsync(SqlConnection conn, TableSchema table, CancellationToken ct)
        {
            using var cmd = new SqlCommand($"SELECT COUNT_BIG(*) FROM {table.QualifiedName};", conn)
            {
                CommandTimeout = 120
            };
            return (long)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L);
        }

        internal IReadOnlyList<ColumnSchema> BuildProjection(TableSchema table)
        {
            var result = new List<ColumnSchema>();
            foreach (var column in table.GetDataColumns(_options.PreserveIdentity))
            {
                if (column.IsRowVersion)
                    continue;
                result.Add(column);
            }

            return result;
        }

        private SqlBulkCopyOptions BuildCopyOptions()
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

        /// <summary>
        /// Thời gian chờ hiệu dụng của SqlBulkCopy (giây). 0 = không giới hạn, đúng
        /// semantics của SqlBulkCopy.BulkCopyTimeout — khi UI để 0 thì KHÔNG được để
        /// thuộc tính này rơi về mặc định 30 giây của driver.
        /// </summary>
        internal int EffectiveBulkCopyTimeoutSeconds => _options.BulkCopyTimeoutSeconds;

        // ----------------------------------------------------------------------------
        // Scheduler: song song nhiều bảng theo tầng phụ thuộc (DAG từ foreign key)
        // ----------------------------------------------------------------------------

        private sealed class TransferScheduler
        {
            private readonly DataCopier _owner;
            private readonly List<(TableSchema Table, TransferTablePlan Plan)> _jobs;
            private readonly MigrationOptions _options;
            private readonly ILogger _logger;
            private readonly TransferCheckpointFile? _checkFile;
            private readonly IProgress<MigrationProgress>? _progress;
            private readonly CancellationToken _ct;

            private readonly Dictionary<string, (TableSchema Table, TransferTablePlan Plan)> _byPlain;
            private readonly Dictionary<string, int> _indegree = new(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, List<string>> _children = new(StringComparer.OrdinalIgnoreCase);
            private readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);
            private readonly List<TransferTableResult> _results = new();
            private readonly object _gate = new();
            private CancellationTokenSource? _failFastCts;

            private long _doneRows;
            private long _doneBytes;
            private long _sampleRows;
            private long _sampleBytes;
            private DateTime _sampleAt = DateTime.UtcNow;
            private bool _circularWarned;

            public TransferScheduler(DataCopier owner, List<(TableSchema, TransferTablePlan)> jobs,
                MigrationOptions options, ILogger logger, TransferCheckpointFile? checkFile,
                IProgress<MigrationProgress>? progress, CancellationToken ct)
            {
                _owner = owner;
                _jobs = jobs;
                _options = options;
                _logger = logger;
                _checkFile = checkFile;
                _progress = progress;
                _ct = ct;

                _byPlain = _jobs.ToDictionary(j => j.Item1.PlainName, StringComparer.OrdinalIgnoreCase);
                foreach (var job in _jobs)
                {
                    _indegree[job.Item1.PlainName] = 0;
                    _children[job.Item1.PlainName] = new List<string>();
                    _pending.Add(job.Item1.PlainName);
                }

                foreach (var job in _jobs)
                {
                    foreach (var fk in job.Item1.ForeignKeys)
                    {
                        var parent = fk.ReferencedTableSchema + "." + fk.ReferencedTableName;
                        if (!_byPlain.ContainsKey(parent))
                            continue; // cha nằm ngoài danh sách di chuyển → không chặn
                        if (string.Equals(parent, job.Item1.PlainName, StringComparison.OrdinalIgnoreCase))
                            continue; // self-reference
                        _indegree[job.Item1.PlainName]++;
                        _children[parent].Add(job.Item1.PlainName);
                    }
                }
            }

            public IReadOnlyList<TransferTableResult> CopyAll()
            {
                _failFastCts = CancellationTokenSource.CreateLinkedTokenSource(_ct);
                var parallel = Math.Max(1, _options.EffectiveParallelism);

                var workers = new Task[parallel];
                for (var i = 0; i < parallel; i++)
                    workers[i] = Task.Run(() => WorkerLoop());

                Task.WhenAll(workers).GetAwaiter().GetResult();

                if (_failFastCts.IsCancellationRequested && !_ct.IsCancellationRequested)
                    throw new InvalidOperationException(
                        "Sao chép dữ liệu bị hủy do một bảng lỗi và bật chế độ dừng ngay (FailFast).");

                return _results.ToList();
            }

            private void WorkerLoop()
            {
                var cts = _failFastCts!;
                while (TryTakeNext(out var job) && !cts.IsCancellationRequested)
                {
                    var result = _owner.CopyTableAsync(job.Item1, job.Item2, _checkFile,
                        cts.Token).GetAwaiter().GetResult();

                    lock (_gate)
                    {
                        _results.Add(result);
                        _doneRows += result.RowsCopied;
                        _doneBytes += result.BytesCopied;
                    }

                    if (!result.Success)
                    {
                        _logger.LogError("Bảng {T} chép thất bại: {Error}", job.Item1.PlainName, result.Error);
                        if (_options.FailFast)
                        {
                            cts.Cancel();
                            break;
                        }
                    }
                    else
                    {
                        Finish(job.Item1.PlainName);
                    }

                    PulseProgress();
                }
            }

            private bool TryTakeNext(out (TableSchema Table, TransferTablePlan Plan) job)
            {
                lock (_gate)
                {
                    if (_pending.Count == 0)
                    {
                        job = default;
                        return false;
                    }

                    var key = _pending.FirstOrDefault(k => _indegree[k] == 0);
                    if (key == null)
                    {
                        if (!_circularWarned)
                        {
                            _circularWarned = true;
                            _logger.LogWarning(
                                "Có nhóm foreign key vòng giữa các bảng; xử lý tuần tự theo thứ tự khai báo.");
                        }

                        key = _pending.First();
                    }

                    _pending.Remove(key);
                    job = _byPlain[key];
                    return true;
                }
            }

            private void Finish(string plainName)
            {
                lock (_gate)
                {
                    if (!_children.TryGetValue(plainName, out var children))
                        return;
                    foreach (var child in children)
                    {
                        if (_indegree.TryGetValue(child, out var d) && d > 0)
                            _indegree[child] = d - 1;
                    }
                }
            }

            private void PulseProgress()
            {
                if (_progress == null)
                    return;

                lock (_gate)
                {
                    var now = DateTime.UtcNow;
                    var elapsed = (now - _sampleAt).TotalSeconds;
                    if (elapsed < 1.0)
                        return;

                    var rowsDelta = _doneRows - _sampleRows;
                    var bytesDelta = _doneBytes - _sampleBytes;
                    var rate = bytesDelta / elapsed;
                    _sampleRows = _doneRows;
                    _sampleBytes = _doneBytes;
                    _sampleAt = now;

                    var totalEstimate = _byPlain.Values.Sum(v => Math.Max(0, v.Plan.EstimatedRows));
                    var percent = totalEstimate > 0
                        ? 70 + (int)Math.Clamp(_doneRows * 20.0 / totalEstimate, 0, 20)
                        : 78;

                    var msg = $"Đang sao chép dữ liệu: {_doneRows:n0} dòng • {rate / 1024.0 / 1024.0:0.0} MB/s";
                    if (totalEstimate > 0)
                    {
                        var remaining = Math.Max(0, totalEstimate - _doneRows);
                        var remainingBytes = remaining * (_doneRows > 0
                            ? (double)_doneBytes / _doneRows
                            : 128.0);
                        if (rate > 0 && remainingBytes > 0)
                        {
                            var etaSeconds = remainingBytes / rate;
                            msg += $" • còn khoảng {TimeSpan.FromSeconds(etaSeconds):h\\h\\ mm\\m}";
                        }
                    }

                    _progress.Report(new MigrationProgress(percent, msg));
                }
            }
        }
    }

    /// <summary>Orders tables so parents are copied before children (topological sort).</summary>
    internal static class DataOrderer
    {
        public static IReadOnlyList<TableSchema> OrderByDependencies(IReadOnlyList<TableSchema> tables)
        {
            var keyOf = (TableSchema t) => t.PlainName.ToLowerInvariant();
            var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < tables.Count; i++)
                index[keyOf(tables[i])] = i;

            var graph = new List<List<int>>(tables.Count);
            var indegree = new int[tables.Count];
            for (var i = 0; i < tables.Count; i++)
                graph.Add(new List<int>());

            for (var i = 0; i < tables.Count; i++)
            {
                foreach (var fk in tables[i].ForeignKeys)
                {
                    var parentKey = (fk.ReferencedTableSchema + "." + fk.ReferencedTableName).ToLowerInvariant();
                    if (index.TryGetValue(parentKey, out var parent))
                    {
                        if (parent == i) continue; // self-reference
                        graph[parent].Add(i);
                        indegree[i]++;
                    }
                }
            }

            var visited = new bool[tables.Count];
            var result = new List<TableSchema>(tables.Count);
            var queue = new Queue<int>();
            for (var i = 0; i < tables.Count; i++)
            {
                if (indegree[i] == 0)
                    queue.Enqueue(i);
            }

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (visited[current]) continue;
                visited[current] = true;
                result.Add(tables[current]);

                foreach (var child in graph[current])
                {
                    indegree[child]--;
                    if (indegree[child] == 0)
                        queue.Enqueue(child);
                }
            }

            // Append any unvisited members (circular FK groups) in their original order.
            for (var i = 0; i < tables.Count; i++)
            {
                if (!visited[i])
                    result.Add(tables[i]);
            }

            return result;
        }
    }
}