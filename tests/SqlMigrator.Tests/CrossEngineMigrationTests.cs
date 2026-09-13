using System.Collections.Generic;
using System.Linq;
using SqlMigrator.Core.Models;
using SqlMigrator.Core.Services.DataMove;
using SqlMigrator.Core.Services.DbProviders;
using Xunit;

namespace SqlMigrator.Tests;

public class CrossEngineMigrationTests
{
    [Theory]
    [InlineData("SqlServer", "SqlServer", true)]
    [InlineData("SqlServer", "PostgreSql", true)]
    [InlineData("PostgreSql", "SqlServer", true)]
    [InlineData("SqlServer", "Sqlite", true)]
    [InlineData("Sqlite", "SqlServer", true)]
    [InlineData("PostgreSql", "Sqlite", true)]
    [InlineData("Sqlite", "PostgreSql", true)]
    [InlineData("Sqlite", "Sqlite", true)]
    [InlineData("PostgreSql", "PostgreSql", true)]
    [InlineData("SqlServer", "MySql", true)]
    [InlineData("MySql", "SqlServer", true)]
    [InlineData("MySql", "MySql", true)]
    [InlineData("MySql", "PostgreSql", true)]
    [InlineData("MySql", "Sqlite", true)]
    [InlineData("SqlServer", "MongoDb", true)]
    [InlineData("MySql", "MongoDb", true)]
    [InlineData("MongoDb", "MongoDb", true)]
    [InlineData("MongoDb", "Sqlite", true)]
    public void IsSupportedPair_Matrix(string src, string dst, bool expected)
    {
        var s = EngineInfo.ParseEngine(src);
        var d = EngineInfo.ParseEngine(dst);
        Assert.Equal(expected, MigrationGuard.IsSupportedPair(s, d));
        Assert.Equal(expected, MigrationGuard.IsSupportedPair(d, s));
    }

    [Theory]
    [InlineData("SqlServer", "SqlServer", true)]
    [InlineData("SqlServer", "PostgreSql", true)]
    [InlineData("Sqlite", "PostgreSql", true)]
    [InlineData("SqlServer", "MySql", true)]
    [InlineData("MySql", "Sqlite", true)]
    [InlineData("", "", true)]
    public void EnsureSupportedEngines_MessageOrNull(string src, string dst, bool allowed)
    {
        var msg = MigrationGuard.EnsureSupportedEngines(src, dst);
        Assert.Equal(allowed, msg == null);
    }

    private static CanonicalTable Table(string name, string? schema = "dbo",
        params (string Col, string RefTable)[] fks)
    {
        return new CanonicalTable
        {
            Schema = schema,
            Name = name,
            Columns = new List<CanonicalColumn>
            {
                new() { Name = "Id", Type = CanonicalType.Int32, IsPrimaryKey = true, IsNullable = false }
            },
            PrimaryKeyColumns = new List<string> { "Id" },
            ForeignKeys = fks.Select(f => new CanonicalForeignKey
            {
                Name = "FK_" + name + "_" + f.Col,
                Columns = new List<string> { f.Col },
                RefSchema = schema,
                RefTable = f.RefTable,
                RefColumns = new List<string> { "Id" }
            }).ToList()
        };
    }

    [Fact]
    public void OrderTables_ParentsBeforeChildren()
    {
        var tables = new List<CanonicalTable>
        {
            Table("OrderLines", "dbo", ("OrderId", "Orders")),
            Table("Orders", "dbo", ("CustomerId", "Customers")),
            Table("Customers")
        };
        var ordered = CrossEngineMigrationService.OrderTables(tables)
            .Select(t => t.Name).ToList();
        Assert.True(ordered.IndexOf("Customers") < ordered.IndexOf("Orders"));
        Assert.True(ordered.IndexOf("Orders") < ordered.IndexOf("OrderLines"));
    }

    [Fact]
    public void OrderTables_SelfReference_DoesNotThrow()
    {
        var tables = new List<CanonicalTable>
        {
            Table("Employees", "dbo", ("ManagerId", "Employees"))
        };
        var ordered = CrossEngineMigrationService.OrderTables(tables);
        Assert.Single(ordered);
    }

    [Fact]
    public void FilterTables_EmptyOption_ReturnsAll()
    {
        var tables = new List<CanonicalTable> { Table("A"), Table("B") };
        var result = CrossEngineMigrationService.FilterTables(
            tables, new CrossEngineOptions());
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void FilterTables_OnlyTables_FiltersCaseInsensitive()
    {
        var tables = new List<CanonicalTable> { Table("Orders"), Table("Customers") };
        var result = CrossEngineMigrationService.FilterTables(
            tables, new CrossEngineOptions { OnlyTables = new[] { "dbo.orders" } });
        Assert.Single(result);
        Assert.Equal("Orders", result[0].Name);
    }
}
