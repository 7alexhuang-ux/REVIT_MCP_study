# 整地工作流 v2：三鈕任務流＋A0 單張總比較（C-00c）設計

> 2026-07-06 與使用者以視覺化討論頁兩輪腦力激盪後拍板。本設計**取代** `2026-07-05-grading-phase5-presentation-drawings-design.md` 的 5C/5D 排版方案（該檔 5A/5B 的演算法修正與熱區/範圍線工具不受影響，均已實作）；原「一案一張 A1」流程保留改名為「方案成套圖」，供方案定案後出單案細圖。

## 拍板決策（2026-07-06）

| 事項 | 決議 |
|---|---|
| 按鈕流程 | **案乙三鈕任務流**：新整地方案（對話框）→ 方案總覽 → 比較出圖 |
| 比較圖紙 | **C-00c：A0 橫式（1189×841）單張總比較**，五列＝3D／坡度熱區／正剖面／剖面透視／明細表，一欄一方案 |
| 明細表欄位 | 基本組（挖方、填方、淨土方、挖填總量、最大挖深/填高、擾動面積）＋**鬆實方三本帳**（運土鬆方、所需自然方、實質淨土方；無係數時欄位顯示「—」）；六級坡面積佔比本輪不做 |
| Offset 10m 測試方案 | **刪除**（還原地形、移出登記簿）——使用者已同意破壞性操作 |
| 方案命名 | 自動帶「中文模式＋輸入參數」（如「外延 5m」「放坡 1:2」「投影直貼」），對話框可改；名稱經 `GradingNames.SanitizeElementName` 消毒；參數值變動時自動名即時跟著變（使用者改過名則不再覆蓋） |
| 介面語言 | 模式一律中英對照顯示：投影直貼 Footprint／外延漸變 Offset／放坡銜接 Slope；參數欄位提供常用選項下拉＋自由輸入（可編輯 ComboBox），不得只給固定值 |
| 剖面切線 | 預設自動：沿場地最大高差方向、過控制樓板組合質心；三方案共用同一條切線（可比性）；參數可覆寫 |
| Footprint 定位 | 進比較圖，標「理想對照：垂直擋土、不可施工」 |
| 圖紙編號 | 比較圖紙 GRD-C01 起；成套圖 GRD-A01 起；圖紙加「整地」分類參數（脫離瀏覽器「???」群組） |
| Ribbon 圖示 | 四風格提案由 Codex 生成中，按鈕定案後另輪挑選套用 |
| W3 複合邊坡 | 獨立 TDD 循環，分三段：**W3-1 挖填雙坡率**（邊界點依挖/填自動套用坡率，對話框「統一坡率／挖填分坡」切換，方案名自動組「放坡 挖1:n 填1:m」、登記簿存雙參數）→ **W3-2 臨路邊例外**（點選特定樓板邊指定例外坡率，區段交界坡率漸變混合）→ **W3-3 平台 bench**（本測試基地最大挖深 4.68m 未達設平台高度，後排）。坡率常用選項（挖 1:1～1:1.5、填 1:1.5～1:2）為工程慣例值，W3-1 實作時須查證水保技術規範挖/填邊坡條文對齊。**與 V2 的先後順序待使用者拍板**（建議 V2 先、W3-1 緊接） |

## 背景：本設計要解的三個實測痛點

1. **參數寫死**：現況「Offset外延/放坡1:2」按鈕不能調距離與坡率，使用者實際流程是「做三個方案並調整」。
2. **進度不可見**：登記簿已完整記錄每個方案，但沒有 UI 能看「我做到哪、數字多少」。
3. **排圖重疊**：`place_views_on_sheet` 只放座標不設比例，實機圖紙全部疊在一起（2026-07-06 截圖實證）。

## 元件設計

### W2-A 新整地方案對話框（WPF，Revit 2024+）

- 觸發：ribbon「新整地方案」；前置檢查選取集合（1 Toposolid＋≥1 Floor，不合格 TaskDialog 引導）。
- 欄位：
  - 模式 radio，**中英對照**：投影直貼 Footprint／外延漸變 Offset／放坡銜接 Slope。
  - 外延距離（僅外延啟用）：**可編輯 ComboBox**，常用選項 3／5／10／15／20 m，可自由輸入任意正數。
  - 坡率 1:n（僅放坡啟用）：**可編輯 ComboBox**，常用選項 1:1（陡）／1:1.5／1:2（常用）／1:3（緩），可自由輸入任意 1:n。
  - 方案名稱：依「中文模式＋目前輸入值」即時自動組（外延 5m／放坡 1:2／投影直貼），使用者可改；一旦手動改過即停止自動覆蓋。
  - （選配摺疊）鬆方係數/壓實係數成對。
- 驗證：距離>0、坡率 n>0，非法值紅框＋不可執行。
- 行為：組 `grade_toposolid_to_floors` 參數經既有 `CommandExecutor.ExecuteCommand` 執行；成功後 TaskDialog 摘要 CUT/FILL/警告，問「繼續做下一個方案？」（是→保留選取重開對話框；否→關閉）。
- 不新增演算法；純 UI 殼。

### W2-B 方案總覽視窗（WPF，非模態）

- 讀登記簿（`ReadSchemeRecords`）填 DataGrid：方案名、模式/參數、挖方、填方、淨土方、挖填總量、最大挖深/填高、擾動面積、狀態（設計地形存在與否）。
- 動作列：開啟方案視圖（activate SchemeViewId）、還原方案（既有 `restore_grading_scheme`）、刪除方案（新命令，見 W2-D）、匯出 Excel（既有 `export_grading_comparison`）、比較出圖（勾選列→W2-C）。
- 非模態（ModelessWindow＋ExternalEvent），使用者可邊看模型邊操作。

### W2-C 比較出圖 `create_grading_comparison_sheet`（新 MCP 工具＋ribbon 鈕共用）

流程（逐方案容錯，沿用 presentation 模式）：

1. **指標回寫共用參數**：把登記簿指標寫到設計地形的專案共用參數（`RevitMCP方案名`、`RevitMCP挖方`、`RevitMCP填方`、`RevitMCP淨土方`、`RevitMCP挖填總量`、`RevitMCP最大挖深`、`RevitMCP最大填高`、`RevitMCP擾動面積`、鬆實方三欄）。共用參數檔隨外掛佈署。
2. **視圖確保**：每方案——3D 方案視圖（既有）、坡度熱區視圖（既有，含範圍線）、正剖面（`create_section_view` 沿共同切線）、剖面透視（新：3D 視圖＋section box 貼合切線帶、透視或等角剖開）。
3. **明細表**：原生 `ViewSchedule`（Toposolid 類別，過濾 `RevitMCP方案名` 有值），欄位即上表；`ScheduleSheetInstance` 放圖紙底列。
4. **排版引擎（修根因）**：`place_views_on_sheet` 升級——每格依「格框可用尺寸 ÷ 視圖模型範圍」自動計算並寫入視圖比例（圖形視圖）、置中放 viewport；明細表與圖例另走 ScheduleSheetInstance／Legend 路徑。格陣：first column 列標籤（Legend 視圖或文字註記），之後一欄一方案；五列比例 1.1 : 1.1 : 1 : 1 : 0.7。
5. **圖框**：`get_titleblocks` 找 A0；無 A0 圖框→明確回報引導載入（不硬出）。
6. **PDF**：既有 `export_sheets_pdf`。

參數：`designToposolidIds`（選，預設登記簿全部）、`sectionLineOverride`（選）、`export`、`outputPath`、`titleBlockId`。

### W2-D 刪除方案 `remove_grading_scheme`（新，小命令）

- 行為：還原原地形階段設定（重用 restore 的還原路徑）→ 刪設計地形副本＋方案視圖（含熱區複本視圖）→ 移除登記簿記錄。
- 破壞性：MCP 呼叫端與總覽視窗都要確認語句；回應列出實際刪除的元素 ID。
- 首個用例：刪除本次驗證的 Offset 10m。

### 收尾與同步

- ribbon：整地圖說面板改三鈕（新整地方案／方案總覽／比較出圖）；舊三顆模式鈕移除（案乙決議）；「一鍵出圖」改名「方案成套圖」移入下拉（SplitButton）或暫時保留第四鈕——實作時以面板寬度取捨，預設 SplitButton。
- MCP server：`grading-tools.ts` 加 `create_grading_comparison_sheet`、`remove_grading_scheme` 包裝（維持外層包裝、內層 index.ts 計數 96 口徑，實作時以 `verify-qaqc.ps1` 驗證）。
- 文件：CLAUDE.md 工具計數口徑確認、`domain/grading-slope-classification.md` 不變、log 記錄。

## 測試與驗收

1. **NUnit 純邏輯**：格位排版計算（格框尺寸→視圖比例）、指標→共用參數映射、剖面切線自動選線（最大高差方向）、挖填總量/淨土方計算。
2. **TS schema**：新工具存在、參數型別、profile 過濾。
3. **實機（強制，Revit 2024＋TOPO_example）**：
   - 三鈕流全程：新方案×3（各模式調參數）→ 總覽數字與登記簿一致 → 比較出圖產出 A0 單張＋PDF。
   - 圖紙驗收：五列四欄不重疊、比例可讀、明細表數字＝登記簿數字。
   - `remove_grading_scheme` 刪 Offset 10m：地形還原、登記簿 3 筆、無孤兒視圖。
4. 建置 R24/R23 零錯誤；`verify-qaqc.ps1 -SkipBuild -SkipDeploy` 通過（既有債除外）。

## 建置順序（子階段，各自實機驗收）

| 子階段 | 交付 | 驗收點 |
|---|---|---|
| V2-1 | 排版引擎修正（自動比例）＋剖面/剖透視圖組合 | 舊 presentation 路徑重跑不疊圖 |
| V2-2 | 共用參數回寫＋原生明細表＋C-00c 圖紙 `create_grading_comparison_sheet` | A0 單張成品 |
| V2-3 | `remove_grading_scheme`＋刪 Offset 10m | 登記簿清潔 |
| V2-4 | WPF 對話框＋總覽視窗＋ribbon 改三鈕 | 三鈕流全程實機 |
| V2-5 | 圖示套用（另輪挑選）＋文件/QA/日誌同步 | QA 綠 |

## 風險與開放項

- **A0 圖框**：TOPO_example 目前圖框未盤點；V2-2 起手先 `get_titleblocks`，無 A0 則請使用者載入（Revit 內建 A0 公制圖框可用）。
- **共用參數**：需綁定 Toposolid 類別；跨專案重用時外掛自帶 `.txt` 共用參數檔，首次執行自動綁定。
- **剖面透視**：Revit 透視圖放圖紙有限制（透視 viewport 需鎖定裁剪）；若透視不可排，退等角剖切（isometric＋section box），實作時實機定案並回報。
- **非模態總覽**：Revit API 呼叫須經 ExternalEvent 排隊，視窗內全部動作走既有 `ExternalEventManager`。
