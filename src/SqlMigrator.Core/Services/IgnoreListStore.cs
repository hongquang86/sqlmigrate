using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SqlMigrator.Core.Models;

namespace SqlMigrator.Core.Services
{
    /// <summary>
    /// Lưu danh sách đối tượng user chấp nhận bỏ qua vĩnh viễn, theo từng cặp
    /// nguồn→đích (file JSON local, không bao giờ lên server hay git).
    /// Chỉ lưu trữ local; không đọc/ghi database nào.
    /// </summary>
    public sealed class IgnoreListStore
    {
        private readonly ILogger _logger;
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        public IgnoreListStore(ILogger logger)
        {
            _logger = logger;
        }

        private static string BaseFolder
        {
            get
            {
                var folder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SqlMigrator", "Ignored");
                Directory.CreateDirectory(folder);
                return folder;
            }
        }

        private static string FilePathOf(string sourceConnectionString, string destinationConnectionString) =>
            Path.Combine(BaseFolder,
                SyncBaselineStore.KeyOf(sourceConnectionString, destinationConnectionString) + ".json");

        /// <summary>Đọc danh sách (rỗng nếu chưa có/hỏng file — không ném lỗi).</summary>
        public async Task<List<IgnoredObject>> LoadAsync(
            string sourceConnectionString, string destinationConnectionString,
            CancellationToken ct = default)
        {
            var path = FilePathOf(sourceConnectionString, destinationConnectionString);
            if (!File.Exists(path))
                return new List<IgnoredObject>();
            try
            {
                await using var stream = File.OpenRead(path);
                var list = await JsonSerializer.DeserializeAsync<List<IgnoredObject>>(
                    stream, JsonOptions, ct).ConfigureAwait(false);
                return list ?? new List<IgnoredObject>();
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Không đọc được danh sách bỏ qua '{Path}': {Message}", path, ex.Message);
                return new List<IgnoredObject>();
            }
        }

        public async Task SaveAsync(
            string sourceConnectionString, string destinationConnectionString,
            IEnumerable<IgnoredObject> items, CancellationToken ct = default)
        {
            var path = FilePathOf(sourceConnectionString, destinationConnectionString);
            var json = JsonSerializer.Serialize(items.ToList(), JsonOptions);
            await File.WriteAllTextAsync(path, json, System.Text.Encoding.UTF8, ct).ConfigureAwait(false);
        }

        /// <summary>Thêm các mục (gộp trùng theo loại+schema+tên, giữ bản cũ).</summary>
        public async Task<int> AddAsync(
            string sourceConnectionString, string destinationConnectionString,
            IEnumerable<IgnoredObject> entries, CancellationToken ct = default)
        {
            var current = await LoadAsync(sourceConnectionString, destinationConnectionString, ct)
                .ConfigureAwait(false);
            var added = 0;
            foreach (var e in entries)
            {
                var key = IgnoreMatcher.CanonicalKey(e.ObjectType, e.Schema, e.Name);
                var exists = current.Any(c =>
                    IgnoreMatcher.CanonicalKey(c.ObjectType, c.Schema, c.Name)
                        .Equals(key, StringComparison.OrdinalIgnoreCase));
                if (exists)
                    continue;
                if (e.IgnoredAtUtc == default)
                    e.IgnoredAtUtc = DateTime.UtcNow;
                current.Add(e);
                added++;
            }
            if (added > 0)
                await SaveAsync(sourceConnectionString, destinationConnectionString, current, ct)
                    .ConfigureAwait(false);
            return added;
        }

        /// <summary>Xóa một mục. Trả true nếu có xóa.</summary>
        public async Task<bool> RemoveAsync(
            string sourceConnectionString, string destinationConnectionString,
            string objectType, string schema, string name, CancellationToken ct = default)
        {
            var current = await LoadAsync(sourceConnectionString, destinationConnectionString, ct)
                .ConfigureAwait(false);
            var key = IgnoreMatcher.CanonicalKey(objectType, schema, name);
            var removed = current.RemoveAll(c =>
                IgnoreMatcher.CanonicalKey(c.ObjectType, c.Schema, c.Name)
                    .Equals(key, StringComparison.OrdinalIgnoreCase));
            if (removed > 0)
                await SaveAsync(sourceConnectionString, destinationConnectionString, current, ct)
                    .ConfigureAwait(false);
            return removed > 0;
        }

        /// <summary>Xóa toàn bộ danh sách của cặp nguồn→đích.</summary>
        public async Task ClearAsync(
            string sourceConnectionString, string destinationConnectionString,
            CancellationToken ct = default)
        {
            var path = FilePathOf(sourceConnectionString, destinationConnectionString);
            if (File.Exists(path))
                File.Delete(path);
            await Task.CompletedTask.ConfigureAwait(false);
        }
    }
}
