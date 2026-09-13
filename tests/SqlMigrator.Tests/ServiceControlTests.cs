using SqlMigrator.Core.Services.Manage;
using Xunit;

namespace SqlMigrator.Tests;

public class ServiceControlTests
{
    [Theory]
    [InlineData("STATE              : 4  RUNNING", WindowsServiceState.Running)]
    [InlineData("STATE : 1 STOPPED", WindowsServiceState.Stopped)]
    [InlineData("state : 2 START_PENDING", WindowsServiceState.StartPending)]
    [InlineData("STATE : 3 STOP_PENDING", WindowsServiceState.StopPending)]
    [InlineData("STATE : 7 PAUSED", WindowsServiceState.Paused)]
    [InlineData("garbage", WindowsServiceState.Unknown)]
    [InlineData("", WindowsServiceState.Unknown)]
    public void ParseState_Cases(string output, WindowsServiceState expected)
    {
        Assert.Equal(expected, ServiceControlService.ParseState(output));
    }

    [Fact]
    public void ParseDisplayName_Extracts()
    {
        Assert.Equal("SQL Server (MSSQLSERVER)",
            ServiceControlService.ParseDisplayName("DISPLAY_NAME : SQL Server (MSSQLSERVER)", "x"));
        Assert.Equal("x", ServiceControlService.ParseDisplayName("nothing", "x"));
    }

    [Theory]
    [InlineData("", "")]
    [InlineData(".", "")]
    [InlineData("localhost", "")]
    [InlineData("db01", "\\\\db01")]
    [InlineData("\\\\db01", "\\\\db01")]
    public void MachineArg_Cases(string input, string expected)
    {
        Assert.Equal(expected, ServiceControlService.MachineArg(input));
    }

    [Theory]
    [InlineData("SELECT 1", true)]
    [InlineData("  with x as (select 1) select * from x", true)]
    [InlineData("SHOW DATABASES", true)]
    [InlineData("UPDATE t SET a = 1", false)]
    [InlineData("KILL 51", false)]
    public void IsSelectLike_Cases(string sql, bool expected)
    {
        Assert.Equal(expected, QueryRunnerService.IsSelectLike(sql));
    }
}
