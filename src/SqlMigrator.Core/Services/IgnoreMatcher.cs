using System;
using System.Collections.Generic;

namespace SqlMigrator.Core.Services
{
    /// <summary>
    /// So khớp đối tượng với danh sách bỏ qua đã chấp nhận, bất kể cách gọi tên loại
    /// khác nhau giữa các module (TABLE/USER_TABLE/U, VIEW/V, STORED_PROCEDURE/P...).
    /// Thuần logic để dễ kiểm thử.
    /// </summary>
    internal static class IgnoreMatcher
    {
        /// <summary>Chuẩn hóa loại về dạng chuẩn TABLE/VIEW/PROCEDURE/FUNCTION/TRIGGER/....</summary>
        internal static string CanonicalKind(string? rawType)
        {
            if (string.IsNullOrWhiteSpace(rawType))
                return "";
            switch (rawType.Trim().ToUpperInvariant())
            {
                case "TABLE":
                case "USER_TABLE":
                case "U":
                    return "TABLE";
                case "VIEW":
                case "V":
                    return "VIEW";
                case "STORED_PROCEDURE":
                case "SQL_STORED_PROCEDURE":
                case "P":
                    return "PROCEDURE";
                case "FUNCTION":
                case "SQL_SCALAR_FUNCTION":
                case "SQL_TABLE_FUNCTION":
                case "SQL_INLINE_TABLE_FUNCTION":
                case "FN":
                case "IF":
                case "TF":
                    return "FUNCTION";
                case "TRIGGER":
                case "SQL_TRIGGER":
                case "SQL_DML_TRIGGER":
                case "TR":
                    return "TRIGGER";
                case "SEQUENCE":
                case "SEQ":
                case "SO":
                    return "SEQUENCE";
                case "FK":
                case "FOREIGN_KEY":
                case "SQL_FOREIGN_KEY":
                case "F":
                    return "FK";
                case "COLUMN":
                case "C":
                    return "COLUMN";
                default:
                    return rawType.Trim().ToUpperInvariant();
            }
        }

        /// <summary>Khóa chuẩn "LOẠI:schema.name" để so sánh không phân biệt hoa/thường.</summary>
        internal static string CanonicalKey(string? rawType, string? schema, string? name)
        {
            var s = (schema ?? "").Trim().Trim('[', ']');
            var n = (name ?? "").Trim().Trim('[', ']');
            var full = string.IsNullOrEmpty(s) ? n : s + "." + n;
            return CanonicalKind(rawType) + ":" + full;
        }

        /// <summary>Tách "schema.name" (hoặc "SERVER:x") thành schema + name.</summary>
        internal static (string Schema, string Name) SplitName(string? objectName)
        {
            if (string.IsNullOrWhiteSpace(objectName))
                return ("", "");
            var text = objectName.Trim();
            var dot = text.LastIndexOf('.');
            if (dot <= 0)
                return ("", text.Trim('[', ']'));
            return (text.Substring(0, dot).Trim('[', ']', ' '), text.Substring(dot + 1).Trim('[', ']', ' '));
        }

        /// <summary>Đối tượng (loại + tên) có nằm trong danh sách bỏ qua không.</summary>
        internal static bool IsIgnored(
            IReadOnlyList<Models.IgnoredObject> ignored,
            string? rawType, string? objectName)
        {
            if (ignored.Count == 0 || string.IsNullOrWhiteSpace(objectName))
                return false;
            var (schema, name) = SplitName(objectName);
            var key = CanonicalKey(rawType, schema, name);
            foreach (var entry in ignored)
            {
                if (CanonicalKey(entry.ObjectType, entry.Schema, entry.Name)
                    .Equals(key, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
    }
}
