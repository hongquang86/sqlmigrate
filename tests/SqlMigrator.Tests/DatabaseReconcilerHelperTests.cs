using System;
using System.Collections.Generic;
using SqlMigrator.Core.Services;
using Xunit;

namespace SqlMigrator.Tests;

public class DatabaseReconcilerHelperTests
{
    [Fact]
    public void IsMissingDependencyError_InvalidObjectName_ReturnsTrue()
    {
        var ex = new Exception("Invalid object name 'dbo.vwCuocTamTinh'.");
        Assert.True(DatabaseReconciler.IsMissingDependencyError(ex));
    }

    [Fact]
    public void IsMissingDependencyError_AmbiguousColumn_ReturnsFalse()
    {
        // Lỗi SQL khác (không phải thiếu object) → không retry.
        var ex = new Exception("Ambiguous column name 'Active'.");
        Assert.False(DatabaseReconciler.IsMissingDependencyError(ex));
    }

    [Fact]
    public void IsMissingDependencyError_UnrelatedError_ReturnsFalse()
    {
        var ex = new InvalidOperationException("boom");
        Assert.False(DatabaseReconciler.IsMissingDependencyError(ex));
    }

    [Fact]
    public void ExtractReferencedObjects_BracketedTwoPart_ReturnsTable()
    {
        var schemas = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "dbo" };
        var refs = DatabaseReconciler.ExtractReferencedObjects(
            "CREATE VIEW dbo.V AS SELECT * FROM [dbo].[MyTable];", schemas);
        Assert.Contains("dbo.MyTable", refs);
    }

    [Fact]
    public void ExtractReferencedObjects_UnbracketedTwoPart_ReturnsTable()
    {
        var schemas = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "dbo" };
        var refs = DatabaseReconciler.ExtractReferencedObjects(
            "CREATE VIEW dbo.V AS SELECT * FROM dbo.vwCuocTamTinh;", schemas);
        Assert.Contains("dbo.vwCuocTamTinh", refs);
    }

    [Fact]
    public void ExtractReferencedObjects_ColumnAlias_NotReturned()
    {
        // "a.Id" là alias cột, không phải bảng — schema "a" không có thật nên bị loại.
        var schemas = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "dbo" };
        var refs = DatabaseReconciler.ExtractReferencedObjects(
            "SELECT a.Id, a.Name FROM dbo.T AS a;", schemas);
        Assert.DoesNotContain("a.Id", refs);
        Assert.Contains("dbo.T", refs);
    }

    [Fact]
    public void ExtractReferencedObjects_FunctionCall_NotReturned()
    {
        var schemas = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "dbo" };
        var refs = DatabaseReconciler.ExtractReferencedObjects(
            "SELECT dbo.fn_SplitString(@x, ',');", schemas);
        Assert.DoesNotContain("dbo.fn_SplitString", refs);
    }

    [Fact]
    public void ExtractReferencedObjects_ThreePartName_ReturnsSchemaTable()
    {
        var schemas = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "dbo" };
        var refs = DatabaseReconciler.ExtractReferencedObjects(
            "SELECT * FROM [MyDb].[dbo].[T];", schemas);
        Assert.Contains("dbo.T", refs);
    }
}
