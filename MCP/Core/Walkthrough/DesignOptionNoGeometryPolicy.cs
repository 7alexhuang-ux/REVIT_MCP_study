using System;
using System.Collections.Generic;

namespace RevitMCP.Core.Walkthrough
{
    /// <summary>判斷輸入（純資料）。尺寸單位為 Revit 內部單位英尺；未知為 null（null 一律不放行）。</summary>
    public sealed class NoGeometryFacts
    {
        public string CategoryName { get; set; }
        public string BuiltInCategory { get; set; }
        public string ClassName { get; set; }
        public string Name { get; set; }
        /// <summary>系統嵌板寬（CURTAIN_WALL_PANELS_WIDTH）；讀不到為 null。</summary>
        public double? PanelWidthFeet { get; set; }
        /// <summary>系統嵌板高（CURTAIN_WALL_PANELS_HEIGHT）；讀不到為 null。</summary>
        public double? PanelHeightFeet { get; set; }
        /// <summary>尺寸取得方式（"parameter"／"boundingBox"），只用於 basis 說明。</summary>
        public string PanelSizeSource { get; set; }
        /// <summary>Panel.FindHostPanel() 回傳、且確認是本選項成員 Wall 的 ElementId；否則 null。</summary>
        public long? HostWallId { get; set; }
        /// <summary>該宿主牆本次是否已匯出幾何（triangles &gt; 0）。</summary>
        public bool HostWallExported { get; set; }
        public int HostWallTriangles { get; set; }
    }

    /// <summary>放行決定：Reason 為機器可讀短碼，Basis 為人讀依據。</summary>
    public sealed class IgnoreDecision
    {
        public IgnoreDecision(string reason, string basis) { Reason = reason; Basis = basis; }
        public string Reason { get; }
        public string Basis { get; }
    }

    /// <summary>
    /// 設計選項匯出時，哪些「沒有輸出任何三角面」的成員是已知的非幾何元素（不算匯出失敗）。
    /// 純字串判斷、不依賴 Revit API，可離線單元測試。
    /// 原則：只放行確定不會有可見幾何的類型（空嵌板、格線、標註、參考平面、草圖線、開口切割、約束、Level、Sketch、
    /// 草圖平面、牆輪廓定義），以及兩類需要「事實」佐證的帷幕嵌板：零尺寸嵌板（寬高皆 0）、嵌入牆嵌板（幾何在宿主牆）。
    /// 有尺寸的 Glazed、找不到已匯出宿主牆的 Wall 嵌板、牆 Aluminium 等實體類一律不放行，仍算失敗。
    /// 放行者仍會逐筆列在報告的 ignoredNoGeometry（含 reason 與 basis），不會消失。
    /// 需讀 Revit 元素的事實由 CommandExecutor 端收集成 <see cref="NoGeometryFacts"/> 純資料再交給本類判斷。
    /// </summary>
    public static class DesignOptionNoGeometryPolicy
    {
        // 以語系無關的 BuiltInCategory 名稱（Enum.ToString）為主，Revit 語系字串為輔。
        private static readonly HashSet<string> GridLineBuiltIns = new HashSet<string>(StringComparer.Ordinal)
        {
            "OST_CurtainGrids", "OST_CurtainGridsWall", "OST_CurtainGridsRoof", "OST_CurtainGridsSystem", "OST_CurtainGridsCurtaSystem"
        };
        private static readonly HashSet<string> GridLineCategoryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "帷幕牆格點", "帷幕牆格線", "帷幕系統格線", "Curtain Grids", "Curtain Wall Grids", "Curtain System Grids"
        };
        private static readonly HashSet<string> DimensionCategoryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "自動繪製標註", "尺寸標註", "Dimensions", "Automatic Sketch Dimensions"
        };
        private static readonly HashSet<string> ReferencePlaneCategoryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "參考平面", "Reference Planes", "Reference Plane"
        };
        private static readonly HashSet<string> SketchLineCategoryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "<草圖>", "<Sketch>"
        };
        private static readonly HashSet<string> OpeningBuiltIns = new HashSet<string>(StringComparer.Ordinal)
        {
            "OST_ShaftOpening", "OST_SWallRectOpening", "OST_ArcWallRectOpening"
        };
        private static readonly HashSet<string> OpeningCategoryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "豎井開口", "矩形直牆開口", "弧形牆開口", "Shaft Openings", "Rectangular Straight Wall Opening", "Arc Wall Opening"
        };
        private static readonly HashSet<string> ConstraintCategoryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "約束", "Constraints"
        };

        /// <summary>零尺寸容許誤差（英尺，約 0.03 mm）。</summary>
        public const double ZeroSizeToleranceFeet = 1e-4;

        /// <summary>
        /// 回傳放行原因（機器可讀的短碼）；回傳 null 代表不在白名單，必須算失敗。僅用字串屬性、不含嵌板尺寸／宿主牆事實。
        /// </summary>
        public static string IgnoreReason(string categoryName, string builtInCategory, string className, string name)
        {
            return Evaluate(new NoGeometryFacts
            {
                CategoryName = categoryName, BuiltInCategory = builtInCategory, ClassName = className, Name = name
            })?.Reason;
        }

        private static bool IsCurtainPanelCategory(string cat, string bic) =>
            bic == "OST_CurtainWallPanels" || cat == "帷幕板" || cat.Equals("Curtain Panels", StringComparison.OrdinalIgnoreCase);

        /// <summary>完整判斷；null 代表不放行。</summary>
        public static IgnoreDecision Evaluate(NoGeometryFacts f)
        {
            if (f == null) return null;
            var cat = f.CategoryName ?? "";
            var bic = f.BuiltInCategory ?? "";
            var cls = f.ClassName ?? "";
            var nm = f.Name ?? "";

            // 帷幕嵌板：Empty_Panel 本就是空嵌板。
            if (IsCurtainPanelCategory(cat, bic) && nm.Equals("Empty_Panel", StringComparison.OrdinalIgnoreCase))
                return new IgnoreDecision("empty-curtain-panel", "name=Empty_Panel");

            // 系統嵌板（class Panel）：寬高皆讀得到且皆為 0 → 零尺寸空格；有尺寸者不放行。
            if (IsCurtainPanelCategory(cat, bic) && cls == "Panel" && f.PanelWidthFeet.HasValue && f.PanelHeightFeet.HasValue
                && Math.Abs(f.PanelWidthFeet.Value) <= ZeroSizeToleranceFeet && Math.Abs(f.PanelHeightFeet.Value) <= ZeroSizeToleranceFeet)
                return new IgnoreDecision("zero-size-curtain-panel",
                    "width=" + f.PanelWidthFeet.Value.ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture) +
                    " ft, height=" + f.PanelHeightFeet.Value.ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture) +
                    " ft (source=" + (f.PanelSizeSource ?? "unknown") + ", tolerance=" + ZeroSizeToleranceFeet.ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture) + " ft)");

            // 嵌入牆嵌板：Panel.FindHostPanel() 指向本選項成員 Wall，且該牆本次已匯出幾何（幾何由宿主牆承擔）。
            if (IsCurtainPanelCategory(cat, bic) && cls == "Panel" && f.HostWallId.HasValue && f.HostWallExported && f.HostWallTriangles > 0)
                return new IgnoreDecision("embedded-wall-panel",
                    "host wall " + f.HostWallId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                    " exported triangles=" + f.HostWallTriangles.ToString(System.Globalization.CultureInfo.InvariantCulture));

            if (GridLineBuiltIns.Contains(bic) || GridLineCategoryNames.Contains(cat) || cls == "CurtainGridLine")
                return new IgnoreDecision("curtain-grid-line", "category/class");
            if (bic == "OST_Dimensions" || DimensionCategoryNames.Contains(cat) || cls == "Dimension")
                return new IgnoreDecision("dimension", "category/class");
            if (bic == "OST_CLines" || ReferencePlaneCategoryNames.Contains(cat) || cls == "ReferencePlane")
                return new IgnoreDecision("reference-plane", "category/class");
            if (bic == "OST_SketchLines" || SketchLineCategoryNames.Contains(cat))
                return new IgnoreDecision("sketch-model-line", "category");
            if (OpeningBuiltIns.Contains(bic) || OpeningCategoryNames.Contains(cat))
                return new IgnoreDecision("opening-cut", "category");
            if (bic == "OST_Constraints" || ConstraintCategoryNames.Contains(cat))
                return new IgnoreDecision("constraint", "category");

            var noCategory = string.IsNullOrEmpty(cat);
            // 無分類的 Level／Sketch／SketchPlane（Category 為 null 的元素）；有分類的實體元素不會靠 class 名被放行。
            if (noCategory && cls == "Level") return new IgnoreDecision("level", "class=Level, category=null");
            if (noCategory && cls == "Sketch") return new IgnoreDecision("sketch", "class=Sketch, category=null");
            if (noCategory && cls == "SketchPlane") return new IgnoreDecision("sketch-plane", "class=SketchPlane, category=null");
            if (bic == "OST_Levels") return new IgnoreDecision("level", "category");

            // 牆輪廓定義：無分類的 Family／Element 且名稱以 "Wall Profile" 開頭（其他無分類 Element／Family 不放行）。
            if (noCategory && (cls == "Family" || cls == "Element") && nm.StartsWith("Wall Profile", StringComparison.OrdinalIgnoreCase))
                return new IgnoreDecision("wall-profile-definition", "class=" + cls + ", category=null, name starts with 'Wall Profile'");
            return null;
        }
    }
}
