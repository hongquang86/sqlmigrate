using System.Collections.Generic;
using SqlMigrator.Core.Models;
using SqlMigrator.Core.Services.Backup;
using Xunit;

namespace SqlMigrator.Tests;

public class BackupSqlBuilderTests
{
    [Fact]
    public void BuildBackupSql_FullCompressed_ExactSyntax()
    {
        var sql = BackupSqlBuilder.BuildBackupSql(new BackupRequest
        {
            Database = "REHOSPOS",
            BackupFile = @"D:\Bak\re.bak",
            FullBackup = true,
            Compression = true
        });

        Assert.Equal(
            "BACKUP DATABASE [REHOSPOS] TO DISK = N'D:\\Bak\\re.bak', COMPRESSION, STATS = 5;",
            sql);
    }

    [Fact]
    public void BuildBackupSql_DiffNoCompression_ExactSyntax()
    {
        var sql = BackupSqlBuilder.BuildBackupSql(new BackupRequest
        {
            Database = "Db",
            BackupFile = "f.bak",
            FullBackup = false,
            Compression = false
        });

        Assert.Contains("WITH DIFFERENTIAL", sql);
        Assert.Contains("NO_COMPRESSION", sql);
    }

    [Fact]
    public void BuildBackupSql_EscapesSingleQuoteInPath()
    {
        var sql = BackupSqlBuilder.BuildBackupSql(new BackupRequest
        {
            Database = "Db",
            BackupFile = @"D:\it's\a.bak"
        });

        Assert.Contains(@"N'D:\it''s\a.bak'", sql);
    }

    [Fact]
    public void BuildRestoreSql_MoveReplaceRecovery_ExactClauses()
    {
        var sql = BackupSqlBuilder.BuildRestoreSql(
            new RestoreRequest
            {
                Database = "NewDb",
                BackupFile = "f.bak",
                DataDirectory = @"D:\Data",
                LogDirectory = @"D:\Log",
                ReplaceExisting = true,
                WithRecovery = true
            },
            new List<BackupFileEntry>
            {
                new() { LogicalName = "Db_data", PhysicalName = @"D:\Old\Db.mdf", Type = "D" },
                new() { LogicalName = "Db_log", PhysicalName = @"D:\Old\Db_log.ldf", Type = "L" }
            });

        Assert.StartsWith("RESTORE DATABASE [NewDb] FROM DISK = N'f.bak' WITH ", sql);
        Assert.Contains("MOVE N'Db_data' TO N'D:\\Data\\Db_NewDb.mdf'", sql);
        Assert.Contains("MOVE N'Db_log' TO N'D:\\Log\\Db_log_NewDb.ldf'", sql);
        Assert.Contains("REPLACE", sql);
        Assert.Contains("RECOVERY", sql);
        Assert.DoesNotContain("NORECOVERY", sql);
    }

    [Fact]
    public void BuildRestoreSql_NoMoveNoReplace_NoRecovery()
    {
        var sql = BackupSqlBuilder.BuildRestoreSql(
            new RestoreRequest { Database = "Db", BackupFile = "f.bak", WithRecovery = false },
            new List<BackupFileEntry>());

        Assert.Equal(
            "RESTORE DATABASE [Db] FROM DISK = N'f.bak' WITH NORECOVERY, STATS = 5;",
            sql);
    }

    [Theory]
    [InlineData("10 percent processed.", 10)]
    [InlineData("100 percent processed.", 100)]
    [InlineData("Backup completed.", null)]
    [InlineData("", null)]
    public void ParsePercent_ExtractsProgressOrNull(string message, int? expected)
    {
        Assert.Equal(expected, BackupSqlBuilder.ParsePercent(message));
    }
}
