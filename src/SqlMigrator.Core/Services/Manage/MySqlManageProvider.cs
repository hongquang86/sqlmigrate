using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MySqlConnector;
using SqlMigrator.Core.Interfaces;
using SqlMigrator.Core.Models;

namespace SqlMigrator.Core.Services.Manage
{
    /// <summary>
    /// Quản trị MySQL/MariaDB: đọc database/size qua information_schema.TABLES
    /// (data_usage = data+index, data_free), session qua information_schema.PROCESSLIST,
    /// kill bằng KILL thread_id (id là số lấy từ danh sách nên ghép lệnh an toàn).
    /// </summary>
    public sealed class MySqlManageProvider : IManageProvider
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
                ConnectionTimeout = (uint)Math.Clamp(probe.TimeoutSeconds > 0 ? probe.TimeoutSeconds : 15, 1, 60),
                SslMode = MySqlSslMode.Preferred,
                Pooling = false
            };
            return builder.ConnectionString;
        }

        public async Task<IReadOnlyList<ManagedDatabase>> ListDatabasesAsync(
            DbProbe probe, CancellationToken ct = default)
        {
            var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "information_schema", "mysql", "performance_schema", "sys"
            };
            var sizes = new Dictionary<string, (double Used, double Free)>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var conn = new MySqlConnection(BuildConnectionString(probe));
                await conn.OpenAsync(ct).ConfigureAwait(false);
                using var cmd = new MySqlCommand(
                    "SELECT TABLE_SCHEMA, SUM(DATA_LENGTH + INDEX_LENGTH), SUM(DATA_FREE) "
                    + "FROM information_schema.TABLES GROUP BY TABLE_SCHEMA;", conn);
                using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    var schema = reader.IsDBNull(0) ? "" : reader.GetString(0);
                    if (string.IsNullOrEmpty(schema))
                        continue;
                    sizes[schema] = (
                        reader.IsDBNull(1) ? 0 : Convert.ToDouble(reader.GetValue(1)) / 1048576,
                        reader.IsDBNull(2) ? 0 : Convert.ToDouble(reader.GetValue(2)) / 1048576);
                }
            }
            catch
            {
                // Không đọc được size (thiếu quyền) → vẫn liệt kê database bên dưới.
            }
            var result = new List<ManagedDatabase>();
            foreach (var db in await DataMove.MySqlEndpoint.ListDatabasesAsync(probe, ct).ConfigureAwait(false))
            {
                if (skip.Contains(db))
                    continue;
                sizes.TryGetValue(db, out var size);
                result.Add(new ManagedDatabase
                {
                    Name = db,
                    State = "ONLINE",
                    AllocatedMb = Math.Round(size.Used + size.Free, 1),
                    UsedMb = Math.Round(size.Used, 1)
                });
            }
            return result;
        }

        public async Task<IReadOnlyList<ManagedSession>> ListSessionsAsync(
            DbProbe probe, CancellationToken ct = default)
        {
            var result = new List<ManagedSession>();
            using var conn = new MySqlConnection(BuildConnectionString(probe));
            await conn.OpenAsync(ct).ConfigureAwait(false);
            using var cmd = new MySqlCommand(
                "SELECT ID, USER, HOST, DB, COMMAND, TIME, STATE, INFO "
                + "FROM information_schema.PROCESSLIST ORDER BY ID;", conn);
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                result.Add(new ManagedSession
                {
                    Id = Convert.ToString(reader.GetValue(0)) ?? "",
                    Login = reader.IsDBNull(1) ? "" : reader.GetString(1),
                    Host = reader.IsDBNull(2) ? "" : reader.GetString(2),
                    Database = reader.IsDBNull(3) ? "" : reader.GetString(3),
                    Status = reader.IsDBNull(6) ? "" : reader.GetString(6),
                    Command = reader.IsDBNull(4) ? "" : reader.GetString(4),
                    WaitInfo = reader.IsDBNull(5) ? "" : Convert.ToString(reader.GetValue(5)) ?? "",
                    LoginTime = DateTime.MinValue
                });
            }
            return result;
        }

        public async Task<KillSessionResult> KillSessionAsync(
            DbProbe probe, string sessionId, CancellationToken ct = default)
        {
            if (!long.TryParse(sessionId, out var threadId) || threadId <= 0)
                return new KillSessionResult { Error = "Id phiên không hợp lệ: " + sessionId };
            string before = "";
            try
            {
                before = await SessionStateAsync(probe, threadId, ct).ConfigureAwait(false) ?? "";
                using var conn = new MySqlConnection(BuildConnectionString(probe));
                await conn.OpenAsync(ct).ConfigureAwait(false);
                // KILL không nhận tham số hóa — id đã kiểm tra là số nguyên dương.
                using var cmd = new MySqlCommand("KILL " + threadId + ";", conn) { CommandTimeout = 60 };
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return new KillSessionResult { BeforeStatus = before, Error = ex.Message };
            }
            string? after = null;
            try
            {
                await Task.Delay(1000, ct).ConfigureAwait(false);
                after = await SessionStateAsync(probe, threadId, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch { after = null; }
            return new KillSessionResult
            {
                KillSent = true,
                Confirmed = ManageVerify.IsKillConfirmed(before, after),
                BeforeStatus = before,
                AfterStatus = after ?? "(đã biến mất)"
            };
        }

        private static async Task<string?> SessionStateAsync(
            DbProbe probe, long threadId, CancellationToken ct)
        {
            try
            {
                using var conn = new MySqlConnection(BuildConnectionString(probe));
                await conn.OpenAsync(ct).ConfigureAwait(false);
                using var cmd = new MySqlCommand(
                    "SELECT STATE FROM information_schema.PROCESSLIST WHERE ID = @id;", conn);
                cmd.Parameters.AddWithValue("@id", threadId);
                var value = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
                return value == null || value is DBNull ? null : value.ToString();
            }
            catch
            {
                return null;
            }
        }
    }
}
