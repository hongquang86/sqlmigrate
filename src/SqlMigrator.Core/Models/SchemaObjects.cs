using System;
using System.Collections.Generic;

namespace SqlMigrator.Core.Models
{
    /// <summary>
    /// One executable unit of SQL captured from the source. The <see cref="Definition"/>
    /// is a single batch (never contains a GO separator) that can be executed on the destination.
    /// </summary>
    public sealed class DatabaseObject
    {
        public DatabaseObjectType Type { get; init; } = DatabaseObjectType.Undefined;
        public string Schema { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string Definition { get; init; } = string.Empty;

        /// <summary>True when the object was intentionally skipped (e.g. unsupported feature).</summary>
        public bool IsSkipped { get; init; }

        /// <summary>Populated when <see cref="IsSkipped"/> is true.</summary>
        public string? SkipReason { get; init; }

        /// <summary>Human readable, e.g. "dbo.MyProc".</summary>
        public string DisplayName =>
            string.IsNullOrEmpty(Schema) ? Name : Schema + "." + Name;

        /// <summary>Fully qualified arbitrary identifier (not schema-constrained), e.g. for primary keys.</summary>
        public string QualifiedName => SqlMigrator.Core.Services.Quoting.QuoteQualifiedName(Schema, Name);
    }

    /// <summary>Description of a single table column (materializes only what the data copier cares about).</summary>
    public sealed class ColumnSchema
    {
        public string Name { get; init; } = string.Empty;
        public string DataTypeName { get; init; } = string.Empty;
        public bool IsComputed { get; init; }
        public bool IsIdentity { get; init; }
        public bool IsRowVersion { get; init; }
        public bool IsNullable { get; init; }
        public bool IsPrimaryKey { get; init; }
        /// <summary>max_length từ sys.columns (-1 = max). Dùng dựng kiểu đầy đủ khi migrate chéo.</summary>
        public short MaxLength { get; init; }
        /// <summary>precision từ sys.columns (decimal/numeric/datetime...).</summary>
        public byte Precision { get; init; }
        /// <summary>scale từ sys.columns.</summary>
        public byte Scale { get; init; }
        public string QuotedName => SqlMigrator.Core.Services.Quoting.QuoteIdentifier(Name);
    }

    /// <summary>Everything the migrator needs to copy (or disable constraints on) one table.</summary>
    public sealed class TableSchema
    {
        public string Schema { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public bool HasIdentity { get; set; }
        public bool IsMemoryOptimized { get; init; }
        public bool IsTemporal { get; init; }
        public bool IsFileTable { get; init; }
        public bool IsExternal { get; init; }
        public bool IsReplicated { get; init; }

        private string? _skipReason;

        /// <summary>True when the table was flagged to be skipped (e.g. unsupported feature).</summary>
        public bool IsSkipped => _skipReason != null;

        /// <summary>Why the table was skipped, when applicable.</summary>
        public string? SkipReason => _skipReason;

        /// <summary>Marks the table skipped with a reason (called by the compatibility checker).</summary>
        public void MarkSkipped(string reason) => _skipReason = reason;

        private readonly List<ColumnSchema> _columns = new List<ColumnSchema>();
        private readonly List<ForeignKeySchema> _foreignKeys = new List<ForeignKeySchema>();

        /// <summary>Columns in ordinal order (set by the schema extractor).</summary>
        public IReadOnlyList<ColumnSchema> Columns => _columns;

        /// <summary>Foreign keys owned by this table.</summary>
        public IReadOnlyList<ForeignKeySchema> ForeignKeys => _foreignKeys;

        internal void AddColumn(ColumnSchema column) => _columns.Add(column);

        internal void AddForeignKey(ForeignKeySchema foreignKey) => _foreignKeys.Add(foreignKey);

        private string? _historySchema;
        private string? _historyTable;
        private string? _periodStartColumn;
        private string? _periodEndColumn;

        /// <summary>Schema của bảng lịch sử (chỉ bảng temporal từ SQL 2016+).</summary>
        public string? HistorySchema => _historySchema;

        /// <summary>Tên bảng lịch sử (chỉ bảng temporal từ SQL 2016+).</summary>
        public string? HistoryTable => _historyTable;

        /// <summary>Cột bắt đầu kỳ hiệu lực (PERIOD FOR SYSTEM_TIME).</summary>
        public string? PeriodStartColumn => _periodStartColumn;

        /// <summary>Cột kết thúc kỳ hiệu lực (PERIOD FOR SYSTEM_TIME).</summary>
        public string? PeriodEndColumn => _periodEndColumn;

        /// <summary>Bảng này có phải temporal cần chuyển đổi khi hạ cấp không.</summary>
        public bool NeedsTemporalConversion =>
            IsTemporal && !string.IsNullOrWhiteSpace(_periodStartColumn)
                       && !string.IsNullOrWhiteSpace(_periodEndColumn);

        /// <summary>Tên đầy đủ bảng lịch sử, VD: dbo.MyTable_History.</summary>
        public string HistoryPlainName => (_historySchema ?? Schema) + "." + (_historyTable ?? (Name + "_History"));

        /// <summary>Ghi nhận thông tin temporal đọc từ nguồn (tên bảng lịch sử + 2 cột kỳ).</summary>
        internal void SetTemporalInfo(string? historySchema, string? historyTable,
            string? periodStart, string? periodEnd)
        {
            _historySchema = historySchema;
            _historyTable = historyTable;
            _periodStartColumn = periodStart;
            _periodEndColumn = periodEnd;
        }

        /// <summary>Display name, e.g. [dbo].[Orders].</summary>
        public string QualifiedName => SqlMigrator.Core.Services.Quoting.QuoteQualifiedName(Schema, Name);

        /// <summary>Display name without quoting, e.g. dbo.Orders.</summary>
        public string PlainName => Schema + "." + Name;

        /// <summary>
        /// The ordered set of columns that must be physically inserted when copying data.
        /// Computed columns are always excluded; identity columns are excluded unless
        /// <paramref name="preserveIdentity"/> is true. The returned order matches the
        /// source projection order used by <c>SqlBulkCopy</c>.
        /// </summary>
        public IReadOnlyList<ColumnSchema> GetDataColumns(bool preserveIdentity)
        {
            var result = new List<ColumnSchema>(Columns.Count);
            foreach (var column in Columns)
            {
                // Cột computed và cột rowversion/timestamp không được chèn giá trị tường minh.
                if (column.IsComputed || column.IsRowVersion)
                    continue;
                if (column.IsIdentity && !preserveIdentity)
                    continue;
                result.Add(column);
            }

            return result;
        }

        public override string ToString() => PlainName;
    }

    /// <summary>Metadata for a foreign key constraint (used for disable/enable and creation).</summary>
    public sealed class ForeignKeySchema
    {
        public string Name { get; init; } = string.Empty;
        public string TableSchema { get; init; } = string.Empty;
        public string TableName { get; init; } = string.Empty;
        public string ReferencedTableSchema { get; init; } = string.Empty;
        public string ReferencedTableName { get; init; } = string.Empty;
        public bool IsDisabled { get; init; }
        public bool IsNotTrusted { get; init; }
        public bool IsSystemNamed { get; init; }

        public string OwnerQualifiedName => SqlMigrator.Core.Services.Quoting.QuoteQualifiedName(TableSchema, TableName);
        public string ReferencedQualifiedName => SqlMigrator.Core.Services.Quoting.QuoteQualifiedName(ReferencedTableSchema, ReferencedTableName);
        public string QualifiedName => SqlMigrator.Core.Services.Quoting.QuoteQualifiedName(TableSchema, Name);
    }

    /// <summary>A warning recorded during extraction/validation (never fatal by itself).</summary>
    public sealed class CompatibilityIssue
    {
        public string ObjectName { get; init; } = string.Empty;
        public string Severity { get; init; } = "Warning"; // "Warning" | "Error"
        public string Message { get; init; } = string.Empty;
        public string Feature { get; init; } = string.Empty;

        public override string ToString() => $"[{Severity}] {ObjectName}: {Message}";
    }
}