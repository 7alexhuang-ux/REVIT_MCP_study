using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace RevitMCP.Core.Walkthrough
{
    /// <summary>平面視圖範圍（glTF 公尺；高度類欄位是 glTF Y，已扣原點 Z）。</summary>
    public sealed class ViewPlanRange
    {
        public double CutPlaneMeters { get; set; }
        public double? TopMeters { get; set; }
        public double? BottomMeters { get; set; }
        public double? ViewDepthMeters { get; set; }
    }

    public sealed class ViewCrop
    {
        public bool Active { get; set; }
        public Vec3 Min { get; set; }
        public Vec3 Max { get; set; }
    }

    /// <summary>剖面／立面的框（glTF 公尺）。right／up／ViewDirection 為單位向量。</summary>
    public sealed class ViewFrame
    {
        public Vec3 Origin { get; set; }
        public Vec3 Right { get; set; }
        public Vec3 Up { get; set; }
        public Vec3 ViewDirection { get; set; }
        public double WidthMeters { get; set; }
        public double HeightMeters { get; set; }
        /// <summary>檢視深度；遠裁剪關閉（不裁剪）時為 null＝不限深度（不輸出 0）。</summary>
        public double? DepthMeters { get; set; }
    }

    public sealed class ViewPositionLevel
    {
        public string LevelUniqueId { get; set; }
        public string Name { get; set; }
        public double ElevationMeters { get; set; }
        public double RevitZMeters { get; set; }
    }

    public sealed class ViewPositionEntry
    {
        public string UniqueId { get; set; }
        public string Name { get; set; }
        /// <summary>plan／ceiling／section／elevation／detail</summary>
        public string Kind { get; set; }
        public string LevelUniqueId { get; set; }
        public ViewPlanRange ViewRange { get; set; }
        public ViewCrop Crop { get; set; }
        public ViewFrame Frame { get; set; }
        /// <summary>觀看方向（glTF，單位向量，從相機看向模型）；detail 為 null。</summary>
        public Vec3? ViewDirection { get; set; }
    }

    /// <summary>
    /// view_positions.json 的座標換算與框組裝（規格 issue-manager docs/spec/revit-addin-export.md §5.3、§6.1）。
    /// 純計算，不依賴 Revit API：輸入是 Revit 內部座標（Z 向上）已轉成公尺的值。
    /// glTF 公式：gltf = (x−ox, z−oz, −(y−oy))，與 named_views.json 相同。
    /// </summary>
    public static class ViewPositionsMath
    {
        private const double Tolerance = 1e-9;
        private const double Orthogonality = 0.01;

        public static Vec3 ToGltfPoint(Vec3 revitMeters, Vec3 offset) =>
            new Vec3(revitMeters.X - offset.X, revitMeters.Z - offset.Z, -(revitMeters.Y - offset.Y));

        public static Vec3 ToGltfDirection(Vec3 d) => new Vec3(d.X, d.Z, -d.Y);

        /// <summary>Revit Z（公尺）→ glTF Y：只扣原點的 Z。</summary>
        public static double ToGltfHeight(double revitZMeters, Vec3 offset) => revitZMeters - offset.Z;

        public static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

        public static bool IsFinite(Vec3 v) => IsFinite(v.X) && IsFinite(v.Y) && IsFinite(v.Z);

        /// <summary>
        /// 觀看方向（Revit 座標）。Revit 的視圖座標 BasisX＝右、BasisY＝上、BasisZ＝右×上＝朝向觀看者；
        /// 看向模型的方向是它的反向，即 上×右。不直接用 View.ViewDirection，避免依賴其正負號定義。
        /// </summary>
        public static Vec3 LookDirection(Vec3 right, Vec3 up) => up.Cross(right).Normalized();

        /// <summary>局部盒（在 transform 座標下的 min／max）的八個角點，回傳 Revit 座標公尺。</summary>
        public static List<Vec3> BoxCorners(Vec3 origin, Vec3 basisX, Vec3 basisY, Vec3 basisZ, Vec3 localMin, Vec3 localMax)
        {
            var corners = new List<Vec3>(8);
            for (var x = 0; x < 2; x++)
                for (var y = 0; y < 2; y++)
                    for (var z = 0; z < 2; z++)
                    {
                        var lx = x == 0 ? localMin.X : localMax.X;
                        var ly = y == 0 ? localMin.Y : localMax.Y;
                        var lz = z == 0 ? localMin.Z : localMax.Z;
                        corners.Add(origin + basisX * lx + basisY * ly + basisZ * lz);
                    }
            return corners;
        }

        /// <summary>點集轉 glTF 後的軸對齊包圍盒；空集合或含非有限值回 null。</summary>
        public static ViewCrop BuildCrop(IEnumerable<Vec3> revitCornersMeters, Vec3 offset, bool active)
        {
            var points = revitCornersMeters?.Select(p => ToGltfPoint(p, offset)).ToList();
            if (points == null || points.Count == 0 || points.Any(p => !IsFinite(p))) return null;
            return new ViewCrop
            {
                Active = active,
                Min = new Vec3(points.Min(p => p.X), points.Min(p => p.Y), points.Min(p => p.Z)),
                Max = new Vec3(points.Max(p => p.X), points.Max(p => p.Y), points.Max(p => p.Z)),
            };
        }

        /// <summary>
        /// 平面視圖範圍。cutZ 為必填：沒有切高就回 null（網站端會把整個視圖視為無位置）。
        /// 其餘平面拿不到（如 Unlimited）給 null，不猜值。參數都是 Revit Z 公尺（絕對高度）。
        /// </summary>
        public static ViewPlanRange BuildPlanRange(double? cutZ, double? topZ, double? bottomZ, double? viewDepthZ, Vec3 offset) =>
            BuildPlanRange(cutZ, topZ, bottomZ, viewDepthZ, offset, out _);

        /// <summary>
        /// 同上並以 problem 回報略過原因：noCutPlane（沒有切高）、cutBelowBottom（切高低於下緣，資料矛盾）。
        /// 參數是絕對高度＝該平面所屬樓層高程＋該平面偏移（呼叫端已加好）。
        /// </summary>
        public static ViewPlanRange BuildPlanRange(double? cutZ, double? topZ, double? bottomZ, double? viewDepthZ, Vec3 offset, out string problem)
        {
            problem = null;
            if (!cutZ.HasValue || !IsFinite(cutZ.Value)) { problem = "noCutPlane"; return null; }
            if (bottomZ.HasValue && IsFinite(bottomZ.Value) && cutZ.Value < bottomZ.Value - 1e-9) { problem = "cutBelowBottom"; return null; }
            double? Height(double? z) => z.HasValue && IsFinite(z.Value) ? ToGltfHeight(z.Value, offset) : (double?)null;
            return new ViewPlanRange
            {
                CutPlaneMeters = ToGltfHeight(cutZ.Value, offset),
                TopMeters = Height(topZ),
                BottomMeters = Height(bottomZ),
                ViewDepthMeters = Height(viewDepthZ),
            };
        }

        /// <summary>
        /// 剖面／立面的 frame。局部座標（transform 基底）下的 CropBox：X 為右、Y 為上、Z 朝向觀看者；
        /// 剖切平面在局部 z＝0，檢視深度＝max(0, −minZ)（往遠處）。origin 取裁剪範圍在剖切平面上的中心。
        /// 回 null 並以 problem 說明原因：基底非單位／不垂直、寬高非正、含非有限值。
        /// note 非空表示仍產生 frame 但有需要提醒的事（例如 maxZ &gt; 0 的近側範圍被忽略）。
        /// </summary>
        public static ViewFrame BuildFrame(Vec3 originMeters, Vec3 basisX, Vec3 basisY, Vec3 localMinMeters, Vec3 localMaxMeters,
            Vec3 offset, out string problem, out string note) =>
            BuildFrame(originMeters, basisX, basisY, localMinMeters, localMaxMeters, offset, true, out problem, out note);

        /// <param name="farClipActive">遠裁剪是否啟用；false（不裁剪）時 DepthMeters 為 null＝不限深度，不輸出 0。</param>
        public static ViewFrame BuildFrame(Vec3 originMeters, Vec3 basisX, Vec3 basisY, Vec3 localMinMeters, Vec3 localMaxMeters,
            Vec3 offset, bool farClipActive, out string problem, out string note)
        {
            problem = null; note = null;
            if (!IsFinite(originMeters) || !IsFinite(basisX) || !IsFinite(basisY) || !IsFinite(localMinMeters) || !IsFinite(localMaxMeters))
            { problem = "座標含 NaN 或 Infinity"; return null; }
            var right = basisX.Normalized();
            var up = basisY.Normalized();
            if (right.Length < Tolerance || up.Length < Tolerance) { problem = "視圖基底向量長度為 0"; return null; }
            if (Math.Abs(right.Dot(up)) > Orthogonality) { problem = "視圖 right／up 不垂直"; return null; }
            up = (up - right * right.Dot(up)).Normalized(); // 吃掉微小的數值誤差，保證正交
            var width = localMaxMeters.X - localMinMeters.X;
            var height = localMaxMeters.Y - localMinMeters.Y;
            if (!(width > Tolerance) || !(height > Tolerance)) { problem = "裁剪框寬或高不是正數"; return null; }
            var depth = Math.Max(0, -localMinMeters.Z);
            if (localMaxMeters.Z > 1e-6) note = "裁剪框在剖切平面前方還有範圍（maxZ>0），已忽略，frame 只含剖切平面往遠處的深度";
            var centerX = (localMinMeters.X + localMaxMeters.X) / 2;
            var centerY = (localMinMeters.Y + localMaxMeters.Y) / 2;
            var originRevit = originMeters + right * centerX + up * centerY;
            var look = LookDirection(right, up);
            return new ViewFrame
            {
                Origin = ToGltfPoint(originRevit, offset),
                Right = ToGltfDirection(right).Normalized(),
                Up = ToGltfDirection(up).Normalized(),
                ViewDirection = ToGltfDirection(look).Normalized(),
                WidthMeters = width,
                HeightMeters = height,
                DepthMeters = farClipActive ? depth : (double?)null,
            };
        }

        // ---------------------------------------------------------------- JSON

        private static JArray Arr(Vec3 v)
        {
            if (!IsFinite(v)) throw new ArgumentException("座標包含 NaN 或 Infinity。");
            return new JArray(Math.Round(v.X, 6), Math.Round(v.Y, 6), Math.Round(v.Z, 6));
        }

        private static JToken Num(double? v) => v.HasValue && IsFinite(v.Value) ? new JValue(Math.Round(v.Value, 6)) : JValue.CreateNull();

        /// <summary>組出 view_positions.json 的內容（schemaVersion 1，規格 §6.1／§5.3）。</summary>
        public static JObject BuildFile(string revitDoc, Vec3 offset, IEnumerable<ViewPositionLevel> levels,
            IEnumerable<ViewPositionEntry> views, IEnumerable<string> warnings)
        {
            var viewArray = new JArray();
            foreach (var view in views)
            {
                var item = new JObject
                {
                    ["uniqueId"] = view.UniqueId,
                    ["name"] = view.Name,
                    ["kind"] = view.Kind,
                    ["levelUniqueId"] = view.LevelUniqueId == null ? JValue.CreateNull() : (JToken)view.LevelUniqueId,
                    ["viewRange"] = JValue.CreateNull(),
                    ["crop"] = JValue.CreateNull(),
                    ["sectionLine"] = JValue.CreateNull(),
                    ["viewDirection"] = view.ViewDirection.HasValue ? Arr(view.ViewDirection.Value) : JValue.CreateNull(),
                    ["frame"] = JValue.CreateNull(),
                };
                if (view.ViewRange != null)
                    item["viewRange"] = new JObject
                    {
                        ["topMeters"] = Num(view.ViewRange.TopMeters),
                        ["cutPlaneMeters"] = Num(view.ViewRange.CutPlaneMeters),
                        ["bottomMeters"] = Num(view.ViewRange.BottomMeters),
                        ["viewDepthMeters"] = Num(view.ViewRange.ViewDepthMeters),
                    };
                if (view.Crop != null)
                    item["crop"] = new JObject { ["active"] = view.Crop.Active, ["min"] = Arr(view.Crop.Min), ["max"] = Arr(view.Crop.Max) };
                if (view.Frame != null)
                {
                    var f = view.Frame;
                    item["frame"] = new JObject
                    {
                        ["origin"] = Arr(f.Origin), ["right"] = Arr(f.Right), ["up"] = Arr(f.Up),
                        ["widthMeters"] = Math.Round(f.WidthMeters, 6), ["heightMeters"] = Math.Round(f.HeightMeters, 6),
                        ["depthMeters"] = Num(f.DepthMeters),
                        ["viewDirection"] = Arr(f.ViewDirection),
                    };
                    // 剖線：剖切平面上沿 right 的線段，供人看與日後使用；網站端目前不讀。
                    item["sectionLine"] = new JObject
                    {
                        ["p0"] = Arr(f.Origin - f.Right * (f.WidthMeters / 2)),
                        ["p1"] = Arr(f.Origin + f.Right * (f.WidthMeters / 2)),
                    };
                }
                viewArray.Add(item);
            }

            return new JObject
            {
                ["schemaVersion"] = 1,
                ["originOffsetMeters"] = Arr(offset),
                ["revitDoc"] = revitDoc,
                ["levels"] = new JArray((levels ?? Enumerable.Empty<ViewPositionLevel>()).Select(l => new JObject
                {
                    ["levelUniqueId"] = l.LevelUniqueId, ["name"] = l.Name,
                    ["elevationMeters"] = Math.Round(l.ElevationMeters, 6), ["revitZMeters"] = Math.Round(l.RevitZMeters, 6),
                })),
                ["views"] = viewArray,
                ["warnings"] = new JArray(warnings ?? Enumerable.Empty<string>()),
            };
        }
    }
}
