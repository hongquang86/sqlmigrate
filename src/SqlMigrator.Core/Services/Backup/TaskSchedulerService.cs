using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SqlMigrator.Core.Models;

namespace SqlMigrator.Core.Services.Backup
{
    /// <summary>Kết quả gọi schtasks.exe (không chứa secret).</summary>
    public sealed class SchtasksResult
    {
        public bool Success { get; init; }
        public string Output { get; init; } = string.Empty;
    }

    /// <summary>
    /// Đăng ký/gỡ job backup vào Windows Task Scheduler bằng schtasks.exe có sẵn
    /// (không thêm package). Task chạy <c>"&lt;exe&gt;" --run-backup &lt;jobId&gt;</c>
    /// dưới tài khoản hiện tại nên DPAPI giải mã được.
    /// </summary>
    public sealed class TaskSchedulerService
    {
        public const string TaskFolder = "SqlMigrator";

        /// <summary>Tên task an toàn cho schtasks (bỏ ký tự đặc biệt, giữ chữ/số/gạch/cách).</summary>
        internal static string SafeTaskName(BackupJob job)
        {
            var sb = new StringBuilder();
            foreach (var ch in job.Name.Trim())
            {
                if (char.IsLetterOrDigit(ch) || ch == ' ' || ch == '_' || ch == '-')
                    sb.Append(ch);
            }
            var name = sb.ToString().Trim();
            if (string.IsNullOrWhiteSpace(name))
                name = "Job";
            return TaskFolder + "\\" + name + " [" + job.Id.Substring(0, Math.Min(8, job.Id.Length)) + "]";
        }

        /// <summary>Dựng tham số schtasks /Create cho job (thuần chuỗi để kiểm thử).</summary>
        internal static string BuildCreateArgs(string exePath, BackupJob job)
        {
            var task = SafeTaskName(job);
            var action = "\"\\\"" + exePath + "\\\" --run-backup " + job.Id + "\"";
            var schedule = job.Frequency switch
            {
                BackupJobFrequency.Hourly => "/SC MINUTE /MO " + Math.Clamp(job.IntervalHours, 1, 24) * 60,
                BackupJobFrequency.Weekly => "/SC WEEKLY /D " + DayCode(job.DayOfWeek) + " /ST " + TimeCode(job.TimeOfDay),
                _ => "/SC DAILY /ST " + TimeCode(job.TimeOfDay)
            };
            return $"/Create /TN \"{task}\" /TR {action} {schedule} /F";
        }

        internal static string TimeCode(string timeOfDay)
        {
            if (TimeSpan.TryParse((timeOfDay ?? "").Trim(), out var t))
                return t.ToString(@"hh\:mm");
            return "01:00";
        }

        internal static string DayCode(string day)
        {
            return (day ?? "").Trim().ToUpperInvariant() switch
            {
                "MON" => "MON",
                "TUE" => "TUE",
                "WED" => "WED",
                "THU" => "THU",
                "FRI" => "FRI",
                "SAT" => "SAT",
                _ => "SUN"
            };
        }

        public async Task<SchtasksResult> RegisterAsync(string exePath, BackupJob job, CancellationToken ct = default)
        {
            return await RunAsync("/Create", BuildCreateArgs(exePath, job), ct).ConfigureAwait(false);
        }

        public async Task<SchtasksResult> UnregisterAsync(BackupJob job, CancellationToken ct = default)
        {
            var result = await RunRawAsync(
                $"/Delete /TN \"{SafeTaskName(job)}\" /F", ct).ConfigureAwait(false);
            // Xóa task không tồn tại cũng coi như xong (mã 1 + "không tìm thấy").
            if (!result.Success && result.Output.IndexOf("không", StringComparison.OrdinalIgnoreCase) >= 0)
                return new SchtasksResult { Success = true, Output = result.Output };
            if (!result.Success && result.Output.IndexOf("could not find", StringComparison.OrdinalIgnoreCase) >= 0)
                return new SchtasksResult { Success = true, Output = result.Output };
            return result;
        }

        public async Task<bool> ExistsAsync(BackupJob job, CancellationToken ct = default)
        {
            var result = await RunRawAsync(
                $"/Query /TN \"{SafeTaskName(job)}\"", ct).ConfigureAwait(false);
            return result.Success;
        }

        private static async Task<SchtasksResult> RunAsync(
            string verb, string args, CancellationToken ct)
        {
            return await RunRawAsync(args, ct).ConfigureAwait(false);
        }

        private static async Task<SchtasksResult> RunRawAsync(string args, CancellationToken ct)
        {
            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "schtasks",
                        Arguments = args,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };
                if (!process.Start())
                    return new SchtasksResult { Output = "Không khởi động được schtasks." };
                var output = await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
                var error = await process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
                await process.WaitForExitAsync(ct).ConfigureAwait(false);
                var combined = (output + "\n" + error).Trim();
                return new SchtasksResult { Success = process.ExitCode == 0, Output = combined };
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return new SchtasksResult { Output = ex.Message };
            }
        }
    }
}
