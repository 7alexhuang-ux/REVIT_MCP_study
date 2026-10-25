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
        public const string NothingSelected = "請至少勾選一項";
        public const string NotLoggedInReason = "請先登入";

        public const string MissingSheets = "這個版本將缺圖（沒有勾選任何圖紙）";
        public const string MissingModel = "這個版本將缺模型（沒有勾選 3D 模型）";

        public const string OtherViewsHeader = "其他視圖（不在圖紙上）";
        public const string SheetsHeader = "圖紙";

        public static string Summary(int sheets, int views, int models)
        {
            return "將上傳 " + sheets + " 張圖紙、" + views + " 個視圖、" + models + " 個模型";
        }
    }
}
