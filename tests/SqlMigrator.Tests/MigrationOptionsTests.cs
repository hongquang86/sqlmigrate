using SqlMigrator.Core.Models;
using Xunit;

namespace SqlMigrator.Tests;

/// <summary>Kiểm thử phép kiểm duyệt tham số <see cref="MigrationOptions"/>.</summary>
public class MigrationOptionsTests
{
    [Fact]
    public void Validate_KhiThiếuChuỗiKếtNốiNguồnPhảiNém()
    {
        var options = new MigrationOptions
        {
            DestinationConnectionString = "Data Source=.;Initial Catalog=dest;"
        };

        var ex = Assert.Throws<InvalidOperationException>(() => options.Validate());
        Assert.Contains("SourceConnectionString", ex.Message);
    }

    [Fact]
    public void Validate_KhiThiếuChuỗiKếtNốiĐíchPhảiNém()
    {
        var options = new MigrationOptions
        {
            SourceConnectionString = "Data Source=.;Initial Catalog=src;"
        };

        var ex = Assert.Throws<InvalidOperationException>(() => options.Validate());
        Assert.Contains("DestinationConnectionString", ex.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-100)]
    public void Validate_KhiBatchSizeKhôngHợpLệPhảiNém(int batchSize)
    {
        var options = new MigrationOptions
        {
            SourceConnectionString = "Data Source=.;Initial Catalog=src;",
            DestinationConnectionString = "Data Source=.;Initial Catalog=dest;",
            BatchSize = batchSize
        };

        Assert.Throws<InvalidOperationException>(() => options.Validate());
    }

    [Fact]
    public void Validate_TùyChọnHợpLệKhôngNém()
    {
        var options = new MigrationOptions
        {
            SourceConnectionString = "Data Source=.;Initial Catalog=src;",
            DestinationConnectionString = "Data Source=.;Initial Catalog=dest;"
        };

        var ex = Record.Exception(() => options.Validate());
        Assert.Null(ex);
    }

    [Fact]
    public void Validate_CommandTimeoutKhôngĐượcNhỏHơnHoặcBằng0()
    {
        var options = new MigrationOptions
        {
            SourceConnectionString = "Data Source=.;Initial Catalog=src;",
            DestinationConnectionString = "Data Source=.;Initial Catalog=dest;",
            CommandTimeoutSeconds = 0
        };

        Assert.Throws<InvalidOperationException>(() => options.Validate());
    }

    [Fact]
    public void GiáTrịMặcĐịnhĐượcĐặtĐúng()
    {
        var options = new MigrationOptions();
        Assert.Equal(MigrationMode.Full, options.Mode);
        Assert.Equal(5000, options.BatchSize);
        Assert.True(options.DisableForeignKeyConstraints);
        Assert.True(options.CheckDataAfterLoad);
        Assert.True(options.EnableStreaming);
        Assert.Equal(600, options.CommandTimeoutSeconds);
    }
}