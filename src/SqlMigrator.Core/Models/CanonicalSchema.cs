using System.Collections.Generic;

namespace SqlMigrator.Core.Models
{
    /// <summary>Hệ kiểu chuẩn nội bộ để di chuyển chéo engine (SQL Server/PostgreSQL/...).</summary>
    public enum CanonicalType
    {
        Unknown,
        Int16,
        Int32,
        Int64,
        Decimal,
        Float,
        Double,
        String,
        Bool,
        Date,
        Time,
        DateTime,
        DateTimeTz,
        Binary,
        Guid,
        Json,
        Money
    }

    /// <summary>Một cột trong mô hình chuẩn (đủ để sinh DDL mọi engine).</summary>
    public sealed class CanonicalColumn
    {
        public string Name { get; init; } = string.Empty;
        public CanonicalType Type { get; init; } = CanonicalType.Unknown;
        /// <summary>Độ dài KÝ TỰ (-1 = max, 0 = không áp dụng).</summary>
        public int MaxLength { get; init; }
        public byte Precision { get; init; }
        public byte Scale { get; init; }
        /// <summary>Chuỗi có phân biệt unicode không (NVARCHAR/TEXT UTF-8...).</summary>
        public bool IsUnicode { get; init; } = true;
        public bool IsNullable { get; init; } = true;
        public bool IsPrimaryKey { get; init; }
        public bool IsIdentity { get; init; }
        public bool IsComputed { get; init; }
        /// <summary>Mặc định gốc (để dịch sang engine đích, null = không có).</summary>
        public string? DefaultSql { get; init; }
    }

    /// <summary>Khóa ngoại trong mô hình chuẩn.</summary>
    public sealed class CanonicalForeignKey
    {
        public string? Name { get; init; }
        public IReadOnlyList<string> Columns { get; init; } = new List<string>();
        public string? RefSchema { get; init; }
        public string RefTable { get; init; } = string.Empty;
        public IReadOnlyList<string> RefColumns { get; init; } = new List<string>();
    }

    /// <summary>Một bảng trong mô hình chuẩn (cột theo đúng thứ tự).</summary>
    public sealed class CanonicalTable
    {
        /// <summary>Schema (null với engine không có schema như SQLite).</summary>
        public string? Schema { get; init; }
        public string Name { get; init; } = string.Empty;
        public IReadOnlyList<CanonicalColumn> Columns { get; init; } = new List<CanonicalColumn>();
        public IReadOnlyList<string> PrimaryKeyColumns { get; init; } = new List<string>();
        public IReadOnlyList<CanonicalForeignKey> ForeignKeys { get; init; } = new List<CanonicalForeignKey>();

        public string DisplayName =>
            string.IsNullOrWhiteSpace(Schema) ? Name : Schema + "." + Name;
    }

    /// <summary>Schema chuẩn của một database (đủ để sinh DDL + chép dữ liệu chéo engine).</summary>
    public sealed class CanonicalSchema
    {
        public IReadOnlyList<CanonicalTable> Tables { get; init; } = new List<CanonicalTable>();
    }
}
