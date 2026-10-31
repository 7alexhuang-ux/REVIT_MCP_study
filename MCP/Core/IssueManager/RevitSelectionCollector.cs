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
    /// 開視窗只讀「便宜欄位」（名稱、識別碼、種類、圖紙-視口-視圖對應）；視口外框（Viewport.GetBoxOutline，
    /// 大專案每個要好幾秒）不在這裡讀，由 <see cref="RevitBundleExportSteps.MeasureViewports"/> 在匯出時分批量測。
    /// </summary>
    internal static class RevitSelectionCollector
    {
        public static ProjectSnapshot Collect(Document doc, Action<string> log = null)
        {
            if (doc == null) throw new ArgumentNullException("doc");
            return SnapshotBuilder.Build(new RevitProjectSource(doc), log);
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

        private sealed class RevitProjectSource : IProjectSource
        {
            private readonly Document _doc;
            public RevitProjectSource(Document doc) { _doc = doc; }

            public string Title { get { return _doc.Title; } }
            public string Path { get { return _doc.PathName; } }

            public IList<SourceView> ReadViews()
            {
                var list = new List<SourceView>();
                using (var collector = new FilteredElementCollector(_doc).OfClass(typeof(View)))
                {
                    foreach (var view in collector.Cast<View>())
                    {
                        if (view.IsTemplate) continue;
                        var kind = KindOf(view);
                        if (kind == null) continue;
                        list.Add(new SourceView { UniqueId = view.UniqueId, ElementId = view.Id.GetIdValue(), Name = view.Name, Kind = kind });
                    }
                }
                return list;
            }

            public IList<SourceSheet> ReadSheets()
            {
                var list = new List<SourceSheet>();
                using (var collector = new FilteredElementCollector(_doc).OfClass(typeof(ViewSheet)))
                {
                    foreach (var sheet in collector.Cast<ViewSheet>())
                    {
                        if (sheet.IsPlaceholder) continue;
                        var item = new SourceSheet
                        {
                            UniqueId = sheet.UniqueId, ElementId = sheet.Id.GetIdValue(), Number = sheet.SheetNumber, Title = sheet.Name
                        };
                        // 只取視口與其視圖的 ElementId（便宜）；不呼叫 GetBoxOutline、不打開或重新生成視圖。
                        foreach (var viewportId in sheet.GetAllViewports())
                        {
                            var viewport = _doc.GetElement(viewportId) as Viewport;
                            if (viewport == null) continue;
                            item.Viewports.Add(new SourceViewport { ViewportElementId = viewportId.GetIdValue(), ViewElementId = viewport.ViewId.GetIdValue() });
                        }
                        list.Add(item);
                    }
                }
                return list;
            }

            public IList<SourceSheetSet> ReadSheetSets()
            {
                var list = new List<SourceSheetSet>();
                using (var collector = new FilteredElementCollector(_doc).OfClass(typeof(ViewSheetSet)))
                {
                    foreach (var set in collector.Cast<ViewSheetSet>())
                    {
                        var entry = new SourceSheetSet { Name = set.Name };
                        if (!string.IsNullOrWhiteSpace(entry.Name) && !entry.Name.StartsWith("<", StringComparison.Ordinal))
                        {
                            foreach (View member in set.Views)
                            {
                                entry.Members.Add(new SourceSheetSetMember { UniqueId = member.UniqueId, IsSheet = member is ViewSheet });
                            }
                        }
                        list.Add(entry);
                    }
                }
                return list;
            }
        }
    }
}
