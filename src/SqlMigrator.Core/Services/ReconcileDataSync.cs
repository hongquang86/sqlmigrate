using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using SqlMigrator.Core.Models;

namespace SqlMigrator.Core.Services
{
    /// <summary>
    /// Đồng bộ dữ liệu nguồn → đích theo từng bảng đã chọn.
    /// Chỉ THÊM dòng thiếu + SỬA dòng lệch giá trị — KHÔNG BAO GIỜ xóa dòng đích.
    /// So khớp theo khóa ổn định (PK/identity 1 cột) theo chunk keyset; bảng không
    /// có khóa ổn định thì bỏ qua kèm lý do. Nguồn CHỈ ĐỌC; ghi chỉ trên đích,
    /// mỗi chunk một transaction (lỗi chunk nào dừng bảng đó, bảng khác vẫn chạy).
    /// </summary>
    public sealed class ReconcileDataSync
    {
        private const int ChunkRows = 2000;
        private readonly MigrationOptions _options;
        private readonly ILogger _logger;

        public ReconcileDataSync(MigrationOptions options, ILogger logger)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Đếm thử (không ghi gì): mỗi bảng sẽ thêm/sửa bao nhiêu dòng.
        /// Chạy 2-pass (đếm trước, sync sau) để user xem trước rồi mới OK.
        /// </summary>
        public async Task<IReadOnlyList<DataSyncPreviewItem>> PreviewAsync(
            SchemaModel schema,
            IReadOnlyList<string> tableNames,
            CancellationToken ct = default,
            IProgress<MigrationProgress>? progress = null)
        {
            var items = new List<DataSyncPreviewItem>();
            using var source = SqlConnectionFactory.Open(
                _options.SourceConnectionString, _options.CommandTimeoutSeconds, ct);
            using var dest = SqlConnectionFactory.Open(
                _options.DestinationConnectionString, _options.CommandTimeoutSeconds, ct);

            for (var i = 0; i < tableNames.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var tableName = tableNames[i];
                progress?.Report(new MigrationProgress(
                    (int)(i * 100.0 / Math.Max(1, tableNames.Count)),
                    $"Đang ước lượng {tableName} ({i + 1}/{tableNames.Count})..."));

                var item = new DataSyncPreviewItem { Table = tableName };
                try
                {
                    var table = FindTable(schema, tableName);
                    if (table == null)
                    {
                        item.Skipped = true;
                        item.SkipReason = "Không tìm thấy metadata bảng từ lần quét.";
                        items.Add(item);
                        continue;
                    }
                    if (!await DestTableExistsAsync(dest, table, ct).ConfigureAwait(false))
                    {
                        item.Skipped = true;
                        item.SkipReason = "Bảng chưa tồn tại trên đích — hãy tạo bảng trước.";
                        items.Add(item);
                        continue;
                    }
                    var plan = TryBuildSyncPlan(table, out var skipReason);
                    if (plan == null)
                    {
                        item.Skipped = true;
                        item.SkipReason = skipReason;
                        items.Add(item);
                        continue;
                    }

                    var counts = await WalkTableAsync(
                        source, dest, table, plan, dryRun: true, ct).ConfigureAwait(false);
                    item.SourceRows = counts.SourceRows;
                    item.DestRows = counts.DestRows;
                    item.WillInsert = counts.Inserts;
                    item.WillUpdate = counts.Updates;
                }
                catch (Exception ex)
                {
                    item.Skipped = true;
                    item.SkipReason = "Lỗi ước lượng: " + ex.Message;
                    _logger.LogWarning("Ước lượng sync bảng {T} thất bại: {Message}", tableName, ex.Message);
                }
                items.Add(item);
            }

            return items;
        }

        /// <summary>Sync thật các bảng đã chọn (ghi đích). Mỗi bảng lỗi thì dừng bảng đó.</summary>
        public async Task<DataSyncResult> SyncAsync(
            SchemaModel schema,
            IReadOnlyList<string> tableNames,
            CancellationToken ct = default,
            IProgress<MigrationProgress>? progress = null)
        {
            var sw = Stopwatch.StartNew();
            var results = new List<DataSyncTableResult>();

            using var source = SqlConnectionFactory.Open(
                _options.SourceConnectionString, _options.CommandTimeoutSeconds, ct);
            using var dest = SqlConnectionFactory.Open(
                _options.DestinationConnectionString, _options.CommandTimeoutSeconds, ct);

            for (var i = 0; i < tableNames.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var tableName = tableNames[i];
                var item = new DataSyncTableResult { Table = tableName };
                progress?.Report(new MigrationProgress(
                    (int)(i * 100.0 / Math.Max(1, tableNames.Count)),
                    $"Đang sync {tableName} ({i + 1}/{tableNames.Count})..."));

                try
                {
                    var table = FindTable(schema, tableName);
                    if (table == null)
                    {
                        item.Skipped = true;
                        item.SkipReason = "Không tìm thấy metadata bảng từ lần quét.";
                        results.Add(item);
                        continue;
                    }
                    if (!await DestTableExistsAsync(dest, table, ct).ConfigureAwait(false))
                    {
                        item.Skipped = true;
                        item.SkipReason = "Bảng chưa tồn tại trên đích — hãy tạo bảng trước.";
                        results.Add(item);
                        continue;
                    }
                    var plan = TryBuildSyncPlan(table, out var skipReason);
                    if (plan == null)
                    {
                        item.Skipped = true;
                        item.SkipReason = skipReason;
                        results.Add(item);
                        continue;
                    }

                    var counts = await WalkTableAsync(
                        source, dest, table, plan, dryRun: false, ct).ConfigureAwait(false);
                    item.Inserted = counts.Inserts;
                    item.Updated = counts.Updates;
                    _logger.LogInformation("Sync {T} xong: thêm {I:n0}, sửa {U:n0}.",
                        tableName, counts.Inserts, counts.Updates);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    item.Errors = new List<string> { ex.Message };
                    _logger.LogError(ex, "Sync bảng {T} thất bại.", tableName);
                }
                results.Add(item);
            }

            sw.Stop();
            var result = new DataSyncResult
            {
                Tables = results,
                Elapsed = sw.Elapsed,
                Success = results.All(r => r.Errors.Count == 0)
            };
            _logger.LogInformation("Sync dữ liệu xong sau {Elapsed}: thêm {I:n0}, sửa {U:n0}.",
                sw.Elapsed, result.TotalInserted, result.TotalUpdated);
            return result;
        }

        /// <summary>Dựng plan sync: keyset + giữ identity (để khớp và chép đúng khóa).</summary>
        internal static TransferTablePlan? TryBuildSyncPlan(TableSchema table, out string? skipReason)
        {
            var plan = TransferChunker.BuildPlan(
                table, preserveIdentity: true, useKeysetChunking: true, estimatedRows: 0);
            if (plan.Strategy != TransferStrategy.KeysetChunked
                || plan.KeyColumn == null
                || plan.KeyKind == TransferKeyKind.None)
            {
                skipReason = "Bảng không có khóa chính/identity 1 cột ổn định để đối chiếu theo dòng.";
                return null;
            }
            if (plan.KeyKind == TransferKeyKind.RowVersion)
            {
                skipReason = "Khóa rowversion thay đổi mỗi lần sửa — không dùng để đối chiếu.";
                return null;
            }
            var keyIdx = plan.Columns.ToList().FindIndex(c =>
                string.Equals(c, plan.KeyColumn, StringComparison.OrdinalIgnoreCase));
            if (keyIdx < 0)
            {
                skipReason = "Cột khóa không nằm trong danh sách cột chép.";
                return null;
            }
            skipReason = null;
            return plan;
        }

        /// <summary>
        /// Toàn bộ cột identity của bảng đích (chỉ SELECT catalog đích).
        /// Rỗng (không ném lỗi) khi đọc lỗi — khi đó coi như không có identity.
        /// </summary>
        internal static async Task<IReadOnlyList<string>> ReadDestIdentityColumnsAsync(
            SqlConnection dest, TableSchema table, CancellationToken ct)
        {
            var result = new List<string>();
            try
            {
                using var cmd = new SqlCommand(
                    "SELECT c.name FROM sys.columns c "
                    + "JOIN sys.tables t ON t.object_id = c.object_id "
                    + "JOIN sys.schemas s ON s.schema_id = t.schema_id "
                    + "WHERE s.name = @schema AND t.name = @table AND c.is_identity = 1;", dest)
                { CommandTimeout = 60 };
                cmd.Parameters.AddWithValue("@schema", table.Schema);
                cmd.Parameters.AddWithValue("@table", table.Name);
                using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    result.Add(reader.GetString(0));
            }
            catch
            {
                // Đọc lỗi thì trả rỗng (giữ hành vi cũ, lỗi thật sẽ hiện ở bulk).
            }
            return result;
        }

        /// <summary>
        /// Có cần bật IDENTITY_INSERT không: đích có cột identity NẰM TRONG
        /// danh sách cột sẽ chép. Thuần logic để dễ kiểm thử.
        /// </summary>
        internal static bool NeedsIdentityInsert(
            IReadOnlyList<string> destIdentityCols, IReadOnlyList<string> insertColumns)
        {
            foreach (var col in destIdentityCols)
            {
                if (insertColumns.Contains(col, StringComparer.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static TableSchema? FindTable(SchemaModel schema, string tableName) =>
            schema.Tables.FirstOrDefault(t =>
                string.Equals(t.PlainName, tableName, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Tên 2 phần không ngoặc cho OBJECT_ID (dùng PlainName "dbo.T" — KHÔNG dùng
        /// QualifiedName.Trim vì Trim ăn ký tự 2 đầu thành "dbo].[T" và OBJECT_ID luôn NULL).
        /// </summary>
        internal static string BuildObjectIdName(TableSchema table) =>
            table.Schema + "." + table.Name;

        private static async Task<bool> DestTableExistsAsync(
            SqlConnection dest, TableSchema table, CancellationToken ct)
        {
            using var cmd = new SqlCommand(
                "SELECT OBJECT_ID(@qname, 'U');", dest) { CommandTimeout = 60 };
            cmd.Parameters.AddWithValue("@qname", BuildObjectIdName(table));
            var value = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return value != null && value != DBNull.Value;
        }

        private sealed class WalkCounts
        {
            public long SourceRows;
            public long DestRows;
            public long Inserts;
            public long Updates;
        }

        /// <summary>
        /// Đi hết bảng theo chunk keyset: đọc nguồn + đích, diff, (đếm | áp dụng).
        /// dryRun=true chỉ đếm. Ném lỗi để bên gọi dừng bảng đó.
        /// </summary>
        private async Task<WalkCounts> WalkTableAsync(
            SqlConnection source, SqlConnection dest,
            TableSchema table, TransferTablePlan plan, bool dryRun, CancellationToken ct)
        {
            var counts = new WalkCounts();
            var keyIdx = plan.Columns.ToList().FindIndex(c =>
                string.Equals(c, plan.KeyColumn, StringComparison.OrdinalIgnoreCase));
            var updateSql = ReconcileSql.BuildUpdateSql(plan);
            var canUpdate = !string.IsNullOrEmpty(updateSql);
            // Nguồn sự thật cho IDENTITY_INSERT là catalog ĐÍCH (bảng đích có thể
            // khác metadata nguồn trong trường hợp lạ). Đọc một lần mỗi bảng.
            var destIdentityCols = await ReadDestIdentityColumnsAsync(
                dest, table, ct).ConfigureAwait(false);
            var identityOn = NeedsIdentityInsert(destIdentityCols, plan.Columns);
            _logger.LogInformation(
                "Sync {T}: metadata nguồn HasIdentity={HasId}; cột identity trên đích: {Cols} → IDENTITY_INSERT {State}.",
                table.PlainName, table.HasIdentity,
                destIdentityCols.Count > 0 ? string.Join(", ", destIdentityCols) : "(không có)",
                identityOn ? "BẬT" : "TẮT");

            string? lo = null;
            while (true)
            {
                ct.ThrowIfCancellationRequested();

                var keysSql = ReconcileSql.BuildKeysRangeSql(plan, lo, null, ChunkRows, out var keyParams);
                var keys = await ReadKeysAsync(source, keysSql, keyParams, plan, ct).ConfigureAwait(false);
                if (keys.Count == 0)
                    break;
                var endKey = keys[keys.Count - 1];

                var dataSql = TransferChunker.BuildChunkDataSql(plan, lo, endKey, out var srcDataParams);
                var srcByKey = await ReadRowsByKeyAsync(source, dataSql, srcDataParams, plan, keyIdx, ct)
                    .ConfigureAwait(false);
                // Dựng mảng SqlParameter RIÊNG cho lệnh đích: một SqlParameter chỉ thuộc
                // được một SqlParameterCollection, dùng chung sẽ ném
                // "already contained by another SqlParameterCollection".
                var destDataSql = TransferChunker.BuildChunkDataSql(plan, lo, endKey, out var destDataParams);
                var destRows = await ReadRowsListAsync(dest, destDataSql, destDataParams, plan, keyIdx, ct)
                    .ConfigureAwait(false);

                counts.SourceRows += srcByKey.Count;
                counts.DestRows += destRows.Count;

                var actions = ReconcileSql.DiffRows(srcByKey, destRows, skipUpdate: !canUpdate);
                if (dryRun)
                {
                    counts.Inserts += actions.Inserts.Count;
                    counts.Updates += actions.Updates.Count;
                }
                else
                {
                    using var tx = (SqlTransaction)await dest.BeginTransactionAsync(ct).ConfigureAwait(false);
                    try
                    {
                        long inserted = 0;
                        long updated = 0;
                        if (actions.Inserts.Count > 0)
                        {
                            inserted = await BulkInsertAsync(
                                dest, tx, table, plan, srcByKey, actions.Inserts,
                                identityOn, ct).ConfigureAwait(false);
                        }
                        if (actions.Updates.Count > 0 && canUpdate)
                        {
                            updated = await ApplyUpdatesAsync(
                                dest, tx, plan, updateSql, keyIdx,
                                actions.Updates, ct).ConfigureAwait(false);
                        }
                        await tx.CommitAsync(ct).ConfigureAwait(false);
                        counts.Inserts += inserted;
                        counts.Updates += updated;
                    }
                    catch
                    {
                        try { await tx.RollbackAsync(ct).ConfigureAwait(false); } catch { }
                        throw;
                    }
                }

                lo = endKey;
                if (keys.Count < ChunkRows)
                    break;
            }
            return counts;
        }

        private async Task<List<string>> ReadKeysAsync(
            SqlConnection conn, string sql, SqlParameter[] ps,
            TransferTablePlan plan, CancellationToken ct)
        {
            var keys = new List<string>();
            using var cmd = new SqlCommand(sql, conn) { CommandTimeout = _options.CommandTimeoutSeconds };
            cmd.Parameters.AddRange(ps);
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                keys.Add(TransferChunker.SerializeKey(plan.KeyKind, reader.GetValue(0)));
            return keys;
        }

        private async Task<Dictionary<string, object[]>> ReadRowsByKeyAsync(
            SqlConnection conn, string sql, SqlParameter[] ps,
            TransferTablePlan plan, int keyIdx, CancellationToken ct)
        {
            var dict = new Dictionary<string, object[]>(StringComparer.Ordinal);
            using var cmd = new SqlCommand(sql, conn) { CommandTimeout = _options.CommandTimeoutSeconds };
            cmd.Parameters.AddRange(ps);
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var values = new object[reader.FieldCount];
                reader.GetValues(values);
                dict[TransferChunker.SerializeKey(plan.KeyKind, values[keyIdx])] = values;
            }
            return dict;
        }

        private async Task<List<(string Key, object[] Values)>> ReadRowsListAsync(
            SqlConnection conn, string sql, SqlParameter[] ps,
            TransferTablePlan plan, int keyIdx, CancellationToken ct)
        {
            var rows = new List<(string Key, object[] Values)>();
            using var cmd = new SqlCommand(sql, conn) { CommandTimeout = _options.CommandTimeoutSeconds };
            cmd.Parameters.AddRange(ps);
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var values = new object[reader.FieldCount];
                reader.GetValues(values);
                rows.Add((TransferChunker.SerializeKey(plan.KeyKind, values[keyIdx]), values));
            }
            return rows;
        }

        private async Task<long> BulkInsertAsync(
            SqlConnection dest, SqlTransaction tx,
            TableSchema table, TransferTablePlan plan,
            Dictionary<string, object[]> srcByKey, List<string> insertKeys,
            bool identityOn, CancellationToken ct)
        {
            var dt = new DataTable();
            foreach (var c in plan.Columns)
                dt.Columns.Add(c, typeof(object));
            foreach (var key in insertKeys)
            {
                var values = srcByKey[key];
                var row = dt.NewRow();
                for (var i = 0; i < values.Length; i++)
                    row[i] = values[i] ?? DBNull.Value;
                dt.Rows.Add(row);
            }

            if (identityOn)
            {
                using var on = new SqlCommand(
                    $"SET IDENTITY_INSERT {table.QualifiedName} ON;", dest, tx)
                    { CommandTimeout = _options.CommandTimeoutSeconds };
                await on.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            try
            {
                using var bulk = new SqlBulkCopy(dest, SqlBulkCopyOptions.TableLock, tx)
                {
                    DestinationTableName = table.QualifiedName,
                    BatchSize = insertKeys.Count,
                    EnableStreaming = false
                };
                if (_options.BulkCopyTimeoutSeconds > 0)
                    bulk.BulkCopyTimeout = _options.BulkCopyTimeoutSeconds;
                foreach (var column in plan.Columns)
                    bulk.ColumnMappings.Add(column, column);
                await bulk.WriteToServerAsync(dt, ct).ConfigureAwait(false);
            }
            finally
            {
                if (identityOn)
                {
                    using var off = new SqlCommand(
                        $"SET IDENTITY_INSERT {table.QualifiedName} OFF;", dest, tx)
                        { CommandTimeout = _options.CommandTimeoutSeconds };
                    await off.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
            }
            return insertKeys.Count;
        }

        private async Task<long> ApplyUpdatesAsync(
            SqlConnection dest, SqlTransaction tx,
            TransferTablePlan plan, string updateSql, int keyIdx,
            List<(string Key, object[] Values)> updates, CancellationToken ct)
        {
            using var cmd = new SqlCommand(updateSql, dest, tx)
            {
                CommandTimeout = _options.CommandTimeoutSeconds
            };
            long count = 0;
            foreach (var (key, values) in updates)
            {
                ct.ThrowIfCancellationRequested();
                cmd.Parameters.Clear();
                var p = 0;
                for (var i = 0; i < plan.Columns.Count; i++)
                {
                    if (i == keyIdx)
                        continue;
                    cmd.Parameters.AddWithValue("@p" + p++, values[i] ?? DBNull.Value);
                }
                cmd.Parameters.Add(new SqlParameter("@pk", TransferChunker.SqlDbTypeFor(plan.KeyKind))
                {
                    Value = TransferChunker.ParseKey(plan.KeyKind, key)
                });
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                count++;
            }
            return count;
        }
    }
}
