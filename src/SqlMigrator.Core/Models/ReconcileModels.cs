using System;
using System.Collections.Generic;
using System.Linq;

namespace SqlMigrator.Core.Models
{
    /// <summary>Loại vấn đề khi quét so sánh nguồn vs đích.</summary>
    public enum ReconcileIssueType
    {
        /// <summary>Đối tượng (bảng/view/SP/FN/trigger/sequence/FK) có trên nguồn nhưng thiếu trên đích.</summary>
        MissingObject,
        /// <summary>Cột có trên bảng nguồn nhưng thiếu trên bảng đích đã tồn tại.</summary>
        MissingColumn,
        /// <summary>Dữ liệu lệch giữa nguồn và đích (chỉ thông tin, không tự sửa).</summary>
        DataDifference,
        /// <summary>View/SP/FN trên đích tham chiếu đối tượng không tồn tại trên đích.</summary>
        BrokenDependency
    }

    /// <summary>Mức độ nghiêm trọng của vấn đề.</summary>
    public enum ReconcileIssueSeverity
    {
        /// <summary>Phải xử lý — đối tượng không tạo được, dữ liệu không toàn vẹn.</summary>
        Error,
        /// <summary>Nên xử lý — thiếu cột, FK, nhưng chưa ảnh hưởng nghiêm trọng.</summary>
        Warning,
        /// <summary>Thông tin — chỉ để tham khảo.</summary>
        Info
    }

    /// <summary>Trạng thái xử lý của một vấn đề.</summary>
    public enum ReconcileIssueStatus
    {
        /// <summary>Chưa xử lý.</summary>
        Pending,
        /// <summary>Đã chọn để xử lý.</summary>
        Selected,
        /// <summary>Đang xử lý.</summary>
        Processing,
        /// <summary>Đã xử lý thành công.</summary>
        Fixed,
        /// <summary>Xử lý thất bại.</summary>
        Failed,
        /// <summary>Bỏ qua (user chọn không xử lý).</summary>
        Skipped
    }

    /// <summary>
    /// Một vấn đề cụ thể khi so sánh nguồn vs đích.
    /// Mỗi issue đại diện cho một đối tượng/cột/dữ liệu lệch.
    /// </summary>
    public sealed class ReconcileIssue
    {
        /// <summary>Loại vấn đề.</summary>
        public ReconcileIssueType Type { get; set; }

        /// <summary>Tên đối tượng (VD: "dbo.TableName", "dbo.ViewName").</summary>
        public string ObjectName { get; set; } = string.Empty;

        /// <summary>Loại đối tượng SQL (TABLE, VIEW, STORED_PROCEDURE, FUNCTION, TRIGGER, SEQUENCE, FK, COLUMN).</summary>
        public string ObjectType { get; set; } = string.Empty;

        /// <summary>Mô tả chi tiết vấn đề (hiển thị cho user).</summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>Gợi ý cách xử lý.</summary>
        public string SuggestedAction { get; set; } = string.Empty;

        /// <summary>Mức độ nghiêm trọng.</summary>
        public ReconcileIssueSeverity Severity { get; set; }

        /// <summary>Trạng thái xử lý hiện tại.</summary>
        public ReconcileIssueStatus Status { get; set; } = ReconcileIssueStatus.Pending;

        /// <summary>
        /// Script CREATE gốc đọc từ database nguồn.
        /// Null nếu đối tượng không có script (VD: bảng thiếu do migration chưa chạy).
        /// </summary>
        public string? SourceScript { get; set; }

        /// <summary>
        /// Script đã được rewrite cho phiên bản đích.
        /// Null nếu không cần rewrite hoặc chưa rewrite.
        /// </summary>
        public string? RewrittenScript { get; set; }

        /// <summary>
        /// Lỗi SQL Server trả về khi thử CREATE trên đích.
        /// Null nếu chưa thử hoặc tạo thành công.
        /// </summary>
        /// <summary>Lỗi SQL Server trả về khi thử CREATE trên đích.</summary>
        public string? ErrorMessage { get; set; }

        /// <summary>
        /// Danh sách rule IDs đã áp dụng khi rewrite.
        /// </summary>
        public IReadOnlyList<string> AppliedRules { get; set; } = Array.Empty<string>();

        /// <summary>
        /// Danh sách rule IDs KHÔNG THỂ áp dụng (feature không hỗ trợ trên đích).
        /// </summary>
        public IReadOnlyList<string> UnsupportedRules { get; set; } = Array.Empty<string>();

        /// <summary>Danh sách tên đối tượng mà đối tượng này phụ thuộc vào (nếu có).</summary>
        public IReadOnlyList<string> Dependencies { get; set; } = Array.Empty<string>();

        /// <summary>Số dòng lệch (chỉ cho DataDifference).</summary>
        public long? SourceRowCount { get; set; }
        public long? DestRowCount { get; set; }

        /// <summary>
        /// Cảnh báo kèm theo sau rewrite tự động (VD: dữ liệu hết che sau khi lột masking).
        /// User không cần làm gì thêm nhưng bắt buộc phải đọc.
        /// </summary>
        public IReadOnlyList<string> Warnings { get; set; } = Array.Empty<string>();

        /// <summary>
        /// Id các shim Compat đã/cần triển khai lên đích cho issue này (VD: "Json").
        /// </summary>
        public IReadOnlyList<string> RequiredShims { get; set; } = Array.Empty<string>();

        /// <summary>Có thể tự xử lý an toàn không.</summary>
        public bool CanAutoFix { get; set; }

        /// <summary>Đã thử CREATE trên đích chưa.</summary>
        public bool HasTriedCreate { get; set; }

        /// <summary>Tạo thành công trên đích chưa.</summary>
        public bool CreateSucceeded { get; set; }

        /// <summary>Ngày tạo object trên nguồn (sys.objects, chỉ SELECT).</summary>
        public DateTime? CreatedDate { get; set; }

        /// <summary>Ngày sửa gần nhất trên nguồn (sys.objects, chỉ SELECT).</summary>
        public DateTime? ModifiedDate { get; set; }

        /// <summary>
        /// Tổng số lần chạy ghi nhận được (Query Store, gộp plan cache).
        /// Null = không có dữ liệu (server tắt QS + không đọc được cache).
        /// </summary>
        public long? UseCount { get; set; }

        /// <summary>Lần chạy gần nhất ghi nhận được. Null = chưa từng thấy dùng.</summary>
        public DateTime? LastUsedDate { get; set; }

        /// <summary>
        /// Nguồn chứng cứ độ dùng: "Query Store", "plan cache", hoặc null
        /// (chưa tra được). Dùng để diễn giải LastUsedDate trung thực.
        /// </summary>
        public string? UsageEvidence { get; set; }
    }

    /// <summary>
    /// Kết quả quét so sánh nguồn vs đích (chỉ ĐỌC, KHÔNG ghi).
    /// Chứa danh sách vấn đề + thống kê tóm tắt.
    /// </summary>
    public sealed class ReconcileResult
    {
        public bool Success { get; set; } = true;
        public TimeSpan Elapsed { get; set; }

        /// <summary>Danh sách vấn đề tìm được.</summary>
        public IReadOnlyList<ReconcileIssue> Issues { get; set; } = Array.Empty<ReconcileIssue>();

        /// <summary>Lỗi quét (nếu có).</summary>
        public IReadOnlyList<string> Errors { get; set; } = Array.Empty<string>();

        /// <summary>Cảnh báo trong quá trình quét.</summary>
        public IReadOnlyList<string> Warnings { get; set; } = Array.Empty<string>();

        public void AddError(string message)
        {
            var list = new List<string>(Errors) { message };
            Errors = list;
            Success = false;
        }

        public void AddWarning(string message)
        {
            var list = new List<string>(Warnings) { message };
            Warnings = list;
        }

        // ---- Thống kê tóm tắt ----
        public int MissingTables => CountBy(ReconcileIssueType.MissingObject, "TABLE");
        public int MissingViews => CountBy(ReconcileIssueType.MissingObject, "VIEW");
        public int MissingProcedures => CountBy(ReconcileIssueType.MissingObject, "STORED_PROCEDURE");
        public int MissingFunctions => CountBy(ReconcileIssueType.MissingObject, "FUNCTION");
        public int MissingTriggers => CountBy(ReconcileIssueType.MissingObject, "TRIGGER");
        public int MissingSequences => CountBy(ReconcileIssueType.MissingObject, "SEQUENCE");
        public int MissingColumns => Issues.Count(i => i.Type == ReconcileIssueType.MissingColumn);
        public int MissingForeignKeys => CountBy(ReconcileIssueType.MissingObject, "FK");
        public int DataDifferences => Issues.Count(i => i.Type == ReconcileIssueType.DataDifference);
        public int BrokenDependencies => Issues.Count(i => i.Type == ReconcileIssueType.BrokenDependency);
        public int TotalIssues => Issues.Count;

        private int CountBy(ReconcileIssueType type, string objectType) =>
            Issues.Count(i => i.Type == type && i.ObjectType == objectType);
    }

    /// <summary>Kết quả sau khi xử lý (fix) các vấn đề được chọn.</summary>
    public sealed class ReconcileFixResult
    {
        public bool Success { get; set; } = true;
        public TimeSpan Elapsed { get; set; }

        /// <summary>Các vấn đề đã fix thành công.</summary>
        public IReadOnlyList<ReconcileIssue> FixedIssues { get; set; } = Array.Empty<ReconcileIssue>();

        /// <summary>Các vấn đề fix thất bại.</summary>
        public IReadOnlyList<ReconcileIssue> FailedIssues { get; set; } = Array.Empty<ReconcileIssue>();

        /// <summary>Các vấn đề bỏ qua (chưa có script hoặc user bỏ qua).</summary>
        public IReadOnlyList<ReconcileIssue> SkippedIssues { get; set; } = Array.Empty<ReconcileIssue>();

        /// <summary>Lỗi chung.</summary>
        public IReadOnlyList<string> Errors { get; set; } = Array.Empty<string>();

        public void AddError(string message)
        {
            var list = new List<string>(Errors) { message };
            Errors = list;
        }
    }

    /// <summary>
    /// Một dòng xem trước sync dữ liệu: app SẼ thêm/sửa bao nhiêu dòng nếu user OK.
    /// Chỉ đếm, không ghi database nào.
    /// </summary>
    public sealed class DataSyncPreviewItem
    {
        public string Table { get; set; } = string.Empty;
        public long SourceRows { get; set; }
        public long DestRows { get; set; }
        public long WillInsert { get; set; }
        public long WillUpdate { get; set; }
        /// <summary>True khi bảng này không sync được (không khóa ổn định...).</summary>
        public bool Skipped { get; set; }
        public string? SkipReason { get; set; }
    }

    /// <summary>Kết quả sync dữ liệu một bảng (chỉ thêm + sửa, không bao giờ xóa).</summary>
    public sealed class DataSyncTableResult
    {
        public string Table { get; set; } = string.Empty;
        public long Inserted { get; set; }
        public long Updated { get; set; }
        public bool Skipped { get; set; }
        public string? SkipReason { get; set; }
        public IReadOnlyList<string> Errors { get; set; } = Array.Empty<string>();
    }

    /// <summary>Kết quả một đợt sync dữ liệu các bảng đã chọn.</summary>
    public sealed class DataSyncResult
    {
        public bool Success { get; set; } = true;
        public TimeSpan Elapsed { get; set; }
        public IReadOnlyList<DataSyncTableResult> Tables { get; set; } = Array.Empty<DataSyncTableResult>();

        public long TotalInserted
        {
            get
            {
                long sum = 0;
                foreach (var t in Tables)
                    sum += t.Inserted;
                return sum;
            }
        }

        public long TotalUpdated
        {
            get
            {
                long sum = 0;
                foreach (var t in Tables)
                    sum += t.Updated;
                return sum;
            }
        }
    }
}
