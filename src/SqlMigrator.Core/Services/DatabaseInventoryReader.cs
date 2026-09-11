using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using SqlMigrator.Core.Models;

namespace SqlMigrator.Core.Services
{
    /// <summary>
    /// Đọc hồ sơ kiểm kê database bằng các SELECT catalog (sys.objects, sys.tables,
    /// sys.sequences, sys.partitions...). CHỈ ĐỌC — không sửa bất cứ thứ gì.
    /// Mỗi truy vấn bọc try/catch riêng nên thiếu quyền/phiên bản cũ chỉ làm
    /// mất một phần số liệu, không sập cả lần đọc.
    /// </summary>
    public sealed class DatabaseInventoryReader
    {
        private readonly ILogger _logger;

        public DatabaseInventoryReader(ILogger logger)
        {
            _logger = logger;
        }

        public async Task<DatabaseInventory> ReadAsync(string connectionString, CancellationToken ct = default)
        {
            var tables = new List<InventoryObject>();
            var views = new List<InventoryObject>();
            var procs = new List<InventoryObject>();
            var funcs = new List<InventoryObject>();
            var triggers = new List<InventoryObject>();
            var sequences = new List<string>();
            long totalColumns = 0;
            long estimatedRows = 0;
            var temporal = -1;
            var masked = -1;
            var rls = -1;
            var encrypted = -1;
            var serverTriggers = -1;
            string dbName = "";
            var major = 0;

            try
            {
                var builder = new SqlConnectionStringBuilder(connectionString);
                dbName = builder.InitialCatalog ?? "";

                using var conn = new SqlConnection(connectionString);
                await conn.OpenAsync(ct).ConfigureAwait(false);

                major = await ReadMajorAsync(conn, ct).ConfigureAwait(false);

                // Đối tượng theo loại (schema rỗng với Database DDL trigger → ISNULL).
                using (var cmd = new SqlCommand(
                    "SELECT type, ISNULL(SCHEMA_NAME(schema_id), ''), name FROM sys.objects " +
                    "WHERE is_ms_shipped = 0 AND type IN ('U','V','P','FN','IF','TF','TR');", conn)
                    { CommandTimeout = 60 })
                using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        // Trim vì sys.objects.type là char(2) ("U " có dấu cách đuôi).
                        var prefix = InventoryComparer.InventoryKey(reader.GetString(0));
                        if (prefix == null)
                            continue;
                        var obj = new InventoryObject
                        {
                            Type = reader.GetString(0).Trim(),
                            Schema = reader.GetString(1),
                            Name = reader.GetString(2)
                        };
                        switch (prefix)
                        {
                            case "TABLE:": tables.Add(obj); break;
                            case "VIEW:": views.Add(obj); break;
                            case "P:": procs.Add(obj); break;
                            case "F:": funcs.Add(obj); break;
                            case "TR:": triggers.Add(obj); break;
                        }
                    }
                }

                // Sequence (SQL 2012+).
                if (major >= 11)
                    sequences = await ReadStringsAsync(conn,
                        "SELECT ISNULL(SCHEMA_NAME(schema_id), '') + '.' + name FROM sys.sequences;",
                        ct).ConfigureAwait(false);

                // Tổng số cột bảng người dùng.
                totalColumns = await ReadLongAsync(conn,
                    "SELECT COUNT(*) FROM sys.columns c " +
                    "JOIN sys.tables t ON t.object_id = c.object_id WHERE t.is_ms_shipped = 0;",
                    ct).ConfigureAwait(false);

                // Ước lượng tổng số dòng (rẻ hơn COUNT từng bảng).
                estimatedRows = await ReadLongAsync(conn,
                    "SELECT COALESCE(SUM(p.rows), 0) FROM sys.tables t " +
                    "JOIN sys.partitions p ON p.object_id = t.object_id AND p.index_id IN (0, 1) " +
                    "WHERE t.is_ms_shipped = 0;",
                    ct).ConfigureAwait(false);

                // Cờ tính năng 2016+ (major 13).
                if (major >= 13)
                {
                    temporal = (int)await ReadLongAsync(conn,
                        "SELECT COUNT(*) FROM sys.tables WHERE temporal_type = 2;", ct).ConfigureAwait(false);
                    masked = (int)await ReadLongAsync(conn,
                        "SELECT COUNT(*) FROM sys.columns WHERE is_masked = 1;", ct).ConfigureAwait(false);
                    rls = (int)await ReadLongAsync(conn,
                        "SELECT COUNT(*) FROM sys.security_policies;", ct).ConfigureAwait(false);
                    encrypted = (int)await ReadLongAsync(conn,
                        "SELECT COUNT(*) FROM sys.columns WHERE encryption_type IS NOT NULL;", ct).ConfigureAwait(false);
                }

                // Trigger cấp server (cần VIEW ANY DEFINITION; lỗi thì bỏ qua).
                try
                {
                    serverTriggers = (int)await ReadLongAsync(conn,
                        "SELECT COUNT(*) FROM sys.server_triggers WHERE is_disabled = 0;", ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug("Không đọc được sys.server_triggers ({Message}).", ex.Message);
                }

                return new DatabaseInventory
                {
                    DatabaseName = dbName,
                    ServerMajorVersion = major,
                    Ok = true,
                    Tables = tables,
                    Views = views,
                    Procedures = procs,
                    Functions = funcs,
                    Triggers = triggers,
                    Sequences = sequences,
                    TotalColumns = totalColumns,
                    EstimatedRows = estimatedRows,
                    TemporalTables = temporal,
                    MaskedColumns = masked,
                    RlsPolicies = rls,
                    EncryptedColumns = encrypted,
                    ServerTriggers = serverTriggers
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Không đọc được hồ sơ database '{Db}': {Message}", dbName, ex.Message);
                return new DatabaseInventory { DatabaseName = dbName, Ok = false, Error = ex.Message };
            }
        }

        private static async Task<int> ReadMajorAsync(SqlConnection conn, CancellationToken ct)
        {
            try
            {
                using var cmd = new SqlCommand(
                    "SELECT CAST(SERVERPROPERTY('ProductMajorVersion') AS int);", conn)
                    { CommandTimeout = 30 };
                return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
            }
            catch
            {
                return 0;
            }
        }

        private static async Task<List<string>> ReadStringsAsync(
            SqlConnection conn, string sql, CancellationToken ct)
        {
            var list = new List<string>();
            using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 60 };
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                list.Add(reader.GetString(0));
            return list;
        }

        private static async Task<long> ReadLongAsync(SqlConnection conn, string sql, CancellationToken ct)
        {
            using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 120 };
            return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
        }
    }
}
