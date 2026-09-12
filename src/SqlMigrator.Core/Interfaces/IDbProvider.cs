using System.Threading;
using System.Threading.Tasks;
using SqlMigrator.Core.Models;

namespace SqlMigrator.Core.Interfaces
{
    /// <summary>Thông tin mở kết nối probing (không chứa mật khẩu văn bản thuần khi log).</summary>
    public sealed class DbProbe
    {
        /// <summary>Host/instance (SQL/PG/MySQL/Mongo) hoặc đường dẫn file (.db SQLite).</summary>
        public string Host { get; init; } = string.Empty;
        public int Port { get; init; }
        /// <summary>Database/file mặc định (có thể rỗng với detect).</summary>
        public string Database { get; init; } = string.Empty;
        public string User { get; init; } = string.Empty;
        public string Password { get; init; } = string.Empty;
        public bool UseWindowsAuth { get; init; }
        /// <summary>Timeout mỗi lần thử (giây).</summary>
        public int TimeoutSeconds { get; init; } = 3;
    }

    /// <summary>
    /// Cổng giao tiếp của một hệ CSDL: nhận diện version + khai báo khả năng.
    /// Pha migrate/backup/manage chi tiết của từng engine triển khai ở các pha sau.
    /// </summary>
    public interface IDbProvider
    {
        DatabaseEngine Engine { get; }
        string DisplayName { get; }
        /// <summary>Port mặc định (0 nếu không dùng port, VD: SQLite file).</summary>
        int DefaultPort { get; }
        ProviderCapabilities Capabilities { get; }

        /// <summary>
        /// Thử nhận diện: mở kết nối đọc version, đóng ngay. Trả null khi không khớp.
        /// Không sửa bất cứ thứ gì phía server. Không bao giờ ném lỗi ra ngoài.
        /// </summary>
        Task<EngineInfo?> DetectAsync(DbProbe probe, CancellationToken ct = default);
    }
}
