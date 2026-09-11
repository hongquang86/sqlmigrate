using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace SqlMigrator.Core.Security
{
    /// <summary>Lưu trữ danh sách profile kết nối (dữ liệu mã hóa toàn bộ bằng DPAPI).</summary>
    public interface IConnectionProfileStore
    {
        IReadOnlyList<ConnectionProfile> LoadAll();
        void SaveAll(IEnumerable<ConnectionProfile> profiles);
        void Upsert(ConnectionProfile profile);
        void Delete(string id);
        ConnectionProfile? Get(string id);
    }

    /// <summary>
    /// Lưu các profile dưới dạng JSON được mã hóa toàn bộ bằng DPAPI trong
    /// <c>%AppData%\SqlMigrator\profiles.json</c>. Không bao giờ tồn tại bản plaintext
    /// trên đĩa. Mọi thao tác đọc/ghi đều qua <see cref="IDataProtector"/>.
    /// </summary>
    public sealed class ConnectionProfileStore : IConnectionProfileStore
    {
        private readonly IDataProtector _protector;
        private readonly string _filePath;
        private readonly JsonSerializerOptions _jsonOptions;

        public ConnectionProfileStore(IDataProtector protector, string? filePath = null)
        {
            _protector = protector ?? throw new ArgumentNullException(nameof(protector));
            _filePath = filePath ?? GetDefaultPath();
            _jsonOptions = new JsonSerializerOptions { WriteIndented = false };
        }

        /// <summary>Đường dẫn mặc định tới file profile dưới thư mục AppData của người dùng.</summary>
        internal static string GetDefaultPath()
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "SqlMigrator");
            return Path.Combine(dir, "profiles.json");
        }

        public IReadOnlyList<ConnectionProfile> LoadAll()
        {
            if (!File.Exists(_filePath))
                return Array.Empty<ConnectionProfile>();

            try
            {
                var encrypted = File.ReadAllBytes(_filePath);
                var json = _protector.Unprotect(encrypted);
                var list = JsonSerializer.Deserialize<List<ConnectionProfile>>(json, _jsonOptions);
                return list ?? new List<ConnectionProfile>();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                                       ex is System.Security.Cryptography.CryptographicException ||
                                       ex is JsonException)
            {
                // File hỏng hoặc không giải mã được (ví dụ đổi tài khoản Windows):
                // trả về danh sách rỗng để ứng dụng vẫn khởi động bình thường.
                return Array.Empty<ConnectionProfile>();
            }
        }

        public void SaveAll(IEnumerable<ConnectionProfile> profiles)
        {
            if (profiles == null) throw new ArgumentNullException(nameof(profiles));

            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(profiles, _jsonOptions);
            var encrypted = _protector.Protect(System.Text.Encoding.UTF8.GetBytes(json));
            File.WriteAllBytes(_filePath, encrypted);
        }

        public void Upsert(ConnectionProfile profile)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));

            var profiles = new List<ConnectionProfile>(LoadAll());
            var existing = profiles.FindIndex(p => string.Equals(p.Id, profile.Id, StringComparison.Ordinal));
            if (existing >= 0)
                profiles[existing] = profile;
            else
                profiles.Add(profile);

            SaveAll(profiles);
        }

        public void Delete(string id)
        {
            var profiles = new List<ConnectionProfile>(LoadAll());
            profiles.RemoveAll(p => string.Equals(p.Id, id, StringComparison.Ordinal));
            SaveAll(profiles);
        }

        public ConnectionProfile? Get(string id)
        {
            foreach (var profile in LoadAll())
            {
                if (string.Equals(profile.Id, id, StringComparison.Ordinal))
                    return profile;
            }

            return null;
        }
    }
}