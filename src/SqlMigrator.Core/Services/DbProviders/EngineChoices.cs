using System.Collections.Generic;

namespace SqlMigrator.Core.Services.DbProviders
{
    /// <summary>Mục chọn hệ CSDL cho combobox (value lưu profile, display tiếng Việt).</summary>
    public sealed class EngineChoice
    {
        public string Value { get; init; } = string.Empty;
        public string Display { get; init; } = string.Empty;
        public int DefaultPort { get; init; }
    }

    /// <summary>Danh sách engine cố định, dùng chung cho UI và kiểm thử.</summary>
    public static class EngineChoices
    {
        public static readonly IReadOnlyList<EngineChoice> All =
            new List<EngineChoice>
            {
                new() { Value = "", Display = "Tự động", DefaultPort = 0 },
                new() { Value = "SqlServer", Display = "SQL Server", DefaultPort = 1433 },
                new() { Value = "PostgreSql", Display = "PostgreSQL", DefaultPort = 5432 },
                new() { Value = "MySql", Display = "MySQL / MariaDB", DefaultPort = 3306 },
                new() { Value = "Sqlite", Display = "SQLite (file)", DefaultPort = 0 },
                new() { Value = "MongoDb", Display = "MongoDB", DefaultPort = 27017 }
            }.AsReadOnly();

        /// <summary>Tên hiển thị từ value lưu trữ (lạ/trống → Tự động/SQL Server).</summary>
        public static string DisplayOf(string? value)
        {
            foreach (var c in All)
            {
                if (c.Value.Equals((value ?? "").Trim(), System.StringComparison.OrdinalIgnoreCase))
                    return c.Display;
            }
            return "SQL Server";
        }
    }
}
