using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using SqlMigrator.Core.Interfaces;
using SqlMigrator.Core.Models;

namespace SqlMigrator.Core.Services
{
    /// <summary>
    /// Chạy các kiểm tra nhanh TRƯỚC khi di chuyển (preflight):
    ///  - kết nối nguồn/đích còn sống (có retry cho lỗi tạm thời),
    ///  - phiên bản ProductMajorVersion của hai server,
    ///  - phân loại nâng/hạ/cùng phiên bản,
    ///  - database nguồn/đích có tồn tại không, số bảng mỗi bên,
    ///  - số trigger cấp server trên nguồn + quyền đọc/khả năng tạo trên đích.
    /// Không sửa bất cứ thứ gì trên hai server.
    /// </summary>
    public sealed class PreflightChecker : IPreflightChecker
    {
        private readonly MigrationOptions _options;
        private readonly ILogger _logger;

        public PreflightChecker(MigrationOptions options, ILogger logger)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<PreflightResult> CheckAsync(CancellationToken ct = default)
        {
            var result = new PreflightResult();

            var sourceDb = DatabaseNameOf(_options.SourceConnectionString);
            var destDb = DatabaseNameOf(_options.DestinationConnectionString);

            // Phiên bản hai server (đồng thời là kiểm tra kết nối có retry).
            var sourceMajor = await ReadProductMajorWithRetryAsync(_options.SourceConnectionString, ct).ConfigureAwait(false);
            var destMajor = await ReadProductMajorWithRetryAsync(_options.DestinationConnectionString, ct).ConfigureAwait(false);
            result.SourceMajorVersion = sourceMajor;
            result.DestinationMajorVersion = destMajor;

            if (sourceMajor < 0)
                result.AddError("Không kết nối được server nguồn để đọc phiên bản.");
            if (destMajor < 0)
                result.AddError("Không kết nối được server đích để đọc phiên bản.");

            if (sourceMajor >= 0 && destMajor >= 0)
            {
                result.Kind = sourceMajor.CompareTo(destMajor) switch
                {
                    0 => MigrationKind.SameVersion,
                    > 0 => MigrationKind.Downgrade,
                    _ => MigrationKind.Upgrade
                };
            }

            // Database tồn tại không (nếu chuỗi kết nối không có catalog thì xem như gắn master).
            result.SourceDatabaseExists = string.IsNullOrWhiteSpace(sourceDb) || await DatabaseExistsAsync(_options.SourceConnectionString, sourceDb, ct).ConfigureAwait(false);
            result.DestinationDatabaseExists = string.IsNullOrWhiteSpace(destDb) || await DatabaseExistsAsync(_options.DestinationConnectionString, destDb, ct).ConfigureAwait(false);

            if (!result.SourceDatabaseExists && !string.IsNullOrWhiteSpace(sourceDb))
                result.AddError($"Database nguồn '{sourceDb}' không tồn tại trên server nguồn.");

            if (!result.DestinationDatabaseExists && !_options.CreateDestinationDatabase && !string.IsNullOrWhiteSpace(destDb))
                result.AddError($"Database đích '{destDb}' không tồn tại và bạn chưa bật 'Tạo database đích nếu chưa tồn tại'.");

            // Số bảng mỗi bên (chỉ khi database gắn đúng).
            result.SourceTableCount = await TableCountAsync(_options.SourceConnectionString, ct).ConfigureAwait(false);
            if (result.DestinationDatabaseExists)
                result.DestinationTableCount = await TableCountAsync(_options.DestinationConnectionString, ct).ConfigureAwait(false);

            // Hồ sơ kiểm kê chi tiết (đếm view/SP/function/trigger/sequence + cờ 2016+).
            // Chỉ SELECT catalog nên an toàn; lỗi thì bỏ qua, giữ số liệu cũ.
            try
            {
                var reader = new DatabaseInventoryReader(_logger);
                if (result.SourceDatabaseExists)
                    result.SourceInventory = await reader.ReadAsync(_options.SourceConnectionString, ct).ConfigureAwait(false);
                if (result.DestinationDatabaseExists)
                    result.DestinationInventory = await reader.ReadAsync(_options.DestinationConnectionString, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Không đọc được hồ sơ kiểm kê ({Message}).", ex.Message);
            }

            if (result.DestinationTableCount > 0 && !_options.TruncateDestinationTables)
                result.AddWarning($"Database đích đã có {result.DestinationTableCount:N0} bảng với dữ liệu hiện hữu; nếu không xóa trước, dữ liệu có thể bị trùng/trộn lẫn.");

            await ProbeServerTriggersAsync(result, ct).ConfigureAwait(false);

            // Tổng hợp cảnh báo theo loại di chuyển.
            ApplyKindWarnings(result);

            foreach (var e in result.Errors)
                _logger.LogWarning("Preflight: {Message}", e);
            foreach (var w in result.Warnings)
                _logger.LogWarning("Preflight: {Message}", w);

            return result;
        }

        private void ApplyKindWarnings(PreflightResult result)
        {
            switch (result.Kind)
            {
                case MigrationKind.Downgrade:
                    result.AddWarning(
                        $"HẠ CẤP phiên bản: nguồn SQL {PreflightResult.DescribeVersion(result.SourceMajorVersion)} → " +
                        $"đích SQL {PreflightResult.DescribeVersion(result.DestinationMajorVersion)}. " +
                        "Các cấu trúc không được đích hỗ trợ sẽ bị cảnh báo hoặc bỏ qua.");
                    break;
                case MigrationKind.Upgrade:
                    result.AddWarning(
                        $"NÂNG CẤP phiên bản: nguồn SQL {PreflightResult.DescribeVersion(result.SourceMajorVersion)} → " +
                        $"đích SQL {PreflightResult.DescribeVersion(result.DestinationMajorVersion)}. Di chuyển lên phiên bản mới hơn, dữ liệu sẽ không bị hạ cấp trường.");
                    break;
                case MigrationKind.SameVersion:
                    result.AddWarning("Cùng phiên bản giữa nguồn và đích; di chuyển ngang an toàn nhất.");
                    break;
            }
        }

        private async Task ProbeServerTriggersAsync(PreflightResult result, CancellationToken ct)
        {
            // Đếm trigger cấp server trên nguồn; truy cập sys.server_triggers cần quyền
            // VIEW ANY DEFINITION (ẩn trong các cấu hình thấp hơn) → thử là biết ngay.
            const string countQuery = "SELECT COUNT(*) FROM sys.server_triggers WHERE is_disabled = 0;";
            try
            {
                using (var conn = new SqlConnection(_options.SourceConnectionString))
                {
                    await conn.OpenAsync(ct).ConfigureAwait(false);
                    using var cmd = new SqlCommand(countQuery, conn) { CommandTimeout = 30 };
                    result.SourceServerTriggerCount = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
                }

                if (result.SourceServerTriggerCount > 0)
                    result.AddWarning(
                        $"Nguồn có {result.SourceServerTriggerCount} trigger cấp server (ON ALL SERVER). " +
                        "Việc trích xuất cần quyền VIEW ANY DEFINITION và tạo trên đích cần CONTROL SERVER.");
            }
            catch (Exception ex)
            {
                result.SourceServerTriggerCount = -1;
                result.AddWarning("Không đọc được trigger cấp server trên nguồn (có thể thiếu quyền VIEW ANY DEFINITION): " + ex.Message);
            }

            // Kiểm tra đích có hỗ trợ server trigger và người dùng đích có CONTROL SERVER không,
            // bằng cách thử CREATE/DROP một trigger tạm — tuyệt đối đảm bảo hủy sạch sau đó.
            if (result.DestinationMajorVersion >= 9 && result.DestinationMajorVersion > 0)
                await ProbeCreateServerTriggerAsync(result, ct).ConfigureAwait(false);
        }

        private async Task ProbeCreateServerTriggerAsync(PreflightResult result, CancellationToken ct)
        {
            const string triggerName = "SQLMIGRATOR_PREFLIGHT_PROBE";
            const string createSql =
                "CREATE TRIGGER " + triggerName + " ON ALL SERVER " +
                "FOR DDL_LOGIN_EVENTS AS RETURN;";
            const string dropSql = "DROP TRIGGER " + triggerName + " ON ALL SERVER;";

            SqlConnection? conn = null;
            try
            {
                conn = new SqlConnection(_options.DestinationConnectionString);
                await conn.OpenAsync(ct).ConfigureAwait(false);

                try
                {
                    using (var cmd = new SqlCommand(createSql, conn) { CommandTimeout = 30 })
                        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
                catch (Exception createEx)
                {
                    result.AddWarning(
                        "Không tạo được trigger cấp server thử trên đích (cần CONTROL SERVER): " + createEx.Message);
                    return;
                }

                try
                {
                    using var drop = new SqlCommand(dropSql, conn) { CommandTimeout = 30 };
                    await drop.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
                catch (Exception dropEx)
                {
                    // Trigger vẫn còn lại — cảnh báo để người dùng tự xóa.
                    result.AddError(
                        $"Đã tạo trigger thử '{triggerName}' trên server đích nhưng KHÔNG xóa được: " + dropEx.Message +
                        ". Vui lòng dùng lệnh: DROP TRIGGER " + triggerName + " ON ALL SERVER;");
                }
            }
            catch (Exception ex)
            {
                result.AddWarning("Không dò được khả năng tạo trigger cấp server trên đích: " + ex.Message);
            }
            finally
            {
                conn?.Dispose();
            }
        }

        private async Task<int> ReadProductMajorWithRetryAsync(string connectionString, CancellationToken ct)
        {
            return await SqlRetry.WithRetryAsync(async token =>
            {
                using var conn = new SqlConnection(connectionString);
                await conn.OpenAsync(token).ConfigureAwait(false);
                using var cmd = new SqlCommand("SELECT CAST(SERVERPROPERTY('ProductMajorVersion') AS int);", conn)
                {
                    CommandTimeout = 30
                };
                return Convert.ToInt32(await cmd.ExecuteScalarAsync(token).ConfigureAwait(false));
            }, _logger, "Đọc phiên bản server", ct).ConfigureAwait(false);
        }

        private async Task<bool> DatabaseExistsAsync(string connectionString, string database, CancellationToken ct)
        {
            return await SqlRetry.WithRetryAsync(async token =>
            {
                using var conn = new SqlConnection(connectionString);
                await conn.OpenAsync(token).ConfigureAwait(false);
                using var cmd = new SqlCommand("SELECT CASE WHEN DB_ID(@db) IS NULL THEN 0 ELSE 1 END;", conn)
                {
                    CommandTimeout = 30
                };
                cmd.Parameters.AddWithValue("@db", database);
                return Convert.ToInt32(await cmd.ExecuteScalarAsync(token).ConfigureAwait(false)) == 1;
            }, _logger, "Kiểm tra database tồn tại", ct).ConfigureAwait(false);
        }

        private async Task<long> TableCountAsync(string connectionString, CancellationToken ct)
        {
            try
            {
                using var conn = new SqlConnection(connectionString);
                await conn.OpenAsync(ct).ConfigureAwait(false);
                using var cmd = new SqlCommand("SELECT COUNT(*) FROM sys.tables WHERE is_ms_shipped = 0;", conn)
                {
                    CommandTimeout = 30
                };
                // COUNT(*) trả về Int32; không ép kiểu boxed trực tiếp sang long (InvalidCastException).
                return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Preflight: không đếm được số bảng: {Message}", ex.Message);
                return -1;
            }
        }

        private static string DatabaseNameOf(string connectionString)
        {
            try
            {
                return new SqlConnectionStringBuilder(connectionString).InitialCatalog;
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}