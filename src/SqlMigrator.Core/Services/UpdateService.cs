using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace SqlMigrator.Core.Services
{
    /// <summary>Thông tin bản phát hành mới nhất trên GitHub.</summary>
    public sealed class UpdateInfo
    {
        public bool Available { get; init; }
        public Version LatestVersion { get; init; } = new Version(0, 0);
        public string TagName { get; init; } = string.Empty;
        /// <summary>URL tải trực tiếp asset cài đặt (setup exe hoặc zip portable).</summary>
        public string DownloadUrl { get; init; } = string.Empty;
        public string FileName { get; init; } = string.Empty;
        /// <summary>True khi asset là bộ cài đặt (exe), false khi là zip portable.</summary>
        public bool IsInstaller { get; init; }
        public string ReleaseNotes { get; init; } = string.Empty;
    }

    /// <summary>
    /// Kiểm tra + tải bản cập nhật từ GitHub Releases (repo công khai, không cần token).
    /// Chỉ ĐỌC từ mạng về máy local; không gửi bất cứ thứ gì đi ngoài request GET.
    /// </summary>
    public sealed class UpdateService : IDisposable
    {
        public const string DefaultOwner = "hongquang86";
        public const string DefaultRepo = "sqlmigrate";

        private readonly HttpClient _http;
        private readonly bool _ownHttp;
        private readonly string _owner;
        private readonly string _repo;
        private readonly ILogger _logger;
        private bool _disposed;

        public UpdateService(
            HttpClient? http = null,
            string owner = DefaultOwner,
            string repo = DefaultRepo,
            ILogger? logger = null)
        {
            _owner = string.IsNullOrWhiteSpace(owner) ? DefaultOwner : owner.Trim();
            _repo = string.IsNullOrWhiteSpace(repo) ? DefaultRepo : repo.Trim();
            _logger = logger ?? NullLogger.Instance;
            if (http != null)
            {
                _http = http;
                _ownHttp = false;
            }
            else
            {
                _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                _ownHttp = true;
            }
        }

        /// <summary>
        /// Hỏi GitHub release mới nhất và so với phiên bản đang chạy.
        /// Trả null khi không kiểm tra được (mất mạng, repo riêng, API đổi...).
        /// preferInstaller: null = ưu tiên setup exe; true = chỉ setup; false = chỉ zip.
        /// </summary>
        public async Task<UpdateInfo?> CheckForUpdateAsync(
            Version current, bool? preferInstaller = null, CancellationToken ct = default)
        {
            if (current == null)
                throw new ArgumentNullException(nameof(current));

            string url = $"https://api.github.com/repos/{_owner}/{_repo}/releases/latest";
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                // GitHub API bắt buộc có User-Agent.
                request.Headers.TryAddWithoutValidation("User-Agent", "SqlMigrator-Updater");
                request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");

                using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Kiểm tra cập nhật thất bại (HTTP {Status}).", (int)response.StatusCode);
                    return null;
                }

                var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                return ParseRelease(json, current, preferInstaller);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Không kiểm tra được cập nhật ({Message}).", ex.Message);
                return null;
            }
        }

        /// <summary>Tải file cập nhật về đĩa, báo tiến độ % (0-100). Ghi file local.</summary>
        public async Task DownloadAsync(
            string downloadUrl, string destPath, IProgress<double>? progress = null,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(downloadUrl))
                throw new ArgumentException("Thiếu URL tải.", nameof(downloadUrl));
            if (string.IsNullOrWhiteSpace(destPath))
                throw new ArgumentException("Thiếu đường dẫn đích.", nameof(destPath));

            using var request = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
            request.Headers.TryAddWithoutValidation("User-Agent", "SqlMigrator-Updater");
            using var response = await _http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength;
            var dir = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            using var net = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var file = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 81920, useAsync: true);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await net.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) > 0)
            {
                await file.WriteAsync(buffer, 0, read, ct).ConfigureAwait(false);
                done += read;
                if (total.HasValue && total.Value > 0)
                    progress?.Report(done * 100.0 / total.Value);
            }
            progress?.Report(100);
        }

        /// <summary>
        /// Phân tích JSON release GitHub. Public để kiểm thử (không cần mạng).
        /// Quy ước asset: mặc định ưu tiên file setup (*Setup*.exe), rồi đến zip portable.
        /// preferInstaller true/false ép chỉ lấy đúng loại đó.
        /// </summary>
        public static UpdateInfo? ParseRelease(string json, Version current, bool? preferInstaller = null)
        {
            if (string.IsNullOrWhiteSpace(json) || current == null)
                return null;

            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (!root.TryGetProperty("tag_name", out var tagProp))
                    return null;
                var tag = tagProp.GetString() ?? "";
                if (!TryParseVersion(tag, out var latest) || latest == null)
                    return null;
                if (latest <= current)
                    return new UpdateInfo { Available = false, LatestVersion = latest, TagName = tag };

                string notes = "";
                if (root.TryGetProperty("body", out var bodyProp))
                    notes = bodyProp.GetString() ?? "";

                // Chọn asset: setup exe trước, zip portable sau.
                string url = "";
                string file = "";
                var isInstaller = false;
                if (root.TryGetProperty("assets", out var assetsProp)
                    && assetsProp.ValueKind == JsonValueKind.Array)
                {
                    var candidates = new List<(string Name, string Url)>();
                    foreach (var a in assetsProp.EnumerateArray())
                    {
                        if (!a.TryGetProperty("name", out var n) ||
                            !a.TryGetProperty("browser_download_url", out var u))
                            continue;
                        candidates.Add((n.GetString() ?? "", u.GetString() ?? ""));
                    }

                    var setup = candidates.FirstOrDefault(c =>
                        c.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                        && c.Name.IndexOf("setup", StringComparison.OrdinalIgnoreCase) >= 0);
                    var zip = candidates.FirstOrDefault(c =>
                        c.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));

                    // preferInstaller: true = chỉ setup, false = chỉ zip, null = setup trước zip sau.
                    if (preferInstaller != false && !string.IsNullOrEmpty(setup.Url))
                    {
                        url = setup.Url;
                        file = setup.Name;
                        isInstaller = true;
                    }
                    else if (preferInstaller != true && !string.IsNullOrEmpty(zip.Url))
                    {
                        url = zip.Url;
                        file = zip.Name;
                        isInstaller = false;
                    }
                }

                if (string.IsNullOrEmpty(url))
                    return new UpdateInfo { Available = false, LatestVersion = latest, TagName = tag };

                return new UpdateInfo
                {
                    Available = true,
                    LatestVersion = latest,
                    TagName = tag,
                    DownloadUrl = url,
                    FileName = file,
                    IsInstaller = isInstaller,
                    ReleaseNotes = notes ?? ""
                };
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>Chấp nhận tag "v1.2.3" hoặc "1.2.3" (bỏ hậu tố -beta...).</summary>
        public static bool TryParseVersion(string tag, out Version? version)
        {
            version = null;
            if (string.IsNullOrWhiteSpace(tag))
                return false;
            var t = tag.Trim().TrimStart('v', 'V');
            var dash = t.IndexOfAny(new[] { '-', '+' });
            if (dash > 0)
                t = t.Substring(0, dash);
            if (Version.TryParse(t, out var v))
            {
                version = v;
                return true;
            }
            return false;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            if (_ownHttp)
                _http.Dispose();
        }
    }
}
