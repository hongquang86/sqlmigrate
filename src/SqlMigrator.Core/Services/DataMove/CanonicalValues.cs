using System;
using System.Globalization;
using SqlMigrator.Core.Models;

namespace SqlMigrator.Core.Services.DataMove
{
    /// <summary>
    /// Chuẩn hóa giá trị đọc từ engine nguồn về CLR trung tính, rồi chuyển sang
    /// dạng engine đích nuốt được. Quy tắc: không bao giờ lặng lẽ làm mất dữ liệu
    /// mà không báo — trường hợp éo le thì ToString() + cảnh báo ở tầng gọi.
    /// </summary>
    public static class CanonicalValues
    {
        /// <summary>
        /// Chuẩn hóa một ô đọc từ reader: DBNull → null; kiểu lạ của driver
        /// (SqlGeography/SqlHierarchyId...) → chuỗi hiển thị của nó.
        /// </summary>
        public static object? Normalize(object? value)
        {
            if (value == null || value is DBNull)
                return null;
            var type = value.GetType();
            if (type.IsPrimitive || value is string || value is decimal
                || value is DateTime || value is DateTimeOffset || value is DateOnly
                || value is TimeOnly || value is TimeSpan || value is Guid || value is byte[])
                return value;
            // Kiểu đặc thù driver (VD: Microsoft.SqlServer.Types.*) → ToString().
            // FullName chứa "SqlServer.Types" thì chắc chắn là spatial/hierarchy.
            var fullName = type.FullName ?? "";
            if (fullName.StartsWith("Microsoft.SqlServer.Types.", StringComparison.Ordinal))
                return value.ToString();
            return value;
        }

        /// <summary>Chuyển giá trị trung tính sang dạng engine đích chấp nhận.</summary>
        public static object? ConvertFor(DatabaseEngine target, object? value, CanonicalType type)
        {
            if (value == null)
                return null;
            return target switch
            {
                DatabaseEngine.Sqlite => ForSqlite(value, type),
                DatabaseEngine.PostgreSql => ForPostgres(value, type),
                _ => ForSqlServer(value, type)
            };
        }

        private static object? ForSqlite(object? value, CanonicalType type)
        {
            return value switch
            {
                Guid g => g.ToString("D"),
                DateTimeOffset dto => dto.ToString("O", CultureInfo.InvariantCulture),
                DateOnly d => d.ToString("O", CultureInfo.InvariantCulture),
                TimeOnly t => t.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture),
                TimeSpan ts => ts.ToString("c", CultureInfo.InvariantCulture),
                bool b => b ? 1L : 0L,
                _ => value
            };
        }

        private static object? ForPostgres(object? value, CanonicalType type)
        {
            // Npgsql ánh xạ native hầu hết CLR types; chỉ chuẩn hóa ngày/giờ .NET 6+.
            return value switch
            {
                DateOnly d => d,
                TimeOnly t => t,
                TimeSpan ts => ts,
                _ => value
            };
        }

        private static object? ForSqlServer(object? value, CanonicalType type)
        {
            return value switch
            {
                DateOnly d => d.ToDateTime(TimeOnly.MinValue),
                TimeOnly t => t.ToTimeSpan(),
                _ => value
            };
        }
    }
}
