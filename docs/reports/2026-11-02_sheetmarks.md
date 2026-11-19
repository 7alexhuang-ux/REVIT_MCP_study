# 圖紙版面資料：讓圖上的剖面線、點位能對到 3D（imsheetmarks，2026-11-02）

分支：外掛 `imsheetmarks`（REVIT_MCP_study，起點 `7ee56a0`＝imaddinproj）；網站 `imsheetmarks`（issue-manager，`C:\Project\issue-manager\.wt_sheetmarks`，起點 main `9690fca2`）。兩邊都沒有 push、沒有部署、沒有動 Revit 裝的 DLL、沒有碰 Revit、沒有連正式庫。

## 一句話

外掛現在會把「每張圖紙上每個視口放在哪裡、多大、比例幾分之幾、有沒有旋轉」以及「平面圖上看得到哪些剖面／立面標記線」一起交給網站；網站收下來存好，並給圖面頁一支 API 加一組函式：**點 PDF 上的一個位置，就知道點到哪條剖面線（要開哪個 3D 視角），或點在平面上的哪個模型座標**。**這次只做資料，沒有畫面**（畫面在圖面頁重排那條線，imdrawsplit）。

## 改了什麼

### 外掛（`C:\Project\REVIT_MCP_study\.worktrees\imsheetmarks`，commit `e8963f4`）

- `MCP/Core/IssueManager/SheetMarkMath.cs`（新，純計算、有單元測試）：紙張座標（公釐）與模型座標（glTF 公尺）互轉、視口旋轉、線段與視口外框相交、剖面標記是否在這張平面上看得到、組出 JSON、併入位置檔。
- `MCP/Core/IssueManager/RevitSheetLayoutReader.cs`（新，只在 Revit 內編譯）：唯讀取圖紙外框、視口外框／中心／比例／旋轉、視圖座標系、平面切高、裁剪框，以及剖面／立面的剖切線。
- `BundleExporter`：位置檔寫完後多一個「匯出圖紙版面」步驟（每 4 張圖紙一批，批次之間回到 Revit 訊息迴圈、可取消），把 `sheetLayout` 併進 `view_positions.json`；失敗只加警告、位置檔維持原樣照常上傳；超過位置檔 4 MiB 上限的安全值就捨棄版面並警告。視口外框重用量測步驟的結果，單一視口的圖紙現在也會量一次。
- `ExportRequest.IncludeSheetLayout`（預設開；視窗沒有新開關）。
- 版本格式：**manifest 不動**（schema 嚴格，新欄位會被舊網站整包 400）；版號放在區塊裡的 `formatVersion: 1`。
- 測試：新增 25 個（轉換、旋轉、反函數、線段、標記、JSON、併入、分批、失敗不擋上傳、不勾選、雜湊對得上併入後的檔案）；既有兩個測試的步驟計數隨新增的步驟更新。`MCP.Tests` 全部 819 通過、0 失敗（2 個先前就略過的）。

### 網站（`C:\Project\issue-manager\.wt_sheetmarks`，commit `bf525086`）

- 規格 `C:\Project\issue-manager\.wt_sheetmarks\docs\spec\revit-addin-export.md` §10、ADR **D-910**（查過所有 worktree：最大 D-905 在 imdrawsplit 未提交，故取 910）、CHANGELOG。
- migration `0179_sheet_layouts`（`when` 1793400709000；查過所有分支最大是 0178／1793400708000）：只新增表 `sheet_layouts`（一張圖紙一筆 jsonb，圖紙刪除連動刪除）。`check:journal` 通過。
- finalize：讀位置檔的 `sheetLayout`，逐張／視口／標記驗證，壞的略過並警告（`sheet_layout_invalid／_unsupported_version／_skipped:N／_unknown_sheet:N`，白話在三語 messages）；與新版本圖紙同一交易寫入；發行 S／提送 R 複製圖紙時一併複製。
- API：`GET /api/projects/:id/sets/:setId/sheets/:sheetId/layout`，回版面並附每個視口／標記對應的 3D 視圖（`target.modelViewId`）；沒有版面回 `{layout:null}`。
- 給圖面頁：`src/lib/sheet-layout.ts`（`hitTestPdfPoint`＝點擊 → 命中標記或視口內的模型座標點；`modelPointToPdfNorm`＝3D → 圖面；`overlayShapes`＝畫疊圖用）與 `src/lib/use-sheet-layout.ts`（`useSheetLayout(projectId,setId,sheetId)` → `{status, layout, hit, overlay}`）。頁面長寬比和圖紙差超過 2% 時 `hit` 回 `null`（不硬轉）。
- 測試：`sheet-layout.test.ts`（25，與外掛同一個範例）、`addin-bundle-finalize-layout.test.ts`（5）、`sheet-layouts.db.test.ts`（6，真實 postgres：寫入、提交交易、發行複製、讀取附 target、刪除連動；既有 `addin-bundle-attach.db.test.ts` 9 項回歸也過）。`tsc`、`eslint --max-warnings 0` 無錯，`vitest` 5160 通過，`next build` 成功。唯一失敗的 `letter-threads/action.test.ts`「我方是受文者時…」與本案無關（在另一個 worktree 同樣失敗，先前就有）。

## 新 DLL

`C:\Project\REVIT_MCP_study\.worktrees\imsheetmarks\MCP\bin\Release.R24\RevitMCP.dll`
MD5 `B609F93C8DC214BE9973D0454C2DCA73`，4,764,160 位元組（11/02 23:41 建，`dotnet build MCP/RevitMCP.csproj -c Release.R24` 成功、0 錯誤）。**沒有安裝。**

## 安裝與上線順序

1. **網站先上**：套 migration `0179_sheet_layouts`，部署（同時是相容收資料；沒有新外掛時行為完全不變）。需 Alex 同意後由主線做。
2. **再裝外掛**（Revit 關掉才能覆蓋 DLL）：備份 `%APPDATA%\Autodesk\Revit\Addins\2024\RevitMCP\RevitMCP.dll` → 用上面的新 DLL 覆蓋。其餘 DLL 不用動。
3. **重新上傳 CPC 一次**（Add-Ins › Issue Manager › 上傳到 Issue Manager，勾位置／模型）：完成摘要不該多出「圖紙版面」警告；打開 `…/sets/<版本>/sheets/<圖紙>/layout`（登入後）看是否回非 null，並把 `overlayShapes` 疊在 PDF 上目視比對 2～3 張平面圖的剖面標記位置。
4. 新舊混用都不會壞：新網站＋舊外掛＝沒有版面（照舊）；舊網站＋新外掛＝多的資料被忽略。只有「新網站收到新外掛上傳的版本」才有版面，舊版本要重傳。

## 還沒驗證的（外掛的 Revit 讀值沒有在 Revit 實測，依指示不碰 Revit）

1. **`ViewSheet.Outline` 是否就是 PDF 一頁的範圍**：Revit 匯出 PDF 若加邊界或縮放，位置會整體偏移；網站只有長寬比的保險（偏移量偵測不到）。
2. **視口 ↔ 視圖座標的對應規則**（視口中心對應 `View.Outline` 中心、`Viewport.Rotation` 正負號）：沿用外掛既有 `ViewportPosition` 在 2026-05-01 視覺實測推出的規則，但旋轉過的視口、視圖方向不是正東的平面沒有單獨實測。
3. **剖面標記線是用剖面視圖的裁剪框推的，不是讀 Revit 的標記元素**：標記被隱藏、剖面線在平面上被截短、立面標記（只有一個符號）時，可能和圖上畫的不一樣；「這張平面看得到」是幾何近似（線段與視口外框相交＋切高在剖面垂直範圍內）。
4. **耗時**：外框已重用量測步驟，但單一視口的圖紙原本不量、現在要量；大專案要看一次實測的秒數。

## 替 Alex 留的決定

- 版面資料目前一律匯出（`IncludeSheetLayout` 預設開）。如果實測發現太慢，可以改成預設關、在視窗加勾選；程式已有旗標，只缺視窗上的開關。
- 圖面頁（imdrawsplit）接 hook 時要處理：標記的 `target` 為 `null`（該剖面視圖這次沒勾選或沒匯出）時，顯示「尚無 3D 對應」。
