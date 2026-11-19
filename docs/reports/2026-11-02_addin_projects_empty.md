# 上傳視窗「專案」下拉是空的：調查與修正（2026-11-02）

分支：外掛 `imaddinproj`（REVIT_MCP_study，起點 `f4abd77`＝目前裝在 Revit 的那一版）；伺服器 `imaddinproj`（issue-manager，`C:\Project\issue-manager\.wt_addinproj`，起點 main）。兩邊都沒有 push、沒有部署、沒有動 Revit 裝的 DLL。

## 一句話結論

用來調查的材料裡，**找不到「清單一定會是空的」的程式缺陷**；找到的是三個會讓人看到空白下拉、又查不出原因的問題（外掛兩個、伺服器一個）。主線這邊沒法在不碰 Revit 的情況下實際看到下拉裡面有沒有項目，所以下面把「確定的」和「推測的」分開寫。

## 確定的事（有查證）

1. 目前裝在 Revit 的 DLL 就是 `.worktrees\imaddinmerge`（commit `f4abd77`）建出來的：`%APPDATA%\Autodesk\Revit\Addins\2024\RevitMCP\RevitMCP.dll` 的 MD5 `536C28773530CFE6270FE8F0B02E6FC2`，與 imaddinmerge 的 `MCP\bin\Release.R24\RevitMCP.dll`（11/01 23:07）完全相同。`IssueManager` 資料夾在 `f4abd77` 與上一版 `2439c80` 之間沒有差異，所以不是合併弄壞的。
2. 正式站的外掛端點存在：`/api/addin/me`、`/api/addin/projects`、`/api/addin/projects/[id]/models` 不帶權杖都回 401（不是 404），表示新版已部署。
3. 外掛讀專案清單的流程（`UploadWindow.cs` 的 `InitialLoadAsync` → `LoadProjectsAsync` → `AddinApiClient.GetProjectsAsync`）本身正確：視窗開啟時只要已登入就會讀；登入完成後也會讀；解析 `{"projects":[{id,code,name}]}` 與伺服器 `route.ts` 回的格式一致。
4. 日誌（`RevitMCP_20261102.log`）裡完全沒有任何專案讀取的紀錄。原因是**舊版程式在成功時本來就不寫 log**；失敗時 `ShowError` 會寫「上傳視窗：<種類>」，這個也沒出現，所以**沒有發生過任何 API 例外**（沒有 401、逾時、500、格式錯誤）。
5. 伺服器權限判斷：用單元測試重現 HQ＋專案 admin 成員，不論 `PERMS_V2` 開或關、角色權限表 upload_wip 為開，`can(…, "upload_wip")` 都是 true，清單會有 CPC。只有帳號是唯讀（viewer）或權限表把 HQ 的 upload_wip 關掉才會是空的。主線查到正式庫 hq/pm/pl/tm 的 upload_wip 全為 true，所以伺服器端依資料應該回 CPC。

## 看起來合理的原因（推測，未能在 Revit 內證實）

- **下拉沒有預設值、也沒有提示字**：`ComboBox` 沒選任何東西時，關著的時候就是一格空白（階段下拉有「請選擇階段」這個選項，專案下拉沒有）。只要伺服器回的是一個以上的專案、使用者沒有展開下拉，看起來就是「空的」。成功時狀態列被清成空字串，也沒有任何「已讀取 N 個專案」的回饋，所以主線看到的「沒有出現『沒有可上傳…』字樣」與這個情形吻合。
- **重新讀取專案後選取會丟**：`SetProjects` 舊寫法只檢查「舊選取的編號還在不在」，但新清單是全新物件，舊選取物件已不在下拉裡（下拉會顯示空白，VM 卻以為還選著）。
- **伺服器**：`listUploadableProjects` 沒有像網頁的專案清單 `listProjectsForUser` 那樣先補 HQ／PM 的成員資格（新專案要等到網頁打開過一次才會出現），也沒有排除已封存的專案（列出來後選了會 404）。這對 7.alex（已是 CPC 的 admin 成員）不是原因，但會讓別的 HQ／PM 帳號的清單缺專案。

如果修好後下拉展開仍是空的、提示字寫「沒有可上傳的專案」，那就是伺服器真的回了空陣列：請看視窗提示與新的 log（見下），再查該帳號在正式庫的成員資格是否為 admin／staff 且專案未封存。

## 改了什麼

### 外掛（`C:\Project\REVIT_MCP_study\.worktrees\imaddinproj`，分支 `imaddinproj`）

- `MCP/Core/IssueManager/UploadWindow.cs`
  - 專案下拉上蓋一行提示字（不攔截滑鼠）：未登入「請先登入」、讀取中「讀取專案中…」、失敗「專案讀取失敗，請按『重新讀取』」、空清單「沒有可上傳的專案」、有清單但沒選「請選擇專案（共 N 個）」；選了專案就隱藏。
  - 專案列右邊新增「重新讀取」鈕（已登入且沒在忙時可按）。
  - `LoadProjectsAsync`：開始與結束都寫 MCP 日誌（「上傳視窗：開始讀取專案清單。」「…讀取完成，N 個。」「…讀取失敗（例外型別）。」）；成功只有 1 個專案時自動選好；成功多個時狀態列寫「已讀取 N 個可上傳的專案，請從『專案』下拉選一個。」；失敗顯示白話原因並寫 log；登出、登入重來、又按重新讀取時，舊的讀取結果作廢（避免舊結果蓋掉新的）。
- `MCP/Core/IssueManager/ExportSelectionViewModel.cs`：`SetProjects` 換清單後依編號重新選回同一個專案（WPF 換 ItemsSource 會先把選取清成 null，舊寫法會留下不在清單裡的舊物件）。
- `MCP.Tests/IssueManager/UploadWindowTests.cs`：新增 2 個測試（提示字各狀態、重新讀取後選取保留／清除、下拉項目數）。

### 伺服器（`C:\Project\issue-manager\.wt_addinproj`，分支 `imaddinproj`，commit `ef9a4d1e`，未部署）

- `src/db/addin-queries.ts`：`listUploadableProjects` 先呼叫 `ensureOrgMembershipsForUser`（只在 PERMS_V2 開且是 HQ／PM 時補成員資格，有補到才重讀權限使用者），並排除 `archived_at` 不為空的專案。
- `src/db/addin-projects-list.test.ts`：7 個測試（HQ 在旗標開／關都列得到、角色權限表開／關 upload_wip、唯讀帳號為空、新專案補資格後重讀、查詢條件含已封存排除）。

## 測試結果

- 外掛：`dotnet test MCP.Tests/RevitMCP.Tests.csproj --filter IssueManager`：通過 330、失敗 0、略過 2（略過的 2 個是先前就略過的 FindScript 測試）。基準（未加新測試）328 通過。
- 外掛建置：`dotnet build MCP/RevitMCP.csproj -c Release.R24` 成功，0 錯誤。
- 伺服器：新測試 7 個全過；`src/lib/addin*`、`permissions*`、`roles*` 共 15 檔 301 測試全過；`tsc --noEmit` 無錯。

## 沒做／限制

- 沒有在 Revit 內實際操作視窗（Revit 正在匯出 PDF，依指示不碰）。畫面效果（提示字位置、按鈕大小）只在單元測試層驗證，需要安裝後目視。
- 另一個現象「關掉視窗後功能區按鈕反白但不展開」：日誌在 18:50 之後沒有任何新的「開視窗讀取…」紀錄，代表按鈕沒有進到 `UploadToIssueManagerCommand.Execute`（進去的話第一行就會寫 log）。這個時段 Revit 正在處理 PDF 匯出（19:22 `export_sheets_pdf`），Revit 忙碌時功能區按了不會執行是已知行為；程式裡 `_window` 在 `Closed` 事件有清成 null，沒有看到會讓按鈕失效的寫法。這點未能證實，請在 PDF 匯出結束、Revit 閒下來之後再試一次；若仍不開，再看日誌有無「開啟上傳到 Issue Manager 視窗失敗」。
- 伺服器的修正不會影響 7.alex 目前的清單（他本來就有 CPC 的成員資格）；它修的是其他 HQ／PM 帳號與已封存專案。

## 安裝步驟（由主線決定時間；需要先關掉 Revit，DLL 載入中無法覆蓋）

新 DLL：`C:\Project\REVIT_MCP_study\.worktrees\imaddinproj\MCP\bin\Release.R24\RevitMCP.dll`（MD5 `40991B52AA7AE8991171E45C65AA45C5`，4,742,656 位元組，11/02 19:34 建）。

1. 等 Revit 的 PDF 匯出結束、存檔，關閉 Revit 2024。
2. 備份舊檔：複製 `%APPDATA%\Autodesk\Revit\Addins\2024\RevitMCP\RevitMCP.dll` 為 `RevitMCP.dll.bak-20261102-preprojfix`（同資料夾）。
3. 用新 DLL 覆蓋 `%APPDATA%\Autodesk\Revit\Addins\2024\RevitMCP\RevitMCP.dll`。其餘 DLL（ClosedXML、Newtonsoft.Json 等）不用動。
4. 開 Revit、開 AR-CPC.rvt，Add-Ins › Issue Manager › 上傳到 Issue Manager。確認專案列有提示字或已選好的專案；到 `%APPDATA%\RevitMCP\Logs\RevitMCP_<日期>.log` 看有沒有「上傳視窗：專案清單讀取完成，N 個。」
5. 伺服器端修正要等使用者同意後，另行把 issue-manager 的 `imaddinproj` 分支併入 main 再部署（外掛本身不依賴它）。
