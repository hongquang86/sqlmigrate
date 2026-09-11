using System;
using System.Security.Cryptography;
using System.Text;

namespace SqlMigrator.Core.Security
{
    /// <summary>
    /// Trừu tượng hóa việc mã hóa/giải mã dữ liệu nhạy cảm (mật khẩu, chuỗi kết nối).
    /// Cho phép cài đặt thực tế bằng DPAPI và cài đặt giả trong unit test.
    /// </summary>
    public interface IDataProtector
    {
        /// <summary>Mã hóa một mảng byte.</summary>
        byte[] Protect(byte[] plainBytes);

        /// <summary>Giải mã một mảng byte đã được <see cref="Protect"/>.</summary>
        byte[] Unprotect(byte[] protectedBytes);

        /// <summary>Mã hóa một chuỗi văn bản (UTF-8).</summary>
        string ProtectString(string plainText);

        /// <summary>Giải mã chuỗi đã được <see cref="ProtectString"/>.</summary>
        string UnprotectString(string protectedText);
    }

    /// <summary>
    /// Cài đặt dùng <see cref="ProtectedData"/> (DPAPI) ở phạm vi người dùng hiện tại.
    /// Chỉ tài khoản Windows hiện tại mới giải mã được dữ liệu — phù hợp quy tắc bảo mật
    /// "không bao giờ lưu mật khẩu dạng văn bản thuần (plaintext)".
    /// DPAPI chỉ khả dụng trên Windows; do đó class này được đánh dấu platform.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public sealed class DpapiDataProtector : IDataProtector
    {
        // Entropy cố định, dùng chung cho toàn ứng dụng để thêm một lớp chống giải mã đơn giản
        // từ bên ngoài (dữ liệu mã hóa vẫn gắn với người dùng Windows qua DPAPI).
        private static readonly byte[] AppEntropy = Encoding.UTF8.GetBytes("SqlMigrator-Dpapi-V1");

        public byte[] Protect(byte[] plainBytes)
        {
            if (plainBytes == null) throw new ArgumentNullException(nameof(plainBytes));
            return ProtectedData.Protect(plainBytes, AppEntropy, DataProtectionScope.CurrentUser);
        }

        public byte[] Unprotect(byte[] protectedBytes)
        {
            if (protectedBytes == null) throw new ArgumentNullException(nameof(protectedBytes));
            return ProtectedData.Unprotect(protectedBytes, AppEntropy, DataProtectionScope.CurrentUser);
        }

        public string ProtectString(string plainText)
        {
            if (string.IsNullOrEmpty(plainText)) return string.Empty;
            var bytes = Encoding.UTF8.GetBytes(plainText);
            return Convert.ToBase64String(Protect(bytes));
        }

        public string UnprotectString(string protectedText)
        {
            if (string.IsNullOrEmpty(protectedText)) return string.Empty;
            var bytes = Unprotect(Convert.FromBase64String(protectedText));
            return Encoding.UTF8.GetString(bytes);
        }
    }
}