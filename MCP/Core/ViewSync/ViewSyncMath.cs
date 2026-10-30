using System;
using System.Collections.Generic;
using System.Linq;
using RevitMCP.Core.Walkthrough;

namespace RevitMCP.Core.ViewSync
{
    // 網頁 3D 視角／剖面 → Revit 聯動（issue-manager D-710）的座標與幾何純計算。
    // 不依賴 Revit API：輸入輸出都是 Vec3／數字，單位在名稱上寫清楚（Meters＝glTF 公尺；Feet＝Revit 內部呎）。
    // glTF（Y 向上、已扣 originOffsetMeters）→ Revit 內部座標：
    //   x_ft = (gx + ox)/0.3048、y_ft = (−gz + oy)/0.3048、z_ft = (gy + oz)/0.3048（與 ViewPositionsMath.ToGltfPoint 互逆）。

    public sealed class CameraSolution
    {
        public Vec3 EyeFeet { get; set; }
        public Vec3 Forward { get; set; }
        public Vec3 Up { get; set; }
        public double TargetDistanceFeet { get; set; }
    }

    public sealed class ClipBoxSolution
    {
        public Vec3 MinFeet { get; set; }
        public Vec3 MaxFeet { get; set; }
    }

    /// <summary>剖面視圖的框：Transform 基底（Revit 慣例 X=右、Y=上、Z=朝向觀看者＝−看的方向）與局部 Min／Max（呎）。</summary>
    public sealed class SectionFrame
    {
        public Vec3 OriginFeet { get; set; }
        public Vec3 BasisX { get; set; }
        public Vec3 BasisY { get; set; }
        public Vec3 BasisZ { get; set; }
        public Vec3 LocalMinFeet { get; set; }
        public Vec3 LocalMaxFeet { get; set; }
    }

    public sealed class LevelRef
    {
        public string UniqueId { get; set; }
        public string Name { get; set; }
        public double ElevationFeet { get; set; }
    }

    public sealed class PlanRangeOffsets
    {
        public double Top { get; set; }
        public double Cut { get; set; }
        public double Bottom { get; set; }
        public double ViewDepth { get; set; }
    }

    /// <summary>透視視野的裁剪框換算模型。</summary>
    public enum FovCropModel
    {
        /// <summary>
        /// 預設（2026-10-31 Revit 2024 實測，Snowdon「IM 對位（臨時）」視圖）：透視裁剪框的 X／Y 是對稱矩形，
        /// 位於眼睛前方固定的焦距平面（新建透視視圖＝<see cref="ViewSyncMath.RevitPerspectiveFocalFeet"/>＝0.1 ft），
        /// 與 Max.Z（近裁剪）、遠裁剪、目標距離都無關：半高 ＝ 焦距 × tan(垂直視角/2)。
        /// 實測：半高 0.04142 ft → 讀回垂直 45.00°；半高加倍 → 79.28°（tan 線性，焦距固定 0.1 ft）。
        /// </summary>
        FocalPlane,
        /// <summary>已證偽（只留給 viewsync_debug_fov 對照）：X／Y 半寬 ＝ 目標距離 × tan(視角/2)。目標距離 63 m 時視角變成約 180°，畫面全空白。</summary>
        TargetPlane,
        /// <summary>已證偽（只留給 viewsync_debug_fov 對照）：CameraPathMath 舊假設，Max 角在近裁剪面、Min 角在遠裁剪面；Revit 把 Min／Max 當同一平面的矩形，結果不對稱。</summary>
        NearPlane,
    }

    public enum ViewSyncDisplayMode { None, FlatColors, Shading, Wireframe, HiddenLine, Realistic }

    public static class ViewSyncMath
    {
        public const double FeetToMeters = CameraPathMath.FeetToMeters;
        private const double Eps = 1e-9;

        /// <summary>Revit 新建透視視圖的裁剪框焦距平面距離（呎）。2026-10-31 實測 0.1 ft（＝30.48 mm）；既有相機可能不同，套用後須讀回校正。</summary>
        public const double RevitPerspectiveFocalFeet = 0.1;

        // ------------------------------------------------------------------ 座標

        public static Vec3 GltfPointToRevitFeet(Vec3 g, Vec3 offsetMeters) =>
            new Vec3((g.X + offsetMeters.X) / FeetToMeters, (-g.Z + offsetMeters.Y) / FeetToMeters, (g.Y + offsetMeters.Z) / FeetToMeters);

        /// <summary>方向向量（無單位）：glTF → Revit。</summary>
        public static Vec3 GltfDirToRevit(Vec3 d) => new Vec3(d.X, -d.Z, d.Y);

        public static Vec3 RevitFeetToGltfPoint(Vec3 feet, Vec3 offsetMeters) =>
            new Vec3(feet.X * FeetToMeters - offsetMeters.X, feet.Z * FeetToMeters - offsetMeters.Z, -(feet.Y * FeetToMeters - offsetMeters.Y));

        public static bool IsFinite(Vec3 v) => ViewPositionsMath.IsFinite(v);

        // ------------------------------------------------------------------ 相機

        /// <summary>
        /// 網頁相機 → Revit ViewOrientation3D 的 eye／forward／up。forward 為 (target − position) 正規化；
        /// up 對 forward 正交化（退化時退回世界 Z）。position 與 target 重合 → ViewSyncException(camera_degenerate)。
        /// </summary>
        public static CameraSolution ComputeCamera(Vec3 position, Vec3 target, Vec3 up, Vec3 offsetMeters)
        {
            var eye = GltfPointToRevitFeet(position, offsetMeters);
            var tgt = GltfPointToRevitFeet(target, offsetMeters);
            var forward = tgt - eye;
            if (forward.Length < Eps)
                throw new ViewSyncException("camera_degenerate", "相機位置與目標點重合，無法決定視線方向。");
            var upRevit = GltfDirToRevit(up);
            var (f, u) = CameraPathMath.OrthonormalizeCamera(forward.Normalized(), upRevit);
            return new CameraSolution { EyeFeet = eye, Forward = f, Up = u, TargetDistanceFeet = forward.Length };
        }

        // ------------------------------------------------------------------ 透視視野

        /// <summary>水平視角（度）＝ 2·atan(aspect·tan(垂直視角/2))。</summary>
        public static double HorizontalFovDegrees(double fovVerticalDeg, double aspect)
        {
            ValidateFov(fovVerticalDeg, aspect);
            return 2.0 * Math.Atan(aspect * Math.Tan(fovVerticalDeg * Math.PI / 360.0)) * 180.0 / Math.PI;
        }

        /// <summary>
        /// 由垂直視角＋寬高比算 Revit 透視裁剪框的 Min／Max（呎，裁剪框座標：X 右、Y 上、Z 朝向觀看者，眼睛在原點）。
        /// Max.Z＝−nearFeet（近裁剪面）、Min.Z＝−farFeet（遠裁剪面），X／Y 範圍依 <paramref name="model"/>：
        /// FocalPlane（預設）＝對稱矩形，半寬／半高 ＝ <paramref name="focalFeet"/> × tan(視角/2)，與目標距離無關。
        /// </summary>
        public static (Vec3 Min, Vec3 Max) ComputePerspectiveCrop(double fovVerticalDeg, double aspect, double targetDistanceFeet,
            double nearFeet, double farFeet, FovCropModel model, double focalFeet = RevitPerspectiveFocalFeet)
        {
            ValidateFov(fovVerticalDeg, aspect);
            if (!(nearFeet > 0) || !(farFeet > nearFeet))
                throw new ViewSyncException("fov_invalid", "透視近／遠裁剪距離不合理（需 0 < near < far）。");
            var tanY = Math.Tan(fovVerticalDeg * Math.PI / 360.0);
            var tanX = tanY * aspect;
            double halfX, halfY;
            if (model == FovCropModel.NearPlane)
            {
                halfX = nearFeet * tanX; halfY = nearFeet * tanY;
                return (new Vec3(-farFeet * tanX, -farFeet * tanY, -farFeet), new Vec3(halfX, halfY, -nearFeet));
            }

            var dist = model == FovCropModel.FocalPlane ? focalFeet : targetDistanceFeet;
            if (!(dist > Eps))
                throw new ViewSyncException("fov_invalid", model == FovCropModel.FocalPlane ? "焦距平面距離必須大於 0。" : "目標距離必須大於 0。");
            halfX = dist * tanX; halfY = dist * tanY;
            return (new Vec3(-halfX, -halfY, -farFeet), new Vec3(halfX, halfY, -nearFeet));
        }

        /// <summary>
        /// 讀回校正：套用裁剪框後 Revit 實際視角（measured，度）與想要的不同時（既有相機焦距不是 0.1 ft），
        /// 依 tan 比例縮放 X／Y 半寬（焦距固定時 tan(半角) 與半寬成正比），Z 不動，維持對稱。
        /// measured 不合理（≤0 或 ≥180）→ 回原框。
        /// </summary>
        public static (Vec3 Min, Vec3 Max) RescaleCropToFov(Vec3 min, Vec3 max, double desiredVDeg, double desiredHDeg,
            double measuredVDeg, double measuredHDeg)
        {
            double Ratio(double want, double got) =>
                got > 0 && got < 180 && want > 0 && want < 180 ? Math.Tan(want * Math.PI / 360.0) / Math.Tan(got * Math.PI / 360.0) : 1.0;
            var kx = Ratio(desiredHDeg, measuredHDeg);
            var ky = Ratio(desiredVDeg, measuredVDeg);
            var halfX = (max.X - min.X) / 2 * kx;
            var halfY = (max.Y - min.Y) / 2 * ky;
            return (new Vec3(-halfX, -halfY, min.Z), new Vec3(halfX, halfY, max.Z));
        }

        /// <summary>半高（或半寬）與距離換算隱含視角（度）。除錯用：同一個裁剪框在不同模型假設下的 FOV。</summary>
        public static double ImpliedFovDegrees(double halfExtent, double distance) =>
            distance > Eps ? 2.0 * Math.Atan(halfExtent / distance) * 180.0 / Math.PI : double.NaN;

        private static void ValidateFov(double fovVerticalDeg, double aspect)
        {
            if (!(fovVerticalDeg > 0 && fovVerticalDeg < 180) || double.IsNaN(fovVerticalDeg))
                throw new ViewSyncException("fov_invalid", "垂直視角必須在 0–180 度之間。");
            if (!(aspect > 0) || double.IsInfinity(aspect))
                throw new ViewSyncException("fov_invalid", "畫面寬高比必須是正數。");
        }

        // ------------------------------------------------------------------ 剖面框（軸對齊）

        /// <summary>網頁世界座標剖面框（glTF 公尺）→ Revit 軸對齊盒（呎）。八個角轉換後取 min／max；任一軸寬度≈0 → clip_box_empty。</summary>
        public static ClipBoxSolution ComputeClipBox(Vec3 minWorld, Vec3 maxWorld, Vec3 offsetMeters)
        {
            var corners = new List<Vec3>(8);
            for (var i = 0; i < 8; i++)
            {
                var g = new Vec3((i & 1) == 0 ? minWorld.X : maxWorld.X, (i & 2) == 0 ? minWorld.Y : maxWorld.Y, (i & 4) == 0 ? minWorld.Z : maxWorld.Z);
                corners.Add(GltfPointToRevitFeet(g, offsetMeters));
            }

            var min = new Vec3(corners.Min(c => c.X), corners.Min(c => c.Y), corners.Min(c => c.Z));
            var max = new Vec3(corners.Max(c => c.X), corners.Max(c => c.Y), corners.Max(c => c.Z));
            if (max.X - min.X < 1e-6 || max.Y - min.Y < 1e-6 || max.Z - min.Z < 1e-6)
                throw new ViewSyncException("clip_box_empty", "剖面框某個方向的厚度為 0，無法套用。");
            return new ClipBoxSolution { MinFeet = min, MaxFeet = max };
        }

        // ------------------------------------------------------------------ 剖面視圖框

        /// <summary>
        /// 網頁剖切平面 → Revit 剖面框。lookDirection＝從相機看進模型的方向；upWorld 為剖面上方；rightWorld 為（選填）右方，
        /// 有給時必須與 look×up 同向（否則 plane_basis_inconsistent）。origin＝剖切平面矩形中心。
        /// Revit 慣例 BasisX＝右、BasisY＝上、BasisZ＝BasisX×BasisY＝−look（朝向觀看者）；
        /// 剖切平面在局部 z＝0，檢視深度往遠處（局部 −Z）：LocalMin＝(−W/2, −H/2, −depth)、LocalMax＝(W/2, H/2, 0)。
        /// </summary>
        public static SectionFrame ComputeSectionFrame(Vec3 originWorld, Vec3 lookWorld, Vec3 upWorld, Vec3? rightWorld,
            double widthMeters, double heightMeters, double depthMeters, Vec3 offsetMeters)
        {
            if (!(widthMeters > 1e-6) || !(heightMeters > 1e-6) || !(depthMeters > 1e-6))
                throw new ViewSyncException("plane_invalid", "剖面的寬、高、深必須是正數。");
            var look = GltfDirToRevit(lookWorld).Normalized();
            if (look.Length < 0.5) throw new ViewSyncException("plane_invalid", "剖面的觀看方向長度為 0。");
            var up = GltfDirToRevit(upWorld);
            up = (up - look * up.Dot(look));
            if (up.Length < 1e-6) throw new ViewSyncException("plane_invalid", "剖面的上方向與觀看方向平行。");
            up = up.Normalized();
            var right = look.Cross(up).Normalized();
            if (rightWorld.HasValue)
            {
                var given = GltfDirToRevit(rightWorld.Value).Normalized();
                if (given.Dot(right) < 0.99)
                    throw new ViewSyncException("plane_basis_inconsistent", "剖面的 right 與 normal／up 不是右手系（right 應等於 normal × up）。");
            }

            var basisZ = right.Cross(up).Normalized(); // = −look
            var w = widthMeters / FeetToMeters; var h = heightMeters / FeetToMeters; var d = depthMeters / FeetToMeters;
            return new SectionFrame
            {
                OriginFeet = GltfPointToRevitFeet(originWorld, offsetMeters),
                BasisX = right,
                BasisY = up,
                BasisZ = basisZ,
                LocalMinFeet = new Vec3(-w / 2, -h / 2, -d),
                LocalMaxFeet = new Vec3(w / 2, h / 2, 0),
            };
        }

        // ------------------------------------------------------------------ 樓層與平面視圖範圍

        /// <summary>取高度不高於 cutHeightFeet 的最近樓層；全部都比切高高 → 取最低樓層並 belowAll＝true；沒有樓層回 null。</summary>
        public static LevelRef PickLevel(IReadOnlyList<LevelRef> levels, double cutHeightFeet, out bool belowAll)
        {
            belowAll = false;
            if (levels == null || levels.Count == 0) return null;
            LevelRef best = null;
            foreach (var l in levels)
                if (l.ElevationFeet <= cutHeightFeet + 1e-6 && (best == null || l.ElevationFeet > best.ElevationFeet)) best = l;
            if (best != null) return best;
            belowAll = true;
            return levels.OrderBy(l => l.ElevationFeet).First();
        }

        /// <summary>
        /// 把新平面視圖的視圖範圍調整成切高＝desiredCut（相對樓層的偏移，呎）：
        /// Revit 要求 top ≥ cut ≥ bottom ≥ viewDepth，所以必要時抬高 top、壓低 bottom／viewDepth，其餘不動。
        /// </summary>
        public static PlanRangeOffsets FitPlanRange(double top, double cut, double bottom, double viewDepth, double desiredCut)
        {
            const double gap = 0.1;
            var newCut = desiredCut;
            var newTop = Math.Max(top, newCut + gap);
            var newBottom = Math.Min(bottom, newCut);
            var newDepth = Math.Min(viewDepth, newBottom);
            return new PlanRangeOffsets { Top = newTop, Cut = newCut, Bottom = newBottom, ViewDepth = newDepth };
        }

        /// <summary>
        /// 天花平面的視圖範圍（相對樓層的偏移，呎）：切高 desiredCut；頂部＝上一層樓的偏移（upperLevelOffset，沒有上一層給 null）
        /// 或切高＋3 m，且至少比切高高 gap；底部與視圖深度不高於 min(0, 切高)。保證 top ≥ cut ≥ bottom ≥ viewDepth，視圖範圍不會不合法。
        /// </summary>
        public static PlanRangeOffsets FitCeilingRange(double desiredCut, double? upperLevelOffset)
        {
            const double gap = 0.1;
            const double defaultAbove = 3.0 / FeetToMeters;
            var top = upperLevelOffset.HasValue && upperLevelOffset.Value >= desiredCut + gap
                ? upperLevelOffset.Value
                : desiredCut + defaultAbove;
            var bottom = Math.Min(0.0, desiredCut);
            return new PlanRangeOffsets { Top = top, Cut = desiredCut, Bottom = bottom, ViewDepth = bottom };
        }

        /// <summary>
        /// levelHint.cutHeightMeters ＝ 世界 y（glTF 公尺，已扣 originOffset）。換成 Revit 內部呎只加一次原點 Z。
        /// 與 Clipping.Max.Y 備援同一座標系。
        /// </summary>
        public static double CutHeightToRevitFeet(double cutHeightMetersGltf, double originZMeters) =>
            (cutHeightMetersGltf + originZMeters) / FeetToMeters;

        // ------------------------------------------------------------------ 名稱

        private static readonly char[] ForbiddenNameChars = { '\\', ':', '{', '}', '[', ']', '|', ';', '<', '>', '?', '`', '~' };

        /// <summary>Revit 視圖名稱不能含 \ : { } [ ] | ; &lt; &gt; ? ` ~ 與控制字元；空白則用預設名。</summary>
        public static string SanitizeViewName(string name, string fallback = "IM 視圖")
        {
            var chars = (name ?? string.Empty).Select(c => char.IsControl(c) ? ' ' : ForbiddenNameChars.Contains(c) ? '_' : c).ToArray();
            var s = new string(chars).Trim();
            if (s.Length > 200) s = s.Substring(0, 200).Trim();
            return s.Length == 0 ? fallback : s;
        }

        /// <summary>名稱重複時加「 (2)」「 (3)」…；比對不分大小寫。</summary>
        public static string UniqueName(string desired, IEnumerable<string> existingNames)
        {
            var existing = new HashSet<string>(existingNames ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            if (!existing.Contains(desired)) return desired;
            for (var n = 2; n < 100000; n++)
            {
                var candidate = desired + " (" + n + ")";
                if (!existing.Contains(candidate)) return candidate;
            }

            return desired + " (" + Guid.NewGuid().ToString("N").Substring(0, 6) + ")";
        }

        public const string TempViewNamePerspective = "IM 對位（臨時）";
        public const string TempViewNameOrthographic = "IM 對位（臨時・正交）";

        /// <summary>臨時視圖名稱：Revit 同一個 3D 視圖不能在透視與正交間切換，所以兩種各一個專用視圖。</summary>
        public static string TempViewName(bool orthographic) => orthographic ? TempViewNameOrthographic : TempViewNamePerspective;

        // ------------------------------------------------------------------ 顯示模式

        /// <summary>
        /// 網頁顯示模式字串 → 枚舉（不分大小寫，接受英文代號與中文）。
        /// 一致色彩→FlatColors（Revit「一致的色彩」）、著色→Shading、線框→Wireframe、隱藏線→HLR、寫實→Realistic。
        /// 空白或無法辨識回 None（呼叫端回警告、不改視覺樣式）。
        /// </summary>
        public static ViewSyncDisplayMode ParseDisplayMode(string mode)
        {
            if (string.IsNullOrWhiteSpace(mode)) return ViewSyncDisplayMode.None;
            switch (mode.Trim().ToLowerInvariant().Replace("_", "").Replace("-", "").Replace(" ", ""))
            {
                case "consistent": case "consistentcolors": case "consistentcolor": case "flat": case "flatcolors": case "flatcolor": case "一致色彩": case "一致的色彩":
                    return ViewSyncDisplayMode.FlatColors;
                case "shaded": case "shading": case "著色": case "陰影": case "shadedwithedges": case "shadingwithedges":
                    return ViewSyncDisplayMode.Shading;
                case "wireframe": case "wire": case "線框":
                    return ViewSyncDisplayMode.Wireframe;
                case "hiddenline": case "hidden": case "hlr": case "隱藏線":
                    return ViewSyncDisplayMode.HiddenLine;
                case "realistic": case "real": case "寫實":
                    return ViewSyncDisplayMode.Realistic;
                default:
                    return ViewSyncDisplayMode.None;
            }
        }

        // ------------------------------------------------------------------ 連結名稱、模型與原點核對

        /// <summary>連結標題比對：不分大小寫、忽略 .rvt 副檔名與前後空白。</summary>
        public static bool TitleMatches(string a, string b)
        {
            string N(string s)
            {
                s = (s ?? string.Empty).Trim();
                foreach (var ext in new[] { ".rvt", ".glb", ".gltf" })
                    if (s.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) { s = s.Substring(0, s.Length - ext.Length); break; }
                return s;
            }

            var na = N(a); var nb = N(b);
            return na.Length > 0 && string.Equals(na, nb, StringComparison.OrdinalIgnoreCase);
        }

        public enum ModelMatch { Unspecified, Match, TitleOnly, Mismatch }

        /// <summary>
        /// 請求的模型（title／guid）對 Revit 目前文件：有 guid 比 guid（Match／Mismatch）；沒 guid 只比標題（TitleOnly＝未驗證／Mismatch）；
        /// 都沒給 Unspecified。
        /// </summary>
        public static ModelMatch CompareModel(string requestTitle, string requestGuid, string docTitle, string docGuid)
        {
            if (!string.IsNullOrWhiteSpace(requestGuid))
                return string.Equals(requestGuid.Trim(), (docGuid ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase) ? ModelMatch.Match : ModelMatch.Mismatch;
            if (!string.IsNullOrWhiteSpace(requestTitle))
                return TitleMatches(requestTitle, docTitle) ? ModelMatch.TitleOnly : ModelMatch.Mismatch;
            return ModelMatch.Unspecified;
        }

        public enum OriginMatch { Unverified, Match, Mismatch }

        /// <summary>請求的 originOffsetMeters 對 add-in 自己知道的值（camera.json）；known 為 null＝無從比對。容差 1 mm。</summary>
        public static OriginMatch CompareOrigin(Vec3 requested, Vec3? known)
        {
            if (!known.HasValue) return OriginMatch.Unverified;
            return (requested - known.Value).Length <= 0.001 ? OriginMatch.Match : OriginMatch.Mismatch;
        }
    }
}
