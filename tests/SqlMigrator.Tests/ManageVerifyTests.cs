using SqlMigrator.Core.Services.Manage;
using Xunit;

namespace SqlMigrator.Tests;

public class ManageVerifyTests
{
    [Theory]
    [InlineData("running", null, true)]
    [InlineData("running", "", true)]
    [InlineData("running", "killed", true)]
    [InlineData("running", "rollback", true)]
    [InlineData("sleep", "Sleep", false)]
    [InlineData("running", "running", false)]
    public void IsKillConfirmed_StateChangeRule(string before, string? after, bool expected)
    {
        Assert.Equal(expected, ManageVerify.IsKillConfirmed(before, after));
    }

    [Fact]
    public void IsKillConfirmed_CaseInsensitive()
    {
        Assert.True(ManageVerify.IsKillConfirmed("RUNNING", "Killed"));
        Assert.False(ManageVerify.IsKillConfirmed("Sleep", "SLEEP"));
    }
}
