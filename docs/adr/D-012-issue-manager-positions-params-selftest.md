# D-012：上傳到 Issue Manager 補上視圖位置與參數／面積表匯出，版本號由網站指派；加一個 opt-in 的實機自測鉤子

> 文件性質：架構決策紀錄（人類與 AI 共用）。日期：2026-11-01。分支 `imrevitaddin`（起點 `merge/upstream-2026-10-30` 併入 `v4b3`）。
> 依據 Issue Manager repo 的 `docs/spec/revit-addin-export.md` §8、ADR D-801～D-805（Alex 2026-10-31 10:33～10:38 版本包決定、2026-11-01 01:46「Revit 一鍵匯出到 IM 是下一個 IM 優先」）。

**背景**

D-011 的外掛端（v4b～v4b3）只匯出 PDF／GLB／3D 視角，視圖位置檔屬 V5 沒做；Issue Manager 的版本包（D-766）要求圖說、模型、面積表共用同一個版本號，且網站端 D-705 已能收平面／立面／剖面位置。

**決策**

1. **視圖位置跟著勾選自動匯出**：勾選的視圖含平面／天花／剖面／立面時，匯出步驟多一步 `export_view_positions`（既有指令，不新增 MCP 工具），原點與 GLB 相同；沒有模型時略過並提醒（網站端沒 GLB 就不匯入位置）。詳圖沒有位置（D-705）。
2. **新增 `params.json`（im-revit-params/1）**：視窗多一個「匯出參數與面積表」勾選（預設勾）。讀值在 `RevitParamsReader`（唯讀、不開 Transaction、不新增 MCP 工具）：樓層高程（扣 GLB 原點的 Z）、門窗牆樓板屋頂天花柱梁樓梯房間的寬高長厚面積體積，以及「房間面積表」。**讀不到的欄位不寫（不補 0、不猜）**；只讀目前開啟的專案（連結模型的構件未納入，會在警告說明）。資料→JSON 的規則在 `ParamsFileBuilder`（純邏輯、有離線測試）；超過 8 MiB 時依「房間→門窗→其他」的順序保留並提醒。
3. **版本號由網站指派**：外掛不產生 `v{n}`（它不知道網站上已有哪些版本）；finalize 回應多 `version`，完成畫面顯示「已上傳為 v14」。
4. **相容**：新外掛連到舊網站時，建立回應沒有 `viewPositions`／`params` 上傳網址 → 事先略過並在 manifest warnings 與完成畫面提醒；舊外掛連新網站照常。
5. **實機自測鉤子 `ImSelfTest`（opt-in）**：只有使用者預先建立 `%USERPROFILE%\.revit-mcp\im-selftest.json` 才啟用（與 autostart 標記同一個模型；外掛不自行建立）。背景計時器每 3 秒看一次標記檔（不依賴 Idling：Idling 只在有使用者操作後才觸發），有標記就改名 `.done` 並把「開始」排進 ExternalEvent，在不點畫面的情況下跑「真實 Revit 讀值＋真實匯出＋真實上傳流程」（`UploadFlow` 與按鈕相同），結果寫進標記指定的結果檔；匯出的小檔另存一份當證據。這是為了讓沒有人坐在 Revit 前時也能做實機驗收；它**不取代**對 ribbon 按鈕與 WPF 視窗的實際點擊驗證。

**理由**

- 沿用既有指令，不增加 MCP 工具數（137 不變，工具計數 gate 不受影響）。
- 版本號的唯一來源只能是網站資料庫（專案鎖內），否則兩個人同時上傳會撞號。
- 自測鉤子比「另寫一支 WebSocket 腳本」更接近真實路徑：用的是同一份 `UploadFlow`、同一個上傳器。

6. **類別鍵用 Revit 內建類別英文名**（`Walls`、`Doors`、`Rooms`…），不用介面語言的顯示名稱：實機（繁中 Revit）第一次匯出得到「牆」「門」，與 params 規格不符，已修正並重跑確認。
7. **三角面預算可用環境變數 `REVITMCP_IM_MAX_TRIANGLES` 調高**（預設仍是 3,000,000，超過會判定匯出不完整而中止）：Snowdon 範例檔主模型 3,000,065 面，剛好超過預設。

**後果**

- 匯出步驟由 4 個增為最多 6 個（PDF → GLB → 命名視圖 → 視圖位置 → 參數），每步一次 ExternalEvent。
- `docs/schemas/bundle-manifest.schema.json` 與 `web-authoritative/` 副本同步 Issue Manager 的新版（多 `files.params`），差異仍只有 `$id` 與 `description`。
- ribbon 按鈕／視窗點擊、使用者真實瀏覽器登入流程**未在本輪實機操作**（沒有可操作畫面的工具）；見 Issue Manager `docs/reports/2026-11-01_imrevitaddin.md`。
