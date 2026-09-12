using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using NpgsqlTypes;
using SqlMigrator.Core.Interfaces;
using SqlMigrator.Core.Models;

namespace SqlMigrator.Core.Services.DataMove
{
    /// <summary>
    /// Endpoint PostgreSQL cho di chuyển chéo engine: đọc schema chuẩn qua
    /// information_schema (bền version), đọc chunk keyset (LIMIT), ghi COPY BINARY.
    /// Nguồn chỉ SELECT; đích chỉ ghi theo lệnh mover.
    /// </summary>
    public sealed class PostgresEndpoint : IDbEndpoint
    {
        public DatabaseEngine Engine => DatabaseEngine.PostgreSql;

        internal static string BuildConnectionString(DbProbe probe)
        {
            var builder = new NpgsqlConnectionStringBuilder
            {
                Host = probe.Host,
                Port = probe.Port > 0 ? probe.Port : 5432,
                Username = string.IsNullOrWhiteSpace(probe.User) ? "postgres" : probe.User,
                Password = probe.Password ?? "",
                Database = string.IsNullOrWhiteSpace(probe.Database) ? "postgres" : probe.Database,
                Timeout = Math.Clamp(probe.TimeoutSeconds > 0 ? probe.TimeoutSeconds : 30, 1, 300),
                CommandTimeout = Math.Clamp(probe.TimeoutSeconds > 0 ? probe.TimeoutSeconds : 30, 1, 600),
                SslMode = SslMode.Prefer,
                Pooling = true
            };
            return builder.ConnectionString;
        }

        internal static string Qualify(string? schema, string table) =>
            "\"" + (string.IsNullOrWhiteSpace(schema) ? "public" : schema!).Replace("\"", "\"\"") + "\".\""
            + table.Replace("\"", "\"\"") + "\"";

        /// <summary>
        /// Liệt kê database người dùng trên server PostgreSQL (để UI nạp combobox).
        /// Chỉ SELECT pg_database; không chạm dữ liệu người dùng.
        /// </summary>
        public static async Task<List<string>> ListDatabasesAsync(
            DbProbe probe, CancellationToken ct = default)
        {
            var admin = new DbProbe
            {
                Host = probe.Host,
                Port = probe.Port,
                Database = "postgres",
                User = probe.User,
                Password = probe.Password,
                TimeoutSeconds = probe.TimeoutSeconds > 0 ? probe.TimeoutSeconds : 5
            };
            using var conn = new NpgsqlConnection(BuildConnectionString(admin));
            await conn.OpenAsync(ct).ConfigureAwait(false);
            using var cmd = new NpgsqlCommand(
                "SELECT datname FROM pg_database "
                + "WHERE datistemplate = false AND datallowconn = true ORDER BY 1;", conn)
            { CommandTimeout = 15 };
            var result = new List<string>();
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                result.Add(reader.GetString(0));
            return result;
        }

        public async Task<CanonicalSchema> ReadSchemaAsync(DbProbe probe, CancellationToken ct = default)
        {
            var tables = new List<CanonicalTable>();
            using var conn = new NpgsqlConnection(BuildConnectionString(probe));
            await conn.OpenAsync(ct).ConfigureAwait(false);

            var names = new List<(string Schema, string Name)>();
            using (var cmd = new NpgsqlCommand(
                "SELECT table_schema, table_name FROM information_schema.tables "
                + "WHERE table_type = 'BASE TABLE' "
                + "AND table_schema NOT IN ('pg_catalog', 'information_schema') "
                + "ORDER BY table_schema, table_name;", conn))
            using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    names.Add((reader.GetString(0), reader.GetString(1)));
            }

            foreach (var (schema, name) in names)
            {
                ct.ThrowIfCancellationRequested();
                tables.Add(await ReadTableAsync(conn, schema, name, ct).ConfigureAwait(false));
            }
            return new CanonicalSchema { Tables = tables };
        }

        private static async Task<CanonicalTable> ReadTableAsync(
            NpgsqlConnection conn, string schema, string name, CancellationToken ct)
        {
            var columns = new List<CanonicalColumn>();
            var byName = new Dictionary<string, CanonicalColumn>(StringComparer.OrdinalIgnoreCase);
            using (var cmd = new NpgsqlCommand(
                "SELECT column_name, data_type, character_maximum_length, "
                + "numeric_precision, numeric_scale, datetime_precision, "
                + "is_nullable, is_identity, is_generated, column_default "
                + "FROM information_schema.columns "
                + "WHERE table_schema = @s AND table_name = @t ORDER BY ordinal_position;", conn))
            {
                cmd.Parameters.AddWithValue("@s", schema);
                cmd.Parameters.AddWithValue("@t", name);
                using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    var dataType = reader.GetString(1);
                    var maxLen = reader.IsDBNull(2) ? 0 : reader.GetInt32(2);
                    var spec = DbTypeMappers.ParsePostgres(
                        dataType + (dataType is "character varying" or "character" && maxLen > 0
                            ? $"({maxLen})" : ""));
                    var col = new CanonicalColumn
                    {
                        Name = reader.GetString(0),
                        Type = spec.Type,
                        MaxLength = spec.Type == CanonicalType.String
                            ? (dataType is "character varying" or "character" ? maxLen : -1)
                            : spec.MaxLength,
                        Precision = reader.IsDBNull(3) ? (byte)0 : (byte)reader.GetInt32(3),
                        Scale = dataType.StartsWith("timestamp", StringComparison.OrdinalIgnoreCase)
                            || dataType.StartsWith("time", StringComparison.OrdinalIgnoreCase)
                            ? (reader.IsDBNull(5) ? (byte)0 : (byte)reader.GetInt32(5))
                            : (reader.IsDBNull(4) ? (byte)0 : (byte)reader.GetInt32(4)),
                        IsNullable = reader.GetString(6) == "YES",
                        IsIdentity = reader.GetString(7) == "YES",
                        IsComputed = reader.GetString(8) != "NEVER",
                        DefaultSql = reader.IsDBNull(9) ? null : reader.GetString(9)
                    };
                    columns.Add(col);
                    byName[col.Name] = col;
                }
            }

            // Khóa chính (giữ đúng thứ tự).
            var pk = new List<string>();
            using (var cmd = new NpgsqlCommand(
                "SELECT kcu.column_name FROM information_schema.table_constraints tc "
                + "JOIN information_schema.key_column_usage kcu "
                + "ON kcu.constraint_schema = tc.constraint_schema "
                + "AND kcu.constraint_name = tc.constraint_name "
                + "WHERE tc.table_schema = @s AND tc.table_name = @t "
                + "AND tc.constraint_type = 'PRIMARY KEY' ORDER BY kcu.ordinal_position;", conn))
            {
                cmd.Parameters.AddWithValue("@s", schema);
                cmd.Parameters.AddWithValue("@t", name);
                using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    pk.Add(reader.GetString(0));
            }
            var pkSet = new HashSet<string>(pk, StringComparer.OrdinalIgnoreCase);
            columns = columns.Select(c => new CanonicalColumn
            {
                Name = c.Name,
                Type = c.Type,
                MaxLength = c.MaxLength,
                Precision = c.Precision,
                Scale = c.Scale,
                IsNullable = c.IsNullable,
                IsPrimaryKey = pkSet.Contains(c.Name),
                IsIdentity = c.IsIdentity,
                IsComputed = c.IsComputed,
                DefaultSql = c.DefaultSql
            }).ToList();

            return new CanonicalTable
            {
                Schema = schema,
                Name = name,
                Columns = columns,
                PrimaryKeyColumns = pk,
                ForeignKeys = await ReadForeignKeysAsync(conn, schema, name, ct).ConfigureAwait(false)
            };
        }

        private static async Task<List<CanonicalForeignKey>> ReadForeignKeysAsync(
            NpgsqlConnection conn, string schema, string name, CancellationToken ct)
        {
            // Gom theo tên constraint, giữ thứ tự ordinal_position.
            var groups = new Dictionary<string, (List<string> Cols, string RefSchema, string RefTable, List<string> RefCols)>(
                StringComparer.OrdinalIgnoreCase);
            var order = new List<string>();
            using var cmd = new NpgsqlCommand(
                "SELECT tc.constraint_name, kcu.column_name, "
                + "ccu.table_schema, ccu.table_name, ccu.column_name "
                + "FROM information_schema.table_constraints tc "
                + "JOIN information_schema.key_column_usage kcu "
                + "ON kcu.constraint_schema = tc.constraint_schema "
                + "AND kcu.constraint_name = tc.constraint_name "
                + "JOIN information_schema.constraint_column_usage ccu "
                + "ON ccu.constraint_schema = tc.constraint_schema "
                + "AND ccu.constraint_name = tc.constraint_name "
                + "AND ccu.ordinal_position = kcu.ordinal_position "
                + "WHERE tc.table_schema = @s AND tc.table_name = @t "
                + "AND tc.constraint_type = 'FOREIGN KEY' "
                + "ORDER BY tc.constraint_name, kcu.ordinal_position;", conn);
            cmd.Parameters.AddWithValue("@s", schema);
            cmd.Parameters.AddWithValue("@t", name);
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var fkName = reader.GetString(0);
                if (!groups.TryGetValue(fkName, out var g))
                {
                    g = (new List<string>(), "", "", new List<string>());
                    groups[fkName] = g;
                    order.Add(fkName);
                }
                g.Cols.Add(reader.GetString(1));
                g.RefSchema = reader.GetString(2);
                g.RefTable = reader.GetString(3);
                g.RefCols.Add(reader.GetString(4));
            }
            var result = new List<CanonicalForeignKey>();
            foreach (var key in order)
            {
                var g = groups[key];
                result.Add(new CanonicalForeignKey
                {
                    Name = key,
                    Columns = g.Cols,
                    RefSchema = g.RefSchema,
                    RefTable = g.RefTable,
                    RefColumns = g.RefCols
                });
            }
            return result;
        }

        public async Task<bool> TableExistsAsync(DbProbe probe, string? schema, string table,
            CancellationToken ct = default)
        {
            using var conn = new NpgsqlConnection(BuildConnectionString(probe));
            await conn.OpenAsync(ct).ConfigureAwait(false);
            using var cmd = new NpgsqlCommand(
                "SELECT EXISTS(SELECT 1 FROM information_schema.tables "
                + "WHERE table_schema = @s AND table_name = @t);", conn);
            cmd.Parameters.AddWithValue("@s", string.IsNullOrWhiteSpace(schema) ? "public" : schema!);
            cmd.Parameters.AddWithValue("@t", table);
            return Convert.ToBoolean(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
        }

        public async Task ExecuteDdlAsync(DbProbe probe, string ddl, CancellationToken ct = default)
        {
            using var conn = new NpgsqlConnection(BuildConnectionString(probe));
            await conn.OpenAsync(ct).ConfigureAwait(false);
            using var cmd = new NpgsqlCommand(ddl, conn) { CommandTimeout = 600 };
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        public async Task DropTableIfExistsAsync(DbProbe probe, string? schema, string table,
            bool cascade, CancellationToken ct = default)
        {
            using var conn = new NpgsqlConnection(BuildConnectionString(probe));
            await conn.OpenAsync(ct).ConfigureAwait(false);
            using var cmd = new NpgsqlCommand(
                $"DROP TABLE IF EXISTS {Qualify(schema, table)}{(cascade ? " CASCADE" : "")};", conn)
            { CommandTimeout = 600 };
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        public async Task<long> CountRowsAsync(DbProbe probe, string? schema, string table,
            CancellationToken ct = default)
        {
            using var conn = new NpgsqlConnection(BuildConnectionString(probe));
            await conn.OpenAsync(ct).ConfigureAwait(false);
            using var cmd = new NpgsqlCommand($"SELECT COUNT(*) FROM {Qualify(schema, table)};", conn)
            { CommandTimeout = 600 };
            return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
        }

        internal static string BuildSelectBatch(
            CanonicalTable table, string? afterKeyExclusive, int batchSize,
            out List<(string Name, NpgsqlDbType Type, object Value)> parameters)
        {
            parameters = new List<(string, NpgsqlDbType, object)>();
            var cols = string.Join(", ", ColumnList(table));
            var from = Qualify(table.Schema, table.Name);
            if (table.PrimaryKeyColumns.Count == 1)
            {
                var key = table.PrimaryKeyColumns[0];
                var sql = $"SELECT {cols} FROM {from}";
                if (!string.IsNullOrEmpty(afterKeyExclusive))
                {
                    sql += $" WHERE {Quote(key)} > @k";
                    parameters.Add(("@k", KeyDbType(table, key), (object)afterKeyExclusive));
                }
                sql += $" ORDER BY {Quote(key)} LIMIT @n;";
                parameters.Add(("@n", NpgsqlDbType.Integer, (object)batchSize));
                return sql;
            }
            var offset = 0;
            int.TryParse(afterKeyExclusive, out offset);
            parameters.Add(("@skip", NpgsqlDbType.Integer, (object)offset));
            parameters.Add(("@n", NpgsqlDbType.Integer, (object)batchSize));
            return $"SELECT {cols} FROM {from} ORDER BY 1 LIMIT @n OFFSET @skip;";
        }

        internal static NpgsqlDbType KeyDbType(CanonicalTable table, string keyColumn)
        {
            foreach (var c in table.Columns)
            {
                if (!c.Name.Equals(keyColumn, StringComparison.OrdinalIgnoreCase))
                    continue;
                return c.Type switch
                {
                    CanonicalType.Int16 or CanonicalType.Int32 or CanonicalType.Int64 => NpgsqlDbType.Bigint,
                    CanonicalType.Guid => NpgsqlDbType.Uuid,
                    CanonicalType.Date or CanonicalType.DateTime => NpgsqlDbType.Timestamp,
                    CanonicalType.DateTimeTz => NpgsqlDbType.TimestampTz,
                    _ => NpgsqlDbType.Varchar
                };
            }
            return NpgsqlDbType.Varchar;
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
                        => long.Parse(key, System.Globalization.CultureInfo.InvariantCulture),
                    CanonicalType.Guid => Guid.Parse(key),
                    CanonicalType.Date or CanonicalType.DateTime or CanonicalType.DateTimeTz
                        => DateTime.Parse(key, System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.RoundtripKind),
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
                long l => l.ToString(System.Globalization.CultureInfo.InvariantCulture),
                int i => i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                short s => s.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Guid g => g.ToString(),
                DateTime dt => dt.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                DateTimeOffset dto => dto.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? ""
            };
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
            var sql = BuildSelectBatch(table, afterKeyExclusive, batchSize, out var parameters);
            using var conn = new NpgsqlConnection(BuildConnectionString(probe));
            await conn.OpenAsync(ct).ConfigureAwait(false);
            using var cmd = new NpgsqlCommand(sql, conn) { CommandTimeout = 600 };
            foreach (var (name, type, value) in parameters)
            {
                var paramValue = name == "@k" && table.PrimaryKeyColumns.Count == 1
                    ? ParseKey(table, (string)value)
                    : value;
                cmd.Parameters.Add(new NpgsqlParameter(name, type) { Value = paramValue });
            }
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

        public async Task<long> WriteBatchAsync(
            DbProbe probe, CanonicalTable table, IReadOnlyList<object?[]> rows,
            CancellationToken ct = default)
        {
            if (rows.Count == 0)
                return 0;
            var cols = ColumnList(table);
            using var conn = new NpgsqlConnection(BuildConnectionString(probe));
            await conn.OpenAsync(ct).ConfigureAwait(false);
            using var importer = conn.BeginBinaryImport(
                $"COPY {Qualify(table.Schema, table.Name)} ({string.Join(", ", cols)}) FROM STDIN (FORMAT BINARY)");
            foreach (var row in rows)
            {
                ct.ThrowIfCancellationRequested();
                importer.StartRow();
                for (var i = 0; i < table.Columns.Count && i < row.Length; i++)
                {
                    var converted = CanonicalValues.ConvertFor(
                        DatabaseEngine.PostgreSql, row[i], table.Columns[i].Type);
                    if (converted == null)
                        importer.WriteNull();
                    else
                        importer.Write(converted, ColumnDbType(table.Columns[i]));
                }
            }
            await importer.CompleteAsync(ct).ConfigureAwait(false);
            return rows.Count;
        }

        internal static NpgsqlDbType ColumnDbType(CanonicalColumn column)
        {
            return column.Type switch
            {
                CanonicalType.Int16 => NpgsqlDbType.Smallint,
                CanonicalType.Int32 => NpgsqlDbType.Integer,
                CanonicalType.Int64 => NpgsqlDbType.Bigint,
                CanonicalType.Decimal => NpgsqlDbType.Numeric,
                CanonicalType.Float => NpgsqlDbType.Real,
                CanonicalType.Double => NpgsqlDbType.Double,
                CanonicalType.String => NpgsqlDbType.Text,
                CanonicalType.Bool => NpgsqlDbType.Boolean,
                CanonicalType.Date => NpgsqlDbType.Date,
                CanonicalType.Time => NpgsqlDbType.Time,
                CanonicalType.DateTime => NpgsqlDbType.Timestamp,
                CanonicalType.DateTimeTz => NpgsqlDbType.TimestampTz,
                CanonicalType.Binary => NpgsqlDbType.Bytea,
                CanonicalType.Guid => NpgsqlDbType.Uuid,
                CanonicalType.Json => NpgsqlDbType.Jsonb,
                CanonicalType.Money => NpgsqlDbType.Numeric,
                _ => NpgsqlDbType.Text
            };
        }
    }
}
