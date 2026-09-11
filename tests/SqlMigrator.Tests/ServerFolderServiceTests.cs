using SqlMigrator.Core.Services;
using Xunit;

namespace SqlMigrator.Tests;

/// <summary>
/// Kiểm thử logic thuần của trình duyệt thư mục trên server (ServerFolderService).
/// Phần gọi SQL thật không test được trong môi trường đơn vị nên chỉ test phần parse/chuẩn hoá.
/// </summary>
public class ServerFolderServiceTests
{
    [Theory]
    [InlineData("C", "C:\\")]
    [InlineData("D", "D:\\")]
    [InlineData(" E ", "E:\\")]
    public void NormalizeDrive_ChuẩnHoáỔĐĩa(string raw, string expected)
    {
        Assert.Equal(expected, ServerFolderService.NormalizeDrive(raw));
    }

    [Fact]
    public void NormalizeDrive_ChuỗiTrốngTrảVềTrống()
    {
        Assert.Equal(string.Empty, ServerFolderService.NormalizeDrive(""));
        Assert.Equal(string.Empty, ServerFolderService.NormalizeDrive("   "));
        Assert.Equal(string.Empty, ServerFolderService.NormalizeDrive(null!));
    }

    [Fact]
    public void NormalizeFolders_BỏÔTrốngVàSắpXếpTheoTên()
    {
        // Thứ tự về trước phải được sắp xếp lại (không phụ thuộc thứ tự trả về của server).
        var result = ServerFolderService.NormalizeFolders(new[] { "  ", "DATA", "LOG", "", "Program Files" });

        Assert.Equal(new[] { "DATA", "LOG", "Program Files" }, result);
    }

    [Fact]
    public void NormalizeFolders_ChuỗiTrốngTrảVềDanhSáchRỗng()
    {
        var result = ServerFolderService.NormalizeFolders(Array.Empty<string>());
        Assert.Empty(result);
    }
}