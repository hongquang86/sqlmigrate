using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SqlMigrator.Core.Models;

namespace SqlMigrator.Core.Interfaces
{
    /// <summary>
    /// Captures the entire schema of the source database as an ordered, dependency-safe
    /// set of script batches plus rich metadata (tables, columns, foreign keys).
    /// </summary>
    public interface ISchemaExtractor
    {
        Task<SchemaModel> ExtractAsync(CancellationToken ct = default);
    }

    /// <summary>Kết quả kiểm tra trước khi di chuyển (preflight): phiên bản, database, số bảng...</summary>
    public interface IPreflightChecker
    {
        Task<PreflightResult> CheckAsync(CancellationToken ct = default);
    }

    /// <summary>Builds scripts on the destination database in the correct dependency order.</summary>
    public interface ISchemaBuilder
    {
        /// <summary>Runs all primary object scripts (schemas, sequences, tables, views, modules...).</summary>
        Task<SchemaBuildResult> BuildAsync(SchemaModel schema, CancellationToken ct = default);

        /// <summary>Runs deferred scripts (foreign keys) after primary build and before data load.</summary>
        Task<SchemaBuildResult> BuildDeferredObjectsAsync(SchemaModel schema, CancellationToken ct = default);
    }

    /// <summary>Bulk-copies every table's rows from source to destination.</summary>
    public interface IDataCopier
    {
        Task<DataCopyResult> CopyAsync(SchemaModel schema, CancellationToken ct = default,
            IProgress<MigrationProgress>? progress = null);
    }

    /// <summary>
    /// So sánh toàn vẹn dữ liệu giữa database nguồn và database đích: đếm số dòng
    /// (COUNT_BIG) và checksum nội dung (CHECKSUM_AGG(BINARY_CHECKSUM(*))) cho từng bảng
    /// để xác nhận đích đầy đủ và đúng hoàn toàn như nguồn.
    /// </summary>
    public interface IDataVerifier
    {
        Task<DataVerificationReport> VerifyAsync(SchemaModel schema, CancellationToken ct = default,
            IProgress<MigrationProgress>? progress = null);
    }

    /// <summary>
    /// Quét và phân tích sự khác biệt giữa database nguồn và đích, hiển thị báo cáo
    /// chi tiết + gợi ý xử lý. KHÔNG ghi database nguồn — chỉ đọc.
    /// Sau khi user chọn vấn đề muốn xử lý, gọi FixAsync để fix những mục được chọn.
    /// </summary>
    public interface IDatabaseReconciler
    {
        /// <summary>
        /// Quét source vs dest: đọc schema nguồn, inventory đích, so sánh.
        /// Trả về ReconcileResult chứa danh sách vấn đề + thống kê.
        /// CHỈ ĐỌC — KHÔNG ghi database nguồn hay đích.
        /// </summary>
        Task<ReconcileResult> ScanAsync(CancellationToken ct = default,
            IProgress<MigrationProgress>? progress = null);

        /// <summary>
        /// Xử lý (fix) các vấn đề được user chọn trên database ĐÍCH.
        /// Mỗi issue sẽ được xử lý theo loại: tạo object thiếu, thêm cột thiếu, etc.
        /// KHÔNG ghi database nguồn.
        /// </summary>
        Task<ReconcileFixResult> FixAsync(IReadOnlyList<ReconcileIssue> issues,
            CancellationToken ct = default,
            IProgress<MigrationProgress>? progress = null);
    }

    /// <summary>
    /// Toggles CHECK constraints, foreign keys, triggers and non-clustered indexes on
    /// destination tables to make bulk loading fast and ordering-independent.
    /// </summary>
    public interface IConstraintManager
    {
        Task DisableConstraintsAsync(IEnumerable<TableSchema> tables, CancellationToken ct = default);
        Task EnableConstraintsAsync(IEnumerable<TableSchema> tables, bool checkExistingData, CancellationToken ct = default);
        Task DisableTriggersAsync(IEnumerable<TableSchema> tables, CancellationToken ct = default);
        Task EnableTriggersAsync(IEnumerable<TableSchema> tables, CancellationToken ct = default);
        Task DisableSecondaryIndexesAsync(IEnumerable<TableSchema> tables, CancellationToken ct = default);
        Task RebuildSecondaryIndexesAsync(IEnumerable<TableSchema> tables, CancellationToken ct = default);
    }

    /// <summary>
    /// Compares source and destination server versions and reports features that the
    /// destination does not support (memory-optimized tables, columnstore, temporal, ...).
    /// </summary>
    public interface ICompatibilityChecker
    {
        Task<CompatibilityReport> CheckAsync(SchemaModel schema, CancellationToken ct = default);
    }

    /// <summary>Result of a compatibility analysis.</summary>
    public sealed class CompatibilityReport
    {
        public IReadOnlyList<CompatibilityIssue> Issues { get; init; } = System.Array.Empty<CompatibilityIssue>();

        /// <summary>Plain names ("schema.table") of tables that must be skipped on the destination.</summary>
        public IReadOnlyList<string> TablesToSkip { get; init; } = System.Array.Empty<string>();
    }
}