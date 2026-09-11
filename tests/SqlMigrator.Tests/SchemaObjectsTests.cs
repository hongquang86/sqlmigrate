using SqlMigrator.Core.Models;
using Xunit;

namespace SqlMigrator.Tests;

/// <summary>Kiểm thử cấu trúc mô hình schema (bảng, cột, khóa ngoại).</summary>
public class SchemaObjectsTests
{
    [Fact]
    public void TableSchema_KhiBịĐánhDấuSkippedThìIsSkippedLàTrue()
    {
        var table = new TableSchema { Schema = "dbo", Name = "Orders" };
        Assert.False(table.IsSkipped);

        table.MarkSkipped("Bảng temporal không hỗ trợ trên đích");
        Assert.True(table.IsSkipped);
        Assert.Equal("Bảng temporal không hỗ trợ trên đích", table.SkipReason);
    }

    [Fact]
    public void GetDataColumns_KhôngBọcCộtIdentityKhiKhôngGiữIdentity()
    {
        var table = new TableSchema { Schema = "dbo", Name = "Orders" };
        AddProbeColumns(table);

        var columns = table.GetDataColumns(preserveIdentity: false);
        Assert.Equal(new[] { "Name", "Price", "CreatedAt" }, columns.Select(c => c.Name).ToArray());
    }

    [Fact]
    public void GetDataColumns_BaoGồmCộtIdentityKhiGiữIdentity()
    {
        var table = new TableSchema { Schema = "dbo", Name = "Orders" };
        AddProbeColumns(table);

        var columns = table.GetDataColumns(preserveIdentity: true);
        Assert.Contains(columns, c => c.Name == "Id" && c.IsIdentity);
    }

    [Fact]
    public void GetDataColumns_KhôngBaoGiờBaoGồmCộtComputed()
    {
        var table = new TableSchema { Schema = "dbo", Name = "Orders" };
        AddProbeColumns(table);

        var columns = table.GetDataColumns(preserveIdentity: true);
        Assert.DoesNotContain(columns, c => c.Name == "Total");
        Assert.DoesNotContain(columns, c => c.Name == "RowVer");
    }

    [Fact]
    public void TableSchema_QualifiedNameVàPlainName()
    {
        var table = new TableSchema { Schema = "sales", Name = "Order Detail" };
        Assert.Equal("[sales].[Order Detail]", table.QualifiedName);
        Assert.Equal("sales.Order Detail", table.PlainName);
    }

    [Fact]
    public void DatabaseObject_DisplayNameNốiSchameVàTên()
    {
        var obj = new DatabaseObject { Type = DatabaseObjectType.StoredProcedure, Schema = "dbo", Name = "GetOrders" };
        Assert.Equal("dbo.GetOrders", obj.DisplayName);
    }

    private static void AddProbeColumns(TableSchema table)
    {
        table.AddColumn(new ColumnSchema { Name = "Id", IsIdentity = true, IsPrimaryKey = true });
        table.AddColumn(new ColumnSchema { Name = "Name", DataTypeName = "nvarchar" });
        table.AddColumn(new ColumnSchema { Name = "Price", DataTypeName = "decimal" });
        table.AddColumn(new ColumnSchema { Name = "Total", DataTypeName = "decimal", IsComputed = true });
        table.AddColumn(new ColumnSchema { Name = "CreatedAt", DataTypeName = "datetime" });
        table.AddColumn(new ColumnSchema { Name = "RowVer", DataTypeName = "rowversion", IsRowVersion = true });
    }
}