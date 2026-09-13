using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SqlMigrator.Core.Interfaces;
using SqlMigrator.Core.Models;

namespace SqlMigrator.Core.Services.Manage
{
    /// <summary>Kết quả chạy mongodump/mongorestore (lỗi tiếng Việt, không chứa secret).</summary>
    public sealed class MongoDumpResult
    {
        public bool Success { get; init; }
        public TimeSpan Elapsed { get; init; }
        public string? Error { get; init; }
    }

    /// <summary>
    /// Sao lưu/khôi phục MongoDB bằng binary mongodump/mongorestore (đường dẫn do
    /// người dùng cấu hình vì tool không bundle binary). Không bao giờ logfull
    /// command line (chứa mật khẩu) — chỉ log host/database đã che secret.
    /// </summary>
    public sealed class MongoDumpService
    {
        /// <summary>
        /// Sao lưu một database ra thư mục (mongodump --out). binaryPath có thể là
        /// tên file (tìm qua PATH) hoặc đường dẫn đầy đủ tới mongodump(.exe).
        /// </summary>
        public async Task<MongoDumpResult> BackupAsync(
            string binaryPath, DbProbe probe, string dbName, string outDir,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(dbName))
                return new MongoDumpResult { Error = "Chưa chọn database MongoDB cần sao lưu." };
            if (string.IsNullOrWhiteSpace(outDir))
                return new MongoDumpResult { Error = "Chưa chọn thư mục lưu bản sao lưu." };
            var uri = MongoManageProvider.BuildConnectionString(
                new DbProbe
                {
                    Host = probe.Host, Port = probe.Port, User = probe.User,
                    Password = probe.Password, TimeoutSeconds = probe.TimeoutSeconds
                });
            var args = $"--uri=\"{uri}\" --db=\"{dbName}\" --out=\"{outDir}\"";
            return await RunAsync(binaryPath, args,
                $"mongodb://{probe.Host}/{dbName}", ct).ConfigureAwait(false);
        }

        /// <summary>Khôi phục từ thư mục dump (mongorestore --nsInclude db.*).</summary>
        public async Task<MongoDumpResult> RestoreAsync(
            string binaryPath, DbProbe probe, string dbName, string dumpDir,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(dbName))
                return new MongoDumpResult { Error = "Chưa nhập tên database MongoDB đích." };
            if (string.IsNullOrWhiteSpace(dumpDir) || !Directory.Exists(dumpDir))
                return new MongoDumpResult { Error = "Thư mục dump không tồn tại: " + dumpDir };
            var uri = MongoManageProvider.BuildConnectionString(
                new DbProbe
                {
                    Host = probe.Host, Port = probe.Port, User = probe.User,
                    Password = probe.Password, TimeoutSeconds = probe.TimeoutSeconds
                });
            var args = $"--uri=\"{uri}\" --nsInclude=\"{dbName}.*\" \"{dumpDir}\"";
            return await RunAsync(binaryPath, args,
                $"mongodb://{probe.Host}/{dbName}", ct).ConfigureAwait(false);
        }

        private static async Task<MongoDumpResult> RunAsync(
            string binaryPath, string args, string safeTarget, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            var exe = (binaryPath ?? "").Trim();
            if (string.IsNullOrWhiteSpace(exe))
                return new MongoDumpResult { Error = "Chưa cấu hình đường dẫn binary mongodump/mongorestore." };
            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = exe,
                        Arguments = args,
                        UseShellExecute = false,
                        RedirectStandardError = true,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true
                    }
                };
                // Chỉ log mục tiêu đã che secret, KHÔNG log args (chứa mật khẩu).
                // (Log do tầng gọi ghi; ở đây không giữ logger để tránh rò rỉ.)
                var stderr = new StringBuilder();
                process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };
                if (!process.Start())
                    return new MongoDumpResult { Elapsed = sw.Elapsed, Error = "Không khởi động được " + exe };
                process.BeginErrorReadLine();
                await process.WaitForExitAsync(ct).ConfigureAwait(false);
                sw.Stop();
                if (process.ExitCode != 0)
                {
                    var tail = stderr.ToString();
                    if (tail.Length > 2000)
                        tail = tail.Substring(tail.Length - 2000);
                    return new MongoDumpResult
                    {
                        Elapsed = sw.Elapsed,
                        Error = $"Tool báo lỗi (mục tiêu {safeTarget}, mã {process.ExitCode}): " + tail.Trim()
                    };
                }
                return new MongoDumpResult { Success = true, Elapsed = sw.Elapsed };
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                sw.Stop();
                return new MongoDumpResult { Elapsed = sw.Elapsed, Error = ex.Message };
            }
        }
    }
}
