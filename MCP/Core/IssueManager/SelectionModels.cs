#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;

namespace RevitMCP.Core.IssueManager
{
    /// <summary>manifest 的視圖種類（對應 model_views.view_kind：3d／plan／section／elevation，另有天花與詳圖）。</summary>
    public static class ViewKinds
    {
        public const string ThreeD = "3d";
        public const string Plan = "plan";
        public const string Ceiling = "ceiling";
        public const string Section = "section";
        public const string Elevation = "elevation";
        public const string Detail = "detail";

        /// <summary>勾選視窗依這個順序分組顯示。</summary>
        public static readonly string[] Ordered = { ThreeD, Plan, Ceiling, Elevation, Section, Detail };

        public static bool IsKnown(string kind) { return Array.IndexOf(Ordered, kind) >= 0; }

        public static string Label(string kind)
        {
            switch (kind)
            {
                case ThreeD: return "3D 視圖（含 Camera）";
                case Plan: return "平面圖";
                case Ceiling: return "天花平面";
                case Elevation: return "立面圖";
                case Section: return "剖面圖";
                case Detail: return "詳圖";
                default: return kind;
            }
        }
    }

    /// <summary>可匯出的視圖（已排除樣板、圖例、明細表、製圖視圖等不對應模型位置的種類）。</summary>
    public sealed class ViewItem
    {
        public string UniqueId { get; set; }
        public long ElementId { get; set; }
        public string Name { get; set; }
        public string Kind { get; set; }
    }

    /// <summary>圖紙上的一個視口：用視圖的 UniqueId 連回視圖，寬高（英呎，圖紙座標）用來決定主要視口。</summary>
    public sealed class ViewportItem
    {
        public string ViewUniqueId { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public double Area { get { return Math.Abs(Width) * Math.Abs(Height); } }
    }

    public sealed class SheetItem
    {
        public string UniqueId { get; set; }
        public long ElementId { get; set; }
        /// <summary>Revit 圖紙號。上傳時直接當 sheetNumber，不經圖框辨識。</summary>
        public string Number { get; set; }
        public string Title { get; set; }
        public List<ViewportItem> Viewports { get; set; } = new List<ViewportItem>();
    }

    /// <summary>Revit 內建的命名圖紙集（ViewSheetSet）；成員可以是圖紙，也可以是視圖。</summary>
    public sealed class SheetSetItem
    {
        public string Name { get; set; }
        public List<string> SheetUniqueIds { get; set; } = new List<string>();
        public List<string> ViewUniqueIds { get; set; } = new List<string>();
    }

    /// <summary>打開視窗當下，從 Revit 文件讀到的全部可選項目。</summary>
    public sealed class ProjectSnapshot
    {
        public string DocumentTitle { get; set; }
        public string DocumentPath { get; set; }
        public List<SheetItem> Sheets { get; set; } = new List<SheetItem>();
        public List<ViewItem> Views { get; set; } = new List<ViewItem>();
        public List<SheetSetItem> SheetSets { get; set; } = new List<SheetSetItem>();
    }

    /// <summary>圖號自然排序：A-2 排在 A-10 前面，英文不分大小寫；排序結果同時決定 PDF 頁序與 manifest 的 pageNumber。</summary>
    public sealed class NaturalComparer : IComparer<string>
    {
        public static readonly NaturalComparer Instance = new NaturalComparer();

        public int Compare(string x, string y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x == null) return -1;
            if (y == null) return 1;
            int i = 0, j = 0;
            while (i < x.Length && j < y.Length)
            {
                if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
                {
                    var si = i;
                    var sj = j;
                    while (i < x.Length && char.IsDigit(x[i])) i++;
                    while (j < y.Length && char.IsDigit(y[j])) j++;
                    var a = x.Substring(si, i - si).TrimStart('0');
                    var b = y.Substring(sj, j - sj).TrimStart('0');
                    if (a.Length != b.Length) return a.Length < b.Length ? -1 : 1;
                    var c = string.CompareOrdinal(a, b);
                    if (c != 0) return c < 0 ? -1 : 1;
                }
                else
                {
                    var cx = char.ToUpperInvariant(x[i]);
                    var cy = char.ToUpperInvariant(y[j]);
                    if (cx != cy) return cx < cy ? -1 : 1;
                    i++;
                    j++;
                }
            }
            if (i < x.Length) return 1;
            if (j < y.Length) return -1;
            return string.CompareOrdinal(x, y);
        }
    }

    public static class PrimaryViewport
    {
        /// <summary>圖紙上面積最大的視口（sheet-sets.md R5）；面積相同取清單中較前者。清單空回 -1。</summary>
        public static int IndexOf(IList<ViewportItem> viewports)
        {
            var best = -1;
            var bestArea = double.NegativeInfinity;
            for (var i = 0; i < viewports.Count; i++)
            {
                if (viewports[i].Area > bestArea + 1e-12)
                {
                    best = i;
                    bestArea = viewports[i].Area;
                }
            }
            return best;
        }
    }

    /// <summary>勾選視窗最後交給匯出器的請求。</summary>
    public sealed class ExportRequest
    {
        public string ProjectId { get; set; }
        public string Stage { get; set; }
        public string Description { get; set; }
        /// <summary>使用者選的 ViewSheetSet 名稱；「（自訂）」時為 null（manifest 省略 sheetSet）。</summary>
        public string SheetSetName { get; set; }
        /// <summary>已依圖號自然排序；每張圖紙的 Viewports 只留下有被勾選的視圖。</summary>
        public List<SheetItem> Sheets { get; set; } = new List<SheetItem>();
        public List<ViewItem> Views { get; set; } = new List<ViewItem>();
        public bool IncludeModel { get; set; }
        public bool IncludeDesignOptions { get; set; }

        public int ViewCount { get { return Views.Count; } }
        public IEnumerable<ViewItem> ThreeDViews { get { return Views.Where(v => v.Kind == ViewKinds.ThreeD); } }
    }
}
