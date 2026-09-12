namespace SqlMigrator.Core.Models
{
    /// <summary>What part of the source database should be migrated.</summary>
    public enum MigrationMode
    {
        /// <summary>Copy both schema and data.</summary>
        Full,

        /// <summary>Copy schema objects only (no data).</summary>
        SchemaOnly,

        /// <summary>Copy data only (destination schema must already exist).</summary>
        DataOnly
    }

    /// <summary>
    /// Phân loại quá trình di chuyển dựa trên phiên bản server nguồn/đích:
    /// nâng cấp, hạ cấp hay di chuyển ngang cùng phiên bản.
    /// </summary>
    public enum MigrationKind
    {
        /// <summary>Chưa xác định được (không đọc nổi phiên bản).</summary>
        Unknown,

        /// <summary>Nguồn và đích cùng phiên bản → di chuyển ngang.</summary>
        SameVersion,

        /// <summary>Đích mới hơn nguồn → NÂNG CẤP (upgrade).</summary>
        Upgrade,

        /// <summary>Đích cũ hơn nguồn → HẠ CẤP (downgrade), nhiều cấu trúc có thể bị bỏ qua.</summary>
        Downgrade
    }

    /// <summary>Kinds of database objects the migrator knows how to handle.</summary>
    public enum DatabaseObjectType
    {
        Undefined,
        Schema,
        Sequence,
        UserDefinedDataType,
        UserDefinedTableType,
        UserDefinedClrType,
        Table,
        View,
        StoredProcedure,
        Function,
        Trigger,
        ServerTrigger,
        ForeignKey,
        /// <summary>Nhóm file (FG) — tạo bởi bước provision, không chạy như script thường.</summary>
        Filegroup,
        /// <summary>Hàm partition (CREATE PARTITION FUNCTION) — trước scheme và bảng.</summary>
        PartitionFunction,
        /// <summary>Scheme partition (CREATE PARTITION SCHEME) — sau function, trước bảng.</summary>
        PartitionScheme,
        /// <summary>Từ đồng nghĩa (CREATE SYNONYM) — sau bảng, trước view/module.</summary>
        Synonym,
        /// <summary>Cột computed hoãn tạo (ALTER TABLE ADD) — sau module (cần function).</summary>
        ComputedColumn,
        Index,
        DatabasePermission,
        DatabaseOption
    }

    /// <summary>
    /// Hệ quản trị CSDL mà app nhận diện/hỗ trợ. MySql bao gồm MariaDB
    /// (phân biệt qua chuỗi version, trường <c>EngineInfo.Variant</c>).
    /// </summary>
    public enum DatabaseEngine
    {
        /// <summary>Chưa xác định.</summary>
        Unknown,
        /// <summary>Microsoft SQL Server (đã hỗ trợ đầy đủ).</summary>
        SqlServer,
        /// <summary>PostgreSQL.</summary>
        PostgreSql,
        /// <summary>MySQL / MariaDB (chung protocol).</summary>
        MySql,
        /// <summary>SQLite (file).</summary>
        Sqlite,
        /// <summary>MongoDB (document).</summary>
        MongoDb
    }
}