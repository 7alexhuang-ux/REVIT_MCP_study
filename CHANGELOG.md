# 變更日誌 (Changelog)

所有重要的專案變更都會記錄在此檔案中。

格式基於 [Keep a Changelog](https://keepachangelog.com/zh-TW/1.0.0/)。

---

## [Unreleased] — universal-window 分支

### 🆕 新增 — 萬用窗族生成器（Phase 1，進行中）

- **文件**：`docs/universal-window/SPEC.md`（規格 v0.1）、`docs/universal-window/ROADMAP.md`、`docs/universal-window/configurator-demo.html`（互動示意，Phase 0）、`docs/ADR.md`（D-001～D-007：拓樸生成時決定／幾何程式直接畫／Phase 1 固定尺寸／圖面慣例／交疊＝扇框寬／MO 空心切透／worktree 分支）。
- **工具**：`generate_window_family`（規格 JSON → Revit 2024 生成 .rfa 並載入專案）。TS：`MCP-Server/src/tools/window-family-tools.ts`（full／architect profile，120 s timeout）；C#：`MCP/Core/WindowFamily/{WindowFamilySpec,WindowLayoutMath,UniversalWindowParameters,WindowFamilyBuilder}.cs`、`MCP/Core/Commands/CommandExecutor.WindowFamily.cs`、共用參數檔 `MCP/Resources/shared-parameters/universal-window.txt`。狀態：**已編譯、單元測試 85 pass、未在 Revit 內實測**；工具回傳 `templateProbe`（參考平面名、視圖名、室外方向）供首次實測回填 SPEC §10。
- **安裝腳本**：`scripts/install-addon.ps1` 一併部署共用參數檔到 `%APPDATA%\RevitMCP\shared-parameters\`。
- **計數同步**：Runtime MCP tools 192 → 201（含 main 既有 8 個整地工具未計入的漂移與新增 generate_window_family）。
- **已知未處理**：`revit-tools.ts` 在 full／architect／structural profile 會把 grading 工具重複註冊（既有 bug，另案處理）；ADR D-006 專案端「放窗後自動切割相交飾板」留待 Phase 3。

### 🆕 新增 — 窗型擴充 v0.2 實作（2026-10-31，universal-window 分支）

- **規格模型**：sash type 新增 `hopper`（下懸／內倒，預設內開）、`sliding_v`（垂直上下拉）、`screen`（紗窗）；新增 `swing`（out／in）；`track` 改整數軌序（0＝最外軌），**相容舊 JSON**（`"outer"`→0、`"inner"`→1）。驗證：滑動類不可與開啟類同格、sliding 與 sliding_v 不可同格、screen 不能單獨成格、同軌相鄰衝突。
- **佈局數學**：sliding_v 垂直交疊（扇高 = (格高 + (n−1)·交疊)/n）；紗窗與所罩玻璃扇同尺寸、佔自己的軌道，不計玻璃／有效採光／可開口面積與扇數（另計 `screenCount`）；多軌深度位置（≥3 軌框深等分）。框深預設 8 → 7 cm（ADR D-010）。
- **Builder**：下懸／垂直上下拉／紗窗的立面與平面符號，內開改虛線、平面開啟線畫向室內側；IFC OperationType BOTTOMHUNG／SLIDINGVERTICAL，紗窗不輸出玻璃扇屬性；`UW_開啟方式` 用代號 H／V／SC。**未在 Revit 內實測**。
- **MCP schema**：`window-family-tools.ts` 同步 type enum、`swing`、`track` 整數；工具數不變（仍 201）。
- **demo**：`configurator-demo.html` 加入三種新窗型、三個範例（三軌含紗窗、垂直上下拉、下懸內倒氣窗）、軌序與方向控制，JSON 輸出為 v0.2 形狀。
- **測試**：MCP.Tests 新增 16 個（總計 101）、MCP-Server 新增 1 個。

### 🆕 新增 — 萬用窗 add-in 入口（2026-10-31，universal-window 分支）

- **ribbon**：「萬用窗」面板＋「產生窗族」按鈕（`MCP/Application.cs`；圖示由 `UniversalWindowIcon` 以 WPF 向量繪製，專案原本沒有圖示資源）。**不需開啟 MCP 服務、不需 AI**（ADR D-008）。
- **命令與對話框**：`MCP/Commands/UniversalWindowCommand.cs`（IExternalCommand，modal）、`MCP/Core/WindowFamily/UniversalWindowDialog.cs`（純程式碼 WPF，與既有 LogViewerWindow 同風格）。10 個範例、排／樘／格子小地圖、每扇型式／鉸鏈／開向／軌序、框料與交疊、族名型別名自動命名、輸出資料夾與載入／覆蓋、即時立面線框預覽、引擎驗證錯誤直接顯示、結果面板（族路徑、扇數、玻璃／採光／可開口面積、warnings）。
- **共用引擎**：新增 `WindowFamilyService.Generate`（目標專案檢查＋建族＋載入），MCP 的 `GenerateWindowFamily` 改走同一個方法，行為不變；add-in 在 IExternalCommand 的 Revit API 內容裡直接呼叫，不經 WebSocket／ExternalEventManager。
- **規格組裝**：`WindowSpecBuilder`（純 C#）保存 UI 狀態並組出與 MCP 工具同形的規格 JSON；比例自動補滿 100、型別名 `{W}x{H}`、族名 `UW-R{R}C{C}-…`、改排樘數時保留既有格。
- **測試**：MCP.Tests 新增 13 個（總計 114）。對話框另以暫時的 WPF 煙霧工具（不入庫）建立視窗、逐一套用 10 個範例、改格子／輸入錯誤／產生流程，並截圖檢查預覽。
- **文件**：新增 `docs/universal-window/ADDIN-GUIDE.md`（human，已登錄 `DOCUMENT_AUDIENCE_INVENTORY.md`）；SPEC §11、ROADMAP Phase 1.5 同步。
- **未驗證**：未在 Revit 內實機測試（對話框 modal 行為、生成後載入、ribbon 圖示呈現）。工具計數不變（add-in 入口不是 MCP 工具）。

### 📐 規格 v0.2（2026-10-31）

- `docs/universal-window/SPEC.md` v0.2：窗型擴充（下懸／內倒、垂直上下拉、紗窗扇、整數軌序）、框深預設 7 cm（YKK AP YRB-A70）、§11 入口架構。
- `docs/ADR.md` 新增 D-008（add-in 與 MCP 各自獨立、共用引擎）、D-009（窗型擴充範圍）、D-010（框深 7 cm）。
- 新增研究：`docs/universal-window/research/{addin-market,window-taxonomy,ykkap-frames}.md`。

### 📚 調查（2026-09-13～14，見 SPEC §10 未查證清單）

- 社群族群標準（NBS BIM Object Standard、Autodesk Content Style Guide、IFC4.3 IfcWindow 枚舉）、台灣送審三軌（節能 §310、EEWH、建照 §41/§43/§79/§110＋消防 §188–189）、踩坑 20 條、台灣鋁窗框料預設值（框深 8/10 cm 有型錄來源，框寬類估值未查證）。

---

## [1.6.0] - 2026-07-31

### 🆕 新增 — MCP 2026-07-28 升級（Path-A dual-era + WP-F Apps）

- **工具標註**：全 167 工具補上 `title` + `readOnlyHint`/`destructiveHint`（`MCP-Server/src/tools/annotations.ts` 於 `registerRevitTools()` 中央注入）；77 唯讀 / 90 寫入 / 2 破壞性（`delete_element`、`dedup_detail_elements_in_view`）。純 metadata，舊客戶端會忽略。
- **`tools/list` 決定性排序**：依工具名稱 codepoint 排序（locale-independent），每次啟動/呼叫順序一致。
- **MCP Apps 互動式 UI（extension `io.modelcontextprotocol/ui`）**：第一個互動工具 —— `detect_clashes` 的 clash viewer（碰撞摘要 + 清單、「在 Revit 上色」→ `colorize_clashes`、逐列「定位」→ `zoom_to_element`）。以低階 `Server` 手動接線（`resources` capability + `ListResources`/`ReadResource`，MIME `text/html;profile=mcp-app`）；UI 為單一自我包含 HTML（esbuild）。需宿主支援該 extension 才會 render，否則忽略 `_meta.ui`、工具照常回傳文字。
- **SDK 升級**：`@modelcontextprotocol/sdk` 1.22 → 1.30（協定 2025-11-25，仍保留 2025-06-18 → dual-era 相容）；新增相依 `@modelcontextprotocol/ext-apps`、`zod`，devDep `esbuild`。

### 🔧 CI / QAQC

- **`.github/workflows/check-pr.yml`**：knowledge-only 限制改為僅套用外部 fork PR（`head.repo.fork == true`）；維護者同倉庫 PR 可修改程式碼與設定。
- **`scripts/verify-qaqc.ps1`**：新增 **Phase 9**（9-1 工具 annotation 覆蓋 + destructive 白名單；9-2 Apps `ui://` 資源解析 / MIME / 自我包含），與既有 8 phase 併存。

### ⏳ 延後（等官方 SDK 釋出 2026-07-28 協定）

- Stateless 連線、`server/discover`、`resultType`、Tasks core、HTTP / OAuth 傳輸與授權 —— 由每日雲端 watcher 追蹤上游 SDK，條件成立即接手（需本機 build + live-Revit red→green）。

### 📌 版號說明

- 本版將 npm 套件版號對齊專案版號線：CHANGELOG 前一版為 1.5.2，npm 套件先前停在 1.0.2，本次一併追上至 **1.6.0**（`package.json` / `server.json` / npm 三處同步）。

### 🙏 遷移

- 詳見 `docs/MIGRATION_GUIDE.md`「MCP 2026-07-28 雙時代相容升級」章節。

---

## [1.5.2] - 2026-07-16

### 🐛 Bug 修正（PowerShell 部署腳本）

- **`scripts/setup.ps1`**：修正 PowerShell 5.1 下 `$ErrorActionPreference = "Stop"` 搭配 native 指令（dotnet/npm/winget/net）`2>&1` 重導向時，會把 stderr 上的一般進度/警告行升級成終止例外的問題，導致 `dotnet build` 實際 0 錯誤卻誤報「編譯例外」。新增 `Invoke-ExternalCommand` helper，在 8 處 native 呼叫點暫時將 EAP 切回 Continue，`$LASTEXITCODE` 判斷邏輯不變。由 @ray92chiu-png 於 issue #89 精準定位並提供修法（commit `1b5a71e`，保留原作者署名）。
- **`scripts/release-port.ps1`**：橫向掃描發現 `net stop`/`net start http` 同樣踩到這顆地雷，套用同款修法（commit `33f1a44`）。

### 🙏 致謝

- 感謝首次貢獻者 @ray92chiu-png（邱仁）回報 issue #89 並附完整重現步驟與 diff，詳見 `docs/BIM_MCP/reference/contributors.html`。

---

## [1.5.1] - 2026-03-09

### 🐛 Bug 修正（C# — 需重新編譯）

- **`CommandExecutor.cs`**：修正 4 個問題 — null reference 風險、port 8966→8964 統一、catch 範圍過寬、重複初始化
- **`ExteriorWallOpeningChecker.cs`**：修正外牆開口條件判斷邏輯
- **`RevitMCP.csproj`**：改用 Nice3point.Revit.Sdk 6.1.0，統一支援 Revit 2022–2026

### 🆕 新增

- **`RevitCompatibility.cs`**：跨版本相容性輔助層（新增檔案）
- **`CLAUDE.md`**：Claude Code 專用上下文指引
- **`docs/MIGRATION_GUIDE.md`**：fork 成員升級遷移指南
- **`教材/`**：完整公開 8 堂課教材、投影片、延伸閱讀資源
  - 新增 `05-Skill遷移實戰篇.md`（Agent Skill 架構實作教學）

###  文件整理

- **合併後刪除**：ANNOUNCEMENT.md → CHANGELOG.md、MCP_Server_Setup_Guide.md → README.md、ARCHITECTURE.md → README.md
- **搬移**：QUICK_TEST.md → docs/、4 個學習筆記 → 教材/
- **刪除**：mission.md、Domain_to_Skill_Migration_Guide.md
- **更新**：README.md 新增導覽表、README.en.md 支援 Revit 2025/2026、DOCS_STRUCTURE.md 重寫

###  成員需執行的動作

**C# 程式碼有更新，需重新編譯 DLL：**

```powershell
cd "專案路徑/MCP"
dotnet build -c Release.R22   # Revit 2022
dotnet build -c Release.R23   # Revit 2023
dotnet build -c Release.R24   # Revit 2024
dotnet build -c Release.R25   # Revit 2025
dotnet build -c Release.R26   # Revit 2026
```

複製 DLL → Revit Addins 資料夾 → 重啟 Revit。

**MCP Server 無變動**，不需要 `npm install` 或 `npm run build`。

---

## [1.5.0] - 2026-03-04

### ✨ 新功能

#### 整合 4 個全新 MCP 工具
- **`unjoin_wall_joins` & `rejoin_wall_joins`**：解決牆體上色時的幾何接合干擾，確保視覺化上色流程準確無誤
- **`get_room_daylight_info`**：居室採光分析功能（由 DAVIT 貢獻），AI 可直接讀取並分析房間採光比例
- **`get_view_templates`**：視圖樣版查詢工具（由 Alex Huang 貢獻），支援樣版設置、篩選器與隱藏品類的整併分析

### 🔧 改進

#### 通訊標準統一
- **Port 統一為 8964**：修正部分配置文件中 port 為 8966 的不一致問題。全專案（C#、TypeScript、config.json）均統一使用 8964

#### 功能映射優化
- **完整對比驗證**：TypeScript 端工具定義與 C# 端指令處理已完全對應（37 個工具）

###  重要提醒

本次更新涉及 C# 後端邏輯與 TypeScript 工具架構的大幅變動，需重新編譯部署（參考 README.md 的部署步驟）。

---

## [1.4.1] - 2024-12-18

### 🐛 Bug 修正

#### 目錄重構後的安裝腳本路徑錯誤
- **問題**：12/17 進行目錄重構（`MCP/MCP/` → `MCP/`），但安裝腳本 `install-addon-bom.ps1` 未同步更新，導致找不到 DLL
- **症狀**：
  - 執行安裝時出現「✗ 錯誤：找不到 RevitMCP.dll」
  - Revit 啟動時無法載入 MCP Plugin
- **修正**：更新所有路徑參照為單層 `MCP\` 結構
  - `MCP\bin\Release.2024\RevitMCP.dll`
  - `MCP\RevitMCP.2024.addin`
  - `MCP\bin\Release\Newtonsoft.Json.dll`
- **影響範圍**：所有透過 `install-addon-bom.ps1` 執行安裝的用戶

### 🔧 改進

- **新增 `verify-installation.ps1`**：安裝前驗證工具
  - 檢查目錄結構是否正確
  - 檢查 DLL 是否已建置
  - 檢查安裝腳本路徑是否正確
  - 提供下一步操作建議

- **更新 `GEMINI.md`**：AI 助手智能部署指南
  - 新增環境偵測協定（Revit 版本 + AI Client）
  - 提供版本特定的建置命令對照表
  - 說明 Revit 2024 的 56 個警告是正常的（API 相容性）
  - 加入常見問題處理邏輯
  - 協助 AI 助手為使用者生成客製化部署指令

###  修改檔案

| 檔案 | 變更 |
|------|------|
| `scripts/install-addon-bom.ps1` | 修正 3 處路徑：DLL、.addin、Newtonsoft.Json |
| `scripts/verify-installation.ps1` | 新增安裝驗證工具 |

###  重要提醒

**目錄結構已統一為單層 `MCP\` 結構，所有路徑參照請確認正確：**
-  正確：`MCP\bin\Release.2024\RevitMCP.dll`
-  錯誤：`MCP\MCP\bin\Release.2024\RevitMCP.dll`

---

## [1.4.0] - 2025-12-14

### ✨ 新功能

#### 容積檢討工具
- **新增 `get_rooms_by_level`**：取得指定樓層的所有房間清單
  - 回傳：房間名稱、編號、面積
  - 回傳：中心點座標（公釐）
  - 回傳：資料完整度統計（有/無名稱的房間數量）
  - 可用於容積檢討作業

### 🐛 Bug 修正

#### 補齊 C# 實作
- **補齊 `create_floor`**：建立樓板（原本只有 TypeScript 定義，缺少 C# 實作）
- **補齊 `modify_element_parameter`**：修改元素參數
- **補齊 `create_door`**：建立門
- **補齊 `create_window`**：建立窗

### 📚 文件更新

- 更新 README.md 工具清單，補齊遺漏的工具說明
- 新增 `domain/` 資料夾，放置容積檢討開發文件

###  修改檔案

| 檔案 | 變更 |
|------|------|
| `MCP-Server/src/tools/revit-tools.ts` | 新增 get_rooms_by_level 工具定義 |
| `MCP/MCP/Core/CommandExecutor.cs` | 新增 5 個命令實作 |
| `README.md` | 更新工具清單 |
| `domain/README.md` | 新增容積檢討開發計劃 |

---

## [1.3.0] - 2025-12-12

### ✨ 新功能

#### 家具工具擴展
- **新增 `get_furniture_types`**：取得專案中已載入的家具類型清單
  - 回傳：類型名稱、族群名稱、是否已啟用
  - 支援類別篩選（如：椅子、桌子、床）
- **新增 `place_furniture`**：在指定位置放置家具
  - 支援指定座標（公釐）
  - 支援指定樓層
  - 支援旋轉角度（度）
- **新增 `get_room_info`**：取得房間詳細資訊
  - 回傳：房間名稱、編號、面積
  - 回傳：中心點座標（公釐）
  - 回傳：邊界範圍 BoundingBox（MinX, MinY, MaxX, MaxY）
  - 可用於智慧放置家具

### 📚 文件更新

#### Git Pull 更新提醒
- **新增**：在 README.md 和 README.en.md 最前面加入警告區塊
- **內容**：提醒使用者 git pull 後如有 C# 程式碼變更，需重新編譯並部署 DLL
- **包含**：更新類型對照表（C#/TypeScript/設定檔各自的處理方式）

###  修改檔案

| 檔案 | 變更 |
|------|------|
| `MCP-Server/src/tools/revit-tools.ts` | 新增 3 個工具定義（#14-#16） |
| `MCP/MCP/Core/CommandExecutor.cs` | 新增 3 個命令實作 + Architecture 命名空間 |
| `README.md` | 新增 Git Pull 更新提醒區塊 |
| `README.en.md` | 新增 Git Pull 更新提醒區塊（英文版） |

---

## [1.2.0] - 2025-12-11

### ✨ 新功能

#### Grid 與 Column 工具擴展
- **新增 `get_all_grids`**：取得專案中所有網格線（Grid）資訊
  - 回傳：Grid 名稱、方向（水平/垂直）、起點/終點座標（公釐）
  - 可用於計算網格交會點，配合 `create_column` 使用
- **新增 `get_column_types`**：取得專案中可用的柱類型
  - 回傳：類型名稱、族群名稱、尺寸資訊（寬x深）
  - 支援材質篩選（如：混凝土、鋼）
- **新增 `create_column`**：在指定位置建立柱子
  - 支援指定底部/頂部樓層
  - 支援指定柱類型名稱
  - 座標使用公釐，內部自動轉換為 Revit 英尺單位

###  修改檔案

| 檔案 | 變更 |
|------|------|
| `MCP-Server/src/tools/revit-tools.ts` | 新增 3 個工具定義 |
| `MCP/MCP/Core/CommandExecutor.cs` | 新增 3 個命令實作 |

---

## [1.1.0] - 2025-12-11

### 🐛 Bug 修正

#### JSON 屬性名稱大小寫不匹配問題
- **問題**：MCP Server 發送的 JSON 使用 camelCase（`commandName`、`parameters`、`requestId`），但 Revit Add-in (C#) 期待 PascalCase（`CommandName`、`Parameters`、`RequestId`）
- **症狀**：透過 Gemini CLI 或其他 MCP 客戶端連線時出現「命令執行逾時」錯誤
- **修正**：修改 `MCP-Server/src/socket.ts`，將發送的 JSON 屬性名稱改為 PascalCase
- **影響範圍**：所有使用 MCP Server 的客戶端（Gemini CLI、Claude Desktop、VS Code Copilot、Antigravity）

### 📚 文件更新

#### Gemini CLI MCP 設定方式錯誤修正
- **問題**：原文件說明使用 `gemini --config gemini_mcp_config.json` 參數，但 Gemini CLI 實際上使用 `~/.gemini/settings.json` 設定 MCP
- **修正**：更新 README.md 和 README.en.md，說明正確的設定方式
- **新增內容**：
  - 設定檔位置：`%USERPROFILE%\.gemini\settings.json`
  - 如何加入 `mcpServers` 區塊
  - 使用 `/mcp list` 驗證連接

#### PowerShell 執行原則錯誤解決方法
- **問題**：Windows 預設停用 PowerShell 腳本執行，導致 `npm install` 失敗
- **新增**：在文件中加入 `Set-ExecutionPolicy` 解決方法

#### Git Clone 首次設定指南
- **問題**：透過 `git clone` 取得專案的使用者無法直接使用，因為以下檔案不包含在儲存庫中：
  - `MCP-Server/build/` - 需要執行 `npm run build` 產生
  - `MCP-Server/node_modules/` - 需要執行 `npm install` 產生
  - `MCP/MCP/bin/` - 需要編譯或下載 Release
- **新增**：在 README 中加入「 透過 Git Clone 的首次設定」區塊

#### 設定檔硬編碼路徑問題
- **問題**：`gemini_mcp_config.json` 和 `claude_desktop_config.json` 使用硬編碼的使用者路徑
- **修正**：改為佔位符路徑 `【請修改此路徑】`，提醒使用者自行修改
- **保留**：`.vscode/mcp.json` 使用 `${workspaceFolder}` 變數，無需修改

---

## [1.0.0] - 2025-12-10

### ✨ 新功能

- 初始版本發布
- Revit Add-in (C#) 實作
- MCP Server (Node.js/TypeScript) 實作
- 支援 10 種 Revit 操作工具
- 支援多種 AI 平台（Claude Desktop、Gemini CLI、VS Code Copilot、Antigravity）

---

## 提交歷史對照

| Commit | 說明 |
|--------|------|
| `0dcdc54` | 修正 JSON 屬性名稱為 PascalCase |
| `348775d` | 修正 Gemini CLI MCP 設定說明 |
| `2e75839` | 新增 Git Clone 首次設定指南 |
| `ed7ee1b` | 新增英文版文件支援 |
| `a223ede` | 更新 WebSocket 說明與技術補充附錄 |
| `d088f06` | 新增安裝指令稿 |
