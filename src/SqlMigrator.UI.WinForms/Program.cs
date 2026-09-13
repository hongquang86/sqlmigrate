using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlMigrator.Core.Security;
using SqlMigrator.UI;

namespace SqlMigrator.UI.WinForms;

static class Program
{
    /// <summary>
    ///  Điểm vào chính của ứng dụng. Dựng container Dependency Injection cho toàn bộ
    ///  service rồi chạy cửa sổ chính.
    /// </summary>
    [STAThread]
    static int Main(string[] args)
    {
        // Chế độ headless cho Task Scheduler: SqlMigrator.exe --run-backup <jobId>
        // chạy job rồi thoát với mã lỗi, không mở giao diện.
        if (args.Length >= 2 && args[0].Equals("--run-backup", StringComparison.OrdinalIgnoreCase))
            return RunBackupJob(args[1]);

        ApplicationConfiguration.Initialize();

        var services = new ServiceCollection();
        services.AddSingleton<IDataProtector, DpapiDataProtector>();
        services.AddSingleton<IConnectionProfileStore, ConnectionProfileStore>();
        services.AddSingleton<SecureConnectionStringBuilder>();
        services.AddSingleton<MigrateTabPage>();
        services.AddSingleton<Components.BackupRestoreTabPage>();
        services.AddSingleton<Components.ManageTabPage>();
        services.AddSingleton<MainForm>();

        using var provider = services.BuildServiceProvider();
        Application.Run(provider.GetRequiredService<MainForm>());
        return 0;
    }

    /// <summary>Chạy job sao lưu headless, log ra file theo ngày (không log secret).</summary>
    private static int RunBackupJob(string jobId)
    {
        var logPath = Core.Services.Backup.BackupJobRunner.LogFilePath();
        var logger = new Core.Services.SqlMigratorLogger(logPath, "BackupJob");
        try
        {
            var protector = new DpapiDataProtector();
            var builder = new SecureConnectionStringBuilder(protector);
            var runner = new Core.Services.Backup.BackupJobRunner(protector, builder, logger);
            return runner.RunAsync(jobId).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Chạy job sao lưu thất bại.");
            return 1;
        }
    }
}