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
    /// Lịch sử sao lưu/khôi phục (file JSON local, tối đa 200 dòng mới nhất).
    /// Chỉ ghi file local; không chạm database nào.
    /// </summary>
    public sealed class BackupHistoryStore
    {
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
        private const int MaxEntries = 200;

        private static string FilePath
        {
            get
            {
                var folder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SqlMigrator");
                Directory.CreateDirectory(folder);
                return Path.Combine(folder, "backup-history.json");
            }
        }

        public static async Task<IReadOnlyList<BackupHistoryEntry>> LoadAsync(
            CancellationToken ct = default)
        {
            if (!File.Exists(FilePath))
                return Array.Empty<BackupHistoryEntry>();
            try
            {
                await using var stream = File.OpenRead(FilePath);
                return await JsonSerializer.DeserializeAsync<List<BackupHistoryEntry>>(
                    stream, JsonOptions, ct).ConfigureAwait(false)
                    ?? new List<BackupHistoryEntry>();
            }
            catch
            {
                return Array.Empty<BackupHistoryEntry>();
            }
        }

        public static async Task AppendAsync(BackupHistoryEntry entry, CancellationToken ct = default)
        {
            var list = (await LoadAsync(ct).ConfigureAwait(false)).ToList();
            list.Insert(0, entry);
            while (list.Count > MaxEntries)
                list.RemoveAt(list.Count - 1);
            var json = JsonSerializer.Serialize(list, JsonOptions);
            await File.WriteAllTextAsync(FilePath, json, System.Text.Encoding.UTF8, ct)
                .ConfigureAwait(false);
        }
    }
}
