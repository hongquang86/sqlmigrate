using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using SqlMigrator.Core.Interfaces;
using SqlMigrator.Core.Models;

namespace SqlMigrator.Core.Services.DbProviders
{
    /// <summary>Provider SQL Server: nhận diện version + full capabilities (baseline).</summary>
    public sealed class SqlServerProvider : IDbProvider
    {
        public DatabaseEngine Engine => DatabaseEngine.SqlServer;
        public string DisplayName => "SQL Server";
        public int DefaultPort => 1433;
        public ProviderCapabilities Capabilities => ProviderCapabilities.SqlServerFull();

        public async Task<EngineInfo?> DetectAsync(DbProbe probe, CancellationToken ct = default)
        {
            if (probe == null || string.IsNullOrWhiteSpace(probe.Host))
                return null;
            try
            {
                var dataSource = probe.Port > 0 && !probe.Host.Contains(",") && !probe.Host.Contains("\\")
                    ? probe.Host + "," + probe.Port
                    : probe.Host;
                var builder = new SqlConnectionStringBuilder
                {
                    DataSource = dataSource,
                    InitialCatalog = "master",
                    ConnectTimeout = Math.Clamp(probe.TimeoutSeconds, 1, 30),
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

                using var conn = new SqlConnection(builder.ConnectionString);
                await conn.OpenAsync(ct).ConfigureAwait(false);
                using var cmd = new SqlCommand(
                    "SELECT CAST(SERVERPROPERTY('ProductMajorVersion') AS int), @@VERSION;", conn)
                    { CommandTimeout = Math.Clamp(probe.TimeoutSeconds, 1, 30) + 5 };
                using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                    return null;
                var major = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
                var full = reader.IsDBNull(1) ? "" : reader.GetString(1);
                var firstLine = full.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                return new EngineInfo
                {
                    Engine = DatabaseEngine.SqlServer,
                    DisplayName = DisplayName,
                    Version = firstLine.Length > 0 ? firstLine[0].Trim() : "",
                    MajorVersion = major,
                    SupportsMigration = true
                };
            }
            catch
            {
                return null;
            }
        }
    }
}
