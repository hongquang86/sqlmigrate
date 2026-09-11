using SqlMigrator.Core.Models;
using SqlMigrator.Core.Services;
using Xunit;

namespace SqlMigrator.Tests;

/// <summary>Kiểm thử thành phần kiểm tra toàn vẹn dữ liệu <see cref="DataVerifier"/>.</summary>
public class DataVerifierTests
{
    [Fact]
    public void KeyExpression_VớiKhóaRowVersion_BọcConvertBigint()
    {
        var plan = new TransferTablePlan
        {
            Schema = "dbo",
            Name = "Log",
            KeyKind = TransferKeyKind.RowVersion,
            KeyColumn = "version"
        };

        var sql = DataVerifier.KeyExpression(plan);
        Assert.Equal("CONVERT(bigint, [version])", sql);
    }

    [Fact]
    public void KeyExpression_VớiKhóaNumber_ChỉBọcDấuNgoặc()
    {
        var plan = new TransferTablePlan
        {
            Schema = "dbo",
            Name = "Orders",
            KeyKind = TransferKeyKind.Numeric,
            KeyColumn = "OrderId"
        };

        var sql = DataVerifier.KeyExpression(plan);
        Assert.Equal("[OrderId]", sql);
    }

    [Fact]
    public void KeyExpression_CộtTênChứaKýTựĐặcBiệt_KhôngVỡSql()
    {
        var plan = new TransferTablePlan
        {
            Schema = "my schema",
            Name = "weird]table",
            KeyKind = TransferKeyKind.Numeric,
            KeyColumn = "]]strange"
        };

        var sql = DataVerifier.KeyExpression(plan);
        Assert.Equal("[]]]]strange]", sql);
    }

    [Fact]
    public void Report_KhiChưaCóProblem_SuccessLàTrue()
    {
        var report = new DataVerificationReport
        {
            Tables = new[]
            {
                new TableVerificationResult { PlainName = "dbo.A", Status = VerificationStatus.VerifiedExact, SourceRowCount = 10, DestinationRowCount = 10 },
                new TableVerificationResult { PlainName = "dbo.B", Status = VerificationStatus.Empty }
            },
            VerifiedCount = 1,
            IssueCount = 0,
            FailedCount = 0,
            SkippedCount = 0
        };

        Assert.True(report.Success);
        Assert.Equal(2, report.TotalChecked);
    }

    [Fact]
    public void Report_KhiCóLệchSốDòng_SuccessLàFalse()
    {
        var report = new DataVerificationReport
        {
            Tables = new[]
            {
                new TableVerificationResult { PlainName = "dbo.A", Status = VerificationStatus.RowCountMismatch, SourceRowCount = 5, DestinationRowCount = 4 },
                new TableVerificationResult { PlainName = "dbo.B", Status = VerificationStatus.VerifiedExact, SourceRowCount = 1, DestinationRowCount = 1 }
            },
            VerifiedCount = 1,
            IssueCount = 1,
            FailedCount = 0,
            SkippedCount = 0
        };

        Assert.False(report.Success);
        Assert.Equal(1, report.IssueCount);
        Assert.Equal(2, report.TotalChecked);
    }

    [Fact]
    public void Report_KhiCóLỗiĐọc_FailedTínhVàoKhôngĐạt()
    {
        var report = new DataVerificationReport
        {
            Tables = new[]
            {
                new TableVerificationResult { PlainName = "dbo.C", Status = VerificationStatus.Failed, Message = "mất kết nối" }
            },
            VerifiedCount = 0,
            IssueCount = 0,
            FailedCount = 1,
            SkippedCount = 0
        };

        Assert.False(report.Success);
        Assert.Equal(1, report.FailedCount);
    }

    [Fact]
    public void Report_SkippedKhôngTínhVàoTotalChecked()
    {
        var report = new DataVerificationReport
        {
            Tables = new[]
            {
                new TableVerificationResult { PlainName = "dbo.A", Status = VerificationStatus.VerifiedExact },
                new TableVerificationResult { PlainName = "dbo.B", Status = VerificationStatus.Skipped }
            },
            VerifiedCount = 1,
            IssueCount = 0,
            FailedCount = 0,
            SkippedCount = 1
        };

        Assert.Equal(1, report.TotalChecked);
        Assert.True(report.Success);
    }

    [Fact]
    public void RowCountMatches_LàTrue_KhiHaiPhíaBằngNhau()
    {
        var good = new TableVerificationResult { SourceRowCount = 100, DestinationRowCount = 100 };
        var bad = new TableVerificationResult { SourceRowCount = 100, DestinationRowCount = 99 };

        Assert.True(good.RowCountMatches);
        Assert.False(bad.RowCountMatches);
    }
}