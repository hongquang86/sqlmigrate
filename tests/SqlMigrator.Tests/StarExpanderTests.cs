using System.Collections.Generic;
using SqlMigrator.Core.Services;
using Xunit;

namespace SqlMigrator.Tests;

public class StarExpanderTests
{
    [Fact]
    public void ParseTableAliases_FlatJoin_MapsAliases()
    {
        var list = StarExpander.ParseTableAliases(
            "SELECT * FROM dbo.Orders o JOIN [dbo].[Items] AS [i] ON i.OrderId = o.Id WHERE o.Id > 0");

        Assert.Equal(2, list.Count);
        Assert.Equal("o", list[0].Alias);
        Assert.Equal("dbo.Orders", list[0].Table);
        Assert.Equal("[i]", list[1].Alias);
        Assert.Equal("dbo.Items", list[1].Table);
    }

    [Fact]
    public void ParseTableAliases_KeywordAfterTable_NotAlias()
    {
        var list = StarExpander.ParseTableAliases("SELECT * FROM dbo.T WHERE Id > 0");

        Assert.Single(list);
        Assert.Equal("T", list[0].Alias);
    }

    [Fact]
    public void ParseTableAliases_NoAlias_UsesShortName()
    {
        var list = StarExpander.ParseTableAliases("SELECT * FROM dbo.MyTable");

        Assert.Single(list);
        Assert.Equal("MyTable", list[0].Alias);
        Assert.Equal("dbo.MyTable", list[0].Table);
    }

    [Fact]
    public void ExpandStars_AliasStar_QualifiesColumns()
    {
        var map = new List<(string, IReadOnlyList<string>)>
        {
            ("o", new List<string> { "Id", "Active" })
        };

        var (changed, script) = StarExpander.ExpandStars(
            "SELECT o.* FROM dbo.Orders o", map);

        Assert.True(changed);
        Assert.Contains("[o].[Id], [o].[Active]", script);
        Assert.DoesNotContain("o.*", script);
    }

    [Fact]
    public void ExpandStars_BareStar_ExpandsAllAliasesInOrder()
    {
        var map = new List<(string, IReadOnlyList<string>)>
        {
            ("a", new List<string> { "Id" }),
            ("b", new List<string> { "Active" })
        };

        var (changed, script) = StarExpander.ExpandStars(
            "SELECT * FROM dbo.A a JOIN dbo.B b ON b.Id = a.Id", map);

        Assert.True(changed);
        Assert.Contains("[a].[Id], [b].[Active]", script);
    }

    [Fact]
    public void ExpandStars_CountAndMultiply_Untouched()
    {
        var map = new List<(string, IReadOnlyList<string>)>
        {
            ("t", new List<string> { "Qty", "Price" })
        };

        var (changed, script) = StarExpander.ExpandStars(
            "SELECT COUNT(*), Qty * Price FROM dbo.T t GROUP BY Qty, Price", map);

        Assert.False(changed);
        Assert.Contains("COUNT(*)", script);
        Assert.Contains("Qty * Price", script);
    }

    [Fact]
    public void ExpandStars_UnknownAlias_Untouched()
    {
        var map = new List<(string, IReadOnlyList<string>)>
        {
            ("a", new List<string> { "Id" })
        };

        var (changed, script) = StarExpander.ExpandStars("SELECT z.* FROM dbo.Z z", map);

        Assert.False(changed);
        Assert.Contains("z.*", script);
    }

    [Fact]
    public void DescribeMissingDependency_ThreePart_PointsToOtherDatabase()
    {
        var msg = DatabaseReconciler.DescribeMissingDependency(
            "Invalid object name 'ADEL9200.dbo.ROOMINFO'.");

        Assert.Contains("ADEL9200", msg);
        Assert.Contains("DATABASE KHÁC", msg);
    }

    [Fact]
    public void DescribeMissingDependency_TwoPart_GenericGuidance()
    {
        var msg = DatabaseReconciler.DescribeMissingDependency(
            "Invalid object name 'dbo.SomeTable'.");

        Assert.Contains("dbo.SomeTable", msg);
        Assert.DoesNotContain("DATABASE KHÁC", msg);
    }

    [Fact]
    public void DescribeMissingDependency_NullMessage_GenericGuidance()
    {
        Assert.Contains("phụ thuộc", DatabaseReconciler.DescribeMissingDependency(null));
    }

    [Fact]
    public void ExtractCrossDatabaseRefs_ThreePartForms_Detected()
    {
        var refs = DatabaseReconciler.ExtractCrossDatabaseRefs(
            "SELECT * FROM ADEL9200.dbo.ROOMINFO UNION SELECT * FROM [OtherDb].[dbo].[T];",
            "REHOSPOS");

        Assert.Contains("ADEL9200.dbo.ROOMINFO", refs);
        Assert.Contains("OtherDb.dbo.T", refs);
    }

    [Fact]
    public void ExtractCrossDatabaseRefs_OwnDatabase_Excluded()
    {
        var refs = DatabaseReconciler.ExtractCrossDatabaseRefs(
            "SELECT * FROM REHOSPOS.dbo.T1 JOIN [REHOSPOS].[dbo].[T2] ON 1=1;",
            "rehospos");

        Assert.Empty(refs);
    }

    [Fact]
    public void ExtractCrossDatabaseRefs_TwoPartAndAlias_Ignored()
    {
        var refs = DatabaseReconciler.ExtractCrossDatabaseRefs(
            "SELECT a.Id FROM dbo.T AS a;",
            "REHOSPOS");

        Assert.Empty(refs);
    }

    [Fact]
    public void ExtractCrossDatabaseRefs_FourPartLinkedServer_Detected()
    {
        var refs = DatabaseReconciler.ExtractCrossDatabaseRefs(
            "SELECT * FROM [LINKED].[RemoteDb].[dbo].[T];",
            "REHOSPOS");

        Assert.Contains("LINKED.RemoteDb", refs);
    }
}
