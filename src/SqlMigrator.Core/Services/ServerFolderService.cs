using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;

namespace SqlMigrator.Core.Services
{
    /// <summary>Kết quả truy vấn thư mục mặc định chứa file database của SQL Server.</summary>
    public sealed class ServerFolderDefaults
    {
        /// <summary>Đường dẫn mặc định cho file dữ liệu (.mdf) đăng ký lúc cài đặt.</summary>
        public string? DataPath { get; init; }

        /// <summary>Đường dẫn mặc định cho file log (.ldf) đăng ký lúc cài đặt.</summary>
        public string? LogPath { get; init; }

        public bool IsEmpty => string.IsNullOrWhiteSpace(DataPath) && string.IsNullOrWhiteSpace(LogPath);
    }

    /// <summary>
    /// Liệt kê ổ đĩa / thư mục NGAY TRÊN SQL Server (qua kết nối đã có, không cần chia sẻ
    /// mạng SMB). Dùng <c>xp_fixeddrives</c> cho ổ đĩa và <c>xp_dirtree</c> cho thư mục con;
    /// toàn bộ đường dẫn được truyền bằng tham số (không nối chuỗi) để chống SQL injection.
    /// Không bao giờ log chuỗi kết nối, mật khẩu hoặc đường dẫn thật.
    /// </summary>
    public interface IServerFolderLister
    {
        /// <summary>Trả về thư mục mặc định cho data (.mdf) và log (.ldf) của server, hoặc null nếu không đọc được.</summary>
        Task<ServerFolderDefaults?> GetDefaultPathsAsync(string connectionString, CancellationToken ct = default);

        /// <summary>Danh sách ổ đĩa trên server (vd ["C:\", "D:\"]).</summary>
        Task<IReadOnlyList<string>> GetDrivesAsync(string connectionString, CancellationToken ct = default);

        /// <summary>Danh sách thư mục con (1 cấp) của <paramref name="path"/> trên server.</summary>
        Task<IReadOnlyList<string>> GetChildFoldersAsync(string connectionString, string path, CancellationToken ct = default);
    }

    /// <inheritdoc cref="IServerFolderLister"/>
    /// <remarks>
    /// Luôn chuyển Initial Catalog về "master" vì <c>xp_dirtree</c> / <c>xp_fixeddrives</c> là
    /// thủ tục hệ thống trong master; mở kết nối bằng auto-fallback (cert tự ký / SQL cũ thiếu TLS).
    /// </remarks>
    public sealed class ServerFolderService : IServerFolderLister
    {
        private readonly Action<string>? _log;

        public ServerFolderService(Action<string>? log = null)
        {
            _log = log;
        }

        public async Task<ServerFolderDefaults?> GetDefaultPathsAsync(
            string connectionString, CancellationToken ct = default)
        {
            const string query =
                "SELECT SERVERPROPERTY('InstanceDefaultDataPath') AS dataPath, " +
                       "SERVERPROPERTY('InstanceDefaultLogPath') AS logPath;";

            using var conn = SqlConnectionFactory.OpenWithFallback(
                ToMaster(connectionString), 30, _log, ct);
            using var cmd = new SqlCommand(query, conn) { CommandTimeout = 30 };
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                return null;

            return new ServerFolderDefaults
            {
                DataPath = reader.IsDBNull(0) ? null : reader.GetString(0).Trim(),
                LogPath = reader.IsDBNull(1) ? null : reader.GetString(1).Trim()
            };
        }

        public async Task<IReadOnlyList<string>> GetDrivesAsync(
            string connectionString, CancellationToken ct = default)
        {
            const string query = "EXEC master.dbo.xp_fixeddrives;";
            var result = new List<string>();

            using var conn = SqlConnectionFactory.OpenWithFallback(
                ToMaster(connectionString), 30, _log, ct);
            using var cmd = new SqlCommand(query, conn) { CommandTimeout = 30 };
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                result.Add(NormalizeDrive(reader.GetString(0)));

            return result;
        }

        public async Task<IReadOnlyList<string>> GetChildFoldersAsync(
            string connectionString, string path, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Chưa có đường dẫn để duyệt thư mục.", nameof(path));

            const string query = "EXEC master.dbo.xp_dirtree @path, 1, 0;";
            var result = new List<string>();

            using var conn = SqlConnectionFactory.OpenWithFallback(
                ToMaster(connectionString), 30, _log, ct);
            using var cmd = new SqlCommand(query, conn) { CommandTimeout = 30 };
            cmd.Parameters.AddWithValue("@path", path);
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var sub = reader.GetString(0);
                if (!string.IsNullOrWhiteSpace(sub))
                    result.Add(sub);
            }

            return NormalizeFolders(result);
        }

        /// <summary>Đưa kết nối về database master để chạy thủ tục hệ thống.</summary>
        private static string ToMaster(string connectionString)
        {
            return new SqlConnectionStringBuilder(connectionString)
            {
                InitialCatalog = "master",
                MultipleActiveResultSets = false
            }.ConnectionString;
        }

        /// <summary>Chuẩn hoá một ổ đĩa thô từ <c>xp_fixeddrives</c> (vd "C") thành "C:\".</summary>
        public static string NormalizeDrive(string raw)
        {
            var drive = (raw ?? string.Empty).Trim();
            return drive.Length == 0 ? string.Empty : drive + ":\\";
        }

        /// <summary>Lọc danh sách thư mục con: luôn bỏ ô trống, sắp xếp theo tên.</summary>
        public static IReadOnlyList<string> NormalizeFolders(IEnumerable<string> rows)
        {
            var list = new List<string>();
            foreach (var row in rows)
            {
                var name = (row ?? string.Empty).Trim();
                if (name.Length > 0) list.Add(name);
            }

            list.Sort(StringComparer.OrdinalIgnoreCase);
            return list;
        }
    }
}