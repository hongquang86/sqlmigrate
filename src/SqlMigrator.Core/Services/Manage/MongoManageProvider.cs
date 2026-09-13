using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MongoDB.Bson;
using MongoDB.Driver;
using SqlMigrator.Core.Interfaces;
using SqlMigrator.Core.Models;

namespace SqlMigrator.Core.Services.Manage
{
    /// <summary>
    /// Quản trị MongoDB ở mức đọc (Pha 5): dbStats từng database + currentOp
    /// (danh sách operation đang chạy). Chưa kill (giữ read-only cho an toàn).
    /// </summary>
    public sealed class MongoManageProvider : IManageProvider
    {
        public DatabaseEngine Engine => DatabaseEngine.MongoDb;

        internal static string BuildConnectionString(DbProbe probe)
        {
            if (!string.IsNullOrWhiteSpace(probe.Database) && probe.Database.Contains("://"))
                return probe.Database;
            var host = string.IsNullOrWhiteSpace(probe.Host) ? "localhost" : probe.Host;
            var port = probe.Port > 0 ? probe.Port : 27017;
            var auth = string.IsNullOrWhiteSpace(probe.User)
                ? ""
                : Uri.EscapeDataString(probe.User) + ":" + Uri.EscapeDataString(probe.Password ?? "") + "@";
            return $"mongodb://{auth}{host}:{port}/?serverSelectionTimeoutMS={Math.Clamp(probe.TimeoutSeconds > 0 ? probe.TimeoutSeconds : 5, 1, 30) * 1000}";
        }

        public async Task<IReadOnlyList<ManagedDatabase>> ListDatabasesAsync(
            DbProbe probe, CancellationToken ct = default)
        {
            var result = new List<ManagedDatabase>();
            var client = new MongoClient(BuildConnectionString(probe));
            using var cursor = await client.ListDatabasesAsync(ct).ConfigureAwait(false);
            var dbs = await cursor.ToListAsync(ct).ConfigureAwait(false);
            foreach (var db in dbs)
            {
                ct.ThrowIfCancellationRequested();
                var name = db.GetValue("name", "").AsString;
                if (name is "admin" or "local" or "config")
                    continue;
                double mb = -1;
                try
                {
                    var stats = await client.GetDatabase(name)
                        .RunCommandAsync<BsonDocument>(new BsonDocument("dbStats", 1), null, ct)
                        .ConfigureAwait(false);
                    // dataSize + indexSize (bytes) → MB.
                    mb = (stats.GetValue("dataSize", 0).ToDouble()
                        + stats.GetValue("indexSize", 0).ToDouble()) / 1048576;
                }
                catch
                {
                    // Không đọc được stats (thiếu quyền) → vẫn liệt kê database.
                }
                result.Add(new ManagedDatabase
                {
                    Name = name,
                    State = "ONLINE",
                    AllocatedMb = mb < 0 ? 0 : Math.Round(mb, 1),
                    UsedMb = mb < 0 ? -1 : Math.Round(mb, 1)
                });
            }
            return result;
        }

        public async Task<IReadOnlyList<ManagedSession>> ListSessionsAsync(
            DbProbe probe, CancellationToken ct = default)
        {
            var result = new List<ManagedSession>();
            var client = new MongoClient(BuildConnectionString(probe));
            var currentOp = await client.GetDatabase("admin")
                .RunCommandAsync<BsonDocument>(
                    new BsonDocument { { "currentOp", 1 }, { "$all", true } }, null, ct)
                .ConfigureAwait(false);
            if (!currentOp.TryGetValue("inprog", out var inprog) || !inprog.IsBsonArray)
                return result;
            foreach (var op in inprog.AsBsonArray)
            {
                if (op is not BsonDocument doc)
                    continue;
                result.Add(new ManagedSession
                {
                    Id = doc.GetValue("opid", "").ToString() ?? "",
                    Login = doc.GetValue("client", "").ToString() ?? "",
                    Host = doc.GetValue("client", "").ToString() ?? "",
                    Database = doc.GetValue("ns", "").ToString() ?? "",
                    Status = doc.GetValue("state", doc.GetValue("op", "").ToString() ?? "").ToString() ?? "",
                    Command = doc.GetValue("op", "").ToString() ?? "",
                    WaitInfo = doc.GetValue("waitingForLock", false).ToBoolean() ? "waitingForLock" : ""
                });
            }
            return result;
        }

        public Task<KillSessionResult> KillSessionAsync(
            DbProbe probe, string sessionId, CancellationToken ct = default)
        {
            // Pha 5 chỉ đọc với MongoDB — kill qua killOp để dành pha sau cho an toàn.
            return Task.FromResult(new KillSessionResult
            {
                Error = "Pha này chưa hỗ trợ kill operation MongoDB (mới đọc dbStats/currentOp)."
            });
        }
    }
}
