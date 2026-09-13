using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using MySqlConnector;
using SqlMigrator.Core.Interfaces;
using SqlMigrator.Core.Models;

namespace SqlMigrator.Core.Services.DataMove
{
    /// <summary>
    /// Endpoint MySQL/MariaDB cho di chuyển chéo engine: đọc schema chuẩn qua
    /// information_schema (ổn định cả 2 variant), đọc chunk keyset (LIMIT),
    /// ghi multi-row INSERT theo đợt. Nguồn chỉ SELECT; đích chỉ ghi theo mover.
    /// Schema SQL Server được bỏ qua — kết nối đã trỏ đúng database đích.
    /// </summary>
    public sealed class MySqlEndpoint : IDbEndpoint
    {
        public DatabaseEngine Engine => DatabaseEngine.MySql;

        internal static string BuildConnectionString(DbProbe probe)
        {
            var builder = new MySqlConnectionStringBuilder
            {
                Server = probe.Host,
                Port = (uint)(probe.Port > 0 ? probe.Port : 3306),
                UserID = string.IsNullOrWhiteSpace(probe.User) ? "root" : probe.User,
                Password = probe.Password ?? "",
                ConnectionTimeout = (uint)Math.Clamp(probe.TimeoutSeconds > 0 ? probe.TimeoutSeconds : 30, 1, 300),
                DefaultCommandTimeout = (uint)Math.Clamp(probe.TimeoutSeconds > 0 ? probe.TimeoutSeconds : 30, 1, 600),
                SslMode = MySqlSslMode.Preferred,
                Pooling = true
            };
            if (!string.IsNullOrWhiteSpace(probe.Database))
                builder.Database = probe.Database;
            return builder.ConnectionString;
        }

        internal static string Qualify(string table) =>
            "`" + table.Replace("`", "``") + "`";

        /// <summary>
        /// Liệt kê database người dùng trên server MySQL/MariaDB (để UI nạp combobox).
        /// Chỉ SHOW DATABASES; không chạm dữ liệu người dùng.
        /// </summary>
        public static async Task<List<string>> ListDatabasesAsync(
            DbProbe probe, CancellationToken ct = default)
        {
            var admin = new DbProbe
            {
                Host = probe.Host,
                Port = probe.Port,
                User = probe.User,
                Password = probe.Password,
                TimeoutSeconds = probe.TimeoutSeconds > 0 ? probe.TimeoutSeconds : 5
            };
            var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "information_schema", "mysql", "performance_schema", "sys"
            };
            var result = new List<string>();
            using var conn = new MySqlConnection(BuildConnectionString(admin));
            await conn.OpenAsync(ct).ConfigureAwait(false);
            using var cmd = new MySqlCommand("SHOW DATABASES;", conn) { CommandTimeout = 15 };
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var name = reader.GetString(0);
                if (!skip.Contains(name))
                    result.Add(name);
            }
            return result;
        }

        public async Task<CanonicalSchema> ReadSchemaAsync(DbProbe probe, CancellationToken ct = default)
        {
            var tables = new List<CanonicalTable>();
            using var conn = new MySqlConnection(BuildConnectionString(probe));
            await conn.OpenAsync(ct).ConfigureAwait(false);
            var db = await CurrentDatabaseAsync(conn, probe.Database, ct).ConfigureAwait(false);

            var names = new List<string>();
            using (var cmd = new MySqlCommand(
                "SELECT TABLE_NAME FROM information_schema.TABLES "
                + "WHERE TABLE_SCHEMA = @db AND TABLE_TYPE IN ('BASE TABLE','SYSTEM VIEW') "
                + "ORDER BY TABLE_NAME;", conn))
            {
                cmd.Parameters.AddWithValue("@db", db);
                using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    names.Add(reader.GetString(0));
            }

            foreach (var name in names)
            {
                ct.ThrowIfCancellationRequested();
                tables.Add(await ReadTableAsync(conn, db, name, ct).ConfigureAwait(false));
            }
            return new CanonicalSchema { Tables = tables };
        }

        private static byte ToByteOrZero(object? value)
        {
            if (value == null)
                return 0;
            try { return (byte)Math.Min(Convert.ToUInt64(value), 255); }
            catch { return 0; }
        }

        private static async Task<string> CurrentDatabaseAsync(
            MySqlConnection conn, string? fallback, CancellationToken ct)
        {
            if (!string.IsNullOrWhiteSpace(fallback))
                return fallback!;
            using var cmd = new MySqlCommand("SELECT DATABASE();", conn);
            var current = (await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))?.ToString();
            return string.IsNullOrWhiteSpace(current) ? "" : current!;
        }

        private static async Task<CanonicalTable> ReadTableAsync(
            MySqlConnection conn, string db, string name, CancellationToken ct)
        {
            var columns = new List<CanonicalColumn>();
            using (var cmd = new MySqlCommand(
                "SELECT COLUMN_NAME, DATA_TYPE, COLUMN_TYPE, IS_NULLABLE, COLUMN_DEFAULT, EXTRA, "
                + "CHARACTER_MAXIMUM_LENGTH, NUMERIC_PRECISION, NUMERIC_SCALE, DATETIME_PRECISION "
                + "FROM information_schema.COLUMNS "
                + "WHERE TABLE_SCHEMA = @db AND TABLE_NAME = @t ORDER BY ORDINAL_POSITION;", conn))
            {
                cmd.Parameters.AddWithValue("@db", db);
                cmd.Parameters.AddWithValue("@t", name);
                using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    var dataType = reader.GetString(1);
                    var columnType = reader.GetString(2);
                    var spec = DbTypeMappers.ParseMySql(dataType, columnType);
                    // CHARACTER_MAXIMUM_LENGTH của text dài có thể vượt int — chỉ lấy khi vừa int.
                    long charLen = 0;
                    if (!reader.IsDBNull(6))
                    {
                        try { charLen = Convert.ToInt64(reader.GetValue(6)); } catch { charLen = 0; }
                    }
                    var extra = reader.IsDBNull(5) ? "" : reader.GetString(5);
                    columns.Add(new CanonicalColumn
                    {
                        Name = reader.GetString(0),
                        Type = spec.Type,
                        MaxLength = spec.Type == CanonicalType.String
                            ? (charLen > 0 && charLen <= int.MaxValue ? (int)charLen : -1)
                            : spec.MaxLength,
                        Precision = ToByteOrZero(reader.IsDBNull(7) ? null : reader.GetValue(7)),
                        Scale = ToByteOrZero(reader.IsDBNull(8) ? null : reader.GetValue(8)),
                        IsNullable = reader.GetString(3) == "YES",
                        IsIdentity = extra.IndexOf("auto_increment", StringComparison.OrdinalIgnoreCase) >= 0,
                        IsComputed = extra.IndexOf("VIRTUAL GENERATED", StringComparison.OrdinalIgnoreCase) >= 0
                            || extra.IndexOf("STORED GENERATED", StringComparison.OrdinalIgnoreCase) >= 0,
                        DefaultSql = reader.IsDBNull(4) ? null : reader.GetString(4)
                    });
                }
            }

            // Khóa chính (giữ đúng thứ tự SEQ_IN_INDEX).
            var pk = new List<string>();
            using (var cmd = new MySqlCommand(
                "SELECT COLUMN_NAME FROM information_schema.STATISTICS "
                + "WHERE TABLE_SCHEMA = @db AND TABLE_NAME = @t AND INDEX_NAME = 'PRIMARY' "
                + "ORDER BY SEQ_IN_INDEX;", conn))
            {
                cmd.Parameters.AddWithValue("@db", db);
                cmd.Parameters.AddWithValue("@t", name);
                using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    pk.Add(reader.GetString(0));
            }
            var pkSet = new HashSet<string>(pk, StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < columns.Count; i++)
            {
                var c = columns[i];
                if (pkSet.Contains(c.Name))
                {
                    columns[i] = new CanonicalColumn
                    {
                        Name = c.Name, Type = c.Type, MaxLength = c.MaxLength,
                        Precision = c.Precision, Scale = c.Scale, IsNullable = c.IsNullable,
                        IsPrimaryKey = true, IsIdentity = c.IsIdentity,
                        IsComputed = c.IsComputed, DefaultSql = c.DefaultSql
                    };
                }
            }

            return new CanonicalTable
            {
                Schema = null,
                Name = name,
                Columns = columns,
                PrimaryKeyColumns = pk,
                ForeignKeys = await ReadForeignKeysAsync(conn, db, name, ct).ConfigureAwait(false)
            };
        }

        private static async Task<List<CanonicalForeignKey>> ReadForeignKeysAsync(
            MySqlConnection conn, string db, string name, CancellationToken ct)
        {
            var groups = new Dictionary<string, (List<string> Cols, string RefTable, List<string> RefCols)>(
                StringComparer.OrdinalIgnoreCase);
            var order = new List<string>();
            using var cmd = new MySqlCommand(
                "SELECT k.CONSTRAINT_NAME, k.COLUMN_NAME, k.REFERENCED_TABLE_NAME, k.REFERENCED_COLUMN_NAME "
                + "FROM information_schema.KEY_COLUMN_USAGE k "
                + "WHERE k.TABLE_SCHEMA = @db AND k.TABLE_NAME = @t "
                + "AND k.REFERENCED_TABLE_NAME IS NOT NULL "
                + "ORDER BY k.CONSTRAINT_NAME, k.ORDINAL_POSITION;", conn);
            cmd.Parameters.AddWithValue("@db", db);
            cmd.Parameters.AddWithValue("@t", name);
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var fkName = reader.GetString(0);
                if (!groups.TryGetValue(fkName, out var g))
                {
                    g = (new List<string>(), "", new List<string>());
                    groups[fkName] = g;
                    order.Add(fkName);
                }
                g.Cols.Add(reader.GetString(1));
                g.RefTable = reader.IsDBNull(2) ? g.RefTable : reader.GetString(2);
                g.RefCols.Add(reader.IsDBNull(3) ? "" : reader.GetString(3));
            }
            var result = new List<CanonicalForeignKey>();
            foreach (var key in order)
            {
                var g = groups[key];
                result.Add(new CanonicalForeignKey
                {
                    Name = key,
                    Columns = g.Cols,
                    RefSchema = null,
                    RefTable = g.RefTable,
                    RefColumns = g.RefCols
                });
            }
            return result;
        }

        public async Task<bool> TableExistsAsync(DbProbe probe, string? schema, string table,
            CancellationToken ct = default)
        {
            using var conn = new MySqlConnection(BuildConnectionString(probe));
            await conn.OpenAsync(ct).ConfigureAwait(false);
            var db = await CurrentDatabaseAsync(conn, probe.Database, ct).ConfigureAwait(false);
            using var cmd = new MySqlCommand(
                "SELECT EXISTS(SELECT 1 FROM information_schema.TABLES "
                + "WHERE TABLE_SCHEMA = @db AND TABLE_NAME = @t);", conn);
            cmd.Parameters.AddWithValue("@db", db);
            cmd.Parameters.AddWithValue("@t", table);
            return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false)) == 1;
        }

        public async Task ExecuteDdlAsync(DbProbe probe, string ddl, CancellationToken ct = default)
        {
            using var conn = new MySqlConnection(BuildConnectionString(probe));
            await conn.OpenAsync(ct).ConfigureAwait(false);
            using var cmd = new MySqlCommand(ddl, conn) { CommandTimeout = 600 };
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        public async Task DropTableIfExistsAsync(DbProbe probe, string? schema, string table,
            bool cascade, CancellationToken ct = default)
        {
            using var conn = new MySqlConnection(BuildConnectionString(probe));
            await conn.OpenAsync(ct).ConfigureAwait(false);
            using var cmd = new MySqlCommand(
                $"SET FOREIGN_KEY_CHECKS=0; DROP TABLE IF EXISTS {Qualify(table)}; SET FOREIGN_KEY_CHECKS=1;",
                conn)
            { CommandTimeout = 600 };
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        public async Task<long> CountRowsAsync(DbProbe probe, string? schema, string table,
            CancellationToken ct = default)
        {
            using var conn = new MySqlConnection(BuildConnectionString(probe));
            await conn.OpenAsync(ct).ConfigureAwait(false);
            using var cmd = new MySqlCommand($"SELECT COUNT(*) FROM {Qualify(table)};", conn)
            { CommandTimeout = 600 };
            return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
        }

        public async Task<IReadOnlyList<object?[]>> ReadBatchAsync(
            DbProbe probe, CanonicalTable table, string? afterKeyExclusive, int batchSize,
            CancellationToken ct = default)
        {
            var rows = new List<object?[]>();
            var sql = BuildSelectBatch(table, afterKeyExclusive, batchSize, out var keyValue);
            using var conn = new MySqlConnection(BuildConnectionString(probe));
            await conn.OpenAsync(ct).ConfigureAwait(false);
            using var cmd = new MySqlCommand(sql, conn) { CommandTimeout = 600 };
            if (keyValue != null)
                cmd.Parameters.AddWithValue("@k", keyValue);
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var raw = new object[reader.FieldCount];
                reader.GetValues(raw);
                var row = new object?[raw.Length];
                for (var i = 0; i < raw.Length; i++)
                    row[i] = raw[i] is DBNull ? null : CanonicalValues.Normalize(raw[i]);
                rows.Add(row);
            }
            return rows;
        }

        internal static string BuildSelectBatch(
            CanonicalTable table, string? afterKeyExclusive, int batchSize, out object? keyValue)
        {
            keyValue = null;
            var cols = new List<string>();
            foreach (var c in table.Columns)
                cols.Add(Qualify(c.Name));
            var from = Qualify(table.Name);
            var take = Math.Clamp(batchSize, 1, 50000);
            if (table.PrimaryKeyColumns.Count == 1)
            {
                var key = table.PrimaryKeyColumns[0];
                var sql = $"SELECT {string.Join(", ", cols)} FROM {from}";
                if (!string.IsNullOrEmpty(afterKeyExclusive))
                {
                    sql += $" WHERE {Qualify(key)} > @k";
                    keyValue = ParseKey(table, afterKeyExclusive);
                }
                return sql + $" ORDER BY {Qualify(key)} LIMIT {take};";
            }
            var offset = 0;
            int.TryParse(afterKeyExclusive, out offset);
            return $"SELECT {string.Join(", ", cols)} FROM {from} ORDER BY 1 LIMIT {take} OFFSET {Math.Max(0, offset)};";
        }

        internal static object ParseKey(CanonicalTable table, string key)
        {
            foreach (var c in table.Columns)
            {
                if (!c.Name.Equals(table.PrimaryKeyColumns[0], StringComparison.OrdinalIgnoreCase))
                    continue;
                return c.Type switch
                {
                    CanonicalType.Int16 or CanonicalType.Int32 or CanonicalType.Int64
                        => long.Parse(key, CultureInfo.InvariantCulture),
                    CanonicalType.Date or CanonicalType.DateTime or CanonicalType.DateTimeTz
                        => DateTime.Parse(key, CultureInfo.InvariantCulture,
                            DateTimeStyles.RoundtripKind),
                    _ => key
                };
            }
            return key;
        }

        internal static string SerializeKey(object? value)
        {
            return value switch
            {
                null => "",
                long l => l.ToString(CultureInfo.InvariantCulture),
                int i => i.ToString(CultureInfo.InvariantCulture),
                short s => s.ToString(CultureInfo.InvariantCulture),
                Guid g => g.ToString(),
                DateTime dt => dt.ToString("O", CultureInfo.InvariantCulture),
                DateTimeOffset dto => dto.ToString("O", CultureInfo.InvariantCulture),
                _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""
            };
        }

        public async Task<long> WriteBatchAsync(
            DbProbe probe, CanonicalTable table, IReadOnlyList<object?[]> rows,
            CancellationToken ct = default)
        {
            if (rows.Count == 0)
                return 0;
            var cols = new List<string>();
            foreach (var c in table.Columns)
                cols.Add(Qualify(c.Name));
            var colList = string.Join(", ", cols);
            var target = Qualify(table.Name);
            long written = 0;
            // Chia nhỏ statement (500 dòng/lệnh) để không vượt max_allowed_packet.
            const int chunk = 500;
            using var conn = new MySqlConnection(BuildConnectionString(probe));
            await conn.OpenAsync(ct).ConfigureAwait(false);
            for (var start = 0; start < rows.Count; start += chunk)
            {
                ct.ThrowIfCancellationRequested();
                var count = Math.Min(chunk, rows.Count - start);
                var placeholders = new List<string>(count);
                var values = new List<object?>(count * table.Columns.Count);
                for (var r = 0; r < count; r++)
                {
                    var row = rows[start + r];
                    var marks = new string[table.Columns.Count];
                    for (var i = 0; i < table.Columns.Count; i++)
                    {
                        marks[i] = "@p" + values.Count;
                        var converted = CanonicalValues.ConvertFor(
                            DatabaseEngine.MySql,
                            i < row.Length ? row[i] : null,
                            table.Columns[i].Type);
                        values.Add(converted ?? (object)DBNull.Value);
                    }
                    placeholders.Add("(" + string.Join(", ", marks) + ")");
                }
                using var cmd = new MySqlCommand(
                    $"INSERT INTO {target} ({colList}) VALUES {string.Join(", ", placeholders)};",
                    conn)
                { CommandTimeout = 600 };
                for (var i = 0; i < values.Count; i++)
                    cmd.Parameters.AddWithValue("@p" + i, values[i]);
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                written += count;
            }
            return written;
        }
    }
}
