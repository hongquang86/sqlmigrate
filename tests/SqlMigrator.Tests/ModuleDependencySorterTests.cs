using System.Collections.Generic;
using SqlMigrator.Core.Services;
using Xunit;

namespace SqlMigrator.Tests;

public class ModuleDependencySorterTests
{
    [Fact]
    public void SortKeys_ChainDependency_ParentsFirst()
    {
        // VIE_CuocTamTinhHT phụ thuộc vwCuocTamTinh: gốc phải đứng trước.
        var names = new List<string> { "dbo.VIE_CuocTamTinhHT", "dbo.vwCuocTamTinh" };
        var deps = new Dictionary<string, List<string>>
        {
            ["dbo.VIE_CuocTamTinhHT"] = new List<string> { "dbo.vwCuocTamTinh" }
        };

        var ordered = ModuleDependencySorter.SortKeys(names, deps);

        Assert.Equal("dbo.vwCuocTamTinh", ordered[0]);
        Assert.Equal("dbo.VIE_CuocTamTinhHT", ordered[1]);
    }

    [Fact]
    public void SortKeys_NoDependencies_KeepsOriginalOrder()
    {
        var names = new List<string> { "dbo.B", "dbo.A", "dbo.C" };
        var deps = new Dictionary<string, List<string>>();

        Assert.Equal(names, ModuleDependencySorter.SortKeys(names, deps));
    }

    [Fact]
    public void SortKeys_Diamond_ParentsBeforeChildren()
    {
        var names = new List<string> { "dbo.Top", "dbo.Left", "dbo.Right", "dbo.Base" };
        var deps = new Dictionary<string, List<string>>
        {
            ["dbo.Top"] = new List<string> { "dbo.Left", "dbo.Right" },
            ["dbo.Left"] = new List<string> { "dbo.Base" },
            ["dbo.Right"] = new List<string> { "dbo.Base" }
        };

        var ordered = ModuleDependencySorter.SortKeys(names, deps);

        Assert.True(ordered.IndexOf("dbo.Base") < ordered.IndexOf("dbo.Left"));
        Assert.True(ordered.IndexOf("dbo.Base") < ordered.IndexOf("dbo.Right"));
        Assert.True(ordered.IndexOf("dbo.Left") < ordered.IndexOf("dbo.Top"));
        Assert.True(ordered.IndexOf("dbo.Right") < ordered.IndexOf("dbo.Top"));
    }

    [Fact]
    public void SortKeys_ExternalDependency_Ignored()
    {
        // Phụ thuộc bảng (ngoài đoạn module) không chặn sắp xếp.
        var names = new List<string> { "dbo.V" };
        var deps = new Dictionary<string, List<string>>
        {
            ["dbo.V"] = new List<string> { "dbo.SomeTable" }
        };

        Assert.Equal(names, ModuleDependencySorter.SortKeys(names, deps));
    }

    [Fact]
    public void SortKeys_Circular_KeepsOriginalOrder()
    {
        var names = new List<string> { "dbo.A", "dbo.B" };
        var deps = new Dictionary<string, List<string>>
        {
            ["dbo.A"] = new List<string> { "dbo.B" },
            ["dbo.B"] = new List<string> { "dbo.A" }
        };

        Assert.Equal(names, ModuleDependencySorter.SortKeys(names, deps));
    }

    [Fact]
    public void SortKeys_CaseInsensitive_MatchesNames()
    {
        var names = new List<string> { "dbo.Child", "DBO.PARENT" };
        var deps = new Dictionary<string, List<string>>
        {
            ["DBO.CHILD"] = new List<string> { "dbo.parent" }
        };

        var ordered = ModuleDependencySorter.SortKeys(names, deps);

        Assert.Equal("DBO.PARENT", ordered[0]);
        Assert.Equal("dbo.Child", ordered[1]);
    }
}
