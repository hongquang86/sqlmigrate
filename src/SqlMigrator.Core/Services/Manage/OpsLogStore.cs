using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SqlMigrator.Core.Services.Manage
{
    /// <summary>Một dòng lịch sử thao tác quản trị (kill/dump/restore/service/truy vấn...).</summary>
    public sealed class OpsLogEntry
    {
        public DateTime AtUtc { get; set; }
        /// <summary>VD: Kill session, Sao lưu MongoDB, Start service, Truy vấn.</summary>
        public string Action { get; set; } = string.Empty;
        /// <summary>Đối tượng tác động (server/db/phiên — không chứa secret).</summary>
        public string Target { get; set; } = string.Empty;
        public string Detail { get; set; } = string.Empty;
        public bool Success { get; set; }
    }

    /// <summary>
    /// Kho lịch sử thao tác quản trị (file JSON local, tối đa 300 dòng mới nhất).
    /// Chỉ ghi file local; không chạm database nào. Không bao giờ ghi mật khẩu
    /// hay toàn văn câu truy vấn (chỉ ghi loại + số dòng).
    /// </summary>
    public sealed class OpsLogStore
    {
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
        private const int MaxEntries = 300;

        internal static string FilePath
        {
            get
            {
                var folder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SqlMigrator");
                Directory.CreateDirectory(folder);
                return Path.Combine(folder, "ops-log.json");
            }
        }

        public static async Task<IReadOnlyList<OpsLogEntry>> LoadAsync(CancellationToken ct = default)
        {
            if (!File.Exists(FilePath))
                return Array.Empty<OpsLogEntry>();
            try
            {
                await using var stream = File.OpenRead(FilePath);
                return await JsonSerializer.DeserializeAsync<List<OpsLogEntry>>(
                    stream, JsonOptions, ct).ConfigureAwait(false)
                    ?? new List<OpsLogEntry>();
            }
            catch
            {
                return Array.Empty<OpsLogEntry>();
            }
        }

        public static async Task AppendAsync(OpsLogEntry entry, CancellationToken ct = default)
        {
            entry.AtUtc = entry.AtUtc == default ? DateTime.UtcNow : entry.AtUtc;
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
