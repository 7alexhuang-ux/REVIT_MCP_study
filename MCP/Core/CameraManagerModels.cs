using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace RevitMCP.Core
{
    // 相機管理停靠面板的資料模型，只是純資料，不含任何 Revit API 呼叫。
    //
    // 注意：這裡全部用 C# property（{ get; set; }），不是 public field —— WPF 的
    // Binding 只認得屬性，綁 public field 會在畫面上悄悄留白（不會拋例外，非常難查）。
    // DataGridTextColumn 的 Binding 就是綁在這些屬性上。

    /// <summary>DataGrid 一列 = 一個透視相機視圖。</summary>
    internal class CameraRow
    {
        public ElementId ViewId { get; set; }
        /// <summary>相機視圖的 Element.UniqueId（跨 session 穩定，不受 ElementId 重新編號影響）；
        /// 用於 Key Plan 剖面等以 Extensible Storage 連結相機的查詢（見 CameraKeyPlanService）。</summary>
        public string UniqueId { get; set; }
        public string Name { get; set; }
        public string LevelName { get; set; }
        /// <summary>Eye 正下方那個樓層的 ElementId；找不到基準樓層時為 null。用於挑選預設底圖平面、換樓層編輯。</summary>
        public ElementId LevelId { get; set; }
        /// <summary>離樓板高度 (cm)，找不到樓層下方基準時為 null。</summary>
        public double? HeightAboveLevelCm { get; set; }
        /// <summary>水平角 °：0 = 專案北 (+Y)，順時針為正。</summary>
        public double HorizontalAngleDeg { get; set; }
        public double PitchDeg { get; set; }
        /// <summary>Eye Elevation 參數的原始顯示字串（性質面板看到的字串，含專案顯示單位）。</summary>
        public string EyeElevationDisplay { get; set; }
        /// <summary>水平視野角估算值（度）；未經實機驗證，唯讀顯示，絕不寫回模型。null = 算不出來。</summary>
        public double? FovEstimateDeg { get; set; }
        /// <summary>2026-09-25：水平視錐左/右緣相對正前方的帶號角度（度，相機右為正；見 CameraMath.PerspectiveFov）。
        /// 使用者只拖裁剪框一邊時兩者不對稱；迷你地圖扇形用這兩個值。null＝算不出來（退回對稱）。</summary>
        public double? FovLeftDeg { get; set; }
        public double? FovRightDeg { get; set; }
        /// <summary>遠裁剪距離 (cm)；找不到對應參數時為 null，面板應改唯讀並說明。</summary>
        public double? FarClipCm { get; set; }
        /// <summary>遠裁剪參數是否可寫入（IsReadOnly==false）。找不到參數時一律 false。</summary>
        public bool FarClipEditable { get; set; }
        /// <summary>清單「遠裁剪 cm」常駐 TextBox 的 IsReadOnly 繫結用（2026-09-25）。</summary>
        public bool FarClipReadOnly => !FarClipEditable;

        // 平面座標（內部單位 feet），用於底圖疊圖與 Apply 高度時的比對。
        public double EyeXFeet { get; set; }
        public double EyeYFeet { get; set; }
        public double EyeZFeet { get; set; }
        public double ForwardXFeet { get; set; }
        public double ForwardYFeet { get; set; }
        public double ForwardZFeet { get; set; }

        /// <summary>是否落在目前底圖的裁剪範圍內；false 時只列在清單、不畫在底圖上。</summary>
        public bool InMapRange { get; set; }

        /// <summary>"" = 正常，"偏低"／"偏高" = 高度落在建議範圍外。</summary>
        public string HeightWarning { get; set; }

        /// <summary>這台相機是否已經有「一鍵 4 視圖」建立的 Key Plan 剖面（ES 連結）。false 時面板
        /// 不排讀剖面線的 ExternalEvent（2026-09-24 第三輪）。</summary>
        public bool HasKeyPlanSection { get; set; }

        /// <summary>2026-09-25 室內／室外判定（與一鍵 4 視圖同一套 FindEyeRoom）：true＝室外（眼睛不在任何房間），
        /// false＝室內，null＝判定失敗／找不到樓層。</summary>
        public bool? IsOutdoor { get; set; }
        /// <summary>室外相機的扇形半徑（feet）＝min(遠裁剪, 室外範圍半徑)；迷你地圖以模型比例畫出。室內為 0。</summary>
        public double OutdoorFanRadiusFeet { get; set; }
        /// <summary>DataGrid 列 ToolTip 前綴用：「室內」「室外」或「?」。</summary>
        public string IndoorOutdoorText => IsOutdoor.HasValue ? (IsOutdoor.Value ? "室外" : "室內") : "?";

        public string ViewIdText => ViewId.GetIdValue().ToString();

        public string HeightAboveLevelCmText => HeightAboveLevelCm.HasValue
            ? HeightAboveLevelCm.Value.ToString("F1")
            : "—";

        /// <summary>DataGrid「遠裁剪 cm」選用欄的顯示字串（找不到參數時顯示 —）。</summary>
        public string FarClipCmText => FarClipCm.HasValue
            ? FarClipCm.Value.ToString("F1")
            : "—";

        /// <summary>DataGrid「狀態」欄：高度示警優先，其次是不在底圖範圍時的提示，都沒有就空白。</summary>
        public string StatusText
        {
            get
            {
                if (!string.IsNullOrEmpty(HeightWarning)) return "高度" + HeightWarning;
                if (!InMapRange) return "不在底圖範圍";
                return "";
            }
        }

        /// <summary>平面座標 X（cm），提供給細節編輯欄使用（以內部單位換算，供顯示與回填）。</summary>
        public double EyeXCm => CameraMath.FeetToMm(EyeXFeet) / 10.0;
        public double EyeYCm => CameraMath.FeetToMm(EyeYFeet) / 10.0;
    }

    /// <summary>ComboBox 裡的一個平面視圖選項。</summary>
    internal class PlanViewOption
    {
        public ElementId ViewId { get; set; }
        public string DisplayName { get; set; }
        public ElementId GenLevelId { get; set; }
        /// <summary>平面視圖本身的名稱（不含類型後綴）；底圖清單精簡模式標籤用。</summary>
        public string PlanName { get; set; }
        /// <summary>GenLevel 名稱（沒有則 null）。</summary>
        public string LevelName { get; set; }

        // 2026-09-25（③ 底圖挑選）：裁剪框（CropBox）攤平成純 double——原點、X/Y 基底（只取 XY 分量）、
        // 視圖自身座標的 Min/Max（feet）。HasCrop＝false 表示讀不到（不參與「包含相機」判斷）。
        public bool HasCrop { get; set; }
        public double CropOriginX { get; set; }
        public double CropOriginY { get; set; }
        public double CropBasisXX { get; set; } = 1;
        public double CropBasisXY { get; set; }
        public double CropBasisYX { get; set; }
        public double CropBasisYY { get; set; } = 1;
        public double CropMinX { get; set; }
        public double CropMinY { get; set; }
        public double CropMaxX { get; set; }
        public double CropMaxY { get; set; }
        /// <summary>ViewType == FloorPlan（天花／面積平面為 false）。</summary>
        public bool IsFloorPlan { get; set; }
        /// <summary>從屬視圖（GetPrimaryViewId 有效）。挑底圖時次要。</summary>
        public bool IsDependent { get; set; }

        public double CropWidthFeet => HasCrop ? System.Math.Abs(CropMaxX - CropMinX) : 0;
        public double CropHeightFeet => HasCrop ? System.Math.Abs(CropMaxY - CropMinY) : 0;
        public double CropAreaSqFeet => CropWidthFeet * CropHeightFeet;

        /// <summary>模型座標 (x,y) 是否落在裁剪框內（矩形框；非矩形裁剪形狀以外接框近似）。純算術。</summary>
        public bool CropContains(double x, double y)
        {
            if (!HasCrop) return false;
            double dx = x - CropOriginX, dy = y - CropOriginY;
            double lx = dx * CropBasisXX + dy * CropBasisXY;
            double ly = dx * CropBasisYX + dy * CropBasisYY;
            return lx >= System.Math.Min(CropMinX, CropMaxX) && lx <= System.Math.Max(CropMinX, CropMaxX) &&
                   ly >= System.Math.Min(CropMinY, CropMaxY) && ly <= System.Math.Max(CropMinY, CropMaxY);
        }

        /// <summary>log 用：「名稱 id=… 裁剪 W×H m」。</summary>
        public string CropLogText =>
            $"「{PlanName ?? DisplayName}」id={ViewId?.GetIdValue()} 裁剪 {CameraMath.FeetToMm(CropWidthFeet) / 1000.0:F1}×{CameraMath.FeetToMm(CropHeightFeet) / 1000.0:F1} m" +
            (IsDependent ? "（從屬）" : "") + (IsFloorPlan ? "" : "（非樓層平面）");

        public override string ToString() => DisplayName;
    }

    /// <summary>
    /// 2026-09-25（規格 §4.8 底圖挑選，使用者回報：相機在 L1 但預設底圖是一張 8.3×16.7 m 的放大平面，相機不在圖內）：
    /// 自動挑底圖（預設、跟隨、自動切換、精簡清單）一律用同一套規則。純資料運算，不碰 Revit API。
    ///   同一樓層的已裁剪平面中：先限樓層平面（沒有才用天花／面積平面）；
    ///   有「裁剪框包含相機眼睛 XY」者 → 其中非從屬優先、面積最大；
    ///   都不包含（或沒給眼睛座標）→ 非從屬優先、面積最大。
    /// </summary>
    internal static class BaseMapPlanChooser
    {
        internal static PlanViewOption ChooseForLevel(IEnumerable<PlanViewOption> all, ElementId levelId,
            IList<double[]> eyesXY, out string why)
        {
            why = "";
            if (all == null || levelId == null) { why = "no level"; return null; }
            long lv = levelId.GetIdValue();
            List<PlanViewOption> ofLevel = all.Where(p => p?.GenLevelId != null && p.GenLevelId.GetIdValue() == lv).ToList();
            if (ofLevel.Count == 0) { why = "該樓層沒有已裁剪平面"; return null; }
            List<PlanViewOption> pool = ofLevel.Where(p => p.IsFloorPlan).ToList();
            if (pool.Count == 0) pool = ofLevel;

            int Contained(PlanViewOption p)
            {
                if (eyesXY == null) return 0;
                int n = 0;
                foreach (double[] e in eyesXY)
                    if (e != null && e.Length >= 2 && p.CropContains(e[0], e[1])) n++;
                return n;
            }

            PlanViewOption best = pool
                .OrderByDescending(Contained)
                .ThenBy(p => p.IsDependent ? 1 : 0)
                .ThenByDescending(p => p.CropAreaSqFeet)
                .ThenBy(p => p.PlanName ?? p.DisplayName, System.StringComparer.Ordinal)
                .First();
            int c = Contained(best);
            why = (eyesXY == null || eyesXY.Count == 0)
                ? $"最大面積（同層候選 {pool.Count} 張）"
                : c > 0 ? $"包含相機 {c}/{eyesXY.Count} 台、面積最大（同層候選 {pool.Count} 張）"
                        : $"沒有任何候選包含相機 → 最大面積（同層候選 {pool.Count} 張）";
            return best;
        }
    }

    /// <summary>ComboBox 裡的一個樓層選項（細節編輯「所在樓層」、複製相機到其他樓層共用）。</summary>
    internal class LevelOption
    {
        public ElementId LevelId { get; set; }
        public string Name { get; set; }
        public double ProjectElevationFeet { get; set; }

        public override string ToString() => Name;
    }

    /// <summary>「重新讀取相機清單＋平面視圖清單＋樓層清單」這一步驟（在 ExternalEvent 內執行）的結果。</summary>
    internal class CameraManagerSnapshot
    {
        public List<CameraRow> Cameras { get; set; } = new List<CameraRow>();
        public List<PlanViewOption> PlanViews { get; set; } = new List<PlanViewOption>();
        public List<LevelOption> Levels { get; set; } = new List<LevelOption>();
        public ElementId DefaultPlanViewId { get; set; }
        /// <summary>目前作用中視圖的 Id（由呼叫端在 ExternalEvent 內另外補上，ReadSnapshot 本身不填）；
        /// 供面板「預設選取：作用中視圖是透視圖就選它，否則選第一列」使用。</summary>
        public ElementId ActiveViewId { get; set; }
        /// <summary>目前文件內是否存在任何「已啟用 CropBox」的平面視圖；false 時底圖改用中性格線（4.8）。</summary>
        public bool HasCroppedPlan { get; set; }
        public string ErrorMessage { get; set; }
        public string DocumentTitle { get; set; }
    }

    /// <summary>「匯出底圖 + 量出裁剪範圍」這一步驟（在 ExternalEvent 內執行）的結果。</summary>
    internal class BaseMapSnapshot
    {
        public string ImageFilePath { get; set; }
        public string TempDirectory { get; set; }

        /// <summary>CropBox 在視圖自身座標系（feet）的範圍；配合下方攤平的原點／基底可還原成模型座標。</summary>
        public double CropMinXFeet { get; set; }
        public double CropMinYFeet { get; set; }
        public double CropMaxXFeet { get; set; }
        public double CropMaxYFeet { get; set; }

        /// <summary>2026-09-24 對位改版：匯出圖片實際像素（從 PNG 檔頭讀出，0＝未知）、純模型裁剪框
        /// 範圍（不含註記裁剪外擴，供「顯示對位框」除錯疊圖）、以及換算範圍的來源說明。</summary>
        public int ImagePixelWidth { get; set; }
        public int ImagePixelHeight { get; set; }
        public double ModelCropMinXFeet { get; set; }
        public double ModelCropMinYFeet { get; set; }
        public double ModelCropMaxXFeet { get; set; }
        public double ModelCropMaxYFeet { get; set; }
        public string ExtentsSource { get; set; }
        // 2026-09-24 第二輪崩潰調查：原本這裡直接存 Revit 的 Transform（APIObject，IDisposable、
        // 背後是原生物件），再拿到 WPF 執行緒上呼叫 .Inverse／.OfPoint——等於在 API context 以外
        // 操作 Revit 原生物件，而且物件的生命週期交給 GC。改成在 ExportBaseMap（API context 內）
        // 就攤平成純 double：原點＋三個基底向量。CropBox.Transform 是剛體轉換（正交基底），反轉
        // 換算＝轉置，見 ModelToLocal／ModelVectorToLocal。XYZ／ElementId 經反射確認是純受控
        // 類別（非 APIObject、非 IDisposable、無 finalizer），可安全留在快照裡。
        public double OriginX { get; set; }
        public double OriginY { get; set; }
        public double OriginZ { get; set; }
        public double BasisXX { get; set; } = 1;
        public double BasisXY { get; set; }
        public double BasisXZ { get; set; }
        public double BasisYX { get; set; }
        public double BasisYY { get; set; } = 1;
        public double BasisYZ { get; set; }
        public double BasisZX { get; set; }
        public double BasisZY { get; set; }
        public double BasisZZ { get; set; } = 1;

        /// <summary>模型座標點 → 視圖自身座標（只取 X/Y，平面用）。純算術，任何執行緒皆可呼叫。</summary>
        public void ModelToLocal(double x, double y, double z, out double localX, out double localY)
        {
            double dx = x - OriginX, dy = y - OriginY, dz = z - OriginZ;
            localX = dx * BasisXX + dy * BasisXY + dz * BasisXZ;
            localY = dx * BasisYX + dy * BasisYY + dz * BasisYZ;
        }

        /// <summary>模型座標向量 → 視圖自身座標（只取 X/Y）。</summary>
        public void ModelVectorToLocal(double x, double y, double z, out double localX, out double localY)
        {
            localX = x * BasisXX + y * BasisXY + z * BasisXZ;
            localY = x * BasisYX + y * BasisYY + z * BasisYZ;
        }
        /// <summary>底圖平面視圖所在的樓層 Id（找不到時為 null）；供迷你 key plan 篩選「只畫同樓層相機」。</summary>
        public ElementId GenLevelId { get; set; }

        public string ErrorMessage { get; set; }
        /// <summary>非致命提醒（例如底圖平面未啟用 CropBox，疊圖可能不準），匯出仍會繼續。</summary>
        public string Warning { get; set; }
    }

    /// <summary>細節編輯欄一次送出的內容（4.3）。哪個欄位有變更由呼叫端自行比對再決定要不要送。</summary>
    internal class CameraEditRequest
    {
        public ElementId ViewId { get; set; }
        public double EyeXFeet { get; set; }
        public double EyeYFeet { get; set; }
        public ElementId LevelId { get; set; }
        public double HeightAboveLevelCm { get; set; }
        public double HorizontalAngleDeg { get; set; }
        public double PitchDeg { get; set; }
        /// <summary>null = 不變更遠裁剪。</summary>
        public double? FarClipCm { get; set; }
    }

    /// <summary>
    /// 等角 Key Plan 視圖的範圍模式（規格 Change 2「工具」區的小 ComboBox）：
    ///   FullWithUpperBeams（預設）：頂面延伸到最近上層樓板底再退 10mm（含上層樑，找不到樓板時
    ///     退回下一樓層高程、再沒有就 eye 高度+3m）。
    ///   CurrentLevelTopOnly：頂面直接停在下一樓層高程（沒有下一樓層時同上退回 eye+3m），
    ///     不特別找上層樓板/樑。
    /// 兩種模式的 Z 下限（往下延伸支撐樑）規則相同，差異只在頂面怎麼決定。
    /// </summary>
    public enum CameraIsoExtentMode
    {
        FullWithUpperBeams = 0,
        CurrentLevelTopOnly = 1
    }

    /// <summary>
    /// 2026-09-25（使用者拍板）：Key Plan 剖面的方向。
    ///   AlongRoomLongAxis（預設，選項 C「對齊房間長邊」）：剖面沿眼睛所在房間的長軸、通過眼睛，
    ///     水平範圍＝房間沿該軸 +0.6m；找不到房間時退回 AlongSightLine。
    ///   AlongSightLine（選項 A「沿視線（側視）」）：舊規則，剖面平面包含水平視線。
    /// </summary>
    public enum CameraSectionOrientationMode
    {
        AlongRoomLongAxis = 0,
        AlongSightLine = 1
    }

    /// <summary>人視高預設值等，存在外掛設定檔（本機）。public：<see cref="RevitMCP.Configuration.ServiceSettings.CameraManager"/>
    /// 是 public 屬性，屬性型別的存取範圍不能比屬性窄（CS0053）。</summary>
    public class CameraManagerSettings
    {
        public double DefaultHeightAboveLevelCm { get; set; } = 160.0;

        /// <summary>相機分頁「清單／地圖」GridSplitter 位置：清單（DataGrid）佔可用高度的比例
        /// （0～1），地圖佔剩下的比例。拖曳 GridSplitter 後即時存檔。2026-09-24 拿掉「選取中
        /// 相機」卡片後，預設把騰出來的空間讓給地圖，預設值由 0.45 下修為 0.35（只影響新安裝，
        /// 已存過設定的使用者維持自己拖過的值）。</summary>
        public double CameraTabListFraction { get; set; } = 0.35;

        /// <summary>2026-09-24 第四輪版面（位置圖佔下半、⑤ 收進選單）：② 相機清單佔「清單＋位置圖」可用高度的
        /// 比例，預設 1/3。換新鍵名是因為版面整個重排，舊的 <see cref="CameraTabListFraction"/>（舊版面拖出的值）
        /// 不再適用；舊鍵保留只為相容既有設定檔。</summary>
        public double CameraListFractionV3 { get; set; } = 1.0 / 3.0;

        /// <summary>等角 Key Plan 視圖的範圍模式，見 <see cref="CameraIsoExtentMode"/>。</summary>
        public CameraIsoExtentMode IsoExtentMode { get; set; } = CameraIsoExtentMode.FullWithUpperBeams;

        /// <summary>Key Plan 剖面方向，見 <see cref="CameraSectionOrientationMode"/>（預設對齊房間長邊）。</summary>
        public CameraSectionOrientationMode SectionOrientationMode { get; set; } = CameraSectionOrientationMode.AlongRoomLongAxis;

        /// <summary>2026-09-24：Key Plan 平面／剖面相機標記線（線型「相機標記線」）與箭頭填滿顏色，
        /// 十六進位 #RRGGBB；預設藍 #3B82C4（面板 accent 色）。</summary>
        public string MarkerColorHex { get; set; } = "#3B82C4";

        /// <summary>2026-09-25：Key Plan 平面／剖面的表現法。true＝「淡化背景」（預設：模型半色調、
        /// 隱藏雜項註解類別、房間標籤半色調、自動挑 1:50/100/200 比例）；false＝「原始」（下次一鍵
        /// 4 視圖時還原 Display Model 為一般、取消隱藏這些類別、清掉房間標籤半色調）。</summary>
        public bool KeyPlanFadedStyle { get; set; } = true;

        /// <summary>2026-09-25：室外相機（眼睛不在任何房間內）的扇形／Key Plan 範圍半徑（m），實際半徑＝
        /// min(遠裁剪, 本值)。「一鍵 4 視圖 ▾ → 室外範圍半徑…」可改；有效範圍 5～1000 m（超出以預設 60 m 處理）。</summary>
        public double OutdoorRadiusM { get; set; } = DefaultOutdoorRadiusM;

        public const double DefaultOutdoorRadiusM = 60.0;
        public const double MinOutdoorRadiusM = 5.0;
        public const double MaxOutdoorRadiusM = 1000.0;

        /// <summary>讀取時防呆：非有限值或超出 [5, 1000] m → 預設 60 m。</summary>
        public double GetEffectiveOutdoorRadiusM()
        {
            double v = OutdoorRadiusM;
            return (double.IsNaN(v) || double.IsInfinity(v) || v < MinOutdoorRadiusM || v > MaxOutdoorRadiusM) ? DefaultOutdoorRadiusM : v;
        }

        // ── DataGrid 選用欄位（右鍵標題列勾選，本機記憶，預設全部關閉） ──
        public bool ShowColumnPlaneX { get; set; } = false;
        public bool ShowColumnPlaneY { get; set; } = false;
        public bool ShowColumnFarClip { get; set; } = false;
        public bool ShowColumnFov { get; set; } = false;
        public bool ShowColumnEyeElevation { get; set; } = false;

        /// <summary>迷你 key plan 是否顯示其他樓層的相機（灰色淡化＋「(樓層)」字尾）。預設關閉，
        /// 只畫底圖所在樓層的相機（見 2026-09-24 使用者實測回報：不同樓層相機混在一張底圖上
        /// 位置容易誤判）。</summary>
        public bool ShowOtherLevelCamerasOnMap { get; set; } = false;
        /// <summary>2026-09-25：③ 底圖清單「顯示全部平面」。false（預設）＝只列有相機的樓層、每層一張偏好平面。</summary>
        public bool ShowAllPlansInBaseMap { get; set; } = false;
    }
}
