using SqlMigrator.Core.Services;
using Xunit;

namespace SqlMigrator.Tests;

public class AmbiguousColumnResolverTests
{
    [Fact]
    public void FindBareOccurrences_QualifiedAndString_Ignored()
    {
        var sql = "SELECT Active, a.Active, [b].[Active], 'Active' FROM dbo.A a JOIN dbo.B b ON 1=1";

        var positions = AmbiguousColumnResolver.FindBareOccurrences(sql, "Active");

        // Chỉ lần đầu (trần) được bắt; a.Active, [b].[Active] và 'Active' bị loại.
        Assert.Single(positions);
        Assert.Equal(7, positions[0]);
    }

    [Fact]
    public void FindBareOccurrences_BracketedBare_Matched()
    {
        var positions = AmbiguousColumnResolver.FindBareOccurrences(
            "SELECT [Active], Name FROM dbo.T", "Active");

        Assert.Single(positions);
    }

    [Fact]
    public void FindBareOccurrences_LongerName_NotMatched()
    {
        var positions = AmbiguousColumnResolver.FindBareOccurrences(
            "SELECT IsActive, Active2 FROM dbo.T", "Active");

        Assert.Empty(positions);
    }

    [Fact]
    public void QualifyOccurrences_ReplacesBareKeepsQualified()
    {
        var result = AmbiguousColumnResolver.QualifyOccurrences(
            "SELECT Active, a.Active FROM dbo.A a", "Active", "b");

        Assert.Equal("SELECT [b].[Active], a.Active FROM dbo.A a", result);
    }

    [Fact]
    public void QualifyOccurrences_NoMatch_ReturnsOriginal()
    {
        const string sql = "SELECT Id FROM dbo.T";
        Assert.Equal(sql, AmbiguousColumnResolver.QualifyOccurrences(sql, "Active", "a"));
    }

    [Fact]
    public void ExtractViewBody_StripsHeadersAndCheckOption()
    {
        const string script = "SET ANSI_NULLS ON\nGO\nSET QUOTED_IDENTIFIER ON\nGO\n"
            + "CREATE VIEW [dbo].[V] AS\nSELECT Active FROM dbo.A WITH CHECK OPTION";

        Assert.Equal("SELECT Active FROM dbo.A", AmbiguousColumnResolver.ExtractViewBody(script));
    }

    [Fact]
    public void ExtractViewBody_NoCreateView_ReturnsNull()
    {
        Assert.Null(AmbiguousColumnResolver.ExtractViewBody("SELECT 1"));
        Assert.Null(AmbiguousColumnResolver.ExtractViewBody(""));
    }
}
