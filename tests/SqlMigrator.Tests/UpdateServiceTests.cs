using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using SqlMigrator.Core.Services;
using Xunit;

namespace SqlMigrator.Tests;

public class UpdateServiceTests
{
    private const string ReleaseJson = @"{
  ""tag_name"": ""v1.2.0"",
  ""body"": ""Sửa lỗi X"",
  ""assets"": [
    { ""name"": ""SQLMigrator_ClickToRun.zip"", ""browser_download_url"": ""https://example.com/zip"" },
    { ""name"": ""SQLMigrator_Setup_1.2.0.exe"", ""browser_download_url"": ""https://example.com/setup.exe"" }
  ]
}";

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly string _json;
        private readonly HttpStatusCode _status;
        public StubHandler(string json, HttpStatusCode status = HttpStatusCode.OK)
        {
            _json = json;
            _status = status;
        }
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // GitHub API bắt buộc User-Agent — updater phải luôn gửi.
            Assert.True(request.Headers.Contains("User-Agent"));
            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_json)
            });
        }
    }

    [Fact]
    public async Task CheckForUpdate_NewerRelease_PicksSetupExe()
    {
        using var svc = new UpdateService(new HttpClient(new StubHandler(ReleaseJson)));

        var info = await svc.CheckForUpdateAsync(new Version(1, 1, 0));

        Assert.NotNull(info);
        Assert.True(info!.Available);
        Assert.Equal(new Version(1, 2, 0), info.LatestVersion);
        Assert.True(info.IsInstaller);
        Assert.Equal("https://example.com/setup.exe", info.DownloadUrl);
        Assert.Contains("Sửa lỗi X", info.ReleaseNotes);
    }

    [Fact]
    public async Task CheckForUpdate_SameVersion_NotAvailable()
    {
        using var svc = new UpdateService(new HttpClient(new StubHandler(ReleaseJson)));

        var info = await svc.CheckForUpdateAsync(new Version(1, 2, 0));

        Assert.NotNull(info);
        Assert.False(info!.Available);
    }

    [Fact]
    public async Task CheckForUpdate_ApiError_ReturnsNull()
    {
        using var svc = new UpdateService(new HttpClient(
            new StubHandler("{}", HttpStatusCode.NotFound)));

        Assert.Null(await svc.CheckForUpdateAsync(new Version(1, 0, 0)));
    }

    [Fact]
    public async Task CheckForUpdate_ZipOnlyRelease_PicksZip()
    {
        const string json = @"{ ""tag_name"": ""2.0.0"", ""assets"": [
            { ""name"": ""SQLMigrator_ClickToRun.zip"", ""browser_download_url"": ""https://example.com/z"" } ] }";
        using var svc = new UpdateService(new HttpClient(new StubHandler(json)));

        var info = await svc.CheckForUpdateAsync(new Version(1, 9, 9));

        Assert.NotNull(info);
        Assert.True(info!.Available);
        Assert.False(info.IsInstaller);
        Assert.Equal("https://example.com/z", info.DownloadUrl);
    }

    [Fact]
    public async Task CheckForUpdate_PreferZip_PicksZipOverSetup()
    {
        using var svc = new UpdateService(new HttpClient(new StubHandler(ReleaseJson)));

        var info = await svc.CheckForUpdateAsync(new Version(1, 1, 0), preferInstaller: false);

        Assert.NotNull(info);
        Assert.True(info!.Available);
        Assert.False(info.IsInstaller);
        Assert.Equal("https://example.com/zip", info.DownloadUrl);
    }

    [Fact]
    public async Task CheckForUpdate_PreferInstallerWithoutSetup_ReturnsNotAvailable()
    {
        const string json = @"{ ""tag_name"": ""2.0.0"", ""assets"": [
            { ""name"": ""SQLMigrator_ClickToRun.zip"", ""browser_download_url"": ""https://example.com/z"" } ] }";
        using var svc = new UpdateService(new HttpClient(new StubHandler(json)));

        var info = await svc.CheckForUpdateAsync(new Version(1, 9, 9), preferInstaller: true);

        Assert.NotNull(info);
        Assert.False(info!.Available);
    }

    [Theory]
    [InlineData("v1.2.3", 1, 2, 3)]
    [InlineData("1.2.3", 1, 2, 3)]
    [InlineData("v2.0.0-beta", 2, 0, 0)]
    public void TryParseVersion_AcceptsCommonTags(string tag, int major, int minor, int build)
    {
        Assert.True(UpdateService.TryParseVersion(tag, out var v));
        Assert.Equal(new Version(major, minor, build), v);
    }

    [Theory]
    [InlineData("")]
    [InlineData("latest")]
    public void TryParseVersion_RejectsGarbage(string tag)
    {
        Assert.False(UpdateService.TryParseVersion(tag, out _));
    }
}
