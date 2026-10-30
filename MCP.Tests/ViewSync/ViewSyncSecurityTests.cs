using System;
using System.Linq;
using NUnit.Framework;
using RevitMCP.Core.ViewSync;

namespace RevitMCP.Tests.ViewSync
{
    [TestFixture]
    public class ViewSyncSecurityTests
    {
        private static readonly string[] Allowed = { "https://issue-manager-sigma.vercel.app", "http://localhost:3600" };

        private static PairedWebClient Client(string token, string origin) =>
            new PairedWebClient { TokenHash = TokenUtil.Hash(token), Origin = origin, CreatedUtc = DateTime.UtcNow };

        // ---------------------------------------------------------------- Origin

        [TestCase("https://issue-manager-sigma.vercel.app", true)]
        [TestCase("https://ISSUE-MANAGER-SIGMA.vercel.app", true)]
        [TestCase("https://issue-manager-sigma.vercel.app/", true)]
        [TestCase("http://localhost:3600", true)]
        [TestCase("http://localhost:3601", false)]
        [TestCase("http://localhost", false)]
        [TestCase("https://issue-manager-sigma.vercel.app.evil.com", false)]
        [TestCase("http://issue-manager-sigma.vercel.app", false)]
        [TestCase("https://evil.com", false)]
        [TestCase("null", false)]
        [TestCase("", false)]
        [TestCase(null, false)]
        [TestCase("https://user@issue-manager-sigma.vercel.app", false)]
        [TestCase("https://issue-manager-sigma.vercel.app/path", false)]
        [TestCase("file:///C:/x.html", false)]
        public void Origin_Whitelist(string origin, bool expected)
        {
            Assert.That(OriginPolicy.IsAllowed(origin, Allowed), Is.EqualTo(expected));
        }

        [Test]
        public void Origin_EmptyWhitelist_RejectsEverything()
        {
            Assert.That(OriginPolicy.IsAllowed("http://localhost:3600", new string[0]), Is.False);
            Assert.That(OriginPolicy.IsAllowed("http://localhost:3600", null), Is.False);
        }

        [Test]
        public void Origin_Normalize_DropsDefaultPort()
        {
            Assert.That(OriginPolicy.Normalize("https://Example.com:443"), Is.EqualTo("https://example.com"));
            Assert.That(OriginPolicy.Normalize("http://localhost:3600/"), Is.EqualTo("http://localhost:3600"));
        }

        [Test]
        public void Origin_DefaultAllowedOrigins_AreValid()
        {
            foreach (var o in OriginPolicy.DefaultAllowedOrigins) Assert.That(OriginPolicy.Normalize(o), Is.Not.Null, o);
            Assert.That(OriginPolicy.DefaultAllowedOrigins, Does.Contain("https://issue-manager-sigma.vercel.app"));
            Assert.That(OriginPolicy.DefaultAllowedOrigins, Does.Contain("http://localhost:3600"));
        }

        // ---------------------------------------------------------------- 指令白名單

        [Test]
        public void Commands_OnlyViewSyncAreBrowserAllowed()
        {
            foreach (var c in new[] { "viewsync_pair", "viewsync_status", "viewsync_apply_temp", "viewsync_open_view", "viewsync_create_view", "VIEWSYNC_STATUS" })
                Assert.That(ViewSyncCommands.IsBrowserAllowed(c), Is.True, c);
            foreach (var c in new[] { "create_wall", "delete_element", "export_web_compare", "open_document", "viewsync_debug_fov", "viewsync_status ", "", null, "viewsync_" })
                Assert.That(ViewSyncCommands.IsBrowserAllowed(c), Is.False, c ?? "(null)");
        }

        [Test]
        public void Commands_CameraManagerCommandsAreBrowserAllowed_AndNeedToken()
        {
            foreach (var c in new[] { "list_cameras", "set_camera_pose", "copy_camera_to_level", "run_camera_keyplan",
                                      "run_camera_overview", "create_camera_sheet", "camera_version", "get_view_extents", "LIST_CAMERAS" })
                Assert.That(ViewSyncCommands.IsBrowserAllowed(c), Is.True, c);
            // get_camera_info 只給 MCP 內部用；pair 之外不得靠相機指令繞過 token
            Assert.That(ViewSyncCommands.IsBrowserAllowed("get_camera_info"), Is.False);
            Assert.That(ViewSyncCommands.IsBrowserAllowed("list_cameras "), Is.False);
            Assert.That(ViewSyncCommands.IsPair("list_cameras"), Is.False);

            var token = TokenUtil.GenerateToken();
            var clients = new[] { Client(token, "http://localhost:3600") };
            Assert.That(ViewSyncAuthorizer.Check("http://localhost:3600", "set_camera_pose", token, Allowed, clients), Is.EqualTo(AuthDecision.Allowed));
            Assert.That(ViewSyncAuthorizer.Check("http://localhost:3600", "set_camera_pose", null, Allowed, clients), Is.EqualTo(AuthDecision.TokenMissing));
            Assert.That(ViewSyncAuthorizer.Check("http://localhost:3600", "set_camera_pose", token + "0", Allowed, clients), Is.EqualTo(AuthDecision.TokenInvalid));
            Assert.That(ViewSyncAuthorizer.Check("https://evil.com", "list_cameras", token, Allowed, clients), Is.EqualTo(AuthDecision.OriginRejected));
        }

        // ---------------------------------------------------------------- 授權決策

        [Test]
        public void Authorize_RejectsDisallowedOriginBeforeAnythingElse()
        {
            var clients = new[] { Client("tok", "https://evil.com") };
            Assert.That(ViewSyncAuthorizer.Check("https://evil.com", "viewsync_status", "tok", Allowed, clients), Is.EqualTo(AuthDecision.OriginRejected));
            Assert.That(ViewSyncAuthorizer.Check(null, "viewsync_pair", null, Allowed, clients), Is.EqualTo(AuthDecision.OriginRejected));
        }

        [Test]
        public void Authorize_RejectsNonWhitelistedCommandEvenWithValidToken()
        {
            var clients = new[] { Client("tok", "http://localhost:3600") };
            Assert.That(ViewSyncAuthorizer.Check("http://localhost:3600", "delete_element", "tok", Allowed, clients), Is.EqualTo(AuthDecision.CommandNotAllowed));
        }

        [Test]
        public void Authorize_PairNeedsNoToken_OthersDo()
        {
            Assert.That(ViewSyncAuthorizer.Check("http://localhost:3600", "viewsync_pair", null, Allowed, new PairedWebClient[0]), Is.EqualTo(AuthDecision.Allowed));
            Assert.That(ViewSyncAuthorizer.Check("http://localhost:3600", "viewsync_status", null, Allowed, new PairedWebClient[0]), Is.EqualTo(AuthDecision.TokenMissing));
            Assert.That(ViewSyncAuthorizer.Check("http://localhost:3600", "viewsync_status", "", Allowed, new PairedWebClient[0]), Is.EqualTo(AuthDecision.TokenMissing));
        }

        [Test]
        public void Authorize_Token_MatchesHashOnly()
        {
            var token = TokenUtil.GenerateToken();
            var clients = new[] { Client(token, "http://localhost:3600") };
            Assert.That(ViewSyncAuthorizer.Check("http://localhost:3600", "viewsync_status", token, Allowed, clients), Is.EqualTo(AuthDecision.Allowed));
            Assert.That(ViewSyncAuthorizer.Check("http://localhost:3600", "viewsync_status", token + "0", Allowed, clients), Is.EqualTo(AuthDecision.TokenInvalid));
            Assert.That(ViewSyncAuthorizer.Check("http://localhost:3600", "viewsync_status", clients[0].TokenHash, Allowed, clients), Is.EqualTo(AuthDecision.TokenInvalid), "雜湊本身不能當 token");
        }

        [Test]
        public void Authorize_Token_BoundToPairedOrigin()
        {
            var token = TokenUtil.GenerateToken();
            var clients = new[] { Client(token, "http://localhost:3600") };
            Assert.That(ViewSyncAuthorizer.Check("https://issue-manager-sigma.vercel.app", "viewsync_status", token, Allowed, clients), Is.EqualTo(AuthDecision.TokenInvalid));
        }

        [Test]
        public void Authorize_Revoked_NoClients_Rejected()
        {
            var token = TokenUtil.GenerateToken();
            Assert.That(ViewSyncAuthorizer.Check("http://localhost:3600", "viewsync_apply_temp", token, Allowed, new PairedWebClient[0]), Is.EqualTo(AuthDecision.TokenInvalid));
            Assert.That(ViewSyncAuthorizer.Check("http://localhost:3600", "viewsync_apply_temp", token, Allowed, null), Is.EqualTo(AuthDecision.TokenInvalid));
        }

        [Test]
        public void ErrorCodes_ExistForEveryRejection()
        {
            foreach (var d in new[] { AuthDecision.OriginRejected, AuthDecision.CommandNotAllowed, AuthDecision.TokenMissing, AuthDecision.TokenInvalid })
            {
                Assert.That(ViewSyncAuthorizer.ErrorCode(d), Is.Not.Null.And.Not.Empty);
                Assert.That(ViewSyncAuthorizer.ErrorMessage(d), Is.Not.Null.And.Not.Empty);
            }

            Assert.That(ViewSyncAuthorizer.ErrorCode(AuthDecision.Allowed), Is.Null);
        }

        // ---------------------------------------------------------------- token

        [Test]
        public void Token_IsRandom32BytesHex_AndHashIsStable()
        {
            var a = TokenUtil.GenerateToken(); var b = TokenUtil.GenerateToken();
            Assert.That(a, Has.Length.EqualTo(64));
            Assert.That(a, Does.Match("^[0-9a-f]{64}$"));
            Assert.That(a, Is.Not.EqualTo(b));
            Assert.That(TokenUtil.Hash(a), Is.EqualTo(TokenUtil.Hash(a)));
            Assert.That(TokenUtil.Hash(a), Is.Not.EqualTo(a));
            Assert.That(TokenUtil.Hash("abc"), Is.EqualTo("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"));
        }

        [Test]
        public void ConstantTimeEquals_Works()
        {
            Assert.That(TokenUtil.ConstantTimeEquals("abc", "abc"), Is.True);
            Assert.That(TokenUtil.ConstantTimeEquals("abc", "abd"), Is.False);
            Assert.That(TokenUtil.ConstantTimeEquals("abc", "ab"), Is.False);
            Assert.That(TokenUtil.ConstantTimeEquals(null, "ab"), Is.False);
        }

        // ---------------------------------------------------------------- 配對碼

        private sealed class Clock
        {
            public DateTime Now = new DateTime(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);
        }

        [Test]
        public void Pairing_Code_IsSixDigits_AndRedeemsOnce()
        {
            var clock = new Clock();
            var m = new PairingCodeManager(() => clock.Now);
            Assert.That(m.HasActiveCode, Is.False);
            Assert.That(m.TryRedeem("123456"), Is.EqualTo(PairingResult.NoActiveCode));
            var code = m.Issue();
            Assert.That(code, Does.Match("^[0-9]{6}$"));
            Assert.That(m.HasActiveCode, Is.True);
            Assert.That(m.TryRedeem(" " + code + " "), Is.EqualTo(PairingResult.Ok));
            Assert.That(m.TryRedeem(code), Is.EqualTo(PairingResult.NoActiveCode), "用過即失效");
        }

        [Test]
        public void Pairing_Code_ExpiresAfterFiveMinutes()
        {
            var clock = new Clock();
            var m = new PairingCodeManager(() => clock.Now);
            var code = m.Issue();
            Assert.That(m.ExpiresAtUtc, Is.EqualTo(clock.Now.AddMinutes(5)));
            clock.Now = clock.Now.AddMinutes(4).AddSeconds(59);
            Assert.That(m.HasActiveCode, Is.True);
            clock.Now = clock.Now.AddSeconds(2);
            Assert.That(m.HasActiveCode, Is.False);
            Assert.That(m.TryRedeem(code), Is.EqualTo(PairingResult.Expired));
            Assert.That(m.TryRedeem(code), Is.EqualTo(PairingResult.NoActiveCode));
        }

        [Test]
        public void Pairing_WrongGuesses_InvalidateCode()
        {
            var clock = new Clock();
            var m = new PairingCodeManager(() => clock.Now, maxFailures: 3);
            var code = m.Issue();
            var wrong = code == "000000" ? "000001" : "000000";
            Assert.That(m.TryRedeem(wrong), Is.EqualTo(PairingResult.Wrong));
            Assert.That(m.TryRedeem(wrong), Is.EqualTo(PairingResult.Wrong));
            Assert.That(m.TryRedeem(wrong), Is.EqualTo(PairingResult.TooManyAttempts));
            Assert.That(m.TryRedeem(code), Is.EqualTo(PairingResult.NoActiveCode), "整組作廢，連正確碼也不能用");
        }

        [Test]
        public void Pairing_Issue_ReplacesOldCode()
        {
            var m = new PairingCodeManager();
            string first = null, second = null;
            for (var i = 0; i < 50 && first == second; i++) { first = m.Issue(); second = m.Issue(); }
            Assert.That(first, Is.Not.EqualTo(second));
            Assert.That(m.TryRedeem(first), Is.EqualTo(PairingResult.Wrong));
            Assert.That(m.TryRedeem(second), Is.EqualTo(PairingResult.Ok));
        }

        [Test]
        public void Pairing_Cancel_RemovesCode()
        {
            var m = new PairingCodeManager();
            var code = m.Issue();
            m.Cancel();
            Assert.That(m.TryRedeem(code), Is.EqualTo(PairingResult.NoActiveCode));
        }

        [Test]
        public void Pairing_Codes_AreDistributed()
        {
            var m = new PairingCodeManager();
            var codes = Enumerable.Range(0, 200).Select(_ => m.Issue()).Distinct().Count();
            Assert.That(codes, Is.GreaterThan(150));
        }
    }
}
