using System.IO;
using System.Text;
using SqlMigrator.Core.Models;
using SqlMigrator.Core.Security;
using Xunit;

namespace SqlMigrator.Tests;

/// <summary>
/// Kiểm thử toàn bộ module bảo mật: DPAPI, kho profile, bộ dựng chuỗi kết nối an toàn.
/// Nhấn mạnh nguyên tắc "không bao giờ lưu/in mật khẩu dạng văn bản thuần".
/// </summary>
public class SecurityTests
{
    private sealed class MemoryProtector : IDataProtector
    {
        public byte[] Protect(byte[] plainBytes) => plainBytes;
        public byte[] Unprotect(byte[] protectedBytes) => protectedBytes;
        public string ProtectString(string plainText) => Convert.ToBase64String(Encoding.UTF8.GetBytes(plainText));
        public string UnprotectString(string protectedText) => Encoding.UTF8.GetString(Convert.FromBase64String(protectedText));
    }

    [Fact]
    public void DpapiProtector_MãHóaRồiGiảiMãTrảĐúngDữLiệu()
    {
        if (!OperatingSystem.IsWindows()) return;
        var protector = new DpapiDataProtector();
        var secret = System.Text.Encoding.UTF8.GetBytes("mat-khau-cua-toi");
        var encrypted = protector.Protect(secret);
        var decrypted = protector.Unprotect(encrypted);

        Assert.False(secret.SequenceEqual(encrypted), "Bản mã hóa không được bằng bản gốc.");
        Assert.Equal(secret, decrypted);
    }

    [Fact]
    public void ConnectionProfile_SafeSummaryKhôngChứaMậtKhẩu()
    {
        var protector = new MemoryProtector();
        var profile = new ConnectionProfile
        {
            Server = "myserver",
            Database = "sale",
            Authentication = AuthenticationMode.SqlLogin,
            UserName = "sa",
            ProtectedPassword = protector.Protect(System.Text.Encoding.UTF8.GetBytes("sup3rs3cret"))
        };

        Assert.DoesNotContain("sup3rs3cret", profile.SafeSummary);
        Assert.DoesNotContain(profile.SafeSummary, "Password");
    }

    [Fact]
    public void ConnectionProfile_SafeSummaryHiểnThịThôngTinKhôngNhạyCảm()
    {
        var profile = new ConnectionProfile
        {
            Id = "abc",
            Name = "prod",
            Server = "myserver",
            Database = "sale",
            Authentication = AuthenticationMode.Windows
        };

        Assert.Contains("myserver", profile.SafeSummary);
        Assert.Contains("sale", profile.SafeSummary);
    }

    [Fact]
    public void ProfileStore_UpsertLoadXóaHoạtĐộngĐúng()
    {
        var folder = Path.Combine(Path.GetTempPath(), "SqlMigratorTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var protector = new MockDataProtector();
            var store = new ConnectionProfileStore(protector, Path.Combine(folder, "profiles.json"));

            Assert.Empty(store.LoadAll());

            var profile = new ConnectionProfile
            {
                Name = "test",
                Server = "localhost",
                Database = "db1",
                Authentication = AuthenticationMode.SqlLogin,
                UserName = "sa",
                ProtectedPassword = protector.Protect(System.Text.Encoding.UTF8.GetBytes("pw"))
            };
            store.Upsert(profile);

            var loaded = store.LoadAll();
            Assert.Single(loaded);
            Assert.Equal("test", loaded[0].Name);
            Assert.Equal(profile.Id, loaded[0].Id);
            Assert.Equal("localhost", loaded[0].Server);
            Assert.Equal("sa", loaded[0].UserName);

            store.Delete(profile.Id);
            Assert.Empty(store.LoadAll());
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void ProfileStore_UpsertTrùngIdThayThếThayVìThêm()
    {
        var folder = Path.Combine(Path.GetTempPath(), "SqlMigratorTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var store = new ConnectionProfileStore(new MockDataProtector(), Path.Combine(folder, "profiles.json"));
            var p = new ConnectionProfile { Name = "first" };
            store.Upsert(p);
            p.Name = "second";
            store.Upsert(p);

            var all = store.LoadAll();
            Assert.Single(all);
            Assert.Equal("second", all[0].Name);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void ProfileStore_FileTrênDiskKhôngChứaVănBảnThuần()
    {
        var folder = Path.Combine(Path.GetTempPath(), "SqlMigratorTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var store = new ConnectionProfileStore(new MockDataProtector(), Path.Combine(folder, "profiles.json"));
            store.Upsert(new ConnectionProfile
            {
                Server = "srv",
                Database = "db",
                Authentication = AuthenticationMode.SqlLogin,
                UserName = "sa",
                ProtectedPassword = System.Text.Encoding.UTF8.GetBytes("secret!")
            });

            var raw = File.ReadAllText(Path.Combine(folder, "profiles.json"));
            Assert.DoesNotContain("secret!", raw);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void SecureBuilder_SetPasswordRồiGetPasswordTrảĐúngMậtKhẩu()
    {
        var builder = new SecureConnectionStringBuilder(new MockDataProtector());
        var profile = new ConnectionProfile
        {
            Server = "localhost",
            Database = "db1",
            Authentication = AuthenticationMode.SqlLogin,
            UserName = "sa"
        };

        builder.SetPassword(profile, "mat-khau-123");
        Assert.Equal("mat-khau-123", builder.GetPassword(profile));
    }

    [Fact]
    public void SecureBuilder_ProfileVớiMậtKhẩuSauBuildThìProtectedPasswordKhôngPhảiPlaintext()
    {
        var protector = new MockDataProtector();
        var builder = new SecureConnectionStringBuilder(protector);
        var profile = new ConnectionProfile
        {
            Server = "localhost",
            Authentication = AuthenticationMode.SqlLogin,
            UserName = "sa"
        };

        builder.SetPassword(profile, "abcXyz!");
        // Chuỗi mã hóa trên profile phải khác mật khẩu gốc (chứng minh đang mã hóa, không lưu plaintext).
        Assert.NotEqual(System.Text.Encoding.UTF8.GetBytes("abcXyz!"), profile.ProtectedPassword);
    }

    [Fact]
    public void SecureBuilder_LuônBậtMãHóaKếtNối()
    {
        var profile = new ConnectionProfile { Server = "myserver", Database = "db1", Authentication = AuthenticationMode.Windows };
        var builder = new SecureConnectionStringBuilder(new MockDataProtector());

        var cs = builder.Build(profile);
        Assert.Contains("Encrypt=True", cs, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Persist Security Info=False", cs, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SecureBuilder_WindowsAuthKhôngNhúngUserID()
    {
        var profile = new ConnectionProfile { Server = "myserver", Authentication = AuthenticationMode.Windows };
        var builder = new SecureConnectionStringBuilder(new MockDataProtector());

        var cs = builder.Build(profile);
        Assert.Contains("Integrated Security=True", cs, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("User ID=", cs, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class MockDataProtector : IDataProtector
    {
        // Bóp méo bằng XOR để Protect() cho ra khác dữ liệu gốc, Unprotect trả về gốc.
        private const byte Mask = 0x5A;
        public byte[] Protect(byte[] plainBytes) => plainBytes.Select(b => (byte)(b ^ Mask)).ToArray();
        public byte[] Unprotect(byte[] protectedBytes) => protectedBytes.Select(b => (byte)(b ^ Mask)).ToArray();
        public string ProtectString(string plainText) => Convert.ToBase64String(Protect(Encoding.UTF8.GetBytes(plainText)));
        public string UnprotectString(string protectedText) => Encoding.UTF8.GetString(Unprotect(Convert.FromBase64String(protectedText)));
    }
}