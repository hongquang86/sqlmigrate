using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using SqlMigrator.Core.Interfaces;
using SqlMigrator.Core.Models;

namespace SqlMigrator.Core.Services.DbProviders
{
    /// <summary>
    /// Provider SQLite (file .db, không có server/port). Pha 1 chỉ nhận diện
    /// bằng cách mở file chỉ đọc và đọc version.
    /// </summary>
    public sealed class SqliteProvider : IDbProvider
    {
        public DatabaseEngine Engine => DatabaseEngine.Sqlite;
        public string DisplayName => "SQLite";
        public int DefaultPort => 0;
        public ProviderCapabilities Capabilities => ProviderCapabilities.DetectedOnly();

        public async Task<EngineInfo?> DetectAsync(DbProbe probe, CancellationToken ct = default)
        {
            // Host ở đây là đường dẫn file .db (UI dùng file picker, không phải ô server).
            if (probe == null || string.IsNullOrWhiteSpace(probe.Host) || !File.Exists(probe.Host))
                return null;
            try
            {
                var builder = new SqliteConnectionStringBuilder
                {
                    DataSource = probe.Host,
                    Mode = SqliteOpenMode.ReadOnly
                };
                using var conn = new SqliteConnection(builder.ConnectionString);
                await conn.OpenAsync(ct).ConfigureAwait(false);
                using var cmd = new SqliteCommand("SELECT sqlite_version();", conn)
                {
                    CommandTimeout = Math.Clamp(probe.TimeoutSeconds, 1, 30) + 5
                };
                var version = (await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))?.ToString() ?? "";
                return new EngineInfo
                {
                    Engine = DatabaseEngine.Sqlite,
                    DisplayName = DisplayName,
                    Version = version.Trim(),
                    MajorVersion = ParseMajor(version),
                    SupportsMigration = false
                };
            }
            catch
            {
                return null;
            }
        }

        internal static int ParseMajor(string version)
        {
            if (string.IsNullOrWhiteSpace(version))
                return 0;
            var head = version.Trim().Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries);
            return head.Length > 0 && int.TryParse(head[0], out var major) ? major : 0;
        }
    }
}
