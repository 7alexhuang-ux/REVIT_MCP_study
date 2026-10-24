using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitMCP.Core.Walkthrough;

namespace RevitMCP.Core
{
    /// <summary>
    /// Walkthrough package export: WALK-NN perspective stations -> resampled camera path ->
    /// plan/section images with pixel registration -> per-frame images -> glTF model -> offline viewer.
    /// Data contract: viewer/walkthrough/CONTRACT.md. Human workflow: domain/walkthrough-package.md.
    /// </summary>
    public partial class CommandExecutor
    {
        private const string WalkDefaultPrefix = "WALK-";
        private const string WalkManifestFile = "manifest.json";
        private const string WalkDataJsFile = "data.js";
        private const string WalkModelFile = "model.glb";
        private const double WalkFeetToMeters = CameraPathMath.FeetToMeters;

        private sealed class WalkStationInfo
        {
            public View3D View;
            public CameraStation Station;
            public FieldOfView Fov;
            public string LevelName;
            public double EyeHeightAboveLevel;
        }

        // ────────────────────────────────────────────────
        // get_camera_stations
        // ────────────────────────────────────────────────

        private object GetCameraStations(JObject parameters)
        {
            var doc = _uiApp.ActiveUIDocument.Document;
            var prefix = parameters["stationPrefix"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(prefix))
            {
                prefix = WalkDefaultPrefix;
            }

            var warnings = new List<string>();
            var stations = ReadWalkStations(doc, prefix, warnings, perspectiveOnly: false);

            return new
            {
                stationPrefix = prefix,
                count = stations.Count,
                perspectiveCount = stations.Count(s => s.View.IsPerspective),
                stations = stations.Select(s => new
                {
                    viewId = s.View.Id.GetIdValue(),
                    name = s.View.Name,
                    order = s.Station.Order,
                    isPerspective = s.View.IsPerspective,
                    eye = s.Station.Eye.ToArray(4),
                    forward = s.Station.Forward.ToArray(6),
                    up = s.Station.Up.ToArray(6),
                    fovXDeg = s.Fov.IsValid ? Math.Round(s.Fov.FovXDeg, 3) : (double?)null,
                    fovYDeg = s.Fov.IsValid ? Math.Round(s.Fov.FovYDeg, 3) : (double?)null,
                    aspect = s.Fov.IsValid ? Math.Round(s.Fov.Aspect, 4) : (double?)null,
                    levelName = s.LevelName,
                    eyeHeightAboveLevel = Math.Round(s.EyeHeightAboveLevel, 3)
                }).ToList(),
                units = "m (Revit internal coordinates x 0.3048, no origin offset)",
                warnings
            };
        }

        private List<WalkStationInfo> ReadWalkStations(Document doc, string prefix, List<string> warnings, bool perspectiveOnly)
        {
            var levels = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => l.ProjectElevation)
                .ToList();

            var found = new List<WalkStationInfo>();
            foreach (var view in new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>())
            {
                if (view.IsTemplate || !CameraPathMath.TryParseStationOrder(view.Name, prefix, out var order))
                {
                    continue;
                }

                if (!view.IsPerspective)
                {
                    warnings.Add($"{view.Name} 不是透視視圖（等角 3D），已{(perspectiveOnly ? "略過" : "標記")}；請改用相機工具建立或切換為透視。");
                    if (perspectiveOnly)
                    {
                        continue;
                    }
                }

                ViewOrientation3D orientation;
                try
                {
                    orientation = view.GetOrientation();
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException ex)
                {
                    warnings.Add($"{view.Name} 無法讀取相機方位，已略過：{ex.Message}");
                    continue;
                }

                var eye = orientation.EyePosition;
                var eyeMeters = new Vec3(eye.X * WalkFeetToMeters, eye.Y * WalkFeetToMeters, eye.Z * WalkFeetToMeters);
                var info = new WalkStationInfo
                {
                    View = view,
                    Station = new CameraStation
                    {
                        Name = view.Name,
                        Order = order,
                        Eye = eyeMeters,
                        Forward = WalkToVec3(orientation.ForwardDirection),
                        Up = WalkToVec3(orientation.UpDirection)
                    },
                    Fov = view.IsPerspective ? ComputeViewFov(view, orientation) : new FieldOfView(0, 0, false, false)
                };

                var level = levels.LastOrDefault(l => l.ProjectElevation <= eye.Z + 0.01) ?? levels.FirstOrDefault();
                if (level != null)
                {
                    info.LevelName = level.Name;
                    info.EyeHeightAboveLevel = (eye.Z - level.ProjectElevation) * WalkFeetToMeters;
                }

                if (view.IsPerspective && !info.Fov.IsValid)
                {
                    warnings.Add($"{view.Name} 的裁剪框無法換算視角，將使用預設 50°。");
                }
                else if (view.IsPerspective && !info.Fov.IsSymmetric)
                {
                    warnings.Add($"{view.Name} 的裁剪區域未置中於視線，3D 視窗可能與影格有偏移；建議重設相機或以校準模式修正。");
                }

                found.Add(info);
            }

            found = found.OrderBy(s => s.Station.Order).ThenBy(s => s.View.Name, StringComparer.OrdinalIgnoreCase).ToList();

            for (var i = 1; i < found.Count; i++)
            {
                var previous = found[i - 1].Station;
                var current = found[i].Station;
                if (previous.Order == current.Order)
                {
                    warnings.Add($"{previous.Name} 與 {current.Name} 編號相同，排序可能不如預期。");
                }
                else if (current.Order != previous.Order + 1)
                {
                    warnings.Add($"站號從 {previous.Order} 跳到 {current.Order}，請確認中間沒有漏站。");
                }

                var gap = (current.Eye - previous.Eye).Length;
                if (gap < 0.05)
                {
                    warnings.Add($"{previous.Name} 與 {current.Name} 眼點幾乎重疊（{gap:0.000} m），視角會在一格內瞬間轉向；原地轉身請把兩站錯開至少 0.3 m。");
                }
                else if (gap > 15)
                {
                    warnings.Add($"{previous.Name} 到 {current.Name} 相距 {gap:0.0} m，超過建議的 5–8 m，轉角可能被切。");
                }
            }

            var perspective = found.Where(s => s.View.IsPerspective && s.Fov.IsValid).ToList();
            if (perspective.Count > 1)
            {
                var fovMin = perspective.Min(s => s.Fov.FovYDeg);
                var fovMax = perspective.Max(s => s.Fov.FovYDeg);
                if (fovMax - fovMin > 0.5)
                {
                    warnings.Add($"各站垂直視角不一致（{fovMin:0.0}° – {fovMax:0.0}°）；影格會以第一站視角輸出，請把各站裁剪區域設成相同大小。");
                }

                var heights = perspective.Select(s => s.EyeHeightAboveLevel).ToList();
                if (heights.Max() - heights.Min() > 0.3)
                {
                    warnings.Add($"各站眼高差 {heights.Max() - heights.Min():0.00} m，走動畫面會上下晃動；建議統一眼高。");
                }

                if (perspective.Select(s => s.LevelName).Distinct().Count() > 1)
                {
                    warnings.Add("站點分布在不同樓層；平面視窗只有一張平面圖，跨層段落的標記僅供參考。");
                }
            }

            if (found.Count == 0)
            {
                warnings.Add($"找不到名稱為「{prefix}01」、「{prefix}02」… 的 3D 視圖。請依 domain/walkthrough-package.md 排站。");
            }

            return found;
        }

        private static FieldOfView ComputeViewFov(View3D view, ViewOrientation3D orientation)
        {
            try
            {
                var crop = view.CropBox;
                if (crop == null)
                {
                    return new FieldOfView(0, 0, false, false);
                }

                var eyeLocal = crop.Transform.Inverse.OfPoint(orientation.EyePosition);
                return CameraPathMath.FieldOfViewFromCropBox(WalkToVec3(crop.Min), WalkToVec3(crop.Max), WalkToVec3(eyeLocal));
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                return new FieldOfView(0, 0, false, false);
            }
        }

        // ────────────────────────────────────────────────
        // create_camera_stations（從零在 Revit 建立 WALK-NN 透視相機站點）
        // ────────────────────────────────────────────────

        private object CreateCameraStations(JObject parameters)
        {
            var doc = _uiApp.ActiveUIDocument.Document;
            var warnings = new List<string>();

            var input = parameters["stations"] as JArray;
            if (input == null || input.Count == 0)
            {
                throw new ArgumentException(
                    "stations 為必填：請提供 [{name?, eye:[x,y,z], target:[x,y,z] 或 fwd:[x,y,z]（擇一）, up?:[x,y,z]}] 陣列，座標單位為公尺。");
            }

            var prefix = parameters["stationPrefix"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(prefix))
            {
                prefix = WalkDefaultPrefix;
            }

            var startNumber = parameters["startNumber"]?.Value<int?>() ?? 1;
            if (startNumber < 1)
            {
                throw new ArgumentException("startNumber 必須大於等於 1。");
            }

            var coordinates = parameters["coordinates"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(coordinates))
            {
                coordinates = "revit";
            }

            if (coordinates != "revit" && coordinates != "package")
            {
                throw new ArgumentException("coordinates 只能是 \"revit\"（Revit 專案座標，公尺）或 \"package\"（交付包模型座標，需搭配 packageDir）。");
            }

            var offset = Vec3.Zero;
            if (coordinates == "package")
            {
                var packageDir = parameters["packageDir"]?.Value<string>();
                if (string.IsNullOrWhiteSpace(packageDir))
                {
                    throw new ArgumentException("coordinates=\"package\" 需要同時提供 packageDir，才能取得 manifest 的 originOffset。");
                }

                var manifest = ReadWalkManifest(packageDir);
                offset = WalkReadVec3(manifest["units"]?["originOffset"]);
            }

            var fovYDeg = parameters["fovYDeg"]?.Value<double?>() ?? 50.0;
            var aspect = parameters["aspect"]?.Value<double?>() ?? 1.7778;
            if (fovYDeg <= 0 || fovYDeg >= 170)
            {
                throw new ArgumentException("fovYDeg 必須介於 0 與 170 度之間。");
            }

            if (aspect <= 0)
            {
                throw new ArgumentException("aspect 必須大於 0。");
            }

            var overwrite = parameters["overwrite"]?.Value<bool?>() ?? false;
            var copySourceName = parameters["copySettingsFromViewName"]?.Value<string>();
            View3D copySource = null;
            if (!string.IsNullOrWhiteSpace(copySourceName))
            {
                copySource = FindWalkView<View3D>(doc, copySourceName);
                if (copySource == null)
                {
                    warnings.Add($"找不到名稱為「{copySourceName}」的 3D 視圖，將改用預設 3D 視圖類型新建。");
                }
            }

            var crop = CameraPathMath.CropBoxFromFieldOfView(fovYDeg, aspect);

            var allViews = new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>()
                .Where(v => !v.IsTemplate).ToList();
            var existingNames = new HashSet<string>(allViews.Select(v => v.Name), StringComparer.Ordinal);

            var created = new List<object>();
            var skipped = new List<object>();
            ElementId perspectiveTypeId = null;
            var counter = startNumber;

            using (var group = new TransactionGroup(doc, "走動展示：建立相機站點"))
            {
                group.Start();

                foreach (var raw in input.OfType<JObject>())
                {
                    var name = raw.Value<string>("name");
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        do
                        {
                            name = prefix + counter.ToString("00", CultureInfo.InvariantCulture);
                            counter++;
                        } while (existingNames.Contains(name));
                    }

                    var eyeToken = raw["eye"];
                    if (eyeToken == null)
                    {
                        skipped.Add(new { name, reason = "缺少必填的 eye" });
                        continue;
                    }

                    var hasTarget = raw["target"] != null;
                    var hasFwd = raw["fwd"] != null;
                    if (hasTarget == hasFwd)
                    {
                        skipped.Add(new { name, reason = "target 與 fwd 需擇一提供（不可都給或都不給）" });
                        continue;
                    }

                    var eyeMeters = WalkReadVec3(eyeToken);
                    var fwdMeters = hasFwd ? WalkReadVec3(raw["fwd"]) : WalkReadVec3(raw["target"]) - eyeMeters;
                    if (fwdMeters.Length < 1e-6)
                    {
                        skipped.Add(new { name, reason = "forward 方向為零向量（target 與 eye 重合，或 fwd 為零向量）" });
                        continue;
                    }

                    var upMeters = raw["up"] != null ? WalkReadVec3(raw["up"]) : Vec3.UnitZ;
                    (Vec3 Forward, Vec3 Up) frame;
                    try
                    {
                        frame = CameraPathMath.OrthonormalizeCamera(fwdMeters, upMeters);
                    }
                    catch (ArgumentException ex)
                    {
                        skipped.Add(new { name, reason = $"相機方向無效：{ex.Message}" });
                        continue;
                    }

                    var existingView = allViews.FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.Ordinal));
                    if (existingView != null && !overwrite)
                    {
                        skipped.Add(new { name, reason = "視圖已存在（overwrite=false，未改動）" });
                        continue;
                    }

                    var eyeModel = eyeMeters + offset;
                    var eyeFeet = new XYZ(eyeModel.X / WalkFeetToMeters, eyeModel.Y / WalkFeetToMeters, eyeModel.Z / WalkFeetToMeters);

                    View3D view = null;
                    string failReason = null;
                    using (var tx = new Transaction(doc, $"走動展示：建立站點 {name}"))
                    {
                        tx.Start();
                        try
                        {
                            if (existingView != null)
                            {
                                view = existingView;
                                if (view.IsLocked)
                                {
                                    view.Unlock();
                                }

                                if (!view.IsPerspective)
                                {
                                    if (view.CanToggleBetweenPerspectiveAndIsometric())
                                    {
                                        view.ToggleToPerspective();
                                    }
                                    else
                                    {
                                        failReason = "既有視圖無法切換為透視，未改動";
                                    }
                                }
                            }
                            else if (copySource != null && copySource.CanViewBeDuplicated(ViewDuplicateOption.Duplicate))
                            {
                                view = (View3D)doc.GetElement(copySource.Duplicate(ViewDuplicateOption.Duplicate));
                                if (view.IsLocked)
                                {
                                    view.Unlock();
                                }

                                if (!view.IsPerspective)
                                {
                                    if (view.CanToggleBetweenPerspectiveAndIsometric())
                                    {
                                        view.ToggleToPerspective();
                                    }
                                    else
                                    {
                                        warnings.Add($"複製自「{copySource.Name}」的視圖無法切換為透視，{name} 將維持等角，視角可能不如預期。");
                                    }
                                }
                            }
                            else
                            {
                                if (perspectiveTypeId == null)
                                {
                                    perspectiveTypeId = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType))
                                        .Cast<ViewFamilyType>()
                                        .FirstOrDefault(t => t.ViewFamily == ViewFamily.ThreeDimensional)?.Id;
                                }

                                if (perspectiveTypeId == null)
                                {
                                    failReason = "專案中找不到 3D 視圖類型（ViewFamilyType），無法建立透視視圖。";
                                }
                                else
                                {
                                    view = View3D.CreatePerspective(doc, perspectiveTypeId);
                                }
                            }

                            if (failReason == null && view != null)
                            {
                                view.Name = name;
                                view.SetOrientation(new ViewOrientation3D(eyeFeet, WalkToXyz(frame.Up), WalkToXyz(frame.Forward)));
                                if (view.CanSaveOrientation())
                                {
                                    view.SaveOrientation();
                                }

                                view.CropBoxActive = true;

                                // CropBoxFromFieldOfView returns Min/Max in the *camera* frame
                                // (X right, Y up, Z toward the viewer, eye at the origin) — the
                                // same frame ComputeViewFov reads back through crop.Transform.
                                // A fresh BoundingBoxXYZ carries Transform.Identity, which would
                                // hand Revit those numbers as world coordinates and leave the
                                // rendered frames looking at nothing; take the box Revit already
                                // maintains for this view (its Transform is the camera frame,
                                // valid only after SetOrientation) and swap Min/Max in place.
                                var cropBox = view.CropBox;
                                cropBox.Min = WalkToXyz(crop.Min);
                                cropBox.Max = WalkToXyz(crop.Max);
                                view.CropBox = cropBox;
                            }

                            if (failReason != null)
                            {
                                tx.RollBack();
                            }
                            else
                            {
                                tx.Commit();
                            }
                        }
                        catch (Exception ex) when (!(ex is OutOfMemoryException))
                        {
                            failReason = ex.Message;
                            if (tx.GetStatus() == TransactionStatus.Started)
                            {
                                tx.RollBack();
                            }
                        }
                    }

                    if (failReason != null)
                    {
                        skipped.Add(new { name, reason = failReason });
                        continue;
                    }

                    existingNames.Add(name);
                    allViews.RemoveAll(v => v.Id == view.Id);
                    allViews.Add(view);

                    created.Add(new
                    {
                        name = view.Name,
                        viewId = view.Id.GetIdValue(),
                        eye = eyeMeters.ToArray(4),
                        fwd = frame.Forward.ToArray(6),
                        fovYDeg = Math.Round(fovYDeg, 3),
                        aspect = Math.Round(aspect, 4)
                    });
                }

                group.Assimilate();
            }

            Logger.Info($"[走動展示] create_camera_stations：{created.Count} 個新建/更新、{skipped.Count} 個略過（FOV {fovYDeg:0.#}°、aspect {aspect:0.###}）");

            return new
            {
                stationPrefix = prefix,
                coordinates,
                fovYDeg = Math.Round(fovYDeg, 3),
                aspect = Math.Round(aspect, 4),
                created,
                skipped,
                warnings,
                nextSteps = new[]
                {
                    "get_camera_stations 檢查新站點是否符合排站規則（間距、眼高、跳號、視角一致）",
                    "export_walkthrough_package 建立走動展示交付包"
                }
            };
        }

        // ────────────────────────────────────────────────
        // export_walkthrough_package
        // ────────────────────────────────────────────────

        private object ExportWalkthroughPackage(JObject parameters)
        {
            var doc = _uiApp.ActiveUIDocument.Document;
            var warnings = new List<string>();

            var prefix = parameters["stationPrefix"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(prefix))
            {
                prefix = WalkDefaultPrefix;
            }

            var planViewName = parameters["planViewName"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(planViewName))
            {
                throw new ArgumentException("planViewName 為必填：請提供要放在平面視窗的平面視圖名稱。");
            }

            var sectionViewName = parameters["sectionViewName"]?.Value<string>();
            var framesPerSegment = WalkClamp(parameters["framesPerSegment"]?.Value<int?>() ?? 24, 2, 240);
            var frameWidth = WalkClamp(parameters["frameWidth"]?.Value<int?>() ?? 1280, 320, 3840);

            var planView = FindWalkView<ViewPlan>(doc, planViewName)
                ?? throw new ArgumentException($"找不到名稱為「{planViewName}」的平面視圖（ViewPlan，非樣板）。");
            ViewSection sectionView = null;
            if (!string.IsNullOrWhiteSpace(sectionViewName))
            {
                sectionView = FindWalkView<ViewSection>(doc, sectionViewName)
                    ?? throw new ArgumentException($"找不到名稱為「{sectionViewName}」的剖面視圖（ViewSection，非樣板）。");
            }
            else
            {
                warnings.Add("未指定 sectionViewName，交付包不含剖面視窗。");
            }

            var stations = ReadWalkStations(doc, prefix, warnings, perspectiveOnly: true);
            if (stations.Count < 2)
            {
                throw new InvalidOperationException(
                    $"至少需要 2 個透視站點（目前 {stations.Count} 個）。請建立「{prefix}01」、「{prefix}02」… 透視 3D 視圖，細節見 domain/walkthrough-package.md。");
            }

            var packageDir = CreateWalkPackageDirectory(doc, parameters["outputPath"]?.Value<string>(), parameters["packageName"]?.Value<string>());
            Logger.Info($"[走動展示] 建立交付包：{packageDir}（{stations.Count} 站，每段 {framesPerSegment} 格）");

            // Origin offset: plan crop center (keeps numbers small in the viewer), z = 0.
            var planCrop = planView.CropBox;
            if (!planView.CropBoxActive)
            {
                warnings.Add($"平面視圖「{planView.Name}」未啟用裁剪區域，匯出時會暫時啟用；請確認裁剪框框住走動範圍。");
            }

            var planCenter = planCrop.Transform.OfPoint((planCrop.Min + planCrop.Max) * 0.5);
            var offset = new Vec3(Math.Round(planCenter.X * WalkFeetToMeters, 3), Math.Round(planCenter.Y * WalkFeetToMeters, 3), 0);

            // Path.
            var modelStations = stations.Select(s => new CameraStation
            {
                Name = s.Station.Name,
                Order = s.Station.Order,
                Eye = s.Station.Eye - offset,
                Forward = s.Station.Forward,
                Up = s.Station.Up
            }).ToList();
            var path = CameraPathMath.BuildPath(modelStations, framesPerSegment);

            var fov = stations[0].Fov;
            var cameraSource = "cropbox-derived";
            if (!fov.IsValid)
            {
                fov = new FieldOfView(2 * Math.Atan(Math.Tan(25 * Math.PI / 180) * 16.0 / 9.0) * 180 / Math.PI, 50, true, true);
                cameraSource = "default";
            }

            var frameHeight = Math.Max(1, (int)Math.Round(frameWidth / fov.Aspect));

            // Plan image.
            var planJson = ExportWalkPlanImage(doc, planView, packageDir, offset, warnings);

            // Section image.
            JObject sectionJson = null;
            if (sectionView != null)
            {
                sectionJson = ExportWalkSectionImage(doc, sectionView, packageDir, offset, warnings);
            }

            var exportTemp = Path.Combine(packageDir, "_export_tmp");
            if (Directory.Exists(exportTemp))
            {
                Directory.Delete(exportTemp, true);
            }

            var viewerCopied = CopyWalkViewerTemplate(packageDir, warnings);

            var samples = new JArray(path.Samples.Select(sample => new JObject
            {
                ["s"] = Math.Round(sample.S, 4),
                ["t"] = Math.Round(sample.T, 8),
                ["p"] = new JArray(sample.P.ToArray(4)),
                ["fwd"] = new JArray(sample.Fwd.ToArray(6)),
                ["up"] = new JArray(sample.Up.ToArray(6))
            }));

            var manifest = new JObject
            {
                ["schemaVersion"] = 1,
                ["generator"] = "RevitMCP",
                ["createdAt"] = DateTimeOffset.Now.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture),
                ["project"] = new JObject
                {
                    ["name"] = doc.Title,
                    ["revitFile"] = doc.PathName ?? string.Empty,
                    ["revitVersion"] = _uiApp.Application.VersionNumber
                },
                ["units"] = new JObject
                {
                    ["length"] = "m",
                    ["angle"] = "deg",
                    ["axis"] = "revit-z-up",
                    ["originOffset"] = new JArray(offset.ToArray(3))
                },
                ["stations"] = new JArray(stations.Select((s, i) => new JObject
                {
                    ["name"] = s.View.Name,
                    ["viewId"] = s.View.Id.GetIdValue(),
                    ["sampleIndex"] = path.StationSampleIndices[i],
                    ["levelName"] = s.LevelName ?? string.Empty,
                    ["eye"] = new JArray((s.Station.Eye - offset).ToArray(4)),
                    ["fwd"] = new JArray(s.Station.Forward.ToArray(6)),
                    ["up"] = new JArray(s.Station.Up.ToArray(6)),
                    ["eyeHeightAboveLevel"] = Math.Round(s.EyeHeightAboveLevel, 3),
                    ["fovYDeg"] = s.Fov.IsValid ? (JToken)Math.Round(s.Fov.FovYDeg, 3) : JValue.CreateNull()
                })),
                ["path"] = new JObject
                {
                    ["sampleCount"] = path.Samples.Count,
                    ["totalLengthMeters"] = Math.Round(path.TotalLength, 4),
                    ["interpolation"] = "catmull-rom-centripetal-arclength",
                    ["samples"] = samples
                },
                ["camera"] = new JObject
                {
                    ["fovYDeg"] = Math.Round(fov.FovYDeg, 4),
                    ["fovXDeg"] = Math.Round(fov.FovXDeg, 4),
                    ["aspect"] = Math.Round(fov.Aspect, 6),
                    ["near"] = 0.1,
                    ["far"] = 1000,
                    ["source"] = cameraSource,
                    ["calibratedAt"] = null
                },
                ["frames"] = new JObject
                {
                    ["count"] = path.Samples.Count,
                    ["width"] = frameWidth,
                    ["height"] = frameHeight,
                    ["fileNamePattern"] = "frame_%04d.png",
                    ["renderViewName"] = "WalkRender_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture)
                },
                ["variants"] = new JArray
                {
                    new JObject
                    {
                        ["id"] = "base",
                        ["label"] = "Revit",
                        ["dir"] = "frames/base",
                        ["frameCount"] = path.Samples.Count,
                        ["complete"] = false
                    }
                },
                ["plan"] = planJson,
                ["section"] = sectionJson,
                ["model"] = null,
                ["warnings"] = new JArray(warnings)
            };

            WriteWalkManifest(packageDir, manifest);
            Logger.Info($"[走動展示] 交付包完成：{path.Samples.Count} 格、路徑 {path.TotalLength:0.0} m");

            return new
            {
                packageDir,
                indexHtml = Path.Combine(packageDir, "index.html"),
                viewerCopied,
                stationCount = stations.Count,
                frameCount = path.Samples.Count,
                pathLengthMeters = Math.Round(path.TotalLength, 2),
                frameSize = new[] { frameWidth, frameHeight },
                fovYDeg = Math.Round(fov.FovYDeg, 2),
                planPng = planJson?["image"] != null ? Path.Combine(packageDir, planJson.Value<string>("image")) : null,
                sectionPng = sectionJson?["image"] != null ? Path.Combine(packageDir, sectionJson.Value<string>("image")) : null,
                manifestPath = Path.Combine(packageDir, WalkManifestFile),
                nextSteps = new[]
                {
                    "export_walkthrough_frames packageDir=<packageDir>（逐格匯出影格）",
                    "export_model_gltf packageDir=<packageDir>（匯出 3D 模型）",
                    "雙擊 index.html 開啟"
                },
                warnings
            };
        }

        private static T FindWalkView<T>(Document doc, string name) where T : View
        {
            var candidates = new FilteredElementCollector(doc)
                .OfClass(typeof(T))
                .Cast<T>()
                .Where(v => !v.IsTemplate)
                .ToList();
            return candidates.FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.Ordinal))
                   ?? candidates.FirstOrDefault(v => string.Equals(v.Name.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        private static string CreateWalkPackageDirectory(Document doc, string outputPath, string packageName)
        {
            string parent;
            if (!string.IsNullOrWhiteSpace(outputPath))
            {
                parent = outputPath;
            }
            else if (!string.IsNullOrEmpty(doc.PathName))
            {
                parent = Path.GetDirectoryName(doc.PathName);
            }
            else
            {
                parent = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            }

            var baseName = string.IsNullOrWhiteSpace(packageName)
                ? "WalkPackage_" + DateTime.Now.ToString("yyyyMMdd_HHmm", CultureInfo.InvariantCulture)
                : packageName.Trim();
            foreach (var invalid in Path.GetInvalidFileNameChars())
            {
                baseName = baseName.Replace(invalid, '_');
            }

            Directory.CreateDirectory(parent);
            var dir = Path.Combine(parent, baseName);
            var suffix = 2;
            while (Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any())
            {
                dir = Path.Combine(parent, $"{baseName}_{suffix++}");
            }

            Directory.CreateDirectory(dir);
            return dir;
        }

        private JObject ExportWalkPlanImage(Document doc, ViewPlan view, string packageDir, Vec3 offset, List<string> warnings)
        {
            try
            {
                var export = ExportWalkRegisteredImage(doc, view, packageDir, "plan", 2000, warnings);
                var crop = export.Crop;
                var t = crop.Transform;
                var origin = WalkToModel(t.Origin, offset);
                var matrix = PlanProjection.ModelToPixel(
                    origin.X, origin.Y,
                    t.BasisX.X, t.BasisX.Y,
                    t.BasisY.X, t.BasisY.Y,
                    crop.Min.X * WalkFeetToMeters, crop.Min.Y * WalkFeetToMeters,
                    crop.Max.X * WalkFeetToMeters, crop.Max.Y * WalkFeetToMeters,
                    export.Width, export.Height);

                double levelElevation = 0;
                if (view.GenLevel != null)
                {
                    levelElevation = view.GenLevel.ProjectElevation * WalkFeetToMeters - offset.Z;
                }

                return new JObject
                {
                    ["viewName"] = view.Name,
                    ["image"] = "plan.png",
                    ["imageSize"] = new JArray(export.Width, export.Height),
                    ["modelToPixel"] = new JArray(matrix.ToArray()),
                    ["levelElevation"] = Math.Round(levelElevation, 4)
                };
            }
            catch (Exception ex) when (!(ex is OutOfMemoryException))
            {
                Logger.Error("[走動展示] 平面圖匯出失敗", ex);
                warnings.Add($"平面圖匯出失敗：{ex.Message}");
                return null;
            }
        }

        private JObject ExportWalkSectionImage(Document doc, ViewSection view, string packageDir, Vec3 offset, List<string> warnings)
        {
            try
            {
                var export = ExportWalkRegisteredImage(doc, view, packageDir, "section", 2000, warnings);
                var crop = export.Crop;
                var t = crop.Transform;
                var matrix = PlanProjection.ModelToPixel(
                    0, 0, 1, 0, 0, 1,
                    crop.Min.X * WalkFeetToMeters, crop.Min.Y * WalkFeetToMeters,
                    crop.Max.X * WalkFeetToMeters, crop.Max.Y * WalkFeetToMeters,
                    export.Width, export.Height);
                return new JObject
                {
                    ["viewName"] = view.Name,
                    ["image"] = "section.png",
                    ["imageSize"] = new JArray(export.Width, export.Height),
                    ["frame"] = new JObject
                    {
                        ["origin"] = new JArray(WalkToModel(t.Origin, offset).ToArray(4)),
                        ["right"] = new JArray(WalkToVec3(t.BasisX).ToArray(6)),
                        ["up"] = new JArray(WalkToVec3(t.BasisY).ToArray(6)),
                        ["normal"] = new JArray(WalkToVec3(t.BasisZ).ToArray(6))
                    },
                    ["uvToPixel"] = new JArray(matrix.ToArray()),
                    ["depthRangeMeters"] = new JArray(
                        Math.Round(Math.Min(crop.Min.Z, crop.Max.Z) * WalkFeetToMeters, 4),
                        Math.Round(Math.Max(crop.Min.Z, crop.Max.Z) * WalkFeetToMeters, 4))
                };
            }
            catch (Exception ex) when (!(ex is OutOfMemoryException))
            {
                Logger.Error("[走動展示] 剖面圖匯出失敗", ex);
                warnings.Add($"剖面圖匯出失敗：{ex.Message}");
                return null;
            }
        }

        private sealed class WalkRegisteredImage
        {
            public BoundingBoxXYZ Crop;
            public int Width;
            public int Height;
        }

        /// <summary>
        /// Exports a cropped 2D view edge-to-edge from a temporary duplicate (the user's view is never modified):
        /// the duplicate drops its view template, turns crop on, hides the crop frame and disables the annotation crop
        /// so the image bounds equal the crop rectangle. The duplicate is deleted afterwards.
        /// </summary>
        private static WalkRegisteredImage ExportWalkRegisteredImage(Document doc, View view, string packageDir, string fileBase, int pixelSize, List<string> warnings)
        {
            var option = view.CanViewBeDuplicated(ViewDuplicateOption.WithDetailing)
                ? ViewDuplicateOption.WithDetailing
                : ViewDuplicateOption.Duplicate;
            if (!view.CanViewBeDuplicated(option))
            {
                throw new InvalidOperationException($"視圖「{view.Name}」無法複製，無法匯出對位圖。");
            }

            ElementId tempId;
            using (var tx = new Transaction(doc, "走動展示：建立對位圖暫存視圖"))
            {
                tx.Start();
                tempId = view.Duplicate(option);
                var temp = (View)doc.GetElement(tempId);
                if (temp.ViewTemplateId != ElementId.InvalidElementId)
                {
                    temp.ViewTemplateId = ElementId.InvalidElementId;
                }

                temp.CropBoxActive = true;
                temp.CropBoxVisible = false;
                var annotationParam = temp.get_Parameter(BuiltInParameter.VIEWER_ANNOTATION_CROP_ACTIVE);
                if (annotationParam != null && !annotationParam.IsReadOnly)
                {
                    annotationParam.Set(0);
                }

                tx.Commit();
            }

            BoundingBoxXYZ crop;
            string target;
            double outlineWidth = 0, outlineHeight = 0;
            try
            {
                var temp = (View)doc.GetElement(tempId);
                crop = temp.CropBox;
                try
                {
                    // View.Outline is the view extent in paper feet; x Scale gives model feet. If it is larger than the
                    // crop rectangle, the exported image likely includes annotations beyond the crop (registration risk).
                    var outline = temp.Outline;
                    outlineWidth = (outline.Max.U - outline.Min.U) * temp.Scale;
                    outlineHeight = (outline.Max.V - outline.Min.V) * temp.Scale;
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException)
                {
                    outlineWidth = outlineHeight = 0;
                }

                target = ExportWalkViewImage(doc, temp, packageDir, fileBase, pixelSize, FitDirectionType.Horizontal);
            }
            finally
            {
                using (var tx = new Transaction(doc, "走動展示：刪除對位圖暫存視圖"))
                {
                    tx.Start();
                    doc.Delete(tempId);
                    tx.Commit();
                }
            }

            if (!PlanProjection.TryReadPngSize(target, out var width, out var height))
            {
                throw new InvalidOperationException($"無法讀取 {Path.GetFileName(target)} 的影像尺寸。");
            }

            var cropWidth = crop.Max.X - crop.Min.X;
            var cropHeight = crop.Max.Y - crop.Min.Y;
            var mismatch = PlanProjection.AspectMismatch(cropWidth, cropHeight, width, height);
            if (mismatch > 0.015)
            {
                warnings.Add($"{view.Name} 匯出圖比例與裁剪框相差 {mismatch:P1}，平面/剖面標記可能有偏移（可能是非矩形裁剪或出圖留白）。");
            }

            if (outlineWidth > 0 && outlineHeight > 0
                && (outlineWidth > cropWidth * 1.01 || outlineHeight > cropHeight * 1.01))
            {
                warnings.Add($"{view.Name} 的視圖外框比裁剪框大（{outlineWidth * WalkFeetToMeters:0.0}×{outlineHeight * WalkFeetToMeters:0.0} m 對 {cropWidth * WalkFeetToMeters:0.0}×{cropHeight * WalkFeetToMeters:0.0} m），可能有註解或符號超出裁剪框，標記可能偏移；請開網頁核對站點位置。");
            }

            Logger.Info($"[走動展示] {view.Name} 對位：crop {cropWidth:0.###}×{cropHeight:0.###} ft、outline×scale {outlineWidth:0.###}×{outlineHeight:0.###} ft、image {width}×{height} px");

            return new WalkRegisteredImage { Crop = crop, Width = width, Height = height };
        }

        /// <summary>Exports one view to &lt;dir&gt;/&lt;fileBase&gt;.png, renaming Revit's decorated output name.</summary>
        private static string ExportWalkViewImage(Document doc, View view, string directory, string fileBase, int pixelSize, FitDirectionType fitDirection)
        {
            var tempDir = Path.Combine(directory, "_export_tmp");
            Directory.CreateDirectory(tempDir);
            foreach (var stale in Directory.GetFiles(tempDir))
            {
                File.Delete(stale);
            }

            var options = new ImageExportOptions
            {
                ExportRange = ExportRange.SetOfViews,
                FilePath = Path.Combine(tempDir, "export"),
                HLRandWFViewsFileType = ImageFileType.PNG,
                ShadowViewsFileType = ImageFileType.PNG,
                ImageResolution = ImageResolution.DPI_150,
                ZoomType = ZoomFitType.FitToPage,
                FitDirection = fitDirection,
                PixelSize = pixelSize
            };
            options.SetViewsAndSheets(new List<ElementId> { view.Id });
            doc.ExportImage(options);

            var produced = Directory.GetFiles(tempDir, "*.png")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault()
                ?? throw new InvalidOperationException($"視圖「{view.Name}」匯出失敗：找不到輸出的 PNG 檔。");

            var target = Path.Combine(directory, fileBase + ".png");
            if (File.Exists(target))
            {
                File.Delete(target);
            }

            File.Move(produced, target);
            return target;
        }

        // ────────────────────────────────────────────────
        // export_walkthrough_frames
        // ────────────────────────────────────────────────

        private object ExportWalkthroughFrames(JObject parameters)
        {
            var doc = _uiApp.ActiveUIDocument.Document;
            var packageDir = RequireWalkPackageDir(parameters);
            var manifest = ReadWalkManifest(packageDir);
            var warnings = new List<string>();

            var variant = parameters["variant"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(variant))
            {
                variant = "base";
            }

            if (!Regex.IsMatch(variant, "^[a-zA-Z0-9_-]+$"))
            {
                throw new ArgumentException("variant 只能包含英數字、底線與連字號。");
            }

            var total = manifest["path"]?.Value<int>("sampleCount") ?? 0;
            var samples = manifest["path"]?["samples"] as JArray;
            if (total <= 0 || samples == null || samples.Count != total)
            {
                throw new InvalidOperationException("manifest.json 的路徑資料不完整，請重新執行 export_walkthrough_package。");
            }

            var startIndex = WalkClamp(parameters["startIndex"]?.Value<int?>() ?? 0, 0, total);
            var count = WalkClamp(parameters["count"]?.Value<int?>() ?? 24, 1, 120);
            var end = Math.Min(total, startIndex + count);
            var framesInfo = (JObject)manifest["frames"];
            var frameWidth = framesInfo.Value<int?>("width") ?? 1280;
            var variantEntry = EnsureWalkVariant(manifest, variant, total, out var variantCreated);
            var variantDir = Path.Combine(packageDir, variantEntry.Value<string>("dir").Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(variantDir);

            var offset = WalkReadVec3(manifest["units"]?["originOffset"]);
            var stopwatch = Stopwatch.StartNew();
            var exported = 0;
            var manifestChanged = variantCreated;
            var processedEnd = startIndex;
            // Keep each batch well under the MCP server's 600 s command timeout, even for slow realistic styles.
            var batchTimeBudget = TimeSpan.FromSeconds(240);

            if (startIndex < end)
            {
                var renderView = EnsureWalkRenderView(doc, manifest, warnings);
                var renderViewName = renderView.Name;
                Logger.Info($"[走動展示] 影格 {startIndex}–{end - 1} / {total}（variant={variant}）開始");

                // One undo item per batch instead of one per frame.
                using (var group = new TransactionGroup(doc, "走動展示：匯出影格"))
                {
                    group.Start();
                    try
                    {
                        for (var i = startIndex; i < end; i++)
                        {
                            var sample = (JObject)samples[i];
                            var p = WalkReadVec3(sample["p"]) + offset;
                            var frame = CameraPathMath.OrthonormalizeCamera(WalkReadVec3(sample["fwd"]), WalkReadVec3(sample["up"]));
                            var eye = new XYZ(p.X / WalkFeetToMeters, p.Y / WalkFeetToMeters, p.Z / WalkFeetToMeters);

                            using (var tx = new Transaction(doc, "走動展示：設定影格相機"))
                            {
                                tx.Start();
                                renderView.SetOrientation(new ViewOrientation3D(eye, WalkToXyz(frame.Up), WalkToXyz(frame.Forward)));
                                if (renderView.CanSaveOrientation())
                                {
                                    renderView.SaveOrientation();
                                }

                                tx.Commit();
                            }

                            var fileBase = string.Format(CultureInfo.InvariantCulture, "frame_{0:0000}", i);
                            var target = ExportWalkViewImage(doc, renderView, variantDir, fileBase, frameWidth, FitDirectionType.Horizontal);
                            exported++;
                            processedEnd = i + 1;

                            if (i == startIndex && PlanProjection.TryReadPngSize(target, out var w, out var h)
                                && (framesInfo.Value<int?>("height") != h || framesInfo.Value<int?>("width") != w))
                            {
                                framesInfo["width"] = w;
                                framesInfo["height"] = h;
                                var cameraAspect = manifest["camera"]?.Value<double?>("aspect") ?? 0;
                                var imageAspect = (double)w / h;
                                if (cameraAspect > 0 && Math.Abs(imageAspect - cameraAspect) / cameraAspect > 0.01)
                                {
                                    warnings.Add($"影格實際比例 {imageAspect:0.000} 與相機換算比例 {cameraAspect:0.000} 不同，已以影格為準；3D 對位請用校準模式確認。");
                                    manifest["camera"]["aspect"] = Math.Round(imageAspect, 6);
                                    var fovY = manifest["camera"].Value<double>("fovYDeg");
                                    manifest["camera"]["fovXDeg"] = Math.Round(2 * Math.Atan(Math.Tan(fovY * Math.PI / 360) * imageAspect) * 180 / Math.PI, 4);
                                }

                                manifestChanged = true;
                            }

                            if (stopwatch.Elapsed > batchTimeBudget && i + 1 < end)
                            {
                                warnings.Add($"本批已達 {batchTimeBudget.TotalSeconds:0} 秒時間上限，提前在第 {i} 格結束，其餘由下一批接續。");
                                break;
                            }
                        }
                    }
                    catch (Exception ex) when (!(ex is OutOfMemoryException))
                    {
                        // Persist progress so a resume does not redo finished frames, then surface the failure.
                        AppendWalkWarnings(manifest, warnings);
                        File.WriteAllText(Path.Combine(packageDir, WalkManifestFile), manifest.ToString(Formatting.Indented), new UTF8Encoding(false));
                        throw new InvalidOperationException(
                            $"第 {processedEnd} 格匯出失敗：{ex.Message}（前 {exported} 格已完成；暫存視圖「{renderViewName}」保留供續跑，可用 startIndex={processedEnd} 繼續）", ex);
                    }
                    finally
                    {
                        if (group.HasStarted())
                        {
                            group.Assimilate();
                        }
                    }
                }

                var tempExportDir = Path.Combine(variantDir, "_export_tmp");
                if (Directory.Exists(tempExportDir))
                {
                    Directory.Delete(tempExportDir, true);
                }

                Logger.Info($"[走動展示] 影格 {startIndex}–{processedEnd - 1} 完成，耗時 {stopwatch.Elapsed.TotalSeconds:0.0} 秒");
            }

            var nextIndex = processedEnd < total ? processedEnd : (int?)null;
            if (nextIndex == null)
            {
                var existing = Enumerable.Range(0, total)
                    .Count(i => File.Exists(Path.Combine(variantDir, string.Format(CultureInfo.InvariantCulture, "frame_{0:0000}.png", i))));
                variantEntry["complete"] = existing == total;
                if (existing != total)
                {
                    warnings.Add($"variant {variant} 只有 {existing}/{total} 格，缺格可用 startIndex 補跑。");
                }

                DeleteWalkRenderView(doc, manifest);
                manifestChanged = true;
            }

            AppendWalkWarnings(manifest, warnings);
            if (manifestChanged)
            {
                WriteWalkManifest(packageDir, manifest);
            }
            else
            {
                // Mid-run batches only refresh manifest.json; data.js is rewritten when something the viewer needs changes.
                File.WriteAllText(Path.Combine(packageDir, WalkManifestFile), manifest.ToString(Formatting.Indented), new UTF8Encoding(false));
            }

            return new
            {
                packageDir,
                variant,
                exported,
                startIndex,
                nextIndex,
                total,
                elapsedMs = stopwatch.ElapsedMilliseconds,
                framesDir = variantDir,
                warnings
            };
        }

        private static JObject EnsureWalkVariant(JObject manifest, string variant, int total, out bool created)
        {
            created = false;
            var variants = manifest["variants"] as JArray;
            if (variants == null)
            {
                variants = new JArray();
                manifest["variants"] = variants;
            }

            var entry = variants.OfType<JObject>().FirstOrDefault(v => string.Equals(v.Value<string>("id"), variant, StringComparison.Ordinal));
            if (entry == null)
            {
                entry = new JObject
                {
                    ["id"] = variant,
                    ["label"] = variant,
                    ["dir"] = "frames/" + variant,
                    ["frameCount"] = total,
                    ["complete"] = false
                };
                variants.Add(entry);
                created = true;
            }

            return entry;
        }

        private View3D EnsureWalkRenderView(Document doc, JObject manifest, List<string> warnings)
        {
            var name = manifest["frames"]?.Value<string>("renderViewName") ?? "WalkRender_TMP";
            var existing = new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>()
                .FirstOrDefault(v => !v.IsTemplate && v.Name == name);
            if (existing != null)
            {
                if (existing.IsLocked)
                {
                    using (var tx = new Transaction(doc, "走動展示：解鎖影格暫存視圖"))
                    {
                        tx.Start();
                        existing.Unlock();
                        tx.Commit();
                    }
                }

                return existing;
            }

            var firstStation = manifest["stations"]?.First as JObject;
            View3D source = null;
            if (firstStation != null)
            {
                var id = firstStation.Value<long>("viewId");
                source = doc.GetElement(WalkElementId(id)) as View3D;
                if (source == null || source.Name != firstStation.Value<string>("name"))
                {
                    source = new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>()
                        .FirstOrDefault(v => !v.IsTemplate && v.Name == firstStation.Value<string>("name")) ?? source;
                }
            }

            if (source == null || !source.IsPerspective)
            {
                throw new InvalidOperationException("找不到第一站透視視圖，無法建立影格用暫存視圖；請確認站點視圖仍存在。");
            }

            using (var tx = new Transaction(doc, "走動展示：建立影格暫存視圖"))
            {
                tx.Start();
                var duplicateId = source.Duplicate(ViewDuplicateOption.Duplicate);
                var view = (View3D)doc.GetElement(duplicateId);
                view.Name = name;
                if (view.IsLocked)
                {
                    view.Unlock();
                }

                tx.Commit();
                Logger.Info($"[走動展示] 建立暫存視圖 {name}（複製自 {source.Name}）");
                return view;
            }
        }

        private static void DeleteWalkRenderView(Document doc, JObject manifest)
        {
            var name = manifest["frames"]?.Value<string>("renderViewName");
            if (string.IsNullOrEmpty(name))
            {
                return;
            }

            var view = new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>()
                .FirstOrDefault(v => !v.IsTemplate && v.Name == name);
            if (view == null)
            {
                return;
            }

            var activeViewId = doc.ActiveView?.Id ?? ElementId.InvalidElementId;
            if (activeViewId == view.Id)
            {
                Logger.Info($"[走動展示] 暫存視圖 {name} 目前為作用中視圖，保留不刪。");
                return;
            }

            using (var tx = new Transaction(doc, "走動展示：刪除影格暫存視圖"))
            {
                tx.Start();
                doc.Delete(view.Id);
                tx.Commit();
            }
        }

        // ────────────────────────────────────────────────
        // apply_camera_stations（把網頁編輯過的站點寫回 Revit 視圖）
        // ────────────────────────────────────────────────

        private object ApplyCameraStations(JObject parameters)
        {
            var doc = _uiApp.ActiveUIDocument.Document;
            var warnings = new List<string>();

            var input = parameters["stations"] as JArray;
            if (input == null || input.Count == 0)
            {
                throw new ArgumentException("stations 為必填：請提供 [{name, eye:[x,y,z], fwd:[x,y,z], up:[x,y,z]}] 陣列（網頁「下載站點」產生的 stations.json 內容）。");
            }

            var packageDir = parameters["packageDir"]?.Value<string>();
            JObject manifest = null;
            var offset = Vec3.Zero;
            if (!string.IsNullOrWhiteSpace(packageDir) && File.Exists(Path.Combine(packageDir, WalkManifestFile)))
            {
                manifest = ReadWalkManifest(packageDir);
                offset = WalkReadVec3(manifest["units"]?["originOffset"]);
            }

            // package = 交付包模型座標（已扣 originOffset）；revit = Revit 專案座標公尺（未扣）。
            var coordinates = parameters["coordinates"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(coordinates))
            {
                coordinates = manifest != null ? "package" : "revit";
            }

            if (coordinates != "package" && coordinates != "revit")
            {
                throw new ArgumentException("coordinates 只能是 \"package\"（交付包座標）或 \"revit\"（Revit 專案座標公尺）。");
            }

            if (coordinates == "package" && manifest == null)
            {
                throw new ArgumentException("coordinates=package 需要同時提供有效的 packageDir 才能取得 originOffset。");
            }

            var apply = string.Equals(parameters["mode"]?.Value<string>() ?? "preview", "apply", StringComparison.OrdinalIgnoreCase);
            var createMissing = parameters["createMissing"]?.Value<bool?>() ?? false;
            var unlock = parameters["unlockLockedViews"]?.Value<bool?>() ?? false;
            var frameOffset = coordinates == "package" ? offset : Vec3.Zero;

            var allViews = new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>()
                .Where(v => !v.IsTemplate).ToList();
            var templateSource = allViews.FirstOrDefault(v => v.IsPerspective
                && input.OfType<JObject>().Any(j => string.Equals(j.Value<string>("name"), v.Name, StringComparison.OrdinalIgnoreCase)));

            var results = new List<object>();
            var pending = new List<Tuple<View3D, XYZ, Vec3, Vec3>>();
            var created = 0;

            foreach (var entry in input.OfType<JObject>())
            {
                var name = entry.Value<string>("name");
                if (string.IsNullOrWhiteSpace(name))
                {
                    warnings.Add("有一筆站點沒有 name，已略過。");
                    continue;
                }

                var eyeModel = WalkReadVec3(entry["eye"]) + frameOffset;
                var frame = CameraPathMath.OrthonormalizeCamera(WalkReadVec3(entry["fwd"]), WalkReadVec3(entry["up"]));
                var eye = new XYZ(eyeModel.X / WalkFeetToMeters, eyeModel.Y / WalkFeetToMeters, eyeModel.Z / WalkFeetToMeters);

                var view = allViews.FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.Ordinal))
                           ?? allViews.FirstOrDefault(v => string.Equals(v.Name.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase));

                if (view == null)
                {
                    if (!createMissing)
                    {
                        warnings.Add($"找不到視圖「{name}」，已略過（要自動建立請設 createMissing=true）。");
                        continue;
                    }

                    if (templateSource == null)
                    {
                        warnings.Add($"找不到視圖「{name}」，且沒有既有站點可當樣板，無法建立。");
                        continue;
                    }

                    if (!apply)
                    {
                        results.Add(new { name, action = "would-create", templateView = templateSource.Name });
                        created++;
                        continue;
                    }

                    using (var tx = new Transaction(doc, $"走動展示：建立站點 {name}"))
                    {
                        tx.Start();
                        var duplicated = (View3D)doc.GetElement(templateSource.Duplicate(ViewDuplicateOption.Duplicate));
                        duplicated.Name = name;
                        view = duplicated;
                        tx.Commit();
                    }

                    allViews.Add(view);
                    created++;
                }

                if (!view.IsPerspective)
                {
                    warnings.Add($"「{view.Name}」不是透視視圖，已略過。");
                    continue;
                }

                if (view.IsLocked && !unlock)
                {
                    warnings.Add($"「{view.Name}」的方位已鎖定，已略過（要強制改寫請設 unlockLockedViews=true）。");
                    continue;
                }

                var before = view.GetOrientation();
                var beforeEye = new Vec3(before.EyePosition.X, before.EyePosition.Y, before.EyePosition.Z) * WalkFeetToMeters;
                var afterEye = eyeModel;
                var beforeFwd = WalkToVec3(before.ForwardDirection);
                var headingDelta = Math.Acos(Math.Max(-1, Math.Min(1, beforeFwd.Normalized().Dot(frame.Forward)))) * 180 / Math.PI;

                results.Add(new
                {
                    name = view.Name,
                    viewId = view.Id.GetIdValue(),
                    action = apply ? "applied" : "would-apply",
                    eyeBefore = beforeEye.ToArray(4),
                    eyeAfter = afterEye.ToArray(4),
                    moveMeters = Math.Round((afterEye - beforeEye).Length, 4),
                    heightDeltaMeters = Math.Round(afterEye.Z - beforeEye.Z, 4),
                    headingDeltaDeg = Math.Round(headingDelta, 2)
                });

                if (apply)
                {
                    pending.Add(Tuple.Create(view, eye, frame.Up, frame.Forward));
                }
            }

            if (apply && pending.Count > 0)
            {
                using (var group = new TransactionGroup(doc, "走動展示：寫回相機站點"))
                {
                    group.Start();
                    foreach (var item in pending)
                    {
                        using (var tx = new Transaction(doc, $"走動展示：設定 {item.Item1.Name} 相機"))
                        {
                            tx.Start();
                            if (item.Item1.IsLocked && unlock)
                            {
                                item.Item1.Unlock();
                            }

                            item.Item1.SetOrientation(new ViewOrientation3D(item.Item2, WalkToXyz(item.Item3), WalkToXyz(item.Item4)));
                            if (item.Item1.CanSaveOrientation())
                            {
                                item.Item1.SaveOrientation();
                            }

                            tx.Commit();
                        }
                    }

                    group.Assimilate();
                }

                Logger.Info($"[走動展示] 已寫回 {pending.Count} 個站點相機（新建 {created} 個）");

                if (manifest != null)
                {
                    // Frames were rendered with the old cameras: mark every variant stale for the viewer.
                    foreach (var variant in (manifest["variants"] as JArray ?? new JArray()).OfType<JObject>())
                    {
                        variant["stale"] = true;
                    }

                    manifest["stationsAppliedAt"] = DateTimeOffset.Now.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);
                    AppendWalkWarnings(manifest, new List<string>
                    {
                        "站點已在 Revit 更新，交付包裡的影格與路徑仍是舊的；請重新執行 export_walkthrough_package 與 export_walkthrough_frames。"
                    });
                    WriteWalkManifest(packageDir, manifest);
                }
            }

            return new
            {
                mode = apply ? "apply" : "preview",
                coordinates,
                requested = input.Count,
                changed = results.Count,
                createdViews = created,
                stations = results,
                nextSteps = apply
                    ? new[]
                    {
                        "重新執行 export_walkthrough_package（重新取樣路徑與平面對位）",
                        "再執行 export_walkthrough_frames 重出影格"
                    }
                    : new[] { "確認上面的位移量後，以 mode=\"apply\" 再呼叫一次即可寫回 Revit" },
                warnings
            };
        }

        // ────────────────────────────────────────────────
        // export_model_gltf
        // ────────────────────────────────────────────────

        private object ExportModelGltf(JObject parameters)
        {
            var doc = _uiApp.ActiveUIDocument.Document;
            var packageDir = RequireWalkPackageDir(parameters);
            var manifest = ReadWalkManifest(packageDir);
            var warnings = new List<string>();
            var embed = parameters["embedInDataJs"]?.Value<bool?>() ?? true;
            var offset = WalkReadVec3(manifest["units"]?["originOffset"]);

            View3D source;
            var sourceViewName = parameters["sourceViewName"]?.Value<string>();
            if (!string.IsNullOrWhiteSpace(sourceViewName))
            {
                source = FindWalkView<View3D>(doc, sourceViewName)
                         ?? throw new ArgumentException($"找不到名稱為「{sourceViewName}」的 3D 視圖。");
            }
            else
            {
                var firstName = (manifest["stations"]?.First as JObject)?.Value<string>("name");
                source = string.IsNullOrEmpty(firstName) ? null : FindWalkView<View3D>(doc, firstName);
                source = source ?? throw new InvalidOperationException("找不到第一站視圖，請以 sourceViewName 指定要匯出的 3D 視圖。");
            }

            // Spatial cull instead of a category cull: the camera path already says what the
            // walk can see, so derive the export volume from it rather than asking the user to
            // place a section box by hand or dropping whole categories to fit the budget.
            var boundsMode = parameters["bounds"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(boundsMode))
            {
                boundsMode = "path";
            }

            if (boundsMode != "path" && boundsMode != "none")
            {
                throw new ArgumentException("bounds 只能是 \"path\"（預設，依相機路徑自動裁切）或 \"none\"（完全沿用來源視圖範圍）。");
            }

            var marginMeters = parameters["marginMeters"]?.Value<double?>() ?? 25.0;
            var belowMeters = parameters["belowMeters"]?.Value<double?>() ?? 2.5;
            var aboveMeters = parameters["aboveMeters"]?.Value<double?>() ?? 8.0;
            if (marginMeters < 0 || belowMeters < 0 || aboveMeters < 0)
            {
                throw new ArgumentException("marginMeters／belowMeters／aboveMeters 不可為負數。");
            }

            BoundingBoxXYZ sectionBox = null;
            if (boundsMode == "path")
            {
                sectionBox = ComputeWalkPathSectionBox(manifest, offset, marginMeters, belowMeters, aboveMeters);
                if (sectionBox == null)
                {
                    warnings.Add("manifest 沒有可用的路徑取樣點，無法依路徑自動裁切；本次改以來源視圖的完整範圍匯出。");
                }
            }

            var stopwatch = Stopwatch.StartNew();
            ElementId tempViewId;
            using (var tx = new Transaction(doc, "走動展示：建立模型匯出暫存視圖"))
            {
                tx.Start();
                tempViewId = source.Duplicate(ViewDuplicateOption.Duplicate);
                var temp = (View3D)doc.GetElement(tempViewId);
                if (temp.IsLocked)
                {
                    temp.Unlock();
                }

                if (temp.IsPerspective && temp.CanToggleBetweenPerspectiveAndIsometric())
                {
                    temp.ToggleToIsometric();
                }

                if (temp.IsPerspective)
                {
                    warnings.Add("暫存視圖無法切換為等角，透視裁剪範圍外的元件可能不會匯出。");
                }
                else
                {
                    temp.CropBoxActive = false;
                }

                // bounds="none" leaves whatever the source view already had (including an
                // inherited section box) untouched — that is the "export exactly what this
                // view shows" escape hatch.
                if (sectionBox != null)
                {
                    temp.IsSectionBoxActive = true;
                    temp.SetSectionBox(sectionBox);
                }

                tx.Commit();
            }

            var builder = new GltfBuilder();
            RevitGltfExportContext context;
            try
            {
                var tempView = (View3D)doc.GetElement(tempViewId);
                context = new RevitGltfExportContext(doc, builder, offset);
                using (var exporter = new CustomExporter(doc, context))
                {
                    exporter.IncludeGeometricObjects = false;
                    exporter.ShouldStopOnError = false;
                    exporter.Export(tempView);
                }
            }
            finally
            {
                using (var tx = new Transaction(doc, "走動展示：刪除模型匯出暫存視圖"))
                {
                    tx.Start();
                    doc.Delete(tempViewId);
                    tx.Commit();
                }
            }

            if (context.BudgetExceeded)
            {
                warnings.Add(sectionBox != null
                    ? $"三角面超過上限 {context.TriangleBudget:N0}，匯出已提前停止（模型不完整）；請縮小 marginMeters／aboveMeters 讓裁切範圍更貼近路徑，或在來源 3D 視圖隱藏家具、植栽、機電等類別。"
                    : $"三角面超過上限 {context.TriangleBudget:N0}，匯出已提前停止（模型不完整）；bounds=\"path\" 可依相機路徑自動裁切，通常足以解決。");
            }

            var glb = builder.BuildGlb();
            var glbPath = Path.Combine(packageDir, WalkModelFile);
            File.WriteAllBytes(glbPath, glb);
            var sizeMb = glb.Length / 1024.0 / 1024.0;
            if (sizeMb > 40)
            {
                warnings.Add($"模型檔 {sizeMb:0.0} MB，網頁載入會變慢；建議在來源 3D 視圖隱藏家具、植栽、MEP 等類別後重匯。");
            }

            if (embed && glb.LongLength > WalkMaxEmbeddedGlbBytes)
            {
                embed = false;
                warnings.Add($"模型檔 {sizeMb:0.0} MB 超過嵌入上限 {WalkMaxEmbeddedGlbBytes / 1024 / 1024} MB，未嵌入 data.js，網頁 3D 視窗將不顯示模型；請減少可見類別後重匯。");
            }

            manifest["model"] = new JObject
            {
                ["glb"] = WalkModelFile,
                ["yUpConverted"] = true,
                ["triangleCount"] = builder.TriangleCount,
                ["materialCount"] = builder.MaterialCount,
                ["sourceViewName"] = source.Name,
                ["embedded"] = embed,
                ["bounds"] = sectionBox == null
                    ? (JToken)"none"
                    : new JObject
                    {
                        ["mode"] = "path",
                        ["marginMeters"] = marginMeters,
                        ["belowMeters"] = belowMeters,
                        ["aboveMeters"] = aboveMeters,
                        ["minMeters"] = new JArray(
                            Math.Round(sectionBox.Min.X * WalkFeetToMeters - offset.X, 3),
                            Math.Round(sectionBox.Min.Y * WalkFeetToMeters - offset.Y, 3),
                            Math.Round(sectionBox.Min.Z * WalkFeetToMeters - offset.Z, 3)),
                        ["maxMeters"] = new JArray(
                            Math.Round(sectionBox.Max.X * WalkFeetToMeters - offset.X, 3),
                            Math.Round(sectionBox.Max.Y * WalkFeetToMeters - offset.Y, 3),
                            Math.Round(sectionBox.Max.Z * WalkFeetToMeters - offset.Z, 3))
                    }
            };
            AppendWalkWarnings(manifest, warnings);
            WriteWalkManifest(packageDir, manifest);
            Logger.Info($"[走動展示] glTF 完成：{builder.TriangleCount:N0} 三角面、{builder.MaterialCount} 材質、{sizeMb:0.0} MB、{stopwatch.Elapsed.TotalSeconds:0.0} 秒");

            return new
            {
                packageDir,
                glbPath,
                triangleCount = builder.TriangleCount,
                vertexCount = builder.VertexCount,
                materialCount = builder.MaterialCount,
                fileSizeMB = Math.Round(sizeMb, 2),
                embeddedInDataJs = embed,
                sourceViewName = source.Name,
                bounds = boundsMode,
                sectionBoxMeters = sectionBox == null ? null : new
                {
                    min = new[]
                    {
                        Math.Round(sectionBox.Min.X * WalkFeetToMeters, 2),
                        Math.Round(sectionBox.Min.Y * WalkFeetToMeters, 2),
                        Math.Round(sectionBox.Min.Z * WalkFeetToMeters, 2)
                    },
                    max = new[]
                    {
                        Math.Round(sectionBox.Max.X * WalkFeetToMeters, 2),
                        Math.Round(sectionBox.Max.Y * WalkFeetToMeters, 2),
                        Math.Round(sectionBox.Max.Z * WalkFeetToMeters, 2)
                    }
                },
                elapsedMs = stopwatch.ElapsedMilliseconds,
                warnings
            };
        }

        // ────────────────────────────────────────────────
        // export_walkthrough_depth（呼叫外部 Node 腳本離線渲染深度圖；純外部程序，不碰 Revit API）
        // ────────────────────────────────────────────────

        /// <summary>Largest amount of time the Node render-depth.mjs subprocess is allowed to run before being killed.</summary>
        private static readonly TimeSpan WalkDepthTimeout = TimeSpan.FromMinutes(8);

        private object ExportWalkthroughDepth(JObject parameters)
        {
            var packageDir = RequireWalkPackageDir(parameters);
            var warnings = new List<string>();

            var scriptPath = FindWalkDepthScript();
            if (scriptPath == null)
            {
                throw new InvalidOperationException(
                    "找不到深度圖渲染腳本 bridge/node/walkthrough/render-depth.mjs。請重跑 scripts/install-addon.ps1 部署腳本後再試。");
            }

            var nodePath = FindNodeExecutable();
            if (nodePath == null)
            {
                throw new InvalidOperationException(
                    "找不到 Node.js（node 指令不在 PATH，也找不到常見安裝路徑）。請先安裝 Node.js（https://nodejs.org/）後再試，" +
                    "或確認已加入 PATH／使用支援的版本管理工具（nvm-windows、nvs）。");
            }

            var width = parameters["width"]?.Value<int?>();
            var start = parameters["start"]?.Value<int?>();
            var count = parameters["count"]?.Value<int?>();
            var encoding = parameters["encoding"]?.Value<string>();
            if (!string.IsNullOrWhiteSpace(encoding) && encoding != "gray8" && encoding != "gray16")
            {
                throw new ArgumentException("encoding 只能是 \"gray8\" 或 \"gray16\"。");
            }

            var alsoNormals = parameters["alsoNormals"]?.Value<bool?>() ?? false;
            var overwrite = parameters["overwrite"]?.Value<bool?>() ?? false;
            var chromePath = parameters["chromePath"]?.Value<string>();
            if (!string.IsNullOrWhiteSpace(chromePath) && !File.Exists(chromePath))
            {
                warnings.Add($"指定的 chromePath「{chromePath}」找不到檔案，仍會原樣傳給腳本；若失敗請確認路徑正確。");
            }

            var arguments = new StringBuilder();
            arguments.Append('"').Append(scriptPath).Append('"');
            arguments.Append(" --package \"").Append(packageDir).Append('"');
            if (width.HasValue) arguments.Append(" --width ").Append(width.Value);
            if (start.HasValue) arguments.Append(" --start ").Append(start.Value);
            if (count.HasValue) arguments.Append(" --count ").Append(count.Value);
            if (!string.IsNullOrWhiteSpace(encoding)) arguments.Append(" --encoding ").Append(encoding);
            if (!string.IsNullOrWhiteSpace(chromePath)) arguments.Append(" --chrome \"").Append(chromePath).Append('"');
            if (alsoNormals) arguments.Append(" --also-normals");
            if (overwrite) arguments.Append(" --overwrite");

            var stdOutLines = new List<string>();
            var stdErrLines = new List<string>();
            var stopwatch = Stopwatch.StartNew();

            var psi = new ProcessStartInfo
            {
                FileName = nodePath,
                Arguments = arguments.ToString(),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(scriptPath)
            };

            Logger.Info($"[走動深度] 開始渲染：{packageDir}（node={nodePath}，script={scriptPath}）");

            int exitCode;
            var timedOut = false;
            using (var proc = new Process { StartInfo = psi })
            {
                proc.OutputDataReceived += (s, e) =>
                {
                    if (e.Data == null) return;
                    lock (stdOutLines) { stdOutLines.Add(e.Data); }
                    Logger.Info("[走動深度] " + e.Data);
                };
                proc.ErrorDataReceived += (s, e) =>
                {
                    if (e.Data == null) return;
                    lock (stdErrLines) { stdErrLines.Add(e.Data); }
                    Logger.Info("[走動深度][stderr] " + e.Data);
                };

                try
                {
                    proc.Start();
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"啟動 Node 腳本失敗：{ex.Message}", ex);
                }

                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();

                var finished = proc.WaitForExit((int)WalkDepthTimeout.TotalMilliseconds);
                if (!finished)
                {
                    // Stop before the MCP server's long-command timeout so the caller gets a resumable
                    // answer (already-rendered frames are kept; the script skips them on the next run).
                    timedOut = true;
                    try { proc.Kill(); } catch { /* best effort */ }
                    try { proc.WaitForExit(5000); } catch { /* best effort */ }
                    exitCode = 0;
                }
                else
                {
                    // Let the async output/error readers flush their last lines before we read the lists.
                    proc.WaitForExit();
                    exitCode = proc.ExitCode;
                }
            }

            stopwatch.Stop();

            if (!timedOut && exitCode != 0)
            {
                var tail = string.Join(Environment.NewLine, TailLines(stdErrLines, 20));
                throw new InvalidOperationException(
                    $"深度圖渲染失敗（exit code {exitCode}）。stderr 最後 20 行：{Environment.NewLine}{tail}");
            }

            var depthDir = Path.Combine(packageDir, "depth");
            var exportedCount = Directory.Exists(depthDir)
                ? Directory.EnumerateFiles(depthDir, "*", SearchOption.AllDirectories).Count()
                : 0;

            if (exportedCount == 0)
            {
                var parsed = ParseWalkDepthExportedCount(stdOutLines);
                if (parsed.HasValue)
                {
                    exportedCount = parsed.Value;
                }
            }

            if (!Directory.Exists(depthDir) || exportedCount == 0)
            {
                warnings.Add("腳本執行成功（exit code 0）但找不到 depth/ 輸出檔案，請檢查上面的 stdout 是否有額外警告。");
            }

            int? nextIndex = null;
            var totalSamples = 0;
            try
            {
                totalSamples = ReadWalkManifest(packageDir)["path"]?.Value<int>("sampleCount") ?? 0;
            }
            catch (Exception ex) when (!(ex is OutOfMemoryException))
            {
                warnings.Add($"無法讀取 manifest 的 sampleCount：{ex.Message}");
            }

            var depthFrameCount = Directory.Exists(depthDir)
                ? Directory.EnumerateFiles(depthDir, "frame_*.png", SearchOption.TopDirectoryOnly).Count()
                : 0;

            if (timedOut)
            {
                nextIndex = depthFrameCount;
                warnings.Add($"已達 {WalkDepthTimeout.TotalMinutes:0} 分鐘時間上限並中止；已完成 {depthFrameCount}"
                             + (totalSamples > 0 ? $"/{totalSamples}" : string.Empty)
                             + $" 格，請以 start={depthFrameCount} 續跑（已完成的格不會重做）。");
            }
            else if (totalSamples > 0 && depthFrameCount < totalSamples)
            {
                nextIndex = depthFrameCount;
                warnings.Add($"depth/ 只有 {depthFrameCount}/{totalSamples} 格；若非刻意指定 count，請以 start={depthFrameCount} 續跑。");
            }

            Logger.Info($"[走動深度] {(timedOut ? "時間上限中止" : "完成")}：{exportedCount} 檔"
                        + $"（深度影格 {depthFrameCount}{(totalSamples > 0 ? "/" + totalSamples : string.Empty)}），"
                        + $"耗時 {stopwatch.Elapsed.TotalSeconds:0.0} 秒");

            return new
            {
                packageDir,
                completed = !timedOut && nextIndex == null,
                nextIndex,
                totalSamples,
                depthFrameCount,
                depthDir,
                exported = exportedCount,
                elapsedMs = stopwatch.ElapsedMilliseconds,
                scriptPath,
                nodePath,
                logTail = TailLines(stdOutLines, 5),
                warnings
            };
        }

        private static List<string> TailLines(List<string> lines, int count)
        {
            List<string> snapshot;
            lock (lines) { snapshot = new List<string>(lines); }
            return snapshot.Skip(Math.Max(0, snapshot.Count - count)).ToList();
        }

        /// <summary>Best-effort parse of a trailing "exported N" style line; falls back silently if the script's stdout format differs.</summary>
        private static int? ParseWalkDepthExportedCount(List<string> lines)
        {
            List<string> snapshot;
            lock (lines) { snapshot = new List<string>(lines); }
            for (var i = snapshot.Count - 1; i >= 0; i--)
            {
                var m = Regex.Match(snapshot[i], @"exported\s+(\d+)", RegexOptions.IgnoreCase);
                if (m.Success && int.TryParse(m.Groups[1].Value, out var n))
                {
                    return n;
                }
            }

            return null;
        }

        /// <summary>Worker script search: dll folder → repo dev tree bridge/node/walkthrough/render-depth.mjs → %APPDATA%\RevitMCP\bridge\node\walkthrough\render-depth.mjs.</summary>
        private static string FindWalkDepthScript()
        {
            var asmDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty;

            var candidate = Path.Combine(asmDir, "render-depth.mjs");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            var dir = new DirectoryInfo(asmDir);
            for (var i = 0; i < 6 && dir != null; i++, dir = dir.Parent)
            {
                candidate = Path.Combine(dir.FullName, "bridge", "node", "walkthrough", "render-depth.mjs");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            candidate = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "RevitMCP", "bridge", "node", "walkthrough", "render-depth.mjs");
            return File.Exists(candidate) ? candidate : null;
        }

        /// <summary>Node executable search: PATH → common per-machine installs → nvm-windows/nvs version folders (best effort).</summary>
        private static string FindNodeExecutable()
        {
            if (CanRunNodeVersionCheck("node"))
            {
                return "node";
            }

            var flatCandidates = new List<string>();
            AddWalkNodeCandidate(flatCandidates, Environment.SpecialFolder.ProgramFiles, "nodejs", "node.exe");
            AddWalkNodeCandidate(flatCandidates, Environment.SpecialFolder.ProgramFilesX86, "nodejs", "node.exe");
            AddWalkNodeCandidate(flatCandidates, Environment.SpecialFolder.LocalApplicationData, "nvs", "default", "node.exe");
            AddWalkNodeCandidate(flatCandidates, Environment.SpecialFolder.ApplicationData, "nvm", "node.exe");

            foreach (var candidate in flatCandidates)
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            // nvm-windows / nvs store each installed version under its own subfolder; probe recursively as a fallback.
            foreach (var root in new[]
            {
                WalkSpecialFolderPath(Environment.SpecialFolder.LocalApplicationData, "nvs"),
                WalkSpecialFolderPath(Environment.SpecialFolder.ApplicationData, "nvm")
            })
            {
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
                {
                    continue;
                }

                try
                {
                    var found = Directory.EnumerateFiles(root, "node.exe", SearchOption.AllDirectories).FirstOrDefault();
                    if (found != null)
                    {
                        return found;
                    }
                }
                catch (IOException) { /* transient FS error: keep looking elsewhere */ }
                catch (UnauthorizedAccessException) { /* permission issue on a subfolder: keep looking elsewhere */ }
            }

            return null;
        }

        private static void AddWalkNodeCandidate(List<string> candidates, Environment.SpecialFolder folder, params string[] parts)
        {
            var baseDir = WalkSpecialFolderPath(folder, null);
            if (string.IsNullOrEmpty(baseDir))
            {
                return;
            }

            var combined = new List<string> { baseDir };
            combined.AddRange(parts);
            candidates.Add(Path.Combine(combined.ToArray()));
        }

        private static string WalkSpecialFolderPath(Environment.SpecialFolder folder, string subPath)
        {
            try
            {
                var baseDir = Environment.GetFolderPath(folder);
                if (string.IsNullOrEmpty(baseDir))
                {
                    return null;
                }

                return subPath == null ? baseDir : Path.Combine(baseDir, subPath);
            }
            catch
            {
                return null;
            }
        }

        private static bool CanRunNodeVersionCheck(string fileName)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = "--version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using (var proc = Process.Start(psi))
                {
                    return proc != null && proc.WaitForExit(5000) && proc.ExitCode == 0;
                }
            }
            catch
            {
                return false;
            }
        }

        // ────────────────────────────────────────────────
        // Package I/O helpers
        // ────────────────────────────────────────────────

        private static string RequireWalkPackageDir(JObject parameters)
        {
            var packageDir = parameters["packageDir"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(packageDir))
            {
                throw new ArgumentException("packageDir 為必填：請使用 export_walkthrough_package 回傳的 packageDir。");
            }

            if (!File.Exists(Path.Combine(packageDir, WalkManifestFile)))
            {
                throw new ArgumentException($"「{packageDir}」不是走動展示交付包（找不到 manifest.json）。");
            }

            return packageDir;
        }

        private static JObject ReadWalkManifest(string packageDir)
        {
            var text = File.ReadAllText(Path.Combine(packageDir, WalkManifestFile), Encoding.UTF8);
            return JObject.Parse(text);
        }

        /// <summary>Largest GLB that is still base64-embedded into data.js (browsers struggle with larger strings).</summary>
        private const long WalkMaxEmbeddedGlbBytes = 150L * 1024 * 1024;

        /// <summary>
        /// Writes manifest.json and data.js (window.WALK_DATA). The model is embedded when manifest.model.embedded is true:
        /// from model.glb when present (streamed base64), otherwise the previous data.js payload is preserved.
        /// </summary>
        private static void WriteWalkManifest(string packageDir, JObject manifest)
        {
            var utf8 = new UTF8Encoding(false);
            File.WriteAllText(Path.Combine(packageDir, WalkManifestFile), manifest.ToString(Formatting.Indented), utf8);

            var glbPath = Path.Combine(packageDir, WalkModelFile);
            var dataPath = Path.Combine(packageDir, WalkDataJsFile);
            var model = manifest["model"] as JObject;
            var wantModel = model != null && (model.Value<bool?>("embedded") ?? false);
            string preservedBase64 = null;
            if (wantModel && !File.Exists(glbPath) && File.Exists(dataPath))
            {
                preservedBase64 = ExtractWalkGlbBase64(dataPath);
            }

            var tempPath = dataPath + ".tmp";
            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, utf8))
            {
                writer.Write("window.WALK_DATA = {\"manifest\": ");
                writer.Write(manifest.ToString(Formatting.None));
                writer.Write(", \"glbBase64\": ");
                if (wantModel && File.Exists(glbPath))
                {
                    writer.Write('"');
                    writer.Flush();
                    using (var glb = File.OpenRead(glbPath))
                    using (var base64 = new System.Security.Cryptography.CryptoStream(
                               new NonClosingStream(stream), new System.Security.Cryptography.ToBase64Transform(),
                               System.Security.Cryptography.CryptoStreamMode.Write))
                    {
                        glb.CopyTo(base64, 1 << 20);
                        base64.FlushFinalBlock();
                    }

                    writer.Write('"');
                }
                else if (preservedBase64 != null)
                {
                    writer.Write('"');
                    writer.Write(preservedBase64);
                    writer.Write('"');
                }
                else
                {
                    writer.Write("null");
                }

                writer.Write("};\n");
            }

            if (File.Exists(dataPath))
            {
                File.Replace(tempPath, dataPath, null);
            }
            else
            {
                File.Move(tempPath, dataPath);
            }
        }

        private static string ExtractWalkGlbBase64(string dataPath)
        {
            const string marker = "\"glbBase64\": \"";
            var text = File.ReadAllText(dataPath, Encoding.UTF8);
            var start = text.LastIndexOf(marker, StringComparison.Ordinal);
            if (start < 0)
            {
                return null;
            }

            start += marker.Length;
            var end = text.IndexOf('"', start);
            return end > start ? text.Substring(start, end - start) : null;
        }

        /// <summary>Lets CryptoStream finish its final block without closing the underlying data.js stream.</summary>
        private sealed class NonClosingStream : Stream
        {
            private readonly Stream _inner;
            public NonClosingStream(Stream inner) { _inner = inner; }
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => _inner.Length;
            public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }
            public override void Flush() => _inner.Flush();
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
            protected override void Dispose(bool disposing) => _inner.Flush();
        }

        private static void AppendWalkWarnings(JObject manifest, List<string> warnings)
        {
            if (warnings.Count == 0)
            {
                return;
            }

            var array = manifest["warnings"] as JArray ?? new JArray();
            foreach (var warning in warnings)
            {
                if (!array.Values<string>().Contains(warning))
                {
                    array.Add(warning);
                }
            }

            manifest["warnings"] = array;
        }

        /// <summary>Viewer template search: dll folder → repo viewer/walkthrough (dev) → %APPDATA%\RevitMCP\viewer\walkthrough.</summary>
        private static string FindWalkViewerTemplate()
        {
            var asmDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty;
            var candidate = Path.Combine(asmDir, "viewer", "walkthrough");
            if (File.Exists(Path.Combine(candidate, "index.html")))
            {
                return candidate;
            }

            var dir = new DirectoryInfo(asmDir);
            for (var i = 0; i < 6 && dir != null; i++, dir = dir.Parent)
            {
                candidate = Path.Combine(dir.FullName, "viewer", "walkthrough");
                if (File.Exists(Path.Combine(candidate, "index.html")))
                {
                    return candidate;
                }
            }

            candidate = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RevitMCP", "viewer", "walkthrough");
            return File.Exists(Path.Combine(candidate, "index.html")) ? candidate : null;
        }

        private static bool CopyWalkViewerTemplate(string packageDir, List<string> warnings)
        {
            var template = FindWalkViewerTemplate();
            if (template == null)
            {
                warnings.Add("找不到網頁 viewer 模板（viewer/walkthrough），交付包只有資料沒有 index.html；請重新執行 scripts/install-addon.ps1。");
                return false;
            }

            var skipDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "dev", "node_modules", ".git" };
            var skipFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "CONTRACT.md", "build-vendor.mjs", "package.json", "package-lock.json" };
            var root = new DirectoryInfo(template);
            foreach (var file in root.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                var relative = file.FullName.Substring(root.FullName.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (parts.Take(parts.Length - 1).Any(skipDirectories.Contains)
                    || skipFiles.Contains(file.Name)
                    || file.Name.EndsWith(".test.cjs", StringComparison.OrdinalIgnoreCase)
                    || file.Name.EndsWith(".test.js", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var destination = Path.Combine(packageDir, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination));
                file.CopyTo(destination, true);
            }

            return true;
        }

        private static Vec3 WalkToVec3(XYZ v) => new Vec3(v.X, v.Y, v.Z);

        private static XYZ WalkToXyz(Vec3 v) => new XYZ(v.X, v.Y, v.Z);

        private static Vec3 WalkToModel(XYZ internalPoint, Vec3 offset) => new Vec3(
            internalPoint.X * WalkFeetToMeters - offset.X,
            internalPoint.Y * WalkFeetToMeters - offset.Y,
            internalPoint.Z * WalkFeetToMeters - offset.Z);

        /// <summary>
        /// Section box (Revit feet, world-axis-aligned) around everything the walk can actually see:
        /// the bounding box of the sampled camera path, grown by <paramref name="marginMeters"/>
        /// horizontally and by below/above in Z. Exporting the glTF through this keeps every
        /// category (doors, windows, curtain walls, furniture) at full fidelity inside the walk
        /// volume instead of forcing a category cull — the model only loses what the walk can
        /// never reach, which is also what made the export overrun its triangle budget.
        /// Returns null when the manifest has no usable path samples.
        /// </summary>
        private static BoundingBoxXYZ ComputeWalkPathSectionBox(
            JObject manifest, Vec3 offset, double marginMeters, double belowMeters, double aboveMeters)
        {
            var samples = manifest["path"]?["samples"] as JArray;
            if (samples == null || samples.Count == 0)
            {
                return null;
            }

            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            var found = 0;
            foreach (var sample in samples.OfType<JObject>())
            {
                var p = sample["p"] as JArray;
                if (p == null || p.Count < 3)
                {
                    continue;
                }

                // Samples are in package/model coordinates (origin offset removed) — add it back
                // to land in Revit project coordinates, which is what SetSectionBox expects.
                var x = p[0].Value<double>() + offset.X;
                var y = p[1].Value<double>() + offset.Y;
                var z = p[2].Value<double>() + offset.Z;
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                minZ = Math.Min(minZ, z); maxZ = Math.Max(maxZ, z);
                found++;
            }

            if (found == 0)
            {
                return null;
            }

            return new BoundingBoxXYZ
            {
                Transform = Transform.Identity,
                Min = new XYZ(
                    (minX - marginMeters) / WalkFeetToMeters,
                    (minY - marginMeters) / WalkFeetToMeters,
                    (minZ - belowMeters) / WalkFeetToMeters),
                Max = new XYZ(
                    (maxX + marginMeters) / WalkFeetToMeters,
                    (maxY + marginMeters) / WalkFeetToMeters,
                    (maxZ + aboveMeters) / WalkFeetToMeters)
            };
        }

        private static Vec3 WalkReadVec3(JToken token)
        {
            if (token is JArray array && array.Count >= 3)
            {
                return new Vec3(array[0].Value<double>(), array[1].Value<double>(), array[2].Value<double>());
            }

            return Vec3.Zero;
        }

        private static int WalkClamp(int value, int min, int max) => Math.Max(min, Math.Min(max, value));

        private static ElementId WalkElementId(long value)
        {
#if REVIT2025_OR_GREATER
            return new ElementId(value);
#else
            return new ElementId((int)value);
#endif
        }
    }
}
