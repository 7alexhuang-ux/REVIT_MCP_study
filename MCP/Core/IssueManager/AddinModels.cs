#nullable disable
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace RevitMCP.Core.IssueManager
{
    /// <summary>外掛登入後的使用者資訊（addin-auth.md §4.3 成功回應的 user）。</summary>
    public sealed class AddinUser
    {
        public string Id { get; set; }
        public string Email { get; set; }
        public string Name { get; set; }
        public string AccountRole { get; set; }
        /// <summary>所屬專案中最高的角色（admin／staff／engineer／client），可能為 null。</summary>
        public string Role { get; set; }

        public static AddinUser FromJson(JToken token)
        {
            if (token == null || token.Type != JTokenType.Object) return new AddinUser();
            return new AddinUser
            {
                Id = (string)token["id"],
                Email = (string)token["email"],
                Name = (string)token["name"],
                AccountRole = (string)token["accountRole"],
                Role = token["role"] != null && token["role"].Type == JTokenType.String ? (string)token["role"] : null
            };
        }
    }

    /// <summary>GET /api/addin/projects 的一筆（只有 id、code、name；不含任何預設階段，D-587）。</summary>
    public sealed class AddinProject
    {
        public string Id { get; set; }
        public string Code { get; set; }
        public string Name { get; set; }
        public string Label { get { return string.IsNullOrWhiteSpace(Code) ? Name : Code + "　" + Name; } }
    }

    /// <summary>建立版本包時伺服器給的單一檔案上傳憑證：分段大小與依序的簽名網址。</summary>
    public sealed class BundleFileTicket
    {
        public string Kind { get; set; }
        public string Name { get; set; }
        public long PartBytes { get; set; }
        public List<string> Urls { get; set; } = new List<string>();
    }

    public sealed class BundleCreateResult
    {
        public string BundleId { get; set; }
        public DateTimeOffset? ExpiresAt { get; set; }
        public List<BundleFileTicket> Files { get; set; } = new List<BundleFileTicket>();

        public BundleFileTicket Find(string kind)
        {
            foreach (var file in Files)
            {
                if (string.Equals(file.Kind, kind, StringComparison.Ordinal)) return file;
            }
            return null;
        }
    }

    public sealed class FinalizeResult
    {
        public string SetId { get; set; }
        public string Url { get; set; }
        public List<string> Warnings { get; set; } = new List<string>();
    }

    public enum AddinErrorKind
    {
        /// <summary>連不到伺服器、逾時。</summary>
        Network,
        /// <summary>沒有登入或登入已失效，需要重新登入。</summary>
        NeedLogin,
        /// <summary>找不到（含沒有權限；伺服器不區分）。</summary>
        NotFound,
        /// <summary>送出的內容被拒（階段／說明／檔案檢查失敗等）。</summary>
        Rejected,
        /// <summary>外掛版本太舊，伺服器要求更新（426）。</summary>
        UpgradeRequired,
        /// <summary>使用者在瀏覽器按了取消。</summary>
        Denied,
        Server,
        Other
    }

    /// <summary>
    /// 對 Issue Manager 呼叫失敗。訊息是固定的繁中白話文，不含權杖、授權碼、verifier；
    /// Code 只接受伺服器回傳的短代碼（[a-z0-9_]），不回顯其他內容。
    /// </summary>
    public sealed class AddinApiException : Exception
    {
        private static readonly Regex SafeCode = new Regex("^[a-z0-9_]{1,40}$", RegexOptions.Compiled);

        public AddinApiException(AddinErrorKind kind, string message, int statusCode = 0, string code = null)
            : base(message)
        {
            Kind = kind;
            StatusCode = statusCode;
            Code = code != null && SafeCode.IsMatch(code) ? code : null;
        }

        public AddinErrorKind Kind { get; private set; }
        public int StatusCode { get; private set; }
        public string Code { get; private set; }
    }
}
