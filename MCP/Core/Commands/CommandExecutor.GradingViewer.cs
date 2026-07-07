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
                var view = EnsureTerrainReviewView(doc, _uiApp.ActiveUIDocument.ActiveView);

                // 階段篩選設成「顯示拆除物件為正常色」，讓被拆除的原地形可見；只影響拆除類、不影響新建樓板。
                // 修正 2026-07-07 實機缺陷：原本誤選「Show Complete」——該內建篩選其實「不顯示」拆除物件 → 原地形消失。
                var revealFilter = EnsureRevealPhaseFilter(doc);
                if (revealFilter != ElementId.InvalidElementId)
                {
                    view.get_Parameter(BuiltInParameter.VIEW_PHASE_FILTER)?.Set(revealFilter);
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

                // 方案名可含 Revit 禁字（如「1:12」的冒號），視圖改名前必過消毒，否則整個檢視器炸掉。
                var desired = GradingNames.SanitizeElementName($"{TerrainReviewViewPrefix}｜{label}");
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

        /// <summary>
        /// 取得（或建立）專用「整地檢視」3D 視圖：已存在（名稱前綴）即重用；否則**複製使用者目前的 3D 視圖**
        /// 以繼承其表現法（避免「表現法差很多」），非 3D 則退回等角新視圖；關閉剖面框確保地形完整可見。
        /// </summary>
        private static View3D EnsureTerrainReviewView(Document doc, View activeView)
        {
            var existing = new FilteredElementCollector(doc)
                .OfClass(typeof(View3D)).Cast<View3D>()
                .FirstOrDefault(view => !view.IsTemplate
                    && view.Name.StartsWith(TerrainReviewViewPrefix, StringComparison.Ordinal));
            if (existing != null)
            {
                return existing;
            }

            View3D reviewView = null;
            if (activeView is View3D source && !source.IsTemplate
                && source.ViewType == ViewType.ThreeD
                && source.CanViewBeDuplicated(ViewDuplicateOption.Duplicate))
            {
                var duplicateId = source.Duplicate(ViewDuplicateOption.Duplicate);
                reviewView = doc.GetElement(duplicateId) as View3D;
            }

            if (reviewView == null)
            {
                var viewFamilyType = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                    .FirstOrDefault(type => type.ViewFamily == ViewFamily.ThreeDimensional)
                    ?? throw new InvalidOperationException("找不到 3D 視圖類型。");
                reviewView = View3D.CreateIsometric(doc, viewFamilyType.Id);
            }

            if (reviewView.IsSectionBoxActive)
            {
                reviewView.IsSectionBoxActive = false; // 避免剖面框裁掉地形
            }

            reviewView.Name = $"{TerrainReviewViewPrefix}｜（未選）";
            return reviewView;
        }

        /// <summary>
        /// 取得（或建立）「顯示拆除物件為正常色」的階段篩選，讓被拆除的原地形能正常顯示。
        /// 內建「Show Complete/完成後」會「不顯示」拆除物件（原地形消失的根因），故不可用；
        /// 優先自建全 ByCategory 篩選，API 不支援時退回內建「Show All/全部顯示」（拆除呈半色調，仍可見）。
        /// </summary>
        private static ElementId EnsureRevealPhaseFilter(Document doc)
        {
            const string filterName = "整地-原地形顯示";
            var existing = new FilteredElementCollector(doc)
                .OfClass(typeof(PhaseFilter)).Cast<PhaseFilter>()
                .FirstOrDefault(filter => filter.Name == filterName);
            if (existing != null)
            {
                return existing.Id;
            }

            try
            {
                var created = PhaseFilter.Create(doc, filterName);
                // 四種狀態一律「ShowByCategory（正常色）」；關鍵是拆除也正常顯示 → 原地形看得到。
                created.SetPhaseStatusPresentation(ElementOnPhaseStatus.New, PhaseStatusPresentation.ShowByCategory);
                created.SetPhaseStatusPresentation(ElementOnPhaseStatus.Existing, PhaseStatusPresentation.ShowByCategory);
                created.SetPhaseStatusPresentation(ElementOnPhaseStatus.Demolished, PhaseStatusPresentation.ShowByCategory);
                created.SetPhaseStatusPresentation(ElementOnPhaseStatus.Temporary, PhaseStatusPresentation.ShowByCategory);
                return created.Id;
            }
            catch
            {
                var showAll = new FilteredElementCollector(doc)
                    .OfClass(typeof(PhaseFilter)).Cast<PhaseFilter>()
                    .FirstOrDefault(filter => filter.Name == "全部顯示" || filter.Name == "Show All"
                        || filter.Name.IndexOf("全部", StringComparison.Ordinal) >= 0
                        || filter.Name.IndexOf("All", StringComparison.OrdinalIgnoreCase) >= 0);
                return showAll?.Id ?? ElementId.InvalidElementId;
            }
        }
    }
}
#endif
