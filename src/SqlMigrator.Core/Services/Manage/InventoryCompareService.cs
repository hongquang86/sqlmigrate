using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SqlMigrator.Core.Interfaces;
using SqlMigrator.Core.Models;
using SqlMigrator.Core.Services.DataMove;

namespace SqlMigrator.Core.Services.Manage
{
    /// <summary>Một dòng đối chiếu: số dòng nguồn vs đích của cùng một bảng.</summary>
    public sealed class InventoryCompareRow
    {
        public string Table { get; init; } = string.Empty;
        /// <summary>-1 = bảng không tồn tại bên đó.</summary>
        public long SourceRows { get; init; } = -1;
        public long DestRows { get; init; } = -1;
        public bool Match => SourceRows >= 0 && SourceRows == DestRows;
    }

    /// <summary>
    /// Đối chiếu nhanh 2 database (kể cả khác engine): liệt kê bảng bên nguồn
    /// (canonical, không đọc dữ liệu) rồi đếm dòng từng bảng 2 bên. Chỉ đọc.
    /// </summary>
    public sealed class InventoryCompareService
    {
        public async Task<IReadOnlyList<InventoryCompareRow>> CompareAsync(
            DbProbe source, DatabaseEngine srcEngine,
            DbProbe dest, DatabaseEngine dstEngine,
            IProgress<MigrationProgress>? progress = null,
            CancellationToken ct = default)
        {
            var srcEp = CrossEngineMigrationService.GetEndpoint(srcEngine);
            var dstEp = CrossEngineMigrationService.GetEndpoint(dstEngine);
            var schema = await srcEp.ReadSchemaAsync(source, ct).ConfigureAwait(false);
            var rows = new List<InventoryCompareRow>();
            var done = 0;
            foreach (var table in schema.Tables)
            {
                ct.ThrowIfCancellationRequested();
                done++;
                progress?.Report(new MigrationProgress(
                    (int)(done * 100.0 / Math.Max(1, schema.Tables.Count)),
                    $"Đối chiếu {table.DisplayName} ({done}/{schema.Tables.Count})..."));
                long srcCount = -1;
                long dstCount = -1;
                try
                {
                    srcCount = await srcEp.CountRowsAsync(source, table.Schema, table.Name, ct)
                        .ConfigureAwait(false);
                }
                catch
                {
                    // Đếm lỗi thì để -1 (lệch) thay vì dừng cả đợt.
                }
                try
                {
                    if (await dstEp.TableExistsAsync(dest, table.Schema, table.Name, ct)
                        .ConfigureAwait(false))
                    {
                        dstCount = await dstEp.CountRowsAsync(dest, table.Schema, table.Name, ct)
                            .ConfigureAwait(false);
                    }
                }
                catch
                {
                }
                rows.Add(new InventoryCompareRow
                {
                    Table = table.DisplayName,
                    SourceRows = srcCount,
                    DestRows = dstCount
                });
            }
            return rows;
        }
    }
}
