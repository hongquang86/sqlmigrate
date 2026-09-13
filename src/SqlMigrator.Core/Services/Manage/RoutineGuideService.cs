using System;
using System.Collections.Generic;
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
    /// <summary>
    /// Báo cáo routine cần xử lý tay sau di chuyển chéo engine (mover chỉ chép
    /// bảng + dữ liệu): liệt kê view/procedure/function/trigger của nguồn kèm
    /// hướng dẫn viết lại theo engine đích. Chỉ đọc catalog nguồn.
    /// </summary>
    public sealed class RoutineGuideService
    {
        public sealed class RoutineItem
        {
            public string Schema { get; init; } = string.Empty;
            public string Name { get; init; } = string.Empty;
            /// <summary>View / Procedure / Function / Trigger.</summary>
            public string Kind { get; init; } = string.Empty;
            public long DefinitionLength { get; init; }
            public string Guidance { get; init; } = string.Empty;
        }

        /// <summary>Ma trận hướng dẫn viết lại theo (loại routine, engine đích). Thuần dữ liệu để kiểm thử.</summary>
        internal static string GuidanceFor(string kind, DatabaseEngine dst)
        {
            var k = (kind ?? "").Trim().ToLowerInvariant();
            var isView = k.Contains("view");
            var isProc = k.Contains("proc");
            var isFunc = k.Contains("func");
            var isTrigger = k.Contains("trig");
            return dst switch
            {
                DatabaseEngine.MySql => isView
                    ? "Viết lại view sang MySQL: TOP → LIMIT, ISNULL() → IFNULL(), GETDATE()/SYSDATETIME() → NOW(), hàm chuỗi T-SQL → tương đương MySQL; kiểm tra quyền DEFINER."
                    : isProc
                    ? "Viết lại procedure sang MySQL dialect: bỏ TRY/CATCH → DECLARE HANDLER, OUTPUT → OUT, TOP → LIMIT, @@ROWCOUNT → ROW_COUNT(); mỗi lệnh DELIMITER đúng."
                    : isFunc
                    ? "Viết lại function: scalar/table-valued T-SQL không chạy thẳng — tách table-valued thành view + join; kiểm tra DETERMINISTIC/READS SQL DATA."
                    : isTrigger
                    ? "Viết lại trigger FOR EACH ROW (MySQL không có INSTEAD OF); mỗi bảng chỉ 1 trigger cùng thời điểm/sự kiện; dùng OLD./NEW. thay inserted/deleted."
                    : "Kiểm tra tay cú pháp MySQL/MariaDB.",
                DatabaseEngine.PostgreSql => isView
                    ? "Viết lại view sang PG: TOP → LIMIT/FETCH, ISNULL() → COALESCE, GETDATE() → NOW(), + → || cho nối chuỗi; kiểm tra schema search_path."
                    : isProc
                    ? "Viết lại procedure sang PL/pgSQL (CALL) hoặc function RETURNS void: biến @ → không @, TRY/CATCH → EXCEPTION, SCOPE_IDENTITY() → RETURNING."
                    : isFunc
                    ? "Viết lại function sang plpgsql/sql: RETURNS TABLE cho table-valued, kiểm tra IMMUTABLE/STABLE/VOLATILE."
                    : isTrigger
                    ? "Viết lại trigger: PG dùng function RETURNS trigger + CREATE TRIGGER FOR EACH ROW; INSTEAD OF chỉ cho view."
                    : "Kiểm tra tay cú pháp PostgreSQL.",
                DatabaseEngine.SqlServer => isView
                    ? "Viết lại view sang T-SQL: LIMIT → TOP/FETCH, IFNULL()/NVL() → ISNULL(), NOW()/SYSDATE → GETDATE(), || → + (hoặc CONCAT)."
                    : isProc
                    ? "Viết lại procedure sang T-SQL: LIMIT → TOP, EXCEPTION → TRY/CATCH, RETURNING → OUTPUT/SCOPE_IDENTITY()."
                    : isFunc
                    ? "Viết lại function sang T-SQL scalar/inline table-valued; kiểm tra SCHEMABINDING nếu view dùng."
                    : isTrigger
                    ? "Viết lại trigger T-SQL theo inserted/deleted (set-based, không FOR EACH ROW)."
                    : "Kiểm tra tay cú pháp T-SQL.",
                DatabaseEngine.Sqlite => "SQLite không có procedure/function/trigger phức tạp — chuyển logic về tầng ứng dụng; view chỉ SELECT đơn giản, không TOP/LIMIT phức tạp quá mức.",
                _ => "Engine đích chưa hỗ trợ hướng dẫn — xử lý thủ công."
            };
        }

        public async Task<IReadOnlyList<RoutineItem>> ListRoutinesAsync(
            DbProbe probe, DatabaseEngine src, DatabaseEngine dst, CancellationToken ct = default)
        {
            return src switch
            {
                DatabaseEngine.SqlServer => await FromSqlServerAsync(probe, dst, ct).ConfigureAwait(false),
                DatabaseEngine.PostgreSql => await FromPostgresAsync(probe, dst, ct).ConfigureAwait(false),
                DatabaseEngine.MySql => await FromMySqlAsync(probe, dst, ct).ConfigureAwait(false),
                _ => Array.Empty<RoutineItem>()
            };
        }

        private static async Task<IReadOnlyList<RoutineItem>> FromSqlServerAsync(
            DbProbe probe, DatabaseEngine dst, CancellationToken ct)
        {
            var result = new List<RoutineItem>();
            using var conn = new SqlConnection(SqlServerManageProvider.BuildConnectionString(probe));
            await conn.OpenAsync(ct).ConfigureAwait(false);
            using var cmd = new SqlCommand(
                "SELECT s.name, o.name, "
                + "CASE WHEN o.type IN ('V') THEN 'View' "
                + "WHEN o.type IN ('P','PC','X') THEN 'Procedure' "
                + "WHEN o.type IN ('FN','IF','TF','AF','FT') THEN 'Function' "
                + "WHEN o.type IN ('TR') THEN 'Trigger' ELSE o.type END, "
                + "ISNULL(DATALENGTH(m.definition), 0) "
                + "FROM sys.objects o JOIN sys.schemas s ON s.schema_id = o.schema_id "
                + "LEFT JOIN sys.sql_modules m ON m.object_id = o.object_id "
                + "WHERE o.is_ms_shipped = 0 AND o.type IN ('V','P','PC','X','FN','IF','TF','AF','FT','TR') "
                + "ORDER BY 3, 1, 2;", conn);
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var kind = reader.GetString(2);
                result.Add(new RoutineItem
                {
                    Schema = reader.GetString(0),
                    Name = reader.GetString(1),
                    Kind = kind,
                    DefinitionLength = reader.IsDBNull(3) ? 0 : reader.GetInt64(3),
                    Guidance = GuidanceFor(kind, dst)
                });
            }
            return result;
        }

        private static async Task<IReadOnlyList<RoutineItem>> FromPostgresAsync(
            DbProbe probe, DatabaseEngine dst, CancellationToken ct)
        {
            var result = new List<RoutineItem>();
            using var conn = new NpgsqlConnection(DataMove.PostgresEndpoint.BuildConnectionString(probe));
            await conn.OpenAsync(ct).ConfigureAwait(false);
            using (var cmd = new NpgsqlCommand(
                "SELECT routine_schema, routine_name, routine_type FROM information_schema.routines "
                + "WHERE routine_schema NOT IN ('pg_catalog','information_schema') ORDER BY 1, 2;", conn))
            using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    var kind = reader.GetString(2).IndexOf("FUNCTION", StringComparison.OrdinalIgnoreCase) >= 0
                        ? "Function" : "Procedure";
                    result.Add(new RoutineItem
                    {
                        Schema = reader.GetString(0),
                        Name = reader.GetString(1),
                        Kind = kind,
                        Guidance = GuidanceFor(kind, dst)
                    });
                }
            }
            using (var cmd = new NpgsqlCommand(
                "SELECT table_schema, table_name FROM information_schema.views "
                + "WHERE table_schema NOT IN ('pg_catalog','information_schema') ORDER BY 1, 2;", conn))
            using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    result.Add(new RoutineItem
                    {
                        Schema = reader.GetString(0),
                        Name = reader.GetString(1),
                        Kind = "View",
                        Guidance = GuidanceFor("View", dst)
                    });
                }
            }
            return result;
        }

        private static async Task<IReadOnlyList<RoutineItem>> FromMySqlAsync(
            DbProbe probe, DatabaseEngine dst, CancellationToken ct)
        {
            var result = new List<RoutineItem>();
            using var conn = new MySqlConnection(DataMove.MySqlEndpoint.BuildConnectionString(probe));
            await conn.OpenAsync(ct).ConfigureAwait(false);
            using (var cmd = new MySqlCommand(
                "SELECT ROUTINE_SCHEMA, ROUTINE_NAME, ROUTINE_TYPE FROM information_schema.ROUTINES "
                + "ORDER BY 1, 2;", conn))
            using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    var kind = reader.GetString(2).IndexOf("FUNCTION", StringComparison.OrdinalIgnoreCase) >= 0
                        ? "Function" : "Procedure";
                    result.Add(new RoutineItem
                    {
                        Schema = reader.GetString(0),
                        Name = reader.GetString(1),
                        Kind = kind,
                        Guidance = GuidanceFor(kind, dst)
                    });
                }
            }
            using (var cmd = new MySqlCommand(
                "SELECT TABLE_SCHEMA, TABLE_NAME FROM information_schema.VIEWS ORDER BY 1, 2;", conn))
            using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    result.Add(new RoutineItem
                    {
                        Schema = reader.GetString(0),
                        Name = reader.GetString(1),
                        Kind = "View",
                        Guidance = GuidanceFor("View", dst)
                    });
                }
            }
            using (var cmd = new MySqlCommand(
                "SELECT TRIGGER_SCHEMA, TRIGGER_NAME FROM information_schema.TRIGGERS ORDER BY 1, 2;", conn))
            using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    result.Add(new RoutineItem
                    {
                        Schema = reader.GetString(0),
                        Name = reader.GetString(1),
                        Kind = "Trigger",
                        Guidance = GuidanceFor("Trigger", dst)
                    });
                }
            }
            return result;
        }
    }
}
