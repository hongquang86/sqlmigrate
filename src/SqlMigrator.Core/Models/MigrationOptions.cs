using System;

namespace SqlMigrator.Core.Models
{
    /// <summary>
    /// Fully describes how a migration run should behave. All values have safe defaults
    /// so the type can be constructed directly in unit tests or via the CLI.
    /// </summary>
    public sealed class MigrationOptions
    {
        /// <summary>ADO.NET connection string to the source SQL Server.</summary>
        public string SourceConnectionString { get; set; } = string.Empty;

        /// <summary>ADO.NET connection string to the destination SQL Server.</summary>
        public string DestinationConnectionString { get; set; } = string.Empty;

        /// <summary>
        /// Hệ CSDL nguồn ("SqlServer", "PostgreSql", ...). Rỗng = SQL Server
        /// (tương thích cấu hình cũ). Pha 1 chỉ chạy SQL Server ↔ SQL Server.
        /// </summary>
        public string SourceEngine { get; set; } = string.Empty;

        /// <summary>Hệ CSDL đích (như trên).</summary>
        public string DestinationEngine { get; set; } = string.Empty;

        /// <summary>The migration mode (full / schema_only / data_only).</summary>
        public MigrationMode Mode { get; set; } = MigrationMode.Full;

        /// <summary>Drop and recreate the destination database before beginning. Default false.</summary>
        public bool CreateDestinationDatabase { get; set; }

        /// <summary>
        /// Thư mục chứa file dữ liệu (.mdf) của database đích khi tạo mới. Để trống = dùng
        /// thư mục mặc định của server đích.
        /// </summary>
        public string? DestinationDataFileDirectory { get; set; }

        /// <summary>
        /// Thư mục chứa file log (.ldf) của database đích khi tạo mới. Để trống = dùng
        /// thư mục mặc định của server đích (hoặc cùng thư mục với .mdf nếu ô này trống).
        /// </summary>
        public string? DestinationLogFileDirectory { get; set; }

        /// <summary>Disable all foreign keys on the destination before loading data. Default true.</summary>
        public bool DisableForeignKeyConstraints { get; set; } = true;

        /// <summary>Disable all CHECK constraints on the destination before loading data. Default true.</summary>
        public bool DisableCheckConstraints { get; set; } = true;

        /// <summary>Disable triggers on destination tables during data load. Default false.</summary>
        public bool DisableTriggers { get; set; }

        /// <summary>Disable (non-clustered) secondary indexes on destination tables during load. Default false.</summary>
        public bool DisableIndexesDuringLoad { get; set; }

        /// <summary>Preserve the exact identity values from the source. Default false (destination renumbers).</summary>
        public bool PreserveIdentity { get; set; }

        /// <summary>Rows per bulk copy batch. Default 5000.</summary>
        public int BatchSize { get; set; } = 5000;

        /// <summary>Where to write the detailed log file. May be null (console only).</summary>
        public string? LogFilePath { get; set; }

        /// <summary>
        /// Copy server-level triggers (ON ALL SERVER, e.g. login audits) from the source
        /// server. Requires CONTROL SERVER on the source to read and on the destination to
        /// create; failures are recorded as warnings. Default true.
        /// </summary>
        public bool CopyServerTriggers { get; set; } = true;

        /// <summary>
        /// When a table's bulk copy fails partway and the destination table was empty before
        /// the run, delete the partially loaded rows so the destination does not keep a mix
        /// of old/new data. Default true.
        /// </summary>
        public bool CleanupPartialTableOnError { get; set; } = true;

        /// <summary>
        /// When re-enabling constraints after load, use WITH CHECK (validate existing rows).
        /// When false, uses WITH NOCHECK (trusts loaded data). Default true.
        /// </summary>
        public bool CheckDataAfterLoad { get; set; } = true;

        /// <summary>Keep going past non-critical failures (skip object, warn). Default true.</summary>
        public bool ContinueOnNonCriticalErrors { get; set; } = true;

        /// <summary>
        /// Khi hạ cấp (nguồn mới hơn đích) hoặc đích thiếu feature: vẫn cố TẠO bảng/đối tượng
        /// dùng feature không hỗ trợ thay vì bỏ qua cứng từ đầu. Nếu đích từ chối thì ghi cảnh
        /// báo và bỏ qua đối tượng đó khi <see cref="ContinueOnNonCriticalErrors"/> bật.
        /// </summary>
        public bool ForceUnsupportedFeatures { get; set; }

        /// <summary>Copy database and object permissions. Default true.</summary>
        public bool CopyPermissions { get; set; } = true;

        /// <summary>Wrap each batch in an explicit transaction (SqlBulkCopy.UseInternalTransaction). Default false.</summary>
        public bool UseInternalTransaction { get; set; }

        /// <summary>Enable streaming for text/ntext/image/lob columns. Default true.</summary>
        public bool EnableStreaming { get; set; } = true;

        /// <summary>Bulk copy operation timeout in seconds (0 = no timeout). Default 0.</summary>
        public int BulkCopyTimeoutSeconds { get; set; }

        /// <summary>General command timeout in seconds. Default 600.</summary>
        public int CommandTimeoutSeconds { get; set; } = 600;

        // ---- Transfer engine (chunked/parallel/resumable) -----------------------------

        /// <summary>
        /// Số luồng tối đa sao chép dữ liệu song song (nhiều bảng / dải phân vùng).
        /// Theo mặc định = min(CPU, 4). Giới hạn cứng ở [1..16].
        /// </summary>
        public int MaxParallelism { get; set; } = -1;

        /// <summary>
        /// Ngân sách bộ nhớ tối đa (MB) cho toàn bộ transfer engine; được chia cho các
        /// worker theo bề rộng dòng để RAM luôn bounded dù DB lớn. Mặc định 128.
        /// </summary>
        public int MaxBufferMB { get; set; } = 128;

        /// <summary>Số dòng tối đa mỗi chunk keyset (0 = tự chọn theo ngân sách bộ nhớ). Mặc định 0.</summary>
        public int ChunkRowCount { get; set; }

        /// <summary>
        /// Đọc theo chunk keyset (WHERE pk &gt; @mốc) thay vì SELECT nguyên bảng để không
        /// giữ khóa dài và chống spool LOB trên server nguồn. Luôn bật với bảng có khóa.
        /// </summary>
        public bool UseKeysetChunking { get; set; } = true;

        /// <summary>
        /// Cho phép app bật READ_COMMITTED_SNAPSHOT trên DB nguồn (cần quyền ALTER DATABASE).
        /// CẢNH BÁO: đây là thao tác GHI lên nguồn nên mặc định TẮT để giữ nguyên tắc
        /// "nguồn chỉ đọc". Chỉ bật khi bạn chủ động chấp nhận đổi cấu hình nguồn.
        /// Nếu thiếu quyền sẽ chỉ cảnh báo chứ không dừng. Mặc định false.
        /// </summary>
        public bool EnableReadCommittedSnapshotOnSource { get; set; } = false;

        /// <summary>
        /// Tạm chuyển recovery model của DB ĐÍCH sang BULK_LOGGED trong lúc đổ dữ liệu
        /// (tránh no-space log) rồi khôi phục lại sau khi xong. Mặc định true.
        /// </summary>
        public bool SwitchDestinationRecoveryDuringLoad { get; set; } = true;

        /// <summary>
        /// Bật ghi checkpoint (mốc chunk) để quá trình bị đứt/tắt giữa chừng có thể
        /// tiếp tục từ mốc thay vì đổ lại cả bảng. Mặc định true.
        /// </summary>
        public bool EnableTransferCheckpoint { get; set; } = true;

        /// <summary>Truy vấn ước lượng số dòng (sys.dm_db_partition_stats) khi bảng lớn thay vì COUNT_BIG. Mặc định true.</summary>
        public bool UseEstimatedRowCounts { get; set; } = true;

        /// <summary>
        /// Tự động kiểm tra toàn vẹn dữ liệu (số dòng + checksum nội dung) database đích
        /// so với nguồn ngay sau khi việc di chuyển hoàn tất. Mặc định true.
        /// </summary>
        public bool VerifyAfterMigration { get; set; } = true;

        /// <summary>Trên đích: bảng phân vùng được chia dải partition để copy song song mỗi partition. Mặc định true.</summary>
        public bool ParallelPartitionCopy { get; set; } = true;

        /// <summary>Optionally restrict the migration to these tables only. Entries are "schema.name".</summary>
        public System.Collections.Generic.IList<string> IncludeTablesOnly { get; set; } = new System.Collections.Generic.List<string>();

        /// <summary>Skip these tables entirely. Entries are "schema.name".</summary>
        public System.Collections.Generic.IList<string> ExcludeTables { get; set; } = new System.Collections.Generic.List<string>();

        /// <summary>Skip other objects by name (views, procedures, functions, sequences).</summary>
        public System.Collections.Generic.IList<string> ExcludeObjects { get; set; } = new System.Collections.Generic.List<string>();

        /// <summary>True when the destination should be emptied of user data before loading (data_only into an existing table).</summary>
        public bool TruncateDestinationTables { get; set; }

        /// <summary>
        /// Fail fast on the first error instead of skipping and continuing. Default false
        /// (i.e. non-critical errors are tolerated when ContinueOnNonCriticalErrors is true).
        /// </summary>
        public bool FailFast { get; set; }

        /// <summary>Validates the option bag and throws for clearly invalid input.</summary>
        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(SourceConnectionString))
                throw new InvalidOperationException("SourceConnectionString is required.");
            if (string.IsNullOrWhiteSpace(DestinationConnectionString))
                throw new InvalidOperationException("DestinationConnectionString is required.");
            if (BatchSize <= 0)
                throw new InvalidOperationException("BatchSize must be greater than zero.");
            if (BulkCopyTimeoutSeconds < 0)
                throw new InvalidOperationException("BulkCopyTimeoutSeconds cannot be negative.");
            if (CommandTimeoutSeconds <= 0)
                throw new InvalidOperationException("CommandTimeoutSeconds must be greater than zero.");
            if (MaxParallelism is < -1 or > 16)
                throw new InvalidOperationException("MaxParallelism must be between 1 and 16 (or -1 for auto).");
            if (MaxBufferMB <= 0 || MaxBufferMB > 4096)
                throw new InvalidOperationException("MaxBufferMB must be between 1 and 4096.");
            if (ChunkRowCount < 0)
                throw new InvalidOperationException("ChunkRowCount cannot be negative.");
            if (MaxParallelism == 0)
                MaxParallelism = -1;
        }

        /// <summary>Số luồng song song hiệu dụng (tự động nếu -1).</summary>
        public int EffectiveParallelism =>
            MaxParallelism > 0 ? MaxParallelism : Math.Clamp(Environment.ProcessorCount, 1, 4);
    }
}