using System;

namespace SqlMigrator.Core.Services.Manage
{
    /// <summary>
    /// Xác nhận kill theo "state đổi" (quyết định Pha 4): thành công khi phiên
    /// biến mất khỏi danh sách, hoặc trạng thái sau khác trạng thái trước
    /// (VD: running → killed/rollback). Thuần logic để kiểm thử không cần server.
    /// </summary>
    public static class ManageVerify
    {
        public static bool IsKillConfirmed(string beforeStatus, string? afterStatus)
        {
            // Phiên biến mất (after null/rỗng) = đã kill xong.
            if (string.IsNullOrWhiteSpace(afterStatus))
                return true;
            // Trạng thái đổi (kể cả大小写) = kill đã có tác dụng.
            return !afterStatus.Trim().Equals(
                (beforeStatus ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
        }
    }
}
