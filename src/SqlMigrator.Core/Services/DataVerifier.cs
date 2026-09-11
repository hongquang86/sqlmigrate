using System;
using System.Collections.Generic;
using System.Data;
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
    /// Kiểm tra toàn vẹn dữ liệu database đích so với nguồn sau khi di chuyển:
    ///  - luôn đếm chính xác (COUNT_BIG) cho từng bảng — không bao giờ dùng ước lượng;
    ///  - so checksum nội dung (CHECKSUM_AGG(BINARY_CHECKSUM(*))) theo dải keyset nếu bảng có
    ///    khóa chunk được (khóa ngắn, không giữ khóa dài), ngược lại scan toàn bảng;
    ///  - so sánh từng chunk để chỉ rõ khoảng khóa lệch, không chỉ báo "lệch" chung chung.
    /// Kết quả tổng hợp vào <see cref="DataVerificationReport"/> để UI và orchestrator dùng.
    /// </summary>
    public sealed class DataVerifier : IDataVerifier
    {
        private readonly MigrationOptions _options;
        private readonly ILogger _logger;

        public DataVerifier(MigrationOptions options, ILogger logger)
        {
            _options = options;
            _logger = logger;
        }

        /// <summary>Số dòng tối đa mỗi dải keyset khi kiểm tra (bound bộ nhớ & giữ khóa ngắn).</summary>
        internal const int VerifyChunkRows = 50000;

        public async Task<DataVerificationReport> VerifyAsync(SchemaModel schema, CancellationToken ct = default,
            IProgress<MigrationProgress>? progress = null)
        {
            var sw = Stopwatch.StartNew();
            var results = new List<TableVerificationResult>();

            // So sánh cấu trúc (số bảng/view/function/proc mỗi loại) giữa nguồn và đích;
            // không thất bại chung, chỉ ghi chú cho báo cáo.
            StructureCheckResult? structure = null;
            try
            {
                structure = await CompareStructureAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("So sánh cấu trúc nguồn/đích thất bại: {Message}", ex.Message);
            }

            var copyable = schema.Tables
                .Where(t => !t.IsSkipped && !t.IsExternal)
                .ToList();

            for (var i = 0; i < copyable.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var table = copyable[i];
                progress?.Report(new MigrationProgress(
                    10 + (int)(i * 80.0 / Math.Max(1, copyable.Count)),
                    string.Format("Đang kiểm tra toàn vẹn bảng {0}/{1}...", i + 1, copyable.Count)));

                TableVerificationResult r;
                try
                {
                    r = await VerifyTableAsync(table, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Kiểm tra toàn vẹn bảng {T} thất bại: {Message}", table.PlainName, ex.Message);
                    r = new TableVerificationResult
                    {
                        PlainName = table.PlainName,
                        Status = VerificationStatus.Failed,
                        Message = ex.Message
                    };
                }

                results.Add(r);
                LogVerificationResult(r);
            }

            sw.Stop();
            return BuildReport(results, sw.Elapsed, structure);
        }

        /// <summary>
        /// Đếm số đối tượng theo loại trên cả nguồn và đích rồi liệt kê đối tượng có ở
        /// nguồn nhưng thiếu trên đích (bảng, view, function, stored procedure, trigger).
        /// </summary>
        internal async Task<StructureCheckResult> CompareStructureAsync(CancellationToken ct)
        {
            var source = await CountObjectsAsync(_options.SourceConnectionString, ct).ConfigureAwait(false);
            var dest = await CountObjectsAsync(_options.DestinationConnectionString, ct).ConfigureAwait(false);

            var missing = new List<string>();
            foreach (var type in source.Keys)
            {
                if (!dest.ContainsKey(type))
                {
                    missing.Add(type);
                    continue;
                }

                var sourceObjects = source[type];
                var destObjects = dest[type];
                foreach (var obj in sourceObjects)
                {
                    if (!destObjects.Contains(obj))
                        missing.Add(obj);
                }
            }

            return new StructureCheckResult
            {
                SourceCounts = source.ToDictionary(k => k.Key, v => v.Value.Count),
                DestinationCounts = dest.ToDictionary(k => k.Key, v => v.Value.Count),
                MissingOnDestination = missing
            };
        }

        private static async Task<Dictionary<string, HashSet<string>>> CountObjectsAsync(string connectionString,
            CancellationToken ct)
        {
            const string query = @"
SELECT type_desc, ISNULL(OBJECT_SCHEMA_NAME(object_id), '') + '.' + name
  FROM sys.objects
 WHERE is_ms_shipped = 0
   AND type IN ('U','V','FN','IF','TF','P','TR')
 ORDER BY type_desc, OBJECT_SCHEMA_NAME(object_id), name;";

            var result = new Dictionary<string, HashSet<string>>();
            using var conn = SqlConnectionFactory.Open(connectionString, 120, ct);
            using var cmd = new SqlCommand(query, conn) { CommandTimeout = 120 };
            using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    var type = reader.GetString(0);
                    var name = reader.GetString(1);
                    if (!result.TryGetValue(type, out var set))
                        result[type] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    set.Add(name);
                }
            }

            return result;
        }

        private async Task<TableVerificationResult> VerifyTableAsync(TableSchema table, CancellationToken ct)
        {
            var result = new TableVerificationResult { PlainName = table.PlainName };

            using var source = SqlConnectionFactory.Open(_options.SourceConnectionString,
                _options.CommandTimeoutSeconds, ct);
            using var dest = SqlConnectionFactory.Open(_options.DestinationConnectionString,
                _options.CommandTimeoutSeconds, ct);

            if (!await TableExistsAsync(dest, table, ct).ConfigureAwait(false))
            {
                result.Status = VerificationStatus.MissingOnDestination;
                result.Message = "Bảng không tồn tại trên database đích.";
                return result;
            }

            result.SourceRowCount = await CountRowsAsync(source, table, ct).ConfigureAwait(false);
            result.DestinationRowCount = await CountRowsAsync(dest, table, ct).ConfigureAwait(false);

            if (result.SourceRowCount != result.DestinationRowCount)
            {
                result.Status = VerificationStatus.RowCountMismatch;
                result.Message = string.Format(
                    "Số dòng lệch: nguồn {0:n0} ≠ đích {1:n0}.",
                    result.SourceRowCount, result.DestinationRowCount);
                return result;
            }

            if (result.SourceRowCount == 0)
            {
                result.Status = VerificationStatus.Empty;
                return result;
            }

            var plan = TransferChunker.BuildPlan(table, preserveIdentity: false,
                useKeysetChunking: true, estimatedRows: result.SourceRowCount);

            if (plan.Strategy == TransferStrategy.KeysetChunked)
            {
                var mismatch = await CompareByKeysetAsync(source, dest, table, plan, result, ct).ConfigureAwait(false);
                result.Status = mismatch == null
                    ? VerificationStatus.VerifiedExact
                    : VerificationStatus.ContentMismatch;
                result.Message = mismatch;
                return result;
            }

            result.SourceChecksum = await ComputeChecksumAsync(source, table, ct).ConfigureAwait(false);
            result.DestinationChecksum = await ComputeChecksumAsync(dest, table, ct).ConfigureAwait(false);

            result.Status = result.SourceChecksum == result.DestinationChecksum
                ? VerificationStatus.VerifiedExact
                : VerificationStatus.ContentMismatch;

            if (result.Status == VerificationStatus.ContentMismatch)
                result.Message = "Cùng số dòng nhưng nội dung khác nhau trên toàn bảng.";
            return result;
        }

        /// <summary>
        /// So sánh từng dải keyset (WHERE key &gt; @mốc, TOP n) giữa nguồn và đích.
        /// Trả về thông điệp chi tiết ở dải lệch đầu tiên, hoặc null nếu mọi dải khớp.
        /// </summary>
        private async Task<string?> CompareByKeysetAsync(SqlConnection source, SqlConnection dest, TableSchema table,
            TransferTablePlan plan, TableVerificationResult result, CancellationToken ct)
        {
            var startKey = (string?)null;
            while (true)
            {
                ct.ThrowIfCancellationRequested();

                IReadOnlyList<string> keys;
                try
                {
                    keys = await ReadChunkKeysAsync(source, plan, startKey, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    return "Lỗi đọc dải khóa nguồn: " + ex.Message;
                }

                if (keys.Count == 0)
                    return null;

                var endKey = keys[keys.Count - 1];

                long srcCount;
                int? srcSum;
                long dstCount;
                int? dstSum;
                try
                {
                    (srcCount, srcSum) = await ReadChunkStatsAsync(source, plan, startKey, endKey, ct).ConfigureAwait(false);
                    (dstCount, dstSum) = await ReadChunkStatsAsync(dest, plan, startKey, endKey, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    return "Lỗi đọc dải [" + startKey + ".." + endKey + "]: " + ex.Message;
                }

                if (srcCount != dstCount)
                    return string.Format("Dải khóa [{0}..{1}]: số dòng lệch (nguồn {2:n0} ≠ đích {3:n0}).",
                        startKey ?? "đầu", endKey, srcCount, dstCount);

                if (srcSum != dstSum)
                    return string.Format("Dải khóa [{0}..{1}]: checksum nội dung khác nhau.",
                        startKey ?? "đầu", endKey);

                startKey = endKey;
            }
        }

        private async Task<IReadOnlyList<string>> ReadChunkKeysAsync(SqlConnection conn, TransferTablePlan plan,
            string? startKey, CancellationToken ct)
        {
            return await SqlRetry.WithRetryAsync(async token =>
            {
                var sql = TransferChunker.BuildChunkKeysSql(plan, VerifyChunkRows, startKey, out var keyParams);
                using var cmd = new SqlCommand(sql, conn) { CommandTimeout = _options.CommandTimeoutSeconds };
                cmd.Parameters.AddRange(keyParams);
                using var reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
                var list = new List<string>(Math.Min(VerifyChunkRows, 10000));
                while (await reader.ReadAsync(token).ConfigureAwait(false))
                    list.Add(TransferChunker.SerializeKey(plan.KeyKind, reader.GetValue(0)));
                return list;
            }, _logger, "Đọc khóa chunk " + plan.PlainName, ct).ConfigureAwait(false);
        }

        private async Task<(long Count, int? Checksum)> ReadChunkStatsAsync(SqlConnection conn,
            TransferTablePlan plan, string? startKey, string? endKey, CancellationToken ct)
        {
            const string sqlTpl =
                "SELECT COUNT_BIG(*) AS rn, CHECKSUM_AGG(BINARY_CHECKSUM(*)) AS cs FROM {0} WHERE {1};";

            var clauses = new List<string>();
            var keyExpr = KeyExpression(plan);

            if (!string.IsNullOrEmpty(startKey))
                clauses.Add(keyExpr + " > @start");
            if (!string.IsNullOrEmpty(endKey))
                clauses.Add(keyExpr + " <= @end");

            return await SqlRetry.WithRetryAsync(async token =>
            {
                using var cmd = new SqlCommand(
                    string.Format(sqlTpl, plan.QualifiedName, clauses.Count > 0 ? string.Join(" AND ", clauses) : "1=1"),
                    conn)
                { CommandTimeout = _options.CommandTimeoutSeconds };

                if (!string.IsNullOrEmpty(startKey))
                    cmd.Parameters.Add(new SqlParameter("@start", TransferChunker.SqlDbTypeFor(plan.KeyKind))
                    {
                        Value = TransferChunker.ParseKey(plan.KeyKind, startKey)
                    });
                if (!string.IsNullOrEmpty(endKey))
                    cmd.Parameters.Add(new SqlParameter("@end", TransferChunker.SqlDbTypeFor(plan.KeyKind))
                    {
                        Value = TransferChunker.ParseKey(plan.KeyKind, endKey)
                    });

                using var reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
                if (!await reader.ReadAsync(token).ConfigureAwait(false))
                    return ((long)0, (int?)null);

                var count = reader.GetInt64(0);
                var cs = reader.IsDBNull(1) ? (int?)null : reader.GetInt32(1);
                return (count, cs);
            }, _logger, "Đọc thống kê chunk " + plan.PlainName, ct).ConfigureAwait(false);
        }

        private async Task<bool> TableExistsAsync(SqlConnection conn, TableSchema table, CancellationToken ct)
        {
            const string sql = "SELECT CASE WHEN OBJECT_ID(@qname, 'U') IS NULL THEN 0 ELSE 1 END;";
            using var cmd = new SqlCommand(sql, conn) { CommandTimeout = _options.CommandTimeoutSeconds };
            cmd.Parameters.AddWithValue("@qname", table.QualifiedName);
            var value = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return Convert.ToInt32(value) == 1;
        }

        private async Task<long> CountRowsAsync(SqlConnection conn, TableSchema table, CancellationToken ct)
        {
            return await SqlRetry.WithRetryAsync(async token =>
            {
                using var cmd = new SqlCommand(
                    TransferChunker.BuildExactCountSql(table.QualifiedName), conn)
                { CommandTimeout = _options.CommandTimeoutSeconds };
                var value = await cmd.ExecuteScalarAsync(token).ConfigureAwait(false);
                return Convert.ToInt64(value);
            }, _logger, "Đếm dòng " + table.PlainName, ct).ConfigureAwait(false);
        }

        private async Task<int?> ComputeChecksumAsync(SqlConnection conn, TableSchema table, CancellationToken ct)
        {
            const string sql = "SELECT CHECKSUM_AGG(BINARY_CHECKSUM(*)) FROM {0};";
            return await SqlRetry.WithRetryAsync(async token =>
            {
                using var cmd = new SqlCommand(
                    string.Format(sql, table.QualifiedName), conn)
                { CommandTimeout = _options.CommandTimeoutSeconds };
                var value = await cmd.ExecuteScalarAsync(token).ConfigureAwait(false);
                return value is DBNull or null ? (int?)null : Convert.ToInt32(value);
            }, _logger, "Tính checksum " + table.PlainName, ct).ConfigureAwait(false);
        }

        internal static string KeyExpression(TransferTablePlan plan)
        {
            if (plan.KeyColumn == null)
                return "1=1";
            if (plan.KeyKind == TransferKeyKind.RowVersion)
                return "CONVERT(bigint, " + Quoting.QuoteIdentifier(plan.KeyColumn) + ")";
            return Quoting.QuoteIdentifier(plan.KeyColumn);
        }

        private static DataVerificationReport BuildReport(IReadOnlyList<TableVerificationResult> results,
            TimeSpan elapsed, StructureCheckResult? structure)
        {
            return new DataVerificationReport
            {
                Tables = results,
                Structure = structure,
                Elapsed = elapsed,
                VerifiedCount = results.Count(r => r.Status == VerificationStatus.VerifiedExact),
                IssueCount = results.Count(r => r.Status is VerificationStatus.RowCountMismatch
                    or VerificationStatus.ContentMismatch
                    or VerificationStatus.MissingOnDestination),
                FailedCount = results.Count(r => r.Status == VerificationStatus.Failed),
                SkippedCount = results.Count(r => r.Status == VerificationStatus.Skipped)
            };
        }

        private void LogVerificationResult(TableVerificationResult r)
        {
            var prefix = r.Status switch
            {
                VerificationStatus.VerifiedExact => "ĐÚNG ✓",
                VerificationStatus.Empty => "RỖNG",
                VerificationStatus.RowCountMismatch => "LỆCH SỐ DÒNG ✗",
                VerificationStatus.ContentMismatch => "LỆCH NỘI DUNG ✗",
                VerificationStatus.MissingOnDestination => "THIẾU BẢNG ✗",
                VerificationStatus.Failed => "LỖI ĐỌC ✗",
                VerificationStatus.Skipped => "BỎ QUA",
                _ => "?"
            };

            if (r.Status == VerificationStatus.VerifiedExact)
                _logger.LogInformation("  {Table}: {Status} ({Rows:n0} dòng)", r.PlainName, prefix, r.SourceRowCount);
            else
                _logger.LogWarning("  {Table}: {Status}. {Message}", r.PlainName, prefix, r.Message);
        }
    }
}