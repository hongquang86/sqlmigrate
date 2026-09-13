using System;
using System.Collections.Generic;

namespace SqlMigrator.Core.Models
{
    /// <summary>Thông tin một database phục vụ tab Quản trị (chỉ đọc).</summary>
    public sealed class ManagedDatabase
    {
        public string Name { get; init; } = string.Empty;
        public string State { get; init; } = string.Empty;
        /// <summary>Tổng dung lượng cấp phát (MB).</summary>
        public double AllocatedMb { get; init; }
        /// <summary>Dung lượng dữ liệu đã dùng (MB). -1 = không đọc được.</summary>
        public double UsedMb { get; init; } = -1;
        public string? Note { get; init; }
    }

    /// <summary>Một phiên làm việc đang chạy trên server (chỉ đọc).</summary>
    public sealed class ManagedSession
    {
        /// <summary>Id phiên (SPID SQL Server / PROCESSLIST ID MySQL). Dạng chuỗi để chung 2 engine.</summary>
        public string Id { get; init; } = string.Empty;
        public string Login { get; init; } = string.Empty;
        public string Host { get; init; } = string.Empty;
        public string Database { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public string Command { get; init; } = string.Empty;
        public string WaitInfo { get; init; } = string.Empty;
        public DateTime LoginTime { get; init; }
    }

    /// <summary>Kết quả kill session + xác nhận đổi trạng thái.</summary>
    public sealed class KillSessionResult
    {
        public bool KillSent { get; init; }
        /// <summary>True khi sau kill: phiên biến mất hoặc trạng thái đã đổi.</summary>
        public bool Confirmed { get; init; }
        public string BeforeStatus { get; init; } = string.Empty;
        public string AfterStatus { get; init; } = string.Empty;
        public string? Error { get; init; }
    }

    /// <summary>Kết quả đọc manage một lượt (databases + sessions).</summary>
    public sealed class ManageSnapshot
    {
        public IReadOnlyList<ManagedDatabase> Databases { get; init; } = Array.Empty<ManagedDatabase>();
        public IReadOnlyList<ManagedSession> Sessions { get; init; } = Array.Empty<ManagedSession>();
    }
}
