#if REVIT2024_OR_GREATER
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Analysis;
using Newtonsoft.Json.Linq;
using RevitMCP.Core.Grading;

namespace RevitMCP.Core
{
    public partial class CommandExecutor
    {
        private const string SlopeSchemaName = "設計地形表面坡度";
        private const string SlopeDisplayStyleName = "RevitMCP 水保六級坡";

        /// <summary>顯示值上蓋（%）：>55 已同屬六級坡，避免極陡側面把圖例值域拉爆。</summary>
        private const double SlopeDisplayCapPercent = 60.0;

        /// <summary>
        /// 坡度熱區圖：以 AVF 在方案視圖上為設計地形頂面依「水保六級坡」著色。
        /// 各取樣點坡度由面法線求得（slope% = 100×√(nx²+ny²)/nz），再以 SlopeClassifier 分級（1..6）。
        /// AVF 值＝坡度%（上蓋 60），色帶分段點綁在六級坡門檻（5/15/30/40/55）→ 保證六段離散色。
        /// 僅視覺化；坡度統計權威值以正式計算書為準。
        /// </summary>
        private object CreateSlopeHeatmap(JObject parameters)
        {
            var doc = _uiApp.ActiveUIDocument.Document;
            var (design, record) = RequireSchemeRecord(doc, parameters);
            var sampleStepMeters = parameters["sampleStepMeters"]?.Value<double?>() ?? 2.0;
            if (sampleStepMeters <= 0)
            {
                throw new ArgumentException("sampleStepMeters 必須大於 0。");
            }

            var view = ResolveSchemeView(doc, parameters, record);
            var stepFeet = UnitUtils.ConvertToInternalUnits(sampleStepMeters, UnitTypeId.Meters);

            // 坡度熱區納入較陡設計面（門檻放寬至 0.1，排除近垂直側面）。
            var topFaces = CollectUpwardFaces(design, 0.1);
            if (topFaces.Count == 0)
            {
                throw new InvalidOperationException("設計地形沒有可著色的頂面。");
            }

            var styledFaces = 0;
            var totalPoints = 0;
            var maxSlopePercent = 0.0;
            var levelCounts = new int[7]; // 索引 1..6 對應六級坡

            // 2026-07-08 實機差分：鎖定視圖「同交易內解鎖」後 AddSpatialFieldPrimitive 仍拋例外、
            // 未鎖定視圖同碼成功——解鎖必須先行提交，AVF 用獨立交易，完成後再恢復鎖定。
            var wasLocked = UnlockViewCommitted(doc, view);
            try
            {
            using (var transaction = new Transaction(doc, "建立坡度熱區圖"))
            {
                GradingFailuresPreprocessor.Attach(transaction, new GradingFailuresPreprocessor());
                if (transaction.Start() != TransactionStatus.Started)
                {
                    throw new InvalidOperationException("無法啟動建立坡度熱區圖交易。");
                }

                var manager = SpatialFieldManager.GetSpatialFieldManager(view)
                    ?? SpatialFieldManager.CreateSpatialFieldManager(view, 1);
                ClearSpatialFieldPrimitives(manager);
                var schemaIndex = FindOrRegisterSlopeSchema(manager);

                foreach (var face in topFaces)
                {
                    var uvBox = face.GetBoundingBox();
                    var origin = face.Evaluate(uvBox.Min);
                    var uCorner = face.Evaluate(new UV(uvBox.Max.U, uvBox.Min.V));
                    var vCorner = face.Evaluate(new UV(uvBox.Min.U, uvBox.Max.V));
                    var uSteps = Math.Max(2, Math.Min(80, (int)Math.Ceiling(origin.DistanceTo(uCorner) / stepFeet)));
                    var vSteps = Math.Max(2, Math.Min(80, (int)Math.Ceiling(origin.DistanceTo(vCorner) / stepFeet)));

                    var uvPoints = new List<UV>();
                    var values = new List<ValueAtPoint>();
                    for (var uIndex = 0; uIndex <= uSteps; uIndex++)
                    {
                        for (var vIndex = 0; vIndex <= vSteps; vIndex++)
                        {
                            var uv = new UV(
                                uvBox.Min.U + ((uvBox.Max.U - uvBox.Min.U) * uIndex / uSteps),
                                uvBox.Min.V + ((uvBox.Max.V - uvBox.Min.V) * vIndex / vSteps));
                            if (!face.IsInside(uv))
                            {
                                continue;
                            }

                            XYZ normal;
                            try
                            {
                                normal = face.ComputeNormal(uv);
                            }
                            catch (Autodesk.Revit.Exceptions.ApplicationException)
                            {
                                continue;
                            }

                            if (normal.Z <= 1e-6)
                            {
                                continue; // 近垂直面無有效朝上坡度。
                            }

                            var slopePercent = 100.0
                                * Math.Sqrt((normal.X * normal.X) + (normal.Y * normal.Y)) / normal.Z;
                            var level = SlopeClassifier.Classify(slopePercent);
                            uvPoints.Add(uv);
                            values.Add(new ValueAtPoint(
                                new List<double> { Math.Min(slopePercent, SlopeDisplayCapPercent) }));
                            maxSlopePercent = Math.Max(maxSlopePercent, slopePercent);
                            levelCounts[level]++;
                        }
                    }

                    if (uvPoints.Count < 3)
                    {
                        continue;
                    }

                    var primitiveIndex = manager.AddSpatialFieldPrimitive(face.Reference);
                    manager.UpdateSpatialFieldPrimitive(
                        primitiveIndex,
                        new FieldDomainPointsByUV(uvPoints),
                        new FieldValues(values),
                        schemaIndex);
                    styledFaces++;
                    totalPoints += uvPoints.Count;
                }

                if (styledFaces == 0)
                {
                    throw new InvalidOperationException("坡度熱區圖取樣不到任何有效面。");
                }

                ApplySlopeDisplayStyle(doc, view);
                if (transaction.Commit() != TransactionStatus.Committed)
                {
                    throw new InvalidOperationException("建立坡度熱區圖交易未能提交。");
                }
            }
            }
            finally
            {
                RelockViewCommitted(doc, view, wasLocked);
            }

            var legend = SlopeClassifier.Classes.Select(slopeClass => new
            {
                Level = slopeClass.Level,
                Name = slopeClass.Name,
                Range = slopeClass.RangeText,
                Attribute = slopeClass.Attribute,
                PointCount = levelCounts[slopeClass.Level]
            }).ToList();

            return new
            {
                ViewId = view.Id.GetIdValue(),
                StyledFaceCount = styledFaces,
                SamplePointCount = totalPoints,
                MaxSlopePercent = maxSlopePercent,
                Legend = legend,
                Message = $"坡度熱區圖已建立（{styledFaces} 個面、{totalPoints} 個取樣點；"
                    + "色帶採台灣水土保持技術規範六級坡；AVF 僅為視覺化，坡度統計以正式計算書為準）。"
            };
        }

        private static int FindOrRegisterSlopeSchema(SpatialFieldManager manager)
        {
            foreach (var index in manager.GetRegisteredResults())
            {
                if (manager.GetResultSchema(index).Name == SlopeSchemaName)
                {
                    return index;
                }
            }

            return manager.RegisterResult(new AnalysisResultSchema(
                SlopeSchemaName, "設計地形表面坡度（%）；色帶依水保六級坡門檻 5/15/30/40/55 分段"));
        }

        /// <summary>清空視圖 AVF 管理器（primitives 與結果註冊一併清除），讓熱區重跑具冪等性；呼叫後需重新註冊 schema。</summary>
        private static void ClearSpatialFieldPrimitives(SpatialFieldManager manager)
        {
            manager.Clear();
        }

        private static void ApplySlopeDisplayStyle(Document doc, View view)
        {
            var colorSettings = new AnalysisDisplayColorSettings
            {
                MinColor = ToRevitColor(SlopeClassifier.GetClass(1)),
                MaxColor = ToRevitColor(SlopeClassifier.GetClass(6)),
                ColorSettingsType = AnalysisDisplayStyleColorSettingsType.SolidColorRanges
            };

            // AVF 值＝坡度%；分段點明確綁在六級坡門檻（5/15/30/40/55）且各帶該級定義色，
            // 保證渲染為六段離散色（實機教訓：未綁值的中間色不會落在級別邊界，看起來不是六色）。
            var intermediate = new List<AnalysisDisplayColorEntry>();
            for (var level = 2; level <= 6; level++)
            {
                var slopeClass = SlopeClassifier.GetClass(level);
                intermediate.Add(new AnalysisDisplayColorEntry(
                    ToRevitColor(slopeClass), slopeClass.LowerExclusivePercent.Value));
            }

            colorSettings.SetIntermediateColors(intermediate);

            var style = new FilteredElementCollector(doc)
                .OfClass(typeof(AnalysisDisplayStyle))
                .Cast<AnalysisDisplayStyle>()
                .FirstOrDefault(candidate => candidate.Name == SlopeDisplayStyleName);
            if (style == null)
            {
                var surfaceSettings = new AnalysisDisplayColoredSurfaceSettings { ShowGridLines = false };
                var legendSettings = new AnalysisDisplayLegendSettings();
                style = AnalysisDisplayStyle.CreateAnalysisDisplayStyle(
                    doc, SlopeDisplayStyleName, surfaceSettings, colorSettings, legendSettings);
            }
            else
            {
                // 既有樣式（可能由舊版程式建立）一律覆寫色帶設定，避免模型殘留舊六色定義。
                style.SetColorSettings(colorSettings);
            }

            view.AnalysisDisplayStyleId = style.Id;
        }

        private static Color ToRevitColor(SlopeClass slopeClass)
        {
            return new Color((byte)slopeClass.Red, (byte)slopeClass.Green, (byte)slopeClass.Blue);
        }
    }
}
#endif
