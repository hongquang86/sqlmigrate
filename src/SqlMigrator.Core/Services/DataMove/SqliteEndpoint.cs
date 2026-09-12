using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using SqlMigrator.Core.Interfaces;
using SqlMigrator.Core.Models;

namespace SqlMigrator.Core.Services.DataMove
{
    /// <summary>
    /// Endpoint SQLite (file local) cho di chuyển chéo engine: đọc schema qua
    /// sqlite_master/PRAGMA, đọc chunk LIMIT/OFFSET, ghi multi-row INSERT.
    /// Không có server nên probe.Host chính là đường dẫn file .db.
    /// </summary>
    public sealed class SqliteEndpoint : IDbEndpoint
    {
        public DatabaseEngine Engine => DatabaseEngine.Sqlite;

        internal static string BuildConnectionString(DbProbe probe, bool readOnly = false)
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = probe.Host,
                Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate
            };
            return builder.ConnectionString;
        }

        public async Task<CanonicalSchema> ReadSchemaAsync(DbProbe probe, CancellationToken ct = default)
        {
            var tables = new List<CanonicalTable>();
            using var conn = new SqliteConnection(BuildConnectionString(probe, readOnly: true));
            await conn.OpenAsync(ct).ConfigureAwait(false);

            var names = new List<string>();
            using (var cmd = new SqliteCommand(
                "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' "
                + "ORDER BY name;", conn))
            using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    names.Add(reader.GetString(0));
            }

            foreach (var name in names)
            {
                ct.ThrowIfCancellationRequested();
                tables.Add(await ReadTableAsync(conn, name, ct).ConfigureAwait(false));
            }
            return new CanonicalSchema { Tables = tables };
        }

        private static async Task<CanonicalTable> ReadTableAsync(
            SqliteConnection conn, string name, CancellationToken ct)
        {
            var columns = new List<CanonicalColumn>();
            var pkOrder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            using (var cmd = new SqliteCommand($"PRAGMA table_info({Quote(name)});", conn))
            using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    // cid, name, type, notnull, dflt_value, pk
                    var colName = reader.GetString(1);
                    var declType = reader.IsDBNull(2) ? "" : reader.GetString(2);
                    var spec = DbTypeMappers.ParseSqlite(declType);
                    var pkPos = reader.GetInt32(5);
                    if (pkPos > 0)
                        pkOrder[colName] = pkPos;
                    columns.Add(new CanonicalColumn
                    {
                        Name = colName,
                        Type = spec.Type,
                        MaxLength = spec.MaxLength,
                        Precision = spec.Precision,
                        Scale = spec.Scale,
                        IsNullable = reader.GetInt32(3) == 0,
                        IsPrimaryKey = pkPos > 0,
                        DefaultSql = reader.IsDBNull(4) ? null : reader.GetString(4)
                    });
                }
            }

            var pk = new List<string>();
            foreach (var entry in new SortedDictionary<int, string>(
                pkOrder.ToDictionary(kv => kv.Value, kv => kv.Key)))
                pk.Add(entry.Value);

            return new CanonicalTable
            {
                Schema = null,
                Name = name,
                Columns = columns,
                PrimaryKeyColumns = pk,
                ForeignKeys = await ReadForeignKeysAsync(conn, name, ct).ConfigureAwait(false)
            };
        }

        private static async Task<List<CanonicalForeignKey>> ReadForeignKeysAsync(
            SqliteConnection conn, string name, CancellationToken ct)
        {
            // Gom theo id, giữ thứ tự seq.
            var groups = new Dictionary<long, (List<string> Cols, string RefTable, List<string> RefCols)>();
            var order = new List<long>();
            using (var cmd = new SqliteCommand($"PRAGMA foreign_key_list({Quote(name)});", conn))
            using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                // id, seq, table, from, to, on_update, on_delete, match
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    var id = reader.GetInt64(0);
                    if (!groups.TryGetValue(id, out var g))
                    {
                        g = (new List<string>(), reader.GetString(2), new List<string>());
                        groups[id] = g;
                        order.Add(id);
                    }
                    g.Cols.Add(reader.GetString(3));
                    g.RefCols.Add(reader.GetString(4));
                }
            }
            var result = new List<CanonicalForeignKey>();
            foreach (var id in order)
            {
                var g = groups[id];
                result.Add(new CanonicalForeignKey
                {
                    Columns = g.Cols,
                    RefTable = g.RefTable,
                    RefColumns = g.RefCols
                });
            }
            return result;
        }

        public async Task<bool> TableExistsAsync(DbProbe probe, string? schema, string table,
            CancellationToken ct = default)
        {
            if (!File.Exists(probe.Host))
                return false;
            using var conn = new SqliteConnection(BuildConnectionString(probe, readOnly: true));
            await conn.OpenAsync(ct).ConfigureAwait(false);
            using var cmd = new SqliteCommand(
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @t;", conn);
            cmd.Parameters.AddWithValue("@t", table);
            return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false)) > 0;
        }

        public async Task ExecuteDdlAsync(DbProbe probe, string ddl, CancellationToken ct = default)
        {
            using var conn = new SqliteConnection(BuildConnectionString(probe));
            await conn.OpenAsync(ct).ConfigureAwait(false);
            using var cmd = new SqliteCommand(ddl, conn) { CommandTimeout = 600 };
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        public async Task DropTableIfExistsAsync(DbProbe probe, string? schema, string table,
            bool cascade, CancellationToken ct = default)
        {
            using var conn = new SqliteConnection(BuildConnectionString(probe));
            await conn.OpenAsync(ct).ConfigureAwait(false);
            using var cmd = new SqliteCommand($"DROP TABLE IF EXISTS {Quote(table)};", conn)
            { CommandTimeout = 600 };
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        public async Task<long> CountRowsAsync(DbProbe probe, string? schema, string table,
            CancellationToken ct = default)
        {
            using var conn = new SqliteConnection(BuildConnectionString(probe, readOnly: true));
            await conn.OpenAsync(ct).ConfigureAwait(false);
            using var cmd = new SqliteCommand($"SELECT COUNT(*) FROM {Quote(table)};", conn)
            { CommandTimeout = 600 };
            return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
        }

        internal static string BuildSelectBatch(
            CanonicalTable table, string? afterKeyExclusive, int batchSize)
        {
            var cols = string.Join(", ", ColumnList(table));
            if (table.PrimaryKeyColumns.Count == 1)
            {
                var key = table.PrimaryKeyColumns[0];
                var sql = $"SELECT {cols} FROM {Quote(table.Name)}";
                if (!string.IsNullOrEmpty(afterKeyExclusive))
                    sql += $" WHERE {Quote(key)} > @k";
                sql += $" ORDER BY {Quote(key)} LIMIT @n;";
                return sql;
            }
            var offset = 0;
            int.TryParse(afterKeyExclusive, out offset);
            return $"SELECT {cols} FROM {Quote(table.Name)} LIMIT @n OFFSET @skip;";
        }

        private static List<string> ColumnList(CanonicalTable table)
        {
            var cols = new List<string>();
            foreach (var c in table.Columns)
                cols.Add(Quote(c.Name));
            return cols;
        }

        private static string Quote(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";

        public async Task<IReadOnlyList<object?[]>> ReadBatchAsync(
            DbProbe probe, CanonicalTable table, string? afterKeyExclusive, int batchSize,
            CancellationToken ct = default)
        {
            var rows = new List<object?[]>();
            var sql = BuildSelectBatch(table, afterKeyExclusive, batchSize);
            using var conn = new SqliteConnection(BuildConnectionString(probe, readOnly: true));
            await conn.OpenAsync(ct).ConfigureAwait(false);
            using var cmd = new SqliteCommand(sql, conn) { CommandTimeout = 600 };
            if (table.PrimaryKeyColumns.Count == 1)
            {
                if (!string.IsNullOrEmpty(afterKeyExclusive))
                    cmd.Parameters.AddWithValue("@k", ParseKey(table, afterKeyExclusive));
                cmd.Parameters.AddWithValue("@n", batchSize);
            }
            else
            {
                var offset = 0;
                int.TryParse(afterKeyExclusive, out offset);
                cmd.Parameters.AddWithValue("@n", batchSize);
                cmd.Parameters.AddWithValue("@skip", offset);
            }
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var values = new object?[reader.FieldCount];
                reader.GetValues(values);
                for (var i = 0; i < values.Length; i++)
                {
                    if (values[i] is DBNull)
                        values[i] = null;
                    else
                        values[i] = CanonicalValues.Normalize(values[i]);
                }
                rows.Add(values);
            }
            return rows;
        }

        internal static object ParseKey(CanonicalTable table, string key)
        {
            var keyType = CanonicalType.String;
            foreach (var c in table.Columns)
            {
                if (c.Name.Equals(table.PrimaryKeyColumns[0], StringComparison.OrdinalIgnoreCase))
                {
                    keyType = c.Type;
                    break;
                }
            }
            return keyType switch
            {
                CanonicalType.Int16 or CanonicalType.Int32 or CanonicalType.Int64
                    => long.TryParse(key, System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out var l) ? l : key,
                CanonicalType.Guid => Guid.TryParse(key, out var g) ? (object)g : key,
                _ => key
            };
        }

        internal static string SerializeKey(CanonicalTable table, object? keyValue)
        {
            return keyValue switch
            {
                null => "",
                long l => l.ToString(System.Globalization.CultureInfo.InvariantCulture),
                int i => i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Guid g => g.ToString(),
                _ => Convert.ToString(keyValue, System.Globalization.CultureInfo.InvariantCulture) ?? ""
            };
        }

        /// <summary>Số biến tối đa mỗi lệnh INSERT (SQLite cũ giới hạn 999).</summary>
        internal const int MaxVariablesPerBatch = 500;

        internal static string BuildInsertBatch(
            CanonicalTable table, int rowCount, out int cappedRows)
        {
            cappedRows = rowCount;
            var colCount = Math.Max(1, table.Columns.Count);
            var maxRows = Math.Max(1, MaxVariablesPerBatch / colCount);
            if (cappedRows > maxRows)
                cappedRows = maxRows;
            var cols = string.Join(", ", ColumnList(table));
            // Tên param duy nhất mỗi dòng: @p{r}_{i}.
            var allRows = new List<string>();
            for (var r = 0; r < cappedRows; r++)
            {
                var cells = new List<string>();
                for (var i = 0; i < table.Columns.Count; i++)
                    cells.Add("@p" + r + "_" + i);
                allRows.Add("(" + string.Join(", ", cells) + ")");
            }
            return $"INSERT INTO {Quote(table.Name)} ({cols}) VALUES {string.Join(", ", allRows)};";
        }

        public async Task<long> WriteBatchAsync(
            DbProbe probe, CanonicalTable table, IReadOnlyList<object?[]> rows,
            CancellationToken ct = default)
        {
            if (rows.Count == 0)
                return 0;
            using var conn = new SqliteConnection(BuildConnectionString(probe));
            await conn.OpenAsync(ct).ConfigureAwait(false);
            using (var pragma = new SqliteCommand("PRAGMA foreign_keys = ON;", conn))
                await pragma.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            long written = 0;
            var index = 0;
            while (index < rows.Count)
            {
                ct.ThrowIfCancellationRequested();
                var sql = BuildInsertBatch(table, rows.Count - index, out var capped);
                using var tx = conn.BeginTransaction();
                try
                {
                    using var cmd = new SqliteCommand(sql, conn, tx) { CommandTimeout = 600 };
                    for (var r = 0; r < capped; r++)
                    {
                        var row = rows[index + r];
                        for (var i = 0; i < table.Columns.Count && i < row.Length; i++)
                        {
                            var converted = CanonicalValues.ConvertFor(
                                DatabaseEngine.Sqlite, row[i], table.Columns[i].Type);
                            cmd.Parameters.AddWithValue("@p" + r + "_" + i, converted ?? DBNull.Value);
                        }
                    }
                    await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                    await tx.CommitAsync(ct).ConfigureAwait(false);
                    written += capped;
                    index += capped;
                }
                catch
                {
                    try { await tx.RollbackAsync(ct).ConfigureAwait(false); } catch { }
                    throw;
                }
            }
            return written;
        }
    }
}
