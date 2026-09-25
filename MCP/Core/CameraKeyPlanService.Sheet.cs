using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Autodesk.Revit.UI;
using RevitMCP.Configuration;

// Revit 2025+ ElementId: int → long
#if REVIT2025_OR_GREATER
using IdType = System.Int64;
#else
using IdType = System.Int32;
#endif

namespace RevitMCP.Core
{
    /// <summary>
    /// 2026-09-25 Wave 3：一鍵出圖紙（spec §10.13、ADR D-029）。
    ///
    /// 一台相機一張圖紙「Key Plan-&lt;相機名&gt;」：左側約 60% 放透視圖，右欄由上而下放 Key Plan 平面／剖面／等角。
    /// 規則：
    ///   1. 圖框類型：呼叫端指定 → 面板設定（<see cref="CameraManagerSettings.SheetTitleBlockTypeId"/>）→ 專案內最常用 → 第一個圖框類型 → 無圖框。
    ///   2. 可繪區＝圖框實例在圖紙上的外接框向內縮 <see cref="CameraManagerSettings.SheetMarginMm"/>；沒有圖框＝A3 橫式 420×297 mm。
    ///   3. 只調整視埠「位置」（置中於格子），不改任何視圖比例；透視圖本來就沒有比例，原樣放置。放不進格子只回報 Overflow。
    ///   4. 視埠類型：沿用專案既有、會顯示標題的視埠類型（最常用者），不新建。
    ///   5. 一張視圖只能在一張圖紙上：已在「本相機的圖紙」（ES Role＝Sheet）上的視埠保留原位；在別張圖紙上的回報並略過。
    ///   6. 相機還沒有 Key Plan 視圖時，先跑一鍵 4 視圖（不並排）。
    /// 單一交易「建立相機圖紙」；呼叫端須在 API context、交易外。
    /// </summary>
    internal static partial class CameraKeyPlanService
    {
        internal const string SheetRole = "Sheet";
        internal const string SheetTransactionName = "建立相機圖紙";
        private const double SheetDefaultWidthMm = 420.0;   // 沒有圖框時的預設可繪區（A3 橫式）
        private const double SheetDefaultHeightMm = 297.0;
        private const double SheetPerspectiveFraction = 0.6; // 透視圖佔可繪區寬度的比例
        private const double SheetCellGapMm = 6.0;           // 格子之間的間距（留給視埠標題）

        internal sealed class SheetViewportInfo
        {
            public string Role;            // Perspective／Plan／Section／Iso
            public ElementId ViewId;
            public string ViewName;
            public ElementId ViewportId;
            public string Status;          // 新放置／保留原位／略過
            public string Note;
            public double CenterXFeet, CenterYFeet, WidthFeet, HeightFeet;
            public double CellMinX, CellMinY, CellMaxX, CellMaxY;
            public bool Overflow;
            public string ViewportTypeName;
        }

        internal sealed class SheetResult
        {
            public ElementId SheetId;
            public string SheetNumber;
            public string SheetName;
            public bool Created;
            public bool RanFourViews;
            public ElementId TitleBlockTypeId;
            public string TitleBlockName;
            public string TitleBlockSource;
            public string ViewportTypeLog;
            public double DrawMinX, DrawMinY, DrawMaxX, DrawMaxY; // 可繪區（圖紙座標 feet）
            public string DrawAreaSource;
            public List<SheetViewportInfo> Viewports = new List<SheetViewportInfo>();
            public List<string> Warnings = new List<string>();
            public string Error;
        }

        // ══════════════════════════════════════════════════════════
        // 圖框類型
        // ══════════════════════════════════════════════════════════

        /// <summary>專案內所有圖框類型＋已放置次數（依使用次數多→少、名稱排序）。</summary>
        internal static List<TitleBlockOption> ListTitleBlockTypes(Document doc)
        {
            var list = new List<TitleBlockOption>();
            if (doc == null) return list;
            var usage = new Dictionary<long, int>();
            foreach (Element tb in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_TitleBlocks).WhereElementIsNotElementType())
            {
                long tid = tb.GetTypeId().GetIdValue();
                usage[tid] = usage.TryGetValue(tid, out int n) ? n + 1 : 1;
            }
            foreach (FamilySymbol fs in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_TitleBlocks).WhereElementIsElementType().OfType<FamilySymbol>())
            {
                long id = fs.Id.GetIdValue();
                list.Add(new TitleBlockOption
                {
                    Id = id,
                    Name = (fs.FamilyName ?? "") + "：" + fs.Name,
                    UseCount = usage.TryGetValue(id, out int n) ? n : 0
                });
            }
            return list.OrderByDescending(o => o.UseCount).ThenBy(o => o.Name, StringComparer.Ordinal).ToList();
        }

        /// <summary>挑圖框類型：requested（MCP 參數）→ 面板設定 → 最常用 → 第一個；都沒有＝InvalidElementId。</summary>
        internal static ElementId ChooseTitleBlockType(Document doc, long? requested, out string name, out string source)
        {
            name = null;
            source = null;
            List<TitleBlockOption> all = ListTitleBlockTypes(doc);
            TitleBlockOption Find(long id) => all.FirstOrDefault(o => o.Id == id);

            if (requested.HasValue && requested.Value > 0)
            {
                TitleBlockOption o = Find(requested.Value);
                if (o == null) throw new Exception($"titleBlockTypeId {requested.Value} 不是圖框類型。");
                name = o.Name; source = "指定";
                return new ElementId((IdType)o.Id);
            }
            long setting = 0;
            try { setting = ConfigManager.Instance.Settings.CameraManager.SheetTitleBlockTypeId; } catch { }
            if (setting > 0)
            {
                TitleBlockOption o = Find(setting);
                if (o != null) { name = o.Name; source = "面板設定"; return new ElementId((IdType)o.Id); }
                Logger.Info($"相機圖紙：面板設定的圖框類型 id={setting} 已不存在，改用最常用的圖框。");
            }
            if (all.Count == 0) { source = "專案內沒有圖框類型（無圖框）"; return ElementId.InvalidElementId; }
            TitleBlockOption first = all[0];
            name = first.Name;
            source = first.UseCount > 0 ? $"最常用（{first.UseCount} 張圖紙）" : "沒有圖紙使用圖框，取第一個類型";
            return new ElementId((IdType)first.Id);
        }

        // ══════════════════════════════════════════════════════════
        // 視埠類型：沿用會顯示標題的既有類型（最常用者），不新建
        // ══════════════════════════════════════════════════════════

        private static readonly string[] NoTitleTexts = { "No", "否", "無", "不", "不顯示", "Never" };

        private static ElementId ChooseViewportType(Document doc, out string typeName, out string log)
        {
            typeName = null;
            var usage = new Dictionary<long, int>();
            foreach (Viewport vp in new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>())
            {
                long tid = vp.GetTypeId().GetIdValue();
                usage[tid] = usage.TryGetValue(tid, out int n) ? n + 1 : 1;
            }

            long vpCat = (long)(int)BuiltInCategory.OST_Viewports;
            var candidates = new List<(ElementType t, bool title, int used, string raw)>();
            foreach (ElementType t in new FilteredElementCollector(doc).OfClass(typeof(ElementType)).Cast<ElementType>())
            {
                if (t.Category == null || t.Category.Id.GetIdValue() != vpCat) continue;
                bool title = true;
                string raw = "(無參數)";
                try
                {
                    Parameter p = t.get_Parameter(BuiltInParameter.VIEWPORT_ATTR_SHOW_LABEL);
                    if (p != null)
                    {
                        string vs = p.AsValueString();
                        int iv = p.StorageType == StorageType.Integer ? p.AsInteger() : -1;
                        raw = $"{vs ?? "?"}/{iv}";
                        // 未查證：整數值與「是／否／多個視埠時」的對應。先看顯示字串，取不到才以 0＝否判斷。
                        title = !string.IsNullOrWhiteSpace(vs)
                            ? !NoTitleTexts.Any(x => string.Equals(vs.Trim(), x, StringComparison.OrdinalIgnoreCase))
                            : iv != 0;
                    }
                }
                catch { }
                candidates.Add((t, title, usage.TryGetValue(t.Id.GetIdValue(), out int u) ? u : 0, raw));
            }

            log = "視埠類型：" + string.Join("；", candidates.Select(c => $"{c.t.Name}[標題={c.raw}，用 {c.used} 次]"));
            var pick = candidates.Where(c => c.title).OrderByDescending(c => c.used).ThenBy(c => c.t.Name, StringComparer.Ordinal).FirstOrDefault();
            if (pick.t != null)
            {
                typeName = pick.t.Name;
                log += $" → 採用「{pick.t.Name}」";
                return pick.t.Id;
            }
            log += " → 沒有顯示標題的視埠類型，沿用 Revit 預設（未新建類型）";
            return ElementId.InvalidElementId;
        }

        // ══════════════════════════════════════════════════════════
        // 進入點
        // ══════════════════════════════════════════════════════════

        internal static ViewSheet FindCameraSheet(Document doc, string cameraUniqueId)
        {
            if (doc == null || string.IsNullOrEmpty(cameraUniqueId) || Schema.Lookup(SchemaGuid) == null) return null;
            return FindLinkedView(doc, cameraUniqueId, SheetRole) as ViewSheet;
        }

        /// <summary>建立或更新一台相機的圖紙。titleBlockTypeId＝null 走面板設定／最常用。</summary>
        internal static SheetResult CreateOrUpdateCameraSheet(UIApplication uiApp, Document doc, ElementId cameraViewId, long? titleBlockTypeId)
        {
            var res = new SheetResult();
            if (doc == null) { res.Error = "目前沒有開啟中的文件。"; return res; }
            View3D camera = doc.GetElement(cameraViewId) as View3D;
            if (camera == null || camera.IsTemplate || !camera.IsPerspective) { res.Error = "找不到這個相機視圖（可能已被刪除），請按重新整理。"; return res; }
            string cid = camera.UniqueId;
            string camName = camera.Name;

            // 1) 沒有 Key Plan 視圖 → 先跑一鍵 4 視圖（不並排）
            View plan = FindLinkedKeyPlanView(doc, cid, "Plan");
            View section = FindLinkedKeyPlanView(doc, cid, "Section");
            View iso = FindLinkedKeyPlanView(doc, cid, "Iso");
            if (plan == null || section == null || iso == null)
            {
                Logger.Info($"相機圖紙：「{camName}」缺少 Key Plan 視圖（平面={plan != null} 剖面={section != null} 等角={iso != null}），先執行一鍵 4 視圖。");
                KeyPlanResult kp = CreateOrUpdateFourViews(uiApp, doc, cameraViewId, false);
                if (kp.Error != null) { res.Error = "先建立 Key Plan 失敗：" + kp.Error; return res; }
                res.RanFourViews = true;
                plan = doc.GetElement(kp.PlanViewId) as View;
                section = doc.GetElement(kp.SectionViewId) as View;
                iso = doc.GetElement(kp.IsoViewId) as View;
            }

            // 2) 交易外：圖框、視埠類型、各視圖目前在哪張圖紙
            ElementId tbType;
            try { tbType = ChooseTitleBlockType(doc, titleBlockTypeId, out res.TitleBlockName, out res.TitleBlockSource); }
            catch (Exception ex) { res.Error = ex.Message; return res; }
            res.TitleBlockTypeId = tbType;
            ElementId vpType = ChooseViewportType(doc, out string vpTypeName, out string vpLog);
            res.ViewportTypeLog = vpLog;
            Logger.Info("相機圖紙：" + vpLog);

            ViewSheet sheet = FindCameraSheet(doc, cid);
            var viewOnSheet = new Dictionary<long, Viewport>();
            foreach (Viewport vp in new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>())
                viewOnSheet[vp.ViewId.GetIdValue()] = vp;

            var items = new List<(string role, View view)>
            {
                ("Perspective", camera), ("Plan", plan), ("Section", section), ("Iso", iso)
            };

            double marginFeet = CameraMath.MmToFeet(GetSheetMarginMm());
            using (Transaction t = TransactionHelper.Begin(doc, SheetTransactionName))
            {
                t.Start();
                try
                {
                    string sheetName = "Key Plan-" + camName;
                    if (sheet == null)
                    {
                        sheet = ViewSheet.Create(doc, tbType);
                        TagLink(sheet, cid, SheetRole);
                        res.Created = true;
                    }
                    else if (tbType != ElementId.InvalidElementId)
                    {
                        // 既有圖紙：圖框類型與目前選擇不同 → 換類型（不動位置）
                        Element tbInst = FirstTitleBlockOn(doc, sheet);
                        if (tbInst != null && tbInst.GetTypeId() != tbType)
                        {
                            try { tbInst.ChangeTypeId(tbType); res.Warnings.Add("已把既有圖紙的圖框換成「" + res.TitleBlockName + "」。"); }
                            catch (Exception ex) { res.Warnings.Add("更換圖框類型失敗：" + ex.Message); }
                        }
                    }
                    if (sheet.Name != sheetName)
                    {
                        try { sheet.Name = sheetName; }
                        catch (Exception ex) { res.Warnings.Add("圖紙改名失敗：" + ex.Message); }
                    }
                    doc.Regenerate();

                    // 3) 可繪區
                    Element tb = FirstTitleBlockOn(doc, sheet);
                    BoundingBoxXYZ bb = null;
                    try { bb = tb?.get_BoundingBox(sheet); } catch { bb = null; }
                    double x0, y0, x1, y1;
                    if (bb != null && bb.Max.X - bb.Min.X > 2 * marginFeet && bb.Max.Y - bb.Min.Y > 2 * marginFeet)
                    {
                        x0 = bb.Min.X + marginFeet; y0 = bb.Min.Y + marginFeet;
                        x1 = bb.Max.X - marginFeet; y1 = bb.Max.Y - marginFeet;
                        res.DrawAreaSource = $"圖框外接框 {CameraMath.FeetToMm(bb.Max.X - bb.Min.X):F0}×{CameraMath.FeetToMm(bb.Max.Y - bb.Min.Y):F0} mm 內縮 {GetSheetMarginMm():0.#} mm";
                    }
                    else
                    {
                        x0 = marginFeet; y0 = marginFeet;
                        x1 = CameraMath.MmToFeet(SheetDefaultWidthMm) - marginFeet;
                        y1 = CameraMath.MmToFeet(SheetDefaultHeightMm) - marginFeet;
                        res.DrawAreaSource = "沒有圖框（或讀不到外接框）→ A3 橫式 420×297 mm 內縮 " + GetSheetMarginMm().ToString("0.#") + " mm";
                    }
                    res.DrawMinX = x0; res.DrawMinY = y0; res.DrawMaxX = x1; res.DrawMaxY = y1;

                    // 4) 格子：左 60% 透視；右欄三等分（上→下：平面、剖面、等角）
                    double gap = CameraMath.MmToFeet(SheetCellGapMm);
                    double splitX = x0 + (x1 - x0) * SheetPerspectiveFraction;
                    double rowH = (y1 - y0) / 3.0;
                    var cells = new Dictionary<string, double[]>
                    {
                        ["Perspective"] = new[] { x0, y0, splitX - gap / 2, y1 },
                        ["Plan"] = new[] { splitX + gap / 2, y1 - rowH + gap / 2, x1, y1 },
                        ["Section"] = new[] { splitX + gap / 2, y1 - 2 * rowH + gap / 2, x1, y1 - rowH - gap / 2 },
                        ["Iso"] = new[] { splitX + gap / 2, y0, x1, y1 - 2 * rowH - gap / 2 }
                    };

                    foreach (var (role, view) in items)
                    {
                        var info = new SheetViewportInfo { Role = role, ViewId = view?.Id, ViewName = view?.Name };
                        double[] c = cells[role];
                        info.CellMinX = c[0]; info.CellMinY = c[1]; info.CellMaxX = c[2]; info.CellMaxY = c[3];
                        res.Viewports.Add(info);
                        if (view == null) { info.Status = "略過"; info.Note = "找不到視圖"; continue; }

                        if (viewOnSheet.TryGetValue(view.Id.GetIdValue(), out Viewport existing) && doc.GetElement(existing.Id) != null)
                        {
                            if (existing.SheetId == sheet.Id)
                            {
                                info.Status = "保留原位";
                                info.ViewportId = existing.Id;
                                FillViewportGeometry(doc, existing, info);
                                continue;
                            }
                            ViewSheet other = doc.GetElement(existing.SheetId) as ViewSheet;
                            info.Status = "略過";
                            info.Note = $"已在其他圖紙「{other?.SheetNumber} - {other?.Name}」上（一張視圖只能放一張圖紙）";
                            res.Warnings.Add($"{view.Name}：{info.Note}");
                            continue;
                        }
                        if (!Viewport.CanAddViewToSheet(doc, sheet.Id, view.Id))
                        {
                            info.Status = "略過";
                            info.Note = "Revit 不允許把這張視圖放到圖紙（CanAddViewToSheet=false）";
                            res.Warnings.Add($"{view.Name}：{info.Note}");
                            continue;
                        }

                        XYZ center = new XYZ((c[0] + c[2]) / 2, (c[1] + c[3]) / 2, 0);
                        Viewport vp = Viewport.Create(doc, sheet.Id, view.Id, center);
                        if (vpType != ElementId.InvalidElementId)
                        {
                            try
                            {
                                if (vp.GetValidTypes().Contains(vpType)) vp.ChangeTypeId(vpType);
                            }
                            catch (Exception ex) { Logger.Error("相機圖紙：套用視埠類型失敗", ex); }
                        }
                        doc.Regenerate();
                        try { vp.SetBoxCenter(center); } catch (Exception ex) { Logger.Error("相機圖紙：視埠置中失敗", ex); }
                        info.Status = "新放置";
                        info.ViewportId = vp.Id;
                        FillViewportGeometry(doc, vp, info);
                    }

                    t.Commit();
                }
                catch (Exception ex)
                {
                    try { if (t.HasStarted() && !t.HasEnded()) t.RollBack(); } catch { }
                    Logger.Error("相機圖紙：建立失敗（整筆回滾）", ex);
                    res.Error = "建立圖紙失敗：" + ex.Message;
                    res.SheetId = null;
                    return res;
                }
            }

            res.SheetId = sheet.Id;
            res.SheetNumber = sheet.SheetNumber;
            res.SheetName = sheet.Name;
            foreach (SheetViewportInfo v in res.Viewports.Where(v => v.Overflow))
                res.Warnings.Add($"{v.ViewName}：視埠 {CameraMath.FeetToMm(v.WidthFeet):F0}×{CameraMath.FeetToMm(v.HeightFeet):F0} mm 超出格子 {CameraMath.FeetToMm(v.CellMaxX - v.CellMinX):F0}×{CameraMath.FeetToMm(v.CellMaxY - v.CellMinY):F0} mm（比例未變更，請手動調整）");
            Logger.Info($"相機圖紙：「{camName}」→ {res.SheetNumber} {res.SheetName}（{(res.Created ? "新建" : "更新")}；圖框 {res.TitleBlockName ?? "無"}［{res.TitleBlockSource}］；{res.DrawAreaSource}）；" +
                        string.Join("；", res.Viewports.Select(v => $"{v.Role}={v.Status}{(v.Overflow ? "(超出)" : "")}{(v.Note != null ? "：" + v.Note : "")}")));
            return res;
        }

        private static double GetSheetMarginMm()
        {
            double m = 15.0;
            try { m = ConfigManager.Instance.Settings.CameraManager.SheetMarginMm; } catch { }
            return (double.IsNaN(m) || double.IsInfinity(m) || m < 0 || m > 100) ? 15.0 : m;
        }

        private static Element FirstTitleBlockOn(Document doc, ViewSheet sheet) =>
            new FilteredElementCollector(doc, sheet.Id).OfCategory(BuiltInCategory.OST_TitleBlocks).WhereElementIsNotElementType().FirstElement();

        private static void FillViewportGeometry(Document doc, Viewport vp, SheetViewportInfo info)
        {
            try
            {
                XYZ ctr = vp.GetBoxCenter();
                Outline o = vp.GetBoxOutline();
                info.CenterXFeet = ctr.X; info.CenterYFeet = ctr.Y;
                info.WidthFeet = o.MaximumPoint.X - o.MinimumPoint.X;
                info.HeightFeet = o.MaximumPoint.Y - o.MinimumPoint.Y;
                double tol = CameraMath.MmToFeet(0.5);
                info.Overflow = info.WidthFeet > (info.CellMaxX - info.CellMinX) + tol || info.HeightFeet > (info.CellMaxY - info.CellMinY) + tol;
                info.ViewportTypeName = doc.GetElement(vp.GetTypeId())?.Name;
            }
            catch (Exception ex) { Logger.Error("相機圖紙：讀取視埠範圍失敗", ex); }
        }
    }
}
