using System.Collections.Generic;
using SqlMigrator.Core.Models;
using SqlMigrator.Core.Services;
using Xunit;

namespace SqlMigrator.Tests;

public class IgnoreListTests
{
    [Theory]
    [InlineData("TABLE", "TABLE")]
    [InlineData("USER_TABLE", "TABLE")]
    [InlineData("U", "TABLE")]
    [InlineData("VIEW", "VIEW")]
    [InlineData("STORED_PROCEDURE", "PROCEDURE")]
    [InlineData("SQL_STORED_PROCEDURE", "PROCEDURE")]
    [InlineData("P", "PROCEDURE")]
    [InlineData("SQL_SCALAR_FUNCTION", "FUNCTION")]
    [InlineData("FN", "FUNCTION")]
    [InlineData("SQL_TRIGGER", "TRIGGER")]
    [InlineData("TR", "TRIGGER")]
    [InlineData("SEQUENCE", "SEQUENCE")]
    [InlineData("FK", "FK")]
    [InlineData("COLUMN", "COLUMN")]
    public void CanonicalKind_NormalizesAliases(string raw, string expected)
    {
        Assert.Equal(expected, IgnoreMatcher.CanonicalKind(raw));
    }

    [Fact]
    public void IsIgnored_MatchesAcrossNamingConventions()
    {
        var ignored = new List<IgnoredObject>
        {
            new IgnoredObject { ObjectType = "VIEW", Schema = "dbo", Name = "VIE_RoomService" }
        };

        // Reconcile dùng "VIEW", Verify dùng type_desc "VIEW" — đều khớp.
        Assert.True(IgnoreMatcher.IsIgnored(ignored, "VIEW", "dbo.VIE_RoomService"));
        Assert.True(IgnoreMatcher.IsIgnored(ignored, "V", "DBO.vie_roomservice"));
        // Loại khác hoặc tên khác thì không khớp.
        Assert.False(IgnoreMatcher.IsIgnored(ignored, "TABLE", "dbo.VIE_RoomService"));
        Assert.False(IgnoreMatcher.IsIgnored(ignored, "VIEW", "dbo.Other"));
        Assert.False(IgnoreMatcher.IsIgnored(new List<IgnoredObject>(), "VIEW", "dbo.VIE_RoomService"));
    }

    [Fact]
    public void FilterMissing_RemovesIgnoredKeepsRest()
    {
        var ignored = new List<IgnoredObject>
        {
            new IgnoredObject { ObjectType = "VIEW", Schema = "dbo", Name = "V1" }
        };

        var kept = InventoryComparer.FilterMissing(
            new[] { "dbo.V1", "dbo.V2" }, "View", ignored);

        Assert.Equal(new[] { "dbo.V2" }, kept);
    }

    [Fact]
    public void FilterMissing_NoIgnores_KeepsAll()
    {
        var kept = InventoryComparer.FilterMissing(
            new[] { "dbo.A" }, "Bảng", new List<IgnoredObject>());

        Assert.Equal(new[] { "dbo.A" }, kept);
    }

    [Fact]
    public void FormatComparison_IgnoredCountsAsMatch()
    {
        var source = new DatabaseInventory
        {
            DatabaseName = "S",
            ServerMajorVersion = 13,
            Ok = true,
            Views = new List<InventoryObject>
            {
                new InventoryObject { Type = "V", Schema = "dbo", Name = "V1" }
            }
        };
        var dest = new DatabaseInventory
        {
            DatabaseName = "D",
            ServerMajorVersion = 12,
            Ok = true
        };
        var ignored = new List<IgnoredObject>
        {
            new IgnoredObject { ObjectType = "VIEW", Schema = "dbo", Name = "V1" }
        };

        var lines = InventoryComparer.FormatComparison(source, dest, ignored);

        Assert.Contains(lines, l => l.Contains("KHỚP 100%"));
        Assert.Contains(lines, l => l.Contains("Đã ẩn 1 mục"));
    }
}
