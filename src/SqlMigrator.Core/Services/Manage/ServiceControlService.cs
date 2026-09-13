using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SqlMigrator.Core.Services.Manage
{
    /// <summary>Trạng thái service Windows (mã số sc.exe).</summary>
    public enum WindowsServiceState
    {
        Unknown,
        Stopped,
        StartPending,
        StopPending,
        Running,
        Paused
    }

    public sealed class WindowsServiceInfo
    {
        public string Name { get; init; } = string.Empty;
        public string DisplayName { get; init; } = string.Empty;
        public WindowsServiceState State { get; init; }
    }

    /// <summary>Kết quả lệnh start/stop (không chứa secret).</summary>
    public sealed class ServiceCommandResult
    {
        public bool Success { get; init; }
        public string Output { get; init; } = string.Empty;
    }

    /// <summary>
    /// Start/Stop/Restart service database bằng sc.exe có sẵn của Windows (không thêm
    /// package, build offline được). Máy trống = local; "maychu" hoặc "\\maychu" = remote
    /// (cần quyền admin trên máy đó). Lỗi thiếu quyền hiện nguyên văn để dễ chẩn đoán.
    /// </summary>
    public sealed class ServiceControlService
    {
        /// <summary>Tên service phổ biến để gợi ý trong combobox (người dùng vẫn gõ tay được).</summary>
        public static IReadOnlyList<(string Name, string Label)> KnownServices { get; } =
            new List<(string, string)>
            {
                ("MSSQLSERVER", "SQL Server (mặc định)"),
                ("MSSQL$SQLEXPRESS", "SQL Server Express"),
                ("SQLSERVERAGENT", "SQL Server Agent"),
                ("SQLBrowser", "SQL Server Browser"),
                ("MySQL80", "MySQL 8.0"),
                ("MySQL", "MySQL (tên chung)"),
                ("postgresql-x64-15", "PostgreSQL 15"),
                ("postgresql-x64-16", "PostgreSQL 16"),
                ("MongoDB", "MongoDB")
            }.AsReadOnly();

        internal static string MachineArg(string machine)
        {
            var m = (machine ?? "").Trim().TrimStart('\\');
            return string.IsNullOrWhiteSpace(m) || m is "." or "localhost" ? "" : @"\\" + m;
        }

        /// <summary>Phân tích STATE từ output `sc query` (dạng "STATE : 4 RUNNING"). Thuần để kiểm thử.</summary>
        internal static WindowsServiceState ParseState(string scOutput)
        {
            var m = Regex.Match(scOutput ?? "", @"STATE\s*:\s*(\d+)", RegexOptions.IgnoreCase);
            if (!m.Success || !int.TryParse(m.Groups[1].Value, out var code))
                return WindowsServiceState.Unknown;
            return code switch
            {
                1 => WindowsServiceState.Stopped,
                2 => WindowsServiceState.StartPending,
                3 => WindowsServiceState.StopPending,
                4 => WindowsServiceState.Running,
                7 => WindowsServiceState.Paused,
                _ => WindowsServiceState.Unknown
            };
        }

        internal static string ParseDisplayName(string scOutput, string fallback)
        {
            var m = Regex.Match(scOutput ?? "", @"DISPLAY_NAME\s*:\s*(.+)", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value.Trim() : fallback;
        }

        public async Task<WindowsServiceInfo> QueryAsync(
            string machine, string serviceName, CancellationToken ct = default)
        {
            ValidateName(serviceName);
            var result = await RunScAsync(MachineArg(machine), "query", QuoteName(serviceName), ct)
                .ConfigureAwait(false);
            if (!result.Success)
                return new WindowsServiceInfo { Name = serviceName, State = WindowsServiceState.Unknown };
            return new WindowsServiceInfo
            {
                Name = serviceName,
                DisplayName = ParseDisplayName(result.Output, serviceName),
                State = ParseState(result.Output)
            };
        }

        public async Task<ServiceCommandResult> StartAsync(
            string machine, string serviceName, CancellationToken ct = default)
        {
            ValidateName(serviceName);
            return await RunScAsync(MachineArg(machine), "start", QuoteName(serviceName), ct)
                .ConfigureAwait(false);
        }

        public async Task<ServiceCommandResult> StopAsync(
            string machine, string serviceName, CancellationToken ct = default)
        {
            ValidateName(serviceName);
            return await RunScAsync(MachineArg(machine), "stop", QuoteName(serviceName), ct)
                .ConfigureAwait(false);
        }

        /// <summary>Restart = stop, chờ tới 60s, rồi start.</summary>
        public async Task<ServiceCommandResult> RestartAsync(
            string machine, string serviceName, CancellationToken ct = default)
        {
            ValidateName(serviceName);
            var stop = await StopAsync(machine, serviceName, ct).ConfigureAwait(false);
            if (!stop.Success)
                return stop;
            for (var i = 0; i < 60; i++)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(1000, ct).ConfigureAwait(false);
                var state = (await QueryAsync(machine, serviceName, ct).ConfigureAwait(false)).State;
                if (state == WindowsServiceState.Stopped)
                    break;
            }
            return await StartAsync(machine, serviceName, ct).ConfigureAwait(false);
        }

        private static void ValidateName(string serviceName)
        {
            if (string.IsNullOrWhiteSpace(serviceName))
                throw new ArgumentException("Chưa nhập tên service.", nameof(serviceName));
            // Chặn ký tự nguy hiểm vì tên ghép vào lệnh sc (dù đã bọc ngoặc kép).
            if (serviceName.IndexOfAny(new[] { '&', '|', ';', '<', '>', '^', '%', '!', '`', '$' }) >= 0)
                throw new ArgumentException("Tên service chứa ký tự không hợp lệ.", nameof(serviceName));
        }

        private static string QuoteName(string serviceName) => "\"" + serviceName.Trim() + "\"";

        private static async Task<ServiceCommandResult> RunScAsync(
            string machineArg, string verb, string quotedName, CancellationToken ct)
        {
            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "sc",
                        Arguments = string.IsNullOrEmpty(machineArg)
                            ? $"{verb} {quotedName}"
                            : $"{machineArg} {verb} {quotedName}",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };
                if (!process.Start())
                    return new ServiceCommandResult { Output = "Không khởi động được sc.exe." };
                var output = await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
                var error = await process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
                await process.WaitForExitAsync(ct).ConfigureAwait(false);
                var combined = (output + "\n" + error).Trim();
                return new ServiceCommandResult { Success = process.ExitCode == 0, Output = combined };
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return new ServiceCommandResult { Output = ex.Message };
            }
        }
    }
}
