using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace SqlMigrator.Core.Services
{
    /// <summary>
    /// Bung SELECT * tường minh khi CREATE view/function lỗi "Ambiguous column name".
    /// Nguyên nhân điển hình: view dùng SELECT * từ JOIN, lúc tạo trên nguồn chỉ một
    /// bảng có cột X, sau này bảng kia ALTER thêm cột X → tạo lại trên đích sập 209.
    /// Cách sửa: liệt kê tường minh [alias].[cột] theo đúng cột của bảng nguồn.
    /// Thuần xử lý chuỗi (danh sách cột do bên gọi đọc từ catalog nguồn).
    /// </summary>
    public static class StarExpander
    {
        /// <summary>Từ khóa không bao giờ là alias bảng (tránh nuốt WHERE/JOIN...).</summary>
        private static readonly HashSet<string> Keywords = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase
        )
        {
            "WHERE", "JOIN", "INNER", "LEFT", "RIGHT", "FULL", "OUTER", "CROSS",
            "ON", "GROUP", "ORDER", "HAVING", "UNION", "ALL", "AND", "OR",
            "FROM", "SELECT", "FOR", "OPTION", "PIVOT", "UNPIVOT", "APPLY",
            "AS", "BY", "INTO", "VALUES", "SET", "WHEN", "THEN", "ELSE", "END"
        };

        /// <summary>
        /// Phân tích mệnh đề FROM/JOIN phẳng thành danh sách (alias, "schema.table").
        /// Alias vắng mặt thì lấy tên bảng ngắn. Bỏ qua derived table, gọi hàm TVF,
        /// alias trùng từ khóa. Giữ thứ tự xuất hiện (để bung SELECT * trần).
        /// </summary>
        internal static IReadOnlyList<(string Alias, string Table)> ParseTableAliases(string script)
        {
            var result = new List<(string Alias, string Table)>();
            if (string.IsNullOrWhiteSpace(script))
                return result;

            foreach (Match m in Regex.Matches(script,
                @"(?:FROM|JOIN)\s+(?<ref>\[[^\]]+\]\s*\.\s*\[[^\]]+\]|[\w$#@]+\s*\.\s*[\w$#@]+|\[[^\]]+\]|[\w$#@]+)",
                RegexOptions.IgnoreCase))
            {
                var table = NormalizeTableRef(m.Groups["ref"].Value);
                if (table.Length == 0)
                    continue;

                var rest = script.Substring(m.Index + m.Length);
                string? alias = null;
                var am = Regex.Match(rest, @"^\s+(?:AS\s+)?(?<a>[\w$#@]+|\[[^\]]+\])",
                    RegexOptions.IgnoreCase);
                if (am.Success && !Keywords.Contains(am.Groups["a"].Value.Trim('[', ']')))
                    alias = am.Groups["a"].Value.Trim();

                // Gọi hàm dạng bảng Fn(...) — không tra cột được, bỏ qua.
                var after = am.Success ? rest.Substring(am.Length) : rest;
                if (after.TrimStart().StartsWith("(", StringComparison.Ordinal))
                    continue;

                if (string.IsNullOrEmpty(alias))
                {
                    var dot = table.LastIndexOf('.');
                    alias = dot >= 0 ? table.Substring(dot + 1) : table;
                }

                if (!result.Any(x => x.Alias.Equals(alias, StringComparison.OrdinalIgnoreCase)))
                    result.Add((alias!, table));
            }
            return result;
        }

        /// <summary>Chuẩn hóa tham chiếu bảng: bỏ ngoặc, gọn khoảng trắng ("[dbo] . [T]" → "dbo.T").</summary>
        internal static string NormalizeTableRef(string reference)
        {
            if (string.IsNullOrWhiteSpace(reference))
                return string.Empty;
            var noBracket = Regex.Replace(reference, @"\[([^\]]+)\]", "$1");
            return Regex.Replace(noBracket, @"\s*\.\s*", ".").Trim();
        }

        /// <summary>
        /// Bung "alias.*" và "*" trần thành danh sách cột tường minh.
        /// aliasColumns: alias (không phân biệt hoa/thường) → tên cột theo thứ tự.
        /// Trả (Changed, Script).
        /// </summary>
        public static (bool Changed, string Script) ExpandStars(
            string script,
            IReadOnlyList<(string Alias, IReadOnlyList<string> Columns)> aliasColumns)
        {
            if (string.IsNullOrWhiteSpace(script) || aliasColumns.Count == 0)
                return (false, script);

            var map = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (alias, cols) in aliasColumns)
            {
                if (!map.ContainsKey(alias) && cols.Count > 0)
                    map[alias] = cols;
            }
            if (map.Count == 0)
                return (false, script);

            var result = script;

            // Dạng "alias.*": giữ đúng chữ alias trong câu để thay.
            result = Regex.Replace(result,
                @"(?<![\w$#@\]])(?<alias>[\w$#@]+|\[[^\]]+\])\s*\.\s*\*",
                m =>
                {
                    var alias = m.Groups["alias"].Value.Trim();
                    var bare = alias.Trim('[', ']');
                    if (!map.TryGetValue(bare, out var cols))
                        return m.Value;
                    return string.Join(", ", cols.Select(c => "[" + alias.Trim('[', ']') + "].[" + c.Replace("]", "]]") + "]"));
                },
                RegexOptions.IgnoreCase);

            // Dạng "*" trần (không phải COUNT(*), không phải nhân): bung mọi alias theo thứ tự.
            var all = new List<string>();
            foreach (var (alias, cols) in aliasColumns)
            {
                if (!map.ContainsKey(alias))
                    continue;
                var bare = alias.Trim('[', ']');
                all.AddRange(cols.Select(c => "[" + bare + "].[" + c.Replace("]", "]]") + "]"));
            }
            if (all.Count > 0)
            {
                // Dấu . loại trừ "alias.*" còn sót (alias lạ): không bung bừa sang mọi bảng.
                result = Regex.Replace(result,
                    @"(?<![\w$#@\]\.\(])\*(?=\s*(,|FROM\b))",
                    string.Join(", ", all),
                    RegexOptions.IgnoreCase);
            }

            return (!result.Equals(script, StringComparison.Ordinal), result);
        }
    }
}
