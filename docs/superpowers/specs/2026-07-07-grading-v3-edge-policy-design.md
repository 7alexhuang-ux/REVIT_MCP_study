# 整地 V3：板＋邊界政策模型（S1 板高程/斜板＋S2 逐邊坡率）設計

> 2026-07-07 與使用者兩輪腦力激盪拍板。本設計是「基地整地設計系統」的第一批（S1＋S2）；
> S3（板間銜接/高差標註）與 S4（斜板自動建議）為下一批，僅列綱要。
> 前情：V2/V2.1 見 `2026-07-06-grading-workflow-v2-design.md`。

## 統一概念模型（拍板）

**基地＝一群「板」（平台/坡道，皆為控制樓板）＋原地形；所有設計決策發生在「邊界」上。**

| 邊界類型 | 銜接政策 | 歸屬 |
|---|---|---|
| 板 ↔ 原地形 | 放坡 1:n，**可逐邊不同**（預設 1:2、臨路無障礙邊 1:12） | S2（本批） |
| 板 ↔ 板 | 連續／台階（斷開＋標註） | S3（下一批） |

三層分工：**決策記錄**（邊政策，存 ES＋登記簿快照）→ **幾何執行**（放坡照政策）→ **文件化**（標註/檢核）。

## 使用者拍板決策（2026-07-07）

| 事項 | 決議 |
|---|---|
| 需求本質 | 甲：臨路側「地面」以 1:12 緩放上來（土坡），非斜板 RAMP 結構（斜板另屬 S1 內建能力） |
| 邊的指定 | **做法 1：Revit 內直接點邊**（原生 hover 高亮）；對話框列「已指定清單」供檢視/刪改 |
| 道路方位 | 不需指定——點哪條邊＝指定哪條邊；放坡方向天生垂直於邊往外，直到碰原地形收坡 |
| 逐邊坡率 UX | 選項 A（每板一坡率）被使用者正確否決：細長坡道板兩短邊會被迫同坡率。**逐邊（B）才對** |
| 斜板方向指定 | 點一條「低側邊」＋坡率 1:n；低側邊維持目前板高程、其餘依坡率抬升 |
| 自動判斷（M2） | 降級為「建議值」，使用者確認才套用（S4） |
| 交付節奏 | spec→TDD→部署→**AI 以 MCP 連線自驗**→使用者總驗一次 |

## S2 逐邊坡率——技術設計

### 資料模型
- **邊政策**存於樓板的 Extensible Storage（schema `RevitMCP_EdgeSlopePolicies`：`SchemaVersion`＋`PoliciesJson`）。
  政策＝邊的**幾何指紋**＋坡率：`{MidX, MidY, DirX, DirY, Length（內部英呎）, SlopeRatio("1:12")}`。
  政策屬於「板」（板的設計屬性），方案執行時消費；方案登記簿**快照**當時生效的政策（SchemaVersion 3→4，新欄 `EdgePolicies`）。
- 指紋匹配（純函式）：中點距離 ≤ min(邊長/2, 1m)、方向平行 |dot|≥cos10°、長度差 ≤20%。配不到→警告「請重新指定」並忽略該政策。

### 幾何規則（純函式，NUnit 覆蓋）
`RunPerRise` 從單值變成**逐邊值**，三個消費點全部改吃「該點所屬邊的坡率」：

1. `Polygon2D.DensifiedBoundaryWithCornerArcs` 新增帶 `edgeRates[]` 的多載，回傳 `(BasePoint, OutwardDir, RunPerRise)`：
   - 直邊細分點 → 該邊坡率；
   - **凸角圓弧扇** → 沿扇形自入邊坡率**線性插值**到出邊坡率（扇形正是混坡的自然位置，帶寬沿轉角螺旋漸變）；
   - 凹角/共線單錨點 → 兩鄰邊坡率平均。
2. `Polygon2D.NearestBoundaryPointDetailed`：回傳最近點＋所屬邊 index＋邊上參數 t（供距離場查坡率）。
3. `BoundaryRateField`（新純類）：`RateAt(sample)`——最近邊坡率，**距角點沿邊弧長 < 混坡長（預設 2m，可調）且兩鄰邊坡率不同**時，向「角點平均值」線性混坡；與 1 的扇形插值在角點端點一致。
4. adapter `BuildTransitionRings`／帶頂點分類改吃逐錨點/逐取樣點坡率；`maxExtension` 上限檢查逐點用各自坡率。

### UX（Ribbon「邊坡率」鈕＋MCP 工具）
- Ribbon：選樓板（或點鈕後提示）→ `PickObject(Edge)` 迴圈點邊（ESC 結束）→ 每點一條跳小框填坡率（可編輯下拉 1:10/1:12/1:15/1:20＋自由輸入）→ 結束顯示摘要（邊① 長42m＝1:12…）→ 存 ES。可「清除本板全部」。
- MCP 工具 `set_floor_edge_slope`：`{floorId, x, y（公尺，內部原點座標）, slopeRatio, clear?}`——以 (x,y) 找該板最近邊界邊；`clear=true` 清空該板政策。非互動、AI 可驅動。
- 政策僅 `slope_transition` 模式生效；其他模式帶政策執行→警告提示。

## S1 板高程/斜板——技術設計

- **平板**：設 `FLOOR_HEIGHTABOVELEVEL_PARAM`（自標高偏移，公尺輸入）。
- **斜板**：點「低側邊」＋坡率 1:n → 純函式 `SlopedPlaneMath.VertexRises(vertices, edgeStart, edgeEnd, runPerRise)`＝各頂點距低側邊線的垂距/n（朝板內側為正、負值取 0）→ `SlabShapeEditor` 逐頂點寫入（沿用兩段式校準精神：先讀基準再寫差值）。低側邊維持原高程。
- **還原平板**：`SlabShapeEditor.ResetSlabShape()`。
- Ribbon「板高程」鈕：對話框列選取板（編號｜目前標高｜動作：維持/改偏移/改斜板(坡率填在列上)/還原平板）→ 確定後對「改斜板」列逐板提示點低側邊 → 單一交易套用。**先收集意圖、關閉對話框後才點選**（避免模態/PickObject 衝突）。
- MCP 工具 `set_floor_elevations`：`{floors:[{floorId, offsetMeters? | slope:{x,y,slopeRatio} | reset:true}]}`。
- 風險：SlabShape 對弧形邊會分段；斜板放坡時 `FloorFootprint.BottomElevationAt` 已支援變高程邊界（現有能力），S2 自動銜接。

## S3／S4 綱要（下一批）

- **S3**：容差判板相鄰；高差 ≤2cm 視為連續、超過列報告請使用者逐條決定「台階（標註兩側高程＋Δ）或調標高」；先報告後執行，不自動改幾何。
- **S4**：斜板建議（兩端高差→建議方向/坡率，確認才套用）。

## 驗收

1. NUnit：指紋匹配邊界、rated 密錨點（扇形插值/凹角平均/細分）、`BoundaryRateField` 混坡、`NearestBoundaryPointDetailed`、`SlopedPlaneMath`。
2. 建置 R24/R23 0 錯、npm 全綠、QA/QC 既有債之外無新失敗。
3. 實機（AI 以 MCP 自驗，TOPO_example）：
   - S1：改一板偏移＋一板轉斜板（1:12、點低側邊）→ 讀回高程驗證；還原平板。
   - S2：坡道板臨路長邊設 1:12、其餘預設 1:2 → 建方案 → 檢查登記簿快照含政策、警告無 unmatched；轉角處帶寬漸變（無突變裂縫）；500mm 包絡閘門沿用。
   - 使用者最後總驗一次。

## 開放項

- 混坡長度預設 2m，實機看效果再調。
- 「指定落點」（坡腳精準接道路特定高程）不在本批。
- S3 前置：多板相鄰時放坡帶實際行為需實機查證（現有 `Contains` 跳點機制的表現）。
