using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;

namespace RevitMCP.Core
{
    /// <summary>
    /// 2026-09-25（MCP 自測：室內相機被判成室外、樓層被判成女兒牆）：眼睛所在房間的穩健搜尋＋室內相機的樓層規則。
    ///
    /// 舊做法：doc.GetRoomAtPoint 依序試「眼睛高度／下方樓層 +1 m／+0.3 m」。實測失敗情境：
    ///   ① 眼睛略低於房間底（相機放在另一個 Level、眼睛 −150 mm，而房間底在 0 mm）；
    ///   ② 下方「建築樓層」不是房間所在樓層（夾層、女兒牆被標成 Building Story），+1 m／+0.3 m 落到別層；
    ///   ③ 眼睛略高於房間上限（挑高／閣樓相機）。
    /// 新做法（純查詢）：
    ///   1. 候選＝已放置房間（面積 &gt; 0、與透視圖同一個 Phase）中，BoundingBox XY 含眼睛者（便宜的前篩），
    ///      再以房間邊界線段（Tessellate，所有迴圈奇偶規則，含中空）做點在多邊形內判定。
    ///   2. 其中垂直範圍 [底, 頂] 含眼睛高度者優先（多個時取底最高者＝最內層）；
    ///      都不含 → 取垂直距離最近、且 ≤ 2.0 m 者（例：眼睛比房間底低 150 mm）。
    ///   3. 同 Phase 找不到任何 XY 候選時，改不分 Phase 再找一次並記 log（透視圖 Phase 與房間 Phase 不同的模型）。
    ///   底＝房間 Level 高程＋底部偏移；頂＝底＋UnboundedHeight（取不到用 BoundingBox 頂）。
    /// 室內相機的樓層＝房間的 Level（room.LevelId），離樓板高度以該樓層計算；室外維持「眼睛正下方的建築樓層」。
    /// </summary>
    internal static partial class CameraKeyPlanService
    {
        private const double EyeRoomVerticalToleranceMm = 2000.0;
        private static readonly HashSet<string> _loggedEyeRoomChoices = new HashSet<string>(StringComparer.Ordinal);

        // ══════════════════════════════════════════════════════════
        // 2026-09-26 第四輪（使用者：「每點一個視圖都會卡一下」）：房間／樓層判定快取
        //   每台相機以「文件＋相機 Id」為鍵，值鍵＝模型 epoch＋眼睛座標（四捨五入到 1 mm）＋Phase。
        //   相機沒動、模型（相機以外的元素）沒變 → 直接用上次的房間與樓層，不再掃描全部房間做多邊形判定。
        //   模型 epoch 由 Application 的 DocumentChanged 在「相機以外」的元素變更時 +1（本工具自己的交易不算）。
        // ══════════════════════════════════════════════════════════
        private static int _modelEpoch;
        /// <summary>模型 epoch（相機以外的元素變更次數）；底圖點陣快取也用它判斷是否過期。任何執行緒可讀。</summary>
        internal static int ModelEpoch => Volatile.Read(ref _modelEpoch);

        /// <summary>DocumentChanged（非本工具交易、且變更的不只是透視相機）時呼叫：房間／樓層快取與底圖快取全部失效。</summary>
        internal static void BumpModelEpoch() => Interlocked.Increment(ref _modelEpoch);

        /// <summary>
        /// 2026-09-26 稽核：文件關閉／開啟時清掉所有「以文件路徑＋標題為鍵」的快取。
        /// 舊版只在 DocumentChanged 才 bump epoch → 關檔再開同一檔（沒有任何編輯）時快取命中，
        /// 回傳的 RoomCtx.Room 是已關閉文件的 Room → 讀 .Id 丟 InvalidObjectException（一鍵 4 視圖／標記全失敗）。
        /// 兩份未存檔的「專案1」同名時也會互相命中。只在 API context（DocumentClosing／DocumentOpened）呼叫。
        /// </summary>
        internal static void ResetDocumentCaches(string reason)
        {
            try
            {
                BumpModelEpoch();
                _storeyCache.Clear();
                _planLevelsKey = null;
                _planLevels = new HashSet<long>();
                _raycastViewKey = null;
                _raycastViewId = null;
                ClearPoseRecords();
                _overviewStoreyHash.Clear();
                Logger.Info("相機管理：文件" + reason + " → 清除房間／樓層、姿態、總覽快取。");
            }
            catch (Exception ex) { Logger.Error("相機管理：清除文件快取失敗", ex); }
        }

        private sealed class StoreyCacheEntry
        {
            public string ValueKey;
            public RoomCtx Room;
            public ElementId LevelId;
            public string Source;
            public double? SurfaceZ;
            public bool SurfaceResolved;
            public string SurfaceLog;
            public string StoreyLog;
        }
        private static readonly Dictionary<string, StoreyCacheEntry> _storeyCache = new Dictionary<string, StoreyCacheEntry>(StringComparer.Ordinal);
        private static int _storeyCacheHits, _storeyCacheMisses;

        /// <summary>取出並歸零「房間／樓層快取」命中／未命中次數（ReadSnapshot 計時用）。</summary>
        internal static string TakeStoreyCacheStats()
        {
            string s = $"房間/樓層快取 命中 {_storeyCacheHits}、重算 {_storeyCacheMisses}";
            _storeyCacheHits = 0; _storeyCacheMisses = 0;
            return s;
        }

        private static bool IsValidRevitObject(Element e)
        {
            try { return e != null && e.IsValidObject; } catch { return false; }
        }

        private static string StoreyDocKey(Document doc)
        {
            try { return (doc?.PathName ?? "") + "|" + (doc?.Title ?? ""); } catch { return "?"; }
        }

        private static string StoreyValueKey(View3D camera, XYZ eye)
        {
            string phase = "";
            try { phase = camera.get_Parameter(BuiltInParameter.VIEW_PHASE)?.AsElementId()?.GetIdValue().ToString() ?? ""; } catch { }
            return $"{ModelEpoch}|{Math.Round(CameraMath.FeetToMm(eye.X))}|{Math.Round(CameraMath.FeetToMm(eye.Y))}|{Math.Round(CameraMath.FeetToMm(eye.Z))}|{phase}";
        }

        // ── 有非樣板樓層平面的 Level（多層房間／室外站立面規則用），每個 epoch 算一次 ──
        private static string _planLevelsKey;
        private static HashSet<long> _planLevels = new HashSet<long>();

        private static HashSet<long> GetLevelIdsWithFloorPlan(Document doc)
        {
            string key = StoreyDocKey(doc) + "|" + ModelEpoch;
            if (key == _planLevelsKey) return _planLevels;
            var set = new HashSet<long>();
            try
            {
                foreach (ViewPlan vp in new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>())
                {
                    try
                    {
                        if (vp.IsTemplate || vp.ViewType != ViewType.FloorPlan) continue;
                        Level gl = vp.GenLevel;
                        if (gl != null) set.Add(gl.Id.GetIdValue());
                    }
                    catch { }
                }
            }
            catch (Exception ex) { Logger.Error("相機管理：讀取有樓層平面的 Level 失敗（多層房間規則改用房間 Level）", ex); }
            _planLevels = set;
            _planLevelsKey = key;
            return set;
        }

        /// <summary>
        /// CameraMath.ComputeGeometry 呼叫：搜尋眼睛所在房間並存進 geom；決定相機的樓層（清單、③ 位置圖跟隨、Key Plan、
        /// 樓層總覽分組、剖面樓層記號、A 標記都經由這裡取得同一個值）。2026-09-26 第四輪規則（D-040）：
        ///   室內：樓層＝{room.Level} ∪ {房間垂直範圍內、且至少有一張非樣板樓層平面的 Level} 之中，高程 ≤ 眼睛 − 300 mm 的最高者；
        ///         都不符合 → room.Level（例：大廳樓梯房間掛在 L1、往上跨到 L3，眼睛在 L3 → 樓層＝L3）。
        ///   室外：眼睛正下方往下打（樓板／屋頂／地形／基地），站立面 Z → 樓層＝有平面、高程 ≤ Z + 300 mm 的最高 Level；
        ///         沒有 → 高程最接近 Z 的 Level；沒打到 → 眼睛下方最近的建築樓層（舊規則）。
        /// 結果以「相機＋眼睛位置＋模型 epoch」快取。
        /// </summary>
        internal static void ApplyIndoorStorey(View3D camera, CameraMath.CameraGeometry geom)
        {
            if (camera == null || geom == null || geom.Eye == null) return;
            Document doc = camera.Document;
            string ck = StoreyDocKey(doc) + "|" + camera.Id.GetIdValue();
            string vk = StoreyValueKey(camera, geom.Eye);
            // 2026-09-26 稽核：快取的 Room 可能屬於已關閉的文件或已刪除（Undo 不一定 bump epoch）→ 失效就重算。
            if (_storeyCache.TryGetValue(ck, out StoreyCacheEntry hit) && hit.ValueKey == vk &&
                (hit.Room?.Room == null || IsValidRevitObject(hit.Room.Room)))
            {
                _storeyCacheHits++;
                ApplyStoreyEntry(doc, geom, hit);
                return;
            }
            _storeyCacheMisses++;

            RoomCtx rc = null;
            try { rc = FindEyeRoomCore(doc, camera, geom); }
            catch (Exception ex)
            {
                Logger.Error("相機管理：眼睛所在房間搜尋失敗", ex);
                rc = new RoomCtx { Log = "failed: " + ex.Message };
            }
            var entry = new StoreyCacheEntry { ValueKey = vk, Room = rc, LevelId = geom.LevelBelow?.Id, Source = "elevation" };
            try
            {
                if (rc?.Room is Room room)
                {
                    Level roomLevel = null;
                    try { roomLevel = room.LevelId != null && room.LevelId != ElementId.InvalidElementId ? doc.GetElement(room.LevelId) as Level : null; }
                    catch { roomLevel = null; }
                    if (roomLevel != null)
                    {
                        Level storey = PickIndoorStorey(doc, roomLevel, rc, geom.Eye.Z, out string slog);
                        entry.LevelId = storey.Id;
                        entry.Source = storey.Id == roomLevel.Id ? "room" : "roomSpan";
                        entry.StoreyLog = slog;
                        rc.Log += $" storey=「{storey.Name}」（{slog}）" +
                                  (geom.LevelBelow == null || geom.LevelBelow.Id != storey.Id ? $"（高程判定原為「{geom.LevelBelow?.Name ?? "(無)"}」）" : "");
                    }
                }
                else
                {
                    double? surf = EnsureStandingSurface(doc, geom);
                    if (surf.HasValue)
                    {
                        Level st = PickOutdoorStorey(doc, surf.Value, out string slog);
                        if (st != null) { entry.LevelId = st.Id; entry.Source = "surface"; entry.StoreyLog = slog; }
                        else entry.StoreyLog = "站立面找不到樓層 → 眼睛下方建築樓層";
                    }
                    else entry.StoreyLog = "往下沒打到站立面 → 眼睛下方建築樓層（" + (geom.StandingSurfaceLog ?? "") + "）";
                }
            }
            catch (Exception ex)
            {
                Logger.Error("相機管理：樓層判定失敗（沿用眼睛下方建築樓層）", ex);
                entry.StoreyLog = "樓層判定失敗：" + ex.Message;
            }
            entry.SurfaceZ = geom.StandingSurfaceZ;
            entry.SurfaceResolved = geom.StandingSurfaceResolved;
            entry.SurfaceLog = geom.StandingSurfaceLog;

            _storeyCache.TryGetValue(ck, out StoreyCacheEntry prev);
            _storeyCache[ck] = entry;
            ApplyStoreyEntry(doc, geom, entry);
            if (prev == null || prev.LevelId?.GetIdValue() != entry.LevelId?.GetIdValue())
                Logger.Info($"相機管理：「{camera.Name}」樓層＝「{geom.LevelBelow?.Name ?? "(無)"}」（來源 {entry.Source}；{entry.StoreyLog}" +
                            (entry.SurfaceResolved ? $"；站立面 {entry.SurfaceLog}" : "") + "）");
        }

        private static void ApplyStoreyEntry(Document doc, CameraMath.CameraGeometry geom, StoreyCacheEntry e)
        {
            geom.EyeRoom = e.Room;
            geom.EyeRoomResolved = true;
            geom.StoreySource = e.Source ?? "elevation";
            geom.StoreyLog = e.StoreyLog;
            if (e.SurfaceResolved)
            {
                geom.StandingSurfaceResolved = true;
                geom.StandingSurfaceZ = e.SurfaceZ;
                geom.StandingSurfaceLog = e.SurfaceLog;
            }
            Level lv = null;
            try { lv = e.LevelId != null ? doc.GetElement(e.LevelId) as Level : null; } catch { lv = null; }
            if (lv == null) return;
            geom.LevelBelow = lv;
            geom.AboveLevelBelowMm = Math.Round(geom.EyeZMm - CameraMath.FeetToMm(lv.ProjectElevation), 1);
        }

        private const double StoreyEyeClearanceMm = 300.0;

        /// <summary>多層房間：{room.Level} ∪ {房間垂直範圍 (底, 頂] 內且有樓層平面的 Level} 中，高程 ≤ 眼睛 − 300 mm 的最高者；都不符合＝room.Level。</summary>
        private static Level PickIndoorStorey(Document doc, Level roomLevel, RoomCtx rc, double eyeZ, out string log)
        {
            double limit = eyeZ - CameraMath.MmToFeet(StoreyEyeClearanceMm);
            var cands = new List<Level> { roomLevel };
            string span = "無房間垂直範圍";
            if (rc?.SpanBaseZ != null && rc.SpanTopZ != null)
            {
                double b = rc.SpanBaseZ.Value, t = rc.SpanTopZ.Value;
                span = $"房間範圍 [{CameraMath.FeetToMm(b):F0},{CameraMath.FeetToMm(t):F0}]mm";
                HashSet<long> withPlan = GetLevelIdsWithFloorPlan(doc);
                foreach (Level l in CameraMath.GetLevelsSorted(doc))
                {
                    if (l.Id == roomLevel.Id) continue;
                    double z = l.ProjectElevation;
                    if (z > b + 1e-6 && z <= t + 1e-6 && withPlan.Contains(l.Id.GetIdValue())) cands.Add(l);
                }
            }
            Level pick = cands.Where(l => l.ProjectElevation <= limit + 1e-9).OrderByDescending(l => l.ProjectElevation).FirstOrDefault();
            log = $"{span}；候選 {string.Join("、", cands.Select(l => l.Name))}；取 ≤ 眼睛−{StoreyEyeClearanceMm:F0}mm 最高者" +
                  (pick == null ? " → 都不符合，用房間 Level" : $"「{pick.Name}」");
            return pick ?? roomLevel;
        }

        /// <summary>室外：有樓層平面、高程 ≤ 站立面 + 300 mm 的最高 Level；沒有 → 高程最接近站立面的 Level。</summary>
        private static Level PickOutdoorStorey(Document doc, double surfaceZ, out string log)
        {
            List<Level> all = CameraMath.GetLevelsSorted(doc);
            if (all.Count == 0) { log = "沒有任何 Level"; return null; }
            HashSet<long> withPlan = GetLevelIdsWithFloorPlan(doc);
            double lim = surfaceZ + CameraMath.MmToFeet(StoreyEyeClearanceMm);
            Level pick = all.Where(l => withPlan.Contains(l.Id.GetIdValue()) && l.ProjectElevation <= lim + 1e-9).LastOrDefault();
            if (pick != null) { log = $"站立面 {CameraMath.FeetToMm(surfaceZ):F0}mm → 有平面且 ≤ 站立面+300mm 的最高 Level「{pick.Name}」"; return pick; }
            pick = all.OrderBy(l => Math.Abs(l.ProjectElevation - surfaceZ)).First();
            log = $"站立面 {CameraMath.FeetToMm(surfaceZ):F0}mm → 沒有符合的有平面 Level，取高程最接近者「{pick.Name}」";
            return pick;
        }

        // ── 站立面射線（ReferenceIntersector，往下）──
        private static string _raycastViewKey;
        private static ElementId _raycastViewId;

        /// <summary>找一張可做 ReferenceIntersector 的 3D 視圖：非樣板、非透視、沒開剖面框（優先名稱「{3D}」）。每個 epoch 找一次。</summary>
        private static View3D FindRaycastView(Document doc)
        {
            string key = StoreyDocKey(doc) + "|" + ModelEpoch;
            if (key == _raycastViewKey && _raycastViewId != null) return doc.GetElement(_raycastViewId) as View3D;
            View3D pick = null;
            try
            {
                List<View3D> all = new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>()
                    .Where(v => !v.IsTemplate && !v.IsPerspective).ToList();
                pick = all.FirstOrDefault(v => !v.IsSectionBoxActive && v.Name != null && v.Name.StartsWith("{3D", StringComparison.Ordinal))
                       ?? all.FirstOrDefault(v => !v.IsSectionBoxActive)
                       ?? all.FirstOrDefault();
            }
            catch (Exception ex) { Logger.Error("相機管理：找站立面射線用 3D 視圖失敗", ex); }
            _raycastViewKey = key;
            _raycastViewId = pick?.Id;
            return pick;
        }

        /// <summary>眼睛正下方的站立面 Z（樓板／屋頂／地形／基地／建築墊，含連結模型）；同一份 geom 只打一次。</summary>
        private static double? EnsureStandingSurface(Document doc, CameraMath.CameraGeometry geom)
        {
            if (geom == null || geom.Eye == null) return null;
            if (geom.StandingSurfaceResolved) return geom.StandingSurfaceZ;
            geom.StandingSurfaceResolved = true;
            try
            {
                View3D v3 = FindRaycastView(doc);
                if (v3 == null) { geom.StandingSurfaceLog = "沒有可用的 3D 視圖（非透視、非樣板）"; return null; }
                var cats = new List<BuiltInCategory> { BuiltInCategory.OST_Floors, BuiltInCategory.OST_Roofs, BuiltInCategory.OST_Topography, BuiltInCategory.OST_Site, BuiltInCategory.OST_BuildingPad };
                if (Enum.TryParse("OST_Toposolid", out BuiltInCategory topo)) cats.Add(topo);
                var ri = new ReferenceIntersector(new ElementMulticategoryFilter(cats), FindReferenceTarget.Face, v3) { FindReferencesInRevitLinks = true };
                ReferenceWithContext rwc = ri.FindNearest(geom.Eye, XYZ.BasisZ.Negate());
                if (rwc == null)
                {
                    geom.StandingSurfaceLog = $"往下沒打到（3D 視圖「{v3.Name}」）";
                    return null;
                }
                double z = geom.Eye.Z - rwc.Proximity;
                string what = "?";
                try
                {
                    Reference r = rwc.GetReference();
                    Element el = doc.GetElement(r.ElementId);
                    bool linked = r.LinkedElementId != null && r.LinkedElementId != ElementId.InvalidElementId;
                    what = linked ? $"連結「{el?.Name}」內元素 {r.LinkedElementId.GetIdValue()}" : $"{el?.Category?.Name ?? "?"} {r.ElementId.GetIdValue()}「{el?.Name}」";
                }
                catch { }
                geom.StandingSurfaceZ = z;
                geom.StandingSurfaceLog = $"Z={CameraMath.FeetToMm(z):F0}mm（眼睛下 {CameraMath.FeetToMm(rwc.Proximity):F0}mm，{what}，3D 視圖「{v3.Name}」）";
                return z;
            }
            catch (Exception ex)
            {
                geom.StandingSurfaceLog = "射線失敗：" + ex.Message;
                Logger.Error("相機管理：站立面射線失敗（沿用舊規則）", ex);
                return null;
            }
        }

        /// <summary>
        /// 眼睛所在房間。geom 已搜尋過（ComputeGeometry）就直接用結果，不重搜。找不到房間回傳 Room==null 的 RoomCtx
        /// （Log 說明候選與原因）。輪廓＝房間邊界線段（Tessellate）；取不到邊界就用房間 BoundingBox 四角。
        /// </summary>
        private static RoomCtx FindEyeRoom(Document doc, View3D camera, CameraMath.CameraGeometry geom)
        {
            if (geom != null && geom.EyeRoomResolved) return geom.EyeRoom ?? new RoomCtx { Log = "none (resolved, null)" };
            RoomCtx rc = FindEyeRoomCore(doc, camera, geom);
            if (geom != null) { geom.EyeRoom = rc; geom.EyeRoomResolved = true; }
            return rc;
        }

        private static ElementId GetRoomPhaseId(Element room)
        {
            foreach (string n in new[] { "ROOM_PHASE_ID", "ROOM_PHASE" })
            {
                try
                {
                    if (!Enum.TryParse(n, out BuiltInParameter bip)) continue;
                    Parameter p = room.get_Parameter(bip);
                    if (p != null && p.StorageType == StorageType.ElementId)
                    {
                        ElementId id = p.AsElementId();
                        if (id != null && id != ElementId.InvalidElementId) return id;
                    }
                }
                catch { }
            }
            return null;
        }

        /// <summary>房間邊界迴圈（XY，已 Tessellate）；讀不到回空清單。</summary>
        private static List<List<XYZ>> GetRoomLoopsXY(Room room)
        {
            var loops = new List<List<XYZ>>();
            IList<IList<BoundarySegment>> segs = room.GetBoundarySegments(new SpatialElementBoundaryOptions());
            if (segs == null) return loops;
            foreach (IList<BoundarySegment> loop in segs)
            {
                var pts = new List<XYZ>();
                foreach (BoundarySegment seg in loop)
                {
                    Curve c = seg?.GetCurve();
                    if (c == null) continue;
                    IList<XYZ> tp = c.Tessellate();
                    for (int k = 0; k < tp.Count; k++)
                    {
                        XYZ q = new XYZ(tp[k].X, tp[k].Y, 0);
                        if (pts.Count == 0 || !pts[pts.Count - 1].IsAlmostEqualTo(q)) pts.Add(q);
                    }
                }
                if (pts.Count >= 3) loops.Add(pts);
            }
            return loops;
        }

        /// <summary>奇偶規則點在多邊形內（所有迴圈一起算，外框＋中空都正確）。</summary>
        private static bool PointInLoops(List<List<XYZ>> loops, double x, double y)
        {
            bool inside = false;
            foreach (List<XYZ> pts in loops)
            {
                int n = pts.Count;
                for (int i = 0, j = n - 1; i < n; j = i++)
                {
                    double xi = pts[i].X, yi = pts[i].Y, xj = pts[j].X, yj = pts[j].Y;
                    if ((yi > y) != (yj > y))
                    {
                        double xCross = xi + (y - yi) * (xj - xi) / (yj - yi);
                        if (x < xCross) inside = !inside;
                    }
                }
            }
            return inside;
        }

        private sealed class EyeRoomCandidate
        {
            public Room Room;
            public double BaseZ, TopZ;
            public List<List<XYZ>> Loops;
            public double Dist; // 眼睛到 [底, 頂] 的垂直距離（在範圍內＝0）
            public double HorizDist; // 最近房間 fallback：眼睛 XY 到房間邊界的水平距離
        }

        private const double EyeRoomNearestXYToleranceMm = 1500.0;

        private static double DistPointToLoops(List<List<XYZ>> loops, double x, double y)
        {
            double best = double.MaxValue;
            foreach (List<XYZ> pts in loops)
            {
                int n = pts.Count;
                for (int i = 0; i < n; i++)
                {
                    XYZ a = pts[i], b = pts[(i + 1) % n];
                    double dx = b.X - a.X, dy = b.Y - a.Y, len2 = dx * dx + dy * dy;
                    double t = len2 > 1e-12 ? ((x - a.X) * dx + (y - a.Y) * dy) / len2 : 0.0;
                    t = Math.Max(0.0, Math.Min(1.0, t));
                    double px = a.X + t * dx - x, py = a.Y + t * dy - y;
                    double d = Math.Sqrt(px * px + py * py);
                    if (d < best) best = d;
                }
            }
            return best;
        }

        /// <summary>眼睛 XY 不在任何房間內時：水平距離 ≤ 1.5 m、垂直範圍含眼睛（或差 ≤ tolZ）的最近房間。</summary>
        private static EyeRoomCandidate FindNearestEyeRoom(Document doc, XYZ eye, ElementId phaseId, double tolZ, out string log,
            Func<double, double, string, bool> rejectAbove = null)
        {
            log = "";
            double tolXY = CameraMath.MmToFeet(EyeRoomNearestXYToleranceMm);
            EyeRoomCandidate best = null;
            int scanned = 0;
            foreach (Room r in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType().OfType<Room>())
            {
                try
                {
                    if (r.Location == null || !(r.Area > 1e-9)) continue;
                    if (phaseId != null)
                    {
                        ElementId rp = GetRoomPhaseId(r);
                        if (rp != null && rp != phaseId) continue;
                    }
                    BoundingBoxXYZ bb = r.get_BoundingBox(null);
                    if (bb == null) continue;
                    if (eye.X < bb.Min.X - tolXY || eye.X > bb.Max.X + tolXY || eye.Y < bb.Min.Y - tolXY || eye.Y > bb.Max.Y + tolXY) continue;
                    double baseZ = (r.Level?.ProjectElevation ?? bb.Min.Z) + r.BaseOffset;
                    double topZ = baseZ + r.UnboundedHeight;
                    if (!(topZ > baseZ + 1e-6)) topZ = bb.Max.Z;
                    double dz = eye.Z < baseZ ? baseZ - eye.Z : (eye.Z > topZ ? eye.Z - topZ : 0.0);
                    if (dz > tolZ) continue;
                    if (rejectAbove != null && rejectAbove(topZ, baseZ, r.Id.GetIdValue() + "「" + SafeRoomLabel(r) + "」（最近房間）")) continue;
                    List<List<XYZ>> loops = GetRoomLoopsXY(r);
                    if (loops.Count == 0) continue;
                    scanned++;
                    double dxy = DistPointToLoops(loops, eye.X, eye.Y);
                    if (dxy > tolXY) continue;
                    // 同距離時優先垂直範圍含眼睛者
                    if (best == null || dxy + dz * 0.001 < best.HorizDist + best.Dist * 0.001)
                        best = new EyeRoomCandidate { Room = r, BaseZ = baseZ, TopZ = topZ, Loops = loops, Dist = dz, HorizDist = dxy };
                }
                catch (Exception ex) { Logger.Error($"相機管理：房間 {r?.Id.GetIdValue()} 最近距離判定失敗（略過）", ex); }
            }
            log = best == null ? $"水平 {EyeRoomNearestXYToleranceMm:F0}mm 內無符合垂直條件的房間（掃描 {scanned} 間）"
                               : $"{best.Room.Id.GetIdValue()} d={CameraMath.FeetToMm(best.HorizDist):F0}mm";
            return best;
        }

        private static List<EyeRoomCandidate> CollectEyeRoomCandidates(Document doc, XYZ eye, ElementId phaseId, out int bboxHits)
        {
            bboxHits = 0;
            var list = new List<EyeRoomCandidate>();
            double tol = CameraMath.MmToFeet(1.0);
            foreach (Room r in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType().OfType<Room>())
            {
                try
                {
                    if (r.Location == null || !(r.Area > 1e-9)) continue;
                    if (phaseId != null)
                    {
                        ElementId rp = GetRoomPhaseId(r);
                        if (rp != null && rp != phaseId) continue;
                    }
                    BoundingBoxXYZ bb = r.get_BoundingBox(null);
                    if (bb == null) continue;
                    if (eye.X < bb.Min.X - tol || eye.X > bb.Max.X + tol || eye.Y < bb.Min.Y - tol || eye.Y > bb.Max.Y + tol) continue;
                    bboxHits++;
                    List<List<XYZ>> loops = GetRoomLoopsXY(r);
                    if (loops.Count == 0 || !PointInLoops(loops, eye.X, eye.Y)) continue;
                    double baseZ = (r.Level?.ProjectElevation ?? bb.Min.Z) + r.BaseOffset;
                    double topZ = baseZ + r.UnboundedHeight;
                    if (!(topZ > baseZ + 1e-6)) topZ = bb.Max.Z;
                    double dist = eye.Z < baseZ ? baseZ - eye.Z : (eye.Z > topZ ? eye.Z - topZ : 0.0);
                    list.Add(new EyeRoomCandidate { Room = r, BaseZ = baseZ, TopZ = topZ, Loops = loops, Dist = dist });
                }
                catch (Exception ex) { Logger.Error($"相機管理：房間 {r?.Id.GetIdValue()} 範圍判定失敗（略過）", ex); }
            }
            return list;
        }

        private static RoomCtx FindEyeRoomCore(Document doc, View3D camera, CameraMath.CameraGeometry geom)
        {
            var rc = new RoomCtx();
            if (doc == null || camera == null || geom?.Eye == null) { rc.Log = "none (no camera/eye)"; return rc; }
            Phase phase = null;
            try
            {
                Parameter pp = camera.get_Parameter(BuiltInParameter.VIEW_PHASE);
                if (pp != null) phase = doc.GetElement(pp.AsElementId()) as Phase;
            }
            catch { phase = null; }

            XYZ eye = geom.Eye;
            List<EyeRoomCandidate> cands = CollectEyeRoomCandidates(doc, eye, phase?.Id, out int bboxHits);
            string phaseNote = $"phase={phase?.Name ?? "(none)"}";
            if (cands.Count == 0 && phase != null)
            {
                cands = CollectEyeRoomCandidates(doc, eye, null, out int bboxHitsAny);
                if (cands.Count > 0) { phaseNote += "（同 Phase 無候選，改不分 Phase）"; bboxHits = bboxHitsAny; }
            }

            double tolFeet = CameraMath.MmToFeet(EyeRoomVerticalToleranceMm);
            // 2026-09-26 第四輪（使用者：「Camera - Fifth Floor Roof Garden」站在屋頂上卻被判成下層房間的室內）：
            // 眼睛在房間頂「以上」的候選，往下打站立面；站立面 ≥ 房間頂 − 300 mm 且 > 房間底 + 1 m（＝眼睛站在房間上方另一層
            // 樓板／屋頂上，而不是站在這個房間自己的地板上）→ 不是這個房間。
            double sep = CameraMath.MmToFeet(StoreyEyeClearanceMm);
            double aboveBase = CameraMath.MmToFeet(1000.0);
            string rejectNote = "";
            bool RejectAbove(double topZ, double baseZ, string label)
            {
                if (!(eye.Z > topZ + 1e-9)) return false;
                double? surf = EnsureStandingSurface(doc, geom);
                if (!surf.HasValue || surf.Value < topZ - sep || surf.Value <= baseZ + aboveBase) return false;
                rejectNote += $"；排除 {label}（眼睛在房間頂 {CameraMath.FeetToMm(topZ):F0}mm 之上、站立面 {CameraMath.FeetToMm(surf.Value):F0}mm 隔開）";
                return true;
            }
            cands = cands.Where(c => !RejectAbove(c.TopZ, c.BaseZ, c.Room.Id.GetIdValue() + "「" + SafeRoomLabel(c.Room) + "」")).ToList();
            string candText = string.Join("；", cands.Select(c =>
                $"{c.Room.Id.GetIdValue()}「{SafeRoomLabel(c.Room)}」[{CameraMath.FeetToMm(c.BaseZ):F0},{CameraMath.FeetToMm(c.TopZ):F0}]mm d={CameraMath.FeetToMm(c.Dist):F0}"));
            EyeRoomCandidate pick = cands.Where(c => c.Dist <= 1e-9).OrderByDescending(c => c.BaseZ).FirstOrDefault();
            string how = pick != null ? "xyPolygon+zSpan" : null;
            if (pick == null)
            {
                pick = cands.Where(c => c.Dist <= tolFeet).OrderBy(c => c.Dist).FirstOrDefault();
                if (pick != null) how = $"xyPolygon+nearestZ({CameraMath.FeetToMm(pick.Dist):F0}mm ≤ {EyeRoomVerticalToleranceMm:F0}mm)";
            }
            if (pick == null)
            {
                // 2026-09-25 第二輪：眼睛 XY 不在任何房間內（相機在未放房間的樓梯間／挑空，或剛好在邊界外）→
                // 取水平距離（眼睛到房間邊界）≤ 1.5 m、且垂直範圍含眼睛（或差 ≤ 2 m）的最近房間，當室內。
                EyeRoomCandidate near = FindNearestEyeRoom(doc, eye, phase?.Id, tolFeet, out string nearLog, RejectAbove);
                if (near == null && phase != null) near = FindNearestEyeRoom(doc, eye, null, tolFeet, out nearLog, RejectAbove);
                if (near != null)
                {
                    pick = near;
                    how = $"最近房間 d={CameraMath.FeetToMm(near.HorizDist):F0}mm（水平 ≤ {EyeRoomNearestXYToleranceMm:F0}mm，垂直 d={CameraMath.FeetToMm(near.Dist):F0}mm）";
                }
                else if (!string.IsNullOrEmpty(nearLog)) phaseNote += "；最近房間：" + nearLog;
            }
            phaseNote += rejectNote;
            if (pick == null)
            {
                rc.Log = $"none ({phaseNote}, eyeZ={CameraMath.FeetToMm(eye.Z):F0}mm, bboxHits={bboxHits}, xyHits={cands.Count}" +
                         (cands.Count > 0 ? $"：{candText}，垂直距離都 > {EyeRoomVerticalToleranceMm:F0}mm" : "") + ")";
                return rc;
            }

            Room room = pick.Room;
            rc.Room = room;
            rc.SpanBaseZ = pick.BaseZ;
            rc.SpanTopZ = pick.TopZ;
            foreach (List<XYZ> loop in pick.Loops)
            {
                for (int k = 0; k < loop.Count; k++)
                {
                    rc.Pts.Add(loop[k]);
                    rc.Segs.Add(new KeyValuePair<XYZ, XYZ>(loop[k], loop[(k + 1) % loop.Count]));
                }
            }
            bool hasBounds = SectionBoxLevelHelper.TryGetWorldBounds(room, out double bx0, out double by0, out double bz0, out double bx1, out double by1, out double bz1);
            string footSrc = "boundary";
            if (rc.Pts.Count < 3 && hasBounds)
            {
                footSrc = "boundingBox";
                XYZ a = new XYZ(bx0, by0, 0), b = new XYZ(bx1, by0, 0), c2 = new XYZ(bx1, by1, 0), d = new XYZ(bx0, by1, 0);
                rc.Pts.Clear(); rc.Segs.Clear();
                rc.Pts.AddRange(new[] { a, b, c2, d });
                rc.Segs.Add(new KeyValuePair<XYZ, XYZ>(a, b)); rc.Segs.Add(new KeyValuePair<XYZ, XYZ>(b, c2));
                rc.Segs.Add(new KeyValuePair<XYZ, XYZ>(c2, d)); rc.Segs.Add(new KeyValuePair<XYZ, XYZ>(d, a));
            }
            if (rc.Pts.Count > 0)
            {
                rc.MinX = rc.Pts.Min(q => q.X); rc.MaxX = rc.Pts.Max(q => q.X);
                rc.MinY = rc.Pts.Min(q => q.Y); rc.MaxY = rc.Pts.Max(q => q.Y);
            }
            if (hasBounds) rc.TopZ = bz1;
            if (pick.HorizDist > 1e-9 && rc.Pts.Count > 0)
            {
                // 最近房間 fallback：眼睛在房間外 → Key Plan 範圍需同時含眼睛
                rc.MinX = Math.Min(rc.MinX, eye.X); rc.MaxX = Math.Max(rc.MaxX, eye.X);
                rc.MinY = Math.Min(rc.MinY, eye.Y); rc.MaxY = Math.Max(rc.MaxY, eye.Y);
            }

            rc.Log = $"id={room.Id.GetIdValue()} 「{SafeRoomLabel(room)}」 foundBy={how} {phaseNote} footprint={footSrc}[{rc.Pts.Count} pts] " +
                     $"X[{CameraMath.FeetToMm(rc.MinX):F0},{CameraMath.FeetToMm(rc.MaxX):F0}] Y[{CameraMath.FeetToMm(rc.MinY):F0},{CameraMath.FeetToMm(rc.MaxY):F0}]mm " +
                     $"span[{CameraMath.FeetToMm(pick.BaseZ):F0},{CameraMath.FeetToMm(pick.TopZ):F0}]mm eyeZ={CameraMath.FeetToMm(eye.Z):F0}mm " +
                     $"top(bbox)={(rc.TopZ.HasValue ? CameraMath.FeetToMm(rc.TopZ.Value).ToString("F0") + "mm" : "?")} candidates={cands.Count}";

            string key = camera.Id.GetIdValue() + ":" + room.Id.GetIdValue() + ":" + how;
            if (_loggedEyeRoomChoices.Add(key))
                Logger.Info($"相機管理：「{camera.Name}」眼睛所在房間＝{room.Id.GetIdValue()}「{SafeRoomLabel(room)}」（{how}；候選 {candText}）");
            return rc;
        }
    }
}
