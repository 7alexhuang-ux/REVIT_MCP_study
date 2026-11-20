using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Autodesk.Revit.DB;

#if REVIT2025_OR_GREATER
using IdType = System.Int64;
#else
using IdType = System.Int32;
#endif

namespace RevitMCP.Core
{
    public partial class CommandExecutor
    {
        #region 視圖圖形診斷：其餘所有會改變外觀的層級

        // 視圖參數中與外觀有關的 BuiltInParameter 名稱片段（圖形顯示選項、階段、規程、底圖、日光…）
        private static readonly string[] DiagViewParamHints =
        {
            "GRAPHIC_DISPLAY", "MODEL_GRAPHICS_STYLE", "VIEW_PHASE", "VIEW_DISCIPLINE", "VIEW_DETAIL_LEVEL",
            "VIEW_UNDERLAY", "VIEWER_", "SUN", "PHOTO", "LIGHT", "SHADOW", "BACKGROUND", "SILHOUETTE",
            "SKETCHY", "DEPTH", "TRANSPARENCY", "AMBIENT", "EXPOSURE", "SHOW_HIDDEN", "PARTS_VISIBILITY",
            "VIS_GRAPHICS", "VIEW_SHOW", "ANALYTICAL"
        };

        // ---------- 視圖：圖形顯示選項、暫時模式、工作共用顯示、剖面框、階段 ----------

        private Dictionary<string, object> DescribeDiagViewLayers(Document doc, View view)
        {
            var info = new Dictionary<string, object>();

            // 圖形顯示選項與其他外觀參數：直接讀視圖參數，涵蓋各版本新增的項目
            var displayParams = new Dictionary<string, object>();
            try
            {
                foreach (Parameter p in view.Parameters)
                {
                    if (!(p.Definition is InternalDefinition def)) continue;
                    string bip = def.BuiltInParameter.ToString();
                    if (!DiagViewParamHints.Any(h => bip.IndexOf(h, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                    object v = DiagParamValue(doc, p);
                    if (v == null) continue;
                    displayParams[$"{def.Name} [{bip}]"] = v;
                }
            }
            catch { }
            info["ViewAppearanceParameters"] = displayParams;

            // 圖形顯示選項物件（素描線、深度提示、背景、彩現設定…），以反射讀取以相容各版本
            var displayObjects = new Dictionary<string, object>();
            try
            {
                foreach (var m in view.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (m.GetParameters().Length != 0 || !m.Name.StartsWith("Get")) continue;
                    string rt = m.ReturnType.Name;
                    if (!(rt.StartsWith("ViewDisplay") || rt == "RenderingSettings")) continue;
                    try { displayObjects[m.Name.Substring(3)] = DiagDumpObject(doc, m.Invoke(view, null), 2); }
                    catch (Exception ex) { displayObjects[m.Name.Substring(3)] = "N/A: " + (ex.InnerException?.Message ?? ex.Message); }
                }
                foreach (var propName in new[] { "ShadowIntensity", "SunlightIntensity", "SunAndShadowSettings" })
                {
                    var pi = view.GetType().GetProperty(propName);
                    if (pi == null) continue;
                    try { displayObjects[propName] = DiagDumpObject(doc, pi.GetValue(view), 1); } catch { }
                }
            }
            catch { }
            info["GraphicDisplayOptions"] = displayObjects;

            // 工作共用顯示模式：開著時會用工作集／擁有者／模型更新狀態的顏色蓋掉一切
            try { info["WorksharingDisplayMode"] = view.GetWorksharingDisplayMode().ToString(); } catch { }

            // 暫時隱藏／隔離、顯示隱藏元素、暫時視圖性質
            var tempModes = new List<string>();
            try
            {
                foreach (TemporaryViewMode mode in Enum.GetValues(typeof(TemporaryViewMode)))
                {
                    try { if (view.IsInTemporaryViewMode(mode)) tempModes.Add(mode.ToString()); } catch { }
                }
            }
            catch { }
            info["ActiveTemporaryModes"] = tempModes;
            try
            {
                var m = view.GetType().GetMethod("GetTemporaryViewPropertiesId", Type.EmptyTypes);
                if (m?.Invoke(view, null) is ElementId tvp && tvp != ElementId.InvalidElementId)
                    info["TemporaryViewPropertiesTemplate"] = doc.GetElement(tvp)?.Name ?? tvp.GetIdValue().ToString();
            }
            catch { }

            // 剖面框／裁剪
            if (view is View3D v3)
            {
                try { info["SectionBoxActive"] = v3.IsSectionBoxActive; } catch { }
            }
            try { info["CropBoxActive"] = view.CropBoxActive; } catch { }

            // 階段
            try
            {
                var phaseId = view.get_Parameter(BuiltInParameter.VIEW_PHASE)?.AsElementId();
                var filterId = view.get_Parameter(BuiltInParameter.VIEW_PHASE_FILTER)?.AsElementId();
                info["Phase"] = phaseId != null ? doc.GetElement(phaseId)?.Name : null;
                info["PhaseFilter"] = filterId != null ? doc.GetElement(filterId)?.Name : null;
            }
            catch { }

            // 篩選器套用順序（後面的覆蓋前面的）
            try
            {
                var m = view.GetType().GetMethod("GetOrderedFilters", Type.EmptyTypes);
                if (m?.Invoke(view, null) is IEnumerable ordered)
                    info["FilterOrder"] = ordered.Cast<ElementId>().Select(id => doc.GetElement(id)?.Name ?? id.GetIdValue().ToString()).ToList();
            }
            catch { }

            // 連結模型的視圖顯示設定（依主體／依連結視圖／自訂）
            try
            {
                var m = view.GetType().GetMethod("GetLinkOverrides", new[] { typeof(ElementId) });
                if (m != null)
                {
                    var links = new List<Dictionary<string, object>>();
                    foreach (var li in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Take(30))
                    {
                        try
                        {
                            links.Add(new Dictionary<string, object>
                            {
                                ["LinkInstance"] = li.Name,
                                ["Settings"] = DiagDumpObject(doc, m.Invoke(view, new object[] { li.Id }), 1)
                            });
                        }
                        catch { }
                    }
                    if (links.Count > 0) info["LinkOverrides"] = links;
                }
            }
            catch { }

            return info;
        }

        /// <summary>
        /// 視圖樣板鎖住的「全部」參數名稱（不只四個圖形相關項目）。
        /// </summary>
        private List<string> DescribeDiagAllTemplateControlled(Document doc, View template)
        {
            var list = new List<string>();
            if (template == null) return list;
            try
            {
                var notControlled = new HashSet<IdType>(template.GetNonControlledTemplateParameterIds().Select(id => id.GetIdValue()));
                foreach (var id in template.GetTemplateParameterIds())
                {
                    if (notControlled.Contains(id.GetIdValue())) continue;
                    list.Add(DiagParamIdName(doc, id));
                }
            }
            catch { }
            return list;
        }

        private string DiagParamIdName(Document doc, ElementId id)
        {
            try
            {
                long v = id.GetIdValue();
                if (v < 0) return LabelUtils.GetLabelFor((BuiltInParameter)(int)v);
                return doc.GetElement(id)?.Name ?? v.ToString();
            }
            catch { return id.GetIdValue().ToString(); }
        }

        // ---------- 元素：可見性、工作集、階段狀態、設計選項、族群材料參數、品類／子品類、物件樣式 ----------

        private Dictionary<string, object> DescribeDiagElementLayers(Document doc, View view, Element el)
        {
            var info = new Dictionary<string, object>();

            try { info["HiddenInView"] = el.IsHidden(view); } catch { }

            // 工作集
            try
            {
                if (doc.IsWorkshared && el.WorksetId != WorksetId.InvalidWorksetId)
                {
                    var ws = doc.GetWorksetTable().GetWorkset(el.WorksetId);
                    info["Workset"] = ws?.Name;
                    info["WorksetVisibleInView"] = view.IsWorksetVisible(el.WorksetId);
                    info["WorksetVisibilitySetting"] = view.GetWorksetVisibility(el.WorksetId).ToString();
                }
            }
            catch { }

            // 階段狀態與該階段篩選器對此狀態的表現法
            try
            {
                var phaseId = view.get_Parameter(BuiltInParameter.VIEW_PHASE)?.AsElementId();
                var filterId = view.get_Parameter(BuiltInParameter.VIEW_PHASE_FILTER)?.AsElementId();
                if (phaseId != null && phaseId != ElementId.InvalidElementId)
                {
                    var status = el.GetPhaseStatus(phaseId);
                    info["PhaseStatus"] = status.ToString();
                    if (doc.GetElement(filterId) is PhaseFilter pf)
                        info["PhaseStatusPresentation"] = pf.GetPhaseStatusPresentation(status).ToString();
                }
            }
            catch { }

            // 設計選項
            try { info["DesignOption"] = el.DesignOption?.Name ?? "Main Model"; } catch { }

            // 實例與類型上所有「材料」參數（族群材料參數、結構材料、表面材料…）；空值 = 依品類
            info["MaterialParameters"] = DescribeDiagMaterialParameters(doc, el);

            // 品類：物件樣式（全專案）+ 本視圖的子品類可見性／覆寫
            var cat = el.Category;
            if (cat != null)
            {
                info["ObjectStyle"] = DescribeDiagObjectStyle(cat);
                var subs = new List<Dictionary<string, object>>();
                try
                {
                    foreach (Category sub in cat.SubCategories)
                    {
                        var d = new Dictionary<string, object> { ["SubCategory"] = sub.Name };
                        try { d["HiddenInView"] = view.GetCategoryHidden(sub.Id); } catch { }
                        var ogs = DescribeDiagOverride(doc, SafeGetCategoryOverrides(view, sub.Id));
                        if (ogs != null && ogs["HasAnyOverride"] is bool h && h) d["ViewOverride"] = ogs;
                        var style = DescribeDiagObjectStyle(sub);
                        if (style.TryGetValue("Material", out var sm) && sm != null) d["ObjectStyleMaterial"] = sm;
                        if ((d.TryGetValue("HiddenInView", out var hid) && hid is bool hb && hb) || d.Count > 2)
                            subs.Add(d);
                    }
                }
                catch { }
                info["SubCategoriesWithEffect"] = subs;
            }

            return info;
        }

        private OverrideGraphicSettings SafeGetCategoryOverrides(View view, ElementId catId)
        {
            try { return view.GetCategoryOverrides(catId); } catch { return null; }
        }

        private Dictionary<string, object> DescribeDiagObjectStyle(Category cat)
        {
            var d = new Dictionary<string, object> { ["Category"] = cat.Name };
            try { d["LineColor"] = DiagRgb(cat.LineColor); } catch { }
            try { d["ProjectionLineWeight"] = cat.GetLineWeight(GraphicsStyleType.Projection); } catch { }
            try { d["CutLineWeight"] = cat.GetLineWeight(GraphicsStyleType.Cut); } catch { }
            try { d["Material"] = cat.Material?.Name; } catch { }
            return d;
        }

        private List<Dictionary<string, object>> DescribeDiagMaterialParameters(Document doc, Element el)
        {
            var list = new List<Dictionary<string, object>>();
            var scopes = new List<(string, Element)> { ("Instance", el) };
            try
            {
                var type = doc.GetElement(el.GetTypeId());
                if (type != null) scopes.Add(("Type", type));
            }
            catch { }

            foreach (var (scope, owner) in scopes)
            {
                try
                {
                    foreach (Parameter p in owner.Parameters)
                    {
                        if (p.StorageType != StorageType.ElementId) continue;
                        bool isMaterialSpec = false;
                        try { isMaterialSpec = p.Definition.GetDataType() == SpecTypeId.Reference.Material; } catch { }
                        var id = p.AsElementId();
                        var mat = id != null && id != ElementId.InvalidElementId ? doc.GetElement(id) as Material : null;
                        if (!isMaterialSpec && mat == null) continue;

                        list.Add(new Dictionary<string, object>
                        {
                            ["Scope"] = scope,
                            ["Parameter"] = p.Definition.Name,
                            ["Material"] = mat?.Name ?? "<依品類>",
                            ["MaterialId"] = mat != null ? (object)id.GetIdValue() : null
                        });
                    }
                }
                catch { }
            }
            return list;
        }

        // ---------- 通用：參數值與物件傾印 ----------

        private object DiagParamValue(Document doc, Parameter p)
        {
            try
            {
                if (!p.HasValue) return null;
                switch (p.StorageType)
                {
                    case StorageType.ElementId:
                        var id = p.AsElementId();
                        if (id == null || id == ElementId.InvalidElementId) return null;
                        return doc.GetElement(id)?.Name ?? p.AsValueString() ?? id.GetIdValue().ToString();
                    case StorageType.String:
                        return p.AsString();
                    default:
                        return p.AsValueString() ?? (object)(p.StorageType == StorageType.Integer ? p.AsInteger() : p.AsDouble());
                }
            }
            catch { return null; }
        }

        /// <summary>
        /// 以反射把 Revit 設定物件攤平成可讀字典（只取無參數的公開屬性）。
        /// 用於各版本 API 不一致的圖形顯示選項類別。
        /// </summary>
        private object DiagDumpObject(Document doc, object obj, int depth)
        {
            if (obj == null) return null;
            switch (obj)
            {
                case string s: return s;
                case bool _: case int _: case long _: case short _: return obj;
                case double d: return Math.Round(d, 4);
                case float f: return Math.Round(f, 4);
                case Enum e: return e.ToString();
                case Color c: return DiagRgb(c);
                case ElementId id: return id == ElementId.InvalidElementId ? null : (doc.GetElement(id)?.Name ?? id.GetIdValue().ToString());
                case XYZ xyz: return $"({xyz.X:F2},{xyz.Y:F2},{xyz.Z:F2})";
            }
            if (depth <= 0) return obj.GetType().Name;

            var d2 = new Dictionary<string, object>();
            foreach (var pi in obj.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (pi.GetIndexParameters().Length != 0 || pi.Name == "IsValidObject") continue;
                try { d2[pi.Name] = DiagDumpObject(doc, pi.GetValue(obj), depth - 1); }
                catch { }
            }
            return d2;
        }

        // ---------- 推定：其餘層級的成因 ----------

        private List<Dictionary<string, object>> BuildDiagLayerCauses(Dictionary<string, object> viewLayers, Dictionary<string, object> elemLayers, Element target)
        {
            var causes = new List<Dictionary<string, object>>();

            if (viewLayers != null)
            {
                if (viewLayers.TryGetValue("WorksharingDisplayMode", out var wdm) && wdm is string mode && mode != "Off")
                    causes.Add(MakeDiagCause(0, "工作共用顯示模式", "整個視圖",
                        $"工作共用顯示 = {mode}：所有元素改用工作集／擁有者／更新狀態的顏色，覆蓋材料與一切覆寫。",
                        "視圖控制列的工作共用顯示按鈕 > 關閉。"));

                if (viewLayers.TryGetValue("ActiveTemporaryModes", out var tm) && tm is List<string> modes && modes.Count > 0)
                    causes.Add(MakeDiagCause(0, "暫時視圖模式", "整個視圖",
                        $"目前啟用：{string.Join("、", modes)}"
                        + (viewLayers.TryGetValue("TemporaryViewPropertiesTemplate", out var tvp) ? $"（暫時視圖性質樣板 '{tvp}'）" : "")
                        + "——畫面是暫時狀態，不代表視圖本身設定。",
                        "視圖控制列：重設暫時隱藏／隔離、關閉顯示隱藏元素、還原暫時視圖性質。"));
            }

            if (elemLayers != null && target != null)
            {
                if (elemLayers.TryGetValue("HiddenInView", out var hid) && hid is bool h && h)
                    causes.Add(MakeDiagCause(2, "元素在視圖中隱藏", "此視圖的這個元素",
                        $"元素 {target.Id.GetIdValue()} 被『在視圖中隱藏 > 元素』。", "顯示隱藏元素（燈泡）> 取消隱藏。"));

                if (elemLayers.TryGetValue("WorksetVisibleInView", out var wv) && wv is bool w && !w)
                    causes.Add(MakeDiagCause(2, $"工作集 '{elemLayers["Workset"]}' 不可見", "此視圖該工作集的所有元素",
                        "元素所屬工作集在本視圖被關閉。", "VG > 工作集 頁籤改回顯示。"));

                if (elemLayers.TryGetValue("PhaseStatusPresentation", out var psp) && psp is string pres && pres != "ShowByCategory")
                    causes.Add(MakeDiagCause(2, "階段篩選器", "此視圖該階段狀態的所有元素",
                        $"元素階段狀態 = {elemLayers["PhaseStatus"]}，階段篩選器對此狀態設為 {pres}"
                        + (pres == "ShowOverriden" || pres == "ShowOverridden" ? "：顏色／填充改用『階段 > 圖形覆寫』的設定（API 不提供讀取，需在 Revit 中查看）。" : "。"),
                        "管理 > 階段 > 階段篩選器／圖形覆寫，或改視圖的階段篩選器。"));

                if (elemLayers.TryGetValue("MaterialParameters", out var mp) && mp is List<Dictionary<string, object>> mps
                    && mps.Any(m => (m["Material"] as string) == "<依品類>")
                    && elemLayers.TryGetValue("ObjectStyle", out var os) && os is Dictionary<string, object> style)
                {
                    string catMat = style.TryGetValue("Material", out var cm) ? cm as string : null;
                    var byCat = mps.Where(m => (m["Material"] as string) == "<依品類>").Select(m => $"{m["Scope"]}.{m["Parameter"]}");
                    causes.Add(MakeDiagCause(5, "依品類材料（物件樣式）", "全專案此品類",
                        $"材料參數 {string.Join(", ", byCat)} 為『依品類』，實際使用物件樣式的品類材料 '{catMat ?? "無"}'。",
                        "管理 > 物件樣式 修改品類材料，或在族群／類型中指定材料。"));
                }

                if (elemLayers.TryGetValue("SubCategoriesWithEffect", out var sc) && sc is List<Dictionary<string, object>> subs && subs.Count > 0)
                    causes.Add(MakeDiagCause(4, "子品類", "此視圖該子品類的所有元素",
                        "子品類有隱藏、覆寫或物件樣式材料：" + string.Join("；", subs.Select(s => s["SubCategory"])) + "。",
                        "VG > 模型類別 展開該品類檢查子品類；物件樣式檢查子品類材料。"));
            }

            return causes;
        }

        #endregion
    }
}
