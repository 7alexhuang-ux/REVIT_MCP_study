# 整地方案 × 坵塊限建疊圖檢查設計（`check_grading_parcel_limits`）

> 背景對話：2026-10-05。repo 目前有兩套獨立的山坡地工具：坵塊法（`get_parcel_contour_layers`／`preview_parcel_slope`／`create_parcel_slope_map`，`domain/parcel-slope-analysis.md`）算原地形每個坵塊的平均坡度與法定限制；整地方案（`*_grading_*` 一批工具）讓使用者用樓板登記「整地後平台」。使用者要一個新工具把兩者接起來：把坵塊法算出的限建坵塊疊到整地方案上，自動檢查方案樓板有沒有壓到限建範圍，並在視圖上標出來。

## 1. 目標與動機

坵塊法與整地方案目前是兩條互不相交的資料流：坵塊法的結果只活在單次呼叫的回應 JSON 與視圖上的標註圖元裡，整地方案的樓板輪廓另外存在登記簿快照裡，沒有任何現成工具同時讀兩者。本設計定義新工具 `check_grading_parcel_limits`，在不碰舊有兩套工具的前提下，把兩邊現有的資料結構接起來做幾何重疊檢查。

## 2. 現況事實摘要（附檔案:行號）

### 2.1 坵塊法資料流

**坵塊格網怎麼定義**：
- 範圍（`minX/minY/maxX/maxY`）來自**等高線曲線的取樣點**，不是使用者直接指定——`ResolveBounds`（`MCP/Core/Commands/CommandExecutor.ParcelSlope.cs:330-354`）逐一取 DWG 等高線的取樣點（`CurveSamplePoints`，同檔 460-474）算 bounding box。
- 格網本體由 `ParcelSlopeMath.BuildGrid(minX, minY, maxX, maxY, cellSizeMeters, rotationDegrees)` 產生（`MCP/Core/Grading/ParcelSlopeMath.cs:117-183`），旋轉中心固定在**網格範圍中心**（非原點），`rotationDegrees` 預設 0。
- 座標系：DWG 等高線透過 `ImportInstance.get_Geometry(Options{View=...})` 讀取，`FlattenGeometry`（`ParcelSlope.cs:476-498`）會展開 `GeometryInstance` 的巢狀變換，所以拿到的曲線**已經是專案內部座標系**（跟樓板、地形同一個座標系），不是 DWG 檔案自己的座標。單位上是 Revit 內部英呎，`ParcelFeetToMeters`（`ParcelSlope.cs:28-31`，就是 `UnitUtils.ConvertFromInternalUnits(…, Meters)`）直接轉公尺，**沒有額外平移**——即公尺座標的原點＝專案內部原點，跟樓板/地形的座標系一致，疊圖不需要座標轉換。

**`preview_parcel_slope` / `create_parcel_slope_map` 回傳欄位**（`BuildParcelResponse`，`ParcelSlope.cs:580-637`）：
- 逐格：`label`、`col`、`row`、`centerX/Y`（公尺）、`intersectionCount`、`slopePercent`（四捨五入 2 位，**沒有上限截斷**）、`restriction`（enum 字串 `None`/`OpenSpaceOnly`/`ExcludedFromOpenSpace`）、`restrictionLabel`。
- 彙總：`summary`（max/min/average slope、三類計數）、`cellCount`、`contourCurveCount`、法條公式字串、`disclaimer`（§262 均質區禁止自動結論的提醒）。
- `create_parcel_slope_map` 多畫圖：每格 4 條 `DetailCurve`（**view-specific，只在平面視圖有效**）＋置中 `TextNote`（含編號/n/S%，受限格另加文字），用 `OverrideGraphicSettings` 上色（橘/紅，`ApplyParcelColor`，`ParcelSlope.cs:560-574`）——上色是**視圖覆寫**不是元素本身顏色。

**是否持久化：否。** 全文（`ParcelSlope.cs` 全檔已讀）沒有任何 `ExtensibleStorage`／共用參數寫入呼叫；`create_parcel_slope_map` 建立的 `DetailCurve`＋`TextNote` 只是圖元，坡度分級數字只存在於該次呼叫的回應 JSON 與圖面文字裡，日後任何命令都**讀不回**「哪些坵塊 >30%」這個結構化結果，只能用相同參數（`layerName`、`contourIntervalMeters`、`cellSizeMeters`、`rotationDegrees`、`viewId`）重新呼叫 `preview_parcel_slope`/`create_parcel_slope_map` 重算。重算成本：重新走一次 DWG 幾何讀取＋展平＋逐格四邊交點測試（上限 `MaxParcelCells=20000` 格，`ParcelSlope.cs:20`），無需開 Transaction（`PreviewParcelSlope` 全程唯讀）。

### 2.2 整地方案資料流

- **方案記錄怎麼存**：`GradingSchemeRecord`（`MCP/Core/Grading/GradingSchemeRecord.cs`）是純 POCO，序列化成 JSON 存在**設計地形（design Toposolid）的 Extensible Storage**，schema 名稱 `RevitMCP_GradingScheme`（`MCP/Core/Grading/RevitToposolidGradingAdapter.cs:380-381`）。讀寫入口：`RequireSchemeRecord`／`UpdateSchemeRecord`（`MCP/Core/Commands/CommandExecutor.GradingView.cs:17-50`），底層 `TryReadSchemeRecord`/`ReadSchemeRecords`/`WriteSchemeRecord`（`RevitToposolidGradingAdapter.cs:302, 332, 247`）。
- **樓板 ID 清單**：`record["FloorIds"]`（`JArray`），目前 schema v6（`GradingSchemeRecord.cs:13, 24`）存的是使用者當初選取的樓板 ID（本分支另有一份待拍板規格 `2026-07-10-grading-floor-per-scheme-design.md` 提議改成「樓板設計副本」模式，**目前尚未實作**，本設計沿用現況：`FloorIds` 就是要比對的樓板）。
- **樓板平面輪廓怎麼拿**：既有 helper `IToposolidGradingAdapter.ExtractBottomFootprints(IReadOnlyList<Floor> floors)`（介面宣告 `RevitToposolidGradingAdapter.cs:15`，實作 `ExtractBottomFootprint` 單數版本在 1897-1954）回傳 `IReadOnlyList<FloorFootprint>`，`FloorFootprint.OuterLoop` 是 `IReadOnlyList<Point2D>`（`Point2D` 定義在 `MCP/Core/Grading/GradingModels.cs:8-13`，純 X/Y double）。**單位是 Revit 內部英呎**（未經公尺轉換，與 `CommandExecutor.GradingExtentLines.cs:81-92` 直接拿 `footprint.OuterLoop` 的點去跟英呎量測的 margin 比較一致）。這個 helper 已經被 `CreateGradingExtentLines`（`GradingExtentLines.cs:47`）與 `CollectDaylightElevationSamples`（`GradingView.cs:1170`）重複使用，是現成、可直接沿用的樓板輪廓來源，**不需要新寫**。
- **方案視圖**：一律是 **`View3D`**（鎖定方位、複製使用者當前 3D 視圖表現法），由 `CreateGradingSchemeView`（`GradingView.cs:75-143`）建立，`ResolveSchemeView`（`GradingView.cs:53-68`）依 `viewId` 參數或 `record.SchemeViewId` 找回。**不是平面視圖**——這點對疊圖呈現有直接影響，見下。
- **既有繪圖慣例可沿用**：
  - `CreateGradingExtentLines`（`GradingExtentLines.cs`）：在原地形/設計地形之間取差值場，等值線（marching squares）轉成封閉環，**貼地形**（射線求交 `IntersectTerrainTopZ`＋150mm 淨空，`GradingExtentLines.cs:168-195`）畫成 `ModelCurve`（3D，非 `DetailCurve`，因為目標是 3D 視圖），每方案一個專屬 `GraphicsStyle` 子分類控制顏色/線寬＋分視圖可見性（`EnsureExtentLineStyle`／`ApplyExtentLineCategoryVisibility`，272-338），重跑前先用登記簿記的 `ExtentLineIds` 冪等清除舊圖元（`DeletePreviousExtentLines`，239-270）。
  - `AnnotateGradingScheme`（`GradingView.cs:840-1110`）：在方案視圖外側欄位放帶引線的 `TextNote` 標籤（`TryPlaceOutboardLabel`）與 `SpotDimension`（`TryPlaceSpotElevation`），重跑前先清掉上次標籤（868-881）。
  - `CreateCutfillHeatmap`/坡度熱區：用 `SpatialFieldManager`（AVF）在設計地形頂面著色，純視覺化不產生數字權威值。

**重要落差**：坵塊法原生輸出（`DetailCurve`＋`TextNote`）假設目標是**平面視圖**；整地方案視圖是**View3D**。新工具若要把限建坵塊疊到方案視圖上，不能照搬 `CreateParcelSlopeMap` 的畫法，必須仿 `CreateGradingExtentLines` 改用貼地形的 `ModelCurve`（見下方設計提案）。

### 2.3 新增工具要動的檔案（以 `create_parcel_slope_map` 為例盤點）

| 層 | 檔案 | 這次要做的事 |
|---|---|---|
| TS 工具定義 | `MCP-Server/src/tools/grading-tools.ts:418-430`（`create_parcel_slope_map` 條目） | 在陣列裡新增一個工具物件；**不需要**動 `revit-tools.ts` 或 `index.ts`——`executeRevitTool`（`MCP-Server/src/tools/revit-tools.ts:35-56`）是 toolName→commandName 的通用轉送，沒有逐工具特判（除 `query_elements_with_filter` 改名與 `export_walkthrough_frames` 的分批特例）；`gradingTools` 陣列本身已在 `revit-tools.ts:13, 27-29` 被整批併入 `full/architect/structural` profile。 |
| C# dispatcher | `MCP/Core/CommandExecutor.cs:492-503`（`case "create_parcel_slope_map"` 等） | 新增一個 `case "check_grading_parcel_limits": result = CheckGradingParcelLimits(parameters); break;`。 |
| 命令實作 | `MCP/Core/Commands/CommandExecutor.ParcelSlope.cs`（坵塊）／`CommandExecutor.GradingExtentLines.cs`（整地） | 新工具橫跨兩個既有命令檔的職責，建議另開一個新檔（見下）而不是塞進既有檔。 |
| 純數學 | `MCP/Core/Grading/ParcelSlopeMath.cs`（坵塊）、`RevitToposolidGradingAdapter.cs`（整地） | 新增一個獨立的純數學檔案（見下），零 Revit 相依。 |
| 測試專案引用 | `MCP.Tests/RevitMCP.Tests.csproj:12-33` | 這是 **Link 既有原始檔**（`<Compile Include="..\MCP\Core\Grading\ParcelSlopeMath.cs" Link="..." Condition="Exists(...)" />`），**不是 project reference**。新增的純數學檔要在這裡補一行 `<Compile Include>`，否則測試專案看不到它。 |
| 測試 | `MCP.Tests/Grading/ParcelSlopeMathTests.cs`（坵塊）同目錄其他 `*Tests.cs` | 新增 `MCP.Tests/Grading/ParcelLimitCheckTests.cs`，NUnit，`[TestFixture]`。 |
| Ribbon | `MCP/Commands/GradingRibbonCommands.cs` | **不需要**——已查證該檔沒有任何坵塊相關按鈕（grep 無結果），坵塊三個工具都是 MCP-only，沒有 Ribbon UI；新工具沿用此慣例。 |
| CommandModels | `MCP/Models/CommandModels.cs` | **不需要**——該檔只有通用的 `RevitCommandRequest`/`Response`，坵塊與整地現有工具都直接用 `JObject parameters` 手動解析，沒有逐工具 DTO。 |
| 計數同步 | `CLAUDE.md`「Current Source-of-Truth Counts」表、`README.md`、`README.en.md`、`docs/DOCUMENT_AUDIENCE_INVENTORY.md` | 新增一個 runtime tool 後，`Runtime MCP tools` 計數要從 103 改 104，依 `CLAUDE.md` 自己的規則跑 `scripts/verify-qaqc.ps1 -SkipBuild -SkipDeploy`。 |

## 3. 設計提案

### 3.1 純數學類別：`ParcelLimitCheck`（新檔 `MCP/Core/Grading/ParcelLimitCheck.cs`）

零 Revit 相依，輸入「樓板輪廓多邊形（公尺）」＋「坵塊清單（4 角點、分級、坡度）」，輸出「樓板壓到哪些限建坵塊、各自重疊面積」。門檻值**不自建**，全部引用 `ParcelSlopeMath.SteepThresholdPercent`（30）／`ParcelSlopeMath.NoOpenSpaceThresholdPercent`（55）／`ParcelSlopeMath.Classify`（既有，`ParcelSlopeMath.cs:22-26, 74-89`）。

```csharp
namespace RevitMCP.Core.Grading
{
    /// <summary>一格坵塊供疊圖檢查用的摘要（由呼叫端從 ParcelComputation 轉出）。</summary>
    public sealed class ParcelCellSummary
    {
        public string Label;
        public IReadOnlyList<Point2D> CornersMeters; // 4 角，沿用 ParcelSlopeMath.BuildGrid 的順序
        public double SlopePercent;
        public ParcelRestriction Restriction; // 複用既有 enum，不重複定義
    }

    public sealed class ParcelCellOverlap
    {
        public string CellLabel;
        public ParcelRestriction Restriction;
        public double SlopePercent;
        public double OverlapAreaSquareMeters;
        public double OverlapFractionOfFloor; // 0~1
    }

    public sealed class ParcelFloorLimitResult
    {
        public long FloorId;
        public double FloorAreaSquareMeters;
        public double OpenSpaceOnlyOverlapAreaSquareMeters;   // (30%,55%] 重疊面積
        public double ExcludedOverlapAreaSquareMeters;        // >55% 重疊面積
        public IReadOnlyList<ParcelCellOverlap> MatchedCells;  // Restriction != None 者
    }

    public static class ParcelLimitCheck
    {
        /// <summary>單一樓板 vs 坵塊清單的重疊檢查。樓板輪廓與坵塊角點須同單位（公尺）、同座標系。</summary>
        public static ParcelFloorLimitResult CheckFloor(
            long floorId,
            IReadOnlyList<Point2D> floorOuterLoopMeters,
            IReadOnlyList<ParcelCellSummary> cells,
            double areaToleranceSquareMeters = 1e-6);

        /// <summary>Sutherland-Hodgman：用凸多邊形 clipPolygon（坵塊，必為凸）裁切 subjectPolygon（樓板，可凹）。</summary>
        internal static IReadOnlyList<Point2D> Clip(
            IReadOnlyList<Point2D> subjectPolygon, IReadOnlyList<Point2D> clipPolygon);
    }
}
```

演算法：
1. 樓板總面積：`Polygon2D.Area(floorOuterLoopMeters)`（既有，`MCP/Core/Grading/Polygon2D.cs:47`，直接複用，不重寫 shoelace）。
2. 對每格坵塊：先用 bounding-box 粗篩（樓板與格子的 X/Y 範圍不重疊即跳過，效能優化，非必要但建議），再呼叫 `Clip(floorLoop, cellCorners)`（新寫，Sutherland-Hodgman——坵塊必為正方形＝凸多邊形，可當 clip polygon；樓板允許凹多邊形，只能當 subject，這正是 Sutherland-Hodgman 的使用前提）。
3. 裁切結果多邊形面積＝`Polygon2D.Area(clipped)`（複用）；若格子 `Restriction == None` 跳過累計（只關心限建格）。
4. 按 `Restriction` 分桶累加 `OpenSpaceOnlyOverlapAreaSquareMeters` / `ExcludedOverlapAreaSquareMeters`，並收集逐格 `ParcelCellOverlap`（area > tolerance 才收錄，見 §5 未決問題 4）。
5. **不**在這個類別裡下任何「違反」/「不得開發建築」結論——只回面積與格子清單，判斷留給呼叫端的 disclaimer 與使用者。

### 3.2 命令實作：新檔 `MCP/Core/Commands/CommandExecutor.GradingParcelLimitCheck.cs`

`#if REVIT2024_OR_GREATER` 包住（理由同 `GradingExtentLines.cs`：依賴 `Toposolid`／方案登記簿）。`CheckGradingParcelLimits(JObject parameters)` 流程：

1. `RequireSchemeRecord(doc, parameters)` → 取得 `design`（Toposolid）與 `record`（含 `FloorIds`）。
2. 坵塊參數照抄坵塊工具的參數名（`layerName`、`contourIntervalMeters`、`cellSizeMeters`、`rotationDegrees`）＋一個**另外的** `parcelViewId`（見下）。呼叫既有私有方法 `ReadParcelInput` + `ComputeParcelSlope`（兩者都在同一個 `partial class CommandExecutor` 裡，**直接可呼叫，不必重寫**坵塊幾何讀取邏輯）。
3. 把 `ParcelComputation.Cells`（私有 `ParcelCellResult`）轉成公開的 `ParcelCellSummary` 清單（`CornersMeters` 直接用 `cell.Corners`，已是公尺）。
4. `adapter.ValidateFloors(doc, floorIds)` → `adapter.ExtractBottomFootprints(floors)`；每個 `FloorFootprint.OuterLoop`（英呎）轉公尺（沿用 `ParcelFeetToMeters` 風格的逐點轉換）。
5. 對每個樓板呼叫 `ParcelLimitCheck.CheckFloor(...)`，彙總回應 JSON。
6. `drawOverlay=true`（預設）時，開一個 Transaction：對有重疊的坵塊，取其 4 角點用 `RevitToposolidGradingAdapter.IntersectTerrainTopZ`（既有，`GradingExtentLines.cs` 已示範用法）貼到設計地形頂面＋淨空，畫 `ModelCurve` 環（**不是** `DetailCurve`，因為目標是 `View3D`），套一個新的「坵塊限建疊圖-{designId}」`GraphicsStyle` 子分類（橘/紅分級著色，仿 `EnsureExtentLineStyle`），重跑前先清掉上次疊圖（登記簿記新欄位 `ParcelLimitOverlayIds`，仿 `ExtentLineIds` 的冪等清除模式）。另外對每個 touched 樓板放一個 outboard `TextNote`（仿 `TryPlaceOutboardLabel`）寫「限空地 X.XX m²／不得計入法定空地 Y.YY m²」。

**兩個 view 的角色不能混淆**：`parcelViewId` 是坵塊法讀 DWG 等高線用的**平面視圖**（沿用 `ResolveParcelView` 邏輯：顯式指定或目前作用中平面視圖）；`viewId`／`record.SchemeViewId` 是**要畫疊圖的 3D 方案視圖**。兩者不必是同一個視圖——坵塊角點座標已經是專案座標系的公尺值，跟讀取時用哪個視圖無關（視圖只決定讀哪個 `ImportInstance` 的幾何，不影響座標轉換）。

### 3.3 門檻呈現

直接複用 `ParcelSlopeMath.SteepThresholdPercent`（30）與 `NoOpenSpaceThresholdPercent`（55），不另寫數字。回應 JSON 加一個 `thresholds` 區塊原樣輸出這兩個常數，供前端/報表顯示，確保往後法條若有修正只需改一處。

輸出用兩條獨立數字呈現，不合併成單一「違規與否」布林（避免看起來像終局結論）：
- `openSpaceOnlyOverlapAreaSquareMeters`／`Fraction`：對應 §262 第2項中段（30~55%，限空地不得配置建築物），也**構成** §262 第1項第1款「坡度陡峭」情形（但書未成立時不得開發建築——本工具不判但書）。
- `excludedOverlapAreaSquareMeters`／`Fraction`：對應 §262 第2項後段（>55%，不得計入法定空地面積）。

`disclaimer` 欄位原樣沿用坵塊法既有四條（`ParcelSlope.cs:627-635`）＋新增一條：「本結果僅標示樓板輪廓與限建坵塊的幾何重疊，不等於『不得開發建築』或『不合格』的法律結論；§262 但書（均質區劃分、區內最高低點坡度<15%、無顯著獨立山頭或跨越主嶺線）需人工判讀地形，本工具不自動判定。」

## 4. 持久化問題：結論與兩方案比較

**結論（§2.1 已證）：坵塊法結果目前不持久化。** 新工具有兩個選項：

| | 方案 A：本工具內重算（建議） | 方案 B：擴充 `create_parcel_slope_map` 存快照 |
|---|---|---|
| 做法 | 直接呼叫既有私有方法 `ReadParcelInput`/`ComputeParcelSlope` 重新跑一次坵塊法 | 新增 ExtensibleStorage schema（比照 `RevitMCP_GradingScheme`），`create_parcel_slope_map` 多寫一份逐格結果；新工具改讀快照 |
| 改動量 | 小：只動新檔＋一個 switch case | 大：`ParcelSlope.cs`、`RevitToposolidGradingAdapter.cs`-like 新讀寫層、schema 版本管理、既有兩個坵塊工具的回應都要補「已存快照」欄位 |
| 正確性風險 | 低：永遠讀當下的 DWG，不會過期 | 高：DWG 若事後修改/移動/換圖層，快照會**靜默**與現況不符——這正是 `parcel-slope-analysis.md` §6 一直在警告的「首曲線/計曲線誤判」「比例尺無法驗證」風險的疊加版，多一層「使用者不知道在看舊資料」的風險 |
| 效能 | 每次呼叫重算一次（上限 20000 格的交點測試），不含 Transaction（唯讀部分） | 僅首次計算，之後讀檔案 O(1) |
| 與現有慣例一致性 | 高：坵塊法本來就是設計成「參數不變、結果可重現」的無狀態計算 | 低：坵塊法目前刻意不持久化任何法定判定（§4「不可自動化」同一精神），新增快照等於幫這套刻意唯讀的機制開一個會過期的副本 |

**建議採方案 A**：改動小、沒有新的過期風險，且完全對齊坵塊法「每次都從權威 DWG 重算、不信任舊快取」的既有設計哲學。效能代價可接受（上限已有 `MaxParcelCells` 把關）。方案 B 僅在未來有「坵塊法計算很貴、使用者頻繁重跑疊圖」的實測痛點時才考慮。

## 5. 視覺呈現提案

目標視圖：整地方案的 `View3D`（`ResolveSchemeView`），**不是**坵塊法原生的平面視圖。沿用 `CreateGradingExtentLines` 的「貼地形 `ModelCurve`＋專屬 `GraphicsStyle` 子分類＋分視圖可見性＋登記簿記 ID 冪等清除」整套手法（`GradingExtentLines.cs:198-338`），理由是這條路徑已經解決了「3D 視圖裡怎麼畫一條貼合地形、不穿地、各方案不互相污染」的所有已知坑（射線取樣、淨空、簡化容差、分視圖可見性）。文字標籤沿用 `AnnotateGradingScheme` 的 outboard label＋leader 手法（`GradingView.cs:1246+`，`TryPlaceOutboardLabel`）。

不建議用 AVF 熱區（`CreateCutfillHeatmap`/`CreateSlopeHeatmap` 的手法）畫限建範圍，因為 AVF 著色的是**連續色帶**（挖填深度、六級坡坡度），而限建坵塊是**離散的法定分級**（None/OpenSpaceOnly/ExcludedFromOpenSpace）配合清晰的格線邊界更貼近坵塊圖本身的呈現方式（坵塊法原生輸出就是網格線＋文字，不是色帶）。

## 6. 要新增／修改的檔案清單

新增：
- `MCP/Core/Grading/ParcelLimitCheck.cs`
- `MCP/Core/Commands/CommandExecutor.GradingParcelLimitCheck.cs`
- `MCP.Tests/Grading/ParcelLimitCheckTests.cs`

修改：
- `MCP/Core/CommandExecutor.cs`（新增 switch case，約 492-503 行附近）
- `MCP-Server/src/tools/grading-tools.ts`（新增工具定義）
- `MCP.Tests/RevitMCP.Tests.csproj`（新增 `<Compile Include>` 連結新的 `ParcelLimitCheck.cs`）
- `MCP/Core/Grading/GradingSchemeRecord.cs`（若採疊圖冪等清除，新增 `ParcelLimitOverlayIds` 欄位，`SchemaVersion` 6→7）
- `CLAUDE.md`／`README.md`／`README.en.md`／`docs/DOCUMENT_AUDIENCE_INVENTORY.md`（Runtime MCP tools 計數 103→104）
- `domain/parcel-slope-analysis.md`（§8 相關，加一條指向整地方案疊圖工具的交叉參照；是否需要更多視主線裁決，見未決問題 7）

## 7. 測試計畫（NUnit，`MCP.Tests/Grading/ParcelLimitCheckTests.cs`）

1. 樓板完全落在單一 `None` 坵塊內 → 無任何 `MatchedCells`，兩個限建面積皆 0。
2. 樓板完全落在單一 `OpenSpaceOnly`（30~55%）坵塊內 → `OpenSpaceOnlyOverlapAreaSquareMeters` ≈ 樓板全部面積，`ExcludedOverlapAreaSquareMeters` = 0。
3. 樓板跨兩格，一格 `None` 一格 `ExcludedFromOpenSpace`（>55%）→ 重疊面積正確分配，兩格面積之和＝樓板總面積（容差內）。
4. 樓板與坵塊邊界剛好共線相切（零面積重疊）→ 依未決問題 4 的裁決行為斷言（收錄 area=0 或完全排除，兩者選一並鎖死）。
5. 樓板為 L 形凹多邊形跨 3 格 → 驗證 `Clip` 對凹多邊形 subject 的正確性（不可用凸包近似糊弄過去）。
6. 坵塊網格 `rotationDegrees != 0` 時，重疊面積仍正確（驗證角點已經是旋轉後座標，不需要疊圖端再轉一次）。
7. 門檻邊界：構造 `SlopePercent` 恰為 30.0 與 55.0 的格子，斷言呼叫的是 `ParcelSlopeMath.Classify` 而非另一份重複的門檻判斷（例如直接斷言兩個常數值等於 `ParcelSlopeMath.SteepThresholdPercent`/`NoOpenSpaceThresholdPercent`）。
8. 面積防重複計算：樓板跨 4 格共用邊，總重疊面積不得超過樓板總面積＋容差（防 clip 演算法在共享邊上雙計）。
9. 空輸入：坵塊清單為空，或全部 `Restriction == None` → 回傳空 `MatchedCells`、不丟例外。
10.（可選）大樓板跨數百格的規模探針，只驗證不逾時／不堆疊溢位，非嚴格效能測試。

## 8. 風險與未決問題（需要使用者或主線決定）

1. **持久化方式**：採本設計建議的方案 A（本工具內重算，不新增 ExtensibleStorage schema）嗎？（是／否——若否，需另定案方案 B 的 schema 版本與過期判定規則）
2. **工具拆分**：單一工具＋`drawOverlay` 布林旗標（本設計預設做法），還是比照坵塊法拆成 `preview_grading_parcel_limits`（只算不畫）＋`check_grading_parcel_limits`（算＋畫）兩隻？
3. **相切/共線零面積重疊**：收錄進 `MatchedCells`（area=0，供使用者知道「貼著邊但沒真的壓到」）還是完全排除不回傳？
4. **疊圖冪等**：是否要求登記簿新增 `ParcelLimitOverlayIds` 欄位＋`SchemaVersion` 升版來精確清除上次疊圖（比照 `ExtentLineIds`），還是接受目前 `DeletePreviousExtentLines` 式的「舊版無記錄者用幾何猜測清除」的過渡做法？
5. **`FloorIds` 現況 vs 未來樓板設計副本**：本設計沿用 `record["FloorIds"]` 現況（原始樓板 ID）。若 `2026-07-10-grading-floor-per-scheme-design.md` 的「樓板設計副本」提案之後落地，本工具要比對的樓板 ID 來源需要同步改成 `DesignFloorIds`——這次是否要預留參數相容性，還是等該提案定案後再補？
6. **視覺呈現二選一**：ModelCurve 貼地形線框疊圖（本設計建議，理由見 §5）還是 AVF 色塊？或兩者都要（線框標邊界＋色塊標強度）？
7. **domain 文件要不要擴充**：只在 `domain/parcel-slope-analysis.md` §8 加一條交叉參照（本設計做法），還是需要在 `CLAUDE.md` 的 Domain Knowledge 觸發關鍵字表新增一列、甚至另開一個 domain 檔描述「坵塊×整地疊圖」SOP？
8. **效能上限**：`check_grading_parcel_limits` 重算坵塊法時沿用既有 `MaxParcelCells=20000` 上限即可，還是因為多了逐樓板×逐格 clip 的 O(floors×cells) 複雜度，需要另設更低的格數警示門檻？

## 驗收條件自查

- 設計稿涵蓋：背景目標、現況事實（附檔案:行號）、設計提案（純數學類別、持久化兩方案比較、視覺呈現、檔案清單、測試計畫、未決問題）。
- 持久化結論：明確「否」，證據見 §2.1（`ParcelSlope.cs` 全文無 ExtensibleStorage/共用參數寫入）。
- 門檻值全部引用 `ParcelSlopeMath.SteepThresholdPercent`/`NoOpenSpaceThresholdPercent`/`Classify`，未另寫數字。
- 未決問題共 8 條，均為是非題或選擇題。
