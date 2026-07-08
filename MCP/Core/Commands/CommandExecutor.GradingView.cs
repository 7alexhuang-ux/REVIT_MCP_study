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
        private const string CutFillSchemaName = "整地挖填深度";
        private const string CutFillDisplayStyleName = "RevitMCP 挖填熱區";

        /// <summary>取得指定設計地形與其方案記錄；無記錄即拒絕。</summary>
        private (Toposolid Design, JObject Record) RequireSchemeRecord(Document doc, JObject parameters)
        {
            var designId = parameters["designToposolidId"]?.Value<long>() ?? 0;
            if (designId <= 0 || designId > int.MaxValue)
            {
                throw new ArgumentException("designToposolidId 必須介於 1 與 Int32.MaxValue 之間。");
            }

            if (!(doc.GetElement(new ElementId(checked((int)designId))) is Toposolid design))
            {
                throw new InvalidOperationException($"元素 ID {designId} 不是 Toposolid。");
            }

            var json = RevitToposolidGradingAdapter.TryReadSchemeRecord(doc, designId);
            if (json == null)
            {
                throw new InvalidOperationException(
                    $"元素 ID {designId} 沒有整地方案記錄；請先以 grade_toposolid_to_floors 建立方案。");
            }

            return (design, JObject.Parse(json));
        }

        /// <summary>把（修改後的）方案記錄 JSON 覆寫回設計地形的 Extensible Storage。</summary>
        private static void UpdateSchemeRecord(Document doc, Toposolid design, JObject record)
        {
            IToposolidGradingAdapter adapter = new RevitToposolidGradingAdapter();
            adapter.WriteSchemeRecord(
                doc,
                design,
                record.ToString(Newtonsoft.Json.Formatting.None),
                record.Value<string>("AssociationId"));
        }

        /// <summary>依 viewId 參數或記錄的 SchemeViewId 解析方案視圖。</summary>
        private static View ResolveSchemeView(Document doc, JObject parameters, JObject record)
        {
            var viewIdValue = parameters["viewId"]?.Value<long?>() ?? record.Value<long?>("SchemeViewId");
            if (!viewIdValue.HasValue || viewIdValue.Value <= 0)
            {
                throw new InvalidOperationException(
                    "未指定 viewId 且方案沒有 SchemeViewId；請先執行 create_grading_scheme_view。");
            }

            if (!(doc.GetElement(new ElementId(checked((int)viewIdValue.Value))) is View view))
            {
                throw new InvalidOperationException($"視圖 ID {viewIdValue.Value} 不存在。");
            }

            return view;
        }

        /// <summary>
        /// 建立方案鎖定 3D 視圖：保留模型全部既有元素（柱樑、梁板、植栽…），只控制地形可見性
        /// （顯示本方案設計地形、隱藏原地形與其他方案）；複製使用者當前 3D 視圖繼承表現法；
        /// 鎖定方位（Spot Elevation 前提）、可選匯出 PNG；視圖 ID 與截圖路徑回寫方案記錄。
        /// </summary>
        private object CreateGradingSchemeView(JObject parameters)
        {
            var doc = _uiApp.ActiveUIDocument.Document;
            var (design, record) = RequireSchemeRecord(doc, parameters);
            var exportPng = parameters["exportPng"]?.Value<bool?>() ?? true;
            var outputPath = parameters["outputPath"]?.Value<string>();
            var schemeName = record.Value<string>("SchemeName") ?? $"方案{design.Id.GetIdValue()}";

            // 2026-07-08 使用者裁決：方案視圖不得隔離到只剩控制樓板＋設計地形——柱樑、梁板、
            // 植栽等既有元素全部保留，只控制「哪個地形可見」（藏原地形與其他方案設計地形）。
            var hideIds = CollectOtherTerrainIds(doc, design.Id);

            View3D view;
            using (var transaction = new Transaction(doc, "建立整地方案視圖"))
            {
                GradingFailuresPreprocessor.Attach(transaction, new GradingFailuresPreprocessor());
                if (transaction.Start() != TransactionStatus.Started)
                {
                    throw new InvalidOperationException("無法啟動建立整地方案視圖交易。");
                }

                view = CreateContextSchemeView3D(doc, _uiApp.ActiveUIDocument.ActiveView);
                view.Name = UniqueViewName(doc, $"整地-{schemeName}");
                ApplySchemeVisibility(doc, view, hideIds);
                view.SaveOrientationAndLock();
                record["SchemeViewId"] = view.Id.GetIdValue();
                UpdateSchemeRecord(doc, design, record);
                if (transaction.Commit() != TransactionStatus.Committed)
                {
                    throw new InvalidOperationException("建立整地方案視圖交易未能提交。");
                }
            }

            string screenshotPath = null;
            if (exportPng)
            {
                screenshotPath = ExportViewPng(doc, view, outputPath, $"整地-{schemeName}");
                using (var transaction = new Transaction(doc, "回寫方案截圖路徑"))
                {
                    GradingFailuresPreprocessor.Attach(transaction, new GradingFailuresPreprocessor());
                    if (transaction.Start() != TransactionStatus.Started)
                    {
                        throw new InvalidOperationException("無法啟動回寫方案截圖路徑交易。");
                    }

                    record["ScreenshotPath"] = screenshotPath;
                    UpdateSchemeRecord(doc, design, record);
                    if (transaction.Commit() != TransactionStatus.Committed)
                    {
                        throw new InvalidOperationException("回寫方案截圖路徑交易未能提交。");
                    }
                }
            }

            return new
            {
                ViewId = view.Id.GetIdValue(),
                ViewName = view.Name,
                ScreenshotPath = screenshotPath,
                Message = $"方案視圖「{view.Name}」已建立並鎖定"
                    + (screenshotPath != null ? $"，截圖已輸出：{screenshotPath}" : "。")
            };
        }

        /// <summary>登記簿內全部地形（原地形＋各方案設計地形）中，非目標方案者——方案視圖要藏的清單。</summary>
        private static IReadOnlyList<ElementId> CollectOtherTerrainIds(Document doc, ElementId targetDesignId)
        {
            var ids = new List<ElementId>();
            foreach (var json in RevitToposolidGradingAdapter.ReadSchemeRecords(doc))
            {
                var record = JObject.Parse(json);
                var originalId = new ElementId(checked((int)record.Value<long>("OriginalToposolidId")));
                var designId = new ElementId(checked((int)record.Value<long>("DesignToposolidId")));
                if (originalId != targetDesignId && !ids.Contains(originalId))
                {
                    ids.Add(originalId);
                }

                if (designId != targetDesignId && !ids.Contains(designId))
                {
                    ids.Add(designId);
                }
            }

            return ids;
        }

        /// <summary>
        /// 2026-07-08 使用者裁決：方案視圖保留全部既有元素。做法與整地檢視器一致——
        /// 複製使用者當前 3D 視圖以繼承表現法（非 3D 退回等角新視圖）、關閉剖面框。
        /// </summary>
        private static View3D CreateContextSchemeView3D(Document doc, View activeView)
        {
            View3D view = null;
            if (activeView is View3D source && !source.IsTemplate
                && source.ViewType == ViewType.ThreeD
                && source.CanViewBeDuplicated(ViewDuplicateOption.Duplicate))
            {
                view = doc.GetElement(source.Duplicate(ViewDuplicateOption.Duplicate)) as View3D;
            }

            if (view == null)
            {
                var viewFamilyType = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                    .FirstOrDefault(candidate => candidate.ViewFamily == ViewFamily.ThreeDimensional)
                    ?? throw new InvalidOperationException("找不到 3D 視圖類型（ViewFamilyType）。");
                view = View3D.CreateIsometric(doc, viewFamilyType.Id);
            }

            if (view.IsSectionBoxActive)
            {
                view.IsSectionBoxActive = false; // 避免使用者視圖的剖面框裁掉地形。
            }

            return view;
        }

        /// <summary>
        /// 方案視圖可見性：階段篩選藏拆除物（原地形與其子分割）＋明確隱藏其他地形。
        /// 階段篩選建立失敗時仍有明確隱藏兜底（hideIds 已含原地形）。
        /// </summary>
        private static void ApplySchemeVisibility(Document doc, View view, IReadOnlyList<ElementId> hideIds)
        {
            var phaseFilterId = EnsureSchemeResultPhaseFilter(doc);
            if (phaseFilterId != ElementId.InvalidElementId)
            {
                view.get_Parameter(BuiltInParameter.VIEW_PHASE_FILTER)?.Set(phaseFilterId);
            }

            var toHide = hideIds
                .Select(id => doc.GetElement(id))
                .Where(element => element != null && element.CanBeHidden(view) && !element.IsHidden(view))
                .Select(element => element.Id)
                .ToList();
            if (toHide.Count > 0)
            {
                view.HideElements(toHide);
            }
        }

        /// <summary>
        /// 「整地-方案顯示」階段篩選：拆除／暫時不顯示（原地形消失、成果乾淨）、新建／既有正常。
        /// 與檢視器的「整地-原地形顯示」（拆除正常色）互補，兩者用途相反。
        /// </summary>
        private static ElementId EnsureSchemeResultPhaseFilter(Document doc)
        {
            const string filterName = "整地-方案顯示";
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
                created.SetPhaseStatusPresentation(ElementOnPhaseStatus.New, PhaseStatusPresentation.ShowByCategory);
                created.SetPhaseStatusPresentation(ElementOnPhaseStatus.Existing, PhaseStatusPresentation.ShowByCategory);
                created.SetPhaseStatusPresentation(ElementOnPhaseStatus.Demolished, PhaseStatusPresentation.DontShow);
                created.SetPhaseStatusPresentation(ElementOnPhaseStatus.Temporary, PhaseStatusPresentation.DontShow);
                return created.Id;
            }
            catch
            {
                return ElementId.InvalidElementId; // 兜底：呼叫端的明確隱藏清單已含原地形。
            }
        }

        private static string UniqueViewName(Document doc, string baseName)
        {
            baseName = GradingNames.SanitizeElementName(baseName);
            var existingNames = new HashSet<string>(
                new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Select(v => v.Name));
            if (!existingNames.Contains(baseName))
            {
                return baseName;
            }

            for (var suffix = 2; suffix < 1000; suffix++)
            {
                var candidate = $"{baseName}({suffix})";
                if (!existingNames.Contains(candidate))
                {
                    return candidate;
                }
            }

            throw new InvalidOperationException("無法產生不重複的視圖名稱。");
        }

        private static string ExportViewPng(Document doc, View view, string outputPath, string baseName)
        {
            string directory;
            string fileBase;
            if (string.IsNullOrEmpty(outputPath))
            {
                var projectPath = doc.PathName;
                directory = string.IsNullOrEmpty(projectPath)
                    ? Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
                    : System.IO.Path.GetDirectoryName(projectPath);
                fileBase = baseName;
            }
            else
            {
                directory = System.IO.Path.GetDirectoryName(outputPath);
                if (string.IsNullOrEmpty(directory))
                {
                    throw new ArgumentException("outputPath 必須包含資料夾路徑。");
                }

                System.IO.Directory.CreateDirectory(directory);
                fileBase = System.IO.Path.GetFileNameWithoutExtension(outputPath);
            }

            foreach (var invalid in System.IO.Path.GetInvalidFileNameChars())
            {
                fileBase = fileBase.Replace(invalid, '_');
            }

            var exportBase = System.IO.Path.Combine(directory, $"{fileBase}_raw");
            var options = new ImageExportOptions
            {
                ExportRange = ExportRange.SetOfViews,
                FilePath = exportBase,
                HLRandWFViewsFileType = ImageFileType.PNG,
                ShadowViewsFileType = ImageFileType.PNG,
                ImageResolution = ImageResolution.DPI_150,
                ZoomType = ZoomFitType.FitToPage,
                PixelSize = 2000
            };
            options.SetViewsAndSheets(new List<ElementId> { view.Id });
            doc.ExportImage(options);

            // Revit 會替輸出檔名附加視圖描述；找到實際輸出檔並改名為可預期的目標檔名。
            var produced = System.IO.Directory.GetFiles(directory, $"{fileBase}_raw*.png")
                .OrderByDescending(System.IO.File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (produced == null)
            {
                throw new InvalidOperationException("視圖截圖匯出失敗：找不到輸出的 PNG 檔。");
            }

            var target = System.IO.Path.Combine(directory, $"{fileBase}.png");
            if (System.IO.File.Exists(target))
            {
                System.IO.File.Delete(target);
            }

            System.IO.File.Move(produced, target);
            return target;
        }

        /// <summary>
        /// 挖填熱區圖：以 AVF 在方案視圖上為設計地形頂面著色，值 = 原地形Z − 設計Z（公尺，正=挖紅、負=填藍）。
        /// 僅視覺化，不產生土方數字。
        /// </summary>
        private object CreateCutfillHeatmap(JObject parameters)
        {
            var doc = _uiApp.ActiveUIDocument.Document;
            var (design, record) = RequireSchemeRecord(doc, parameters);
            var sampleStepMeters = parameters["sampleStepMeters"]?.Value<double?>() ?? 2.0;
            if (sampleStepMeters <= 0)
            {
                throw new ArgumentException("sampleStepMeters 必須大於 0。");
            }

            var view = ResolveSchemeView(doc, parameters, record);
            var originalId = record.Value<long?>("OriginalToposolidId") ?? 0;
            if (!(doc.GetElement(new ElementId(checked((int)originalId))) is Toposolid originalToposolid))
            {
                throw new InvalidOperationException($"原地形 ID {originalId} 不存在或已被刪除。");
            }

            var originalSolids = RevitToposolidGradingAdapter.CollectSolids(originalToposolid);
            if (originalSolids.Count == 0)
            {
                throw new InvalidOperationException("原地形沒有可用的實體幾何。");
            }

            var boundingBox = originalToposolid.get_BoundingBox(null) ?? design.get_BoundingBox(null);
            if (boundingBox == null)
            {
                throw new InvalidOperationException("無法取得地形範圍盒。");
            }

            var rayBottomZ = boundingBox.Min.Z - 10.0;
            var rayTopZ = boundingBox.Max.Z + 10.0;
            var stepFeet = UnitUtils.ConvertToInternalUnits(sampleStepMeters, UnitTypeId.Meters);

            var topFaces = CollectUpwardFaces(design, 0.5);
            if (topFaces.Count == 0)
            {
                throw new InvalidOperationException("設計地形沒有可著色的頂面。");
            }

            var styledFaces = 0;
            var totalPoints = 0;
            var maxCutMeters = 0.0;
            var maxFillMeters = 0.0;
            // 2026-07-08 實機差分：鎖定視圖「同交易內解鎖」後 AddSpatialFieldPrimitive 仍拋例外、
            // 未鎖定視圖同碼成功——解鎖必須先行提交，AVF 用獨立交易，完成後再恢復鎖定。
            var wasLocked = UnlockViewCommitted(doc, view);
            try
            {
            using (var transaction = new Transaction(doc, "建立挖填熱區圖"))
            {
                GradingFailuresPreprocessor.Attach(transaction, new GradingFailuresPreprocessor());
                if (transaction.Start() != TransactionStatus.Started)
                {
                    throw new InvalidOperationException("無法啟動建立挖填熱區圖交易。");
                }

                var manager = SpatialFieldManager.GetSpatialFieldManager(view)
                    ?? SpatialFieldManager.CreateSpatialFieldManager(view, 1);
                var schemaIndex = FindOrRegisterCutFillSchema(manager);

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

                            var point = face.Evaluate(uv);
                            var terrainZ = RevitToposolidGradingAdapter.IntersectTerrainTopZ(
                                originalSolids, new Point2D(point.X, point.Y), rayBottomZ, rayTopZ);
                            if (!terrainZ.HasValue)
                            {
                                continue;
                            }

                            var depthMeters = UnitUtils.ConvertFromInternalUnits(
                                terrainZ.Value - point.Z, UnitTypeId.Meters);
                            uvPoints.Add(uv);
                            values.Add(new ValueAtPoint(new List<double> { depthMeters }));
                            maxCutMeters = Math.Max(maxCutMeters, depthMeters);
                            maxFillMeters = Math.Max(maxFillMeters, -depthMeters);
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
                    throw new InvalidOperationException("熱區圖取樣不到任何有效點（設計地形與原地形可能沒有垂直重疊）。");
                }

                ApplyCutFillDisplayStyle(doc, view);
                if (transaction.Commit() != TransactionStatus.Committed)
                {
                    throw new InvalidOperationException("建立挖填熱區圖交易未能提交。");
                }
            }
            }
            finally
            {
                RelockViewCommitted(doc, view, wasLocked);
            }

            return new
            {
                ViewId = view.Id.GetIdValue(),
                StyledFaceCount = styledFaces,
                SamplePointCount = totalPoints,
                MaxCutDepthMeters = maxCutMeters,
                MaxFillHeightMeters = maxFillMeters,
                Message = $"挖填熱區圖已建立（{styledFaces} 個面、{totalPoints} 個取樣點；"
                    + "紅=挖、藍=填；AVF 僅為視覺化，土方量以登記簿為準）。"
            };
        }

        /// <summary>
        /// 方案視圖以 SaveOrientationAndLock 鎖定，鎖定中的 3D 視圖會拒絕 AVF 相關修改。
        /// 2026-07-08 實機差分：「同交易內解鎖」不夠——AddSpatialFieldPrimitive 仍拋例外，
        /// 解鎖必須以獨立交易先行提交（含重生），AVF 才吃得到解鎖狀態。
        /// </summary>
        private static bool UnlockViewCommitted(Document doc, View view)
        {
            if (!(view is View3D view3D) || !view3D.IsLocked)
            {
                return false;
            }

            using (var transaction = new Transaction(doc, "解鎖方案視圖（AVF 前置）"))
            {
                GradingFailuresPreprocessor.Attach(transaction, new GradingFailuresPreprocessor());
                if (transaction.Start() != TransactionStatus.Started)
                {
                    throw new InvalidOperationException("無法啟動解鎖方案視圖交易。");
                }

                view3D.Unlock();
                doc.Regenerate();
                if (transaction.Commit() != TransactionStatus.Committed)
                {
                    throw new InvalidOperationException("解鎖方案視圖交易未能提交。");
                }
            }

            return true;
        }

        /// <summary>恢復方案視圖鎖定（獨立交易；AVF 主體失敗也要執行，避免視圖留在解鎖狀態）。</summary>
        private static void RelockViewCommitted(Document doc, View view, bool wasLocked)
        {
            if (!wasLocked || !(view is View3D view3D))
            {
                return;
            }

            using (var transaction = new Transaction(doc, "恢復方案視圖鎖定"))
            {
                GradingFailuresPreprocessor.Attach(transaction, new GradingFailuresPreprocessor());
                if (transaction.Start() != TransactionStatus.Started)
                {
                    return; // 鎖不回去不應蓋掉 AVF 主體的結果或例外。
                }

                view3D.SaveOrientationAndLock();
                transaction.Commit();
            }
        }

        private static IReadOnlyList<Face> CollectUpwardFaces(Toposolid toposolid, double minimumNormalZ)
        {
            var faces = new List<Face>();
            var geometry = toposolid.get_Geometry(new Options { ComputeReferences = true });
            if (geometry == null)
            {
                return faces;
            }

            foreach (var geometryObject in geometry)
            {
                if (!(geometryObject is Solid solid) || solid.Volume <= 0)
                {
                    continue;
                }

                foreach (Face face in solid.Faces)
                {
                    if (face.Reference == null)
                    {
                        continue;
                    }

                    var uvBox = face.GetBoundingBox();
                    var midUV = new UV((uvBox.Min.U + uvBox.Max.U) / 2, (uvBox.Min.V + uvBox.Max.V) / 2);
                    XYZ normal;
                    try
                    {
                        normal = face.ComputeNormal(midUV);
                    }
                    catch (Autodesk.Revit.Exceptions.ApplicationException)
                    {
                        continue;
                    }

                    if (normal.Z > minimumNormalZ)
                    {
                        faces.Add(face);
                    }
                }
            }

            return faces;
        }

        private static int FindOrRegisterCutFillSchema(SpatialFieldManager manager)
        {
            foreach (var index in manager.GetRegisteredResults())
            {
                if (manager.GetResultSchema(index).Name == CutFillSchemaName)
                {
                    return index;
                }
            }

            return manager.RegisterResult(new AnalysisResultSchema(
                CutFillSchemaName, "原地形Z−設計Z（公尺）；正=挖、負=填"));
        }

        private static void ApplyCutFillDisplayStyle(Document doc, View view)
        {
            var style = new FilteredElementCollector(doc)
                .OfClass(typeof(AnalysisDisplayStyle))
                .Cast<AnalysisDisplayStyle>()
                .FirstOrDefault(candidate => candidate.Name == CutFillDisplayStyleName);
            if (style == null)
            {
                var surfaceSettings = new AnalysisDisplayColoredSurfaceSettings { ShowGridLines = false };
                var colorSettings = new AnalysisDisplayColorSettings
                {
                    MinColor = new Color(0, 85, 204),
                    MaxColor = new Color(204, 0, 0),
                    ColorSettingsType = AnalysisDisplayStyleColorSettingsType.GradientColor
                };
                var legendSettings = new AnalysisDisplayLegendSettings();
                style = AnalysisDisplayStyle.CreateAnalysisDisplayStyle(
                    doc, CutFillDisplayStyleName, surfaceSettings, colorSettings, legendSettings);
            }

            view.AnalysisDisplayStyleId = style.Id;
        }

        /// <summary>
        /// 標高標註：在方案鎖定 3D 視圖為控制樓板頂面（中心＋角點內縮）與
        /// 設計地形沿樓板邊界的 daylight 取樣點建立 Spot Elevation。逐點容錯，回報成敗數。
        /// </summary>
        private object AnnotateGradingScheme(JObject parameters)
        {
            var doc = _uiApp.ActiveUIDocument.Document;
            var (design, record) = RequireSchemeRecord(doc, parameters);
            var annotateFloors = parameters["annotateFloors"]?.Value<bool?>() ?? true;
            var annotateDaylight = parameters["annotateDaylight"]?.Value<bool?>() ?? false;
            var view = ResolveSchemeView(doc, parameters, record);

            var floorIds = (record["FloorIds"] as JArray)?.Values<long>().ToList() ?? new List<long>();
            var cornerInset = UnitUtils.ConvertToInternalUnits(300, UnitTypeId.Millimeters);
            var placed = 0;
            var failed = 0;
            using (var transaction = new Transaction(doc, "整地方案標高標註"))
            {
                GradingFailuresPreprocessor.Attach(transaction, new GradingFailuresPreprocessor());
                if (transaction.Start() != TransactionStatus.Started)
                {
                    throw new InvalidOperationException("無法啟動整地方案標高標註交易。");
                }

                if (annotateFloors)
                {
                    foreach (var floorId in floorIds)
                    {
                        if (!(doc.GetElement(new ElementId(checked((int)floorId))) is Floor floor))
                        {
                            failed++;
                            continue;
                        }

                        foreach (var face in CollectUpwardPlanarFaces(floor))
                        {
                            foreach (var point in BuildFloorAnnotationPoints(face, cornerInset))
                            {
                                if (TryPlaceSpotElevation(doc, view, face.Reference, point))
                                {
                                    placed++;
                                }
                                else
                                {
                                    failed++;
                                }
                            }
                        }
                    }
                }

                if (annotateDaylight)
                {
                    var designFaces = CollectUpwardFaces(design, 0.3);
                    IToposolidGradingAdapter adapter = new RevitToposolidGradingAdapter();
                    var floors = adapter.ValidateFloors(doc, floorIds);
                    var footprints = adapter.ExtractBottomFootprints(floors);
                    foreach (var footprint in footprints)
                    {
                        var loop = footprint.OuterLoop;
                        var step = Math.Max(1, loop.Count / 12);
                        for (var index = 0; index < loop.Count; index += step)
                        {
                            var boundary = loop[index];
                            if (!TryProjectToFace(
                                designFaces, new XYZ(boundary.X, boundary.Y, 0),
                                out var face, out var projected))
                            {
                                failed++;
                                continue;
                            }

                            if (TryPlaceSpotElevation(doc, view, face.Reference, projected))
                            {
                                placed++;
                            }
                            else
                            {
                                failed++;
                            }
                        }
                    }
                }

                if (transaction.Commit() != TransactionStatus.Committed)
                {
                    throw new InvalidOperationException("整地方案標高標註交易未能提交。");
                }
            }

            return new
            {
                ViewId = view.Id.GetIdValue(),
                PlacedCount = placed,
                FailedCount = failed,
                Message = $"標高標註完成：成功 {placed} 點、失敗 {failed} 點"
                    + (failed > 0 ? "（失敗點多為面參考無效或位置重疊，可於視圖手動補標）。" : "。")
            };
        }

        private static IReadOnlyList<PlanarFace> CollectUpwardPlanarFaces(Element element)
        {
            var faces = new List<PlanarFace>();
            var geometry = element.get_Geometry(new Options { ComputeReferences = true });
            if (geometry == null)
            {
                return faces;
            }

            foreach (var geometryObject in geometry)
            {
                if (!(geometryObject is Solid solid) || solid.Volume <= 0)
                {
                    continue;
                }

                foreach (Face face in solid.Faces)
                {
                    if (face is PlanarFace planarFace
                        && planarFace.FaceNormal.Z > 0.965
                        && planarFace.Reference != null)
                    {
                        faces.Add(planarFace);
                    }
                }
            }

            return faces;
        }

        private static IReadOnlyList<XYZ> BuildFloorAnnotationPoints(PlanarFace face, double cornerInset)
        {
            var points = new List<XYZ>();
            var uvBox = face.GetBoundingBox();
            var midUV = new UV((uvBox.Min.U + uvBox.Max.U) / 2, (uvBox.Min.V + uvBox.Max.V) / 2);
            XYZ centroid = null;
            if (face.IsInside(midUV))
            {
                centroid = face.Evaluate(midUV);
                points.Add(centroid);
            }

            var loops = face.GetEdgesAsCurveLoops();
            if (loops == null || loops.Count == 0)
            {
                return points;
            }

            foreach (var curve in loops[0])
            {
                var corner = curve.GetEndPoint(0);
                if (centroid == null)
                {
                    points.Add(corner);
                    continue;
                }

                var toward = centroid - corner;
                var distance = toward.GetLength();
                points.Add(distance > cornerInset
                    ? corner + (toward.Normalize() * cornerInset)
                    : corner);
            }

            return points;
        }

        private static bool TryPlaceSpotElevation(Document doc, View view, Reference reference, XYZ point)
        {
            try
            {
                doc.Create.NewSpotElevation(view, reference, point, point, point, point, false);
                return true;
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                return false;
            }
        }

        private static bool TryProjectToFace(
            IReadOnlyList<Face> faces,
            XYZ point,
            out Face nearestFace,
            out XYZ projected)
        {
            nearestFace = null;
            projected = null;
            var nearestDistance = double.MaxValue;
            foreach (var face in faces)
            {
                IntersectionResult result;
                try
                {
                    result = face.Project(point);
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException)
                {
                    continue;
                }

                if (result == null)
                {
                    continue;
                }

                // 只比 XY 距離：投影點應落在邊界樁的正上方或極近處。
                var deltaX = result.XYZPoint.X - point.X;
                var deltaY = result.XYZPoint.Y - point.Y;
                var distance = Math.Sqrt((deltaX * deltaX) + (deltaY * deltaY));
                if (distance < nearestDistance)
                {
                    nearestDistance = distance;
                    nearestFace = face;
                    projected = result.XYZPoint;
                }
            }

            var tolerance = UnitUtils.ConvertToInternalUnits(500, UnitTypeId.Millimeters);
            return nearestFace != null && nearestDistance <= tolerance;
        }

        /// <summary>
        /// 方案還原：把記錄的樓板「自標高偏移」寫回（僅支援樓板高程不同型方案）；
        /// rerunGrading=true 時以原參數重跑整地落成新方案。
        /// </summary>
        private object RestoreGradingScheme(JObject parameters)
        {
            var doc = _uiApp.ActiveUIDocument.Document;
            var (design, record) = RequireSchemeRecord(doc, parameters);
            var rerunGrading = parameters["rerunGrading"]?.Value<bool?>() ?? false;
            var schemeName = record.Value<string>("SchemeName") ?? "未命名方案";

            var metrics = (record["FloorMetrics"] as JArray)?.OfType<JObject>().ToList()
                ?? new List<JObject>();
            if (metrics.Count == 0 || metrics.Any(metric => metric.Value<double?>("HeightOffsetMeters") == null))
            {
                throw new InvalidOperationException(
                    "此方案記錄沒有樓板高程資料（SchemaVersion<3 的舊記錄），無法自動還原；"
                    + "可改用 Design Option 作手動幾何容器。");
            }

            var floors = new List<(Floor Floor, double TargetOffsetFeet, double CurrentOffsetFeet)>();
            foreach (var metric in metrics)
            {
                var floorId = metric.Value<long>("FloorId");
                if (!(doc.GetElement(new ElementId(checked((int)floorId))) is Floor floor))
                {
                    throw new InvalidOperationException(
                        $"控制樓板 ID {floorId} 已不存在——此方案屬於輪廓不同型，無法以參數寫回還原；"
                        + "請改用 Design Option 作手動幾何容器。");
                }

                var offsetParameter = floor.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM);
                if (offsetParameter == null || offsetParameter.IsReadOnly)
                {
                    throw new InvalidOperationException($"樓板 ID {floorId} 的自標高偏移不可寫入。");
                }

                floors.Add((
                    floor,
                    UnitUtils.ConvertToInternalUnits(
                        metric.Value<double>("HeightOffsetMeters"), UnitTypeId.Meters),
                    offsetParameter.AsDouble()));
            }

            var alreadyThere = floors.All(entry =>
                Math.Abs(entry.TargetOffsetFeet - entry.CurrentOffsetFeet)
                    <= UnitUtils.ConvertToInternalUnits(1.0, UnitTypeId.Millimeters));
            var adjusted = new List<object>();
            if (!alreadyThere)
            {
                using (var transaction = new Transaction(doc, "還原整地方案樓板高程"))
                {
                    GradingFailuresPreprocessor.Attach(transaction, new GradingFailuresPreprocessor());
                    if (transaction.Start() != TransactionStatus.Started)
                    {
                        throw new InvalidOperationException("無法啟動還原整地方案交易。");
                    }

                    foreach (var (floor, targetOffsetFeet, currentOffsetFeet) in floors)
                    {
                        var offsetParameter = floor.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM);
                        if (!offsetParameter.Set(targetOffsetFeet))
                        {
                            throw new InvalidOperationException($"樓板 ID {floor.Id.Value} 的自標高偏移寫入失敗。");
                        }

                        adjusted.Add(new
                        {
                            FloorId = floor.Id.GetIdValue(),
                            PreviousOffsetMeters = UnitUtils.ConvertFromInternalUnits(
                                currentOffsetFeet, UnitTypeId.Meters),
                            RestoredOffsetMeters = UnitUtils.ConvertFromInternalUnits(
                                targetOffsetFeet, UnitTypeId.Meters)
                        });
                    }

                    if (transaction.Commit() != TransactionStatus.Committed)
                    {
                        throw new InvalidOperationException("還原整地方案交易未能提交。");
                    }
                }
            }

            object gradeResult = null;
            if (rerunGrading)
            {
                var gradeParameters = new JObject
                {
                    ["toposolidId"] = record.Value<long>("OriginalToposolidId"),
                    ["floorIds"] = new JArray(metrics.Select(metric => metric.Value<long>("FloorId"))),
                    ["mode"] = record.Value<string>("Mode") ?? "footprint_only",
                    ["schemeName"] = $"{schemeName}-還原重跑"
                };
                var offsetDistance = record.Value<double?>("OffsetDistanceMeters");
                if (offsetDistance.HasValue)
                {
                    gradeParameters["offsetDistance"] = offsetDistance.Value;
                }

                var slopeRatio = record.Value<string>("SlopeRatio");
                if (!string.IsNullOrWhiteSpace(slopeRatio))
                {
                    gradeParameters["slopeRatio"] = slopeRatio;
                }

                var maxExtension = record.Value<double?>("MaxExtensionMeters");
                if (maxExtension.HasValue)
                {
                    gradeParameters["maxExtension"] = maxExtension.Value;
                }

                var ledger = record["Ledger"] as JObject;
                if (ledger != null)
                {
                    gradeParameters["looseFactor"] = ledger.Value<double>("LooseFactor");
                    gradeParameters["compactionFactor"] = ledger.Value<double>("CompactionFactor");
                }

                gradeResult = GradeToposolidToFloors(gradeParameters);
            }

            return new
            {
                SchemeName = schemeName,
                AlreadyAtScheme = alreadyThere,
                AdjustedFloors = adjusted,
                RerunResult = gradeResult,
                Message = alreadyThere
                    ? $"樓板已在「{schemeName}」的高程狀態，未做調整。"
                    : $"已把 {adjusted.Count} 片樓板的自標高偏移寫回「{schemeName}」記錄值"
                      + (rerunGrading ? "，並已重跑整地。" : "。")
            };
        }
    }
}
#endif
