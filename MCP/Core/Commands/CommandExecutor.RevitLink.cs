// Revit 跳轉的外掛端指令（docs/REVIT_LINK.md、ADR D-015；對應 issue-manager 規格 revit-link.md §15、§27.2、§27.3）。
// 仿照 CommandExecutor.ViewSync.cs（partial class CommandExecutor；ViewSyncRequireDocument、ViewSyncActivate、ViewSyncException 沿用既有的）。
// 這個檔案裡的指令全部唯讀：不開 Transaction、不建立視圖、不套用暫時設定、不刪除或修改元素（單元測試掃原始碼保證，見 MCP.Tests/ViewSync/RevitLinkTests.cs）。
// 唯一的「副作用」是切換使用中視圖與設定選取（UI 狀態），不是模型修改，也不會讓模型變成「已修改」。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCP.Core.ViewSync;

#if REVIT2025_OR_GREATER
using IdType = System.Int64;
#else
using IdType = System.Int32;
#endif

namespace RevitMCP.Core
{
    /// <summary>
    /// 瀏覽器可呼叫的四個新指令（全部唯讀）：viewsync_open_link、viewsync_get_selection、viewsync_capture_view、viewsync_draft_take。
    /// viewsync_draft_put 不在這裡：它只供外掛內部（OpenIssueDraft）使用，不在瀏覽器白名單內，瀏覽器送來一律 command_not_allowed。
    /// </summary>
    public partial class CommandExecutor
    {
        private const int RevitLinkCaptureMaxBytes = 5 * 1024 * 1024;

        // ------------------------------------------------------------------ 模型版次（spec §27.2）

        /// <summary>model_revision＝「儲存次數:版本 GUID」，取自 Document.GetDocumentVersion。只比相同與否，不比大小。取不到回 null（網頁當作「無法比對」，不顯示警告）。</summary>
        internal static string RevitLinkRevisionOf(Document doc)
        {
            try
            {
                var version = Document.GetDocumentVersion(doc);
                return version.NumberOfSaves + ":" + version.VersionGUID;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static JObject RevitLinkModelJson(Document doc)
        {
            var info = ViewSyncModelInfo.FromDocument(doc);
            return new JObject
            {
                ["title"] = info.Title,
                ["guid"] = info.Guid,
                ["revision"] = RevitLinkRevisionOf(doc),
            };
        }

        /// <summary>沿用 D-710 的模型核對（GUID 兩邊都有就比 GUID，否則比檔名）：不符丟 model_mismatch；只比了檔名時加警告。</summary>
        private static void RevitLinkCheckModel(string requestTitle, string requestGuid, Document doc, List<string> warnings)
        {
            var info = ViewSyncModelInfo.FromDocument(doc);
            switch (ViewSyncMath.CompareModel(requestTitle, requestGuid, info.Title, info.Guid))
            {
                case ViewSyncMath.ModelMatch.Mismatch:
                    throw new ViewSyncException("model_mismatch",
                        $"Revit 目前開的是「{info.Title}」，這個連結要的是「{(string.IsNullOrWhiteSpace(requestTitle) ? requestGuid : requestTitle)}」。");
                case ViewSyncMath.ModelMatch.TitleOnly:
                    warnings.Add("模型只比對了檔名（連結沒有 Revit 檔識別，未驗證是同一個檔）。");
                    break;
            }
        }

        // ------------------------------------------------------------------ viewsync_open_link

        /// <summary>
        /// 輸入：{ model:{title,guid?,revision?}, link:{elementUniqueIds[], elementIds[]?, viewUniqueId?, sheetNumber?}, allowFallback, revisionPolicy: "continue"|"block"|"sheet_only" }
        /// 輸出：{ level:"element"|"view"|"sheet"|"none", found[], missing[], hintUsed, view:{uniqueId,name}, selectedCount, modelRevision?, modelRevisionDiffers?, warnings[] }
        /// 錯誤碼：model_mismatch、model_revision_differs（revisionPolicy=block 且版次不同）、activate_failed、no_document、not_project_document。
        /// 全部找不到時回 level="none"（不丟錯），由網頁依 allowFallback 與連結內容決定畫面。
        /// 唯讀：找視圖→切 ActiveView→用 UniqueId 找元素→選取並 ShowElements；不改模型。
        /// </summary>
        private object ViewSyncOpenLink(JObject p)
        {
            var uiDoc = ViewSyncRequireDocument(false);
            var doc = uiDoc.Document;
            var warnings = new List<string>();

            var model = p["model"] as JObject ?? new JObject();
            var modelTitle = model.Value<string>("title");
            var modelGuid = model.Value<string>("guid");
            if (!string.IsNullOrWhiteSpace(modelTitle) || !string.IsNullOrWhiteSpace(modelGuid))
                RevitLinkCheckModel(modelTitle, modelGuid, doc, warnings);

            var currentRevision = RevitLinkRevisionOf(doc);
            var linkRevision = model.Value<string>("revision");
            bool? differs = (currentRevision != null && !string.IsNullOrWhiteSpace(linkRevision)) ? (bool?)(currentRevision != linkRevision) : null;
            var policy = p.Value<string>("revisionPolicy") ?? "continue";
            if (differs == true && policy == "block")
                throw new ViewSyncException("model_revision_differs", "模型在連結建立後又存過檔，設定為不跳轉。");

            var link = p["link"] as JObject ?? new JObject();
            var uniqueIds = (link["elementUniqueIds"] as JArray)?.Select(t => t.Value<string>()).Where(s => !string.IsNullOrWhiteSpace(s)).ToList() ?? new List<string>();
            var sheetOnly = differs == true && policy == "sheet_only"; // 「只開到圖紙，不選元素」
            if (sheetOnly) uniqueIds.Clear();
            var viewUniqueId = link.Value<string>("viewUniqueId");
            var sheetNumber = link.Value<string>("sheetNumber");
            var allowFallback = p["allowFallback"]?.Value<bool>() ?? true;

            // 1. 找元素（UniqueId 為主；ElementId 只當後備提示，命中時 hintUsed=true）
            var found = new List<Element>();
            var missing = new List<string>();
            foreach (var uid in uniqueIds)
            {
                Element element = null;
                try { element = doc.GetElement(uid); } catch (Exception) { /* 格式怪異的字串當作找不到 */ }
                if (element != null) found.Add(element); else missing.Add(uid);
            }
            var hintUsed = false;
            if (found.Count == 0 && !sheetOnly)
            {
                if (link["elementIds"] is JArray hintIds)
                {
                    foreach (var token in hintIds)
                    {
                        long raw;
                        if (!long.TryParse(token.Value<string>(), out raw) || raw < 0) continue;
#if !REVIT2025_OR_GREATER
                        if (raw > int.MaxValue) continue; // Revit 2024 的 ElementId 是 32 位元
#endif
                        var byId = doc.GetElement(((IdType)raw).ToElementId());
                        if (byId != null) { found.Add(byId); hintUsed = true; warnings.Add("element_id_hint_used"); }
                    }
                }
            }

            // 2. 決定要開的視圖：元素連結優先用 viewUniqueId；找不到或元素在該視圖看不到時退到元素所在的預設視圖（hidden_in_view）
            View view = null;
            if (!string.IsNullOrWhiteSpace(viewUniqueId))
            {
                try { view = doc.GetElement(viewUniqueId) as View; } catch (Exception) { view = null; }
            }
            if (view != null && (view.IsTemplate || view.ViewType == ViewType.ProjectBrowser || view.ViewType == ViewType.SystemBrowser || view.ViewType == ViewType.Internal)) view = null;

            string level;
            if (found.Count > 0)
            {
                level = "element";
                if (view == null || !RevitLinkVisibleIn(doc, view, found))
                {
                    var better = RevitLinkViewShowing(doc, found);
                    if (view != null && better != null) warnings.Add("hidden_in_view");
                    view = better ?? view ?? uiDoc.ActiveView;
                }
            }
            else if (sheetOnly && allowFallback && !string.IsNullOrWhiteSpace(sheetNumber) && RevitLinkFindSheet(doc, sheetNumber) is View sheetView)
            {
                level = "sheet";
                view = sheetView;
            }
            else if (view != null && allowFallback) level = "view";
            else if (!string.IsNullOrWhiteSpace(sheetNumber) && allowFallback && (view = RevitLinkFindSheet(doc, sheetNumber)) != null) level = "sheet";
            else level = "none";

            if (level == "none")
                return RevitLinkOpenResult("none", found, missing, hintUsed, null, 0, currentRevision, differs, warnings);

            // 3. 切視圖並選取（Revit 正忙、在編輯模式時 ExternalEvent 排不進來，由 SocketService 逾時回應）
            if (!ViewSyncActivate(uiDoc, view, warnings))
                throw new ViewSyncException("activate_failed", warnings.LastOrDefault() ?? "無法切換視圖。");
            var selected = 0;
            if (level == "element")
            {
                var ids = found.Select(e => e.Id).ToList();
                uiDoc.Selection.SetElementIds(ids);
                try { uiDoc.ShowElements(ids); } catch (Exception) { warnings.Add("show_elements_failed"); }
                selected = ids.Count;
            }
            return RevitLinkOpenResult(level, found, missing, hintUsed, view, selected, currentRevision, differs, warnings);
        }

        private static JObject RevitLinkOpenResult(string level, List<Element> found, List<string> missing, bool hintUsed, View view, int selected, string revision, bool? differs, List<string> warnings)
        {
            var result = new JObject
            {
                ["level"] = level,
                ["found"] = new JArray(found.Select(e => e.UniqueId)),
                ["missing"] = new JArray(missing),
                ["hintUsed"] = hintUsed,
                ["view"] = view == null ? new JObject() : new JObject { ["uniqueId"] = view.UniqueId, ["name"] = view.Name },
                ["selectedCount"] = selected,
                ["warnings"] = new JArray(warnings),
            };
            if (revision != null) result["modelRevision"] = revision;
            if (differs.HasValue) result["modelRevisionDiffers"] = differs.Value; // 沒送 model.revision 就不回（網頁當作無法比對）
            return result;
        }

        /// <summary>元素在這個視圖看得到（沒被隱藏、沒被視圖範圍切掉）。只查這幾個元素，不列舉整個視圖。</summary>
        private static bool RevitLinkVisibleIn(Document doc, View view, List<Element> elements)
        {
            try
            {
                var ids = elements.Select(e => e.Id).ToList();
                if (ids.Count == 0) return false;
                var visible = new FilteredElementCollector(doc, view.Id).WherePasses(new ElementIdSetFilter(ids)).ToElements();
                return visible.Any(e => !e.IsHidden(view));
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>元素在目前視圖看不到時，找一個看得到它的視圖：先樓層平面（元素所在樓層），再預設 3D 視圖（最多試 10 個）。找不到回 null。</summary>
        private static View RevitLinkViewShowing(Document doc, List<Element> elements)
        {
            var first = elements[0];
            var candidates = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(v => !v.IsTemplate && v.CanBePrinted).ToList();
            if (first.LevelId != ElementId.InvalidElementId)
            {
                var plans = candidates.OfType<ViewPlan>().Where(v => v.ViewType == ViewType.FloorPlan && v.GenLevel != null && v.GenLevel.Id == first.LevelId);
                foreach (var plan in plans) if (RevitLinkVisibleIn(doc, plan, elements)) return plan;
            }
            var views3D = candidates.OfType<View3D>().Where(v => !v.IsPerspective).OrderBy(v => v.Name == "{3D}" ? 0 : 1).Take(10);
            foreach (var v3 in views3D) if (RevitLinkVisibleIn(doc, v3, elements)) return v3;
            return null;
        }

        private static View RevitLinkFindSheet(Document doc, string sheetNumber)
        {
            return new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                .FirstOrDefault(s => string.Equals(s.SheetNumber, sheetNumber, StringComparison.OrdinalIgnoreCase));
        }

        // ------------------------------------------------------------------ viewsync_get_selection

        /// <summary>讀目前選取（「加入 Revit 連結」與反向建議題用）。輸出 { model:{title,guid,path?,revision}, view:{uniqueId,name,type,sheetNumber?}, elements:[{uniqueId,elementId,category,name,level?}] }；沒選回 selection_empty。</summary>
        private object ViewSyncGetSelection(JObject p)
        {
            var uiDoc = ViewSyncRequireDocument(false);
            var doc = uiDoc.Document;
            var ids = uiDoc.Selection.GetElementIds();
            if (ids.Count == 0) throw new ViewSyncException("selection_empty", "Revit 裡目前沒有選取任何元素。");
            var elements = new JArray();
            foreach (var id in ids.Take(200))
            {
                var element = doc.GetElement(id);
                if (element == null) continue;
                string levelName = null;
                try
                {
                    if (element.LevelId != ElementId.InvalidElementId) levelName = (doc.GetElement(element.LevelId) as Level)?.Name;
                }
                catch (Exception) { /* 取不到樓層就不帶 */ }
                string name = null;
                try { name = element.Name; } catch (Exception) { /* 少數元素讀 Name 會丟例外 */ }
                elements.Add(new JObject
                {
                    ["uniqueId"] = element.UniqueId,
                    ["elementId"] = id.GetIdValue().ToString(),
                    ["category"] = element.Category?.Name,
                    ["name"] = name,
                    ["level"] = levelName,
                });
            }
            var view = uiDoc.ActiveView;
            var modelJson = RevitLinkModelJson(doc);
            // 本機路徑只回給沒有 Origin 的本機連線（沿用 viewsync_status 的規則）
            if (p["__browser"]?.Type != JTokenType.Boolean || !p["__browser"].Value<bool>())
                modelJson["path"] = doc.PathName;
            return new JObject
            {
                ["model"] = modelJson,
                ["view"] = new JObject
                {
                    ["uniqueId"] = view?.UniqueId,
                    ["name"] = view?.Name,
                    ["type"] = view?.ViewType.ToString(),
                    ["sheetNumber"] = RevitLinkSheetNumberOf(doc, view),
                },
                ["elements"] = elements,
            };
        }

        /// <summary>目前視圖若是圖紙或已放在圖紙上，回圖號；沒有回 null。</summary>
        private static string RevitLinkSheetNumberOf(Document doc, View view)
        {
            if (view == null) return null;
            var sheet = view as ViewSheet;
            if (sheet != null) return sheet.SheetNumber;
            try
            {
                var viewport = new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>().FirstOrDefault(v => v.ViewId == view.Id);
                return viewport == null ? null : (doc.GetElement(viewport.SheetId) as ViewSheet)?.SheetNumber;
            }
            catch (Exception)
            {
                return null; // 取不到圖號時不帶
            }
        }

        // ------------------------------------------------------------------ viewsync_capture_view

        /// <summary>輸出目前視圖 PNG：{ mime:"image/png", dataBase64, width, height }；超過 5 MB 自動降解析度重出（長邊從 maxLongEdgePx 起，每次乘 0.7，最多 4 次）。失敗回 capture_failed。</summary>
        private object ViewSyncCaptureView(JObject p)
        {
            var uiDoc = ViewSyncRequireDocument(false);
            var doc = uiDoc.Document;
            var view = uiDoc.ActiveView;
            if (view == null) throw new ViewSyncException("capture_failed", "目前沒有作用中的視圖。");
            if (view.ViewType == ViewType.Schedule || view.ViewType == ViewType.ColumnSchedule || view.ViewType == ViewType.PanelSchedule)
                throw new ViewSyncException("capture_failed", "目前的視圖是明細表，無法截圖。");

            var edge = Math.Max(400, Math.Min(4000, p["maxLongEdgePx"]?.Value<int>() ?? 2000));
            for (var attempt = 0; attempt < 4; attempt++, edge = Math.Max(200, (int)(edge * 0.7)))
            {
                CaptureResult capture;
                try
                {
                    capture = ExportViewToImage(doc, view, edge, false, FitDirectionType.Horizontal);
                }
                catch (Exception ex)
                {
                    throw new ViewSyncException("capture_failed", "無法輸出目前視圖的截圖：" + ex.Message);
                }

                try
                {
                    if (capture.ByteSize > RevitLinkCaptureMaxBytes) continue;
                    int width, height;
                    RevitLinkPngSize(capture.FilePath, out width, out height);
                    return new JObject { ["mime"] = "image/png", ["dataBase64"] = capture.Base64, ["width"] = width, ["height"] = height };
                }
                finally
                {
                    CleanupCaptureDir(capture.Directory);
                }
            }
            throw new ViewSyncException("capture_failed", "視圖截圖超過 5 MB，降低解析度後仍然太大。");
        }

        /// <summary>讀 PNG 檔頭（IHDR）的寬高；讀不到回 0（網頁當作未知）。</summary>
        private static void RevitLinkPngSize(string path, out int width, out int height)
        {
            width = 0; height = 0;
            try
            {
                var header = new byte[24];
                using (var stream = File.OpenRead(path))
                {
                    if (stream.Read(header, 0, 24) < 24) return;
                }
                width = (header[16] << 24) | (header[17] << 16) | (header[18] << 8) | header[19];
                height = (header[20] << 24) | (header[21] << 16) | (header[22] << 8) | header[23];
            }
            catch (Exception)
            {
                width = 0; height = 0;
            }
        }

        // ------------------------------------------------------------------ viewsync_draft_take（draft_put 只在外掛內部，見 CommandExecutor.RevitLinkDraft.cs）

        private object ViewSyncDraftTake(JObject p)
        {
            var code = p.Value<string>("code");
            JObject draft;
            var status = RevitLinkDraftStore.Shared.Take(code, out draft);
            if (status == DraftTakeStatus.Expired) throw new ViewSyncException("draft_expired", "草稿已過期，請回 Revit 再按一次。");
            if (status != DraftTakeStatus.Ok) throw new ViewSyncException("draft_not_found", "草稿已被領走或不存在，請回 Revit 再按一次。");
            return draft;
        }
    }
}
