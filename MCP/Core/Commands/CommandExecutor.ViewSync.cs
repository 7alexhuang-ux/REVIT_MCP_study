using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCP.Core.ViewSync;
using RevitMCP.Core.Walkthrough;

namespace RevitMCP.Core
{
    /// <summary>
    /// 網頁 3D 視角／剖面 → Revit 聯動（issue-manager D-710，規格 docs/spec/revit-viewsync.md §3、§4、§6）。
    /// 瀏覽器只能呼叫 viewsync_status／apply_temp／open_view／create_view（SocketService 授權後才進到這裡）；
    /// viewsync_debug_fov 只開給沒有 Origin 的本機呼叫（實測校正透視視野用）。
    /// 純計算在 Core/ViewSync/ViewSyncMath.cs（有單元測試）；這裡只做 Revit API 呼叫。
    /// 一律：交易失敗整筆回復、不刪既有視圖；錯誤用 ViewSyncException(code, message)。
    /// </summary>
    public partial class CommandExecutor
    {
        private const double ViewSyncFtToM = ViewSyncMath.FeetToMeters;

        // ------------------------------------------------------------------ viewsync_status

        private object ViewSyncStatus(JObject p)
        {
            var result = new JObject
            {
                ["protocol"] = 1,
                ["revitVersion"] = _uiApp.Application.VersionNumber,
            };
            var uiDoc = _uiApp.ActiveUIDocument;
            if (uiDoc == null)
            {
                result["hasDocument"] = false;
                return result;
            }

            var doc = uiDoc.Document;
            var info = ViewSyncModelInfo.FromDocument(doc);
            result["hasDocument"] = true;
            result["isProjectDocument"] = !doc.IsFamilyDocument;
            result["model"] = new JObject
            {
                ["title"] = info.Title,
                ["guid"] = info.Guid,
            };
            // 本機路徑只回給沒有 Origin 的本機連線；瀏覽器連線（SocketService 加 __browser）不回。
            if (p["__browser"]?.Type != JTokenType.Boolean || !p["__browser"].Value<bool>())
            {
                ((JObject)result["model"])["path"] = info.Path;
                ((JObject)result["model"])["centralPath"] = ViewSyncModelInfo.CentralPath(doc);
            }
            result["isReadOnly"] = doc.IsReadOnly;
            result["isWorkshared"] = doc.IsWorkshared;
            var active = uiDoc.ActiveView;
            result["activeView"] = active == null ? null : new JObject
            {
                ["name"] = active.Name,
                ["uniqueId"] = active.UniqueId,
                ["viewType"] = active.ViewType.ToString(),
            };

            var resolved = ViewSyncResolveOrigin(doc);
            var known = resolved.Origin;
            result["origin"] = new JObject
            {
                ["offsetMeters"] = known.HasValue ? new JArray(known.Value.X, known.Value.Y, known.Value.Z) : null,
                ["source"] = resolved.Source,
                ["fromRegistry"] = resolved.FromRegistry,
            };

            // 請求順便帶 model／originOffsetMeters 時，回報核對結果（網頁可在送出前先檢查）。
            var req = ViewSyncRequestParser.ParseCommon(p);
            result["check"] = new JObject
            {
                ["model"] = ViewSyncMath.CompareModel(req.ModelTitle, req.ModelGuid, info.Title, info.Guid).ToString(),
                ["origin"] = req.HasOrigin ? ViewSyncOriginVerdict(req.OriginOffsetMeters, resolved) : "Unspecified",
            };
            return result;
        }

        // ------------------------------------------------------------------ viewsync_apply_temp

        private object ViewSyncApplyTemp(JObject p)
        {
            var req = ViewSyncRequestParser.ParseApplyTemp(p);
            var uiDoc = ViewSyncRequireDocument(true);
            var doc = uiDoc.Document;
            var warnings = new List<string>();
            ViewSyncCheckModelAndOrigin(req, doc, warnings);

            var ortho = req.Camera.Orthographic;
            var cam = ViewSyncMath.ComputeCamera(req.Camera.Position, req.Camera.Target, req.Camera.Up, req.OriginOffsetMeters);
            var clip = req.Clipping.Enabled ? ViewSyncMath.ComputeClipBox(req.Clipping.Min.Value, req.Clipping.Max.Value, req.OriginOffsetMeters) : null;

            var view = new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>()
                .FirstOrDefault(v => !v.IsTemplate && v.IsPerspective == !ortho && ViewSyncMarker.IsMarked(v, ViewSyncMarker.TempKind));
            if (view != null) ViewSyncEnsureEditable(doc, view.Id, "臨時對位視圖");

            var created = false;
            using (var tx = new Transaction(doc, "IM 對位（臨時）"))
            {
                tx.Start();
                try
                {
                    if (view == null)
                    {
                        view = ViewSyncCreate3D(doc, ortho);
                        view.Name = ViewSyncMath.UniqueName(ViewSyncMath.TempViewName(ortho), ViewSyncAllViewNames(doc));
                        ViewSyncMarker.Mark(view, ViewSyncMarker.TempKind);
                        created = true;
                    }

                    ViewSyncApplyCamera(view, cam, clip, req.Camera, warnings);
                    ViewSyncApplyDisplayMode(view, req.Mode, warnings);
                    ViewSyncApplyVisibility(doc, view, req, true, warnings);
                    tx.Commit();
                }
                catch
                {
                    if (tx.GetStatus() == TransactionStatus.Started) tx.RollBack();
                    throw;
                }
            }

            var activated = ViewSyncActivate(uiDoc, view, warnings);
            return new JObject
            {
                ["viewUniqueId"] = view.UniqueId,
                ["viewId"] = view.Id.GetIdValue(),
                ["name"] = view.Name,
                ["created"] = created,
                ["activated"] = activated,
                ["orthographic"] = ortho,
                ["sectionBoxActive"] = view.IsSectionBoxActive,
                ["warnings"] = new JArray(warnings),
            };
        }

        // ------------------------------------------------------------------ viewsync_open_view

        private object ViewSyncOpenView(JObject p)
        {
            var req = ViewSyncRequestParser.ParseOpenView(p);
            var uiDoc = ViewSyncRequireDocument(false);
            var doc = uiDoc.Document;
            var warnings = new List<string>();
            if (!string.IsNullOrWhiteSpace(req.ModelTitle) || !string.IsNullOrWhiteSpace(req.ModelGuid))
                ViewSyncCheckModel(req, doc, warnings);

            var view = doc.GetElement(req.ViewUniqueId) as View;
            if (view == null || view.IsTemplate)
                throw new ViewSyncException("view_not_found", "Revit 找不到這個視圖（可能已被刪除或改版重建）。");
            if (view.ViewType == ViewType.ProjectBrowser || view.ViewType == ViewType.SystemBrowser || view.ViewType == ViewType.Internal)
                throw new ViewSyncException("view_not_found", "這不是可以開啟的視圖。");

            var activated = ViewSyncActivate(uiDoc, view, warnings);
            if (!activated) throw new ViewSyncException("activate_failed", warnings.LastOrDefault() ?? "無法切換視圖。");
            return new JObject
            {
                ["viewUniqueId"] = view.UniqueId,
                ["name"] = view.Name,
                ["viewType"] = view.ViewType.ToString(),
                ["activated"] = true,
                ["warnings"] = new JArray(warnings),
            };
        }

        // ------------------------------------------------------------------ viewsync_create_view

        private object ViewSyncCreateView(JObject p)
        {
            var req = ViewSyncRequestParser.ParseCreateView(p);
            var uiDoc = ViewSyncRequireDocument(true);
            var doc = uiDoc.Document;
            var warnings = new List<string>();
            ViewSyncCheckModelAndOrigin(req, doc, warnings);

            var desired = ViewSyncMath.SanitizeViewName(req.Name);
            var name = ViewSyncMath.UniqueName(desired, ViewSyncAllViewNames(doc));
            if (name != req.Name) warnings.Add($"視圖名稱調整為「{name}」（原「{req.Name}」含不合法字元或與既有視圖重複）。");

            // 交易前先算完所有純計算（失敗不碰文件）。
            CameraSolution cam = null; ClipBoxSolution clip = null; SectionFrame frame = null;
            if (req.Kind == "3d")
            {
                cam = ViewSyncMath.ComputeCamera(req.Camera.Position, req.Camera.Target, req.Camera.Up, req.OriginOffsetMeters);
                if (req.Clipping.Enabled) clip = ViewSyncMath.ComputeClipBox(req.Clipping.Min.Value, req.Clipping.Max.Value, req.OriginOffsetMeters);
            }
            else if (req.Kind == "section")
            {
                var pl = req.Plane;
                frame = ViewSyncMath.ComputeSectionFrame(pl.Origin, pl.Normal, pl.Up, pl.Right, pl.Width, pl.Height, pl.Depth, req.OriginOffsetMeters);
            }
            else if (req.Clipping.Enabled)
            {
                clip = ViewSyncMath.ComputeClipBox(req.Clipping.Min.Value, req.Clipping.Max.Value, req.OriginOffsetMeters);
            }

            View view;
            string levelName = null;
            using (var tx = new Transaction(doc, "IM 建立視圖"))
            {
                tx.Start();
                try
                {
                    if (req.Kind == "3d")
                    {
                        var v3 = ViewSyncCreate3D(doc, req.Camera.Orthographic);
                        v3.Name = name;
                        ViewSyncApplyCamera(v3, cam, clip, req.Camera, warnings);
                        view = v3;
                    }
                    else if (req.Kind == "section")
                    {
                        view = ViewSyncCreateSection(doc, frame, name);
                    }
                    else
                    {
                        view = ViewSyncCreatePlan(doc, req, clip, name, warnings, out levelName);
                    }

                    ViewSyncApplyDisplayMode(view, req.Mode, warnings);
                    ViewSyncApplyVisibility(doc, view, req, false, warnings);
                    tx.Commit();
                }
                catch
                {
                    if (tx.GetStatus() == TransactionStatus.Started) tx.RollBack();
                    throw;
                }
            }

            var activated = ViewSyncActivate(uiDoc, view, warnings);
            return new JObject
            {
                ["kind"] = req.Kind,
                ["viewUniqueId"] = view.UniqueId,
                ["viewId"] = view.Id.GetIdValue(),
                ["name"] = view.Name,
                ["requestedName"] = req.Name,
                ["renamed"] = view.Name != req.Name,
                ["levelName"] = levelName,
                ["activated"] = activated,
                ["warnings"] = new JArray(warnings),
            };
        }

        private static View ViewSyncCreateSection(Document doc, SectionFrame frame, string name)
        {
            var type = ViewSyncFindViewFamilyType(doc, ViewFamily.Section);
            var t = Transform.Identity;
            t.Origin = new XYZ(frame.OriginFeet.X, frame.OriginFeet.Y, frame.OriginFeet.Z);
            t.BasisX = ViewSyncXyz(frame.BasisX);
            t.BasisY = ViewSyncXyz(frame.BasisY);
            t.BasisZ = ViewSyncXyz(frame.BasisZ);
            var box = new BoundingBoxXYZ { Transform = t, Min = ViewSyncXyz(frame.LocalMinFeet), Max = ViewSyncXyz(frame.LocalMaxFeet) };
            var section = ViewSection.CreateSection(doc, type.Id, box);
            section.Name = name;
            return section;
        }

        private static View ViewSyncCreatePlan(Document doc, ViewSyncRequest req, ClipBoxSolution clip, string name,
            List<string> warnings, out string levelName)
        {
            var ceiling = req.Kind == "ceiling";
            var type = ViewSyncFindViewFamilyType(doc, ceiling ? ViewFamily.CeilingPlan : ViewFamily.FloorPlan);
            // cutHeightMeters＝世界 y（glTF 公尺、已扣原點）→ 只加一次原點 Z。
            var cutFeet = ViewSyncMath.CutHeightToRevitFeet(req.CutHeightMeters.Value, req.OriginOffsetMeters.Z);

            Level level = null;
            if (!string.IsNullOrWhiteSpace(req.LevelUniqueId)) level = doc.GetElement(req.LevelUniqueId) as Level;
            if (level == null)
            {
                var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                    .Select(l => new LevelRef { UniqueId = l.UniqueId, Name = l.Name, ElevationFeet = l.ProjectElevation }).ToList(); // 與相機管理一致用 ProjectElevation
                var picked = ViewSyncMath.PickLevel(levels, cutFeet, out var belowAll);
                if (picked == null) throw new ViewSyncException("no_level", "專案裡沒有樓層，無法建立平面視圖。");
                if (belowAll) warnings.Add($"切高低於所有樓層，改用最低樓層「{picked.Name}」。");
                level = (Level)doc.GetElement(picked.UniqueId);
            }

            levelName = level.Name;
            var plan = ViewPlan.Create(doc, type.Id, level.Id);
            plan.Name = name;

            // 視圖範圍：四個平面都明確綁到這個樓層，切高＝網頁切高 − 樓層高程（Revit 要求 top ≥ cut ≥ bottom ≥ depth）。
            // 偏移以 ProjectElevation 為基準（與 CameraKeyPlanService 一致）；ProjectElevation 與 Elevation 的差距（專案基準點）待實機核對。
            var cutOffset = cutFeet - level.ProjectElevation;
            PlanRangeOffsets fit;
            if (ceiling)
            {
                var upper = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                    .Where(l => l.ProjectElevation > level.ProjectElevation + 1e-6)
                    .OrderBy(l => l.ProjectElevation).FirstOrDefault();
                fit = ViewSyncMath.FitCeilingRange(cutOffset, upper == null ? (double?)null : upper.ProjectElevation - level.ProjectElevation);
            }
            else
            {
                fit = ViewSyncMath.FitPlanRange(0, 0, 0, 0, cutOffset);
            }
            try
            {
                var range = plan.GetViewRange();
                foreach (var plane in new[] { PlanViewPlane.TopClipPlane, PlanViewPlane.CutPlane, PlanViewPlane.BottomClipPlane, PlanViewPlane.ViewDepthPlane })
                    range.SetLevelId(plane, level.Id);
                range.SetOffset(PlanViewPlane.TopClipPlane, fit.Top);
                range.SetOffset(PlanViewPlane.CutPlane, fit.Cut);
                range.SetOffset(PlanViewPlane.BottomClipPlane, fit.Bottom);
                range.SetOffset(PlanViewPlane.ViewDepthPlane, fit.ViewDepth);
                plan.SetViewRange(range);
            }
            catch (Exception ex)
            {
                // 視圖範圍不合法不能讓整筆交易回復：保留 Revit 預設範圍並提醒。
                warnings.Add("視圖範圍設定失敗，已保留預設範圍：" + ex.Message);
            }

            if (clip != null)
            {
                try
                {
                    plan.CropBoxActive = true;
                    var crop = plan.CropBox;
                    var inv = crop.Transform.Inverse;
                    var a = inv.OfPoint(new XYZ(clip.MinFeet.X, clip.MinFeet.Y, level.ProjectElevation));
                    var b = inv.OfPoint(new XYZ(clip.MaxFeet.X, clip.MaxFeet.Y, level.ProjectElevation));
                    crop.Min = new XYZ(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), crop.Min.Z);
                    crop.Max = new XYZ(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), crop.Max.Z);
                    plan.CropBox = crop;
                }
                catch (Exception ex)
                {
                    warnings.Add("平面裁剪框設定失敗，已保留預設範圍：" + ex.Message);
                }
            }

            return plan;
        }

        // ------------------------------------------------------------------ viewsync_debug_fov（只給沒有 Origin 的本機呼叫）

        /// <summary>
        /// 透視視野校正：讀出 3D 視圖的裁剪框（Transform／Min／Max）與在各種假設下隱含的 FOV；
        /// 帶 set = { fovVerticalDeg, aspect, model: "focal"（預設）|"target"|"near" } 時，在該視圖（預設作用中視圖）套用一次再讀回。
        /// 參數 viewUniqueId 可指定視圖，targetDistanceMeters 可指定目標距離（缺省取裁剪框遠近平面中點）。
        /// </summary>
        private object ViewSyncDebugFov(JObject p)
        {
            var uiDoc = ViewSyncRequireDocument(false);
            var doc = uiDoc.Document;
            var id = p["viewUniqueId"]?.Value<string>();
            var view = (string.IsNullOrWhiteSpace(id) ? uiDoc.ActiveView : doc.GetElement(id)) as View3D;
            if (view == null) throw new ViewSyncException("view_not_found", "找不到 3D 視圖（缺省使用作用中視圖，必須是 3D）。");
            var targetM = p["targetDistanceMeters"]?.Value<double?>();

            JObject before = ViewSyncDescribeCrop(view, targetM);
            var set = p["set"] as JObject;
            JObject after = null;
            string setNote = null;
            if (set != null)
            {
                var fov = set["fovVerticalDeg"]?.Value<double>() ?? 60;
                var aspect = set["aspect"]?.Value<double>() ?? 16.0 / 9.0;
                var model = ViewSyncRequestParser.ParseFovModel(set["model"]?.Value<string>());
                var targetFeet = targetM.HasValue ? targetM.Value / ViewSyncFtToM : 30.0;
                using (var tx = new Transaction(doc, "IM 透視視野實測"))
                {
                    tx.Start();
                    try
                    {
                        setNote = ViewSyncSetPerspectiveCrop(view, fov, aspect, targetFeet, model);
                        tx.Commit();
                    }
                    catch
                    {
                        if (tx.GetStatus() == TransactionStatus.Started) tx.RollBack();
                        throw;
                    }
                }

                after = ViewSyncDescribeCrop(view, targetM);
            }

            return new JObject { ["viewUniqueId"] = view.UniqueId, ["name"] = view.Name, ["before"] = before, ["after"] = after, ["setNote"] = setNote };
        }

        private static JObject ViewSyncDescribeCrop(View3D view, double? targetMeters)
        {
            var o = view.GetOrientation();
            var crop = view.CropBox;
            var o3 = new JObject
            {
                ["isPerspective"] = view.IsPerspective,
                ["cropBoxActive"] = view.CropBoxActive,
                ["eyeFeet"] = new JArray(o.EyePosition.X, o.EyePosition.Y, o.EyePosition.Z),
                ["forward"] = new JArray(o.ForwardDirection.X, o.ForwardDirection.Y, o.ForwardDirection.Z),
                ["up"] = new JArray(o.UpDirection.X, o.UpDirection.Y, o.UpDirection.Z),
                ["cropMin"] = new JArray(crop.Min.X, crop.Min.Y, crop.Min.Z),
                ["cropMax"] = new JArray(crop.Max.X, crop.Max.Y, crop.Max.Z),
                ["cropOriginFeet"] = new JArray(crop.Transform.Origin.X, crop.Transform.Origin.Y, crop.Transform.Origin.Z),
            };
            if (view.IsPerspective)
            {
                var near = -crop.Max.Z; var far = -crop.Min.Z;
                var target = targetMeters.HasValue ? targetMeters.Value / ViewSyncFtToM : (near + far) / 2;
                o3["targetDistanceFeetUsed"] = target;
                // 同一個裁剪框，用不同距離當分母會得到不同 FOV；對照畫面實際視野就知道 Revit 用的是哪個。
                // Revit 實際視野（裁剪形狀換算，2026-10-31 實測與 0.1 ft 焦距平面一致）。
                var actual = CameraMath.ComputePerspectiveFov(view);
                o3["revitFov"] = new JObject
                {
                    ["source"] = actual?.Source,
                    ["verticalDeg"] = actual?.VFovDeg,
                    ["horizontalDeg"] = actual?.HFovDeg,
                };
                o3["impliedFovVerticalDeg"] = new JObject
                {
                    ["ifRectangleAtFocalPlane"] = ViewSyncMath.ImpliedFovDegrees(crop.Max.Y, ViewSyncMath.RevitPerspectiveFocalFeet),
                    ["ifRectangleAtNearPlane"] = ViewSyncMath.ImpliedFovDegrees(crop.Max.Y, near),
                    ["ifRectangleAtFarPlane"] = ViewSyncMath.ImpliedFovDegrees(crop.Max.Y, far),
                    ["ifRectangleAtTargetDistance"] = ViewSyncMath.ImpliedFovDegrees(crop.Max.Y, target),
                };
                o3["impliedFovHorizontalDeg"] = new JObject
                {
                    ["ifRectangleAtFocalPlane"] = ViewSyncMath.ImpliedFovDegrees(crop.Max.X, ViewSyncMath.RevitPerspectiveFocalFeet),
                    ["ifRectangleAtNearPlane"] = ViewSyncMath.ImpliedFovDegrees(crop.Max.X, near),
                    ["ifRectangleAtFarPlane"] = ViewSyncMath.ImpliedFovDegrees(crop.Max.X, far),
                    ["ifRectangleAtTargetDistance"] = ViewSyncMath.ImpliedFovDegrees(crop.Max.X, target),
                };
            }

            return o3;
        }

        // ------------------------------------------------------------------ 共用：文件、核對、視圖

        private UIDocument ViewSyncRequireDocument(bool forWrite)
        {
            var uiDoc = _uiApp.ActiveUIDocument;
            if (uiDoc == null) throw new ViewSyncException("no_document", "Revit 目前沒有開啟的專案。");
            if (uiDoc.Document.IsFamilyDocument) throw new ViewSyncException("not_project_document", "目前開啟的是族群文件，不是專案。");
            if (forWrite && uiDoc.Document.IsReadOnly) throw new ViewSyncException("document_read_only", "這個文件是唯讀的，無法建立或修改視圖。");
            return uiDoc;
        }

        /// <summary>camera.json 的 GLB 原點（add-in 自己知道的值）；沒有檔或讀不到回 null。</summary>
        private static Vec3? ViewSyncKnownOrigin(out string source)
        {
            source = null;
            try
            {
                var path = Path.Combine(Path.GetFullPath(WebCompareDefaultOutputDir), "camera.json");
                if (!File.Exists(path)) return null;
                var camera = JObject.Parse(File.ReadAllText(path));
                var token = camera["gltf"]?["originOffsetMeters"];
                if (token == null) return null;
                source = path;
                return ParseNamedViewsVector(token, "camera.json.gltf.originOffsetMeters");
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void ViewSyncCheckModel(ViewSyncRequest req, Document doc, List<string> warnings)
        {
            var info = ViewSyncModelInfo.FromDocument(doc);
            switch (ViewSyncMath.CompareModel(req.ModelTitle, req.ModelGuid, info.Title, info.Guid))
            {
                case ViewSyncMath.ModelMatch.Mismatch:
                    throw new ViewSyncException("model_mismatch",
                        $"Revit 目前開的是「{info.Title}」，這個網頁模型來自「{(string.IsNullOrWhiteSpace(req.ModelTitle) ? req.ModelGuid : req.ModelTitle)}」。");
                case ViewSyncMath.ModelMatch.TitleOnly:
                    warnings.Add("模型只比對了檔名（網頁 GLB 沒有 Revit 檔識別，未驗證是同一個檔）。");
                    break;
            }
        }

        private static void ViewSyncCheckModelAndOrigin(ViewSyncRequest req, Document doc, List<string> warnings)
        {
            ViewSyncCheckModel(req, doc, warnings);
            var resolved = ViewSyncResolveOrigin(doc);
            var cmp = ViewSyncMath.CompareOrigin(req.OriginOffsetMeters, resolved.Origin);
            if (cmp == ViewSyncMath.OriginMatch.Match) return;
            if (resolved.FromRegistry)
            {
                throw new ViewSyncException("origin_mismatch",
                    "網頁模型的原點與這份 Revit 文件最近一次匯出的原點不同（網頁模型可能已過期），請重新匯出並重新上傳模型。");
            }
            // 沒有這份文件自己的匯出紀錄：預設資料夾的 camera.json 可能是別的案，只警告、不擋。
            warnings.Add("origin_unverified：找不到這份文件的匯出原點紀錄（export_origins.json），無法核對網頁模型原點（未驗證）。"
                + "可先重新匯出，或用 viewsync_register_origin 補登。");
        }

        private static string ViewSyncOriginVerdict(Vec3 requested, ResolvedOrigin resolved)
        {
            var cmp = ViewSyncMath.CompareOrigin(requested, resolved.Origin);
            if (cmp == ViewSyncMath.OriginMatch.Match) return "Match";
            if (resolved.FromRegistry) return "Mismatch";
            return "Unverified"; // origin_unverified：無本文件紀錄時不下 Mismatch 結論
        }

        /// <summary>核對用原點：這份文件的匯出登錄優先；找不到才退回預設資料夾 camera.json（只當警告用）。</summary>
        private static ResolvedOrigin ViewSyncResolveOrigin(Document doc)
        {
            var info = ViewSyncModelInfo.FromDocument(doc);
            return ExportOriginRegistry.Resolve(ExportOriginRegistry.DefaultPath, info.Guid, info.Path, () =>
            {
                var o = ViewSyncKnownOrigin(out var src);
                return (o, src);
            });
        }

        /// <summary>匯出成功後登錄這份文件的 GLB 原點；失敗只記 log，不影響匯出。</summary>
        internal static void ViewSyncRecordExportOrigin(Document doc, Vec3 offset, string outputDir)
        {
            try
            {
                var info = ViewSyncModelInfo.FromDocument(doc);
                ExportOriginRegistry.Upsert(ExportOriginRegistry.DefaultPath, new ExportOriginEntry
                {
                    DocumentPath = info.Path,
                    DocumentGuid = info.Guid,
                    Title = info.Title,
                    OriginOffsetMeters = new[] { offset.X, offset.Y, offset.Z },
                    OutputDir = outputDir,
                    ExportedAt = DateTime.UtcNow.ToString("o"),
                });
            }
            catch (Exception) { }
        }

        // ------------------------------------------------------------------ viewsync_register_origin（只給沒有 Origin 的本機呼叫）

        /// <summary>補登目前作用中文件的 GLB 原點（例如 GLB 是別次匯出的）。參數 originOffsetMeters [x,y,z]、outputDir（選填）。</summary>
        private object ViewSyncRegisterOrigin(JObject p)
        {
            var uiDoc = ViewSyncRequireDocument(false);
            var token = p["originOffsetMeters"];
            if (token == null) throw new ViewSyncException("bad_request", "缺少 originOffsetMeters（[x,y,z] 公尺）。");
            var offset = ParseNamedViewsVector(token, "originOffsetMeters");
            var outputDir = p["outputDir"]?.Value<string>() ?? "(manual)";
            ViewSyncRecordExportOrigin(uiDoc.Document, offset, outputDir);
            var info = ViewSyncModelInfo.FromDocument(uiDoc.Document);
            var saved = ExportOriginRegistry.Find(ExportOriginRegistry.DefaultPath, info.Guid, info.Path);
            if (saved == null) throw new ViewSyncException("register_failed", "寫入登錄檔失敗：" + ExportOriginRegistry.DefaultPath);
            return new JObject
            {
                ["registered"] = true,
                ["registryPath"] = ExportOriginRegistry.DefaultPath,
                ["model"] = new JObject { ["title"] = info.Title, ["path"] = info.Path, ["guid"] = info.Guid },
                ["originOffsetMeters"] = new JArray(saved.OriginOffsetMeters[0], saved.OriginOffsetMeters[1], saved.OriginOffsetMeters[2]),
                ["outputDir"] = saved.OutputDir,
            };
        }

        private static void ViewSyncEnsureEditable(Document doc, ElementId id, string what)
        {
            if (!doc.IsWorkshared) return;
            var status = WorksharingUtils.GetCheckoutStatus(doc, id, out var owner);
            if (status == CheckoutStatus.OwnedByOtherUser)
                throw new ViewSyncException("view_not_editable", $"{what}目前被「{owner}」借用，無法修改。");
        }

        private static List<string> ViewSyncAllViewNames(Document doc) =>
            new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Select(v => v.Name).ToList();

        private static ViewFamilyType ViewSyncFindViewFamilyType(Document doc, ViewFamily family)
        {
            var type = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                .OrderBy(t => t.Name, StringComparer.Ordinal).FirstOrDefault(t => t.ViewFamily == family);
            if (type == null) throw new ViewSyncException("view_type_missing", $"專案裡沒有可用的視圖類型（{family}）。");
            return type;
        }

        private static View3D ViewSyncCreate3D(Document doc, bool orthographic)
        {
            var type = ViewSyncFindViewFamilyType(doc, ViewFamily.ThreeDimensional);
            return orthographic ? View3D.CreateIsometric(doc, type.Id) : View3D.CreatePerspective(doc, type.Id);
        }

        private static XYZ ViewSyncXyz(Vec3 v) => new XYZ(v.X, v.Y, v.Z);

        private static bool ViewSyncActivate(UIDocument uiDoc, View view, List<string> warnings)
        {
            try
            {
                uiDoc.ActiveView = view;
                return true;
            }
            catch (Exception ex)
            {
                warnings.Add("已完成，但無法切換到該視圖：" + ex.Message);
                return false;
            }
        }

        /// <summary>相機、剖面框、透視視野（需在交易內）。</summary>
        private static void ViewSyncApplyCamera(View3D view, CameraSolution cam, ClipBoxSolution clip, ViewSyncCamera input, List<string> warnings)
        {
            if (view.IsLocked) view.Unlock();
            if (view.ViewTemplateId != ElementId.InvalidElementId) view.ViewTemplateId = ElementId.InvalidElementId;

            view.SetOrientation(new ViewOrientation3D(ViewSyncXyz(cam.EyeFeet), ViewSyncXyz(cam.Up), ViewSyncXyz(cam.Forward)));
            if (clip != null)
            {
                view.IsSectionBoxActive = true;
                view.SetSectionBox(new BoundingBoxXYZ { Min = ViewSyncXyz(clip.MinFeet), Max = ViewSyncXyz(clip.MaxFeet) });
            }
            else
            {
                view.IsSectionBoxActive = false;
            }

            if (!view.IsPerspective) return;
            if (!input.FovVerticalDeg.HasValue)
            {
                warnings.Add("網頁沒有送 fovVerticalDeg，透視視野由 Revit 決定，可能與網頁不同。");
                return;
            }

            try
            {
                var aspect = input.Aspect ?? 16.0 / 9.0;
                if (!input.Aspect.HasValue) warnings.Add("網頁沒有送畫面寬高比 camera.aspect，視野以 16:9 換算。");
                var msg = ViewSyncSetPerspectiveCrop(view, input.FovVerticalDeg.Value, aspect, cam.TargetDistanceFeet, input.FovModel);
                if (msg != null) warnings.Add(msg);
            }
            catch (Exception ex)
            {
                warnings.Add("透視視野設定失敗，視野由 Revit 決定：" + ex.Message);
            }
        }

        /// <summary>
        /// 設定透視視野（需在交易內）。FocalPlane（預設）套用後 Regenerate，以 CameraMath.ComputePerspectiveFov（裁剪形狀＝Revit 實際視野）讀回；
        /// 差超過 0.2° 就依 tan 比例縮放 X／Y 再套一次（既有視圖焦距可能不是 0.1 ft）。回傳給使用者看的說明（null＝不需說明）。
        /// </summary>
        private static string ViewSyncSetPerspectiveCrop(View3D view, double fovVerticalDeg, double aspect, double targetDistanceFeet, FovCropModel model)
        {
            if (!view.IsPerspective) throw new ViewSyncException("fov_invalid", "只有透視視圖可以設定視野。");
            var crop = view.CropBox;
            var near = -crop.Max.Z;
            var far = -crop.Min.Z;
            if (!(near > 1e-6)) near = 1.0;
            if (!(far > near)) far = near * 1000.0;
            far = Math.Max(far, Math.Max(targetDistanceFeet * 4.0, near * 2.0));
            var box = ViewSyncMath.ComputePerspectiveCrop(fovVerticalDeg, aspect, targetDistanceFeet, near, far, model);
            crop.Min = ViewSyncXyz(box.Min);
            crop.Max = ViewSyncXyz(box.Max);
            view.CropBox = crop;
            if (model != FovCropModel.FocalPlane) return $"透視視野以舊換算模型 {model} 套用（已證偽，僅供對照）。";

            var wantH = ViewSyncMath.HorizontalFovDegrees(fovVerticalDeg, aspect);
            CameraMath.PerspectiveFov got;
            try
            {
                view.Document.Regenerate();
                got = CameraMath.ComputePerspectiveFov(view);
            }
            catch (Exception ex)
            {
                return "透視視野已套用，但無法讀回確認：" + ex.Message;
            }

            if (got?.VFovDeg == null || got.HFovDeg == null || got.Source != "C(cropShape)")
                return "透視視野已套用，但讀不到 Revit 實際視角（" + (got?.Source ?? "none") + "），未校正。";
            if (Math.Abs(got.VFovDeg.Value - fovVerticalDeg) <= 0.2 && Math.Abs(got.HFovDeg.Value - wantH) <= 0.2) return null;

            var fixedBox = ViewSyncMath.RescaleCropToFov(box.Min, box.Max, fovVerticalDeg, wantH, got.VFovDeg.Value, got.HFovDeg.Value);
            crop = view.CropBox;
            crop.Min = ViewSyncXyz(fixedBox.Min);
            crop.Max = ViewSyncXyz(fixedBox.Max);
            view.CropBox = crop;
            view.Document.Regenerate();
            var again = CameraMath.ComputePerspectiveFov(view);
            return $"透視視野依 Revit 讀回校正一次：第一次 垂直 {got.VFovDeg:F1}°／水平 {got.HFovDeg:F1}°，" +
                   $"校正後 垂直 {again?.VFovDeg:F1}°／水平 {again?.HFovDeg:F1}°（目標 {fovVerticalDeg:F1}°／{wantH:F1}°）。";
        }

        private static void ViewSyncApplyDisplayMode(View view, string mode, List<string> warnings)
        {
            if (string.IsNullOrWhiteSpace(mode)) return;
            var parsed = ViewSyncMath.ParseDisplayMode(mode);
            if (parsed == ViewSyncDisplayMode.None)
            {
                warnings.Add($"無法辨識的顯示模式「{mode}」，未變更視覺樣式。");
                return;
            }

            try
            {
                if (!view.HasDisplayStyle() || !view.CanModifyDisplayStyle())
                {
                    warnings.Add("這個視圖不能修改視覺樣式。");
                    return;
                }

                switch (parsed)
                {
                    case ViewSyncDisplayMode.FlatColors: view.DisplayStyle = DisplayStyle.FlatColors; break;
                    case ViewSyncDisplayMode.Shading: view.DisplayStyle = DisplayStyle.Shading; break;
                    case ViewSyncDisplayMode.Wireframe: view.DisplayStyle = DisplayStyle.Wireframe; break;
                    case ViewSyncDisplayMode.HiddenLine: view.DisplayStyle = DisplayStyle.HLR; break;
                    case ViewSyncDisplayMode.Realistic: view.DisplayStyle = DisplayStyle.Realistic; break;
                }
            }
            catch (Exception ex)
            {
                warnings.Add("視覺樣式設定失敗：" + ex.Message);
            }
        }

        // ------------------------------------------------------------------ 隱藏元件、圖層、透明度、設計選項

        /// <summary>
        /// 套用隱藏元件（hiddenUniqueIds）、連結圖層開關與透明度（layers）、設計選項（designOptions）。需在交易內。
        /// temporary＝true（臨時對位視圖）：用「暫時隱藏」（不存檔），並每次重設連結實例與模型類別的圖形取代；
        /// false（新建視圖）：永久「在視圖中隱藏」。
        /// </summary>
        private static void ViewSyncApplyVisibility(Document doc, View view, ViewSyncRequest req, bool temporary, List<string> warnings)
        {
            // 設計選項：Revit 2024 公開 API 沒有逐視圖設定設計選項可見性的方法（見 DesignOptions.cs 的 DesignOptionVisibilityNote）。
            if (req.DesignOptions.Count > 0)
                warnings.Add($"設計選項（{req.DesignOptions.Count} 組）未套用：Revit 2024 API 沒有逐視圖設定設計選項可見性的方法，請在 Revit「可見性/圖形 > 設計選項」手動設定。");

            var hideIds = new HashSet<ElementId>();

            // 隱藏元件
            var skippedLink = 0; var notFound = 0; var cannotHide = 0;
            foreach (var h in req.Hidden)
            {
                if (h.LinkTitle != null) { skippedLink++; continue; }
                var e = doc.GetElement(h.UniqueId);
                if (e == null) { notFound++; continue; }
                if (!e.CanBeHidden(view)) { cannotHide++; continue; }
                if (!temporary && e.IsHidden(view)) continue;
                hideIds.Add(e.Id);
            }

            if (skippedLink > 0) warnings.Add($"{skippedLink} 個連結模型內的元件無法逐一隱藏（Revit API 只能隱藏主模型元件或整個連結實例）。");
            if (notFound > 0) warnings.Add($"{notFound} 個要隱藏的元件在 Revit 找不到（可能已刪除或網頁模型過期）。");
            if (cannotHide > 0) warnings.Add($"{cannotHide} 個元件在這個視圖不能被隱藏。");

            // 圖層（連結）
            var linkInstances = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>().ToList();
            var overrides = new Dictionary<ElementId, int>(); // 連結實例 → 表面透明度 %
            var hostTransparency = 0;
            foreach (var layer in req.Layers)
            {
                if (string.Equals(layer.Source, RevitGltfExportContext.HostName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(layer.Source, "host", StringComparison.OrdinalIgnoreCase))
                {
                    if (!layer.Visible) warnings.Add("主模型無法整個隱藏（略過）。");
                    else hostTransparency = ViewSyncPercent(layer.Transparency);
                    continue;
                }

                var matches = linkInstances.Where(l => ViewSyncMath.TitleMatches(layer.Source, ViewSyncLinkTitle(l))).ToList();
                if (matches.Count == 0) { warnings.Add($"找不到連結模型「{layer.Source}」，圖層設定略過。"); continue; }
                foreach (var m in matches)
                {
                    if (!layer.Visible)
                    {
                        if (m.CanBeHidden(view)) hideIds.Add(m.Id);
                        else warnings.Add($"連結「{layer.Source}」在這個視圖不能隱藏。");
                    }
                    else
                    {
                        overrides[m.Id] = ViewSyncPercent(layer.Transparency);
                    }
                }
            }

            // 透明度：連結實例（temporary 時每次先全部重設，避免殘留上一次的設定）
            if (temporary || overrides.Count > 0)
            {
                foreach (var l in linkInstances)
                {
                    try
                    {
                        var ogs = new OverrideGraphicSettings();
                        if (overrides.TryGetValue(l.Id, out var pct) && pct > 0) ogs.SetSurfaceTransparency(pct);
                        view.SetElementOverrides(l.Id, ogs);
                    }
                    catch (Exception ex)
                    {
                        warnings.Add($"連結「{ViewSyncLinkTitle(l)}」透明度設定失敗：{ex.Message}");
                    }
                }
            }

            // 透明度：主模型 = 所有模型類別的表面透明度取代
            if (temporary || hostTransparency > 0)
            {
                var applied = 0; var failed = 0;
                foreach (Category cat in doc.Settings.Categories)
                {
                    if (cat == null || cat.CategoryType != CategoryType.Model) continue;
                    try
                    {
                        var ogs = new OverrideGraphicSettings();
                        if (hostTransparency > 0) ogs.SetSurfaceTransparency(hostTransparency);
                        view.SetCategoryOverrides(cat.Id, ogs);
                        applied++;
                    }
                    catch (Exception)
                    {
                        failed++;
                    }
                }

                if (hostTransparency > 0)
                    warnings.Add($"主模型透明度 {hostTransparency}% 已套用到 {applied} 個模型類別（Revit 沒有整個主模型的透明度設定，以逐類別取代代替）"
                        + (failed > 0 ? $"；{failed} 個類別不支援。" : "。"));
            }

            // 套用隱藏
            if (temporary)
            {
                try
                {
                    if (view.IsTemporaryHideIsolateActive()) view.DisableTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate);
                    if (hideIds.Count > 0)
                    {
                        if (!view.CanUseTemporaryVisibilityModes()) throw new InvalidOperationException("這個視圖不能使用暫時隱藏。");
                        view.HideElementsTemporary(hideIds.ToList());
                    }
                }
                catch (Exception ex)
                {
                    warnings.Add("暫時隱藏失敗，元件維持顯示：" + ex.Message);
                }
            }
            else if (hideIds.Count > 0)
            {
                try
                {
                    view.HideElements(hideIds.ToList());
                }
                catch (Exception ex)
                {
                    warnings.Add("在視圖中隱藏元件失敗，元件維持顯示：" + ex.Message);
                }
            }
        }

        /// <summary>0–1 → 0–99（Revit 表面透明度 API 只接受 1–99；0 代表不取代）。</summary>
        private static int ViewSyncPercent(double transparency)
        {
            var pct = (int)Math.Round(transparency * 100.0);
            return Math.Max(0, Math.Min(99, pct));
        }

        private static string ViewSyncLinkTitle(RevitLinkInstance instance)
        {
            try
            {
                var linked = instance.GetLinkDocument();
                if (linked != null) return linked.Title;
            }
            catch (Exception) { /* 連結未載入 */ }

            return instance.Name;
        }
    }
}
