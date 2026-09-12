using System;
using System.Threading;
using System.Threading.Tasks;
using MongoDB.Bson;
using MongoDB.Driver;
using SqlMigrator.Core.Interfaces;
using SqlMigrator.Core.Models;

namespace SqlMigrator.Core.Services.DbProviders
{
    /// <summary>Provider MongoDB: Pha 1 chỉ ping + đọc version (migrate ở pha sau).</summary>
    public sealed class MongoDbProvider : IDbProvider
    {
        public DatabaseEngine Engine => DatabaseEngine.MongoDb;
        public string DisplayName => "MongoDB";
        public int DefaultPort => 27017;
        public ProviderCapabilities Capabilities => ProviderCapabilities.DetectedOnly();

        public async Task<EngineInfo?> DetectAsync(DbProbe probe, CancellationToken ct = default)
        {
            if (probe == null || string.IsNullOrWhiteSpace(probe.Host))
                return null;
            try
            {
                var credential = string.IsNullOrWhiteSpace(probe.User)
                    ? ""
                    : Uri.EscapeDataString(probe.User) + ":" + Uri.EscapeDataString(probe.Password ?? "") + "@";
                var port = probe.Port > 0 ? probe.Port : DefaultPort;
                var settings = MongoClientSettings.FromConnectionString(
                    $"mongodb://{credential}{probe.Host}:{port}/?serverSelectionTimeoutMS={Math.Clamp(probe.TimeoutSeconds, 1, 30) * 1000}");
                var client = new MongoClient(settings);
                var admin = client.GetDatabase("admin");
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                linked.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(probe.TimeoutSeconds, 1, 30) + 5));
                var info = await admin.RunCommandAsync<BsonDocument>(
                    new BsonDocument("buildinfo", 1), cancellationToken: linked.Token).ConfigureAwait(false);
                var version = info.TryGetValue("version", out var v) ? v.AsString : "";
                return new EngineInfo
                {
                    Engine = DatabaseEngine.MongoDb,
                    DisplayName = DisplayName,
                    Version = (version ?? "").Trim(),
                    MajorVersion = ParseMajor(version ?? ""),
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
