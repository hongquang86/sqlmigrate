using System;

namespace SqlMigrator.Core.Security
{
    /// <summary>Loại xác thực khi kết nối tới SQL Server.</summary>
    public enum AuthenticationMode
    {
        /// <summary>Dùng tài khoản Windows / Integrated Security.</summary>
        Windows,

        /// <summary>Dùng tài khoản đăng nhập SQL Server (User ID / Password).</summary>
        SqlLogin
    }

    /// <summary>
    /// Thông tin kết nối tới một server (nguồn hoặc đích). Mật khẩu được lưu ở dạng
    /// đã mã hóa bằng DPAPI (<see cref="ProtectedPassword"/>); đối tượng này chưa bao giờ
    /// chứa mật khẩu văn bản thuần.
    /// </summary>
    public sealed class ConnectionProfile
    {
        /// <summary>Mã định danh duy nhất của profile.</summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>Nhóm của profile ("source" cho server nguồn, "dest" cho server đích). Dùng để tách danh sách profile giữa các khối trên giao diện.</summary>
        public string Kind { get; set; } = string.Empty;

        /// <summary>Tên hiển thị (ví dụ: "Máy chủ sản xuất").</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>Tên server / instance (ví dụ: ".", "localhost,1433", "MYSERVER\\INST2").</summary>
        public string Server { get; set; } = string.Empty;

        /// <summary>Database mặc định.</summary>
        public string Database { get; set; } = string.Empty;

        /// <summary>Loại xác thực.</summary>
        public AuthenticationMode Authentication { get; set; } = AuthenticationMode.Windows;

        /// <summary>Tên đăng nhập (chỉ dùng với <see cref="AuthenticationMode.SqlLogin"/>).</summary>
        public string UserName { get; set; } = string.Empty;

        /// <summary>Mật khẩu đã mã hóa bằng DPAPI (nếu rỗng nghĩa là không có mật khẩu).</summary>
        public byte[] ProtectedPassword { get; set; } = Array.Empty<byte>();

        /// <summary>Có bật mã hóa kết nối (Encrypt) hay không. Mặc định luôn bật.</summary>
        public bool EncryptConnection { get; set; } = true;

        /// <summary>Có chấp nhận chứng chỉ server tự ký khi Encrypt hay không.</summary>
        public bool TrustServerCertificate { get; set; }

        /// <summary>
        /// Hệ CSDL ("SqlServer", "PostgreSql", "MySql", "Sqlite", "MongoDb").
        /// Rỗng = tự động (profile cũ mặc định SQL Server để tương thích ngược).
        /// </summary>
        public string Engine { get; set; } = string.Empty;

        /// <summary>Port (0 = port mặc định của engine).</summary>
        public int Port { get; set; }

        /// <summary>Chuỗi hiển thị an toàn (không bao giờ chứa mật khẩu).</summary>
        public string SafeSummary =>
            $"Server={Server};Database={Database};Auth={Authentication};User={(string.IsNullOrWhiteSpace(UserName) ? "(Windows)" : UserName)}";

        public override string ToString() => SafeSummary;
    }
}