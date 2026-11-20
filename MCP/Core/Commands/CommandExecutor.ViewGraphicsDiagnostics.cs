using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;

#if REVIT2025_OR_GREATER
using IdType = System.Int64;
#else
using IdType = System.Int32;
#endif

namespace RevitMCP.Core
{
    public partial class CommandExecutor
    {
        #region 視圖圖形診斷（唯讀）

        /// <summary>
        /// 唯讀診斷：回答「這張圖現在為什麼長這樣」。
        /// 依 Revit 的圖形覆寫優先序，一次收集所有可能來源：
        ///   視覺型式 > 視圖樣板 > 元素個別覆寫 > 篩選器 > 類別覆寫 > 材料本身
        /// 不開 Transaction、不修改任何東西。
        /// </summary>
        private object GetViewGraphicsDiagnostics(JObject parameters)
        {
            Document doc = _uiApp.ActiveUIDocument.Document;

            // --- 目標視圖 ---
            View view;
            IdType? viewIdParam = parameters["viewId"]?.Value<IdType>();
            if (viewIdParam.HasValue && viewIdParam.Value != 0)
            {
                view = doc.GetElement(viewIdParam.Value.ToElementId()) as View;
                if (view == null)
                    throw new Exception($"找不到視圖 ID {viewIdParam.Value}");
            }
            else
            {
                view = _uiApp.ActiveUIDocument.ActiveView;
            }

            // --- 目標元素（選填）---
            Element target = null;
            IdType? elementIdParam = parameters["elementId"]?.Value<IdType>();
            if (elementIdParam.HasValue && elementIdParam.Value != 0)
            {
                target = doc.GetElement(elementIdParam.Value.ToElementId());
                if (target == null)
                    throw new Exception($"找不到元素 ID {elementIdParam.Value}");
            }

            // --- 多元素比對（選填）：回答「同材料為何長得不一樣」---
            var compareElements = new List<Element>();
            if (parameters["elementIds"] is JArray idArray)
            {
                foreach (var token in idArray)
                {
                    var el = doc.GetElement(token.Value<IdType>().ToElementId());
                    if (el == null) throw new Exception($"找不到元素 ID {token}");
                    compareElements.Add(el);
                }
                if (target == null && compareElements.Count > 0) target = compareElements[0];
            }

            var result = new Dictionary<string, object>();

            result["View"] = DescribeDiagView(doc, view);
            result["ViewLayers"] = DescribeDiagViewLayers(doc, view);
            result["Filters"] = DescribeDiagFilters(doc, view, target);

            if (target != null)
            {
                result["Element"] = new Dictionary<string, object>
                {
                    ["ElementId"] = target.Id.GetIdValue(),
                    ["Name"] = target.Name,
                    ["Category"] = target.Category?.Name ?? "N/A",
                    ["TypeId"] = target.GetTypeId().GetIdValue()
                };
                result["ElementOverride"] = DescribeDiagOverride(doc, SafeGetElementOverrides(view, target.Id));
                result["CategoryOverride"] = DescribeDiagCategoryOverride(doc, view, target.Category);
                result["ElementLayers"] = DescribeDiagElementLayers(doc, view, target);
                result["Materials"] = DescribeDiagMaterials(doc, target);
                result["SubComponents"] = DescribeDiagSubComponents(doc, target);
                result["PaintedFaces"] = DescribeDiagPaintedFaces(doc, target);
                result["ModeResolution"] = BuildDiagModeResolution(DiagAllMaterials(result));
            }

            if (compareElements.Count > 1)
                result["Comparison"] = BuildDiagComparison(doc, view, compareElements);

            // 選填：列出此視圖所有「有覆寫」的模型品類與子品類（V/G Model Categories 每一格）
            if (parameters["listCategoryOverrides"]?.Value<bool>() == true)
                result["CategoryOverrides"] = DescribeDiagAllCategoryOverrides(doc, view);

            // 選填：高程基準（主檔與每個連結的專案基準點／測量點／內部原點）＋ 等高線標籤與高程點
            if (parameters["includeSiteReference"]?.Value<bool>() == true)
                result["SiteReference"] = DescribeDiagSiteReference(doc);

            result["Verdict"] = BuildDiagVerdict(view, target, result);
            return result;
        }

        // ---------- 視圖本身 ----------

        private Dictionary<string, object> DescribeDiagView(Document doc, View view)
        {
            var info = new Dictionary<string, object>
            {
                ["ElementId"] = view.Id.GetIdValue(),
                ["Name"] = view.Name,
                ["ViewType"] = view.ViewType.ToString(),
                ["Scale"] = view.Scale
            };

            try { info["DisplayStyle"] = view.DisplayStyle.ToString(); }
            catch { info["DisplayStyle"] = "N/A"; }

            try { info["DetailLevel"] = view.DetailLevel.ToString(); }
            catch { info["DetailLevel"] = "N/A"; }

            // 視覺型式是否會把所有表面上色（Wireframe / HLR 之外都會）
            try
            {
                var ds = view.DisplayStyle;
                info["DisplayStyleShadesSurfaces"] =
                    ds != DisplayStyle.Wireframe && ds != DisplayStyle.HLR;
            }
            catch { info["DisplayStyleShadesSurfaces"] = false; }

            // 視圖樣板
            try
            {
                if (view.ViewTemplateId != ElementId.InvalidElementId)
                {
                    var template = doc.GetElement(view.ViewTemplateId) as View;
                    info["ViewTemplateId"] = view.ViewTemplateId.GetIdValue();
                    info["ViewTemplateName"] = template?.Name ?? "Unknown";
                    info["ControlledByTemplate"] = DescribeDiagTemplateControl(template);
                    info["AllControlledByTemplate"] = DescribeDiagAllTemplateControlled(doc, template);
                }
                else
                {
                    info["ViewTemplateId"] = 0;
                    info["ViewTemplateName"] = null;
                    info["ControlledByTemplate"] = new List<string>();
                }
            }
            catch
            {
                info["ViewTemplateName"] = "N/A";
                info["ControlledByTemplate"] = new List<string>();
            }

            return info;
        }

        /// <summary>
        /// 列出被視圖樣板鎖住的圖形相關參數（使用者在視圖上改不動的那些）
        /// </summary>
        private List<string> DescribeDiagTemplateControl(View template)
        {
            var controlled = new List<string>();
            if (template == null) return controlled;

            try
            {
                var notControlled = new HashSet<IdType>(
                    template.GetNonControlledTemplateParameterIds().Select(id => id.GetIdValue()));

                var interesting = new Dictionary<BuiltInParameter, string>
                {
                    [BuiltInParameter.MODEL_GRAPHICS_STYLE] = "視覺型式 (Display Style)",
                    [BuiltInParameter.VIS_GRAPHICS_MODEL] = "模型類別 V/G 覆寫",
                    [BuiltInParameter.VIS_GRAPHICS_FILTERS] = "篩選器 (Filters)",
                    [BuiltInParameter.VIEW_DETAIL_LEVEL] = "詳細程度"
                };

                foreach (var kv in interesting)
                {
                    try
                    {
                        var pid = new ElementId(kv.Key).GetIdValue();
                        if (!notControlled.Contains(pid))
                            controlled.Add(kv.Value);
                    }
                    catch { }
                }
            }
            catch { }

            return controlled;
        }

        // ---------- 篩選器 ----------

        private List<Dictionary<string, object>> DescribeDiagFilters(Document doc, View view, Element target)
        {
            var list = new List<Dictionary<string, object>>();
            ICollection<ElementId> filterIds;
            try { filterIds = view.GetFilters(); }
            catch { return list; }

            foreach (var fid in filterIds)
            {
                var entry = new Dictionary<string, object>();
                var filterElem = doc.GetElement(fid);
                entry["FilterId"] = fid.GetIdValue();
                entry["Name"] = filterElem?.Name ?? "Unknown";

                try { entry["Enabled"] = view.GetIsFilterEnabled(fid); }
                catch { entry["Enabled"] = false; }

                try { entry["Visible"] = view.GetFilterVisibility(fid); }
                catch { entry["Visible"] = true; }

                try { entry["Overrides"] = DescribeDiagOverride(doc, view.GetFilterOverrides(fid)); }
                catch { entry["Overrides"] = null; }

                // 這個篩選器有沒有抓到目標元素
                entry["MatchesTargetElement"] = DiagFilterMatches(filterElem as ParameterFilterElement, target);

                list.Add(entry);
            }

            return list;
        }

        private object DiagFilterMatches(ParameterFilterElement filterElem, Element target)
        {
            if (filterElem == null || target == null) return "N/A";
            try
            {
                // 先看類別是否包含，再跑規則
                if (target.Category == null) return false;
                var cats = filterElem.GetCategories();
                if (!cats.Any(c => c.GetIdValue() == target.Category.Id.GetIdValue())) return false;

                var ef = filterElem.GetElementFilter();
                if (ef == null) return true; // 只有類別、沒有規則 = 全中
                return ef.PassesFilter(target);
            }
            catch { return "N/A"; }
        }

        // ---------- 覆寫設定 ----------

        private OverrideGraphicSettings SafeGetElementOverrides(View view, ElementId id)
        {
            try { return view.GetElementOverrides(id); }
            catch { return null; }
        }

        private Dictionary<string, object> DescribeDiagCategoryOverride(Document doc, View view, Category category)
        {
            if (category == null) return null;
            OverrideGraphicSettings ogs = null;
            try { ogs = view.GetCategoryOverrides(category.Id); }
            catch { }

            var d = DescribeDiagOverride(doc, ogs) ?? new Dictionary<string, object>();
            d["CategoryName"] = category.Name;
            try { d["CategoryHidden"] = view.GetCategoryHidden(category.Id); }
            catch { d["CategoryHidden"] = false; }
            return d;
        }

        /// <summary>
        /// 高程基準診斷：回答「等高線／高程標籤為什麼多出奇怪的小數」。
        /// 逐一列出主檔與各連結文件的基準點（內部座標 mm），以及等高線標籤、高程點等標註的元素與類型參數。
        /// </summary>
        private Dictionary<string, object> DescribeDiagSiteReference(Document doc)
        {
            var result = new Dictionary<string, object>();
            var docs = new List<KeyValuePair<string, Document>> { new KeyValuePair<string, Document>("Host: " + doc.Title, doc) };
            var linkTransforms = new Dictionary<string, object>();
            foreach (var li in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
            {
                var ld = li.GetLinkDocument();
                if (ld == null) continue;
                docs.Add(new KeyValuePair<string, Document>("Link: " + ld.Title, ld));
                try
                {
                    var o = li.GetTotalTransform().Origin;
                    linkTransforms[ld.Title] = new { OriginMm = new { X = Math.Round(o.X * 304.8, 1), Y = Math.Round(o.Y * 304.8, 1), Z = Math.Round(o.Z * 304.8, 1) } };
                }
                catch { }
            }
            result["LinkTransforms"] = linkTransforms;

            var perDoc = new List<object>();
            foreach (var kv in docs)
            {
                var d = kv.Value;
                var entry = new Dictionary<string, object> { ["Document"] = kv.Key };

                var points = new List<object>();
                foreach (var bp in new FilteredElementCollector(d).OfClass(typeof(BasePoint)).Cast<BasePoint>())
                {
                    try
                    {
                        var p = bp.Position;
                        var sp = bp.SharedPosition;
                        points.Add(new
                        {
                            Kind = bp.IsShared ? "SurveyPoint" : "ProjectBasePoint",
                            PositionMm = new { X = Math.Round(p.X * 304.8, 1), Y = Math.Round(p.Y * 304.8, 1), Z = Math.Round(p.Z * 304.8, 1) },
                            SharedPositionMm = new { X = Math.Round(sp.X * 304.8, 1), Y = Math.Round(sp.Y * 304.8, 1), Z = Math.Round(sp.Z * 304.8, 1) }
                        });
                    }
                    catch { }
                }
                entry["BasePoints"] = points;

                var levels = new List<object>();
                foreach (var lv in new FilteredElementCollector(d).OfClass(typeof(Level)).Cast<Level>())
                {
                    try { levels.Add(new { lv.Name, InternalElevationMm = Math.Round(lv.Elevation * 304.8, 1), ProjectElevationMm = Math.Round(lv.ProjectElevation * 304.8, 1) }); }
                    catch { }
                }
                entry["Levels"] = levels;

                // 等高線標籤、高程點、地形相關標註：以品類名稱比對，避免依賴特定 BuiltInCategory
                var annots = new List<object>();
                var typeSeen = new HashSet<IdType>();
                var typeParams = new List<object>();
                foreach (var el in new FilteredElementCollector(d).WhereElementIsNotElementType())
                {
                    string cat = el.Category?.Name ?? "";
                    if (cat.IndexOf("Contour", StringComparison.OrdinalIgnoreCase) < 0 &&
                        cat.IndexOf("Spot Elevation", StringComparison.OrdinalIgnoreCase) < 0 &&
                        cat.IndexOf("等高線", StringComparison.Ordinal) < 0 &&
                        cat.IndexOf("高程點", StringComparison.Ordinal) < 0)
                        continue;
                    if (annots.Count >= 50) break;

                    annots.Add(new
                    {
                        ElementId = el.Id.GetIdValue(),
                        Category = cat,
                        el.Name,
                        OwnerView = el.OwnerViewId != ElementId.InvalidElementId ? d.GetElement(el.OwnerViewId)?.Name : null,
                        Parameters = DiagDumpParameters(el)
                    });

                    var tid = el.GetTypeId();
                    if (tid != ElementId.InvalidElementId && typeSeen.Add(tid.GetIdValue()))
                    {
                        var t = d.GetElement(tid);
                        if (t != null)
                            typeParams.Add(new { TypeId = tid.GetIdValue(), TypeName = t.Name, Category = cat, Parameters = DiagDumpParameters(t) });
                    }
                }
                entry["ContourAndSpotAnnotations"] = annots;
                entry["AnnotationTypes"] = typeParams;

                // 地形本身的基準：每個 Toposolid / Topography 的高程相關參數
                var topos = new List<object>();
                foreach (var el in new FilteredElementCollector(d).WhereElementIsNotElementType())
                {
                    string cat = el.Category?.Name ?? "";
                    if (cat != "Toposolid" && cat != "Topography") continue;
                    if (topos.Count >= 40) break;
                    topos.Add(new { ElementId = el.Id.GetIdValue(), Category = cat, el.Name, Parameters = DiagDumpParameters(el) });
                }
                entry["Terrain"] = topos;

                perDoc.Add(entry);
            }
            result["Documents"] = perDoc;
            result["Note"] = "PositionMm 為該文件內部座標（mm）；標籤顯示值 = 內部高程 − 所選基準點高程。若等高線間距為整數公尺而標籤多出固定小數，比對各基準點 Z 的尾數即可對上。";
            return result;
        }

        /// <summary>
        /// 把元素參數攤平成 名稱→顯示值（AsValueString 優先）
        /// </summary>
        private Dictionary<string, object> DiagDumpParameters(Element el)
        {
            var d = new Dictionary<string, object>();
            foreach (Parameter p in el.Parameters)
            {
                try
                {
                    string name = p.Definition?.Name;
                    if (string.IsNullOrEmpty(name) || d.ContainsKey(name)) continue;
                    object v;
                    switch (p.StorageType)
                    {
                        case StorageType.String: v = p.AsString(); break;
                        case StorageType.Integer: v = p.AsValueString() ?? p.AsInteger().ToString(); break;
                        case StorageType.Double: v = p.AsValueString() ?? p.AsDouble().ToString(); break;
                        case StorageType.ElementId:
                            var eid = p.AsElementId();
                            v = p.AsValueString() ?? (eid == ElementId.InvalidElementId ? null : el.Document.GetElement(eid)?.Name);
                            break;
                        default: v = null; break;
                    }
                    d[name] = v;
                }
                catch { }
            }
            return d;
        }

        /// <summary>
        /// 掃過全部模型品類與子品類，只回傳在此視圖有 V/G 覆寫的那些（可對視圖樣板使用）
        /// </summary>
        private List<Dictionary<string, object>> DescribeDiagAllCategoryOverrides(Document doc, View view)
        {
            var list = new List<Dictionary<string, object>>();
            foreach (Category cat in doc.Settings.Categories)
            {
                if (cat.CategoryType != CategoryType.Model) continue;

                var cats = new List<Category> { cat };
                try { cats.AddRange(cat.SubCategories.Cast<Category>()); } catch { }

                foreach (var c in cats)
                {
                    var d = DescribeDiagCategoryOverride(doc, view, c);
                    if (d == null || !(d["HasAnyOverride"] is bool has) || !has) continue;
                    if (c.Id != cat.Id) d["CategoryName"] = cat.Name + " › " + c.Name;
                    list.Add(d);
                }
            }
            return list;
        }

        /// <summary>
        /// 把 OverrideGraphicSettings 攤平成可讀欄位，並判斷到底有沒有覆寫
        /// </summary>
        private Dictionary<string, object> DescribeDiagOverride(Document doc, OverrideGraphicSettings ogs)
        {
            if (ogs == null) return null;

            var d = new Dictionary<string, object>();
            bool hasAny = false;

            Action<string, Color> addColor = (key, color) =>
            {
                if (color != null && color.IsValid)
                {
                    d[key] = $"RGB({color.Red},{color.Green},{color.Blue})";
                    hasAny = true;
                }
                else
                {
                    d[key] = null;
                }
            };

            Action<string, ElementId> addPattern = (key, id) =>
            {
                if (id != null && id != ElementId.InvalidElementId)
                {
                    var fp = doc.GetElement(id) as FillPatternElement;
                    string name = fp?.Name ?? id.GetIdValue().ToString();
                    bool isSolid = false;
                    try { isSolid = fp?.GetFillPattern()?.IsSolidFill ?? false; } catch { }
                    d[key] = name + (isSolid ? " (實心填滿)" : "");
                    hasAny = true;
                }
                else
                {
                    d[key] = null;
                }
            };

            try { addColor("SurfaceForegroundPatternColor", ogs.SurfaceForegroundPatternColor); } catch { }
            try { addPattern("SurfaceForegroundPattern", ogs.SurfaceForegroundPatternId); } catch { }
            try { addColor("SurfaceBackgroundPatternColor", ogs.SurfaceBackgroundPatternColor); } catch { }
            try { addPattern("SurfaceBackgroundPattern", ogs.SurfaceBackgroundPatternId); } catch { }
            try { addColor("CutForegroundPatternColor", ogs.CutForegroundPatternColor); } catch { }
            try { addPattern("CutForegroundPattern", ogs.CutForegroundPatternId); } catch { }
            try { addColor("CutBackgroundPatternColor", ogs.CutBackgroundPatternColor); } catch { }
            try { addPattern("CutBackgroundPattern", ogs.CutBackgroundPatternId); } catch { }
            try { addColor("ProjectionLineColor", ogs.ProjectionLineColor); } catch { }
            try { addColor("CutLineColor", ogs.CutLineColor); } catch { }

            try
            {
                d["Halftone"] = ogs.Halftone;
                if (ogs.Halftone) hasAny = true;
            }
            catch { }

            try
            {
                int t = ogs.Transparency;
                d["SurfaceTransparency"] = t;
                if (t != 0) hasAny = true;
            }
            catch { }

            try
            {
                int w = ogs.ProjectionLineWeight;
                d["ProjectionLineWeight"] = w == OverrideGraphicSettings.InvalidPenNumber ? (object)null : w;
                if (w != OverrideGraphicSettings.InvalidPenNumber) hasAny = true;
            }
            catch { }

            try
            {
                int w = ogs.CutLineWeight;
                d["CutLineWeight"] = w == OverrideGraphicSettings.InvalidPenNumber ? (object)null : w;
                if (w != OverrideGraphicSettings.InvalidPenNumber) hasAny = true;
            }
            catch { }

            Action<string, ElementId> addLinePattern = (key, id) =>
            {
                if (id != null && id != ElementId.InvalidElementId)
                {
                    d[key] = doc.GetElement(id)?.Name ?? id.GetIdValue().ToString();
                    hasAny = true;
                }
                else
                {
                    d[key] = null;
                }
            };
            try { addLinePattern("ProjectionLinePattern", ogs.ProjectionLinePatternId); } catch { }
            try { addLinePattern("CutLinePattern", ogs.CutLinePatternId); } catch { }

            try { d["SurfaceForegroundPatternVisible"] = ogs.IsSurfaceForegroundPatternVisible; if (!ogs.IsSurfaceForegroundPatternVisible) hasAny = true; } catch { }
            try { d["SurfaceBackgroundPatternVisible"] = ogs.IsSurfaceBackgroundPatternVisible; if (!ogs.IsSurfaceBackgroundPatternVisible) hasAny = true; } catch { }
            try { d["CutForegroundPatternVisible"] = ogs.IsCutForegroundPatternVisible; if (!ogs.IsCutForegroundPatternVisible) hasAny = true; } catch { }
            try { d["CutBackgroundPatternVisible"] = ogs.IsCutBackgroundPatternVisible; if (!ogs.IsCutBackgroundPatternVisible) hasAny = true; } catch { }

            try
            {
                var dl = ogs.DetailLevel;
                d["DetailLevel"] = dl == ViewDetailLevel.Undefined ? null : dl.ToString();
                if (dl != ViewDetailLevel.Undefined) hasAny = true;
            }
            catch { }

            d["HasAnyOverride"] = hasAny;
            return d;
        }

        // ---------- 材料 ----------

        private List<Dictionary<string, object>> DescribeDiagMaterials(Document doc, Element target)
        {
            var list = new List<Dictionary<string, object>>();
            var seen = new HashSet<IdType>();
            var ids = new List<ElementId>();

            // 複合結構（牆／樓板／屋頂／天花）逐層取材料，順序即層序
            try
            {
                var hostAttr = doc.GetElement(target.GetTypeId()) as HostObjAttributes;
                var cs = hostAttr?.GetCompoundStructure();
                if (cs != null)
                {
                    foreach (var layer in cs.GetLayers())
                        ids.Add(layer.MaterialId);
                }
            }
            catch { }

            // 其餘元素走通用取法
            if (ids.Count == 0)
            {
                try { ids.AddRange(target.GetMaterialIds(false)); }
                catch { }
            }

            int index = 0;
            foreach (var mid in ids)
            {
                index++;
                if (mid == null || mid == ElementId.InvalidElementId) continue;
                if (!seen.Add(mid.GetIdValue())) continue;

                var mat = doc.GetElement(mid) as Material;
                if (mat == null) continue;

                var m = new Dictionary<string, object>
                {
                    ["LayerIndex"] = index,
                    ["MaterialId"] = mid.GetIdValue(),
                    ["MaterialName"] = mat.Name
                };

                try
                {
                    var c = mat.Color;
                    m["ShadingColor"] = (c != null && c.IsValid) ? $"RGB({c.Red},{c.Green},{c.Blue})" : null;
                }
                catch { m["ShadingColor"] = null; }

                try { m["UseRenderAppearanceForShading"] = mat.UseRenderAppearanceForShading; }
                catch { }
                try { m["Transparency"] = mat.Transparency; } catch { }
                try { m["Smoothness"] = mat.Smoothness; } catch { }
                try { m["Shininess"] = mat.Shininess; } catch { }

                AddDiagMaterialPattern(doc, m, "SurfaceForeground", mat.SurfaceForegroundPatternId, mat.SurfaceForegroundPatternColor);
                AddDiagMaterialPattern(doc, m, "SurfaceBackground", mat.SurfaceBackgroundPatternId, mat.SurfaceBackgroundPatternColor);
                AddDiagMaterialPattern(doc, m, "CutForeground", mat.CutForegroundPatternId, mat.CutForegroundPatternColor);
                AddDiagMaterialPattern(doc, m, "CutBackground", mat.CutBackgroundPatternId, mat.CutBackgroundPatternColor);

                m["Appearance"] = DescribeDiagAppearance(doc, mat);

                list.Add(m);
            }

            return list;
        }

        private void AddDiagMaterialPattern(Document doc, Dictionary<string, object> bag, string prefix, ElementId patternId, Color color)
        {
            try
            {
                if (patternId != null && patternId != ElementId.InvalidElementId)
                {
                    var fp = doc.GetElement(patternId) as FillPatternElement;
                    bag[prefix + "Pattern"] = fp?.Name ?? patternId.GetIdValue().ToString();
                    bool isSolid = false;
                    try { isSolid = fp?.GetFillPattern()?.IsSolidFill ?? false; } catch { }
                    bag[prefix + "PatternIsSolid"] = isSolid;
                }
                else
                {
                    bag[prefix + "Pattern"] = null;
                    bag[prefix + "PatternIsSolid"] = false;
                }
            }
            catch { }

            try
            {
                bag[prefix + "PatternColor"] =
                    (color != null && color.IsValid) ? $"RGB({color.Red},{color.Green},{color.Blue})" : null;
            }
            catch { }
        }

        // ---------- 推定主因 ----------

        /// <summary>
        /// 依 Revit 覆寫優先序列出「所有」命中的成因，而不是只回第一個。
        /// 一張圖可以同時被視覺型式、篩選器與材料影響；只回一個會讓後續判讀被誤導。
        /// 啟發式判斷，每筆都標明是推定。
        /// </summary>
        private Dictionary<string, object> BuildDiagVerdict(View view, Element target, Dictionary<string, object> result)
        {
            var causes = new List<Dictionary<string, object>>();
            var viewInfo = result["View"] as Dictionary<string, object>;

            // --- 優先序 1：視覺型式（影響整張圖的所有表面）---
            if (viewInfo != null
                && viewInfo.TryGetValue("DisplayStyleShadesSurfaces", out var shadesObj)
                && shadesObj is bool shades && shades)
            {
                string tmpl = viewInfo.TryGetValue("ViewTemplateName", out var tmplObj) ? tmplObj as string : null;
                string via = string.IsNullOrEmpty(tmpl) ? "" : $"，且由視圖樣板 '{tmpl}' 控制（在視圖上改不動）";
                string styleName = viewInfo["DisplayStyle"] as string;
                string colorSource = DiagIsAppearanceStyle(styleName)
                    ? "此模式畫的是材料『外觀』頁籤（彩現資產：顏色、貼圖、色彩、風化），不是圖形頁籤的著色顏色；見各材料 Appearance 與 ModeResolution。"
                    : styleName == "FlatColors"
                        ? "此模式用材料著色顏色平塗，不受光源影響。"
                        : "此模式用材料著色顏色（或勾選『使用彩現外觀』時用外觀資產）再乘上光源明暗。";
                causes.Add(MakeDiagCause(
                    1,
                    "視覺型式 (Display Style)",
                    "整個視圖",
                    $"視覺型式 = {styleName}{via}。{colorSource}",
                    string.IsNullOrEmpty(tmpl)
                        ? "把視覺型式改回 Hidden Line。"
                        : $"先解除或修改視圖樣板 '{tmpl}'，再改視覺型式。"));
            }

            // 工作共用顯示、暫時模式等視圖層級成因（優先序 0，蓋過一切）
            causes.AddRange(BuildDiagLayerCauses(
                result.TryGetValue("ViewLayers", out var vl) ? vl as Dictionary<string, object> : null, null, null));

            if (target == null)
            {
                causes = causes.OrderBy(c => (int)c["Priority"]).ToList();
                return new Dictionary<string, object>
                {
                    ["PrimaryCause"] = causes.Count > 0 ? causes[0]["Source"] : null,
                    ["CauseCount"] = causes.Count,
                    ["Causes"] = causes,
                    ["Summary"] = causes.Count > 0
                        ? $"未指定 elementId，僅回報視圖層級成因（{causes.Count} 項）。要追某個元素的表現法來源請帶 elementId。"
                        : "未指定 elementId，視圖層級沒有發現全域著色設定。要追某個元素的表現法來源請帶 elementId。"
                };
            }

            IdType targetId = target.Id.GetIdValue();

            // --- 優先序 2：元素個別覆寫 ---
            var elemEffects = DescribeDiagEffects(result["ElementOverride"] as Dictionary<string, object>);
            if (elemEffects.Count > 0)
            {
                causes.Add(MakeDiagCause(
                    2,
                    "元素個別覆寫",
                    "此視圖的這個元素",
                    $"元素 {targetId} 在本視圖被直接覆寫（在視圖中覆寫圖形 > 依元素）：{string.Join("；", elemEffects)}。",
                    "用 clear_element_override 還原，或在 Revit 中右鍵 > 在視圖中覆寫圖形 > 依元素 > 重設。"));
            }

            // --- 優先序 3：篩選器（可能有多個同時命中，全部列出）---
            var filters = result["Filters"] as List<Dictionary<string, object>>;
            if (filters != null)
            {
                foreach (var f in filters)
                {
                    bool enabled = f.TryGetValue("Enabled", out var enabledObj) && enabledObj is bool e && e;
                    bool matches = f.TryGetValue("MatchesTargetElement", out var matchObj) && matchObj is bool m && m;
                    if (!enabled || !matches) continue;

                    bool visible = !f.TryGetValue("Visible", out var visObj) || !(visObj is bool v) || v;
                    if (!visible)
                    {
                        causes.Add(MakeDiagCause(
                            3,
                            $"篩選器 '{f["Name"]}'（隱藏）",
                            "此視圖的這個元素",
                            $"篩選器 '{f["Name"]}' 抓到這個元素並把它設為不可見——元素存在但畫不出來。",
                            "到 VG > 篩選器 頁籤，把該列的『可見性』勾回來。"));
                        continue;
                    }

                    var filterEffects = DescribeDiagEffects(f["Overrides"] as Dictionary<string, object>);
                    if (filterEffects.Count > 0)
                    {
                        causes.Add(MakeDiagCause(
                            3,
                            $"篩選器 '{f["Name"]}'",
                            "此視圖的這個元素",
                            $"篩選器 '{f["Name"]}' 抓到這個元素並套了：{string.Join("；", filterEffects)}。",
                            "到 VG > 篩選器 頁籤關掉該覆寫，或修改篩選器規則讓它不要抓到這個元素。"));
                    }
                }
            }

            // --- 優先序 4：類別覆寫 ---
            var catOgs = result["CategoryOverride"] as Dictionary<string, object>;
            if (catOgs != null)
            {
                if (catOgs.TryGetValue("CategoryHidden", out var hiddenObj) && hiddenObj is bool hidden && hidden)
                {
                    causes.Add(MakeDiagCause(
                        4,
                        "類別被關閉",
                        "此視圖的整個品類",
                        $"品類 '{catOgs["CategoryName"]}' 在本視圖被關閉可見性——這個元素根本不會顯示。",
                        "到 VG > 模型類別 頁籤把該品類勾回來。"));
                }

                var catEffects = DescribeDiagEffects(catOgs);
                if (catEffects.Count > 0)
                {
                    causes.Add(MakeDiagCause(
                        4,
                        "類別覆寫",
                        "此視圖的整個品類",
                        $"品類 '{catOgs["CategoryName"]}' 在本視圖被套了：{string.Join("；", catEffects)}——同品類的元素都會受影響。",
                        "到 VG > 模型類別 頁籤清掉該品類的覆寫。"));
                }
            }

            // --- 優先序 5：材料本身（影響全專案）---
            var mats = result["Materials"] as List<Dictionary<string, object>>;
            if (mats != null)
            {
                foreach (var mat in mats)
                {
                    var matEffects = DescribeDiagMaterialEffects(mat);
                    if (matEffects.Count == 0) continue;

                    causes.Add(MakeDiagCause(
                        5,
                        $"材料 '{mat["MaterialName"]}'",
                        "全專案所有視圖",
                        $"材料 '{mat["MaterialName"]}'（第 {mat["LayerIndex"]} 層）本身的設定：{string.Join("；", matEffects)}。",
                        "改材料的圖形設定會影響全專案；若只想改這張圖，改用視圖覆寫或篩選器。"));
                }
            }

            // --- 元素其餘層級：隱藏、工作集、階段、依品類材料、子品類 ---
            causes.AddRange(BuildDiagLayerCauses(null,
                result.TryGetValue("ElementLayers", out var el) ? el as Dictionary<string, object> : null, target));

            // --- 油漆：面上塗了別的材料（含樓梯子構件）---
            var painted = DiagAllPaintedFaces(result);
            if (painted.Count > 0)
            {
                causes.Add(MakeDiagCause(
                    2,
                    "油漆 (Paint)",
                    "這個元素的特定面，全部視圖",
                    $"有 {painted.Count} 個面被油漆工具塗了材料："
                    + string.Join("、", painted.Select(p => p.TryGetValue("PaintedMaterialName", out var n) ? n : null).Where(n => n != null).Distinct())
                    + "——這些面不使用類型的材料。",
                    "修改 > 移除油漆，點選該面。"));
            }

            // --- 外觀資產：擬真類視覺型式下的實際取色來源 ---
            causes.AddRange(BuildDiagAppearanceCauses(viewInfo?["DisplayStyle"] as string, DiagAllMaterials(result)));

            causes = causes.OrderBy(c => (int)c["Priority"]).ToList();

            string summary;
            if (causes.Count == 0)
            {
                summary = "在視覺型式、元素覆寫、篩選器、類別覆寫、材料五個來源都沒找到會改變表現法的設定。"
                        + "請再檢查連結模型覆寫、設計選項、階段化圖形覆寫，或該位置是否疊了填滿區域／詳圖項目。";
            }
            else
            {
                var lines = causes.Select((c, i) => $"{i + 1}. [{c["Source"]}／{c["Scope"]}] {c["Detail"]}");
                summary = $"推定：找到 {causes.Count} 個會影響表現法的成因，依 Revit 覆寫優先序排列（越前面越優先勝出）：\n"
                        + string.Join("\n", lines);
            }

            return new Dictionary<string, object>
            {
                ["PrimaryCause"] = causes.Count > 0 ? causes[0]["Source"] : null,
                ["CauseCount"] = causes.Count,
                ["Causes"] = causes,
                ["Summary"] = summary
            };
        }

        /// <summary>
        /// 元素本身 + 子構件（樓梯梯段／平台）的所有材料，依名稱去重。
        /// </summary>
        private List<Dictionary<string, object>> DiagAllMaterials(Dictionary<string, object> result)
        {
            var all = new List<Dictionary<string, object>>();
            if (result.TryGetValue("Materials", out var m) && m is List<Dictionary<string, object>> mats) all.AddRange(mats);
            if (result.TryGetValue("SubComponents", out var s) && s is List<Dictionary<string, object>> subs)
                foreach (var sub in subs)
                    if (sub["Materials"] is List<Dictionary<string, object>> sm) all.AddRange(sm);
            return all.GroupBy(x => x["MaterialId"]).Select(g => g.First()).ToList();
        }

        private List<Dictionary<string, object>> DiagAllPaintedFaces(Dictionary<string, object> result)
        {
            var all = new List<Dictionary<string, object>>();
            if (result.TryGetValue("PaintedFaces", out var p) && p is List<Dictionary<string, object>> pf) all.AddRange(pf);
            if (result.TryGetValue("SubComponents", out var s) && s is List<Dictionary<string, object>> subs)
                foreach (var sub in subs)
                    if (sub["PaintedFaces"] is List<Dictionary<string, object>> sp) all.AddRange(sp);
            return all;
        }

        private Dictionary<string, object> MakeDiagCause(int priority, string source, string scope, string detail, string fix)
        {
            return new Dictionary<string, object>
            {
                ["Priority"] = priority,
                ["Source"] = source,
                ["Scope"] = scope,
                ["Detail"] = detail,
                ["Fix"] = fix
            };
        }

        /// <summary>
        /// 把一組已攤平的覆寫設定翻成「使用者看得到的效果」清單。
        /// 涵蓋表面／剖面填滿、線色、線寬、半色調、透明度——不是只看表面顏色。
        /// </summary>
        private List<string> DescribeDiagEffects(Dictionary<string, object> ogs)
        {
            var effects = new List<string>();
            if (ogs == null) return effects;

            Action<string, string> add = (key, label) =>
            {
                if (ogs.TryGetValue(key, out var value) && value != null)
                    effects.Add($"{label} = {value}");
            };

            add("SurfaceForegroundPatternColor", "表面前景填滿顏色");
            add("SurfaceForegroundPattern", "表面前景填充樣式");
            add("SurfaceBackgroundPatternColor", "表面背景填滿顏色");
            add("SurfaceBackgroundPattern", "表面背景填充樣式");
            add("CutForegroundPatternColor", "剖面前景填滿顏色");
            add("CutForegroundPattern", "剖面前景填充樣式");
            add("CutBackgroundPatternColor", "剖面背景填滿顏色");
            add("CutBackgroundPattern", "剖面背景填充樣式");
            add("ProjectionLineColor", "投影線顏色");
            add("CutLineColor", "剖面線顏色");
            add("ProjectionLineWeight", "投影線寬");

            if (ogs.TryGetValue("Halftone", out var halftoneObj) && halftoneObj is bool halftone && halftone)
                effects.Add("半色調 (Halftone)");

            if (ogs.TryGetValue("SurfaceTransparency", out var transObj) && transObj is int transparency && transparency != 0)
                effects.Add($"表面透明度 = {transparency}%");

            return effects;
        }

        /// <summary>
        /// 材料本身的圖形設定裡，哪些會實際改變畫面。
        /// </summary>
        private List<string> DescribeDiagMaterialEffects(Dictionary<string, object> mat)
        {
            var effects = new List<string>();
            if (mat == null) return effects;

            Action<string, string> addPattern = (prefix, label) =>
            {
                bool solid = mat.TryGetValue(prefix + "PatternIsSolid", out var solidObj) && solidObj is bool isSolid && isSolid;
                object color = mat.TryGetValue(prefix + "PatternColor", out var colorObj) ? colorObj : null;
                object pattern = mat.TryGetValue(prefix + "Pattern", out var patternObj) ? patternObj : null;

                if (solid && color != null)
                    effects.Add($"{label}為實心填滿 + {color}");
                else if (pattern != null && color != null)
                    effects.Add($"{label} = {pattern} + {color}");
            };

            addPattern("SurfaceForeground", "表面前景填充樣式");
            addPattern("SurfaceBackground", "表面背景填充樣式");
            addPattern("CutForeground", "剖面前景填充樣式");
            addPattern("CutBackground", "剖面背景填充樣式");

            return effects;
        }

        #endregion
    }
}
