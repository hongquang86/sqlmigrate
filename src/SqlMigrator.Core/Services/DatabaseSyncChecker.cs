using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using SqlMigrator.Core.Models;

namespace SqlMigrator.Core.Services
{
    /// <summary>
    /// So sánh dữ liệu NGUỒN hiện tại với mốc baseline gần nhất để phát hiện
    /// "nguồn có dữ liệu mới không" và liệt kê từng bảng + số dòng mới.
    /// Chỉ đọc, không ghi gì trên hai server.
    /// </summary>
    public sealed class DatabaseSyncChecker
    {
        private readonly MigrationOptions _options;
        private readonly ILogger _logger;
        private readonly SyncBaselineStore _store;

        public DatabaseSyncChecker(MigrationOptions options, ILogger logger, SyncBaselineStore store)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _store = store ?? throw new ArgumentNullException(nameof(store));
        }

        public async Task<SyncCheckResult> CheckAsync(CancellationToken ct = default)
        {
            var result = new SyncCheckResult();
            var baseline = await _store.LoadAsync(
                _options.SourceConnectionString, _options.DestinationConnectionString, ct).ConfigureAwait(false);

            if (baseline == null || baseline.Tables.Count == 0)
            {
                result.HasBaseline = false;
                result.Warnings = new[]
                {
                    "Chưa có mốc đồng bộ cho cặp nguồn–đích này. Hãy chạy một lần 'Bắt đầu di chuyển' " +
                    "(chế độ Toàn bộ hoặc Chỉ dữ liệu) để tạo mốc, sau đó nút Cập nhật mới hoạt động."
                };
                return result;
            }

            result.HasBaseline = true;

            // Bảng bị người dùng loại trừ không được xét.
            var excluded = new HashSet<string>(_options.ExcludeTables, StringComparer.OrdinalIgnoreCase);

            IReadOnlyList<CatalogTable>? catalog = null;
            try
            {
                catalog = await SyncBaselineStore.QueryCatalogAsync(_options.SourceConnectionString, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                result.Errors = new[] { "Không đọc được catalog nguồn để dò thay đổi: " + ex.Message };
                return result;
            }

            var catalogLookup = catalog
                .ToDictionary(t => t.PlainName, StringComparer.OrdinalIgnoreCase);

            var changes = new List<SyncTableChange>();
            var warnings = new List<string>();

            foreach (var pair in baseline.Tables)
            {
                if (ct.IsCancellationRequested) break;
                if (excluded.Contains(pair.Key)) continue;
                if (!catalogLookup.TryGetValue(pair.Key, out var current))
                {
                    // Bảng từng có mốc nhưng giờ không còn ở nguồn → người dùng cần biết.
                    warnings.Add($"Bảng '{pair.Key}' có mốc đồng bộ nhưng không còn tồn tại trên nguồn.");
                    continue;
                }

                long? currentMaxId = null;
                long? currentMaxRv = null;
                long currentCount;
                try
                {
                    (currentCount, currentMaxId, currentMaxRv) = await SyncBaselineStore.ReadMeterAsync(
                        _options.SourceConnectionString, current, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    warnings.Add($"Không đo được '{pair.Key}': {ex.Message}");
                    continue;
                }

                var baseTable = pair.Value;
                var rvChanged = baseTable.MaxRowVersion != null && currentMaxRv > baseTable.MaxRowVersion;
                var idDelta = (currentMaxId ?? 0L) - (baseTable.MaxIdentity ?? 0L);
                var countChanged = currentCount != baseTable.RowCount;
                var countDelta = currentCount - baseTable.RowCount;

                var canDetectExact = (baseTable.RowVersionColumn != null && baseTable.MaxRowVersion != null) ||
                                     (baseTable.IdentityColumn != null && baseTable.MaxIdentity != null);

                long deltaCount;
                if (canDetectExact && pair.Value.RowVersionColumn != null && pair.Value.MaxRowVersion != null)
                {
                    // Đếm CHÍNH XÁC số dòng đã thêm/đổi tính từ mốc rowversion.
                    deltaCount = await CountExactNewRowsAsync(current, pair.Value.RowVersionColumn,
                        pair.Value.MaxRowVersion.Value, ct).ConfigureAwait(false);
                }
                else if (canDetectExact && pair.Value.IdentityColumn != null && pair.Value.MaxIdentity != null)
                {
                    deltaCount = Math.Max(0, idDelta);
                }
                else
                {
                    deltaCount = Math.Max(0, countDelta);
                }

                if (rvChanged || idDelta > 0 || countChanged)
                {
                    changes.Add(new SyncTableChange
                    {
                        Schema = current.Schema,
                        Name = current.Name,
                        DeltaCount = deltaCount,
                        CanDetectExactRows = canDetectExact,
                        LastRowVersionChanged = rvChanged,
                        RowCountChanged = countChanged,
                        Note = canDetectExact ? null : "Không có cột rowversion/identity nên chỉ ước lượng theo tổng số dòng."
                    });
                }
            }

            if (warnings.Count > 0)
                result.Warnings = warnings;
            if (changes.Count > 0)
            {
                result.Changes = changes;
                result.HasChanges = true;
                result.TotalNewRows = changes.Sum(c => c.DeltaCount);
            }

            _logger.LogInformation("Kiểm tra đồng bộ: {ChangedCount} bảng thay đổi, ~{Rows:N0} dòng mới.",
                changes.Count, result.TotalNewRows);
            return result;
        }

        private async Task<long> CountExactNewRowsAsync(CatalogTable table, string rowVersionColumn, long baseline,
            CancellationToken ct)
        {
            var sql = "SELECT COUNT_BIG(1) FROM " + table.QualifiedName +
                      " WHERE " + Quoting.QuoteIdentifier(rowVersionColumn) + " > @baseline;";
            using var conn = new SqlConnection(_options.SourceConnectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);
            using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 120 };
            cmd.Parameters.AddWithValue("@baseline", baseline);
            return (long)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L);
        }
    }
}