using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SqlMigrator.Core.Models;

namespace SqlMigrator.Core.Services
{
    /// <summary>
    /// Ghi/đọc checkpoint (mốc keyset từng bảng) để di chuyển dữ liệu giữa nguồn–đích có
    /// thể tiếp tục từ nơi dừng khi bị đứt/tắt giữa chừng. File chỉ chứa mốc + số dòng,
    /// không chứa kết nối hay mật khẩu; tên file là hash của cặp server.
    /// </summary>
    public sealed class TransferCheckpointStore
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        private readonly ILogger _logger;

        public TransferCheckpointStore(ILogger logger)
        {
            _logger = logger;
        }

        public static string BaseFolder
        {
            get
            {
                var folder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SqlMigrator", "transfers");
                Directory.CreateDirectory(folder);
                return folder;
            }
        }

        /// <summary>
        /// Khóa note lỏng: kết hợp khóa hash của cặp nguồn–đích với tên bảng để mỗi bảng
        /// có một mốc riêng nhưng gom chung vào một file duy nhất.
        /// </summary>
        public static string KeyOf(string sourceConnectionString, string destinationConnectionString) =>
            SyncBaselineStore.KeyOf(sourceConnectionString, destinationConnectionString);

        private static string FilePathOf(string key) =>
            Path.Combine(BaseFolder, (string.IsNullOrWhiteSpace(key) ? "unknown" : key) + ".json");

        public async Task<TransferCheckpointFile?> LoadAsync(string sourceConnectionString, string destinationConnectionString,
            CancellationToken ct = default)
        {
            var key = KeyOf(sourceConnectionString, destinationConnectionString);
            var path = FilePathOf(key);
            if (!File.Exists(path))
                return null;

            try
            {
                await using (var stream = File.OpenRead(path))
                {
                    return await JsonSerializer.DeserializeAsync<TransferCheckpointFile>(stream,
                        JsonOptions, ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Không đọc được checkpoint '{Path}': {Message}", path, ex.Message);
                return null;
            }
        }

        public Task SaveAsync(TransferCheckpointFile file, CancellationToken ct = default)
            => SaveAsync(file, FilePathOf(file.Key), ct);

        private async Task SaveAsync(TransferCheckpointFile file, string path, CancellationToken ct)
        {
            try
            {
                var tmp = path + ".tmp";
                file.UpdatedUtc = DateTime.UtcNow;
                await using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await JsonSerializer.SerializeAsync(stream, file, JsonOptions, ct).ConfigureAwait(false);
                    await stream.FlushAsync(ct).ConfigureAwait(false);
                }

                File.Move(tmp, path, overwrite: true);
            }
            catch (Exception ex)
            {
                // Lỗi ghi checkpoint KHÔNG được làm hỏng tiến trình di chuyển:
                // chỉ báo log, transfer vẫn tiếp tục (mất khả năng hồi tục là đáng tiếc, không gây sai dữ liệu).
                _logger.LogWarning("Không ghi được checkpoint '{Path}': {Message}", path, ex.Message);
            }
        }

        public Task DeleteAsync(string sourceConnectionString, string destinationConnectionString)
        {
            var path = Path.Combine(BaseFolder, KeyOf(sourceConnectionString, destinationConnectionString) + ".json");
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Không xóa được checkpoint '{Path}': {Message}", path, ex.Message);
            }
            return Task.CompletedTask;
        }

        /// <summary>Cập nhật mốc một bảng (tạo mới file nếu chưa có).</summary>
        public void UpdateEntry(TransferCheckpointFile file, TransferCheckpointEntry entry)
        {
            file.Upsert(entry);
        }
    }
}