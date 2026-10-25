#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace RevitMCP.Core.IssueManager
{
    /// <summary>
    /// 從 Revit 文件讀出「勾選視窗」要用的清單：圖紙（含視口）、可匯出的視圖、命名圖紙集（ViewSheetSet）。
    /// 唯讀，不開 Transaction；必須在 Revit API 執行緒呼叫。
    /// </summary>
    internal static class RevitSelectionCollector
    {
        public static ProjectSnapshot Collect(Document doc)
        {
            if (doc == null) throw new ArgumentNullException("doc");
            var snapshot = new ProjectSnapshot { DocumentTitle = doc.Title, DocumentPath = doc.PathName };

            var knownViews = new Dictionary<string, ViewItem>(StringComparer.Ordinal);
            foreach (var view in new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>())
            {
                if (view.IsTemplate) continue;
                var kind = KindOf(view);
                if (kind == null) continue;
                var item = new ViewItem { UniqueId = view.UniqueId, ElementId = view.Id.GetIdValue(), Name = view.Name, Kind = kind };
                knownViews[item.UniqueId] = item;
                snapshot.Views.Add(item);
            }

            var sheetUniqueIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var sheet in new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>())
            {
                if (sheet.IsPlaceholder) continue;
                var item = new SheetItem
                {
                    UniqueId = sheet.UniqueId, ElementId = sheet.Id.GetIdValue(), Number = sheet.SheetNumber, Title = sheet.Name
                };
                foreach (var viewportId in sheet.GetAllViewports())
                {
                    var viewport = doc.GetElement(viewportId) as Viewport;
                    if (viewport == null) continue;
                    var view = doc.GetElement(viewport.ViewId) as View;
                    if (view == null || !knownViews.ContainsKey(view.UniqueId)) continue; // 圖例、明細表、製圖視圖不對應模型位置，不列入
                    double width = 0, height = 0;
                    try
                    {
                        var outline = viewport.GetBoxOutline();
                        width = outline.MaximumPoint.X - outline.MinimumPoint.X;
                        height = outline.MaximumPoint.Y - outline.MinimumPoint.Y;
                    }
                    catch (Autodesk.Revit.Exceptions.ApplicationException)
                    {
                        // 取不到外框：面積視為 0，主要視口退回清單中的第一個。
                    }
                    item.Viewports.Add(new ViewportItem { ViewUniqueId = view.UniqueId, Width = width, Height = height });
                }
                sheetUniqueIds.Add(item.UniqueId);
                snapshot.Sheets.Add(item);
            }

            foreach (var set in new FilteredElementCollector(doc).OfClass(typeof(ViewSheetSet)).Cast<ViewSheetSet>())
            {
                var name = set.Name;
                if (string.IsNullOrWhiteSpace(name) || name.StartsWith("<", StringComparison.Ordinal)) continue; // <In-Session> 等暫時集合
                var entry = new SheetSetItem { Name = name };
                foreach (View member in set.Views)
                {
                    if (member is ViewSheet)
                    {
                        if (sheetUniqueIds.Contains(member.UniqueId)) entry.SheetUniqueIds.Add(member.UniqueId);
                    }
                    else if (knownViews.ContainsKey(member.UniqueId))
                    {
                        entry.ViewUniqueIds.Add(member.UniqueId);
                    }
                }
                snapshot.SheetSets.Add(entry);
            }
            return snapshot;
        }

        /// <summary>Revit 視圖種類 → manifest 種類；不對應模型位置的種類（圖例、明細表、製圖、漫遊、渲染…）回 null。</summary>
        internal static string KindOf(View view)
        {
            switch (view.ViewType)
            {
                case ViewType.ThreeD: return ViewKinds.ThreeD;
                case ViewType.FloorPlan:
                case ViewType.EngineeringPlan:
                case ViewType.AreaPlan: return ViewKinds.Plan;
                case ViewType.CeilingPlan: return ViewKinds.Ceiling;
                case ViewType.Section: return ViewKinds.Section;
                case ViewType.Elevation: return ViewKinds.Elevation;
                case ViewType.Detail: return ViewKinds.Detail;
                default: return null;
            }
        }
    }
}
