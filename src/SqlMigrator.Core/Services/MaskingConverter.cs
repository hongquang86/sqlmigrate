using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SqlMigrator.Core.Services
{
    /// <summary>Một cột bị che (Dynamic Data Masking) tách từ DDL nguồn.</summary>
    public sealed class MaskedColumn
    {
        public string Column { get; init; } = string.Empty;
        /// <summary>Hàm mask gốc, VD: email(), partial(1,"XX",2), default(), random(1,9).</summary>
        public string Function { get; init; } = string.Empty;
    }

    /// <summary>Kết quả sinh view che thay thế masking.</summary>
    public sealed class MaskedViewResult
    {
        public string ViewSchema { get; init; } = string.Empty;
        public string ViewName { get; init; } = string.Empty;
        public string Script { get; init; } = string.Empty;
        public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    }

    /// <summary>
    /// Xử lý Dynamic Data Masking (2016+) khi hạ cấp về 2014 (không có masking):
    /// lột mệnh đề MASKED để tạo bảng được, đính marker để khâu fix sinh view
    /// che thay thế. Thuần xử lý chuỗi (không chạm database) nên dễ kiểm thử.
    /// NGUYÊN TẮC: output chỉ để tạo trên ĐÍCH; dữ liệu gốc giữ nguyên giá trị.
    /// </summary>
    public static class MaskingConverter
    {
        /// <summary>Tiền tố marker comment đính vào script đã lột masking.</summary>
        public const string MarkerPrefix = "-- COMPAT-MASKED:";

        /// <summary>Tách danh sách (cột, hàm mask) từ script CREATE TABLE nguồn.</summary>
        public static IReadOnlyList<MaskedColumn> ExtractMasks(string script)
        {
            var list = new List<MaskedColumn>();
            if (string.IsNullOrWhiteSpace(script))
                return list;

            // Tìm trên từng item cấp cao nhất để bắt đúng tên cột (tránh ăn nhầm text khác).
            foreach (var item in SplitBodyItems(script))
            {
                var m = Regex.Match(item,
                    @"^\s*(?<col>\[[^\]]+\]|[\w$#@]+)\s+[\w\[\]#@]+(?:\s*\([^()]*\))?[^\n;]*?MASKED\s+WITH\s*\(\s*FUNCTION\s*=\s*'(?<fn>[^']*)'\s*\)",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline);
                if (!m.Success)
                    continue;
                var col = m.Groups["col"].Value.Trim().Trim('[', ']');
                if (col.Length == 0)
                    continue;
                if (!list.Any(x => x.Column.Equals(col, StringComparison.OrdinalIgnoreCase)))
                    list.Add(new MaskedColumn { Column = col, Function = m.Groups["fn"].Value.Trim() });
            }
            return list;
        }

        /// <summary>
        /// Lột toàn bộ mệnh đề MASKED WITH (...) khỏi script (để 2014 tạo được).
        /// Không đổi gì khác trong script.
        /// </summary>
        public static string StripMasking(string script)
        {
            if (string.IsNullOrWhiteSpace(script))
                return script;
            return Regex.Replace(script,
                @"\s*MASKED\s+WITH\s*\(\s*FUNCTION\s*=\s*'[^']*'\s*\)",
                "", RegexOptions.IgnoreCase);
        }

        /// <summary>Đính marker comment liệt kê cột mask vào đầu script đã lột.</summary>
        public static string AttachMarkers(string strippedScript, IReadOnlyList<MaskedColumn> masks)
        {
            if (masks.Count == 0)
                return strippedScript;
            var sb = new StringBuilder();
            foreach (var m in masks)
                sb.Append(MarkerPrefix).Append(' ')
                  .Append('[').Append(m.Column.Replace("]", "]]")).Append(']')
                  .Append('=').Append(m.Function).AppendLine();
            sb.Append(strippedScript);
            return sb.ToString();
        }

        /// <summary>Đọc marker comment thành danh sách (cột, hàm mask).</summary>
        public static IReadOnlyList<MaskedColumn> TryParseMarkers(string? script)
        {
            var list = new List<MaskedColumn>();
            if (string.IsNullOrWhiteSpace(script))
                return list;
            foreach (Match m in Regex.Matches(script,
                @"--\s*COMPAT-MASKED:\s*\[(?<col>[^\]]+)\]=(?<fn>[^\r\n]*)",
                RegexOptions.IgnoreCase))
            {
                var col = m.Groups["col"].Value.Trim();
                if (col.Length > 0)
                    list.Add(new MaskedColumn { Column = col, Function = m.Groups["fn"].Value.Trim() });
            }
            return list;
        }

        /// <summary>
        /// Sinh script view che thay thế masking: SELECT toàn bộ cột gốc, cột mask
        /// đổi thành biểu thức che tương đương. Tên view: [schema].[Bảng_Masked].
        /// Trả null khi không sinh được (kèm lý do trong warnings).
        /// </summary>
        public static MaskedViewResult? BuildMaskedView(
            string schema, string table, string strippedScript, IReadOnlyList<MaskedColumn> masks)
        {
            var warnings = new List<string>();
            if (masks.Count == 0)
                return null;

            var columns = ParseColumnTypes(strippedScript);
            if (columns.Count == 0)
            {
                warnings.Add("Không đọc được danh sách cột nên không sinh được view che.");
                return null;
            }

            var select = new List<string>();
            foreach (var (name, type) in columns)
            {
                var mask = masks.FirstOrDefault(m =>
                    m.Column.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (mask == null)
                {
                    select.Add("[" + name.Replace("]", "]]") + "]");
                    continue;
                }
                select.Add(BuildMaskExpression(name, type, mask.Function, warnings)
                    + " AS [" + name.Replace("]", "]]") + "]");
            }

            var viewName = table + "_Masked";
            var sb = new StringBuilder();
            sb.Append("IF OBJECT_ID(N'[").Append(schema.Replace("]", "]]")).Append("].[").Append(viewName.Replace("]", "]]"))
              .Append("', N'V') IS NOT NULL DROP VIEW [").Append(schema.Replace("]", "]]")).Append("].[").Append(viewName.Replace("]", "]]")).Append("];");
            sb.Append("\nGO\n");
            sb.Append("CREATE VIEW [").Append(schema.Replace("]", "]]")).Append("].[").Append(viewName.Replace("]", "]]"))
              .Append("] AS\nSELECT\n");
            sb.Append(string.Join(",\n", select));
            sb.Append("\nFROM [").Append(schema.Replace("]", "]]")).Append("].[").Append(table.Replace("]", "]]")).Append("];");

            return new MaskedViewResult
            {
                ViewSchema = schema,
                ViewName = viewName,
                Script = sb.ToString(),
                Warnings = warnings
            };
        }

        /// <summary>
        /// Đọc (tên cột, kiểu gốc) từ CREATE TABLE đã lột (bỏ constraint items).
        /// Kiểu dùng để chọn giá trị default() tương đương.
        /// </summary>
        internal static IReadOnlyList<(string Name, string Type)> ParseColumnTypes(string script)
        {
            var result = new List<(string, string)>();
            foreach (var item in SplitBodyItems(script))
            {
                var t = item.Trim();
                if (t.Length == 0)
                    continue;
                if (Regex.IsMatch(t,
                    @"^\s*(CONSTRAINT\b|PRIMARY\s+KEY\b|UNIQUE\b|FOREIGN\s+KEY\b|CHECK\b|PERIOD\b)",
                    RegexOptions.IgnoreCase))
                    continue;
                var m = Regex.Match(t,
                    @"^\s*(?<col>\[[^\]]+\]|[\w$#@]+)\s+(?<type>\[[^\]]+\]|[\w$#@]+)",
                    RegexOptions.IgnoreCase);
                if (!m.Success)
                    continue;
                var col = m.Groups["col"].Value.Trim().Trim('[', ']');
                var type = m.Groups["type"].Value.Trim().Trim('[', ']').ToLowerInvariant();
                if (col.Length == 0 || type.Length == 0)
                    continue;
                // Bỏ các "cột" thực ra là từ khóa lọt lưới.
                if (type == "constraint" || type == "primary" || type == "foreign" || type == "check")
                    continue;
                result.Add((col, type));
            }
            return result;
        }

        /// <summary>Tách các item cấp cao nhất trong (...) của CREATE TABLE đầu tiên.</summary>
        private static IReadOnlyList<string> SplitBodyItems(string script)
        {
            var m = Regex.Match(script, @"\bCREATE\s+TABLE\b",
                RegexOptions.IgnoreCase);
            if (!m.Success)
                return Array.Empty<string>();
            var open = script.IndexOf('(', m.Index);
            if (open < 0)
                return Array.Empty<string>();

            var depth = 0;
            var inStr = false;
            var inBracket = false;
            var close = -1;
            for (var i = open; i < script.Length; i++)
            {
                var ch = script[i];
                if (inStr)
                {
                    if (ch == '\'')
                    {
                        if (i + 1 < script.Length && script[i + 1] == '\'')
                            i++;
                        else
                            inStr = false;
                    }
                    continue;
                }
                if (inBracket)
                {
                    if (ch == ']')
                    {
                        if (i + 1 < script.Length && script[i + 1] == ']')
                            i++;
                        else
                            inBracket = false;
                    }
                    continue;
                }
                if (ch == '\'') { inStr = true; continue; }
                if (ch == '[') { inBracket = true; continue; }
                if (ch == '(') depth++;
                else if (ch == ')')
                {
                    depth--;
                    if (depth == 0) { close = i; break; }
                }
            }
            if (close < 0)
                return Array.Empty<string>();
            return TemporalTableConverter.SplitTopLevel(script.Substring(open + 1, close - open - 1));
        }

        /// <summary>Dựng biểu thức che tương đương cho một cột.</summary>
        internal static string BuildMaskExpression(
            string column, string baseType, string function, List<string> warnings)
        {
            var c = "[" + column.Replace("]", "]]") + "]";
            var fn = (function ?? "").Trim().ToLowerInvariant();

            // email(): ký tự đầu + XXX@XXXX.com (đúng mẫu native).
            if (fn.StartsWith("email(", StringComparison.Ordinal))
                return "LEFT(" + c + ", 1) + 'XXX@XXXX.com'";

            // partial(prefix, "padding", suffix) — khớp không phân biệt hoa/thường
            // nhưng giữ nguyên chữ gốc của padding.
            var pm = Regex.Match(function ?? "",
                @"partial\(\s*(\d+)\s*,\s*""([^""]*)""\s*,\s*(\d+)\s*\)",
                RegexOptions.IgnoreCase);
            if (pm.Success)
                return "LEFT(" + c + ", " + pm.Groups[1].Value + ") + '"
                    + pm.Groups[2].Value.Replace("'", "''") + "' + RIGHT(" + c + ", " + pm.Groups[3].Value + ")";

            // random() / random(a, b) cho cột số.
            var rm = Regex.Match(fn, @"random\(\s*(\d+)?\s*(?:,\s*(\d+))?\s*\)");
            if (rm.Success)
            {
                var a = rm.Groups[1].Success ? rm.Groups[1].Value : "0";
                var b = rm.Groups[2].Success ? rm.Groups[2].Value : "9";
                return "(ABS(CHECKSUM(NEWID())) % (" + b + " - (" + a + ") + 1) + (" + a + "))";
            }

            // default(): giá trị mặc định theo kiểu cột (đúng semantics native).
            if (fn.StartsWith("default(", StringComparison.Ordinal))
                return DefaultMaskLiteral(baseType);

            warnings.Add("Hàm mask '" + function + "' của cột '" + column
                + "' không nhận diện được — view che dùng giá trị default() thay thế.");
            return DefaultMaskLiteral(baseType);
        }

        /// <summary>Giá trị default() của native masking theo kiểu cột.</summary>
        internal static string DefaultMaskLiteral(string baseType)
        {
            switch (baseType)
            {
                case "char":
                case "varchar":
                case "nchar":
                case "nvarchar":
                case "text":
                case "ntext":
                    return "'XXXX'";
                case "date":
                case "datetime":
                case "datetime2":
                case "smalldatetime":
                case "datetimeoffset":
                    return "'1900-01-01'";
                case "time":
                    return "'00:00:00'";
                case "binary":
                case "varbinary":
                case "image":
                    return "0x00";
                case "uniqueidentifier":
                    return "'00000000-0000-0000-0000-000000000000'";
                default:
                    // Số và các kiểu còn lại: 0 (đúng native default()).
                    return "0";
            }
        }
    }
}
