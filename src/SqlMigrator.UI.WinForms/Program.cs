using Microsoft.Extensions.DependencyInjection;
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
    static void Main()
    {
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
    }
}