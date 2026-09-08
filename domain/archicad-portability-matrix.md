---
name: archicad-portability-matrix
description: "BIM_MCP 工作流在 Archicad 的可攜性分級與驗證狀態：A／B／C 三級的判準、各 Wave 的實際進度，以及「已驗證」與「尚未驗證」的分界。當使用者問某支 Skill 能不能在 Archicad 用、或要規劃下一批轉譯時參考。"
metadata:
  version: "1.1"
  updated: "2026-09-08"
  created: "2026-09-08"
  references:
    - "https://github.com/shuotao/REVIT_MCP_study/issues/98"
  related:
    - archicad-adapter-boundary.md
    - archicad-terminology-map.md
  referenced_by: []  # 尚未被任何 SKILL.md 引用；接線待 archicad-skill-adapter 更新
  contributors:
    - "Archwiz-boss"
  tags: [archicad, portability, matrix, pilot, 可攜性, 分級]
---

# Archicad 可攜性分級與驗證狀態

## 本檔定位

本檔是**中文速查與進度表**。逐一 Skill 的完整 A／B／C 分級表（涵蓋全部 canonical Skills）**權威正本**是：

> `docs/integrations/archicad-skill-portability.md`

新增或調整任何 Skill 的分級，改那一份；本檔只反映結論與進度。兩者衝突以正本為準。

## 1. 分級定義（與 issue #98 用語對照）

repo 正本使用 A／B／C，issue #98 提案原文使用另一組英文詞。**兩者是同一套分級，不是兩套**：

| 正本分級 | issue #98 原文用語 | 意義 | 可以怎麼宣稱 |
|---|---|---|---|
| **A：可直接共用** | `backend-neutral` | 處理的是知識、文件或 repository 工作，不依賴任何 BIM 模型物件。 | 可共用編排；仍須遵守原有的輸入與驗證規則。 |
| **B：需要 adapter** | `adapter-required` | Domain 的目的、公式與決策流程可共用，但工具、識別碼、單位與物件語意必須依 backend 轉譯。 | **只有完成 discovery、資料對齊與驗證的步驟**可稱為支援。 |
| **C：backend-specific** | `backend-specific` | 緊綁 Revit API／Revit 文件模型，或本身就是某一 backend 的安裝／開發工具。 | 保留原路徑；在有獨立 Archicad 設計與測試前不自動轉譯。 |

**B 不等於「已支援」。** 分級是路由判斷，不是驗證結果。

## 2. 驗證狀態的三個等級

沿用 issue #98 的驗收用語，並嚴格區分：

| 狀態 | 意義 |
|---|---|
| `verified` | 有完整 trace：載入的 Skill／Domain 路徑、使用的 Domain 方法、discovery 回傳、唯讀結果、寫入結果、read-back 驗證，全部記錄在案。 |
| `approximate mapping` | 能對應，但語意或結果與 Revit 側不完全一致，差異已寫明。 |
| `unsupported` | 找不到能力，且已證明分頁走完、指令名已重新 discovery。 |

**「MCP 連得上」不構成任何一級。** 三支公開 MCP 工具能通，只證明 transport 可用，不證明任何 canonical Skill 曾被完整跑過。

## 3. Wave 1 三支 Pilot：目前狀態

`element-query`、`room-numbering`、`quantity-takeoff-excel` 三支有正式 pilot 規格檔，位於 `.claude/skills/archicad-skill-adapter/references/pilot-*.md`。目前**一支已達 `verified`，兩支未達**：

| Pilot | 分級 | 證據現況 | 還缺什麼 |
|---|---|---|---|
| `element-query` | B | 有一次 live-test evidence（fork 側提交，**尚待維護者審閱**），全程只經三支公開 MCP 工具、未直接打 JSON API 埠 | 審閱通過後才可標 `verified` |
| `room-numbering` | B | **`verified`（2026-09-08 實機）**：42 個 Zone 完成 dry-run→核准→寫入→read-back，`executionResults` ok=42/failed=0，read-back success=42/unchanged=0 | 維護者複核；本 repo 無 Archicad，無法複驗 |
| `quantity-takeoff-excel` | B | `Live-Test Evidence` 區塊**仍是空白模板** | Zone 邊界與牆門窗關聯的證據鏈、開口扣除、Excel 可追溯欄位 |

### `room-numbering` 通過的條件，以及一個必要的新前置檢查

同一套程序在**鎖定的樓層**上跑，會回報五個批次全部成功卻一個 Zone 都沒改。差別只在目標元素是否可編輯：

- `properties_set_property_values_of_elements` 的失敗訊息在 **per-element 的 `executionResults`** 裡，工具層不會回報錯誤。只看工具層結果就會誤判成功。
- `propertyIsEditable` 描述的是**屬性**，不是這個元素當下能不能寫。元素層級的判準是 `elements_get_elements_by_type` 的 `IsEditable` filter。實測鎖定樓層：`["OnActualFloor"]` 回 105 個，`["OnActualFloor","IsEditable"]` 回 **0** 個，而該層仍完全可見可讀。

因此所有 Archicad 寫入流程都必須加入兩道關卡：**寫入前用 `IsEditable` 過濾確認目標可寫**（掉出來的列為 skipped），**寫入後逐筆檢查 `executionResults` 並 read-back**。細節見 [archicad-adapter-boundary.md](archicad-adapter-boundary.md) 第 3 節 E。

讀取路徑上已取得的可用材料（2026-09-08 實測，`0.5.3` + Archicad 28 + Tapir 1.5.8）：

- `elements_get_relations_of_elements` 對 Zone 回傳的 `wallParts`，提供「哪一面牆、沿哪一段、構成這個 Zone 的第幾條邊」，是算量所需的牆—空間證據鏈。
- 分頁在此版本可完整走完（實測全量 `Wall` 7376 筆／74 頁／0.6 秒）。
- property 值的單位陷阱見 [archicad-terminology-map.md](archicad-terminology-map.md) 第 3 節，算量前必須處理。

## 4. Wave 2：三項判斷（2026-09-08 重測）

| Skill | 原判斷 | 重測結論 |
|---|---|---|
| `element-coloring` | 降級為暫時 Highlight | **維持**，並新增單位風險：以 property 值分組上色，會因格式化字串把不同數值併成同一組 |
| `detect-clashes` | 縮限為 GUID 對撞、排除 Hotlink | **維持**，且 Hotlink 排除已有反面證據（`project_get_hotlinks` 不回 GUID ownership） |
| `wall-orientation-check` | 能力缺口，降級人工檢查 | **前提不成立，建議升為 pilot 候選** |

### `wall-orientation-check` 的狀態變更

原判斷的兩項「缺少的證據」都不成立：

1. `flipped` 已被 `0.5.3` 接受（`GetDetailsOfElements` 仍因其他 14 個欄位失敗，那是 wrapper 回應模型過期，不是 Archicad 的 API 缺口）。
2. Revit 側「房間檢測」在 Archicad 有直接對應：Zone 的 `wallParts` 關係。

實測判定結果（單一樓層，105 個 Zone、280 面牆）：**196 內牆候選／69 外牆候選／15 無判定，覆蓋率 94.6%**，跨樓層誤引用 0 筆。

升為 pilot 時的驗收條件：

- 判定一律標為**候選**，不自動宣稱 correct／incorrect，不自動翻轉任何牆；
- 「無 Zone 界定」必須與「確實無邊界」分開回報；
- 主路線走 `elements_get_relations_of_elements`（可批次），**不要**走 `elements_get_zone_boundaries`（單 Zone 呼叫，實測同一 GUID 首次成功後持續逾時）。

詳細邊界與實測數據見 [archicad-adapter-boundary.md](archicad-adapter-boundary.md) 第 3 節 C。

## 5. 尚未分級與已知缺口

- 上游後續新增的 Skill 若未出現在正本矩陣中，**沒有分級就是沒有分級**，不要以類型相近為由代為推定。
- 正本矩陣 C 類中的 `setup-archicad-mcp` 目前指向本 repo 不存在的 skill（連同其安裝腳本仍只在 fork 側），引用前先確認。
