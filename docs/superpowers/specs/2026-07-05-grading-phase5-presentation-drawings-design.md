# 整地方案業主圖說成套輸出設計（Phase 5）

## 目標

把「整地方案」從目前的「數字比較表 + 單張截圖」提升為**一套能對業主完整講解的 Revit 正式圖說**：對登記簿中的每個方案，自動產出挖填熱區、坡度（六級坡）熱區、標高標註、整地範圍線、邊界剖面等圖面，排上帶圖框的 A1 圖紙，並批次匯出 PDF。全流程以一個協調命令 `create_grading_presentation` 一鍵完成。

本 Phase 疊在既有整地模組（Phase 1–4，`domain` 尚未建檔）之上，沿用「一方案＝一片設計地形＋一鎖定 3D 視圖＋登記簿一筆」的核心結構，不改動整地演算法本體，只新增「表現／出圖」層與一項既有缺陷修正。

## 前置決策（2026-07-05 定案）

- **成品形式**：Revit 正式圖紙成套（帶圖框，可匯 PDF）。
- **收錄模式**：全 3 種——footprint（標「理想值：垂直擋土、不可施工，僅供對照」）、offset_transition、slope_transition。⇒ offset 修正為前置必做。
- **自動化深度**：甲案「全自動成套」——連視圖排上圖紙與 PDF 匯出都自動化。
- **坡度分類**：採**水土保持技術規範六級坡**（見下方權威表），非自訂級距。

## 權威分類：水土保持技術規範 六級坡

坡度熱區工具的著色級距一律以此為準，不得以模型一般知識替代：

| 級別 | 坡度範圍 | 屬性 |
|---|---|---|
| 一級坡 | 5% 以下 | |
| 二級坡 | 超過 5% 至 15% | |
| 三級坡 | 超過 15% 至 30% | |
| 四級坡 | 超過 30% 至 40% | |
| 五級坡 | 超過 40% 至 55% | 宜林地 |
| 六級坡 | 超過 55% | 加強保育地 |

本表同步落成 domain 檔（`domain/grading-slope-classification.md`），坡度熱區工具與輸出圖例均引用之。

## 架構總覽

```text
create_grading_presentation（協調命令，5D）
  對登記簿每個方案：
    1. 方案視圖   create_grading_scheme_view      （既有）
    2. 挖填熱區   create_cutfill_heatmap          （既有）
    3. 坡度熱區   create_slope_heatmap            （新，5B①）
    4. 標高標註   annotate_grading_scheme         （既有）
    5. 範圍線     create_grading_extent_lines     （新，5B②）
    6. 邊界剖面   create_section_view             （既有，組合）
    7. 建圖紙     create_sheets                   （既有）
    8. 排視圖     place_views_on_sheet            （新，5C①）
    9. 匯 PDF     export_sheets_pdf               （新，5C②）
  另建一張跨方案比較圖紙
```

整地演算法（`RevitToposolidGradingAdapter`）僅在 5A 做缺陷修正，其餘皆為新增的表現層命令，彼此以既有 `RevitCommandRequest/Response` 介面串接。

## 技術棧

C#、Revit API（AVF `SpatialFieldManager`、`Viewport.Create`、`PDFExportOptions`、`ModelCurve`/`DetailCurve`）、Extensible Storage（讀登記簿）、NUnit、TypeScript、node:test。

## 全域約束

- 讀取／視覺化類命令不開交易或以最小交易完成；不改動原地形與既有設計地形幾何。
- 熱區與坡度僅為**視覺化定位**；土方量、坡度統計的權威值以登記簿與工具回應數字為準。
- 新增工具沿用 snake_case 命名、既有 dispatcher case 模式、`#if REVIT2024_OR_GREATER` 保護（AVF/整地相依 2024+）。
- **實機驗證強制化**：本次教訓為 offset 缺陷「單元測試綠、實機才爆」。每個新工具與修正都必須在 Revit 2024 + TOPO_example 實機通過後才算完成；純邏輯另走 NUnit。
- 新增 4 個 MCP 工具 → 計數表（CLAUDE.md／README／README.en／DOCUMENT_AUDIENCE_INVENTORY）與 `verify-qaqc.ps1` 同步。

---

## 5A. offset 放坡帶缺陷修正（前置）

### 現況與根因（2026-07-05 實機定位）

- crash（`Null argument: solid`）已於 2026-07-05 修正並部署：`BuildTransitionRings` 重用了兩次 `doc.Regenerate()` 之前的 `solids` 快照，改為重生後重新 `CollectSolids(design)`。slope 模式實機成功、offset 越過 crash。
- 殘留缺陷：offset 模式在「銜接帶包絡驗收」被擋（平板 7512796 周邊超出包絡 0.12–0.17 m，放愈長愈糟）。根因為**放坡帶每個邊界點只播一個圈點**，稀疏圈點之間的三角網過衝包絡。

### 修法（2026-07-05 精化：凸角圓弧）

- **直邊細分**：建圈點前把樓板邊界直邊依最大弦長（預設 1 m）細分，圈點沿邊外法線外推。
- **凸角圓弧**：在**凸角**補一串圓弧控制點——以放坡距離為半徑、繞角點掃過（自入邊外法線至出邊外法線），角度步距 = 最大弦長 ÷ 該處放坡距離（半徑越大點越密）。凹角距離場天然收斂，不加點。
- **依據**：銜接帶本為**距離場**設計（目標高程＝到最近邊界距離），真實坡腳在凸角本就是圓弧；目前每角只放一個點嚴重欠取樣，是 offset 過衝包絡的主因。補圓弧一舉兩得：(1) 消除角落三角網過衝，offset 通過；(2) 放坡外緣渲染為圓角，與距離場一致（同 Topo Align、更正確）。
- 細分點、角弧點與圈點一併加入並刻摺線；slope 模式同樣受惠。
- 沿用既有雙閘門驗收：投影內 2 mm 表面抽樣、銜接帶 500 mm 包絡；修正後 offset 5 m 與 10 m 實機須通過。

### 產出介面

- `BuildTransitionRings` 內部改為「先細分邊界→再播圈點」；不改對外命令參數。
- 純邏輯（邊界細分、最大弦長）以 NUnit 覆蓋。

---

## 5B①. 坡度熱區工具 `create_slope_heatmap`

- **行為**：AVF `SpatialFieldManager` 在方案設計地形頂面著色，值＝**表面坡度（%）**，以網格取樣算局部梯度（`Δz/Δ水平`）。
- **著色**：六級坡固定級距（見權威表），每級一色；圖例標級別、範圍與屬性（宜林地／加強保育地）。
- **參數**：`designToposolidId`（必填）、`viewId`（選，預設方案記錄 `SchemeViewId`）、`sampleStepMeters`（預設 2）。
- **定位**：純視覺化；不寫回土方或坡度統計權威值。方法引用 `domain/grading-slope-classification.md`。

## 5B②. 整地範圍線工具 `create_grading_extent_lines`

- **行為**：以方案的樓板投影外圈 + 放坡外緣（登記簿已存幾何或由範圍幾何重建），在方案視圖建**模型線環**（`ModelCurve`）。
- **兩種線型**：`樓板投影邊界`、`放坡外緣（擾動範圍）`，各用可辨識線型／子類別。
- **參數**：`designToposolidId`（必填）、`viewId`（選）。
- **定位**：讓業主一眼看到擾動範圍大小；範圍面積數字仍以登記簿 `DisturbedAreaSquareMeters`（銜接模式為近似）為準。

## 5C①. 自動排圖工具 `place_views_on_sheet`

- **行為**：`Viewport.Create(doc, sheetId, viewId, 位置)`，依格位陣列把多個視圖排上一張圖紙，並依格框尺寸自動套用視圖比例。
- **參數**：`sheetId`、`placements`（陣列：`viewId` + 格位或座標）、可選 `titleBlockRegion`（排版可用區）。
- **邊界處理**：視圖已在其他圖紙 → 回報並略過（一視圖僅能放一張圖紙）。

## 5C②. PDF 匯出工具 `export_sheets_pdf`

- **行為**：`PDFExportOptions`（Revit 2022+）把指定圖紙批次匯出為單一或逐張 PDF。
- **參數**：`sheetIds`（陣列）、`outputPath`（選，預設專案目錄＋時間戳）、`combine`（預設 true＝合併單檔）。
- **備援**：若 API 匯出受限，回報限制並引導 Revit 內建列印，不以不穩定手段替代。

---

## 5D. 出圖協調命令 `create_grading_presentation`

- **行為**：對登記簿全部（或指定）方案，依「架構總覽」九步依序產出，容錯逐方案回報成敗；最後建一張跨方案比較圖紙。
- **參數**：`schemeIds`／`designToposolidIds`（選，預設全部）、`titleBlockId`（選，預設模型既有 A1 圖框）、`export`（預設 true）、`outputPath`（選）。
- **圖紙構成（每方案一張 A1）**：

```text
┌─────────────┬─────────────┬─────────────┐
│  3D 方案視圖 │  挖填熱區圖  │  坡度熱區圖  │
├─────────────┼─────────────┼─────────────┤
│ 平面+標高+   │  邊界剖面    │  數據框      │
│ 範圍線       │ (原vs設計線) │ CUT/FILL/淨/ │
│             │             │ 最大挖深/擾動 │
└─────────────┴─────────────┴─────────────┘
```

- **比較圖紙**：3 方案關鍵數字（CUT／FILL／淨土方／最大挖深／擾動面積）＋方案縮圖並排；數據取自登記簿。
- **數據框**：以文字註記或明細表呈現登記簿欄位；不重算。

---

## 測試與驗證

1. **純邏輯 NUnit**：邊界細分（最大弦長）、坡度百分比計算、六級坡分級、範圍線環幾何、排版格位計算。
2. **TS schema 測試**：4 個新工具存在、參數型別、profile 過濾（architect/structural 收、mep/fire-safety 不收）。
3. **實機驗證（強制）**：Revit 2024 + TOPO_example——
   - 5A：offset 5 m／10 m 通過包絡驗收、slope 仍過。
   - 5B/5C/5D：三方案各自的熱區/坡度/範圍線/剖面/圖紙/PDF 實機檢視無誤。
4. 建置 R24/R23 0 錯誤、`verify-qaqc.ps1 -SkipBuild -SkipDeploy` 計數檢查通過。

## 計數與文件同步

- 新增 4 工具（`create_slope_heatmap`、`create_grading_extent_lines`、`place_views_on_sheet`、`export_sheets_pdf`）；依既有 grading 工具「外層包裝、內層 index.ts 計數」口徑確認是否影響 96 計數，若影響則同步 CLAUDE.md／README／README.en／DOCUMENT_AUDIENCE_INVENTORY。
- 新增 domain 檔 `domain/grading-slope-classification.md`（六級坡），並在 CLAUDE.md domain 觸發表補列；domain SOP 計數 45→46。
- 更新 `docs/superpowers/specs/2026-07-02-toposolid-footprint-cut-fill-design.md` 的 Roadmap 標記。

## 建置順序（子階段）

| 子階段 | 交付 | 實機驗收點 |
|---|---|---|
| 5A | offset 修正 | offset 5 m/10 m 過閘門 |
| 5B | 坡度熱區 + 範圍線 | 兩圖在方案視圖正確呈現 |
| 5C | 自動排圖 + PDF 匯出 | 視圖上圖紙、PDF 匯出成功 |
| 5D | 協調命令 + 比較圖紙 + 計數/QA/日誌 | 一鍵三方案成套 PDF |

每子階段獨立可驗收，使用者於各段實機確認後再進下一段。

## 開放項與風險

- **圖框依賴**：需模型已載入 A1 圖框；實作起手先 `get_titleblocks` 確認，若無則請使用者載入或改用既有尺寸。
- **自動排版比例**：不同圖框可用區不同，格位／比例採保守預設，實機微調；必要時 `place_views_on_sheet` 開放座標覆寫。
- **PDF API 限制**：`PDFExportOptions` 於 Revit 2022+ 可用；R23/R24 皆支援，若特定環境受限則列印備援。
- **連線穩定**：出圖流程步驟多，須 Revit MCP 服務全程在線；`create_grading_presentation` 逐方案容錯，單步失敗不整組回滾、明確回報。
