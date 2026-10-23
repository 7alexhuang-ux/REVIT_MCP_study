using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitMCP.Core.Walkthrough;

namespace RevitMCP.Core
{
    public partial class CommandExecutor
    {
        /// <summary>
        /// 只讀取非樣板 3D 視圖，不啟用視圖、不修改相機、不開 Transaction。
        /// 全部相機共用既有 GLB 的原點；不能依每個視圖的眼點另外選原點。
        /// </summary>
        private object ExportNamedViews(JObject parameters)
        {
            var uiDoc = _uiApp.ActiveUIDocument ?? throw new InvalidOperationException("目前沒有開啟的 Revit 專案。");
            var doc = uiDoc.Document;
            var outputDir = parameters["outputDir"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(outputDir)) outputDir = WebCompareDefaultOutputDir;
            outputDir = Path.GetFullPath(outputDir);

            var offset = ResolveNamedViewsOrigin(parameters, outputDir, out var originSource);
            var requested = new HashSet<string>(WebCompareStringList(parameters["uniqueIds"]), StringComparer.Ordinal);
            var warnings = new List<string>();
            var views = new JArray();
            var allViews = new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>()
                .Where(v => !v.IsTemplate).OrderBy(v => v.Name, StringComparer.Ordinal)
                .ThenBy(v => v.UniqueId, StringComparer.Ordinal).ToList();

            foreach (var missing in requested.Except(allViews.Select(v => v.UniqueId), StringComparer.Ordinal))
                warnings.Add($"找不到非樣板 3D 視圖 uniqueId：{missing}");

            var openViews = uiDoc.GetOpenUIViews();
            foreach (var view in allViews.Where(v => requested.Count == 0 || requested.Contains(v.UniqueId)))
            {
                try
                {
                    views.Add(BuildNamedView(view, openViews, offset, warnings));
                }
                catch (Exception ex)
                {
                    warnings.Add($"視圖「{view.Name}」（{view.UniqueId}）讀取失敗，未匯出：{ex.Message}");
                }
            }

            var payload = new JObject
            {
                ["originOffsetMeters"] = NamedViewsArray(offset),
                ["views"] = views,
                ["warnings"] = new JArray(warnings)
            };
            Directory.CreateDirectory(outputDir);
            var path = Path.Combine(outputDir, "named_views.json");
            File.WriteAllText(path, payload.ToString(Formatting.Indented), new UTF8Encoding(false));
            var result = (JObject)payload.DeepClone();
            result["path"] = path;
            result["viewCount"] = views.Count;
            result["originOffsetSource"] = originSource;
            return result;
        }

        private static Vec3 ResolveNamedViewsOrigin(JObject parameters, string outputDir, out string source)
        {
            var explicitOffset = parameters["originOffsetMeters"];
            var hasExplicit = explicitOffset != null && explicitOffset.Type != JTokenType.Null;
            var cameraPath = parameters["cameraPath"]?.Value<string>();
            var hasCameraPath = !string.IsNullOrWhiteSpace(cameraPath);
            if (!hasCameraPath) cameraPath = Path.Combine(outputDir, "camera.json");
            cameraPath = Path.GetFullPath(cameraPath);

            Vec3? cameraOffset = null;
            if (File.Exists(cameraPath))
            {
                var camera = JObject.Parse(File.ReadAllText(cameraPath));
                cameraOffset = ParseNamedViewsVector(camera["gltf"]?["originOffsetMeters"], "camera.json.gltf.originOffsetMeters");
            }
            else if (hasCameraPath)
            {
                throw new FileNotFoundException("指定的 cameraPath 不存在，不能確認與 GLB 共用的原點。", cameraPath);
            }

            if (hasExplicit)
            {
                var offset = ParseNamedViewsVector(explicitOffset, "originOffsetMeters");
                if (cameraOffset.HasValue && (offset - cameraOffset.Value).Length > 0.00000001)
                    throw new ArgumentException("originOffsetMeters 與 camera.json 的 GLB 原點不同；請使用相同原點或輸出到另一資料夾。");
                source = cameraOffset.HasValue ? "originOffsetMeters; checked against " + cameraPath : "originOffsetMeters";
                return offset;
            }

            if (!cameraOffset.HasValue)
                throw new ArgumentException("找不到共用 GLB 原點。請提供 originOffsetMeters、cameraPath，或先在 outputDir 匯出 camera.json。不能為各視圖另取眼點作為原點。");
            source = cameraPath;
            return cameraOffset.Value;
        }

        private static Vec3 ParseNamedViewsVector(JToken token, string name)
        {
            // 相容舊 MCP schema 將新參數以 JSON 字串傳來的情境（LESSONS L-13）。
            if (token?.Type == JTokenType.String) token = JArray.Parse(token.Value<string>());
            if (!(token is JArray values) || values.Count != 3 || values.Any(v =>
                    v.Type != JTokenType.Integer && v.Type != JTokenType.Float))
                throw new ArgumentException(name + " 必須是三個有限數字組成的 Revit XYZ 公尺陣列。");
            var result = new Vec3(values[0].Value<double>(), values[1].Value<double>(), values[2].Value<double>());
            NamedViewsArray(result); // 同時拒絕 NaN／Infinity，避免產生無效的座標 JSON。
            return result;
        }

        private static JObject BuildNamedView(View3D view, IList<UIView> openViews, Vec3 offset, List<string> warnings)
        {
            const double toM = CameraPathMath.FeetToMeters;
            var orientation = view.GetOrientation();
            var eye = NamedViewsMeters(orientation.EyePosition);
            var forwardXyz = orientation.ForwardDirection.Normalize();
            var upXyz = orientation.UpDirection.Normalize();
            var forward = new Vec3(forwardXyz.X, forwardXyz.Y, forwardXyz.Z);
            var up = new Vec3(upXyz.X, upXyz.Y, upXyz.Z);
            var target = eye + forward * WebCompareTargetDistanceMeters;
            var gltf = new JObject
            {
                ["eye"] = NamedViewsArray(NamedViewsPoint(eye, offset)),
                ["target"] = NamedViewsArray(NamedViewsPoint(target, offset)),
                ["up"] = NamedViewsArray(NamedViewsDirection(up)),
                ["fovVerticalDeg"] = JValue.CreateNull(),
                ["orthoWidthMeters"] = JValue.CreateNull(),
                ["orthoHeightMeters"] = JValue.CreateNull()
            };
            var revit = new JObject
            {
                ["coordinateSystem"] = "Revit internal coordinates (Z up, right-handed), meters; not shared/survey coordinates",
                ["eye"] = NamedViewsArray(eye),
                ["forward"] = NamedViewsArray(forward),
                ["up"] = NamedViewsArray(up),
                ["target"] = NamedViewsArray(target),
                ["targetDistanceMeters"] = WebCompareTargetDistanceMeters
            };

            if (view.IsPerspective)
            {
                // 裁剪框反推 FOV 已知未驗證（LESSONS L-06），不能把經驗值當作相機真值。
                warnings.Add($"視圖「{view.Name}」（{view.UniqueId}）：透視 FOV 尚未驗證，fovVerticalDeg 為 null（LESSONS L-06）；保留真實眼點與方向，未推測視角。");
            }
            else
            {
                try
                {
                    var uiView = openViews.FirstOrDefault(v => v.ViewId == view.Id);
                    if (uiView == null)
                        throw new InvalidOperationException("視圖沒有已開啟的 UIView；只讀匯出不會切換或開啟視圖");
                    var corners = uiView.GetZoomCorners();
                    if (corners == null || corners.Count != 2)
                        throw new InvalidOperationException("GetZoomCorners 未回傳兩個對角點");
                    var diagonal = corners[1] - corners[0];
                    var right = forwardXyz.CrossProduct(upXyz).Normalize();
                    var width = Math.Abs(diagonal.DotProduct(right)) * toM;
                    var height = Math.Abs(diagonal.DotProduct(upXyz)) * toM;
                    if (width <= 0 || height <= 0 || double.IsNaN(width) || double.IsNaN(height)
                        || double.IsInfinity(width) || double.IsInfinity(height))
                        throw new InvalidOperationException("正交視窗範圍不是有效的正數");
                    gltf["orthoWidthMeters"] = width;
                    gltf["orthoHeightMeters"] = height;
                    revit["orthoSizeSource"] = "UIView.GetZoomCorners (model coordinates; current open view window)";
                    warnings.Add($"視圖「{view.Name}」（{view.UniqueId}）：正交寬高來自目前已開啟的 UIView，眼點仍為 GetOrientation 原始值；未校準視窗 pan／zoom，無法保證像素精準還原構圖。");
                }
                catch (Exception ex)
                {
                    warnings.Add($"視圖「{view.Name}」（{view.UniqueId}）：正交寬高為 null，{ex.Message}。");
                }
            }

            var section = new JObject
            {
                ["enabled"] = view.IsSectionBoxActive,
                ["min"] = JValue.CreateNull(),
                ["max"] = JValue.CreateNull()
            };
            try
            {
                using (var box = view.GetSectionBox())
                {
                    if (box == null) throw new InvalidOperationException("GetSectionBox 回傳 null");
                    var transform = box.Transform;
                    var corners = new List<Vec3>();
                    for (var x = 0; x < 2; x++)
                        for (var y = 0; y < 2; y++)
                            for (var z = 0; z < 2; z++)
                            {
                                var local = new XYZ(x == 0 ? box.Min.X : box.Max.X,
                                    y == 0 ? box.Min.Y : box.Max.Y, z == 0 ? box.Min.Z : box.Max.Z);
                                corners.Add(NamedViewsPoint(NamedViewsMeters(transform.OfPoint(local)), offset));
                            }
                    section["min"] = NamedViewsArray(new Vec3(corners.Min(c => c.X), corners.Min(c => c.Y), corners.Min(c => c.Z)));
                    section["max"] = NamedViewsArray(new Vec3(corners.Max(c => c.X), corners.Max(c => c.Y), corners.Max(c => c.Z)));
                    // AABB 包住旋轉剖面框；保留八角點與原始 transform，讓需要精確裁切的檢視器還原旋轉框。
                    section["corners"] = new JArray(corners.Select(NamedViewsArray));
                    section["boundsType"] = "axisAlignedBoundingBox";
                    revit["sectionBox"] = new JObject
                    {
                        ["enabled"] = view.IsSectionBoxActive,
                        ["localMinMeters"] = NamedViewsArray(NamedViewsMeters(box.Min)),
                        ["localMaxMeters"] = NamedViewsArray(NamedViewsMeters(box.Max)),
                        ["transform"] = new JObject
                        {
                            ["originMeters"] = NamedViewsArray(NamedViewsMeters(transform.Origin)),
                            ["basisX"] = new JArray(transform.BasisX.X, transform.BasisX.Y, transform.BasisX.Z),
                            ["basisY"] = new JArray(transform.BasisY.X, transform.BasisY.Y, transform.BasisY.Z),
                            ["basisZ"] = new JArray(transform.BasisZ.X, transform.BasisZ.Y, transform.BasisZ.Z)
                        }
                    };
                }
            }
            catch (Exception ex)
            {
                warnings.Add($"視圖「{view.Name}」（{view.UniqueId}）：剖面框無法讀取，min／max 為 null：{ex.Message}");
            }

            var viewerMode = WebCompareModes.FirstOrDefault(m => m.Style == view.DisplayStyle).WebMode;
            if (viewerMode == null)
                warnings.Add($"視圖「{view.Name}」（{view.UniqueId}）：{view.DisplayStyle} 沒有對應的 viewerMode，輸出 null。");
            return new JObject
            {
                ["viewId"] = view.Id.GetIdValue(),
                ["uniqueId"] = view.UniqueId,
                ["name"] = view.Name,
                ["isPerspective"] = view.IsPerspective,
                ["displayStyle"] = view.DisplayStyle.ToString(),
                ["viewerMode"] = viewerMode == null ? JValue.CreateNull() : (JToken)viewerMode,
                ["gltf"] = gltf,
                ["sectionBox"] = section,
                ["revit"] = revit
            };
        }

        private static Vec3 NamedViewsMeters(XYZ p) => new Vec3(p.X, p.Y, p.Z) * CameraPathMath.FeetToMeters;
        private static Vec3 NamedViewsPoint(Vec3 p, Vec3 offset) => new Vec3(p.X - offset.X, p.Z - offset.Z, -(p.Y - offset.Y));
        private static Vec3 NamedViewsDirection(Vec3 d) => new Vec3(d.X, d.Z, -d.Y);

        private static JArray NamedViewsArray(Vec3 v)
        {
            if (double.IsNaN(v.X) || double.IsNaN(v.Y) || double.IsNaN(v.Z)
                || double.IsInfinity(v.X) || double.IsInfinity(v.Y) || double.IsInfinity(v.Z))
                throw new ArgumentException("座標包含 NaN 或 Infinity。");
            return new JArray(v.X, v.Y, v.Z);
        }
    }
}
