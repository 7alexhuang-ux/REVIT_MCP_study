#nullable disable
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace RevitMCP.Core.IssueManager
{
    // 開視窗用的「便宜欄位」來源：只含名稱、識別碼、種類與圖紙-視口-視圖的對應。
    // 這組介面刻意不提供任何量測（視口外框、裁剪框、版面位置）：昂貴資料只能在匯出步驟取得
    // （IBundleExportSteps.MeasureViewports），這樣「開視窗時不會呼叫昂貴 API」是由型別保證的。

    public sealed class SourceView
    {
        public string UniqueId { get; set; }
        public long ElementId { get; set; }
        public string Name { get; set; }
        /// <summary>已分類的 manifest 種類（<see cref="ViewKinds"/>）；來源端要先濾掉樣板與不對應模型位置的視圖。</summary>
        public string Kind { get; set; }
    }

    public sealed class SourceViewport
    {
        public long ViewportElementId { get; set; }
        public long ViewElementId { get; set; }
    }

    public sealed class SourceSheet
    {
        public string UniqueId { get; set; }
        public long ElementId { get; set; }
        public string Number { get; set; }
        public string Title { get; set; }
        public List<SourceViewport> Viewports { get; set; } = new List<SourceViewport>();
    }

    public sealed class SourceSheetSetMember
    {
        public string UniqueId { get; set; }
        public bool IsSheet { get; set; }
    }

    public sealed class SourceSheetSet
    {
        public string Name { get; set; }
        public List<SourceSheetSetMember> Members { get; set; } = new List<SourceSheetSetMember>();
    }

    /// <summary>勾選視窗需要的專案清單來源（Revit 實作：RevitSelectionCollector；測試用假實作）。每個方法在開視窗時各呼叫一次。</summary>
    public interface IProjectSource
    {
        string Title { get; }
        string Path { get; }
        IList<SourceView> ReadViews();
        IList<SourceSheet> ReadSheets();
        IList<SourceSheetSet> ReadSheetSets();
    }

    /// <summary>
    /// 把 <see cref="IProjectSource"/> 的便宜欄位組成 <see cref="ProjectSnapshot"/>（純邏輯，不依賴 Revit，可單元測試）。
    /// 視口只帶 ViewportElementId，不帶寬高。每個階段的耗時與數量寫進 log（只有數字，沒有圖紙或專案名稱）。
    /// </summary>
    public static class SnapshotBuilder
    {
        public static ProjectSnapshot Build(IProjectSource source, Action<string> log = null)
        {
            if (source == null) throw new ArgumentNullException("source");
            var write = log ?? (message => { });
            var total = Stopwatch.StartNew();
            var snapshot = new ProjectSnapshot { DocumentTitle = source.Title, DocumentPath = source.Path };

            var stage = Stopwatch.StartNew();
            var viewByElementId = new Dictionary<long, ViewItem>();
            var knownUniqueIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var view in source.ReadViews())
            {
                if (view == null || string.IsNullOrEmpty(view.UniqueId) || !ViewKinds.IsKnown(view.Kind)) continue;
                if (!knownUniqueIds.Add(view.UniqueId)) continue;
                var item = new ViewItem { UniqueId = view.UniqueId, ElementId = view.ElementId, Name = view.Name, Kind = view.Kind };
                viewByElementId[item.ElementId] = item;
                snapshot.Views.Add(item);
            }
            write("開視窗讀取視圖：" + snapshot.Views.Count + " 個，" + stage.ElapsedMilliseconds + " ms");

            stage.Restart();
            var sheetUniqueIds = new HashSet<string>(StringComparer.Ordinal);
            var viewportCount = 0;
            foreach (var sheet in source.ReadSheets())
            {
                if (sheet == null) continue;
                var item = new SheetItem { UniqueId = sheet.UniqueId, ElementId = sheet.ElementId, Number = sheet.Number, Title = sheet.Title };
                foreach (var viewport in sheet.Viewports)
                {
                    ViewItem view;
                    if (!viewByElementId.TryGetValue(viewport.ViewElementId, out view)) continue; // 圖例、明細表、製圖視圖不對應模型位置，不列入
                    item.Viewports.Add(new ViewportItem { ViewUniqueId = view.UniqueId, ViewportElementId = viewport.ViewportElementId });
                    viewportCount++;
                }
                sheetUniqueIds.Add(item.UniqueId);
                snapshot.Sheets.Add(item);
            }
            write("開視窗讀取圖紙：" + snapshot.Sheets.Count + " 張、視口 " + viewportCount + " 個（視口外框延後到匯出才量），" + stage.ElapsedMilliseconds + " ms");

            stage.Restart();
            foreach (var set in source.ReadSheetSets())
            {
                if (set == null) continue;
                var name = set.Name;
                if (string.IsNullOrWhiteSpace(name) || name.StartsWith("<", StringComparison.Ordinal)) continue; // <In-Session> 等暫時集合
                var entry = new SheetSetItem { Name = name };
                foreach (var member in set.Members)
                {
                    if (member.IsSheet)
                    {
                        if (sheetUniqueIds.Contains(member.UniqueId)) entry.SheetUniqueIds.Add(member.UniqueId);
                    }
                    else if (knownUniqueIds.Contains(member.UniqueId))
                    {
                        entry.ViewUniqueIds.Add(member.UniqueId);
                    }
                }
                snapshot.SheetSets.Add(entry);
            }
            write("開視窗讀取圖紙集：" + snapshot.SheetSets.Count + " 個，" + stage.ElapsedMilliseconds + " ms；合計 " + total.ElapsedMilliseconds + " ms");
            return snapshot;
        }
    }
}
