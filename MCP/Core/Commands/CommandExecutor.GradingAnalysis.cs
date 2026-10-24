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
        /// <summary>
        /// 產生分析圖：對單一方案一次備妥「各別出圖」與「比較出圖」都需要的視覺內容——
        /// 方案視圖、挖填熱區、坡度熱區、範圍線、標高標註，另加方案專屬的正剖面＋剖面立體
        /// （單方案版的剖切線只吃這個方案自己的樓板快照範圍，不跟其他方案湊共同範圍）。
        /// 產生結果的視圖 ID 回寫方案記錄供「方案總覽」判斷完成度；本次實作不改動既有
        /// create_grading_presentation／create_grading_comparison_sheet 的視圖建立邏輯，
        /// 避免牽動已經多輪實機驗證過的既有出圖管線——兩者目前仍各自建立自己需要的視圖，
        /// 不會自動重用這裡產生的視圖（見規格 2026-07-10-grading-ribbon-consolidation-design.md）。
        /// </summary>
        private object GenerateGradingAnalysis(JObject parameters)
        {
            var doc = _uiApp.ActiveUIDocument.Document;
            var (design, record) = RequireSchemeRecord(doc, parameters);
            var designId = design.Id.GetIdValue();
            var designElementId = new ElementId(checked((int)designId));
            var schemeName = record.Value<string>("SchemeName") ?? $"方案{designId}";
            var steps = new List<string>();

            var focus = TryComputeComparisonFocus(doc, new[] { record });
            var mainViewId = EnsureSchemeView(doc, designId, record, steps);

            var cutfillViewId = DuplicateSchemeView(doc, mainViewId, $"整地-{schemeName}-挖填");
            TrySubStep(steps, "挖填熱區", () =>
                CreateCutfillHeatmap(new JObject
                {
                    ["designToposolidId"] = designId,
                    ["viewId"] = cutfillViewId,
                    ["sampleStepMeters"] = 3.0
                }));

            var slopeViewId = DuplicateSchemeView(doc, mainViewId, $"整地-{schemeName}-坡度");
            TrySubStep(steps, "坡度熱區", () =>
                CreateSlopeHeatmap(new JObject
                {
                    ["designToposolidId"] = designId,
                    ["viewId"] = slopeViewId,
                    ["sampleStepMeters"] = 3.0
                }));

            ApplyFocusSectionBoxToViewId(doc, mainViewId, focus);
            ApplyFocusSectionBoxToViewId(doc, cutfillViewId, focus);
            ApplyFocusSectionBoxToViewId(doc, slopeViewId, focus);

            TrySubStep(steps, "整地範圍線", () =>
                CreateGradingExtentLines(new JObject { ["designToposolidId"] = designId, ["viewId"] = mainViewId }));

            // 十七輪：與各別出圖同格局（一橫排三格 A0 假設）預估比例，槽高按實際字高。
            var analysisLabelHint = EstimateLabelScaleHint(focus, 380.0, 790.0);
            TrySubStep(steps, "標高標註", () =>
                AnnotateGradingScheme(new JObject
                {
                    ["designToposolidId"] = designId,
                    ["viewId"] = mainViewId,
                    ["annotateDaylight"] = true,
                    ["annotateSlopeLabel"] = true,
                    ["labelScaleHint"] = analysisLabelHint
                }));
            TrySubStep(steps, "標高標註(挖填)", () =>
                AnnotateGradingScheme(new JObject
                {
                    ["designToposolidId"] = designId,
                    ["viewId"] = cutfillViewId,
                    ["annotateFloors"] = false,
                    ["annotateDaylight"] = true,
                    ["annotateSlopeLabel"] = false,
                    ["labelScaleHint"] = analysisLabelHint
                }));
            TrySubStep(steps, "標高標註(坡度)", () =>
                AnnotateGradingScheme(new JObject
                {
                    ["designToposolidId"] = designId,
                    ["viewId"] = slopeViewId,
                    ["annotateFloors"] = false,
                    ["annotateDaylight"] = true,
                    ["annotateSlopeLabel"] = false,
                    ["labelScaleHint"] = analysisLabelHint
                }));

            // 新能力：單方案專屬正剖面＋剖面立體。剖切線計算比照 create_grading_comparison_sheet
            // 的單欄邏輯（CommandExecutor.GradingComparison.cs），但 records 只吃這一個方案自己，
            // 不跟其他方案湊共同範圍——這段是本次新寫、未經實機驗證的邏輯（見規格「待驗證的技術方案」）。
            long sectionId = 0;
            long isoId = 0;
            try
            {
                var originalId = record.Value<long>("OriginalToposolidId");
                var original = doc.GetElement(new ElementId(checked((int)originalId))) as Toposolid
                    ?? throw new InvalidOperationException($"原地形 {originalId} 不存在。");
                var singleRecordList = new[] { record };
                var (sectionStart, sectionEnd) = focus.HasValue
                    ? ComputeFocusedSectionLine(original, focus.Value)
                    : ComputeMaxDropSectionLine(original);
                var orientationView = ResolveComparisonOrientationView(doc, singleRecordList);
                (sectionStart, sectionEnd) = AlignSectionLineToViewRight(sectionStart, sectionEnd, orientationView);
                (sectionStart, sectionEnd) = TrimSectionLineToDisturbance(
                    doc, singleRecordList, original, sectionStart, sectionEnd);
                // 2026-07-10：單方案剖面邏輯首次實機驗證前的診斷加強——把算出來的剖切線座標
                // 寫進 log，首次失敗時不用重跑一次就能從 log 判斷是「方向錯」還是「範圍錯」
                // （座標為專案內部原點公尺，比照登記簿 ElevationBasis 慣例）。
                Logger.Info($"[產生分析圖] 方案剖面：{schemeName}"
                    + $" start=({FeetToMeters(sectionStart.X):0.00},{FeetToMeters(sectionStart.Y):0.00},{FeetToMeters(sectionStart.Z):0.00})"
                    + $" end=({FeetToMeters(sectionEnd.X):0.00},{FeetToMeters(sectionEnd.Y):0.00},{FeetToMeters(sectionEnd.Z):0.00})"
                    + $" focus={(focus.HasValue ? $"center=({focus.Value.CenterXMeters:0.00},{focus.Value.CenterYMeters:0.00},{focus.Value.CenterZMeters:0.00}) width={focus.Value.WidthMeters:0.00} depth={focus.Value.DepthMeters:0.00} height={focus.Value.HeightMeters:0.00}" : "無（退回原地形最大高差線）")}");

                var otherDesigns = CollectOtherTerrainIds(doc, designElementId)
                    .Concat(CollectOtherSchemeFloorIds(doc, record))
                    .ToList();

                using (var transaction = new Transaction(doc, "建立方案分析剖面視圖"))
                {
                    GradingFailuresPreprocessor.Attach(transaction, new GradingFailuresPreprocessor());
                    transaction.Start();
                    // 2026-07-11 十七輪：與比較出圖同步——剖面框高度按實際取樣 Z 範圍
                    // （挖方上緣不再被切），Toposolid 剖切隱藏、設計實線與色塊同源繪製。
                    var analysisOriginalSolids = RevitToposolidGradingAdapter.CollectSolids(original);
                    var analysisDesignSolids = RevitToposolidGradingAdapter.CollectSolids(design);
                    var analysisZRange = ComputeSectionZRange(
                        new IReadOnlyList<Solid>[] { analysisOriginalSolids, analysisDesignSolids },
                        sectionStart, sectionEnd);
                    var section = CreateSchemeSectionView(
                        doc, schemeName, sectionStart, sectionEnd, otherDesigns,
                        focus: focus, zRange: analysisZRange);
                    AddCutFillRegionsToSection(doc, section, record, sectionStart, sectionEnd);
                    AddSurfaceProfileToSection(doc, section, analysisDesignSolids,
                        sectionStart, sectionEnd, EnsureSolidDesignProfileStyle(doc));
                    AddOriginalProfileToSection(doc, section, record, sectionStart, sectionEnd);
                    AddSectionStationLabels(doc, section, record, sectionStart, sectionEnd);
                    sectionId = section.Id.GetIdValue();
                    isoId = CreateSchemeSectionIsoView(
                        doc, schemeName, sectionStart, sectionEnd,
                        otherDesigns.Concat(new[] { original.Id }).ToList(),
                        focus: focus,
                        styleSource: doc.GetElement(new ElementId(checked((int)mainViewId))) as View)
                        .Id.GetIdValue();
                    transaction.Commit();
                }

                steps.Add("剖面×2");
                TrySubStep(steps, "標高標註(剖立體)", () =>
                    AnnotateGradingScheme(new JObject
                    {
                        ["designToposolidId"] = designId,
                        ["viewId"] = isoId,
                        ["annotateFloors"] = false,
                        ["annotateDaylight"] = true,
                        ["annotateSlopeLabel"] = false,
                        ["labelScaleHint"] = analysisLabelHint
                    }));
            }
            catch (Exception ex)
            {
                steps.Add($"剖面失敗：{ex.GetType().Name}: {ex.Message}");
                Logger.Error($"[產生分析圖] 方案剖面建立失敗：{schemeName}", ex);
            }

            using (var transaction = new Transaction(doc, "回寫分析圖產生狀態"))
            {
                GradingFailuresPreprocessor.Attach(transaction, new GradingFailuresPreprocessor());
                transaction.Start();
                record["AnalysisGeneratedAt"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                record["AnalysisCutfillViewId"] = cutfillViewId;
                record["AnalysisSlopeViewId"] = slopeViewId;
                record["AnalysisSectionViewId"] = sectionId;
                record["AnalysisSectionIsoViewId"] = isoId;
                UpdateSchemeRecord(doc, design, record);
                transaction.Commit();
            }

            return new
            {
                DesignToposolidId = designId,
                SchemeName = schemeName,
                MainViewId = mainViewId,
                CutfillViewId = cutfillViewId,
                SlopeViewId = slopeViewId,
                SectionViewId = sectionId,
                SectionIsoViewId = isoId,
                Steps = steps,
                Message = $"方案「{schemeName}」分析圖已產生（{steps.Count} 個步驟）。"
            };
        }
    }
}
#endif
