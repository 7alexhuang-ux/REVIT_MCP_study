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
            var hideIds = CollectOtherTerrainIds(doc, design.Id)
                .Concat(CollectOtherSchemeFloorIds(doc, record))
                .Distinct()
                .ToList();
            var visibleFloorIds = ReadRecordFloorIds(record)
                .Select(id => new ElementId(checked((int)id)))
                .ToList();

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
                ApplySchemeVisibility(doc, view, design.Id, hideIds, visibleFloorIds);
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

        /// <summary>全部方案用過的控制樓板 ID（用於原始地形欄或基準視圖隱藏方案樓板變體）。</summary>
        private static IReadOnlyList<ElementId> CollectAllSchemeFloorIds(Document doc)
        {
            var ids = new List<ElementId>();
            foreach (var json in RevitToposolidGradingAdapter.ReadSchemeRecords(doc))
            {
                foreach (var floorId in ReadRecordFloorIds(JObject.Parse(json)))
                {
                    AddDistinctElementId(ids, new ElementId(checked((int)floorId)));
                }
            }

            return ids;
        }

        /// <summary>非本方案的控制樓板 ID；各方案使用複製樓板時，避免其他方案樓板疊進本方案視圖。</summary>
        private static IReadOnlyList<ElementId> CollectOtherSchemeFloorIds(Document doc, JObject currentRecord)
        {
            var ownFloorIds = new HashSet<long>(ReadRecordFloorIds(currentRecord));
            var ids = new List<ElementId>();
            foreach (var json in RevitToposolidGradingAdapter.ReadSchemeRecords(doc))
            {
                var record = JObject.Parse(json);
                foreach (var floorId in ReadRecordFloorIds(record))
                {
                    if (!ownFloorIds.Contains(floorId))
                    {
                        AddDistinctElementId(ids, new ElementId(checked((int)floorId)));
                    }
                }
            }

            return ids;
        }

        private static IReadOnlyList<long> ReadRecordFloorIds(JObject record)
        {
            return (record?["FloorIds"] as JArray)?.Values<long>().ToList()
                ?? new List<long>();
        }

        private static void AddDistinctElementId(ICollection<ElementId> ids, ElementId candidate)
        {
            if (candidate == null || candidate == ElementId.InvalidElementId)
            {
                return;
            }

            if (!ids.Any(id => id == candidate))
            {
                ids.Add(candidate);
            }
        }

        /// <summary>
        /// 方案 3D 視圖保留專案既有 3D 表現法；Duplicate 後立即清掉 AVF 與剖面框，避免熱區殘留污染。
        /// </summary>
        private static View3D CreateContextSchemeView3D(Document doc, View activeView)
        {
            var styleSource = ResolveStyleSource3D(doc, activeView);
            View3D view = null;
            if (styleSource != null && styleSource.CanViewBeDuplicated(ViewDuplicateOption.Duplicate))
            {
                var duplicatedId = styleSource.Duplicate(ViewDuplicateOption.Duplicate);
                view = doc.GetElement(duplicatedId) as View3D;
            }

            if (view == null)
            {
                var viewFamilyType = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                    .FirstOrDefault(candidate => candidate.ViewFamily == ViewFamily.ThreeDimensional)
                    ?? throw new InvalidOperationException("找不到 3D 視圖類型（ViewFamilyType）。");
                view = View3D.CreateIsometric(doc, viewFamilyType.Id);
            }

            if (activeView is View3D source && !source.IsTemplate)
            {
                try
                {
                    view.SetOrientation(source.GetOrientation());
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException)
                {
                    // 來源視圖若不可取方位，保留 Revit 預設等角即可。
                }
            }

            if (view.IsSectionBoxActive)
            {
                view.IsSectionBoxActive = false; // 避免使用者視圖的剖面框裁掉地形。
            }

            ClearAnalysisVisualization(view);
            return view;
        }

        private static View3D ResolveStyleSource3D(Document doc, View activeView)
        {
            bool IsCleanSource(View3D candidate)
            {
                return candidate != null
                    && !candidate.IsTemplate
                    && candidate.ViewType == ViewType.ThreeD
                    && !candidate.Name.StartsWith("整地-", StringComparison.Ordinal)
                    && candidate.CanViewBeDuplicated(ViewDuplicateOption.Duplicate);
            }

            if (IsCleanSource(activeView as View3D))
            {
                return (View3D)activeView;
            }

            return new FilteredElementCollector(doc)
                .OfClass(typeof(View3D))
                .Cast<View3D>()
                .FirstOrDefault(IsCleanSource)
                ?? (activeView as View3D);
        }

        /// <summary>清掉視圖上的 AVF 顯示與 spatial field primitives，避免出圖視圖繼承熱區殘留。</summary>
        private static void ClearAnalysisVisualization(View view)
        {
            try
            {
                view.AnalysisDisplayStyleId = ElementId.InvalidElementId;
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                // 個別視圖不支援分析顯示時略過。
            }

            try
            {
                var manager = SpatialFieldManager.GetSpatialFieldManager(view);
                manager?.Clear();
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                // 既有 AVF 狀態異常時不阻斷視圖建立。
            }
        }

        /// <summary>將 3D 視圖剖面框聚焦到樓板與周邊整地帶，避免出圖顯示整塊基地。</summary>
        private static void ApplyFocusSectionBox(View view, GradingComparisonFocusBounds? focus)
        {
            if (!focus.HasValue || !(view is View3D view3d))
            {
                return;
            }

            try
            {
                var center = new XYZ(
                    MetersToFeet(focus.Value.CenterXMeters),
                    MetersToFeet(focus.Value.CenterYMeters),
                    MetersToFeet(focus.Value.CenterZMeters));
                var width = MetersToFeet(focus.Value.WidthMeters);
                var depth = MetersToFeet(focus.Value.DepthMeters);
                var height = MetersToFeet(focus.Value.HeightMeters);
                var sectionBox = new BoundingBoxXYZ
                {
                    Transform = Transform.Identity,
                    Min = new XYZ(center.X - (width / 2), center.Y - (depth / 2), center.Z - (height / 2)),
                    Max = new XYZ(
                        center.X + (width / 2),
                        center.Y + (depth / 2),
                        center.Z + (height / 2) + MetersToFeet(60.0))
                };
                view3d.IsSectionBoxActive = true;
                view3d.SetSectionBox(sectionBox);
                // 2026-07-10 十二輪：SectionBox 只限制可見的 3D 幾何，不會裁掉超出範圍的
                // 標註文字——(b)(c) 並排小圖的標籤伸過去就會視覺穿到隔壁欄。加一道真正的
                // 2D CropBox（硬裁切，超出的內容直接不畫），標籤才能安全用滿兩側不互撞。
                SyncCropBoxToSectionBox(view3d, sectionBox);
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                // 鎖定或特殊 3D 視圖若不接受剖面框，不阻斷出圖。
            }
        }

        /// <summary>
        /// 2026-07-10 十二輪：把 View3D 的 2D CropBox 同步到剛設定的 SectionBox 世界座標範圍，
        /// 做出真正的硬裁切（超出範圍的模型幾何與標註文字都不會畫出來）。
        /// 查證來源：The Building Coder（Jeremy Tammik）"Set View Crop to Section Box"——
        /// CropBox 有自己的 Transform（貼合視角朝向，非世界軸向），需先把 SectionBox 角點轉世界座標，
        /// 再轉進 CropBox 自己的局部座標系取 min/max，兩個 Transform 不可混用。
        /// </summary>
        private static void SyncCropBoxToSectionBox(View3D view3d, BoundingBoxXYZ sectionBox)
        {
            if (!view3d.CropBoxActive)
            {
                view3d.CropBoxActive = true;
            }

            view3d.CropBoxVisible = false;
            var cropBox = view3d.CropBox;
            var toLocal = cropBox.Transform.Inverse;
            var minLocal = new XYZ(double.MaxValue, double.MaxValue, double.MaxValue);
            var maxLocal = new XYZ(double.MinValue, double.MinValue, double.MinValue);
            for (var corner = 0; corner < 8; corner++)
            {
                var worldPoint = sectionBox.Transform.OfPoint(new XYZ(
                    (corner & 1) == 0 ? sectionBox.Min.X : sectionBox.Max.X,
                    (corner & 2) == 0 ? sectionBox.Min.Y : sectionBox.Max.Y,
                    (corner & 4) == 0 ? sectionBox.Min.Z : sectionBox.Max.Z));
                var localPoint = toLocal.OfPoint(worldPoint);
                minLocal = new XYZ(
                    Math.Min(minLocal.X, localPoint.X), Math.Min(minLocal.Y, localPoint.Y), Math.Min(minLocal.Z, localPoint.Z));
                maxLocal = new XYZ(
                    Math.Max(maxLocal.X, localPoint.X), Math.Max(maxLocal.Y, localPoint.Y), Math.Max(maxLocal.Z, localPoint.Z));
            }

            cropBox.Min = minLocal;
            cropBox.Max = maxLocal;
            view3d.CropBox = cropBox;
        }

        private static void ApplyFocusSectionBoxToViewId(
            Document doc, long viewIdValue, GradingComparisonFocusBounds? focus)
        {
            if (!focus.HasValue || !(doc.GetElement(new ElementId(checked((int)viewIdValue))) is View view))
            {
                return;
            }

            using (var transaction = new Transaction(doc, "聚焦整地出圖視圖"))
            {
                GradingFailuresPreprocessor.Attach(transaction, new GradingFailuresPreprocessor());
                transaction.Start();
                ApplyFocusSectionBox(view, focus);
                transaction.Commit();
            }
        }

        /// <summary>
        /// 方案視圖可見性：先確保目標設計地形可見（複製來源可能是別的方案視圖、已把它藏了——
        /// 2026-07-08 實機教訓：方案2 地形在自己的方案視圖被繼承隱藏，AVF 引用直接失效），
        /// 再套階段篩選藏拆除物（原地形與其子分割）＋明確隱藏其他地形。
        /// 階段篩選建立失敗時仍有明確隱藏兜底（hideIds 已含原地形）。
        /// </summary>
        private static void ApplySchemeVisibility(
            Document doc,
            View view,
            ElementId targetDesignId,
            IReadOnlyList<ElementId> hideIds,
            IReadOnlyList<ElementId> visibleFloorIds = null)
        {
            if (doc.GetElement(targetDesignId) is Element target && target.IsHidden(view))
            {
                view.UnhideElements(new List<ElementId> { targetDesignId });
            }

            if (visibleFloorIds != null && visibleFloorIds.Count > 0)
            {
                var toUnhide = visibleFloorIds
                    .Select(id => doc.GetElement(id))
                    .Where(element => element != null && element.IsHidden(view))
                    .Select(element => element.Id)
                    .ToList();
                if (toUnhide.Count > 0)
                {
                    view.UnhideElements(toUnhide);
                }
            }

            // 範圍線只顯示本方案的（各方案線型式分視圖控制，2026-07-08 使用者裁決：不得交疊）。
            ApplyExtentLineCategoryVisibility(doc, view, targetDesignId);

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
            // 2026-07-08 實機差分定案：AddSpatialFieldPrimitive(Reference) 需要視圖「至少被開啟過一次」
            // ——同一鎖定視圖未開啟即拋例外、切為作用中視圖後成功；鎖定與否無關（先前兩輪「解鎖」
            // 修法均為混淆變因誤判，已移除）。AVF 前把目標視圖切成作用中（必須在交易外）。
            EnsureViewActivated(view);
            using (var transaction = new Transaction(doc, "建立挖填熱區圖"))
            {
                GradingFailuresPreprocessor.Attach(transaction, new GradingFailuresPreprocessor());
                if (transaction.Start() != TransactionStatus.Started)
                {
                    throw new InvalidOperationException("無法啟動建立挖填熱區圖交易。");
                }

                var manager = SpatialFieldManager.GetSpatialFieldManager(view)
                    ?? SpatialFieldManager.CreateSpatialFieldManager(view, 1);
                ClearSpatialFieldPrimitives(manager);
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
        /// 2026-07-08 實機差分定案：AddSpatialFieldPrimitive(Reference) 需要視圖「至少被開啟過一次」，
        /// 否則拋 ApplicationException；鎖定與否無關（同一鎖定視圖開啟後成功）。
        /// AVF 前把目標視圖切成作用中視圖（同步生效；必須在交易外呼叫）。
        /// </summary>
        private void EnsureViewActivated(View view)
        {
            var uiDocument = _uiApp.ActiveUIDocument;
            if (uiDocument.ActiveView?.Id == view.Id)
            {
                return;
            }

            try
            {
                uiDocument.ActiveView = view;
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                // 切不過去（如視圖不可開啟）就維持原視圖；後續 AVF 失敗會拋出明確例外。
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
                var legendSettings = new AnalysisDisplayLegendSettings
                {
                    ShowLegend = false,
                    ShowDataDescription = false,
                    ShowDataName = false,
                    ShowUnits = false
                };
                style = AnalysisDisplayStyle.CreateAnalysisDisplayStyle(
                    doc, CutFillDisplayStyleName, surfaceSettings, colorSettings, legendSettings);
            }
            else
            {
                var legendSettings = new AnalysisDisplayLegendSettings
                {
                    ShowLegend = false,
                    ShowDataDescription = false,
                    ShowDataName = false,
                    ShowUnits = false
                };
                style.SetLegendSettings(legendSettings);
            }

            view.AnalysisDisplayStyleId = style.Id;
        }

        /// <summary>
        /// 標高標註：在方案鎖定 3D 視圖為控制樓板頂面（中心＋角點內縮）放 Spot Elevation，
        /// daylight 點改「原GL／整地後」合併雙行標籤（2026-07-10 D11 使用者裁決）。
        /// 防疊（D2）：過近點合併（3 m）＋依方位放射錯開＋leader 拉出；逐點容錯回報成敗數。
        /// annotateSlopeLabel=true 時在放坡帶外側加坡率文字（D3）。
        /// </summary>
        private object AnnotateGradingScheme(JObject parameters)
        {
            var doc = _uiApp.ActiveUIDocument.Document;
            var (design, record) = RequireSchemeRecord(doc, parameters);
            var annotateFloors = parameters["annotateFloors"]?.Value<bool?>() ?? true;
            var annotateDaylight = parameters["annotateDaylight"]?.Value<bool?>() ?? false;
            var annotateSlopeLabel = parameters["annotateSlopeLabel"]?.Value<bool?>() ?? false;
            // 2026-07-10 五輪：(b)(c) 並排小圖各自雙側外拉會在中間夾縫互撞——呼叫端可強制單側
            // （-1=只放左欄、+1=只放右欄），null=兩側自動分（(a)(d) 全寬圖適用）。
            var labelSide = parameters["labelSide"]?.Value<int?>();
            // 2026-07-11 十七輪：呼叫端預估的最終出圖比例分母（0/缺省＝無法預估）。
            // 槽位高度取 max(框範圍比例槽高, 紙面字高×hint×1.25)，讓槽高與實際字高同量綱。
            var labelScaleHint = parameters["labelScaleHint"]?.Value<double?>() ?? 0;
            var view = ResolveSchemeView(doc, parameters, record);

            var floorIds = (record["FloorIds"] as JArray)?.Values<long>().ToList() ?? new List<long>();
            var mergeSpacing = UnitUtils.ConvertToInternalUnits(3.0, UnitTypeId.Meters);
            var placed = 0;
            var failed = 0;
            using (var transaction = new Transaction(doc, "整地方案標高標註"))
            {
                GradingFailuresPreprocessor.Attach(transaction, new GradingFailuresPreprocessor());
                if (transaction.Start() != TransactionStatus.Started)
                {
                    throw new InvalidOperationException("無法啟動整地方案標高標註交易。");
                }

                // 重跑冪等：先清掉本視圖先前的整地標籤與 Spot Elevation，避免堆疊。
                var staleAnnotations = new FilteredElementCollector(doc, view.Id)
                    .OfClass(typeof(TextNote)).Cast<TextNote>()
                    .Where(note => (doc.GetElement(note.GetTypeId()) as TextNoteType)?.Name is string typeName
                        && (typeName.StartsWith("整地標高標籤", StringComparison.Ordinal)
                            || typeName.StartsWith("整地坡率標籤", StringComparison.Ordinal)))
                    .Select(note => note.Id)
                    .Concat(new FilteredElementCollector(doc, view.Id)
                        .OfClass(typeof(SpotDimension))
                        .Select(spot => spot.Id))
                    .ToList();
                if (staleAnnotations.Count > 0)
                {
                    doc.Delete(staleAnnotations);
                }

                // 樓板群水平中心：放射錯開的基準。
                // 2026-07-10 二輪視覺檢查：樓板只標「頂面中心」一點——角點高程與 daylight 標籤
                // 站位重複（平板角點值相同），是相碰的主因之一。
                var floorCandidates = new List<(XYZ Point, Reference Reference)>();
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
                            var uvBox = face.GetBoundingBox();
                            var midUV = new UV((uvBox.Min.U + uvBox.Max.U) / 2, (uvBox.Min.V + uvBox.Max.V) / 2);
                            if (face.IsInside(midUV))
                            {
                                floorCandidates.Add((face.Evaluate(midUV), face.Reference));
                            }
                        }
                    }
                }

                // 2026-07-10 三輪使用者裁決：標籤一律「拉線到圖外」——依視圖螢幕座標把
                // 標籤排到地形影像左右兩側的欄位，引線拉回測點，不再壓圖。
                var items = new List<(XYZ Target, string Text, Reference Reference)>();
                if (floorCandidates.Count > 0)
                {
                    var candidatePoints = floorCandidates
                        .Select(c => new Point2D(c.Point.X, c.Point.Y)).ToList();
                    foreach (var index in AnnotationLayoutMath.SelectSpacedIndices(candidatePoints, mergeSpacing))
                    {
                        items.Add((floorCandidates[index].Point, null, floorCandidates[index].Reference));
                    }
                }

                if (annotateDaylight)
                {
                    // 2026-07-11 十七輪（使用者回報五：點位都在轉折點、原/後數值差不多）：
                    // daylight 標籤改「資訊量優先」——按 |原GL−整地後| 由大到小貪婪收點
                    // （間距 3m 內讓位給差值更大的點），高差最大處（最大挖深/填高的故事點）
                    // 必上榜；再按版面限額（強制單側 4 點、雙側自動 6 點）截斷，配合
                    // labelScaleHint 的實高槽位，點數與槽高都不再撐爆側欄。
                    var daylightCandidates = new List<(XYZ Projected, double? OriginalZ, double Diff)>();
                    foreach (var sample in CollectDaylightElevationSamples(doc, record, design, ref failed))
                    {
                        var diff = sample.OriginalZ.HasValue
                            ? Math.Abs(sample.OriginalZ.Value - sample.Projected.Z)
                            : 0;
                        daylightCandidates.Add((sample.Projected, sample.OriginalZ, diff));
                    }

                    var daylightCap = labelSide.HasValue ? 4 : 6;
                    var kept = new List<(XYZ Projected, double? OriginalZ, double Diff)>();
                    foreach (var candidate in daylightCandidates.OrderByDescending(c => c.Diff))
                    {
                        if (kept.Count >= daylightCap)
                        {
                            break;
                        }

                        var tooClose = kept.Any(existing =>
                            new XYZ(existing.Projected.X - candidate.Projected.X,
                                    existing.Projected.Y - candidate.Projected.Y, 0).GetLength() < mergeSpacing);
                        if (!tooClose)
                        {
                            kept.Add(candidate);
                        }
                    }

                    foreach (var sample in kept)
                    {
                        var designMeters = UnitUtils.ConvertFromInternalUnits(
                            sample.Projected.Z, UnitTypeId.Meters);
                        var text = sample.OriginalZ.HasValue
                            ? $"原GL {UnitUtils.ConvertFromInternalUnits(sample.OriginalZ.Value, UnitTypeId.Meters):F1}m\n整地後 {designMeters:F1}m"
                            : $"整地後 {designMeters:F1}m";
                        items.Add((sample.Projected, text, null));
                    }
                }

                if (annotateSlopeLabel && items.Count > 0)
                {
                    // 坡率標籤掛在最東南側的標註點上（右欄），與其他標籤共用槽位不互疊。
                    var anchor = items
                        .OrderByDescending(item => item.Target.X - item.Target.Y)
                        .First().Target;
                    items.Add((anchor, BuildSlopeModeText(record), null));
                }

                if (items.Count > 0)
                {
                    XYZ right;
                    XYZ up;
                    try
                    {
                        right = view.RightDirection;
                        up = view.UpDirection;
                    }
                    catch (Autodesk.Revit.Exceptions.ApplicationException)
                    {
                        right = XYZ.BasisX;
                        up = XYZ.BasisZ;
                    }

                    // 2026-07-10 六輪使用者裁決：標籤必須貼近圖、不能撐大圖的顯示範圍。
                    // 根因：先前用 design 的完整包圍盒（含放坡延伸帶，可能遠大於比較用的共同
                    // 裁切框）當標籤外推基準，導致標籤伸出裁切框外、viewport 外框被撐大、
                    // 地形被迫縮得極小、文字（annotation 不隨視圖比例縮放）相對顯得巨大。
                    // 修正：view 若已套用 3D SectionBox（比較流程一律會套，且全欄共用同一
                    // comparisonFocus），一律用該裁切框的世界座標範圍當基準——確保標籤只在
                    // 「已經看得到的範圍」內，不會撐大 viewport 外框，也讓各欄裁切/縮放一致。
                    var box = TryGetSectionBoxWorldBounds(view) ?? design.get_BoundingBox(null);
                    var center = box == null ? XYZ.Zero : (box.Min + box.Max) / 2;
                    double ScreenR(XYZ p) => (p - center).DotProduct(right);
                    double ScreenU(XYZ p) => (p - center).DotProduct(up);
                    var minR = double.MaxValue;
                    var maxR = double.MinValue;
                    var minU = double.MaxValue;
                    var maxU = double.MinValue;
                    if (box != null)
                    {
                        for (var corner = 0; corner < 8; corner++)
                        {
                            var probe = new XYZ(
                                (corner & 1) == 0 ? box.Min.X : box.Max.X,
                                (corner & 2) == 0 ? box.Min.Y : box.Max.Y,
                                (corner & 4) == 0 ? box.Min.Z : box.Max.Z);
                            minR = Math.Min(minR, ScreenR(probe));
                            maxR = Math.Max(maxR, ScreenR(probe));
                            minU = Math.Min(minU, ScreenU(probe));
                            maxU = Math.Max(maxU, ScreenU(probe));
                        }
                    }
                    else
                    {
                        minR = -30; maxR = 30; minU = -30; maxU = 30;
                    }

                    var screenPoints = items
                        .Select(item => new Point2D(ScreenR(item.Target), ScreenU(item.Target)))
                        .ToList();
                    // labelSide 強制：splitR 設 +∞／-∞ 讓 AssignSideColumnSlots 的側別判斷全倒向一邊。
                    var splitR = labelSide == -1 ? double.PositiveInfinity
                        : labelSide == 1 ? double.NegativeInfinity
                        : 0.0;
                    // 2026-07-10 六輪：box 現在多半是緊裁切框，槽位可用到 0.85；
                    // 外推 margin 縮到 1.5m（原 4m）——標籤要「貼近圖」，只是輔助角色。
                    // 六輪追修：強制單側時項目全擠一邊，均分格位會小於文字實際高度而疊字——
                    // 改按各項實際行數堆疊，不重疊優先於貼合範圍。
                    // 七輪修正：itemHeights 誤用「紙面文字高度」換算成模型距離（2.4mm），
                    // 但此時 view.Scale 尚未定案（列共用比例要到排圖階段才算出）——換算出的模型
                    // 距離只有幾公分，遠小於 box 的公尺級範圍，堆疊後全部項目擠在同一角落，
                    // 視覺上等於沒堆疊（標籤仍互疊）。改用「box 範圍的比例」定義高度，
                    // 與最終列印比例無關：不論 box 多大，堆疊後占用的視覺比例一致。
                    // 八輪追修：v8 實測仍見相鄰標籤黏字（如「整」+「原GL0.6m」疊成「整原GL0.6m」）——
                    // 強制單側時項目數是雙側自動分配的兩倍，0.09/0.05 比例仍偏緊，加大＋放寬間距。
                    // 九輪二次追修：加框後 v9 實測——(a) 3 項/側間隙清楚，(b)(c) 6 項/側框線貼在一起
                    // （非重疊，是紙面間隙小於框線可視寬度）。項目一多，box 範圍分母不變、
                    // 佔比一路頂到 100%+，spacing 這個小分數換算出的紙面間隙被壓縮到接近 0——
                    // 拉高 spacing 佔比（0.03→0.06）確保多項目擠壓時仍有可視留白。
                    var boxURange = Math.Max(1e-6, maxU - minU);
                    // 十七輪（使用者回報一）：比例槽高的量綱與實際字高無關——標註文字模型高度
                    // ＝紙面字高×最終比例，(b)(c) 半寬格最終比例大時實際字高可達比例槽高兩倍，
                    // 框線互切。呼叫端傳 labelScaleHint（預估比例分母）時，槽高改取
                    // max(比例槽高, (行數×1.9×2.4mm＋框留白3.5mm)×hint×1.25)；1.9 為微軟正黑
                    // 實測行距係數（見比較表十七輪自校準說明），1.25 是預估誤差的安全係數。
                    // v17 實測追修：1.25 安全係數下 (b)(c) 框線仍彼此貼齊（預估比例略低於
                    // 實際）——高度係數放大到 1.4、間距紙面 2.5→3.5mm，框與框留出可讀空隙。
                    double HintBlockFeet(int lineCount) => labelScaleHint <= 0
                        ? 0
                        : UnitUtils.ConvertToInternalUnits(
                            ((lineCount * 1.9 * 2.4) + 3.5) * labelScaleHint * 1.4,
                            UnitTypeId.Millimeters);
                    var itemHeights = items
                        .Select(item =>
                        {
                            var lineCount = Math.Max(1, (item.Text ?? string.Empty).Split('\n').Length);
                            return Math.Max(
                                (lineCount >= 2 ? 0.16 : 0.09) * boxURange,
                                HintBlockFeet(lineCount));
                        })
                        .ToList();
                    var slotSpacing = Math.Max(
                        0.06 * boxURange,
                        labelScaleHint <= 0
                            ? 0
                            : UnitUtils.ConvertToInternalUnits(
                                3.5 * labelScaleHint * 1.4, UnitTypeId.Millimeters));
                    var slots = AnnotationLayoutMath.AssignSideColumnSlots(
                        screenPoints, splitR, minU * 0.85, maxU * 0.85, itemHeights, slotSpacing);
                    var margin = UnitUtils.ConvertToInternalUnits(1.5, UnitTypeId.Meters);
                    // 九輪使用者裁決：標籤加外框，避免看起來像連續一整段文字、分不出彼此邊界。
                    var labelTypeId = EnsureTextNoteType(doc, "整地標高標籤", 2.4, withBorder: true);
                    for (var index = 0; index < items.Count; index++)
                    {
                        var (target, text, reference) = items[index];
                        var sideR = slots[index].Side > 0 ? maxR + margin : minR - margin;
                        var label = target
                            + (right * (sideR - screenPoints[index].X))
                            + (up * (slots[index].SlotU - screenPoints[index].Y));
                        var ok = text == null
                            ? TryPlaceSpotElevation(doc, view, reference, target,
                                target + ((label - target) * 0.5), label)
                            : TryPlaceOutboardLabel(doc, view, labelTypeId, label, text,
                                slots[index].Side, target);
                        if (ok) { placed++; } else { failed++; }
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

        /// <summary>
        /// 2026-07-10 六輪：讀取視圖已套用的 3D SectionBox 世界座標範圍（8 角點經 Transform）；
        /// 無 SectionBox 或非 View3D 時回 null，呼叫端退回 design 包圍盒。
        /// </summary>
        private static BoundingBoxXYZ TryGetSectionBoxWorldBounds(View view)
        {
            if (!(view is View3D view3D) || !view3D.IsSectionBoxActive)
            {
                return null;
            }

            BoundingBoxXYZ sectionBox;
            try
            {
                sectionBox = view3D.GetSectionBox();
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                return null;
            }

            var minWorld = new XYZ(double.MaxValue, double.MaxValue, double.MaxValue);
            var maxWorld = new XYZ(double.MinValue, double.MinValue, double.MinValue);
            for (var corner = 0; corner < 8; corner++)
            {
                var local = new XYZ(
                    (corner & 1) == 0 ? sectionBox.Min.X : sectionBox.Max.X,
                    (corner & 2) == 0 ? sectionBox.Min.Y : sectionBox.Max.Y,
                    (corner & 4) == 0 ? sectionBox.Min.Z : sectionBox.Max.Z);
                var world = sectionBox.Transform.OfPoint(local);
                minWorld = new XYZ(
                    Math.Min(minWorld.X, world.X), Math.Min(minWorld.Y, world.Y), Math.Min(minWorld.Z, world.Z));
                maxWorld = new XYZ(
                    Math.Max(maxWorld.X, world.X), Math.Max(maxWorld.Y, world.Y), Math.Max(maxWorld.Z, world.Z));
            }

            return new BoundingBoxXYZ { Min = minWorld, Max = maxWorld };
        }

        /// <summary>
        /// D11：daylight 取樣點蒐集（原GL／整地後 高程對）。取樣沿樓板外輪廓、
        /// 剔除多板共享邊內部點、5m 間距合併。高程為專案內部原點起算。
        /// </summary>
        private IReadOnlyList<(XYZ Projected, double? OriginalZ)> CollectDaylightElevationSamples(
            Document doc, JObject record, Toposolid design, ref int failed)
        {
            var originalId = record.Value<long?>("OriginalToposolidId") ?? 0;
            var originalSolids = doc.GetElement(new ElementId(checked((int)originalId))) is Toposolid original
                ? RevitToposolidGradingAdapter.CollectSolids(original)
                : new List<Solid>();
            var designBox = design.get_BoundingBox(null);
            var rayBottomZ = (designBox?.Min.Z ?? 0) - UnitUtils.ConvertToInternalUnits(30, UnitTypeId.Meters);
            var rayTopZ = (designBox?.Max.Z ?? 0) + UnitUtils.ConvertToInternalUnits(30, UnitTypeId.Meters);

            var designFaces = CollectUpwardFaces(design, 0.3);
            IToposolidGradingAdapter adapter = new RevitToposolidGradingAdapter();
            var floorIds = (record["FloorIds"] as JArray)?.Values<long>().ToList() ?? new List<long>();
            var floors = adapter.ValidateFloors(doc, floorIds);
            var footprints = adapter.ExtractBottomFootprints(floors);

            // 2026-07-10 二輪視覺檢查：多板共享邊的取樣點落在別片樓板「內部」——標出來沒有意義
            // 還互相疊字，一律剔除；合併間距 3m→5m 再減量。
            var containTolerance = UnitUtils.ConvertToInternalUnits(50, UnitTypeId.Millimeters);
            var samples = new List<(XYZ Projected, double? OriginalZ)>();
            for (var footprintIndex = 0; footprintIndex < footprints.Count; footprintIndex++)
            {
                var loop = footprints[footprintIndex].OuterLoop;
                var step = Math.Max(1, loop.Count / 8);
                for (var index = 0; index < loop.Count; index += step)
                {
                    var boundary = loop[index];
                    var insideOtherFloor = false;
                    for (var other = 0; other < footprints.Count; other++)
                    {
                        // 嚴格內部才剔除：共享邊上的點（含端點＝聯集外角）距邊界≈0，保留。
                        if (other != footprintIndex
                            && Polygon2D.Contains(footprints[other].OuterLoop, boundary, containTolerance)
                            && Polygon2D.DistanceToBoundary(footprints[other].OuterLoop, boundary) > containTolerance)
                        {
                            insideOtherFloor = true;
                            break;
                        }
                    }

                    if (insideOtherFloor)
                    {
                        continue;
                    }

                    if (!TryProjectToFace(
                        designFaces, new XYZ(boundary.X, boundary.Y, 0),
                        out _, out var projected))
                    {
                        failed++;
                        continue;
                    }

                    var originalZ = originalSolids.Count > 0
                        ? RevitToposolidGradingAdapter.IntersectTerrainTopZ(
                            originalSolids, new Point2D(projected.X, projected.Y), rayBottomZ, rayTopZ)
                        : null;
                    samples.Add((projected, originalZ));
                }
            }

            if (samples.Count == 0)
            {
                return samples;
            }

            var points2d = samples.Select(s => new Point2D(s.Projected.X, s.Projected.Y)).ToList();
            return AnnotationLayoutMath
                .SelectSpacedIndices(points2d, UnitUtils.ConvertToInternalUnits(5.0, UnitTypeId.Meters))
                .Select(index => samples[index])
                .ToList();
        }

        /// <summary>D3：坡率文字——依模式「放坡 1:n／外延 Xm 漸變／直貼」。</summary>
        private static string BuildSlopeModeText(JObject record)
        {
            var mode = record.Value<string>("Mode") ?? "footprint_only";
            if (mode == "slope_transition")
            {
                return $"放坡 {record.Value<string>("SlopeRatio") ?? "?"}";
            }

            if (mode == "offset_transition")
            {
                return $"外延 {record.Value<double?>("OffsetDistanceMeters") ?? 0.0:0.##}m 漸變";
            }

            return "直貼（無放坡帶）";
        }

        /// <summary>
        /// 圖外標籤：文字放在影像側欄（右欄左對齊、左欄右對齊，文字往外長），
        /// 加直線引線拉回目標點。引線 API 失敗時仍保留標籤本體。
        /// </summary>
        private static bool TryPlaceOutboardLabel(
            Document doc, View view, ElementId textTypeId, XYZ labelPosition,
            string text, int side, XYZ target)
        {
            try
            {
                var note = TextNote.Create(doc, view.Id, labelPosition, text, new TextNoteOptions
                {
                    TypeId = textTypeId,
                    HorizontalAlignment = side > 0
                        ? HorizontalTextAlignment.Left
                        : HorizontalTextAlignment.Right
                });
                try
                {
                    note.AddLeader(side > 0
                        ? TextNoteLeaderTypes.TNLT_STRAIGHT_L
                        : TextNoteLeaderTypes.TNLT_STRAIGHT_R);
                    foreach (var leader in note.GetLeaders())
                    {
                        leader.End = target;
                    }
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException)
                {
                    // 引線設定失敗（個別視圖限制）→ 留標籤無引線，不視為失敗。
                }

                return true;
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                return false;
            }
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

        /// <summary>帶 leader 的 Spot Elevation（D2 防疊）：文字沿放射方向拉出，不再全擠在原點。</summary>
        private static bool TryPlaceSpotElevation(
            Document doc, View view, Reference reference, XYZ point, XYZ bend, XYZ end)
        {
            try
            {
                doc.Create.NewSpotElevation(view, reference, point, bend, end, end, true);
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
