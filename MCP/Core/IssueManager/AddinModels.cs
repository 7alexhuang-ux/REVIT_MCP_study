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

    /// <summary>建立版本包時伺服器回報的上限（revit-addin-export.md §5.2：limits）。沒給的欄位為 0＝未知，不檢查。</summary>
    public sealed class BundleLimits
    {
        public long PdfBytes { get; set; }
        public long GlbBytes { get; set; }
        public long NamedViewsBytes { get; set; }
        public int PdfPages { get; set; }
    }

    public sealed class BundleCreateResult
    {
        public string BundleId { get; set; }
        public DateTimeOffset? ExpiresAt { get; set; }
        public BundleLimits Limits { get; set; } = new BundleLimits();
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

    /// <summary>DELETE /bundles/:id 的結果：取消流程據此決定要告訴使用者什麼（已 finalize 的版本不能用取消刪掉）。</summary>
    public enum BundleDeleteOutcome
    {
        /// <summary>已刪除，或伺服器上已經不存在（404／410）。</summary>
        Deleted,
        /// <summary>409 already_finalized：版本已建立，取消什麼都沒做。</summary>
        AlreadyFinalized,
        /// <summary>409 finalize_in_progress：伺服器正在建立版本，取消什麼都沒做。</summary>
        FinalizeInProgress,
        /// <summary>連不上或其他錯誤（伺服器端暫存會在過期時自動清除）。</summary>
        Failed
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
        private static readonly Regex SafeCode = new Regex("^[a-z0-9_]{1,40}\\z", RegexOptions.Compiled);

        public AddinApiException(AddinErrorKind kind, string message, int statusCode = 0, string code = null, bool isTimeout = false)
            : base(message)
        {
            Kind = kind;
            StatusCode = statusCode;
            IsTimeout = isTimeout;
            Code = code != null && SafeCode.IsMatch(code) ? code : null;
        }

        public AddinErrorKind Kind { get; private set; }
        public int StatusCode { get; private set; }
        public string Code { get; private set; }
        /// <summary>網路錯誤中的「等不到回應（逾時）」：伺服器可能仍在處理，與「連不上」不同。</summary>
        public bool IsTimeout { get; private set; }
    }
}
