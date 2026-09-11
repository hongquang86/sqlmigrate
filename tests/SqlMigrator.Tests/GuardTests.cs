using System.Collections.Generic;
using SqlMigrator.Core.Models;
using SqlMigrator.Core.Services;
using Xunit;

namespace SqlMigrator.Tests;

public class GuardTests
{
    [Fact]
    public void IsSameDatabase_SameServerAndDb_ReturnsTrue()
    {
        Assert.True(SqlConnectionFactory.IsSameDatabase(
            "Server=SQL2016;Database=REHOSPOS;User Id=a;Password=b;",
            "Server=SQL2016;Database=REHOSPOS;User Id=c;Password=d;"));
    }

    [Fact]
    public void IsSameDatabase_CaseInsensitive_ReturnsTrue()
    {
        Assert.True(SqlConnectionFactory.IsSameDatabase(
            "Server=SQL2016;Database=Rehospos;",
            "Server=sql2016;Database=REHOSPOS;"));
    }

    [Fact]
    public void IsSameDatabase_LocalhostAliases_ReturnsTrue()
    {
        Assert.True(SqlConnectionFactory.IsSameDatabase(
            "Server=.;Database=Db1;Integrated Security=true;",
            "Server=localhost;Database=Db1;Integrated Security=true;"));
    }

    [Fact]
    public void IsSameDatabase_DifferentDb_ReturnsFalse()
    {
        Assert.False(SqlConnectionFactory.IsSameDatabase(
            "Server=SQL2016;Database=A;",
            "Server=SQL2016;Database=B;"));
    }

    [Fact]
    public void IsSameDatabase_DifferentServer_ReturnsFalse()
    {
        Assert.False(SqlConnectionFactory.IsSameDatabase(
            "Server=SQL2016;Database=A;",
            "Server=SQL2014;Database=A;"));
    }

    [Fact]
    public void IsSameDatabase_Empty_ReturnsFalse()
    {
        Assert.False(SqlConnectionFactory.IsSameDatabase("", "Server=S;Database=A;"));
    }

    [Fact]
    public void BuildMap_TemporalTable_IncludedWithHistory()
    {
        var t = new TableSchema { Schema = "dbo", Name = "Orders", IsTemporal = true };
        t.SetTemporalInfo("dbo", "Orders_History", "SysStart", "SysEnd");

        Assert.Equal("dbo.Orders_History", t.HistoryPlainName);
        Assert.True(t.NeedsTemporalConversion);

        var map = TemporalTableConverter.BuildMap(new List<TableSchema> { t });
        Assert.True(map.ContainsKey("dbo.Orders"));
        Assert.Equal(("dbo", "Orders_History", "SysStart", "SysEnd"), map["dbo.Orders"]);
    }

    [Fact]
    public void BuildMap_NonTemporal_Excluded()
    {
        var tables = new List<TableSchema>
        {
            new TableSchema { Schema = "dbo", Name = "Plain" }
        };
        var map = TemporalTableConverter.BuildMap(tables);
        Assert.Empty(map);
    }
}
