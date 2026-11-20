using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;

// Revit 2025+ ElementId: int → long
#if REVIT2025_OR_GREATER
using IdType = System.Int64;
#else
using IdType = System.Int32;
#endif

namespace RevitMCP.Core
{
    /// <summary>
    /// 連結模型在單一視圖中的顯示設定（V/G → Revit Links 頁籤的 Display Settings）。
    ///
    /// 存在理由：主檔的視圖篩選器與類別覆寫，只有在連結的 Display Settings 是
    /// "By Host View" 時才會套用到連結元素。設成 "By Linked View" 時，主檔怎麼設都沒反應——
    /// 這是連結模型表現法問題裡最常見、也最難從畫面上看出來的成因。
    /// </summary>
    public partial class CommandExecutor
    {
        // View.GetLinkOverrides / RevitLinkGraphicsSettings / LinkVisibility 是 Revit 2024 API。
        // R22／R23 建置時兩支工具直接回報「不支援」，避免整個 add-in 因此編譯失敗；R24+ 行為不變。
#if !REVIT2024_OR_GREATER
        private const string LinkDisplayUnsupported =
            "get_link_display_settings / set_link_display_settings 需要 Revit 2024 以上（View.GetLinkOverrides 為 2024 API），此版本不支援。";

        private object GetLinkDisplaySettings(JObject parameters)
        {
            throw new NotSupportedException(LinkDisplayUnsupported);
        }

        private object SetLinkDisplaySettings(JObject parameters)
        {
            throw new NotSupportedException(LinkDisplayUnsupported);
        }
#else
        private object GetLinkDisplaySettings(JObject parameters)
        {
            Document doc = _uiApp.ActiveUIDocument.Document;
            View view = ResolveViewForLinks(doc, parameters);

            var links = new FilteredElementCollector(doc)
                .OfClass(typeof(RevitLinkInstance))
                .Cast<RevitLinkInstance>()
                .ToList();

            var rows = new List<object>();
            foreach (var link in links)
            {
                string mode;
                string linkedViewName = null;

                try
                {
                    RevitLinkGraphicsSettings settings = view.GetLinkOverrides(link.Id);
                    if (settings == null)
                    {
                        // 沒有覆寫 = 沿用預設，等同 By Host View
                        mode = "ByHostView";
                    }
                    else
                    {
                        mode = settings.LinkVisibilityType.ToString();
                        if (settings.LinkVisibilityType == LinkVisibility.ByLinkView &&
                            settings.LinkedViewId != null &&
                            settings.LinkedViewId != ElementId.InvalidElementId)
                        {
                            var linkDoc = link.GetLinkDocument();
                            var linkedView = linkDoc != null
                                ? linkDoc.GetElement(settings.LinkedViewId) as View
                                : null;
                            linkedViewName = linkedView != null ? linkedView.Name : "(取不到連結視圖名稱)";
                        }
                    }
                }
                catch (Exception ex)
                {
                    mode = $"(讀取失敗: {ex.Message})";
                }

                var type = doc.GetElement(link.GetTypeId()) as RevitLinkType;

                rows.Add(new
                {
                    LinkInstanceId = link.Id.GetIdValue(),
                    LinkTypeName = type != null ? type.Name : link.Name,
                    DisplaySetting = mode,
                    LinkedViewName = linkedViewName,
                    HiddenInView = link.IsHidden(view),
                    HostFiltersApply = mode == "ByHostView",
                    // VV → Revit Links 每列的完整內容：實例列與類型列（上層列）各自讀
                    InstanceRow = DescribeLinkRow(doc, view, link.Id),
                    TypeRow = type != null ? DescribeLinkRow(doc, view, type.Id) : null,
                    // 連結檔裡可當 Linked view 的平面視圖（id → 名稱），供 set_link_display_settings 的 linkedViewId 使用
                    LinkPlanViews = ListLinkPlanViews(link),
                    // 選填：此主檔視圖中，該連結內指定品類「實際可見」與「不可見」的元素
                    CategoryVisibility = DescribeLinkedCategoryVisibility(doc, view, link, parameters["visibleCategory"]?.Value<string>())
                });
            }

            return new
            {
                Success = true,
                ViewId = view.Id.GetIdValue(),
                ViewName = view.Name,
                LinkCount = rows.Count,
                Note = "HostFiltersApply=false 時，主檔的視圖篩選器與類別覆寫不會作用在該連結的元素上。",
                Links = rows
            };
        }

        /// <summary>
        /// 用 FilteredElementCollector(host, viewId, linkId) 取得連結元素在主檔視圖中的實際可見清單，
        /// 與連結檔中同品類的全部元素比對，列出「在連結裡存在、但此視圖看不到」的元素。
        /// </summary>
        private object DescribeLinkedCategoryVisibility(Document doc, View view, RevitLinkInstance link, string categoryName)
        {
            if (string.IsNullOrWhiteSpace(categoryName)) return null;
            var ld = link.GetLinkDocument();
            if (ld == null) return "(連結未載入)";

            var all = new FilteredElementCollector(ld).WhereElementIsNotElementType()
                .Where(e => e.Category != null && string.Equals(e.Category.Name, categoryName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            HashSet<IdType> visible;
            try
            {
                visible = new HashSet<IdType>(new FilteredElementCollector(doc, view.Id, link.Id)
                    .WhereElementIsNotElementType()
                    .Where(e => e.Category != null && string.Equals(e.Category.Name, categoryName, StringComparison.OrdinalIgnoreCase))
                    .Select(e => e.Id.GetIdValue()));
            }
            catch (Exception ex)
            {
                return $"(讀取失敗: {ex.Message})";
            }

            Func<Element, object> row = e => new
            {
                ElementId = e.Id.GetIdValue(),
                e.Name,
                TypeName = ld.GetElement(e.GetTypeId())?.Name
            };

            return new
            {
                Category = categoryName,
                TotalInLink = all.Count,
                VisibleCount = all.Count(e => visible.Contains(e.Id.GetIdValue())),
                Visible = all.Where(e => visible.Contains(e.Id.GetIdValue())).Select(row).ToList(),
                NotVisible = all.Where(e => !visible.Contains(e.Id.GetIdValue())).Select(row).ToList()
            };
        }

        private List<object> ListLinkPlanViews(RevitLinkInstance link)
        {
            var list = new List<object>();
            var ld = link.GetLinkDocument();
            if (ld == null) return list;
            foreach (var v in new FilteredElementCollector(ld).OfClass(typeof(ViewPlan)).Cast<ViewPlan>())
            {
                if (v.IsTemplate) continue;
                list.Add(new { ViewId = v.Id.GetIdValue(), v.Name, ViewType = v.ViewType.ToString() });
            }
            return list;
        }

        /// <summary>
        /// 讀 VV → Revit Links 單一列（實例或類型）：顯示設定的全部欄位與半色調
        /// </summary>
        private Dictionary<string, object> DescribeLinkRow(Document doc, View view, ElementId id)
        {
            var row = new Dictionary<string, object>();

            try
            {
                RevitLinkGraphicsSettings s = view.GetLinkOverrides(id);
                if (s == null)
                {
                    row["DisplaySettings"] = "(無覆寫)";
                }
                else
                {
                    var fields = new Dictionary<string, object>();
                    foreach (var p in typeof(RevitLinkGraphicsSettings).GetProperties())
                    {
                        if (!p.CanRead || p.GetIndexParameters().Length > 0) continue;
                        try
                        {
                            object v = p.GetValue(s);
                            if (v is ElementId eid)
                            {
                                if (eid == ElementId.InvalidElementId) v = null;
                                else v = eid.GetIdValue();
                            }
                            else if (v != null && !(v is string) && !v.GetType().IsPrimitive && !v.GetType().IsEnum)
                            {
                                v = v.ToString();
                            }
                            fields[p.Name] = v;
                        }
                        catch (Exception ex) { fields[p.Name] = $"(讀取失敗: {ex.Message})"; }
                    }
                    row["DisplaySettings"] = fields;
                }
            }
            catch (Exception ex)
            {
                row["DisplaySettings"] = $"(讀取失敗: {ex.Message})";
            }

            try { row["Halftone"] = view.GetElementOverrides(id).Halftone; }
            catch (Exception ex) { row["Halftone"] = $"(讀取失敗: {ex.Message})"; }

            row["Underlay"] = "(Revit API 未開放讀取)";
            return row;
        }

        private object SetLinkDisplaySettings(JObject parameters)
        {
            Document doc = _uiApp.ActiveUIDocument.Document;
            View view = ResolveViewForLinks(doc, parameters);

            string modeToken = (parameters["displaySetting"]?.Value<string>() ?? "byHostView")
                .Trim().Replace(" ", "").Replace("_", "").ToLowerInvariant();

            LinkVisibility mode;
            switch (modeToken)
            {
                case "byhostview":
                case "host":
                    mode = LinkVisibility.ByHostView;
                    break;
                case "bylinkview":
                case "link":
                    mode = LinkVisibility.ByLinkView;
                    break;
                case "custom":
                    mode = LinkVisibility.Custom;
                    break;
                default:
                    throw new Exception(
                        $"未知的 displaySetting '{modeToken}'。可用值：byHostView / byLinkView / custom");
            }

            var targets = ResolveTargetLinks(doc, parameters);
            if (targets.Count == 0)
            {
                throw new Exception("找不到任何符合條件的連結模型實體（RevitLinkInstance）");
            }

            var applied = new List<object>();
            var failed = new List<object>();

            // 選填：連結檔裡要參照的視圖（ByLinkView / Custom 用）與套用列（instance = 實例列，type = 類型上層列）
            IdType linkedViewIdValue = parameters["linkedViewId"]?.Value<IdType>() ?? 0;
            bool applyToType = string.Equals(parameters["applyTo"]?.Value<string>(), "type", StringComparison.OrdinalIgnoreCase);

            using (var tx = new Transaction(doc, "設定連結顯示方式"))
            {
                tx.Start();

                foreach (var link in targets)
                {
                    var type = doc.GetElement(link.GetTypeId()) as RevitLinkType;
                    string name = type != null ? type.Name : link.Name;

                    try
                    {
                        ElementId rowId = applyToType && type != null ? type.Id : link.Id;
                        RevitLinkGraphicsSettings settings = view.GetLinkOverrides(rowId)
                                                             ?? new RevitLinkGraphicsSettings();
                        settings.LinkVisibilityType = mode;
                        if (linkedViewIdValue > 0)
                        {
                            var linkDoc = link.GetLinkDocument();
                            var lv = linkDoc?.GetElement(new ElementId(linkedViewIdValue)) as View;
                            if (lv == null) throw new Exception($"連結檔裡找不到視圖 ID {linkedViewIdValue}");
                            settings.LinkedViewId = lv.Id;
                        }
                        view.SetLinkOverrides(rowId, settings);

                        applied.Add(new { LinkInstanceId = link.Id.GetIdValue(), LinkTypeName = name, Row = applyToType ? "type" : "instance" });
                    }
                    catch (Exception ex)
                    {
                        failed.Add(new
                        {
                            LinkInstanceId = link.Id.GetIdValue(),
                            LinkTypeName = name,
                            Error = ex.Message
                        });
                    }
                }

                tx.Commit();
            }

            return new
            {
                Success = failed.Count == 0,
                ViewId = view.Id.GetIdValue(),
                ViewName = view.Name,
                DisplaySetting = mode.ToString(),
                AppliedCount = applied.Count,
                FailedCount = failed.Count,
                Applied = applied,
                Failed = failed,
                Message = $"已在視圖 '{view.Name}' 將 {applied.Count} 個連結設為 {mode}"
            };
        }

#endif

        // ── helpers ────────────────────────────────────────────────────────

        private View ResolveViewForLinks(Document doc, JObject parameters)
        {
            IdType viewId = parameters["viewId"]?.Value<IdType>() ?? 0;
            if (viewId > 0)
            {
                var view = doc.GetElement(new ElementId(viewId)) as View;
                if (view == null) throw new Exception($"找不到視圖 ID: {viewId}");
                return view;
            }
            return _uiApp.ActiveUIDocument.ActiveView;
        }

        /// <summary>
        /// 解析目標連結：linkInstanceIds > linkNames > 全部。
        /// </summary>
        private List<RevitLinkInstance> ResolveTargetLinks(Document doc, JObject parameters)
        {
            var all = new FilteredElementCollector(doc)
                .OfClass(typeof(RevitLinkInstance))
                .Cast<RevitLinkInstance>()
                .ToList();

            var idToken = parameters["linkInstanceIds"] as JArray;
            if (idToken != null && idToken.Count > 0)
            {
                var wanted = idToken.Select(t => t.Value<IdType>()).ToHashSet();
                return all.Where(l => wanted.Contains(l.Id.GetIdValue())).ToList();
            }

            var nameToken = parameters["linkNames"] as JArray;
            if (nameToken != null && nameToken.Count > 0)
            {
                var wanted = nameToken.Select(t => t.Value<string>()).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
                return all.Where(l =>
                {
                    var type = doc.GetElement(l.GetTypeId()) as RevitLinkType;
                    string name = type != null ? type.Name : l.Name;
                    return wanted.Any(w => name.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0);
                }).ToList();
            }

            return all;
        }
    }
}
