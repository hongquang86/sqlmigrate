using System;
using System.Collections.Generic;

namespace SqlMigrator.Core.Models
{
    /// <summary>
    /// Toàn cảnh trước khi di chuyển: phiên bản hai server, kiểu di chuyển (nâng/hạ/cùng),
    /// database có tồn tại hay không, và số lượng bảng — để giao diện cảnh báo sớm
    /// trước khi người dùng bấm nút chạy.
    /// </summary>
    public sealed class PreflightResult
    {
        /// <summary>Phiên bản chính (major) server nguồn; -1 nếu không đọc được.</summary>
        public int SourceMajorVersion { get; set; } = -1;

        /// <summary>Phiên bản chính (major) server đích; -1 nếu không đọc được.</summary>
        public int DestinationMajorVersion { get; set; } = -1;

        /// <summary>Phân loại di chuyển dựa trên phiên bản nguồn/đích.</summary>
        public MigrationKind Kind { get; set; } = MigrationKind.Unknown;

        public bool SourceDatabaseExists { get; set; }
        public bool DestinationDatabaseExists { get; set; }

        public long SourceTableCount { get; set; } = -1;
        public long DestinationTableCount { get; set; } = -1;

        /// <summary>Hồ sơ kiểm kê chi tiết nguồn (null khi chưa đọc được).</summary>
        public DatabaseInventory? SourceInventory { get; set; }

        /// <summary>Hồ sơ kiểm kê chi tiết đích (null khi đích chưa tồn tại/chưa đọc được).</summary>
        public DatabaseInventory? DestinationInventory { get; set; }

        /// <summary>Số trigger cấp server trên nguồn; -1 nếu không đọc được.</summary>
        public int SourceServerTriggerCount { get; set; } = -1;

        /// <summary>Lỗi chặn đường vào được (ví dụ không kết nối nổi server).</summary>
        public IReadOnlyList<string> Errors { get; set; } = Array.Empty<string>();

        /// <summary>Điều cần lưu ý (ví dụ hạ cấp phiên bản, đích đã có dữ liệu).</summary>
        public IReadOnlyList<string> Warnings { get; set; } = Array.Empty<string>();

        public void AddError(string message)
        {
            var list = new List<string>(Errors) { message };
            Errors = list;
        }

        public void AddWarning(string message)
        {
            var list = new List<string>(Warnings) { message };
            Warnings = list;
        }

        public static string DescribeVersion(int major) => major switch
        {
            9 => "2005",
            10 => "2008",
            11 => "2012",
            12 => "2014",
            13 => "2016",
            14 => "2017",
            15 => "2019",
            16 => "2022",
            17 => "2025",
            _ => "Server " + major + ".0"
        };
    }
}