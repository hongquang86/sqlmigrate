using System;
using System.Collections.Generic;

namespace SqlMigrator.Core.Models
{
    /// <summary>
    /// Complete extracted view of the source database: ordered object scripts plus
    /// table metadata used for data copy and constraint management.
    /// </summary>
    public sealed class SchemaModel
    {
        /// <summary>Scripts to run first, in dependency-safe order, when building the destination.</summary>
        public IReadOnlyList<DatabaseObject> Objects { get; init; } = Array.Empty<DatabaseObject>();

        /// <summary>Objects that must be created after everything else (currently foreign keys).</summary>
        public IReadOnlyList<DatabaseObject> DeferredObjects { get; init; } = Array.Empty<DatabaseObject>();

        /// <summary>Table metadata used by the data copier and constraint manager.</summary>
        public IReadOnlyList<TableSchema> Tables { get; init; } = Array.Empty<TableSchema>();

        /// <summary>Non-fatal compatibility findings produced while inspecting the source.</summary>
        public IReadOnlyList<CompatibilityIssue> Warnings { get; init; } = Array.Empty<CompatibilityIssue>();

        /// <summary>Source server version, e.g. 13.0.4001.0.</summary>
        public string SourceVersion { get; init; } = string.Empty;

        /// <summary>Destination server version (may be empty until checked).</summary>
        public string DestinationVersion { get; init; } = string.Empty;

        /// <summary>Source database collation (used when creating the destination database).</summary>
        public string DatabaseCollation { get; init; } = string.Empty;

        /// <summary>Source database name (used when creating the destination database).</summary>
        public string DatabaseName { get; init; } = string.Empty;

        /// <summary>Total distinct batches captured (debug/audit).</summary>
        public int ObjectCount => Objects.Count + DeferredObjects.Count;
    }

    /// <summary>Kết quả pha tạo cấu trúc (schema build).</summary>
    public sealed class SchemaBuildResult
    {
        public int Built { get; set; }

        /// <summary>Danh sách lỗi không nghiêm trọng đã bỏ qua (khi tiếp tục sau lỗi).</summary>
        public IReadOnlyList<string> Errors { get; set; } = Array.Empty<string>();

        public void AddError(string message)
        {
            var list = new List<string>(Errors) { message };
            Errors = list;
        }
    }

    /// <summary>
    /// Một mốc tiến trình: phần trăm hoàn thành (ước lượng) và thông điệp trạng thái
    /// bằng tiếng Việt, dùng để cập nhật thanh tiến trình + nhãn trạng thái của giao diện.
    /// </summary>
    public sealed class MigrationProgress
    {
        public int Percent { get; set; }
        public string Message { get; set; } = string.Empty;

        public MigrationProgress(int percent, string message)
        {
            Percent = percent;
            Message = message ?? string.Empty;
        }
    }

    /// <summary>Result of the data copy phase.</summary>
    public sealed class DataCopyResult
    {
        public int TablesCopied { get; set; }
        public int TablesSkipped { get; set; }
        public long RowsCopied { get; set; }
        public long RowsExcluded { get; set; }
        public long BytesCopied { get; set; }

        /// <summary>Tốc độ trung bình (byte/giây) để UI hiển thị MB/s.</summary>
        public double BytesPerSecond { get; set; }
        public TimeSpan Elapsed { get; set; }
        public IReadOnlyList<string> Errors { get; set; } = Array.Empty<string>();
    }

    /// <summary>Overall migration result.</summary>
    public sealed class MigrationResult
    {
        public bool Success { get; set; }
        public TimeSpan Elapsed { get; set; }
        public int ObjectsBuilt { get; set; }
        public int ObjectsSkipped { get; set; }

        /// <summary>Lỗi không nghiêm trọng ghi nhận khi tạo cấu trúc.</summary>
        public IReadOnlyList<string> ObjectErrors { get; set; } = Array.Empty<string>();
        public DataCopyResult? DataCopy { get; set; }
        public IReadOnlyList<string> Errors { get; set; } = Array.Empty<string>();
        public IReadOnlyList<CompatibilityIssue> Warnings { get; set; } = Array.Empty<CompatibilityIssue>();

        /// <summary>Hồ sơ kiểm kê nguồn lúc bắt đầu (để đối chiếu sau migrate).</summary>
        public DatabaseInventory? SourceInventory { get; set; }

        /// <summary>Hồ sơ kiểm kê đích lúc kết thúc (để đối chiếu với nguồn).</summary>
        public DatabaseInventory? DestinationInventory { get; set; }

        /// <summary>Kết quả kiểm tra toàn vẹn dữ liệu đích sau khi di chuyển (nếu được bật).</summary>
        public DataVerificationReport? Verification { get; set; }

        public void AddError(string message)
        {
            var list = new List<string>(Errors) { message };
            Errors = list;
            Success = false;
        }
    }
}