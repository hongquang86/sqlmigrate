using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SqlMigrator.Core.Interfaces;
using SqlMigrator.Core.Models;

namespace SqlMigrator.Core.Interfaces
{
    /// <summary>
    /// Cổng thao tác dữ liệu/schema của một engine: đọc schema chuẩn, đọc chunk,
    /// tạo bảng, kiểm tra, đếm, ghi batch. Mọi phương thức chỉ chạm engine của nó;
    /// nguồn chỉ đọc, đích chỉ ghi theo đúng nguyên tắc một chiều.
    /// </summary>
    public interface IDbEndpoint
    {
        DatabaseEngine Engine { get; }

        /// <summary>Đọc toàn bộ schema bảng (cột/khóa/FK) về mô hình chuẩn.</summary>
        Task<CanonicalSchema> ReadSchemaAsync(DbProbe probe, CancellationToken ct = default);

        /// <summary>Bảng đã tồn tại trên engine này chưa.</summary>
        Task<bool> TableExistsAsync(DbProbe probe, string? schema, string table,
            CancellationToken ct = default);

        /// <summary>Chạy DDL đã dựng sẵn (CREATE TABLE...).</summary>
        Task ExecuteDdlAsync(DbProbe probe, string ddl, CancellationToken ct = default);

        /// <summary>
        /// Xóa bảng nếu tồn tại (dùng khi ghi đè). cascade=true chỉ PG dùng
        /// (CASCADE), các engine khác bỏ qua tham số.
        /// </summary>
        Task DropTableIfExistsAsync(DbProbe probe, string? schema, string table,
            bool cascade, CancellationToken ct = default);

        /// <summary>Đếm dòng chính xác (để đối chiếu sau chép).</summary>
        Task<long> CountRowsAsync(DbProbe probe, string? schema, string table,
            CancellationToken ct = default);

        /// <summary>
        /// Đọc một batch dòng theo thứ tự khóa (afterKeyExclusive null = từ đầu).
        /// Trả danh sách dòng theo đúng thứ tự cột của <paramref name="table"/>.
        /// Hết dữ liệu thì trả rỗng. Không bao giờ giữ toàn bảng trong RAM.
        /// </summary>
        Task<IReadOnlyList<object?[]>> ReadBatchAsync(
            DbProbe probe, CanonicalTable table, string? afterKeyExclusive, int batchSize,
            CancellationToken ct = default);

        /// <summary>
        /// Ghi một batch dòng (thứ tự cột như <paramref name="table"/>) bằng cơ chế
        /// nhanh nhất của engine (COPY/bulk/multi-row). Trả số dòng đã ghi.
        /// </summary>
        Task<long> WriteBatchAsync(
            DbProbe probe, CanonicalTable table, IReadOnlyList<object?[]> rows,
            CancellationToken ct = default);
    }
}
