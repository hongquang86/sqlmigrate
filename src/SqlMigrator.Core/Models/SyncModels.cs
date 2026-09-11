using System;
using System.Collections.Generic;

namespace SqlMigrator.Core.Models
{
    /// <summary>Loại đồng bộ dữ liệu đã chọn (2 giai đoạn thao tác Update trong UI).</summary>
    public enum SyncKind
    {
        /// <summary>Chỉ chép các bản ghi ĐƯỢC THÊM MỚI trên nguồn (append-only).</summary>
        AppendNew,

        /// <summary>
        /// Đồng bộ đầy đủ từng bảng: xóa hết dữ liệu bảng đích rồi chép lại toàn bộ từ nguồn
        /// (bao gồm cả bản ghi mới, bản ghi đã bị SỬA, và giúp đích khớp lại với nguồn).
        /// </summary>
        Full
    }

    /// <summary>
    /// Mốc đồng bộ của một bảng: con trỏ phía NGUỒN (count, max identity, max rowversion)
    /// ghi nhận tại thời điểm lần chạy trước kết thúc thành công.
    /// </summary>
    public sealed class TableBaseline
    {
        public string Schema { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;

        public long RowCount { get; set; }

        /// <summary>MAX(cột identity) phía nguồn lúc đồng bộ; null nếu bảng không có.</summary>
        public long? MaxIdentity { get; set; }

        /// <summary>MAX(cột rowversion chuyển sang bigint) phía nguồn; null nếu bảng không có.</summary>
        public long? MaxRowVersion { get; set; }

        /// <summary>Tên cột identity (null nếu bảng không có).</summary>
        public string? IdentityColumn { get; set; }

        /// <summary>Tên cột rowversion (null nếu bảng không có).</summary>
        public string? RowVersionColumn { get; set; }

        /// <summary>Danh sách cột khóa chính (tên thật, chưa quote).</summary>
        public IReadOnlyList<string> PrimaryKeyColumns { get; set; } = Array.Empty<string>();
    }

    /// <summary>Toàn bộ baseline cho một cặp nguồn–đích.</summary>
    public sealed class SyncBaseline
    {
        public string SourceKey { get; init; } = string.Empty;
        public string DestinationKey { get; init; } = string.Empty;
        public DateTime SavedAtUtc { get; set; } = DateTime.UtcNow;

        /// <summary>Key theo schema.tên bảng (so sánh không phân biệt hoa thường).</summary>
        public IReadOnlyDictionary<string, TableBaseline> Tables { get; set; }
            = new Dictionary<string, TableBaseline>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Một bảng nhận thấy có thay đổi dữ liệu giữa mốc baseline và hiện tại.</summary>
    public sealed class SyncTableChange
    {
        public string Schema { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;

        /// <summary>Dạng schema.tên (không quote), dùng làm khóa đối chiếu.</summary>
        public string PlainName => Schema + "." + Name;

        /// <summary>Số dòng mới/đã đổi ước lượng được.</summary>
        public long DeltaCount { get; init; }

        /// <summary>Có cột monotonic (rowversion hoặc identity) để chọn CHÍNH XÁC dòng mới không.</summary>
        public bool CanDetectExactRows { get; init; }

        /// <summary>Có dữ liệu mới rõ ràng (count tăng / identity tăng / rowversion tăng).</summary>
        public bool HasChanges => DeltaCount != 0 || LastRowVersionChanged || RowCountChanged;

        public bool LastRowVersionChanged { get; init; }
        public bool RowCountChanged { get; init; }

        /// <summary>Ghi chú (ví dụ: cảnh báo chỉ đếm được tổng, không chọn đúng dòng).</summary>
        public string? Note { get; init; }
    }

    /// <summary>Kết quả kiểm tra thay đổi dữ liệu nguồn so với baseline.</summary>
    public sealed class SyncCheckResult
    {
        public bool HasChanges { get; set; }
        public long TotalNewRows { get; set; }

        /// <summary>Danh sách bảng có thay đổi (đã lọc theo bảng được chọn).</summary>
        public IReadOnlyList<SyncTableChange> Changes { get; set; } = Array.Empty<SyncTableChange>();

        /// <summary>Đúng khi có baseline (đã từng chạy full/thành công) — nếu không sẽ không dò được.</summary>
        public bool HasBaseline { get; set; }
        public IReadOnlyList<string> Errors { get; set; } = Array.Empty<string>();
        public IReadOnlyList<string> Warnings { get; set; } = Array.Empty<string>();
    }

    /// <summary>Kết quả đồng bộ của một bảng.</summary>
    public sealed class SyncTableResult
    {
        public string PlainName { get; init; } = string.Empty;
        public long RowsSynced { get; set; }
        public bool Success { get; set; } = true;
        public string? Error { get; set; }
    }

    /// <summary>Kết quả toàn đợt đồng bộ (Update New hoặc Update Full).</summary>
    public sealed class SyncResult
    {
        public bool Success { get; set; } = true;
        public long RowsSynced { get; set; }
        public TimeSpan Elapsed { get; set; }
        public IReadOnlyList<SyncTableResult> Tables { get; set; } = Array.Empty<SyncTableResult>();
        public IReadOnlyList<string> Errors { get; set; } = Array.Empty<string>();

        public void AddError(string message)
        {
            var list = new List<string>(Errors) { message };
            Errors = list;
            Success = false;
        }
    }
}