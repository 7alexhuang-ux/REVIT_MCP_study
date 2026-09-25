using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Autodesk.Revit.UI;
using RevitMCP.Configuration;

namespace RevitMCP.Core
{
    /// <summary>
    /// 「一鍵 4 視圖」（規格書 §5.1 平面／剖面／等角 ＋ §5.3 B 型註解標記，第一版）。
    ///
    /// 設計取捨（第一次交付，未經 Revit 實機驗證，僅保證可編譯、邏輯自洽）：
    ///   1. 三個 Key Plan 視圖與相機的對應關係，用 Extensible Storage 記在「視圖元素本身」
    ///      上（欄位：CameraUniqueId + Role），不是用名稱猜——這樣使用者事後改相機名稱、
    ///      Key Plan 視圖不會變成孤兒，重新整理／改名都抓得到同一個視圖，不會生出重複視圖。
    ///      找不到 ES 連結時退回用「精確名稱比對」找既有視圖（同樣視為既有、更新而非新建），
    ///      兩者都找不到才新建。
    ///   2. 標記（B 型註解，只在平面／剖面）用同一個 Schema、不同 Role（MarkerPlan／
    ///      MarkerSection）貼在畫出來的 DetailCurve 上；每次執行先刪除同一台相機、同一
    ///      Role 的舊標記元素再重畫，不會愈跑愈多。
    ///   3. 版面尺寸（margin、剖面深度、遠裁剪預設值…）是有註記依據的假設值，不是查證過
    ///      的規格——都寫在下面對應的常數旁的註解。
    /// </summary>
    internal static class CameraKeyPlanService
    {
        // ── 假設值（未經使用者驗收，之後可視需要調整） ──
        private const double PlanMarginCm = 200.0;           // 平面裁剪框在相機/視野範圍外再留的邊界
        private const double SectionBackMarginCm = 100.0;    // 剖面：相機背後留一點，不要正好切在相機位置
        private const double SectionForwardMarginCm = 100.0; // 剖面：遠裁剪之外再留一點
        private const double SectionDepthHalfCm = 150.0;     // 剖面「厚度」：沿視線垂直方向前後各留 1.5m
        private const double SectionBelowLevelCm = 100.0;    // 剖面垂直下限：樓層下方 1m（規格 5.1：−1m）
        private const double SectionAboveEyeCm = 300.0;      // 剖面垂直上限：眼睛上方 3m（規格 5.1：+3m）
        private const double DefaultFarClipCm = 2000.0;      // 相機沒有可用遠裁剪值時的預設（20m，未查證，僅供第一版有值可用）
        private const double DefaultFovDeg = 60.0;           // 沒有 FOV 估算值時的預設半視角基準
        // 等角 Key Plan 視圖：方向＝相機水平前方向往下俯 35.26°（2026-09-25 起，取代固定 TOP-BACK-LEFT），
        // 範圍＝眼睛所在房間 +0.6m ＋依樓層/結構延伸的 SectionBox，見下方 BuildIsoOrientation／BuildIsoSectionBox。
        private const double IsoFarClipCapMm = 30000.0;        // XY 遠裁剪扇形的上限（30m），避免超大遠裁剪把等角範圍拉到整個基地
        private const double IsoSupportingBeamToleranceMm = 300.0; // 沿用 set_3d_section_box 的支撐樑容差預設值
        private const double IsoUpperSlabClearanceMm = 10.0;   // 規格：上層樓板底再退 10mm
        private const double IsoMinBackoffCm = 500.0;          // 等角視角眼睛位置至少退後 5m，確保落在 SectionBox 外
        private const double DefaultSlabThicknessMm = 300.0;   // 找不到上層樓板、且上一樓層沒有任何樓板可取厚度時的假設樓板厚
        private const double SectionAboveSlabCm = 50.0;        // 剖面垂直上限：上層樓板底 +0.5m（規格 5.1 修訂）
        // 2026-09-24 第四輪（等角「看得到的房間」＋樓板下限，協調者轉述使用者回報）：
        private const double IsoFloorBelowClearanceMm = 50.0;  // 等角 Z 下限：本層樓板底再往下 50mm
        private const double IsoRoomMarginMm = 500.0;          // 房間聯集外擴 0.5m
        private const double IsoRoomRayStepMm = 500.0;         // 視線取樣間距 0.5m
        private const double IsoRoomRayMaxMm = 15000.0;        // 視線取樣最遠 15m（再與遠裁剪取小）
        private const double IsoRoomRayGapStopMm = 1000.0;     // 連續離開所有房間超過 1m 就停這條射線
        private const double IsoNoRoomFanCapMm = 10000.0;      // （舊）找不到房間時扇形框遠端夾到 10m——2026-09-25 起改用 NoRoomFanCapMm
        // 2026-09-25 第五輪（使用者：「以我們這間房間為主，往外留一些 buffer」）：只用「眼睛所在房間」。
        private const double RoomBufferMm = 600.0;             // 房間外擴 0.6m（等角 XY、平面裁剪、剖面水平範圍、扇形半徑）
        private const double NoRoomFanCapMm = 8000.0;          // 室內但算不出視線離開房間距離：扇形距離夾到 8m
        private const double OutdoorPlanMarginMm = 2000.0;     // 室外：平面裁剪／等角 XY＝扇形外接框 +2m
        private const double OutdoorBehindEyeMm = 2000.0;      // 室外：剖面在眼睛後方留 2m
        private const double OutdoorBelowLevelMm = 1000.0;     // 室外：Z 下限＝min(下方樓層, 最低樓層) −1m
        private const double OutdoorAboveModelMm = 2000.0;     // 室外：Z 上限＝XY 範圍內模型最高點 +2m
        private const double OutdoorModelScanZFeet = 10000.0;  // 室外：找模型最高點時 Outline 的 Z 範圍（±10000 ft，等同不限）
        private const double SectionLevelMinGapMm = 1000.0;    // 剖面樓層標頭：上一層須與下方樓層高差 ≥ 1.0m（近重複樓層不算）
        private const double FanMinRadiusMm = 1500.0;          // 扇形半徑下限 1.5m
        private const double RoomTopIsoBufferMm = 300.0;       // 等角頂＝房間頂 +0.3m（再與上層樓板底 −10mm 取小）
        private const double RoomTopSectionBufferMm = 500.0;   // 剖面頂＝房間頂 +0.5m
        private const double RoomTopSlabToleranceMm = 100.0;   // 上層樓板判定：底面（或頂面）≥ 房間頂 −0.1m
        private const double IsoElevationDeg = 35.26;          // 等角俯角（標準等角 atan(1/√2)）

        /// <summary>「更新相機標記」交易名稱：Application.cs 的 DocumentChanged 用它（加上
        /// <see cref="IsUpdatingMarkers"/>）辨識「我們自己的標記重畫」，避免回饋迴圈。</summary>
        internal const string MarkerUpdateTransactionName = "更新相機標記";
        /// <summary>標記重畫交易進行中（含 Commit 當下觸發的 DocumentChanged）。</summary>
        internal static bool IsUpdatingMarkers { get; private set; }
        // MarkerCrossRadiusCm／MarkerArrowLenCm（模型空間固定 cm）已被下面的「列印比例固定」
        // 常數取代，見 DrawPlanMarker／DrawSectionMarker 上方的 MarkerCirclePrintRadiusMm 等。

        private static double Cm(double cm) => CameraMath.MmToFeet(cm * 10.0);

        // ══════════════════════════════════════════════════════════
        // Extensible Storage：相機 ↔ Key Plan 視圖／標記 的連結
        // ══════════════════════════════════════════════════════════

        // 固定 GUID，不要改——已經寫進使用者 .rvt 檔的 Schema 綁定靠這個值辨識。
        private static readonly Guid SchemaGuid = new Guid("6C9E2F0A-6E2C-4E7C-9C8B-2F6E9B0F1A2D");
        private const string FieldCameraUniqueId = "CameraUniqueId";
        private const string FieldRole = "Role";

        private static Schema GetOrCreateSchema()
        {
            Schema schema = Schema.Lookup(SchemaGuid);
            if (schema != null) return schema;

            SchemaBuilder builder = new SchemaBuilder(SchemaGuid);
            builder.SetSchemaName("RevitMCP_CameraKeyPlanLink");
            builder.SetReadAccessLevel(AccessLevel.Public);
            builder.SetWriteAccessLevel(AccessLevel.Public);
            builder.AddSimpleField(FieldCameraUniqueId, typeof(string));
            builder.AddSimpleField(FieldRole, typeof(string));
            return builder.Finish();
        }

        private static void TagLink(Element el, string cameraUniqueId, string role)
        {
            Schema schema = GetOrCreateSchema();
            Entity entity = new Entity(schema);
            entity.Set(schema.GetField(FieldCameraUniqueId), cameraUniqueId ?? "");
            entity.Set(schema.GetField(FieldRole), role ?? "");
            el.SetEntity(entity);
        }

        private static bool TryReadLink(Element el, out string cameraUniqueId, out string role)
        {
            cameraUniqueId = null;
            role = null;
            Schema schema = Schema.Lookup(SchemaGuid);
            if (schema == null) return false;
            Entity entity = el.GetEntity(schema);
            if (entity == null || !entity.IsValid()) return false;
            cameraUniqueId = entity.Get<string>(schema.GetField(FieldCameraUniqueId));
            role = entity.Get<string>(schema.GetField(FieldRole));
            return true;
        }

        private static View FindLinkedView(Document doc, string cameraUniqueId, string role)
        {
            foreach (View v in new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>())
            {
                if (v.IsTemplate) continue;
                if (TryReadLink(v, out string cid, out string r) && cid == cameraUniqueId && r == role)
                    return v;
            }
            return null;
        }

        // ══════════════════════════════════════════════════════════
        // 2026-09-24 追加：剖面「使用者是否手動調整過」狀態（第二個 Schema，不動上面固定 GUID
        // 的 CameraKeyPlanLink——ES Schema 一旦建立即不可再增減欄位，同一個 GUID 不能改欄位
        // 定義，否則會弄壞已經寫進使用者 .rvt 的既有連結）。記的是「上次程式自動產生剖面時」
        // 的視線原點/水平方向；下次執行時比對剖面目前實際的 CropBox.Transform，若偏差在容差
        // 內＝使用者沒動過（或這正是我們剛寫入的），照常更新；偏差超出容差＝使用者手動調整過，
        // 這次只補畫相機標記，CropBox 完全不動。使用者可用「重設剖面位置」強制覆蓋回去。
        // ══════════════════════════════════════════════════════════

        // 2026-09-24 第三輪修正：第一版（GUID 1A7E4C3B-…）的 double 欄位沒有設定 Spec，Revit 2021+
        // 的 SchemaBuilder.Finish() 對「沒有單位的 double 欄位」一律丟 InvalidOperationException
        // 「Units are required for field OriginX」（2026-09-24 21:33 CameraManagerCrash log 實證，
        // 緊跟在前的 SEHException 只是同一個例外在原生層 ESSchemaBuilder.finish 的第一機會紀錄，
        // 不是另一個錯誤）。Finish 失敗＝Schema 從未註冊進任何文件，舊 GUID 理論上是乾淨的；
        // 但仍換一個新 GUID，杜絕「某個測試版本曾用錯誤欄位定義註冊過」的可能——ES Schema 一旦
        // 註冊，同一 GUID 的欄位定義永遠不能改。舊 GUID 不讀：它從未成功寫入過任何資料。
        // 座標欄位（OriginX/Y/Z）用 SpecTypeId.Length＋UnitTypeId.Feet（內部單位）；方向向量分量
        // （ForwardX/Y）是無單位數值，用 SpecTypeId.Number＋UnitTypeId.General。
        private static readonly Guid SectionStateSchemaGuid = new Guid("5B2D8E61-3C4A-4F7B-A1D9-7E0C6B3F2A58");
        private const string FieldOriginX = "OriginX";
        private const string FieldOriginY = "OriginY";
        private const string FieldOriginZ = "OriginZ";
        private const string FieldForwardX = "ForwardX";
        private const string FieldForwardY = "ForwardY";
        private const double SectionMovedOriginToleranceMm = 5.0;
        private const double SectionMovedAngleToleranceDeg = 2.0;

        private static Schema GetOrCreateSectionStateSchema()
        {
            Schema schema = Schema.Lookup(SectionStateSchemaGuid);
            if (schema != null) return schema;

            SchemaBuilder builder = new SchemaBuilder(SectionStateSchemaGuid);
            builder.SetSchemaName("RevitMCP_CameraSectionStateV2");
            builder.SetReadAccessLevel(AccessLevel.Public);
            builder.SetWriteAccessLevel(AccessLevel.Public);
            builder.AddSimpleField(FieldOriginX, typeof(double)).SetSpec(SpecTypeId.Length);
            builder.AddSimpleField(FieldOriginY, typeof(double)).SetSpec(SpecTypeId.Length);
            builder.AddSimpleField(FieldOriginZ, typeof(double)).SetSpec(SpecTypeId.Length);
            builder.AddSimpleField(FieldForwardX, typeof(double)).SetSpec(SpecTypeId.Number);
            builder.AddSimpleField(FieldForwardY, typeof(double)).SetSpec(SpecTypeId.Number);
            return builder.Finish();
        }

        /// <summary>面板載入時呼叫的自我檢查（不需要文件、不需要交易；Schema 註冊是應用程式層級）：
        /// 確認兩個 ES Schema 都能建出來、且剖面狀態 Schema 的 double 欄位都帶有 Spec。
        /// 回傳 null＝OK，否則為失敗原因。必須在 Revit API context（ExternalEvent／Idling）呼叫。</summary>
        internal static string SelfCheckSchemas()
        {
            try
            {
                Schema link = GetOrCreateSchema();
                if (link == null) return "CameraKeyPlanLink Schema 建立回傳 null";
                Schema state = GetOrCreateSectionStateSchema();
                if (state == null) return "CameraSectionState Schema 建立回傳 null";
                foreach (string f in new[] { FieldOriginX, FieldOriginY, FieldOriginZ, FieldForwardX, FieldForwardY })
                {
                    Field field = state.GetField(f);
                    if (field == null) return "CameraSectionState 缺欄位 " + f;
                    ForgeTypeId spec = field.GetSpecTypeId();
                    if (spec == null || spec.Empty()) return "CameraSectionState 欄位 " + f + " 沒有 Spec";
                }
                return null;
            }
            catch (Exception ex)
            {
                return ex.GetType().Name + ": " + ex.Message;
            }
        }

        /// <summary>剖面狀態是「保留手動調整」用的附屬簿記——任何失敗（Schema、寫入）都只記 log、
        /// 不往外拋，絕不能讓一鍵 4 視圖整筆交易因此回滾。</summary>
        private static void SaveSectionState(Element sectionView, XYZ origin, XYZ forwardHoriz)
        {
            try
            {
                Schema schema = GetOrCreateSectionStateSchema();
                Entity e = new Entity(schema);
                e.Set(schema.GetField(FieldOriginX), origin.X, UnitTypeId.Feet);
                e.Set(schema.GetField(FieldOriginY), origin.Y, UnitTypeId.Feet);
                e.Set(schema.GetField(FieldOriginZ), origin.Z, UnitTypeId.Feet);
                e.Set(schema.GetField(FieldForwardX), forwardHoriz.X, UnitTypeId.General);
                e.Set(schema.GetField(FieldForwardY), forwardHoriz.Y, UnitTypeId.General);
                sectionView.SetEntity(e);
            }
            catch (Exception ex)
            {
                Logger.Error("相機 Key Plan：寫入剖面狀態（ES）失敗，略過（不影響視圖建立）", ex);
                CameraManagerDiagnostics.RecordCaught("SaveSectionState", ex);
            }
        }

        private static bool TryReadSectionState(Element sectionView, out XYZ origin, out XYZ forwardHoriz)
        {
            origin = null;
            forwardHoriz = null;
            try
            {
                Schema schema = Schema.Lookup(SectionStateSchemaGuid);
                if (schema == null) return false;
                Entity e = sectionView.GetEntity(schema);
                if (e == null || !e.IsValid()) return false;

                double ox = e.Get<double>(schema.GetField(FieldOriginX), UnitTypeId.Feet);
                double oy = e.Get<double>(schema.GetField(FieldOriginY), UnitTypeId.Feet);
                double oz = e.Get<double>(schema.GetField(FieldOriginZ), UnitTypeId.Feet);
                double fx = e.Get<double>(schema.GetField(FieldForwardX), UnitTypeId.General);
                double fy = e.Get<double>(schema.GetField(FieldForwardY), UnitTypeId.General);
                origin = new XYZ(ox, oy, oz);
                forwardHoriz = new XYZ(fx, fy, 0);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error("相機 Key Plan：讀取剖面狀態（ES）失敗，視為未手動調整", ex);
                origin = null;
                forwardHoriz = null;
                return false;
            }
        }

        /// <summary>比對剖面目前實際的 CropBox.Transform 與上次程式產生時記下的視線原點/方向；
        /// 沒有存過狀態（例如舊版本產生、還沒補存過）一律視為「沒動過」，照常更新一次並補存狀態。</summary>
        private static bool HasUserMovedSection(ViewSection section, XYZ expectedOrigin, XYZ expectedForwardHoriz)
        {
            if (!TryReadSectionState(section, out XYZ savedOrigin, out XYZ savedForward)) return false;
            try
            {
                Transform tf = section.CropBox.Transform;
                XYZ curOrigin = tf.Origin;
                XYZ curForwardRaw = new XYZ(tf.BasisX.X, tf.BasisX.Y, 0);
                if (curForwardRaw.GetLength() < 1e-6) return false; // 退化情形，無法判斷，當作沒動過
                XYZ curForward = curForwardRaw.Normalize();

                bool originMoved = curOrigin.DistanceTo(savedOrigin) > CameraMath.MmToFeet(SectionMovedOriginToleranceMm);
                bool directionMoved = savedForward.GetLength() > 1e-6 &&
                    curForward.AngleTo(savedForward) > (SectionMovedAngleToleranceDeg * Math.PI / 180.0);
                return originMoved || directionMoved;
            }
            catch
            {
                return false;
            }
        }

        private static void DeleteOldMarkers(Document doc, View view, string cameraUniqueId, string role)
        {
            List<ElementId> toDelete = new FilteredElementCollector(doc, view.Id)
                .WhereElementIsNotElementType()
                .Where(e => TryReadLink(e, out string cid, out string r) && cid == cameraUniqueId && r == role)
                .Select(e => e.Id)
                .ToList();
            if (toDelete.Count > 0) doc.Delete(toDelete);
        }

        // ══════════════════════════════════════════════════════════
        // 進入點
        // ══════════════════════════════════════════════════════════

        internal class KeyPlanResult
        {
            public ElementId PlanViewId;
            public ElementId SectionViewId;
            public ElementId IsoViewId;
            public string Error;
            /// <summary>true＝剖面位置/方向與上次程式產生時不同（使用者在 Revit 內手動調整過），
            /// 這次只補畫相機標記，CropBox 完全沒有被覆寫。</summary>
            public bool SectionPreserved;
        }

        /// <summary>
        /// 建立或更新一台相機的 Key Plan 三視圖＋B 型標記。呼叫端須自行包在 ExternalEvent
        /// 內執行；本方法內部自己開單一交易（中文復原名稱「建立相機 Key Plan」）。
        /// </summary>
        internal static KeyPlanResult CreateOrUpdateFourViews(UIApplication uiApp, Document doc, ElementId cameraViewId, bool forceResetSection = false)
        {
            var result = new KeyPlanResult();
            if (doc == null) { result.Error = "目前沒有開啟中的文件。"; return result; }

            View3D camera = doc.GetElement(cameraViewId) as View3D;
            if (camera == null || camera.IsTemplate || !camera.IsPerspective)
            {
                result.Error = "找不到這個相機視圖（可能已被刪除），請按重新整理。";
                return result;
            }

            List<Level> levels = CameraMath.GetStoreyLevelsSorted(doc);
            CameraParams cp = ComputeCameraParams(camera, levels);
            CameraMath.CameraGeometry geom = cp.Geom;
            if (geom.LevelBelow == null)
            {
                result.Error = "找不到這台相機正下方的樓層，無法產生 Key Plan（一鍵 4 視圖需要樓層基準）。";
                return result;
            }

            string cid = camera.UniqueId;
            string camName = camera.Name;
            string planName = "Key Plan-" + camName + "-平面";
            string sectionName = "Key Plan-" + camName + "-剖面";
            string isoName = "Key Plan-" + camName + "-等角";

            double farClipFeet = cp.FarClipFeet;
            double leftRad = cp.LeftRad, rightRad = cp.RightRad;
            XYZ forwardHoriz = cp.ForwardHoriz;
            double fanRadiusFeet = cp.FanRadiusFeet;
            Logger.Info($"相機 Key Plan：「{camName}」FOV h={(cp.FovDeg.HasValue ? cp.FovDeg.Value.ToString("F1") : "?")}° v={(cp.VFovDeg.HasValue ? cp.VFovDeg.Value.ToString("F1") : "?")}° edges[{cp.EdgesLog}] [{cp.FovLog}]");
            Logger.Info("相機 Key Plan：眼睛所在房間 " + (cp.Room?.Log ?? "(none)") + $"；扇形半徑={CameraMath.FeetToMm(fanRadiusFeet):F0}mm（{cp.FanLog}）");
            Logger.Info($"相機 Key Plan：「{camName}」室內／室外判定＝{cp.ClassLog} → 套用{(cp.IsOutdoor ? "室外規則（扇形半徑 min(遠裁剪,室外半徑)、無樓層切、剖面沿視線）" : "室內規則（房間為主）")}");

            // 2026-09-25 第五輪（使用者：「以我們這間房間為主，往外留一些 buffer」）：等角 XY／平面裁剪／剖面
            // 水平範圍一律只用「眼睛所在房間」外擴 0.6m；眼睛不在房間時退回扇形（距離夾到 8m）。純查詢，交易外算好。
            IsoXY isoXY = ComputeIsoXY(geom, forwardHoriz, leftRad, rightRad, cp);
            Logger.Info("相機 Key Plan：等角 XY " + isoXY.Log);

            // 本層「頂」：以房間頂為主（挑高房間），上層樓板只認房間頂以上者（夾層不算）。純查詢。
            StoreyTop storeyTop = ComputeStoreyTop(doc, geom, isoXY, levels, cp.Room, cp.IsOutdoor);
            Logger.Info("相機 Key Plan：本層頂判定 " + storeyTop.Log);
            CameraSectionOrientationMode sectionMode = GetSectionOrientationMode();

            // ES Schema 註冊不需要交易；先在交易外建好，Schema 問題不會在交易中途才爆出來。
            // 剖面狀態只是附屬簿記：失敗只記 log，視圖照建（SaveSectionState 內部也會吞掉例外）。
            string schemaErr = SelfCheckSchemas();
            if (schemaErr != null) Logger.Error("相機 Key Plan：ES Schema 自我檢查失敗（剖面狀態簿記將略過）：" + schemaErr);

            using (Transaction t = TransactionHelper.Begin(doc, "建立相機 Key Plan"))
            {
                t.Start();
                try
                {
                    ViewPlan planView = GetOrCreatePlanView(doc, uiApp, geom.LevelBelow, planName, cid);
                    ConfigurePlanCropBox(planView, geom, forwardHoriz, leftRad, rightRad, fanRadiusFeet, isoXY, cp.IsOutdoor);
                    bool faded = IsKeyPlanFadedStyle();
                    Logger.Info("相機 Key Plan：表現法＝" + (faded ? "淡化背景" : "原始"));
                    DrawMarkerSafely(doc, "ApplyKeyPlanStylePlan", () => ApplyKeyPlanStyle(planView, faded));
                    DrawMarkerSafely(doc, "DrawPlanMarker",
                        () => DrawPlanMarker(doc, planView, geom, forwardHoriz, leftRad, rightRad, fanRadiusFeet, cid));
                    result.PlanViewId = planView.Id;

                    double? cutPlaneZ = GetPlanCutPlaneZ(doc, planView);
                    XYZ planBasisX = null;
                    try { planBasisX = planView.CropBox?.Transform?.BasisX; } catch { planBasisX = null; }
                    SectionSpec spec = ComputeSectionSpec(geom, cp, isoXY, storeyTop, sectionMode, planBasisX, cutPlaneZ);
                    Logger.Info("相機 Key Plan：剖面規格 " + spec.Log);
                    ViewSection sectionView = GetOrCreateSectionView(doc, sectionName, cid, geom, spec, forceResetSection, out bool preserved);
                    DrawMarkerSafely(doc, "ApplyKeyPlanStyleSection",
                        () => ApplyKeyPlanStyle(sectionView, faded, isSection: true, levelBelow: geom.LevelBelow, outdoor: cp.IsOutdoor, eye: geom.Eye));
                    DrawMarkerSafely(doc, "DrawSectionMarker",
                        () => DrawSectionMarker(doc, sectionView, geom, cp.CenterHoriz ?? forwardHoriz, fanRadiusFeet, cp.DownRad, cp.UpRad, cid));
                    result.SectionViewId = sectionView.Id;
                    result.SectionPreserved = preserved;
                    DrawMarkerSafely(doc, "HideOtherSectionMarkers",
                        () => HideOtherSectionMarkers(doc, planView, sectionView));

                    View3D isoView = GetOrCreateIsoView(doc, isoName, cid, geom, isoXY, storeyTop, forwardHoriz);
                    result.IsoViewId = isoView.Id;

                    t.Commit();
                }
                catch (Exception ex)
                {
                    try { if (t.HasStarted() && !t.HasEnded()) t.RollBack(); }
                    catch (Exception rbEx) { Logger.Error("相機 Key Plan：交易回滾失敗", rbEx); }
                    result.Error = "建立 Key Plan 失敗（已復原，模型未變更）：" + ex.Message;
                    Logger.Error("相機 Key Plan：CreateOrUpdateFourViews 失敗，交易已回滾", ex);
                    CameraManagerDiagnostics.RecordCaught("CreateOrUpdateFourViews", ex);
                }
            }

            return result;
        }

        // ══════════════════════════════════════════════════════════
        // 平面
        // ══════════════════════════════════════════════════════════

        private static ViewPlan GetOrCreatePlanView(Document doc, UIApplication uiApp, Level levelBelow, string planName, string cid)
        {
            View linked = FindLinkedView(doc, cid, "Plan");
            if (linked is ViewPlan linkedPlan)
            {
                // 2026-09-25（使用者決策）：相機樓層不就地修改（清單改樓層＝複製新相機），不再退役／刪除舊樓層平面。
                if (linkedPlan.Name != planName) linkedPlan.Name = MakeUniqueName(doc, planName, linkedPlan.Id);
                EnsureSectionsVisible(linkedPlan);
                return linkedPlan;
            }

            ViewPlan byName = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                .FirstOrDefault(v => !v.IsTemplate && v.Name == planName &&
                                     (v.GenLevel == null || v.GenLevel.Id.GetIdValue() == levelBelow.Id.GetIdValue()));
            if (byName != null)
            {
                TagLink(byName, cid, "Plan");
                EnsureSectionsVisible(byName);
                return byName;
            }

            ViewPlan source = FindSourcePlanForDuplication(doc, uiApp, levelBelow.Id);
            ViewPlan created;
            if (source != null)
            {
                ElementId dupId = source.Duplicate(ViewDuplicateOption.Duplicate);
                created = doc.GetElement(dupId) as ViewPlan;
            }
            else
            {
                // 規格 §5.1：這個樓層沒有既有平面可複製 → 用預設樓層平面視圖類型新建一個。
                ViewFamilyType floorPlanVft = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType))
                    .Cast<ViewFamilyType>().FirstOrDefault(v => v.ViewFamily == ViewFamily.FloorPlan);
                if (floorPlanVft == null) throw new Exception("找不到任何樓層平面視圖類型 (ViewFamily.FloorPlan)。");
                ElementId newId = ViewPlan.Create(doc, floorPlanVft.Id, levelBelow.Id).Id;
                created = doc.GetElement(newId) as ViewPlan;
            }

            if (created == null) throw new Exception("建立/複製平面視圖失敗。");
            created.Name = MakeUniqueName(doc, planName, created.Id);
            TagLink(created, cid, "Plan");
            EnsureSectionsVisible(created);
            return created;
        }

        /// <summary>
        /// 2026-09-24 實測回報：「一鍵4視圖」建好剖面後，Key Plan 平面上完全看不到 Revit 原生的
        /// 剖面線標記（可拖曳調整剖切位置的那條線）。根因：平面視圖是從既有來源平面
        /// <c>Duplicate</c> 出來的，若來源套用了 ViewTemplate，複製出來的 Key Plan 平面會直接
        /// 繼承同一個樣板；樣板若控制了 V/G 的「Sections」類別可見性（很多專案樣板會關掉剖面
        /// 符號以免出圖雜亂），複製出來的 Key Plan 平面就跟著看不到剖面線，且面板自己
        /// SetCategoryHidden 也會被樣板蓋掉。Key Plan 平面是本工具自建的輔助/診斷視圖，不是
        /// 專案出圖用的平面，拿掉樣板不影響使用者原本的樣板本身（只影響這張新視圖）；因此這裡
        /// 明確拆掉 ViewTemplateId，再確保 Sections 類別可見。兩步都包 try/catch，任何版本差異
        /// 或視圖類型不支援都不讓整個「一鍵4視圖」流程中斷。
        /// </summary>
        private static void EnsureSectionsVisible(ViewPlan plan)
        {
            try
            {
                if (plan.ViewTemplateId != ElementId.InvalidElementId)
                    plan.ViewTemplateId = ElementId.InvalidElementId;
            }
            catch { /* 忽略：某些視圖狀態下可能不允許清除樣板，不讓建立流程中斷 */ }

            try
            {
                Category sectionsCat = Category.GetCategory(plan.Document, BuiltInCategory.OST_Sections);
                if (sectionsCat != null && plan.CanCategoryBeHidden(sectionsCat.Id))
                    plan.SetCategoryHidden(sectionsCat.Id, false);
            }
            catch { /* 忽略：找不到類別或該視圖類型不支援時，不讓建立流程中斷 */ }
        }

        /// <summary>
        /// 2026-09-25（使用者要求：Key Plan 平面只顯示「我們的」剖面線）：在這張 Key Plan 平面裡，
        /// 把其他「剖面」的 viewer 標記（OST_Viewers）逐元素隱藏，只留 Key Plan-&lt;相機&gt;-剖面。
        /// 只動剖面（viewer 對應的視圖 ViewType == Section）；立面、詳圖索引（callout）、其他視圖一律不碰。
        /// viewer ↔ 剖面視圖的對應判定（依序，記 log 說明比對依據，不猜）：
        ///   ① ourSection.GetDependentElements(OST_Viewers) 回傳的元素（刪除剖面時會連帶刪除的 viewer）；
        ///   ② viewer.Name 與剖面視圖名稱完全相同（Revit 的剖面 viewer 名稱＝視圖名稱，視圖名稱在文件內唯一）；
        ///   ③ viewer 的 VIEW_NAME 參數值（「視圖名稱」）與剖面視圖名稱相同。
        /// 其他 viewer 同樣以名稱／VIEW_NAME 對應到 ViewType==Section 的視圖才算「剖面」，才會被隱藏。
        /// 每次一鍵 4 視圖與「更新相機標記」都重跑（冪等：已隱藏的 viewer 不會出現在視圖收集器裡）。
        /// 必須在交易內呼叫；任何失敗只記 log，不丟例外。
        /// </summary>
        private static void HideOtherSectionMarkers(Document doc, ViewPlan plan, ViewSection ourSection)
        {
            if (doc == null || plan == null || ourSection == null) return;
            try
            {
                var sectionViewsByName = new Dictionary<string, ViewSection>(StringComparer.Ordinal);
                foreach (ViewSection vs in new FilteredElementCollector(doc).OfClass(typeof(ViewSection)).Cast<ViewSection>())
                {
                    if (vs.IsTemplate || vs.ViewType != ViewType.Section) continue;
                    sectionViewsByName[vs.Name] = vs;
                }

                string ourName = ourSection.Name;
                var ourViewerIds = new HashSet<ElementId>();
                string matchedBy = null;
                try
                {
                    IList<ElementId> deps = ourSection.GetDependentElements(new ElementCategoryFilter(BuiltInCategory.OST_Viewers));
                    if (deps != null)
                        foreach (ElementId d in deps) if (d != ourSection.Id) ourViewerIds.Add(d);
                    if (ourViewerIds.Count > 0) matchedBy = "GetDependentElements";
                }
                catch (Exception ex) { Logger.Error("相機 Key Plan：GetDependentElements(OST_Viewers) 失敗，改用名稱比對", ex); }

                // 以文件層級收集 viewer（含已被隱藏者），用來找「我們的」並確保它沒被藏起來。
                List<Element> allViewers = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Viewers)
                    .WhereElementIsNotElementType().ToList();
                string ViewerViewName(Element e)
                {
                    try
                    {
                        string p = e.get_Parameter(BuiltInParameter.VIEW_NAME)?.AsString();
                        if (!string.IsNullOrEmpty(p)) return p;
                    }
                    catch { }
                    return e.Name;
                }
                if (ourViewerIds.Count == 0)
                {
                    foreach (Element e in allViewers)
                    {
                        if (e.Name == ourName) { ourViewerIds.Add(e.Id); matchedBy = "Name"; }
                        else if (ViewerViewName(e) == ourName) { ourViewerIds.Add(e.Id); matchedBy = matchedBy ?? "VIEW_NAME"; }
                    }
                }
                if (ourViewerIds.Count == 0)
                {
                    Logger.Info($"相機 Key Plan：找不到剖面「{ourName}」對應的 viewer，為避免誤藏，本次不隱藏其他剖面。");
                    return;
                }

                // 我們的 viewer 若曾被藏起來，解除隱藏。
                try
                {
                    var toUnhide = ourViewerIds.Where(id => doc.GetElement(id) is Element e && e.IsHidden(plan)).ToList();
                    if (toUnhide.Count > 0) plan.UnhideElements(toUnhide);
                }
                catch (Exception ex) { Logger.Error("相機 Key Plan：解除隱藏本相機剖面 viewer 失敗", ex); }

                EnsureOurSectionMarkerVisible(doc, plan, ourSection, ourViewerIds);

                var toHide = new List<ElementId>();
                int skippedNonSection = 0;
                foreach (Element e in new FilteredElementCollector(doc, plan.Id).OfCategory(BuiltInCategory.OST_Viewers)
                    .WhereElementIsNotElementType())
                {
                    if (ourViewerIds.Contains(e.Id)) continue;
                    string vn = e.Name;
                    bool isSection = (vn != null && sectionViewsByName.ContainsKey(vn))
                        || (ViewerViewName(e) is string pvn && sectionViewsByName.ContainsKey(pvn));
                    if (!isSection) { skippedNonSection++; continue; }
                    if (!e.CanBeHidden(plan)) continue;
                    toHide.Add(e.Id);
                }
                if (toHide.Count > 0) plan.HideElements(toHide);
                Logger.Info($"相機 Key Plan：平面「{plan.Name}」只留本相機剖面「{ourName}」（viewer 比對依據={matchedBy}，" +
                    $"ids={string.Join(",", ourViewerIds.Select(i => i.GetIdValue().ToString()))}），隱藏其他剖面 {toHide.Count} 個，" +
                    $"略過非剖面 viewer {skippedNonSection} 個");
            }
            catch (Exception ex)
            {
                Logger.Error("相機 Key Plan：隱藏其他剖面標記失敗（視圖照常建立）", ex);
                CameraManagerDiagnostics.RecordCaught("HideOtherSectionMarkers", ex);
            }
        }

        /// <summary>
        /// 2026-09-25（使用者回報：1:100 的 Key Plan 平面上看不到本相機剖面線，log 卻說已只留本相機剖面）。
        /// 逐項檢查並修正，每項記 log 說明「是否是原因、做了什麼」：
        ///   ① 剖面「在比例粗於…時隱藏」（SECTION_COARSER_SCALE_PULLDOWN_METRIC／_IMPERIAL，執行期以名稱解析）
        ///      若比平面比例細 → Revit 不畫剖面標記；每次都設成可接受的最粗值（5000→…→200 依序嘗試）。
        ///   ② 平面 Sections 類別被隱藏 → 取消隱藏。
        ///   ③ viewer 元素逐元素隱藏 → 已由呼叫端 UnhideElements；這裡再記一次最終 IsHidden。
        ///   ④ 平面 Display Model＝半色調會讓標記變灰 → 對 viewer 設元素取代：halftone=false、投影線色＝標記色、線寬 5。
        /// 剖面垂直範圍是否包含平面剖切面（原因②於協調者清單）在 ComputeSectionSpec 已確保並記 log。
        /// </summary>
        private static void EnsureOurSectionMarkerVisible(Document doc, ViewPlan plan, ViewSection section, ICollection<ElementId> viewerIds)
        {
            int planScale = 0;
            try { planScale = plan.Scale; } catch { }
            // ① 比例門檻
            foreach (string name in new[] { "SECTION_COARSER_SCALE_PULLDOWN_METRIC", "SECTION_COARSER_SCALE_PULLDOWN_IMPERIAL" })
            {
                try
                {
                    if (!Enum.TryParse(name, out BuiltInParameter bip)) { Logger.Info($"相機 Key Plan：此版本沒有參數 {name}，略過"); continue; }
                    var owners = new List<Element> { section };
                    foreach (ElementId vid in viewerIds) if (doc.GetElement(vid) is Element ve) owners.Add(ve);
                    foreach (Element owner in owners)
                    {
                        Parameter p = owner.get_Parameter(bip);
                        if (p == null) continue;
                        string who = owner.Id == section.Id ? "section" : "viewer";
                        if (p.IsReadOnly || p.StorageType != StorageType.Integer)
                        {
                            Logger.Info($"相機 Key Plan：{name}（{who}）唯讀或型別={p.StorageType}，值={p.AsValueString()}，略過");
                            continue;
                        }
                        int before = p.AsInteger();
                        bool wasCause = planScale > 0 && before > 0 && before < planScale;
                        int applied = before;
                        foreach (int cand in new[] { 5000, 2500, 2400, 2000, 1200, 1000, 500, 200 })
                        {
                            if (cand <= before) break;
                            try { if (p.Set(cand) && p.AsInteger() == cand) { applied = cand; break; } }
                            catch { /* 此值不在下拉選項內，試下一個 */ }
                        }
                        Logger.Info($"相機 Key Plan：剖面比例門檻 {name}（{who}）{before}→{p.AsInteger()}（{p.AsValueString()}），平面 1:{planScale}" +
                                    (wasCause ? "——原因①成立：門檻比平面比例細，剖面線原本被隱藏" : "——原因①不成立"));
                    }
                }
                catch (Exception ex) { Logger.Error($"相機 Key Plan：設定剖面比例門檻 {name} 失敗", ex); }
            }
            // ② 類別
            try
            {
                Category cat = Category.GetCategory(doc, BuiltInCategory.OST_Sections);
                if (cat != null && plan.CanCategoryBeHidden(cat.Id))
                {
                    bool hidden = plan.GetCategoryHidden(cat.Id);
                    if (hidden) plan.SetCategoryHidden(cat.Id, false);
                    Logger.Info($"相機 Key Plan：平面 Sections 類別 hidden={hidden}" + (hidden ? "——原因成立，已取消隱藏" : "——正常"));
                }
            }
            catch (Exception ex) { Logger.Error("相機 Key Plan：檢查平面 Sections 類別失敗", ex); }
            // ③＋④
            Color color = GetConfiguredMarkerColor();
            foreach (ElementId vid in viewerIds)
            {
                try
                {
                    Element ve = doc.GetElement(vid);
                    bool hiddenNow = ve != null && ve.IsHidden(plan);
                    var ogs = new OverrideGraphicSettings();
                    ogs.SetHalftone(false);
                    ogs.SetProjectionLineColor(color);
                    ogs.SetProjectionLineWeight(MarkerLineWeight);
                    plan.SetElementOverrides(vid, ogs);
                    Logger.Info($"相機 Key Plan：本相機剖面 viewer {vid.GetIdValue()} 最終 IsHidden={hiddenNow}；已設元素取代（非半色調、標記色、線寬 {MarkerLineWeight}）");
                }
                catch (Exception ex) { Logger.Error("相機 Key Plan：本相機剖面 viewer 元素取代失敗", ex); }
            }
        }

        /// <summary>比照「並排檢視」(4.9 A) 的既有視圖挑選順序：已開啟優先、其次已裁剪、其次名稱排序；
        /// 這裡再優先挑 FloorPlan 類型（Key Plan 平面本來就該是一般樓層平面）。</summary>
        private static ViewPlan FindSourcePlanForDuplication(Document doc, UIApplication uiApp, ElementId levelId)
        {
            List<ViewPlan> candidates = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                .Where(v => !v.IsTemplate && v.GenLevel != null && v.GenLevel.Id == levelId)
                .OrderBy(v => v.Name)
                .ToList();
            if (candidates.Count == 0) return null;

            HashSet<ElementId> openIds = new HashSet<ElementId>(
                uiApp?.ActiveUIDocument?.GetOpenUIViews()?.Select(uv => uv.ViewId) ?? Enumerable.Empty<ElementId>());

            List<ViewPlan> floorPlans = candidates.Where(v => v.ViewType == ViewType.FloorPlan).ToList();
            List<ViewPlan> pool = floorPlans.Count > 0 ? floorPlans : candidates;

            return pool.FirstOrDefault(v => openIds.Contains(v.Id) && v.CropBoxActive)
                ?? pool.FirstOrDefault(v => openIds.Contains(v.Id))
                ?? pool.FirstOrDefault(v => v.CropBoxActive)
                ?? pool[0];
        }

        /// <summary>
        /// 裁剪框只調整範圍（Min/Max），刻意不動 Transform——這樣裁剪框跟原平面視圖的座標系
        /// 保持一致，符合規格「keep it axis-aligned to the source view's orientation」。
        /// 框住相機位置 + 視野扇形的三個特徵點（左邊界、中心、右邊界，各在遠裁剪距離處），
        /// 外加固定 margin。
        /// </summary>
        private static void ConfigurePlanCropBox(ViewPlan planView, CameraMath.CameraGeometry geom, XYZ forwardHoriz, double leftRad, double rightRad, double farClipFeet, IsoXY isoXY = null, bool outdoor = false)
        {
            planView.CropBoxActive = true;
            planView.CropBoxVisible = true;

            BoundingBoxXYZ box = planView.CropBox;
            Transform tf = box.Transform;
            Transform inv = tf.Inverse;

            // 2026-09-25 室外規則：裁剪＝非對稱扇形（眼睛＋兩緣＋弧取樣，半徑＝farClipFeet 參數＝室外扇形半徑）外接框 +2m，
            // 在平面視圖自身座標系計算（Transform 不動）。
            if (outdoor)
            {
                try
                {
                    double m = CameraMath.MmToFeet(OutdoorPlanMarginMm);
                    XYZ le = inv.OfPoint(geom.Eye);
                    double ox0 = le.X, ox1 = le.X, oy0 = le.Y, oy1 = le.Y;
                    const int samplesO = 16;
                    for (int i = 0; i <= samplesO; i++)
                    {
                        double a = leftRad + (rightRad - leftRad) * i / samplesO;
                        XYZ l = inv.OfPoint(geom.Eye + PlanEdgeDir(forwardHoriz, a) * farClipFeet);
                        ox0 = Math.Min(ox0, l.X); ox1 = Math.Max(ox1, l.X); oy0 = Math.Min(oy0, l.Y); oy1 = Math.Max(oy1, l.Y);
                    }
                    box.Min = new XYZ(ox0 - m, oy0 - m, box.Min.Z);
                    box.Max = new XYZ(ox1 + m, oy1 + m, box.Max.Z);
                    planView.CropBox = box;
                    Logger.Info($"相機 Key Plan：平面裁剪（室外）＝扇形 R={CameraMath.FeetToMm(farClipFeet):F0}mm 外接框 +2m → {CameraMath.FeetToMm(ox1 - ox0 + 2 * m):F0}×{CameraMath.FeetToMm(oy1 - oy0 + 2 * m):F0}mm");
                    return;
                }
                catch (Exception ex)
                {
                    Logger.Error("相機 Key Plan：平面裁剪（室外規則）失敗，退回扇形規則", ex);
                    box = planView.CropBox; tf = box.Transform; inv = tf.Inverse;
                }
            }

            XYZ dirCenter = forwardHoriz;
            XYZ dirLeft = PlanEdgeDir(forwardHoriz, leftRad);
            XYZ dirRight = PlanEdgeDir(forwardHoriz, rightRad);

            List<XYZ> pts = new List<XYZ>
            {
                geom.Eye,
                geom.Eye + dirCenter * farClipFeet,
                geom.Eye + dirLeft * farClipFeet,
                geom.Eye + dirRight * farClipFeet
            };

            // 2026-09-25（Key Plan 表現法 §5.1）：有「看得到的房間聯集」（已含 +0.5m）時，裁剪＝房間聯集，
            // 必要時外擴包住完整扇形（扇形取弧上取樣點、外留 0.5m）；沒有房間聯集才退回舊規則（扇形 + 2m）。
            if (isoXY != null && isoXY.HasRoomUnion)
            {
                try
                {
                    double fanM = Cm(50);
                    double rx0 = isoXY.RoomMinX, ry0 = isoXY.RoomMinY, rx1 = isoXY.RoomMaxX, ry1 = isoXY.RoomMaxY;
                    var fan = new List<XYZ> { geom.Eye };
                    const int samples = 16;
                    for (int i = 0; i <= samples; i++)
                    {
                        double a = leftRad + (rightRad - leftRad) * i / samples;
                        fan.Add(geom.Eye + PlanEdgeDir(forwardHoriz, a) * farClipFeet);
                    }
                    double z = geom.Eye.Z;
                    var all = new List<XYZ>
                    {
                        new XYZ(rx0, ry0, z), new XYZ(rx1, ry0, z), new XYZ(rx0, ry1, z), new XYZ(rx1, ry1, z)
                    };
                    double ax0 = double.MaxValue, ax1 = double.MinValue, ay0 = double.MaxValue, ay1 = double.MinValue;
                    foreach (XYZ p in all)
                    {
                        XYZ l = inv.OfPoint(p);
                        ax0 = Math.Min(ax0, l.X); ax1 = Math.Max(ax1, l.X); ay0 = Math.Min(ay0, l.Y); ay1 = Math.Max(ay1, l.Y);
                    }
                    foreach (XYZ p in fan)
                    {
                        XYZ l = inv.OfPoint(p);
                        ax0 = Math.Min(ax0, l.X - fanM); ax1 = Math.Max(ax1, l.X + fanM);
                        ay0 = Math.Min(ay0, l.Y - fanM); ay1 = Math.Max(ay1, l.Y + fanM);
                    }
                    box.Min = new XYZ(ax0, ay0, box.Min.Z);
                    box.Max = new XYZ(ax1, ay1, box.Max.Z);
                    planView.CropBox = box;
                    Logger.Info($"相機 Key Plan：平面裁剪＝房間聯集∪完整扇形(+0.5m) → {CameraMath.FeetToMm(ax1 - ax0):F0}×{CameraMath.FeetToMm(ay1 - ay0):F0}mm");
                    return;
                }
                catch (Exception ex)
                {
                    Logger.Error("相機 Key Plan：平面裁剪（房間聯集規則）失敗，退回扇形規則", ex);
                    box = planView.CropBox; tf = box.Transform; inv = tf.Inverse;
                }
            }

            double marginFeet = Cm(PlanMarginCm);
            double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
            foreach (XYZ p in pts)
            {
                XYZ local = inv.OfPoint(p);
                minX = Math.Min(minX, local.X); maxX = Math.Max(maxX, local.X);
                minY = Math.Min(minY, local.Y); maxY = Math.Max(maxY, local.Y);
            }

            box.Min = new XYZ(minX - marginFeet, minY - marginFeet, box.Min.Z);
            box.Max = new XYZ(maxX + marginFeet, maxY + marginFeet, box.Max.Z);
            planView.CropBox = box;
        }

        /// <summary>2026-09-25：平面上的視錐邊界方向。edgeRad＝相對正前方的帶號角（相機右為正）；RotateHorizontal
        /// 以逆時針為正，而相機右＝forward×up 在平面上是「順時針」側（forward=+Y → right=+X），所以旋轉 −edgeRad。
        /// 例：左緣 −44° → 逆時針 44°（左側），右緣 +25° → 順時針 25°（右側）。</summary>
        private static XYZ PlanEdgeDir(XYZ forwardHoriz, double edgeRad) => RotateHorizontal(forwardHoriz, -edgeRad);

        private static XYZ RotateHorizontal(XYZ v, double angleRad)
        {
            double cos = Math.Cos(angleRad), sin = Math.Sin(angleRad);
            return new XYZ(v.X * cos - v.Y * sin, v.X * sin + v.Y * cos, v.Z);
        }

        // ══════════════════════════════════════════════════════════
        // 剖面
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// forceReset＝true 時無條件覆寫（「重設剖面位置」用）；否則先比對使用者是否手動調整過
        /// （<see cref="HasUserMovedSection"/>），調整過就只補 ES 連結／改名、CropBox 完全不動，
        /// preserved 回傳 true 讓呼叫端在狀態列告知使用者。
        /// </summary>
        private static ViewSection GetOrCreateSectionView(Document doc, string sectionName, string cid,
            CameraMath.CameraGeometry geom, SectionSpec spec, bool forceReset, out bool preserved)
        {
            preserved = false;
            BoundingBoxXYZ box = BuildSectionBox(geom, spec);
            XYZ forwardHoriz = spec.Axis; // ES 狀態記的是「剖面 X 軸」（沿視線模式＝水平視線；對齊房間模式＝房間長軸）

            View linked = FindLinkedView(doc, cid, "Section");
            ViewSection existing = linked as ViewSection;
            if (existing == null)
            {
                existing = new FilteredElementCollector(doc).OfClass(typeof(ViewSection)).Cast<ViewSection>()
                    .FirstOrDefault(v => !v.IsTemplate && v.Name == sectionName);
            }

            if (existing != null)
            {
                if (existing.Name != sectionName) existing.Name = MakeUniqueName(doc, sectionName, existing.Id);

                if (!forceReset && HasUserMovedSection(existing, geom.Eye, forwardHoriz))
                {
                    preserved = true;
                }
                else
                {
                    existing.CropBox = box;
                    SaveSectionState(existing, box.Transform.Origin, forwardHoriz); // 2026-09-25：原點可能不是眼睛（見 SectionSpec.Origin）
                }
                TagLink(existing, cid, "Section");
                return existing;
            }

            ViewFamilyType sectionVft = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType))
                .Cast<ViewFamilyType>().FirstOrDefault(v => v.ViewFamily == ViewFamily.Section);
            if (sectionVft == null) throw new Exception("找不到任何剖面視圖類型 (ViewFamily.Section)。");

            ViewSection created = ViewSection.CreateSection(doc, sectionVft.Id, box);
            created.Name = MakeUniqueName(doc, sectionName, created.Id);
            TagLink(created, cid, "Section");
            SaveSectionState(created, box.Transform.Origin, forwardHoriz);
            return created;
        }

        /// <summary>
        /// 「重設剖面位置」：無條件把既有剖面（若存在）的 CropBox 強制覆寫回目前相機的視線
        /// 位置，並補畫標記。呼叫端須自行包在 ExternalEvent + Transaction 內（沿用既有
        /// <see cref="CreateOrUpdateFourViews"/> 的呼叫慣例，這裡直接重用整套流程、只是把
        /// forceResetSection 設為 true）。
        /// </summary>
        internal static KeyPlanResult ResetSectionToSightLine(UIApplication uiApp, Document doc, ElementId cameraViewId)
            => CreateOrUpdateFourViews(uiApp, doc, cameraViewId, forceResetSection: true);

        /// <summary>
        /// 供迷你 key plan（KeyPlanSection）疊畫「目前剖面線」用：讀取這台相機目前連結的剖面
        /// 視圖，回傳其 CropBox 沿剖面本身 X 軸（＝視線水平方向）的兩個端點（模型座標，Z 分量
        /// 取自 CropBox.Transform，供 2D 平面投影時忽略即可）。找不到連結剖面時回傳 false——
        /// 這是唯讀查詢，不開 Transaction，呼叫端仍需經 ExternalEventManager 排程（因為要讀
        /// Document）。回傳的線段反映使用者手動調整後的實際剖面位置（不是程式原本算出來的
        /// 視線），符合「迷你地圖上的剖面線要跟著使用者調整」的需求。
        /// </summary>
        /// <summary>回傳目前文件中「已經有 Key Plan 剖面」的相機 UniqueId 集合（唯讀，只掃
        /// ViewSection）。供快照標記 CameraRow.HasKeyPlanSection，面板據此對沒有剖面的相機完全
        /// 不排 RefreshSelectedSectionLine。</summary>
        internal static HashSet<string> GetCameraUniqueIdsWithSection(Document doc)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            if (doc == null || Schema.Lookup(SchemaGuid) == null) return set;
            foreach (ViewSection v in new FilteredElementCollector(doc).OfClass(typeof(ViewSection)).Cast<ViewSection>())
            {
                if (v.IsTemplate) continue;
                if (TryReadLink(v, out string cid, out string role) && role == "Section" && !string.IsNullOrEmpty(cid))
                    set.Add(cid);
            }
            return set;
        }

        /// <summary>「④ 全部符合畫面」用：這台相機（UniqueId）以 ES 連結的 Key Plan 平面／剖面／等角視圖 Id（唯讀）。</summary>
        internal static List<ElementId> GetLinkedKeyPlanViewIds(Document doc, string cameraUniqueId)
        {
            var ids = new List<ElementId>();
            if (doc == null || string.IsNullOrEmpty(cameraUniqueId)) return ids;
            foreach (string role in new[] { "Plan", "Section", "Iso" })
            {
                View v = FindLinkedView(doc, cameraUniqueId, role);
                if (v != null) ids.Add(v.Id);
            }
            return ids;
        }

        internal static bool TryGetSectionLineForCamera(Document doc, string cameraUniqueId, out XYZ p1, out XYZ p2)
        {
            p1 = null;
            p2 = null;
            if (doc == null || string.IsNullOrEmpty(cameraUniqueId)) return false;

            View linked = FindLinkedView(doc, cameraUniqueId, "Section");
            if (!(linked is ViewSection section)) return false;

            try
            {
                BoundingBoxXYZ box = section.CropBox;
                Transform tf = box.Transform;
                p1 = tf.OfPoint(new XYZ(box.Min.X, 0, 0));
                p2 = tf.OfPoint(new XYZ(box.Max.X, 0, 0));
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 剖面座標系：X＝沿視線水平方向（forwardHoriz）、Y＝世界 Z（上）、
        /// Z＝BasisX×BasisY（看向的方向，即「從側面看」的深度軸）。Origin 放在相機眼睛，
        /// 所以剖面平面剛好包含視線水平投影（規格「cut plane contains camera's horizontal
        /// sight line」）。範圍：X 從相機背後一點到遠裁剪之外一點；Y 從樓層下方 1m 到眼睛上方
        /// 3m（規格 §5.1）；Z（厚度）前後各 1.5m（未查證的假設值，見檔案頂端常數註解）。
        /// </summary>
        private static BoundingBoxXYZ BuildSectionBox(CameraMath.CameraGeometry geom, SectionSpec spec)
        {
            XYZ up = XYZ.BasisZ;
            XYZ axis = spec.Axis;
            XYZ viewDir = axis.CrossProduct(up);
            if (viewDir.GetLength() < 1e-6) viewDir = XYZ.BasisX;
            viewDir = viewDir.Normalize();

            Transform tf = Transform.Identity;
            tf.Origin = spec.Origin ?? geom.Eye;
            tf.BasisX = axis;
            tf.BasisY = up;
            tf.BasisZ = viewDir;

            double halfDepth = Cm(SectionDepthHalfCm);
            double minZ = double.IsNaN(spec.MinZ) ? -halfDepth : spec.MinZ;
            double maxZ = double.IsNaN(spec.MaxZ) ? halfDepth : spec.MaxZ;
            return new BoundingBoxXYZ
            {
                Transform = tf,
                Min = new XYZ(spec.MinX, spec.MinY, minZ),
                Max = new XYZ(spec.MaxX, spec.MaxY, maxZ)
            };
        }

        /// <summary>剖面規格（交易外算好）：X 軸方向（水平）、以眼睛為原點的 X/Y 範圍（feet）。</summary>
        internal sealed class SectionSpec
        {
            public XYZ Axis;
            /// <summary>2026-09-25：剖面原點（剖切平面通過的點）。沿視線＝眼睛；對齊房間長邊＝畫面中心線上
            /// 半個扇形半徑處（Z＝眼睛高）。null＝眼睛。MinX/MaxX/MinY/MaxY 都是相對此原點。</summary>
            public XYZ Origin;
            /// <summary>深度（剖面 Z）範圍；預設 ±1.5m，眼睛不在範圍內時外擴讓眼睛仍在深度內。</summary>
            public double MinZ = double.NaN, MaxZ = double.NaN;
            public double MinX, MaxX, MinY, MaxY;
            public CameraSectionOrientationMode ModeUsed;
            public string Log;
        }

        private static CameraSectionOrientationMode GetSectionOrientationMode()
        {
            try { return ConfigManager.Instance.Settings.CameraManager.SectionOrientationMode; }
            catch { return CameraSectionOrientationMode.AlongRoomLongAxis; }
        }

        /// <summary>
        /// 2026-09-25 第五輪（規格 §5.1）：
        ///   方向——對齊房間長邊（預設）：房間長軸、通過眼睛；方向號誌取「與相機水平前方向同側」（剖面上視線箭頭朝右）。
        ///           沿視線：水平視線方向（舊規則）。找不到房間 → 一律沿視線。
        ///   水平範圍——對齊房間：房間頂點沿長軸投影範圍 ±0.6m；沿視線：視線穿越本房間 BoundingBox 的區段 ±0.6m
        ///           （眼睛後方至少 1m）；沒有房間：[−1m, 扇形半徑 +0.6m]。
        ///   垂直範圍——下限＝樓層 −1m；上限＝房間頂 +0.5m（無房間：上層樓板底 +0.5m，算不出來＝眼睛 +3m）；
        ///           並確保包含 Key Plan 平面的剖切面高程（否則剖面線在平面上不顯示），不含就外擴並記 log。
        /// 任何一步失敗只記 log、退回較簡單的規則。
        /// </summary>
        private static SectionSpec ComputeSectionSpec(CameraMath.CameraGeometry geom, CameraParams cp, IsoXY isoXY, StoreyTop storeyTop,
            CameraSectionOrientationMode mode, XYZ planBasisX, double? cutPlaneZ)
        {
            // 2026-09-25：「沿視線」一律沿畫面中心線（水平視錐左右緣角平分線），不是相機正前方。
            XYZ center = cp.CenterHoriz ?? cp.ForwardHoriz;
            var s = new SectionSpec { Axis = center, ModeUsed = CameraSectionOrientationMode.AlongSightLine, Origin = geom.Eye };
            double buf = CameraMath.MmToFeet(RoomBufferMm);
            var log = new System.Text.StringBuilder();
            log.Append($"中心線 yaw={cp.CenterYawDeg:F1}° ");
            bool done = false;

            if (cp.IsOutdoor)
            {
                // 2026-09-25 室外規則：一律沿視線（忽略「對齊房間長邊」設定）；水平＝眼睛後 2m … 扇形半徑。
                s.Axis = center; s.ModeUsed = CameraSectionOrientationMode.AlongSightLine;
                s.MinX = -CameraMath.MmToFeet(OutdoorBehindEyeMm);
                s.MaxX = Math.Max(cp.FanRadiusFeet, Cm(100));
                log.Append($"OUTDOOR mode=沿視線（室外強制，設定={mode}）X[{CameraMath.FeetToMm(s.MinX):F0},{CameraMath.FeetToMm(s.MaxX):F0}]mm");
                done = true;
            }

            if (!done && mode == CameraSectionOrientationMode.AlongRoomLongAxis)
            {
                try
                {
                    if (cp.Room?.Room != null && cp.Room.Pts.Count >= 3 &&
                        TryRoomLongAxis(cp.Room, geom.Eye, planBasisX, center, out XYZ axis, out double t0, out double t1, out string axLog))
                    {
                        s.Axis = axis; s.ModeUsed = CameraSectionOrientationMode.AlongRoomLongAxis;
                        s.MinX = t0 - buf; s.MaxX = t1 + buf;
                        log.Append($"mode=對齊房間長邊 {axLog} X[{CameraMath.FeetToMm(s.MinX):F0},{CameraMath.FeetToMm(s.MaxX):F0}]mm(相對眼睛)");
                        // 2026-09-25：剖切線改通過「可見範圍中心」＝中心線上半個扇形半徑處（半徑已夾在房間 +0.6m 內），
                        // 方向不變；X 範圍改成相對新原點；眼睛若跑出剖面深度範圍就外擴深度，讓眼睛標記仍在範圍內。
                        try
                        {
                            double half = Math.Max(0.0, cp.FanRadiusFeet) / 2.0;
                            XYZ c = geom.Eye + center * half;
                            XYZ origin = new XYZ(c.X, c.Y, geom.Eye.Z);
                            double shift = (origin - geom.Eye).DotProduct(axis);
                            s.Origin = origin;
                            s.MinX -= shift; s.MaxX -= shift;
                            XYZ viewDir = axis.CrossProduct(XYZ.BasisZ);
                            viewDir = viewDir.GetLength() > 1e-6 ? viewDir.Normalize() : XYZ.BasisX;
                            double d = (geom.Eye - origin).DotProduct(viewDir);
                            double hd = Cm(SectionDepthHalfCm), dm = Cm(50);
                            s.MinZ = Math.Min(-hd, d - dm);
                            s.MaxZ = Math.Max(hd, d + dm);
                            log.Append($" → 原點＝中心線 {CameraMath.FeetToMm(half):F0}mm 處 ({CameraMath.FeetToMm(origin.X):F0},{CameraMath.FeetToMm(origin.Y):F0})mm " +
                                       $"X[{CameraMath.FeetToMm(s.MinX):F0},{CameraMath.FeetToMm(s.MaxX):F0}]mm 眼睛深度={CameraMath.FeetToMm(d):F0}mm " +
                                       $"深度[{CameraMath.FeetToMm(s.MinZ):F0},{CameraMath.FeetToMm(s.MaxZ):F0}]mm" +
                                       (Math.Abs(d) > hd - dm ? "（已外擴含眼睛）" : ""));
                        }
                        catch (Exception exC)
                        {
                            Logger.Error("相機 Key Plan：剖面原點移到可見範圍中心失敗，維持通過眼睛", exC);
                            s.Origin = geom.Eye; s.MinX = t0 - buf; s.MaxX = t1 + buf; s.MinZ = s.MaxZ = double.NaN;
                        }
                        done = true;
                    }
                    else log.Append("mode=對齊房間長邊 → 無房間/長軸算不出 → 退回沿視線; ");
                }
                catch (Exception ex)
                {
                    Logger.Error("相機 Key Plan：房間長軸計算失敗，剖面退回沿視線", ex);
                    log.Append("mode=對齊房間長邊 失敗 → 退回沿視線; ");
                }
            }

            if (!done)
            {
                s.Axis = center; s.ModeUsed = CameraSectionOrientationMode.AlongSightLine; s.Origin = geom.Eye;
                if (isoXY != null && isoXY.HasSightRange)
                {
                    s.MinX = Math.Min(isoXY.SightMinT - buf, -Cm(SectionBackMarginCm));
                    s.MaxX = Math.Max(isoXY.SightMaxT + buf, Cm(100));
                    log.Append($"mode=沿視線 sight[{CameraMath.FeetToMm(isoXY.SightMinT):F0},{CameraMath.FeetToMm(isoXY.SightMaxT):F0}]mm ±0.6m → X[{CameraMath.FeetToMm(s.MinX):F0},{CameraMath.FeetToMm(s.MaxX):F0}]mm");
                }
                else
                {
                    s.MinX = -Cm(SectionBackMarginCm);
                    s.MaxX = cp.FanRadiusFeet + buf;
                    log.Append($"mode=沿視線 (no sight range) X[{CameraMath.FeetToMm(s.MinX):F0},{CameraMath.FeetToMm(s.MaxX):F0}]mm");
                }
            }

            double levelZ = geom.LevelBelow.ProjectElevation;
            double bottomZ = levelZ - Cm(SectionBelowLevelCm);
            double topZ;
            string topSrc;
            if (storeyTop.Outdoor)
            {
                bottomZ = storeyTop.OutdoorBottomZ;
                topZ = storeyTop.OutdoorTopZ;
                topSrc = "outdoor (same Z as iso)";
            }
            else if (storeyTop.RoomTopZ.HasValue)
            {
                topZ = storeyTop.RoomTopZ.Value + CameraMath.MmToFeet(RoomTopSectionBufferMm);
                topSrc = "roomTop+0.5m";
            }
            else
            {
                topZ = storeyTop.SlabBottomZ + Cm(SectionAboveSlabCm);
                topSrc = "slabBottom+0.5m";
            }
            if (!storeyTop.Outdoor && (double.IsNaN(topZ) || double.IsInfinity(topZ) || topZ - geom.Eye.Z < Cm(30)))
            {
                topZ = geom.Eye.Z + Cm(SectionAboveEyeCm);
                topSrc = "fallback eye+3m";
            }
            string cutSrc = "cutPlane=?";
            if (cutPlaneZ.HasValue)
            {
                double c = cutPlaneZ.Value, eps = Cm(10);
                bool inside = c > bottomZ + eps && c < topZ - eps;
                cutSrc = $"cutPlane={CameraMath.FeetToMm(c):F0}mm inside={inside}";
                if (!inside)
                {
                    if (c <= bottomZ + eps) bottomZ = c - Cm(50);
                    if (c >= topZ - eps) topZ = c + Cm(50);
                    cutSrc += " → 外擴垂直範圍以包含平面剖切面";
                }
            }
            s.MinY = bottomZ - geom.Eye.Z;
            s.MaxY = topZ - geom.Eye.Z;
            log.Append($" | Z[{CameraMath.FeetToMm(bottomZ):F0},{CameraMath.FeetToMm(topZ):F0}]mm (bottom={(storeyTop.Outdoor ? "outdoor" : "level−1m")}, top={topSrc}) {cutSrc}");
            XYZ o = s.Origin ?? geom.Eye;
            log.Append($" | 剖面原點=({CameraMath.FeetToMm(o.X):F0},{CameraMath.FeetToMm(o.Y):F0},{CameraMath.FeetToMm(o.Z):F0})mm{(o.IsAlmostEqualTo(geom.Eye) ? "＝眼睛" : "")} axis=({s.Axis.X:F3},{s.Axis.Y:F3})");
            s.Log = log.ToString();
            return s;
        }

        /// <summary>
        /// 房間長軸：候選方向＝Key Plan 平面的 X 軸（軸對齊）＋每條邊界線段方向；對每個方向算「沿該方向／
        /// 其垂直方向」的投影範圍面積，取最小者（有一邊與某邊界共線的最小外接矩形；矩形房間精確）。
        /// 最小面積若沒有比軸對齊框小 10% 以上，就用軸對齊框（剖面線在平面上保持水平/垂直）。
        /// 長短邊差 &lt;15%（近正方形）→ 兩軸中取與相機水平前方向較接近者。號誌：與相機前方向同側。
        /// t0/t1＝房間頂點沿軸、相對眼睛的投影範圍（feet）。
        /// </summary>
        private static bool TryRoomLongAxis(RoomCtx rc, XYZ eye, XYZ planBasisX, XYZ forwardHoriz,
            out XYZ axis, out double t0, out double t1, out string log)
        {
            axis = null; t0 = t1 = 0; log = "";
            XYZ px = planBasisX != null ? new XYZ(planBasisX.X, planBasisX.Y, 0) : XYZ.BasisX;
            px = px.GetLength() > 1e-6 ? px.Normalize() : XYZ.BasisX;

            void Extent(XYZ u, out double lu, out double lv)
            {
                XYZ v = new XYZ(-u.Y, u.X, 0);
                double a0 = double.MaxValue, a1 = double.MinValue, b0 = double.MaxValue, b1 = double.MinValue;
                foreach (XYZ p in rc.Pts)
                {
                    double a = p.X * u.X + p.Y * u.Y, b = p.X * v.X + p.Y * v.Y;
                    a0 = Math.Min(a0, a); a1 = Math.Max(a1, a); b0 = Math.Min(b0, b); b1 = Math.Max(b1, b);
                }
                lu = a1 - a0; lv = b1 - b0;
            }

            Extent(px, out double aabbU, out double aabbV);
            double aabbArea = aabbU * aabbV;
            XYZ bestU = px; double bestArea = aabbArea, bestLu = aabbU, bestLv = aabbV;
            int tested = 0;
            foreach (var seg in rc.Segs)
            {
                XYZ d = new XYZ(seg.Value.X - seg.Key.X, seg.Value.Y - seg.Key.Y, 0);
                if (d.GetLength() < 0.1) continue;
                d = d.Normalize();
                Extent(d, out double lu, out double lv);
                tested++;
                if (lu * lv < bestArea - 1e-9) { bestArea = lu * lv; bestU = d; bestLu = lu; bestLv = lv; }
                if (tested > 400) break;
            }
            bool useObb = bestArea < 0.9 * aabbArea;
            XYZ u0 = useObb ? bestU : px;
            double Lu = useObb ? bestLu : aabbU, Lv = useObb ? bestLv : aabbV;
            XYZ v0 = new XYZ(-u0.Y, u0.X, 0);
            if (!(Lu > 1e-6) || !(Lv > 1e-6)) return false;

            XYZ chosen;
            string why;
            if (Math.Abs(Lu - Lv) / Math.Max(Lu, Lv) < 0.15)
            {
                chosen = Math.Abs(u0.DotProduct(forwardHoriz)) >= Math.Abs(v0.DotProduct(forwardHoriz)) ? u0 : v0;
                why = "近正方形→取較接近視線的軸";
            }
            else
            {
                chosen = Lu >= Lv ? u0 : v0;
                why = "長軸";
            }
            if (chosen.DotProduct(forwardHoriz) < 0) chosen = chosen.Negate();
            axis = chosen.Normalize();

            double mn = double.MaxValue, mx = double.MinValue;
            foreach (XYZ p in rc.Pts)
            {
                double t = (p.X - eye.X) * axis.X + (p.Y - eye.Y) * axis.Y;
                mn = Math.Min(mn, t); mx = Math.Max(mx, t);
            }
            t0 = Math.Min(mn, 0); t1 = Math.Max(mx, 0);
            double sightAngle = Math.Acos(Math.Max(-1, Math.Min(1, Math.Abs(axis.DotProduct(forwardHoriz))))) * 180 / Math.PI;
            log = $"{(useObb ? "OBB" : "AABB(平面方向)")} L={CameraMath.FeetToMm(Math.Max(Lu, Lv)):F0}×{CameraMath.FeetToMm(Math.Min(Lu, Lv)):F0}mm {why} " +
                  $"axis=({axis.X:F3},{axis.Y:F3}) 與視線夾角={sightAngle:F1}° 房間沿軸[{CameraMath.FeetToMm(mn):F0},{CameraMath.FeetToMm(mx):F0}]mm";
            return true;
        }

        /// <summary>Key Plan 平面的剖切面絕對高程（feet）；讀不到回傳 null。</summary>
        private static double? GetPlanCutPlaneZ(Document doc, ViewPlan plan)
        {
            try
            {
                PlanViewRange vr = plan.GetViewRange();
                Level l = doc.GetElement(vr.GetLevelId(PlanViewPlane.CutPlane)) as Level ?? plan.GenLevel;
                if (l == null) return null;
                return l.ProjectElevation + vr.GetOffset(PlanViewPlane.CutPlane);
            }
            catch (Exception ex)
            {
                Logger.Error("相機 Key Plan：讀取平面剖切面高程失敗", ex);
                return null;
            }
        }

        // ══════════════════════════════════════════════════════════
        // 等角
        // ══════════════════════════════════════════════════════════

        private static View3D GetOrCreateIsoView(Document doc, string isoName, string cid,
            CameraMath.CameraGeometry geom, IsoXY isoXY, StoreyTop storeyTop, XYZ forwardHoriz)
        {
            // 2026-09-24 第四輪：三個分支（ES 連結／同名既有／新建）一律「設定 SectionBox → 確保剖面框
            // 類別可見」，不改視圖的 DisplayStyle（使用者現況 FlatColors 維持不動）。
            BoundingBoxXYZ box = BuildIsoSectionBox(doc, geom, isoXY, storeyTop);
            ViewOrientation3D orientation = BuildIsoOrientation(box, forwardHoriz);

            View linked = FindLinkedView(doc, cid, "Iso");
            if (linked is View3D linkedIso)
            {
                if (linkedIso.Name != isoName) linkedIso.Name = MakeUniqueName(doc, isoName, linkedIso.Id);
                linkedIso.SetOrientation(orientation);
                if (!linkedIso.IsSectionBoxActive) linkedIso.IsSectionBoxActive = true;
                linkedIso.SetSectionBox(box);
                EnsureSectionBoxVisible(linkedIso);
                return linkedIso;
            }

            View3D byName = new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>()
                .FirstOrDefault(v => !v.IsTemplate && !v.IsPerspective && v.Name == isoName);
            if (byName != null)
            {
                byName.SetOrientation(orientation);
                byName.IsSectionBoxActive = true;
                byName.SetSectionBox(box);
                EnsureSectionBoxVisible(byName);
                TagLink(byName, cid, "Iso");
                return byName;
            }

            ViewFamilyType threeDVft = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType))
                .Cast<ViewFamilyType>().FirstOrDefault(v => v.ViewFamily == ViewFamily.ThreeDimensional);
            if (threeDVft == null) throw new Exception("找不到任何 3D 視圖類型 (ViewFamily.ThreeDimensional)。");

            View3D created = View3D.CreateIsometric(doc, threeDVft.Id);
            created.Name = MakeUniqueName(doc, isoName, created.Id);
            created.SetOrientation(orientation);
            created.IsSectionBoxActive = true;
            created.SetSectionBox(box);
            EnsureSectionBoxVisible(created);
            TagLink(created, cid, "Iso");
            return created;
        }

        /// <summary>
        /// 讓等角圖看得到剖面框外框，使用者才能直接拖曳調整範圍。新建 3D 視圖可能帶到
        /// ViewFamilyType 的預設樣板，或「剖面框」類別被隱藏——兩者都會讓框線消失。
        /// 只動 Key Plan 等角圖本身，不改任何使用者的視圖樣板；失敗只記 log。
        /// </summary>
        private static void EnsureSectionBoxVisible(View3D iso)
        {
            try
            {
                if (iso.ViewTemplateId != ElementId.InvalidElementId)
                    iso.ViewTemplateId = ElementId.InvalidElementId;
            }
            catch (Exception ex) { Logger.Error($"Key Plan 等角圖移除視圖樣板失敗: {ex.Message}"); }

            try
            {
                ElementId sectionBoxCat = new ElementId(BuiltInCategory.OST_SectionBox);
                bool canHide = iso.CanCategoryBeHidden(sectionBoxCat);
                bool hiddenBefore = canHide && iso.GetCategoryHidden(sectionBoxCat);
                if (hiddenBefore) iso.SetCategoryHidden(sectionBoxCat, false);
                bool hiddenAfter = canHide && iso.GetCategoryHidden(sectionBoxCat);
                string style = "?";
                try { style = iso.DisplayStyle.ToString(); } catch { }
                Logger.Info($"相機 Key Plan：等角 id={iso.Id.GetIdValue()} 剖面框類別 canHide={canHide} hiddenBefore={hiddenBefore} hiddenAfter={hiddenAfter} " +
                    $"sectionBoxActive={iso.IsSectionBoxActive} template={iso.ViewTemplateId?.GetIdValue()} displayStyle={style}（不變更）");
            }
            catch (Exception ex) { Logger.Error($"Key Plan 等角圖顯示剖面框類別失敗: {ex.Message}"); }

            // 2026-09-25：剖面框仍看不到（OST_SectionBox 未隱藏、Section Box=是、無樣板）——主要假設：
            // 視圖的「註解類別」總開關被關（AreAnnotationCategoriesHidden==true；剖面框屬註解類別）。
            // 打開總開關，再逐類別隱藏等角不需要的註解，讓畫面只剩剖面框外框。
            try
            {
                bool annBefore = iso.AreAnnotationCategoriesHidden;
                if (annBefore) iso.AreAnnotationCategoriesHidden = false;
                bool annAfter = iso.AreAnnotationCategoriesHidden;
                bool analytical = false;
                try { analytical = iso.AreAnalyticalModelCategoriesHidden; } catch { }
                Logger.Info($"相機 Key Plan：等角 id={iso.Id.GetIdValue()} AreAnnotationCategoriesHidden before={annBefore} after={annAfter}" +
                    $"（AreAnalyticalModelCategoriesHidden={analytical}，與剖面框無關，不變更）");
            }
            catch (Exception ex) { Logger.Error("相機 Key Plan：等角圖打開註解類別總開關失敗", ex); }

            try
            {
                var names = new List<string>(IsoDeclutterCategoryNames);
                int hidden = HideCategoriesByName(iso, names, includeAllTags: true, keepRoomTags: false, hide: true);
                Logger.Info($"相機 Key Plan：等角 id={iso.Id.GetIdValue()} 隱藏雜項註解類別 {hidden} 個（剖面框保留）");
            }
            catch (Exception ex) { Logger.Error("相機 Key Plan：等角圖隱藏雜項註解類別失敗", ex); }
        }

        // ══════════════════════════════════════════════════════════
        // Key Plan 表現法（2026-09-25，規格 §5.1「Key Plan 表現法」）
        // ══════════════════════════════════════════════════════════

        /// <summary>平面／剖面 Key Plan「淡化背景」要隱藏的註解類別（另外 *Tags 全部，房間標籤除外）。
        /// 不含 OST_Sections（會連本相機剖面一起藏掉；其他剖面改逐元素隱藏，見 HideOtherSectionMarkers）。</summary>
        private static readonly string[] KeyPlanDeclutterCategoryNames =
        {
            "OST_Grids", "OST_Levels", "OST_Dimensions", "OST_TextNotes", "OST_CLines", "OST_VolumeOfInterest",
            "OST_Elev", "OST_ElevationMarks", "OST_Callouts", "OST_CalloutHeads", "OST_CalloutBoundary",
            "OST_SpotElevations", "OST_SpotCoordinates", "OST_SpotSlopes", "OST_Matchline", "OST_RevisionClouds"
        };

        /// <summary>等角圖要隱藏的註解類別（剖面框 OST_SectionBox 保留）；等角圖本來就不需要剖面標記。</summary>
        private static readonly string[] IsoDeclutterCategoryNames =
        {
            "OST_Grids", "OST_Levels", "OST_Dimensions", "OST_TextNotes", "OST_CLines", "OST_VolumeOfInterest",
            "OST_Elev", "OST_ElevationMarks", "OST_Callouts", "OST_CalloutHeads", "OST_CalloutBoundary",
            "OST_Sections", "OST_SpotElevations", "OST_SpotCoordinates", "OST_SpotSlopes", "OST_Matchline", "OST_RevisionClouds"
        };

        /// <summary>依 BuiltInCategory 名稱（字串，執行期解析，避免各版 SDK 缺列舉值時編譯失敗）隱藏／
        /// 取消隱藏類別；includeAllTags＝另外處理所有「*Tags」註解類別（keepRoomTags＝跳過 OST_RoomTags）。
        /// 只動 CanCategoryBeHidden 的類別；回傳實際變更數。個別失敗只記 log。</summary>
        private static int HideCategoriesByName(View view, IEnumerable<string> names, bool includeAllTags, bool keepRoomTags, bool hide)
        {
            Document doc = view.Document;
            var ids = new Dictionary<long, KeyValuePair<ElementId, string>>();
            foreach (string n in names)
            {
                if (!Enum.TryParse(n, out BuiltInCategory bic)) { Logger.Info($"相機 Key Plan：此版本沒有類別 {n}，略過"); continue; }
                Category c = null;
                try { c = Category.GetCategory(doc, bic); } catch { }
                if (c != null) ids[c.Id.GetIdValue()] = new KeyValuePair<ElementId, string>(c.Id, n);
            }
            if (includeAllTags)
            {
                foreach (Category c in doc.Settings.Categories)
                {
                    try
                    {
                        if (c == null || c.CategoryType != CategoryType.Annotation) continue;
                        long v = c.Id.GetIdValue();
                        if (v >= 0) continue;
                        string bn = Enum.GetName(typeof(BuiltInCategory), (int)v);
                        if (bn == null || !bn.EndsWith("Tags", StringComparison.Ordinal)) continue;
                        if (keepRoomTags && bn == "OST_RoomTags") continue;
                        ids[v] = new KeyValuePair<ElementId, string>(c.Id, bn);
                    }
                    catch { }
                }
            }
            int changed = 0;
            foreach (var kv in ids)
            {
                try
                {
                    ElementId cid = kv.Value.Key;
                    if (cid == null || !view.CanCategoryBeHidden(cid)) continue;
                    if (view.GetCategoryHidden(cid) == hide) continue;
                    view.SetCategoryHidden(cid, hide);
                    changed++;
                }
                catch (Exception ex) { Logger.Error($"相機 Key Plan：{(hide ? "隱藏" : "取消隱藏")}類別 {kv.Value.Value} 失敗（略過）", ex); }
            }
            return changed;
        }

        /// <summary>
        /// 套用（或還原）Key Plan 平面／剖面的「淡化背景」表現法。只作用在傳入的 Key Plan 視圖本身，
        /// 必須在裁剪框設定之後、標記繪製之前呼叫（比例會影響標記的紙上尺寸）。每一步獨立 try/catch＋log。
        ///   faded：① Display Model＝一般（2026-09-25 第七輪起不再用半色調，外輪廓要清楚）② 隱藏雜項註解類別
        ///          （房間標籤保留但類別取代半色調）④ 模型類別／牆元素取代（見 ApplyModelCategoryOverrides）
        ///          ③ 比例＝1:50/100/200 中最細且裁剪範圍放得進 A3 橫式 400×270mm 者（都放不下用 1:200）。
        ///   原始：Display Model＝一般（0）、取消隱藏同一組類別、清除房間標籤與 ④ 的類別／牆元素取代；比例不動。
        /// </summary>
        private static void ApplyKeyPlanStyle(View view, bool faded, bool isSection = false, Level levelBelow = null, bool outdoor = false, XYZ eye = null)
        {
            if (view == null) return;
            string label = $"「{view.Name}」(id={view.Id.GetIdValue()})";
            Document doc = view.Document;

            try
            {
                if (view.ViewTemplateId != ElementId.InvalidElementId)
                {
                    view.ViewTemplateId = ElementId.InvalidElementId;
                    Logger.Info($"相機 Key Plan：{label} 移除視圖樣板（讓表現法設定生效）");
                }
            }
            catch (Exception ex) { Logger.Error($"相機 Key Plan：{label} 移除視圖樣板失敗", ex); }

            // ① Display Model
            try
            {
                Parameter p = view.get_Parameter(BuiltInParameter.VIEW_MODEL_DISPLAY_MODE);
                if (p != null && !p.IsReadOnly && p.StorageType == StorageType.Integer)
                {
                    int before = p.AsInteger();
                    string beforeStr = p.AsValueString();
                    // 2026-09-25 第七輪（使用者：「平面淡化可以，但建築外輪廓要清楚」）：Display Model＝半色調會連切到的
                    // 牆一起變灰、外輪廓消失；兩種樣式一律改回「一般」，淡化改由 ④ 類別／元素取代處理。
                    int target = 0; // 0＝一般、1＝半色調、2＝不顯示（Revit UI 下拉順序；以 AsValueString 記 log 供實機核對）
                    if (before != target) p.Set(target);
                    // 2026-09-25（剖面實測 Display Model 疑似未生效）：以 AsValueString 驗證；若不是預期字樣，
                    // 逐一試 0..2 找出顯示字樣符合者（半色調：Halftone／半色調；一般：Normal／一般），找不到就回到 target。
                    string after = p.AsValueString() ?? "";
                    bool Matches(string v) =>
                        v.IndexOf("Normal", StringComparison.OrdinalIgnoreCase) >= 0 || v.Contains("一般") || v.Contains("正常");
                    string probe = "";
                    if (!Matches(after))
                    {
                        int found = -1;
                        for (int k = 0; k <= 2 && found < 0; k++)
                        {
                            try { p.Set(k); string vs = p.AsValueString() ?? ""; probe += $"{k}={vs};"; if (Matches(vs)) found = k; }
                            catch (Exception pex) { probe += $"{k}=ERR({pex.Message});"; }
                        }
                        p.Set(found >= 0 ? found : target);
                        probe = " probe[" + probe + "]" + (found >= 0 ? $" → 用 {found}" : " → 找不到符合字樣，維持 " + target);
                    }
                    Logger.Info($"相機 Key Plan：{label} Display Model {before}（{beforeStr}）→{p.AsInteger()}（{p.AsValueString()}）{probe}");
                }
                else Logger.Info($"相機 Key Plan：{label} Display Model 參數不可用（null/唯讀/型別={p?.StorageType}），略過");
            }
            catch (Exception ex) { Logger.Error($"相機 Key Plan：{label} 設定 Display Model 失敗", ex); }

            // ② 類別隱藏＋房間標籤半色調
            try
            {
                // 剖面：樓層不整類隱藏（改逐元素，只留本層與上一層，見下方）
                IEnumerable<string> names = isSection
                    ? KeyPlanDeclutterCategoryNames.Where(x => x != "OST_Levels")
                    : KeyPlanDeclutterCategoryNames;
                int n = HideCategoriesByName(view, names, includeAllTags: true, keepRoomTags: true, hide: faded);
                if (isSection) HideCategoriesByName(view, new[] { "OST_Levels" }, false, false, hide: false);
                Logger.Info($"相機 Key Plan：{label} {(faded ? "隱藏" : "取消隱藏")}雜項註解類別 {n} 個");
            }
            catch (Exception ex) { Logger.Error($"相機 Key Plan：{label} 類別隱藏/還原失敗", ex); }
            try
            {
                Category roomTags = Category.GetCategory(doc, BuiltInCategory.OST_RoomTags);
                if (roomTags != null && view.IsCategoryOverridable(roomTags.Id))
                {
                    var ogs = new OverrideGraphicSettings();
                    if (faded) ogs.SetHalftone(true);
                    view.SetCategoryOverrides(roomTags.Id, ogs);
                    Logger.Info($"相機 Key Plan：{label} 房間標籤類別取代 halftone={faded}");
                }
            }
            catch (Exception ex) { Logger.Error($"相機 Key Plan：{label} 房間標籤半色調設定失敗", ex); }

            // ④ 模型類別／牆元素取代（取代舊的 Display Model＝半色調；外輪廓最粗最黑）
            try { ApplyModelCategoryOverrides(view, faded, isSection); }
            catch (Exception ex) { Logger.Error($"相機 Key Plan：{label} 模型類別取代失敗", ex); }

            // ⑤ 連結模型顯示＝依主體視圖（2026-09-25：剖面裡堆疊的「L5 TOS」「R2 TOS」「Parking」等是連結模型的樓層，
            // 主體逐元素隱藏管不到；依主體視圖時 Revit 不顯示連結自己的樓層／軸線／註解，只顯示模型幾何）。
            if (faded)
            {
                try { SetLinksByHostView(view); }
                catch (Exception ex) { Logger.Error($"相機 Key Plan：{label} 連結模型顯示設定失敗（略過）", ex); }
            }

            if (isSection)
            {
                try { ApplySectionLevelDatums(view, faded, levelBelow, outdoor, eye); }
                catch (Exception ex) { Logger.Error($"相機 Key Plan：{label} 剖面樓層逐元素隱藏/半色調失敗", ex); }
            }
            else if (faded)
            {
                try { HidePlanDatumBubbles(view); }
                catch (Exception ex) { Logger.Error($"相機 Key Plan：{label} 平面樓層／軸線標頭隱藏失敗", ex); }
            }

            // ③ 比例（剖面多一檔 1:20）
            if (!faded) return;
            try
            {
                BoundingBoxXYZ crop = view.CropBox;
                if (crop == null) return;
                double wMm = CameraMath.FeetToMm(Math.Abs(crop.Max.X - crop.Min.X));
                double hMm = CameraMath.FeetToMm(Math.Abs(crop.Max.Y - crop.Min.Y));
                int chosen = 200;
                foreach (int s in (isSection ? new[] { 20, 50, 100, 200 } : new[] { 50, 100, 200 }))
                {
                    if (wMm / s <= 400.0 && hMm / s <= 270.0) { chosen = s; break; }
                }
                int before = view.Scale;
                if (before != chosen) view.Scale = chosen;
                Logger.Info($"相機 Key Plan：{label} 裁剪 {wMm:F0}×{hMm:F0}mm → 比例 1:{before}→1:{view.Scale}（A3 橫式 400×270mm）");
            }
            catch (Exception ex) { Logger.Error($"相機 Key Plan：{label} 設定比例失敗", ex); }
        }

        /// <summary>
        /// 剖面 Key Plan 的樓層標頭（2026-09-25 使用者截圖：一堆樓層標頭在小裁剪框內巨大且互相重疊）：
        /// faded＝逐元素隱藏這張剖面內所有樓層，只留相機下方樓層與其上一層（本層上下界），這兩個設
        /// 元素取代半色調；原始＝取消隱藏所有被藏的樓層並清掉樓層元素取代。
        /// </summary>
        private static void ApplySectionLevelDatums(View view, bool faded, Level levelBelow, bool outdoor = false, XYZ eye = null)
        {
            Document doc = view.Document;
            List<Level> allLevels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                .OrderBy(l => l.ProjectElevation).ToList();

            if (!faded)
            {
                var toUnhide = allLevels.Where(l => { try { return l.IsHidden(view); } catch { return false; } }).Select(l => l.Id).ToList();
                if (toUnhide.Count > 0) view.UnhideElements(toUnhide);
                int extReset = 0;
                foreach (Level l in allLevels)
                {
                    try { view.SetElementOverrides(l.Id, new OverrideGraphicSettings()); } catch { }
                    // 2026-09-25：淡化樣式設過的「視圖專屬 2D 範圍」還原成模型範圍（只影響這張 Key Plan 剖面）。
                    foreach (DatumEnds end in new[] { DatumEnds.End0, DatumEnds.End1 })
                    {
                        try
                        {
                            if (l.GetDatumExtentTypeInView(end, view) == DatumExtentType.ViewSpecific)
                            {
                                l.SetDatumExtentType(end, view, DatumExtentType.Model);
                                extReset++;
                            }
                        }
                        catch { }
                    }
                }
                Logger.Info($"相機 Key Plan：剖面「{view.Name}」原始樣式：取消隱藏樓層 {toUnhide.Count} 個、清除樓層元素取代、2D 範圍還原模型 {extReset} 端");
                return;
            }

            if (levelBelow == null)
            {
                Logger.Info($"相機 Key Plan：剖面「{view.Name}」找不到相機樓層，樓層標頭不處理");
                return;
            }

            // 2026-09-25（Snowdon Towers 室外相機實測：大量近同高程樓層重疊、部分仍是全黑未隱藏）：
            // 保留＝下方樓層＋「高差 ≥ 1.0m 的下一個上層樓層」（近重複樓層不算）；室外只留下方樓層。
            // 其餘樓層：視圖內 collector ∪ 全模型 Level 清單 → 過濾 CanBeHidden、未隱藏 → 一次 HideElements；
            // 批次例外時逐一隱藏並記錄失敗。保留者一律元素取代半色調。
            var keep = new List<Level> { levelBelow };
            if (!outdoor)
            {
                double gap = CameraMath.MmToFeet(SectionLevelMinGapMm);
                Level above = allLevels.FirstOrDefault(l => l.ProjectElevation >= levelBelow.ProjectElevation + gap);
                if (above != null) keep.Add(above);
            }
            var keepIds = new HashSet<ElementId>(keep.Select(l => l.Id));

            var inView = new HashSet<ElementId>();
            try
            {
                foreach (Element e in new FilteredElementCollector(doc, view.Id).OfClass(typeof(Level)))
                    inView.Add(e.Id);
            }
            catch (Exception ex) { Logger.Error($"相機 Key Plan：剖面「{view.Name}」視圖內樓層 collector 失敗，改用全模型清單", ex); }

            int failures = 0;
            var failNames = new List<string>();
            try
            {
                var keepHidden = keep.Where(l => { try { return l.IsHidden(view); } catch { return false; } }).Select(l => l.Id).ToList();
                if (keepHidden.Count > 0) view.UnhideElements(keepHidden);
            }
            catch (Exception ex) { failures++; failNames.Add("unhide keep: " + ex.Message); }

            var toHide = new List<ElementId>();
            var hideNames = new List<string>();
            int alreadyHidden = 0, notHideable = 0, visibleSeen = 0;
            foreach (Level l in allLevels)
            {
                bool hidden;
                try { hidden = l.IsHidden(view); } catch { hidden = false; }
                bool visible = !hidden && inView.Contains(l.Id);
                if (visible) visibleSeen++;
                if (keepIds.Contains(l.Id)) continue;
                if (hidden) { alreadyHidden++; continue; }
                bool can;
                try { can = l.CanBeHidden(view); } catch { can = false; }
                if (!can)
                {
                    notHideable++;
                    if (visible) { failures++; failNames.Add(l.Name + "(CanBeHidden=false)"); }
                    continue;
                }
                toHide.Add(l.Id);
                hideNames.Add(l.Name);
            }

            int hiddenNow = 0;
            if (toHide.Count > 0)
            {
                try
                {
                    view.HideElements(toHide);
                    hiddenNow = toHide.Count;
                }
                catch (Exception batchEx)
                {
                    Logger.Error($"相機 Key Plan：剖面「{view.Name}」樓層批次隱藏失敗（{toHide.Count} 個），改逐一隱藏", batchEx);
                    foreach (ElementId id in toHide)
                    {
                        try { view.HideElements(new List<ElementId> { id }); hiddenNow++; }
                        catch (Exception ex1)
                        {
                            failures++;
                            failNames.Add((doc.GetElement(id)?.Name ?? id.GetIdValue().ToString()) + "(" + ex1.Message + ")");
                        }
                    }
                }
            }

            var ogs = new OverrideGraphicSettings();
            ogs.SetHalftone(true);
            foreach (Level l in keep)
            {
                try { view.SetElementOverrides(l.Id, ogs); }
                catch (Exception ex) { failures++; failNames.Add(l.Name + "(halftone: " + ex.Message + ")"); }
            }

            // 2026-09-25（使用者：「剖面 Key Plan 每個樓層文字都有白底色塊蓋住其他東西」，後續：標頭要看得到但不能蓋東西）：
            // 樓層類型是全域的，不能為這張視圖換類型；改用「視圖專屬 2D 範圍」：保留樓層線橫跨剖面裁剪寬度、
            // 顯示標頭的那一端伸出裁剪框外 1.5m（遠離相機標記的一側）落在空白處，另一端標頭隱藏。
            // SetCurveInView 失敗的樓層才退回「兩端標頭都隱藏」。
            PlaceSectionLevelHeads(view, keep, eye);

            // 驗證：處理後仍可見、且不在保留清單的樓層
            var stillVisible = new List<string>();
            foreach (Level l in allLevels)
            {
                if (keepIds.Contains(l.Id)) continue;
                try { if (!l.IsHidden(view) && inView.Contains(l.Id)) stillVisible.Add(l.Name); } catch { }
            }

            string keepNames = string.Join(", ", keep.Select(l => $"{l.Name}@{CameraMath.FeetToMm(l.ProjectElevation):F0}mm"));
            Logger.Info($"相機 Key Plan：剖面「{view.Name}」樓層標頭（{(outdoor ? "室外：只留下方樓層" : "室內：下方＋高差≥1m 上一層")}）：" +
                        $"全模型 {allLevels.Count} 個、視圖內 collector {inView.Count} 個、處理前可見 {visibleSeen} 個；" +
                        $"保留 [{keepNames}]（半色調）；本次隱藏 {hiddenNow}/{toHide.Count} 個 [{string.Join(", ", hideNames)}]；" +
                        $"原已隱藏 {alreadyHidden}、不可隱藏 {notHideable}；失敗 {failures}" +
                        (failNames.Count > 0 ? " [" + string.Join("; ", failNames) + "]" : "") +
                        $"；處理後仍可見（非保留）{stillVisible.Count}" + (stillVisible.Count > 0 ? " [" + string.Join(", ", stillVisible) + "]" : ""));
        }

        /// <summary>剖面 Key Plan 保留樓層：顯示標頭的那一端伸出裁剪框外的距離（模型 mm）。</summary>
        private const double SectionLevelHeadOutsideMm = 1500.0;

        /// <summary>
        /// 2026-09-25：剖面 Key Plan 保留樓層的標頭擺放（只動這張視圖的視圖專屬 2D 範圍，正式視圖不受影響）：
        ///   兩端 SetDatumExtentType(ViewSpecific) → 取 GetCurvesInView(ViewSpecific) 那條線，改成沿剖面 X 軸
        ///   橫跨裁剪寬度，且「遠離相機眼睛」那一側多伸出 1.5m；該端 ShowBubbleInView、另一端 HideBubbleInView。
        ///   視圖若開了註解裁剪（會把框外標頭切掉）→ 關閉註解裁剪並記 log。任何一層失敗 → 該層退回兩端標頭隱藏。
        /// </summary>
        private static void PlaceSectionLevelHeads(View view, List<Level> keep, XYZ eye)
        {
            if (view == null || keep == null || keep.Count == 0) return;
            BoundingBoxXYZ crop;
            Transform tf;
            try { crop = view.CropBox; tf = crop.Transform; }
            catch (Exception ex)
            {
                Logger.Error($"相機 Key Plan：剖面「{view.Name}」讀不到裁剪框，保留樓層改為兩端標頭隱藏", ex);
                HideDatumBubblesInView(view, keep.Cast<DatumPlane>(), "剖面保留樓層（退回）");
                return;
            }
            XYZ bx = tf.BasisX;
            double minX = Math.Min(crop.Min.X, crop.Max.X), maxX = Math.Max(crop.Min.X, crop.Max.X);
            double ex0 = eye != null ? (eye - tf.Origin).DotProduct(bx) : (minX + maxX) / 2.0;
            bool bubbleHigh = (ex0 - minX) <= (maxX - ex0); // 眼睛靠近 min 側 → 標頭放 max 側
            double outside = CameraMath.MmToFeet(SectionLevelHeadOutsideMm);
            double lowX = bubbleHigh ? minX : minX - outside;
            double highX = bubbleHigh ? maxX + outside : maxX;

            // 註解裁剪會切掉框外的標頭 → 關閉（只這張 Key Plan 剖面）。
            string annoLog = "註解裁剪=?";
            try
            {
                Parameter ap = view.get_Parameter(BuiltInParameter.VIEWER_ANNOTATION_CROP_ACTIVE);
                if (ap != null && !ap.IsReadOnly && ap.StorageType == StorageType.Integer)
                {
                    if (ap.AsInteger() != 0) { ap.Set(0); annoLog = "註解裁剪 開→關（避免切掉框外標頭）"; }
                    else annoLog = "註解裁剪 原本即關閉";
                }
                else annoLog = "註解裁剪參數不可用";
            }
            catch (Exception ex) { annoLog = "註解裁剪設定失敗：" + ex.Message; }

            var ok = new List<string>();
            var fallback = new List<Level>();
            foreach (Level l in keep)
            {
                try
                {
                    foreach (DatumEnds end in new[] { DatumEnds.End0, DatumEnds.End1 })
                    {
                        if (l.GetDatumExtentTypeInView(end, view) != DatumExtentType.ViewSpecific)
                            l.SetDatumExtentType(end, view, DatumExtentType.ViewSpecific);
                    }
                    IList<Curve> cs = l.GetCurvesInView(DatumExtentType.ViewSpecific, view);
                    if (cs == null || cs.Count == 0) throw new InvalidOperationException("GetCurvesInView(ViewSpecific) 回傳空");
                    Curve c = cs[0];
                    XYZ p0 = c.GetEndPoint(0), p1 = c.GetEndPoint(1);
                    double x0 = (p0 - tf.Origin).DotProduct(bx), x1 = (p1 - tf.Origin).DotProduct(bx);
                    XYZ At(double x) => p0 + bx * (x - x0);
                    bool forward = x0 <= x1;
                    Line nl = forward ? Line.CreateBound(At(lowX), At(highX)) : Line.CreateBound(At(highX), At(lowX));
                    l.SetCurveInView(DatumExtentType.ViewSpecific, view, nl);

                    DatumEnds bubbleEnd = (forward == bubbleHigh) ? DatumEnds.End1 : DatumEnds.End0;
                    DatumEnds otherEnd = bubbleEnd == DatumEnds.End1 ? DatumEnds.End0 : DatumEnds.End1;
                    string bubbleNote = "";
                    try { if (!l.IsBubbleVisibleInView(bubbleEnd, view)) l.ShowBubbleInView(bubbleEnd, view); }
                    catch (Exception exB) { bubbleNote += $" 顯示標頭失敗({exB.Message})"; }
                    try { if (l.IsBubbleVisibleInView(otherEnd, view)) l.HideBubbleInView(otherEnd, view); }
                    catch (Exception exB) { bubbleNote += $" 隱藏另一端失敗({exB.Message})"; }
                    ok.Add($"{l.Name}: 2D 範圍 X[{CameraMath.FeetToMm(lowX):F0},{CameraMath.FeetToMm(highX):F0}]mm 標頭={bubbleEnd}（{(bubbleHigh ? "右" : "左")}側框外 1.5m）{bubbleNote}");
                }
                catch (Exception ex)
                {
                    Logger.Error($"相機 Key Plan：剖面「{view.Name}」樓層 {l.Name} 視圖專屬 2D 範圍設定失敗，退回兩端標頭隱藏", ex);
                    fallback.Add(l);
                }
            }
            if (fallback.Count > 0) HideDatumBubblesInView(view, fallback.Cast<DatumPlane>(), "剖面保留樓層（退回）");
            Logger.Info($"相機 Key Plan：剖面「{view.Name}」保留樓層標頭：{annoLog}；眼睛沿剖面 X={CameraMath.FeetToMm(ex0):F0}mm " +
                        $"裁剪 X[{CameraMath.FeetToMm(minX):F0},{CameraMath.FeetToMm(maxX):F0}]mm；成功 {ok.Count} [{string.Join("; ", ok)}]；退回隱藏 {fallback.Count}");
        }

        /// <summary>
        /// 2026-09-25：把這張 Key Plan 視圖中每個 RevitLinkInstance 的顯示設定改成「依主體視圖」（ByHostView），
        /// 讓連結模型自己的樓層／軸線／註解不顯示（使用者截圖：剖面裡堆疊的連結樓層標頭），模型幾何則照主體
        /// 視圖的類別取代（半色調）顯示。只動傳入的 Key Plan 視圖。View.Get/SetLinkOverrides 為 Revit 2024+ API；
        /// 更舊版本只記 log。之後以 FilteredElementCollector(doc, viewId, linkId) 數連結中「在這張視圖可見」的
        /// Level 數量作驗證（此計數是否完全反映顯示模式未經實機驗證，僅供參考）。
        /// </summary>
        private static void SetLinksByHostView(View view)
        {
            Document doc = view.Document;
            List<RevitLinkInstance> links;
            try { links = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>().ToList(); }
            catch (Exception ex) { Logger.Error($"相機 Key Plan：「{view.Name}」收集連結模型失敗", ex); return; }
            if (links.Count == 0) { Logger.Info($"相機 Key Plan：「{view.Name}」沒有連結模型"); return; }
#if REVIT2024_OR_GREATER
            var rows = new List<string>();
            var changedIds = new List<RevitLinkInstance>();
            int changed = 0, failed = 0;
            foreach (RevitLinkInstance li in links)
            {
                string name = li.Name;
                try
                {
                    bool hidden = false;
                    try { hidden = li.IsHidden(view); } catch { }
                    RevitLinkGraphicsSettings cur = null;
                    try { cur = view.GetLinkOverrides(li.Id); } catch { }
                    string before = cur == null ? "ByHostView(無取代)" : cur.LinkVisibilityType.ToString();
                    if (cur == null || cur.LinkVisibilityType == LinkVisibility.ByHostView)
                    {
                        rows.Add($"{name}: {before} 不變{(hidden ? "（此視圖隱藏）" : "")}");
                        changedIds.Add(li);
                        continue;
                    }
                    var s = new RevitLinkGraphicsSettings { LinkVisibilityType = LinkVisibility.ByHostView };
                    view.SetLinkOverrides(li.Id, s);
                    changed++;
                    changedIds.Add(li);
                    RevitLinkGraphicsSettings after = null;
                    try { after = view.GetLinkOverrides(li.Id); } catch { }
                    rows.Add($"{name}: {before}→{(after == null ? "ByHostView(無取代)" : after.LinkVisibilityType.ToString())}{(hidden ? "（此視圖隱藏）" : "")}");
                }
                catch (Exception ex)
                {
                    failed++;
                    rows.Add($"{name}: 設定失敗 {ex.Message}");
                }
            }

            // 驗證：連結中在這張視圖可見的 Level 數（需要先 Regenerate 讓設定生效）。
            string verify;
            try
            {
                doc.Regenerate();
                var counts = new List<string>();
                int total = 0;
                foreach (RevitLinkInstance li in changedIds)
                {
                    try
                    {
                        int n = new FilteredElementCollector(doc, view.Id, li.Id).OfClass(typeof(Level)).GetElementCount();
                        total += n;
                        if (n > 0) counts.Add($"{li.Name}={n}");
                    }
                    catch (Exception exC) { counts.Add($"{li.Name}=?({exC.Message})"); }
                }
                verify = $"連結中此視圖可見樓層（collector）合計 {total}" + (counts.Count > 0 ? " [" + string.Join(", ", counts) + "]" : "");
            }
            catch (Exception ex) { verify = "驗證計數失敗：" + ex.Message; }

            Logger.Info($"相機 Key Plan：「{view.Name}」連結模型顯示→依主體視圖：連結 {links.Count} 個、改設 {changed}、失敗 {failed}；" +
                        $"{verify}；[{string.Join("; ", rows)}]");
#else
            Logger.Info($"相機 Key Plan：「{view.Name}」連結模型 {links.Count} 個；此 Revit 版本沒有 View.SetLinkOverrides（2024+ API），" +
                        "連結顯示設定未變更，連結模型的樓層可能仍會顯示");
#endif
        }

        /// <summary>
        /// 2026-09-25：在指定視圖內隱藏基準面（樓層／軸線）兩端的標頭（End0、End1）。樓層標頭族群有不透明
        /// 白底，會蓋住 Key Plan 內容；這裡只動「這張視圖」的標頭可見性（HideBubbleInView 是視圖專屬設定），
        /// 不改族群／類型。每一次呼叫各自 try/catch，失敗只記錄不中斷。回傳實際隱藏的標頭數。
        /// </summary>
        private static int HideDatumBubblesInView(View view, IEnumerable<DatumPlane> datums, string what)
        {
            if (view == null || datums == null) return 0;
            int datumCount = 0, hidden = 0, alreadyHidden = 0, failed = 0;
            var failNames = new List<string>();
            foreach (DatumPlane d in datums)
            {
                if (d == null) continue;
                datumCount++;
                foreach (DatumEnds end in new[] { DatumEnds.End0, DatumEnds.End1 })
                {
                    try
                    {
                        if (!d.IsBubbleVisibleInView(end, view)) { alreadyHidden++; continue; }
                        d.HideBubbleInView(end, view);
                        hidden++;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        if (failNames.Count < 8) failNames.Add($"{d.Name}/{end}({ex.Message})");
                    }
                }
            }
            Logger.Info($"相機 Key Plan：「{view.Name}」{what}標頭隱藏：基準面 {datumCount} 個、本次隱藏 {hidden} 個標頭、" +
                        $"原已隱藏 {alreadyHidden}、失敗 {failed}" + (failNames.Count > 0 ? " [" + string.Join("; ", failNames) + "]" : ""));
            return hidden;
        }

        /// <summary>平面 Key Plan：視圖內仍可見的樓層／軸線（正常已被 declutter 整類隱藏，這裡只是保險）兩端標頭隱藏。</summary>
        private static void HidePlanDatumBubbles(View view)
        {
            Document doc = view.Document;
            var visible = new List<DatumPlane>();
            try
            {
                foreach (Element e in new FilteredElementCollector(doc, view.Id).OfClass(typeof(Autodesk.Revit.DB.Grid)))
                {
                    try { if (e is DatumPlane dp && !e.IsHidden(view)) visible.Add(dp); } catch { }
                }
                foreach (Element e in new FilteredElementCollector(doc, view.Id).OfClass(typeof(Level)))
                {
                    try { if (e is DatumPlane dp && !e.IsHidden(view)) visible.Add(dp); } catch { }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"相機 Key Plan：平面「{view.Name}」收集可見樓層／軸線失敗", ex);
                return;
            }
            HideDatumBubblesInView(view, visible, "平面可見樓層／軸線");
        }

        // ── ④ 模型類別取代（2026-09-25 第七輪：外輪廓清楚）─────────────────────────

        /// <summary>淡化背景時「切到要清楚」的結構／圍護類別：剖切線黑、剖切填充圖案色中灰 128（不換圖案）、
        /// 投影線中灰 128（API 的半色調是整個元素一起淡，無法只淡投影線，改以投影線色模擬）。</summary>
        private static readonly string[] KeyPlanCutEmphasisCategoryNames =
        {
            "OST_Walls", "OST_StructuralColumns", "OST_Columns", "OST_Floors", "OST_Roofs", "OST_StructuralFraming",
            "OST_CurtainWallPanels", "OST_CurtainWallMullions", "OST_CurtaSystem", "OST_Stairs"
        };

        /// <summary>迭代模型類別時絕不淡化的類別：相機標記（OST_Lines 子類別「相機標記線」、FilledRegion 屬
        /// OST_DetailComponents）、剖面 viewer、剖面框／相機；OST_RvtLinks 另外處理（半色調）。</summary>
        private static readonly string[] KeyPlanNeverHalftoneCategoryNames =
        {
            "OST_Lines", "OST_DetailComponents", "OST_Viewers", "OST_SectionBox", "OST_Cameras"
        };

        private const int KeyPlanWallCutWeight = 5;
        private const int KeyPlanColumnCutWeight = 4;
        private const int KeyPlanSlabCutWeightSection = 5;
        private const int KeyPlanExteriorWallCutWeight = 7;

        private static Autodesk.Revit.DB.Color KpBlack => new Autodesk.Revit.DB.Color(0, 0, 0);
        private static Autodesk.Revit.DB.Color KpMidGrey => new Autodesk.Revit.DB.Color(128, 128, 128);
        private static Autodesk.Revit.DB.Color KpDarkGrey => new Autodesk.Revit.DB.Color(64, 64, 64);

        /// <summary>切到類別的取代設定；cutWeight=null＝不改模型線寬（只改顏色）。</summary>
        private static OverrideGraphicSettings BuildCutEmphasisOgs(int? cutWeight, Autodesk.Revit.DB.Color cutLineColor)
        {
            var ogs = new OverrideGraphicSettings();
            ogs.SetHalftone(false);
            ogs.SetCutLineColor(cutLineColor);
            if (cutWeight.HasValue) ogs.SetCutLineWeight(cutWeight.Value);
            ogs.SetCutForegroundPatternColor(KpMidGrey);
            ogs.SetCutBackgroundPatternColor(KpMidGrey);
            ogs.SetProjectionLineColor(KpMidGrey);
            return ogs;
        }

        private static int? CutWeightFor(string bicName, bool isSection)
        {
            switch (bicName)
            {
                case "OST_Walls": return KeyPlanWallCutWeight;
                case "OST_StructuralColumns":
                case "OST_Columns": return KeyPlanColumnCutWeight;
                case "OST_Floors":
                case "OST_Roofs": return isSection ? (int?)KeyPlanSlabCutWeightSection : null;
                default: return null;
            }
        }

        /// <summary>
        /// 淡化背景：① 結構／圍護類別剖切線黑（牆 5、柱 4、剖面樓板／屋頂 5，其餘不改線寬）、剖切圖案色中灰、
        /// 投影線中灰；② 其他所有可取代的模型類別（迭代 doc.Settings.Categories，CategoryType.Model）半色調，
        /// 相機標記相關類別除外；③ 連結模型 OST_RvtLinks 類別半色調（RevitLinkGraphicsSettings 無半色調屬性）；
        /// ④ 視圖內的牆逐元素：外牆（帷幕牆 WallKind.Curtain，或 WallType.Function＝Exterior）剖切線黑、線寬 7；
        /// 其他牆剖切線深灰 64、線寬 5。原始：清掉上述所有類別取代與視圖內牆的元素取代。
        /// 每類別／每面牆獨立 try/catch；冪等（每次整組覆寫）。
        /// </summary>
        private static void ApplyModelCategoryOverrides(View view, bool faded, bool isSection)
        {
            Document doc = view.Document;
            string label = $"「{view.Name}」(id={view.Id.GetIdValue()})";
            var emphasis = new HashSet<string>(KeyPlanCutEmphasisCategoryNames);
            var never = new HashSet<string>(KeyPlanNeverHalftoneCategoryNames);
            int emphasized = 0, halftoned = 0, cleared = 0, catFail = 0, skippedNotOverridable = 0;
            bool linkDone = false;

            foreach (Category c in doc.Settings.Categories)
            {
                string bn = null;
                try
                {
                    if (c == null) continue;
                    long v = c.Id.GetIdValue();
                    if (v >= 0) continue;
                    bn = Enum.GetName(typeof(BuiltInCategory), (int)v);
                    if (bn == null) continue;
                    bool isEmphasis = emphasis.Contains(bn);
                    bool isLink = bn == "OST_RvtLinks";
                    if (!isEmphasis && !isLink && c.CategoryType != CategoryType.Model) continue;
                    if (never.Contains(bn)) continue;
                    if (!view.IsCategoryOverridable(c.Id)) { skippedNotOverridable++; continue; }

                    if (!faded)
                    {
                        view.SetCategoryOverrides(c.Id, new OverrideGraphicSettings());
                        cleared++;
                    }
                    else if (isEmphasis)
                    {
                        view.SetCategoryOverrides(c.Id, BuildCutEmphasisOgs(CutWeightFor(bn, isSection), KpBlack));
                        emphasized++;
                    }
                    else
                    {
                        var ogs = new OverrideGraphicSettings();
                        ogs.SetHalftone(true);
                        view.SetCategoryOverrides(c.Id, ogs);
                        halftoned++;
                        if (isLink) linkDone = true;
                    }
                }
                catch (Exception ex)
                {
                    catFail++;
                    Logger.Error($"相機 Key Plan：{label} 類別取代 {bn ?? "?"} 失敗（略過）", ex);
                }
            }

            int ext = 0, inner = 0, wallCleared = 0, wallFail = 0;
            try
            {
                var walls = new FilteredElementCollector(doc, view.Id).OfClass(typeof(Wall)).Cast<Wall>().ToList();
                OverrideGraphicSettings extOgs = faded ? BuildCutEmphasisOgs(KeyPlanExteriorWallCutWeight, KpBlack) : null;
                OverrideGraphicSettings intOgs = faded ? BuildCutEmphasisOgs(KeyPlanWallCutWeight, KpDarkGrey) : null;
                foreach (Wall w in walls)
                {
                    try
                    {
                        if (!faded)
                        {
                            view.SetElementOverrides(w.Id, new OverrideGraphicSettings());
                            wallCleared++;
                            continue;
                        }
                        bool isExterior = false;
                        WallType wt = w.WallType;
                        if (wt != null)
                        {
                            try { isExterior = wt.Kind == WallKind.Curtain; } catch { }
                            if (!isExterior)
                            {
                                try { isExterior = wt.Function == WallFunction.Exterior; } catch { }
                            }
                        }
                        view.SetElementOverrides(w.Id, isExterior ? extOgs : intOgs);
                        if (isExterior) ext++; else inner++;
                    }
                    catch (Exception ex)
                    {
                        wallFail++;
                        if (wallFail <= 3) Logger.Error($"相機 Key Plan：{label} 牆 {w.Id.GetIdValue()} 元素取代失敗（略過）", ex);
                    }
                }
            }
            catch (Exception ex) { Logger.Error($"相機 Key Plan：{label} 收集視圖內牆失敗", ex); }

            if (faded)
                Logger.Info($"相機 Key Plan：{label} 淡化背景類別取代：強調（切到黑）{emphasized} 類、半色調 {halftoned} 類" +
                            $"（連結模型 {(linkDone ? "已半色調" : "未處理")}）、不可取代略過 {skippedNotOverridable}、失敗 {catFail}；" +
                            $"牆元素取代：外牆（黑、線寬 {KeyPlanExteriorWallCutWeight}）{ext} 面、內牆（深灰 64、線寬 {KeyPlanWallCutWeight}）{inner} 面、失敗 {wallFail}");
            else
                Logger.Info($"相機 Key Plan：{label} 原始樣式：清除類別取代 {cleared} 類、清除牆元素取代 {wallCleared} 面；" +
                            $"不可取代略過 {skippedNotOverridable}、失敗 {catFail + wallFail}");
        }

        private static bool IsKeyPlanFadedStyle()
        {
            try { return ConfigManager.Instance.Settings.CameraManager.KeyPlanFadedStyle; }
            catch { return true; }
        }


        /// <summary>本層「頂」的判定結果（純 double，交易外算好）。</summary>
        internal class StoreyTop
        {
            /// <summary>上層樓板／屋頂底面（找不到時為備援值：上一樓層高程 − 代表樓板厚（無則 300mm）；
            /// 連上一樓層都沒有時＝眼睛 +3m）。</summary>
            public double SlabBottomZ;
            /// <summary>上層樑底面最低值；沒有上層樑為 null。</summary>
            public double? BeamBottomZ;
            /// <summary>2026-09-25：眼睛所在房間的頂（房間 BoundingBox Max.Z，反映上限＋上限偏移）；無房間為 null。</summary>
            public double? RoomTopZ;
            /// <summary>2026-09-25 室外規則：不做樓層切／上層樓板判定，改用 OutdoorBottomZ／OutdoorTopZ（等角與剖面共用）。</summary>
            public bool Outdoor;
            public double OutdoorBottomZ, OutdoorTopZ;
            public string Log;
        }

        /// <summary>等角／本層頂共用的 XY 目標範圍：相機眼睛＋視野扇形（遠裁剪先夾到 30m）＋平面 margin。</summary>
        private static void ComputeFanXY(CameraMath.CameraGeometry geom, XYZ forwardHoriz, double leftRad, double rightRad, double farClipFeet,
            out double minX, out double minY, out double maxX, out double maxY, double capMm = IsoFarClipCapMm)
        {
            double reachFeet = Math.Min(farClipFeet, CameraMath.MmToFeet(capMm));
            double marginFeet = Cm(PlanMarginCm);
            XYZ dirLeft = PlanEdgeDir(forwardHoriz, leftRad);
            XYZ dirRight = PlanEdgeDir(forwardHoriz, rightRad);
            XYZ[] pts =
            {
                geom.Eye,
                geom.Eye + forwardHoriz * reachFeet,
                geom.Eye + dirLeft * reachFeet,
                geom.Eye + dirRight * reachFeet
            };
            minX = pts.Min(p => p.X) - marginFeet;
            maxX = pts.Max(p => p.X) + marginFeet;
            minY = pts.Min(p => p.Y) - marginFeet;
            maxY = pts.Max(p => p.Y) + marginFeet;
        }

        /// <summary>
        /// 2026-09-24 修訂（實測回報：「只到當層頂」＝下一樓層高程，上層樓板從該高程往下垂，整片
        /// 樓板（甚至屋頂）落在 SectionBox 內把本層蓋住）。規則：
        ///   上層樓板底＝XY 重疊、底面高於本層樓層 +300mm、頂面高於眼睛的 Floors／Roofs 中底面最低者
        ///   （天花板不算）；找不到 → 上一樓層高程 − 該樓層樓板代表厚度（中位數，無樓板則 300mm）；
        ///   連上一樓層都沒有 → 眼睛 +3m。
        ///   上層樑底＝XY 重疊、底面高於本層 +300mm、頂面高於眼睛的結構構架中底面最低者。
        /// 使用 SectionBoxLevelHelper 的「新增」方法，set_3d_section_box 既有方法與行為不動。
        /// </summary>
        private static StoreyTop ComputeStoreyTop(Document doc, CameraMath.CameraGeometry geom, IsoXY xy, List<Level> levelsSorted, RoomCtx room = null, bool outdoor = false)
        {
            var r = new StoreyTop();
            double levelZ = geom.LevelBelow.ProjectElevation;
            double tol = CameraMath.MmToFeet(IsoSupportingBeamToleranceMm);
            double minX = xy.MinX, minY = xy.MinY, maxX = xy.MaxX, maxY = xy.MaxY;

            if (outdoor)
            {
                // 2026-09-25 室外規則：Z 下限＝min(下方樓層, 模型最低樓層) −1m；Z 上限＝XY 範圍內主要模型類別
                // BoundingBox 最高點（與眼睛取大）+2m；找不到任何模型 → 眼睛 +3m。不做樓層切、不找上層樓板。
                r.Outdoor = true;
                double lowest = levelZ;
                try { if (levelsSorted != null && levelsSorted.Count > 0) lowest = Math.Min(levelZ, levelsSorted.Min(l => l.ProjectElevation)); }
                catch (Exception ex) { Logger.Error("相機 Key Plan：室外讀最低樓層失敗，用下方樓層", ex); }
                r.OutdoorBottomZ = lowest - CameraMath.MmToFeet(OutdoorBelowLevelMm);
                string topSrc;
                try
                {
                    if (SectionBoxLevelHelper.FindMaxModelTopInXY(doc, minX, minY, maxX, maxY, OutdoorModelScanZFeet, out double mt, out int n, out string topEl))
                    {
                        r.OutdoorTopZ = Math.Max(mt, geom.Eye.Z) + CameraMath.MmToFeet(OutdoorAboveModelMm);
                        topSrc = $"modelTop={CameraMath.FeetToMm(mt):F0}mm by {topEl} among {n} elements; max(modelTop, eye)+2m";
                    }
                    else
                    {
                        r.OutdoorTopZ = geom.Eye.Z + Cm(SectionAboveEyeCm);
                        topSrc = "no model elements in XY → eye+3m";
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error("相機 Key Plan：室外找模型最高點失敗，改用眼睛 +3m", ex);
                    r.OutdoorTopZ = geom.Eye.Z + Cm(SectionAboveEyeCm);
                    topSrc = "model scan failed → eye+3m";
                }
                if (!(r.OutdoorTopZ > r.OutdoorBottomZ + Cm(100))) r.OutdoorTopZ = r.OutdoorBottomZ + Cm(300);
                r.RoomTopZ = null;
                r.SlabBottomZ = r.OutdoorTopZ;
                r.Log = $"OUTDOOR levelZ={CameraMath.FeetToMm(levelZ):F0}mm lowestLevel={CameraMath.FeetToMm(lowest):F0}mm eyeZ={CameraMath.FeetToMm(geom.Eye.Z):F0}mm " +
                        $"Z[{CameraMath.FeetToMm(r.OutdoorBottomZ):F0},{CameraMath.FeetToMm(r.OutdoorTopZ):F0}]mm (bottom=min(levelBelow,lowestLevel)−1m; top: {topSrc})";
                return r;
            }

            // 2026-09-25 第五輪（挑高房間＋夾層，Loft Unit 實測：舊規則把房間內夾層當成上層樓板，只高於眼睛 22cm）：
            // 有房間頂 → 上層樓板只認「底面或頂面 ≥ 房間頂 −0.1m」者（房間內、整片低於房間頂的夾層不算）；
            // 上層樑只認頂面 ≥ 房間頂 −0.1m 者（吊在房間頂的樑）。
            if (room?.TopZ != null)
            {
                double roomTop = room.TopZ.Value;
                r.RoomTopZ = roomTop;
                double topTol = CameraMath.MmToFeet(RoomTopSlabToleranceMm);
                string src;
                try
                {
                    Element slab2 = SectionBoxLevelHelper.FindUpperSlabOrRoofAboveRoomTop(doc, minX, minY, maxX, maxY, roomTop, topTol, out double sb, out int skipped);
                    if (slab2 != null)
                    {
                        r.SlabBottomZ = sb;
                        src = $"slab/roof id={slab2.Id.GetIdValue()} ({slab2.Category?.Name}) above roomTop; skipped below-roomTop floors={skipped}";
                    }
                    else
                    {
                        r.SlabBottomZ = double.PositiveInfinity;
                        src = $"no slab above roomTop (skipped below-roomTop floors={skipped})";
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error("相機 Key Plan：找房間頂以上的樓板失敗，只用房間頂", ex);
                    r.SlabBottomZ = double.PositiveInfinity;
                    src = "slab search failed";
                }
                try
                {
                    if (SectionBoxLevelHelper.FindLowestUpperBeamBottom(doc, minX, minY, maxX, maxY, levelZ, roomTop - topTol, tol, out double bb, out int nb))
                        r.BeamBottomZ = bb;
                }
                catch (Exception ex) { Logger.Error("相機 Key Plan：找上層樑失敗，略過", ex); }
                r.Log = $"levelZ={CameraMath.FeetToMm(levelZ):F0}mm eyeZ={CameraMath.FeetToMm(geom.Eye.Z):F0}mm roomTop={CameraMath.FeetToMm(roomTop):F0}mm " +
                        $"slabBottom={(double.IsInfinity(r.SlabBottomZ) ? "none" : CameraMath.FeetToMm(r.SlabBottomZ).ToString("F0") + "mm")} [{src}] " +
                        $"beamBottom={(r.BeamBottomZ.HasValue ? CameraMath.FeetToMm(r.BeamBottomZ.Value).ToString("F0") + "mm" : "none")}";
                return r;
            }

            string slabSrc;
            Element slab = null;
            double slabBottom = double.NaN;
            try
            {
                slab = SectionBoxLevelHelper.FindNearestUpperSlabOrRoof(doc, minX, minY, maxX, maxY, levelZ, geom.Eye.Z, tol, out slabBottom);
            }
            catch (Exception ex) { Logger.Error("相機 Key Plan：找上層樓板失敗，改用備援", ex); slab = null; }

            if (slab != null)
            {
                r.SlabBottomZ = slabBottom;
                slabSrc = $"slab/roof id={slab.Id.GetIdValue()} ({slab.Category?.Name})";
            }
            else
            {
                Level next = levelsSorted?.FirstOrDefault(l => l.ProjectElevation > levelZ + 1e-6);
                if (next != null)
                {
                    double? t = null;
                    try { t = SectionBoxLevelHelper.EstimateTypicalSlabThickness(doc, next.Id); } catch { t = null; }
                    double thick = t ?? CameraMath.MmToFeet(DefaultSlabThicknessMm);
                    r.SlabBottomZ = next.ProjectElevation - thick;
                    slabSrc = $"fallback nextLevel '{next.Name}' - thickness {CameraMath.FeetToMm(thick):F0}mm ({(t.HasValue ? "median of floors at that level" : "default 300mm")})";
                }
                else
                {
                    r.SlabBottomZ = geom.Eye.Z + Cm(300);
                    slabSrc = "fallback no upper level: eye+3m";
                }
            }

            try
            {
                if (SectionBoxLevelHelper.FindLowestUpperBeamBottom(doc, minX, minY, maxX, maxY, levelZ, geom.Eye.Z, tol, out double beamBottom, out int n))
                    r.BeamBottomZ = beamBottom;
            }
            catch (Exception ex) { Logger.Error("相機 Key Plan：找上層樑失敗，略過", ex); }

            r.Log = $"levelZ={CameraMath.FeetToMm(levelZ):F0}mm eyeZ={CameraMath.FeetToMm(geom.Eye.Z):F0}mm " +
                    $"slabBottom={CameraMath.FeetToMm(r.SlabBottomZ):F0}mm [{slabSrc}] " +
                    $"beamBottom={(r.BeamBottomZ.HasValue ? CameraMath.FeetToMm(r.BeamBottomZ.Value).ToString("F0") + "mm" : "none")}";
            return r;
        }

        /// <summary>
        /// 等角視角範圍：
        ///   XY：<see cref="ComputeFanXY"/>（相機眼睛＋視野扇形，遠裁剪夾到 30m，加平面 margin）。
        ///   Z 下限：相機所在樓層高程，往下用「支撐樑」延伸（規則同 set_3d_section_box）。
        ///   Z 上限（2026-09-24 修訂，兩種模式都要「從上方看得到本層」）：
        ///     - FullWithUpperBeams「等角：整層（見上層樑）」：上層樓板底 −10mm（上層樑看得到、樓板切掉）。
        ///     - CurrentLevelTopOnly「等角：只到當層（不含上層）」：min(上層樓板底, 上層樑底) −10mm。
        ///   上層樓板底的判定與備援見 <see cref="ComputeStoreyTop"/>。
        /// </summary>
        private static BoundingBoxXYZ BuildIsoSectionBox(Document doc, CameraMath.CameraGeometry geom, IsoXY xy, StoreyTop storeyTop)
        {
            double targetMinX = xy.MinX, targetMinY = xy.MinY, targetMaxX = xy.MaxX, targetMaxY = xy.MaxY;
            double levelZ = geom.LevelBelow.ProjectElevation;
            double toleranceFeet = CameraMath.MmToFeet(IsoSupportingBeamToleranceMm);

            if (storeyTop != null && storeyTop.Outdoor)
            {
                // 2026-09-25 室外規則：XY＝扇形外接框 +2m（IsoXY），Z＝StoreyTop 室外範圍；不找支撐樑／本層樓板／上層樓板。
                Logger.Info($"相機 Key Plan：等角範圍（室外）X[{CameraMath.FeetToMm(targetMinX):F0},{CameraMath.FeetToMm(targetMaxX):F0}] " +
                            $"Y[{CameraMath.FeetToMm(targetMinY):F0},{CameraMath.FeetToMm(targetMaxY):F0}] Z[{CameraMath.FeetToMm(storeyTop.OutdoorBottomZ):F0},{CameraMath.FeetToMm(storeyTop.OutdoorTopZ):F0}]mm");
                return new BoundingBoxXYZ
                {
                    Transform = Transform.Identity,
                    Min = new XYZ(targetMinX, targetMinY, storeyTop.OutdoorBottomZ),
                    Max = new XYZ(targetMaxX, targetMaxY, storeyTop.OutdoorTopZ)
                };
            }

            // 2026-09-24 第四輪：XY 只由 IsoXY（看得到的房間）決定——支撐樑只用來往下延伸 Z，
            // 不再把整根樑的 XY 聯集進來（長樑會把範圍拉到整排單元）。
            double minX = targetMinX, minY = targetMinY, maxX = targetMaxX, maxY = targetMaxY;
            double bx0 = targetMinX, by0 = targetMinY, bx1 = targetMaxX, by1 = targetMaxY, beamMinZ = levelZ;
            int beamCount = 0;
            try
            {
                beamCount = SectionBoxLevelHelper.IncludeSupportingBeams(doc,
                    targetMinX, targetMinY, targetMaxX, targetMaxY, levelZ, toleranceFeet,
                    ref bx0, ref by0, ref beamMinZ, ref bx1, ref by1);
            }
            catch (Exception ex) { Logger.Error("相機 Key Plan：找支撐樑失敗，略過", ex); beamMinZ = levelZ; }

            // Z 下限（2026-09-24 第四輪，使用者回報「沒看到樓板」）：舊規則下限＝樓層高程，樓板頂剛好在
            // 樓層高程、板身在下方 → 整片被切掉。新規則：min(本層樓板底 −50mm, 支撐樑底)；本層樓板＝
            // XY 與範圍重疊、頂面在樓層高程 ±300mm 內的 Floors（取 BoundingBox 最低 Z）；找不到 → 樓層 −300mm。
            string bottomSrc;
            double floorBased;
            if (TryFindFloorBottomAtLevel(doc, targetMinX, targetMinY, targetMaxX, targetMaxY, levelZ, toleranceFeet,
                    out double floorBottom, out int floorCount, out string floorIds))
            {
                floorBased = floorBottom - CameraMath.MmToFeet(IsoFloorBelowClearanceMm);
                bottomSrc = $"floors[{floorCount}] ({floorIds}) bottom={CameraMath.FeetToMm(floorBottom):F0}mm −{IsoFloorBelowClearanceMm:F0}mm";
            }
            else
            {
                floorBased = levelZ - CameraMath.MmToFeet(DefaultSlabThicknessMm);
                bottomSrc = $"no floor at level → level −{DefaultSlabThicknessMm:F0}mm";
            }
            double minZ = floorBased;
            if (beamCount > 0 && beamMinZ < minZ)
            {
                minZ = beamMinZ;
                bottomSrc += $"; supporting beams[{beamCount}] bottom={CameraMath.FeetToMm(beamMinZ):F0}mm (chosen)";
            }
            else if (beamCount > 0)
            {
                bottomSrc += $"; supporting beams[{beamCount}] bottom={CameraMath.FeetToMm(beamMinZ):F0}mm (higher, not chosen)";
            }
            Logger.Info($"相機 Key Plan：等角 Z 下限={CameraMath.FeetToMm(minZ):F0}mm levelZ={CameraMath.FeetToMm(levelZ):F0}mm 來源：{bottomSrc}");

            CameraIsoExtentMode mode = ConfigManager.Instance.Settings.CameraManager.IsoExtentMode;
            double clearanceFeet = CameraMath.MmToFeet(IsoUpperSlabClearanceMm);
            double maxZ;
            if (storeyTop.RoomTopZ.HasValue)
            {
                // 2026-09-25：房間頂 +0.3m 為主，再與（房間頂以上的）上層樓板底 −10mm 取小；「只到當層」再與上層樑底 −10mm 取小。
                maxZ = storeyTop.RoomTopZ.Value + CameraMath.MmToFeet(RoomTopIsoBufferMm);
                if (!double.IsInfinity(storeyTop.SlabBottomZ) && !double.IsNaN(storeyTop.SlabBottomZ))
                    maxZ = Math.Min(maxZ, storeyTop.SlabBottomZ - clearanceFeet);
                if (mode == CameraIsoExtentMode.CurrentLevelTopOnly && storeyTop.BeamBottomZ.HasValue)
                    maxZ = Math.Min(maxZ, storeyTop.BeamBottomZ.Value - clearanceFeet);
            }
            else
            {
                double top = storeyTop.SlabBottomZ;
                if (mode == CameraIsoExtentMode.CurrentLevelTopOnly && storeyTop.BeamBottomZ.HasValue)
                    top = Math.Min(top, storeyTop.BeamBottomZ.Value);
                maxZ = top - clearanceFeet;
            }

            if (maxZ <= minZ) maxZ = minZ + Cm(300); // 保底：極端情形（找到的頂低於底）絕不做出負高度的 SectionBox
            Logger.Info($"相機 Key Plan：等角範圍 mode={mode} topZ={CameraMath.FeetToMm(maxZ):F0}mm minZ={CameraMath.FeetToMm(minZ):F0}mm");

            return new BoundingBoxXYZ
            {
                Transform = Transform.Identity,
                Min = new XYZ(minX, minY, minZ),
                Max = new XYZ(maxX, maxY, maxZ)
            };
        }

        /// <summary>找出 levelsSorted 中，高程略高於 aboveZ 的下一個樓層高程；levelsSorted 必須已依高程排序。</summary>
        private static double? FindNextLevelElevation(List<Level> levelsSorted, double aboveZ)
        {
            Level next = levelsSorted?.FirstOrDefault(l => l.ProjectElevation > aboveZ + 1e-6);
            return next?.ProjectElevation;
        }

        // ══════════════════════════════════════════════════════════
        // 2026-09-24 第四輪：相機參數共用、等角「看得到的房間」XY、本層樓板底、標記即時重畫
        // ══════════════════════════════════════════════════════════

        /// <summary>一鍵 4 視圖與「更新相機標記」共用的相機衍生參數（遠裁剪、水平/垂直半視角、水平前方向、
        /// 眼睛所在房間、扇形半徑）。</summary>
        internal sealed class CameraParams
        {
            public CameraMath.CameraGeometry Geom;
            public XYZ ForwardHoriz;
            /// <summary>2026-09-25「畫面中心線」：水平視錐左右緣的角平分線（非對稱裁剪時≠相機正前方），
            /// ＝PlanEdgeDir(ForwardHoriz, (LeftRad+RightRad)/2)。平面視線、剖面位置/方向、扇形半徑都用它。</summary>
            public XYZ CenterHoriz;
            public double CenterYawDeg;
            public double FarClipFeet;
            public double HalfFovRad;
            public double HalfVFovRad;
            /// <summary>2026-09-25 非對稱視錐：水平左/右緣（相機右為正）、垂直下/上緣（上為正）相對正前方的帶號角度（rad）。
            /// 平面扇形＝RotateHorizontal(forwardHoriz, −edge)（RotateHorizontal 是逆時針為正、相機右在順時針側）；
            /// 剖面扇形＝pitch + DownRad … pitch + UpRad。算不出 FOV 時退回對稱 ±Half*。</summary>
            public double LeftRad, RightRad, DownRad, UpRad;
            public string EdgesLog;
            public double? FovDeg;
            public double? VFovDeg;
            public string FovLog;
            public RoomCtx Room;
            /// <summary>平面／剖面扇形半徑（feet）：視線離開房間的距離 +0.6m，夾在 [1.5m, 遠裁剪]；無房間＝min(遠裁剪, 8m)。</summary>
            public double FanRadiusFeet;
            public string FanLog;
            /// <summary>2026-09-25 室內／室外判定：眼睛所在房間（FindEyeRoom）找得到＝室內，否則＝室外。</summary>
            public bool IsOutdoor;
            public string ClassLog;
        }

        private static CameraParams ComputeCameraParams(View3D camera, List<Level> levels)
        {
            var cp = new CameraParams();
            cp.Geom = CameraMath.ComputeGeometry(camera, levels);

            Parameter farClipParam = CameraMath.FindFarClipOffsetParameter(camera);
            double farClipFeet = farClipParam != null ? farClipParam.AsDouble() : Cm(DefaultFarClipCm);
            if (!(farClipFeet > 1e-6)) farClipFeet = Cm(DefaultFarClipCm);
            cp.FarClipFeet = farClipFeet;

            // 2026-09-25：FOV 改由 CameraMath.ComputePerspectiveFov 統一計算（清單欄位、迷你地圖、平面/剖面扇形共用）。
            CameraMath.PerspectiveFov fov = CameraMath.ComputePerspectiveFov(camera);
            cp.FovDeg = fov.HFovDeg;
            cp.VFovDeg = fov.VFovDeg;
            cp.FovLog = fov.Log;
            cp.HalfFovRad = (cp.FovDeg ?? DefaultFovDeg) / 2.0 * Math.PI / 180.0;
            cp.HalfVFovRad = (cp.VFovDeg ?? (cp.FovDeg ?? DefaultFovDeg) * 0.75) / 2.0 * Math.PI / 180.0;
            const double D2R = Math.PI / 180.0;
            bool asym = fov.LeftDeg.HasValue && fov.RightDeg.HasValue && fov.DownDeg.HasValue && fov.UpDeg.HasValue
                        && fov.RightDeg.Value > fov.LeftDeg.Value && fov.UpDeg.Value > fov.DownDeg.Value;
            cp.LeftRad = asym ? fov.LeftDeg.Value * D2R : -cp.HalfFovRad;
            cp.RightRad = asym ? fov.RightDeg.Value * D2R : cp.HalfFovRad;
            cp.DownRad = asym ? fov.DownDeg.Value * D2R : -cp.HalfVFovRad;
            cp.UpRad = asym ? fov.UpDeg.Value * D2R : cp.HalfVFovRad;
            cp.EdgesLog = $"leftDeg={cp.LeftRad / D2R:F1} rightDeg={cp.RightRad / D2R:F1} downDeg={cp.DownRad / D2R:F1} upDeg={cp.UpRad / D2R:F1}" +
                          (asym ? $" (source={fov.Source})" : " (symmetric fallback)");

            XYZ fh = new XYZ(cp.Geom.Forward.X, cp.Geom.Forward.Y, 0);
            cp.ForwardHoriz = fh.GetLength() > 1e-6 ? fh.Normalize() : XYZ.BasisY;
            try
            {
                XYZ ch = PlanEdgeDir(cp.ForwardHoriz, (cp.LeftRad + cp.RightRad) / 2.0);
                cp.CenterHoriz = ch.GetLength() > 1e-6 ? new XYZ(ch.X, ch.Y, 0).Normalize() : cp.ForwardHoriz;
            }
            catch { cp.CenterHoriz = cp.ForwardHoriz; }
            cp.CenterYawDeg = CameraMath.ComputeHorizontalAngleDeg(cp.CenterHoriz);
            cp.EdgesLog += $" | 中心線 yaw={cp.CenterYawDeg:F1}°（相機 yaw={CameraMath.ComputeHorizontalAngleDeg(cp.ForwardHoriz):F1}°，偏 {(cp.LeftRad + cp.RightRad) / 2.0 / D2R:F1}°）";

            // 眼睛所在房間＋扇形半徑（任何失敗 → 無房間規則）
            double minR = CameraMath.MmToFeet(FanMinRadiusMm);
            try
            {
                cp.Room = cp.Geom.LevelBelow != null ? FindEyeRoom(camera.Document, camera, cp.Geom) : null;
            }
            catch (Exception ex)
            {
                Logger.Error("相機 Key Plan：找眼睛所在房間失敗，改用無房間規則", ex);
                cp.Room = null;
            }
            cp.IsOutdoor = cp.Room?.Room == null;
            cp.ClassLog = cp.IsOutdoor
                ? "室外（" + (cp.Geom.LevelBelow == null ? "找不到下方樓層" : "眼睛不在任何房間：" + (cp.Room?.Log ?? "room lookup failed")) + "）"
                : "室內（房間 id=" + cp.Room.Room.Id.GetIdValue() + "）";
            if (cp.IsOutdoor)
            {
                double outdoorR = GetOutdoorRadiusFeet(out double outdoorM);
                double ro = Math.Max(minR, Math.Min(farClipFeet, outdoorR));
                cp.FanRadiusFeet = ro;
                cp.FanLog = $"outdoor → min(farClip {CameraMath.FeetToMm(farClipFeet):F0}mm, 室外半徑 {outdoorM:F0}m) = {CameraMath.FeetToMm(ro):F0}mm (≥1.5m)";
                return cp;
            }
            double? exit = null;
            if (cp.Room?.Room != null)
            {
                try { exit = RayExitDistance(cp.Room, cp.Geom.Eye, cp.CenterHoriz); } // 2026-09-25：沿畫面中心線
                catch (Exception ex) { Logger.Error("相機 Key Plan：視線離開房間距離計算失敗", ex); exit = null; }
            }
            double r;
            if (exit.HasValue)
            {
                r = exit.Value + CameraMath.MmToFeet(RoomBufferMm);
                cp.FanLog = $"exit={CameraMath.FeetToMm(exit.Value):F0}mm +0.6m";
            }
            else
            {
                r = Math.Min(farClipFeet, CameraMath.MmToFeet(NoRoomFanCapMm));
                cp.FanLog = cp.Room?.Room != null ? "room but no exit hit → min(farClip,8m)" : "no room → min(farClip,8m)";
            }
            r = Math.Min(farClipFeet, Math.Max(minR, r));
            cp.FanRadiusFeet = r;
            cp.FanLog += $" → clamp[1.5m, farClip {CameraMath.FeetToMm(farClipFeet):F0}mm]";
            return cp;
        }

        /// <summary>室外範圍半徑（設定，m → feet）；讀設定失敗用預設 60m。</summary>
        private static double GetOutdoorRadiusFeet(out double meters)
        {
            meters = CameraManagerSettings.DefaultOutdoorRadiusM;
            try { meters = ConfigManager.Instance.Settings.CameraManager.GetEffectiveOutdoorRadiusM(); }
            catch (Exception ex) { Logger.Error("相機 Key Plan：讀取室外範圍半徑設定失敗，用預設 60m", ex); }
            return CameraMath.MmToFeet(meters * 1000.0);
        }

        /// <summary>
        /// 清單列用的室內／室外判定（與一鍵 4 視圖同一套 FindEyeRoom）：true＝室外、false＝室內、null＝無法判定
        /// （找不到下方樓層或查詢例外）。outdoorFanRadiusFeet＝室外時的扇形半徑 min(遠裁剪, 室外半徑)，室內為 0。
        /// 呼叫端須在 API context。
        /// </summary>
        internal static bool? ClassifyIndoorOutdoor(View3D camera, CameraMath.CameraGeometry geom, out double outdoorFanRadiusFeet, out string log)
        {
            outdoorFanRadiusFeet = 0;
            log = null;
            if (camera == null || geom?.LevelBelow == null) { log = "no level below"; return null; }
            try
            {
                RoomCtx rc = FindEyeRoom(camera.Document, camera, geom);
                if (rc?.Room != null) { log = "室內 " + rc.Log; return false; }
                Parameter farClipParam = CameraMath.FindFarClipOffsetParameter(camera);
                double farClipFeet = farClipParam != null ? farClipParam.AsDouble() : Cm(DefaultFarClipCm);
                if (!(farClipFeet > 1e-6)) farClipFeet = Cm(DefaultFarClipCm);
                double r = GetOutdoorRadiusFeet(out _);
                outdoorFanRadiusFeet = Math.Max(CameraMath.MmToFeet(FanMinRadiusMm), Math.Min(farClipFeet, r));
                log = "室外 " + (rc?.Log ?? "");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error("相機管理：室內／室外判定失敗（清單顯示 ?）", ex);
                log = "failed: " + ex.Message;
                return null;
            }
        }

        /// <summary>室外扇形外接框（XY，模型座標）：眼睛＋左緣到右緣弧上 16 等分取樣點（含兩緣端點），半徑 radiusFeet，再外擴 marginFeet。</summary>
        private static void ComputeSampledFanBox(XYZ eye, XYZ forwardHoriz, double leftRad, double rightRad, double radiusFeet, double marginFeet,
            out double minX, out double minY, out double maxX, out double maxY)
        {
            minX = maxX = eye.X; minY = maxY = eye.Y;
            const int samples = 16;
            for (int i = 0; i <= samples; i++)
            {
                double a = leftRad + (rightRad - leftRad) * i / samples;
                XYZ p = eye + PlanEdgeDir(forwardHoriz, a) * radiusFeet;
                minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
                minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
            }
            minX -= marginFeet; minY -= marginFeet; maxX += marginFeet; maxY += marginFeet;
        }

        /// <summary>眼睛所在房間（2026-09-25）：房間元素、平面輪廓點/線段（XY）、輪廓外接框（不含 buffer）、房間頂。</summary>
        internal sealed class RoomCtx
        {
            public Element Room;
            public List<XYZ> Pts = new List<XYZ>();
            public List<KeyValuePair<XYZ, XYZ>> Segs = new List<KeyValuePair<XYZ, XYZ>>();
            public double MinX, MinY, MaxX, MaxY;
            public double? TopZ;
            public string Log;
        }

        /// <summary>
        /// 眼睛所在房間：doc.GetRoomAtPoint（透視圖 VIEW_PHASE），依序試「眼睛高度」「樓層 +1m」「樓層 +0.3m」
        /// （挑高房間跨兩層時眼睛高度仍在房間體積內；其他特殊情形再退到樓層附近）。輪廓＝房間邊界線段
        /// （Tessellate）；取不到邊界就用房間 BoundingBox 四角。房間頂＝房間 BoundingBox Max.Z。找不到房間回傳
        /// Room==null 的 RoomCtx（Log 說明試過哪些點）。
        /// </summary>
        private static RoomCtx FindEyeRoom(Document doc, View3D camera, CameraMath.CameraGeometry geom)
        {
            var rc = new RoomCtx();
            Phase phase = null;
            try
            {
                Parameter pp = camera.get_Parameter(BuiltInParameter.VIEW_PHASE);
                if (pp != null) phase = doc.GetElement(pp.AsElementId()) as Phase;
            }
            catch { phase = null; }

            double levelZ = geom.LevelBelow.ProjectElevation;
            var tries = new List<KeyValuePair<string, XYZ>>
            {
                new KeyValuePair<string, XYZ>("eyeZ", geom.Eye),
                new KeyValuePair<string, XYZ>("level+1m", new XYZ(geom.Eye.X, geom.Eye.Y, levelZ + CameraMath.MmToFeet(1000))),
                new KeyValuePair<string, XYZ>("level+0.3m", new XYZ(geom.Eye.X, geom.Eye.Y, levelZ + CameraMath.MmToFeet(300)))
            };
            Autodesk.Revit.DB.Architecture.Room room = null;
            string how = null;
            foreach (var t in tries)
            {
                try { room = phase != null ? doc.GetRoomAtPoint(t.Value, phase) : doc.GetRoomAtPoint(t.Value); }
                catch { room = null; }
                if (room != null) { how = t.Key; break; }
            }
            if (room == null)
            {
                rc.Log = $"none (phase={phase?.Name ?? "(none)"}, tried eyeZ/level+1m/level+0.3m)";
                return rc;
            }
            rc.Room = room;

            try
            {
                var opts = new SpatialElementBoundaryOptions();
                IList<IList<BoundarySegment>> loops = room.GetBoundarySegments(opts);
                if (loops != null)
                {
                    foreach (IList<BoundarySegment> loop in loops)
                    {
                        foreach (BoundarySegment seg in loop)
                        {
                            Curve c = seg?.GetCurve();
                            if (c == null) continue;
                            IList<XYZ> tp = c.Tessellate();
                            for (int k = 0; k < tp.Count; k++)
                            {
                                XYZ q = new XYZ(tp[k].X, tp[k].Y, 0);
                                rc.Pts.Add(q);
                                if (k > 0) rc.Segs.Add(new KeyValuePair<XYZ, XYZ>(new XYZ(tp[k - 1].X, tp[k - 1].Y, 0), q));
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { Logger.Error("相機 Key Plan：讀房間邊界失敗，改用房間 BoundingBox", ex); rc.Pts.Clear(); rc.Segs.Clear(); }

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

            string unb = "?";
            try
            {
                double baseZ = (room.Level?.ProjectElevation ?? levelZ) + room.BaseOffset;
                unb = CameraMath.FeetToMm(baseZ + room.UnboundedHeight).ToString("F0") + "mm";
            }
            catch { }
            rc.Log = $"id={room.Id.GetIdValue()} 「{SafeRoomLabel(room)}」 foundBy={how} phase={phase?.Name ?? "(none)"} footprint={footSrc}[{rc.Pts.Count} pts] " +
                     $"X[{CameraMath.FeetToMm(rc.MinX):F0},{CameraMath.FeetToMm(rc.MaxX):F0}] Y[{CameraMath.FeetToMm(rc.MinY):F0},{CameraMath.FeetToMm(rc.MaxY):F0}]mm " +
                     $"top(bbox)={(rc.TopZ.HasValue ? CameraMath.FeetToMm(rc.TopZ.Value).ToString("F0") + "mm" : "?")} base+unboundedHeight={unb}";
            return rc;
        }

        /// <summary>水平視線（自眼睛 XY 沿 dir）第一次碰到房間邊界線段的距離（feet）；沒碰到回傳 null。</summary>
        private static double? RayExitDistance(RoomCtx rc, XYZ eye, XYZ dir)
        {
            double best = double.PositiveInfinity;
            double Cross(double ux, double uy, double vx, double vy) => ux * vy - uy * vx;
            foreach (var seg in rc.Segs)
            {
                double ex = seg.Value.X - seg.Key.X, ey = seg.Value.Y - seg.Key.Y;
                double denom = Cross(dir.X, dir.Y, ex, ey);
                if (Math.Abs(denom) < 1e-12) continue;
                double wx = seg.Key.X - eye.X, wy = seg.Key.Y - eye.Y;
                double t = Cross(wx, wy, ex, ey) / denom;
                double u = Cross(wx, wy, dir.X, dir.Y) / denom;
                if (t > 1e-4 && u >= -1e-9 && u <= 1 + 1e-9 && t < best) best = t;
            }
            return double.IsInfinity(best) ? (double?)null : best;
        }

        /// <summary>等角 SectionBox 的 XY 範圍（模型座標，feet）＋房間範圍（平面裁剪外擴用）。</summary>
        internal sealed class IsoXY
        {
            public double MinX, MinY, MaxX, MaxY;
            public bool HasRoomUnion;
            public double RoomMinX, RoomMinY, RoomMaxX, RoomMaxY;
            /// <summary>水平視線穿過「眼睛所在房間」外接框的參數範圍（feet，自眼睛起算，可為負＝眼睛後方），
            /// 不含 buffer；剖面（沿視線模式）水平範圍用。</summary>
            public bool HasSightRange;
            public double SightMinT, SightMaxT;
            public string Log;
        }

        /// <summary>
        /// 等角 XY 規則（2026-09-25 第五輪，取代「視線取樣房間聯集」——實測取樣會穿牆進到鄰戶）：
        ///   有眼睛所在房間 → 房間輪廓外接框 +0.6m（不再與扇形框取交集）。
        ///   沒有 → 扇形框（距離夾到 8m）。
        /// </summary>
        private static IsoXY ComputeIsoXY(CameraMath.CameraGeometry geom, XYZ forwardHoriz, double leftRad, double rightRad, CameraParams cp)
        {
            var r = new IsoXY();
            RoomCtx rc = cp.Room;
            if (cp.IsOutdoor)
            {
                // 2026-09-25 室外規則：扇形（半徑 min(遠裁剪, 室外半徑)，含弧取樣）外接框 +2m；剖面視線範圍 0…半徑。
                ComputeSampledFanBox(geom.Eye, forwardHoriz, leftRad, rightRad, cp.FanRadiusFeet, CameraMath.MmToFeet(OutdoorPlanMarginMm),
                    out r.MinX, out r.MinY, out r.MaxX, out r.MaxY);
                r.HasSightRange = true; r.SightMinT = 0; r.SightMaxT = cp.FanRadiusFeet;
                r.Log = $"source=outdoorFan R={CameraMath.FeetToMm(cp.FanRadiusFeet):F0}mm +2m → X[{CameraMath.FeetToMm(r.MinX):F0},{CameraMath.FeetToMm(r.MaxX):F0}] Y[{CameraMath.FeetToMm(r.MinY):F0},{CameraMath.FeetToMm(r.MaxY):F0}]mm " +
                        $"({CameraMath.FeetToMm(r.MaxX - r.MinX):F0}×{CameraMath.FeetToMm(r.MaxY - r.MinY):F0}mm)";
                return r;
            }
            try
            {
                if (rc?.Room != null && rc.MaxX > rc.MinX && rc.MaxY > rc.MinY)
                {
                    double m = CameraMath.MmToFeet(RoomBufferMm);
                    r.HasRoomUnion = true;
                    r.RoomMinX = rc.MinX - m; r.RoomMinY = rc.MinY - m; r.RoomMaxX = rc.MaxX + m; r.RoomMaxY = rc.MaxY + m;
                    r.MinX = r.RoomMinX; r.MinY = r.RoomMinY; r.MaxX = r.RoomMaxX; r.MaxY = r.RoomMaxY;
                    if (TryRayBoxT(geom.Eye, cp.CenterHoriz ?? forwardHoriz, rc.MinX, rc.MinY, rc.MaxX, rc.MaxY, out double t0, out double t1))
                    {
                        r.HasSightRange = true; r.SightMinT = t0; r.SightMaxT = t1;
                    }
                    r.Log = $"source=eyeRoom id={rc.Room.Id.GetIdValue()}「{SafeRoomLabel(rc.Room)}」 +0.6m → X[{CameraMath.FeetToMm(r.MinX):F0},{CameraMath.FeetToMm(r.MaxX):F0}] Y[{CameraMath.FeetToMm(r.MinY):F0},{CameraMath.FeetToMm(r.MaxY):F0}]mm" +
                            (r.HasSightRange ? $" sight[{CameraMath.FeetToMm(r.SightMinT):F0},{CameraMath.FeetToMm(r.SightMaxT):F0}]mm" : " sight=none");
                    return r;
                }
            }
            catch (Exception ex)
            {
                Logger.Error("相機 Key Plan：等角房間範圍計算失敗，退回扇形框（8m）", ex);
            }

            ComputeFanXY(geom, forwardHoriz, leftRad, rightRad, cp.FarClipFeet, out r.MinX, out r.MinY, out r.MaxX, out r.MaxY, NoRoomFanCapMm);
            r.HasSightRange = true; r.SightMinT = 0; r.SightMaxT = cp.FanRadiusFeet;
            r.Log = $"source=fanFallback(no eye room, cap {NoRoomFanCapMm / 1000:F0}m) X[{CameraMath.FeetToMm(r.MinX):F0},{CameraMath.FeetToMm(r.MaxX):F0}] Y[{CameraMath.FeetToMm(r.MinY):F0},{CameraMath.FeetToMm(r.MaxY):F0}]mm";
            return r;
        }

        /// <summary>水平射線（origin + t·dir）與 XY 軸對齊矩形的交段 [t0,t1]（slab 法，t 可為負）。</summary>
        private static bool TryRayBoxT(XYZ origin, XYZ dir, double x0, double y0, double x1, double y1, out double t0, out double t1)
        {
            t0 = double.NegativeInfinity; t1 = double.PositiveInfinity;
            double[] o = { origin.X, origin.Y }, d = { dir.X, dir.Y }, lo = { x0, y0 }, hi = { x1, y1 };
            for (int i = 0; i < 2; i++)
            {
                if (Math.Abs(d[i]) < 1e-9)
                {
                    if (o[i] < lo[i] || o[i] > hi[i]) return false;
                    continue;
                }
                double a = (lo[i] - o[i]) / d[i], b = (hi[i] - o[i]) / d[i];
                if (a > b) { double tmp = a; a = b; b = tmp; }
                t0 = Math.Max(t0, a); t1 = Math.Min(t1, b);
            }
            return t1 > t0;
        }

        private static string SafeRoomLabel(Element room)
        {
            try
            {
                string num = room.get_Parameter(BuiltInParameter.ROOM_NUMBER)?.AsString();
                string name = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString();
                return ((num ?? "") + " " + (name ?? "")).Trim();
            }
            catch { return "?"; }
        }

        /// <summary>本層樓板底：XY 與範圍重疊、BoundingBox 頂面在樓層高程 ±tol 內的 Floors 中，最低的 BoundingBox Z。</summary>
        private static bool TryFindFloorBottomAtLevel(Document doc, double x0, double y0, double x1, double y1, double levelZ, double tol,
            out double bottomZ, out int count, out string ids)
        {
            bottomZ = double.PositiveInfinity;
            count = 0;
            var idList = new List<string>();
            try
            {
                var floors = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Floors).WhereElementIsNotElementType();
                foreach (Element f in floors)
                {
                    if (!SectionBoxLevelHelper.TryGetWorldBounds(f, out double a0, out double b0, out double z0, out double a1, out double b1, out double z1)) continue;
                    bool overlaps = a1 > x0 && a0 < x1 && b1 > y0 && b0 < y1;
                    if (!overlaps || Math.Abs(z1 - levelZ) > tol) continue;
                    count++;
                    if (idList.Count < 6) idList.Add(f.Id.GetIdValue().ToString());
                    if (z0 < bottomZ) bottomZ = z0;
                }
            }
            catch (Exception ex)
            {
                Logger.Error("相機 Key Plan：找本層樓板失敗", ex);
                count = 0;
            }
            ids = string.Join(",", idList) + (count > idList.Count ? ",…" : "");
            return count > 0 && !double.IsInfinity(bottomZ);
        }

        /// <summary>
        /// 規格 §4.4（2026-09-24 第四輪）：透視圖裁剪框（FOV）或相機位置/方向改變後，只重畫這些相機在
        /// Key Plan 平面／剖面上的標記（含水平／垂直視野扇形），單一交易「更新相機標記」。不動任何
        /// 裁剪框、剖面範圍、等角 SectionBox，也不並排——剖面自己的裁剪若已包不住扇形就維持原樣
        /// （扇形半徑本來就會截到剖面裁剪邊）。呼叫端須在 API context（CameraManagerEventQueue）。
        /// 回傳重畫的相機數；沒有 Key Plan 的相機直接略過。
        /// </summary>
        internal static int UpdateMarkersForCameras(Document doc, ICollection<ElementId> cameraIds)
        {
            if (doc == null || cameraIds == null || cameraIds.Count == 0) return 0;
            if (Schema.Lookup(SchemaGuid) == null) return 0; // 從未建立過 Key Plan

            // 一次掃描所有視圖，找出 cid → (Plan, Section)
            var targets = new Dictionary<string, View3D>(StringComparer.Ordinal);
            foreach (ElementId id in cameraIds)
            {
                if (doc.GetElement(id) is View3D v && !v.IsTemplate && v.IsPerspective) targets[v.UniqueId] = v;
            }
            if (targets.Count == 0) return 0;

            var plans = new Dictionary<string, ViewPlan>(StringComparer.Ordinal);
            var sections = new Dictionary<string, ViewSection>(StringComparer.Ordinal);
            foreach (View v in new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>())
            {
                if (v.IsTemplate) continue;
                if (!TryReadLink(v, out string cid, out string role) || cid == null || !targets.ContainsKey(cid)) continue;
                if (role == "Plan" && v is ViewPlan vp) plans[cid] = vp;
                else if (role == "Section" && v is ViewSection vs) sections[cid] = vs;
            }
            List<string> work = targets.Keys.Where(k => plans.ContainsKey(k) || sections.ContainsKey(k)).ToList();
            if (work.Count == 0) return 0;

            List<Level> levels = CameraMath.GetStoreyLevelsSorted(doc);
            int done = 0;
            IsUpdatingMarkers = true;
            try
            {
                using (Transaction t = TransactionHelper.Begin(doc, MarkerUpdateTransactionName))
                {
                    t.Start();
                    try
                    {
                        foreach (string cid in work)
                        {
                            View3D cam = targets[cid];
                            CameraParams cp = ComputeCameraParams(cam, levels);
                            if (cp.Geom.LevelBelow == null)
                            {
                                Logger.Info($"相機 Key Plan：更新標記略過「{cam.Name}」（找不到下方樓層）。");
                                continue;
                            }
                            if (plans.TryGetValue(cid, out ViewPlan plan))
                                DrawMarkerSafely(doc, "UpdatePlanMarker",
                                    () => DrawPlanMarker(doc, plan, cp.Geom, cp.ForwardHoriz, cp.LeftRad, cp.RightRad, cp.FanRadiusFeet, cid));
                            if (sections.TryGetValue(cid, out ViewSection sec))
                                DrawMarkerSafely(doc, "UpdateSectionMarker",
                                    () => DrawSectionMarker(doc, sec, cp.Geom, cp.CenterHoriz ?? cp.ForwardHoriz, cp.FanRadiusFeet, cp.DownRad, cp.UpRad, cid));
                            if (plan != null && sec != null)
                                DrawMarkerSafely(doc, "UpdateHideOtherSections",
                                    () => HideOtherSectionMarkers(doc, plan, sec));
                            done++;
                            Logger.Info($"相機 Key Plan：更新標記「{cam.Name}」hFOV={(cp.FovDeg.HasValue ? cp.FovDeg.Value.ToString("F1") : "?")}° " +
                                $"vFOV={(cp.VFovDeg.HasValue ? cp.VFovDeg.Value.ToString("F1") : "?")}° edges[{cp.EdgesLog}] farClip={CameraMath.FeetToMm(cp.FarClipFeet):F0}mm " +
                                $"fanR={CameraMath.FeetToMm(cp.FanRadiusFeet):F0}mm ({cp.FanLog}) room={(cp.Room?.Room != null ? cp.Room.Room.Id.GetIdValue().ToString() : "none")} " +
                                $"plan={(plan != null ? plan.Id.GetIdValue().ToString() : "-")} section={(sec != null ? sec.Id.GetIdValue().ToString() : "-")} | FOV[{cp.FovLog}]");
                        }
                        t.Commit();
                    }
                    catch
                    {
                        try { if (t.HasStarted() && !t.HasEnded()) t.RollBack(); } catch { }
                        throw;
                    }
                }
            }
            finally
            {
                IsUpdatingMarkers = false;
            }
            return done;
        }

        /// <summary>
        /// 2026-09-25 第五輪（使用者：等角要「從相機起點的方向看」，舊版固定 TOP-BACK-LEFT 常從反方向看）：
        /// 前方向＝相機水平前方向（yaw）往下俯 35.26°（標準等角俯角）；眼睛放在 SectionBox 中心沿 −forward
        /// 退到 box 外（至少 5m 或對角線長）＝相機後上方；up＝與 forward 垂直、不滾動（BuildLevelUp）。
        /// 平行投影下眼睛精確位置不影響畫面，只要在 box 外即可。
        /// </summary>
        private static ViewOrientation3D BuildIsoOrientation(BoundingBoxXYZ box, XYZ forwardHoriz)
        {
            XYZ fh = forwardHoriz != null ? new XYZ(forwardHoriz.X, forwardHoriz.Y, 0) : XYZ.BasisY;
            fh = fh.GetLength() > 1e-6 ? fh.Normalize() : XYZ.BasisY;
            // 2026-09-25 第六輪（使用者：等角必須是真正的標準等角）：從 ViewCube 上方四角
            // （水平 NE/NW/SE/SW＝yaw 45°/135°/225°/315°，專案座標 X/Y 軸）挑水平方向與相機水平前方向
            // 最接近者，forward＝(±1, ±1, −1)/√3（俯角 atan(1/√2)=35.264°），不再用相機精確 yaw。
            XYZ forward = CameraMath.SnapToIsoCorner(fh, out double cornerYaw);
            XYZ center = (box.Min + box.Max) / 2.0;
            double diagonal = (box.Max - box.Min).GetLength();
            double backoff = Math.Max(diagonal, Cm(IsoMinBackoffCm));
            XYZ eye = center - forward * backoff;
            XYZ up = CameraMath.BuildLevelUp(forward);
            Logger.Info($"相機 Key Plan：等角方向 相機yaw={CameraMath.ComputeHorizontalAngleDeg(fh):F1}° → ViewCube 角 yaw={cornerYaw:F0}° 俯角=35.264° forward=({forward.X:F4},{forward.Y:F4},{forward.Z:F4})");
            return new ViewOrientation3D(eye, up, forward);
        }

        // ══════════════════════════════════════════════════════════
        // B 型標記（規格 §5.3，第一版：只有平面／剖面，3D 不畫）
        // ══════════════════════════════════════════════════════════

        // 2026-09-24：標記改版——方向指示不再是「開口線段箭頭」，改成「圓圈（眼睛位置）＋
        // 方向桿＋實心三角形箭頭」，且大小改成「列印比例固定」（依 view.Scale 換算，印出來
        // 恆定約幾 mm），不再是模型空間固定的 cm 常數——這樣不同比例的 Key Plan 視圖，標記
        // 印在紙上看起來都一樣大。視野扇形維持原有的物理範圍（跟著 farClipFeet／halfFovRad），
        // 不受這次改版影響。
        private const double MarkerCirclePrintRadiusMm = 1.0;
        // 2026-09-25（使用者回報：看大範圍時三角形太小）：箭頭總長（眼睛→箭尖）改成
        //   arrowLen = max(8mm 紙上長度 × view.Scale, 15% × 畫出來的扇形半徑)
        // 頭寬 ≈ 0.6 × arrowLen（半寬 0.3），頭長沿用舊比例 1.4/3 ≈ 0.467 × arrowLen；桿仍是線。
        // 這是模型/詳圖幾何，使用者縮放時自然跟著縮放。
        private const double MarkerArrowMinPrintLenMm = 8.0;
        private const double MarkerArrowFanRatio = 0.15;
        private const double MarkerArrowHeadLenRatio = 1.4 / 3.0;
        private const double MarkerArrowHeadHalfWidthRatio = 0.3;
        /// <summary>箭頭 FilledRegion 在該視圖的「依元素取代」表面透明度（0–100）。</summary>
        private const int MarkerArrowTransparency = 30; // 2026-09-25：眼睛楔形 30%（比扇形 70% 深）

        /// <summary>箭頭總長（feet）：max(8mm 紙上 × 比例, 15% × 扇形半徑)。</summary>
        private static double ComputeArrowLenFeet(View view, double fanRadiusFeet)
        {
            double byPaper = PrintSizeFeet(view, MarkerArrowMinPrintLenMm);
            double byFan = (fanRadiusFeet > 0 && !double.IsInfinity(fanRadiusFeet) && !double.IsNaN(fanRadiusFeet))
                ? fanRadiusFeet * MarkerArrowFanRatio : 0.0;
            return Math.Max(byPaper, byFan);
        }

        /// <summary>
        /// 箭頭半透明：在箭頭所屬視圖對這個 FilledRegion 設「依元素取代」——表面前景＝實心填滿、
        /// 顏色＝標記色、表面透明度 50。依據：Autodesk 社群已解答帖「How to change transparency of a
        /// filled region」＝ Override Graphics in View › By Element › Surface Transparency 對填充區域有效；
        /// API 端為 OverrideGraphicSettings.SetSurfaceTransparency（未在本機 Revit 實機驗證）。
        /// 失敗退回 Halftone＋淡化色，並記 log。回傳採用的方式字串供 log。
        /// </summary>
        private static string ApplyArrowOverride(Document doc, View view, ElementId regionId, Color color, int transparency = MarkerArrowTransparency)
        {
            if (doc == null || view == null || regionId == null || regionId == ElementId.InvalidElementId) return "skip";
            try
            {
                var ogs = new OverrideGraphicSettings();
                ElementId solidId = FindSolidFillPatternId(doc);
                if (solidId != ElementId.InvalidElementId)
                {
                    ogs.SetSurfaceForegroundPatternId(solidId);
                    ogs.SetSurfaceForegroundPatternVisible(true);
                }
                ogs.SetSurfaceForegroundPatternColor(color);
                ogs.SetSurfaceTransparency(transparency);
                view.SetElementOverrides(regionId, ogs);
                return "transparency" + transparency;
            }
            catch (Exception ex)
            {
                Logger.Error("相機 Key Plan：箭頭表面透明度取代失敗，退回 Halftone＋淡化色", ex);
                try
                {
                    var ogs = new OverrideGraphicSettings();
                    ogs.SetHalftone(true);
                    ogs.SetSurfaceForegroundPatternColor(LightenColor(color, 0.5));
                    view.SetElementOverrides(regionId, ogs);
                    return "halftone-fallback";
                }
                catch (Exception ex2)
                {
                    Logger.Error("相機 Key Plan：箭頭 Halftone 退回也失敗，箭頭維持不透明", ex2);
                    return "none";
                }
            }
        }

        private static Color LightenColor(Color c, double t)
        {
            byte L(byte v) => (byte)Math.Round(v + (255 - v) * t);
            return new Color(L(c.Red), L(c.Green), L(c.Blue));
        }

        private static ElementId FindSolidFillPatternId(Document doc)
        {
            List<FillPatternElement> all = new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement))
                .Cast<FillPatternElement>().ToList();
            FillPatternElement p =
                all.FirstOrDefault(x => x.GetFillPattern()?.IsSolidFill == true && x.GetFillPattern().Target == FillPatternTarget.Drafting)
                ?? all.FirstOrDefault(x => x.GetFillPattern()?.IsSolidFill == true);
            return p?.Id ?? ElementId.InvalidElementId;
        }

        /// <summary>「④ 標記顏色」變更時，把所有既有相機箭頭（Role＝MarkerPlan／MarkerSection 的
        /// FilledRegion）在其所屬視圖的取代顏色一併更新。回傳更新數。須在交易內呼叫。</summary>
        private static int RefreshArrowOverrides(Document doc, Color color)
        {
            int n = 0;
            foreach (FilledRegion fr in new FilteredElementCollector(doc).OfClass(typeof(FilledRegion)).Cast<FilledRegion>())
            {
                try
                {
                    if (!TryReadLink(fr, out string _, out string role)) continue;
                    if (role != "MarkerPlan" && role != "MarkerSection") continue;
                    if (!(doc.GetElement(fr.OwnerViewId) is View v)) continue;
                    // 預設楔形 30%、扇形 70%；沿用該元素目前的透明度（使用者可能調過），只更新顏色。
                    int tr = MarkerArrowTransparency;
                    try { int cur = v.GetElementOverrides(fr.Id).Transparency; if (cur > 0 && cur < 100) tr = cur; } catch { }
                    ApplyArrowOverride(doc, v, fr.Id, color, tr);
                    n++;
                }
                catch (Exception ex) { Logger.Error("相機 Key Plan：更新既有箭頭取代顏色失敗（略過該箭頭）", ex); }
            }
            return n;
        }

        /// <summary>對應 UI 面板的 accent 色 (#3B82C4)，讓 Revit 標記與面板配色一致。</summary>
        private static readonly Color MarkerFillColor = new Color(59, 130, 196);

        /// <summary>把「列印後想要的毫米」換算成這個 view 的模型空間長度（feet）：
        /// modelLength = printLength_mm * view.Scale，再轉 feet。找不到有效比例（&lt;=0，
        /// 例如某些特殊視圖）時退回 1:100，不讓標記大小算出負值或除以零。</summary>
        private static double PrintSizeFeet(View view, double printMm)
        {
            int scale = view.Scale > 0 ? view.Scale : 100;
            return CameraMath.MmToFeet(printMm * scale);
        }

        /// <summary>用兩個半圓弧拼出一個完整圓圈（Arc.Create 對「剛好一整圈」的角度是退化情形，
        /// 兩個 180° 半圓弧是常見且穩健的替代做法）。</summary>
        private static void AddCircle(List<Curve> curves, XYZ center, double radius, XYZ xAxis, XYZ yAxis)
        {
            curves.Add(Arc.Create(center, radius, 0, Math.PI, xAxis, yAxis));
            curves.Add(Arc.Create(center, radius, Math.PI, 2 * Math.PI, xAxis, yAxis));
        }

        /// <summary>
        /// 找（或建立）一個「前景填滿樣式＝實心」的 <see cref="FilledRegionType"/>，供箭頭三角形
        /// 使用。優先重用文件內已有的實心類型，不要每次都新建一個。找不到任何實心
        /// <see cref="FillPattern"/>（極少數情形，例如樣板被清得很乾淨）或文件內連一個
        /// FilledRegionType 都沒有時丟例外，呼叫端會 catch 並退回細線畫法，不讓整個標記失敗。
        /// ⚠️ 未經 Revit 實機驗證：FilledRegionType.Duplicate/ForegroundPatternId 賦值的行為
        /// 依 API 文件推斷，尚未在真實 .rvt 開檔測試。
        /// </summary>
        private const string MarkerRegionTypeName = "相機標記-實心";
        private const string MarkerLineStyleName = "相機標記線";
        private const int MarkerLineWeight = 5;
        /// <summary>視野扇形半透明填滿的表面透明度（0–100）。</summary>
        private const int MarkerFanTransparency = 70;

        /// <summary>設定裡的標記顏色（#RRGGBB）；解析失敗退回預設藍 #3B82C4。</summary>
        internal static Color GetConfiguredMarkerColor()
        {
            try
            {
                string hex = ConfigManager.Instance.Settings.CameraManager.MarkerColorHex;
                if (TryParseHexColor(hex, out byte r, out byte g, out byte b)) return new Color(r, g, b);
            }
            catch { }
            return new Color(59, 130, 196);
        }

        internal static bool TryParseHexColor(string hex, out byte r, out byte g, out byte b)
        {
            r = g = b = 0;
            if (string.IsNullOrWhiteSpace(hex)) return false;
            string h = hex.Trim().TrimStart('#');
            if (h.Length != 6) return false;
            try
            {
                r = Convert.ToByte(h.Substring(0, 2), 16);
                g = Convert.ToByte(h.Substring(2, 2), 16);
                b = Convert.ToByte(h.Substring(4, 2), 16);
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// 2026-09-24：相機標記線的線型＝OST_Lines 的子類別「相機標記線」（投影線寬 3、實線、
        /// 顏色取設定值），不存在才建立一次；已存在就原樣沿用（使用者可能在「物件型式」改過顏色，
        /// 不覆寫）。回傳其投影 GraphicsStyle；任何失敗回傳 null（呼叫端就用預設線型，不中斷）。
        /// 必須在交易內呼叫（建立子類別會修改文件）。
        /// </summary>
        private static GraphicsStyle GetOrCreateMarkerLineStyle(Document doc)
        {
            try
            {
                Category lines = Category.GetCategory(doc, BuiltInCategory.OST_Lines);
                if (lines == null) return null;
                Category sub = null;
                foreach (Category c in lines.SubCategories)
                {
                    if (c.Name == MarkerLineStyleName) { sub = c; break; }
                }
                if (sub == null)
                {
                    sub = doc.Settings.Categories.NewSubcategory(lines, MarkerLineStyleName);
                    sub.LineColor = GetConfiguredMarkerColor();
                    try { sub.SetLinePatternId(LinePatternElement.GetSolidPatternId(), GraphicsStyleType.Projection); } catch { }
                }
                // 2026-09-25：投影線寬 3→5（Key Plan 表現法：標記加粗），既有線型也一併改。
                try
                {
                    int? w = sub.GetLineWeight(GraphicsStyleType.Projection);
                    if (w != MarkerLineWeight)
                    {
                        sub.SetLineWeight(MarkerLineWeight, GraphicsStyleType.Projection);
                        Logger.Info($"相機 Key Plan：線型「{MarkerLineStyleName}」投影線寬 {w}→{MarkerLineWeight}");
                    }
                }
                catch (Exception ex) { Logger.Error("相機 Key Plan：設定相機標記線線寬失敗", ex); }
                return sub.GetGraphicsStyle(GraphicsStyleType.Projection);
            }
            catch (Exception ex)
            {
                Logger.Error("相機 Key Plan：建立/取得線型「相機標記線」失敗，標記改用預設線型", ex);
                return null;
            }
        }

        /// <summary>
        /// 「④ 標記顏色」：在單一交易「變更相機標記顏色」內，把線型「相機標記線」的 LineColor 與
        /// FilledRegionType「相機標記-實心」的前景顏色一起改掉（兩者不存在就先建立），既有標記因為
        /// 共用同一個線型/類型，全部一次變色。回傳 null＝成功，否則錯誤訊息。呼叫端須在 ExternalEvent 內。
        /// </summary>
        internal static string ApplyMarkerColor(Document doc, byte r, byte g, byte b)
        {
            if (doc == null) return "目前沒有開啟中的文件。";
            using (Transaction t = TransactionHelper.Begin(doc, "變更相機標記顏色"))
            {
                try
                {
                    t.Start();
                    Color color = new Color(r, g, b);
                    GraphicsStyle gs = GetOrCreateMarkerLineStyle(doc);
                    if (gs?.GraphicsStyleCategory != null) gs.GraphicsStyleCategory.LineColor = color;
                    try
                    {
                        ElementId typeId = GetOrCreateSolidFilledRegionType(doc);
                        if (doc.GetElement(typeId) is FilledRegionType frt && frt.Name == MarkerRegionTypeName)
                            frt.ForegroundPatternColor = color;
                    }
                    catch (Exception ex) { Logger.Error("相機 Key Plan：變更箭頭填滿顏色失敗（線色仍會更新）", ex); }
                    try
                    {
                        int n = RefreshArrowOverrides(doc, color);
                        Logger.Info($"相機 Key Plan：標記顏色變更，已更新 {n} 個既有箭頭的視圖取代顏色");
                    }
                    catch (Exception ex) { Logger.Error("相機 Key Plan：更新既有箭頭取代顏色失敗（線色仍會更新）", ex); }
                    t.Commit();
                    return null;
                }
                catch (Exception ex)
                {
                    try { if (t.HasStarted() && !t.HasEnded()) t.RollBack(); } catch { }
                    Logger.Error("相機 Key Plan：變更相機標記顏色失敗", ex);
                    return "變更標記顏色失敗：" + ex.Message;
                }
            }
        }

        /// <summary>標記繪製一律包在 SubTransaction＋try/catch：任何失敗只回滾標記本身並記 log，
        /// 絕不讓一鍵 4 視圖整筆交易回滾（視圖照樣建立）。</summary>
        private static void DrawMarkerSafely(Document doc, string what, Action draw)
        {
            SubTransaction st = null;
            try
            {
                st = new SubTransaction(doc);
                st.Start();
                draw();
                st.Commit();
            }
            catch (Exception ex)
            {
                try { if (st != null && st.HasStarted() && !st.HasEnded()) st.RollBack(); } catch { }
                Logger.Error($"相機 Key Plan：{what} 失敗，略過標記（視圖照常建立）", ex);
                CameraManagerDiagnostics.RecordCaught(what, ex);
            }
            finally
            {
                st?.Dispose();
            }
        }

        private static void CreateMarkerCurves(Document doc, View view, IEnumerable<Curve> curves, string cid, string role, GraphicsStyle style)
        {
            foreach (Curve c in curves)
            {
                if (c == null || c.Length < 1e-6) continue;
                DetailCurve dc = doc.Create.NewDetailCurve(view, c);
                if (style != null)
                {
                    try { dc.LineStyle = style; } catch { }
                }
                TagLink(dc, cid, role);
            }
        }

        private static ElementId GetOrCreateSolidFilledRegionType(Document doc)
        {
            // 2026-09-24：優先用專屬名稱「相機標記-實心」（顏色可由「④ 標記顏色」統一改），沒有才建立；
            // 建立失敗才退回任何既有實心類型。
            FilledRegionType named = new FilteredElementCollector(doc).OfClass(typeof(FilledRegionType))
                .Cast<FilledRegionType>().FirstOrDefault(t => t.Name == MarkerRegionTypeName);
            if (named != null) return named.Id;
            try
            {
                return CreateMarkerRegionType(doc);
            }
            catch (Exception ex)
            {
                Logger.Error("相機 Key Plan：建立「相機標記-實心」失敗，改用既有實心填滿類型", ex);
            }
            FilledRegionType existing = new FilteredElementCollector(doc).OfClass(typeof(FilledRegionType))
                .Cast<FilledRegionType>().FirstOrDefault(t => IsSolidForeground(doc, t));
            if (existing != null) return existing.Id;
            throw new Exception("找不到任何實心填滿區域類型。");
        }

        private static ElementId CreateMarkerRegionType(Document doc)
        {

            // FilledRegion 的前景樣式要用 Drafting 目標的填滿樣式（Model 目標套用在 FilledRegionType
            // 上不保證有效）；優先找 Drafting＋實心，退一步才接受任何 IsSolidFill（不同 Revit
            // 版本/樣板內建樣式命名不一定一致，這裡用旗標比對而非名稱字串，較穩健）。
            List<FillPatternElement> allPatterns = new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement))
                .Cast<FillPatternElement>().ToList();
            FillPatternElement solidPattern =
                allPatterns.FirstOrDefault(p => p.GetFillPattern()?.IsSolidFill == true && p.GetFillPattern().Target == FillPatternTarget.Drafting)
                ?? allPatterns.FirstOrDefault(p => p.GetFillPattern()?.IsSolidFill == true);
            if (solidPattern == null)
                throw new Exception("找不到任何實心填滿樣式 (FillPattern.IsSolidFill)。");

            FilledRegionType baseType = new FilteredElementCollector(doc).OfClass(typeof(FilledRegionType))
                .Cast<FilledRegionType>().FirstOrDefault();
            if (baseType == null)
                throw new Exception("文件內找不到任何 FilledRegionType 可供複製。");

            string uniqueName = MakeUniqueTypeName(doc, typeof(FilledRegionType), MarkerRegionTypeName);
            FilledRegionType created = baseType.Duplicate(uniqueName) as FilledRegionType;
            if (created == null)
                throw new Exception("複製 FilledRegionType 失敗。");

            created.ForegroundPatternId = solidPattern.Id;
            created.ForegroundPatternColor = GetConfiguredMarkerColor();
            return created.Id;
        }

        private static bool IsSolidForeground(Document doc, FilledRegionType type)
        {
            ElementId fgId = type.ForegroundPatternId;
            if (fgId == null || fgId == ElementId.InvalidElementId) return false;
            return (doc.GetElement(fgId) as FillPatternElement)?.GetFillPattern()?.IsSolidFill == true;
        }

        /// <summary>
        /// 2026-09-25（箭頭反轉）：取代舊的「朝前三角形箭頭」——在眼睛處畫一個實心小楔形（FilledRegion），
        /// 頂點＝眼睛、沿「與視野扇形相同的兩條（非對稱）邊界」向外張開，長度＝原箭頭長度規則
        /// （max(8mm 紙上, 15% 扇形半徑)）。同標記色、透明度 30%（比扇形填滿 70% 深），在扇形根部讀成較深的一塊。
        /// edgeEndA／edgeEndB＝兩條邊界在楔形長度處的端點，arcMid＝兩端點之間的弧中點（三點弧）；全部須與 eye 共面於視圖平面。
        /// </summary>
        private static void CreateEyeWedgeRegion(Document doc, View view, XYZ eye, XYZ edgeEndA, XYZ arcMid, XYZ edgeEndB,
            string cid, string role, GraphicsStyle style)
        {
            ElementId typeId = GetOrCreateSolidFilledRegionType(doc);
            CurveLoop loop = new CurveLoop();
            loop.Append(Line.CreateBound(eye, edgeEndA));
            loop.Append(Arc.Create(edgeEndA, edgeEndB, arcMid));
            loop.Append(Line.CreateBound(edgeEndB, eye));

            FilledRegion region = FilledRegion.Create(doc, typeId, view.Id, new List<CurveLoop> { loop });
            if (style != null)
            {
                try { region.SetLineStyleId(style.Id); } catch { /* 外框線型設定失敗不影響填滿 */ }
            }
            TagLink(region, cid, role);
            string mode = ApplyArrowOverride(doc, view, region.Id, GetConfiguredMarkerColor(), MarkerArrowTransparency);
            Logger.Info($"相機 Key Plan：眼睛楔形 {role} 長={CameraMath.FeetToMm(eye.DistanceTo(edgeEndA)):F0}/{CameraMath.FeetToMm(eye.DistanceTo(edgeEndB)):F0}mm 取代={mode}");
        }

        /// <summary>視野扇形半透明填滿（FilledRegion，同標記色、透明度 70%，取代方式同箭頭）。</summary>
        private static void CreateFanWedgeRegion(Document doc, View view, CurveLoop loop, string cid, string role, GraphicsStyle style)
        {
            ElementId typeId = GetOrCreateSolidFilledRegionType(doc);
            FilledRegion region = FilledRegion.Create(doc, typeId, view.Id, new List<CurveLoop> { loop });
            if (style != null)
            {
                try { region.SetLineStyleId(style.Id); } catch { }
            }
            TagLink(region, cid, role);
            string mode = ApplyArrowOverride(doc, view, region.Id, GetConfiguredMarkerColor(), MarkerFanTransparency);
            Logger.Info($"相機 Key Plan：扇形填滿 {role} 取代={mode}");
        }

        private static void DrawPlanMarker(Document doc, ViewPlan planView, CameraMath.CameraGeometry geom,
            XYZ forwardHoriz, double leftRad, double rightRad, double farClipFeet, string cid)
        {
            // 2026-09-25：farClipFeet 參數現在傳入的是「扇形半徑」（視線離開房間 +0.6m，夾 [1.5m, 遠裁剪]），名稱沿用。
            // 2026-09-25（非對稱視錐）：扇形從相機左緣 leftRad 到右緣 rightRad（相對正前方帶號角，相機右為正），
            // 不再是 ±h/2；中央視線仍沿相機正前方。原本的朝前三角形箭頭改成「眼睛處的實心楔形」。
            DeleteOldMarkers(doc, planView, cid, "MarkerPlan");
            GraphicsStyle style = GetOrCreateMarkerLineStyle(doc);

            double levelZ = geom.LevelBelow.ProjectElevation;
            XYZ eyeFlat = new XYZ(geom.Eye.X, geom.Eye.Y, levelZ);
            // NewDetailCurve(view, curve) 不需要顯式 SketchPlane 參數，Revit 依 view 本身的
            // 平面（平面視圖＝任意水平面）自動判定；這裡只要保證每條 curve 的 Z 一致落在
            // levelZ 這個水平面即可（FilledRegion 同理）。
            XYZ side = forwardHoriz.CrossProduct(XYZ.BasisZ);
            side = side.GetLength() > 1e-6 ? side.Normalize() : XYZ.BasisX;

            double circleR = PrintSizeFeet(planView, MarkerCirclePrintRadiusMm);
            double arrowLen = Math.Min(ComputeArrowLenFeet(planView, farClipFeet), farClipFeet); // 平面扇形半徑＝farClipFeet

            List<Curve> curves = new List<Curve>();
            AddCircle(curves, eyeFlat, circleR, forwardHoriz, side);

            // 中央視線：2026-09-25 起沿「畫面中心線」（左右緣角平分線；對稱視錐時＝相機正前方），從眼睛圓圈邊到扇形半徑。
            XYZ centerDir = PlanEdgeDir(forwardHoriz, (leftRad + rightRad) / 2.0);
            centerDir = centerDir.GetLength() > 1e-6 ? new XYZ(centerDir.X, centerDir.Y, 0).Normalize() : forwardHoriz;
            XYZ shaftStart = eyeFlat + centerDir * circleR;
            XYZ sightEnd = eyeFlat + centerDir * farClipFeet;
            if (farClipFeet > circleR && (sightEnd - shaftStart).GetLength() > 1e-6)
                curves.Add(Line.CreateBound(shaftStart, sightEnd));

            // 視野扇形：相機左緣／右緣邊界線 + 弧（物理範圍，不受「列印比例固定」影響）。
            // 弧從右緣（平面上順時針側）逆時針掃到左緣，掃角＝rightRad − leftRad＝水平總視角。
            XYZ dirLeft = PlanEdgeDir(forwardHoriz, leftRad);
            XYZ dirRight = PlanEdgeDir(forwardHoriz, rightRad);
            double sweep = rightRad - leftRad;
            XYZ arcY = XYZ.BasisZ.CrossProduct(dirRight).Normalize();
            curves.Add(Line.CreateBound(eyeFlat, eyeFlat + dirLeft * farClipFeet));
            curves.Add(Line.CreateBound(eyeFlat, eyeFlat + dirRight * farClipFeet));
            try
            {
                curves.Add(Arc.Create(eyeFlat, farClipFeet, 0, sweep, dirRight, arcY));
            }
            catch
            {
                // 視角接近/超過 180 度等退化情形：跳過弧線，保留左右邊界線即可。
            }

            // 扇形半透明填滿（先建，在邊線與楔形之下）
            try
            {
                XYZ leftEnd = eyeFlat + dirLeft * farClipFeet, rightEnd = eyeFlat + dirRight * farClipFeet;
                CurveLoop wedge = new CurveLoop();
                wedge.Append(Line.CreateBound(eyeFlat, rightEnd));
                wedge.Append(Arc.Create(eyeFlat, farClipFeet, 0, sweep, dirRight, arcY));
                wedge.Append(Line.CreateBound(leftEnd, eyeFlat));
                CreateFanWedgeRegion(doc, planView, wedge, cid, "MarkerPlan", style);
            }
            catch (Exception ex) { Logger.Error("相機 Key Plan：平面扇形填滿失敗（邊線照畫）", ex); }

            CreateMarkerCurves(doc, planView, curves, cid, "MarkerPlan", style);

            // 眼睛楔形（取代舊的朝前三角形箭頭）：頂點在眼睛、沿相同兩條邊界張開，長度＝箭頭長度規則。
            try
            {
                XYZ dirMid = PlanEdgeDir(forwardHoriz, (leftRad + rightRad) / 2.0);
                CreateEyeWedgeRegion(doc, planView, eyeFlat,
                    eyeFlat + dirRight * arrowLen, eyeFlat + dirMid * arrowLen, eyeFlat + dirLeft * arrowLen,
                    cid, "MarkerPlan", style);
            }
            catch (Exception ex)
            {
                Logger.Error("相機 Key Plan：平面眼睛楔形填滿失敗（扇形與視線照畫）", ex);
            }
            Logger.Info($"相機 Key Plan：平面扇形 leftDeg={leftRad * 180 / Math.PI:F1} rightDeg={rightRad * 180 / Math.PI:F1} " +
                        $"radius={CameraMath.FeetToMm(farClipFeet):F0}mm wedgeLen={CameraMath.FeetToMm(arrowLen):F0}mm");
        }

        /// <summary>
        /// 剖面標記：眼睛圓圈＋水平視線＋箭頭＋垂直視野扇形。2026-09-25 第五輪：
        ///   半徑＝fanRadiusFeet（與平面扇形相同：視線離開房間 +0.6m），不再截到剖面裁剪邊；
        ///   剖面可能「對齊房間長邊」而非沿視線 → 視線與扇形邊線一律「投影到剖面平面」：水平分量用
        ///   alongRaw＝forwardHoriz 去掉剖面法向分量（不正規化，所以斜交時自然縮短＝透視縮減），垂直分量不變。
        ///   視線與剖面平面夾角 &gt; 60° 記 log（扇形明顯縮短）；接近 90°（|alongRaw|&lt;0.05）只畫眼睛圓圈。
        /// 若使用者手動移過剖面，眼睛先投影到剖面目前的實際平面上，避免 NewDetailCurve 因不共面失敗。
        /// </summary>
        private static void DrawSectionMarker(Document doc, ViewSection sectionView, CameraMath.CameraGeometry geom,
            XYZ forwardHoriz, double fanRadiusFeet, double downRad, double upRad, string cid)
        {
            DeleteOldMarkers(doc, sectionView, cid, "MarkerSection");
            GraphicsStyle style = GetOrCreateMarkerLineStyle(doc);

            XYZ up = XYZ.BasisZ;
            XYZ n = sectionView.ViewDirection;
            if (n == null || n.GetLength() < 1e-6) return;
            n = n.Normalize();
            XYZ eye = geom.Eye - n * (geom.Eye - sectionView.Origin).DotProduct(n);
            XYZ alongRaw = forwardHoriz - n * forwardHoriz.DotProduct(n);
            alongRaw = new XYZ(alongRaw.X, alongRaw.Y, 0);
            double lenA = alongRaw.GetLength();
            double planeAngleDeg = Math.Acos(Math.Max(0.0, Math.Min(1.0, lenA))) * 180.0 / Math.PI;

            double circleR = PrintSizeFeet(sectionView, MarkerCirclePrintRadiusMm);
            List<Curve> curves = new List<Curve>();
            XYZ sideAxis = lenA > 1e-6 ? alongRaw / lenA : up.CrossProduct(n).Normalize();
            AddCircle(curves, eye, circleR, sideAxis, up);

            if (lenA < 0.05)
            {
                CreateMarkerCurves(doc, sectionView, curves, cid, "MarkerSection", style);
                Logger.Info($"相機 Key Plan：剖面標記 視線幾乎垂直剖面平面（夾角 {planeAngleDeg:F1}°），只畫眼睛圓圈");
                return;
            }
            XYZ alongN = alongRaw / lenA;
            double radius = fanRadiusFeet;
            if (!(radius > 0) || double.IsInfinity(radius) || double.IsNaN(radius)) radius = CameraMath.MmToFeet(FanMinRadiusMm);

            curves.Add(Line.CreateBound(eye, eye + alongRaw * radius)); // 水平視線（投影後長度）

            double pitch = 0, aLow = 0, aHigh = 0;
            XYZ dLow = alongRaw, dHigh = alongRaw, dMid = alongRaw;
            bool fanOk = false;
            try
            {
                // 2026-09-25（非對稱視錐）：垂直扇形從 pitch+downRad 到 pitch+upRad（相對正前方帶號角，上為正），
                // 不再是 pitch ± v/2。中央視線（水平視線）不變。
                pitch = Math.Asin(Math.Max(-1.0, Math.Min(1.0, geom.Forward.Z)));
                aLow = pitch + downRad; aHigh = pitch + upRad;
                double aMid = (aLow + aHigh) / 2.0;
                dLow = alongRaw * Math.Cos(aLow) + up * Math.Sin(aLow);
                dHigh = alongRaw * Math.Cos(aHigh) + up * Math.Sin(aHigh);
                dMid = alongRaw * Math.Cos(aMid) + up * Math.Sin(aMid);
                fanOk = true;
            }
            catch (Exception ex)
            {
                Logger.Error("相機 Key Plan：剖面垂直扇形方向計算失敗，只畫視線", ex);
            }

            double arrowLen = ComputeArrowLenFeet(sectionView, radius * lenA);
            // 楔形沿扇形邊界取「與扇形同比例」的長度：k＝arrowLen／(投影後扇形半徑)，夾 ≤ 1。
            double wedgeK = Math.Min(1.0, arrowLen / Math.Max(1e-9, radius * lenA));

            if (fanOk && radius > circleR * 2)
            {
                try
                {
                    XYZ lowEnd = eye + dLow * radius, highEnd = eye + dHigh * radius, midEnd = eye + dMid * radius;
                    curves.Add(Line.CreateBound(eye, lowEnd));
                    curves.Add(Line.CreateBound(eye, highEnd));
                    curves.Add(Arc.Create(lowEnd, highEnd, midEnd)); // 三點弧（斜交時為橢圓弧的近似）
                    try
                    {
                        CurveLoop wedge = new CurveLoop();
                        wedge.Append(Line.CreateBound(eye, lowEnd));
                        wedge.Append(Arc.Create(lowEnd, highEnd, midEnd));
                        wedge.Append(Line.CreateBound(highEnd, eye));
                        CreateFanWedgeRegion(doc, sectionView, wedge, cid, "MarkerSection", style);
                    }
                    catch (Exception ex) { Logger.Error("相機 Key Plan：剖面扇形填滿失敗（邊線照畫）", ex); }
                }
                catch (Exception ex) { Logger.Error("相機 Key Plan：剖面垂直視野扇形失敗，只畫視線", ex); }
            }
            Logger.Info($"相機 Key Plan：剖面垂直視野扇形 pitch={pitch * 180 / Math.PI:F1}° downDeg={downRad * 180 / Math.PI:F1} upDeg={upRad * 180 / Math.PI:F1} " +
                        $"(vFOV={(upRad - downRad) * 180 / Math.PI:F1}°) radius={CameraMath.FeetToMm(radius):F0}mm " +
                        $"視線與剖面平面夾角={planeAngleDeg:F1}°" + (planeAngleDeg > 60 ? "（>60°：扇形投影後明顯縮短）" : ""));

            CreateMarkerCurves(doc, sectionView, curves, cid, "MarkerSection", style);

            // 眼睛楔形（取代舊的朝前三角形箭頭）：頂點在眼睛、沿相同的上下兩條邊界張開。
            if (fanOk)
            {
                try
                {
                    double wl = radius * wedgeK;
                    CreateEyeWedgeRegion(doc, sectionView, eye, eye + dLow * wl, eye + dMid * wl, eye + dHigh * wl,
                        cid, "MarkerSection", style);
                }
                catch (Exception ex)
                {
                    Logger.Error("相機 Key Plan：剖面眼睛楔形填滿失敗（扇形與視線照畫）", ex);
                }
            }
        }

        /// <summary>射線（起點在裁剪框內）沿 dir 走到裁剪框 X/Y 邊界的距離（只看剖面的 X＝水平、Y＝垂直，
        /// 深度 Z 忽略）；起點不在框內或算不出來時回傳 +∞（不截短）。</summary>
        private static double RayToCropEdge(BoundingBoxXYZ crop, XYZ origin, XYZ dir)
        {
            try
            {
                Transform inv = crop.Transform.Inverse;
                XYZ o = inv.OfPoint(origin);
                XYZ d = inv.OfVector(dir);
                if (o.X < crop.Min.X - 1e-6 || o.X > crop.Max.X + 1e-6 || o.Y < crop.Min.Y - 1e-6 || o.Y > crop.Max.Y + 1e-6)
                    return double.PositiveInfinity;
                double t = double.PositiveInfinity;
                if (d.X > 1e-9) t = Math.Min(t, (crop.Max.X - o.X) / d.X);
                else if (d.X < -1e-9) t = Math.Min(t, (crop.Min.X - o.X) / d.X);
                if (d.Y > 1e-9) t = Math.Min(t, (crop.Max.Y - o.Y) / d.Y);
                else if (d.Y < -1e-9) t = Math.Min(t, (crop.Min.Y - o.Y) / d.Y);
                return t > 0 ? t : double.PositiveInfinity;
            }
            catch
            {
                return double.PositiveInfinity;
            }
        }

        // ══════════════════════════════════════════════════════════
        // 小工具
        // ══════════════════════════════════════════════════════════

        private static string MakeUniqueName(Document doc, string baseName, ElementId excludeId)
        {
            HashSet<string> existing = new HashSet<string>(
                new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                    .Where(v => v.Id != excludeId)
                    .Select(v => v.Name),
                StringComparer.OrdinalIgnoreCase);

            if (!existing.Contains(baseName)) return baseName;
            int suffix = 2;
            string candidate = baseName + " (" + suffix + ")";
            while (existing.Contains(candidate))
            {
                suffix++;
                candidate = baseName + " (" + suffix + ")";
            }
            return candidate;
        }

        /// <summary>同 <see cref="MakeUniqueName"/>，但用於任意 <see cref="ElementType"/> 子類別
        /// （例如 <see cref="FilledRegionType"/>）的名稱去重，不限於 View。</summary>
        private static string MakeUniqueTypeName(Document doc, Type elementType, string baseName)
        {
            HashSet<string> existing = new HashSet<string>(
                new FilteredElementCollector(doc).OfClass(elementType).Cast<ElementType>().Select(t => t.Name),
                StringComparer.OrdinalIgnoreCase);

            if (!existing.Contains(baseName)) return baseName;
            int suffix = 2;
            string candidate = baseName + " " + suffix;
            while (existing.Contains(candidate))
            {
                suffix++;
                candidate = baseName + " " + suffix;
            }
            return candidate;
        }
    }
}
