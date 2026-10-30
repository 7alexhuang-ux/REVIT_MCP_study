using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitMCP.Core.Walkthrough;

namespace RevitMCP.Core
{
    public partial class CommandExecutor
    {
        private const string ViewPositionsFileName = "view_positions.json";

        /// <summary>
        /// 唯讀匯出平面／天花／剖面／立面（與詳圖，視為無位置）的位置到 view_positions.json，
        /// 供 issue-manager 把圖紙視口連到 3D（規格 issue-manager docs/spec/revit-addin-export.md §5.3、§6.1，D-705）。
        /// 不開 Transaction、不啟用視圖。座標一律 glTF 公尺（Y 向上），原點位移與同次匯出的 GLB／named_views.json 相同。
        /// </summary>
        private object ExportViewPositions(JObject parameters)
        {
            var uiDoc = _uiApp.ActiveUIDocument ?? throw new InvalidOperationException("目前沒有開啟的 Revit 專案。");
            var doc = uiDoc.Document;
            var outputDir = parameters["outputDir"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(outputDir)) outputDir = WebCompareDefaultOutputDir;
            outputDir = Path.GetFullPath(outputDir);

            // 原點與 export_named_views 同一套來源：originOffsetMeters／cameraPath／outputDir 的 camera.json。
            var offset = ResolveNamedViewsOrigin(parameters, outputDir, out var originSource);

            var requestedIds = ParseViewPositionsIdSet(parameters["viewIds"]);
            var requestedUniqueIds = new HashSet<string>(WebCompareStringList(parameters["uniqueIds"]), StringComparer.Ordinal);
            var hasExplicit = requestedIds.Count > 0 || requestedUniqueIds.Count > 0;
            // 預設只輸出放在圖紙上的視圖；明指 viewIds／uniqueIds 時預設不再以圖紙過濾，除非明寫 onlyOnSheets。
            var onlyOnSheets = ParseViewPositionsBool(parameters["onlyOnSheets"], !hasExplicit);

            var warnings = new List<string>();
            var skipped = new SortedDictionary<string, int>(StringComparer.Ordinal);
            void Skip(string reason) { skipped.TryGetValue(reason, out var n); skipped[reason] = n + 1; }

            var sheetViewIds = new HashSet<long>();
            if (onlyOnSheets)
            {
                foreach (var sheet in new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>())
                    foreach (var viewportId in sheet.GetAllViewports())
                    {
                        var viewport = doc.GetElement(viewportId) as Viewport;
                        if (viewport != null) sheetViewIds.Add((long)viewport.ViewId.GetIdValue());
                    }
            }

            var candidates = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                .OrderBy(v => v.Name, StringComparer.Ordinal).ThenBy(v => v.UniqueId, StringComparer.Ordinal).ToList();
            foreach (var id in requestedIds.Where(id => candidates.All(v => (long)v.Id.GetIdValue() != id)))
                warnings.Add($"找不到 viewId：{id}");
            foreach (var uid in requestedUniqueIds.Where(u => candidates.All(v => v.UniqueId != u)))
                warnings.Add($"找不到 uniqueId：{uid}");

            var entries = new List<ViewPositionEntry>();
            foreach (var view in candidates)
            {
                var kind = ViewPositionsKind(view);
                if (kind == null) { if (!view.IsTemplate) Skip("unsupportedViewType"); continue; }
                if (view.IsTemplate) { Skip("template"); continue; }
                if (hasExplicit && !requestedIds.Contains((long)view.Id.GetIdValue()) && !requestedUniqueIds.Contains(view.UniqueId)) continue;
                if (onlyOnSheets && !sheetViewIds.Contains((long)view.Id.GetIdValue())) { Skip("notOnSheet"); continue; }

                try
                {
                    var entry = BuildViewPositionEntry(doc, view, kind, offset, warnings, out var skipReason);
                    if (entry == null) Skip(skipReason ?? "noGeometry");
                    else entries.Add(entry);
                }
                catch (Exception ex)
                {
                    Skip("readFailed");
                    warnings.Add($"視圖「{view.Name}」（{view.UniqueId}）讀取失敗，未匯出：{ex.Message}");
                }
            }

            var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                .OrderBy(l => l.ProjectElevation).ThenBy(l => l.UniqueId, StringComparer.Ordinal)
                .Select(l =>
                {
                    var z = l.ProjectElevation * CameraPathMath.FeetToMeters;
                    return new ViewPositionLevel
                    {
                        LevelUniqueId = l.UniqueId, Name = l.Name,
                        ElevationMeters = ViewPositionsMath.ToGltfHeight(z, offset), RevitZMeters = z,
                    };
                }).ToList();

            var payload = ViewPositionsMath.BuildFile(doc.Title, offset, levels, entries, warnings);
            Directory.CreateDirectory(outputDir);
            var path = Path.Combine(outputDir, ViewPositionsFileName);
            File.WriteAllText(path, payload.ToString(Formatting.Indented), new UTF8Encoding(false));

            var counts = new JObject();
            foreach (var group in entries.GroupBy(e => e.Kind).OrderBy(g => g.Key, StringComparer.Ordinal)) counts[group.Key] = group.Count();
            return new JObject
            {
                ["path"] = path,
                ["viewCount"] = entries.Count,
                ["countsByKind"] = counts,
                ["skipped"] = new JObject(skipped.Select(p => new JProperty(p.Key, p.Value))),
                ["unlimitedDepthViews"] = entries.Count(e => e.Frame != null && !e.Frame.DepthMeters.HasValue),
                ["onlyOnSheets"] = onlyOnSheets,
                ["levelCount"] = levels.Count,
                ["originOffsetMeters"] = NamedViewsArray(offset),
                ["originOffsetSource"] = originSource,
                ["warnings"] = new JArray(warnings.Take(200)),
                ["warningCount"] = warnings.Count
            };
        }

        /// <summary>Revit ViewType → 規格 kind；不是這五類回 null。</summary>
        private static string ViewPositionsKind(View view)
        {
            switch (view.ViewType)
            {
                case ViewType.FloorPlan:
                case ViewType.EngineeringPlan:
                case ViewType.AreaPlan:
                    return view is ViewPlan ? "plan" : null;
                case ViewType.CeilingPlan: return view is ViewPlan ? "ceiling" : null;
                case ViewType.Section: return view is ViewSection ? "section" : null;
                case ViewType.Elevation: return view is ViewSection ? "elevation" : null;
                case ViewType.Detail: return view is ViewSection ? "detail" : null;
                default: return null;
            }
        }

        private static ViewPositionEntry BuildViewPositionEntry(Document doc, View view, string kind, Vec3 offset,
            List<string> warnings, out string skipReason)
        {
            skipReason = null;
            var entry = new ViewPositionEntry { UniqueId = view.UniqueId, Name = view.Name, Kind = kind };
            var label = $"視圖「{view.Name}」（{view.UniqueId}）";
            if (kind == "detail") return entry; // 無位置，仍列出，網站端計入 detail_viewports_not_linked

            var box = view.CropBox;
            if (box == null) { warnings.Add($"{label}：CropBox 為 null，視為無位置"); skipReason = "noCropBox"; return null; }
            var transform = box.Transform;
            var origin = NamedViewsMeters(transform.Origin);
            var basisX = new Vec3(transform.BasisX.X, transform.BasisX.Y, transform.BasisX.Z);
            var basisY = new Vec3(transform.BasisY.X, transform.BasisY.Y, transform.BasisY.Z);
            var basisZ = new Vec3(transform.BasisZ.X, transform.BasisZ.Y, transform.BasisZ.Z);
            var localMin = NamedViewsMeters(box.Min);
            var localMax = NamedViewsMeters(box.Max);

            if (kind == "plan" || kind == "ceiling")
            {
                var plan = (ViewPlan)view;
                var range = plan.GetViewRange();
                double? PlaneZ(PlanViewPlane plane)
                {
                    try
                    {
                        // Unlimited／上下一層等無法對應到單一樓層的設定，GetElement 會回 null → 給 null，不猜值。
                        var level = doc.GetElement(range.GetLevelId(plane)) as Level;
                        if (level == null) return null;
                        return (level.ProjectElevation + range.GetOffset(plane)) * CameraPathMath.FeetToMeters;
                    }
                    catch (Exception) { return null; }
                }

                entry.ViewRange = ViewPositionsMath.BuildPlanRange(PlaneZ(PlanViewPlane.CutPlane), PlaneZ(PlanViewPlane.TopClipPlane),
                    PlaneZ(PlanViewPlane.BottomClipPlane), PlaneZ(PlanViewPlane.ViewDepthPlane), offset, out var rangeProblem);
                if (entry.ViewRange == null)
                {
                    warnings.Add(rangeProblem == "cutBelowBottom"
                        ? $"{label}：切高低於下緣（資料矛盾），略過"
                        : $"{label}：讀不到切高（cut plane），視為無位置");
                    skipReason = rangeProblem ?? "noCutPlane";
                    return null;
                }
                if (kind == "ceiling" && !entry.ViewRange.TopMeters.HasValue)
                    warnings.Add($"{label}：天花平面讀不到視圖範圍頂部（top），topMeters 為 null");
                entry.LevelUniqueId = plan.GenLevel?.UniqueId;
                entry.Crop = ViewPositionsMath.BuildCrop(ViewPositionsMath.BoxCorners(origin, basisX, basisY, basisZ, localMin, localMax),
                    offset, view.CropBoxActive);
                var right = view.RightDirection; var up = view.UpDirection;
                entry.ViewDirection = ViewPositionsMath.ToGltfDirection(ViewPositionsMath.LookDirection(
                    new Vec3(right.X, right.Y, right.Z), new Vec3(up.X, up.Y, up.Z))).Normalized();
                return entry;
            }

            // section／elevation
            // 遠裁剪：VIEWER_BOUND_FAR_CLIPPING 0＝不裁剪（不限深度）；讀不到參數時視為有裁剪、沿用 CropBox 深度。
            var farClipActive = true;
            try { farClipActive = view.get_Parameter(BuiltInParameter.VIEWER_BOUND_FAR_CLIPPING)?.AsInteger() != 0; }
            catch (Exception) { /* 取不到參數就沿用 CropBox 深度 */ }
            var frame = ViewPositionsMath.BuildFrame(origin, basisX, basisY, localMin, localMax, offset, farClipActive, out var problem, out var note);
            if (frame == null) { warnings.Add($"{label}：{problem}，視為無位置"); skipReason = "badFrame"; return null; }
            if (note != null) warnings.Add($"{label}：{note}");
            var viewRight = view.RightDirection;
            if (new Vec3(viewRight.X, viewRight.Y, viewRight.Z).Dot(basisX.Normalized()) < 0.999)
                warnings.Add($"{label}：CropBox.Transform 的 X 軸與 View.RightDirection 不一致，frame 以 Transform 為準，請人工確認方向");
            if (!farClipActive) warnings.Add($"{label}：遠裁剪為「不裁剪」，depthMeters 輸出 null（不限深度）");
            entry.Frame = frame;
            entry.ViewDirection = frame.ViewDirection;
            entry.Crop = ViewPositionsMath.BuildCrop(ViewPositionsMath.BoxCorners(origin, basisX, basisY, basisZ, localMin, localMax),
                offset, view.CropBoxActive);
            return entry;
        }

        private static HashSet<long> ParseViewPositionsIdSet(JToken token)
        {
            var result = new HashSet<long>();
            if (token == null || token.Type == JTokenType.Null) return result;
            if (token.Type == JTokenType.String) // 相容舊 MCP schema 以 JSON 字串傳陣列（LESSONS L-13）
            {
                var text = token.Value<string>();
                token = text != null && text.TrimStart().StartsWith("[") ? JArray.Parse(text) : new JArray(text);
            }
            if (!(token is JArray array)) throw new ArgumentException("viewIds 必須是 ElementId 整數陣列。");
            foreach (var item in array)
            {
                if (item.Type == JTokenType.Integer) result.Add(item.Value<long>());
                else if (item.Type == JTokenType.String && long.TryParse(item.Value<string>(), out var parsed)) result.Add(parsed);
                else throw new ArgumentException("viewIds 內含不是整數的值。");
            }
            return result;
        }

        private static bool ParseViewPositionsBool(JToken token, bool fallback)
        {
            if (token == null || token.Type == JTokenType.Null) return fallback;
            if (token.Type == JTokenType.Boolean) return token.Value<bool>();
            if (token.Type == JTokenType.String && bool.TryParse(token.Value<string>(), out var parsed)) return parsed;
            throw new ArgumentException("onlyOnSheets 必須是 true 或 false。");
        }
    }
}
