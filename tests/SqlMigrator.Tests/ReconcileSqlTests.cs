using System.Data;
using SqlMigrator.Core.Models;
using SqlMigrator.Core.Services;
using Xunit;

namespace SqlMigrator.Tests;

public class ReconcileSqlTests
{
    // -----------------------------------------------------------------------
    // KeyExpression
    // -----------------------------------------------------------------------

    [Fact]
    public void KeyExpression_IntNumeric_ReturnsQuotedColumn()
    {
        var plan = MakePlan(TransferKeyKind.Numeric, "Id");
        Assert.Equal("[Id]", ReconcileSql.KeyExpression(plan));
    }

    [Fact]
    public void KeyExpression_RowVersion_ReturnsConvert()
    {
        var plan = new TransferTablePlan
        {
            Schema = "dbo", Name = "T", KeyKind = TransferKeyKind.RowVersion, KeyColumn = "Ver"
        };
        Assert.Equal("CONVERT(bigint, [Ver])", ReconcileSql.KeyExpression(plan));
    }

    [Fact]
    public void KeyExpression_NullKey_ReturnsAlwaysTrue()
    {
        var plan = MakePlan(TransferKeyKind.None, null);
        Assert.Equal("1=1", ReconcileSql.KeyExpression(plan));
    }

    // -----------------------------------------------------------------------
    // BuildUpdateSql
    // -----------------------------------------------------------------------

    [Fact]
    public void BuildUpdateSql_MultipleColumns_SetsNonKeyColumns()
    {
        var plan = MakePlan(TransferKeyKind.Numeric, "Id", "Name", "Age");
        var sql = ReconcileSql.BuildUpdateSql(plan);
        Assert.Contains("SET [Name] = @p0, [Age] = @p1", sql);
        Assert.Contains("WHERE [Id] = @pk", sql);
    }

    [Fact]
    public void BuildUpdateSql_OnlyKeyColumn_ReturnsEmptyString()
    {
        var plan = MakePlan(TransferKeyKind.Numeric, "Id");
        var sql = ReconcileSql.BuildUpdateSql(plan);
        Assert.Equal(string.Empty, sql);
    }

    // -----------------------------------------------------------------------
    // BuildDeleteSql
    // -----------------------------------------------------------------------

    [Fact]
    public void BuildDeleteSql_DeletesByKey()
    {
        var sql = ReconcileSql.BuildDeleteSql(MakePlan(TransferKeyKind.Numeric, "Id"));
        Assert.Equal("DELETE FROM [dbo].[T] WHERE [Id] = @pk;", sql);
    }

    // -----------------------------------------------------------------------
    // BuildTailDeleteSql
    // -----------------------------------------------------------------------

    [Fact]
    public void BuildTailDeleteSql_DeleteKeysGreaterThanMax()
    {
        var sql = ReconcileSql.BuildTailDeleteSql(MakePlan(TransferKeyKind.Numeric, "Id"));
        Assert.Equal("DELETE FROM [dbo].[T] WHERE [Id] > @max;", sql);
    }

    // -----------------------------------------------------------------------
    // BuildAddColumnSql
    // -----------------------------------------------------------------------

    [Fact]
    public void BuildAddColumnSql_NullableColumn()
    {
        var sql = ReconcileSql.BuildAddColumnSql("dbo", "T", "Desc", "nvarchar(200)", isNullable: true);
        Assert.Equal("ALTER TABLE [dbo].[T] ADD [Desc] nvarchar(200) NULL;", sql);
    }

    [Fact]
    public void BuildAddColumnSql_NotNullColumn()
    {
        var sql = ReconcileSql.BuildAddColumnSql("dbo", "T", "Code", "int", isNullable: false);
        Assert.Equal("ALTER TABLE [dbo].[T] ADD [Code] int NOT NULL;", sql);
    }

    // -----------------------------------------------------------------------
    // BuildDataTypeSql
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("sys", "int", (short)4, (byte)0, (byte)0, "int")]
    [InlineData("sys", "bigint", (short)8, (byte)0, (byte)0, "bigint")]
    [InlineData("sys", "varchar", (short)100, (byte)0, (byte)0, "varchar(100)")]
    [InlineData("sys", "nvarchar", (short)-1, (byte)0, (byte)0, "nvarchar(max)")]
    [InlineData("sys", "nvarchar", (short)200, (byte)0, (byte)0, "nvarchar(100)")]
    [InlineData("sys", "decimal", (short)0, (byte)18, (byte)2, "decimal(18,2)")]
    [InlineData("sys", "datetime2", (short)0, (byte)0, (byte)7, "datetime2(7)")]
    [InlineData("sys", "binary", (short)16, (byte)0, (byte)0, "binary(16)")]
    [InlineData("sys", "varbinary", (short)-1, (byte)0, (byte)0, "varbinary(max)")]
    [InlineData("sys", "float", (short)0, (byte)53, (byte)0, "float")]
    [InlineData("dbo", "MyType", (short)0, (byte)0, (byte)0, "[dbo].[MyType]")]
    public void BuildDataTypeSql_ReturnsExpected(string schema, string type, short len, byte prec, byte scale, string expected)
    {
        Assert.Equal(expected, ReconcileSql.BuildDataTypeSql(schema, type, len, prec, scale));
    }

    // -----------------------------------------------------------------------
    // BuildKeysRangeSql
    // -----------------------------------------------------------------------

    [Fact]
    public void BuildKeysRangeSql_IncludesTopParamAndOrder()
    {
        var plan = MakePlan(TransferKeyKind.Numeric, "Id");
        var sql = ReconcileSql.BuildKeysRangeSql(plan, "10", "100", 500, out var ps);
        Assert.Contains("SELECT TOP (@rows)", sql);
        Assert.Contains("[Id] > @lo", sql);
        Assert.Contains("[Id] <= @hi", sql);
        Assert.Contains("ORDER BY k", sql);
        Assert.Equal(3, ps.Length); // @rows, @lo, @hi
    }

    [Fact]
    public void BuildKeysRangeSql_NullLo_OmitsLowerBound()
    {
        var plan = MakePlan(TransferKeyKind.Numeric, "Id");
        var sql = ReconcileSql.BuildKeysRangeSql(plan, null, "100", 500, out var ps);
        Assert.DoesNotContain("@lo", sql);
        Assert.Equal(2, ps.Length); // @rows, @hi
    }

    // -----------------------------------------------------------------------
    // RowsEqual
    // -----------------------------------------------------------------------

    [Fact]
    public void RowsEqual_SameValues_ReturnsTrue()
    {
        Assert.True(ReconcileSql.RowsEqual(new object[] { 1, "abc" }, new object[] { 1, "abc" }));
    }

    [Fact]
    public void RowsEqual_DifferentValues_ReturnsFalse()
    {
        Assert.False(ReconcileSql.RowsEqual(new object[] { 1, "abc" }, new object[] { 1, "abd" }));
    }

    [Fact]
    public void RowsEqual_DifferentLength_ReturnsFalse()
    {
        Assert.False(ReconcileSql.RowsEqual(new object[] { 1 }, new object[] { 1, 2 }));
    }

    // -----------------------------------------------------------------------
    // ValuesEqual
    // -----------------------------------------------------------------------

    [Fact]
    public void ValuesEqual_NullBoth_ReturnsTrue()
    {
        Assert.True(ReconcileSql.ValuesEqual(null, null));
    }

    [Fact]
    public void ValuesEqual_NullVsValue_ReturnsFalse()
    {
        Assert.False(ReconcileSql.ValuesEqual(null, "x"));
    }

    [Fact]
    public void ValuesEqualDBNullBoth_ReturnsTrue()
    {
        Assert.True(ReconcileSql.ValuesEqual(DBNull.Value, DBNull.Value));
    }

    [Fact]
    public void ValuesEqual_SameByteArrays_ReturnsTrue()
    {
        Assert.True(ReconcileSql.ValuesEqual(new byte[] { 1, 2 }, new byte[] { 1, 2 }));
    }

    [Fact]
    public void ValuesEqual_DifferentByteArrays_ReturnsFalse()
    {
        Assert.False(ReconcileSql.ValuesEqual(new byte[] { 1, 2 }, new byte[] { 1, 3 }));
    }

    [Fact]
    public void ValuesEqual_SameString_ReturnsTrue()
    {
        Assert.True(ReconcileSql.ValuesEqual("hello", "hello"));
    }

    [Fact]
    public void ValuesEqual_DifferentString_ReturnsFalse()
    {
        Assert.False(ReconcileSql.ValuesEqual("hello", "world"));
    }

    [Fact]
    public void ValuesEqual_IntAndLong_NumericCompareTrue()
    {
        Assert.True(ReconcileSql.ValuesEqual(42, 42L));
    }

    [Fact]
    public void ValuesEqual_Guids_SameValue_ReturnsTrue()
    {
        var g = Guid.NewGuid();
        Assert.True(ReconcileSql.ValuesEqual(g, g));
    }

    [Fact]
    public void ValuesEqual_DateTimes_SameValue_ReturnsTrue()
    {
        var dt = new DateTime(2025, 1, 15, 10, 30, 0, DateTimeKind.Utc);
        Assert.True(ReconcileSql.ValuesEqual(dt, dt));
    }

    // -----------------------------------------------------------------------
    // DiffRows
    // -----------------------------------------------------------------------

    [Fact]
    public void DiffRows_IdenticalSources_EmptyActions()
    {
        var src = new Dictionary<string, object[]> { ["A"] = new object[] { 1 }, ["B"] = new object[] { 2 } };
        var dest = new List<(string Key, object[] Values)> { ("A", new object[] { 1 }), ("B", new object[] { 2 }) };
        var actions = ReconcileSql.DiffRows(src, dest);
        Assert.Empty(actions.Inserts);
        Assert.Empty(actions.Updates);
        Assert.Empty(actions.Deletes);
    }

    [Fact]
    public void DiffRows_SourceHasExtra_CreatesInserts()
    {
        var src = new Dictionary<string, object[]> { ["A"] = new object[] { 1 }, ["C"] = new object[] { 3 } };
        var dest = new List<(string Key, object[] Values)> { ("A", new object[] { 1 }) };
        var actions = ReconcileSql.DiffRows(src, dest);
        Assert.Single(actions.Inserts);
        Assert.Equal("C", actions.Inserts[0]);
        Assert.Empty(actions.Updates);
        Assert.Empty(actions.Deletes);
    }

    [Fact]
    public void DiffRows_DestHasExtra_CreatesDeletes()
    {
        var src = new Dictionary<string, object[]> { ["A"] = new object[] { 1 } };
        var dest = new List<(string Key, object[] Values)> { ("A", new object[] { 1 }), ("B", new object[] { 2 }) };
        var actions = ReconcileSql.DiffRows(src, dest);
        Assert.Empty(actions.Inserts);
        Assert.Single(actions.Deletes);
        Assert.Equal("B", actions.Deletes[0]);
    }

    [Fact]
    public void DiffRows_SameKeyDifferentContent_CreatesUpdate()
    {
        var src = new Dictionary<string, object[]> { ["A"] = new object[] { 99 } };
        var dest = new List<(string Key, object[] Values)> { ("A", new object[] { 1 }) };
        var actions = ReconcileSql.DiffRows(src, dest);
        Assert.Single(actions.Updates);
        Assert.Equal("A", actions.Updates[0].Key);
        Assert.Equal(new object[] { 99 }, actions.Updates[0].Values);
    }

    [Fact]
    public void DiffRows_SkipUpdate_ProducesNoUpdates()
    {
        var src = new Dictionary<string, object[]> { ["A"] = new object[] { 99 } };
        var dest = new List<(string Key, object[] Values)> { ("A", new object[] { 1 }) };
        var actions = ReconcileSql.DiffRows(src, dest, skipUpdate: true);
        Assert.Empty(actions.Updates);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static TransferTablePlan MakePlan(TransferKeyKind keyKind, string? keyColumn, params string[] extraColumns)
    {
        var cols = new List<string>();
        if (keyColumn != null) cols.Add(keyColumn);
        cols.AddRange(extraColumns);
        return new TransferTablePlan
        {
            Schema = "dbo",
            Name = "T",
            Strategy = TransferStrategy.KeysetChunked,
            KeyKind = keyKind,
            KeyColumn = keyColumn,
            Columns = cols.ToArray()
        };
    }
}