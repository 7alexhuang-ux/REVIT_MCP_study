using System;
using System.Collections.Generic;
using System.Linq;
using RevitMCP.Configuration;

namespace RevitMCP.Core.ViewSync
{
    /// <summary>
    /// 瀏覽器連線的授權狀態：Origin 白名單與配對 token 存在 ConfigManager 的設定檔，配對碼只在記憶體。
    /// 判斷邏輯在 <see cref="ViewSyncAuthorizer"/>／<see cref="PairingCodeManager"/>（純計算，有單元測試），這裡只負責接設定檔與鎖。
    /// </summary>
    public sealed class ViewSyncAuthService
    {
        private static readonly Lazy<ViewSyncAuthService> _instance = new Lazy<ViewSyncAuthService>(() => new ViewSyncAuthService());
        public static ViewSyncAuthService Instance => _instance.Value;

        private readonly object _lock = new object();
        private readonly PairingCodeManager _codes = new PairingCodeManager();

        /// <summary>撤銷全部配對時觸發；SocketService 據此關閉現有的瀏覽器連線。</summary>
        public event EventHandler Revoked;

        public PairingCodeManager Codes => _codes;

        public bool IsOriginAllowed(string origin)
        {
            lock (_lock) return OriginPolicy.IsAllowed(origin, ConfigManager.Instance.Settings.AllowedOrigins);
        }

        public AuthDecision Authorize(string origin, string commandName, string token)
        {
            lock (_lock)
            {
                var s = ConfigManager.Instance.Settings;
                return ViewSyncAuthorizer.Check(origin, commandName, token, s.AllowedOrigins, s.PairedWebClients);
            }
        }

        /// <summary>Revit 端「網頁配對」按鈕呼叫：產生 6 位數配對碼（5 分鐘有效）。</summary>
        public string IssuePairingCode() => _codes.Issue();

        /// <summary>
        /// 瀏覽器送 viewsync_pair { code }：碼對 → 產生 token、存雜湊進設定檔、回傳明文 token（只此一次）。
        /// 失敗回 null 並以 result 說明原因。
        /// </summary>
        public string TryPair(string origin, string code, out PairingResult result)
        {
            result = _codes.TryRedeem(code);
            if (result != PairingResult.Ok) return null;
            var token = TokenUtil.GenerateToken();
            lock (_lock)
            {
                var s = ConfigManager.Instance.Settings;
                s.PairedWebClients.Add(new PairedWebClient
                {
                    TokenHash = TokenUtil.Hash(token),
                    Origin = OriginPolicy.Normalize(origin),
                    CreatedUtc = DateTime.UtcNow,
                });
                ConfigManager.Instance.SaveSettings();
            }

            return token;
        }

        /// <summary>撤銷所有網頁配對（清空 token、作廢配對碼），回傳被撤銷的裝置數。</summary>
        public int RevokeAll()
        {
            int count;
            lock (_lock)
            {
                var s = ConfigManager.Instance.Settings;
                count = s.PairedWebClients.Count;
                s.PairedWebClients.Clear();
                ConfigManager.Instance.SaveSettings();
            }

            _codes.Cancel();
            Revoked?.Invoke(this, EventArgs.Empty);
            return count;
        }

        public int PairedCount
        {
            get { lock (_lock) return ConfigManager.Instance.Settings.PairedWebClients.Count; }
        }

        public IReadOnlyList<string> AllowedOrigins
        {
            get { lock (_lock) return ConfigManager.Instance.Settings.AllowedOrigins.ToList(); }
        }
    }
}
