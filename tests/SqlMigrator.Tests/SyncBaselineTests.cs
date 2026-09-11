using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SqlMigrator.Core.Models;
using SqlMigrator.Core.Services;
using Xunit;

namespace SqlMigrator.Tests
{
    /// <summary>
    /// Kiểm tra các phần thuần (không cần server) của tính năng đồng bộ tăng dần:
    /// khóa file baseline, tuần tự hóa JSON, mô tả phiên bản.
    /// </summary>
    public class SyncBaselineTests
    {
        private static readonly string Source = "Server=.;Initial Catalog=SourceDb;User Id=sa;Password=Secret99;Encrypt=True;";
        private static readonly string Dest = "Server=dest;Initial Catalog=DestDb;User Id=sa;Password=Secret99;Encrypt=True;";

        [Fact]
        public void KeyOf_IsDeterministic()
        {
            var first = SyncBaselineStore.KeyOf(Source, Dest);
            var second = SyncBaselineStore.KeyOf(Source, Dest);
            Assert.Equal(first, second);
        }

        [Fact]
        public void KeyOf_DistinctForDifferentPairs()
        {
            var pairA = SyncBaselineStore.KeyOf(Source, Dest);
            var pairB = SyncBaselineStore.KeyOf(Dest, Source);
            var pairC = SyncBaselineStore.KeyOf(Source, Source.Replace("SourceDb", "OtherDb"));
            Assert.NotEqual(pairA, pairB);
            Assert.NotEqual(pairA, pairC);
        }

        [Fact]
        public void KeyOf_IsHexHash_NotPlaintext()
        {
            var key = SyncBaselineStore.KeyOf(Source, Dest);
            Assert.Equal(64, key.Length);
            Assert.All(key, ch => Assert.True(Uri.IsHexDigit(ch)));
            Assert.DoesNotContain("SourceDb", key);
            Assert.DoesNotContain("Secret99", key);
        }

        [Fact]
        public void LoadAsync_ReturnsNull_WhenNoBaselineExists()
        {
            var store = new SyncBaselineStore(new Microsoft.Extensions.Logging.Abstractions.NullLogger<SyncBaselineStore>());
            var loaded = store.LoadAsync(Source, Dest).GetAwaiter().GetResult();
            Assert.Null(loaded);
        }

        [Fact]
        public void Baseline_JsonRoundTrip_PreservesValues()
        {
            var baseline = new SyncBaseline
            {
                SourceKey = "abc",
                DestinationKey = "def",
                Tables = new System.Collections.Generic.Dictionary<string, TableBaseline>(StringComparer.OrdinalIgnoreCase)
                {
                    ["dbo.Orders"] = new TableBaseline
                    {
                        Schema = "dbo",
                        Name = "Orders",
                        RowCount = 42,
                        MaxIdentity = 100,
                        MaxRowVersion = 987654321,
                        IdentityColumn = "OrderId",
                        RowVersionColumn = "RowVersion",
                        PrimaryKeyColumns = new[] { "OrderId" }
                    }
                }
            };

            var json = JsonSerializer.Serialize(baseline);
            var restored = JsonSerializer.Deserialize<SyncBaseline>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            Assert.NotNull(restored);
            Assert.Equal(42, restored!.Tables["dbo.Orders"].RowCount);
            Assert.Equal(100, restored.Tables["dbo.Orders"].MaxIdentity);
            Assert.Equal(987654321, restored.Tables["dbo.Orders"].MaxRowVersion);
            Assert.Equal("OrderId", restored.Tables["dbo.Orders"].IdentityColumn);
            Assert.Equal(new[] { "OrderId" }, restored.Tables["dbo.Orders"].PrimaryKeyColumns);
        }

        [Fact]
        public void SyncTableChange_HasChanges_ReflectsDeltas()
        {
            var change = new SyncTableChange
            {
                Schema = "dbo",
                Name = "T",
                DeltaCount = 0,
                CanDetectExactRows = false,
                LastRowVersionChanged = true,
                RowCountChanged = false
            };
            Assert.True(change.HasChanges);
            Assert.Equal("dbo.T", change.PlainName);

            var noChange = new SyncTableChange
            {
                Schema = "dbo",
                Name = "T",
                DeltaCount = 0,
                LastRowVersionChanged = false,
                RowCountChanged = false
            };
            Assert.False(noChange.HasChanges);
        }

        [Fact]
        public void PreflightDescribeVersion_MapsKnownMajorVersions()
        {
            Assert.Equal("2022", PreflightResult.DescribeVersion(16));
            Assert.Equal("2019", PreflightResult.DescribeVersion(15));
            Assert.Equal("2008", PreflightResult.DescribeVersion(10));
            Assert.Equal("Server 99.0", PreflightResult.DescribeVersion(99));
        }
    }
}