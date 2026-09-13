using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SqlMigrator.Core.Interfaces;
using SqlMigrator.Core.Models;

namespace SqlMigrator.Core.Services.Manage
{
    /// <summary>
    /// Cổng quản trị đọc + kill session của một engine (tab Quản trị chạy flow riêng,
    /// không can thiệp pipeline migrate). Mọi thao tác đọc đều SELECT; thao tác ghi
    /// duy nhất là KILL session do người dùng xác nhận rõ ràng.
    /// </summary>
    public interface IManageProvider
    {
        DatabaseEngine Engine { get; }

        /// <summary>Đọc danh sách database + dung lượng (tablespace/data usage).</summary>
        Task<IReadOnlyList<ManagedDatabase>> ListDatabasesAsync(
            DbProbe probe, CancellationToken ct = default);

        /// <summary>Đọc các session đang chạy.</summary>
        Task<IReadOnlyList<ManagedSession>> ListSessionsAsync(
            DbProbe probe, CancellationToken ct = default);

        /// <summary>
        /// Kill session theo Id lấy từ <see cref="ListSessionsAsync"/> (Id đã được
        /// kiểm tra là số trước khi ghép lệnh vì KILL không nhận tham số hóa).
        /// Xong tự đọc lại để xác nhận trạng thái đã đổi.
        /// </summary>
        Task<KillSessionResult> KillSessionAsync(
            DbProbe probe, string sessionId, CancellationToken ct = default);
    }
}
