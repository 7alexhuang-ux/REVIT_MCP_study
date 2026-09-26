using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Autodesk.Revit.UI;

namespace RevitMCP.Core
{
    /// <summary>
    /// 2026-09-26 第四輪（使用者實測：A 標記模式下每次相機變動都「運算量很大、當機」；Navigation Wheel 繞行後標記不跟）：
    /// 相機變動後的「增量」更新（D-041）。
    ///   ① 只處理真的變了的相機：姿態雜湊＝眼睛／前方向／上方向／裁剪框／遠裁剪＋模型 epoch；與上次畫標記時相同 → 整台略過。
    ///   ② 樓層總覽只動「舊樓層＋新樓層」那兩張的這台相機標記；A 標記只同步變了的相機；簡化樓層記號只在樓層變了才重畫。
    ///   ③ 眼睛移動 &gt; 300 mm 或水平角變 &gt; 5°、且相機已有 Key Plan → 走一鍵 4 視圖的更新路徑（不並排）重算範圍／剖面／等角。
    ///   ④ 導覽（Navigation Wheel 等不觸發 DocumentChanged 的變動）偵測到的相機，若沒有任何視角版本 → 先把「移動前」的姿態存成「A案」。
    /// 全部在呼叫端的 API context（面板佇列 ExternalEvent 或 MCP ExternalEvent）內執行；每一步 try/catch＋log。
    /// </summary>
    internal static partial class CameraKeyPlanService
    {
        /// <summary>變動前的姿態提示（面板清單列舊值、導覽監看的基準）。Before＝移動前的完整視角（自動存「A案」用）。</summary>
        internal sealed class CameraPoseHint
        {
            public XYZ Eye;
            public double YawDeg;
            public CameraViewVersion Before;
            public string Source;
        }

        private sealed class PoseRecord
        {
            public string Hash;
            public XYZ Eye;
            public double YawDeg;
            public long? StoreyId;
        }

        /// <summary>上次畫標記時的姿態（鍵＝文件＋相機 UniqueId）。</summary>
        private static readonly Dictionary<string, PoseRecord> _lastDrawn = new Dictionary<string, PoseRecord>(StringComparer.Ordinal);
        /// <summary>上次 Key Plan 範圍（一鍵 4 視圖）計算時的姿態。</summary>
        private static readonly Dictionary<string, PoseRecord> _lastKeyPlan = new Dictionary<string, PoseRecord>(StringComparer.Ordinal);

        // 本工具自己改了 Key Plan／總覽視圖內容（標記、樓層記號、裁剪）的次數；③ 位置圖若用這些視圖當底圖，底圖點陣快取以此判斷過期。
        private static int _ownViewsEpoch;
        internal static int OwnViewsEpoch => System.Threading.Volatile.Read(ref _ownViewsEpoch);
        internal static void BumpOwnViewsEpoch() => System.Threading.Interlocked.Increment(ref _ownViewsEpoch);

        /// <summary>這張視圖是不是本工具建立／連結的視圖（Key Plan 平面／剖面／等角、樓層總覽…）。</summary>
        internal static bool IsOwnTaggedView(View v)
        {
            try { return v != null && Schema.Lookup(SchemaGuid) != null && TryReadLink(v, out _, out _); } catch { return false; }
        }

        internal const double RealignMoveMm = 300.0;
        internal const double RealignYawDeg = 5.0;

        private static string PoseKey(Document doc, string cid) => StoreyDocKey(doc) + "|" + cid;

        private static string R(double v, int digits) => Math.Round(v, digits).ToString(CultureInfo.InvariantCulture);

        /// <summary>
        /// 相機姿態雜湊：眼睛（0.1 mm）、前方向／上方向（1e-5）、裁剪框啟用與 Min/Max（0.1 mm）、遠裁剪（0.1 mm）。
        /// 只讀，便宜（面板導覽監看每 300 ms 呼叫一次）。讀取失敗的欄位記成「?」。
        /// </summary>
        internal static string ComputePoseHash(View3D cam)
        {
            if (cam == null) return "";
            var sb = new System.Text.StringBuilder(160);
            try
            {
                ViewOrientation3D o = cam.GetOrientation();
                XYZ e = o.EyePosition, f = o.ForwardDirection, u = o.UpDirection;
                sb.Append(R(CameraMath.FeetToMm(e.X), 1)).Append(',').Append(R(CameraMath.FeetToMm(e.Y), 1)).Append(',').Append(R(CameraMath.FeetToMm(e.Z), 1));
                sb.Append('|').Append(R(f.X, 5)).Append(',').Append(R(f.Y, 5)).Append(',').Append(R(f.Z, 5));
                sb.Append('|').Append(R(u.X, 5)).Append(',').Append(R(u.Y, 5)).Append(',').Append(R(u.Z, 5));
            }
            catch { sb.Append("?"); }
            try
            {
                sb.Append('|').Append(cam.CropBoxActive ? 1 : 0);
                BoundingBoxXYZ cb = cam.CropBox;
                if (cb != null)
                    sb.Append('|').Append(R(CameraMath.FeetToMm(cb.Min.X), 1)).Append(',').Append(R(CameraMath.FeetToMm(cb.Min.Y), 1)).Append(',').Append(R(CameraMath.FeetToMm(cb.Min.Z), 1))
                      .Append(',').Append(R(CameraMath.FeetToMm(cb.Max.X), 1)).Append(',').Append(R(CameraMath.FeetToMm(cb.Max.Y), 1)).Append(',').Append(R(CameraMath.FeetToMm(cb.Max.Z), 1));
            }
            catch { sb.Append("|?"); }
            try
            {
                Parameter far = CameraMath.FindFarClipOffsetParameter(cam);
                sb.Append('|').Append(far != null ? R(CameraMath.FeetToMm(far.AsDouble()), 1) : "-");
            }
            catch { sb.Append("|?"); }
            return sb.ToString();
        }

        private static double YawOf(XYZ forward)
        {
            try { return CameraMath.ComputeHorizontalAngleDeg(forward); } catch { return 0; }
        }

        /// <summary>標記用雜湊＝姿態＋模型 epoch（房間變了扇形半徑也會變）＋標記型式（切換型式一律強制重畫，此處只是保險）。</summary>
        private static string MarkerHash(View3D cam) => ComputePoseHash(cam) + "|e" + ModelEpoch + "|m" + GetMarkerMode();

        private static void RecordDrawn(Document doc, View3D cam, CameraParams cp)
        {
            try
            {
                _lastDrawn[PoseKey(doc, cam.UniqueId)] = new PoseRecord
                {
                    Hash = MarkerHash(cam),
                    Eye = cp?.Geom?.Eye,
                    YawDeg = cp?.Geom?.Forward != null ? YawOf(cp.Geom.Forward) : 0,
                    StoreyId = cp?.Geom?.LevelBelow != null ? (long?)cp.Geom.LevelBelow.Id.GetIdValue() : null
                };
            }
            catch { }
        }

        private static void RecordKeyPlanPose(Document doc, View3D cam, CameraParams cp)
        {
            try
            {
                _lastKeyPlan[PoseKey(doc, cam.UniqueId)] = new PoseRecord
                {
                    Hash = ComputePoseHash(cam),
                    Eye = cp?.Geom?.Eye,
                    YawDeg = cp?.Geom?.Forward != null ? YawOf(cp.Geom.Forward) : 0,
                    StoreyId = cp?.Geom?.LevelBelow != null ? (long?)cp.Geom.LevelBelow.Id.GetIdValue() : null
                };
            }
            catch { }
        }

        private static long? LastDrawnStorey(Document doc, string cid)
            => _lastDrawn.TryGetValue(PoseKey(doc, cid), out PoseRecord r) ? r.StoreyId : null;

        private static bool HasLastDrawnRecord(Document doc, string cid) => _lastDrawn.ContainsKey(PoseKey(doc, cid));

        /// <summary>這台相機是否已有 Key Plan 平面／剖面／等角任一張（ES 連結）。</summary>
        internal static bool HasKeyPlanViews(Document doc, string cid)
        {
            if (doc == null || string.IsNullOrEmpty(cid) || Schema.Lookup(SchemaGuid) == null) return false;
            foreach (View v in new FilteredElementCollector(doc).OfClass(typeof(View)).WherePasses(new ExtensibleStorageFilter(SchemaGuid)).Cast<View>())
            {
                if (v.IsTemplate) continue;
                if (TryReadLink(v, out string c, out string r) && c == cid && (r == "Plan" || r == "Section" || r == "Iso")) return true;
            }
            return false;
        }

        /// <summary>
        /// 相機變動後的更新入口（面板去抖佇列、set_camera_pose 共用）。回傳摘要（狀態列／MCP 回傳）。
        /// uiApp 可為 null（此時不做 Key Plan 範圍重算，只更新標記）。
        /// </summary>
        internal static string UpdateCamerasAfterChange(UIApplication uiApp, Document doc, ICollection<ElementId> cameraIds,
            IDictionary<ElementId, CameraPoseHint> hints, out int updated)
        {
            updated = 0;
            if (doc == null || cameraIds == null || cameraIds.Count == 0) return "沒有相機需要更新。";
            var notes = new List<string>();
            using (CameraPerf perf = CameraPerf.Begin($"相機變動後更新（{cameraIds.Count} 台）"))
            {
                // ④ 導覽前的姿態 → 自動「A案」（只在沒有任何版本時）
                try
                {
                    var toSave = new List<(View3D cam, CameraViewVersion v)>();
                    if (hints != null)
                    {
                        foreach (var kv in hints)
                        {
                            if (kv.Value?.Before == null || !cameraIds.Contains(kv.Key)) continue;
                            if (!(doc.GetElement(kv.Key) is View3D cam) || cam.IsTemplate || !cam.IsPerspective) continue;
                            if (ReadVersions(cam).Count > 0) continue;
                            toSave.Add((cam, kv.Value.Before));
                        }
                    }
                    if (toSave.Count > 0)
                    {
                        using (Transaction t = TransactionHelper.Begin(doc, VersionSaveTransactionName))
                        {
                            t.Start();
                            try
                            {
                                foreach (var (cam, v) in toSave)
                                {
                                    v.Name = OriginalVersionName;
                                    WriteVersions(cam, new List<CameraViewVersion> { v });
                                    Logger.Info($"相機視角版本：「{cam.Name}」第一次被導覽修改前自動存「{OriginalVersionName}」（原始視角）。");
                                }
                                t.Commit();
                                notes.Add($"自動存「{OriginalVersionName}」{toSave.Count} 台");
                            }
                            catch (Exception ex)
                            {
                                try { if (t.HasStarted() && !t.HasEnded()) t.RollBack(); } catch { }
                                Logger.Error("相機視角版本：導覽前自動存 A案 失敗（略過）", ex);
                            }
                        }
                    }
                }
                catch (Exception ex) { Logger.Error("相機變動後更新：自動存原始版本失敗（略過）", ex); }
                perf.Lap("自動存A案");

                // ③ 決定哪些相機要重算 Key Plan 範圍
                var realign = new List<(ElementId id, string why)>();
                var markersOnly = new List<ElementId>();
                foreach (ElementId id in cameraIds)
                {
                    try
                    {
                        if (!(doc.GetElement(id) is View3D cam) || cam.IsTemplate || !cam.IsPerspective || IsSheetPerspectiveCopy(cam)) continue;
                        string why = null;
                        if (uiApp != null && HasKeyPlanViews(doc, cam.UniqueId))
                        {
                            ViewOrientation3D o = cam.GetOrientation();
                            string key = PoseKey(doc, cam.UniqueId);
                            PoseRecord basis = _lastKeyPlan.TryGetValue(key, out PoseRecord kp) ? kp : null;
                            string basisName = "上次 Key Plan";
                            if (basis == null && hints != null && hints.TryGetValue(id, out CameraPoseHint h) && h?.Eye != null)
                            { basis = new PoseRecord { Eye = h.Eye, YawDeg = h.YawDeg }; basisName = "變動前（" + (h.Source ?? "?") + "）"; }
                            if (basis == null && _lastDrawn.TryGetValue(key, out PoseRecord dr) && dr.Eye != null)
                            { basis = dr; basisName = "上次標記"; }
                            if (basis?.Eye != null)
                            {
                                double moved = CameraMath.FeetToMm(Math.Sqrt(Math.Pow(o.EyePosition.X - basis.Eye.X, 2) + Math.Pow(o.EyePosition.Y - basis.Eye.Y, 2) + Math.Pow(o.EyePosition.Z - basis.Eye.Z, 2)));
                                double dyaw = Math.Abs(NormalizeAngle((YawOf(o.ForwardDirection) - basis.YawDeg) * Math.PI / 180.0)) * 180.0 / Math.PI;
                                if (moved > RealignMoveMm || dyaw > RealignYawDeg)
                                    why = $"相對{basisName} 移動 {moved:F0}mm、水平角 {dyaw:F1}°";
                            }
                        }
                        if (why != null) realign.Add((id, why)); else markersOnly.Add(id);
                    }
                    catch (Exception ex)
                    {
                        Logger.Error("相機變動後更新：判斷是否重算 Key Plan 失敗（改為只更新標記）", ex);
                        markersOnly.Add(id);
                    }
                }
                perf.Lap("判斷重算");

                foreach (var (id, why) in realign)
                {
                    try
                    {
                        Logger.Info($"相機變動後更新：相機 id={id.GetIdValue()} {why} → 重算 Key Plan 範圍（一鍵 4 視圖更新路徑，不並排）");
                        KeyPlanResult r = CreateOrUpdateFourViews(uiApp, doc, id);
                        if (r.Error != null) { notes.Add("Key Plan 重算失敗：" + r.Error); markersOnly.Add(id); }
                        else { updated++; notes.Add("已重算 Key Plan 範圍（" + why + "）"); }
                    }
                    catch (Exception ex)
                    {
                        Logger.Error("相機變動後更新：重算 Key Plan 失敗（改為只更新標記）", ex);
                        CameraManagerDiagnostics.RecordCaught("RealignKeyPlan", ex);
                        markersOnly.Add(id);
                    }
                }
                perf.Lap("重算KeyPlan");

                if (markersOnly.Count > 0)
                {
                    int n = UpdateMarkersForCameras(doc, markersOnly, false, out string mSummary);
                    updated += n;
                    if (!string.IsNullOrEmpty(mSummary)) notes.Add(mSummary);
                }
                perf.Lap("更新標記");
                perf.Note(string.Join("；", notes));
            }
            return notes.Count > 0 ? string.Join("；", notes) : "相機標記已是最新。";
        }
    }
}
