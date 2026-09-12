using SqlMigrator.Core.Services;
using Xunit;

namespace SqlMigrator.Tests;

public class ModuleUsageTests
{
    [Theory]
    [InlineData("SELECT * FROM dbo.VIE_RoomService", "dbo", "VIE_RoomService", true)]
    [InlineData("SELECT * FROM [dbo].[VIE_RoomService]", "dbo", "VIE_RoomService", true)]
    [InlineData("SELECT * FROM [dbo].VIE_RoomService", "dbo", "VIE_RoomService", true)]
    [InlineData("SELECT * FROM dbo.[VIE_RoomService]", "dbo", "VIE_RoomService", true)]
    [InlineData("exec DBO.vie_roomservice", "dbo", "VIE_RoomService", true)]
    [InlineData("SELECT * FROM dbo.VIE_RoomService2", "dbo", "VIE_RoomService", false)]
    [InlineData("SELECT * FROM dbo.Other", "dbo", "VIE_RoomService", false)]
    [InlineData("SELECT a.Id FROM t AS a", "dbo", "VIE_RoomService", false)]
    [InlineData("", "dbo", "VIE_RoomService", false)]
    [InlineData(null, "dbo", "VIE_RoomService", false)]
    public void TextReferencesObject_MatchesFormsAndBoundaries(
        string? text, string schema, string name, bool expected)
    {
        Assert.Equal(expected, ModuleUsageReader.TextReferencesObject(text, schema, name));
    }

    [Fact]
    public void FormatUsageDate_Null_ReturnsDash()
    {
        Assert.Equal("—", ModuleUsageText.FormatCreatedDate(null));
    }

    [Fact]
    public void FormatUsageDateTime_Null_ReturnsUnseen()
    {
        Assert.Equal("chưa thấy", ModuleUsageText.FormatLastUsed(null));
    }

    [Fact]
    public void FormatUsageLogLine_NoData_ReturnsEmpty()
    {
        var issue = new SqlMigrator.Core.Models.ReconcileIssue();
        Assert.Equal("", ModuleUsageText.FormatUsageLogLine(issue));
    }

    [Fact]
    public void FormatUsageLogLine_WithDates_ContainsBoth()
    {
        var issue = new SqlMigrator.Core.Models.ReconcileIssue
        {
            CreatedDate = new System.DateTime(2023, 5, 10),
            LastUsedDate = new System.DateTime(2024, 1, 15, 8, 30, 0),
            UseCount = 42,
            UsageEvidence = "Query Store"
        };
        var line = ModuleUsageText.FormatUsageLogLine(issue);
        Assert.Contains("2023", line);
        Assert.Contains("2024", line);
        Assert.Contains("Query Store", line);
    }

    [Fact]
    public void FormatUsageLogLine_NeverUsed_MentionsEvidence()
    {
        var issue = new SqlMigrator.Core.Models.ReconcileIssue
        {
            CreatedDate = new System.DateTime(2022, 3, 1),
            UsageEvidence = "plan cache"
        };
        var line = ModuleUsageText.FormatUsageLogLine(issue);
        Assert.Contains("chưa từng thấy dùng", line);
        Assert.Contains("plan cache", line);
    }
}
