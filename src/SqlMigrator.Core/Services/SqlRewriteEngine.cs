using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace SqlMigrator.Core.Services
{
    /// <summary>Kết quả áp dụng rewrite rules lên một script SQL.</summary>
    public sealed class RewriteResult
    {
        /// <summary>Script sau khi đã rewrite (nếu có thay đổi).</summary>
        public string RewrittenScript { get; set; } = string.Empty;

        /// <summary>Script gốc (không thay đổi).</summary>
        public string OriginalScript { get; set; } = string.Empty;

        /// <summary>Các rule IDs đã áp dụng thành công.</summary>
        public IReadOnlyList<string> AppliedRules { get; set; } = Array.Empty<string>();

        /// <summary>
        /// Cảnh báo kèm theo các rewrite đã áp dụng (VD: lột masking khiến dữ liệu hết che).
        /// User vẫn chỉ cần bấm Xử lý một lần, nhưng phải đọc các cảnh báo này.
        /// </summary>
        public IReadOnlyList<string> Warnings { get; set; } = Array.Empty<string>();

        /// <summary>Các rule IDs KHÔNG THỂ áp dụng (feature không hỗ trợ).</summary>
        public IReadOnlyList<string> UnsupportedRules { get; set; } = Array.Empty<string>();

        /// <summary>
        /// Id các shim Compat phải triển khai lên đích trước khi chạy script đã rewrite.
        /// </summary>
        public IReadOnlyList<string> RequiredShims { get; set; } = Array.Empty<string>();

        /// <summary>Mô tả các vấn đề không hỗ trợ.</summary>
        public IReadOnlyList<string> UnsupportedMessages { get; set; } = Array.Empty<string>();

        /// <summary>Có thay đổi gì không.</summary>
        public bool HasChanges => AppliedRules.Count > 0 || UnsupportedRules.Count > 0;

        /// <summary>Có thể rewrite hoàn toàn không (tất cả rule đều hỗ trợ).</summary>
        public bool IsFullyRewritable => UnsupportedRules.Count == 0;
    }

    /// <summary>Một rewrite rule từ config file.</summary>
    public sealed class RewriteRule
    {
        public string Id { get; set; } = string.Empty;
        public string Pattern { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Action { get; set; } = string.Empty; // regex_replace, inject_function, not_supported
        public string? Message { get; set; }
        public string? SearchRegex { get; set; }
        public string? ReplaceFormat { get; set; }
        /// <summary>
        /// Cảnh báo (tiếng Việt) đính kèm khi rule được áp dụng thành công.
        /// VD: rule lột masking phải cảnh báo dữ liệu đích sẽ hết che.
        /// </summary>
        public string? Warning { get; set; }

        /// <summary>
        /// Id shim Compat cần triển khai lên đích trước khi chạy script đã rewrite
        /// (VD: "Json", "Date", "Session", "TimeZone"). Null/rỗng = không cần.
        /// </summary>
        public string? RequiresShim { get; set; }
        public string? FunctionName { get; set; }
        public string? FunctionDefinition { get; set; }
        public bool Enabled { get; set; } = true;
    }

    /// <summary>Config file chứa các rewrite rules cho downgrade SQL Server.</summary>
    public sealed class RewriteConfig
    {
        public int SourceVersion { get; set; }
        public int TargetVersion { get; set; }
        public IReadOnlyList<RewriteRule> Rules { get; set; } = Array.Empty<RewriteRule>();
    }

    /// <summary>
    /// Engine rewrite SQL scripts từ phiên bản nguồn sang phiên bản đích.
    /// Chỉ ĐỌC rules từ config file, KHÔNG ghi database nào.
    /// </summary>
    public sealed class SqlRewriteEngine
    {
        private readonly RewriteConfig _config;
        private readonly ILogger _logger;

        public SqlRewriteEngine(RewriteConfig config, ILogger logger)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>Tạo SqlRewriteEngine từ file config JSON.</summary>
        public static SqlRewriteEngine FromFile(string configPath, ILogger logger)
        {
            if (!File.Exists(configPath))
                throw new FileNotFoundException("Không tìm thấy file rewrite rules: " + configPath);

            var json = File.ReadAllText(configPath);
            var config = JsonSerializer.Deserialize<RewriteConfig>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip
            });

            if (config == null)
                throw new InvalidOperationException("File rewrite rules rỗng hoặc sai định dạng: " + configPath);

            logger.LogInformation("Đã tải {Count} rewrite rules (SQL {Source} → {Target}).",
                config.Rules.Count(r => r.Enabled), config.SourceVersion, config.TargetVersion);

            return new SqlRewriteEngine(config, logger);
        }

        /// <summary>Lấy version mục tiêu từ config.</summary>
        public int TargetVersion => _config.TargetVersion;

        /// <summary>
        /// Áp dụng rewrite rules lên script SQL.
        /// Trả về RewriteResult chứa script đã rewrite + danh sách rule đã dùng/chưa hỗ trợ.
        /// KHÔNG ghi database nào — chỉ transform chuỗi.
        /// </summary>
        public RewriteResult Rewrite(string script)
        {
            if (string.IsNullOrWhiteSpace(script))
                return new RewriteResult { OriginalScript = script ?? string.Empty, RewrittenScript = script ?? string.Empty };

            var result = new RewriteResult { OriginalScript = script, RewrittenScript = script };
            var applied = new List<string>();
            var unsupported = new List<string>();
            var unsupportedMessages = new List<string>();
            var warnings = new List<string>();
            var shims = new List<string>();

            foreach (var rule in _config.Rules.Where(r => r.Enabled))
            {
                // Kiểm tra pattern cơ bản trước khi regex
                if (!script.Contains(rule.Pattern, StringComparison.OrdinalIgnoreCase))
                    continue;

                switch (rule.Action)
                {
                    case "not_supported":
                        // Rule cảnh báo có thể kèm SearchRegex làm cổng: chỉ gắn cờ khi
                        // regex khớp (VD: OPENJSON dạng WITH mới cấm, dạng thường vẫn rewrite).
                        if (!IsRuleTriggered(rule, script))
                            continue;
                        unsupported.Add(rule.Id);
                        unsupportedMessages.Add(rule.Description + ": " + (rule.Message ?? ""));
                        _logger.LogWarning("Rule {RuleId}: {Description}", rule.Id, rule.Description);
                        break;

                    case "regex_replace":
                        if (ApplyRegexReplace(rule, result))
                        {
                            applied.Add(rule.Id);
                            AddRuleWarning(rule, result, warnings);
                            AddRequiredShim(rule, shims);
                        }
                        break;

                    case "inject_function":
                        if (ApplyInjectFunction(rule, result))
                        {
                            applied.Add(rule.Id);
                            AddRuleWarning(rule, result, warnings);
                            AddRequiredShim(rule, shims);
                        }
                        break;
                }
            }

            result.AppliedRules = applied;
            result.UnsupportedRules = unsupported;
            result.UnsupportedMessages = unsupportedMessages;
            result.Warnings = warnings;
            result.RequiredShims = shims.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return result;
        }

        /// <summary>
        /// Rule không cổng (không có SearchRegex) khớp mọi script chứa Pattern;
        /// rule có cổng chỉ khớp khi regex khớp (dùng cho not_supported chọn lọc).
        /// </summary>
        private static bool IsRuleTriggered(RewriteRule rule, string script)
        {
            if (string.IsNullOrEmpty(rule.SearchRegex))
                return true;
            try
            {
                return Regex.IsMatch(script, rule.SearchRegex,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }
            catch
            {
                // Regex cổng hỏng thì coi như khớp để không bỏ sót cảnh báo.
                return true;
            }
        }

        /// <summary>Ghi nhận shim Compat mà script đã rewrite cần (triển khai lên đích trước CREATE).</summary>
        private static void AddRequiredShim(RewriteRule rule, List<string> shims)
        {
            if (string.IsNullOrWhiteSpace(rule.RequiresShim))
                return;
            if (!shims.Any(s => s.Equals(rule.RequiresShim, StringComparison.OrdinalIgnoreCase)))
                shims.Add(rule.RequiresShim);
        }

        /// <summary>Ghi nhận cảnh báo kèm rule (nếu rule khai báo Warning).</summary>
        private void AddRuleWarning(RewriteRule rule, RewriteResult result, List<string> warnings)
        {
            if (string.IsNullOrWhiteSpace(rule.Warning))
                return;
            var text = rule.Id + ": " + rule.Warning;
            if (!warnings.Contains(text))
            {
                warnings.Add(text);
                _logger.LogWarning("Rule {RuleId} cảnh báo: {Warning}", rule.Id, rule.Warning);
            }
        }

        private bool ApplyRegexReplace(RewriteRule rule, RewriteResult result)
        {
            // ReplaceFormat được phép rỗng (nghĩa là xóa đoạn khớp, VD: lột mệnh đề MASKED).
            if (string.IsNullOrEmpty(rule.SearchRegex) || rule.ReplaceFormat == null)
                return false;

            try
            {
                var regex = new Regex(rule.SearchRegex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (!regex.IsMatch(result.RewrittenScript))
                    return false;

                result.RewrittenScript = regex.Replace(result.RewrittenScript, rule.ReplaceFormat);
                _logger.LogDebug("Áp dụng rule {RuleId}: {Description}", rule.Id, rule.Description);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Lỗi áp dụng rule {RuleId}: {Message}", rule.Id, ex.Message);
                return false;
            }
        }

        private bool ApplyInjectFunction(RewriteRule rule, RewriteResult result)
        {
            if (string.IsNullOrEmpty(rule.FunctionName) || string.IsNullOrEmpty(rule.FunctionDefinition))
                return false;

            if (string.IsNullOrEmpty(rule.SearchRegex) || string.IsNullOrEmpty(rule.ReplaceFormat))
                return false;

            try
            {
                var regex = new Regex(rule.SearchRegex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (!regex.IsMatch(result.RewrittenScript))
                    return false;

                // Thay thế hàm trong script
                result.RewrittenScript = regex.Replace(result.RewrittenScript, rule.ReplaceFormat);

                // Thêm CREATE FUNCTION vào đầu script (trước khi tạo object chính)
                var functionScript = rule.FunctionDefinition + ";\nGO\n\n";
                result.RewrittenScript = functionScript + result.RewrittenScript;

                _logger.LogDebug("Áp dụng rule {RuleId} (inject function {FuncName}): {Description}",
                    rule.Id, rule.FunctionName, rule.Description);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Lỗi áp dụng rule {RuleId}: {Message}", rule.Id, ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Phân tích script và trả về danh sách vấn đề compatibility.
        /// KHÔNG rewrite — chỉ phân tích.
        /// </summary>
        public IReadOnlyList<RewriteIssue> AnalyzeScript(string script)
        {
            var issues = new List<RewriteIssue>();

            foreach (var rule in _config.Rules.Where(r => r.Enabled))
            {
                if (string.IsNullOrEmpty(rule.Pattern) || !script.Contains(rule.Pattern, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!IsRuleTriggered(rule, script))
                    continue;

                issues.Add(new RewriteIssue
                {
                    RuleId = rule.Id,
                    Description = rule.Description,
                    IsSupported = rule.Action != "not_supported",
                    Message = rule.Message ?? "",
                    Pattern = rule.Pattern,
                    Warning = rule.Warning ?? "",
                    RequiresShim = rule.RequiresShim ?? ""
                });
            }

            return issues;
        }
    }

    /// <summary>Vấn đề compatibility khi phân tích script.</summary>
    public sealed class RewriteIssue
    {
        public string RuleId { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public bool IsSupported { get; set; }
        public string Message { get; set; } = string.Empty;
        public string Pattern { get; set; } = string.Empty;
        /// <summary>Cảnh báo kèm theo nếu rule được áp dụng (từ trường Warning của rule).</summary>
        public string Warning { get; set; } = string.Empty;
        /// <summary>Id shim Compat cần triển khai (từ trường RequiresShim của rule).</summary>
        public string RequiresShim { get; set; } = string.Empty;
    }
}
