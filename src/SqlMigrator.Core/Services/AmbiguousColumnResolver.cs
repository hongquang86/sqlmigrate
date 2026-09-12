using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SqlMigrator.Core.Services
{
    /// <summary>
    /// Giải cột mơ hồ ghi tường minh (lỗi 209 không có SELECT * để bung).
    /// Chiến lược: liệt kê vị trí cột trần, thay bằng [alias].[cột] theo từng
    /// khả năng, rồi đối chiếu checksum output ngay trên NGUỒN (chỉ SELECT) để
    /// chọn đúng khả năng duy nhất khớp. Không đoán mò: 0 hoặc nhiều khả năng
    /// khớp đều trả về không làm (để user sửa tay).
    /// </summary>
    public static class AmbiguousColumnResolver
    {
        /// <summary>
        /// Che string literal '...' thành dấu cách (giữ độ dài) để regex không ăn
        /// nhầm chữ trong chuỗi. Xử lý '' escape.
        /// </summary>
        internal static string MaskStrings(string script)
        {
            if (string.IsNullOrEmpty(script))
                return script;
            var sb = new StringBuilder(script.Length);
            var inStr = false;
            for (var i = 0; i < script.Length; i++)
            {
                var ch = script[i];
                if (inStr)
                {
                    sb.Append(' ');
                    if (ch == '\'')
                    {
                        if (i + 1 < script.Length && script[i + 1] == '\'')
                        {
                            sb.Append(' ');
                            i++;
                        }
                        else
                            inStr = false;
                    }
                    continue;
                }
                if (ch == '\'')
                {
                    inStr = true;
                    sb.Append(' ');
                    continue;
                }
                sb.Append(ch);
            }
            return sb.ToString();
        }

        /// <summary>
        /// Vị trí các lần xuất hiện TRẦN của cột (không alias., không [alias].,
        /// không nằm trong string, không phải tiền tố tên dài hơn).
        /// </summary>
        internal static IReadOnlyList<int> FindBareOccurrences(string script, string column)
        {
            var positions = new List<int>();
            if (string.IsNullOrWhiteSpace(script) || string.IsNullOrWhiteSpace(column))
                return positions;
            var masked = MaskStrings(script);
            // Lookbehind chặn cả '[' để "[b].[Active]" không khớp từ bên trong ngoặc.
            var pattern = @"(?<![\w$#@\]\.\[])(?:\[" + Regex.Escape(column) + @"\]|"
                + Regex.Escape(column) + @")(?![\w$#@\[])";
            foreach (Match m in Regex.Matches(masked, pattern, RegexOptions.IgnoreCase))
                positions.Add(m.Index);
            return positions;
        }

        /// <summary>
        /// Thay mọi lần xuất hiện trần của cột thành [alias].[cột] (giữ nguyên
        /// các chỗ đã qualify). Trả script mới; không đổi gì nếu không khớp.
        /// </summary>
        public static string QualifyOccurrences(string script, string column, string alias)
        {
            if (string.IsNullOrWhiteSpace(script) || string.IsNullOrWhiteSpace(column)
                || string.IsNullOrWhiteSpace(alias))
                return script;
            var masked = MaskStrings(script);
            var pattern = @"(?<![\w$#@\]\.\[])(?:\[" + Regex.Escape(column) + @"\]|"
                + Regex.Escape(column) + @")(?![\w$#@\[])";
            var bareAlias = alias.Trim().Trim('[', ']');
            var replacement = "[" + bareAlias.Replace("]", "]]") + "].["
                + column.Trim().Trim('[', ']') + "]";
            // Đi ngược từ cuối để vị trí match không lệch khi chèn.
            var matches = Regex.Matches(masked, pattern, RegexOptions.IgnoreCase)
                .Cast<Match>()
                .OrderByDescending(m => m.Index)
                .ToList();
            var sb = new StringBuilder(script);
            foreach (var m in matches)
            {
                sb.Remove(m.Index, m.Length);
                sb.Insert(m.Index, replacement);
            }
            return sb.ToString();
        }

        /// <summary>
        /// Tách phần query sau AS khỏi script CREATE VIEW (bỏ header SET/GO).
        /// Trả null khi không nhận diện được. Cắt đuôi WITH CHECK OPTION.
        /// </summary>
        internal static string? ExtractViewBody(string script)
        {
            if (string.IsNullOrWhiteSpace(script))
                return null;
            string? body = null;
            foreach (var batch in ScriptUtils.SplitBatches(script))
            {
                if (string.IsNullOrWhiteSpace(batch))
                    continue;
                var m = Regex.Match(batch,
                    @"^\s*CREATE\s+(?:OR\s+ALTER\s+)?VIEW\s+"
                    + @"(?:\[[^\]]+\]\s*\.\s*\[[^\]]+\]|[\w$#@]+\s*\.\s*[\w$#@]+|\[[^\]]+\]|[\w$#@]+)"
                    + @"\s+AS\s+(?<q>[\s\S]*)$",
                    RegexOptions.IgnoreCase);
                if (m.Success)
                    body = m.Groups["q"].Value;
            }
            if (body == null)
                return null;
            body = Regex.Replace(body, @"\s+WITH\s+CHECK\s+OPTION\s*;?\s*$",
                "", RegexOptions.IgnoreCase).Trim();
            body = body.TrimEnd(';').Trim();
            return body.Length > 0 ? body : null;
        }
    }
}
