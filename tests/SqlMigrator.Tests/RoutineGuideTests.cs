using SqlMigrator.Core.Models;
using SqlMigrator.Core.Services.Manage;
using Xunit;

namespace SqlMigrator.Tests;

public class RoutineGuideTests
{
    [Theory]
    [InlineData("Procedure", DatabaseEngine.MySql, "LIMIT")]
    [InlineData("View", DatabaseEngine.MySql, "IFNULL")]
    [InlineData("Trigger", DatabaseEngine.MySql, "FOR EACH ROW")]
    [InlineData("View", DatabaseEngine.PostgreSql, "COALESCE")]
    [InlineData("Procedure", DatabaseEngine.PostgreSql, "PL/pgSQL")]
    [InlineData("Trigger", DatabaseEngine.SqlServer, "inserted")]
    [InlineData("Function", DatabaseEngine.SqlServer, "T-SQL")]
    public void GuidanceFor_KnownPairs_HasKeyHint(string kind, DatabaseEngine dst, string hint)
    {
        Assert.Contains(hint, RoutineGuideService.GuidanceFor(kind, dst));
    }

    [Fact]
    public void GuidanceFor_Sqlite_PointsToAppLayer()
    {
        Assert.Contains("tầng ứng dụng", RoutineGuideService.GuidanceFor("Procedure", DatabaseEngine.Sqlite));
    }

    [Fact]
    public void GuidanceFor_UnknownEngine_SaysUnsupported()
    {
        Assert.Contains("chưa hỗ trợ", RoutineGuideService.GuidanceFor("View", DatabaseEngine.MongoDb));
    }
}
