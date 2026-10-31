using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using RevitMCP.Core.IssueManager;

namespace RevitMCP.Tests.IssueManager
{
    /// <summary>記憶體內的假 Issue Manager 權杖端點：驗證 PKCE、單次授權碼、refresh 輪替與重用偵測。</summary>
    public sealed partial class FakeTokenServer
    {
        public const string BaseUrl = "https://im.example.test";
        private readonly object _gate = new object();
        private readonly Dictionary<string, string> _codeChallenges = new Dictionary<string, string>();
        private readonly HashSet<string> _usedCodes = new HashSet<string>();
        private readonly Dictionary<string, string> _refreshFamily = new Dictionary<string, string>();
        private readonly HashSet<string> _rotated = new HashSet<string>();
        private readonly HashSet<string> _revokedFamilies = new HashSet<string>();
        private readonly Dictionary<string, string> _accessFamily = new Dictionary<string, string>();
        private int _counter;

        public FakeHandler Handler { get; private set; }
        public int RefreshRequests;
        public int ExchangeRequests;
        public bool FailRefreshWith503;
        public int RevokeByBearer;
        public int RevokeByRefresh;
        /// <summary>模擬伺服器端認為 access 已過期：Bearer 一律 401。</summary>
        public bool ForceRevoke401;
        public int AccessExpiresInSeconds = 3600;
        public string LastIssuedAccess;
        public string LastIssuedRefresh;
        public string LastCode;
        public string LastVerifier;
        public string LastChallenge;
        public string LastState;
        public List<string> SecretsSeen = new List<string>();

        public FakeTokenServer()
        {
            Handler = new FakeHandler(Respond);
        }

        public HttpClient CreateClient() { return new HttpClient(Handler); }

        /// <summary>模擬使用者在授權頁按「授權」：產生授權碼並回傳（外掛的瀏覽器動作會把它帶回 loopback）。</summary>
        public string ApproveFromAuthorizeUrl(string authorizeUrl)
        {
            var query = ParseQuery(new Uri(authorizeUrl).Query);
            lock (_gate)
            {
                var code = "imac_" + Guid.NewGuid().ToString("N");
                _codeChallenges[code] = query["code_challenge"];
                LastCode = code;
                LastChallenge = query["code_challenge"];
                LastState = query["state"];
                SecretsSeen.Add(code);
                SecretsSeen.Add(query["state"]);
                return code;
            }
        }

        public static Dictionary<string, string> ParseQuery(string query)
        {
            var result = new Dictionary<string, string>();
            foreach (var pair in query.TrimStart('?').Split('&'))
            {
                var parts = pair.Split(new[] { '=' }, 2);
                result[Uri.UnescapeDataString(parts[0])] = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : string.Empty;
            }
            return result;
        }

        private static string Challenge(string verifier)
        {
            using (var sha = SHA256.Create()) return Pkce.Base64Url(sha.ComputeHash(Encoding.ASCII.GetBytes(verifier)));
        }

        public JObject IssueTokens(string family)
        {
            lock (_gate)
            {
                var access = "imat_a" + (++_counter) + Guid.NewGuid().ToString("N");
                var refresh = "imrt_r" + _counter + Guid.NewGuid().ToString("N");
                _accessFamily[access] = family;
                _refreshFamily[refresh] = family;
                LastIssuedAccess = access;
                LastIssuedRefresh = refresh;
                SecretsSeen.Add(access);
                SecretsSeen.Add(refresh);
                return new JObject
                {
                    ["token_type"] = "Bearer", ["scope"] = "addin", ["access_token"] = access, ["expires_in"] = AccessExpiresInSeconds,
                    ["refresh_token"] = refresh, ["refresh_expires_in"] = 30 * 24 * 3600,
                    ["user"] = new JObject { ["id"] = "u1", ["email"] = "fake.user@example.test", ["name"] = "Fake", ["accountRole"] = "member", ["role"] = "admin" }
                };
            }
        }

        public bool IsAccessValid(string access)
        {
            lock (_gate)
            {
                string family;
                return access != null && _accessFamily.TryGetValue(access, out family) && !_revokedFamilies.Contains(family);
            }
        }

        public void RevokeAllAccessTokens() { lock (_gate) { foreach (var family in _accessFamily.Values.Distinct().ToList()) _revokedFamilies.Add(family); } }

        private HttpResponseMessage Respond(RecordedRequest request)
        {
            var path = request.Uri.AbsolutePath;
            if (request.Method == HttpMethod.Post && path == "/api/addin/token")
            {
                var body = request.Json;
                var grant = (string)body["grant_type"];
                lock (_gate)
                {
                    if (grant == "authorization_code")
                    {
                        ExchangeRequests++;
                        var code = (string)body["code"];
                        var verifier = (string)body["code_verifier"];
                        LastVerifier = verifier;
                        SecretsSeen.Add(verifier);
                        string challenge;
                        if (!_codeChallenges.TryGetValue(code, out challenge) || !_usedCodes.Add(code)) return FakeHandler.Error(HttpStatusCode.BadRequest, "invalid_grant");
                        if (!Pkce.SecureEquals(Challenge(verifier), challenge)) return FakeHandler.Error(HttpStatusCode.BadRequest, "invalid_grant");
                        return FakeHandler.Json(HttpStatusCode.OK, IssueTokens(Guid.NewGuid().ToString("N")));
                    }

                    if (grant == "refresh_token")
                    {
                        RefreshRequests++;
                        if (FailRefreshWith503) return FakeHandler.Error(HttpStatusCode.ServiceUnavailable, "temporarily_unavailable");
                        var refresh = (string)body["refresh_token"];
                        string family;
                        if (!_refreshFamily.TryGetValue(refresh, out family) || _revokedFamilies.Contains(family)) return FakeHandler.Error(HttpStatusCode.BadRequest, "invalid_grant");
                        if (!_rotated.Add(refresh))
                        {
                            _revokedFamilies.Add(family); // 舊 refresh 重複使用 → 整族撤銷
                            return FakeHandler.Error(HttpStatusCode.BadRequest, "invalid_grant");
                        }
                        return FakeHandler.Json(HttpStatusCode.OK, IssueTokens(family));
                    }
                }
                return FakeHandler.Error(HttpStatusCode.BadRequest, "unsupported_grant_type");
            }

            if (request.Method == HttpMethod.Post && path == "/api/addin/token/revoke")
            {
                // addin-auth.md §4.4：Bearer 有效 → 撤銷該 access 的整族；Bearer 無效 → 401；
                // 沒有 Authorization 但 body 帶 refresh_token → 撤銷該 refresh 所屬的族（不存在也回 200）。
                lock (_gate)
                {
                    string family;
                    if (request.Authorization != null)
                    {
                        var access = request.Authorization.Replace("Bearer ", string.Empty);
                        if (IsAccessValid(access) && !ForceRevoke401 && _accessFamily.TryGetValue(access, out family))
                        {
                            _revokedFamilies.Add(family);
                            RevokeByBearer++;
                            return FakeHandler.Json(HttpStatusCode.OK, new JObject { ["revoked"] = true });
                        }
                        return FakeHandler.Error(HttpStatusCode.Unauthorized, "unauthorized");
                    }
                    var refresh = request.Json == null ? null : (string)request.Json["refresh_token"];
                    if (refresh == null) return FakeHandler.Error(HttpStatusCode.BadRequest, "invalid_request");
                    SecretsSeen.Add(refresh);
                    if (_refreshFamily.TryGetValue(refresh, out family)) _revokedFamilies.Add(family);
                    RevokeByRefresh++;
                    return FakeHandler.Json(HttpStatusCode.OK, new JObject { ["revoked"] = true });
                }
            }

            var token = request.Authorization == null ? null : request.Authorization.Replace("Bearer ", string.Empty);
            if (!IsAccessValid(token))
            {
                // 簽名上傳網址在別的主機時不需要 Bearer（模擬雲端儲存）；其他一律 401。
                if (!(path.StartsWith("/upload/", StringComparison.Ordinal) && request.Uri.Host != new Uri(BaseUrl).Host))
                    return FakeHandler.Error(HttpStatusCode.Unauthorized, "unauthorized");
            }
            var bundleResponse = RespondBundles(request);
            if (bundleResponse != null) return bundleResponse;
            if (path == "/api/addin/me") return FakeHandler.Json(HttpStatusCode.OK, new JObject { ["user"] = new JObject { ["email"] = "fake.user@example.test", ["role"] = "admin" } });
            if (path == "/api/addin/projects")
                return FakeHandler.Json(HttpStatusCode.OK, new JObject { ["projects"] = new JArray(new JObject { ["id"] = "p1", ["code"] = "CPC", ["name"] = "測試專案" }) });
            return FakeHandler.Error(HttpStatusCode.NotFound, "not_found");
        }
    }

    [TestFixture]
    public class AddinAuthServiceTests
    {
        private FakeTokenServer _server;
        private InMemoryVault _vault;
        private RecordingLog _log;
        private DateTimeOffset _now;
        private HttpClient _callbackClient;

        [SetUp]
        public void SetUp()
        {
            _server = new FakeTokenServer();
            _vault = new InMemoryVault();
            _log = new RecordingLog();
            _now = new DateTimeOffset(2026, 10, 25, 9, 0, 0, TimeSpan.Zero);
            _callbackClient = new HttpClient();
        }

        [TearDown] public void TearDown() { _callbackClient.Dispose(); }

        private AddinAuthService NewAuth()
        {
            return new AddinAuthService(_server.CreateClient(), _vault, FakeTokenServer.BaseUrl, ClientInfo.Create("PC-01 · Revit 2024", "1.0.0.0"),
                () => _now, _log.Write);
        }

        /// <summary>模擬瀏覽器：解析授權網址 → 使用者授權 → 導向 loopback callback。</summary>
        private ActionBrowser Browser(bool approve = true, string stateOverride = null)
        {
            return new ActionBrowser(url =>
            {
                var query = FakeTokenServer.ParseQuery(new Uri(url).Query);
                var port = int.Parse(query["port"]);
                var state = stateOverride ?? query["state"];
                string target;
                if (approve)
                {
                    var code = _server.ApproveFromAuthorizeUrl(url);
                    target = "http://127.0.0.1:" + port + "/callback?code=" + Uri.EscapeDataString(code) + "&state=" + Uri.EscapeDataString(state);
                }
                else
                {
                    target = "http://127.0.0.1:" + port + "/callback?error=access_denied&state=" + Uri.EscapeDataString(state);
                }
                Task.Run(async () => { try { await _callbackClient.GetAsync(target); } catch (Exception) { } });
            });
        }

        [Test]
        public async Task Login_UsesPkceS256AndStoresOnlyRefreshToken()
        {
            var auth = NewAuth();
            var browser = Browser();
            var user = await auth.LoginAsync(browser, TimeSpan.FromSeconds(10), CancellationToken.None);

            var query = FakeTokenServer.ParseQuery(new Uri(browser.LastUrl).Query);
            Assert.AreEqual("S256", query["code_challenge_method"]);
            Assert.AreEqual("1.0.0.0", query["client_version"]);
            StringAssert.StartsWith("PC-01", query["client_name"]);
            StringAssert.StartsWith(FakeTokenServer.BaseUrl + "/addin/authorize?", browser.LastUrl);
            Assert.That(int.Parse(query["port"]), Is.InRange(1024, 65535));
            Assert.AreEqual(Pkce.ComputeChallenge(_server.LastVerifier), _server.LastChallenge, "換權杖送的 verifier 必須能對上授權時的 challenge");

            Assert.IsTrue(auth.IsLoggedIn);
            Assert.AreEqual("fake.user@example.test", user.Email);
            Assert.AreEqual("admin", user.Role);
            Assert.AreEqual(_server.LastIssuedRefresh, _vault.Stored.RefreshToken);
            Assert.AreEqual(FakeTokenServer.BaseUrl, _vault.Stored.BaseUrl);
            Assert.AreEqual(1, _vault.SaveCount);
            CollectionAssert.DoesNotContain(new[] { _vault.Stored.UserEmail, _vault.Stored.UserName, _vault.Stored.UserRole, _vault.Stored.BaseUrl }, _server.LastIssuedAccess);
            Assert.AreEqual(_now.AddDays(30), _vault.Stored.RefreshExpiresAt);
        }

        [Test]
        public void Login_DeniedInBrowserThrowsDeniedAndSavesNothing()
        {
            var auth = NewAuth();
            var ex = Assert.ThrowsAsync<AddinApiException>(async () => await auth.LoginAsync(Browser(false), TimeSpan.FromSeconds(10), CancellationToken.None));
            Assert.AreEqual(AddinErrorKind.Denied, ex.Kind);
            Assert.AreEqual(0, _vault.SaveCount);
            Assert.AreEqual(0, _server.ExchangeRequests);
            Assert.IsFalse(auth.IsLoggedIn);
        }

        [Test]
        public void Login_TimeoutWhenBrowserNeverCallsBack()
        {
            var auth = NewAuth();
            var ex = Assert.ThrowsAsync<AddinApiException>(async () =>
                await auth.LoginAsync(new ActionBrowser(url => { }), TimeSpan.FromMilliseconds(300), CancellationToken.None));
            Assert.AreEqual(AddinErrorKind.Denied, ex.Kind);
            StringAssert.Contains("逾時", ex.Message);
            Assert.AreEqual(0, _server.ExchangeRequests);
        }

        [Test]
        public void Login_CallbackWithWrongStateNeverReachesTokenEndpoint()
        {
            var auth = NewAuth();
            Assert.ThrowsAsync<AddinApiException>(async () =>
                await auth.LoginAsync(Browser(true, "attacker-controlled-state"), TimeSpan.FromMilliseconds(600), CancellationToken.None));
            Assert.AreEqual(0, _server.ExchangeRequests, "state 不符的回呼不能被拿去換權杖");
        }

        [Test]
        public async Task GetAccessToken_ReturnsCachedWhileValidThenRefreshesAndPersistsRotatedRefresh()
        {
            var auth = NewAuth();
            await auth.LoginAsync(Browser(), TimeSpan.FromSeconds(10), CancellationToken.None);
            var firstAccess = _server.LastIssuedAccess;
            var firstRefresh = _server.LastIssuedRefresh;

            Assert.AreEqual(firstAccess, await auth.GetAccessTokenAsync(CancellationToken.None));
            Assert.AreEqual(0, _server.RefreshRequests);

            _now = _now.AddMinutes(59.5); // 離到期不到 60 秒的安全邊界
            var second = await auth.GetAccessTokenAsync(CancellationToken.None);
            Assert.AreEqual(1, _server.RefreshRequests);
            Assert.AreNotEqual(firstAccess, second);
            Assert.AreEqual(_server.LastIssuedRefresh, _vault.Stored.RefreshToken, "輪替後的新 refresh 必須立刻落檔");
            Assert.AreNotEqual(firstRefresh, _vault.Stored.RefreshToken);
            Assert.AreEqual(_now.AddDays(30), _vault.Stored.RefreshExpiresAt, "滑動式有效期重新計算");
        }

        [Test]
        public async Task GetAccessToken_ConcurrentCallsRefreshExactlyOnce()
        {
            var auth = NewAuth();
            await auth.LoginAsync(Browser(), TimeSpan.FromSeconds(10), CancellationToken.None);
            _now = _now.AddHours(2);
            var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => Task.Run(() => auth.GetAccessTokenAsync(CancellationToken.None))));
            Assert.AreEqual(1, _server.RefreshRequests, "同一個 refresh 並行使用會被判成外洩而撤銷整族");
            Assert.AreEqual(1, results.Distinct().Count());
            Assert.IsTrue(_server.IsAccessValid(results[0]));
        }

        [Test]
        public async Task Refresh_InvalidGrantClearsLocalLogin()
        {
            var auth = NewAuth();
            await auth.LoginAsync(Browser(), TimeSpan.FromSeconds(10), CancellationToken.None);
            _server.RevokeAllAccessTokens();
            _now = _now.AddHours(2);
            var ex = Assert.ThrowsAsync<AddinApiException>(async () => await auth.GetAccessTokenAsync(CancellationToken.None));
            Assert.AreEqual(AddinErrorKind.NeedLogin, ex.Kind);
            Assert.IsFalse(auth.IsLoggedIn);
            Assert.IsNull(_vault.Stored, "整族已作廢：本機檔一併刪除");
        }

        [Test]
        public async Task Refresh_TransientServerErrorKeepsLogin()
        {
            var auth = NewAuth();
            await auth.LoginAsync(Browser(), TimeSpan.FromSeconds(10), CancellationToken.None);
            _server.FailRefreshWith503 = true;
            _now = _now.AddHours(2);
            var ex = Assert.ThrowsAsync<AddinApiException>(async () => await auth.GetAccessTokenAsync(CancellationToken.None));
            Assert.AreEqual(AddinErrorKind.Server, ex.Kind);
            Assert.IsTrue(auth.IsLoggedIn);
            Assert.IsNotNull(_vault.Stored);
            _server.FailRefreshWith503 = false;
            Assert.IsNotNull(await auth.GetAccessTokenAsync(CancellationToken.None), "伺服器恢復後同一份 refresh 還能用");
        }

        [Test]
        public async Task TryRestore_RestoresFromVaultAndThenRefreshesWithoutBrowser()
        {
            var first = NewAuth();
            await first.LoginAsync(Browser(), TimeSpan.FromSeconds(10), CancellationToken.None);

            var second = NewAuth(); // 模擬 Revit 重開
            Assert.IsFalse(second.IsLoggedIn);
            Assert.IsTrue(second.TryRestore());
            Assert.IsTrue(second.IsLoggedIn);
            Assert.AreEqual("fake.user@example.test", second.CurrentUser.Email);
            var access = await second.GetAccessTokenAsync(CancellationToken.None);
            Assert.AreEqual(1, _server.RefreshRequests);
            Assert.IsTrue(_server.IsAccessValid(access));
        }

        [Test]
        public void TryRestore_DiscardsExpiredOrForeignServerCredentials()
        {
            _vault.Stored = new StoredCredentials { BaseUrl = FakeTokenServer.BaseUrl, RefreshToken = "imrt_old", RefreshExpiresAt = _now.AddDays(-1) };
            Assert.IsFalse(NewAuth().TryRestore());
            Assert.IsNull(_vault.Stored);

            _vault.Stored = new StoredCredentials { BaseUrl = "https://other.example.test", RefreshToken = "imrt_other", RefreshExpiresAt = _now.AddDays(5) };
            Assert.IsFalse(NewAuth().TryRestore());
            Assert.IsNull(_vault.Stored, "換了 Issue Manager 網址：舊權杖不能帶去新伺服器");
        }

        [Test]
        public async Task Logout_RevokesOnServerAndDeletesLocalFile()
        {
            var auth = NewAuth();
            await auth.LoginAsync(Browser(), TimeSpan.FromSeconds(10), CancellationToken.None);
            var access = _server.LastIssuedAccess;
            await auth.LogoutAsync(CancellationToken.None);
            Assert.IsFalse(_server.IsAccessValid(access));
            Assert.IsNull(_vault.Stored);
            Assert.IsFalse(auth.IsLoggedIn);
            Assert.AreEqual($"Bearer {access}", _server.Handler.Requests.Last(r => r.Uri.AbsolutePath == "/api/addin/token/revoke").Authorization);
        }

        [Test]
        public async Task Logout_StillClearsLocallyWhenServerUnreachable()
        {
            var auth = NewAuth();
            await auth.LoginAsync(Browser(), TimeSpan.FromSeconds(10), CancellationToken.None);
            _server.Handler.BeforeRespond = r => { if (r.Uri.AbsolutePath.EndsWith("/revoke")) throw new HttpRequestException("offline"); return Task.FromResult(0); };
            var outcome = await auth.LogoutAsync(CancellationToken.None);
            Assert.AreEqual(LogoutOutcome.ServerRevokeFailed, outcome, "撤銷失敗必須如實回報，讓視窗告知使用者");
            Assert.IsNull(_vault.Stored);
            Assert.IsFalse(auth.IsLoggedIn);
        }

        [Test]
        public async Task Logout_ReportsRevokedWhenBearerWorks()
        {
            var auth = NewAuth();
            await auth.LoginAsync(Browser(), TimeSpan.FromSeconds(10), CancellationToken.None);
            Assert.AreEqual(LogoutOutcome.Revoked, await auth.LogoutAsync(CancellationToken.None));
            Assert.AreEqual(1, _server.RevokeByBearer);
            Assert.AreEqual(0, _server.RevokeByRefresh);
        }

        [Test]
        public async Task Logout_AfterAccessExpired_RevokesByRefreshToken()
        {
            var auth = NewAuth();
            await auth.LoginAsync(Browser(), TimeSpan.FromSeconds(10), CancellationToken.None);
            var access = _server.LastIssuedAccess;
            var refresh = _server.LastIssuedRefresh;
            _now = _now.AddHours(2); // access 已過期，refresh 仍有效

            var outcome = await auth.LogoutAsync(CancellationToken.None);

            Assert.AreEqual(LogoutOutcome.Revoked, outcome);
            Assert.AreEqual(0, _server.RevokeByBearer, "過期的 access 不能拿去當 Bearer");
            Assert.AreEqual(1, _server.RevokeByRefresh);
            Assert.IsFalse(_server.IsAccessValid(access), "伺服器端整族已撤銷");
            Assert.IsNull(_vault.Stored);
            StringAssert.DoesNotContain(refresh, _log.All);
        }

        [Test]
        public async Task Logout_BearerRejectedWith401_FallsBackToRefreshToken()
        {
            var auth = NewAuth();
            await auth.LoginAsync(Browser(), TimeSpan.FromSeconds(10), CancellationToken.None);
            var access = _server.LastIssuedAccess;
            _server.ForceRevoke401 = true; // 本機時鐘以為 access 還有效，但伺服器已視為無效

            var outcome = await auth.LogoutAsync(CancellationToken.None);

            Assert.AreEqual(LogoutOutcome.Revoked, outcome);
            Assert.AreEqual(1, _server.RevokeByRefresh, "Bearer 被 401 之後必須改用 refresh_token 撤銷");
            Assert.IsFalse(_server.IsAccessValid(access));
            Assert.IsNull(_vault.Stored);
        }

        [Test]
        public async Task Logout_AfterRestoreFromDisk_RevokesByRefreshToken()
        {
            await NewAuth().LoginAsync(Browser(), TimeSpan.FromSeconds(10), CancellationToken.None);
            var access = _server.LastIssuedAccess;
            var restored = NewAuth(); // 重開 Revit：只有 DPAPI 檔裡的 refresh，沒有 access
            Assert.IsTrue(restored.TryRestore());

            Assert.AreEqual(LogoutOutcome.Revoked, await restored.LogoutAsync(CancellationToken.None));
            Assert.AreEqual(1, _server.RevokeByRefresh);
            Assert.IsFalse(_server.IsAccessValid(access));
            Assert.IsNull(_vault.Stored);
        }

        [Test]
        public async Task Logout_BothBearerAndRefreshRejected_ReportsFailureButClearsLocal()
        {
            var auth = NewAuth();
            await auth.LoginAsync(Browser(), TimeSpan.FromSeconds(10), CancellationToken.None);
            _server.Handler.BeforeRespond = r => { if (r.Uri.AbsolutePath.EndsWith("/revoke")) throw new HttpRequestException("offline"); return Task.FromResult(0); };
            _now = _now.AddHours(2);

            Assert.AreEqual(LogoutOutcome.ServerRevokeFailed, await auth.LogoutAsync(CancellationToken.None));
            Assert.IsNull(_vault.Stored, "撤銷失敗時本機登入資料仍要刪除");
            Assert.IsFalse(auth.IsLoggedIn);
        }

        [Test]
        public async Task Logout_WhenNothingIsValidAnymore_SaysNothingToRevoke()
        {
            var auth = NewAuth();
            await auth.LoginAsync(Browser(), TimeSpan.FromSeconds(10), CancellationToken.None);
            _now = _now.AddDays(40); // access 與 refresh 都過期

            Assert.AreEqual(LogoutOutcome.NothingToRevoke, await auth.LogoutAsync(CancellationToken.None));
            Assert.AreEqual(0, _server.RevokeByBearer + _server.RevokeByRefresh);
            Assert.IsNull(_vault.Stored);
        }

        [Test]
        public async Task A10_NoSecretEverAppearsInLogsOrErrorMessages()
        {
            var auth = NewAuth();
            var messages = new List<string>();
            await auth.LoginAsync(Browser(), TimeSpan.FromSeconds(10), CancellationToken.None);
            _now = _now.AddHours(2);
            await auth.GetAccessTokenAsync(CancellationToken.None);
            _server.FailRefreshWith503 = true;
            _now = _now.AddHours(2);
            try { await auth.GetAccessTokenAsync(CancellationToken.None); } catch (AddinApiException ex) { messages.Add(ex.Message); }
            _server.FailRefreshWith503 = false;
            _server.RevokeAllAccessTokens();
            try { await auth.GetAccessTokenAsync(CancellationToken.None); } catch (AddinApiException ex) { messages.Add(ex.Message); }
            try { await NewAuth().LoginAsync(Browser(false), TimeSpan.FromSeconds(5), CancellationToken.None); } catch (AddinApiException ex) { messages.Add(ex.Message); }
            await auth.LogoutAsync(CancellationToken.None);

            var everything = _log.All + "\n" + string.Join("\n", messages);
            foreach (var secret in _server.SecretsSeen.Where(s => !string.IsNullOrEmpty(s)))
                StringAssert.DoesNotContain(secret, everything);
            Assert.IsNotEmpty(_server.SecretsSeen);
        }
    }
}
