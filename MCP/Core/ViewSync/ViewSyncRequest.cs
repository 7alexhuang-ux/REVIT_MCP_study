using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RevitMCP.Core.Walkthrough;

namespace RevitMCP.Core.ViewSync
{
    // 網頁送來的 viewsync_* 指令參數解析與驗證（純 JSON → 強型別，不依賴 Revit API）。
    // 欄位格式見 issue-manager docs/spec/revit-viewsync.md §4、§6。

    public sealed class ViewSyncCamera
    {
        public Vec3 Position, Target, Up;
        public double? FovVerticalDeg;
        public double? Aspect;
        public bool Orthographic;
        public FovCropModel FovModel = FovCropModel.TargetPlane;
    }

    public sealed class ViewSyncClipping
    {
        public bool Enabled;
        public Vec3? Min, Max;
    }

    public sealed class ViewSyncPlane
    {
        public Vec3 Origin, Normal, Up;
        public Vec3? Right;
        public double Width, Height, Depth;
    }

    public sealed class ViewSyncHiddenRef
    {
        /// <summary>null＝主模型元件；有值＝該連結文件標題內的元件。</summary>
        public string LinkTitle;
        public string UniqueId;
    }

    public sealed class ViewSyncLayer
    {
        public string Source;
        public bool Visible = true;
        /// <summary>0（不透明）–1（全透明）。</summary>
        public double Transparency;
    }

    public sealed class ViewSyncRequest
    {
        public string ModelTitle, ModelGuid;
        public Vec3 OriginOffsetMeters;
        public bool HasOrigin;
        public ViewSyncCamera Camera;
        public ViewSyncClipping Clipping = new ViewSyncClipping();
        public string Mode;
        public List<ViewSyncHiddenRef> Hidden = new List<ViewSyncHiddenRef>();
        public List<ViewSyncLayer> Layers = new List<ViewSyncLayer>();
        public Dictionary<string, string> DesignOptions = new Dictionary<string, string>();

        // viewsync_open_view
        public string ViewUniqueId;

        // viewsync_create_view
        public string Kind;      // 3d | section | plan | ceiling
        public string Name;
        public ViewSyncPlane Plane;
        public string LevelUniqueId;
        public double? CutHeightMeters;
    }

    public static class ViewSyncRequestParser
    {
        public const int MaxHiddenEntries = 50000;

        public static readonly string[] Kinds = { "3d", "section", "plan", "ceiling" };

        /// <summary>
        /// 解析共同欄位：model、originOffsetMeters、state（camera／clipping／mode／hiddenUniqueIds／layers／designOptions）。
        /// 欄位同時接受放在 state 內或參數最上層。不檢查特定指令專屬的必填欄位。
        /// </summary>
        public static ViewSyncRequest ParseCommon(JObject p)
        {
            if (p == null) throw new ViewSyncException("bad_request", "缺少參數。");
            var req = new ViewSyncRequest();
            var model = p["model"] as JObject;
            req.ModelTitle = Str(model?["title"]);
            req.ModelGuid = Str(model?["guid"]);
            if (p["originOffsetMeters"] != null && p["originOffsetMeters"].Type != JTokenType.Null)
            {
                req.OriginOffsetMeters = Vec(p["originOffsetMeters"], "originOffsetMeters");
                req.HasOrigin = true;
            }
            var state = p["state"] as JObject ?? new JObject();

            var cam = (state["camera"] ?? p["camera"]) as JObject;
            if (cam != null) req.Camera = ParseCamera(cam);

            var clip = (state["clipping"] ?? p["clipping"]) as JObject;
            if (clip != null) req.Clipping = ParseClipping(clip);

            req.Mode = Str(state["mode"] ?? p["mode"]);
            req.Hidden = ParseHidden(state["hiddenUniqueIds"] ?? p["hiddenUniqueIds"]);
            req.Layers = ParseLayers(state["layers"] ?? p["layers"]);
            req.DesignOptions = ParseDesignOptions(state["designOptions"] ?? p["designOptions"]);
            return req;
        }

        /// <summary>請求必須指明模型（title 或 guid）與 originOffsetMeters（沒有就用 [0,0,0] 明講），否則 model_missing／origin_missing。</summary>
        public static void RequireModel(ViewSyncRequest req)
        {
            if (!req.HasOrigin)
                throw new ViewSyncException("origin_missing", "請求沒有帶 originOffsetMeters（舊 GLB 沒有原點時請明確送 [0,0,0]）。");
            if (string.IsNullOrWhiteSpace(req.ModelTitle) && string.IsNullOrWhiteSpace(req.ModelGuid))
                throw new ViewSyncException("model_missing", "請求沒有帶 model.title／model.guid，無法確認要改哪個 Revit 檔。");
        }

        public static ViewSyncRequest ParseApplyTemp(JObject p)
        {
            var req = ParseCommon(p);
            RequireModel(req);
            if (req.Camera == null) throw new ViewSyncException("camera_missing", "缺少 state.camera。");
            return req;
        }

        public static ViewSyncRequest ParseOpenView(JObject p)
        {
            var req = ParseCommon(p);
            req.ViewUniqueId = Str(p?["viewUniqueId"]);
            if (string.IsNullOrWhiteSpace(req.ViewUniqueId)) throw new ViewSyncException("bad_request", "缺少 viewUniqueId。");
            return req;
        }

        public static ViewSyncRequest ParseCreateView(JObject p)
        {
            var req = ParseCommon(p);
            RequireModel(req);
            req.Kind = (Str(p["kind"]) ?? string.Empty).Trim().ToLowerInvariant();
            if (!Kinds.Contains(req.Kind)) throw new ViewSyncException("bad_request", "kind 必須是 3d、section、plan 或 ceiling。");
            req.Name = Str(p["name"]);
            if (string.IsNullOrWhiteSpace(req.Name)) throw new ViewSyncException("bad_request", "缺少 name（新視圖名稱）。");

            var state = p["state"] as JObject ?? new JObject();
            if (req.Kind == "3d" && req.Camera == null) throw new ViewSyncException("camera_missing", "建立 3D 視圖需要 state.camera。");
            if (req.Kind == "section")
            {
                var plane = (p["plane"] ?? state["plane"]) as JObject;
                if (plane == null) throw new ViewSyncException("plane_missing", "建立剖面需要 plane。");
                req.Plane = ParsePlane(plane);
            }

            if (req.Kind == "plan" || req.Kind == "ceiling")
            {
                var hint = (p["levelHint"] ?? state["levelHint"]) as JObject;
                req.LevelUniqueId = Str(hint?["levelUniqueId"]);
                req.CutHeightMeters = Num(hint?["cutHeightMeters"], "levelHint.cutHeightMeters");
                if (!req.CutHeightMeters.HasValue && req.Clipping.Enabled && req.Clipping.Max.HasValue)
                    req.CutHeightMeters = req.Clipping.Max.Value.Y; // 剖面框頂面（glTF Y）
                if (!req.CutHeightMeters.HasValue)
                    throw new ViewSyncException("level_hint_missing", "建立平面／天花平面需要 levelHint.cutHeightMeters（或啟用的剖面框）。");
            }

            return req;
        }

        // ------------------------------------------------------------------ 子結構

        public static ViewSyncCamera ParseCamera(JObject cam)
        {
            var c = new ViewSyncCamera
            {
                Position = Vec(cam["position"], "camera.position"),
                Target = Vec(cam["target"], "camera.target"),
                Up = cam["up"] != null && cam["up"].Type != JTokenType.Null ? Vec(cam["up"], "camera.up") : new Vec3(0, 1, 0),
                FovVerticalDeg = Num(cam["fovVerticalDeg"], "camera.fovVerticalDeg"),
                Aspect = Num(cam["aspect"], "camera.aspect"),
                Orthographic = cam["orthographic"]?.Type == JTokenType.Boolean && cam["orthographic"].Value<bool>(),
            };
            var model = (Str(cam["fovModel"]) ?? string.Empty).Trim().ToLowerInvariant();
            if (model == "near" || model == "nearplane") c.FovModel = FovCropModel.NearPlane;
            return c;
        }

        public static ViewSyncClipping ParseClipping(JObject clip)
        {
            var c = new ViewSyncClipping { Enabled = clip["enabled"]?.Type == JTokenType.Boolean && clip["enabled"].Value<bool>() };
            var box = clip["boxWorld"] as JObject;
            if (box != null)
            {
                c.Min = Vec(box["min"], "clipping.boxWorld.min");
                c.Max = Vec(box["max"], "clipping.boxWorld.max");
            }

            if (c.Enabled && !(c.Min.HasValue && c.Max.HasValue))
                throw new ViewSyncException("bad_request", "clipping.enabled 為 true 時必須帶 clipping.boxWorld.min／max。");
            return c;
        }

        public static ViewSyncPlane ParsePlane(JObject plane)
        {
            return new ViewSyncPlane
            {
                Origin = Vec(plane["origin"], "plane.origin"),
                Normal = Vec(plane["normal"], "plane.normal"),
                Up = Vec(plane["up"], "plane.up"),
                Right = plane["right"] != null && plane["right"].Type != JTokenType.Null ? Vec(plane["right"], "plane.right") : (Vec3?)null,
                Width = Num(plane["width"], "plane.width") ?? throw new ViewSyncException("plane_invalid", "plane.width 缺少。"),
                Height = Num(plane["height"], "plane.height") ?? throw new ViewSyncException("plane_invalid", "plane.height 缺少。"),
                Depth = Num(plane["depth"], "plane.depth") ?? throw new ViewSyncException("plane_invalid", "plane.depth 缺少。"),
            };
        }

        /// <summary>hiddenUniqueIds：字串＝主模型元件；{ linkTitle, uniqueId }＝連結內元件。上限 50000 筆，超過 bad_request。</summary>
        public static List<ViewSyncHiddenRef> ParseHidden(JToken token)
        {
            var list = new List<ViewSyncHiddenRef>();
            if (token == null || token.Type == JTokenType.Null) return list;
            if (!(token is JArray arr)) throw new ViewSyncException("bad_request", "hiddenUniqueIds 必須是陣列。");
            if (arr.Count > MaxHiddenEntries) throw new ViewSyncException("bad_request", "hiddenUniqueIds 超過 " + MaxHiddenEntries + " 筆。");
            foreach (var item in arr)
            {
                if (item.Type == JTokenType.String)
                {
                    var id = item.Value<string>();
                    if (!string.IsNullOrWhiteSpace(id)) list.Add(new ViewSyncHiddenRef { UniqueId = id.Trim() });
                }
                else if (item is JObject o)
                {
                    var id = Str(o["uniqueId"]);
                    if (string.IsNullOrWhiteSpace(id)) continue;
                    var link = Str(o["linkTitle"]);
                    list.Add(new ViewSyncHiddenRef { UniqueId = id.Trim(), LinkTitle = string.IsNullOrWhiteSpace(link) ? null : link.Trim() });
                }
            }

            return list;
        }

        public static List<ViewSyncLayer> ParseLayers(JToken token)
        {
            var list = new List<ViewSyncLayer>();
            if (token == null || token.Type == JTokenType.Null) return list;
            if (!(token is JArray arr)) throw new ViewSyncException("bad_request", "layers 必須是陣列。");
            foreach (var item in arr.OfType<JObject>())
            {
                var source = Str(item["source"]);
                if (string.IsNullOrWhiteSpace(source)) continue;
                var layer = new ViewSyncLayer { Source = source.Trim() };
                if (item["visible"]?.Type == JTokenType.Boolean) layer.Visible = item["visible"].Value<bool>();
                var t = Num(item["transparency"], "layers[].transparency");
                if (t.HasValue) layer.Transparency = Math.Max(0, Math.Min(1, t.Value));
                list.Add(layer);
            }

            return list;
        }

        public static Dictionary<string, string> ParseDesignOptions(JToken token)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            if (token is JObject o)
                foreach (var prop in o.Properties())
                    if (prop.Value.Type == JTokenType.String) map[prop.Name] = prop.Value.Value<string>();
            return map;
        }

        // ------------------------------------------------------------------ 基本型別

        private static string Str(JToken t) => t != null && t.Type == JTokenType.String ? t.Value<string>() : null;

        private static double? Num(JToken t, string name)
        {
            if (t == null || t.Type == JTokenType.Null) return null;
            if (t.Type != JTokenType.Integer && t.Type != JTokenType.Float)
                throw new ViewSyncException("bad_request", name + " 必須是數字。");
            var v = t.Value<double>();
            if (double.IsNaN(v) || double.IsInfinity(v)) throw new ViewSyncException("bad_request", name + " 不是有限數字。");
            return v;
        }

        private static Vec3 Vec(JToken t, string name)
        {
            if (!(t is JArray a) || a.Count != 3 || a.Any(v => v.Type != JTokenType.Integer && v.Type != JTokenType.Float))
                throw new ViewSyncException("bad_request", name + " 必須是三個數字的陣列 [x, y, z]。");
            var v3 = new Vec3(a[0].Value<double>(), a[1].Value<double>(), a[2].Value<double>());
            if (!ViewPositionsMath.IsFinite(v3)) throw new ViewSyncException("bad_request", name + " 含 NaN 或 Infinity。");
            return v3;
        }
    }
}
