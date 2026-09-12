using SqlMigrator.Core.Services;
using Xunit;

namespace SqlMigrator.Tests;

public class DiagramTableTests
{
    [Theory]
    [InlineData("dbo", "sysdiagrams")]
    [InlineData("DBO", "SYSDIAGRAMS")]
    [InlineData("dbo", "SysDiagrams")]
    public void IsDiagramTable_DiagramTable_ReturnsTrue(string schema, string name)
    {
        Assert.True(SchemaExtractor.IsDiagramTable(schema, name));
    }

    [Theory]
    [InlineData("dbo", "Orders")]
    [InlineData("sys", "sysdiagrams")]
    [InlineData("", "sysdiagrams")]
    [InlineData("dbo", "")]
    [InlineData(null, null)]
    public void IsDiagramTable_OtherTables_ReturnsFalse(string? schema, string? name)
    {
        Assert.False(SchemaExtractor.IsDiagramTable(schema, name));
    }
}
