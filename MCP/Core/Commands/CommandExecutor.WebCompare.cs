using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitMCP.Core.Walkthrough;

namespace RevitMCP.Core
{
    /// <summary>
    /// export_web_compare: one call produces the five-file set used to compare Revit display modes against a web
    /// viewer (issue-manager S1): per-display-mode PNGs, IFC, a per-element GLB and the shared camera
    /// (camera.txt for humans, camera.json for Playwright). Method notes: docs/WEB_COMPARE_EXPORT.md.
    /// </summary>
    public partial class CommandExecutor
    {
        private const string WebCompareDefaultViewName = "WEB_COMPARE";
        private const string WebCompareDefaultOutputDir = @"C:\Project\issue-manager\spikes\s1-glb\input";
        private const long WebCompareLargeFileBytes = 500L * 1024 * 1024;
        private const double WebCompareTargetDistanceMeters = 10.0;

        /// <summary>Revit display style, file key, and the web-viewer mode it is compared against (null = no web counterpart).</summary>
        private static readonly (DisplayStyle Style, string Key, string WebMode)[] WebCompareModes =
        {
            (DisplayStyle.Wireframe, "wireframe", "wireframe"),
            (DisplayStyle.HLR, "hiddenLine", "hiddenLine"),
            (DisplayStyle.Shading, "shaded", "shaded"),
            (DisplayStyle.ShadingWithEdges, "shadedEdges", null),
            (DisplayStyle.FlatColors, "consistent", "consistent"),
            (DisplayStyle.Realistic, "realistic", "realistic"),
        };

        private object ExportWebCompare(JObject parameters)
        {
            var uiDoc = _uiApp.ActiveUIDocument ?? throw new InvalidOperationException("目前沒有開啟的 Revit 專案。");
            var doc = uiDoc.Document;
            var warnings = new List<string>();
            var files = new List<object>();
            var total = Stopwatch.StartNew();

            var viewName = parameters["viewName"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(viewName))
            {
                viewName = WebCompareDefaultViewName;
            }

            var outputDir = parameters["outputDir"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(outputDir))
            {
                outputDir = WebCompareDefaultOutputDir;
            }

            var pixelSize = parameters["pixelSize"]?.Value<int?>() ?? 1920;
            if (pixelSize < 256 || pixelSize > 8000)
            {
                throw new ArgumentException("pixelSize 需介於 256 與 8000。");
            }

            var includeImages = parameters["includeImages"]?.Value<bool?>() ?? true;
            var includeIfc = parameters["includeIfc"]?.Value<bool?>() ?? true;
            var includeGlb = parameters["includeGlb"]?.Value<bool?>() ?? true;
            var maxTriangles = parameters["maxTriangles"]?.Value<int?>() ?? 3_000_000;

            Directory.CreateDirectory(outputDir);

            var originalView = uiDoc.ActiveView;
            var view = FindOrCreateWebCompareView(doc, viewName, originalView, warnings, out var viewCreated);

            var originalStyle = view.DisplayStyle;
            bool activeSwitched = false;
            JObject camera;
            int imageWidth = 0, imageHeight = 0;

            try
            {
                if (originalView.Id != view.Id)
                {
                    uiDoc.ActiveView = view;
                    activeSwitched = true;
                }

                camera = BuildWebCompareCamera(uiDoc, view, warnings, out var orientation, out var orthoSizeMeters);

                if (includeImages)
                {
                    var produced = new Dictionary<string, string>();
                    foreach (var mode in WebCompareModes)
                    {
                        var sw = Stopwatch.StartNew();
                        var target = Path.Combine(outputDir, $"revit_{mode.Key}.png");
                        try
                        {
                            SetWebCompareDisplayStyle(doc, view, mode.Style, warnings);
                            uiDoc.RefreshActiveView();
                            ExportWebCompareImage(doc, uiDoc, target, pixelSize);
                            produced[mode.Key] = target;
                            files.Add(WebCompareFileEntry($"revit_{mode.Key}.png", target, sw, warnings));
                        }
                        catch (Exception ex)
                        {
                            warnings.Add($"revit_{mode.Key}.png 匯出失敗：{ex.Message}");
                            files.Add(new { name = $"revit_{mode.Key}.png", path = target, ok = false, error = ex.Message });
                        }
                    }

                    foreach (var pair in new[] { ("consistent", "cpc_consistent.png"), ("realistic", "cpc_realistic.png") })
                    {
                        if (!produced.TryGetValue(pair.Item1, out var src))
                        {
                            continue;
                        }

                        var sw = Stopwatch.StartNew();
                        var dest = Path.Combine(outputDir, pair.Item2);
                        File.Copy(src, dest, true);
                        files.Add(WebCompareFileEntry(pair.Item2, dest, sw, warnings));
                    }

                    var firstPng = produced.Values.FirstOrDefault();
                    if (firstPng != null && TryReadPngSize(firstPng, out imageWidth, out imageHeight))
                    {
                        camera["image"] = new JObject { ["widthPx"] = imageWidth, ["heightPx"] = imageHeight };
                        WebCompareCheckAspect(camera, imageWidth, imageHeight, warnings);
                    }
                }
            }
            finally
            {
                if (view.DisplayStyle != originalStyle)
                {
                    try
                    {
                        SetWebCompareDisplayStyle(doc, view, originalStyle, warnings);
                    }
                    catch (Exception ex)
                    {
                        warnings.Add($"還原視圖顯示樣式失敗（請手動改回 {originalStyle}）：{ex.Message}");
                    }
                }

                if (activeSwitched)
                {
                    try
                    {
                        uiDoc.ActiveView = originalView;
                    }
                    catch (Exception ex)
                    {
                        warnings.Add($"還原作用中視圖失敗：{ex.Message}");
                    }
                }
            }

            if (includeIfc)
            {
                var sw = Stopwatch.StartNew();
                var ifcPath = Path.Combine(outputDir, "cpc.ifc");
                var ifcVersion = ExportWebCompareIfc(doc, view, outputDir, "cpc", warnings);
                if (ifcVersion != null && File.Exists(ifcPath))
                {
                    var entry = WebCompareFileEntry("cpc.ifc", ifcPath, sw, warnings);
                    files.Add(entry);
                    camera["ifcSchema"] = ifcVersion;
                }
                else
                {
                    files.Add(new { name = "cpc.ifc", path = ifcPath, ok = false, error = "IFC 匯出失敗，見 warnings" });
                }
            }

            Vec3 offset = camera["gltf"]?["originOffsetMeters"] is JArray off
                ? new Vec3(off[0].Value<double>(), off[1].Value<double>(), off[2].Value<double>())
                : Vec3.Zero;
            int glbTriangles = 0, glbNodes = 0;
            if (includeGlb)
            {
                var sw = Stopwatch.StartNew();
                var glbPath = Path.Combine(outputDir, "cpc.glb");
                try
                {
                    ExportWebCompareGlb(doc, view, glbPath, offset, maxTriangles, warnings, out glbTriangles, out glbNodes);
                    files.Add(WebCompareFileEntry("cpc.glb", glbPath, sw, warnings, new JObject
                    {
                        ["triangleCount"] = glbTriangles,
                        ["elementNodeCount"] = glbNodes
                    }));
                }
                catch (Exception ex)
                {
                    warnings.Add($"cpc.glb 匯出失敗：{ex.Message}");
                    files.Add(new { name = "cpc.glb", path = glbPath, ok = false, error = ex.Message });
                }
            }

            var cameraTxt = Path.Combine(outputDir, "camera.txt");
            var cameraJson = Path.Combine(outputDir, "camera.json");
            var swCam = Stopwatch.StartNew();
            camera["modes"] = new JArray(WebCompareModes.Select(m => new JObject
            {
                ["revitDisplayStyle"] = m.Style.ToString(),
                ["revitFile"] = $"revit_{m.Key}.png",
                ["webMode"] = m.WebMode == null ? JValue.CreateNull() : (JToken)m.WebMode,
                ["webFile"] = m.WebMode == null ? JValue.CreateNull() : (JToken)$"web_{m.WebMode}.png"
            }));
            File.WriteAllText(cameraJson, camera.ToString(Formatting.Indented), new UTF8Encoding(false));
            files.Add(WebCompareFileEntry("camera.json", cameraJson, swCam, warnings));
            File.WriteAllText(cameraTxt, BuildWebCompareCameraText(camera), new UTF8Encoding(false));
            files.Add(WebCompareFileEntry("camera.txt", cameraTxt, swCam, warnings));

            Logger.Info($"[WebCompare] 完成：{files.Count} 個檔、{total.Elapsed.TotalSeconds:0.0} 秒、{warnings.Count} 則警告");

            return new
            {
                outputDir,
                viewName = view.Name,
                viewCreated,
                files,
                camera = camera["gltf"],
                totalElapsedMs = total.ElapsedMilliseconds,
                warnings
            };
        }

        // ── view ────────────────────────────────────────

        private static View3D FindOrCreateWebCompareView(Document doc, string viewName, View activeView, List<string> warnings, out bool created)
        {
            created = false;
            var existing = new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>()
                .FirstOrDefault(v => !v.IsTemplate && string.Equals(v.Name, viewName, StringComparison.Ordinal));
            if (existing != null)
            {
                if (existing.IsLocked)
                {
                    warnings.Add($"視圖「{viewName}」已鎖定方位；本次沿用其相機，不解鎖。");
                }

                return existing;
            }

            if (!(activeView is View3D source) || source.IsTemplate)
            {
                throw new InvalidOperationException(
                    $"找不到 3D 視圖「{viewName}」，且目前作用中的視圖不是 3D 視圖，無法複製建立。請先切到想比對的 3D 視圖再呼叫。");
            }

            using (var tx = new Transaction(doc, "WEB_COMPARE：建立比對用 3D 視圖"))
            {
                tx.Start();
                var newId = source.Duplicate(ViewDuplicateOption.Duplicate);
                var copy = (View3D)doc.GetElement(newId);
                copy.Name = viewName;
                if (copy.IsLocked)
                {
                    copy.Unlock();
                }

                if (copy.ViewTemplateId != ElementId.InvalidElementId)
                {
                    copy.ViewTemplateId = ElementId.InvalidElementId;
                    warnings.Add("已從新建的比對視圖移除視圖樣板，否則樣板會鎖住顯示樣式而無法切換。");
                }

                tx.Commit();
                created = true;
                return copy;
            }
        }

        private static void SetWebCompareDisplayStyle(Document doc, View3D view, DisplayStyle style, List<string> warnings)
        {
            using (var tx = new Transaction(doc, "WEB_COMPARE：切換顯示樣式"))
            {
                tx.Start();
                view.DisplayStyle = style;
                tx.Commit();
            }

            if (view.DisplayStyle != style)
            {
                warnings.Add($"視圖顯示樣式未能切為 {style}（目前 {view.DisplayStyle}），可能被視圖樣板控制；該張 PNG 的樣式不可信。");
            }
        }

        // ── images ──────────────────────────────────────

        private static void ExportWebCompareImage(Document doc, Autodesk.Revit.UI.UIDocument uiDoc, string target, int pixelSize)
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "webcompare_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var options = new ImageExportOptions
                {
                    ExportRange = ExportRange.VisibleRegionOfCurrentView,
                    FilePath = Path.Combine(tempDir, "img"),
                    HLRandWFViewsFileType = ImageFileType.PNG,
                    ShadowViewsFileType = ImageFileType.PNG,
                    ImageResolution = ImageResolution.DPI_150,
                    ZoomType = ZoomFitType.FitToPage,
                    FitDirection = FitDirectionType.Horizontal,
                    PixelSize = pixelSize
                };
                doc.ExportImage(options);

                var produced = Directory.GetFiles(tempDir, "*.png")
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault()
                    ?? throw new InvalidOperationException("ExportImage 沒有產生 PNG 檔。");
                if (File.Exists(target))
                {
                    File.Delete(target);
                }

                File.Move(produced, target);
            }
            finally
            {
                try
                {
                    Directory.Delete(tempDir, true);
                }
                catch (IOException)
                {
                    // Temp cleanup is best-effort.
                }
            }
        }

        private static bool TryReadPngSize(string path, out int width, out int height)
        {
            width = height = 0;
            try
            {
                using (var stream = File.OpenRead(path))
                {
                    var header = new byte[24];
                    if (stream.Read(header, 0, 24) < 24 || header[1] != (byte)'P' || header[2] != (byte)'N' || header[3] != (byte)'G')
                    {
                        return false;
                    }

                    width = (header[16] << 24) | (header[17] << 16) | (header[18] << 8) | header[19];
                    height = (header[20] << 24) | (header[21] << 16) | (header[22] << 8) | header[23];
                    return width > 0 && height > 0;
                }
            }
            catch (IOException)
            {
                return false;
            }
        }

        // ── camera ──────────────────────────────────────

        private static JObject BuildWebCompareCamera(Autodesk.Revit.UI.UIDocument uiDoc, View3D view, List<string> warnings,
            out ViewOrientation3D orientation, out double[] orthoSizeMeters)
        {
            const double toM = CameraPathMath.FeetToMeters;
            orientation = view.GetOrientation();
            var eye = orientation.EyePosition;
            var fwd = orientation.ForwardDirection.Normalize();
            var up = orientation.UpDirection.Normalize();
            var right = fwd.CrossProduct(up).Normalize();
            orthoSizeMeters = null;

            var eyeM = new Vec3(eye.X * toM, eye.Y * toM, eye.Z * toM);
            var fwdV = new Vec3(fwd.X, fwd.Y, fwd.Z);
            var upV = new Vec3(up.X, up.Y, up.Z);
            var targetM = eyeM + fwdV * WebCompareTargetDistanceMeters;

            // Origin offset keeps float32 GLB coordinates small: horizontal position of the eye, elevation untouched.
            var offset = new Vec3(Math.Round(eyeM.X, 3), Math.Round(eyeM.Y, 3), 0.0);

            Vec3 ToGltfPoint(Vec3 p) => new Vec3(p.X - offset.X, p.Z - offset.Z, -(p.Y - offset.Y));
            Vec3 ToGltfDir(Vec3 d) => new Vec3(d.X, d.Z, -d.Y);
            JArray Arr(Vec3 v) => new JArray(Math.Round(v.X, 4), Math.Round(v.Y, 4), Math.Round(v.Z, 4));

            var camera = new JObject
            {
                ["schemaVersion"] = 1,
                ["viewName"] = view.Name,
                ["isPerspective"] = view.IsPerspective,
                ["units"] = "meters",
                ["revit"] = new JObject
                {
                    ["coordinateSystem"] = "Revit internal coordinates (Z up, right-handed), meters; not shared/survey coordinates",
                    ["eye"] = Arr(eyeM),
                    ["forward"] = Arr(fwdV),
                    ["up"] = Arr(upV),
                    ["target"] = Arr(targetM),
                    ["targetDistanceMeters"] = WebCompareTargetDistanceMeters
                },
                ["gltf"] = new JObject
                {
                    ["coordinateSystem"] = "glTF (Y up, right-handed), meters, origin offset subtracted; same space as cpc.glb",
                    ["transform"] = "point: gltf = (x - ox, z - oz, -(y - oy)); direction: gltf = (x, z, -y); (x,y,z) = Revit meters",
                    ["originOffsetMeters"] = Arr(offset),
                    ["eye"] = Arr(ToGltfPoint(eyeM)),
                    ["target"] = Arr(ToGltfPoint(targetM)),
                    ["up"] = Arr(ToGltfDir(upV)),
                    ["forward"] = Arr(ToGltfDir(fwdV))
                }
            };

            if (view.IsPerspective)
            {
                var fov = ComputeWebCompareFov(view, orientation);
                if (fov.IsValid)
                {
                    camera["projection"] = new JObject
                    {
                        ["type"] = "perspective",
                        ["fovVerticalDeg"] = Math.Round(fov.FovYDeg, 3),
                        ["fovHorizontalDeg"] = Math.Round(fov.FovXDeg, 3),
                        ["aspect"] = Math.Round(fov.Aspect, 5),
                        ["symmetric"] = fov.IsSymmetric,
                        ["source"] = "view crop box (Max corner on front clip plane); three.js PerspectiveCamera.fov = fovVerticalDeg"
                    };
                    if (!fov.IsSymmetric)
                    {
                        warnings.Add("透視裁剪框不對稱（相機有位移），three.js 預設相機無法完全重現，網頁截圖構圖會略有偏差。");
                    }
                }
                else
                {
                    camera["projection"] = new JObject { ["type"] = "perspective", ["fovVerticalDeg"] = JValue.CreateNull() };
                    warnings.Add("無法由裁剪框算出透視視角（FOV），camera.json 的 fov 為 null，請手動指定。");
                }
            }
            else
            {
                var projection = new JObject { ["type"] = "orthographic" };
                try
                {
                    var uiView = uiDoc.GetOpenUIViews().FirstOrDefault(v => v.ViewId == view.Id);
                    if (uiView != null)
                    {
                        var corners = uiView.GetZoomCorners();
                        var diag = corners[1] - corners[0];
                        var wM = Math.Abs(diag.DotProduct(right)) * toM;
                        var hM = Math.Abs(diag.DotProduct(up)) * toM;
                        orthoSizeMeters = new[] { wM, hM };
                        projection["widthMeters"] = Math.Round(wM, 4);
                        projection["heightMeters"] = Math.Round(hM, 4);
                        projection["aspect"] = Math.Round(wM / hM, 5);
                        projection["source"] = "UIView.GetZoomCorners (visible region of the view window, meters)";
                    }
                }
                catch (Exception ex)
                {
                    warnings.Add($"讀取正交視窗範圍失敗：{ex.Message}");
                }

                if (orthoSizeMeters == null)
                {
                    warnings.Add("正交視圖的可見範圍未取得，camera.json 無 widthMeters／heightMeters。");
                }

                camera["projection"] = projection;
            }

            return camera;
        }

        private static FieldOfView ComputeWebCompareFov(View3D view, ViewOrientation3D orientation)
        {
            try
            {
                var crop = view.CropBox;
                if (crop == null)
                {
                    return new FieldOfView(0, 0, false, false);
                }

                var eyeLocal = crop.Transform.Inverse.OfPoint(orientation.EyePosition);
                return CameraPathMath.FieldOfViewFromCropBox(
                    new Vec3(crop.Min.X, crop.Min.Y, crop.Min.Z),
                    new Vec3(crop.Max.X, crop.Max.Y, crop.Max.Z),
                    new Vec3(eyeLocal.X, eyeLocal.Y, eyeLocal.Z));
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                return new FieldOfView(0, 0, false, false);
            }
        }

        private static void WebCompareCheckAspect(JObject camera, int widthPx, int heightPx, List<string> warnings)
        {
            var aspect = camera["projection"]?["aspect"]?.Value<double?>();
            if (aspect == null || heightPx == 0)
            {
                return;
            }

            var imageAspect = (double)widthPx / heightPx;
            if (Math.Abs(imageAspect - aspect.Value) > 0.02 * aspect.Value)
            {
                warnings.Add($"PNG 長寬比 {imageAspect:0.000} 與相機 aspect {aspect.Value:0.000} 差異超過 2%（視窗比例與裁剪框不同）；網頁截圖請以 image.widthPx／heightPx 為準，並核對構圖。");
            }
        }

        private static string BuildWebCompareCameraText(JObject camera)
        {
            string V(JToken token)
            {
                var a = (JArray)token;
                return string.Format(CultureInfo.InvariantCulture, "({0:0.###}, {1:0.###}, {2:0.###})",
                    a[0].Value<double>(), a[1].Value<double>(), a[2].Value<double>());
            }

            var revit = camera["revit"];
            var sb = new StringBuilder();
            sb.AppendLine("視圖名稱: " + camera["viewName"]);
            sb.AppendLine("透視: " + ((camera["isPerspective"]?.Value<bool>() ?? false) ? "是" : "否（正交）"));
            sb.AppendLine("座標: Revit 內部座標，公尺，Z 向上（camera.json 另含 glTF 座標）");
            sb.AppendLine("眼點: " + V(revit["eye"]));
            sb.AppendLine("目標: " + V(revit["target"]) + $"（眼點沿視線 {WebCompareTargetDistanceMeters:0} m）");
            sb.AppendLine("上向量: " + V(revit["up"]));
            sb.AppendLine("視線方向: " + V(revit["forward"]));
            var fov = camera["projection"]?["fovVerticalDeg"];
            if (fov != null && fov.Type != JTokenType.Null)
            {
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "垂直視角: {0:0.##} 度", fov.Value<double>()));
            }

            sb.AppendLine("描述: （請在此補一行中文描述，例如：從東南側看主入口與雨庇）");
            return sb.ToString();
        }

        // ── IFC ─────────────────────────────────────────

        /// <summary>Returns the schema actually written ("IFC4RV" / "IFC2x3CV2") or null when both attempts failed.</summary>
        private static string ExportWebCompareIfc(Document doc, View3D view, string folder, string name, List<string> warnings)
        {
            var attempts = new[]
            {
                (IFCVersion.IFC4RV, "IFC4 Reference View"),
                (IFCVersion.IFC2x3CV2, "IFC2x3 Coordination View 2.0")
            };

            for (var i = 0; i < attempts.Length; i++)
            {
                var (version, label) = attempts[i];
                var options = new IFCExportOptions { FileVersion = version, FilterViewId = view.Id };
                options.AddOption("ExportInternalRevitPropertySets", "true");
                options.AddOption("ExportIFCCommonPropertySets", "true");

                var error = TryExportIfc(doc, folder, name, options);
                if (error == null)
                {
                    if (i > 0)
                    {
                        warnings.Add($"IFC4 Reference View 匯出失敗，已改用 {label}。");
                    }

                    return version.ToString();
                }

                warnings.Add($"{label} 匯出失敗：{error}");
            }

            return null;
        }

        private static string TryExportIfc(Document doc, string folder, string name, IFCExportOptions options)
        {
            try
            {
                if (doc.Export(folder, name, options))
                {
                    return null;
                }

                return "Document.Export 回傳 false";
            }
            catch (Exception ex)
            {
                // Some Revit/IFC-exporter builds require an open transaction; retry once inside one and roll it back.
                if (ex.Message.IndexOf("ransaction", StringComparison.OrdinalIgnoreCase) < 0 &&
                    !(ex is Autodesk.Revit.Exceptions.InvalidOperationException))
                {
                    return ex.Message;
                }
            }

            try
            {
                using (var tx = new Transaction(doc, "WEB_COMPARE：IFC 匯出"))
                {
                    tx.Start();
                    var ok = doc.Export(folder, name, options);
                    tx.RollBack();
                    return ok ? null : "Document.Export 回傳 false";
                }
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        // ── GLB ─────────────────────────────────────────

        private static void ExportWebCompareGlb(Document doc, View3D source, string glbPath, Vec3 offset, int maxTriangles,
            List<string> warnings, out int triangleCount, out int elementNodeCount)
        {
            ElementId tempViewId;
            using (var tx = new Transaction(doc, "WEB_COMPARE：建立 GLB 匯出暫存視圖"))
            {
                tx.Start();
                tempViewId = source.Duplicate(ViewDuplicateOption.Duplicate);
                var temp = (View3D)doc.GetElement(tempViewId);
                if (temp.IsLocked)
                {
                    temp.Unlock();
                }

                // Perspective views clip to the frustum; the web viewer needs the whole visible model.
                if (temp.IsPerspective && temp.CanToggleBetweenPerspectiveAndIsometric())
                {
                    temp.ToggleToIsometric();
                }

                if (temp.IsPerspective)
                {
                    warnings.Add("GLB 暫存視圖無法切換為等角，透視裁剪範圍外的元件可能不會匯出。");
                }
                else
                {
                    temp.CropBoxActive = false;
                }

                tx.Commit();
            }

            var builder = new GltfBuilder();
            RevitGltfExportContext context;
            try
            {
                var tempView = (View3D)doc.GetElement(tempViewId);
                context = new RevitGltfExportContext(doc, builder, offset)
                {
                    TrackElements = true,
                    TriangleBudget = maxTriangles
                };
                using (var exporter = new CustomExporter(doc, context))
                {
                    exporter.IncludeGeometricObjects = false;
                    exporter.ShouldStopOnError = false;
                    exporter.Export(tempView);
                }
            }
            finally
            {
                using (var tx = new Transaction(doc, "WEB_COMPARE：刪除 GLB 匯出暫存視圖"))
                {
                    tx.Start();
                    doc.Delete(tempViewId);
                    tx.Commit();
                }
            }

            if (context.BudgetExceeded)
            {
                warnings.Add($"GLB 三角面超過上限 {context.TriangleBudget:N0}，匯出提前停止（模型不完整）；請在 {source.Name} 隱藏家具、植栽、機電等類別，或用 maxTriangles 提高上限。");
            }

            File.WriteAllBytes(glbPath, builder.BuildGlb());
            triangleCount = builder.TriangleCount;
            elementNodeCount = builder.ElementNodeCount;
        }

        // ── shared ──────────────────────────────────────

        private static object WebCompareFileEntry(string name, string path, Stopwatch sw, List<string> warnings, JObject extra = null)
        {
            var size = new FileInfo(path).Length;
            if (size > WebCompareLargeFileBytes)
            {
                warnings.Add($"{name} 大小 {size / 1024.0 / 1024.0:0} MB，超過 500 MB。");
            }

            var entry = new JObject
            {
                ["name"] = name,
                ["path"] = path,
                ["ok"] = true,
                ["sizeBytes"] = size,
                ["sizeMB"] = Math.Round(size / 1024.0 / 1024.0, 2),
                ["elapsedMs"] = sw.ElapsedMilliseconds
            };
            if (extra != null)
            {
                entry.Merge(extra);
            }

            return entry;
        }
    }
}
