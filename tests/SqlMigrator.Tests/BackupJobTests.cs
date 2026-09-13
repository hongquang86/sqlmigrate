using System.Collections.Generic;
using SqlMigrator.Core.Models;
using SqlMigrator.Core.Services.Backup;
using Xunit;

namespace SqlMigrator.Tests;

public class BackupJobTests
{
    private static BackupJob Job() => new()
    {
        Id = "abcdef1234567890",
        Name = "Sao lưu đêm",
        Frequency = BackupJobFrequency.Daily,
        TimeOfDay = "1:05",
        Databases = new List<string> { "REHOSPOS" }
    };

    [Fact]
    public void SafeTaskName_FolderAndShortId()
    {
        var name = TaskSchedulerService.SafeTaskName(Job());

        Assert.StartsWith("SqlMigrator\\", name);
        Assert.Contains("abcdef12", name);
        Assert.Contains("Sao lưu đêm", name);
    }

    [Fact]
    public void SafeTaskName_BlankName_Fallback()
    {
        var job = Job();
        job.Name = "  ";
        Assert.Contains("Job", TaskSchedulerService.SafeTaskName(job));
    }

    [Fact]
    public void BuildCreateArgs_Daily()
    {
        var args = TaskSchedulerService.BuildCreateArgs("C:\\App\\SqlMigrator.exe", Job());

        Assert.Contains("/Create", args);
        Assert.Contains("/SC DAILY", args);
        Assert.Contains("/ST 01:05", args);
        Assert.Contains("--run-backup abcdef1234567890", args);
        Assert.Contains("/F", args);
    }

    [Fact]
    public void BuildCreateArgs_HourlyUsesMinutes()
    {
        var job = Job();
        job.Frequency = BackupJobFrequency.Hourly;
        job.IntervalHours = 6;
        var args = TaskSchedulerService.BuildCreateArgs("C:\\App\\SqlMigrator.exe", job);

        Assert.Contains("/SC MINUTE /MO 360", args);
    }

    [Fact]
    public void BuildCreateArgs_Weekly()
    {
        var job = Job();
        job.Frequency = BackupJobFrequency.Weekly;
        job.DayOfWeek = "SAT";
        job.TimeOfDay = "23:30";
        var args = TaskSchedulerService.BuildCreateArgs("C:\\App\\SqlMigrator.exe", job);

        Assert.Contains("/SC WEEKLY /D SAT /ST 23:30", args);
    }

    [Theory]
    [InlineData("9:5", "09:05")]
    [InlineData("abc", "01:00")]
    [InlineData("", "01:00")]
    public void TimeCode_Cases(string input, string expected)
    {
        Assert.Equal(expected, TaskSchedulerService.TimeCode(input));
    }

    [Theory]
    [InlineData("mon", "MON")]
    [InlineData("xxx", "SUN")]
    public void DayCode_Cases(string input, string expected)
    {
        Assert.Equal(expected, TaskSchedulerService.DayCode(input));
    }
}
