using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using SqlMigrator.Core.Models;

namespace SqlMigrator.Core.Services
{
    /// <summary>
    /// Định dạng hiển thị độ dùng module (ngày tạo/lần cuối) cho báo cáo và log.
    /// Thuần chuỗi để dễ kiểm thử; UI gọi lại thay vì tự định dạng.
    /// </summary>
    public static class ModuleUsageText
    {
        /// <summary>Định dạng ngày tạo cho grid ("—" khi không rõ).</summary>
        public static string FormatCreatedDate(DateTime? date) =>
            date?.ToString("dd/MM/yyyy") ?? "—";

        /// <summary>Định dạng lần dùng cuối ("chưa thấy" khi null).</summary>
        public static string FormatLastUsed(DateTime? lastUsed) =>
            lastUsed?.ToString("dd/MM/yyyy HH:mm") ?? "chưa thấy";

        /// <summary>Dòng log độ dùng: ngày tạo + lần cuối + nguồn chứng cứ (rỗng nếu không có gì).</summary>
        public static string FormatUsageLogLine(ReconcileIssue issue)
        {
            if (issue.CreatedDate == null && issue.LastUsedDate == null && issue.UsageEvidence == null)
                return "";
            var created = issue.CreatedDate != null
                ? "tạo từ " + issue.CreatedDate.Value.ToString("dd/MM/yyyy")
                : "không rõ ngày tạo";
            var used = issue.LastUsedDate != null
                ? "dùng lần cuối " + issue.LastUsedDate.Value.ToString("dd/MM/yyyy HH:mm")
                    + (issue.UseCount != null ? $" ({issue.UseCount:N0} lần)" : "")
                    + " theo " + (issue.UsageEvidence ?? "nguồn chứng cứ")
                : "chưa từng thấy dùng"
                    + (issue.UsageEvidence != null ? " theo " + issue.UsageEvidence : "");
            return created + ", " + used + ".";
        }
    }

    /// <summary>
    /// Tra cứu "độ dùng" của view/SP/function trên database NGUỒN để user quyết định
    /// giữ hay bỏ qua object lỗi: ngày tạo/sửa (sys.objects, luôn có) + số lần chạy
    /// và lần chạy cuối (Query Store nếu bật, gộp plan cache). CHỈ SELECT nguồn,
    /// không bao giờ ghi. Mọi bước bọc try/catch riêng: thiếu quyền hoặc server cũ
    /// thì bỏ qua phần đó, không làm sập lần quét.
    /// TRUNG THỰC: "chưa thấy dùng" nghĩa là không có dấu vết trong Query Store
    /// (theo retention) và plan cache (từ lần restart server) — không chứng minh
    /// tuyệt đối là không ai dùng.
    /// </summary>
    public sealed class ModuleUsageReader
    {
        private readonly ILogger _logger;

        public ModuleUsageReader(ILogger logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Điền CreatedDate/ModifiedDate/UseCount/LastUsedDate/UsageEvidence cho các
        /// issue view/SP/function được chọn. Không ném lỗi.
        /// </summary>
        public async Task FillAsync(
            string sourceConnectionString,
            IReadOnlyList<ReconcileIssue> moduleIssues,
            CancellationToken ct = default)
        {
            if (moduleIssues.Count == 0)
                return;

            try
            {
                using var conn = new SqlConnection(sourceConnectionString);
                await conn.OpenAsync(ct).ConfigureAwait(false);

                await FillDatesAsync(conn, moduleIssues, ct).ConfigureAwait(false);

                // Query Store trước (bền vững), plan cache sau (từ lúc restart).
                var qsFound = await FillFromQueryStoreAsync(conn, moduleIssues, ct).ConfigureAwait(false);
                await FillFromPlanCacheAsync(conn, moduleIssues, !qsFound, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug("Bỏ qua tra cứu độ dùng module ({Message}).", ex.Message);
            }
        }

        /// <summary>Ngày tạo/sửa từ sys.objects cho mọi module người dùng (1 truy vấn).</summary>
        private async Task FillDatesAsync(
            SqlConnection conn, IReadOnlyList<ReconcileIssue> issues, CancellationToken ct)
        {
            const string sql = @"
SELECT s.name + '.' + o.name, o.create_date, o.modify_date
  FROM sys.objects o
  JOIN sys.schemas s ON s.schema_id = o.schema_id
 WHERE o.is_ms_shipped = 0
   AND o.type IN ('V','P','FN','IF','TF','TR');";
            var dates = new Dictionary<string, (DateTime Created, DateTime Modified)>(
                StringComparer.OrdinalIgnoreCase);
            using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 60 };
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                dates[reader.GetString(0)] = (reader.GetDateTime(1), reader.GetDateTime(2));

            foreach (var issue in issues)
            {
                if (dates.TryGetValue(issue.ObjectName, out var d))
                {
                    issue.CreatedDate = d.Created;
                    issue.ModifiedDate = d.Modified;
                }
            }
        }

        /// <summary>
        /// Số lần chạy + lần cuối từ Query Store (theo object_id chứa câu lệnh).
        /// Trả true nếu QS bật và đọc được (kể cả khi object này không có số liệu).
        /// </summary>
        private async Task<bool> FillFromQueryStoreAsync(
            SqlConnection conn, IReadOnlyList<ReconcileIssue> issues, CancellationToken ct)
        {
            try
            {
                using (var opt = new SqlCommand(
                    "SELECT actual_state FROM sys.database_query_store_options;", conn)
                    { CommandTimeout = 30 })
                {
                    var state = Convert.ToInt32(await opt.ExecuteScalarAsync(ct).ConfigureAwait(false));
                    // 1 = READ_ONLY, 2 = READ_WRITE. Các trạng thái khác coi như tắt/lỗi.
                    if (state != 1 && state != 2)
                        return false;
                }

                const string sql = @"
SELECT OBJECT_SCHEMA_NAME(q.object_id) + '.' + OBJECT_NAME(q.object_id),
       SUM(rs.count_executions), MAX(rs.last_execution_time)
  FROM sys.query_store_query q
  JOIN sys.query_store_plan p ON p.query_id = q.query_id
  JOIN sys.query_store_runtime_stats rs ON rs.plan_id = p.plan_id
 WHERE q.object_id IS NOT NULL AND q.object_id <> 0
 GROUP BY OBJECT_SCHEMA_NAME(q.object_id), OBJECT_NAME(q.object_id);";
                var stats = new Dictionary<string, (long Count, DateTime Last)>(
                    StringComparer.OrdinalIgnoreCase);
                using (var cmd = new SqlCommand(sql, conn) { CommandTimeout = 120 })
                using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(ct).ConfigureAwait(false))
                        stats[reader.GetString(0)] = (reader.GetInt64(1), reader.GetDateTime(2));
                }

                foreach (var issue in issues)
                {
                    issue.UsageEvidence = "Query Store";
                    if (stats.TryGetValue(issue.ObjectName, out var s))
                    {
                        issue.UseCount = s.Count;
                        issue.LastUsedDate = s.Last;
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogDebug("Bỏ qua Query Store ({Message}).", ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Gộp chứng cứ từ plan cache (tên object xuất hiện trong câu lệnh đã cache).
        /// onlyMissing=true thì chỉ tra object chưa có số liệu QS. Cần VIEW SERVER STATE.
        /// </summary>
        private async Task FillFromPlanCacheAsync(
            SqlConnection conn, IReadOnlyList<ReconcileIssue> issues, bool onlyMissing, CancellationToken ct)
        {
            try
            {
                var targets = issues
                    .Where(i => !onlyMissing || i.UseCount == null)
                    .ToList();
                if (targets.Count == 0)
                    return;

                // Một truy vấn duy nhất (TOP 500 mới nhất), khớp tên ở C# để rẻ.
                const string sql = @"
SELECT TOP 500 qs.last_execution_time, qs.execution_count, st.text
  FROM sys.dm_exec_query_stats qs
 CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) st
 ORDER BY qs.last_execution_time DESC;";
                var rows = new List<(DateTime Last, long Count, string Text)>();
                using (var cmd = new SqlCommand(sql, conn) { CommandTimeout = 120 })
                using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(ct).ConfigureAwait(false))
                        rows.Add((reader.GetDateTime(0), reader.GetInt64(1),
                            reader.IsDBNull(2) ? "" : reader.GetString(2)));
                }

                DateTime? serverStart = null;
                try
                {
                    using var cmd2 = new SqlCommand(
                        "SELECT sqlserver_start_time FROM sys.dm_os_sys_info;", conn)
                        { CommandTimeout = 30 };
                    var v = await cmd2.ExecuteScalarAsync(ct).ConfigureAwait(false);
                    if (v is DateTime dt)
                        serverStart = dt;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug("Không đọc được giờ khởi động server ({Message}).", ex.Message);
                }

                foreach (var issue in targets)
                {
                    var dot = issue.ObjectName.LastIndexOf('.');
                    if (dot <= 0)
                        continue;
                    var schema = issue.ObjectName.Substring(0, dot);
                    var name = issue.ObjectName.Substring(dot + 1);

                    long count = 0;
                    DateTime? last = null;
                    foreach (var (rowLast, rowCount, text) in rows)
                    {
                        if (!TextReferencesObject(text, schema, name))
                            continue;
                        count += rowCount;
                        if (last == null || rowLast > last)
                            last = rowLast;
                    }

                    if (issue.UsageEvidence == null)
                        issue.UsageEvidence = "plan cache"
                            + (serverStart == null ? "" : " (từ " + serverStart.Value.ToString("dd/MM/yyyy HH:mm") + ")");
                    if (count > 0)
                    {
                        issue.UseCount = (issue.UseCount ?? 0) + count;
                        if (last != null && (issue.LastUsedDate == null || last > issue.LastUsedDate))
                            issue.LastUsedDate = last;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug("Bỏ qua plan cache ({Message}).", ex.Message);
            }
        }

        /// <summary>
        /// Câu SQL có nhắc tới object "schema.name" không (mọi dạng ngoặc, không phân
        /// biệt hoa/thường, có chặn biên để "dbo.A" không khớp "dbo.ABC").
        /// Thuần chuỗi để dễ kiểm thử.
        /// </summary>
        internal static bool TextReferencesObject(string? text, string schema, string name)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(schema) || string.IsNullOrEmpty(name))
                return false;
            var s = Regex.Escape(schema);
            var n = Regex.Escape(name);
            // Biên phải: không phải ký tự tên/biến (tránh khớp tiền tố tên dài hơn).
            const string end = @"(?![\w$#\]])";
            return Regex.IsMatch(text, @"\[" + s + @"\]\s*\.\s*\[" + n + @"\]" + end, RegexOptions.IgnoreCase)
                || Regex.IsMatch(text, @"\[" + s + @"\]\s*\.\s*" + n + end, RegexOptions.IgnoreCase)
                || Regex.IsMatch(text, s + @"\s*\.\s*\[" + n + @"\]" + end, RegexOptions.IgnoreCase)
                || Regex.IsMatch(text, @"(?<![\w@#\$\[])" + s + @"\s*\.\s*" + n + end, RegexOptions.IgnoreCase);
        }
    }
}
