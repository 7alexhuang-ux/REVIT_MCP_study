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
            if (parameters["designOptions"]?.Value<bool?>() == true)
                return ExportDesignOptions(parameters);
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
            var splitOptions = ParseWebCompareSplitOptions(parameters, maxTriangles);
            var contourInterval = parameters["contourIntervalMeters"]?.Value<double?>() ?? 1.0;
            var excludedCategories = WebCompareStringList(parameters["excludeCategories"]);
            var excludedElements = WebCompareStringList(parameters["excludeElements"]);
            var maxElementTriangles = parameters["maxElementTriangles"]?.Value<int?>() ?? 100_000;
            var dumpAppearance = parameters["dumpAppearance"]?.Value<bool?>() ?? false;
            var heavyOptions = new WebCompareHeavyOptions
            {
                OverCapMode = (parameters["overCapMode"]?.Value<string>() ?? "simplify").Trim().ToLowerInvariant(),
                // Default from the S1 round-3 triangle study (ROUND3.md / ADR): 10 % keeps the statue and sofas visually identical.
                SimplifyRatio = parameters["simplifyRatio"]?.Value<double?>() ?? 0.1,
                LevelOfDetail = parameters["levelOfDetail"]?.Value<int?>() ?? -1
            };

            Directory.CreateDirectory(outputDir);

            var sectionOptions = ParseWebCompareSectionOptions(parameters);
            var originalView = uiDoc.ActiveView;
            var view = FindOrCreateWebCompareView(doc, viewName, originalView, warnings, out var viewCreated);

            DisableWebCompareFarClip(doc, view, warnings);

            if (parameters["camera"] is JObject cameraParams)
            {
                try
                {
                    ApplyWebCompareCamera(doc, view, cameraParams);
                }
                catch (Exception ex)
                {
                    warnings.Add($"相機設定失敗：{ex.Message}");
                }
            }

            // Section box first: every PNG, the IFC and the GLB temp view (a duplicate) then share the same range.
            var linkedDocs = WebCompareDocuments(doc);
            var sectionReport = new JObject { ["mode"] = sectionOptions.Mode };
            var terrainPieces = new List<WebCompareTerrainPiece>();
            MeterBox? sectionBox = null;
            try
            {
                sectionBox = ResolveWebCompareSectionBox(linkedDocs, sectionOptions, terrainPieces, warnings, sectionReport);
                if (sectionBox.HasValue && sectionOptions.Apply)
                {
                    ApplyWebCompareSectionBox(doc, view, sectionBox.Value);
                    sectionReport["applied"] = true;
                }
            }
            catch (Exception ex)
            {
                sectionReport["error"] = ex.Message;
                warnings.Add($"section box 設定失敗：{ex.Message}");
            }

            var linkReport = new JObject();
            try
            {
                DescribeWebCompareLinks(doc, view, linkedDocs, linkReport);
            }
            catch (Exception ex)
            {
                linkReport["error"] = ex.Message;
            }

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

                    // 不再另存 cpc_consistent.png／cpc_realistic.png：它們與 revit_*.png 位元相同，只會造成「兩份圖像是兩個來源」的誤會。
                    foreach (var stale in new[] { "cpc_consistent.png", "cpc_realistic.png" })
                    {
                        var stalePath = Path.Combine(outputDir, stale);
                        if (File.Exists(stalePath))
                        {
                            File.Delete(stalePath);
                            warnings.Add($"已刪除舊版複本 {stale}（與 revit_*.png 相同，請改用 revit_consistent.png／revit_realistic.png）。");
                        }
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
                    ExportWebCompareGlb(doc, view, glbPath, offset, maxTriangles, warnings, out glbTriangles, out glbNodes,
                        linkedDocs, sectionOptions, sectionBox, terrainPieces, linkReport, contourInterval, excludedCategories,
                        excludedElements, maxElementTriangles, dumpAppearance, heavyOptions, null, splitOptions);
                    files.Add(WebCompareFileEntry("cpc.glb", glbPath, sw, warnings, new JObject
                    {
                        ["triangleCount"] = glbTriangles,
                        ["elementNodeCount"] = glbNodes
                    }));
                    AddWebCompareSplitFileEntries(files, splitOptions, sw, warnings);
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

            var result = new
            {
                outputDir,
                viewName = view.Name,
                viewState = DescribeWebCompareViewState(view),
                viewCreated,
                files,
                camera = camera["gltf"],
                sectionBox = sectionReport,
                linkDiagnostics = linkReport,
                totalElapsedMs = total.ElapsedMilliseconds,
                warnings
            };
            try
            {
                // The MCP client may time out long exports; the full report always lands next to the files.
                File.WriteAllText(Path.Combine(outputDir, "export_report.json"), JsonConvert.SerializeObject(result, Formatting.Indented), new UTF8Encoding(false));
            }
            catch (IOException ex)
            {
                Logger.Info("[WebCompare] 無法寫入 export_report.json：" + ex.Message);
            }

            return result;
        }

        /// <summary>
        /// A view that still has "Far Clip Active" makes ExportImage return an all-white PNG (observed with a 154 m far clip
        /// on a 190 m site), so the comparison view always runs with the far clip off.
        /// </summary>
        private static void DisableWebCompareFarClip(Document doc, View3D view, List<string> warnings)
        {
            try
            {
                var parameter = view.get_Parameter(BuiltInParameter.VIEWER_BOUND_ACTIVE_FAR);
                if (parameter == null || parameter.IsReadOnly || parameter.AsInteger() == 0)
                {
                    return;
                }

                using (var tx = new Transaction(doc, "WEB_COMPARE：關閉遠裁剪"))
                {
                    tx.Start();
                    parameter.Set(0);
                    tx.Commit();
                }

                warnings.Add("比對視圖原本啟用遠裁剪（Far Clip Active），已關閉，否則 PNG 會全白。");
            }
            catch (Exception ex)
            {
                warnings.Add("關閉遠裁剪失敗：" + ex.Message);
            }
        }

        private static JObject DescribeWebCompareViewState(View3D view)
        {
            var state = new JObject();
            try
            {
                var o = view.GetOrientation();
                var crop = view.CropBox;
                state["eyeFeet"] = new JArray(o.EyePosition.X, o.EyePosition.Y, o.EyePosition.Z);
                state["cropActive"] = view.CropBoxActive;
                state["cropVisible"] = view.CropBoxVisible;
                state["cropMinFeet"] = new JArray(crop.Min.X, crop.Min.Y, crop.Min.Z);
                state["cropMaxFeet"] = new JArray(crop.Max.X, crop.Max.Y, crop.Max.Z);
                state["farClipActive"] = view.get_Parameter(BuiltInParameter.VIEWER_BOUND_ACTIVE_FAR)?.AsInteger();
                state["farClipOffsetFeet"] = view.get_Parameter(BuiltInParameter.VIEWER_BOUND_OFFSET_FAR)?.AsDouble();
                state["displayStyle"] = view.DisplayStyle.ToString();
                state["isPerspective"] = view.IsPerspective;
            }
            catch (Exception ex)
            {
                state["error"] = ex.Message;
            }

            return state;
        }

        /// <summary>Reads a string list that may arrive as a JSON array, a JSON-array string (clients without a typed schema stringify it) or a comma list.</summary>
        private static List<string> WebCompareStringList(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                return new List<string>();
            }

            if (token is JArray array)
            {
                return array.Select(t => t.Value<string>()).Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
            }

            var text = token.Value<string>()?.Trim() ?? string.Empty;
            if (text.StartsWith("[", StringComparison.Ordinal))
            {
                try
                {
                    return JArray.Parse(text).Select(t => t.Value<string>()).Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
                }
                catch (JsonException)
                {
                    // fall through to the comma split
                }
            }

            return text.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
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
                    // 明確指定視圖，不依賴 UI 目前畫面（同一外部事件內切換作用中視圖時畫面尚未重繪，會匯出空白圖）。
                    ExportRange = ExportRange.SetOfViews,
                    FilePath = Path.Combine(tempDir, "img"),
                    HLRandWFViewsFileType = ImageFileType.PNG,
                    ShadowViewsFileType = ImageFileType.PNG,
                    ImageResolution = ImageResolution.DPI_150,
                    ZoomType = ZoomFitType.FitToPage,
                    FitDirection = FitDirectionType.Horizontal,
                    PixelSize = pixelSize
                };
                options.SetViewsAndSheets(new List<ElementId> { uiDoc.ActiveView.Id });
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
                    try
                    {
                        // Revit's perspective crop box is not the true view frustum. Working hypothesis (S1 round 2):
                        // true tangent = (crop near-plane half extent / near plane distance) / tan(20 deg), centred.
                        // NOT verified: the silhouette IoU used to fit it is non-discriminative when the model fills the
                        // frame, and round 3 still saw ~2x framing error (docs/LESSONS.md L-06).
                        var crop = view.CropBox;
                        var eyeLocal = crop.Transform.Inverse.OfPoint(orientation.EyePosition);
                        var nearV = crop.Max - eyeLocal;
                        var minV = crop.Min - eyeLocal;
                        var depthNear = -nearV.Z;
                        if (depthNear > 1e-9)
                        {
                            const double scale = 2.7475; // 1 / tan(20 deg)
                            var halfH = (nearV.X - minV.X) / 2.0 / depthNear * scale;
                            var halfV = (nearV.Y - minV.Y) / 2.0 / depthNear * scale;
                            camera["projection"]["frustumTan"] = new JObject
                            {
                                ["left"] = Math.Round(-halfH, 6),
                                ["right"] = Math.Round(halfH, 6),
                                ["bottom"] = Math.Round(-halfV, 6),
                                ["top"] = Math.Round(halfV, 6),
                                ["fovVerticalDeg"] = Math.Round(2.0 * Math.Atan(halfV) * 180.0 / Math.PI, 3),
                                ["source"] = "empirical hypothesis, NOT verified: crop near-plane half extent / near distance / tan(20deg); centred. Round 3 still saw ~2x framing error (docs/LESSONS.md L-06)",
                                ["rawCropTanNearPlane"] = new JObject
                                {
                                    ["left"] = Math.Round(minV.X / depthNear, 6),
                                    ["right"] = Math.Round(nearV.X / depthNear, 6),
                                    ["bottom"] = Math.Round(minV.Y / depthNear, 6),
                                    ["top"] = Math.Round(nearV.Y / depthNear, 6)
                                }
                            };
                            camera["projection"]["fovVerticalDeg"] = camera["projection"]["frustumTan"]["fovVerticalDeg"];
                            camera["projection"]["fovHorizontalDeg"] = Math.Round(2.0 * Math.Atan(halfH) * 180.0 / Math.PI, 3);
                            camera["projection"]["aspect"] = Math.Round(halfH / halfV, 5);
                            camera["projection"]["symmetric"] = true;
                            camera["projection"]["source"] = "frustumTan (empirical, see frustumTan.source); the raw crop box is NOT the true frustum";
                        }
                    }
                    catch (Exception ex)
                    {
                        warnings.Add("無法計算視錐切線（frustumTan）：" + ex.Message);
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

        /// <summary>How heavy elements are handled: over-cap mode, simplification ratio and Revit tessellation detail.</summary>
        private sealed class WebCompareHeavyOptions
        {
            public string OverCapMode = "simplify";
            public double SimplifyRatio;
            public int LevelOfDetail = -1;
        }

        /// <summary>
        /// splitLinks：主檔只放主模型，每個連結文件另寫 part_&lt;slug&gt;.glb，並輸出 parts.json（viewer3d.md §22.2）。
        /// 傳 null＝行為與先前完全相同。Files 是匯出後實際寫出的分檔與清單，供呼叫端列入回報。
        /// </summary>
        private sealed class WebCompareSplitOptions
        {
            /// <summary>每個分檔的三角面上限；0＝沿用 maxTriangles。</summary>
            public int MaxPartTriangles;
            /// <summary>主檔＋全部分檔合計的三角面上限；超過整個匯出提前停止並警告。</summary>
            public int MaxTotalTriangles;
            public readonly List<WebCompareWrittenFile> Files = new List<WebCompareWrittenFile>();
            public JObject Manifest = new JObject();
        }

        private sealed class WebCompareWrittenFile
        {
            public string Name = string.Empty;
            public string Path = string.Empty;
            public JObject Extra = new JObject();
        }

        /// <summary>解析 splitLinks／maxPartTriangles；splitLinks 不為 true 時回 null（maxPartTriangles 也不檢查）。</summary>
        private static WebCompareSplitOptions? ParseWebCompareSplitOptions(JObject parameters, int maxTriangles)
        {
            if (!(parameters["splitLinks"]?.Value<bool?>() ?? false))
            {
                return null;
            }

            var maxPart = parameters["maxPartTriangles"]?.Value<int?>();
            if (maxPart.HasValue && maxPart.Value < 1)
            {
                throw new ArgumentException("maxPartTriangles 必須是 1 以上的整數。");
            }

            var maxTotal = parameters["maxTotalTriangles"]?.Value<long?>();
            if (maxTotal.HasValue && maxTotal.Value < 1)
            {
                throw new ArgumentException("maxTotalTriangles 必須是 1 以上的整數。");
            }

            var partBudget = maxPart ?? maxTriangles;
            // 預設合計上限＝主檔上限＋最多 6 個分檔各自的上限（超過 6 個連結時仍受合計保護）。
            var total = maxTotal ?? ((long)maxTriangles + 6L * partBudget);
            return new WebCompareSplitOptions
            {
                MaxPartTriangles = partBudget,
                MaxTotalTriangles = (int)Math.Min(int.MaxValue, total)
            };
        }

        private static void AddWebCompareSplitFileEntries(List<object> files, WebCompareSplitOptions? split, Stopwatch sw, List<string> warnings)
        {
            if (split == null)
            {
                return;
            }

            foreach (var written in split.Files)
            {
                files.Add(WebCompareFileEntry(written.Name, written.Path, sw, warnings, written.Extra));
            }
        }

        private static void ExportWebCompareGlb(Document doc, View3D source, string glbPath, Vec3 offset, int maxTriangles,
            List<string> warnings, out int triangleCount, out int elementNodeCount,
            List<WebCompareDoc> linkedDocs, WebCompareSectionOptions sectionOptions, MeterBox? sectionBox,
            List<WebCompareTerrainPiece> terrainPieces, JObject linkReport, double webCompareContourInterval, List<string> webCompareExcludedCategories,
            List<string> webCompareExcludedElements, int webCompareMaxElementTriangles, bool webCompareDumpAppearance,
            WebCompareHeavyOptions heavyOptions, Func<Document, Element, bool> elementPredicate = null, WebCompareSplitOptions? split = null)
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
                    TriangleBudget = maxTriangles,
                    ContourIntervalMeters = webCompareContourInterval,
                    DiagPath = Path.ChangeExtension(glbPath, ".diag.log")
                };
                context.ElementPredicate = elementPredicate;
                foreach (var excluded in webCompareExcludedCategories)
                {
                    context.ExcludedCategories.Add(excluded);
                }

                foreach (var key in webCompareExcludedElements)
                {
                    context.ExcludedElementKeys.Add(key);
                }

                context.MaxElementTriangles = webCompareMaxElementTriangles;
                context.OverCapMode = heavyOptions.OverCapMode;
                context.SimplifyRatio = heavyOptions.SimplifyRatio;
                context.LevelOfDetail = heavyOptions.LevelOfDetail;
                context.DumpAppearance = webCompareDumpAppearance;
                if (split != null)
                {
                    context.SplitLinks = true;
                    context.PartTriangleBudget = split.MaxPartTriangles > 0 ? split.MaxPartTriangles : maxTriangles;
                    context.TotalTriangleBudget = split.MaxTotalTriangles;
                }

                File.WriteAllText(context.DiagPath, "");
                context.Diag($"export start view={tempView.Name} perspective={tempView.IsPerspective} cropActive={tempView.CropBoxActive} sectionBoxActive={tempView.IsSectionBoxActive}");
                try
                {
                    using (var exporter = new CustomExporter(doc, context))
                    {
                        exporter.IncludeGeometricObjects = false;
                        exporter.ShouldStopOnError = elementPredicate != null;
                        exporter.Export(tempView);
                    }
                    context.Diag("export finished normally");
                    context.Flush();
                }
                catch (Exception ex)
                {
                    context.Diag("CustomExporter.Export exception: " + ex);
                    throw;
                }
                finally
                {
                    context.Diag($"summary linkBegin={context.LinkBeginCount} triangles={builder.TriangleCount} nodes={builder.ElementNodeCount} exceptions={context.ExceptionCount} skippedPolymesh={context.SkippedPolymeshes}");
                    context.Diag("elementsSeenByDoc=" + JsonConvert.SerializeObject(context.ElementsSeenByDoc));
                    context.Diag("elementCounts=" + JsonConvert.SerializeObject(context.ElementCounts));
                    context.Diag("trianglesByKey=" + JsonConvert.SerializeObject(context.TrianglesByKey.OrderByDescending(kv => kv.Value).Take(40).ToDictionary(kv => kv.Key, kv => kv.Value)));
                    context.Diag("droppedElements=" + JsonConvert.SerializeObject(context.DroppedElements));
                    context.Diag($"textured={context.TexturedMaterials} images={builder.ImageCount} contourSegments={context.ContourSegments}");
                    if (split != null)
                    {
                        context.Diag("splitParts=" + JsonConvert.SerializeObject(context.LinkParts.Select(p => new
                        {
                            p.Title, p.Key, triangles = p.Builder.TriangleCount, nodes = p.Builder.ElementNodeCount, p.OverBudget
                        })));
                    }

                    context.Diag("textureProblems=" + JsonConvert.SerializeObject(context.TextureProblems));
                    context.Diag("transparentMaterials=" + JsonConvert.SerializeObject(context.TransparentMaterials));
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

            // Linked terrain: CustomExporter is expected to include links, but terrain from a link was missing in
            // practice. Report what it did, and triangulate the link's Toposolid/Topography ourselves when absent.
            var nativeTerrain = context.ElementCounts
                .Where(kv => !kv.Key.StartsWith(RevitGltfExportContext.HostName + "|", StringComparison.Ordinal) &&
                             (kv.Key.EndsWith("|Toposolid", StringComparison.OrdinalIgnoreCase) ||
                              kv.Key.EndsWith("|Toposolids", StringComparison.OrdinalIgnoreCase) ||
                              kv.Key.EndsWith("|Topography", StringComparison.OrdinalIgnoreCase)))
                .Sum(kv => kv.Value);
            var terrainReport = new JObject
            {
                ["mode"] = sectionOptions.TerrainMode,
                ["nativeLinkTerrainElements"] = nativeTerrain,
                ["linkBeginCount"] = context.LinkBeginCount,
                ["elementCountsByLinkAndCategory"] = JObject.FromObject(context.ElementCounts),
                ["trianglesByLink"] = JObject.FromObject(context.LinkTriangles),
                ["elementsSeenByDoc"] = JObject.FromObject(context.ElementsSeenByDoc),
                ["exceptions"] = context.ExceptionCount,
                ["budgetExceeded"] = context.BudgetExceeded,
                ["trianglesByKey"] = JObject.FromObject(context.TrianglesByKey),
                ["texturedMaterials"] = context.TexturedMaterials,
                ["images"] = builder.ImageCount,
                ["textureProblems"] = JObject.FromObject(context.TextureProblems),
                ["transparentMaterials"] = JObject.FromObject(context.TransparentMaterials),
                ["contourSegments"] = context.ContourSegments,
                ["droppedElements"] = JArray.FromObject(context.DroppedElements),
                ["heavyElements"] = new JObject
                {
                    ["maxElementTriangles"] = webCompareMaxElementTriangles,
                    ["overCapMode"] = heavyOptions.OverCapMode,
                    ["simplifyRatio"] = heavyOptions.SimplifyRatio,
                    ["levelOfDetail"] = heavyOptions.LevelOfDetail,
                    ["simplifySeconds"] = Math.Round(context.SimplifyTime.Elapsed.TotalSeconds, 2),
                    ["trianglesBeforeCap"] = context.ElementTriangleStats.Sum(e => (long)e.Triangles),
                    ["trianglesAfterCap"] = context.ElementTriangleStats.Sum(e => (long)e.Kept),
                    ["top"] = JArray.FromObject(context.ElementTriangleStats.OrderByDescending(e => e.Triangles).Take(40))
                }
            };
            try
            {
                // Full per-element list for the triangle study (every element with geometry, heaviest first).
                File.WriteAllText(Path.ChangeExtension(glbPath, ".elements.json"),
                    JsonConvert.SerializeObject(context.ElementTriangleStats.OrderByDescending(e => e.Triangles), Formatting.Indented),
                    new UTF8Encoding(false));
            }
            catch (IOException ex)
            {
                warnings.Add("無法寫入元件三角面清單：" + ex.Message);
            }
            var useFallback = sectionOptions.TerrainMode == "always" || (sectionOptions.TerrainMode == "auto" && nativeTerrain == 0);
            terrainReport["fallbackUsed"] = useFallback;
            if (useFallback)
            {
                var far = new MeterBox(-WebCompareFarMeters, -WebCompareFarMeters, -WebCompareFarMeters, WebCompareFarMeters, WebCompareFarMeters, WebCompareFarMeters);
                var pieces = terrainPieces;
                if (pieces.Count == 0)
                {
                    pieces = CollectLinkedTerrain(linkedDocs, sectionBox ?? far, Math.Max(0, maxTriangles - builder.TriangleCount), warnings, out _);
                }

                var terrainBox = sectionBox ?? far;
                var added = 0;
                if (split == null)
                {
                    added = AddTerrainPiecesToBuilder(builder, pieces, terrainBox, offset);
                }
                else
                {
                    // 補算的連結地形放進它所屬連結文件的那一檔（Family 欄位存的是文件標題）。
                    foreach (var group in pieces.GroupBy(p => p.Info?.Family ?? RevitGltfExportContext.HostName))
                    {
                        foreach (var piece in group)
                        {
                            if (piece.Info != null && string.IsNullOrEmpty(piece.Info.Source))
                            {
                                piece.Info.Source = group.Key;
                            }
                        }

                        // 補算地形也受各檔上限：超出剩餘額度的元素略過並警告。
                        var target = context.PartBuilderFor(group.Key);
                        var remaining = (group.Key == RevitGltfExportContext.HostName ? maxTriangles : context.PartTriangleBudget) - target.TriangleCount;
                        var allowed = new List<WebCompareTerrainPiece>();
                        foreach (var piece in group)
                        {
                            if (piece.Triangles.Count > remaining)
                            {
                                warnings.Add($"連結地形 {piece.Info?.Name}（{group.Key}）會超過該檔三角面上限，已略過。");
                                continue;
                            }

                            remaining -= piece.Triangles.Count;
                            allowed.Add(piece);
                        }

                        added += AddTerrainPiecesToBuilder(target, allowed, terrainBox, offset);
                    }
                }

                terrainReport["fallbackElements"] = pieces.Count;
                terrainReport["fallbackTriangles"] = added;
                if (pieces.Count == 0)
                {
                    warnings.Add("連結模型中找不到 Toposolid／Topography 幾何（或全在 section box 外），GLB 沒有地形。");
                }
            }

            linkReport["glbTerrain"] = terrainReport;

            var propertyLineSegments = AddPropertyLinesToBuilder(builder, linkedDocs, offset, warnings,
                split == null ? (Func<string, GltfBuilder?>?)null : context.TryGetVisitedPartBuilder);
            linkReport["glbPropertyLineSegments"] = propertyLineSegments;
            builder.OriginOffsetMeters = offset;

            if (context.BudgetExceeded && split == null)
            {
                warnings.Add($"GLB 三角面超過上限 {context.TriangleBudget:N0}，匯出提前停止（模型不完整）；請在 {source.Name} 隱藏家具、植栽、機電等類別，或用 maxTriangles 提高上限。");
            }

            var mainBytes = builder.BuildGlb();
            File.WriteAllBytes(glbPath, mainBytes);
            triangleCount = builder.TriangleCount;
            elementNodeCount = builder.ElementNodeCount;

            if (split != null)
            {
                WriteWebCompareSplitParts(context, builder, mainBytes.Length, glbPath, offset, split, warnings, linkReport, maxTriangles);
            }
        }

        /// <summary>
        /// splitLinks：寫出每個連結分檔 part_&lt;slug&gt;.glb 與 parts.json（viewer3d.md §22.2）。沒有任何構件的分檔不輸出。
        /// 與主檔放在同一個資料夾；資料夾內不屬於本次輸出的舊 part_*.glb 會刪除（避免網頁匯入到過期分檔）。
        /// </summary>
        private static void WriteWebCompareSplitParts(RevitGltfExportContext context, GltfBuilder mainBuilder, int mainBytes, string glbPath,
            Vec3 offset, WebCompareSplitOptions split, List<string> warnings, JObject linkReport, int maxTriangles)
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(glbPath));
            // 前一版 parts.json 列出的分檔：只有這些才可能被當成「過期分檔」刪除（共用資料夾裡別人的 part_*.glb 不碰）。
            var previousFiles = new List<string>();
            try
            {
                var previousPath = Path.Combine(dir, "parts.json");
                if (File.Exists(previousPath))
                {
                    var previous = JObject.Parse(File.ReadAllText(previousPath, Encoding.UTF8));
                    foreach (var p in (previous["parts"] as JArray) ?? new JArray())
                    {
                        var f = p["file"]?.Value<string>();
                        if (!string.IsNullOrEmpty(f) && string.Equals(Path.GetFileName(f), f, StringComparison.Ordinal) &&
                            f.StartsWith("part_", StringComparison.OrdinalIgnoreCase) && f.EndsWith(".glb", StringComparison.OrdinalIgnoreCase))
                        {
                            previousFiles.Add(f);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException)
            {
                warnings.Add("讀不到前一版 parts.json，不清除舊分檔：" + ex.Message);
            }

            var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var manifestParts = new JArray();
            var emptyParts = new JArray();
            foreach (var part in context.LinkParts.OrderBy(p => p.Title, StringComparer.Ordinal))
            {
                var nodes = part.Builder.ElementNodeCount;
                if (nodes == 0)
                {
                    emptyParts.Add(part.Title);
                    continue;
                }

                var fileName = LinkPartsManifest.PartFileName(part.Key);
                var bytes = part.Builder.BuildGlb();
                var path = Path.Combine(dir, fileName);
                File.WriteAllBytes(path, bytes);
                written.Add(fileName);
                var defaultVisible = LinkPartsManifest.ComputeDefaultVisible(part.TrianglesByCategory, RevitGltfExportContext.StructuralMepBuiltInCategories);
                var entry = new JObject
                {
                    ["key"] = part.Key,
                    ["label"] = part.Title,
                    ["file"] = fileName,
                    ["triangles"] = part.Builder.TriangleCount,
                    ["nodes"] = nodes,
                    ["bytes"] = bytes.Length,
                    ["defaultVisible"] = defaultVisible,
                    ["topCategories"] = new JArray(LinkPartsManifest.TopCategories(part.TrianglesByCategory, 5)
                        .Select(c => new JObject { ["category"] = c.Name, ["builtInCategory"] = c.BuiltInCategory != 0 ? (JToken)((BuiltInCategory)c.BuiltInCategory).ToString() : JValue.CreateNull(), ["builtInCategoryId"] = c.BuiltInCategory, ["triangles"] = c.Triangles }))
                };
                manifestParts.Add(entry);
                split.Files.Add(new WebCompareWrittenFile
                {
                    Name = fileName,
                    Path = path,
                    Extra = new JObject { ["triangleCount"] = part.Builder.TriangleCount, ["elementNodeCount"] = nodes, ["partKey"] = part.Key, ["defaultVisible"] = defaultVisible }
                });
                if (part.OverBudget)
                {
                    warnings.Add($"連結分檔 {part.Title} 三角面超過上限 {context.PartTriangleBudget:N0}，該檔提前停止（不完整）；請用 maxPartTriangles 提高上限，或在連結模型隱藏不需要的類別。");
                }
            }

            var manifest = new JObject
            {
                ["version"] = 1,
                ["originOffsetMeters"] = new JArray(offset.X, offset.Y, offset.Z),
                ["main"] = new JObject
                {
                    ["file"] = Path.GetFileName(glbPath),
                    ["triangles"] = mainBuilder.TriangleCount,
                    ["nodes"] = mainBuilder.ElementNodeCount,
                    ["bytes"] = mainBytes
                },
                ["parts"] = manifestParts
            };
            var manifestPath = Path.Combine(dir, "parts.json");
            File.WriteAllText(manifestPath, manifest.ToString(Formatting.Indented), new UTF8Encoding(false));
            split.Manifest = manifest;
            split.Files.Add(new WebCompareWrittenFile
            {
                Name = "parts.json",
                Path = manifestPath,
                Extra = new JObject { ["partCount"] = manifestParts.Count }
            });

            var removed = new JArray();
            foreach (var name in previousFiles.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (written.Contains(name))
                {
                    continue;
                }

                var stale = Path.Combine(dir, name);
                try
                {
                    if (File.Exists(stale))
                    {
                        File.Delete(stale);
                        removed.Add(name);
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    warnings.Add($"無法刪除過期的分檔 {name}：{ex.Message}");
                }
            }

            if (removed.Count > 0)
            {
                warnings.Add("已刪除本次輸出沒有的舊分檔：" + string.Join("、", removed.Select(t => t.ToString())) + "。");
            }

            if (context.MainOverBudget)
            {
                warnings.Add($"GLB 主檔三角面超過上限 {context.TriangleBudget:N0}，主檔提前停止（模型不完整）；請隱藏部分類別，或用 maxTriangles 提高上限。");
            }

            if (context.TotalBudgetExceeded)
            {
                warnings.Add($"主檔與分檔合計三角面超過上限 {context.TotalTriangleBudget:N0}，匯出提前停止（模型不完整）；請用 maxTotalTriangles 提高上限，或隱藏部分類別。");
            }

            linkReport["splitLinks"] = new JObject
            {
                ["manifest"] = manifestPath,
                ["partCount"] = manifestParts.Count,
                ["parts"] = manifestParts,
                ["emptyPartsSkipped"] = emptyParts,
                ["staleRemoved"] = removed,
                ["partTriangleBudget"] = context.PartTriangleBudget > 0 ? context.PartTriangleBudget : maxTriangles,
                ["totalTriangleBudget"] = context.TotalTriangleBudget
            };
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
