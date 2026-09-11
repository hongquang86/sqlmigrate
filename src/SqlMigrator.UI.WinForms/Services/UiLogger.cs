using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlMigrator.Core;
using SqlMigrator.Core.Interfaces;
using SqlMigrator.Core.Models;
using SqlMigrator.Core.Security;
using SqlMigrator.Core.Services;

namespace SqlMigrator.UI.Services
{
    /// <summary>
    /// ILogger đưa dòng nhật ký vào khung nhật ký real-time của màn hình Chạy thông qua
    /// một delegate. Sink được gọi từ luồng chạy migration nên bên trong phải đảm bảo
    /// gọi đúng luồng UI (Control.Invoke).
    /// </summary>
    public sealed class UiLogger : ILogger
    {
        private readonly Action<string> _sink;
        private readonly string _category;

        public UiLogger(Action<string> sink, string category)
        {
            _sink = sink ?? throw new ArgumentNullException(nameof(sink));
            _category = category ?? throw new ArgumentNullException(nameof(category));
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            if (exception != null)
                message += Environment.NewLine + exception.ToString();

            var levelTag = logLevel switch
            {
                LogLevel.Trace => "DUYỆT",
                LogLevel.Debug => "GỠ RỐI",
                LogLevel.Warning => "CẢNH BÁO",
                LogLevel.Error => "LỖI",
                LogLevel.Critical => "NGHIÊM TRỌNG",
                _ => "THÔNG TIN"
            };

            _sink(string.Format("[{0:HH:mm:ss.fff}] [{1}] {2}", DateTime.Now, levelTag, message));
        }
    }

    /// <summary>Bó lại các service cần cho màn hình Chạy (orchestrator + đồng bộ tăng dần).</summary>
    public sealed class MigrationContext
    {
        public required MigrationOrchestrator Orchestrator { get; init; }
        public required IPreflightChecker PreflightChecker { get; init; }
        public required DatabaseSyncChecker SyncChecker { get; init; }
        public required IncrementalDataUpdater SyncUpdater { get; init; }
        public required SyncBaselineStore BaselineStore { get; init; }
        public required ISchemaExtractor SchemaExtractor { get; init; }
        public required IDataVerifier DataVerifier { get; init; }
        public required IDatabaseReconciler DatabaseReconciler { get; init; }
    }

    /// <summary>
    /// Dựng đầy đủ các service Core với Dependency Injection theo từng lần chạy.
    /// Logger và MigrationOptions của lần chạy hiện tại được cấp trực tiếp.
    /// </summary>
    public static class MigrationPipelineFactory
    {
        private static ServiceProvider BuildProvider(MigrationOptions options, ILogger logger, IDataProtector protector)
        {
            var services = new ServiceCollection();
            services.AddSingleton(options);
            services.AddSingleton<ILogger>(logger);
            services.AddSingleton<IDataProtector>(protector);
            services.AddSingleton<ISchemaExtractor, SchemaExtractor>();
            services.AddSingleton<ICompatibilityChecker, VersionCompatibilityChecker>();
            services.AddSingleton<ISchemaBuilder, SchemaBuilder>();
            services.AddSingleton<IConstraintManager, ConstraintManager>();
            services.AddSingleton<IDataCopier, DataCopier>();
            services.AddSingleton<IDataVerifier, DataVerifier>();
            services.AddSingleton<IPreflightChecker, PreflightChecker>();
            services.AddSingleton<IDatabaseReconciler, DatabaseReconciler>();
            services.AddSingleton<SyncBaselineStore>();
            services.AddSingleton<DatabaseSyncChecker>();
            services.AddSingleton<IncrementalDataUpdater>();
            services.AddSingleton<MigrationOrchestrator>();
            return services.BuildServiceProvider();
        }

        public static MigrationOrchestrator Create(MigrationOptions options, ILogger logger, IDataProtector protector)
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(logger);
            var provider = BuildProvider(options, logger, protector);
            return provider.GetRequiredService<MigrationOrchestrator>();
        }

        public static MigrationContext CreateContext(MigrationOptions options, ILogger logger, IDataProtector protector)
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(logger);
            var provider = BuildProvider(options, logger, protector);
            return new MigrationContext
            {
                Orchestrator = provider.GetRequiredService<MigrationOrchestrator>(),
                PreflightChecker = provider.GetRequiredService<IPreflightChecker>(),
                SyncChecker = provider.GetRequiredService<DatabaseSyncChecker>(),
                SyncUpdater = provider.GetRequiredService<IncrementalDataUpdater>(),
                BaselineStore = provider.GetRequiredService<SyncBaselineStore>(),
                SchemaExtractor = provider.GetRequiredService<ISchemaExtractor>(),
                DataVerifier = provider.GetRequiredService<IDataVerifier>(),
                DatabaseReconciler = provider.GetRequiredService<IDatabaseReconciler>()
            };
        }
    }
}