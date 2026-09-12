using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using SqlMigrator.Core.Interfaces;
using SqlMigrator.Core.Models;
using SqlMigrator.Core.Services.DataMove;

namespace SqlMigrator.Core.Services.DataMove
{
    /// <summary>
    /// Endpoint SQL Server cho di chuyển chéo engine: đọc schema chuẩn, đọc chunk,
    /// tạo bảng, đếm, ghi bulk. Nguồn chỉ SELECT; đích chỉ ghi theo lệnh mover.
    /// </summary>
    public sealed class SqlServerEndpoint : IDbEndpoint
    {
        public DatabaseEngine Engine => DatabaseEngine.SqlServer;

        private static SqlConnectionStringBuilder BuildCs(DbProbe probe)
        {
            var builder = new SqlConnectionStringBuilder
            {
                DataSource = probe.Port > 0 && !probe.Host.Contains(",") && !probe.Host.Contains("\\")
                    ? probe.Host + "," + probe.Port
                    : probe.Host,
                InitialCatalog = string.IsNullOrWhiteSpace(probe.Database) ? "master" : probe.Database,
                ConnectTimeout = Math.Clamp(probe.TimeoutSeconds > 0 ? probe.TimeoutSeconds : 30, 1, 120),
                Encrypt = false,
                TrustServerCertificate = true,
                Pooling = true
            };
            if (probe.UseWindowsAuth || string.IsNullOrWhiteSpace(probe.User))
                builder.IntegratedSecurity = true;
            else
            {
                builder.UserID = probe.User;
                builder.Password = probe.Password;
            }
            return builder;
        }

        public async Task<CanonicalSchema> ReadSchemaAsync(DbProbe probe, CancellationToken ct = default)
        {
            var tables = new List<CanonicalTable>();
            using var conn = new SqlConnection(BuildCs(probe).ConnectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);

            var tableNames = new List<(string Schema, string Name)>();
            using (var cmd = new SqlCommand(
                "SELECT s.name, t.name FROM sys.tables t "
                + "JOIN sys.schemas s ON s.schema_id = t.schema_id "
                + "WHERE t.is_ms_shipped = 0 AND t.is_memory_optimized = 0 AND t.is_filetable = 0 "
                + "ORDER BY s.name, t.name;", conn)
                { CommandTimeout = 120 })
            using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    tableNames.Add((reader.GetString(0), reader.GetString(1)));
            }

            foreach (var (schema, name) in tableNames)
            {
                ct.ThrowIfCancellationRequested();
                tables.Add(await ReadTableAsync(conn, schema, name, ct).ConfigureAwait(false));
            }
            return new CanonicalSchema { Tables = tables };
        }

        private static async Task<CanonicalTable> ReadTableAsync(
            SqlConnection conn, string schema, string name, CancellationToken ct)
        {
            var columns = new List<CanonicalColumn>();
            using (var cmd = new SqlCommand(
                "SELECT c.name, TYPE_NAME(c.user_type_id), c.max_length, c.precision, c.scale, "
                + "c.is_nullable, c.is_computed, c.is_identity, "
                + "CAST(CASE WHEN TYPE_NAME(c.user_type_id) IN ('timestamp','rowversion') THEN 1 ELSE 0 END AS bit), "
                + "CAST(ISNULL((SELECT 1 FROM sys.index_columns ic "
                + "JOIN sys.indexes ix ON ix.object_id = ic.object_id AND ix.index_id = ic.index_id AND ix.is_primary_key = 1 "
                + "WHERE ic.object_id = c.object_id AND ic.column_id = c.column_id), 0) AS bit) "
                + "FROM sys.columns c JOIN sys.tables t ON t.object_id = c.object_id "
                + "JOIN sys.schemas s ON s.schema_id = t.schema_id "
                + "WHERE s.name = @s AND t.name = @t ORDER BY c.column_id;", conn)
                { CommandTimeout = 120 })
            {
                cmd.Parameters.AddWithValue("@s", schema);
                cmd.Parameters.AddWithValue("@t", name);
                using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    var spec = DbTypeMappers.ParseSqlServer(
                        reader.GetString(1) + TypeSuffix(reader.GetInt16(2), reader.GetByte(3), reader.GetByte(4),
                            reader.GetString(1)));
                    columns.Add(new CanonicalColumn
                    {
                        Name = reader.GetString(0),
                        Type = spec.Type,
                        MaxLength = CharLength(reader.GetString(1), reader.GetInt16(2), spec.MaxLength),
                        Precision = reader.GetByte(3),
                        Scale = reader.GetByte(4),
                        IsNullable = reader.GetBoolean(5),
                        IsComputed = reader.GetBoolean(6),
                        IsIdentity = reader.GetBoolean(7),
                        IsPrimaryKey = reader.GetBoolean(9)
                    });
                }
            }

            var pkCols = columns.Where(c => c.IsPrimaryKey).Select(c => c.Name).ToList();
            return new CanonicalTable
            {
                Schema = schema,
                Name = name,
                Columns = columns,
                PrimaryKeyColumns = pkCols,
                ForeignKeys = await ReadForeignKeysAsync(conn, schema, name, ct).ConfigureAwait(false)
            };
        }

        private static async Task<List<CanonicalForeignKey>> ReadForeignKeysAsync(
            SqlConnection conn, string schema, string name, CancellationToken ct)
        {
            var groups = new Dictionary<string, (List<string> Cols, string RefSchema, string RefTable, List<string> RefCols)>(
                StringComparer.OrdinalIgnoreCase);
            var order = new List<string>();
            using var cmd = new SqlCommand(
                "SELECT fk.name, c1.name, rs.name, rt.name, c2.name "
                + "FROM sys.foreign_keys fk "
                + "JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id "
                + "JOIN sys.tables pt ON pt.object_id = fk.parent_object_id "
                + "JOIN sys.schemas ps ON ps.schema_id = pt.schema_id "
                + "JOIN sys.columns c1 ON c1.object_id = fkc.parent_object_id AND c1.column_id = fkc.parent_column_id "
                + "JOIN sys.tables rt ON rt.object_id = fk.referenced_object_id "
                + "JOIN sys.schemas rs ON rs.schema_id = rt.schema_id "
                + "JOIN sys.columns c2 ON c2.object_id = fkc.referenced_object_id AND c2.column_id = fkc.referenced_column_id "
                + "WHERE ps.name = @s AND pt.name = @t "
                + "ORDER BY fk.name, fkc.constraint_column_id;", conn)
            { CommandTimeout = 120 };
            cmd.Parameters.AddWithValue("@s", schema);
            cmd.Parameters.AddWithValue("@t", name);
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var fkName = reader.GetString(0);
                if (!groups.TryGetValue(fkName, out var g))
                {
                    g = (new List<string>(), reader.GetString(2), reader.GetString(3), new List<string>());
                    groups[fkName] = g;
                    order.Add(fkName);
                }
                g.Cols.Add(reader.GetString(1));
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

        /// <summary>Hậu tố (n) cho parser kiểu (max_length của sys là byte).</summary>
        internal static string TypeSuffix(short maxLength, byte precision, byte scale, string typeName)
        {
            var lower = (typeName ?? "").Trim().ToLowerInvariant();
            if (lower is "decimal" or "numeric")
                return $"({precision},{scale})";
            if (lower is "char" or "varchar" or "binary" or "varbinary")
                return maxLength == -1 ? "(max)" : $"({maxLength})";
            if (lower is "nchar" or "nvarchar")
                return maxLength == -1 ? "(max)" : $"({maxLength / 2})";
            if (lower is "time" or "datetime2" or "datetimeoffset")
                return $"({scale})";
            if (lower is "float" && precision > 0 && precision != 53)
                return $"({precision})";
            return "";
        }

        /// <summary>Độ dài ký tự từ max_length byte của sys.columns.</summary>
        internal static int CharLength(string typeName, short maxLength, int parsed)
        {
            if (maxLength == -1)
                return -1;
            var lower = (typeName ?? "").Trim().ToLowerInvariant();
            if (lower is "nchar" or "nvarchar" or "ntext")
                return maxLength / 2;
            return parsed;
        }

        public async Task<bool> TableExistsAsync(DbProbe probe, string? schema, string table,
            CancellationToken ct = default)
        {
            using var conn = new SqlConnection(BuildCs(probe).ConnectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);
            using var cmd = new SqlCommand(
                "SELECT CASE WHEN OBJECT_ID(@qname, 'U') IS NULL THEN 0 ELSE 1 END;", conn)
            { CommandTimeout = 60 };
            var qname = string.IsNullOrWhiteSpace(schema) ? table : schema + "." + table;
            cmd.Parameters.AddWithValue("@qname", qname);
            return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false)) == 1;
        }

        public async Task ExecuteDdlAsync(DbProbe probe, string ddl, CancellationToken ct = default)
        {
            using var conn = new SqlConnection(BuildCs(probe).ConnectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);
            foreach (var batch in ScriptUtils.SplitBatches(ddl))
            {
                if (string.IsNullOrWhiteSpace(batch))
                    continue;
                using var cmd = new SqlCommand(batch, conn) { CommandTimeout = 600 };
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }

        public async Task DropTableIfExistsAsync(DbProbe probe, string? schema, string table,
            bool cascade, CancellationToken ct = default)
        {
            using var conn = new SqlConnection(BuildCs(probe).ConnectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);
            var name = string.IsNullOrWhiteSpace(schema)
                ? Quoting.QuoteIdentifier(table)
                : Quoting.QuoteQualifiedName(schema, table);
            using var cmd = new SqlCommand(
                $"IF OBJECT_ID(N'{name.Replace("'", "''")}', 'U') IS NOT NULL DROP TABLE {name};", conn)
            { CommandTimeout = 600 };
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        public async Task EnsureSchemaAsync(DbProbe probe, string schema, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(schema)
                || schema.Equals("dbo", StringComparison.OrdinalIgnoreCase))
                return;
            using var conn = new SqlConnection(BuildCs(probe).ConnectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);
            using var cmd = new SqlCommand(
                "IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = @s) "
                + "EXEC('CREATE SCHEMA ' + QUOTENAME(@s));", conn)
            { CommandTimeout = 120 };
            cmd.Parameters.AddWithValue("@s", schema);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        public async Task<long> CountRowsAsync(DbProbe probe, string? schema, string table,
            CancellationToken ct = default)
        {
            using var conn = new SqlConnection(BuildCs(probe).ConnectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);
            var name = string.IsNullOrWhiteSpace(schema)
                ? Quoting.QuoteIdentifier(table)
                : Quoting.QuoteQualifiedName(schema, table);
            using var cmd = new SqlCommand("SELECT COUNT_BIG(*) FROM " + name + ";", conn)
            { CommandTimeout = 600 };
            return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
        }

        public async Task<IReadOnlyList<object?[]>> ReadBatchAsync(
            DbProbe probe, CanonicalTable table, string? afterKeyExclusive, int batchSize,
            CancellationToken ct = default)
        {
            var rows = new List<object?[]>();
            var cols = string.Join(", ", ColumnList(table));
            var from = string.IsNullOrWhiteSpace(table.Schema)
                ? Quoting.QuoteIdentifier(table.Name)
                : Quoting.QuoteQualifiedName(table.Schema, table.Name);
            using var conn = new SqlConnection(BuildCs(probe).ConnectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);

            string sql;
            using var cmd = new SqlCommand { Connection = conn, CommandTimeout = 600 };
            if (table.PrimaryKeyColumns.Count == 1)
            {
                var key = table.PrimaryKeyColumns[0];
                sql = $"SELECT TOP (@n) {cols} FROM {from}";
                var keyCol = FindColumn(table, key);
                if (!string.IsNullOrEmpty(afterKeyExclusive))
                {
                    sql += $" WHERE {Quoting.QuoteIdentifier(key)} > @k";
                    cmd.Parameters.Add(KeyParam(keyCol, afterKeyExclusive));
                }
                sql += $" ORDER BY {Quoting.QuoteIdentifier(key)};";
                cmd.Parameters.Add(new SqlParameter("@n", SqlDbType.Int) { Value = batchSize });
            }
            else
            {
                var offset = 0;
                int.TryParse(afterKeyExclusive, out offset);
                sql = $"SELECT {cols} FROM {from} ORDER BY (SELECT NULL) OFFSET @skip ROWS FETCH NEXT @n ROWS ONLY;";
                cmd.Parameters.Add(new SqlParameter("@skip", SqlDbType.Int) { Value = offset });
                cmd.Parameters.Add(new SqlParameter("@n", SqlDbType.Int) { Value = batchSize });
            }
            cmd.CommandText = sql;
            using var reader = await cmd.ExecuteReaderAsync(
                System.Data.CommandBehavior.SequentialAccess, ct).ConfigureAwait(false);
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

        public async Task<long> WriteBatchAsync(
            DbProbe probe, CanonicalTable table, IReadOnlyList<object?[]> rows,
            CancellationToken ct = default)
        {
            if (rows.Count == 0)
                return 0;
            var from = string.IsNullOrWhiteSpace(table.Schema)
                ? Quoting.QuoteIdentifier(table.Name)
                : Quoting.QuoteQualifiedName(table.Schema, table.Name);
            using var conn = new SqlConnection(BuildCs(probe).ConnectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);

            var dt = new System.Data.DataTable();
            foreach (var c in table.Columns)
                dt.Columns.Add(c.Name, typeof(object));
            foreach (var row in rows)
            {
                var dr = dt.NewRow();
                for (var i = 0; i < row.Length && i < dt.Columns.Count; i++)
                    dr[i] = row[i] ?? DBNull.Value;
                dt.Rows.Add(row);
            }

            var hasIdentity = table.Columns.Any(c => c.IsIdentity);
            if (hasIdentity)
            {
                using var on = new SqlCommand($"SET IDENTITY_INSERT {from} ON;", conn)
                { CommandTimeout = 600 };
                await on.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            try
            {
                var options = SqlBulkCopyOptions.TableLock
                    | SqlBulkCopyOptions.KeepIdentity;
                using var bulk = new SqlBulkCopy(conn, options, null)
                {
                    DestinationTableName = from,
                    BatchSize = rows.Count,
                    EnableStreaming = false
                };
                foreach (var c in table.Columns)
                    bulk.ColumnMappings.Add(c.Name, c.Name);
                await bulk.WriteToServerAsync(dt, ct).ConfigureAwait(false);
            }
            finally
            {
                if (hasIdentity)
                {
                    using var off = new SqlCommand($"SET IDENTITY_INSERT {from} OFF;", conn)
                    { CommandTimeout = 600 };
                    await off.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
            }
            return rows.Count;
        }

        private static List<string> ColumnList(CanonicalTable table)
        {
            var cols = new List<string>();
            foreach (var c in table.Columns)
                cols.Add(Quoting.QuoteIdentifier(c.Name));
            return cols;
        }

        private static CanonicalColumn? FindColumn(CanonicalTable table, string name)
        {
            foreach (var c in table.Columns)
            {
                if (c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return c;
            }
            return null;
        }

        private static SqlParameter KeyParam(CanonicalColumn? keyCol, string key)
        {
            var type = keyCol?.Type ?? Models.CanonicalType.String;
            return type switch
            {
                Models.CanonicalType.Int16 or Models.CanonicalType.Int32 or Models.CanonicalType.Int64
                    => new SqlParameter("@k", SqlDbType.BigInt)
                    { Value = long.Parse(key, CultureInfo.InvariantCulture) },
                Models.CanonicalType.Guid
                    => new SqlParameter("@k", SqlDbType.UniqueIdentifier) { Value = Guid.Parse(key) },
                Models.CanonicalType.Date or Models.CanonicalType.DateTime
                    => new SqlParameter("@k", SqlDbType.DateTime2)
                    { Value = DateTime.Parse(key, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) },
                _ => new SqlParameter("@k", SqlDbType.NVarChar, 4000) { Value = key }
            };
        }
    }
}
