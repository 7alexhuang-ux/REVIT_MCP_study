using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace RevitMCP.Core.ViewSync
{
    // 網頁 3D 視角／剖面 → Revit 聯動（issue-manager D-710）的瀏覽器連線安全邏輯。
    // 純計算，不依賴 Revit API／WebSocket，可直接單元測試。
    // 規格：issue-manager docs/spec/revit-viewsync.md §2。

    /// <summary>瀏覽器（帶 Origin 的連線）可呼叫的指令白名單。其他指令（現有 MCP 指令）一律拒絕。</summary>
    public static class ViewSyncCommands
    {
        public const string Pair = "viewsync_pair";
        public const string Status = "viewsync_status";
        public const string ApplyTemp = "viewsync_apply_temp";
        public const string OpenView = "viewsync_open_view";
        public const string CreateView = "viewsync_create_view";

        /// <summary>
        /// 相機管理（issue-manager D-712 網頁版相機管理）可由瀏覽器呼叫的指令。與 viewsync_* 同樣的 Origin＋token 規則。
        /// 不含 get_camera_info（只給 MCP 內部用）。
        /// </summary>
        public static readonly string[] CameraCommands =
        {
            "list_cameras", "set_camera_pose", "copy_camera_to_level", "run_camera_keyplan",
            "run_camera_overview", "create_camera_sheet", "camera_version", "get_view_extents"
        };

        /// <summary>配對以外、需要 token 的瀏覽器指令（也是會轉給 CommandExecutor 的指令）。</summary>
        public static readonly string[] Authorized =
            new[] { Status, ApplyTemp, OpenView, CreateView }.Concat(CameraCommands).ToArray();

        /// <summary>名稱完全相等（不分大小寫）才算；不 Trim，避免白名單判斷與執行器判斷不一致。</summary>
        public static bool IsBrowserAllowed(string commandName)
        {
            if (string.IsNullOrEmpty(commandName)) return false;
            if (string.Equals(commandName, Pair, StringComparison.OrdinalIgnoreCase)) return true;
            return Authorized.Any(c => string.Equals(commandName, c, StringComparison.OrdinalIgnoreCase));
        }

        public static bool IsPair(string commandName) =>
            string.Equals(commandName, Pair, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Origin 標頭正規化與白名單比對。</summary>
    public static class OriginPolicy
    {
        /// <summary>預設白名單：正式站、本機開發。可在 config.json 的 allowedOrigins 增刪。</summary>
        public static readonly string[] DefaultAllowedOrigins =
        {
            "https://issue-manager-sigma.vercel.app",
            "http://localhost:3600",
        };

        /// <summary>
        /// 正規化成 scheme://host[:port]（小寫、預設埠省略、無結尾斜線）。
        /// 不是 http／https、帶路徑／查詢／帳密、"null"、空字串 → 回 null（視為不合法）。
        /// </summary>
        public static string Normalize(string origin)
        {
            if (string.IsNullOrWhiteSpace(origin)) return null;
            if (!Uri.TryCreate(origin.Trim(), UriKind.Absolute, out var uri)) return null;
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;
            if (!string.IsNullOrEmpty(uri.UserInfo)) return null;
            if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) return null;
            if (uri.AbsolutePath != "/") return null;
            return uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant();
        }

        public static bool IsAllowed(string origin, IEnumerable<string> allowedOrigins)
        {
            var normalized = Normalize(origin);
            if (normalized == null || allowedOrigins == null) return false;
            foreach (var allowed in allowedOrigins)
            {
                var a = Normalize(allowed);
                if (a != null && string.Equals(a, normalized, StringComparison.Ordinal)) return true;
            }

            return false;
        }
    }

    /// <summary>已配對的網頁裝置（存在 config.json；只存 token 的 SHA-256，不存明文）。</summary>
    public sealed class PairedWebClient
    {
        public string TokenHash { get; set; }
        public string Origin { get; set; }
        public DateTime CreatedUtc { get; set; }
    }

    public static class TokenUtil
    {
        /// <summary>隨機 32 bytes → 64 字元小寫十六進位。</summary>
        public static string GenerateToken()
        {
            var bytes = new byte[32];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            return ToHex(bytes);
        }

        public static string Hash(string token)
        {
            using (var sha = SHA256.Create())
                return ToHex(sha.ComputeHash(Encoding.UTF8.GetBytes(token ?? string.Empty)));
        }

        /// <summary>固定時間比較（長度不同直接 false，不洩漏位置）。</summary>
        public static bool ConstantTimeEquals(string a, string b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            var diff = 0;
            for (var i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        private static string ToHex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }

    public enum PairingResult { Ok, NoActiveCode, Expired, Wrong, TooManyAttempts }

    /// <summary>
    /// 6 位數配對碼：同時只有一組有效、5 分鐘過期、用過即失效、猜錯 N 次整組作廢（防暴力猜測）。
    /// 時鐘可注入以便測試。
    /// </summary>
    public sealed class PairingCodeManager
    {
        private readonly object _lock = new object();
        private readonly Func<DateTime> _utcNow;
        private readonly TimeSpan _validity;
        private readonly int _maxFailures;
        private string _code;
        private DateTime _expiresUtc;
        private int _failures;

        public PairingCodeManager(Func<DateTime> utcNow = null, TimeSpan? validity = null, int maxFailures = 5)
        {
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
            _validity = validity ?? TimeSpan.FromMinutes(5);
            _maxFailures = maxFailures;
        }

        public DateTime ExpiresAtUtc { get { lock (_lock) return _expiresUtc; } }

        public bool HasActiveCode
        {
            get { lock (_lock) return _code != null && _utcNow() <= _expiresUtc; }
        }

        /// <summary>產生新配對碼（蓋掉舊的）。</summary>
        public string Issue()
        {
            lock (_lock)
            {
                _code = RandomSixDigits();
                _expiresUtc = _utcNow() + _validity;
                _failures = 0;
                return _code;
            }
        }

        public void Cancel()
        {
            lock (_lock) { _code = null; _failures = 0; }
        }

        public PairingResult TryRedeem(string code)
        {
            lock (_lock)
            {
                if (_code == null) return PairingResult.NoActiveCode;
                if (_utcNow() > _expiresUtc) { _code = null; return PairingResult.Expired; }
                if (TokenUtil.ConstantTimeEquals(_code, (code ?? string.Empty).Trim()))
                {
                    _code = null;
                    _failures = 0;
                    return PairingResult.Ok;
                }

                _failures++;
                if (_failures >= _maxFailures)
                {
                    _code = null;
                    return PairingResult.TooManyAttempts;
                }

                return PairingResult.Wrong;
            }
        }

        private static string RandomSixDigits()
        {
            using (var rng = RandomNumberGenerator.Create())
            {
                var buffer = new byte[4];
                // 拒絕取樣：避免 uint % 1_000_000 的偏差
                const uint limit = uint.MaxValue - (uint.MaxValue % 1000000u);
                uint value;
                do
                {
                    rng.GetBytes(buffer);
                    value = BitConverter.ToUInt32(buffer, 0);
                } while (value >= limit);

                return (value % 1000000u).ToString("D6");
            }
        }
    }

    public enum AuthDecision { Allowed, OriginRejected, CommandNotAllowed, TokenMissing, TokenInvalid }

    /// <summary>瀏覽器請求的授權判斷：Origin 白名單 → 指令白名單 → token（配對指令免 token）。</summary>
    public static class ViewSyncAuthorizer
    {
        public static AuthDecision Check(string origin, string commandName, string token,
            IEnumerable<string> allowedOrigins, IEnumerable<PairedWebClient> clients)
        {
            if (!OriginPolicy.IsAllowed(origin, allowedOrigins)) return AuthDecision.OriginRejected;
            if (!ViewSyncCommands.IsBrowserAllowed(commandName)) return AuthDecision.CommandNotAllowed;
            if (ViewSyncCommands.IsPair(commandName)) return AuthDecision.Allowed;
            if (string.IsNullOrEmpty(token)) return AuthDecision.TokenMissing;

            var normalizedOrigin = OriginPolicy.Normalize(origin);
            var hash = TokenUtil.Hash(token);
            foreach (var client in clients ?? Enumerable.Empty<PairedWebClient>())
            {
                if (client == null || string.IsNullOrEmpty(client.TokenHash)) continue;
                if (!TokenUtil.ConstantTimeEquals(client.TokenHash, hash)) continue;
                // token 綁定配對時的 Origin；Origin 欄位空白（手動編輯）視為不限。
                var bound = OriginPolicy.Normalize(client.Origin);
                if (bound != null && !string.Equals(bound, normalizedOrigin, StringComparison.Ordinal)) continue;
                return AuthDecision.Allowed;
            }

            return AuthDecision.TokenInvalid;
        }

        public static string ErrorCode(AuthDecision decision)
        {
            switch (decision)
            {
                case AuthDecision.OriginRejected: return "origin_rejected";
                case AuthDecision.CommandNotAllowed: return "command_not_allowed";
                case AuthDecision.TokenMissing: return "token_missing";
                case AuthDecision.TokenInvalid: return "token_invalid";
                default: return null;
            }
        }

        public static string ErrorMessage(AuthDecision decision)
        {
            switch (decision)
            {
                case AuthDecision.OriginRejected: return "這個網站不在 Revit 外掛允許的網域清單內。";
                case AuthDecision.CommandNotAllowed: return "瀏覽器不能呼叫這個指令。";
                case AuthDecision.TokenMissing: return "尚未配對：請在 Revit 功能區按「網頁配對」取得配對碼。";
                case AuthDecision.TokenInvalid: return "配對已失效或被撤銷，請重新配對。";
                default: return null;
            }
        }
    }

    /// <summary>可帶機器可讀錯誤碼的例外；CommandExecutor 會把 Code 放進回應的 errorCode。</summary>
    public sealed class ViewSyncException : Exception
    {
        public string Code { get; }

        public ViewSyncException(string code, string message) : base(message)
        {
            Code = code;
        }
    }
}
