# 整地 Phase 4：方案視圖、熱區圖、標註、還原與計算書 實作計畫

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 完成整地功能的表現與送審層：每方案一個鎖定 3D 視圖（截圖來源＋標註載體＋方案書籤）、Excel 比較表嵌入截圖、挖填熱區圖（AVF）、樓板標高標註、方案還原（寫回樓板高程）、方格法土方計算書。

**Architecture:** 五個新 MCP 工具＋一個既有工具擴充，全部經 `revit-tools.ts` 外層註冊（內層計數 96 不變）。方案識別一律以 `designToposolidId`（記錄就掛在設計地形上）。記錄 `SchemaVersion` 升 3：`FloorMetric` 加 `HeightOffsetMeters`（還原依據）、記錄加 `SchemeViewId`／`ScreenshotPath`（視圖工具寫回）。方格法計算書的格子數學為純函式（NUnit 可測），取樣深度來自原/設計地形射線（呈現用），總量與 Revit 內建 CUT/FILL 並列對照、差額顯示——不假裝格子加總等於正式報表值。

**Tech Stack:** 同前；另用 Revit AVF（`SpatialFieldManager`）、`ImageExportOptions`、ClosedXML `AddPicture`。

## Global Constraints（同前階段，另加）

- 邊界剖面不寫新程式：規格原文即「以 `create_section_view` 自動建立」，由 AI 客戶端以登記簿座標組合既有工具完成；規格補一段工作流說明。
- 方案還原僅支援「樓板高程不同」型方案（規格定案）；輪廓不同型明確拒絕並引導 Design Option 手動容器。
- 熱區圖以 AVF 畫在方案視圖上，值 = 原地形Z − 設計Z（正=挖紅、負=填藍）；AVF 只是視覺化，不產生土方數字。
- Spot Elevation 需鎖定 3D 視圖（方案視圖建立時即鎖定）；逐點 try/catch，回報成功/失敗數，不因單點失敗全毀。
- 方格法格子體積為「格中心深度 × 格面積」的呈現值；Excel 須並列 Revit CUT/FILL 與差額（%），差額即方格法離散誤差，誠實揭露。

---

### Task 1: 純邏輯——FloorMetric.HeightOffsetMeters、SchemaVersion 3、EarthworkGrid（TDD）

**Files:** `GradingSchemeRecord.cs`（v3 欄位）、新 `MCP/Core/Grading/EarthworkGrid.cs`＋測試＋csproj Include。

**Interfaces（Produces）:**
- `FloorMetric.HeightOffsetMeters : double?`（樓板「自標高偏移」，還原依據）
- `GradingSchemeRecord`：`SchemaVersion=3`、`long? SchemeViewId`、`string ScreenshotPath`
- `EarthworkGrid.Compute(IReadOnlyList<GridCell> cells, double cellAreaSquareMeters) : EarthworkGridResult`；`GridCell`：`Row`、`Column`、`double? DepthMeters`（正=挖、負=填、null=格中心無地形）；Result：`CutCubicMeters`（Σ正深×格積）、`FillCubicMeters`（Σ|負深|×格積）、`SampledCellCount`

**測試要點：** v3 預設值與新欄位 null；EarthworkGrid 混合格（+2、−1、null、0）× 格積 100 → cut 200、fill 100、sampled 3。

**Steps:** 失敗測試 → 實作 → 全綠 → commit `功能：記錄 v3 與方格法格子純計算`

---

### Task 2: 方案視圖工具 `create_grading_scheme_view`

**Files:** 新 `MCP/Core/Commands/CommandExecutor.GradingView.cs`、dispatcher case、adapter 加 `TryReadSchemeRecord(Document, long designId) : string`（static）與 `UpdateSchemeRecord`（覆寫 entity）。

**參數：** `designToposolidId`（必填）、`exportPng`（預設 true）、`outputPath`（可選，預設專案目錄 `整地-{方案名}.png`）。

**流程：**
1. 讀記錄（無記錄 → 拒絕：「此元素沒有整地方案記錄」）；取方案名、控制樓板 IDs、原地形 ID。
2. 交易：`View3D.CreateIsometric(doc, viewFamilyTypeId)` → 命名 `整地-{方案名}`（重名加流水號）→ `IsolateElementsTemporary` 後 `ConvertTemporaryHideIsolateToPermanent`（隔離設計地形＋控制樓板）→ `view.SaveOrientationAndLock()` → 記錄 JSON 更新 `SchemeViewId` 回寫 entity。
3. `exportPng`：交易外 `ImageExportOptions`（`SetViewsAndSheets`、`ZoomFitToPage`、2000px、PNG）匯出，`ScreenshotPath` 回寫（第二個小交易）。
4. 回應：ViewId、ViewName、ScreenshotPath、Message。

**Steps:** 實作 → R24/R23 0 錯誤 → commit `功能：整地方案鎖定 3D 視圖與截圖（create_grading_scheme_view）`

---

### Task 3: 挖填熱區圖 `create_cutfill_heatmap`

**Files:** `CommandExecutor.GradingView.cs` 追加、dispatcher case。

**參數：** `designToposolidId`（必填）、`viewId`（可選；預設用記錄的 `SchemeViewId`，皆無 → 拒絕請先建方案視圖）、`sampleStepMeters`（預設 2）。

**流程（單一交易）：**
1. 讀記錄取原地形 ID；取設計地形頂面（`Options{ComputeReferences=true}`，法線 Z>0.5 的面）。
2. `SpatialFieldManager.GetSpatialFieldManager(view) ?? CreateSpatialFieldManager(view, 1)`；`RegisterResult(new AnalysisResultSchema("整地挖填深度", "原地形Z−設計Z（公尺），正=挖、負=填"))`。
3. 每個頂面：`AddSpatialFieldPrimitive(face.Reference)`；UV 網格取樣（步距 `sampleStepMeters`）→ 世界 XY → 原地形射線 Z（`IntersectTerrainTopZ` 模式，原地形實體）→ 值 = 原Z − 設計面點Z（公尺）；`UpdateSpatialFieldPrimitive(FieldDomainPointsByUV, FieldValues)`。無值面跳過。
4. `AnalysisDisplayStyle`：名稱「RevitMCP 挖填熱區」，`AnalysisDisplayColoredSurfaceSettings`（ShowGridLines=false）＋ `AnalysisDisplayColorSettings`（紅 `#CC0000` ↔ 白 ↔ 藍 `#0055CC`）＋ Legend 顯示；存在同名樣式即重用；設 `view.AnalysisDisplayStyleId`。
5. 回應：樣式化面數、取樣點數、最大挖深/最大填高（取樣範圍內）、Message（明示 AVF 僅視覺化）。

**Steps:** 實作 → R24/R23 0 錯誤 → commit `功能：挖填熱區圖 AVF 視覺化（create_cutfill_heatmap）`

---

### Task 4: 標高標註 `annotate_grading_scheme`

**Files:** `CommandExecutor.GradingView.cs` 追加、dispatcher case。

**參數：** `designToposolidId`、`viewId`（可選，預設 SchemeViewId）、`annotateFloors`（預設 true：每片樓板頂面中心＋各角點）、`annotateDaylight`（預設 false：設計地形頂面沿樓板邊界外圈取樣點，銜接模式才有意義）。

**流程（單一交易）：** 讀記錄→樓板頂面（法線 Z>0.965 的 PlanarFace，`ComputeReferences=true`）→ 每面：中心（`face.Evaluate` UV 中點）＋邊界角點內縮 300mm；`doc.Create.NewSpotElevation(view, face.Reference, point, point+忽略彎折向量, point, point, false)`；逐點 try/catch 計數。回應：placed/failed 數、Message。

**Steps:** 實作 → 建置 → commit `功能：整地方案標高標註（annotate_grading_scheme）`

---

### Task 5: 方案還原 `restore_grading_scheme`

**Files:** `CommandExecutor.GradingView.cs` 追加（或新檔 `CommandExecutor.GradingRestore.cs`）、dispatcher case。

**參數：** `designToposolidId`（要還原的方案）、`rerunGrading`（預設 false）。

**流程：**
1. 讀記錄；`FloorMetrics[].HeightOffsetMeters` 任一為 null → 拒絕：「此方案記錄無樓板高程資料（SchemaVersion<3），無法自動還原」。
2. 驗證樓板仍存在且類別正確；逐板比較目前偏移，全部一致 → 回報「已在該方案狀態」。
3. 交易：逐板 `FLOOR_HEIGHTABOVELEVEL_PARAM.Set(記錄值)`（公尺→內部）；提交。
4. `rerunGrading=true`：轉呼叫 `GradeToposolidToFloors`（原地形 ID＋原參數＋schemeName=「{原方案名}-還原重跑」）。
5. 回應：調整樓板數、各板 (floorId, 原偏移, 還原偏移)、Message。輪廓不同（樓板遺失）→ 繁體中文錯誤引導 Design Option 手動容器。

**Steps:** 實作 → 建置 → commit `功能：整地方案還原（restore_grading_scheme）`

---

### Task 6: 方格法計算書 `export_earthwork_gridsheet`

**Files:** 新 `MCP/Core/Commands/CommandExecutor.GradingGridsheet.cs`、dispatcher case。

**參數：** `designToposolidId`、`cellSizeMeters`（預設 10，水保常用 10m 方格）、`outputPath`（可選）。

**流程：**
1. 讀記錄取原地形 ID 與 CUT/FILL 正式值；取兩地形實體。
2. 以擾動範圍 bbox 建方格；每格中心雙射線（原/設計）→ `GridCell.DepthMeters = 原Z−設計Z`（無交集 null）。
3. `EarthworkGrid.Compute` 得方格法 cut/fill。
4. ClosedXML 兩工作表：「方格明細」＝行列矩陣（每格上：原GL／下：設計GL／角：挖填深，紅挖藍填底色）＋「總表」＝方格法 CUT/FILL、Revit 正式 CUT/FILL、差額與差額%（明示為離散誤差）、格數、格徑、方案識別欄。
5. 回應：OutputPath、方格法/正式值/差額、Message。

**Steps:** 實作 → 建置 → commit `功能：方格法土方計算書（export_earthwork_gridsheet）`

---

### Task 7: Excel 比較表嵌入截圖

**Files:** `CommandExecutor.GradingScheme.cs`（`ExportGradingComparison` 擴充）。

**流程：** 新參數 `includeScreenshots`（預設 true）；記錄有 `ScreenshotPath` 且檔案存在 → 每方案列高 120，第 22 欄「方案截圖」`AddPicture(path).MoveTo(cell).WithSize(160, 110)`；無截圖寫「（尚未建立方案視圖）」。既有數值欄不動。

**Steps:** 實作 → 建置 → commit `功能：Excel 比較表嵌入方案截圖`

---

### Task 8: 記錄寫入端 v3 欄位補齊 ＋ TS schema 與測試（TDD）

1. `CommandExecutor.ToposolidGrading.cs`：`BuildFloorMetrics` 補 `HeightOffsetMeters`（讀 `FLOOR_HEIGHTABOVELEVEL_PARAM` 轉公尺）。
2. `grading-tools.ts`：五個新工具 schema＋`export_grading_comparison` 加 `includeScreenshots`；profile 測試清單擴為 9 個工具名；各工具預設值斷言。

**Steps:** 失敗測試 → 實作 → `npm run build && npm test` 全綠 → commit `功能：Phase 4 工具 MCP 介面`

---

### Task 9: 規格同步、QA/QC、日誌、合併與部署

1. 規格：「紀錄、標註與輸出 Roadmap」各項標已實作＋工具名；「樓板與圖面標註」「進階功能」（熱區圖、計算書）、「方案還原策略」更新；邊界剖面補「既有工具組合工作流」段；登記欄位清單補 v3。
2. NUnit 全綠、R24/R23 0 錯誤、npm 全綠 → QA/QC 基線比對 → 日誌 → merge --no-ff → 部署。

## Self-Review 紀錄

- Spec coverage：方案視圖（鎖定/隔離/命名/書籤）✓、截圖（PNG＋Excel 嵌入）✓、熱區圖 ✓、標高標註（NewSpotElevation、鎖定 3D）✓、方案還原（高程型；輪廓型拒絕引導）✓、計算書（方格法＋正式值對照）✓、邊界剖面（既有工具組合，規格註記）✓。
- Placeholder scan：無 TBD；各任務含關鍵 API 與參數列。
- Type consistency：`TryReadSchemeRecord`/`UpdateSchemeRecord` Task 2 定義、Task 3–6 重用；`SchemeViewId`（long?）與 viewId 參數一致；EarthworkGrid 與 Task 6 呼叫一致。
