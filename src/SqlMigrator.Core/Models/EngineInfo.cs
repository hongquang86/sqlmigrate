using System;

namespace SqlMigrator.Core.Models
{
    /// <summary>Kết quả nhận diện một server database (chỉ đọc version, không sửa gì).</summary>
    public sealed class EngineInfo
    {
        public DatabaseEngine Engine { get; init; } = DatabaseEngine.Unknown;
        /// <summary>Tên hiển thị tiếng Việt, VD: "SQL Server", "PostgreSQL".</summary>
        public string DisplayName { get; init; } = "Không xác định";
        /// <summary>Chuỗi version gốc server trả về (có thể rỗng).</summary>
        public string Version { get; init; } = string.Empty;
        /// <summary>Major version số (0 nếu không đọc được).</summary>
        public int MajorVersion { get; init; }
        /// <summary>Biến thể cùng protocol, VD: "MariaDB" cho engine MySql.</summary>
        public string Variant { get; init; } = string.Empty;
        /// <summary>Engine này đã tham gia ít nhất một cặp di chuyển được hỗ trợ chưa (xem MigrationGuard).</summary>
        public bool SupportsMigration { get; init; }

        /// <summary>Mô tả một dòng cho log/giao diện.</summary>
        public string Describe()
        {
            var name = string.IsNullOrWhiteSpace(Variant) ? DisplayName : $"{DisplayName} ({Variant})";
            return string.IsNullOrWhiteSpace(Version) ? name : $"{name} {Version}";
        }

        /// <summary>Tên engine chuẩn từ chuỗi lưu trong profile (rỗng/lạ → SqlServer để tương thích profile cũ).</summary>
        public static DatabaseEngine ParseEngine(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return DatabaseEngine.SqlServer;
            return Enum.TryParse<DatabaseEngine>(value.Trim(), ignoreCase: true, out var engine)
                && Enum.IsDefined(typeof(DatabaseEngine), engine)
                && engine != DatabaseEngine.Unknown
                ? engine
                : DatabaseEngine.SqlServer;
        }
    }
}
