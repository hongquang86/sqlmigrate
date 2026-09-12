using System;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using SqlMigrator.Core.Interfaces;
using SqlMigrator.Core.Models;

namespace SqlMigrator.Core.Services.DbProviders
{
    /// <summary>Provider PostgreSQL: Pha 1 chỉ nhận diện version (migrate ở pha sau).</summary>
    public sealed class PostgreSqlProvider : IDbProvider
    {
        public DatabaseEngine Engine => DatabaseEngine.PostgreSql;
        public string DisplayName => "PostgreSQL";
        public int DefaultPort => 5432;
        public ProviderCapabilities Capabilities => ProviderCapabilities.DetectedOnly();

        public async Task<EngineInfo?> DetectAsync(DbProbe probe, CancellationToken ct = default)
        {
            if (probe == null || string.IsNullOrWhiteSpace(probe.Host))
                return null;
            try
            {
                var builder = new NpgsqlConnectionStringBuilder
                {
                    Host = probe.Host,
                    Port = probe.Port > 0 ? probe.Port : DefaultPort,
                    Username = string.IsNullOrWhiteSpace(probe.User) ? "postgres" : probe.User,
                    Password = probe.Password ?? "",
                    Database = "postgres",
                    Timeout = Math.Clamp(probe.TimeoutSeconds, 1, 30),
                    SslMode = SslMode.Prefer,
                    Pooling = false
                };
                using var conn = new NpgsqlConnection(builder.ConnectionString);
                await conn.OpenAsync(ct).ConfigureAwait(false);
                using var cmd = new NpgsqlCommand("SHOW server_version;", conn)
                {
                    CommandTimeout = Math.Clamp(probe.TimeoutSeconds, 1, 30) + 5
                };
                var version = (await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))?.ToString() ?? "";
                return new EngineInfo
                {
                    Engine = DatabaseEngine.PostgreSql,
                    DisplayName = DisplayName,
                    Version = version.Trim(),
                    MajorVersion = ParseMajor(version),
                    SupportsMigration = MigrationGuard.IsSupportedPair(
                        DatabaseEngine.PostgreSql, DatabaseEngine.SqlServer)
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
            var head = version.Trim().Split(new[] { ' ', '.', '-', '_' }, StringSplitOptions.RemoveEmptyEntries);
            return head.Length > 0 && int.TryParse(head[0], out var major) ? major : 0;
        }
    }
}
