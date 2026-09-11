using System;
using Microsoft.Data.SqlClient;

namespace SqlMigrator.Core.Security
{
    /// <summary>
    /// Dựng chuỗi kết nối an toàn từ <see cref="ConnectionProfile"/>.
    /// Luôn bật mã hóa kết nối (Encrypt) và <c>Persist Security Info = false</c> để
    /// driver không giữ lại mật khẩu sau khi mở kết nối. Mật khẩu được giải mã bằng
    /// DPAPI ngay tại thời điểm dựng chuỗi — chuỗi kết quả không bao giờ được đưa vào
    /// nhật ký hoặc in ra giao diện.
    /// </summary>
    public sealed class SecureConnectionStringBuilder
    {
        private const string AppName = "SqlMigrator";
        private readonly IDataProtector _protector;

        public SecureConnectionStringBuilder(IDataProtector protector)
        {
            _protector = protector ?? throw new ArgumentNullException(nameof(protector));
        }

        /// <summary>Dựng chuỗi kết nối hoàn chỉnh từ profile.</summary>
        public string Build(ConnectionProfile profile, int connectTimeoutSeconds = 30)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            if (string.IsNullOrWhiteSpace(profile.Server))
                throw new InvalidOperationException("Chưa nhập tên server.");

            var builder = new SqlConnectionStringBuilder
            {
                DataSource = profile.Server,
                InitialCatalog = profile.Database,
                ApplicationName = AppName,
                ConnectTimeout = connectTimeoutSeconds,
                // Bảo mật: luôn bật mã hóa truyền tải TLS khi server hỗ trợ.
                Encrypt = profile.EncryptConnection,
                TrustServerCertificate = profile.TrustServerCertificate,
                // Không để driver giữ lại mật khẩu sau khi mở kết nối.
                PersistSecurityInfo = false,
                Pooling = true,
                MultipleActiveResultSets = false
            };

            if (profile.Authentication == AuthenticationMode.Windows)
            {
                builder.IntegratedSecurity = true;
            }
            else
            {
                builder.IntegratedSecurity = false;
                builder.UserID = profile.UserName ?? string.Empty;
                var password = GetPassword(profile);
                if (!string.IsNullOrEmpty(password))
                    builder.Password = password;
            }

            return builder.ConnectionString;
        }

        /// <summary>
        /// Lấy mật khẩu đã giải mã của profile. Trả về <c>null</c> nếu không có mật khẩu.
        /// Lưu ý: kết quả là bí mật, chỉ dùng trực tiếp khi thật sự cần.
        /// </summary>
        public string? GetPassword(ConnectionProfile profile)
        {
            if (profile == null) return null;
            if (profile.ProtectedPassword == null || profile.ProtectedPassword.Length == 0)
                return null;

            var bytes = _protector.Unprotect(profile.ProtectedPassword);
            return bytes.Length == 0 ? null : System.Text.Encoding.UTF8.GetString(bytes);
        }

        /// <summary>Mã hóa mật khẩu văn bản thuần rồi gán vào profile (không lưu plaintext).</summary>
        public void SetPassword(ConnectionProfile profile, string? plainPassword)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            if (string.IsNullOrEmpty(plainPassword))
            {
                profile.ProtectedPassword = Array.Empty<byte>();
                return;
            }

            var bytes = System.Text.Encoding.UTF8.GetBytes(plainPassword);
            profile.ProtectedPassword = _protector.Protect(bytes);
        }
    }
}