using System;
using System.Collections.Generic;

namespace SqlMigrator.Core.Models
{
    /// <summary>Một đối tượng trong database (bảng/view/SP/...).</summary>
    public sealed class InventoryObject
    {
        /// <summary>Loại sys.objects: U (bảng), V (view), P (proc), FN/IF/TF (function), TR (trigger).</summary>
        public string Type { get; init; } = string.Empty;
        public string Schema { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string PlainName => string.IsNullOrEmpty(Schema) ? Name : Schema + "." + Name;
    }

    /// <summary>
    /// Hồ sơ kiểm kê một database (đếm + liệt kê đối tượng, ước lượng dòng,
    /// cờ tính năng 2016+). Đọc bằng SELECT catalog nên an toàn, rẻ (mili-giây).
    /// </summary>
    public sealed class DatabaseInventory
    {
        public string DatabaseName { get; init; } = string.Empty;
        public int ServerMajorVersion { get; init; }
        public bool Ok { get; init; }
        public string? Error { get; init; }

        public IReadOnlyList<InventoryObject> Tables { get; init; } = Array.Empty<InventoryObject>();
        public IReadOnlyList<InventoryObject> Views { get; init; } = Array.Empty<InventoryObject>();
        public IReadOnlyList<InventoryObject> Procedures { get; init; } = Array.Empty<InventoryObject>();
        public IReadOnlyList<InventoryObject> Functions { get; init; } = Array.Empty<InventoryObject>();
        public IReadOnlyList<InventoryObject> Triggers { get; init; } = Array.Empty<InventoryObject>();
        public IReadOnlyList<string> Sequences { get; init; } = Array.Empty<string>();

        /// <summary>Tổng số cột của bảng người dùng.</summary>
        public long TotalColumns { get; init; }
        /// <summary>Ước lượng tổng số dòng (sys.partitions, rẻ hơn COUNT).</summary>
        public long EstimatedRows { get; init; }

        // Cờ tính năng SQL 2016+ (có ý nghĩa khi hạ cấp); -1 = không đọc được.
        public int TemporalTables { get; init; }
        public int MaskedColumns { get; init; }
        public int RlsPolicies { get; init; }
        public int EncryptedColumns { get; init; }
        public int ServerTriggers { get; init; } = -1;
    }
}
