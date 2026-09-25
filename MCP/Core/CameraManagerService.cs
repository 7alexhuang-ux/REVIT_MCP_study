using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ClosedXML.Excel;

namespace RevitMCP.Core
{
    /// <summary>
    /// 相機管理停靠面板的 Revit 讀寫邏輯，刻意不含任何 WPF 型別 —— 面板（UI 層）只呼叫這裡的
    /// 方法，這裡只呼叫 Revit API。拆開的理由：
    ///   1. 這批方法全部假設「已經在 Revit API 執行環境內」（ExternalEvent 的 Execute 內，
    ///      或 DocumentChanged／ViewActivated 這類 Revit 自己觸發、本來就在 API context 的
    ///      事件），呼叫端（UI 層）要自己決定何時經 ExternalEventManager 排程。
    ///   2. 第 2 階段（Key Plan／相機標記）要加新方法時，不需要碰任何 WPF 程式碼。
    /// </summary>
    internal static class CameraManagerService
    {
        internal const double LowHeightWarningCm = 120.0;
        internal const double HighHeightWarningCm = 200.0;

        // ══════════════════════════════════════════════════════════
        // 4.2 讀取相機清單 + 平面／樓層清單
        // ══════════════════════════════════════════════════════════

        internal static CameraManagerSnapshot ReadSnapshot(Document doc)
        {
            var snapshot = new CameraManagerSnapshot();
            if (doc == null)
            {
                snapshot.ErrorMessage = "目前沒有開啟中的文件。";
                return snapshot;
            }
            snapshot.DocumentTitle = doc.Title;

            try
            {
                List<Level> levels = CameraMath.GetStoreyLevelsSorted(doc);
                foreach (Level l in levels)
                {
                    snapshot.Levels.Add(new LevelOption
                    {
                        LevelId = l.Id,
                        Name = l.Name,
                        ProjectElevationFeet = l.ProjectElevation
                    });
                }

                List<View3D> cameraViews = new FilteredElementCollector(doc)
                    .OfClass(typeof(View3D))
                    .Cast<View3D>()
                    .Where(v => !v.IsTemplate && v.IsPerspective)
                    .OrderBy(v => v.Name)
                    .ToList();

                var levelCameraCount = new Dictionary<ElementId, int>();
                HashSet<string> camerasWithSection;
                try { camerasWithSection = CameraKeyPlanService.GetCameraUniqueIdsWithSection(doc); }
                catch (Exception ex)
                {
                    Logger.Error("相機管理：讀取 Key Plan 剖面連結失敗（略過，不影響清單）", ex);
                    camerasWithSection = new HashSet<string>();
                }

                foreach (View3D view in cameraViews)
                {
                    CameraRow row = BuildCameraRow(view, levels);
                    row.HasKeyPlanSection = row.UniqueId != null && camerasWithSection.Contains(row.UniqueId);
                    snapshot.Cameras.Add(row);

                    if (row.LevelId != null)
                    {
                        levelCameraCount[row.LevelId] = levelCameraCount.TryGetValue(row.LevelId, out int c) ? c + 1 : 1;
                    }
                }

                // 4.8：底圖只提供「已啟用 CropBox」的平面（沒有的話面板改畫中性格線，見 HasCroppedPlan）。
                List<ViewPlan> croppedPlanViews = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewPlan))
                    .Cast<ViewPlan>()
                    .Where(v => !v.IsTemplate &&
                        (v.ViewType == ViewType.FloorPlan || v.ViewType == ViewType.CeilingPlan || v.ViewType == ViewType.AreaPlan) &&
                        v.CropBoxActive)
                    .OrderBy(v => v.Name)
                    .ToList();

                snapshot.HasCroppedPlan = croppedPlanViews.Count > 0;
                var storeyIds = new HashSet<long>(levels.Select(l => (long)l.Id.GetIdValue()));

                foreach (ViewPlan pv in croppedPlanViews)
                {
                    var opt = new PlanViewOption
                    {
                        ViewId = pv.Id,
                        GenLevelId = pv.GenLevel != null ? pv.GenLevel.Id : null,
                        PlanName = pv.Name,
                        LevelName = pv.GenLevel?.Name,
                        DisplayName = pv.Name + "（" + pv.ViewType + "）",
                        IsFloorPlan = pv.ViewType == ViewType.FloorPlan,
                        GenLevelElevationFeet = pv.GenLevel != null ? (double?)pv.GenLevel.ProjectElevation : null,
                        GenLevelIsStorey = pv.GenLevel != null && storeyIds.Contains(pv.GenLevel.Id.GetIdValue())
                    };
                    // 2026-09-25（§4.8 底圖挑選）：攤平裁剪框供「包含相機／最大面積」判斷；失敗只記 log。
                    try
                    {
                        ElementId primaryId = pv.GetPrimaryViewId();
                        opt.IsDependent = primaryId != ElementId.InvalidElementId;
                        opt.PrimaryViewId = opt.IsDependent ? primaryId : null;
                        BoundingBoxXYZ cb = pv.CropBox;
                        if (cb != null)
                        {
                            Transform tf = cb.Transform ?? Transform.Identity;
                            opt.CropOriginX = tf.Origin.X; opt.CropOriginY = tf.Origin.Y;
                            opt.CropBasisXX = tf.BasisX.X; opt.CropBasisXY = tf.BasisX.Y;
                            opt.CropBasisYX = tf.BasisY.X; opt.CropBasisYY = tf.BasisY.Y;
                            opt.CropMinX = cb.Min.X; opt.CropMinY = cb.Min.Y;
                            opt.CropMaxX = cb.Max.X; opt.CropMaxY = cb.Max.Y;
                            opt.HasCrop = true;
                        }
                    }
                    catch (Exception exCrop)
                    {
                        Logger.Error($"相機管理：讀取平面「{pv.Name}」裁剪框失敗（不參與包含相機判斷）", exCrop);
                    }
                    snapshot.PlanViews.Add(opt);
                }

                snapshot.DefaultPlanViewId = ElementId.InvalidElementId;
                if (croppedPlanViews.Count > 0)
                {
                    ElementId busiestLevelId = levelCameraCount.Count > 0
                        ? levelCameraCount.OrderByDescending(kv => kv.Value).First().Key
                        : null;

                    // 2026-09-25（§4.8 第二版）：相機最多的樓層，用共用的 BaseMapPlanChooser.Choose（步驟 0–4）。
                    PlanViewOption chosenOpt = null;
                    string why = "";
                    if (busiestLevelId != null)
                    {
                        List<CameraRow> onLevel = snapshot.Cameras
                            .Where(r => r.LevelId != null && r.LevelId.GetIdValue() == busiestLevelId.GetIdValue()).ToList();
                        List<double[]> eyes = onLevel.Select(r => new[] { r.EyeXFeet, r.EyeYFeet, r.EyeZFeet }).ToList();
                        chosenOpt = BaseMapPlanChooser.Choose(snapshot.PlanViews, busiestLevelId, eyes, snapshot.Levels, out why, out _,
                            SourcePlanHint.MostCommon(onLevel));
                    }
                    if (chosenOpt == null)
                    {
                        chosenOpt = snapshot.PlanViews.OrderBy(p => p.IsDependent ? 1 : 0).ThenByDescending(p => p.CropAreaSqFeet).First();
                        why = (busiestLevelId == null ? "沒有相機樓層" : "相機最多的樓層沒有已裁剪平面") + " → 全部平面中最大面積";
                    }
                    snapshot.DefaultPlanViewId = chosenOpt.ViewId;
                    Logger.Info($"相機管理：預設底圖 {chosenOpt.CropLogText}（{why}）");
                }
            }
            catch (Exception ex)
            {
                snapshot.ErrorMessage = "讀取相機清單失敗：" + ex.Message;
            }

            return snapshot;
        }

        /// <summary>單一相機視圖 → CameraRow。給 ReadSnapshot 和 DocumentChanged 局部刷新共用。</summary>
        internal static CameraRow BuildCameraRow(View3D view, IList<Level> levelsSorted)
        {
            CameraMath.CameraGeometry geom = CameraMath.ComputeGeometry(view, levelsSorted);
            double? heightCm = geom.AboveLevelBelowMm.HasValue
                ? (double?)Math.Round(geom.AboveLevelBelowMm.Value / 10.0, 1)
                : null;

            string warning = "";
            if (heightCm.HasValue)
            {
                if (heightCm.Value < LowHeightWarningCm) warning = "偏低";
                else if (heightCm.Value > HighHeightWarningCm) warning = "偏高";
            }

            Parameter farClipParam = CameraMath.FindFarClipOffsetParameter(view);
            double? farClipCm = farClipParam != null
                ? (double?)Math.Round(CameraMath.FeetToMm(farClipParam.AsDouble()) / 10.0, 1)
                : null;

            CameraMath.PerspectiveFov fov = null;
            try { fov = CameraMath.ComputePerspectiveFov(view); }
            catch (Exception ex) { Logger.Error("相機管理面板：視野角計算失敗（FOV 欄留空）", ex); }

            // 2026-09-25 室內／室外判定（同一鍵 4 視圖的 FindEyeRoom）；失敗＝null（清單顯示 ?），不影響其他欄位。
            bool? isOutdoor = CameraKeyPlanService.ClassifyIndoorOutdoor(view, geom, out double outdoorFanR, out string classLog);
            Logger.Info($"相機管理：「{view.Name}」室內／室外＝{(isOutdoor.HasValue ? (isOutdoor.Value ? "室外" : "室內") : "?")}（{classLog}）" +
                        (isOutdoor == true ? $" 扇形半徑={CameraMath.FeetToMm(outdoorFanR):F0}mm" : ""));

            // 2026-09-25（§4.8 步驟 0）：相機放置時的平面（ES 記錄；沒有＝null）。
            ViewPlan src = CameraSourcePlanStore.TryGetSourcePlan(view);
            ElementId srcPrimary = null;
            bool srcCropped = false;
            if (src != null)
            {
                try
                {
                    srcCropped = src.CropBoxActive;
                    ElementId pid = src.GetPrimaryViewId();
                    if (pid != ElementId.InvalidElementId) srcPrimary = pid;
                }
                catch (Exception exSrc) { Logger.Error("相機管理：讀取放置平面屬性失敗", exSrc); }
            }

            return new CameraRow
            {
                SourcePlanId = src?.Id,
                SourcePlanName = src?.Name,
                SourcePlanCropped = srcCropped,
                SourcePlanPrimaryId = srcPrimary,
                IsOutdoor = isOutdoor,
                OutdoorFanRadiusFeet = outdoorFanR,
                ViewId = view.Id,
                UniqueId = view.UniqueId,
                Name = view.Name,
                LevelName = geom.LevelBelow != null ? geom.LevelBelow.Name : "(找不到樓層)",
                LevelId = geom.LevelBelow != null ? geom.LevelBelow.Id : null,
                HeightAboveLevelCm = heightCm,
                HorizontalAngleDeg = CameraMath.ComputeHorizontalAngleDeg(geom.Forward),
                PitchDeg = geom.PitchDeg,
                EyeElevationDisplay = CameraMath.GetEyeElevationDisplayString(view),
                FovEstimateDeg = fov?.HFovDeg.HasValue == true ? (double?)Math.Round(fov.HFovDeg.Value, 1) : null,
                FovLeftDeg = fov?.LeftDeg,
                FovRightDeg = fov?.RightDeg,
                FarClipCm = farClipCm,
                FarClipEditable = farClipParam != null && !farClipParam.IsReadOnly,
                EyeXFeet = geom.Eye.X,
                EyeYFeet = geom.Eye.Y,
                EyeZFeet = geom.Eye.Z,
                ForwardXFeet = geom.Forward.X,
                ForwardYFeet = geom.Forward.Y,
                ForwardZFeet = geom.Forward.Z,
                InMapRange = false, // 底圖畫完才知道，DrawMap 端回填
                HeightWarning = warning
            };
        }

        // ══════════════════════════════════════════════════════════
        // 4.8 底圖匯出
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// 匯出平面視圖成底圖，並記下這張圖對應的模型範圍（CropBox，view 自身座標系＋
        /// 轉回模型座標的 Transform）。只會被叫在 CropBoxActive==true 的平面（ReadSnapshot
        /// 已過濾），修掉「底圖用了沒裁剪的平面 → CropBox 與實際匯出範圍不一致 → 相機全部
        /// 判定在範圍外、底圖上一個標記都不畫」的根因（見 docs/camera-manager-spec 4.9 前的
        /// 已知問題記錄／本次交付報告）。
        /// </summary>
        /// <summary>
        /// 2026-09-24 對位改版（取代「推測匯出範圍」）：匯出一張「確定性」的暫存複本視圖——
        /// 樣板拿掉、CropBoxActive=true／CropBoxVisible=false、裁剪形狀還原成矩形、註記裁剪
        /// 開啟且四邊外擴設到 Revit 允許的最小值、會伸出裁剪框的註記類別（格線、樓層、剖面／
        /// 立面／詳圖索引標記、尺寸、標籤、文字、參考平面、範圍框…）用視圖層級類別隱藏。
        /// 這樣匯出的點陣圖範圍只可能是「模型裁剪框」或「模型裁剪框＋最小註記外擴」，兩者用
        /// 圖片長寬比挑較吻合者，並把所有數字寫進 log。
        ///
        /// 交易結構：TransactionGroup 內 Transaction 1 建立並設定暫存視圖 → Commit → 在沒有任何
        /// Transaction 開啟（doc 不可修改）的狀態下 ExportImage → RollBack 整個 group，暫存視圖
        /// 消失、不留復原紀錄。⚠️ 「TransactionGroup 開著、但無 Transaction 時可否 ExportImage」
        /// API 文件沒有明寫（未查證）：若 ExportImage 在 group 內丟例外，改走備援——group 仍
        /// RollBack，接著「建立暫存視圖 Commit → 匯出 → finally 用新交易刪除暫存視圖」。
        /// 暫存視圖建立本身失敗（例如文件唯讀）時，退回 <see cref="ExportBaseMapLegacy"/>。
        /// </summary>
        internal static bool IsExportingBaseMap { get; private set; }

        internal static BaseMapSnapshot ExportBaseMap(Document doc, ElementId planViewId)
        {
            View source = doc?.GetElement(planViewId) as View;
            if (source == null || !(source is ViewPlan))
                return ExportBaseMapLegacy(doc, planViewId);

            IsExportingBaseMap = true;
            try
            {
                BaseMapSnapshot viaGroup = null;
                bool groupExportFailed = false;
                try
                {
                    using (TransactionGroup tg = new TransactionGroup(doc, "相機管理：底圖暫存視圖"))
                    {
                        tg.Start();
                        try
                        {
                            ViewPlan temp = CreateBaseMapTempView(doc, (ViewPlan)source, out string setupLog);
                            try
                            {
                                viaGroup = ExportDeterministic(doc, temp, (ViewPlan)source, setupLog + " mode=TransactionGroup");
                            }
                            catch (Exception exExport)
                            {
                                groupExportFailed = true;
                                Logger.Error("相機管理面板：TransactionGroup 內 ExportImage 失敗，改走備援（commit→export→delete）", exExport);
                            }
                        }
                        finally
                        {
                            if (tg.HasStarted() && !tg.HasEnded()) tg.RollBack();
                        }
                    }
                }
                catch (Exception exSetup)
                {
                    Logger.Error("相機管理面板：建立底圖暫存視圖失敗，退回舊版匯出（推測範圍）", exSetup);
                    CameraManagerDiagnostics.RecordCaught("ExportBaseMap.TempView", exSetup);
                    return ExportBaseMapLegacy(doc, planViewId);
                }

                if (!groupExportFailed && viaGroup != null) return viaGroup;

                // 備援：commit → export → 一定刪除
                ElementId tempId = null;
                try
                {
                    using (Transaction t = new Transaction(doc, "相機管理：建立底圖暫存視圖"))
                    {
                        t.Start();
                        ViewPlan temp = CreateBaseMapTempView(doc, (ViewPlan)source, out _, inTransaction: true);
                        tempId = temp.Id;
                        t.Commit();
                    }
                    ViewPlan committed = doc.GetElement(tempId) as ViewPlan;
                    return ExportDeterministic(doc, committed, (ViewPlan)source, "mode=CommitThenDelete");
                }
                catch (Exception exFallback)
                {
                    Logger.Error("相機管理面板：備援底圖匯出失敗，退回舊版匯出", exFallback);
                    return ExportBaseMapLegacy(doc, planViewId);
                }
                finally
                {
                    if (tempId != null && tempId != ElementId.InvalidElementId)
                    {
                        try
                        {
                            using (Transaction td = new Transaction(doc, "相機管理：刪除底圖暫存視圖"))
                            {
                                td.Start();
                                doc.Delete(tempId);
                                td.Commit();
                            }
                        }
                        catch (Exception exDel)
                        {
                            Logger.Error($"相機管理面板：刪除底圖暫存視圖 {tempId.GetIdValue()} 失敗（請手動刪除名稱以「_CameraMgr_BaseMap」開頭的視圖）", exDel);
                        }
                    }
                }
            }
            finally
            {
                IsExportingBaseMap = false;
            }
        }

        /// <summary>要隱藏的註記類別（會伸出裁剪框）；另外所有名稱以 Tags 結尾的註記類別也一併隱藏。</summary>
        private static readonly BuiltInCategory[] BaseMapHiddenAnnotationCats =
        {
            BuiltInCategory.OST_Grids, BuiltInCategory.OST_Levels,
            BuiltInCategory.OST_Sections, BuiltInCategory.OST_Elev, BuiltInCategory.OST_Callouts,
            BuiltInCategory.OST_Viewers,
            BuiltInCategory.OST_Dimensions, BuiltInCategory.OST_SpotElevations, BuiltInCategory.OST_SpotCoordinates,
            BuiltInCategory.OST_TextNotes, BuiltInCategory.OST_CLines, BuiltInCategory.OST_VolumeOfInterest,
            BuiltInCategory.OST_Matchline
        };

        /// <summary>在已開啟的 Transaction（inTransaction=true）或自己開一個 Transaction 內建立並設定暫存視圖。</summary>
        private static ViewPlan CreateBaseMapTempView(Document doc, ViewPlan source, out string setupLog, bool inTransaction = false)
        {
            Transaction own = null;
            if (!inTransaction)
            {
                own = new Transaction(doc, "相機管理：設定底圖暫存視圖");
                own.Start();
            }
            try
            {
                ElementId dupId = source.Duplicate(ViewDuplicateOption.Duplicate);
                ViewPlan temp = doc.GetElement(dupId) as ViewPlan;
                if (temp == null) throw new Exception("複製平面視圖失敗。");
                try { temp.Name = "_CameraMgr_BaseMap_" + Guid.NewGuid().ToString("N").Substring(0, 8); } catch { }
                try { if (temp.ViewTemplateId != ElementId.InvalidElementId) temp.ViewTemplateId = ElementId.InvalidElementId; } catch { }

                temp.CropBoxActive = true;
                temp.CropBoxVisible = false;
                temp.CropBox = source.CropBox; // Duplicate 已經複製裁剪框，這裡明確再設一次

                double left = double.NaN, right = double.NaN, top = double.NaN, bottom = double.NaN;
                ViewCropRegionShapeManager crsm = temp.GetCropRegionShapeManager();
                if (crsm != null)
                {
                    try { if (crsm.ShapeSet) crsm.RemoveCropRegionShape(); } catch { }
                }
                try
                {
                    Parameter annoCrop = temp.get_Parameter(BuiltInParameter.VIEWER_ANNOTATION_CROP_ACTIVE);
                    if (annoCrop != null && !annoCrop.IsReadOnly) annoCrop.Set(1);
                }
                catch { }
                if (crsm != null && crsm.CanHaveAnnotationCrop)
                {
                    SetMinOffset(v => crsm.LeftAnnotationCropOffset = v);
                    SetMinOffset(v => crsm.RightAnnotationCropOffset = v);
                    SetMinOffset(v => crsm.TopAnnotationCropOffset = v);
                    SetMinOffset(v => crsm.BottomAnnotationCropOffset = v);
                    left = crsm.LeftAnnotationCropOffset; right = crsm.RightAnnotationCropOffset;
                    top = crsm.TopAnnotationCropOffset; bottom = crsm.BottomAnnotationCropOffset;
                }

                int hidden = 0;
                var cats = new List<Category>();
                foreach (BuiltInCategory bic in BaseMapHiddenAnnotationCats)
                {
                    try { Category c = Category.GetCategory(doc, bic); if (c != null) cats.Add(c); } catch { }
                }
                foreach (Category c in doc.Settings.Categories)
                {
                    try
                    {
                        if (c.CategoryType == CategoryType.Annotation && c.Id.GetIdValue() < 0 &&
                            Enum.GetName(typeof(BuiltInCategory), (int)c.Id.GetIdValue())?.EndsWith("Tags", StringComparison.Ordinal) == true)
                            cats.Add(c);
                    }
                    catch { }
                }
                foreach (Category c in cats)
                {
                    try
                    {
                        if (temp.CanCategoryBeHidden(c.Id)) { temp.SetCategoryHidden(c.Id, true); hidden++; }
                    }
                    catch { }
                }

                if (own != null) own.Commit();

                setupLog = $"annoOffsetsFt L={left:F6} R={right:F6} T={top:F6} B={bottom:F6} hiddenCats={hidden}";
                return temp;
            }
            catch
            {
                if (own != null && own.HasStarted() && !own.HasEnded()) own.RollBack();
                throw;
            }
        }

        /// <summary>把註記裁剪外擴設到允許的最小值：先試 0，被拒就用逐步加大的值（1mm…）。
        /// 最終值一律以讀回的屬性為準（Revit 可能自行夾值）。</summary>
        private static void SetMinOffset(Action<double> setter)
        {
            double[] tries = { 0.0, CameraMath.MmToFeet(0.1), CameraMath.MmToFeet(1), CameraMath.MmToFeet(10), CameraMath.MmToFeet(100) };
            foreach (double v in tries)
            {
                try { setter(v); return; } catch { }
            }
        }

        private static BaseMapSnapshot ExportDeterministic(Document doc, ViewPlan temp, ViewPlan source, string setupLog)
        {
            var result = new BaseMapSnapshot { GenLevelId = source.GenLevel?.Id };

            BoundingBoxXYZ cropBox = temp.CropBox;
            Transform tf = cropBox.Transform;
            result.OriginX = tf.Origin.X; result.OriginY = tf.Origin.Y; result.OriginZ = tf.Origin.Z;
            result.BasisXX = tf.BasisX.X; result.BasisXY = tf.BasisX.Y; result.BasisXZ = tf.BasisX.Z;
            result.BasisYX = tf.BasisY.X; result.BasisYY = tf.BasisY.Y; result.BasisYZ = tf.BasisY.Z;
            result.BasisZX = tf.BasisZ.X; result.BasisZY = tf.BasisZ.Y; result.BasisZZ = tf.BasisZ.Z;
            result.ModelCropMinXFeet = cropBox.Min.X; result.ModelCropMinYFeet = cropBox.Min.Y;
            result.ModelCropMaxXFeet = cropBox.Max.X; result.ModelCropMaxYFeet = cropBox.Max.Y;

            double oL = 0, oR = 0, oT = 0, oB = 0;
            try
            {
                ViewCropRegionShapeManager crsm = temp.GetCropRegionShapeManager();
                if (crsm != null && crsm.CanHaveAnnotationCrop)
                {
                    oL = crsm.LeftAnnotationCropOffset; oR = crsm.RightAnnotationCropOffset;
                    oT = crsm.TopAnnotationCropOffset; oB = crsm.BottomAnnotationCropOffset;
                }
            }
            catch { }

            string tempDir = Path.Combine(Path.GetTempPath(), "RevitMCP_CameraManager", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                // 與 capture_view_image（CommandExecutor.ViewCapture.cs ExportViewToImage）相同的選項。
                var options = new ImageExportOptions
                {
                    FilePath = Path.Combine(tempDir, "basemap"),
                    ExportRange = ExportRange.SetOfViews,
                    FitDirection = FitDirectionType.Horizontal,
                    HLRandWFViewsFileType = ImageFileType.PNG,
                    ShadowViewsFileType = ImageFileType.PNG,
                    ImageResolution = ImageResolution.DPI_150,
                    ShouldCreateWebSite = false,
                    ZoomType = ZoomFitType.FitToPage,
                    PixelSize = 1600
                };
                options.SetViewsAndSheets(new List<ElementId> { temp.Id });
                doc.ExportImage(options);

                string exported = Directory.GetFiles(tempDir).OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault();
                if (string.IsNullOrEmpty(exported)) throw new Exception("ExportImage 沒有產生任何檔案。");

                TryReadPngSize(exported, out int pw, out int ph);
                result.ImagePixelWidth = pw;
                result.ImagePixelHeight = ph;

                double cropW = cropBox.Max.X - cropBox.Min.X, cropH = cropBox.Max.Y - cropBox.Min.Y;
                double annoW = cropW + oL + oR, annoH = cropH + oT + oB;
                double imgAspect = ph > 0 ? (double)pw / ph : double.NaN;
                double cropAspect = cropH > 1e-9 ? cropW / cropH : double.NaN;
                double annoAspect = annoH > 1e-9 ? annoW / annoH : double.NaN;

                bool useAnno = !double.IsNaN(imgAspect) && !double.IsNaN(annoAspect) &&
                    Math.Abs(annoAspect - imgAspect) < Math.Abs(cropAspect - imgAspect);
                if (useAnno)
                {
                    result.CropMinXFeet = cropBox.Min.X - oL; result.CropMaxXFeet = cropBox.Max.X + oR;
                    result.CropMinYFeet = cropBox.Min.Y - oB; result.CropMaxYFeet = cropBox.Max.Y + oT;
                    result.ExtentsSource = "crop+minAnnotationOffsets";
                }
                else
                {
                    result.CropMinXFeet = cropBox.Min.X; result.CropMaxXFeet = cropBox.Max.X;
                    result.CropMinYFeet = cropBox.Min.Y; result.CropMaxYFeet = cropBox.Max.Y;
                    result.ExtentsSource = "modelCrop";
                }

                Logger.Info("相機管理面板：底圖對位 " + setupLog +
                    $" | image={pw}x{ph}px aspect={imgAspect:F4}" +
                    $" | cropMm={CameraMath.FeetToMm(cropW):F0}x{CameraMath.FeetToMm(cropH):F0} aspect={cropAspect:F4}" +
                    $" | crop+annoMm={CameraMath.FeetToMm(annoW):F0}x{CameraMath.FeetToMm(annoH):F0} aspect={annoAspect:F4}" +
                    $" | chosen={result.ExtentsSource}" +
                    $" | extentsLocalMm X[{CameraMath.FeetToMm(result.CropMinXFeet):F0},{CameraMath.FeetToMm(result.CropMaxXFeet):F0}] Y[{CameraMath.FeetToMm(result.CropMinYFeet):F0},{CameraMath.FeetToMm(result.CropMaxYFeet):F0}]");

                result.ImageFilePath = exported;
                result.TempDirectory = tempDir;
                return result;
            }
            catch
            {
                CleanupTempDir(tempDir);
                throw;
            }
        }

        /// <summary>讀 PNG IHDR（位元組 16–23，big-endian 寬／高）；失敗回傳 0。</summary>
        private static void TryReadPngSize(string path, out int width, out int height)
        {
            width = height = 0;
            try
            {
                using (FileStream fs = File.OpenRead(path))
                {
                    byte[] buf = new byte[24];
                    if (fs.Read(buf, 0, 24) < 24) return;
                    if (buf[0] != 0x89 || buf[1] != 0x50) return;
                    width = (buf[16] << 24) | (buf[17] << 16) | (buf[18] << 8) | buf[19];
                    height = (buf[20] << 24) | (buf[21] << 16) | (buf[22] << 8) | buf[23];
                }
            }
            catch { width = height = 0; }
        }

        internal static BaseMapSnapshot ExportBaseMapLegacy(Document doc, ElementId planViewId)
        {
            var result = new BaseMapSnapshot();
            string tempDir = null;

            try
            {
                View view = doc.GetElement(planViewId) as View;
                if (view == null)
                {
                    result.ErrorMessage = "找不到指定的平面視圖，請按重新整理。";
                    return result;
                }
                if (!view.CropBoxActive)
                {
                    result.Warning = "此平面視圖未啟用 CropBox（不應該被選到，請回報）。";
                }

                result.GenLevelId = (view as ViewPlan)?.GenLevel?.Id;

                BoundingBoxXYZ cropBox = view.CropBox;
                // 攤平成純 double（見 BaseMapSnapshot 註解），不把 Revit Transform 帶出 API context。
                Transform cropTf = cropBox.Transform;
                result.OriginX = cropTf.Origin.X; result.OriginY = cropTf.Origin.Y; result.OriginZ = cropTf.Origin.Z;
                result.BasisXX = cropTf.BasisX.X; result.BasisXY = cropTf.BasisX.Y; result.BasisXZ = cropTf.BasisX.Z;
                result.BasisYX = cropTf.BasisY.X; result.BasisYY = cropTf.BasisY.Y; result.BasisYZ = cropTf.BasisY.Z;
                result.BasisZX = cropTf.BasisZ.X; result.BasisZY = cropTf.BasisZ.Y; result.BasisZZ = cropTf.BasisZ.Z;
                double minX = cropBox.Min.X, minY = cropBox.Min.Y, maxX = cropBox.Max.X, maxY = cropBox.Max.Y;

                // 2026-09-24 實測回報：底圖疊圖位置系統性偏移，根因是平面視圖若啟用了「註記裁剪」
                // (Annotation Crop)，ExportImage 的 FitToPage 匯出範圍是「模型裁剪框 + 註記裁剪
                // 外擴量」（圖名泡、標籤等會伸出模型裁剪框外），但先前的像素↔模型換算只用了
                // view.CropBox（純模型裁剪框），造成兩者不一致、標記全部偏移。這裡用
                // ViewCropRegionShapeManager 的四個 AnnotationCropOffset 把裁剪框往外擴，讓
                // 換算用的範圍與匯出圖片實際涵蓋的範圍一致。
                try
                {
                    ViewCropRegionShapeManager crsm = view.GetCropRegionShapeManager();
                    if (crsm != null && crsm.CanHaveAnnotationCrop)
                    {
                        // ViewCropRegionShapeManager 沒有「是否啟用註記裁剪」的旗標屬性（只有
                        // CanHaveAnnotationCrop 這個「這個視圖類型是否支援」的能力旗標）；「目前
                        // 有沒有打開」是 View 上一個叫「Annotation Crop」的內建參數，但用顯示名稱
                        // LookupParameter 在非英文語系 Revit 會找不到。這裡雙重保險：先試
                        // LookupParameter，找不到就退回「四個外擴量是否有非零值」當代理判斷——
                        // 停用時外擴量通常維持 0，不會誤觸發外擴；找到的話以參數值為準。
                        bool annotationCropActive;
                        Parameter annoCropParam = view.LookupParameter("Annotation Crop");
                        if (annoCropParam != null && annoCropParam.StorageType == StorageType.Integer)
                        {
                            annotationCropActive = annoCropParam.AsInteger() != 0;
                        }
                        else
                        {
                            annotationCropActive = crsm.TopAnnotationCropOffset > 1e-9 || crsm.BottomAnnotationCropOffset > 1e-9
                                || crsm.LeftAnnotationCropOffset > 1e-9 || crsm.RightAnnotationCropOffset > 1e-9;
                        }

                        if (annotationCropActive)
                        {
                            minX -= crsm.LeftAnnotationCropOffset;
                            maxX += crsm.RightAnnotationCropOffset;
                            minY -= crsm.BottomAnnotationCropOffset;
                            maxY += crsm.TopAnnotationCropOffset;
                        }
                    }
                }
                catch
                {
                    // 某些視圖類型/版本可能不支援 ViewCropRegionShapeManager 或其中某個屬性；
                    // 忽略、退回純模型 CropBox，不讓底圖匯出因此中斷。
                }

                result.CropMinXFeet = minX;
                result.CropMinYFeet = minY;
                result.CropMaxXFeet = maxX;
                result.CropMaxYFeet = maxY;

                tempDir = Path.Combine(Path.GetTempPath(), "RevitMCP_CameraManager", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDir);

                var options = new ImageExportOptions
                {
                    FilePath = Path.Combine(tempDir, "basemap"),
                    ExportRange = ExportRange.SetOfViews,
                    FitDirection = FitDirectionType.Horizontal,
                    HLRandWFViewsFileType = ImageFileType.PNG,
                    ShadowViewsFileType = ImageFileType.PNG,
                    ImageResolution = ImageResolution.DPI_150,
                    ShouldCreateWebSite = false,
                    ZoomType = ZoomFitType.FitToPage,
                    PixelSize = 1600
                };
                options.SetViewsAndSheets(new List<ElementId> { view.Id });

                doc.ExportImage(options);

                string exported = Directory.GetFiles(tempDir)
                    .OrderByDescending(f => new FileInfo(f).Length)
                    .FirstOrDefault();

                if (string.IsNullOrEmpty(exported))
                {
                    result.ErrorMessage = "底圖匯出失敗：ExportImage 沒有產生任何檔案。";
                    CleanupTempDir(tempDir);
                    return result;
                }

                result.ImageFilePath = exported;
                result.TempDirectory = tempDir;
                return result;
            }
            catch (Exception ex)
            {
                result.ErrorMessage = "底圖匯出失敗：" + ex.Message;
                CleanupTempDir(tempDir);
                return result;
            }
        }

        internal static void CleanupTempDir(string directory)
        {
            try
            {
                if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
                    Directory.Delete(directory, true);
            }
            catch
            {
                // 暫存清理失敗不影響主要功能
            }
        }

        // ══════════════════════════════════════════════════════════
        // 4.5 人視高預設值套用 / 4.3 細節編輯
        // ══════════════════════════════════════════════════════════

        internal static (int okCount, int skippedCount, string error) ApplyHeightToCameras(
            Document doc, IList<ElementId> viewIds, double heightCm)
        {
            if (doc == null) return (0, 0, "目前沒有開啟中的文件。");

            int okCount = 0, skippedCount = 0;
            try
            {
                List<Level> levels = CameraMath.GetStoreyLevelsSorted(doc);
                using (Transaction t = TransactionHelper.Begin(doc, "套用相機人視高"))
                {
                    t.Start();
                    foreach (ElementId id in viewIds)
                    {
                        View3D v = doc.GetElement(id) as View3D;
                        if (v == null || v.IsTemplate || !v.IsPerspective) { skippedCount++; continue; }

                        double eyeZFeet = v.GetOrientation().EyePosition.Z;
                        Level levelBelow = CameraMath.FindLevelBelow(levels, eyeZFeet);
                        if (levelBelow == null) { skippedCount++; continue; }

                        v.SetOrientation(CameraMath.BuildOrientationForHeightAboveLevel(v, levelBelow, heightCm));
                        okCount++;
                    }
                    t.Commit();
                }
                return (okCount, skippedCount, null);
            }
            catch (Exception ex)
            {
                return (okCount, skippedCount, "套用高度失敗：" + ex.Message);
            }
        }

        /// <summary>4.3 細節編輯：一次寫入位置／樓層／高度／角度／遠裁剪，一個交易、一個中文復原名稱。</summary>
        internal static string ApplyDetailEdit(Document doc, CameraEditRequest req)
        {
            if (doc == null) return "目前沒有開啟中的文件。";

            try
            {
                View3D v = doc.GetElement(req.ViewId) as View3D;
                if (v == null || v.IsTemplate || !v.IsPerspective)
                    return "找不到這個相機視圖（可能已被刪除），請按重新整理。";

                Level level = doc.GetElement(req.LevelId) as Level;
                if (level == null) return "找不到指定的樓層。";

                using (Transaction t = TransactionHelper.Begin(doc, "編輯相機"))
                {
                    t.Start();

                    // 先套位置／樓層／高度（眼睛 X/Y/Z），再套角度（角度重建時眼睛位置不變，
                    // 所以順序對最終結果沒有影響，但先位置後角度比較符合「使用者由上往下填欄位」的直覺）。
                    ViewOrientation3D afterPosition = CameraMath.BuildOrientationForPosition(
                        v, req.EyeXFeet, req.EyeYFeet, level, req.HeightAboveLevelCm);
                    v.SetOrientation(afterPosition);

                    ViewOrientation3D afterAngles = CameraMath.BuildOrientationForAngles(
                        v, req.HorizontalAngleDeg, req.PitchDeg);
                    v.SetOrientation(afterAngles);

                    if (req.FarClipCm.HasValue)
                    {
                        Parameter farClipParam = CameraMath.FindFarClipOffsetParameter(v);
                        if (farClipParam != null && !farClipParam.IsReadOnly)
                        {
                            double feet = CameraMath.MmToFeet(req.FarClipCm.Value * 10.0);
                            farClipParam.Set(feet);
                        }
                    }

                    t.Commit();
                }
                return null;
            }
            catch (Exception ex)
            {
                return "編輯相機失敗：" + ex.Message;
            }
        }

        // ══════════════════════════════════════════════════════════
        // Tab1 清單內編輯：重新命名（名稱欄可直接改）
        // ══════════════════════════════════════════════════════════

        /// <summary>清單「名稱」欄內編輯：重複名稱直接拒絕並回錯誤訊息，不寫入。</summary>
        internal static string RenameCameraView(Document doc, ElementId viewId, string newName)
        {
            if (doc == null) return "目前沒有開啟中的文件。";
            if (string.IsNullOrWhiteSpace(newName)) return "名稱不可為空白。";
            newName = newName.Trim();

            try
            {
                View3D v = doc.GetElement(viewId) as View3D;
                if (v == null || v.IsTemplate || !v.IsPerspective)
                    return "找不到這個相機視圖（可能已被刪除），請按重新整理。";

                if (string.Equals(v.Name, newName, StringComparison.Ordinal)) return null;

                bool duplicate = new FilteredElementCollector(doc)
                    .OfClass(typeof(View))
                    .Cast<View>()
                    .Any(o => o.Id != viewId && string.Equals(o.Name, newName, StringComparison.OrdinalIgnoreCase));
                if (duplicate) return "已存在同名視圖「" + newName + "」，請換一個名稱。";

                using (Transaction t = TransactionHelper.Begin(doc, "重新命名相機視圖"))
                {
                    t.Start();
                    v.Name = newName;
                    t.Commit();
                }
                return null;
            }
            catch (Exception ex)
            {
                return "重新命名失敗：" + ex.Message;
            }
        }

        // ══════════════════════════════════════════════════════════
        // 4.6 複製相機到其他樓層
        // ══════════════════════════════════════════════════════════

        internal static (int created, string error) CopyCameraToLevels(
            Document doc, ElementId sourceViewId, IList<ElementId> targetLevelIds)
            => CopyCameraToLevels(doc, sourceViewId, targetLevelIds, null);

        /// <summary>2026-09-25：多一個 <paramref name="createdCameras"/>（可為 null）回傳新相機 (Id, 名稱)，
        /// 供清單「樓層」欄改樓層＝複製後選取新相機。</summary>
        internal static (int created, string error) CopyCameraToLevels(
            Document doc, ElementId sourceViewId, IList<ElementId> targetLevelIds,
            List<(ElementId id, string name)> createdCameras)
        {
            if (doc == null) return (0, "目前沒有開啟中的文件。");

            try
            {
                View3D source = doc.GetElement(sourceViewId) as View3D;
                if (source == null || source.IsTemplate || !source.IsPerspective)
                    return (0, "找不到來源相機視圖（可能已被刪除），請按重新整理。");

                List<Level> levels = CameraMath.GetStoreyLevelsSorted(doc);
                ViewOrientation3D srcOrientation = source.GetOrientation();
                double eyeZFeet = srcOrientation.EyePosition.Z;
                Level srcLevelBelow = CameraMath.FindLevelBelow(levels, eyeZFeet);
                double heightAboveLevelCm = srcLevelBelow != null
                    ? CameraMath.FeetToMm(eyeZFeet - srcLevelBelow.ProjectElevation) / 10.0
                    : 0.0;

                HashSet<string> existingNames = new HashSet<string>(
                    new FilteredElementCollector(doc)
                        .OfClass(typeof(View3D))
                        .Cast<View3D>()
                        .Select(v => v.Name),
                    StringComparer.OrdinalIgnoreCase);

                int created = 0;
                using (Transaction t = TransactionHelper.Begin(doc, "複製相機到其他樓層"))
                {
                    t.Start();
                    foreach (ElementId targetLevelId in targetLevelIds)
                    {
                        Level targetLevel = doc.GetElement(targetLevelId) as Level;
                        if (targetLevel == null) continue;

                        ElementId copyId = source.Duplicate(ViewDuplicateOption.Duplicate);
                        View3D copy = doc.GetElement(copyId) as View3D;
                        if (copy == null) continue;

                        string baseName = source.Name + " - " + targetLevel.Name;
                        string finalName = baseName;
                        int suffix = 2;
                        while (existingNames.Contains(finalName))
                        {
                            finalName = baseName + " (" + suffix + ")";
                            suffix++;
                        }
                        copy.Name = finalName;
                        existingNames.Add(finalName);

                        copy.SetOrientation(CameraMath.BuildOrientationForPosition(
                            copy, srcOrientation.EyePosition.X, srcOrientation.EyePosition.Y,
                            targetLevel, heightAboveLevelCm));

                        created++;
                        createdCameras?.Add((copyId, finalName));
                    }
                    t.Commit();
                }
                return (created, null);
            }
            catch (Exception ex)
            {
                return (0, "複製相機失敗：" + ex.Message);
            }
        }

        // ══════════════════════════════════════════════════════════
        // 4.7 匯出 Excel
        // ══════════════════════════════════════════════════════════

        internal static string ExportExcel(IList<CameraRow> rows, string filePath)
        {
            try
            {
                using (var workbook = new XLWorkbook())
                {
                    var ws = workbook.Worksheets.Add("相機清單");
                    string[] headers =
                    {
                        "名稱", "樓層", "離樓板高度cm", "平面X(cm)", "平面Y(cm)",
                        "水平角(°)", "俯仰角(°)", "水平視野角估算(°)", "遠裁剪(cm)", "Eye Elevation 原始值"
                    };
                    for (int i = 0; i < headers.Length; i++)
                        ws.Cell(1, i + 1).Value = headers[i];
                    ws.Row(1).Style.Font.Bold = true;

                    int r = 2;
                    foreach (CameraRow row in rows)
                    {
                        ws.Cell(r, 1).Value = row.Name;
                        ws.Cell(r, 2).Value = row.LevelName;
                        ws.Cell(r, 3).Value = row.HeightAboveLevelCm ?? double.NaN;
                        ws.Cell(r, 4).Value = row.EyeXCm;
                        ws.Cell(r, 5).Value = row.EyeYCm;
                        ws.Cell(r, 6).Value = row.HorizontalAngleDeg;
                        ws.Cell(r, 7).Value = row.PitchDeg;
                        ws.Cell(r, 8).Value = row.FovEstimateDeg ?? double.NaN;
                        ws.Cell(r, 9).Value = row.FarClipCm ?? double.NaN;
                        ws.Cell(r, 10).Value = row.EyeElevationDisplay;
                        r++;
                    }
                    ws.Columns().AdjustToContents();
                    workbook.SaveAs(filePath);
                }
                return null;
            }
            catch (Exception ex)
            {
                return "匯出 Excel 失敗：" + ex.Message;
            }
        }

        // ══════════════════════════════════════════════════════════
        // 跳轉視圖
        // ══════════════════════════════════════════════════════════

        internal static string JumpToView(UIApplication uiApp, ElementId viewId)
        {
            Document doc = uiApp?.ActiveUIDocument?.Document;
            View3D v = doc?.GetElement(viewId) as View3D;
            if (v == null) return "找不到這個相機視圖（可能已被刪除），請按重新整理。";
            try
            {
                uiApp.ActiveUIDocument.ActiveView = v;
                return null;
            }
            catch (Exception ex)
            {
                return "跳轉失敗：" + ex.Message;
            }
        }

        // ══════════════════════════════════════════════════════════
        // 4.9 A：並排檢視 —— 找相機所在樓層一張「既有」平面視圖
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// 找相機 level-below 對應的既有平面視圖：優先「已經開啟中的」，其次優先已啟用 CropBox，
        /// 再其次依名稱排序取第一個。絕不新建視圖（4.9 A 只用既有視圖）。
        /// </summary>
        internal static ElementId FindPlanViewForLevel(UIApplication uiApp, Document doc, ElementId levelId)
        {
            if (doc == null || levelId == null) return null;

            List<ViewPlan> candidates = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewPlan))
                .Cast<ViewPlan>()
                .Where(v => !v.IsTemplate && v.GenLevel != null && v.GenLevel.Id == levelId)
                .OrderBy(v => v.Name)
                .ToList();
            if (candidates.Count == 0) return null;

            HashSet<ElementId> openIds = new HashSet<ElementId>(
                uiApp.ActiveUIDocument?.GetOpenUIViews()?.Select(uv => uv.ViewId) ?? Enumerable.Empty<ElementId>());

            ViewPlan chosen = candidates.FirstOrDefault(v => openIds.Contains(v.Id) && v.CropBoxActive)
                ?? candidates.FirstOrDefault(v => openIds.Contains(v.Id))
                ?? candidates.FirstOrDefault(v => v.CropBoxActive)
                ?? candidates[0];
            return chosen.Id;
        }

        /// <summary>目前所有開啟中的 UIView（並排檢視前拍照，供「還原」比對用）。</summary>
        internal static List<(ElementId viewId, UIView uiView)> SnapshotOpenViews(UIApplication uiApp)
        {
            var list = new List<(ElementId, UIView)>();
            IList<UIView> open = uiApp.ActiveUIDocument?.GetOpenUIViews();
            if (open == null) return list;
            foreach (UIView uv in open) list.Add((uv.ViewId, uv));
            return list;
        }
    }
}
