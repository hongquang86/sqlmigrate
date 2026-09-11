using SqlMigrator.Core.Services;
using Xunit;

namespace SqlMigrator.Tests;

/// <summary>
/// Kiểm thử việc bọc tên đối tượng an toàn (tương đương QUOTENAME).
/// Chú trọng trường hợp tên chứa ký tự đặc biệt như dấu đóng ngoặc vuông.
/// </summary>
public class QuotingTests
{
    [Theory]
    [InlineData("dbo", "[dbo]")]
    [InlineData("Orders", "[Orders]")]
    [InlineData("Café Order", "[Café Order]")]
    public void QuoteIdentifier_BọcTênThôngThường(string input, string expected)
    {
        Assert.Equal(expected, Quoting.QuoteIdentifier(input));
    }

    [Fact]
    public void QuoteIdentifier_NhânĐôiDấuNgoặcVuôngTrongTên()
    {
        // Tên "weird]name" phải thành "[weird]]name]" để không phá vỡ câu SQL.
        Assert.Equal("[weird]]name]", Quoting.QuoteIdentifier("weird]name"));
    }

    [Fact]
    public void QuoteIdentifier_ChuỗiRỗngPhảiNém()
    {
        Assert.Throws<System.ArgumentException>(() => Quoting.QuoteIdentifier(string.Empty));
        Assert.Throws<System.ArgumentException>(() => Quoting.QuoteIdentifier(null!));
    }

    [Fact]
    public void QuoteQualifiedName_CóSchemaBọcCảHaiPhần()
    {
        Assert.Equal("[dbo].[Orders]", Quoting.QuoteQualifiedName("dbo", "Orders"));
    }

    [Fact]
    public void QuoteQualifiedName_KhôngSchemaChỉBọcTên()
    {
        Assert.Equal("[PK_Orders]", Quoting.QuoteQualifiedName(null, "PK_Orders"));
    }

    [Fact]
    public void QuoteQualifiedName_SchemaRỗngChỉBọcTên()
    {
        Assert.Equal("[Orders]", Quoting.QuoteQualifiedName(string.Empty, "Orders"));
    }

    [Theory]
    [InlineData("A connection was successfully established but the certificate chain was issued by an authority that is not trusted.")]
    [InlineData(".Net SqlClient Data Provider: SSL Provider, error: 0 - The certificate chain was issued by an authority that is not trusted.")]
    [InlineData("The server certificate was not accepted. Check that TrustServerCertificate is set correctly.")]
    public void IsCertificateTrustError_NhậnDiệnLỗiChứngChỉ(string message)
    {
        Assert.True(SqlConnectionFactory.IsCertificateTrustError(new System.Exception(message)));
    }

    [Theory]
    [InlineData("A TLS 1.2 connection could not be negotiated because the server does not support it.")]
    [InlineData("SSL Security error - The client and server cannot communicate, because they do not possess a common algorithm.")]
    public void IsLegacyTlsError_NhậnDiệnLỗiTlsServerCũ(string message)
    {
        Assert.True(SqlConnectionFactory.IsLegacyTlsError(new System.Exception(message)));
    }

    [Theory]
    [InlineData("Invalid object name 'dbo.Products'.")]
    [InlineData("Cannot open database \"X\" requested by the login. The login failed.")]
    public void IsCertificateTrustError_KhôngNhậnNhầmLỗiKhác(string message)
    {
        Assert.False(SqlConnectionFactory.IsCertificateTrustError(new System.Exception(message)));
    }
}