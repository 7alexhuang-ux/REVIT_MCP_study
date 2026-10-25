# D-011：「上傳到 Issue Manager」走獨立按鈕＋既有指令分派，登入用瀏覽器授權碼＋PKCE＋DPAPI

> 文件性質：架構決策紀錄（人類與 AI 共用）。日期：2026-10-25。分支 `v4b`。
> 依據 Issue Manager repo 的 `docs/spec/revit-addin-export.md`、`addin-auth.md`、`sheet-sets.md`（D-594～D-597、D-604，Alex 已拍板）。本輪僅建置與離線測試，未在 Revit 實跑。

**背景**

Issue Manager 要讓使用者在 Revit 內勾選圖紙／視圖／模型，一次匯出並上傳成網站上的一個工作版。外掛端需要功能區按鈕、勾選視窗、登入與上傳客戶端；網站端（4A）尚未實作，所以客戶端依規格合約寫成介面，並用本機假伺服器驗證。

**決策**

1. **不經 MCP WebSocket。** 按鈕是 `IExternalCommand`，匯出經 `ExternalEventManager` 佇列在 Revit API 執行緒執行。匯出本身呼叫既有指令（`export_sheets_pdf`、`list_design_options`、`export_design_options`、`export_named_views`），透過 `CommandExecutor.ExecuteCommand` 分派，不另寫匯出碼，也不是 CLAUDE.md 禁止的臨時 WebSocket 腳本。MCP 服務關閉時可用，也不與「一次一個 MCP 連線」限制衝突。
2. **純邏輯與 Revit 分層。** 登入、API 客戶端、ViewModel、manifest、匯出流程、上傳器、WPF 視窗都不引用 Revit API，連結進 `MCP.Tests` 單元測試；只有讀 Revit 清單、經分派器匯出、工作階段三個檔案依賴 Revit。視窗以注入的服務運作，所以能在測試中建構並檢查綁定。
3. **GLB 一律走 `export_design_options`。** 它用 `TransactionGroup` 回復，不留下 `WEB_COMPARE` 之類的持久視圖；`export_web_compare` 單視圖路徑會建立／修改視圖、改 section box 與遠裁剪，不適合在使用者模型上自動執行。有選項分件時用既有 `tools/merge-option-glbs.mjs`（Node）合併；沒有分件直接用 `main_model.glb`。
4. **「含設計選項」未勾 ＝ 主模型＋各集合的主要選項（`isPrimary`）。** 只匯出主模型會漏掉主要選項內的元素（它們在目前組態是可見的）；`export_design_options` 的 `optionKeys` 參數正好能限定。這是對規格「選項：含設計選項」的解讀，待 Alex 確認。
5. **報告 `complete:false` 就中止模型步驟**，不上傳缺漏的模型（LESSONS L-15）；使用者可取消勾選模型先上傳圖紙。
6. **登入：授權碼＋PKCE＋loopback。** loopback 用 `TcpListener(127.0.0.1:0)` 而非 `HttpListener`（不需 URL ACL／系統管理員，專案內 `SocketService` 已為此踩過 port 修復的坑）。refresh token 用 DPAPI（P/Invoke `crypt32`，因為 Revit 2025+ 的 .NET 8 預設不含 `ProtectedData`）；refresh 單鎖序列化以免觸發伺服器的重用偵測。
7. **上傳器自帶續傳狀態。** `upload_state.json`（只存 sha256／段數，不含權杖與網址）讓「重試」跳過已完成的檔案，已完成但檔案內容變了則重傳。
8. **PDF 頁序＝傳入圖紙順序，manifest 的 `pageNumber` 用同一順序（圖號自然排序）**。Revit 合併 PDF 時是否遵守傳入順序**未查證**，列為實機驗證項；外掛另用 PDF 頁數估計做提醒（只當警告）。
9. **視圖位置檔不做**（V5）。manifest 省略 `files.viewPositions`。

**理由**

- 沿用已通過編譯／離線測試的匯出指令，減少新增 Revit API 風險面，也讓「匯出什麼」只有一份實作。
- 純邏輯分層讓規格的離線驗收（X01～X07）都能自動測，Revit 專屬的部分縮到最小，實機清單才短而明確。
- 授權碼＋PKCE＋loopback 是規格（D-596）；其餘細節（TcpListener、P/Invoke、單鎖）都是為了避免已知的 Windows／輪替陷阱。

**後果**

- 匯出期間 Revit 單執行緒被佔住；視窗靠 `Dispatcher.Invoke(Background)` 在步驟之間重繪與處理「取消」，這會讓 Revit 主視窗在匯出期間可能仍收到訊息——**需實機確認**，視窗上已提示不要操作 Revit。
- 與網站端的對齊工作：schema 副本、列舉、簽名網址數量（建立 bundle 時尚不知各檔大小）、PDF 頁序。見 [ISSUE_MANAGER_UPLOAD.md](../ISSUE_MANAGER_UPLOAD.md)「與網站端 schema 的對齊」與交班單。
- 新增 115 個 NUnit 測試與兩個 Node 測試檔；`MCP.Tests` 因此改為 `UseWPF`、引用 Newtonsoft.Json。
- 只有 Release.R24 驗證可建置；其他年份既有檔案本來就有建置錯誤（見功能說明文件）。
