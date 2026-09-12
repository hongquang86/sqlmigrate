namespace SqlMigrator.Core.Models
{
    /// <summary>
    /// Khả năng của một engine theo từng nhóm chức năng. Pha 1 chỉ SQL Server
    /// đầy đủ; các engine khác mở dần theo roadmap (migrate → backup → manage).
    /// </summary>
    public sealed class ProviderCapabilities
    {
        public bool Schemas { get; init; }
        public bool Routines { get; init; }
        public bool Identity { get; init; }
        public bool Sequences { get; init; }
        public bool BulkCopy { get; init; }
        public bool NativeBackup { get; init; }
        public bool Jobs { get; init; }
        public bool KillSession { get; init; }
        /// <summary>Di chuyển schema+dữ liệu tới/đi engine này đã chạy được chưa.</summary>
        public bool Migration { get; init; }

        /// <summary>Bộ khả năng đầy đủ của SQL Server (baseline hiện tại).</summary>
        public static ProviderCapabilities SqlServerFull() => new()
        {
            Schemas = true,
            Routines = true,
            Identity = true,
            Sequences = true,
            BulkCopy = true,
            NativeBackup = true,
            Jobs = true,
            KillSession = true,
            Migration = true
        };

        /// <summary>Bộ khả năng tối thiểu: mới nhận diện, chưa migrate được.</summary>
        public static ProviderCapabilities DetectedOnly() => new();
    }
}
