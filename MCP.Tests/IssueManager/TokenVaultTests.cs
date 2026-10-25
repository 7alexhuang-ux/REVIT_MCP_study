using System;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using RevitMCP.Core.IssueManager;

namespace RevitMCP.Tests.IssueManager
{
    [TestFixture]
    public class TokenVaultTests
    {
        private string _dir;

        [SetUp] public void SetUp() { _dir = TempDir.Create("vault"); }
        [TearDown] public void TearDown() { TempDir.TryDelete(_dir); }

        private static StoredCredentials Sample(string refresh)
        {
            return new StoredCredentials
            {
                BaseUrl = "https://im.example.test", RefreshToken = refresh, RefreshExpiresAt = new DateTimeOffset(2026, 11, 25, 0, 0, 0, TimeSpan.Zero),
                UserId = "u1", UserEmail = "a@example.test", UserName = "A", UserRole = "admin", AccountRole = "member"
            };
        }

        private static bool Contains(byte[] haystack, byte[] needle)
        {
            for (var i = 0; i + needle.Length <= haystack.Length; i++)
            {
                var match = true;
                for (var j = 0; j < needle.Length && match; j++) match = haystack[i + j] == needle[j];
                if (match) return true;
            }
            return false;
        }

        [Test]
        public void X04_FileIsNotPlaintextAndRoundTrips()
        {
            var path = Path.Combine(_dir, "auth.bin");
            var vault = new DpapiTokenVault(path);
            const string secret = "imrt_SECRET-REFRESH-TOKEN-0123456789abcdefghijklmnop";
            vault.Save(Sample(secret));

            var raw = File.ReadAllBytes(path);
            Assert.IsFalse(Contains(raw, Encoding.UTF8.GetBytes(secret)), "UTF-8 明文不得出現在檔案內");
            Assert.IsFalse(Contains(raw, Encoding.Unicode.GetBytes(secret)), "UTF-16 明文不得出現在檔案內");
            Assert.IsFalse(Contains(raw, Encoding.UTF8.GetBytes("a@example.test")), "連 email 也不以明文落檔");
            Assert.AreNotEqual((byte)'{', raw[0], "不是 JSON 明文");
            Assert.IsFalse(File.Exists(path + ".tmp"), "暫存檔應已取代掉");

            var loaded = new DpapiTokenVault(path).Load();
            Assert.AreEqual(secret, loaded.RefreshToken);
            Assert.AreEqual("a@example.test", loaded.UserEmail);
            Assert.AreEqual(new DateTimeOffset(2026, 11, 25, 0, 0, 0, TimeSpan.Zero), loaded.RefreshExpiresAt);
        }

        [Test]
        public void X04_DeleteRemovesFile()
        {
            var path = Path.Combine(_dir, "auth.bin");
            var vault = new DpapiTokenVault(path);
            vault.Save(Sample("imrt_x"));
            Assert.IsTrue(File.Exists(path));
            vault.Delete();
            Assert.IsFalse(File.Exists(path));
            Assert.IsNull(vault.Load());
            Assert.DoesNotThrow(vault.Delete, "重複刪除不丟例外");
        }

        [Test]
        public void SaveOverwritesPreviousAtomically()
        {
            var path = Path.Combine(_dir, "nested", "auth.bin");
            var vault = new DpapiTokenVault(path);
            vault.Save(Sample("imrt_first"));
            vault.Save(Sample("imrt_second"));
            Assert.AreEqual("imrt_second", vault.Load().RefreshToken);
            Assert.AreEqual(1, Directory.GetFiles(Path.GetDirectoryName(path)).Length);
        }

        [Test]
        public void CorruptOrTamperedFileLoadsAsNull()
        {
            var path = Path.Combine(_dir, "auth.bin");
            var vault = new DpapiTokenVault(path);
            vault.Save(Sample("imrt_x"));
            var raw = File.ReadAllBytes(path);
            raw[raw.Length / 2] ^= 0xFF;
            File.WriteAllBytes(path, raw);
            Assert.IsNull(vault.Load());
            File.WriteAllText(path, "not a dpapi blob");
            Assert.IsNull(vault.Load());
        }

        [Test]
        public void MissingFileLoadsAsNull()
        {
            Assert.IsNull(new DpapiTokenVault(Path.Combine(_dir, "missing.bin")).Load());
        }

        [Test]
        public void DefaultPathIsUnderLocalAppDataNotRoaming()
        {
            var path = DpapiTokenVault.DefaultPath();
            StringAssert.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), path);
            StringAssert.Contains("issue-manager", path);
        }
    }

    [TestFixture]
    public class SettingsTests
    {
        [TestCase("https://im.example.test", "https://im.example.test")]
        [TestCase("https://im.example.test/", "https://im.example.test")]
        [TestCase("https://im.example.test/app/", "https://im.example.test/app")]
        [TestCase("http://127.0.0.1:8799", "http://127.0.0.1:8799")]
        [TestCase("http://localhost:3000/", "http://localhost:3000")]
        public void NormalizeAcceptsHttpsAndLoopbackHttp(string input, string expected)
        {
            Assert.AreEqual(expected, IssueManagerSettings.NormalizeBaseUrl(input));
        }

        [TestCase("http://im.example.test")]
        [TestCase("ftp://im.example.test")]
        [TestCase("not a url")]
        [TestCase("https://user:pass@im.example.test")]
        [TestCase("")]
        public void NormalizeRejectsCleartextAndGarbage(string input)
        {
            Assert.Throws<ArgumentException>(() => IssueManagerSettings.NormalizeBaseUrl(input));
        }

        [Test]
        public void ResolveReturnsNullWhenNothingConfigured()
        {
            Assert.IsNull(IssueManagerSettings.ResolveBaseUrl(name => null, Path.Combine(Path.GetTempPath(), "no-such-config.json")));
        }

        [Test]
        public void EnvironmentVariableWinsOverConfigFile()
        {
            var dir = TempDir.Create("settings");
            try
            {
                var path = Path.Combine(dir, "issue-manager.json");
                File.WriteAllText(path, "{\"baseUrl\":\"https://from-file.example.test\"}");
                Assert.AreEqual("https://from-file.example.test", IssueManagerSettings.ResolveBaseUrl(name => null, path));
                Assert.AreEqual("https://from-env.example.test",
                    IssueManagerSettings.ResolveBaseUrl(name => name == IssueManagerSettings.EnvironmentVariable ? "https://from-env.example.test" : null, path));
            }
            finally { TempDir.TryDelete(dir); }
        }

        [Test]
        public void ClientInfoSanitizesToServerLimits()
        {
            var info = ClientInfo.Create("PC\u0007-01 · Revit 2024" + new string('x', 200), "1.0.0.0 (build/9)" + new string('9', 50));
            Assert.LessOrEqual(info.Name.Length, 80);
            Assert.IsFalse(info.Name.Any(char.IsControl));
            Assert.LessOrEqual(info.Version.Length, 32);
            StringAssert.IsMatch("^[A-Za-z0-9._+-]*$", info.Version);
        }
    }
}
