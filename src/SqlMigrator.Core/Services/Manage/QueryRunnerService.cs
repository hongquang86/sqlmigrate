using System;
using System.Data;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using MySqlConnector;
using Npgsql;
using SqlMigrator.Core.Interfaces;
using SqlMigrator.Core.Models;

namespace SqlMigrator.Core.Services.Manage
{
    /// <summary>Kết quả chạy câu lệnh: bảng dữ liệu (SELECT) hoặc số dòng ảnh hưởng.</summary>
    public sealed class QueryResult
    {
        public bool Success { get; init; }
        public DataTable? Table { get; init; }
        public long RowsAffected { get; init; } = -1;
        public TimeSpan Elapsed { get; init; }
        public string? Error { get; init; }
        public bool Truncated { get; init; }
    }

    /// <summary>
    /// Hộp truy vấn SQL cho tab Quản trị (SQL Server / MySQL / PostgreSQL / SQLite;
    /// MongoDB không có SQL nên tắt ở UI). SELECT trả tối đa <see cref="MaxRows"/>
    /// dòng; lệnh khác chạy ExecuteNonQuery và báo số dòng ảnh hưởng.
    /// </summary>
    public sealed class QueryRunnerService
    {
        public const int MaxRows = 5000;

        public async Task<QueryResult> ExecuteAsync(
            DbProbe probe, DatabaseEngine engine, string sql, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(sql))
                return new QueryResult { Error = "Chưa nhập câu lệnh." };
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                using var conn = OpenConnection(probe, engine);
                await conn.OpenAsync(ct).ConfigureAwait(false);
                if (IsSelectLike(sql))
                {
                    // Microsoft.Data.Sqlite không có DataAdapter: nạp thủ công qua reader.
                    if (engine == DatabaseEngine.Sqlite)
                        return await ExecuteReaderLiteAsync(probe, sql, ct).ConfigureAwait(false);
                    using var adapter = CreateAdapter(sql, conn, engine);
                    adapter.SelectCommand!.CommandTimeout = 300;
                    var table = new DataTable();
                    adapter.Fill(0, MaxRows + 1, table);
                    sw.Stop();
                    var truncated = false;
                    if (table.Rows.Count > MaxRows)
                    {
                        table.Rows.RemoveAt(table.Rows.Count - 1);
                        truncated = true;
                    }
                    return new QueryResult
                    {
                        Success = true, Table = table, Elapsed = sw.Elapsed, Truncated = truncated
                    };
                }
                using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                cmd.CommandTimeout = 300;
                var affected = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                sw.Stop();
                return new QueryResult { Success = true, RowsAffected = affected, Elapsed = sw.Elapsed };
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                sw.Stop();
                return new QueryResult { Elapsed = sw.Elapsed, Error = ex.Message };
            }
        }

        internal static bool IsSelectLike(string sql)
        {
            var t = sql.TrimStart('(', ' ', '\t', '\r', '\n').ToUpperInvariant();
            return t.StartsWith("SELECT") || t.StartsWith("WITH") || t.StartsWith("VALUES")
                || t.StartsWith("TABLE") || t.StartsWith("EXPLAIN") || t.StartsWith("SHOW")
                || t.StartsWith("DESCRIBE") || t.StartsWith("DESC ");
        }

        private static DbConnection OpenConnection(DbProbe probe, DatabaseEngine engine)
        {
            return engine switch
            {
                DatabaseEngine.MySql => new MySqlConnection(DataMove.MySqlEndpoint.BuildConnectionString(probe)),
                DatabaseEngine.PostgreSql => new NpgsqlConnection(DataMove.PostgresEndpoint.BuildConnectionString(probe)),
                DatabaseEngine.Sqlite => new SqliteConnection(DataMove.SqliteEndpoint.BuildConnectionString(probe)),
                _ => new SqlConnection(SqlServerManageProvider.BuildConnectionString(probe))
            };
        }

        /// <summary>Nạp SELECT cho SQLite (không có DataAdapter): cắt ở MaxRows.</summary>
        internal static async Task<QueryResult> ExecuteReaderLiteAsync(
            DbProbe probe, string sql, CancellationToken ct)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                using var conn = new SqliteConnection(DataMove.SqliteEndpoint.BuildConnectionString(probe));
                await conn.OpenAsync(ct).ConfigureAwait(false);
                using var cmd = new SqliteCommand(sql, conn) { CommandTimeout = 300 };
                using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                var table = new DataTable();
                var cols = reader.FieldCount;
                for (var i = 0; i < cols; i++)
                    table.Columns.Add(reader.GetName(i));
                var truncated = false;
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    if (table.Rows.Count >= MaxRows + 1)
                    {
                        truncated = true;
                        break;
                    }
                    var values = new object?[cols];
                    reader.GetValues(values);
                    for (var i = 0; i < cols; i++)
                    {
                        if (values[i] is DBNull)
                            values[i] = null;
                    }
                    table.Rows.Add(values);
                }
                sw.Stop();
                if (truncated && table.Rows.Count > MaxRows)
                    table.Rows.RemoveAt(table.Rows.Count - 1);
                return new QueryResult
                {
                    Success = true, Table = table, Elapsed = sw.Elapsed, Truncated = truncated
                };
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                sw.Stop();
                return new QueryResult { Elapsed = sw.Elapsed, Error = ex.Message };
            }
        }

        private static DbDataAdapter CreateAdapter(string sql, DbConnection conn, DatabaseEngine engine)
        {
            return engine switch
            {
                DatabaseEngine.MySql => new MySqlDataAdapter(sql, (MySqlConnection)conn),
                DatabaseEngine.PostgreSql => new NpgsqlDataAdapter(sql, (NpgsqlConnection)conn),
                _ => new SqlDataAdapter(sql, (SqlConnection)conn)
            };
        }
    }
}
