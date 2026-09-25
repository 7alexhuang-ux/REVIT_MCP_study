using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Autodesk.Revit.DB.Structure;
using RevitMCP.Configuration;

namespace RevitMCP.Core
{
    /// <summary>
    /// 2026-09-25 Wave 2：A 型相機標記（模型族群，平面／剖面／3D 都看得到；spec §10.11）＋拖標記 → 相機（§10.12）。
    ///
    /// 組成（每台相機）：
    ///   ① 族群實例「RevitMCP 相機標記」：相機體（200×120×120 mm 擠出）＋視線箭頭（扁平擠出），原點＝眼睛、族群 +Y＝相機前方。
    ///      族群第一次需要時用程式從 Revit 的一般模型樣板建立、存成暫存 .rfa 再載入（D-026）。實例參數：視野角左、視野角右、
    ///      遠裁剪、俯仰角、相機名稱（資料用，可做標籤／明細表）。非工作平面族群不能傾斜 → 一律水平，俯仰角只寫參數。
    ///   ② 視野扇形：DirectShape（一般模型，幾何指定子類別「相機標記」的 GraphicsStyle），水平扇形 10 mm 厚，
    ///      左右緣＝相機實際（非對稱）邊界角、半徑＝與 Key Plan 相同的扇形半徑；釘選（Pinned）避免誤拖。
    ///      —— 族群內的扇形改由參數驅動需要角度標註＋鎖定的參數化草圖，無法在沒有實機的情況下可靠建立，故扇形改用每台相機一個 DirectShape（D-026）。
    ///   ③ 子類別「相機標記」（一般模型）：所有透視圖以類別可見性隱藏它 → 相機自己永遠看不到標記。Enscape 是否排除＝使用者實測（roadmap）。
    /// 兩者都以 ES（CameraKeyPlanLink：相機 UniqueId＋Role＝MarkerA／MarkerAFan）連結相機；同步時同一台相機只留一個、相機刪除後清掉。
    /// </summary>
    internal static partial class CameraKeyPlanService
    {
        internal const string AMarkerFamilyName = "RevitMCP 相機標記";
        internal const string AMarkerSubcategoryName = "相機標記";
        internal const string AMarkerRole = "MarkerA";
        internal const string AFanRole = "MarkerAFan";
        internal const string AMarkerTransactionName = "更新相機 A 標記";
        internal const string AFamilyLoadTransactionName = "載入相機標記族群";
        internal const string MarkerToCameraTransactionName = "由標記更新相機";
        private const string AMarkerMaterialName = "RevitMCP 相機標記";
        private const string ParamLeft = "視野角左";
        private const string ParamRight = "視野角右";
        private const string ParamFar = "遠裁剪";
        private const string ParamPitch = "俯仰角";
        private const string ParamCamName = "相機名稱";
        private const double AFanThicknessMm = 10.0;
        private const int AMarkerMaterialTransparency = 70;

        // ── 設定 ──
        internal static CameraMarkerMode GetMarkerMode()
        {
            if (_markerModeOverride.HasValue) return _markerModeOverride.Value; // 2026-09-25：MCP run_camera_keyplan markerMode 單次覆寫
            try { return ConfigManager.Instance.Settings.CameraManager.MarkerMode; }
            catch { return CameraMarkerMode.B; }
        }
        internal static bool MarkerModeIncludesA() { CameraMarkerMode m = GetMarkerMode(); return m == CameraMarkerMode.A || m == CameraMarkerMode.AB; }
        internal static bool MarkerModeIncludesB() { CameraMarkerMode m = GetMarkerMode(); return m == CameraMarkerMode.B || m == CameraMarkerMode.AB; }

        // ── 查詢 ──
        internal static FamilySymbol FindAMarkerSymbol(Document doc)
        {
            if (doc == null) return null;
            Family fam = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>().FirstOrDefault(f => f.Name == AMarkerFamilyName);
            if (fam == null) return null;
            foreach (ElementId id in fam.GetFamilySymbolIds())
                if (doc.GetElement(id) is FamilySymbol fs) return fs;
            return null;
        }

        /// <summary>這個元素是不是 A 型標記族群實例（族群名稱＋ES Role＝MarkerA）；是的話回傳相機 UniqueId。</summary>
        internal static bool IsAMarkerInstance(Element e, out string cameraUniqueId)
        {
            cameraUniqueId = null;
            try
            {
                if (!(e is FamilyInstance fi)) return false;
                if (fi.Symbol?.Family?.Name != AMarkerFamilyName) return false;
                if (!TryReadLink(fi, out string cid, out string role) || role != AMarkerRole || string.IsNullOrEmpty(cid)) return false;
                cameraUniqueId = cid;
                return true;
            }
            catch { return false; }
        }

        private static List<Element> CollectTagged(Document doc)
        {
            if (Schema.Lookup(SchemaGuid) == null) return new List<Element>();
            return new FilteredElementCollector(doc).WhereElementIsNotElementType()
                .WherePasses(new ExtensibleStorageFilter(SchemaGuid)).ToList();
        }

        // ══════════════════════════════════════════════════════════
        // 族群建立與載入（交易外呼叫）
        // ══════════════════════════════════════════════════════════

        /// <summary>找一般模型族群樣板：依執行中 Revit 版本組路徑，先試 English\Generic Model.rft，再試常見變體。</summary>
        private static string FindGenericModelTemplate(Autodesk.Revit.ApplicationServices.Application app, out string tried)
        {
            string ver = app.VersionNumber;
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Autodesk", "RVT " + ver, "Family Templates");
            var candidates = new List<string>
            {
                Path.Combine(root, "English", "Generic Model.rft"),
                Path.Combine(root, "English", "Metric Generic Model.rft"),
                Path.Combine(root, "English-Imperial", "Generic Model.rft"),
                Path.Combine(root, "English_I", "Generic Model.rft")
            };
            try
            {
                string appTpl = app.FamilyTemplatePath;
                if (!string.IsNullOrEmpty(appTpl))
                {
                    candidates.Add(Path.Combine(appTpl, "Generic Model.rft"));
                    candidates.Add(Path.Combine(appTpl, "Metric Generic Model.rft"));
                }
            }
            catch { }
            tried = string.Join("；", candidates);
            return candidates.FirstOrDefault(File.Exists);
        }

        /// <summary>
        /// 確保文件內有「RevitMCP 相機標記」族群。沒有 → 以一般模型樣板建立族群文件、存成暫存 .rfa、在交易
        /// 「載入相機標記族群」內載入。必須在交易外呼叫。回傳 null＝OK，否則為原因（此時 A 型略過、B 型不受影響）。
        /// </summary>
        internal static string EnsureAMarkerFamily(Document doc, out FamilySymbol symbol)
        {
            symbol = FindAMarkerSymbol(doc);
            if (symbol != null) return null;
            if (doc == null) return "目前沒有開啟中的文件。";
            if (doc.IsModifiable) return "文件正在交易中，無法建立相機標記族群。";

            var app = doc.Application;
            string template = FindGenericModelTemplate(app, out string tried);
            if (template == null)
            {
                string msg = "找不到一般模型族群樣板，A 型相機標記略過（B 型不受影響）。試過：" + tried;
                Logger.Error("相機 A 型標記：" + msg);
                return msg;
            }

            string rfa;
            try { rfa = BuildAMarkerFamilyFile(app, template); }
            catch (Exception ex)
            {
                Logger.Error("相機 A 型標記：建立族群檔失敗", ex);
                CameraManagerDiagnostics.RecordCaught("BuildAMarkerFamilyFile", ex);
                return "建立相機標記族群失敗：" + ex.Message;
            }

            using (Transaction t = TransactionHelper.Begin(doc, AFamilyLoadTransactionName))
            {
                try
                {
                    t.Start();
                    bool ok = doc.LoadFamily(rfa, new AMarkerFamilyLoadOptions(), out Family fam);
                    if (!ok && fam == null) { t.RollBack(); return "載入相機標記族群失敗（LoadFamily 回傳 false）。"; }
                    GetOrCreateProjectMarkerSubcategory(doc);
                    t.Commit();
                }
                catch (Exception ex)
                {
                    try { if (t.HasStarted() && !t.HasEnded()) t.RollBack(); } catch { }
                    Logger.Error("相機 A 型標記：載入族群失敗", ex);
                    return "載入相機標記族群失敗：" + ex.Message;
                }
            }
            symbol = FindAMarkerSymbol(doc);
            Logger.Info($"相機 A 型標記：已由樣板「{template}」建立並載入族群「{AMarkerFamilyName}」（{rfa}）→ symbol={(symbol != null ? symbol.Id.GetIdValue().ToString() : "null")}");
            return symbol != null ? null : "載入後找不到相機標記族群類型。";
        }

        private sealed class AMarkerFamilyLoadOptions : IFamilyLoadOptions
        {
            public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
            {
                overwriteParameterValues = false;
                return true;
            }

            public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues)
            {
                source = FamilySource.Family;
                overwriteParameterValues = false;
                return true;
            }
        }

        /// <summary>用樣板建立族群文件：子類別「相機標記」、實例參數、相機體＋視線箭頭擠出；存成暫存 .rfa 並關閉。回傳檔案路徑。</summary>
        private static string BuildAMarkerFamilyFile(Autodesk.Revit.ApplicationServices.Application app, string templatePath)
        {
            string dir = Path.Combine(Path.GetTempPath(), "RevitMCP");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, AMarkerFamilyName + ".rfa");

            Document fam = app.NewFamilyDocument(templatePath);
            if (fam == null) throw new Exception("NewFamilyDocument 回傳 null（樣板：" + templatePath + "）");
            try
            {
                using (Transaction t = new Transaction(fam, "建立相機標記族群"))
                {
                    t.Start();
                    Category famCat = fam.OwnerFamily.FamilyCategory;
                    Category sub = null;
                    foreach (Category c in famCat.SubCategories) if (c.Name == AMarkerSubcategoryName) { sub = c; break; }
                    if (sub == null) sub = fam.Settings.Categories.NewSubcategory(famCat, AMarkerSubcategoryName);

                    FamilyManager fm = fam.FamilyManager;
                    if (fm.CurrentType == null) fm.NewType("預設");
                    FamilyParameter pL = fm.AddParameter(ParamLeft, GroupTypeId.Geometry, SpecTypeId.Angle, true);
                    FamilyParameter pR = fm.AddParameter(ParamRight, GroupTypeId.Geometry, SpecTypeId.Angle, true);
                    FamilyParameter pF = fm.AddParameter(ParamFar, GroupTypeId.Geometry, SpecTypeId.Length, true);
                    FamilyParameter pP = fm.AddParameter(ParamPitch, GroupTypeId.Geometry, SpecTypeId.Angle, true);
                    fm.AddParameter(ParamCamName, GroupTypeId.IdentityData, SpecTypeId.String.Text, true);
                    try
                    {
                        fm.Set(pL, -30.0 * Math.PI / 180.0);
                        fm.Set(pR, 30.0 * Math.PI / 180.0);
                        fm.Set(pF, CameraMath.MmToFeet(20000.0));
                        fm.Set(pP, 0.0);
                    }
                    catch (Exception ex) { Logger.Error("相機 A 型標記：族群參數預設值設定失敗（不影響）", ex); }

                    double F(double mm) => CameraMath.MmToFeet(mm);
                    // 相機體：x ±60、y −200…0、z −60…+60（mm），眼睛在前緣中心＝族群原點。
                    Extrusion body = NewPrism(fam, new[] { (-60.0, -200.0), (60.0, -200.0), (60.0, 0.0), (-60.0, 0.0) }, F(-60), F(120));
                    body.Subcategory = sub;
                    // 視線箭頭：桿寬 30、頭寬 120、總長 350（mm），厚 20，中心在 z=0。
                    Extrusion arrow = NewPrism(fam, new[] { (-15.0, 0.0), (15.0, 0.0), (15.0, 200.0), (60.0, 200.0), (0.0, 350.0), (-60.0, 200.0), (-15.0, 200.0) }, F(-10), F(20));
                    arrow.Subcategory = sub;
                    t.Commit();
                }
                fam.SaveAs(path, new SaveAsOptions { OverwriteExistingFile = true });
            }
            finally
            {
                try { fam.Close(false); } catch (Exception ex) { Logger.Error("相機 A 型標記：關閉族群文件失敗", ex); }
            }
            return path;
        }

        private static Extrusion NewPrism(Document fam, (double x, double y)[] ptsMm, double zBottomFeet, double heightFeet)
        {
            SketchPlane sp = SketchPlane.Create(fam, Plane.CreateByNormalAndOrigin(XYZ.BasisZ, new XYZ(0, 0, zBottomFeet)));
            var arr = new CurveArray();
            for (int i = 0; i < ptsMm.Length; i++)
            {
                var a = ptsMm[i];
                var b = ptsMm[(i + 1) % ptsMm.Length];
                arr.Append(Line.CreateBound(new XYZ(CameraMath.MmToFeet(a.x), CameraMath.MmToFeet(a.y), zBottomFeet),
                                            new XYZ(CameraMath.MmToFeet(b.x), CameraMath.MmToFeet(b.y), zBottomFeet)));
            }
            var profile = new CurveArrArray();
            profile.Append(arr);
            return fam.FamilyCreate.NewExtrusion(true, profile, sp, heightFeet);
        }

        /// <summary>專案端的一般模型子類別「相機標記」（族群載入時同名合併）：線色＝標記色、材料＝半透明「RevitMCP 相機標記」。須在交易內。</summary>
        private static Category GetOrCreateProjectMarkerSubcategory(Document doc)
        {
            Category gm = Category.GetCategory(doc, BuiltInCategory.OST_GenericModel);
            if (gm == null) return null;
            Category sub = null;
            foreach (Category c in gm.SubCategories) if (c.Name == AMarkerSubcategoryName) { sub = c; break; }
            bool created = false;
            if (sub == null) { sub = doc.Settings.Categories.NewSubcategory(gm, AMarkerSubcategoryName); created = true; }
            try
            {
                sub.LineColor = GetConfiguredMarkerColor();
                if (created || sub.Material == null || sub.Material.Id == ElementId.InvalidElementId)
                {
                    ElementId matId = GetOrCreateMarkerMaterial(doc);
                    if (matId != ElementId.InvalidElementId && doc.GetElement(matId) is Material m) sub.Material = m;
                }
            }
            catch (Exception ex) { Logger.Error("相機 A 型標記：設定子類別線色／材料失敗（不影響標記）", ex); }
            return sub;
        }

        private static ElementId GetOrCreateMarkerMaterial(Document doc)
        {
            Material existing = new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>().FirstOrDefault(m => m.Name == AMarkerMaterialName);
            if (existing != null) return existing.Id;
            try
            {
                ElementId id = Material.Create(doc, AMarkerMaterialName);
                if (doc.GetElement(id) is Material m)
                {
                    m.Color = GetConfiguredMarkerColor();
                    m.Transparency = AMarkerMaterialTransparency;
                }
                return id;
            }
            catch (Exception ex)
            {
                Logger.Error("相機 A 型標記：建立材料失敗（改用預設材料）", ex);
                return ElementId.InvalidElementId;
            }
        }

        // ══════════════════════════════════════════════════════════
        // 同步（相機 → A 標記）
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// 自帶交易「更新相機 A 標記」的版本（一鍵 4 視圖、切換相機標記型式、MCP 用）：確保族群 → 放置／更新這些相機的 A 標記
        /// （cameraIds＝null＝全部透視相機）→ 清掉相機已刪除的 A 標記 → 所有透視圖隱藏子類別。必須在交易外呼叫。
        /// 回傳摘要文字（log／狀態列用）。
        /// </summary>
        internal static string SyncAMarkers(Document doc, ICollection<ElementId> cameraIds, bool placeMissing)
        {
            if (doc == null) return "目前沒有開啟中的文件。";
            string err = EnsureAMarkerFamily(doc, out FamilySymbol sym);
            if (sym == null) return err ?? "找不到相機標記族群。";

            List<Level> levels = CameraMath.GetStoreyLevelsSorted(doc);
            var cams = new List<(View3D cam, CameraParams cp)>();
            IEnumerable<View3D> source = cameraIds == null
                ? new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>()
                : cameraIds.Select(id => doc.GetElement(id) as View3D);
            foreach (View3D v in source)
            {
                if (v == null || v.IsTemplate || !v.IsPerspective || IsSheetPerspectiveCopy(v)) continue; // 出圖用透視複本不是相機
                try { cams.Add((v, ComputeCameraParams(v, levels))); }
                catch (Exception ex) { Logger.Error($"相機 A 型標記：相機「{v.Name}」參數計算失敗（略過）", ex); }
            }

            string log;
            IsUpdatingMarkers = true;
            try
            {
                using (Transaction t = TransactionHelper.Begin(doc, AMarkerTransactionName))
                {
                    t.Start();
                    try
                    {
                        log = SyncAMarkersInTransaction(doc, cams, placeMissing);
                        int orphans = DeleteOrphanTaggedElements(doc);
                        if (orphans > 0) log += $"；清除相機已刪除的標記 {orphans} 個";
                        t.Commit();
                    }
                    catch
                    {
                        try { if (t.HasStarted() && !t.HasEnded()) t.RollBack(); } catch { }
                        throw;
                    }
                }
            }
            finally { IsUpdatingMarkers = false; }
            Logger.Info("相機 A 型標記：" + log);
            return log;
        }

        /// <summary>A 型標記同步本體（須在交易內）：每台相機只留一個族群實例＋一個扇形 DirectShape，位置／旋轉／參數跟著相機；
        /// 並確保所有透視圖隱藏子類別「相機標記」。回傳摘要。</summary>
        private static string SyncAMarkersInTransaction(Document doc, IList<(View3D cam, CameraParams cp)> cams, bool placeMissing)
        {
            FamilySymbol sym = FindAMarkerSymbol(doc);
            if (sym == null) return "族群未載入，略過";
            if (!sym.IsActive) { sym.Activate(); doc.Regenerate(); }
            Category sub = GetOrCreateProjectMarkerSubcategory(doc);
            ElementId gsId = ElementId.InvalidElementId;
            try { gsId = sub?.GetGraphicsStyle(GraphicsStyleType.Projection)?.Id ?? ElementId.InvalidElementId; } catch { }
            ElementId matId = GetOrCreateMarkerMaterial(doc);

            var instByCam = new Dictionary<string, List<FamilyInstance>>(StringComparer.Ordinal);
            var fanByCam = new Dictionary<string, List<DirectShape>>(StringComparer.Ordinal);
            foreach (Element e in CollectTagged(doc))
            {
                if (!TryReadLink(e, out string cid, out string role) || string.IsNullOrEmpty(cid)) continue;
                if (role == AMarkerRole && e is FamilyInstance fi)
                {
                    if (!instByCam.TryGetValue(cid, out var l)) instByCam[cid] = l = new List<FamilyInstance>();
                    l.Add(fi);
                }
                else if (role == AFanRole && e is DirectShape ds)
                {
                    if (!fanByCam.TryGetValue(cid, out var l)) fanByCam[cid] = l = new List<DirectShape>();
                    l.Add(ds);
                }
            }

            int placed = 0, moved = 0, same = 0, dupDeleted = 0, fans = 0, failed = 0;
            foreach (var (cam, cp) in cams)
            {
                string cid = cam.UniqueId;
                try
                {
                    if (cp?.Geom?.LevelBelow == null) continue;
                    // ① 族群實例
                    instByCam.TryGetValue(cid, out var insts);
                    FamilyInstance inst = insts?.FirstOrDefault();
                    if (insts != null && insts.Count > 1)
                    {
                        doc.Delete(insts.Skip(1).Select(x => x.Id).ToList());
                        dupDeleted += insts.Count - 1;
                    }
                    XYZ eye = cp.Geom.Eye;
                    double targetRot = Math.Atan2(-cp.ForwardHoriz.X, cp.ForwardHoriz.Y);
                    if (inst == null)
                    {
                        if (!placeMissing) continue;
                        inst = doc.Create.NewFamilyInstance(eye, sym, cp.Geom.LevelBelow, StructuralType.NonStructural);
                        TagLink(inst, cid, AMarkerRole);
                        doc.Regenerate();
                        placed++;
                    }
                    if (MoveRotateInstance(doc, inst, eye, targetRot)) moved++; else same++;
                    SetParamSafe(inst, ParamLeft, cp.LeftRad);
                    SetParamSafe(inst, ParamRight, cp.RightRad);
                    SetParamSafe(inst, ParamFar, cp.FarClipFeet);
                    SetParamSafe(inst, ParamPitch, cp.Geom.PitchDeg * Math.PI / 180.0);
                    SetParamSafe(inst, ParamCamName, cam.Name);

                    // ② 視野扇形 DirectShape
                    fanByCam.TryGetValue(cid, out var fanList);
                    DirectShape fan = fanList?.FirstOrDefault();
                    if (fanList != null && fanList.Count > 1)
                    {
                        doc.Delete(fanList.Skip(1).Select(x => x.Id).ToList());
                        dupDeleted += fanList.Count - 1;
                    }
                    Solid solid = BuildFanSolid(eye, cp.ForwardHoriz, cp.LeftRad, cp.RightRad, cp.FanRadiusFeet, matId, gsId);
                    if (solid != null)
                    {
                        if (fan == null)
                        {
                            fan = DirectShape.CreateElement(doc, new ElementId(BuiltInCategory.OST_GenericModel));
                            try { fan.ApplicationId = "RevitMCP"; fan.ApplicationDataId = "CameraFan:" + cid; } catch { }
                            TagLink(fan, cid, AFanRole);
                        }
                        else if (fan.Pinned) fan.Pinned = false;
                        fan.SetShape(new List<GeometryObject> { solid });
                        try { fan.Pinned = true; } catch { }
                        fans++;
                    }
                }
                catch (Exception ex)
                {
                    failed++;
                    Logger.Error($"相機 A 型標記：相機「{cam?.Name}」同步失敗（略過）", ex);
                }
            }

            int hidden = HideAMarkersInPerspectiveViews(doc, sub, out int hideFail);
            return $"A 標記 放置 {placed}、移動/旋轉 {moved}、不變 {same}、扇形 {fans}、刪除重複 {dupDeleted}、失敗 {failed}；透視圖隱藏子類別 新增 {hidden} 張（失敗 {hideFail}）";
        }

        private static void SetParamSafe(Element e, string name, double v)
        {
            try { Parameter p = e.LookupParameter(name); if (p != null && !p.IsReadOnly && p.StorageType == StorageType.Double) p.Set(v); } catch { }
        }

        private static void SetParamSafe(Element e, string name, string v)
        {
            try { Parameter p = e.LookupParameter(name); if (p != null && !p.IsReadOnly && p.StorageType == StorageType.String) p.Set(v ?? ""); } catch { }
        }

        /// <summary>把實例移到眼睛、轉到 targetRot（繞 Z，逆時針，族群 +Y＝前方）。回傳是否有變更。</summary>
        private static bool MoveRotateInstance(Document doc, FamilyInstance inst, XYZ eye, double targetRot)
        {
            if (!(inst.Location is LocationPoint lp)) return false;
            bool changed = false;
            XYZ cur = lp.Point;
            XYZ delta = eye - cur;
            if (delta.GetLength() > CameraMath.MmToFeet(0.5))
            {
                ElementTransformUtils.MoveElement(doc, inst.Id, delta);
                changed = true;
            }
            double curRot = 0;
            try { curRot = lp.Rotation; } catch { }
            double d = NormalizeAngle(targetRot - curRot);
            if (Math.Abs(d) > 1e-4)
            {
                XYZ p = (inst.Location as LocationPoint)?.Point ?? eye;
                ElementTransformUtils.RotateElement(doc, inst.Id, Line.CreateBound(p, p + XYZ.BasisZ), d);
                changed = true;
            }
            return changed;
        }

        private static double NormalizeAngle(double a)
        {
            while (a > Math.PI) a -= 2 * Math.PI;
            while (a <= -Math.PI) a += 2 * Math.PI;
            return a;
        }

        private static Solid BuildFanSolid(XYZ eye, XYZ forwardHoriz, double leftRad, double rightRad, double radiusFeet, ElementId matId, ElementId gsId)
        {
            try
            {
                if (!(radiusFeet > 1e-3) || rightRad - leftRad < 1e-3 || rightRad - leftRad > 2 * Math.PI - 1e-3) return null;
                double th = CameraMath.MmToFeet(AFanThicknessMm);
                XYZ o = new XYZ(eye.X, eye.Y, eye.Z - th / 2.0);
                XYZ dl = PlanEdgeDir(forwardHoriz, leftRad), dr = PlanEdgeDir(forwardHoriz, rightRad), dm = PlanEdgeDir(forwardHoriz, (leftRad + rightRad) / 2.0);
                XYZ le = o + dl * radiusFeet, re = o + dr * radiusFeet, me = o + dm * radiusFeet;
                var loop = new CurveLoop();
                loop.Append(Line.CreateBound(o, re));
                loop.Append(Arc.Create(re, le, me));
                loop.Append(Line.CreateBound(le, o));
                if (!loop.IsCounterclockwise(XYZ.BasisZ)) loop.Flip();
                var opts = new SolidOptions(matId ?? ElementId.InvalidElementId, gsId ?? ElementId.InvalidElementId);
                return GeometryCreationUtilities.CreateExtrusionGeometry(new List<CurveLoop> { loop }, XYZ.BasisZ, th, opts);
            }
            catch (Exception ex)
            {
                Logger.Error("相機 A 型標記：扇形實體建立失敗（只放相機體）", ex);
                return null;
            }
        }

        /// <summary>所有透視圖隱藏一般模型子類別「相機標記」（相機永遠看不到標記）。已隱藏者不動；視圖樣板控制 V/G 等失敗只記 log。</summary>
        private static int HideAMarkersInPerspectiveViews(Document doc, Category sub, out int failed)
        {
            failed = 0;
            if (sub == null) return 0;
            int n = 0;
            var failNames = new List<string>();
            foreach (View3D v in new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>().Where(v => !v.IsTemplate && v.IsPerspective))
            {
                try
                {
                    if (!v.CanCategoryBeHidden(sub.Id)) { failed++; failNames.Add(v.Name + "(不可隱藏)"); continue; }
                    if (v.GetCategoryHidden(sub.Id)) continue;
                    v.SetCategoryHidden(sub.Id, true);
                    n++;
                }
                catch (Exception ex) { failed++; failNames.Add(v.Name + "(" + ex.Message + ")"); }
            }
            if (failNames.Count > 0) Logger.Error("相機 A 型標記：以下透視圖無法隱藏「相機標記」子類別（可能是視圖樣板控制 V/G）：" + string.Join("、", failNames.Take(10)));
            return n;
        }

        /// <summary>刪除「相機已不存在」的標記（A 實例、A 扇形、總覽／Key Plan 的 B 標記與名稱）。須在交易內。回傳刪除數。</summary>
        private static int DeleteOrphanTaggedElements(Document doc)
        {
            var roles = new HashSet<string>(new[] { AMarkerRole, AFanRole, "MarkerPlan", "MarkerSection", MarkerNameRole }, StringComparer.Ordinal);
            var ids = new List<ElementId>();
            foreach (Element e in CollectTagged(doc))
            {
                if (!TryReadLink(e, out string cid, out string role) || !roles.Contains(role) || string.IsNullOrEmpty(cid)) continue;
                if (doc.GetElement(cid) is View3D v && v.IsPerspective && !v.IsTemplate) continue;
                if (e is DirectShape ds && ds.Pinned) { try { ds.Pinned = false; } catch { } }
                ids.Add(e.Id);
            }
            if (ids.Count > 0) doc.Delete(ids);
            return ids.Count;
        }

        /// <summary>相機刪除後（面板去抖排入）：單一交易清掉孤兒標記。交易名稱沿用「更新相機標記」（DocumentChanged 忽略）。</summary>
        internal static int CleanupOrphanMarkers(Document doc)
        {
            if (doc == null || Schema.Lookup(SchemaGuid) == null) return 0;
            int n = 0;
            IsUpdatingMarkers = true;
            try
            {
                using (Transaction t = TransactionHelper.Begin(doc, MarkerUpdateTransactionName))
                {
                    t.Start();
                    try { n = DeleteOrphanTaggedElements(doc); if (n > 0) t.Commit(); else t.RollBack(); }
                    catch { try { if (t.HasStarted() && !t.HasEnded()) t.RollBack(); } catch { } throw; }
                }
            }
            finally { IsUpdatingMarkers = false; }
            if (n > 0) Logger.Info($"相機標記：清除相機已刪除的標記 {n} 個");
            return n;
        }

        /// <summary>⋯ 相機標記改為「B 詳圖」時：刪除所有 A 實例與扇形（族群保留在專案內）。自帶交易。回傳刪除數。</summary>
        internal static int RemoveAllAMarkers(Document doc)
        {
            if (doc == null || Schema.Lookup(SchemaGuid) == null) return 0;
            var ids = new List<ElementId>();
            foreach (Element e in CollectTagged(doc))
            {
                if (!TryReadLink(e, out _, out string role) || (role != AMarkerRole && role != AFanRole)) continue;
                ids.Add(e.Id);
            }
            if (ids.Count == 0) return 0;
            IsUpdatingMarkers = true;
            try
            {
                using (Transaction t = TransactionHelper.Begin(doc, AMarkerTransactionName))
                {
                    t.Start();
                    foreach (DirectShape e in ids.Select(i => doc.GetElement(i)).OfType<DirectShape>()) { try { if (e.Pinned) e.Pinned = false; } catch { } }
                    doc.Delete(ids);
                    t.Commit();
                }
            }
            finally { IsUpdatingMarkers = false; }
            Logger.Info($"相機 A 型標記：切回 B 詳圖，刪除 A 實例／扇形 {ids.Count} 個");
            return ids.Count;
        }

        /// <summary>
        /// ⋯ 相機標記型式變更後（面板佇列呼叫）：A 啟用 → 確保族群並同步全部相機；A 關閉 → 刪除 A 標記。
        /// 接著對全部相機跑一次「更新相機標記」，讓 Key Plan／總覽的 B 標記依新設定重畫或刪除。回傳狀態列文字。
        /// </summary>
        internal static string ApplyMarkerModeChange(Document doc)
        {
            if (doc == null) return "目前沒有開啟中的文件。";
            CameraMarkerMode mode = GetMarkerMode();
            string aMsg;
            if (MarkerModeIncludesA()) aMsg = SyncAMarkers(doc, null, placeMissing: true);
            else aMsg = $"已刪除 A 標記 {RemoveAllAMarkers(doc)} 個";
            List<ElementId> all = new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>()
                .Where(v => !v.IsTemplate && v.IsPerspective && !IsSheetPerspectiveCopy(v)).Select(v => v.Id).ToList();
            int n = all.Count > 0 ? UpdateMarkersForCameras(doc, all) : 0;
            string label = mode == CameraMarkerMode.A ? "A 模型" : mode == CameraMarkerMode.AB ? "A+B" : "B 詳圖";
            return $"相機標記＝{label}：{aMsg}；已重畫 {n} 台相機的 Key Plan／總覽標記。";
        }

        // ══════════════════════════════════════════════════════════
        // 反向同步（拖 A 標記 → 相機）
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// 使用者移動／旋轉了 A 標記實例（面板去抖排入）：相機眼睛 XY＝實例位置、眼睛 Z 不變（維持離樓板高度）、
        /// 水平角＝實例旋轉、俯仰角不變。單一交易「由標記更新相機」。差異 &lt; 1 mm 且 &lt; 0.1° 的略過（避免與相機→標記同步互相觸發）。
        /// 回傳被更新的相機 Id。B 型詳圖標記不是群組、不能整組拖曳，不支援反向同步（D-027）。
        /// </summary>
        internal static List<ElementId> SyncCamerasFromAMarkers(Document doc, ICollection<ElementId> instanceIds, out string log)
        {
            var changed = new List<ElementId>();
            log = "";
            if (doc == null || instanceIds == null || instanceIds.Count == 0) return changed;
            List<Level> levels = CameraMath.GetStoreyLevelsSorted(doc);
            var plan = new List<(View3D cam, ViewOrientation3D o, string note)>();
            foreach (ElementId id in instanceIds)
            {
                Element e = doc.GetElement(id);
                if (!IsAMarkerInstance(e, out string cid)) continue;
                if (!(doc.GetElement(cid) is View3D cam) || cam.IsTemplate || !cam.IsPerspective) continue;
                if (!(((FamilyInstance)e).Location is LocationPoint lp)) continue;
                CameraMath.CameraGeometry g = CameraMath.ComputeGeometry(cam, levels);
                XYZ p = lp.Point;
                double rot = 0;
                try { rot = lp.Rotation; } catch { }
                XYZ fh = new XYZ(-Math.Sin(rot), Math.Cos(rot), 0);
                double yaw = CameraMath.ComputeHorizontalAngleDeg(fh);
                double curYaw = CameraMath.ComputeHorizontalAngleDeg(g.Forward);
                double dYaw = Math.Abs(NormalizeAngle((yaw - curYaw) * Math.PI / 180.0)) * 180.0 / Math.PI;
                double dXY = Math.Sqrt((p.X - g.Eye.X) * (p.X - g.Eye.X) + (p.Y - g.Eye.Y) * (p.Y - g.Eye.Y));
                if (dXY < CameraMath.MmToFeet(1.0) && dYaw < 0.1) continue;
                XYZ eye = new XYZ(p.X, p.Y, g.Eye.Z);
                XYZ fwd = CameraMath.DirectionFromAngles(yaw, g.PitchDeg);
                plan.Add((cam, new ViewOrientation3D(eye, CameraMath.BuildLevelUp(fwd), fwd),
                    $"「{cam.Name}」移 {CameraMath.FeetToMm(dXY):F0}mm、水平角 {curYaw:F1}°→{yaw:F1}°"));
            }
            if (plan.Count == 0) { log = "標記與相機一致，不需更新"; return changed; }

            using (Transaction t = TransactionHelper.Begin(doc, MarkerToCameraTransactionName))
            {
                t.Start();
                try
                {
                    foreach (var x in plan)
                    {
                        x.cam.SetOrientation(x.o);
                        changed.Add(x.cam.Id);
                    }
                    t.Commit();
                }
                catch
                {
                    try { if (t.HasStarted() && !t.HasEnded()) t.RollBack(); } catch { }
                    throw;
                }
            }
            log = string.Join("；", plan.Select(x => x.note));
            Logger.Info("由標記更新相機：" + log);
            return changed;
        }
    }
}
