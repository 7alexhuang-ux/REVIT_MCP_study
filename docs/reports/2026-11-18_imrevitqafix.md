# 報告：QA/QC 清理（imrevitqafix）

> 日期：2026-11-18。建造者：Claude Sonnet 5.5（8.ape 帳號）。分支 `imrevitqafix`（起點 `6be1f86`，imrevitaddinjumpv）。
> 範圍：只改測試資料字串、一行設定、文件；**沒有動外掛跳轉功能，沒有重新安裝到 Revit，沒有 push／merge**。

## 1. 結論

QA/QC 全綠（PASS 74／FAIL 0／WARN 4）；`dotnet test` R24、R25 各 838 通過、2 略過、0 失敗。N4～N7 與決策紀錄都已處理。

## 2. 做了什麼

| 項目 | 修法（白話） | 證據 |
|---|---|---|
| 舊 QA/QC 失敗 1：`3-4` 寫死帳號名 | `SheetsOnlyAttachTests.cs` 兩處測試字串 `C:\Users\other\…`、`C:\Users\alex\…` 改成 QA 允許的占位寫法 `C:\Users\<OTHER_MACHINE_USER>\…`。測試只比檔名與中央路徑，不依賴帳號，所以行為不變 | 改後 `3-4` PASS；兩個測試仍過 |
| 舊 QA/QC 失敗 2：`7-17` 聚會日期過期 | `docs/BIM_MCP/shared.js` 的 `MEETUP_SIGNUP` 改成 `{ url: "", eventDate: "" }`。這是檔頭註解寫明的做法：沒有下一場時兩欄留空，按鈕自動顯示「目前未開放報名」，**不會再隨時間過期**。下一場聚會把連結與日期成對填回去即可（舊表單連結仍可從 git 歷史找回，已不放在檔內） | `7-17` PASS |
| N4 | `CLAUDE.md:500`：R25 實際輸出 **9** 個 DLL（8 個基本＋`System.Drawing.Common`）。我重編 `Release.R25` 實數 9：ClosedXML、DocumentFormat.OpenXml、ExcelNumberFormat、Irony、Newtonsoft.Json、RevitMCP、SixLabors.Fonts、System.Drawing.Common、XLParser。R26 沒有重編，文字寫明「未重新計數（先前為 8）」，**不杜撰** | 實跑 `dotnet build -c Release.R25` 0 錯誤 |
| N5 | ADR 標題與各處引用改成「ADR D-015（Revit 跳轉）」，區別 camera-manager 另一套 D-001～D-046 編號：`docs/adr/D-015-…md` 標題、`REVIT_LINK.md`、`SPEC.md`、`CHANGELOG.md`、建造者報告 | grep 確認 |
| N6 | 建造者報告測試數 837 → 838（實跑 R24、R25 各一次都是 838／略過 2／失敗 0） | `/tmp` log 末行「通過: 838」 |
| N7 | ADR D-015 第 9 點補 `browserPath`（指定用哪個瀏覽器開預填頁、檔案不存在時改用預設瀏覽器並記日誌），與 `REVIT_LINK.md` §4 一致 | 見 ADR 第 9 點 |
| 決策紀錄 | 新檔 [`docs/decisions/alex-decisions-log.md`](../decisions/alex-decisions-log.md)：從 BACKLOG 抄 2026-11-18 與外掛有關的決定，每條標「補記（決策日期，來源）」；分成 Alex 親答、其他指示、派工者代決、仍待決四段 | 檔案已建立 |

驗收條目不變：驗收者對 `imrevitaddinjumpv` 的歷史報告我**沒有改**（那是他的獨立紀錄）。

## 3. 驗證

- `pwsh` 以 UTF-8 主控台執行 `scripts/verify-qaqc.ps1 -SkipBuild -SkipDeploy`：PASS 74／FAIL 0／WARN 4（WARN 都是既有：3-5 工作樹、被隔離的 `check_sanitary_fixture_requirements`、兩個 rollback-only 探針）。
- 環境備註（不是 repo 缺陷）：Windows PowerShell 5.1 預設主控台編碼會把 node 輸出的中文工具描述弄亂，`7-14` 的「描述可讀」會誤判 FAIL；用 pwsh 7 並把 `[Console]::OutputEncoding` 設成 UTF-8 即過。驗收者先前用的也是「UTF-8 主控台」。這個 worktree 原本沒有 `node_modules`，我跑了 `npm ci` 與 `npm run build`（被 `.gitignore` 排除）。
- `dotnet test MCP.Tests/RevitMCP.Tests.csproj -c Release.R24`、`-c Release.R25`：各 **通過 838、略過 2、失敗 0**。

## 4. 沒做／待 Alex 決定

1. Revit 2024 實機測試：派工者已代決併進 imifcfloorfix（見決策紀錄 C），本件不做。
2. RevitMCP 外掛無簽章的「永遠載入」：仍待 Alex（見決策紀錄 D）。
3. `7-17` 已改成「未開放報名」狀態；若 Alex 要在網站上開放下一場聚會報名，需要給新的表單連結與日期（例：網址 `https://forms.gle/…`、日期 `2026-12-xx`）。

## 5. 給獨立複驗 imrevitqafixv（非 ape）

見 `C:\Temp\grokbot_sessions\imrevitqafix_result.txt` 末段檢查清單。
