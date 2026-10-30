# 萬用窗族生成器 規格書（SPEC）

> 狀態：v0.2（2026-10-31：窗型擴充、YKK AP 框深、入口架構；v0.1 為 2026-09-14）｜受眾：人＋AI 共用｜語言：zh-TW
> 互動示意：`docs/universal-window/configurator-demo.html`（規格 JSON 的形狀以它為準）
> 決策紀錄：`docs/ADR.md` D-001 ～ D-010｜路線圖：`docs/universal-window/ROADMAP.md`

## 1. 目標

用一句規格（幾排 × 幾樘、每格幾扇、每扇怎麼開）讓 MCP 在 Revit 2024 生成一個可直接出圖、可上門窗表、帶台灣送審參數的鋁窗族（.rfa），並載入目前專案。

不是「一個超級族包所有組合」，而是「一個拓樸一個族、一個尺寸一個型別」（ADR D-001、D-003）。

## 2. 範圍

| 階段 | 內容 | 狀態 |
|---|---|---|
| Phase 0 | 互動示意（configurator-demo.html） | 完成 |
| Phase 1 | 固定尺寸生成器：`generate_window_family` 工具 → C# 生成族 → 載入專案 | 本規格 |
| Phase 2 | Type 層活拉：外框與格線鎖到參考平面，同族複製型別改寬高 | 見 ROADMAP |
| Phase 3 | 配件：紗窗、防墜檢核、窗台板／滴水、分格條、開啟角度、隔音、廠牌系列、方位自動、內倒／直軸 | 見 ROADMAP |

Phase 1 明確不做：巢狀零件族、陣列、可見性參數切換開啟方式、真實鋁材斷面、落地窗（門類別）、轉角窗、非矩形窗。

## 3. 規格模型

```text
整組窗（family）
└─ 格子 grid：rows R（1–3，上→下）× cols C（1–4，左→右，自室外看）
   └─ 格 cell R#C#：sashes[]（1–4 扇）
      └─ 扇 sash：type ∈ {fixed 固定, casement 外推, sliding 橫拉, awning 上懸,
                          hopper 下懸／內倒, sliding_v 垂直上下拉, screen 紗窗}   ← v0.2 新增後三者
                  hinge ∈ {L, R}（僅 casement）
                  swing ∈ {out, in}（casement／awning／hopper；hopper 預設 in＝內倒）
                  track ∈ 整數 0…n−1（sliding／sliding_v／screen；0＝最外軌）   ← v0.2 由 outer/inner 改整數
```

- 排高與樘寬用**比例（%）**描述，實際尺寸由總寬總高與框料尺寸算出。
- 同一格內若全部是 sliding（可加 screen），扇與扇之間水平交疊；全部是 sliding_v（可加 screen），垂直交疊；否則等分。
- 一格內混用「滑動類」與「開啟類」視為無效規格（回錯誤）。滑動類＝sliding、sliding_v、screen；sliding 與 sliding_v 不可同格。
- **三軌橫拉含紗窗**（v0.2，ADR D-009）：軌數＝格內最大 track＋1；screen 扇佔一軌，不計入玻璃面積、有效採光面積與可開口面積；圖面以細虛線表示網面。未指定 track 時，玻璃扇依索引交錯放 0／1 軌、screen 放最內軌。
- **下懸／內倒**（hopper）：鉸鏈在下緣；swing＝in 為內倒（台灣廁所高窗、氣窗常用）。IFC OperationType＝BOTTOMHUNG。
- **垂直上下拉**（sliding_v）：交疊邏輯沿垂直軸，扇高 = (格高 + (n−1)·交疊) / n；IFC OperationType＝SLIDINGVERTICAL。
- 百葉、凸窗、轉角窗、折疊窗**不進格子模型**，另立生成器（ADR D-009，ROADMAP Phase 5）。

## 4. 參數分層（ADR D-001）

| 層 | 參數 | 誰改、何時 | Revit 對應 |
|---|---|---|---|
| 生成時（拓樸） | R、C、每格扇數、每扇 type／hinge／track | 建族時決定；換組合＝換型別或重生 | 族名與型別名的一部分；不做活參數 |
| Type 參數 | 總寬 W、總高 H、排高比、樘寬比、外框寬、中框寬、扇框寬、框深、玻璃厚、交疊量、MO 每側預留 | BIM Manager 依鋁窗廠系列設定 | Phase 1 寫入為型別參數值（幾何不跟動）；Phase 2 綁參考平面 |
| Instance 參數 | 窗台高、框位置偏移、外切透、內切透、開啟角度（Phase 3） | 同事放窗時依現場調 | 窗台高＝Revit 內建 Sill Height；其餘 Phase 1 為固定值＋型別參數紀錄，Phase 2 起活動 |

門窗表邏輯：族＝拓樸、型別＝尺寸＝門窗表型號（W1、W2…），數量由型別統計。

## 5. 幾何規則

單位：規格 JSON 用 cm；C# 內部轉 Revit 內部單位（ft）。

| 項目 | 規則 |
|---|---|
| 外框 | 矩形實體，正面寬 `frameOuter`，深 `frameDepth` |
| 中框 | 樘與樘之間直料、排與排之間橫料，寬 `mullion`，深同外框 |
| 扇框 | 每扇矩形框，正面寬 `sash`；固定扇用 `sash × 0.5`（壓條） |
| 玻璃 | 扇框內矩形薄板，厚 `glass_mm`，置於框深中心 |
| 橫拉交疊 | 預設＝扇框寬（ADR D-005）；扇寬 = (格寬 + (n−1)·交疊) / n；外軌扇在室外側軌道 |
| 框位置 | 框深中心相對牆心偏移 `frameOffsetFromWallCentre`（+ 向室外） |
| MO 切透 | 空心擠出：寬高＝框外緣每側加 `mo.gapPerSide`；深度自外飾板外再外切 `cutBeyondExterior`、自內飾板內再內切 `cutBeyondInterior`（ADR D-006）；族設定「載入時以空心切割」 |
| 座標系 | 沿用 Metric Window.rft：原點在寬度中心、窗台高 0；室外方向以樣板實測為準並寫成常數 |

### 預設值（查證等級）

| 參數 | 預設 | 來源 |
|---|---|---|
| 框深 | **7 cm**（可選 10） | 事務所慣用 YKK AP（Alex 2026-10-31 拍板）：台灣 YRB-A70 框深 70 mm、A100 為 100 mm（https://www.ykkap.com.tw/pdt01-01.aspx 及型錄 PDF 第 18 頁斷面）；見 `research/ykkap-frames.md` |
| 外框寬 / 中框寬 / 扇框寬 | 4.5 / 4.4 / 5 cm | **未查證**估值。YRB-A70 型錄斷面讀到 2.5／54／40／54／2.5 mm 尺寸串，構件對應未確認（型錄為純影像）；待人工看斷面圖後回填 |
| 玻璃溝槽 | 16–42 mm | YKK AP YRB-A（pdt01-01） |
| 玻璃 | 8 mm 單層（膠合 5+5 為替代） | 裝修媒體 |
| 交疊 | ＝扇框寬 | 事務所慣例（Alex 2026-09-14 拍板） |
| 窗台高 | 90 cm | 媒體；建技規 §45 特定類組 ≥110 cm（10 層以上 120） |
| MO 每側預留 | 2.5 cm | 示意值，**未查證** |
| 氣密／水密／耐風壓 | 8 級／35 級／280 kgf/m² | CNS 11527／11528／11526，大同型錄印證。YKK AP YRB-A 型錄標示 2 級／750 或 1500 Pa／3600 Pa（單位制不同，換算未查證，預設暫不改） |

## 6. 圖面表示（ADR D-004）

| | 粗略（1:100） | 中等（1:50） | 細緻（1:20） |
|---|---|---|---|
| 立面 | 外框單線、中框與分扇單線交到框、開啟符號 | 框料雙線、扇框、玻璃、符號 | 框料實厚、扇框內線、玻璃線、交疊標註 |
| 平面 | 三線窗符號，不畫開啟 | 框料矩形、玻璃線、外推 30° **實線**示意 | 加內外軌、交疊尺寸 |
| 剖面 | 框外緣矩形＋玻璃線 | 框料矩形、扇框 | 斷面、軌道、鉸鏈位置 |

符號慣例（拍板）：
- 外推扇立面三角形：頂點在**鉸鏈側**，且頂到**扇框外緣的實際鉸鏈位置**；底邊在把手側扇框外緣兩角。
- 上懸：頂點在上（鉸鏈側）。
- 橫拉：扇中央水平箭頭指向滑動方向；兩扇時相向。
- 外推扇平面：30° 實線自鉸鏈點畫出，加虛線開啟弧；粗略不畫。

實作方式：符號線（symbolic curve）依詳細程度設可見性；模型實體依詳細程度設可見性。不用可見性參數。

## 7. 族內參數（明細表用）

共用參數檔：`MCP/Resources/shared-parameters/universal-window.txt`（Phase 1 建立，之後所有窗族從同一份挑，避免 GUID 衝突）。

| 參數 | 型別 | 說明 |
|---|---|---|
| UW_排數、UW_樘數、UW_扇數、UW_可開扇數 | Integer | 由生成器填 |
| UW_開啟方式 | Text | 例：`R1: F / R2: CL,CR` |
| UW_玻璃面積、UW_有效採光面積、UW_可開口面積 | Area | 依 demo 公式；採光扣 FL+75 cm 以下 |
| UW_MO寬、UW_MO高 | Length | 框外緣加預留 |
| UW_氣密等級、UW_水密等級、UW_耐風壓 | Text／Number | 預設值可改 |
| UW_玻璃種類、UW_防火時效、UW_廠牌系列 | Text | 留空給使用者 |
| UW_Uaf、UW_SF | Number | 節能用，留空 |

IFC：型別參數 `IfcExportAs = IfcWindow`，`PartitioningType` 依 R×C 對應；每扇 OperationType 記在 UW_開啟方式（Phase 1 不做逐扇 IFC 屬性集）。

## 8. MCP 工具介面（Phase 1）

工具名：`generate_window_family`

輸入（與 demo JSON 同形，僅列必填）：

```json
{
  "familyName": "UW-上固定下外推-2樘",
  "typeName": "240x180",
  "generation": { "rows": 2, "cols": 2, "cells": [[{"id":"R1C1","sashes":[{"type":"fixed"}]}, ...]] },
  "typeParams": { "frameOuter": 4.5, "mullion": 4.4, "sash": 5, "frameDepth": 8, "glass_mm": 8, "meetingRailOverlap_mm": 50 },
  "instanceParams": { "width": 240, "height": 180, "sillHeight": 90, "rowRatios": [35,65], "colRatios": [50,50],
                      "frameOffsetFromWallCentre": 0,
                      "mo": { "gapPerSide": 2.5, "cutBeyondExterior": 5, "cutBeyondInterior": 5 } },
  "symbols": { "apexAtHinge": true, "planSwingAngle": 30 },
  "outputDir": "<選填，預設 %APPDATA%\\RevitMCP\\families>",
  "loadIntoProject": true
}
```

輸出：`{ familyPath, familyName, typeName, loaded, familyId, symbolId, derived:{ sashCount, openableCount, glassArea_m2, daylightArea_m2, openableArea_m2, moWidth, moHeight }, warnings[] }`

錯誤：規格無效（格內混用橫拉）、樣板找不到、尺寸算出負值、載入衝突（同名族已存在 → 回報並提供 overwrite 旗標）。

## 9. 驗收條件（Phase 1）

1. `dotnet build -c Release.R24 MCP/RevitMCP.csproj` 通過；`npm run build` 通過；新工具出現在 `registerRevitTools()`，計數同步到 CLAUDE.md／README／README.zh-TW／DOCUMENT_AUDIENCE_INVENTORY 與 docs/BIM_MCP 公開頁（main 分支實測 181 → 182；README.en 與 PROJECT_INTRO 在 main 不存在），`verify-qaqc.ps1 -SkipBuild -SkipDeploy` 通過。
2. 佈局數學（排高、樘寬、扇寬、交疊、面積）有純 C# 單元測試（MCP.Tests），與 demo JSON 的 derived 值一致到 0.1 cm／0.001 m²。
3. 在 Revit 2024 空白專案：demo 六個範例各生成一次，皆能載入並放到 15 cm 牆上；平面、立面、剖面在三個詳細程度各截圖一張，符號與 §6 一致。
4. 門窗表：新增門窗表列出 UW_ 參數，數量正確；同族兩個型別分開統計。
5. MO：放到「RC 15 + 外飾板 3 + 內飾板 2」的三層測試組合，空心切割套用後三層都被切穿。
6. 無未解決警告；族檔 < 2 MB。

## 10. 未查證與待拍板

- 外框寬、中框寬、扇框寬、MO 預留：估值，待事務所鋁窗廠斷面。
- 建築物節約能源設計技術規範公式 6／7 全文未逐字查證。
- CNS 等級是否為建照送審必要欄位：未查證。
- Revit 2024 是否已有 enum 型參數：未查證（Phase 1 不依賴）。
- 樣板 Metric Window.rft 的室外方向與參考平面命名：實作時實測後回填本節。

## 11. 入口架構（v0.2，ADR D-008）

| 入口 | 給誰 | 依賴 | 主要功能 |
|---|---|---|---|
| Revit add-in（ribbon「萬用窗」面板＋對話框） | 不用 AI 的同事 | 只要 add-in 載入；**不需開 MCP 服務、不需 AI** | 選窗型生成（格子預覽，等同 configurator-demo）、從 Excel 門窗表批次生成、窗型庫管理 |
| MCP 工具 `generate_window_family` | AI 客戶端 | MCP 服務開啟 | 自然語言轉規格、競圖快速變體、放窗後切割飾板（Phase 3）、送審檢核（Phase 4） |
| 共用 | — | — | `MCP/Core/WindowFamily/` 生成引擎與規格 JSON；兩個入口都只做「組規格 → 呼叫引擎」，不各自畫幾何 |

依據：`research/addin-market.md`（UniversalDW、id:door&window 等同類產品全為純介面；開源 Revit MCP 專案無生成族功能；ArchSmarterRevitMcpServer 的 Core／Server／Addin 分層）。
