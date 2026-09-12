using System;

namespace SqlMigrator.Core.Models
{
    /// <summary>Yêu cầu sao lưu một database SQL Server ra file phía server.</summary>
    public sealed class BackupRequest
    {
        public string Database { get; init; } = string.Empty;
        /// <summary>Đường dẫn file .bak TRÊN SERVER đích (không phải máy chạy app).</summary>
        public string BackupFile { get; init; } = string.Empty;
        public bool FullBackup { get; init; } = true;
        public bool Compression { get; init; } = true;
    }

    /// <summary>Kết quả một lần sao lưu/khôi phục.</summary>
    public sealed class BackupResult
    {
        public bool Success { get; set; }
        public TimeSpan Elapsed { get; set; }
        public string? Error { get; set; }
    }

    /// <summary>Yêu cầu khôi phục database từ file backup phía server.</summary>
    public sealed class RestoreRequest
    {
        public string Database { get; init; } = string.Empty;
        public string BackupFile { get; init; } = string.Empty;
        /// <summary>Ghi đè database đã tồn tại (kèm ngắt kết nối đang dùng).</summary>
        public bool ReplaceExisting { get; init; }
        /// <summary>Thư mục file dữ liệu (.mdf) khi MOVE; trống = giữ vị trí trong backup.</summary>
        public string? DataDirectory { get; init; }
        /// <summary>Thư mục file log (.ldf) khi MOVE.</summary>
        public string? LogDirectory { get; init; }
        /// <summary>True = RECOVERY (online ngay), false = NORECOVERY (chờ log tiếp).</summary>
        public bool WithRecovery { get; init; } = true;
    }

    /// <summary>File logic trong backup (để MOVE khi restore sang chỗ khác).</summary>
    public sealed class BackupFileEntry
    {
        public string LogicalName { get; init; } = string.Empty;
        public string PhysicalName { get; init; } = string.Empty;
        /// <summary>D = data, L = log.</summary>
        public string Type { get; init; } = string.Empty;
    }

    /// <summary>Một dòng lịch sử sao lưu/khôi phục (lưu file local).</summary>
    public sealed class BackupHistoryEntry
    {
        public DateTime AtUtc { get; set; }
        public string Kind { get; set; } = string.Empty;
        public string Server { get; set; } = string.Empty;
        public string Database { get; set; } = string.Empty;
        public string File { get; set; } = string.Empty;
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
    }
}
