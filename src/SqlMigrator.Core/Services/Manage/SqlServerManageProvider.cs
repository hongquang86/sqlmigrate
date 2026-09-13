using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using SqlMigrator.Core.Interfaces;
using SqlMigrator.Core.Models;

namespace SqlMigrator.Core.Services.Manage
{
    /// <summary>
    /// Quản trị SQL Server: đọc database/size qua sys.databases + sys.master_files
    /// (used qua FILEPROPERTY từng file data), session qua sys.dm_exec_sessions,
    /// kill bằng KILL spid (spid là số lấy từ danh sách nên ghép lệnh an toàn).
    /// </summary>
    public sealed class SqlServerManageProvider : IManageProvider
    {
        public DatabaseEngine Engine => DatabaseEngine.SqlServer;

        internal static string BuildConnectionString(DbProbe probe)
        {
            var dataSource = probe.Port > 0 && !probe.Host.Contains(",") && !probe.Host.Contains("\\")
                ? probe.Host + "," + probe.Port
                : probe.Host;
            var builder = new SqlConnectionStringBuilder
            {
                DataSource = dataSource,
                InitialCatalog = "master",
                ConnectTimeout = Math.Clamp(probe.TimeoutSeconds > 0 ? probe.TimeoutSeconds : 15, 1, 60),
                Encrypt = false,
                TrustServerCertificate = true,
                Pooling = false
            };
            if (probe.UseWindowsAuth || string.IsNullOrWhiteSpace(probe.User))
                builder.IntegratedSecurity = true;
            else
            {
                builder.UserID = probe.User;
                builder.Password = probe.Password;
            }
            return builder.ConnectionString;
        }

        public async Task<IReadOnlyList<ManagedDatabase>> ListDatabasesAsync(
            DbProbe probe, CancellationToken ct = default)
        {
            var result = new List<ManagedDatabase>();
            using var conn = new SqlConnection(BuildConnectionString(probe));
            await conn.OpenAsync(ct).ConfigureAwait(false);
            var infos = new List<(int Id, string Name, string State)>();
            using (var cmd = new SqlCommand(
                "SELECT database_id, name, state_desc FROM sys.databases ORDER BY name;", conn))
            using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    infos.Add((reader.GetInt32(0), reader.GetString(1), reader.GetString(2)));
            }
            foreach (var (id, name, state) in infos)
            {
                ct.ThrowIfCancellationRequested();
                double allocated = 0;
                double used = -1;
                try
                {
                    using var sizeCmd = new SqlCommand(
                        "SELECT ISNULL(SUM(CASE WHEN type_desc = 'ROWS' THEN size ELSE 0 END), 0), "
                        + "ISNULL(SUM(size), 0) FROM sys.master_files WHERE database_id = @id;", conn);
                    sizeCmd.Parameters.AddWithValue("@id", id);
                    using var sizeReader = await sizeCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                    if (await sizeReader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        // size tính bằng trang 8KB; chỉ tính file ROWS cho data used gần đúng.
                        allocated = Convert.ToDouble(sizeReader.GetValue(1)) * 8 / 1024;
                        used = Convert.ToDouble(sizeReader.GetValue(0)) * 8 / 1024;
                    }
                }
                catch
                {
                    // Không đọc được size (thiếu quyền VIEW SERVER STATE...) → vẫn trả database.
                }
                result.Add(new ManagedDatabase
                {
                    Name = name,
                    State = state,
                    AllocatedMb = Math.Round(allocated, 1),
                    UsedMb = used < 0 ? -1 : Math.Round(used, 1)
                });
            }
            return result;
        }

        public async Task<IReadOnlyList<ManagedSession>> ListSessionsAsync(
            DbProbe probe, CancellationToken ct = default)
        {
            var result = new List<ManagedSession>();
            using var conn = new SqlConnection(BuildConnectionString(probe));
            await conn.OpenAsync(ct).ConfigureAwait(false);
            using var cmd = new SqlCommand(
                "SELECT s.session_id, ISNULL(s.login_name, ''), ISNULL(s.host_name, ''), "
                + "ISNULL(DB_NAME(r.database_id), ''), s.status, "
                + "ISNULL(r.command, ''), ISNULL(r.wait_type, ''), s.login_time "
                + "FROM sys.dm_exec_sessions s LEFT JOIN sys.dm_exec_requests r "
                + "ON r.session_id = s.session_id "
                + "WHERE s.is_user_process = 1 ORDER BY s.session_id;", conn);
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                result.Add(new ManagedSession
                {
                    Id = reader.GetInt32(0).ToString(),
                    Login = reader.GetString(1),
                    Host = reader.GetString(2),
                    Database = reader.GetString(3),
                    Status = reader.GetString(4),
                    Command = reader.GetString(5),
                    WaitInfo = reader.GetString(6),
                    LoginTime = reader.IsDBNull(7) ? DateTime.MinValue : reader.GetDateTime(7)
                });
            }
            return result;
        }

        public async Task<KillSessionResult> KillSessionAsync(
            DbProbe probe, string sessionId, CancellationToken ct = default)
        {
            if (!int.TryParse(sessionId, out var spid) || spid <= 0)
                return new KillSessionResult { Error = "Id phiên không hợp lệ: " + sessionId };
            string before = "";
            try
            {
                before = await SessionStatusAsync(probe, spid, ct).ConfigureAwait(false) ?? "";
                using var conn = new SqlConnection(BuildConnectionString(probe));
                await conn.OpenAsync(ct).ConfigureAwait(false);
                // KILL không nhận tham số hóa — spid đã kiểm tra là số nguyên dương.
                using var cmd = new SqlCommand("KILL " + spid + ";", conn) { CommandTimeout = 60 };
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return new KillSessionResult { BeforeStatus = before, Error = ex.Message };
            }
            // Xác nhận theo state đổi: đọc lại sau kill.
            string? after = null;
            try
            {
                await Task.Delay(1000, ct).ConfigureAwait(false);
                after = await SessionStatusAsync(probe, spid, ct).ConfigureAwait(false);
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

        private static async Task<string?> SessionStatusAsync(
            DbProbe probe, int spid, CancellationToken ct)
        {
            try
            {
                using var conn = new SqlConnection(BuildConnectionString(probe));
                await conn.OpenAsync(ct).ConfigureAwait(false);
                using var cmd = new SqlCommand(
                    "SELECT status FROM sys.dm_exec_sessions WHERE session_id = @id;", conn);
                cmd.Parameters.AddWithValue("@id", spid);
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
