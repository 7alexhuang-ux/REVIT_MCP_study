# 規格 — REVIT_MCP_study

> 2026-10-24 建立（範本由 project_docs_init.py 產生，內容依 CLAUDE.md 與各工作流文件歸納）。
> 各工作流的細部合約不在此重複，只列指向；與本檔衝突時以該工作流文件為準。

## 1. 這個專案解決什麼、給誰用

Revit MCP 讓 AI 用 Model Context Protocol 呼叫 Autodesk Revit 的功能：AI Client → MCP-Server（Node／TypeScript，stdio）→ WebSocket（localhost:8964）→ Revit 外掛（C#）→ Revit API。使用者是台灣建築設計與 BIM 從業者；domain 檔（`domain/*.md`）是人與 AI 共讀的 SOP。

## 2. 核心不變式（違反就是 bug）

- 一個 csproj（`MCP/RevitMCP.csproj`）、一個 addin 清單（`MCP/RevitMCP.addin`）、port 8964；不新增版本專屬專案檔。
- 修改模型一律在 Transaction 內且可還原；Revit API 一律經 `ExternalEventManager` 回到 UI 執行緒。
- 不繞過 MCP：不寫臨時 WebSocket 腳本直送 Revit（`tools/revit-command.mjs` 走正式 `RevitSocketClient` 與工具註冊）。
- Revit 端 WebSocket 一次只接一個 MCP 連線。
- 既有指令的預設行為不因新增參數而改變。
- 回答中的 ID、名稱、數量必須出自本回合的工具回傳。

## 3. 功能規格（指向）

| 工作流 | 規格位置 |
|---|---|
| 網頁比對匯出 `export_web_compare` | [WEB_COMPARE_EXPORT.md](WEB_COMPARE_EXPORT.md)、[ADR D-009](adr/D-009-web-compare-heavy-elements.md) |
| 設計選項 `list_design_options`／`export_design_options`、命名 3D 視圖 `export_named_views` | [WEB_COMPARE_EXPORT.md](WEB_COMPARE_EXPORT.md)「設計選項與命名視圖」、[ADR D-010](adr/D-010-design-options-and-named-views.md) |
| 文件與連結 `open_document`／`list_open_documents`／`close_document`／`list_links`／`reload_links`、opt-in 自動啟動 | [WEB_COMPARE_EXPORT.md](WEB_COMPARE_EXPORT.md)「無人操作前置」 |
| 上傳到 Issue Manager（功能區按鈕、勾選視窗、版本包、瀏覽器授權登入；不經 MCP、不是 MCP 工具） | [ISSUE_MANAGER_UPLOAD.md](ISSUE_MANAGER_UPLOAD.md)、[ADR D-011](adr/D-011-issue-manager-upload-addin.md)、[bundle-manifest.schema.json](schemas/bundle-manifest.schema.json) |
| 各 BIM 工作流 | `domain/*.md`（觸發字表見 CLAUDE.md） |

資料合約摘要（網頁端依此讀取）：

- GLB 節點 `extras.designOption = {setKey,setName,optionKey,optionName,isPrimary}`（key 為 Revit UniqueId）；不屬於任何選項的節點不帶此欄位。
- `design_options.json = {sets:[{setKey,setName,options:[{optionKey,optionName,isPrimary,elementCount}]}],generatedAt,revitDoc}`。
- `named_views.json = {originOffsetMeters,views:[{viewId,uniqueId,name,isPerspective,displayStyle,viewerMode,gltf,sectionBox,revit}],warnings}`；座標轉換同 `camera.json`（點 `(x−ox, z−oz, −(y−oy))`，方向 `(x, z, −y)`，target＝眼點沿視線 10 m）。
- 安全規則：`open_document` 對工作共享中央檔搭配 `detach:false` 一律拒絕；`close_document` 有未存檔修改且 `discard:false` 一律拒絕；不預設另存新檔。
- Issue Manager 上傳：版本包 `manifest.json` 為 `schemaVersion 1`（圖號取自 Revit 圖紙號、圖紙與視圖以 UniqueId 對應、檔案附 sha256）；階段必選、**沒有預設值**；權杖（access／refresh）、授權碼、verifier、state 不寫日誌；refresh token 只以 DPAPI 密文落檔；Bearer 只送給 Issue Manager 主機。此功能經 `CommandExecutor.ExecuteCommand` 呼叫既有指令，不是臨時 WebSocket 腳本，也不新增 MCP 工具。

## 4. 驗收原則

- 每一條「完成」都要有實跑證據（測試輸出、畫面、實機回傳）。
- 提交前 `scripts/verify-qaqc.ps1 -SkipBuild -SkipDeploy`；MCP-Server `npm test`；`dotnet test MCP.Tests`。
- 沒有 Revit 可跑的環境只能宣稱「建置與離線測試通過」，不得宣稱實機行為。
