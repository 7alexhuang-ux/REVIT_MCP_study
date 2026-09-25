using System;
using System.Collections.Generic;
using System.Linq;
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

        /// <summary>CameraMath.ComputeGeometry 呼叫：搜尋眼睛所在房間並存進 geom；室內＝把樓層換成房間的 Level。</summary>
        internal static void ApplyIndoorStorey(View3D camera, CameraMath.CameraGeometry geom)
        {
            if (camera == null || geom == null) return;
            RoomCtx rc = null;
            try { rc = FindEyeRoomCore(camera.Document, camera, geom); }
            catch (Exception ex)
            {
                Logger.Error("相機管理：眼睛所在房間搜尋失敗", ex);
                rc = new RoomCtx { Log = "failed: " + ex.Message };
            }
            geom.EyeRoom = rc;
            geom.EyeRoomResolved = true;

            if (!(rc?.Room is Room room)) { geom.StoreySource = "elevation"; return; }
            Level roomLevel = null;
            try { roomLevel = room.LevelId != null && room.LevelId != ElementId.InvalidElementId ? camera.Document.GetElement(room.LevelId) as Level : null; }
            catch { roomLevel = null; }
            if (roomLevel == null) { geom.StoreySource = "elevation"; return; }

            Level before = geom.LevelBelow;
            geom.LevelBelow = roomLevel;
            geom.AboveLevelBelowMm = Math.Round(geom.EyeZMm - CameraMath.FeetToMm(roomLevel.ProjectElevation), 1);
            geom.StoreySource = "room";
            rc.Log += $" storey=room.Level「{roomLevel.Name}」" +
                      (before == null || before.Id != roomLevel.Id ? $"（高程判定原為「{before?.Name ?? "(無)"}」）" : "");
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
        private static EyeRoomCandidate FindNearestEyeRoom(Document doc, XYZ eye, ElementId phaseId, double tolZ, out string log)
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
                EyeRoomCandidate near = FindNearestEyeRoom(doc, eye, phase?.Id, tolFeet, out string nearLog);
                if (near == null && phase != null) near = FindNearestEyeRoom(doc, eye, null, tolFeet, out nearLog);
                if (near != null)
                {
                    pick = near;
                    how = $"最近房間 d={CameraMath.FeetToMm(near.HorizDist):F0}mm（水平 ≤ {EyeRoomNearestXYToleranceMm:F0}mm，垂直 d={CameraMath.FeetToMm(near.Dist):F0}mm）";
                }
                else if (!string.IsNullOrEmpty(nearLog)) phaseNote += "；最近房間：" + nearLog;
            }
            if (pick == null)
            {
                rc.Log = $"none ({phaseNote}, eyeZ={CameraMath.FeetToMm(eye.Z):F0}mm, bboxHits={bboxHits}, xyHits={cands.Count}" +
                         (cands.Count > 0 ? $"：{candText}，垂直距離都 > {EyeRoomVerticalToleranceMm:F0}mm" : "") + ")";
                return rc;
            }

            Room room = pick.Room;
            rc.Room = room;
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
