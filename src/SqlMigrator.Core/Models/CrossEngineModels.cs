using System;
using System.Collections.Generic;

namespace SqlMigrator.Core.Models
{
    /// <summary>Tùy chọn một đợt di chuyển chéo engine.</summary>
    public sealed class CrossEngineOptions
    {
        /// <summary>Ghi đè bảng đích đã tồn tại (DROP rồi tạo lại). Mặc định false = bỏ qua.</summary>
        public bool OverwriteExistingTables { get; set; }
        /// <summary>Số dòng mỗi batch đọc/ghi.</summary>
        public int BatchRows { get; set; } = 5000;
        /// <summary>Chỉ di chuyển các bảng này ("schema.name", rỗng/null = tất cả).</summary>
        public IReadOnlyList<string>? OnlyTables { get; set; }
    }

    /// <summary>Kết quả di chuyển một bảng chéo engine.</summary>
    public sealed class CrossEngineTableResult
    {
        public string Table { get; set; } = string.Empty;
        public long SourceRows { get; set; }
        public long RowsCopied { get; set; }
        public bool Skipped { get; set; }
        public string? SkipReason { get; set; }
        public IReadOnlyList<string> Warnings { get; set; } = Array.Empty<string>();
        public IReadOnlyList<string> Errors { get; set; } = Array.Empty<string>();
    }

    /// <summary>Kết quả một đợt di chuyển chéo engine.</summary>
    public sealed class CrossEngineResult
    {
        public bool Success { get; set; } = true;
        public TimeSpan Elapsed { get; set; }
        public IReadOnlyList<CrossEngineTableResult> Tables { get; set; } = Array.Empty<CrossEngineTableResult>();
        public IReadOnlyList<string> Warnings { get; set; } = Array.Empty<string>();

        public long TotalRows
        {
            get
            {
                long sum = 0;
                foreach (var t in Tables)
                    sum += t.RowsCopied;
                return sum;
            }
        }
    }
}
