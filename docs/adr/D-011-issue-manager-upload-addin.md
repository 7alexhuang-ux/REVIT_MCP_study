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

---

## 審查後修正（2026-10-25，commit `fba7a60`）

> 獨立安全審查（opus）指出五個必修問題，逐項自行確認屬實後修正。以下決策取代上文中與之衝突的敘述（「後果」第一項的 `Dispatcher.Invoke(Background)` 巢狀訊息迴圈已移除）。

**決策**

10. **登出／切換帳號必須真的撤銷伺服器端授權，失敗要如實告知。** access 已過期或被 401 時，改用 `refresh_token` 走同一個 `POST /api/addin/token/revoke`（`addin-auth.md` §4.4，該端點本來就支援）。`LogoutAsync` 回傳 `LogoutOutcome`（`Revoked`／`NothingToRevoke`／`ServerRevokeFailed`）；失敗時本機登入資料仍刪除，視窗顯示「伺服器端撤銷未完成」並指引到網站「帳號 › 已授權的外掛」手動撤銷。登出整段持有 refresh 鎖，避免並行輪替改掉要撤銷的權杖。
11. **只執行絕對路徑的 `node.exe`。** 裸名 `node` 在 `UseShellExecute=false` 時會先在目前工作目錄找；改為標準安裝位置＋PATH 內的絕對目錄，並排除目前工作目錄與開啟中專案資料夾之下的檔案。往上層目錄找 `tools\` 的開發用路徑預設關閉，只有 `REVITMCP_DEV_TOOLS=1` 時啟用。（既有的 `CommandExecutor.Walkthrough.cs` `FindNodeExecutable` 先試裸名 `node`，是同型問題；它不屬於本案（走動展示的深度圖匯出），**本次未修**，已列入交班單待辦。）
12. **等待 ExternalEvent 的行為抽成可測試的 `QueuedApiThreadRunner`。** 排隊中可取消、`Raise()` 回傳值檢查、開始執行前逾時 120 秒＋白話訊息；放棄的工作標記後不會被稍後的 Revit 佇列處理執行。靜態 `_running` 旗標在所有結束路徑釋放。
13. **關閉視窗要清理未完成的 bundle**（詢問或預設放棄；先刪本機檔再盡力通知伺服器），並在外掛啟動時清除超過 3 天的舊暫存。
14. **巢狀訊息迴圈：選「每個 Revit 步驟各自一次 ExternalEvent」，不用 `EnableWindow`。**

**14 的取捨**

| 方案 | 優點 | 缺點 |
|---|---|---|
| A. 每步一次 ExternalEvent（採用） | 步驟之間回到正常訊息迴圈，視窗用一般 WPF 方式更新、取消即時生效（步驟邊界）；沒有任何巢狀 `Dispatcher.Invoke`，不會在 API 呼叫中途處理使用者輸入；不需要碰 Win32 | 步驟之間 Revit 主視窗可以操作：使用者若在匯出中途編輯模型，PDF 與 GLB 可能對應不同狀態（只檢查作用中文件沒換）；GLB 這一步仍會佔住 Revit 執行緒、視窗此時會顯示「沒有回應」 |
| B. 維持單一 ExternalEvent，匯出期間 `EnableWindow(revitHwnd,false)`，`finally` 還原 | 使用者無法操作 Revit 主視窗，模型狀態一致 | 仍保留巢狀訊息迴圈；停用的主視窗會讓 Revit 本身的對話框／焦點行為變得難預測；`finally` 前當掉就留下停用的主視窗；要取得 Revit 主視窗 handle 與還原都需實機驗證 |
| C. A＋B | 兩者優點 | 改動最大、風險最高 |

採用 A 的理由：它移除問題的根源（巢狀訊息迴圈），且不引入「Revit 主視窗被我們停用」這種失敗後果更重的新風險。A 的缺點用視窗提示（「請勿切換文件、編輯模型或關閉專案」）緩解，並列為**需實機觀察**項（交班單）。若實機證明使用者在步驟之間編輯模型造成問題，再於 A 之上加 B。

**其餘加固**：loopback `ExclusiveAddressUse` 與讀取逾時（`Task.WhenAny`／`Task.Delay`）、續傳前重新計算磁碟 sha256、PDF 串流掃描與頁數不符中止、登入中取消的文案、功能區面板建立包 `try/catch`。

**後果**

- `IExportProgress.Pump` 移除；`IApiThreadRunner.RunAsync` 增加 `CancellationToken`；`BundleExporter` 新增 `ExportAsync`（同步 `Export` 保留給測試與無 Revit 情境）；`ExternalEventManager` 新增 `TryExecuteCommand`（回傳 `Raise()` 結果，`ExecuteCommand` 行為不變）。
- `MCP.Tests` 429 → 479（+50）。

---

## 追記：對齊網站端（imv4a）版本包 API 的實際合約（2026-10-25，分支 v4b2）

**背景**：網站端 4A 實作後與客戶端副本有 16 項差異（網站端 `docs/plans/evidence/imv4a/handoff.md` §4）。以下是需要取捨的決定；其餘（201、錯誤碼訊息、kind、limits 等）是單純對齊。

**決策**

15. **finalize 驗證失敗＝bundle 作廢，重試一律重建 bundle。** 網站端為了「暫存已清」與 Supabase 搬移不可還原，400／413 當下就把 bundle 標成 `failed`。客戶端不再對舊 bundle 重送 finalize（只會得到 409）；`UploadFlow.ResumeAsync` 在沒有可用 bundle 時用已匯出的本機檔重新 `POST /bundles` 並整批重傳。**內容被拒**（`invalid_manifest`、`page_count_mismatch`、`invalid_pdf`、413）不重試：同樣的檔案只會再被拒，直接放棄暫存比讓使用者白等一輪上傳好。取捨：`invalid_file`／`checksum_mismatch` 仍給重試（可能是分段在傳輸中壞掉）。
16. **finalize 之後不能取消，如實告知。** 網站端 `DELETE` 已 finalize 回 409 `already_finalized`（不刪版本）。客戶端不在 finalize 開始後鎖住取消按鈕（取消仍可能在 finalize 還沒做完時生效），而是以 `DELETE` 的回應決定訊息：200 才說「已取消」，409 就說「已經上傳完成，無法取消；請到網站刪除」。
17. **沒有狀態端點，用「重送 finalize」代替查詢。** 網站端沒有 `GET /bundles/:id`（`revit-addin-export.md` §5.2「實作回寫」）。finalize 冪等＋`finalize_in_progress` 409 足以表達狀態：等不到回應（逾時、502／504）就隔 5 秒重送，上限 10 分鐘（與網站端「認領逾 10 分鐘視為當掉」一致）。連線被拒／中斷（非逾時）不輪詢。
18. **只勾視圖不能上傳。** 伺服器要求 PDF 與 GLB 至少一個；視圖只是附屬資料。在勾選視窗就停用並說明，不等匯出完才被拒。
19. **schema 以網站端權威檔為準，客戶端不自行放寬或收緊。** `docs/schemas/web-authoritative/` 放原樣副本，兩邊測試比對差異清單只有 `$id`／`description`。`ManifestBuilder` 對顯示用文字截斷、對警告超量收尾、對無法截斷者提前失敗。
20. **伺服器回傳的上傳網址是 `localhost` 而 Issue Manager 不在本機 → 中止**（`ADDIN_PUBLIC_URL` 沒設）；Bearer 仍只送給 Issue Manager 自己的主機。

**後果**

- `AddinApiClient` 新增 `finalizeHttp` 參數與 `DeleteBundleDetailedAsync`（`BundleDeleteOutcome`）；`UploadFlow` 新增 `FinalizePollInterval`／`FinalizeMaxWait`；`MCP.Tests` 479 → 547。
- 未做：finalize 開始後鎖住取消按鈕（見 16）；網站端若日後加 `GET /bundles/:id`，輪詢可改成查詢。
