using SqlMigrator.Core.Models;
using SqlMigrator.Core.Services;
using Xunit;

namespace SqlMigrator.Tests;

public class DataSyncPlanTests
{
    private static TableSchema MakeTable(bool pk = true, bool identity = false, bool rowVersion = false)
    {
        var t = new TableSchema { Schema = "dbo", Name = "T" };
        if (pk)
            t.AddColumn(new ColumnSchema { Name = "Id", DataTypeName = "int", IsPrimaryKey = true, IsNullable = false });
        if (identity)
            t.AddColumn(new ColumnSchema { Name = "Id", DataTypeName = "int", IsIdentity = true, IsNullable = false });
        t.AddColumn(new ColumnSchema { Name = "Name", DataTypeName = "nvarchar", IsNullable = true });
        if (rowVersion)
            t.AddColumn(new ColumnSchema { Name = "Ver", DataTypeName = "rowversion", IsRowVersion = true, IsNullable = false });
        return t;
    }

    [Fact]
    public void TryBuildSyncPlan_PkTable_KeysetPlan()
    {
        var plan = ReconcileDataSync.TryBuildSyncPlan(MakeTable(pk: true), out var skip);

        Assert.NotNull(plan);
        Assert.Null(skip);
        Assert.Equal(TransferStrategy.KeysetChunked, plan!.Strategy);
        Assert.Equal("Id", plan.KeyColumn);
    }

    [Fact]
    public void TryBuildSyncPlan_IdentityTable_NumericKey()
    {
        var plan = ReconcileDataSync.TryBuildSyncPlan(MakeTable(pk: false, identity: true), out var skip);

        Assert.NotNull(plan);
        Assert.Null(skip);
        Assert.Equal(TransferKeyKind.Numeric, plan!.KeyKind);
    }

    [Fact]
    public void TryBuildSyncPlan_NoKey_SkippedWithReason()
    {
        var t = new TableSchema { Schema = "dbo", Name = "T" };
        t.AddColumn(new ColumnSchema { Name = "Name", DataTypeName = "nvarchar", IsNullable = true });

        var plan = ReconcileDataSync.TryBuildSyncPlan(t, out var skip);

        Assert.Null(plan);
        Assert.False(string.IsNullOrWhiteSpace(skip));
    }

    [Fact]
    public void TryBuildSyncPlan_RowVersionKey_SkippedWithReason()
    {
        var t = new TableSchema { Schema = "dbo", Name = "T" };
        t.AddColumn(new ColumnSchema { Name = "Name", DataTypeName = "nvarchar", IsNullable = true });
        t.AddColumn(new ColumnSchema { Name = "Ver", DataTypeName = "rowversion", IsRowVersion = true, IsNullable = false });

        var plan = ReconcileDataSync.TryBuildSyncPlan(t, out var skip);

        Assert.Null(plan);
        Assert.Contains("rowversion", skip);
    }

    [Fact]
    public void BuildObjectIdName_BracketedQualifiedName_UnquotedTwoPart()
    {        // Hồi quy bug Trim('[',']') biến "[dbo].[T]" thành "dbo].[T" khiến
        // OBJECT_ID luôn NULL và sync bỏ qua oan với lý do "bảng chưa tồn tại".
        var t = new TableSchema { Schema = "dbo", Name = "sysdiagrams" };
        Assert.Equal("dbo.sysdiagrams", ReconcileDataSync.BuildObjectIdName(t));
        Assert.Equal("[dbo].[sysdiagrams]", t.QualifiedName);
    }

    [Fact]
    public void DataSyncResult_Totals_SumTables()
    {
        var result = new DataSyncResult
        {
            Tables = new[]
            {
                new DataSyncTableResult { Table = "dbo.A", Inserted = 10, Updated = 5 },
                new DataSyncTableResult { Table = "dbo.B", Inserted = 3, Updated = 0 },
                new DataSyncTableResult { Table = "dbo.C", Skipped = true, SkipReason = "x" }
            }
        };

        Assert.Equal(13, result.TotalInserted);
        Assert.Equal(5, result.TotalUpdated);
    }

    [Theory]
    [InlineData(new[] { "diagram_id" }, new[] { "diagram_id", "name" }, true)]
    [InlineData(new[] { "Diagram_Id" }, new[] { "diagram_id" }, true)]
    [InlineData(new string[0], new[] { "diagram_id" }, false)]
    [InlineData(new[] { "other_id" }, new[] { "diagram_id" }, false)]
    public void NeedsIdentityInsert_MatchesDestReality(
        string[] destIdentityCols, string[] insertColumns, bool expected)
    {
        Assert.Equal(expected, ReconcileDataSync.NeedsIdentityInsert(
            destIdentityCols, insertColumns));
    }

    [Fact]
    public void BuildBulkOptions_IdentityOn_IncludesKeepIdentity()
    {
        var options = ReconcileDataSync.BuildBulkOptions(identityOn: true);

        Assert.True(options.HasFlag(Microsoft.Data.SqlClient.SqlBulkCopyOptions.TableLock));
        Assert.True(options.HasFlag(Microsoft.Data.SqlClient.SqlBulkCopyOptions.KeepIdentity));
    }

    [Fact]
    public void BuildBulkOptions_IdentityOff_TableLockOnly()
    {
        var options = ReconcileDataSync.BuildBulkOptions(identityOn: false);

        Assert.True(options.HasFlag(Microsoft.Data.SqlClient.SqlBulkCopyOptions.TableLock));
        Assert.False(options.HasFlag(Microsoft.Data.SqlClient.SqlBulkCopyOptions.KeepIdentity));
    }
}
