using System;
using System.Threading;
using System.Threading.Tasks;
using MySqlConnector;
using SqlMigrator.Core.Interfaces;
using SqlMigrator.Core.Models;

namespace SqlMigrator.Core.Services.DbProviders
{
    /// <summary>
    /// Provider MySQL/MariaDB (chung protocol, 1 driver MySqlConnector MIT).
    /// Pha 1 chỉ nhận diện version (phân biệt MariaDB qua chuỗi version).
    /// </summary>
    public sealed class MySqlProvider : IDbProvider
    {
        public DatabaseEngine Engine => DatabaseEngine.MySql;
        public string DisplayName => "MySQL / MariaDB";
        public int DefaultPort => 3306;
        public ProviderCapabilities Capabilities => ProviderCapabilities.DetectedOnly();

        public async Task<EngineInfo?> DetectAsync(DbProbe probe, CancellationToken ct = default)
        {
            if (probe == null || string.IsNullOrWhiteSpace(probe.Host))
                return null;
            try
            {
                var builder = new MySqlConnectionStringBuilder
                {
                    Server = probe.Host,
                    Port = (uint)(probe.Port > 0 ? probe.Port : DefaultPort),
                    UserID = string.IsNullOrWhiteSpace(probe.User) ? "root" : probe.User,
                    Password = probe.Password ?? "",
                    ConnectionTimeout = (uint)Math.Clamp(probe.TimeoutSeconds, 1, 30),
                    SslMode = MySqlSslMode.Preferred,
                    Pooling = false
                };
                using var conn = new MySqlConnection(builder.ConnectionString);
                await conn.OpenAsync(ct).ConfigureAwait(false);
                using var cmd = new MySqlCommand("SELECT VERSION();", conn)
                {
                    CommandTimeout = Math.Clamp(probe.TimeoutSeconds, 1, 30) + 5
                };
                var version = (await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))?.ToString() ?? "";
                var isMariaDb = version.IndexOf("mariadb", StringComparison.OrdinalIgnoreCase) >= 0;
                return new EngineInfo
                {
                    Engine = DatabaseEngine.MySql,
                    DisplayName = DisplayName,
                    Version = version.Trim(),
                    MajorVersion = ParseMajor(version),
                    Variant = isMariaDb ? "MariaDB" : "MySQL",
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
            // MariaDB "10.6.12-MariaDB-..." → 10; MySQL "8.0.36" → 8.
            foreach (var token in version.Trim().Split(new[] { ' ', '.', '-', '_' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (int.TryParse(token, out var major))
                    return major;
            }
            return 0;
        }
    }
}
