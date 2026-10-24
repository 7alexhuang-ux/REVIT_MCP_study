---
name: earthwork-balance
description: "整地土方平衡反求 SOP：以二分法求控制樓板的統一升降量，使淨土方（FILL−CUT）逼近目標值（預設 0＝挖填平衡）。定義淨土方號誌、單調性前提、收斂與夾擊條件、試算回滾紀律，以及鬆實方三本帳（運土鬆方／填方所需自然方／實質淨土方）的換算。當使用者提到土方平衡、挖填平衡、淨土方、餘土、進土、平衡高程、統一升降、二分法試算、鬆方、壓實方、earthwork balance、cut fill balance 時觸發。"
metadata:
  version: "1.0"
  updated: "2026-08-17"
  created: "2026-08-17"
  contributors:
    - "Alex Huang (整地模組設計與實作)"
  references:
    - "MCP/Core/Grading/BalanceSolver.cs（二分法純函式，NUnit 覆蓋）"
    - "MCP/Core/Grading/EarthworkLedger.cs（鬆實方三本帳）"
    - "MCP/Core/Commands/CommandExecutor.GradingBalance.cs（Revit 評估管線）"
  related:
    - grading-slope-classification.md
    - floor-slope-analysis.md
    - hillside-site-review.md
  referenced_by: []
  tags: [Revit, Grading, 整地, 土方, 挖填平衡, 淨土方, 二分法, 鬆方, 壓實, MCP, solve_balanced_elevation]
---

# 整地土方平衡反求（Earthwork Balance SOP）

## 1. 目的

在**不改變基地輪廓、只調整控制樓板整體高程**的前提下，反求「淨土方最接近目標值」的統一升降量。
對應工具 `solve_balanced_elevation`；落成方案後的登記簿數字由 `grade_toposolid_to_floors` 產生。

## 2. 名詞與號誌（不得自行改號）

| 名詞 | 定義 | 來源 |
|---|---|---|
| CUT | 挖方體積（m³） | Revit Toposolid 內建 CUT 參數 |
| FILL | 填方體積（m³） | Revit Toposolid 內建 FILL 參數 |
| **淨土方 Net** | **`FILL − CUT`** | 求解器評估函式的回傳值 |
| offset | 控制樓板的統一升降量（公尺，正值＝抬升） | 求解變數 |

**號誌規定：淨土方為正 → 填大於挖 → 需進土；為負 → 挖大於填 → 餘土外運。**
`targetNetCubicMeters = 0` 即求挖填平衡；欲預留餘土則給負值。

CUT/FILL 一律讀 Revit 內建參數，**不得自行積分或以網格重算**——工具只做反求，不重造權威數字。

## 3. 單調性前提（本方法的成立條件）

二分法成立的前提是：**淨土方隨 offset 單調遞增**（樓板抬升 → 挖得少、填得多 → Net 變大）。

此前提在一般整地情境成立。若基地存在使樓板升高反而增加挖方的特殊地形（例如樓板穿出既有高地後改由上方切除），單調性可能破壞，二分法結果不可信 → 改用逐點試算並人工判讀。

## 4. 求解流程（`BalanceSolver.Solve`）

輸入：評估函式 `evaluateNet(offset)`、`target`、`tolerance`、`lowerBound`、`upperBound`、`maxIterations`。

1. **前置驗證**：`tolerance > 0`、`maxIterations > 0`、`lowerBound < upperBound`，否則拋錯。
2. **先評兩端點**：
   - `|net(lowerBound) − target| ≤ tolerance` → 直接回傳該端點（收斂）。
   - `|net(upperBound) − target| ≤ tolerance` → 直接回傳該端點（收斂）。
3. **夾擊檢查**：兩端點的 `net − target` **同號** → 拋錯「調整區間無法夾住目標淨土方」，訊息列出兩端實際淨值。
   **不得外插、不得猜測、不得自動放寬區間**；由使用者決定放寬 `maxAdjustMeters` 或修正目標。
4. **二分迭代**（最多 `maxIterations` 次）：
   - 取中點 `middle = (low + high) / 2`，評估 `middleNet = net(middle) − target`，記錄該步。
   - 全程追蹤 `bestOffset` / `bestNet`（絕對值最小者）。
   - `|middleNet| ≤ tolerance` → 收斂，回傳中點。
   - `sign(middleNet) == sign(lowNet)` → `low = middle`；否則 `high = middle`。
5. **用盡迭代**：回傳 `Converged = false` 與**歷程中最佳解**（非最後一次中點），並附完整迭代歷程。

純函式零 Revit 相依，行為由 `MCP.Tests/Grading/BalanceSolverTests.cs` 五項測試鎖定（線性收斂、容差內即停、迭代上限未收斂、端點同號拋錯、端點本身命中）。

## 5. 評估函式與試算紀律

每次 `evaluateNet(offset)` 在 Revit 端執行一次**完整整地管線**：

1. 開啟獨立交易「平衡試算」。
2. 依 offset 位移控制樓板 → 取底面輪廓 → 建立設計地形副本 → 套用整地 → `Regenerate()`。
3. 讀取設計地形的 CUT/FILL，回傳 `FILL − CUT`。
4. **`finally` 一律 `RollBack()`**——無論成功或例外，試算絕不在模型留下痕跡。

**紀律：試算階段禁止 Commit。** 每次迭代約需數秒（重跑整地），故 `maxIterations` 上限為 30。

## 6. 預設值

| 參數 | 預設 | 約束 |
|---|---:|---|
| `targetNetCubicMeters` | 0 | 無 |
| `toleranceCubicMeters` | 10 | > 0 |
| `maxAdjustMeters` | 10 | > 0，區間為 ±此值 |
| `maxIterations` | 10 | 1–30 |
| `apply` | false | — |

**容差 10 m³ 是工程判斷值，非法規值。** 送審或結算用途應由使用者依案件規模自行指定。

## 7. 套用規則（`apply = true`）

- **僅在 `Converged = true` 時套用**。未收斂 → 加入警告、**不修改模型**，回傳最佳解供人工判斷。
- 套用時另開交易寫入樓板高程，再轉呼叫 `grade_toposolid_to_floors` 落成方案與登記簿。
- 方案名稱未指定時預設「平衡方案（±X.XX m）」。
- `apply = false`（預設）為純試算，模型完全不動。

## 8. 鬆實方三本帳（`EarthworkLedger`）

Revit 的 CUT/FILL 是**自然方（bank）**。實務調度需另外兩本帳：

| 帳別 | 公式 | 意義 |
|---|---|---|
| 運土鬆方 | `CUT × looseFactor` | 挖出土裝車後的運輸體積 |
| 填方所需自然方 | `FILL ÷ compactionFactor` | 要壓實成 FILL 需要的原始土量 |
| **實質淨土方** | **`FILL ÷ compactionFactor − CUT`** | 負值＝餘土，正值＝需進土 |

- `looseFactor` 與 `compactionFactor` **必須成對提供且大於 0**，否則拋錯。
- 兩係數依土壤性質與現場夯實規範決定，**工具不提供預設值**，由使用者指定。

### 求解基準：有無係數決定兩種平衡（使用前必讀）

求解器的評估函式**依是否提供鬆實方係數切換基準**，回傳的 `netBasis` 欄位標明採用哪一種：

| 條件 | `netBasis` | 求解目標 | 意義 |
|---|---|---|---|
| 有給 `looseFactor` + `compactionFactor` | `effective` | `FILL ÷ compactionFactor − CUT = target` | **料源平衡**（挖出的土剛好夠填） |
| 未給係數 | `bank` | `FILL − CUT = target` | 自然方平衡（幾何體積相等） |

**兩者不等價。** 當 `compactionFactor ≠ 1`，只做自然方平衡會低估填方需求：

```
自然方平衡求得 FILL = CUT = V
→ 實質淨土方 = V ÷ compactionFactor − V = V × (1/compactionFactor − 1) ≠ 0
```

例：`compactionFactor = 0.9`、`V = 1000 m³` → 實質仍需進土約 111 m³。

**規定：凡涉及料源調度、外運計價或送審土方數量者，必須提供鬆實方係數**，使 `netBasis = effective`。
未提供係數的 `bank` 結果僅供概念階段的量體感覺，不得作為土方調度依據。

`looseFactor` **不影響平衡解**（運輸體積不參與質量平衡），但因驗證要求成對提供，仍須一併給值；
它只決定登記簿的運土鬆方帳。

> 修訂紀錄：v1.0 初版記錄此處為既有缺陷（求解永遠用 `FILL − CUT`）。
> 2026-08-17 經使用者確認為錯誤並修正 `CommandExecutor.GradingBalance.cs` 的評估函式，改為與 `EarthworkLedger` 同一公式。

## 9. 失敗模式與處置

| 現象 | 原因 | 處置 |
|---|---|---|
| 「無法夾住目標淨土方」 | ±`maxAdjustMeters` 區間內達不到目標 | 放寬 `maxAdjustMeters`，或確認目標是否可達 |
| `Converged = false` | 迭代次數不足或容差過嚴 | 提高 `maxIterations` 或放寬 `toleranceCubicMeters` |
| 「平衡試算於 offset X 失敗」 | 該 offset 下整地管線本身出錯（樓板脫離地形等） | 縮小 `maxAdjustMeters` |

三種情況都**不會修改模型**。

## 10. 相關

- 坡度分級與熱區：`grading-slope-classification.md`
- 樓板排水坡度（不同用途）：`floor-slope-analysis.md`
- 整地本體與放坡模式：`docs/superpowers/specs/2026-07-02-toposolid-footprint-cut-fill-design.md`
