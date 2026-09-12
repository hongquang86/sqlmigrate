using System;

namespace SqlMigrator.Core.Models
{
    /// <summary>
    /// Một đối tượng user chấp nhận bỏ qua vĩnh viễn (không di chuyển, không báo thiếu).
    /// Lưu theo cặp nguồn→đích nên dest khác nhau độc lập nhau.
    /// </summary>
    public sealed class IgnoredObject
    {
        /// <summary>Loại đối tượng theo quy ước reconcile (TABLE, VIEW, ...).</summary>
        public string ObjectType { get; set; } = string.Empty;
        public string Schema { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        /// <summary>Lý do bỏ qua (ghi chú của app lúc thêm).</summary>
        public string Reason { get; set; } = string.Empty;
        public DateTime IgnoredAtUtc { get; set; }

        public string DisplayName =>
            string.IsNullOrEmpty(Schema) ? Name : Schema + "." + Name;
    }
}
