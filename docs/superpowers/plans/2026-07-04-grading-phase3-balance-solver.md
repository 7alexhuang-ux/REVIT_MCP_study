# 整地 Phase 3：平衡高程反求與鬆實方三本帳 實作計畫

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 新增 `solve_balanced_elevation` 工具——以二分法試算樓板統一升降量，使淨土方逼近目標（預設 0）；整地記錄支援鬆實方三本帳（自然方／鬆方／壓實方），避免帳面平衡實際不平衡。

**Architecture:** 二分法求解器為純函式（注入評估函式，NUnit 可測）；每次試算在 `TransactionGroup` 內調整樓板「自標高偏移」、跑既有整地管線讀 CUT/FILL、整組回滾——數字永遠來自 Revit 內建參數，不用自製體積近似。`apply=true` 時把解出的偏移真正寫入樓板並轉呼叫既有 `GradeToposolidToFloors` 落成方案（含登記簿）。三本帳為純計算（`EarthworkLedger`），掛在 grade 工具的可選係數參數上，記錄 `SchemaVersion` 升為 2。

**Tech Stack:** 同 Phase 1/2。

## Global Constraints（同前階段，另加）

- 淨土方號誌沿用既有定義：`Net = Fill − Cut`（負值＝餘土）。三本帳的實質淨土方同號誌：`EffectiveNet = Fill/壓實係數 − Cut`。
- 鬆實方係數：`looseFactor`（鬆方係數）與 `compactionFactor`（壓實係數）皆須 > 0，且必須成對提供；缺一即拒絕。
- 二分法前提：淨土方隨樓板整體升降單調遞增（升板→挖少填多）。區間端點淨值無法夾住目標時，誠實回報兩端淨值並中止，不外插。
- 試算絕不留痕：每次試算的樓板偏移、設計副本、階段調整全部隨 `TransactionGroup.RollBack()` 消失；只有 `apply=true` 的最終落成才寫模型。
- 新工具經 `revit-tools.ts` 外層註冊，內層計數維持 96。

---

### Task 1: 純邏輯——EarthworkLedger 與 BalanceSolver（TDD）

**Files:**
- Create: `MCP/Core/Grading/EarthworkLedger.cs`、`MCP/Core/Grading/BalanceSolver.cs`
- Modify: `MCP.Tests/RevitMCP.Tests.csproj`
- Test: `MCP.Tests/Grading/EarthworkLedgerTests.cs`、`MCP.Tests/Grading/BalanceSolverTests.cs`

**Interfaces（Produces）:**
- `EarthworkLedger.Compute(double cut, double fill, double looseFactor, double compactionFactor) : EarthworkLedger`，屬性：`LooseFactor`、`CompactionFactor`、`HaulVolumeLooseCubicMeters = Cut×loose`、`RequiredBankForFillCubicMeters = Fill/compaction`、`EffectiveNetCubicMeters = Fill/compaction − Cut`。係數 ≤0 拋繁體中文 `ArgumentException`。
- `BalanceSolver.Solve(Func<double,double> evaluateNet, double target, double tolerance, double lowerBound, double upperBound, int maxIterations) : BalanceSolveResult`——標準二分法；`BalanceSolveResult`：`double SolvedOffset`、`double AchievedNet`、`bool Converged`、`IReadOnlyList<BalanceIteration> Iterations`（`Offset`、`Net`）。端點同號（無法夾住）拋 `InvalidOperationException`，訊息含兩端淨值。

**測試（要點）：** Ledger 數值案例（cut=100, fill=80, loose=1.25, compaction=0.9 → 鬆方 125、所需自然方 88.89、實質淨 −11.11）；係數缺陷拒絕。Solver 以 `x=>x*2-5`（線性）在 [-10,10] 收斂至 2.5；tolerance 停止；maxIterations 停止時 `Converged=false`；同號端點拋錯訊息含「無法夾住」。

**Steps:** 失敗測試 → 實作 → 全綠 → commit `功能：鬆實方三本帳與二分法平衡求解純邏輯`

---

### Task 2: 記錄與 grade 工具係數參數

**Files:**
- Modify: `MCP/Core/Grading/GradingModels.cs`（`GradingRequest` 加 `double? LooseFactor`、`double? CompactionFactor`；`TransitionSettings.FromRequest` 加成對驗證）
- Modify: `MCP/Core/Grading/GradingSchemeRecord.cs`（`SchemaVersion` 預設 2；新屬性 `EarthworkLedger Ledger`，可為 null）
- Modify: `MCP/Core/Commands/CommandExecutor.ToposolidGrading.cs`（解析參數；有係數時建 Ledger 進記錄與回應）
- Modify: `MCP/Core/Commands/CommandExecutor.GradingScheme.cs`（Excel 加五欄：鬆方係數|壓實係數|運土鬆方(m³)|填方所需自然方(m³)|實質淨土方(m³)）
- Test: `TransitionSettingsTests` 加成對驗證測試；`GradingSchemeRecordTests` 驗 SchemaVersion=2

**Steps:** 失敗測試 → 實作 → NUnit 全綠＋R24/R23 0 錯誤 → commit `功能：整地記錄鬆實方三本帳（SchemaVersion 2）`

---

### Task 3: solve_balanced_elevation 命令

**Files:**
- Create: `MCP/Core/Commands/CommandExecutor.GradingBalance.cs`
- Modify: `MCP/Core/CommandExecutor.cs`（dispatcher case）
- Modify: `MCP/Core/Grading/RevitToposolidGradingAdapter.cs`（介面＋實作 `void ShiftFloorHeights(Document doc, IReadOnlyList<Floor> floors, double deltaFeet)`——調整 `FLOOR_HEIGHTABOVELEVEL_PARAM`）

**參數：** `toposolidId`、`floorIds`、`mode`/`offsetDistance`/`slopeRatio`/`maxExtension`（同 grade）、`targetNetCubicMeters`（預設 0）、`toleranceCubicMeters`（預設 10）、`maxAdjustMeters`（預設 10，二分區間 ±此值）、`maxIterations`（預設 10）、`apply`（預設 false）、`schemeName`（apply 時使用）、`looseFactor`/`compactionFactor`（apply 時傳遞）。

**流程：**
1. 驗證元素與參數（重用 `GradingRequest`/`TransitionSettings`，`updateExisting=false`）。
2. 評估函式 `EvaluateNet(offsetFeet)`：`TransactionGroup` → 交易A（掛 preprocessor）`ShiftFloorHeights(+offset)` 提交 → 交易B `CreateDesignCopy`＋`ApplyGrading`＋`ReadCutFill` 提交 → 讀淨值 → **`group.RollBack()`** → 回傳 `fill − cut`。每次試算記 `Timing` 階段「試算#N」。
3. 先評估區間兩端 `±maxAdjust`；`BalanceSolver.Solve` 跑二分。
4. `apply=false`：回報 `SolvedOffsetMeters`、`AchievedNetCubicMeters`、`Converged`、`Iterations`（偏移/淨值，公尺與 m³）。
5. `apply=true`：獨立交易把最終偏移寫入樓板（不回滾），再組 JObject 轉呼叫 `GradeToposolidToFloors`（帶 schemeName 與係數），回應合併兩者。
6. 全程失敗都要 `WriteGradingPerformanceRecord`（success=false）。

**Steps:** 實作 → R24/R23 0 錯誤 → commit `功能：solve_balanced_elevation 平衡高程反求命令`

---

### Task 4: TS schema 與測試（TDD）

`grading-tools.ts`：grade 工具加 `looseFactor`/`compactionFactor`（number，exclusiveMinimum 0，描述註明成對）；新工具 `solve_balanced_elevation`（上述參數，`required: ["toposolidId","floorIds"]`）。測試：新工具存在、`targetNetCubicMeters` 預設 0、`apply` 預設 false、profile 清單加入第四個工具名。

**Steps:** 失敗測試 → schema → `npm run build && npm test` 全綠 → commit `功能：平衡反求與鬆實方 MCP 介面`

---

### Task 5: 規格同步、QA/QC、日誌、合併與部署

1. 規格「進階功能」節：平衡高程反求、鬆實方係數標「已實作（2026-07-04）」＋工具/參數名；登記欄位完整度清單狀態更新（三本帳落地、SchemaVersion 2）。
2. NUnit 全綠、R24/R23 0 錯誤、npm 全綠 → QA/QC 與基線比對 → 日誌 → merge --no-ff → Revit 未執行則部署。

## Self-Review 紀錄

- Spec coverage：平衡高程反求（二分法、預設趨近 0）✓；鬆實方三本帳（自然/鬆/壓實）✓；「帳面平衡實際不平衡」由 EffectiveNet 揭示 ✓；試算不留痕 ✓；CUT/FILL 一律 Revit 內建參數 ✓。
- Placeholder scan：無。
- Type consistency：`BalanceSolver.Solve` 簽章與 Task 3 呼叫一致；`EarthworkLedger.Compute` 與 Task 2 記錄/Excel 欄位一致；`ShiftFloorHeights` Task 3 定義與呼叫一致。
