#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;

namespace RevitMCP.Core.IssueManager
{
    /// <summary>
    /// 伺服器 finalize 回的警告碼（<c>代碼</c> 或 <c>代碼:參數…</c>）→ 白話繁體中文（IM 規格 revit-addin-export.md §8.4／§8.6，D-813）。
    /// 外掛視窗目前只有繁體中文（<see cref="Strings"/> 約定）；網站端另有三語版本（messages 的 BundleWarnings）。
    /// 不認得的碼（舊版、未來新增）只顯示通用一句，絕不把代碼本身給使用者看。
    /// </summary>
    public static class WarningText
    {
        public const string Unknown = "伺服器回報了一則這個畫面還不認得的提醒，不影響版本使用；需要時請聯絡管理員。";

        /// <summary>外掛會收到的全部已知警告碼（測試逐一驗證每個碼都有白話）。</summary>
        public static readonly string[] KnownCodes =
        {
            "unknown_source", "extra_files", "page_count_mismatch", "no_schedules", "params_invalid", "params_not_uploadable",
            "view_positions_not_uploadable", "named_views_without_model", "named_views_origin_mismatch", "named_views_skipped",
            "view_positions_without_model", "view_positions_origin_mismatch", "view_positions_duplicate_unique_id", "view_positions_skipped",
            "view_positions_no_geometry", "detail_viewports_not_linked", "non_3d_viewports_without_positions", "viewports_without_model",
            "unknown_view", "unknown_sheet", "more_warnings",
        };

        public static string Describe(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return Unknown;
            var parts = code.Split(':');
            var name = parts[0];
            var first = parts.Length > 1 ? parts[1] : null;
            switch (name)
            {
                case "named_views_skipped": return "有 " + Count(first) + " 個具名視圖沒有匯出（資料不完整或格式不合），其他視圖正常。";
                case "view_positions_duplicate_unique_id": return "有 " + Count(first) + " 個視圖重複出現，只留一份。";
                case "view_positions_skipped": return "有 " + Count(first) + " 個視圖位置沒有收（資料不完整或格式不合）。";
                case "view_positions_no_geometry": return "有 " + Count(first) + " 個視圖在 Revit 裡沒有可用的範圍，無法標出位置。";
                case "detail_viewports_not_linked": return "有 " + Count(first) + " 個詳圖視圖放在圖紙上，這類視圖不會連到 3D 模型。";
                case "non_3d_viewports_without_positions": return "有 " + Count(first) + " 個平面、立面或剖面視圖沒有位置資料，在 3D 裡找不到它們的位置。";
                case "viewports_without_model": return "有 " + Count(first) + " 個 3D 視圖放在圖紙上，但這次沒有匯出模型，所以沒有連結。";
                case "more_warnings": return "還有 " + Count(first) + " 則提醒沒有列出。";
                case "params_invalid": return "參數檔壞掉或格式不對，這次略過；這個版本的面積表沿用上一版，模型與圖說不受影響。";
                case "params_not_uploadable": return "這次的參數檔沒有成功上傳，所以沒有收進面積表；這個版本的面積表沿用上一版。";
                case "view_positions_not_uploadable": return "視圖位置檔沒有成功上傳，這次略過；平面、立面、剖面圖暫時沒有對到 3D 位置。";
                case "named_views_without_model": return "這次沒有匯出 3D 模型，所以具名視圖（3D 視角）全部沒有收。";
                case "named_views_origin_mismatch": return "具名視圖的原點和模型對不上，為避免視角跑位，這次沒有收具名視圖。";
                case "view_positions_without_model": return "這次沒有匯出 3D 模型，所以平面、立面、剖面圖的位置沒有收。";
                case "view_positions_origin_mismatch": return "視圖位置的原點和模型對不上，為避免位置跑掉，這次沒有收視圖位置。";
                case "no_schedules": return "參數檔裡沒有面積表。";
                case "unknown_sheet": return "有一筆視圖連結對不到這個版本裡的圖紙，已略過。";
                case "unknown_view":
                    return string.IsNullOrWhiteSpace(first)
                        ? "有一個圖紙上的視圖在模型裡找不到對應的視角，沒有連結。"
                        : "圖紙 " + first + " 上有一個視圖在模型裡找不到對應的視角，沒有連結。";
                case "unknown_source": return "版本包的來源不認得，標為未知來源。";
                case "extra_files": return "版本包裡有 " + Count(first) + " 個清單沒列的檔案，已忽略。";
                case "page_count_mismatch": return "圖說清單的張數和 PDF 頁數沒對上，沒對到的頁已用圖框辨識或留空待補。";
                default: return Unknown;
            }
        }

        /// <summary>多則警告 → 白話清單（去除重複的通用句，保留順序）。</summary>
        public static IList<string> DescribeAll(IEnumerable<string> codes)
        {
            var result = new List<string>();
            if (codes == null) return result;
            foreach (var code in codes)
            {
                var text = Describe(code);
                if (!result.Contains(text)) result.Add(text);
            }
            return result;
        }

        private static string Count(string raw)
        {
            int n;
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) && n >= 0 ? n.ToString(CultureInfo.InvariantCulture) : "數個";
        }
    }
}
