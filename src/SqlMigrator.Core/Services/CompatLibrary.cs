using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace SqlMigrator.Core.Services
{
    /// <summary>
    /// Một shim tương thích trong thư viện Compat (triển khai T-SQL thuần các
    /// feature SQL 2016 còn thiếu trên SQL 2014).
    /// </summary>
    public sealed class CompatShim
    {
        /// <summary>Id dùng trong rewrite rule (trường RequiresShim), VD: "Json".</summary>
        public string Id { get; init; } = string.Empty;

        /// <summary>Tên resource nhúng chứa script SQL triển khai.</summary>
        public string ResourceName { get; init; } = string.Empty;

        /// <summary>Mô tả ngắn gọn (tiếng Việt) để ghi log.</summary>
        public string Description { get; init; } = string.Empty;
    }

    /// <summary>
    /// Thư viện tương thích SQL 2016 → 2014 (thuần T-SQL, không CLR).
    /// NGUYÊN TẮC BẮT BUỘC: chỉ triển khai lên database ĐÍCH, không bao giờ
    /// chạm nguồn. Mọi script đều idempotent — chạy lại nhiều lần an toàn.
    /// </summary>
    public static class CompatLibrary
    {
        private const string ResourcePrefix = "SqlMigrator.Core.Sql.Compat.";

        /// <summary>Toàn bộ shim có sẵn (ổn định theo Id).</summary>
        public static readonly IReadOnlyList<CompatShim> Shims = new List<CompatShim>
        {
            new CompatShim
            {
                Id = "Json",
                ResourceName = ResourcePrefix + "JsonShim.sql",
                Description = "Hàm JSON (JsonValue/JsonQuery/IsJson/OpenJson/JsonEscape)"
            },
            new CompatShim
            {
                Id = "Date",
                ResourceName = ResourcePrefix + "DateShim.sql",
                Description = "Hàm DatediffBig (bigint, không tràn số)"
            },
            new CompatShim
            {
                Id = "Session",
                ResourceName = ResourcePrefix + "SessionShim.sql",
                Description = "Bảng + hàm SESSION_CONTEXT theo session"
            },
            new CompatShim
            {
                Id = "TimeZone",
                ResourceName = ResourcePrefix + "TimeZoneShim.sql",
                Description = "Bảng offset + hàm chuyển múi giờ (giờ chuẩn, xem lưu ý DST)"
            }
        }.AsReadOnly();

        /// <summary>Đọc script SQL của một shim từ resource nhúng trong assembly.</summary>
        public static string GetShimSql(string shimId)
        {
            var shim = Shims.FirstOrDefault(s =>
                s.Id.Equals(shimId, StringComparison.OrdinalIgnoreCase));
            if (shim == null)
                throw new InvalidOperationException("Không tìm thấy shim tương thích: " + shimId);

            var assembly = typeof(CompatLibrary).Assembly;
            using var stream = assembly.GetManifestResourceStream(shim.ResourceName);
            if (stream == null)
                throw new InvalidOperationException("Không đọc được resource nhúng: " + shim.ResourceName);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        /// <summary>
        /// Triển khai các shim yêu cầu lên database ĐÍCH (tạo schema Compat + hàm/bảng).
        /// CHỈ truyền chuỗi kết nối ĐÍCH vào đây — không bao giờ truyền nguồn.
        /// Idempotent: mỗi shim có guard DROP/CREATE hoặc IF NOT EXISTS nên gọi
        /// nhiều lần (quét + fix) vẫn an toàn.
        /// </summary>
        public static async Task EnsureDeployedAsync(
            string destinationConnectionString,
            IEnumerable<string> shimIds,
            ILogger logger,
            CancellationToken ct = default)
        {
            var ids = shimIds
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (ids.Count == 0)
                return;

            using var conn = new SqlConnection(destinationConnectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);

            foreach (var id in ids)
            {
                ct.ThrowIfCancellationRequested();
                var shim = Shims.FirstOrDefault(s =>
                    s.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
                if (shim == null)
                {
                    logger.LogWarning("Bỏ qua shim không rõ '{ShimId}'.", id);
                    continue;
                }

                var sql = GetShimSql(shim.Id);
                foreach (var batch in ScriptUtils.SplitBatches(sql))
                {
                    if (string.IsNullOrWhiteSpace(batch))
                        continue;
                    using var cmd = new SqlCommand(batch, conn) { CommandTimeout = 300 };
                    await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                logger.LogInformation("Đã triển khai shim tương thích '{Shim}' lên đích: {Description}.",
                    shim.Id, shim.Description);
            }
        }
    }
}
