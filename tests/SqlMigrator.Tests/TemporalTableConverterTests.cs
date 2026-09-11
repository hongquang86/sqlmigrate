using System.Collections.Generic;
using SqlMigrator.Core.Models;
using SqlMigrator.Core.Services;
using Xunit;

namespace SqlMigrator.Tests;

public class TemporalTableConverterTests
{
    private const string TemporalDdl = @"CREATE TABLE [dbo].[Orders](
	[Id] [int] IDENTITY(1,1) NOT NULL,
	[Code] [nvarchar](50) NOT NULL,
	[Qty] [int] NOT NULL,
	[Total] AS ([Qty]*2),
	[SysStart] [datetime2](7) GENERATED ALWAYS AS ROW START NOT NULL,
	[SysEnd] [datetime2](7) GENERATED ALWAYS AS ROW END NOT NULL,
	CONSTRAINT [PK_Orders] PRIMARY KEY CLUSTERED ([Id] ASC),
	PERIOD FOR SYSTEM_TIME ([SysStart], [SysEnd])
)
WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = [dbo].[Orders_History], DATA_CONSISTENCY_CHECK = ON))";

    private static TableSchema MakeOrders()
    {
        var t = new TableSchema { Schema = "dbo", Name = "Orders" };
        t.AddColumn(new ColumnSchema { Name = "Id", DataTypeName = "int", IsIdentity = true, IsNullable = false, IsPrimaryKey = true });
        t.AddColumn(new ColumnSchema { Name = "Code", DataTypeName = "nvarchar", IsNullable = false });
        t.AddColumn(new ColumnSchema { Name = "Qty", DataTypeName = "int", IsNullable = false });
        t.AddColumn(new ColumnSchema { Name = "Total", DataTypeName = "int", IsComputed = true, IsNullable = true });
        t.AddColumn(new ColumnSchema { Name = "SysStart", DataTypeName = "datetime2", IsNullable = false });
        t.AddColumn(new ColumnSchema { Name = "SysEnd", DataTypeName = "datetime2", IsNullable = false });
        return t;
    }

    [Fact]
    public void TryConvert_StripsVersioningFromBase()
    {
        var conv = TemporalTableConverter.TryConvert(TemporalDdl, MakeOrders(), "dbo", "Orders_History", false);

        Assert.NotNull(conv);
        Assert.DoesNotContain("SYSTEM_VERSIONING", conv!.BaseTableScript);
        Assert.DoesNotContain("PERIOD FOR SYSTEM_TIME", conv.BaseTableScript);
        Assert.DoesNotContain("GENERATED ALWAYS", conv.BaseTableScript);
        Assert.Contains("CREATE TABLE [dbo].[Orders]", conv.BaseTableScript);
        Assert.Equal("SysStart", conv.PeriodStart);
        Assert.Equal("SysEnd", conv.PeriodEnd);
    }

    [Fact]
    public void TryConvert_BuildsHistoryWithoutConstraints()
    {
        var conv = TemporalTableConverter.TryConvert(TemporalDdl, MakeOrders(), "dbo", "Orders_History", false);

        Assert.NotNull(conv);
        Assert.NotNull(conv!.HistoryTableScript);
        Assert.Contains("CREATE TABLE [dbo].[Orders_History]", conv.HistoryTableScript);
        Assert.DoesNotContain("PRIMARY KEY", conv.HistoryTableScript);
        Assert.DoesNotContain("IDENTITY", conv.HistoryTableScript);
        // Cột computed giữ định nghĩa để SELECT * UNION vẫn khớp.
        Assert.Contains("[Total] AS ([Qty]*2)", conv.HistoryTableScript);
    }

    [Fact]
    public void TryConvert_EmitsTriggersAndDefaults()
    {
        var conv = TemporalTableConverter.TryConvert(TemporalDdl, MakeOrders(), "dbo", "Orders_History", false);

        Assert.NotNull(conv);
        Assert.Contains("TR_Orders_History_U", conv!.CombinedScript);
        Assert.Contains("TR_Orders_History_D", conv.CombinedScript);
        Assert.Contains("TRIGGER_NESTLEVEL", conv.CombinedScript);
        Assert.Contains("SYSUTCDATETIME()", conv.CombinedScript);
        Assert.Contains("ADD DEFAULT SYSUTCDATETIME() FOR [SysStart]", conv.CombinedScript);
        Assert.Contains("GO", conv.CombinedScript);
        // Trigger liệt kê tường minh, loại computed/identity.
        Assert.Contains("[Code], [Qty], [SysStart], [SysEnd]", conv.TriggerScript);
        Assert.DoesNotContain("[Total]", conv.TriggerScript.Split("INSERT INTO")[1]);
    }

    [Fact]
    public void TryConvert_HistoryInScope_SkipsHistoryCreate()
    {
        var conv = TemporalTableConverter.TryConvert(TemporalDdl, MakeOrders(), "dbo", "Orders_History", true);

        Assert.NotNull(conv);
        Assert.Null(conv!.HistoryTableScript);
        Assert.Contains("CREATE TABLE [dbo].[Orders]", conv.CombinedScript);
        // Chỉ 1 CREATE TABLE (bảng gốc); trigger vẫn được phép nhắc tên lịch sử.
        Assert.Equal(1, conv.CombinedScript.Split("CREATE TABLE").Length - 1);
    }

    [Fact]
    public void TryConvert_NonTemporal_ReturnsNull()
    {
        var conv = TemporalTableConverter.TryConvert(
            "CREATE TABLE [dbo].[T]([Id] [int] NOT NULL);", MakeOrders(), "dbo", "T_History", false);

        Assert.Null(conv);
    }

    [Fact]
    public void SplitTopLevel_RespectsParensAndStrings()
    {
        var items = TemporalTableConverter.SplitTopLevel(
            "[a] INT, [b] DECIMAL(10,2) DEFAULT (0), [c] NVARCHAR(10) DEFAULT 'x,y', CONSTRAINT [PK] PRIMARY KEY ([a])");

        Assert.Equal(4, items.Count);
    }

    [Fact]
    public void StripOutsideQuotes_ProtectsStringLiterals()
    {
        var kept = TemporalTableConverter.StripOutsideQuotes(
            "[Note] NVARCHAR(50) DEFAULT 'UNIQUE'", @"(?<=[\s\)])UNIQUE(?=[\s,]|$)");
        Assert.Contains("'UNIQUE'", kept);

        var stripped = TemporalTableConverter.StripOutsideQuotes(
            "[Email] NVARCHAR(100) UNIQUE NOT NULL", @"(?<=[\s\)])UNIQUE(?=[\s,]|$)");
        Assert.DoesNotContain("UNIQUE", stripped);
        Assert.Contains("[Email]", stripped);
    }

    private static Dictionary<string, (string, string, string, string)> MakeMap()
    {
        return new Dictionary<string, (string, string, string, string)>(
            System.StringComparer.OrdinalIgnoreCase)
        {
            ["dbo.Orders"] = ("dbo", "Orders_History", "SysStart", "SysEnd")
        };
    }

    [Fact]
    public void RewriteSystemTime_AsOf_PreservesAlias()
    {
        var out_ = TemporalTableConverter.RewriteSystemTimeQuery(
            "SELECT o.Id FROM dbo.Orders AS o FOR SYSTEM_TIME AS OF '2024-01-01';", MakeMap());

        Assert.Contains("UNION ALL", out_);
        Assert.Contains("[dbo].[Orders_History]", out_);
        Assert.Contains("[SysStart] <= '2024-01-01' AND '2024-01-01' < [SysEnd]", out_);
        Assert.Contains(") AS o", out_);
        Assert.DoesNotContain("FOR SYSTEM_TIME", out_);
    }

    [Fact]
    public void RewriteSystemTime_AllWithoutAlias_UsesTableName()
    {
        var out_ = TemporalTableConverter.RewriteSystemTimeQuery(
            "SELECT * FROM [dbo].[Orders] FOR SYSTEM_TIME ALL;", MakeMap());

        Assert.Contains("UNION ALL", out_);
        Assert.DoesNotContain("WHERE", out_);
        Assert.Contains(") AS [Orders]", out_);
    }

    [Fact]
    public void RewriteSystemTime_BetweenAndContained_MapCorrectly()
    {
        var between = TemporalTableConverter.RewriteSystemTimeQuery(
            "SELECT * FROM dbo.Orders FOR SYSTEM_TIME BETWEEN '2024-01-01' AND '2024-02-01';", MakeMap());
        Assert.Contains("[SysStart] <= '2024-02-01' AND '2024-01-01' < [SysEnd]", between);

        var contained = TemporalTableConverter.RewriteSystemTimeQuery(
            "SELECT * FROM dbo.Orders FOR SYSTEM_TIME CONTAINED IN ('2024-01-01', '2024-02-01');", MakeMap());
        Assert.Contains("'2024-01-01' <= [SysStart] AND [SysEnd] <= '2024-02-01'", contained);
    }

    [Fact]
    public void RewriteSystemTime_UnknownTable_Untouched()
    {
        var sql = "SELECT * FROM dbo.Other FOR SYSTEM_TIME ALL;";
        Assert.Equal(sql, TemporalTableConverter.RewriteSystemTimeQuery(sql, MakeMap()));
    }
}
