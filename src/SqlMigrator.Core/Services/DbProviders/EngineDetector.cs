using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SqlMigrator.Core.Interfaces;
using SqlMigrator.Core.Models;

namespace SqlMigrator.Core.Services.DbProviders
{
    /// <summary>
    /// Nhận diện engine đang chạy ở đầu bên kia: thử từng provider theo thứ tự
    /// (SQL Server → PostgreSQL → MySQL → MongoDB), timeout ngắn mỗi lần thử.
    /// SQLite là file nên UI gọi trực tiếp SqliteProvider, không qua auto.
    /// Không sửa bất cứ thứ gì phía server; mọi lỗi probing đều nuốt êm.
    /// </summary>
    public static class EngineDetector
    {
        private static readonly IReadOnlyList<IDbProvider> ServerProviders = new List<IDbProvider>
        {
            new SqlServerProvider(),
            new PostgreSqlProvider(),
            new MySqlProvider(),
            new MongoDbProvider()
        }.AsReadOnly();

        /// <summary>Thử nhận diện theo thứ tự ưu tiên, trả engine đầu tiên khớp.</summary>
        public static async Task<EngineInfo> DetectAsync(
            DbProbe probe, ILogger? logger = null, CancellationToken ct = default)
        {
            var log = logger ?? NullLogger.Instance;
            foreach (var provider in ServerProviders)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var info = await provider.DetectAsync(probe, ct).ConfigureAwait(false);
                    if (info != null)
                    {
                        log.LogInformation("Nhận diện engine: {Engine}.", info.Describe());
                        return info;
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    log.LogDebug("Probe {Engine} thất bại ({Message}).", provider.DisplayName, ex.Message);
                }
            }
            return new EngineInfo();
        }

        /// <summary>Tìm provider theo engine (null nếu Unknown).</summary>
        public static IDbProvider? GetProvider(DatabaseEngine engine)
        {
            foreach (var provider in ServerProviders)
            {
                if (provider.Engine == engine)
                    return provider;
            }
            if (engine == DatabaseEngine.Sqlite)
                return new SqliteProvider();
            return null;
        }
    }

    /// <summary>
    /// Chốt chặn chạy migrate: Pha 1 chỉ cho SQL Server ↔ SQL Server; Pha 3 mở thêm
    /// các cặp quan hệ được hỗ trợ (SQLite/PostgreSQL chéo nhau và với SQL Server).
    /// Thuần logic để dễ kiểm thử; orchestrator gọi trước mọi pha ghi.
    /// </summary>
    public static class MigrationGuard
    {
        /// <summary>Trả null khi cặp engine được phép chạy, ngược lại trả thông điệp tiếng Việt.</summary>
        public static string? EnsureSupportedEngines(string? sourceEngine, string? destinationEngine)
        {
            var src = EngineInfo.ParseEngine(sourceEngine);
            var dst = EngineInfo.ParseEngine(destinationEngine);
            if (IsSupportedPair(src, dst))
                return null;
            return $"Cặp engine {Describe(src)} → {Describe(dst)} chưa di chuyển được ở bản này. "
                + "Các engine hỗ trợ: SQL Server, PostgreSQL, SQLite, MySQL/MariaDB, MongoDB. "
                + "Hãy chọn lại cặp được hỗ trợ hoặc chờ các pha engine tiếp theo.";
        }

        /// <summary>Cặp engine đã chạy migrate được chưa (đối xứng).</summary>
        public static bool IsSupportedPair(DatabaseEngine source, DatabaseEngine destination)
        {
            if (source == DatabaseEngine.Unknown || destination == DatabaseEngine.Unknown)
                return false;
            // Pha 6: mọi engine còn lại đều chạy qua mover chuẩn (MongoDB survey
            // document thành bảng). Chỉ Unknown bị chặn.
            return true;
        }

        private static bool IsRelational(DatabaseEngine engine) =>
            engine == DatabaseEngine.SqlServer
            || engine == DatabaseEngine.PostgreSql
            || engine == DatabaseEngine.Sqlite
            || engine == DatabaseEngine.MySql
            || engine == DatabaseEngine.MongoDb;

        private static string Describe(DatabaseEngine engine) => engine switch
        {
            DatabaseEngine.SqlServer => "SQL Server",
            DatabaseEngine.PostgreSql => "PostgreSQL",
            DatabaseEngine.MySql => "MySQL/MariaDB",
            DatabaseEngine.Sqlite => "SQLite",
            DatabaseEngine.MongoDb => "MongoDB",
            _ => "chưa xác định"
        };
    }
}
