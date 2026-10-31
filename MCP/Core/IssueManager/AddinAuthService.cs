#nullable disable
using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace RevitMCP.Core.IssueManager
{
    /// <summary>開啟系統預設瀏覽器（測試時換成不開瀏覽器的版本）。</summary>
    public interface IBrowserLauncher
    {
        void Open(string url);
    }

    /// <summary>登出時伺服器端撤銷的結果（本機登入資料不論結果一律已刪除）。</summary>
    public enum LogoutOutcome
    {
        /// <summary>伺服器已確認撤銷。</summary>
        Revoked,
        /// <summary>本機沒有仍有效的權杖（已過期或沒登入），沒有需要撤銷的授權。</summary>
        NothingToRevoke,
        /// <summary>撤銷沒有成功（網路、權杖被拒等）；伺服器端這一族授權可能仍有效，應請使用者到網站帳號頁手動撤銷。</summary>
        ServerRevokeFailed
    }

    /// <summary>
    /// 外掛登入（D-596／addin-auth.md）：授權碼＋PKCE＋loopback，權杖安全保存、refresh 輪替。
    /// 執行緒安全：refresh 以單一鎖序列化——同一個 refresh token 被兩個請求同時使用，
    /// 會被伺服器判成「舊 refresh 重複使用」而撤銷整族授權，所以絕不並行換發。
    /// 日誌（log）只記步驟與固定訊息，不含權杖、授權碼、verifier、state。
    /// </summary>
    public sealed class AddinAuthService
    {
        private static readonly TimeSpan AccessSkew = TimeSpan.FromSeconds(60);

        private readonly HttpClient _http;
        private readonly ITokenVault _vault;
        private readonly string _baseUrl;
        private readonly ClientInfo _client;
        private readonly Func<DateTimeOffset> _now;
        private readonly Action<string> _log;
        private readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);

        private string _accessToken;
        private DateTimeOffset _accessExpiresAt;
        private string _refreshToken;
        private DateTimeOffset _refreshExpiresAt;
        private AddinUser _user;

        public AddinAuthService(HttpClient http, ITokenVault vault, string baseUrl, ClientInfo client,
            Func<DateTimeOffset> now = null, Action<string> log = null)
        {
            if (http == null) throw new ArgumentNullException("http");
            if (vault == null) throw new ArgumentNullException("vault");
            _http = http;
            _vault = vault;
            _baseUrl = IssueManagerSettings.NormalizeBaseUrl(baseUrl);
            _client = client ?? ClientInfo.Create("Revit", "0");
            _now = now ?? (() => DateTimeOffset.UtcNow);
            _log = log ?? (s => { });
        }

        public string BaseUrl { get { return _baseUrl; } }
        public AddinUser CurrentUser { get { return _user; } }
        public bool IsLoggedIn { get { return _user != null && !string.IsNullOrEmpty(_refreshToken); } }

        /// <summary>啟動時從 DPAPI 檔還原（不連網）。網址不同或 refresh 已過期就丟掉舊資料。</summary>
        public bool TryRestore()
        {
            var stored = _vault.Load();
            if (stored == null || string.IsNullOrEmpty(stored.RefreshToken)) return false;
            if (!string.Equals(stored.BaseUrl, _baseUrl, StringComparison.OrdinalIgnoreCase) || stored.RefreshExpiresAt <= _now())
            {
                _vault.Delete();
                return false;
            }
            _refreshToken = stored.RefreshToken;
            _refreshExpiresAt = stored.RefreshExpiresAt;
            _user = new AddinUser
            {
                Id = stored.UserId, Email = stored.UserEmail, Name = stored.UserName,
                Role = stored.UserRole, AccountRole = stored.AccountRole
            };
            _log("已從本機還原登入狀態。");
            return true;
        }

        public string BuildAuthorizeUrl(string state, string challenge, int port)
        {
            return _baseUrl + "/addin/authorize"
                + "?state=" + Uri.EscapeDataString(state)
                + "&code_challenge=" + Uri.EscapeDataString(challenge)
                + "&code_challenge_method=S256"
                + "&port=" + port
                + "&client_version=" + Uri.EscapeDataString(_client.Version ?? string.Empty)
                + "&client_name=" + Uri.EscapeDataString(_client.Name ?? string.Empty);
        }

        public async Task<AddinUser> LoginAsync(IBrowserLauncher browser, TimeSpan callbackTimeout, CancellationToken ct)
        {
            if (browser == null) throw new ArgumentNullException("browser");
            var state = Pkce.CreateState();
            var verifier = Pkce.CreateVerifier();
            var challenge = Pkce.ComputeChallenge(verifier);

            CallbackResult callback;
            using (var listener = LoopbackCallbackListener.Start(state))
            {
                _log("已開啟本機回呼監聽（127.0.0.1），準備開啟瀏覽器。");
                browser.Open(BuildAuthorizeUrl(state, challenge, listener.Port));
                try
                {
                    callback = await listener.WaitForCallbackAsync(callbackTimeout, ct).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    throw new AddinApiException(AddinErrorKind.Denied, "等不到瀏覽器的授權回應（逾時），請再按一次「登入」。");
                }
            }

            if (!string.IsNullOrEmpty(callback.Error))
                throw new AddinApiException(AddinErrorKind.Denied, "你在瀏覽器取消了授權。");

            var response = await PostTokenAsync(new JObject
            {
                ["grant_type"] = "authorization_code",
                ["code"] = callback.Code,
                ["code_verifier"] = verifier
            }, ct).ConfigureAwait(false);

            await _lock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                Apply(response);
            }
            finally
            {
                _lock.Release();
            }
            _log("登入完成。");
            return _user;
        }

        /// <summary>取得可用的 access token；快過期就用 refresh 換新。</summary>
        public Task<string> GetAccessTokenAsync(CancellationToken ct)
        {
            return GetAccessTokenCoreAsync(null, ct);
        }

        /// <summary>呼叫端拿到 401 之後呼叫：強制換新（若別的請求已經換過就直接用新的）。</summary>
        public Task<string> RefreshAfterUnauthorizedAsync(string rejectedAccessToken, CancellationToken ct)
        {
            return GetAccessTokenCoreAsync(rejectedAccessToken ?? string.Empty, ct);
        }

        private async Task<string> GetAccessTokenCoreAsync(string rejected, CancellationToken ct)
        {
            await _lock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_accessToken != null && _accessExpiresAt - AccessSkew > _now()
                    && (rejected == null || !string.Equals(rejected, _accessToken, StringComparison.Ordinal)))
                    return _accessToken;

                if (string.IsNullOrEmpty(_refreshToken)) throw NeedLogin();
                if (_refreshExpiresAt <= _now())
                {
                    ClearLocal();
                    throw NeedLogin();
                }

                JObject response;
                try
                {
                    response = await PostTokenAsync(new JObject
                    {
                        ["grant_type"] = "refresh_token",
                        ["refresh_token"] = _refreshToken
                    }, ct).ConfigureAwait(false);
                }
                catch (AddinApiException ex) when (ex.Kind == AddinErrorKind.Rejected || ex.Kind == AddinErrorKind.NeedLogin)
                {
                    // invalid_grant：被撤銷、過期或重複使用 → 這一族授權已死，必須重新登入。
                    ClearLocal();
                    throw NeedLogin();
                }

                Apply(response);
                return _accessToken;
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// 登出：先撤銷伺服器端這一族授權，再刪本機資料（不論撤銷成不成功，本機一定刪）。
        /// access 還沒過期就用 Bearer 撤銷；access 已過期、被伺服器拒絕（401）或根本沒有 access（重開 Revit 後從 DPAPI 還原）
        /// 就改用 refresh_token 撤銷（addin-auth.md §4.4）。回傳撤銷結果，呼叫端必須在失敗時如實告知使用者。
        /// 整段持有 refresh 鎖：登出期間不會有並行的 refresh 輪替改掉要撤銷的權杖。
        /// </summary>
        public async Task<LogoutOutcome> LogoutAsync(CancellationToken ct)
        {
            await _lock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                return await RevokeOnServerAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                ClearLocal();
                _lock.Release();
            }
        }

        private async Task<LogoutOutcome> RevokeOnServerAsync(CancellationToken ct)
        {
            var now = _now();
            var accessUsable = !string.IsNullOrEmpty(_accessToken) && _accessExpiresAt - AccessSkew > now;
            var refreshUsable = !string.IsNullOrEmpty(_refreshToken) && _refreshExpiresAt > now;
            if (!accessUsable && !refreshUsable)
            {
                // 兩種權杖都已過期或根本沒有：伺服器端沒有還有效的授權可撤銷。
                _log("本機沒有仍有效的權杖，只清除本機登入資料。");
                return LogoutOutcome.NothingToRevoke;
            }

            if (accessUsable)
            {
                if (await TryRevokeAsync(_accessToken, null, ct).ConfigureAwait(false)) return LogoutOutcome.Revoked;
                _log("以 access 撤銷沒有成功（過期、被拒絕或網路問題），改用 refresh 權杖撤銷。");
            }
            if (refreshUsable && await TryRevokeAsync(null, _refreshToken, ct).ConfigureAwait(false)) return LogoutOutcome.Revoked;

            _log("伺服器端撤銷沒有成功（網路問題或權杖已失效），仍已清除本機登入資料。");
            return LogoutOutcome.ServerRevokeFailed;
        }

        /// <summary>送一次撤銷請求；只有 2xx 算成功。網路錯誤、非 2xx 回 false（取消則照常丟出）。</summary>
        private async Task<bool> TryRevokeAsync(string access, string refresh, CancellationToken ct)
        {
            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Post, _baseUrl + "/api/addin/token/revoke"))
                {
                    request.Headers.TryAddWithoutValidation(AddinHttp.ApiVersionHeader, AddinHttp.ApiVersion);
                    if (!string.IsNullOrEmpty(access))
                    {
                        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + access);
                        request.Content = AddinHttp.Json(new JObject());
                    }
                    else
                    {
                        request.Content = AddinHttp.Json(new JObject { ["refresh_token"] = refresh });
                    }
                    using (var response = await _http.SendAsync(request, ct).ConfigureAwait(false))
                    {
                        return response.IsSuccessStatusCode;
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception)
            {
                return false;
            }
        }

        private void ClearLocal()
        {
            _accessToken = null;
            _refreshToken = null;
            _user = null;
            _vault.Delete();
        }

        private static AddinApiException NeedLogin()
        {
            return new AddinApiException(AddinErrorKind.NeedLogin, "登入已失效，請重新登入。");
        }

        private async Task<JObject> PostTokenAsync(JObject body, CancellationToken ct)
        {
            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Post, _baseUrl + "/api/addin/token"))
                {
                    request.Headers.TryAddWithoutValidation(AddinHttp.ApiVersionHeader, AddinHttp.ApiVersion);
                    request.Content = AddinHttp.Json(body);
                    using (var response = await _http.SendAsync(request, ct).ConfigureAwait(false))
                    {
                        if (!response.IsSuccessStatusCode) throw await AddinHttp.ToExceptionAsync(response).ConfigureAwait(false);
                        return await AddinHttp.ReadJsonAsync(response).ConfigureAwait(false);
                    }
                }
            }
            catch (AddinApiException) { throw; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                throw AddinHttp.FromTransport(ex);
            }
        }

        /// <summary>套用新的權杖組；refresh 立刻寫入 DPAPI 檔（輪替後舊的已作廢，不能只留在記憶體）。</summary>
        private void Apply(JObject response)
        {
            var access = (string)response["access_token"];
            var refresh = (string)response["refresh_token"];
            if (string.IsNullOrEmpty(access) || string.IsNullOrEmpty(refresh))
                throw new AddinApiException(AddinErrorKind.Server, "伺服器回應缺少權杖。");

            var now = _now();
            _accessToken = access;
            _accessExpiresAt = now.AddSeconds(ReadSeconds(response["expires_in"], 3600));
            _refreshToken = refresh;
            _refreshExpiresAt = now.AddSeconds(ReadSeconds(response["refresh_expires_in"], 30 * 24 * 3600));
            if (response["user"] != null && response["user"].Type == JTokenType.Object) _user = AddinUser.FromJson(response["user"]);
            if (_user == null) _user = new AddinUser();

            try
            {
                _vault.Save(new StoredCredentials
                {
                    BaseUrl = _baseUrl,
                    RefreshToken = _refreshToken,
                    RefreshExpiresAt = _refreshExpiresAt,
                    UserId = _user.Id, UserEmail = _user.Email, UserName = _user.Name,
                    UserRole = _user.Role, AccountRole = _user.AccountRole
                });
            }
            catch (Exception)
            {
                _log("警告：無法把登入資料存到本機（下次啟動需要重新登入）。");
            }
        }

        private static double ReadSeconds(JToken token, double fallback)
        {
            if (token != null && (token.Type == JTokenType.Integer || token.Type == JTokenType.Float))
            {
                var value = (double)token;
                if (value > 0 && value < 10 * 365 * 24 * 3600.0) return value;
            }
            return fallback;
        }
    }
}
