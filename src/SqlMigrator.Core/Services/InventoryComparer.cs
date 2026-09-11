using System;
using System.Collections.Generic;
using System.Linq;
using SqlMigrator.Core.Models;

namespace SqlMigrator.Core.Services
{
    /// <summary>Kết quả đối chiếu một nhóm đối tượng (bảng/view/...) nguồn vs đích.</summary>
    public sealed class InventoryDiff
    {
        public string Label { get; init; } = string.Empty;
        public int SourceCount { get; init; }
        public int DestCount { get; init; }
        public IReadOnlyList<string> MissingNames { get; init; } = Array.Empty<string>();
        public bool Matches => MissingNames.Count == 0;
    }

    /// <summary>
    /// So sánh hồ sơ nguồn/đích + dựng các dòng báo cáo tiếng Việt cho nhật ký
    /// real-time. Thuần logic (không chạm database) nên dễ kiểm thử.
    /// </summary>
    public static class InventoryComparer
    {
        /// <summary>
        /// Chuẩn hóa mã loại sys.objects thành tiền tố key kiểm kê.
        /// BẮT BUỘC Trim vì cột type là char(2) ("U "/"V "/"P " có dấu cách đuôi).
        /// Trả null với loại không quan tâm.
        /// </summary>
        internal static string? InventoryKey(string? sysType)
        {
            return (sysType ?? "").Trim() switch
            {
                "U" => "TABLE:",
                "V" => "VIEW:",
                "P" => "P:",
                "FN" or "IF" or "TF" => "F:",
                "TR" => "TR:",
                _ => null
            };
        }

        /// <summary>Đối chiếu từng nhóm đối tượng; thiếu = có nguồn, không có đích.</summary>
        public static IReadOnlyList<InventoryDiff> Compare(DatabaseInventory source, DatabaseInventory dest)
        {
            return new List<InventoryDiff>
            {
                Diff("Bảng", source.Tables.Select(t => t.PlainName), dest.Tables.Select(t => t.PlainName)),
                Diff("View", source.Views.Select(t => t.PlainName), dest.Views.Select(t => t.PlainName)),
                Diff("Stored Procedure", source.Procedures.Select(t => t.PlainName), dest.Procedures.Select(t => t.PlainName)),
                Diff("Function", source.Functions.Select(t => t.PlainName), dest.Functions.Select(t => t.PlainName)),
                Diff("Trigger", source.Triggers.Select(t => t.PlainName), dest.Triggers.Select(t => t.PlainName)),
                Diff("Sequence", source.Sequences, dest.Sequences)
            };
        }

        private static InventoryDiff Diff(string label, IEnumerable<string> sourceNames, IEnumerable<string> destNames)
        {
            var src = sourceNames.ToList();
            var dst = new HashSet<string>(destNames, StringComparer.OrdinalIgnoreCase);
            var missing = src.Where(n => !dst.Contains(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return new InventoryDiff
            {
                Label = label,
                SourceCount = src.Count,
                DestCount = dst.Count,
                MissingNames = missing
            };
        }

        /// <summary>Các dòng hồ sơ một database (dùng cho nút Kiểm tra trước khi chạy).</summary>
        public static IReadOnlyList<string> FormatInventory(string title, DatabaseInventory inv)
        {
            var lines = new List<string> { title };
            if (inv == null || !inv.Ok)
            {
                lines.Add("  Không đọc được hồ sơ" + (inv?.Error != null ? ": " + inv.Error : "."));
                return lines;
            }

            var dbLabel = string.IsNullOrWhiteSpace(inv.DatabaseName) ? "(chưa rõ tên)" : inv.DatabaseName;
            lines.Add($"  Database: {dbLabel} (SQL {PreflightResult.DescribeVersion(inv.ServerMajorVersion)})");
            lines.Add($"  Bảng: {inv.Tables.Count:N0} (~{inv.EstimatedRows:N0} dòng, {inv.TotalColumns:N0} cột)"
                + $" | View: {inv.Views.Count:N0}"
                + $" | SP: {inv.Procedures.Count:N0}"
                + $" | Function: {inv.Functions.Count:N0}"
                + $" | Trigger: {inv.Triggers.Count:N0}"
                + $" | Sequence: {inv.Sequences.Count:N0}");

            var features = new List<string>();
            if (inv.TemporalTables > 0) features.Add($"{inv.TemporalTables} bảng temporal");
            if (inv.MaskedColumns > 0) features.Add($"{inv.MaskedColumns} cột masking");
            if (inv.RlsPolicies > 0) features.Add($"{inv.RlsPolicies} chính sách RLS");
            if (inv.EncryptedColumns > 0) features.Add($"{inv.EncryptedColumns} cột Always Encrypted");
            if (inv.ServerTriggers > 0) features.Add($"{inv.ServerTriggers} server trigger");
            lines.Add(features.Count > 0
                ? "  Tính năng 2016+ đang dùng: " + string.Join(", ", features) + "."
                : "  Không dùng tính năng đặc biệt 2016+ (thuận lợi khi hạ cấp).");
            return lines;
        }

        /// <summary>Các dòng đối chiếu đích vs nguồn (dùng sau khi di chuyển xong).</summary>
        public static IReadOnlyList<string> FormatComparison(
            DatabaseInventory source, DatabaseInventory dest, int maxMissingNames = 10)
        {
            var lines = new List<string> { "— ĐỐI CHIẾU ĐÍCH vs NGUỒN —" };
            if (source == null || !source.Ok || dest == null || !dest.Ok)
            {
                lines.Add("  Không đủ hồ sơ để đối chiếu (một bên đọc lỗi).");
                return lines;
            }

            var allMatch = true;
            foreach (var d in Compare(source, dest))
            {
                if (d.Matches)
                {
                    lines.Add($"  ✔ {d.Label}: nguồn {d.SourceCount:N0} → đích {d.DestCount:N0} (đủ).");
                    continue;
                }

                allMatch = false;
                lines.Add($"  ✘ {d.Label}: nguồn {d.SourceCount:N0} → đích {d.DestCount:N0} (thiếu {d.MissingNames.Count:N0}).");
                foreach (var name in d.MissingNames.Take(maxMissingNames))
                    lines.Add("      - thiếu: " + name);
                if (d.MissingNames.Count > maxMissingNames)
                    lines.Add($"      ... và {d.MissingNames.Count - maxMissingNames:N0} mục nữa.");
            }

            lines.Add(allMatch
                ? "Kết luận: đích KHỚP 100% nguồn về mặt đối tượng."
                : "Kết luận: đích CHƯA khớp nguồn — bấm 'Đồng bộ 100%' để xử lý phần thiếu.");
            return lines;
        }
    }
}
