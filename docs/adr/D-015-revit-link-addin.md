# ADR D-015（Revit 跳轉）：Revit 跳轉走瀏覽器直連既有 WebSocket、四個唯讀指令＋內部草稿暫存、兩個反向入口共用同一段程式

> 文件性質：架構決策紀錄（人類與 AI 共用）。日期：2026-11-18。分支 `imrevitaddinjump`。
> 依據：Issue Manager（IM）repo `docs/spec/revit-link.md`（IM 端決策 D-950～D-959，Alex 2026-11-18 已拍板六題）。編號 D-015：已掃本 repo 全部分支的 `docs/adr/`，最大為 D-014，D-015 空號。
> 規格：[REVIT_LINK.md](../REVIT_LINK.md)。

## 背景

IM 要讓網頁議題能「跳到 Revit 裡的那個元件」，也要讓人在 Revit 選了元件後「建立議題」或「查這個元件有哪些議題」。外掛已經有網頁 3D 視角聯動（D-710，`viewsync_*`、Origin 白名單、配對碼換 token）。IM 端用假外掛測過合約，外掛端 C# 只有未編譯草稿。

## 決策

1. **沿用既有 WebSocket（localhost:8964）與配對機制，不新增小幫手、不註冊網址協定。** 新增四個瀏覽器可呼叫的**唯讀**指令：`viewsync_open_link`、`viewsync_get_selection`、`viewsync_capture_view`、`viewsync_draft_take`；其他現有指令仍一律拒絕。
2. **`viewsync_draft_put` 只給外掛內部**，不進白名單；草稿只放記憶體、取一次就刪、預設 10 分鐘過期、代碼 ≥128 位元隨機。
3. **兩個反向入口（功能區按鈕、Claude MCP 指令）共用同一段 `OpenIssueDraft(kind)`**，kind 為 `create`（建立議題）或 `lookup`（這個元素有哪些議題）。網頁端只有一條路，不分入口。
4. **按鈕放在既有「Issue Manager」面板**；按下時若 MCP 服務沒開或沒有已配對網頁裝置，不開瀏覽器，改說明怎麼做（網頁要靠 WebSocket 領草稿）。
5. **模型版本不同一律警告、仍跳轉**（Alex Q3）：`model.revision`＝`<NumberOfSaves>:<VersionGUID>`，只比相同與否。要不要擋、要不要只開到圖紙由網頁專案設定決定，經 `revisionPolicy` 參數傳入；外掛不自己決定。
6. **唯讀範圍硬規定**：本功能原始碼不得有 `Transaction`、建立視圖、套用暫時設定、刪除或修改元素；單元測試掃原始碼保證。
7. **同一份原始碼支援 Revit 2024（`ElementId` 32 位元）與 2025+（64 位元）**，一律經 `RevitCompatibility` helper。
8. **IM 網址解析不猜預設值**：`imBaseUrl` → `ISSUE_MANAGER_URL` → `issue-manager.json` → 網頁白名單第一筆；只收 https（本機測試才允許 http 迴圈位址）。
9. 外掛端可調的值放 `%APPDATA%\RevitMCP\revit-link.json`（草稿分鐘、截圖長邊、是否截圖、IM 網址、`browserPath` 指定用哪個瀏覽器開預填頁，檔案不存在時改用預設瀏覽器並記一行日誌）；沒有檔案就全用預設。

## 理由

- 沿用 D-710 不增加新的攻擊面；寫入類指令（建立視圖、套用暫時設定）在白名單裡本來就和本功能無關，用「不得引用」＋測試封死。
- 兩個入口共用一段程式，避免按鈕與 Claude 指令行為分叉。
- 版本警告的決定權放網頁設定，符合 ALEX 規則（欄位屬性由使用者編輯）。

## 後果

- MCP 工具數 +2（`open_issue_draft`、`open_element_issues`）；另併入 grading-workflow-v2 的 `export_family_previews` +1。計數以原始碼 `registerRevitTools()` 實測為準，不照抄別條分支的數字。
- 這個功能的瀏覽器指令會讓網頁切換 Revit 目前視圖與選取（UI 狀態），但不修改模型。
- IFC 換算修正（imifcfloorfix）不在本件範圍；外掛只用 UniqueId。
