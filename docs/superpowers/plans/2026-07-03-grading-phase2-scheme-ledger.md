# 整地 Phase 2：方案登記簿與 Excel 比較表 實作計畫

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 每次整地執行自動寫入一筆完整方案記錄（Extensible Storage JSON），提供 `list_grading_schemes` 查詢與 `export_grading_comparison` Excel 比較表（數值版；截圖嵌入待 Phase 4），把三方案手動比較變成一鍵。

**Architecture:** 登記簿一筆 = 設計地形上的第二個 Extensible Storage entity（新 GUID，`AssociationId` + `SchemeJson` 兩欄；JSON 承載完整欄位，之後 Phase 3 加欄免 schema 遷移）。指標計算（最大挖深/填高、擾動面積）為純函式（NUnit 可測）；Excel 以 ClosedXML 沿用排煙匯出樣式。方案名稱寫入設計地形 Comments（`RevitMCP {方案名}`），沿用實測驗證過的辨識慣例。

**Tech Stack:** C#、Extensible Storage、Newtonsoft.Json（Revit 端）、ClosedXML、NUnit 3.14、TypeScript、node:test。

## Global Constraints（同 Phase 1，另加）

- 新 ES schema 一經建立欄位不可變：`SchemeJson` 為 JSON 字串欄位，記錄結構演進靠 `SchemaVersion` 欄位（自 1 起）。
- 記錄單位一律使用者單位：公尺、平方公尺、立方公尺。
- 擾動面積在銜接模式下為近似值（外圈多邊形面積），記錄須帶 `DisturbedAreaIsApproximate` 旗標——不得把近似值當精確值輸出。
- 新增 2 個 MCP 工具 → 工具數 96→98：`CLAUDE.md`、`README.md`、`README.en.md`、`docs/DOCUMENT_AUDIENCE_INVENTORY.md` 計數表必須同步。
- 讀取類命令（list/export）不開交易；登記簿寫入在既有交易二內完成（CUT/FILL 讀取之後）。

---

### Task 1: 純邏輯——Polygon2D.Area、GradingSchemeRecord、SchemeMetrics（TDD）

**Files:**
- Modify: `MCP/Core/Grading/Polygon2D.cs`（公開 `Area`）
- Create: `MCP/Core/Grading/GradingSchemeRecord.cs`（記錄模型＋指標計算）
- Modify: `MCP.Tests/RevitMCP.Tests.csproj`（加 Compile Include）
- Test: `MCP.Tests/Grading/GradingSchemeRecordTests.cs`、`Polygon2DTests.cs` 追加

**Interfaces（Produces）:**
- `Polygon2D.Area(IReadOnlyList<Point2D>) : double`（`|SignedArea|`）
- `GradingSchemeRecord`：純資料類，屬性 `int SchemaVersion=1`、`string AssociationId`、`string SchemeName`、`string Timestamp`、`string DocumentTitle`、`string Mode`、`double? OffsetDistanceMeters`、`string SlopeRatio`、`double? MaxExtensionMeters`、`long OriginalToposolidId`、`long DesignToposolidId`、`IReadOnlyList<long> FloorIds`、`double CutCubicMeters`、`double FillCubicMeters`、`double NetCubicMeters`、`double MaxCutDepthMeters`、`double MaxFillHeightMeters`、`double DisturbedAreaSquareMeters`、`bool DisturbedAreaIsApproximate`、`IReadOnlyList<FloorMetric> FloorMetrics`、`IReadOnlyList<string> Warnings`、`string ElevationBasis`
- `FloorMetric`：`long FloorId`、`double AreaSquareMeters`、`double BottomZMinMeters`、`double BottomZMaxMeters`
- `SchemeMetrics.MaxCutDepth(IEnumerable<(double OriginalZ, double TargetZ)>) : double`（max(original−target, 0) 的最大值）
- `SchemeMetrics.MaxFillHeight(...) : double`（max(target−original, 0) 的最大值）

**測試（完整）：** `MaxCutDepth`/`MaxFillHeight` 混合挖填清單各取正向最大、空清單回 0；`Polygon2D.Area` 正方形=100、順時針同值；`GradingSchemeRecord` 預設 `SchemaVersion=1`。

**Steps:** 寫失敗測試 → 確認編譯失敗 → 實作 → 全綠 → commit `功能：方案登記簿記錄模型與指標純函式`

---

### Task 2: Adapter——GradingOutcome、擾動面積、WriteSchemeRecord/ReadSchemeRecords

**Files:**
- Modify: `MCP/Core/Grading/GradingModels.cs`（`GradingOutcome`）
- Modify: `MCP/Core/Grading/RevitToposolidGradingAdapter.cs`

**Interfaces（Produces）:**
- `GradingOutcome`（純模型）：`int ModifiedPointCount`、`double MaxCutDepthFeet`、`double MaxFillHeightFeet`、`double DisturbedAreaSquareFeet`、`bool DisturbedAreaIsApproximate`
- 介面 `ApplyGrading` 回傳型別 `int` → `GradingOutcome`
- 介面新增：`void WriteSchemeRecord(Document doc, Toposolid design, string json, string associationId)`、`int CountSchemeRecords(Document doc)`；靜態 `IReadOnlyList<string> ReadSchemeRecords(Document doc)`
- 新 ES schema：GUID `3F8A9D2C-71B4-4E5A-9C86-2D4E5F6A7B80`，SchemaName `RevitMCP_GradingScheme`，欄位 `AssociationId`(string)、`SchemeJson`(string)

**實作要點：**
1. `ApplyGrading` 收尾以 `targets`（Position.Z 為原地形、TargetZ 為目標）算 `SchemeMetrics.MaxCutDepth/MaxFillHeight`；擾動面積 = Σ footprint `Polygon2D.Area`；銜接模式再加 Σ max(圈面積−footprint 面積, 0)（圈點 ≥3 才算，`DisturbedAreaIsApproximate=true`）。`BuildTransitionRings` 需把圈點集合往外傳（回傳值已是 rings，於 ApplyGrading 保留引用）。
2. `WriteSchemeRecord`：`GetOrCreateSchemeSchema()` 同既有 association schema 模式；entity 設 `AssociationId`、`SchemeJson` 後 `design.SetEntity(entity)`。
3. `CountSchemeRecords`/`ReadSchemeRecords`：`new FilteredElementCollector(doc).OfClass(typeof(Toposolid))`，`element.GetEntity(schema)` 有效者取 `SchemeJson`。schema 尚未存在（`Schema.Lookup` null）→ 回 0／空清單。

**Steps:** 實作 → `dotnet build -c Release.R24`、`Release.R23` 0 錯誤 → NUnit 全綠 → commit `功能：整地結果指標與方案登記簿 ES 讀寫`

---

### Task 3: Executor——記錄寫入、Comments 標籤、list/export 命令

**Files:**
- Modify: `MCP/Core/Commands/CommandExecutor.ToposolidGrading.cs`
- Create: `MCP/Core/Commands/CommandExecutor.GradingScheme.cs`
- Modify: `MCP/Core/CommandExecutor.cs`（dispatcher 兩個 case）

**實作要點：**
1. `GradeToposolidToFloors`：新參數 `schemeName`（可選）；交易二內 `ReadCutFill` 之後——組 `GradingSchemeRecord`（`Timestamp = DateTime.Now("yyyy-MM-dd HH:mm:ss")`、`ElevationBasis = "專案內部原點起算（公尺）"`、FloorMetrics 由 footprint 邊界離散點取 `BottomElevationAt` 的 min/max 轉公尺、面積讀樓板 `HOST_AREA_COMPUTED` 轉 m²）→ `JsonConvert.SerializeObject` → `WriteSchemeRecord`；方案名預設 `方案{CountSchemeRecords(doc)+1}`（交易一前先取）；設計地形 Comments 寫 `RevitMCP {schemeName}`（`ALL_MODEL_INSTANCE_COMMENTS`）。回應加 `SchemeName`、`MaxCutDepthMeters`、`MaxFillHeightMeters`、`DisturbedAreaSquareMeters`、`DisturbedAreaIsApproximate`。
2. `ListGradingSchemes`（無參數）：`ReadSchemeRecords` → `JObject.Parse` 陣列 → `{ Count, Schemes, Message }`。
3. `ExportGradingComparison`（`outputPath` 可選）：無記錄 → 拋「目前模型沒有整地方案記錄。」；ClosedXML 單工作表「方案比較」，欄：方案名稱|時間|模式|Offset(m)|坡度|放坡上限(m)|原地形ID|設計地形ID|樓板IDs|CUT(m³)|FILL(m³)|淨土方(m³)|最大挖深(m)|最大填高(m)|擾動面積(m²)|警告；表頭 #4472C4 白字粗體、交替行 #F2F2F2、框線、凍結首列、`AdjustToContents`；擾動面積近似值加「≈」前綴；預設路徑 = 專案目錄（無則桌面）+ `整地方案比較_{yyyyMMdd_HHmmss}.xlsx`。回 `{ OutputPath, SchemeCount, Message }`。
4. dispatcher（`#if REVIT2024_OR_GREATER` 區塊內）加 `case "list_grading_schemes"`、`case "export_grading_comparison"`。

**Steps:** 實作 → R24/R23 0 錯誤 → commit `功能：整地方案登記簿命令——記錄寫入、查詢與 Excel 比較表`

---

### Task 4: TS schema 與測試（TDD）

**Files:**
- Modify: `MCP-Server/src/tools/grading-tools.ts`、`grading-tools.test.ts`

**實作要點：**
1. `grade_toposolid_to_floors` 加 `schemeName`（string，可選，描述含「預設 方案N」）。
2. 新工具 `list_grading_schemes`（`properties: {}`，description 說明回傳登記簿全部方案）、`export_grading_comparison`（`outputPath` string 可選）。同陣列註冊自然繼承 profile 過濾。
3. 測試：三工具皆存在；`schemeName` 存在；`export_grading_comparison.outputPath` 型別 string；profile 測試改斷言三工具在 full/architect/structural、不在 mep/fire-safety。

**Steps:** 先改測試（fail）→ 改 schema → `npm run build && npm test` 全綠 → commit `功能：登記簿查詢與 Excel 比較表 MCP 工具`

---

### Task 5: 計數同步、規格同步、QA/QC、日誌、合併與部署

1. 工具數 96→98：`CLAUDE.md`（Current Source-of-Truth Counts 表）、`README.md`、`README.en.md`、`docs/DOCUMENT_AUDIENCE_INVENTORY.md` 的 `| Runtime MCP tools | 96 |` 改 98。
2. 規格「紀錄、標註與輸出 Roadmap」：方案登記簿與 Excel 比較表標「已實作（2026-07-03）」，附欄位落地說明與工具名。
3. NUnit 全綠、R24/R23 0 錯誤、npm 全綠 → `verify-qaqc.ps1 -SkipBuild -SkipDeploy`（工具數檢查須過）→ 日誌 → merge --no-ff 回 main → Revit 未執行則部署 R24。

## Self-Review 紀錄

- Spec coverage：登記欄位完整度清單 1–5 全數落地（鬆實方三本帳屬 Phase 3、截圖嵌入屬 Phase 4，記錄結構以 SchemaVersion 預留）；Excel 一列一方案（數值版）✓；Comments 標籤慣例 ✓。
- Placeholder scan：無。
- Type consistency：`ApplyGrading→GradingOutcome` 影響 Task 2/3 兩處已對齊；`WriteSchemeRecord(json, associationId)` 簽章 Task 2 定義 Task 3 呼叫。
