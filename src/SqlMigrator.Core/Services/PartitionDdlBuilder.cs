using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace SqlMigrator.Core.Services
{
    /// <summary>
    /// Dựng DDL partition function/scheme từ catalog nguồn để tạo lại trên đích.
    /// Thuần dựng chuỗi (không chạm database) nên dễ kiểm thử.
    /// </summary>
    internal static class PartitionDdlBuilder
    {
        /// <summary>Định dạng một giá trị biên partition theo kiểu tham số.</summary>
        public static string? FormatBoundary(object? value, string baseTypeName)
        {
            if (value == null || value is DBNull)
                return null; // biên NULL không dựng được → gọi hàm bỏ qua PF này
            var t = (baseTypeName ?? "").Trim().ToLowerInvariant();
            switch (t)
            {
                case "char":
                case "varchar":
                case "nchar":
                case "nvarchar":
                case "text":
                case "ntext":
                case "sysname":
                case "xml":
                case "date":
                case "datetime":
                case "datetime2":
                case "smalldatetime":
                case "datetimeoffset":
                case "time":
                case "uniqueidentifier":
                    return "N'" + Convert.ToString(value, CultureInfo.InvariantCulture)!.Replace("'", "''") + "'";
                case "bit":
                    return Convert.ToBoolean(value, CultureInfo.InvariantCulture) ? "1" : "0";
                default:
                    // Số: decimal/int/float/... in bất biến văn hóa.
                    if (value is IFormattable f)
                        return f.ToString(null, CultureInfo.InvariantCulture);
                    return Convert.ToString(value, CultureInfo.InvariantCulture);
            }
        }

        public static string BuildFunction(
            string name, string parameterTypeSql, bool boundaryRight, IReadOnlyList<string> boundaries)
        {
            return "CREATE PARTITION FUNCTION " + Quoting.QuoteIdentifier(name)
                + "(" + parameterTypeSql + ") AS RANGE "
                + (boundaryRight ? "RIGHT" : "LEFT")
                + " FOR VALUES (" + string.Join(", ", boundaries) + ");";
        }

        public static string BuildScheme(string scheme, string function, IReadOnlyList<string> filegroups)
        {
            var fgs = filegroups.Count == 0
                ? new List<string> { "PRIMARY" }
                : filegroups.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            // Một filegroup duy nhất → ALL TO; nhiều filegroup → liệt kê theo thứ tự partition.
            var to = fgs.Count == 1
                ? "ALL TO (" + Quoting.QuoteIdentifier(fgs[0]) + ")"
                : "TO (" + string.Join(", ", fgs.Select(Quoting.QuoteIdentifier)) + ")";
            return "CREATE PARTITION SCHEME " + Quoting.QuoteIdentifier(scheme)
                + " AS PARTITION " + Quoting.QuoteIdentifier(function) + " " + to + ";";
        }
    }
}
