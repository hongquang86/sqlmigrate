using System;
using System.Collections.Generic;

namespace SqlMigrator.Core.Models
{
    /// <summary>
    /// Kết quả kiểm tra toàn vẹn cho TỪNG bảng: so sánh số dòng và checksum nội dung
    /// giữa database nguồn và database đích. Dùng để xác nhận "đích đầy đủ và đúng
    /// như nguồn" sau khi di chuyển hoặc khi người dùng bấm nút kiểm tra.
    /// </summary>
    public sealed class TableVerificationResult
    {
        /// <summary>Tên bảng dạng schema.name (không bọc dấu ngoặc).</summary>
        public string PlainName { get; init; } = string.Empty;

        /// <summary>Số dòng thực tế trên nguồn (COUNT_BIG).</summary>
        public long SourceRowCount { get; set; }

        /// <summary>Số dòng thực tế trên đích (COUNT_BIG).</summary>
        public long DestinationRowCount { get; set; }

        /// <summary>Checksum nội dung nguồn (CHECKSUM_AGG(BINARY_CHECKSUM(*))). NULL nếu bảng rỗng.</summary>
        public int? SourceChecksum { get; set; }

        /// <summary>Checksum nội dung đích. NULL nếu bảng rỗng.</summary>
        public int? DestinationChecksum { get; set; }

        /// <summary>Đúng khi số dòng khớp.</summary>
        public bool RowCountMatches => SourceRowCount == DestinationRowCount;

        /// <summary>Đúng khi checksum nội dung khớp (mặc định coi 2 bảng rỗng là khớp).</summary>
        public bool ChecksumMatches => SourceChecksum == DestinationChecksum;

        /// <summary>Trạng thái quyết định của bảng.</summary>
        public VerificationStatus Status { get; set; }

        /// <summary>Thông điệp chi tiết khi có sự cố (thiếu bảng, lỗi đọc...).</summary>
        public string? Message { get; set; }
    }

    /// <summary>Kết quả trên một nguồn/đích (dùng chung cho việc đọc số dòng).</summary>
    public enum VerificationStatus
    {
        /// <summary>Số dòng và checksum đều khớp — dữ liệu đầy đủ và đúng hoàn toàn.</summary>
        VerifiedExact,

        /// <summary>Không thể/rỗng bảng trên cả 2 phía (tạm coi là đạt).</summary>
        Empty,

        /// <summary>Bảng chỉ tồn tại ở một phía (thiếu bảng trên đích).</summary>
        MissingOnDestination,

        /// <summary>Số dòng khác nhau giữa nguồn và đích.</summary>
        RowCountMismatch,

        /// <summary>Cùng số dòng nhưng checksum nội dung khác nhau.</summary>
        ContentMismatch,

        /// <summary>Lỗi khi đọc một trong hai phía.</summary>
        Failed,

        /// <summary>Bảng bị loại trừ theo cấu hình (không tham gia kiểm tra).</summary>
        Skipped
    }

    /// <summary>
    /// Báo cáo tổng thể quá trình kiểm tra toàn vẹn database đích so với nguồn.
    /// Tổng hợp từng bảng + các bộ đếm để UI hiển thị thông điệp tổng kết ngắn gọn.
    /// </summary>
    public sealed class DataVerificationReport
    {
        public IReadOnlyList<TableVerificationResult> Tables { get; init; } = Array.Empty<TableVerificationResult>();

        /// <summary>Kết quả so sánh cấu trúc (số đối tượng mỗi loại + danh sách đối tượng thiếu trên đích).</summary>
        public StructureCheckResult? Structure { get; set; }

        /// <summary>Thời gian thực hiện toàn bộ quá trình kiểm tra.</summary>
        public TimeSpan Elapsed { get; set; }

        /// <summary>Số bảng khớp chính xác (VerifiedExact).</summary>
        public int VerifiedCount { get; set; }

        /// <summary>Số bảng có vấn đề nghiêm trọng (count/content mismatch hoặc thiếu bảng).</summary>
        public int IssueCount { get; set; }

        /// <summary>Số bảng lỗi khi đọc (không kết luận được).</summary>
        public int FailedCount { get; set; }

        /// <summary>Số bảng bị loại trừ.</summary>
        public int SkippedCount { get; set; }

        /// <summary>Không có vấn đề nghiêm trọng và không có lỗi đọc.</summary>
        public bool Success => IssueCount == 0 && FailedCount == 0 && (Structure == null || Structure.Success);

        /// <summary>Tổng số bảng được kiểm tra (đã loại trừ các bảng bị bỏ qua).</summary>
        public int TotalChecked => Tables.Count - SkippedCount;
    }

    /// <summary>
    /// Kết quả so sánh cấu trúc giữa nguồn và đích: đếm số đối tượng theo loại
    /// (bảng, view, function, stored procedure, trigger) trên mỗi phía và liệt kê
    /// các đối tượng có mặt ở nguồn nhưng thiếu trên đích.
    /// </summary>
    public sealed class StructureCheckResult
    {
        /// <summary>Số đối tượng theo loại trên NGUỒN.</summary>
        public IReadOnlyDictionary<string, int> SourceCounts { get; init; } = new Dictionary<string, int>();

        /// <summary>Số đối tượng theo loại trên ĐÍCH.</summary>
        public IReadOnlyDictionary<string, int> DestinationCounts { get; init; } = new Dictionary<string, int>();

        /// <summary>Danh sách đối tượng (schema.tên) có ở nguồn nhưng thiếu trên đích.</summary>
        public IReadOnlyList<string> MissingOnDestination { get; init; } = Array.Empty<string>();

        /// <summary>Đúng khi không có đối tượng nào bị thiếu trên đích.</summary>
        public bool Success => MissingOnDestination.Count == 0;
    }
}