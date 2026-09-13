using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using SqlMigrator.Core.Interfaces;
using SqlMigrator.Core.Models;

namespace SqlMigrator.Core.Services.Manage
{
    /// <summary>
    /// Quản trị SQLite (file local, chỉ đọc + kiểm tra): dung lượng file, số bảng,
    /// số dòng từng bảng, PRAGMA integrity_check. Không có khái niệm session/kill.
    /// probe.Host chính là đường dẫn file .db.
    /// </summary>
    public sealed class SqliteManageProvider : IManageProvider
    {
        public DatabaseEngine Engine => DatabaseEngine.Sqlite;

        public Task<IReadOnlyList<ManagedDatabase>> ListDatabasesAsync(
            DbProbe probe, CancellationToken ct = default)
        {
            var path = probe?.Host ?? "";
            if (!File.Exists(path))
                return Task.FromResult<IReadOnlyList<ManagedDatabase>>(Array.Empty<ManagedDatabase>());
            var mb = new FileInfo(path).Length / 1048576.0;
            IReadOnlyList<ManagedDatabase> result = new List<ManagedDatabase>
            {
                new() { Name = Path.GetFileName(path), State = "FILE", AllocatedMb = Math.Round(mb, 1), UsedMb = Math.Round(mb, 1) }
            };
            return Task.FromResult(result);
        }

        public async Task<IReadOnlyList<ManagedSession>> ListSessionsAsync(
            DbProbe probe, CancellationToken ct = default)
        {
            // SQLite không có session server — trả rỗng để UI hiện "không có phiên".
            await Task.CompletedTask.ConfigureAwait(false);
            return Array.Empty<ManagedSession>();
        }

        public Task<KillSessionResult> KillSessionAsync(
            DbProbe probe, string sessionId, CancellationToken ct = default)
        {
            return Task.FromResult(new KillSessionResult
            {
                Error = "SQLite file local không có session để kill."
            });
        }

        /// <summary>Kiểm tra toàn vẹn file (PRAGMA integrity_check, chỉ đọc).</summary>
        public async Task<string> CheckIntegrityAsync(DbProbe probe, CancellationToken ct = default)
        {
            using var conn = new SqliteConnection(
                new SqliteConnectionStringBuilder { DataSource = probe.Host, Mode = SqliteOpenMode.ReadOnly }.ConnectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);
            using var cmd = new SqliteCommand("PRAGMA integrity_check;", conn) { CommandTimeout = 300 };
            return (await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))?.ToString() ?? "?";
        }

        /// <summary>Đếm dòng từng bảng người dùng (để UI hiện quy mô file).</summary>
        public async Task<IReadOnlyList<(string Table, long Rows)>> CountTablesAsync(
            DbProbe probe, CancellationToken ct = default)
        {
            var result = new List<(string, long)>();
            using var conn = new SqliteConnection(
                new SqliteConnectionStringBuilder { DataSource = probe.Host, Mode = SqliteOpenMode.ReadOnly }.ConnectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);
            var names = new List<string>();
            using (var cmd = new SqliteCommand(
                "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name;", conn))
            using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    names.Add(reader.GetString(0));
            }
            foreach (var name in names)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    using var cmd = new SqliteCommand(
                        "SELECT COUNT(*) FROM \"" + name.Replace("\"", "\"\"") + "\";", conn);
                    result.Add((name, Convert.ToInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))));
                }
                catch
                {
                    result.Add((name, -1));
                }
            }
            return result;
        }
    }
}
