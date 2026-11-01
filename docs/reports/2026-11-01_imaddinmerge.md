# imaddinmerge：Revit 外掛合併版（網頁配對 + 上傳到 Issue Manager）

日期：2026-11-01　分支：imaddinmerge（起點 imsheetsonly 2439c80，merge grading-workflow-v2 1a112f3，未 push、未安裝）

## 輸出資料夾
`C:\Project\REVIT_MCP_study\.worktrees\imaddinmerge\MCP\bin\Release.R24\`（RevitMCP.dll，Release.R24 = Revit 2024）

## 驗收
- `dotnet build MCP/RevitMCP.csproj -c Release.R24`：0 錯誤（只有既有的 nullable 警告）。注意 `-c Release` 會因 NuGet 的 `-1.*` 版本字串失敗，須用 `Release.R24`。
- `dotnet test MCP.Tests`：通過 792、失敗 0、略過 2（總計 794）。
- DLL 檢查（反射＋字串）：存在 `WebPairCommand`、`WebRevokeCommand`、`UploadToIssueManagerCommand`、`ViewPositionsResult`／`ViewPositionsMath`、`ViewSyncMath`、`CameraChangePush`、`viewsync_apply_temp`、`viewsync_open_view`、功能區按鈕代號 `MCPWebPair`、`UploadToIssueManager`。
  - 反射 GetTypes 因缺 Revit API 無法載入含 Revit 型別的指令類別，故指令類別名以 metadata 字串確認；尚未在 Revit 內實機驗。

## 衝突處理（9 檔）
| 檔案 | 處理 |
|---|---|
| MCP/Core/CommandExecutor.cs | 兩邊換行不同（imsheetsonly=LF、grading=CRLF），正規化後三方合併；viewsync_* 指令、瀏覽器白名單檢查與上傳／材質／視圖範圍等指令都保留，無重複 case。結果為 LF。 |
| MCP/Application.cs | 「Issue Manager 面板」與「網頁配對／撤銷」按鈕兩者都在；其餘衝突取 imsheetsonly 側（切換連線按鈕、ToggleIcon）。 |
| MCP/Core/SocketService.cs | 單連線鎖定（_activeSocket）與瀏覽器通道（BrowserConnection）並存；補回 `CloseSocketQuietlyAsync`（瀏覽器通道使用）。 |
| ServiceSettings.cs | ExclusiveLock、AllowedOrigins、PairedWebClients 三者並存。 |
| RevitMCP.Tests.csproj | IssueManager 與 ViewSync 的 Compile 項目並存。 |
| CameraKeyPlanService.LiveUpdate.cs／CommandExecutor.CameraManager.cs | 取 grading 側（相機變動推送 CameraChangePush、BuildCameraList 抽出）。 |
| MCP-Server/src/tools/index.ts | 取 imsheetsonly 側（DIAGNOSTIC_TOOLS 已含 cameraTools，full／architect 已含 cameraManagerTools）；grading 的 CAMERA_TOOLS 迴圈捨棄，故 mep／structural／fire-safety 少 cameraManagerTools（與合併前 imsheetsonly 相同）。 |
| CHANGELOG.md | 兩邊條目都保留。 |

## 未驗證
- 未在 Revit 內開啟，功能區兩個面板的按鈕實際出現、兩功能實跑，待主線實機確認。
- MCP-Server 未重新 build／測試（只動 index.ts）。
