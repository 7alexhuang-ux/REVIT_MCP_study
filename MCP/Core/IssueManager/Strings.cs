#nullable disable
namespace RevitMCP.Core.IssueManager
{
    /// <summary>
    /// 外掛視窗字串（revit-addin-export.md §2：先 zh-TW，英文次之，緬文不在本案）。
    /// 集中放這裡，之後加英文只需要把這個類別換成查表。
    /// </summary>
    public static class Strings
    {
        public const string WindowTitle = "上傳到 Issue Manager";
        public const string PanelName = "Issue Manager";
        public const string ButtonText = "上傳到\nIssue Manager";
        public const string ButtonTooltip = "勾選圖紙、視圖與 3D 模型，一次匯出並上傳成 Issue Manager 的一個工作版本。不需要開啟 MCP 服務。";

        public const string NotLoggedIn = "尚未登入";
        public const string Login = "登入";
        public const string Logout = "登出";
        public const string SwitchAccount = "切換帳號";

        public const string StagePlaceholder = "請選擇階段";
        public const string StagePd = "規劃設計（PD）";
        public const string StageSd = "基本設計（SD）";
        public const string StageDd = "細部設計（DD）";

        public const string CustomSheetSet = "（自訂）";

        public const string NoProjectSelected = "請選擇專案";
        public const string NoStageSelected = "請選擇設計階段";
        public const string NoDescription = "請填寫版本說明";
        public const string NothingSelected = "請至少勾選一張圖紙或 3D 模型（只勾視圖不能上傳）";
        public const string NotLoggedInReason = "請先登入";

        public const string MissingSheets = "這個版本將缺圖（沒有勾選任何圖紙）";
        public const string MissingModel = "這個版本將缺模型（沒有勾選 3D 模型）";

        public const string LoggedOut = "已登出，本機的登入資料已刪除。";
        public const string LoggedOutRevokeFailed =
            "已登出，本機的登入資料已刪除，但伺服器端撤銷未完成（網路問題或權杖已失效）。"
            + "為安全起見，請到 Issue Manager 網站的「帳號 › 已授權的外掛」頁面，手動撤銷這台電腦的授權。";
        public const string SwitchRevokeFailed =
            "舊帳號在伺服器端的撤銷未完成（本機的登入資料已刪除）。"
            + "請稍後到 Issue Manager 網站的「帳號 › 已授權的外掛」頁面，手動撤銷這台電腦的舊授權。";
        public const string LoginCancelled = "已取消登入。";
        public const string ConfirmDiscardTitle = "放棄這次上傳？";
        public const string ConfirmDiscardText =
            "放棄這次上傳並清除？\n\n選「是」：通知 Issue Manager 刪除暫存的版本包，並刪除本機匯出的 PDF／GLB 檔（之後要重新匯出）。\n選「否」：回到視窗，仍可按「重試上傳」。";

        public const string FinalizeStage = "伺服器處理中：檢查檔案並建立工作版本（大檔可能需要數分鐘，請稍候）";
        public const string RecreateBundleStage = "重新建立版本包（先前的版本包已被伺服器作廢或已過期；本機檔案不需要重新匯出）";
        public const string CancelTooLate = "已經上傳完成，無法取消；請到 Issue Manager 網站刪除該版本。";
        public const string CancelDuringFinalize = "伺服器正在建立版本，已無法取消；請稍後到 Issue Manager 網站確認，若版本已建立，請在網站刪除該版本。";
        public const string FinalizeStillRunning =
            "伺服器仍在處理這個版本（已等待超過 10 分鐘）。請稍後到 Issue Manager 網站確認版本是否已建立；若沒有，再按「重試上傳」。";

        public static string TooManySheets(int max)
        {
            return "勾選的圖紙超過 " + max + " 張（Issue Manager 單一版本的上限），請分批上傳";
        }

        public static string TooManyViews(int max)
        {
            return "勾選的視圖超過 " + max + " 個（Issue Manager 單一版本的上限），請減少勾選";
        }

        public static string FinalizePolling(int seconds)
        {
            return "伺服器處理中：確認建立結果（已等待 " + seconds + " 秒）…";
        }

        public const string IncludeParamsText = "匯出參數與面積表（params.json）";
        public const string ServerWithoutViewPositions = "這個 Issue Manager 還不能收平面／立面／剖面的位置資料，本次略過；這些圖紙在網站上不能直接對照 3D。";
        public const string ServerWithoutParams = "這個 Issue Manager 還不能收參數與面積表，本次略過；圖紙與模型照常上傳。";

        public static string Uploaded(string version)
        {
            return string.IsNullOrEmpty(version) ? "完成：Issue Manager 已建立一個工作版本。" : "完成：已上傳為 " + version + "（圖說、模型、面積表同一個版本號）。";
        }

        public const string OtherViewsHeader = "其他視圖（不在圖紙上）";
        public const string SheetsHeader = "圖紙";

        public static string Summary(int sheets, int views, int models)
        {
            return "將上傳 " + sheets + " 張圖紙、" + views + " 個視圖、" + models + " 個模型";
        }

        public static string Summary(int sheets, int views, int positioned, int models, bool includeParams)
        {
            return "將上傳 " + sheets + " 張圖紙、" + views + " 個視圖（其中 " + positioned + " 個有位置資料）、" + models + " 個模型、參數與面積表 " + (includeParams ? "○" : "✕");
        }
    }
}
