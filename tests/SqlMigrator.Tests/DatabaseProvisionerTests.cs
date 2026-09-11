using SqlMigrator.Core.Models;
using SqlMigrator.Core.Services;
using Xunit;

namespace SqlMigrator.Tests
{
    /// <summary>
    /// Kiểm thử lệnh CREATE DATABASE do <see cref="DatabaseProvisioner"/> sinh ra.
    /// Hồi quy quan trọng: COLLATE phải đứng SAU các mệnh đề ON / LOG ON, nếu không
    /// SQL Server báo "Incorrect syntax near the keyword 'ON'".
    /// </summary>
    public sealed class DatabaseProvisionerTests
    {
        [Fact]
        public void BuildCreateDatabaseSql_PutsCollateAfterFileClauses()
        {
            var sql = DatabaseProvisioner.BuildCreateDatabaseSql(
                "REHOSPOS", "Vietnamese_CI_AS",
                @"D:\Data\SQL", @"D:\Log\SQL");

            // COLLATE tuyệt đối phải nằm sau ON... LOG ON... chứ không phải trước nó.
            var fileClauseEnd = sql.IndexOf(" COLLATE V", StringComparison.OrdinalIgnoreCase);
            Assert.True(fileClauseEnd > 0, "Phải có mệnh đề COLLATE trong câu lệnh.");

            var onIndex = sql.IndexOf(" ON (", StringComparison.OrdinalIgnoreCase);
            var logOnIndex = sql.IndexOf(" LOG ON (", StringComparison.OrdinalIgnoreCase);
            Assert.True(onIndex > 0 && logOnIndex > onIndex,
                "Mệnh đề ON và LOG ON phải xuất hiện đúng thứ tự.");
            Assert.True(fileClauseEnd > logOnIndex,
                "COLLATE phải đứng SAU LOG ON — để trước sẽ gây lỗi 'Incorrect syntax near the keyword ON'.");

            Assert.StartsWith("CREATE DATABASE [REHOSPOS]", sql, StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith(";", sql, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void BuildCreateDatabaseSql_WithoutFolders_OmitsFileClauses()
        {
            var sql = DatabaseProvisioner.BuildCreateDatabaseSql(
                "AppDb", "SQL_Latin1_General_CP1_CI_AS", null, null);

            Assert.DoesNotContain(" ON (", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(" LOG ON (", sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(" COLLATE SQL_Latin1_General_CP1_CI_AS;", sql, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void BuildCreateDatabaseSql_QuoteIdentifier_ProtectsName()
        {
            var sql = DatabaseProvisioner.BuildCreateDatabaseSql(
                "x]; DROP DATABASE [y", "SQL_Latin1_General_CP1_CI_AS", null, null);

            // Tên DB được bọc kiểu [..]]..] nên dấu ] trong tên không thể thoát khỏi cặp ngoặc.
            Assert.StartsWith("CREATE DATABASE [x]]; DROP DATABASE [y]", sql);
            Assert.EndsWith(";", sql, StringComparison.OrdinalIgnoreCase);
        }
    }
}