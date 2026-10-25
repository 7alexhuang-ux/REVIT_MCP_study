#nullable disable
using System;
using System.IO;
using Newtonsoft.Json.Linq;

namespace RevitMCP.Core.IssueManager
{
    /// <summary>
    /// Issue Manager 網址與用戶端識別。網址來源（先找到先用）：環境變數 ISSUE_MANAGER_URL →
    /// %APPDATA%\RevitMCP\issue-manager.json 的 baseUrl。沒有預設值：寧可顯示「尚未設定」，也不猜一個網址把權杖送出去。
    /// </summary>
    public static class IssueManagerSettings
    {
        public const string EnvironmentVariable = "ISSUE_MANAGER_URL";
        public const string ConfigFileName = "issue-manager.json";

        public static string DefaultConfigPath()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RevitMCP", ConfigFileName);
        }

        /// <summary>回傳正規化後的網址（無結尾斜線）；沒設定回 null；設定了但不合法丟 ArgumentException。</summary>
        public static string ResolveBaseUrl(Func<string, string> getEnvironmentVariable, string configPath)
        {
            var raw = getEnvironmentVariable == null ? null : getEnvironmentVariable(EnvironmentVariable);
            if (string.IsNullOrWhiteSpace(raw) && !string.IsNullOrEmpty(configPath) && File.Exists(configPath))
            {
                try
                {
                    var json = JObject.Parse(File.ReadAllText(configPath));
                    raw = (string)json["baseUrl"];
                }
                catch (Exception)
                {
                    throw new ArgumentException("issue-manager.json 不是有效的 JSON。");
                }
            }
            if (string.IsNullOrWhiteSpace(raw)) return null;
            return NormalizeBaseUrl(raw);
        }

        /// <summary>只允許 https；http 僅限本機（127.0.0.1／localhost，開發用假伺服器），避免權杖走明文。</summary>
        public static string NormalizeBaseUrl(string raw)
        {
            Uri uri;
            if (!Uri.TryCreate((raw ?? string.Empty).Trim(), UriKind.Absolute, out uri))
                throw new ArgumentException("Issue Manager 網址格式不正確。");
            var isLoopback = uri.IsLoopback;
            if (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && isLoopback))
                throw new ArgumentException("Issue Manager 網址必須是 https（本機測試才允許 http）。");
            if (!string.IsNullOrEmpty(uri.UserInfo))
                throw new ArgumentException("Issue Manager 網址不可含帳號密碼。");
            return uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        }
    }

    /// <summary>送給授權頁的外掛識別（顯示在授權頁與「已授權的外掛」清單）。</summary>
    public sealed class ClientInfo
    {
        public string Name { get; set; }
        public string Version { get; set; }

        /// <summary>伺服器限制：名稱 ≤80 字、版本 ≤32 字且只含 [A-Za-z0-9._+-]。</summary>
        public static ClientInfo Create(string name, string version)
        {
            var cleanName = new System.Text.StringBuilder();
            foreach (var c in name ?? string.Empty)
            {
                if (!char.IsControl(c)) cleanName.Append(c);
            }
            var nameText = cleanName.ToString().Trim();
            if (nameText.Length > 80) nameText = nameText.Substring(0, 80);

            var cleanVersion = new System.Text.StringBuilder();
            foreach (var c in version ?? string.Empty)
            {
                var ok = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '.' || c == '_' || c == '+' || c == '-';
                if (ok) cleanVersion.Append(c);
            }
            var versionText = cleanVersion.ToString();
            if (versionText.Length > 32) versionText = versionText.Substring(0, 32);
            return new ClientInfo { Name = nameText, Version = versionText };
        }
    }
}
