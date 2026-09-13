using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SqlMigrator.Core.Models;

namespace SqlMigrator.Core.Services.Backup
{
    /// <summary>
    /// Kho job sao lưu tự động (file JSON local, cùng thư mục lịch sử).
    /// Chỉ ghi file local; không chạm database nào.
    /// </summary>
    public sealed class BackupJobStore
    {
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        internal static string FilePath
        {
            get
            {
                var folder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SqlMigrator");
                Directory.CreateDirectory(folder);
                return Path.Combine(folder, "backup-jobs.json");
            }
        }

        public static async Task<IReadOnlyList<BackupJob>> LoadAsync(CancellationToken ct = default)
        {
            if (!File.Exists(FilePath))
                return Array.Empty<BackupJob>();
            try
            {
                await using var stream = File.OpenRead(FilePath);
                return await JsonSerializer.DeserializeAsync<List<BackupJob>>(
                    stream, JsonOptions, ct).ConfigureAwait(false)
                    ?? new List<BackupJob>();
            }
            catch
            {
                return Array.Empty<BackupJob>();
            }
        }

        public static async Task SaveAsync(IEnumerable<BackupJob> jobs, CancellationToken ct = default)
        {
            var json = JsonSerializer.Serialize(jobs.ToList(), JsonOptions);
            await File.WriteAllTextAsync(FilePath, json, System.Text.Encoding.UTF8, ct)
                .ConfigureAwait(false);
        }

        public async Task<BackupJob?> FindAsync(string id, CancellationToken ct = default)
        {
            var jobs = await LoadAsync(ct).ConfigureAwait(false);
            foreach (var job in jobs)
            {
                if (job.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                    return job;
            }
            return null;
        }
    }
}
