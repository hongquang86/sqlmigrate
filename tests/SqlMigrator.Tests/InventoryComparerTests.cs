using System.Collections.Generic;
using SqlMigrator.Core.Models;
using SqlMigrator.Core.Services;
using Xunit;

namespace SqlMigrator.Tests;

public class InventoryComparerTests
{
    private static DatabaseInventory MakeInventory(
        string[] tables, string[] views, string[] procs)
    {
        List<InventoryObject> Objs(string type, string[] names)
        {
            var list = new List<InventoryObject>();
            foreach (var n in names)
            {
                var dot = n.IndexOf('.');
                list.Add(new InventoryObject
                {
                    Type = type,
                    Schema = dot > 0 ? n.Substring(0, dot) : "",
                    Name = dot > 0 ? n.Substring(dot + 1) : n
                });
            }
            return list;
        }

        return new DatabaseInventory
        {
            DatabaseName = "Db",
            ServerMajorVersion = 13,
            Ok = true,
            Tables = Objs("U", tables),
            Views = Objs("V", views),
            Procedures = Objs("P", procs),
            EstimatedRows = 1000,
            TotalColumns = 50
        };
    }

    [Fact]
    public void Compare_FindsMissingObjectsCaseInsensitive()
    {
        var source = MakeInventory(
            new[] { "dbo.A", "dbo.B" }, new[] { "dbo.V1" }, new[] { "dbo.P1" });
        var dest = MakeInventory(
            new[] { "DBO.A" }, new string[0], new[] { "dbo.P1", "dbo.P2" });

        var diffs = InventoryComparer.Compare(source, dest);

        var tables = Find(diffs, "Bảng");
        Assert.Equal(2, tables.SourceCount);
        Assert.Equal(1, tables.DestCount);
        Assert.Equal(new[] { "dbo.B" }, tables.MissingNames);

        var views = Find(diffs, "View");
        Assert.Equal(new[] { "dbo.V1" }, views.MissingNames);

        var procs = Find(diffs, "Stored Procedure");
        Assert.True(procs.Matches);
    }

    [Fact]
    public void FormatComparison_AllMatch_ReportsHundredPercent()
    {
        var source = MakeInventory(new[] { "dbo.A" }, new string[0], new string[0]);
        var dest = MakeInventory(new[] { "dbo.A" }, new string[0], new string[0]);

        var lines = InventoryComparer.FormatComparison(source, dest);

        Assert.Contains(lines, l => l.Contains("KHỚP 100%"));
    }

    [Fact]
    public void FormatComparison_Missing_ListsNamesAndPointsToReconcile()
    {
        var source = MakeInventory(new[] { "dbo.A", "dbo.B" }, new string[0], new string[0]);
        var dest = MakeInventory(new[] { "dbo.A" }, new string[0], new string[0]);

        var lines = InventoryComparer.FormatComparison(source, dest);

        Assert.Contains(lines, l => l.Contains("dbo.B"));
        Assert.Contains(lines, l => l.Contains("Đồng bộ 100%"));
    }

    [Fact]
    public void FormatInventory_ShowsCountsAndDowngradeFeatures()
    {
        var inv = MakeInventory(new[] { "dbo.A" }, new[] { "dbo.V" }, new[] { "dbo.P" });
        inv = new DatabaseInventory
        {
            DatabaseName = "Src",
            ServerMajorVersion = 13,
            Ok = true,
            Tables = inv.Tables,
            Views = inv.Views,
            Procedures = inv.Procedures,
            EstimatedRows = 5000,
            TotalColumns = 20,
            TemporalTables = 2,
            MaskedColumns = 3
        };

        var lines = InventoryComparer.FormatInventory("— HỒ SƠ —", inv);

        Assert.Contains(lines, l => l.Contains("Bảng: 1"));
        Assert.Contains(lines, l => l.Contains("temporal") && l.Contains("masking"));
    }

    [Fact]
    public void FormatInventory_FailedRead_ExplainsClearly()
    {
        var inv = new DatabaseInventory { Ok = false, Error = "timeout" };
        var lines = InventoryComparer.FormatInventory("— HỒ SƠ —", inv);

        Assert.Contains(lines, l => l.Contains("Không đọc được hồ sơ") && l.Contains("timeout"));
    }

    [Theory]
    // sys.objects.type là char(2): "U "/"V "/"P " có dấu cách đuôi, FN/TR đủ 2 ký tự.
    [InlineData("U ", "TABLE:")]
    [InlineData("V ", "VIEW:")]
    [InlineData("P ", "P:")]
    [InlineData("FN", "F:")]
    [InlineData("IF", "F:")]
    [InlineData("TF", "F:")]
    [InlineData("TR", "TR:")]
    [InlineData("U", "TABLE:")]
    public void InventoryKey_TrimsPaddedSysType(string sysType, string expected)
    {
        Assert.Equal(expected, InventoryComparer.InventoryKey(sysType));
    }

    [Theory]
    [InlineData("S ")]
    [InlineData("PK")]
    [InlineData("")]
    [InlineData(null)]
    public void InventoryKey_UnknownType_ReturnsNull(string? sysType)
    {
        Assert.Null(InventoryComparer.InventoryKey(sysType));
    }

    private static InventoryDiff Find(
        IReadOnlyList<InventoryDiff> diffs, string label)
    {
        foreach (var d in diffs)
        {
            if (d.Label == label)
                return d;
        }
        throw new Xunit.Sdk.XunitException("Không tìm thấy nhóm '" + label + "'.");
    }
}
