using SqlMigrator.Core.Models;
using SqlMigrator.Core.Security;

namespace SqlMigrator.UI.Models
{
    /// <summary>
    /// Toàn bộ dữ liệu do người dùng nhập trên các trang Wizard, được tích lũy qua từng bước
    /// và chuyển thành <see cref="MigrationOptions"/> ở trang cuối (trang Chạy).
    /// </summary>
    public sealed class WizardState
    {
        /// <summary>Profile server nguồn (mật khẩu đã mã hóa DPAPI).</summary>
        public ConnectionProfile? Source { get; set; }

        /// <summary>Profile server đích (mật khẩu đã mã hóa DPAPI).</summary>
        public ConnectionProfile? Destination { get; set; }

        /// <summary>Cho phép app tự tạo database đích nếu chưa tồn tại.</summary>
        public bool CreateDestinationDatabase { get; set; }

        /// <summary>Thư mục chứa file dữ liệu (.mdf) của database đích khi tạo mới (rỗng = dùng mặc định server).</summary>
        public string? DestinationDataFileDirectory { get; set; }

        /// <summary>Thư mục chứa file log (.ldf) của database đích khi tạo mới (rỗng = dùng mặc định server hoặc chung thư mục .mdf).</summary>
        public string? DestinationLogFileDirectory { get; set; }

        /// <summary>Chế độ di chuyển (đầy đủ / chỉ schema / chỉ dữ liệu).</summary>
        public MigrationMode Mode { get; set; } = MigrationMode.Full;

        /// <summary>Tắt khóa ngoại trong lúc đổ dữ liệu để tăng tốc.</summary>
        public bool DisableForeignKeyConstraints { get; set; } = true;

        /// <summary>Tắt ràng buộc CHECK trong lúc đổ dữ liệu để tăng tốc.</summary>
        public bool DisableCheckConstraints { get; set; } = true;

        /// <summary>Tắt trigger bảng trong lúc đổ dữ liệu.</summary>
        public bool DisableTriggers { get; set; }

        /// <summary>Tắt index thứ cấp trong lúc đổ dữ liệu, sau đó rebuild.</summary>
        public bool DisableIndexesDuringLoad { get; set; }

        /// <summary>Giữ nguyên giá trị cột identity của nguồn.</summary>
        public bool PreserveIdentity { get; set; }

        /// <summary>Số dòng mỗi đợt bulk copy.</summary>
        public int BatchSize { get; set; } = 5000;

        /// <summary>Kiểm tra dữ liệu hiện có khi bật lại ràng buộc (WITH CHECK).</summary>
        public bool CheckDataAfterLoad { get; set; } = true;

        /// <summary>Tiếp tục khi gặp lỗi không nghiêm trọng.</summary>
        public bool ContinueOnNonCriticalErrors { get; set; } = true;

        /// <summary>Ép thử tạo đối tượng feature đích không hỗ trợ (hạ cấp), thất bại mới bỏ qua.</summary>
        public bool ForceUnsupportedFeatures { get; set; }

        /// <summary>Xóa dữ liệu hiện có trên đích trước khi đổ (chế độ chỉ dữ liệu).</summary>
        public bool TruncateDestination { get; set; }

        /// <summary>Sao chép trigger cấp server (ON ALL SERVER) từ nguồn.</summary>
        public bool CopyServerTriggers { get; set; } = true;

        /// <summary>Tự dọn dữ liệu bảng đổ dở trên đích khi bulk copy gặp lỗi.</summary>
        public bool CleanupPartialTableOnError { get; set; } = true;

        /// <summary>Bọc mỗi đợt vào một transaction riêng.</summary>
        public bool UseInternalTransaction { get; set; }

        /// <summary>Số luồng tối đa sao chép song song (bảng độc lập). -1 = tự động.</summary>
        public int MaxParallelism { get; set; } = -1;

        /// <summary>Ngân sách bộ nhớ tối đa (MB) cho engine dữ liệu lớn.</summary>
        public int MaxBufferMB { get; set; } = 128;

        /// <summary>Số dòng tối đa mỗi chunk (0 = tự chọn theo ngân sách).</summary>
        public int ChunkRowCount { get; set; }

        /// <summary>Dùng chunk keyset (WHERE key > mốc) thay vì scan toàn bảng.</summary>
        public bool UseKeysetChunking { get; set; } = true;

        /// <summary>
        /// Cho phép bật READ_COMMITTED_SNAPSHOT trên nguồn (cần quyền ALTER DB).
        /// Mặc định TẮT vì đây là thao tác ghi lên nguồn.
        /// </summary>
        public bool EnableReadCommittedSnapshotOnSource { get; set; } = false;

        /// <summary>Tạm chuyển đích sang BULK_LOGGED khi đổ dữ liệu lớn.</summary>
        public bool SwitchDestinationRecoveryDuringLoad { get; set; } = true;

        /// <summary>Bật checkpoint hồi tục (ghi mốc chunk vào file).</summary>
        public bool EnableTransferCheckpoint { get; set; } = true;

        /// <summary>Ước lượng số dòng từ sys.partitions thay vì COUNT_BIG.</summary>
        public bool UseEstimatedRowCounts { get; set; } = true;

        /// <summary>Tự kiểm tra toàn vẹn dữ liệu đích (số dòng + checksum) sau khi di chuyển.</summary>
        public bool VerifyAfterMigration { get; set; } = true;

        /// <summary>Thời gian chờ tối đa của thao tác bulk copy (giây; 0 = không giới hạn).</summary>
        public int BulkCopyTimeoutSeconds { get; set; }

        /// <summary>Thời gian chờ tối đa của câu lệnh (giây).</summary>
        public int CommandTimeoutSeconds { get; set; } = 600;

        /// <summary>Đường dẫn file nhật ký (rỗng = chỉ hiển thị trong màn hình Chạy).</summary>
        public string? LogFilePath { get; set; }

        /// <summary>Danh sách bảng bị loại trừ dạng "schema.tên_bảng".</summary>
        public IList<string> ExcludeTables { get; } = new List<string>();

        /// <summary>Danh sách đối tượng (SP/function/view/trigger) bị loại trừ dạng "schema.tên".</summary>
        public IList<string> ExcludeObjects { get; } = new List<string>();

        /// <summary>Biến MigrationOptions đã sẵn sàng để chạy.</summary>
        public MigrationOptions BuildMigrationOptions()
        {
            if (Source == null) throw new InvalidOperationException("Chưa cấu hình server nguồn.");
            if (Destination == null) throw new InvalidOperationException("Chưa cấu hình server đích.");

            var options = new MigrationOptions
            {
                SourceConnectionString = string.Empty, // được điền sau qua bộ dựng chuỗi kết nối an toàn
                DestinationConnectionString = string.Empty,
                Mode = Mode,
                CreateDestinationDatabase = CreateDestinationDatabase,
                DestinationDataFileDirectory = string.IsNullOrWhiteSpace(DestinationDataFileDirectory) ? null : DestinationDataFileDirectory.Trim(),
                DestinationLogFileDirectory = string.IsNullOrWhiteSpace(DestinationLogFileDirectory) ? null : DestinationLogFileDirectory.Trim(),
                DisableForeignKeyConstraints = DisableForeignKeyConstraints,
                DisableCheckConstraints = DisableCheckConstraints,
                DisableTriggers = DisableTriggers,
                DisableIndexesDuringLoad = DisableIndexesDuringLoad,
                PreserveIdentity = PreserveIdentity,
                BatchSize = BatchSize,
                CheckDataAfterLoad = CheckDataAfterLoad,
                ContinueOnNonCriticalErrors = ContinueOnNonCriticalErrors,
                ForceUnsupportedFeatures = ForceUnsupportedFeatures,
                TruncateDestinationTables = TruncateDestination,
                UseInternalTransaction = UseInternalTransaction,
                CopyServerTriggers = CopyServerTriggers,
                CleanupPartialTableOnError = CleanupPartialTableOnError,
                BulkCopyTimeoutSeconds = BulkCopyTimeoutSeconds,
                CommandTimeoutSeconds = CommandTimeoutSeconds,
                LogFilePath = LogFilePath,
                // Engine dữ liệu lớn
                MaxParallelism = MaxParallelism,
                MaxBufferMB = MaxBufferMB,
                ChunkRowCount = ChunkRowCount,
                UseKeysetChunking = UseKeysetChunking,
                EnableReadCommittedSnapshotOnSource = EnableReadCommittedSnapshotOnSource,
                SwitchDestinationRecoveryDuringLoad = SwitchDestinationRecoveryDuringLoad,
                EnableTransferCheckpoint = EnableTransferCheckpoint,
                UseEstimatedRowCounts = UseEstimatedRowCounts,
                VerifyAfterMigration = VerifyAfterMigration
            };

            foreach (var t in ExcludeTables)
                options.ExcludeTables.Add(t);

            foreach (var o in ExcludeObjects)
                options.ExcludeObjects.Add(o);

            return options;
        }
    }
}