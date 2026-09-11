using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using SqlMigrator.Core.Models;

namespace SqlMigrator.Core.Services
{
    /// <summary>Kết quả chuyển một bảng temporal (2016) sang dạng tương đương 2014.</summary>
    public sealed class TemporalConversion
    {
        public string Schema { get; init; } = string.Empty;
        public string Table { get; init; } = string.Empty;
        public string HistorySchema { get; init; } = string.Empty;
        public string HistoryTable { get; init; } = string.Empty;
        public string PeriodStart { get; init; } = string.Empty;
        public string PeriodEnd { get; init; } = string.Empty;
        /// <summary>Script CREATE bảng gốc đã lột versioning.</summary>
        public string BaseTableScript { get; init; } = string.Empty;
        /// <summary>Script CREATE bảng lịch sử (null khi lịch sử đã di chuyển thường).</summary>
        public string? HistoryTableScript { get; init; }
        /// <summary>Script trigger duy trì lịch sử (có thể rỗng kèm cảnh báo).</summary>
        public string TriggerScript { get; init; } = string.Empty;
        /// <summary>Toàn bộ bundle nối bằng GO (base → history → default → trigger).</summary>
        public string CombinedScript { get; init; } = string.Empty;
        public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    }

    /// <summary>
    /// Chuyển bảng system-versioned temporal (SQL 2016+) sang tương đương SQL 2014:
    /// bảng thường + bảng lịch sử + trigger AFTER UPDATE/DELETE + DEFAULT kỳ hạn.
    /// Thuần xử lý chuỗi (không chạm database) nên dễ kiểm thử.
    /// NGUYÊN TẮC: output chỉ để tạo trên ĐÍCH; không bao giờ sửa nguồn.
    /// </summary>
    public static class TemporalTableConverter
    {
        /// <summary>
        /// Chuyển script CREATE TABLE temporal sang bundle 2014.
        /// Trả null khi không chuyển được (kèm lý do trong warnings).
        /// </summary>
        public static TemporalConversion? TryConvert(
            string createTableScript,
            TableSchema table,
            string historySchema,
            string historyTable,
            bool historyInScope)
        {
            var warnings = new List<string>();
            if (string.IsNullOrWhiteSpace(createTableScript))
                return null;

            // Tách batch: giữ batch CREATE TABLE + index, bỏ batch ALTER ... SYSTEM_VERSIONING.
            var kept = new List<string>();
            foreach (var batch in ScriptUtils.SplitBatches(createTableScript))
            {
                if (string.IsNullOrWhiteSpace(batch))
                    continue;
                if (Regex.IsMatch(batch, @"\bSYSTEM_VERSIONING\b", RegexOptions.IgnoreCase)
                    && !Regex.IsMatch(batch, @"\bCREATE\s+TABLE\b", RegexOptions.IgnoreCase))
                    continue; // batch ALTER ... SET (SYSTEM_VERSIONING...) riêng lẻ thì bỏ
                kept.Add(batch);
            }

            var createIdx = kept.FindIndex(b =>
                Regex.IsMatch(b, @"\bCREATE\s+TABLE\b", RegexOptions.IgnoreCase));
            if (createIdx < 0)
            {
                warnings.Add("Không tìm thấy CREATE TABLE trong script nên không chuyển temporal được.");
                return null;
            }

            // Tìm 2 cột kỳ hạn từ PERIOD FOR SYSTEM_TIME (trong script gốc).
            var period = Regex.Match(kept[createIdx],
                @"PERIOD\s+FOR\s+SYSTEM_TIME\s*\(\s*\[?([\w$#]+)\]?\s*,\s*\[?([\w$#]+)\]?\s*\)",
                RegexOptions.IgnoreCase);
            if (!period.Success)
            {
                warnings.Add("Không tìm thấy PERIOD FOR SYSTEM_TIME nên không xác định được cột kỳ hạn.");
                return null;
            }

            var startCol = period.Groups[1].Value;
            var endCol = period.Groups[2].Value;

            // Lột GENERATED ALWAYS khỏi 2 cột kỳ hạn.
            var stripped = Regex.Replace(kept[createIdx],
                @"\bGENERATED\s+ALWAYS\s+AS\s+ROW\s+(?:START|END)\b",
                "", RegexOptions.IgnoreCase);

            // Bỏ mệnh đề PERIOD (thường đứng cuối, nuốt luôn dấu phẩy phía trước).
            string withoutPeriod = Regex.Replace(stripped,
                @",\s*PERIOD\s+FOR\s+SYSTEM_TIME\s*\([^()]*\)",
                "", RegexOptions.IgnoreCase);
            if (ReferenceEquals(withoutPeriod, stripped) || withoutPeriod == stripped)
            {
                withoutPeriod = Regex.Replace(stripped,
                    @"PERIOD\s+FOR\s+SYSTEM_TIME\s*\([^()]*\)\s*,?",
                    "", RegexOptions.IgnoreCase);
            }

            // Bỏ đuôi WITH (SYSTEM_VERSIONING = ON (...)).
            var baseScript = Regex.Replace(withoutPeriod,
                @"\s*WITH\s*\(\s*SYSTEM_VERSIONING\s*=\s*ON\s*\([^()]*\)\s*\)\s*;?\s*$",
                "", RegexOptions.IgnoreCase | RegexOptions.Singleline).Trim();
            if (!baseScript.EndsWith(";", StringComparison.Ordinal))
                baseScript += ";";

            if (Regex.IsMatch(baseScript, @"\bSYSTEM_VERSIONING\b", RegexOptions.IgnoreCase)
                || Regex.IsMatch(baseScript, @"\bPERIOD\s+FOR\s+SYSTEM_TIME\b", RegexOptions.IgnoreCase))
            {
                warnings.Add("Script sau khi lột vẫn còn từ khóa temporal nên không dùng được.");
                return null;
            }

            var parts = new List<string> { baseScript };
            string? historyScript = null;

            // Dựng CREATE bảng lịch sử từ CREATE đã lột (bỏ ràng buộc, bỏ IDENTITY).
            if (!historyInScope)
            {
                historyScript = BuildHistoryScript(baseScript, historySchema, historyTable, warnings);
                if (historyScript == null)
                    return null; // warnings đã ghi lý do
                parts.Add(historyScript);
            }

            // DEFAULT kỳ hạn cho bảng gốc (hệ thống 2016 tự điền, 2014 phải có default).
            var baseName = "[" + table.Schema + "].[" + table.Name + "]";
            parts.Add("ALTER TABLE " + baseName + " ADD DEFAULT SYSUTCDATETIME() FOR [" + startCol + "];");
            parts.Add("ALTER TABLE " + baseName
                + " ADD DEFAULT CONVERT(DATETIME2, '9999-12-31 23:59:59.9999999') FOR [" + endCol + "];");

            // Trigger duy trì lịch sử.
            var triggerSql = BuildTriggers(table, historySchema, historyTable, startCol, endCol, warnings);
            if (!string.IsNullOrWhiteSpace(triggerSql))
                parts.Add(triggerSql);

            warnings.Add("Lịch sử trước thời điểm di chuyển: "
                + (historyInScope
                    ? "giữ nguyên từ bảng lịch sử nguồn (di chuyển như bảng thường)."
                    : "bắt đầu trống từ lúc tạo trên đích."));

            return new TemporalConversion
            {
                Schema = table.Schema,
                Table = table.Name,
                HistorySchema = historySchema,
                HistoryTable = historyTable,
                PeriodStart = startCol,
                PeriodEnd = endCol,
                BaseTableScript = baseScript,
                HistoryTableScript = historyScript,
                TriggerScript = triggerSql,
                CombinedScript = string.Join("\r\nGO\r\n", parts),
                Warnings = warnings
            };
        }

        /// <summary>
        /// Dựng CREATE bảng lịch sử từ CREATE đã lột: đổi tên, bỏ mọi ràng buộc
        /// (CONSTRAINT/PRIMARY/UNIQUE/FOREIGN/CHECK), bỏ IDENTITY. Cột computed
        /// giữ nguyên định nghĩa AS (trigger liệt kê tường minh nên vẫn chèn được,
        /// giá trị quá khứ do chính engine tính từ cột anh em).
        /// </summary>
        internal static string? BuildHistoryScript(
            string baseScript, string historySchema, string historyTable, List<string> warnings)
        {
            var m = Regex.Match(baseScript,
                @"\bCREATE\s+TABLE\s+(?<name>(?:\[[^\]]+\]\s*\.\s*\[[^\]]+\]|[\w$#@]+\s*\.\s*[\w$#@]+|[\w$#@]+))\s*(?<rest>.*)$",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (!m.Success)
            {
                warnings.Add("Không phân tích được tên bảng trong CREATE TABLE.");
                return null;
            }

            var rest = m.Groups["rest"].Value.TrimStart();
            if (!rest.StartsWith("(", StringComparison.Ordinal))
            {
                warnings.Add("Không tìm thấy danh sách cột trong CREATE TABLE.");
                return null;
            }

            // Tìm dấu ')' đóng của danh sách cột (nhận biết string/bracket).
            var open = rest.IndexOf('(');
            var depth = 0;
            var inStr = false;
            var inBracket = false;
            var close = -1;
            for (var i = open; i < rest.Length; i++)
            {
                var ch = rest[i];
                if (inStr)
                {
                    if (ch == '\'')
                    {
                        if (i + 1 < rest.Length && rest[i + 1] == '\'')
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
                        if (i + 1 < rest.Length && rest[i + 1] == ']')
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
            {
                warnings.Add("Danh sách cột trong CREATE TABLE mất cân bằng ngoặc.");
                return null;
            }

            var items = SplitTopLevel(rest.Substring(open + 1, close - open - 1));
            var keptCols = new List<string>();
            foreach (var item in items)
            {
                var t = item.Trim();
                if (t.Length == 0)
                    continue;
                // Bỏ mọi ràng buộc cấp bảng/cột dạng khai báo riêng.
                if (Regex.IsMatch(t,
                    @"^\s*(CONSTRAINT\b|PRIMARY\s+KEY\b|UNIQUE\b|FOREIGN\s+KEY\b|CHECK\b|PERIOD\b)",
                    RegexOptions.IgnoreCase))
                    continue;
                // Bỏ thuộc tính IDENTITY (lịch sử chèn giá trị tường minh).
                t = StripOutsideQuotes(t, @"\bIDENTITY\s*\([^()]*\)").Trim();
                // Bỏ UNIQUE / PRIMARY KEY inline (lịch sử giữ nhiều phiên bản nên
                // không được unique). Chỉ strip ngoài string literal để khỏi ăn
                // nhầm DEFAULT 'UNIQUE'.
                t = StripOutsideQuotes(t, @"(?<=[\s\)])UNIQUE(?=[\s,]|$)").Trim();
                t = StripOutsideQuotes(t, @"(?<=[\s\)])PRIMARY\s+KEY(?=[\s,]|$)").Trim();
                // Bỏ FOREIGN KEY inline dạng REFERENCES ... (ràng buộc đi theo
                // object FK riêng; giữ lại có thể chặn CREATE khi bảng cha thiếu).
                t = StripOutsideQuotes(t, @"\s+REFERENCES\s+[^\s,]+(\s*\([^()]*\))?").Trim();
                if (t.Length == 0 || t.TrimEnd().EndsWith(",", StringComparison.Ordinal))
                    t = t.TrimEnd().TrimEnd(',');
                if (t.Length > 0)
                    keptCols.Add(t);
            }

            if (keptCols.Count == 0)
            {
                warnings.Add("Không còn cột nào để tạo bảng lịch sử.");
                return null;
            }

            var sb = new StringBuilder();
            sb.Append("CREATE TABLE [").Append(historySchema).Append("].[").Append(historyTable).Append("] (\n");
            sb.Append(string.Join(",\n", keptCols));
            sb.Append("\n);");
            return sb.ToString();
        }

        /// <summary>
        /// Xóa một mẫu regex khỏi chuỗi nhưng BỎ QUA nội dung trong string literal
        /// '...' (tránh ăn nhầm DEFAULT 'UNIQUE'). Chỉ xóa khớp đầu tiên.
        /// </summary>
        internal static string StripOutsideQuotes(string item, string pattern)
        {
            var masked = new StringBuilder(item.Length);
            var inStr = false;
            for (var i = 0; i < item.Length; i++)
            {
                var ch = item[i];
                if (inStr)
                {
                    masked.Append(' ');
                    if (ch == '\'')
                    {
                        if (i + 1 < item.Length && item[i + 1] == '\'')
                        {
                            masked.Append(' ');
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
                    masked.Append(' ');
                    continue;
                }
                masked.Append(ch);
            }

            var m = Regex.Match(masked.ToString(), pattern, RegexOptions.IgnoreCase);
            if (!m.Success)
                return item;
            return item.Remove(m.Index, m.Length);
        }

        /// <summary>Tách danh sách cách nhau bởi dấu phẩy cấp cao nhất (bỏ qua
        /// nội dung trong ngoặc, string '...', identifier [...] và comment --).</summary>
        internal static IReadOnlyList<string> SplitTopLevel(string body)
        {
            var items = new List<string>();
            var cur = new StringBuilder();
            var depth = 0;
            var inStr = false;
            var inBracket = false;
            for (var i = 0; i < body.Length; i++)
            {
                var ch = body[i];
                if (inStr)
                {
                    cur.Append(ch);
                    if (ch == '\'')
                    {
                        if (i + 1 < body.Length && body[i + 1] == '\'')
                        {
                            cur.Append('\'');
                            i++;
                        }
                        else
                            inStr = false;
                    }
                    continue;
                }
                if (inBracket)
                {
                    cur.Append(ch);
                    if (ch == ']')
                    {
                        if (i + 1 < body.Length && body[i + 1] == ']')
                        {
                            cur.Append(']');
                            i++;
                        }
                        else
                            inBracket = false;
                    }
                    continue;
                }
                // Comment dòng -- tới hết dòng (SMO đôi khi kèm chú thích).
                if (ch == '-' && i + 1 < body.Length && body[i + 1] == '-')
                {
                    while (i < body.Length && body[i] != '\n')
                    {
                        cur.Append(body[i]);
                        i++;
                    }
                    continue;
                }
                if (ch == '\'') { inStr = true; cur.Append(ch); continue; }
                if (ch == '[') { inBracket = true; cur.Append(ch); continue; }
                if (ch == '(') depth++;
                else if (ch == ')') depth--;
                if (ch == ',' && depth == 0)
                {
                    items.Add(cur.ToString());
                    cur.Clear();
                    continue;
                }
                cur.Append(ch);
            }
            if (cur.Length > 0)
                items.Add(cur.ToString());
            return items;
        }

        /// <summary>
        /// Sinh trigger AFTER UPDATE/DELETE ghi phiên bản cũ vào bảng lịch sử.
        /// Cột chèn tường minh = toàn bộ cột trừ computed/identity/rowversion
        /// (computed giữ trong bảng lịch sử nên SELECT * UNION vẫn khớp).
        /// </summary>
        internal static string BuildTriggers(
            TableSchema table, string historySchema, string historyTable,
            string startCol, string endCol, List<string> warnings)
        {
            var insertCols = table.Columns
                .Where(c => !c.IsComputed && !c.IsIdentity && !c.IsRowVersion)
                .Select(c => c.Name)
                .ToList();
            if (insertCols.Count == 0)
            {
                warnings.Add("Bảng không còn cột chèn được (toàn computed/identity/rowversion) nên bỏ qua trigger lịch sử.");
                return string.Empty;
            }

            string Q(string n) => "[" + n.Replace("]", "]]") + "]";
            var colList = string.Join(", ", insertCols.Select(Q));
            var baseName = Q(table.Schema) + "." + Q(table.Name);
            var histName = Q(historySchema) + "." + Q(historyTable);
            var s = Q(startCol);
            var e = Q(endCol);

            // Điều kiện khớp dòng gốc: ưu tiên khóa chính, không có thì so toàn bộ cột.
            var keyCols = table.Columns.Where(c => c.IsPrimaryKey).Select(c => c.Name).ToList();
            var matchCols = keyCols.Count > 0 ? keyCols : insertCols;
            var match = string.Join(" AND ", matchCols.Select(c =>
                "(i." + Q(c) + " = t." + Q(c)
                + " OR (i." + Q(c) + " IS NULL AND t." + Q(c) + " IS NULL))"));

            var sb = new StringBuilder();
            sb.Append("CREATE TRIGGER ").Append(Q(table.Schema)).Append(".").Append(Q("TR_" + table.Name + "_History_U"))
              .Append(" ON ").Append(baseName).Append(" AFTER UPDATE AS BEGIN SET NOCOUNT ON;\n");
            sb.Append("IF TRIGGER_NESTLEVEL() > 1 RETURN;\n");
            sb.Append("INSERT INTO ").Append(histName)
              .Append(" (").Append(colList).Append(", ").Append(s).Append(", ").Append(e).Append(")\n");
            sb.Append("SELECT ").Append(string.Join(", ", insertCols.Select(c => "d." + Q(c))))
              .Append(", d.").Append(s).Append(", SYSUTCDATETIME() FROM deleted d;\n");
            sb.Append("UPDATE t SET t.").Append(s).Append(" = SYSUTCDATETIME() FROM ").Append(baseName)
              .Append(" t WHERE EXISTS (SELECT 1 FROM inserted i WHERE ").Append(match).Append(");\n");
            sb.Append("END;");
            sb.Append("\nGO\n");
            sb.Append("CREATE TRIGGER ").Append(Q(table.Schema)).Append(".").Append(Q("TR_" + table.Name + "_History_D"))
              .Append(" ON ").Append(baseName).Append(" AFTER DELETE AS BEGIN SET NOCOUNT ON;\n");
            sb.Append("IF TRIGGER_NESTLEVEL() > 1 RETURN;\n");
            sb.Append("INSERT INTO ").Append(histName)
              .Append(" (").Append(colList).Append(", ").Append(s).Append(", ").Append(e).Append(")\n");
            sb.Append("SELECT ").Append(string.Join(", ", insertCols.Select(c => "d." + Q(c))))
              .Append(", d.").Append(s).Append(", SYSUTCDATETIME() FROM deleted d;\n");
            sb.Append("END;");
            return sb.ToString();
        }

        /// <summary>
        /// Viết lại truy vấn FOR SYSTEM_TIME sang UNION bảng gốc + bảng lịch sử.
        /// map: "schema.table" → (historySchema, historyTable, startCol, endCol).
        /// Hỗ trợ AS OF / ALL / BETWEEN / FROM..TO / CONTAINED IN. Không khớp thì giữ nguyên.
        /// </summary>
        public static string RewriteSystemTimeQuery(
            string script,
            IReadOnlyDictionary<string, (string HistorySchema, string HistoryTable, string StartCol, string EndCol)> map)
        {
            if (string.IsNullOrWhiteSpace(script) || map.Count == 0)
                return script;
            if (script.IndexOf("FOR SYSTEM_TIME", StringComparison.OrdinalIgnoreCase) < 0)
                return script;

            var result = script;
            // Tên dài trước để dbo.T2 không bị khớp nhầm thành dbo.T.
            foreach (var key in map.Keys.OrderByDescending(k => k.Length))
            {
                if (!map.TryGetValue(key, out var info))
                    continue;
                var dot = key.IndexOf('.');
                if (dot <= 0)
                    continue;
                var schema = Regex.Escape(key.Substring(0, dot));
                var name = Regex.Escape(key.Substring(dot + 1));
                var tablePattern = "(?:" + schema + "\\s*\\.\\s*" + name + "|\\["
                    + Regex.Escape(key.Substring(0, dot)) + "\\]\\s*\\.\\s*\\["
                    + Regex.Escape(key.Substring(dot + 1)) + "\\])";

                // FROM <bảng> [AS] [alias] FOR SYSTEM_TIME <biến thể>
                var fromPattern = "(FROM\\s+" + tablePattern
                    + ")((?:\\s+(?:AS\\s+)?[\\w$#@\\[\\]]+)?)\\s+FOR\\s+SYSTEM_TIME\\s+";
                var shortName = key.Substring(dot + 1);
                result = RewriteVariant(result, fromPattern, shortName, "ALL\\b", null, info);
                result = RewriteVariant(result, fromPattern, shortName, "AS\\s+OF\\s+(?<a>'[^']*'|[\\w@#]+|\\([^()]*\\))",
                    (a, b) => Q(info.StartCol) + " <= " + a + " AND " + a + " < " + Q(info.EndCol), info);
                result = RewriteVariant(result, fromPattern, shortName,
                    "BETWEEN\\s+(?<a>'[^']*'|[\\w@#]+|\\([^()]*\\))\\s+AND\\s+(?<b>'[^']*'|[\\w@#]+|\\([^()]*\\))",
                    (a, b) => Q(info.StartCol) + " <= " + b + " AND " + a + " < " + Q(info.EndCol), info);
                result = RewriteVariant(result, fromPattern, shortName,
                    "FROM\\s+(?<a>'[^']*'|[\\w@#]+|\\([^()]*\\))\\s+TO\\s+(?<b>'[^']*'|[\\w@#]+|\\([^()]*\\))",
                    (a, b) => Q(info.StartCol) + " < " + b + " AND " + a + " < " + Q(info.EndCol), info);
                result = RewriteVariant(result, fromPattern, shortName,
                    "CONTAINED\\s+IN\\s*\\(\\s*(?<a>'[^']*'|[\\w@#]+|\\([^()]*\\))\\s*,\\s*(?<b>'[^']*'|[\\w@#]+|\\([^()]*\\))\\s*\\)",
                    (a, b) => a + " <= " + Q(info.StartCol) + " AND " + Q(info.EndCol) + " <= " + b, info);
            }
            return result;
        }

        private static string Q(string n) => "[" + n.Replace("]", "]]") + "]";

        private static string RewriteVariant(
            string script, string fromPattern, string shortName, string variantPattern,
            Func<string, string, string>? predicate,
            (string HistorySchema, string HistoryTable, string StartCol, string EndCol) info)
        {
            // Bắt alias (nhóm 2 của fromPattern); thiếu alias thì lấy tên bảng ngắn.
            var full = fromPattern + "(?:" + variantPattern + ")";
            return Regex.Replace(script, full, m =>
            {
                var from = m.Groups[1].Value;
                var aliasRaw = m.Groups[2].Value.Trim();
                string alias = Q(shortName.Trim('[', ']'));
                if (aliasRaw.Length > 0)
                {
                    var am = Regex.Match(aliasRaw, @"(?:AS\s+)?(?<a>[\w$#@]+|\[[^\]]+\])\s*$",
                        RegexOptions.IgnoreCase);
                    if (am.Success)
                        alias = am.Groups["a"].Value;
                }

                // Tách tên bảng gốc từ mệnh đề FROM để UNION đúng bảng.
                var tm = Regex.Match(from, @"FROM\s+(?<t>.+)$",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline);
                var baseTable = tm.Success ? tm.Groups["t"].Value.Trim() : from;
                var histTable = Q(info.HistorySchema) + "." + Q(info.HistoryTable);

                string? where = null;
                if (predicate != null)
                {
                    var a = m.Groups["a"].Value;
                    string b = m.Groups["b"].Success ? m.Groups["b"].Value : a;
                    where = predicate(a, b);
                }

                var sb = new StringBuilder();
                sb.Append("FROM (SELECT * FROM ").Append(baseTable);
                if (where != null)
                    sb.Append(" WHERE ").Append(where);
                sb.Append(" UNION ALL SELECT * FROM ").Append(histTable);
                if (where != null)
                    sb.Append(" WHERE ").Append(where);
                sb.Append(") AS ").Append(alias);
                return sb.ToString();
            }, RegexOptions.IgnoreCase);
        }

        /// <summary>
        /// Dựng map temporal "schema.table" → (history, start, end) từ metadata đã trích.
        /// </summary>
        public static IReadOnlyDictionary<string, (string HistorySchema, string HistoryTable, string StartCol, string EndCol)> BuildMap(
            IEnumerable<TableSchema> tables)
        {
            var map = new Dictionary<string, (string, string, string, string)>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var t in tables)
            {
                if (!t.NeedsTemporalConversion)
                    continue;
                map[t.PlainName] = (
                    t.HistorySchema ?? t.Schema,
                    t.HistoryTable ?? (t.Name + "_History"),
                    t.PeriodStartColumn ?? "SysStartTime",
                    t.PeriodEndColumn ?? "SysEndTime");
            }
            return map;
        }
    }
}
