using System;
using System.Collections.Generic;

namespace SqlMigrator.Core.Models
{
    /// <summary>Cách đọc một bảng nguồn trong quá trình di chuyển.</summary>
    public enum TransferStrategy
    {
        /// <summary>Đọc theo chunk keyset (WHERE key &gt; @mốc) — seek theo index, hồi tục, không giữ khóa dài.</summary>
        KeysetChunked,

        /// <summary>Scan toàn bộ 1 luồng. Dùng khi bảng không có khóa sử dụng được để chunk.</summary>
        FullScan
    }

    /// <summary>Cột được dùng làm con trỏ keyset.</summary>
    public enum TransferKeyKind
    {
        None,
        Numeric,
        RowVersion,
        String,
        DateTime,
        Guid
    }

    /// <summary>
    /// Kế hoạch chuyển một bảng: cột nào được chép, cột khóa nào dùng để chunk,
    /// ước lượng số dòng/bề rộng (để cấp phát bộ nhớ và tính ETA).
    /// </summary>
    public sealed class TransferTablePlan
    {
        public string Schema { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public TransferStrategy Strategy { get; init; } = TransferStrategy.FullScan;
        public TransferKeyKind KeyKind { get; init; } = TransferKeyKind.None;

        /// <summary>Tên cột khóa (tên thật).</summary>
        public string? KeyColumn { get; init; }

        /// <summary>Các cột dữ liệu cần chép (tên thật, đã loại computed/rowversion).</summary>
        public IReadOnlyList<string> Columns { get; init; } = Array.Empty<string>();

        /// <summary>Ước lượng số dòng (đã loại trừ bảng được chọn nhưng bỏ qua).</summary>
        public long EstimatedRows { get; set; }

        /// <summary>Ước lượng bề rộng trung bình một dòng (byte) để tính ngân sách bộ nhớ.</summary>
        public int EstimatedRowWidthBytes { get; set; } = 128;

        public string PlainName => Schema + "." + Name;
        public string QualifiedName => SqlMigrator.Core.Services.Quoting.QuoteQualifiedName(Schema, Name);
    }

    /// <summary>
    /// Một chunk keyset: khóa bắt đầu (exclusive) và khóa kết thúc (inclusive) giúp câu
    /// truy vấn dữ liệu nằm gọn trong dải [lastKey, endKey]. Nếu endKey == null nghĩa là
    /// chunk cuối (đến hết bảng).
    /// </summary>
    public sealed class TransferChunk
    {
        public string TablePlainName { get; init; } = string.Empty;
        public string? StartKey { get; init; }
        public string? EndKey { get; init; }
        public bool IsLast { get; set; }
    }

    /// <summary>Mốc hồi tục của một bảng trong file checkpoint.</summary>
    public sealed class TransferCheckpointEntry
    {
        public string Schema { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public TransferStrategy Strategy { get; init; } = TransferStrategy.FullScan;
        public TransferKeyKind KeyKind { get; init; } = TransferKeyKind.None;

        /// <summary>Giá trị con trỏ keyset hiện tại (dạng chuỗi; null = chưa bắt đầu / full-scan).</summary>
        public string? LastKey { get; set; }

        public int ChunksCompleted { get; set; }
        public long RowsCopied { get; set; }
        public bool Completed { get; set; }
        public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    }

    /// <summary>File checkpoint cho một cặp nguồn–đích.</summary>
    public sealed class TransferCheckpointFile
    {
        /// <summary>Khóa hash của cặp nguồn–đích (SHA-256), khớp với tên file.</summary>
        public string Key { get; init; } = string.Empty;
        public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

        private readonly List<TransferCheckpointEntry> _tables = new();
        public IReadOnlyList<TransferCheckpointEntry> Tables => _tables;

        public void Upsert(TransferCheckpointEntry entry)
        {
            _tables.RemoveAll(t => string.Equals(t.Schema + "." + t.Name, entry.Schema + "." + entry.Name,
                StringComparison.OrdinalIgnoreCase));
            _tables.Add(entry);
        }

        public TransferCheckpointEntry? Find(string schema, string name)
            => _tables.Find(t => string.Equals(t.Schema + "." + t.Name, schema + "." + name,
                StringComparison.OrdinalIgnoreCase));

        /// <summary>Xóa mốc một bảng (dùng khi bảng bị dọn dữ liệu do lỗi và phải chép lại từ đầu).</summary>
        public bool Remove(string schema, string name)
        {
            var removed = _tables.RemoveAll(t => string.Equals(t.Schema + "." + t.Name, schema + "." + name,
                StringComparison.OrdinalIgnoreCase));
            return removed > 0;
        }
    }

    /// <summary>Kết quả chuyển một bảng.</summary>
    public sealed class TransferTableResult
    {
        public string PlainName { get; init; } = string.Empty;
        public long RowsCopied { get; set; }
        public long BytesCopied { get; set; }
        public bool Success { get; set; } = true;
        public string? Error { get; set; }
        public bool Skipped { get; set; }
    }

    /// <summary>Thống kê tốc độ tức thời để hiển thị VIP (MB/s + ETA).</summary>
    public sealed class TransferRateSample
    {
        public int Percent { get; init; }
        public long RowsDone { get; init; }
        public long EstimatedRowsTotal { get; init; }
        public double MbPerSecond { get; init; }
        public double SecondsRemaining { get; init; }
        public string Message { get; init; } = string.Empty;
    }
}