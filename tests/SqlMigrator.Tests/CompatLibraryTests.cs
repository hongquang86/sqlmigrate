using System;
using SqlMigrator.Core.Services;
using Xunit;

namespace SqlMigrator.Tests;

public class CompatLibraryTests
{
    [Theory]
    [InlineData("Json")]
    [InlineData("Date")]
    [InlineData("Session")]
    [InlineData("TimeZone")]
    public void GetShimSql_KnownShim_LoadsNonEmptyScript(string shimId)
    {
        var sql = CompatLibrary.GetShimSql(shimId);

        Assert.False(string.IsNullOrWhiteSpace(sql));
        Assert.Contains("CREATE", sql);
        Assert.Contains("Compat.", sql);
    }

    [Fact]
    public void GetShimSql_UnknownShim_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => CompatLibrary.GetShimSql("Nope"));
    }

    [Fact]
    public void JsonShim_ContainsAllPublicFunctions()
    {
        var sql = CompatLibrary.GetShimSql("Json");

        Assert.Contains("Compat.JsonValue", sql);
        Assert.Contains("Compat.JsonQuery", sql);
        Assert.Contains("Compat.IsJson", sql);
        Assert.Contains("Compat.OpenJson", sql);
        Assert.Contains("Compat.JsonEscape", sql);
    }

    [Fact]
    public void ShimScripts_UseOnlyLegacySyntax()
    {
        // Shim phải chạy được trên SQL 2014 nên không được chứa cú pháp 2016+.
        foreach (var shim in CompatLibrary.Shims)
        {
            var sql = CompatLibrary.GetShimSql(shim.Id);
            Assert.DoesNotContain("STRING_SPLIT(", sql);
            Assert.DoesNotContain("DROP IF EXISTS", sql);
            Assert.DoesNotContain("CREATE OR ALTER", sql);
        }
    }
}
