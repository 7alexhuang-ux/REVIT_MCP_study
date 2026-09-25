using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;

// Revit 2025+ ElementId: int → long
#if REVIT2025_OR_GREATER
using IdType = System.Int64;
#else
using IdType = System.Int32;
#endif

namespace RevitMCP.Core
{
    /// <summary>
    /// 相機管理（Camera Manager）的 MCP 測試工具（2026-09-25 Wave 1）。
    ///
    /// 目的：讓 AI 能自動測試相機管理停靠面板背後的同一套服務程式碼，不必靠人工點面板。
    /// 原則：
    ///   1. 不重複邏輯——讀清單用 CameraManagerService.ReadSnapshot／BuildCameraRow，一鍵 4 視圖用
    ///      CameraKeyPlanService.CreateOrUpdateFourViews（＋CameraManagerService.OpenAndTileFourViews），
    ///      改相機用 CameraManagerService.BuildEditRequestFromRow＋ApplyDetailEdit，複製用 CopyCameraToLevels。
    ///   2. 走既有 MCP ExternalEvent 路徑（CommandExecutor 在 ExternalEvent.Execute 內執行），不經面板的
    ///      CameraManagerEventQueue；上述服務方法全部不含 WPF 型別，各自開自己的 Transaction。
    ///   3. 所有長度輸出為 mm（內部原點座標系），角度為度。
    /// </summary>
    public partial class CommandExecutor
    {
        private static double CmMm(double feet) => Math.Round(CameraMath.FeetToMm(feet), 1);

        private static object CmPoint(XYZ p) => p == null ? null : new { X = CmMm(p.X), Y = CmMm(p.Y), Z = CmMm(p.Z) };

        private static object CmVector(XYZ v) => v == null ? null : new { X = Math.Round(v.X, 5), Y = Math.Round(v.Y, 5), Z = Math.Round(v.Z, 5) };

        private static double? CmRound(double? v, int digits = 1) => v.HasValue ? (double?)Math.Round(v.Value, digits) : null;

        private static object CmBox(BoundingBoxXYZ box)
        {
            if (box == null) return null;
            Transform tf = box.Transform ?? Transform.Identity;
            return new
            {
                MinLocalMm = CmPoint(box.Min),
                MaxLocalMm = CmPoint(box.Max),
                SizeMm = new { X = CmMm(box.Max.X - box.Min.X), Y = CmMm(box.Max.Y - box.Min.Y), Z = CmMm(box.Max.Z - box.Min.Z) },
                Transform = new
                {
                    OriginMm = CmPoint(tf.Origin),
                    BasisX = CmVector(tf.BasisX),
                    BasisY = CmVector(tf.BasisY),
                    BasisZ = CmVector(tf.BasisZ)
                },
                // 世界座標的 Min/Max 角點（Transform 套用後；旋轉的框只代表兩個角點，不是外接框）
                MinWorldMm = CmPoint(tf.OfPoint(box.Min)),
                MaxWorldMm = CmPoint(tf.OfPoint(box.Max))
            };
        }

        /// <summary>解析透視相機：cameraId 可給相機 View3D 或平面上的相機元件；省略＝作用中視圖。</summary>
        private View3D ResolvePerspectiveCamera(Document doc, JObject parameters)
        {
            var p = new JObject();
            if (parameters?["cameraId"] != null) p["viewId"] = parameters["cameraId"];
            View3D view = ResolveCameraView(doc, p);
            if (view.IsTemplate || !view.IsPerspective)
                throw new Exception($"視圖「{view.Name}」不是透視相機（IsPerspective=false 或為樣板）。");
            return view;
        }

        private static CameraSectionOrientationMode? ParseSectionMode(JObject parameters)
        {
            string s = parameters?["sectionMode"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(s)) return null;
            switch (s.Trim().ToLowerInvariant())
            {
                case "room":
                case "roomlongaxis":
                case "alongroomlongaxis":
                    return CameraSectionOrientationMode.AlongRoomLongAxis;
                case "sight":
                case "sightline":
                case "alongsightline":
                    return CameraSectionOrientationMode.AlongSightLine;
                default:
                    throw new Exception($"sectionMode「{s}」無效，請用 room 或 sight。");
            }
        }

        private static CameraIsoExtentMode? ParseIsoMode(JObject parameters)
        {
            string s = parameters?["isoMode"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(s)) return null;
            switch (s.Trim().ToLowerInvariant())
            {
                case "full":
                case "fullwithupperbeams":
                    return CameraIsoExtentMode.FullWithUpperBeams;
                case "current":
                case "currentlevel":
                case "currentleveltoponly":
                    return CameraIsoExtentMode.CurrentLevelTopOnly;
                default:
                    throw new Exception($"isoMode「{s}」無效，請用 full 或 current。");
            }
        }

        // ══════════════════════════════════════════════════════════
        // list_cameras（唯讀）
        // ══════════════════════════════════════════════════════════

        private object ListCameras(JObject parameters)
        {
            Document doc = _uiApp.ActiveUIDocument.Document;
            string nameContains = parameters?["nameContains"]?.Value<string>();

            CameraManagerSnapshot snap = CameraManagerService.ReadSnapshot(doc);
            if (!string.IsNullOrEmpty(snap.ErrorMessage)) throw new Exception(snap.ErrorMessage);

            List<Level> storeys = CameraMath.GetStoreyLevelsSorted(doc);
            CameraSectionOrientationMode configuredMode = CameraKeyPlanService.GetConfiguredSectionOrientationMode();

            var cameras = new List<object>();
            foreach (CameraRow row in snap.Cameras)
            {
                if (!string.IsNullOrEmpty(nameContains) &&
                    (row.Name ?? "").IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) < 0) continue;

                View3D view = doc.GetElement(row.ViewId) as View3D;
                if (view == null) continue;
                CameraMath.CameraGeometry geom = CameraMath.ComputeGeometry(view, storeys);

                CameraMath.PerspectiveFov fov = null;
                try { fov = CameraMath.ComputePerspectiveFov(view); }
                catch (Exception ex) { Logger.Error("list_cameras：視野角計算失敗", ex); }

                long? roomId = null; string roomName = null, roomLog = null;
                try { CameraKeyPlanService.DescribeEyeRoom(view, geom, out roomId, out roomName, out roomLog); }
                catch (Exception ex) { roomLog = "failed: " + ex.Message; }

                // 底圖挑選：與 ③ 位置圖選取相機時同一個呼叫（單台相機＝SourcePlanHint.From(row)）。
                object baseMap = null;
                if (row.LevelId != null)
                {
                    var eyes = new List<double[]> { new[] { row.EyeXFeet, row.EyeYFeet, row.EyeZFeet } };
                    PlanViewOption chosen = BaseMapPlanChooser.Choose(snap.PlanViews, row.LevelId, eyes, snap.Levels,
                        out string why, out int step, SourcePlanHint.From(row));
                    baseMap = new
                    {
                        PlanViewId = chosen?.ViewId?.GetIdValue(),
                        PlanName = chosen?.PlanName,
                        LevelName = chosen?.LevelName,
                        Step = step,
                        Reason = why
                    };
                }

                View plan = CameraKeyPlanService.FindLinkedKeyPlanView(doc, row.UniqueId, "Plan");
                View section = CameraKeyPlanService.FindLinkedKeyPlanView(doc, row.UniqueId, "Section");
                View iso = CameraKeyPlanService.FindLinkedKeyPlanView(doc, row.UniqueId, "Iso");
                bool? sectionMoved = section is ViewSection vs ? (bool?)CameraKeyPlanService.IsSectionManuallyMoved(vs) : null;

                bool outdoor = row.IsOutdoor == true;
                cameras.Add(new
                {
                    Id = row.ViewId.GetIdValue(),
                    row.Name,
                    Level = row.LevelName,
                    LevelId = row.LevelId?.GetIdValue(),
                    HeightAboveLevelCm = row.HeightAboveLevelCm,
                    HeightAboveLevelMm = geom.AboveLevelBelowMm.HasValue ? (double?)Math.Round(geom.AboveLevelBelowMm.Value, 1) : null,
                    HeightWarning = row.HeightWarning,
                    EyeMm = CmPoint(geom.Eye),
                    YawDeg = Math.Round(row.HorizontalAngleDeg, 2),
                    PitchDeg = Math.Round(row.PitchDeg, 2),
                    Fov = fov == null ? null : new
                    {
                        LeftDeg = CmRound(fov.LeftDeg, 2),
                        RightDeg = CmRound(fov.RightDeg, 2),
                        DownDeg = CmRound(fov.DownDeg, 2),
                        UpDeg = CmRound(fov.UpDeg, 2),
                        HorizontalDeg = CmRound(fov.HFovDeg, 2),
                        VerticalDeg = CmRound(fov.VFovDeg, 2),
                        fov.Source
                    },
                    FarClipCm = row.FarClipCm,
                    FarClipEditable = row.FarClipEditable,
                    IndoorOutdoor = row.IndoorOutdoorText,
                    Room = new { Id = roomId, Name = roomName, Log = roomLog },
                    OutdoorFanRadiusMm = outdoor ? (double?)CmMm(row.OutdoorFanRadiusFeet) : null,
                    SourcePlan = row.SourcePlanId == null ? null : new
                    {
                        Id = row.SourcePlanId.GetIdValue(),
                        Name = row.SourcePlanName,
                        Cropped = row.SourcePlanCropped,
                        PrimaryId = row.SourcePlanPrimaryId?.GetIdValue()
                    },
                    BaseMap = baseMap,
                    KeyPlan = new
                    {
                        PlanViewId = plan?.Id.GetIdValue(),
                        SectionViewId = section?.Id.GetIdValue(),
                        IsoViewId = iso?.Id.GetIdValue(),
                        SectionManuallyMoved = sectionMoved
                    },
                    SectionOrientation = new
                    {
                        Configured = configuredMode.ToString(),
                        // 室外一律沿視線；室內找不到房間時 ComputeSectionSpec 也會退回沿視線（以 run_camera_keyplan 的 SectionModeUsed 為準）
                        ExpectedForThisCamera = outdoor ? CameraSectionOrientationMode.AlongSightLine.ToString() : configuredMode.ToString()
                    }
                });
            }

            return new
            {
                Success = true,
                snap.DocumentTitle,
                Count = cameras.Count,
                Note = "長度為內部原點座標系 mm；離樓板高度以建築樓層（Building Story）為基準；水平角 0°=+Y、順時針為正。",
                StoreyLevels = snap.Levels.Select(l => new { Id = l.LevelId.GetIdValue(), l.Name, ElevationMm = CmMm(l.ProjectElevationFeet) }).ToList(),
                Cameras = cameras
            };
        }

        // ══════════════════════════════════════════════════════════
        // run_camera_keyplan（寫入）
        // ══════════════════════════════════════════════════════════

        private object RunCameraKeyPlan(JObject parameters)
        {
            Document doc = _uiApp.ActiveUIDocument.Document;
            View3D camera = ResolvePerspectiveCamera(doc, parameters);
            bool tile = parameters?["tile"]?.Value<bool?>() ?? false;
            bool resetSection = parameters?["resetSection"]?.Value<bool?>() ?? false;
            var options = new CameraKeyPlanService.KeyPlanRunOptions
            {
                SectionMode = ParseSectionMode(parameters),
                IsoMode = ParseIsoMode(parameters)
            };

            CameraKeyPlanService.KeyPlanResult result =
                CameraKeyPlanService.CreateOrUpdateFourViews(_uiApp, doc, camera.Id, resetSection, options);
            if (result.Error != null) throw new Exception(result.Error);

            string tileError = null;
            if (tile) tileError = CameraManagerService.OpenAndTileFourViews(_uiApp, doc, camera.Id, result);

            ViewPlan plan = doc.GetElement(result.PlanViewId) as ViewPlan;
            ViewSection section = doc.GetElement(result.SectionViewId) as ViewSection;
            View3D iso = doc.GetElement(result.IsoViewId) as View3D;
            string cid = camera.UniqueId;
            CameraKeyPlanService.KeyPlanDecisions d = result.Decisions;

            object Describe(View v, Func<object> extents) => v == null ? null : new
            {
                Id = v.Id.GetIdValue(),
                v.Name,
                Scale = SafeScale(v),
                Extents = extents(),
                TaggedElements = CameraKeyPlanService.CountTaggedElementsByRole(doc, v, cid)
            };

            double? isoYaw = null;
            if (iso != null)
            {
                XYZ f = iso.GetOrientation().ForwardDirection;
                isoYaw = Math.Round(CameraMath.ComputeHorizontalAngleDeg(f), 2);
            }

            return new
            {
                Success = true,
                Camera = new { Id = camera.Id.GetIdValue(), camera.Name },
                SectionPreserved = result.SectionPreserved,
                Tiled = tile && tileError == null,
                TileError = tileError,
                Plan = Describe(plan, () => plan == null ? null : new { plan.CropBoxActive, CropBox = CmBox(plan.CropBox) }),
                Section = Describe(section, () => section == null ? null : new { CropBox = CmBox(section.CropBox) }),
                Iso = Describe(iso, () => iso == null ? null : new
                {
                    iso.IsSectionBoxActive,
                    SectionBox = CmBox(iso.GetSectionBox()),
                    ForwardDirection = CmVector(iso.GetOrientation().ForwardDirection),
                    CornerYawDeg = isoYaw
                }),
                Decisions = new
                {
                    IndoorOutdoor = d.IsOutdoor ? "室外" : "室內",
                    d.ClassLog,
                    Room = new { Id = d.RoomId, Name = d.RoomName, Log = d.RoomLog },
                    FarClipMm = CmMm(d.FarClipFeet),
                    FanRadiusMm = CmMm(d.FanRadiusFeet),
                    d.FanLog,
                    CenterYawDeg = Math.Round(d.CenterYawDeg, 2),
                    FovEdgesDeg = new { Left = Math.Round(d.LeftDeg, 2), Right = Math.Round(d.RightDeg, 2), Down = Math.Round(d.DownDeg, 2), Up = Math.Round(d.UpDeg, 2) },
                    HFovDeg = CmRound(d.HFovDeg, 2),
                    VFovDeg = CmRound(d.VFovDeg, 2),
                    StoreyTop = new
                    {
                        RoomTopMm = d.RoomTopZ.HasValue ? (double?)CmMm(d.RoomTopZ.Value) : null,
                        UpperSlabBottomMm = double.IsInfinity(d.StoreyTopSlabBottomZ) || double.IsNaN(d.StoreyTopSlabBottomZ) ? (double?)null : CmMm(d.StoreyTopSlabBottomZ),
                        UpperBeamBottomMm = d.StoreyTopBeamBottomZ.HasValue ? (double?)CmMm(d.StoreyTopBeamBottomZ.Value) : null,
                        Outdoor = d.StoreyTopOutdoor,
                        Log = d.StoreyTopLog
                    },
                    IsoXYLog = d.IsoXYLog,
                    SectionModeRequested = d.SectionModeRequested.ToString(),
                    SectionModeUsed = d.SectionModeUsed.ToString(),
                    d.SectionSpecLog,
                    IsoExtentMode = d.IsoExtentMode.ToString(),
                    KeyPlanStyle = d.FadedStyle ? "淡化背景" : "原始"
                }
            };
        }

        private static int? SafeScale(View v)
        {
            try { return v.Scale; } catch { return null; }
        }

        // ══════════════════════════════════════════════════════════
        // set_camera_pose（寫入）
        // ══════════════════════════════════════════════════════════

        private object SetCameraPose(JObject parameters)
        {
            Document doc = _uiApp.ActiveUIDocument.Document;
            View3D camera = ResolvePerspectiveCamera(doc, parameters);
            List<Level> storeys = CameraMath.GetStoreyLevelsSorted(doc);

            CameraRow before = CameraManagerService.BuildCameraRow(camera, storeys);
            if (before.LevelId == null) throw new Exception("這台相機找不到下方建築樓層，無法編輯（與面板相同限制）。");

            double? h = parameters?["heightAboveLevelCm"]?.Value<double?>();
            double? yaw = parameters?["yawDeg"]?.Value<double?>();
            double? pitch = parameters?["pitchDeg"]?.Value<double?>();
            double? x = parameters?["planXcm"]?.Value<double?>();
            double? y = parameters?["planYcm"]?.Value<double?>();
            double? far = parameters?["farClipCm"]?.Value<double?>();
            if (!h.HasValue && !yaw.HasValue && !pitch.HasValue && !x.HasValue && !y.HasValue && !far.HasValue)
                throw new Exception("至少要給一個欄位：heightAboveLevelCm、yawDeg、pitchDeg、planXcm、planYcm、farClipCm。");
            if (far.HasValue && !before.FarClipEditable)
                throw new Exception("這台相機的遠裁剪參數不可寫入（面板該欄同樣唯讀）。");

            // 與面板清單編輯同一條路徑：用這一列的既有值補齊，再只覆寫給定欄位 → ApplyDetailEdit（一個交易「編輯相機」）。
            CameraEditRequest req = CameraManagerService.BuildEditRequestFromRow(before);
            if (h.HasValue) req.HeightAboveLevelCm = h.Value;
            if (yaw.HasValue) req.HorizontalAngleDeg = yaw.Value;
            if (pitch.HasValue) req.PitchDeg = pitch.Value;
            if (x.HasValue) req.EyeXFeet = CameraMath.MmToFeet(x.Value * 10.0);
            if (y.HasValue) req.EyeYFeet = CameraMath.MmToFeet(y.Value * 10.0);
            if (far.HasValue) req.FarClipCm = far.Value;

            string err = CameraManagerService.ApplyDetailEdit(doc, req);
            if (err != null) throw new Exception(err);

            CameraRow after = CameraManagerService.BuildCameraRow(camera, storeys);
            object Pose(CameraRow r) => new
            {
                Level = r.LevelName,
                r.HeightAboveLevelCm,
                PlanXcm = Math.Round(r.EyeXCm, 1),
                PlanYcm = Math.Round(r.EyeYCm, 1),
                YawDeg = Math.Round(r.HorizontalAngleDeg, 2),
                PitchDeg = Math.Round(r.PitchDeg, 2),
                r.FarClipCm
            };

            return new
            {
                Success = true,
                Camera = new { Id = camera.Id.GetIdValue(), camera.Name },
                Note = "planXcm/planYcm 為內部原點座標系（與面板「平面 X/Y cm」欄相同）。Key Plan 視圖不會自動重算，需要時再跑 run_camera_keyplan。",
                Before = Pose(before),
                After = Pose(after)
            };
        }

        // ══════════════════════════════════════════════════════════
        // copy_camera_to_level（寫入）
        // ══════════════════════════════════════════════════════════

        private object CopyCameraToLevel(JObject parameters)
        {
            Document doc = _uiApp.ActiveUIDocument.Document;
            View3D camera = ResolvePerspectiveCamera(doc, parameters);

            Level target = null;
            IdType? levelId = parameters?["levelId"]?.Value<IdType?>();
            string levelName = parameters?["levelName"]?.Value<string>();
            if (levelId.HasValue)
            {
                target = doc.GetElement(new ElementId(levelId.Value)) as Level;
                if (target == null) throw new Exception($"levelId {levelId.Value} 不是樓層。");
            }
            else if (!string.IsNullOrWhiteSpace(levelName))
            {
                List<Level> all = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().ToList();
                List<Level> matches = all.Where(l => string.Equals(l.Name, levelName.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
                if (matches.Count != 1)
                {
                    string storeys = string.Join("、", CameraMath.GetStoreyLevelsSorted(doc).Select(l => l.Name));
                    throw new Exception(matches.Count == 0
                        ? $"找不到名稱為「{levelName}」的樓層。建築樓層：{storeys}"
                        : $"名稱「{levelName}」對應到 {matches.Count} 個樓層，請改用 levelId。");
                }
                target = matches[0];
            }
            else throw new Exception("請給 levelId 或 levelName。");

            var created = new List<(ElementId id, string name)>();
            var (count, err) = CameraManagerService.CopyCameraToLevels(doc, camera.Id, new List<ElementId> { target.Id }, created);
            if (err != null) throw new Exception(err);
            if (count == 0 || created.Count == 0) throw new Exception("沒有建立任何相機。");

            return new
            {
                Success = true,
                Source = new { Id = camera.Id.GetIdValue(), camera.Name },
                TargetLevel = new { Id = target.Id.GetIdValue(), target.Name, IsBuildingStorey = CameraMath.IsBuildingStorey(target) },
                NewCamera = new { Id = created[0].id.GetIdValue(), Name = created[0].name },
                Note = "原相機與其 Key Plan 視圖不變；新相機沒有 Key Plan 視圖（需要時跑 run_camera_keyplan）。"
            };
        }

        // ══════════════════════════════════════════════════════════
        // get_view_extents（唯讀，通用）
        // ══════════════════════════════════════════════════════════

        private object GetViewExtents(JObject parameters)
        {
            Document doc = _uiApp.ActiveUIDocument.Document;
            IdType? requested = parameters?["viewId"]?.Value<IdType?>();
            View view = requested.HasValue
                ? doc.GetElement(new ElementId(requested.Value)) as View
                : _uiApp.ActiveUIDocument.ActiveView;
            if (view == null) throw new Exception(requested.HasValue ? $"元素 {requested.Value} 不是視圖。" : "沒有作用中視圖。");

            string ParamText(BuiltInParameter bip)
            {
                try
                {
                    Parameter p = view.get_Parameter(bip);
                    if (p == null) return null;
                    return p.AsValueString() ?? (p.StorageType == StorageType.Integer ? p.AsInteger().ToString() : null);
                }
                catch { return null; }
            }

            // 裁剪框
            object crop = null;
            try
            {
                if (view.CanBePrinted || view is View3D || view is ViewPlan || view is ViewSection)
                {
                    crop = new
                    {
                        Active = view.CropBoxActive,
                        Visible = view.CropBoxVisible,
                        Box = CmBox(view.CropBox)
                    };
                }
            }
            catch (Exception ex) { crop = new { Error = ex.Message }; }

            // 註解裁剪
            object annotationCrop = null;
            try
            {
                ViewCropRegionShapeManager sm = view.GetCropRegionShapeManager();
                if (sm != null && sm.CanHaveAnnotationCrop)
                {
                    annotationCrop = new
                    {
                        Active = ParamText(BuiltInParameter.VIEWER_ANNOTATION_CROP_ACTIVE),
                        OffsetsMm = new
                        {
                            Top = CmMm(sm.TopAnnotationCropOffset),
                            Bottom = CmMm(sm.BottomAnnotationCropOffset),
                            Left = CmMm(sm.LeftAnnotationCropOffset),
                            Right = CmMm(sm.RightAnnotationCropOffset)
                        },
                        ShapeEdited = sm.ShapeSet
                    };
                }
            }
            catch (Exception ex) { annotationCrop = new { Error = ex.Message }; }

            // 3D：剖面框／方向／遠裁剪
            object view3d = null;
            if (view is View3D v3)
            {
                object farClip = null;
                if (v3.IsPerspective)
                {
                    Parameter fp = CameraMath.FindFarClipOffsetParameter(v3);
                    farClip = fp == null ? null : new { Parameter = fp.Definition?.Name, Mm = CmMm(fp.AsDouble()), fp.IsReadOnly };
                }
                ViewOrientation3D o = v3.GetOrientation();
                view3d = new
                {
                    v3.IsPerspective,
                    v3.IsSectionBoxActive,
                    SectionBox = v3.IsSectionBoxActive ? CmBox(v3.GetSectionBox()) : null,
                    EyeMm = CmPoint(o.EyePosition),
                    Forward = CmVector(o.ForwardDirection),
                    Up = CmVector(o.UpDirection),
                    PerspectiveFarClip = farClip
                };
            }

            // 遠裁剪（剖面／立面等）
            object farClipping = new
            {
                Mode = ParamText(BuiltInParameter.VIEWER_BOUND_FAR_CLIPPING),
                OffsetFar = ParamText(BuiltInParameter.VIEWER_BOUND_OFFSET_FAR)
            };

            // 視圖樣板
            object template = null;
            try
            {
                if (view.ViewTemplateId != null && view.ViewTemplateId != ElementId.InvalidElementId)
                {
                    Element t = doc.GetElement(view.ViewTemplateId);
                    template = new { Id = view.ViewTemplateId.GetIdValue(), Name = t?.Name };
                }
            }
            catch { }

            // 隱藏類別（主類別＋子類別）
            var hidden = new List<string>();
            try
            {
                foreach (Category cat in doc.Settings.Categories)
                {
                    if (cat == null) continue;
                    try
                    {
                        if (view.CanCategoryBeHidden(cat.Id) && view.GetCategoryHidden(cat.Id)) hidden.Add(cat.Name);
                    }
                    catch { }
                    try
                    {
                        foreach (Category sub in cat.SubCategories)
                        {
                            try
                            {
                                if (view.CanCategoryBeHidden(sub.Id) && view.GetCategoryHidden(sub.Id)) hidden.Add(cat.Name + " › " + sub.Name);
                            }
                            catch { }
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex) { hidden.Add("(讀取失敗：" + ex.Message + ")"); }
            hidden.Sort(StringComparer.Ordinal);

            // 連結顯示設定（Revit 2024+ API）
            var links = new List<object>();
            try
            {
                foreach (RevitLinkInstance li in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
                {
#if REVIT2024_OR_GREATER
                    string vis = null, linkedView = null;
                    try
                    {
                        RevitLinkGraphicsSettings s = view.GetLinkOverrides(li.Id);
                        if (s != null)
                        {
                            vis = s.LinkVisibilityType.ToString();
                            if (s.LinkedViewId != null && s.LinkedViewId != ElementId.InvalidElementId) linkedView = s.LinkedViewId.GetIdValue().ToString();
                        }
                        else vis = "(無取代＝依主體視圖預設)";
                    }
                    catch (Exception ex) { vis = "(讀取失敗：" + ex.Message + ")"; }
                    bool? linkHidden = null;
                    try { linkHidden = li.IsHidden(view); } catch { }
                    links.Add(new { Id = li.Id.GetIdValue(), li.Name, Visibility = vis, LinkedViewId = linkedView, HiddenInView = linkHidden });
#else
                    links.Add(new { Id = li.Id.GetIdValue(), li.Name, Visibility = "(Revit 2023 以前 API 不支援讀取)" });
#endif
                }
            }
            catch (Exception ex) { links.Add(new { Error = ex.Message }); }

            // 視圖專屬元素計數
            int detailLines = 0, filledRegions = 0, textNotes = 0;
            try
            {
                detailLines = new FilteredElementCollector(doc, view.Id).OfClass(typeof(CurveElement))
                    .Cast<CurveElement>().Count(c => c.OwnerViewId == view.Id && c.CurveElementType == CurveElementType.DetailCurve);
                filledRegions = new FilteredElementCollector(doc, view.Id).OfClass(typeof(FilledRegion))
                    .Count(e => e.OwnerViewId == view.Id);
                textNotes = new FilteredElementCollector(doc, view.Id).OfClass(typeof(TextNote))
                    .Count(e => e.OwnerViewId == view.Id);
            }
            catch (Exception ex) { Logger.Error("get_view_extents：計數視圖專屬元素失敗", ex); }

            string detailLevel = null, displayStyle = null;
            try { detailLevel = view.DetailLevel.ToString(); } catch { }
            try { displayStyle = view.DisplayStyle.ToString(); } catch { }

            return new
            {
                Success = true,
                Id = view.Id.GetIdValue(),
                view.Name,
                ViewType = view.ViewType.ToString(),
                Scale = SafeScale(view),
                IsTemplate = view.IsTemplate,
                Note = "長度為內部原點座標系 mm；Box 的 Min/Max 是視圖自身座標（套 Transform 得世界座標）。",
                CropBox = crop,
                AnnotationCrop = annotationCrop,
                View3D = view3d,
                FarClipping = farClipping,
                DisplayModel = ParamText(BuiltInParameter.VIEW_MODEL_DISPLAY_MODE),
                DetailLevel = detailLevel,
                DisplayStyle = displayStyle,
                ViewTemplate = template,
                HiddenCategoryCount = hidden.Count,
                HiddenCategories = hidden,
                Links = links,
                ViewOwnedElements = new { DetailLines = detailLines, FilledRegions = filledRegions, TextNotes = textNotes }
            };
        }
    }
}
