#if REVIT2024_OR_GREATER
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using RevitMCP.Core.Grading;

namespace RevitMCP.Core
{
    public partial class CommandExecutor
    {
        private const string TerrainReviewViewPrefix = "整地檢視";

        /// <summary>
        /// 方案檢視器：在專用「整地檢視」3D 視圖顯示指定地形（原地形或某方案成果），
        /// 保留樓板等現有元素、隱藏其他地形，視圖改名為含編號＋名稱。
        /// 用元素顯示/隱藏＋顯示拆除的階段篩選達成——只影響地形這類拆除物、不影響新建樓板；
        /// 全部關在專用視圖裡，不污染使用者的工作視圖。
        /// </summary>
        private object ShowGradingTerrain(JObject parameters)
        {
            var doc = _uiApp.ActiveUIDocument.Document;
            var records = RevitToposolidGradingAdapter.ReadSchemeRecords(doc)
                .Select(JObject.Parse).ToList();
            if (records.Count == 0)
            {
                throw new InvalidOperationException("目前沒有整地方案；請先建立至少一個方案。");
            }

            var originalId = records[0].Value<long>("OriginalToposolidId");
            var designIds = records.Select(r => r.Value<long>("DesignToposolidId")).ToList();

            var requestedDesign = parameters["designToposolidId"]?.Value<long?>() ?? 0;
            long targetId;
            string label;
            if (requestedDesign > 0)
            {
                var index = designIds.IndexOf(requestedDesign);
                if (index < 0)
                {
                    throw new InvalidOperationException($"方案設計地形 {requestedDesign} 不在登記簿中。");
                }

                targetId = requestedDesign;
                var name = records[index].Value<string>("SchemeName") ?? $"方案{index + 1}";
                label = $"方案{index + 1}：{name}";
            }
            else
            {
                targetId = originalId;
                label = "原地形（拆除前）";
            }

            var allTerrains = new List<long> { originalId };
            allTerrains.AddRange(designIds.Where(id => id != originalId));

            long viewId;
            using (var transaction = new Transaction(doc, "整地檢視顯示地形"))
            {
                GradingFailuresPreprocessor.Attach(transaction, new GradingFailuresPreprocessor());
                transaction.Start();
                var view = EnsureTerrainReviewView(doc);

                // 階段篩選設成「顯示拆除物件」，讓被拆除隱藏的原地形可見；
                // 只影響拆除類元素，新建的樓板不受影響。優先取「完成後」(ByCategory 正常顯示)，退「全部顯示」。
                var revealFilter = new FilteredElementCollector(doc)
                    .OfClass(typeof(PhaseFilter)).Cast<PhaseFilter>()
                    .Where(f => f.Name == "完成後" || f.Name == "Show Complete"
                        || f.Name == "全部顯示" || f.Name == "Show All")
                    .OrderBy(f => f.Name == "完成後" || f.Name == "Show Complete" ? 0 : 1)
                    .FirstOrDefault();
                if (revealFilter != null)
                {
                    view.get_Parameter(BuiltInParameter.VIEW_PHASE_FILTER)?.Set(revealFilter.Id);
                }

                foreach (var terrainId in allTerrains)
                {
                    var element = doc.GetElement(new ElementId(checked((int)terrainId)));
                    if (element == null)
                    {
                        continue;
                    }

                    var hidden = element.IsHidden(view);
                    var single = new List<ElementId> { element.Id };
                    if (terrainId == targetId)
                    {
                        if (hidden) { view.UnhideElements(single); }
                    }
                    else if (!hidden)
                    {
                        view.HideElements(single);
                    }
                }

                var desired = $"{TerrainReviewViewPrefix}｜{label}";
                var clash = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                    .Any(candidate => candidate.Id != view.Id && candidate.Name == desired);
                view.Name = clash ? $"{desired} ({view.Id.GetIdValue()})" : desired;

                viewId = view.Id.GetIdValue();
                transaction.Commit();
            }

            if (doc.GetElement(new ElementId(checked((int)viewId))) is View reviewView)
            {
                _uiApp.ActiveUIDocument.RequestViewChange(reviewView);
            }

            return new
            {
                ViewId = viewId,
                ShownLabel = label,
                TargetToposolidId = targetId,
                Message = $"整地檢視已切換到「{label}」。"
            };
        }

        /// <summary>取得（或建立）專用「整地檢視」等角 3D 視圖，以名稱前綴辨識、冪等重用。</summary>
        private static View3D EnsureTerrainReviewView(Document doc)
        {
            var existing = new FilteredElementCollector(doc)
                .OfClass(typeof(View3D)).Cast<View3D>()
                .FirstOrDefault(view => !view.IsTemplate
                    && view.Name.StartsWith(TerrainReviewViewPrefix, StringComparison.Ordinal));
            if (existing != null)
            {
                return existing;
            }

            var viewFamilyType = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                .FirstOrDefault(type => type.ViewFamily == ViewFamily.ThreeDimensional)
                ?? throw new InvalidOperationException("找不到 3D 視圖類型。");

            var view = View3D.CreateIsometric(doc, viewFamilyType.Id);
            view.Name = $"{TerrainReviewViewPrefix}｜（未選）";
            return view;
        }
    }
}
#endif
