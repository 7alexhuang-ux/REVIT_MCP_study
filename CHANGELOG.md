# 變更日誌 (Changelog)

所有重要的專案變更都會記錄在此檔案中。

格式基於 [Keep a Changelog](https://keepachangelog.com/zh-TW/1.0.0/)。

---

## [Unreleased]

### 🔀 合併 — 同步 upstream/main（93365e7，2026-10-31）

- 在分支 `merge/upstream-2026-10-30` 把 `upstream/main`（原作者 shuotao，領先 327 個 commit）合併進 `grading-workflow-v2`（領先 110 個 commit），解 42 個衝突檔；整地（Toposolid／坵塊法／限建核對）、ViewSync 前身、socket 強化、3D 網頁匯出（設計選項／命名視圖／視圖位置）功能全數保留。
- **整地**：`MCP/Core/Grading/*`、`CommandExecutor.ToposolidGrading.cs`、`grading-tools.ts` 全取我方（upstream 版是 07-03 初版 `0db7c6f`，我方為其後續演進的超集；upstream 僅有的 R26 `AddSplitLine` 修正我方已有等效 `#if` 護欄）。
- **文件／連結生命週期**：`open_document`／`list_open_documents`／`close_document`／`reload_links` 同名工具兩邊各有實作，保留我方 `CommandExecutor.DocumentLifecycle.cs`（`path`／`detach`／`discard` 參數、中央檔保護、`list_links`）；upstream 獨有的 `save_document` 保留在 `CommandExecutor.DocumentOps.cs`（該檔其餘方法已刪）。**upstream 版 `open_document` 的參數名 `filePath`／`detachFromCentral`、`close_document` 的 `documentTitle`／`discardChanges`、`reload_links` 的 `linkTypeIds`／`linkNames` 不再支援。**
- **通訊層**：`SocketService` 以 upstream 的獨占鎖（`ExclusiveLock`，預設 true：已有連線時新連線回 409）、Origin 防護（403）、`SwitchConnection`、`_sendLock` 序列化送出為底，疊上我方的 `ConnectionId`（重連後舊連線的回應不送到新連線）、`StartAsync(showDialogs, repairPortConflicts)`；`ExternalEventManager` 取 upstream 的 `ConcurrentQueue` 版。**行為變更：預設改為獨占連線，不再是「新連線接手舊連線」；需要舊行為時把設定 `ExclusiveLock` 關閉。** `socket.ts` 以 upstream 的單例重連、`?client=` 為底，疊上我方的逾時分級（預設 5 分鐘、長任務 10 分鐘）、`responseRejecters`（斷線立即讓 pending 失敗）。
- `tools/index.ts` 採 upstream 結構（`DIAGNOSTIC_TOOLS` 讓 `documentTools` 進每個 profile、`gradingTools` 進 full／architect／structural），再加我方 `walkthroughTools`、`webCompareTools`；`revit-tools.ts` 的 `registerRevitTools` 改為單純轉呼叫（不再自行追加 `gradingTools`，避免重複註冊）。
- `install-addon.ps1` 採 upstream 重寫版（`-All`／`-NonInteractive`／`Install-ToVersion`），補回我方走動展示 viewer 與深度圖腳本的部署；`verify-qaqc.ps1` 取 upstream，補回對 `.worktrees`／`.claude/worktrees` 的掃描排除。
- `README.en.md` 已被 upstream 改名（英文 `README.md`、繁中 `README.zh-TW.md`），我方 `README.en.md` 的內容（僅計數）隨之放棄。upstream 刪除的 `MCP/bin/**`、`MCP/obj/**` 建置產物跟著刪除。
- 計數重算（`registerRevitTools()` full profile／`domain/*.md` 加 `references`／`.claude/skills/*/SKILL.md`）：**276／100／62**，已寫回 CLAUDE.md、README、README.zh-TW、DOCUMENT_AUDIENCE_INVENTORY 與 docs/BIM_MCP 各頁；`tools-index.html`、`skills-index.html` 由 `gen-tools-index.py`／`gen-skills-index.py` 重生（補 `skills-index-map.json` 的 `hillside-site-review`、`gen-tools-index.py` 的 `walkthrough-tools`／`web-compare-tools` 分類）。
- 兩支腳本內寫死的個人暫存路徑（`render-depth.mjs`、`make-sample-package.mjs`）改為環境變數或 `TEMP`，以通過 upstream 新增的「無硬編碼帳號名」檢查。
- 驗證：MCP-Server `npm run build`、`npm test`（38＋10）；`dotnet build -c Release.R24` 0 錯誤；`dotnet test` 334 通過；`verify-qaqc.ps1 -SkipBuild -SkipDeploy` 見 commit 訊息。未連 Revit、未部署，獨占鎖與合併後的命令分派待實機驗證。

### 新增 — 設計選項實體幾何、命名視圖與無人操作工具（2026-10-23）

- 新增 list_design_options／export_design_options／export_named_views；export_web_compare 以 designOptions:true 選用新流程，預設保留原匯出。穩定 UniqueId、逐元素 designOption extras、design_options.json 及共用原點相機資料。
- 主模型一次＋逐 host option 真實幾何，暫存視圖 TransactionGroup RollBack；API 可見性缺口、後備材質／裁切差異及不完整結果明示。tools/merge-option-glbs.mjs 合併實際 GLB，驗證分件與索引。
- opt-in marker 單次 Idling 自動啟動；新增 open/list/close documents、list/reload links。預設detach開檔，髒文件關閉需明示捨棄；auto不彈啟動對話框、不修port、不殺程序。連結reload會清Undo，整批先驗證。
- 可重跑 JSON 請求、正式 bridge 命令列工具與 GLB inspector；長任務 timeout、完成 timer 清理及錯誤紀錄。詳 docs/WEB_COMPARE_EXPORT.md、docs/adr/D-010-design-options-and-named-views.md。
- 2026-10-24 補強：list_design_options 加 `mainModelElementCount`；export_design_options 報告頂層加 `textureProblems`（找不到貼圖的檔名與原路徑）；新增一鍵驗收 `tools/verify-design-options.mjs`（含離線測試）。
- 2026-10-24 設計選項「無幾何」判定：新增已知非幾何白名單（空嵌板 Empty_Panel、帷幕格線、標註、參考平面、草圖線、開口切割、約束、Level／Sketch）不計失敗但逐筆列 `ignoredNoGeometry`；帷幕牆本體且面板豎框皆在選項內視為容器；其餘 0 面元素以實例幾何／Medium／Coarse 重試，取得者列 `recoveredGeometry`。Glazed／Wall／Aluminium 等實體類仍算失敗。新增 DesignOptionNoGeometryPolicy 與 26 個 NUnit 案例；未連 Revit，重試與容器規則待實機驗證。
- 2026-10-24 實機重跑後再補四類 noGeometry 放行規則：零尺寸帷幕嵌板（System Panel 寬高皆 0）、嵌入牆嵌板（`Panel.FindHostPanel()` 指向已匯出幾何的本選項 Wall）、無分類 SketchPlane（含 1F Level）、無分類且名稱以 Wall Profile 開頭的 Family／Element。`ignoredNoGeometry` 每筆新增 `basis`。判斷輸入改為純資料 `NoGeometryFacts`／`DesignOptionNoGeometryPolicy.Evaluate`，NUnit 288→314；有尺寸 Glazed、找不到宿主牆的 Wall 嵌板、其他無分類 Element 仍算失敗。未連 Revit，待實機重跑驗證。
- 僅建置與離線驗證；尚未部署、未改 Add-ins、未建立 marker、未取得 AR_CPC 實機結果。

### 🔧 修正 — 指令與工具註冊補齊、通訊層強化（2026-10-25 提交）

- **死碼接線**：先前 commit `742efaf` 的 `list_design_options`／`export_design_options`／`export_named_views`／`open_document`／`list_open_documents`／`close_document`／`list_links`／`reload_links` 只有實作、沒有註冊，現已在 `CommandExecutor.cs` 加入分派，並在 `tools/index.ts` 把 `documentTools` 加入 full／architect／mep／structural／fire-safety 五個 profile。整地（坵塊法、限建核對、`generate_grading_analysis`、`duplicate_floor_draft`）與走動展示指令的分派、`walkthroughTools` 註冊也在此批次一併落地。
- **MCP-Server `socket.ts`**：連線去重與逾時、不再無限自動重連、斷線時一併拒絕待處理請求、依指令分級逾時（預設 300 s，長指令 600 s，`REVIT_MCP_COMMAND_TIMEOUT_MS` 可覆寫；`export_design_options`／`open_document`／`reload_links` 列入長指令）、完成後清 timer、同步送出失敗也清理 pending；新增 `socket.test.ts`、`socket-design-options.test.ts`、`design-option-tools.test.ts`。`npm test` 納入 `tools/*.test.mjs`。
- **Revit 端**：`ExternalEventManager` 由單一 action 改為佇列（避免連續請求互相覆蓋）、`RevitCommandRequest.ConnectionId`（重連後舊請求不回送新連線）、`SocketService` 連線加鎖、新連線接手時關閉舊連線並以 connectionId 區分、WebSocket 分片訊息組裝完整再處理；`StartAsync(showDialogs, repairPortConflicts)` 供自動啟動模式不彈對話框、不修 port；`Application.cs` 加 opt-in marker（`~/.revit-mcp/autostart`）單次 Idling 自動啟動與 `REVIT_MCP_PORT` 覆寫。
- 僅建置與離線驗證（R24 建置 0 錯誤、dotnet test 314、npm test 38＋10）；未部署、未連 Revit，自動啟動與佇列行為待實機驗證。

### 🆕 新增 — 整地工作流後續批次（2026-07-08 ～ 2026-10-05，既有 staged 批次整批提交）

> 本段依既有 staged 批次的檔案內容與 `log/2026-07.md` 歸納；本批次原作者未留 CHANGELOG，提交者未逐項重跑實機，僅驗證建置與離線測試。

- **坵塊法與限建核對（4 個 MCP 工具）**：`get_parcel_contour_layers`、`preview_parcel_slope`、`create_parcel_slope_map`（建築技術規則施工編 §261／§262，`domain/parcel-slope-analysis.md`）、`check_grading_parcel_limits`（整地方案 × 坵塊限建疊圖檢查，設計檔 `docs/superpowers/specs/2026-10-05-grading-parcel-limit-check-design.md`）；程式 `CommandExecutor.ParcelSlope.cs`、`CommandExecutor.GradingParcelLimitCheck.cs`、`Core/Grading/ParcelSlopeMath.cs`、`ParcelLimitCheck.cs`。
- **整地分析與樓板設計副本**：`generate_grading_analysis`（`CommandExecutor.GradingAnalysis.cs`）、`duplicate_floor_draft`（`CommandExecutor.FloorDraft.cs`，樓板比照地形走設計副本模式，設計檔 `2026-07-10-grading-floor-per-scheme-design.md`）。
- **Ribbon 與出圖**：平衡高程試算對話框 `BalancedElevationDialog`、圖框挑選 `TitleBlockPickerDialog`／`TitleBlockChoice`（取代 `TerrainViewPickerDialog`）、標註排版 `AnnotationLayoutMath`、出圖契約與 C-00c 品質改版（設計檔 `2026-07-09-grading-ribbon-output-contract-design.md`、`2026-07-10-grading-c00c-output-quality-design.md`、`2026-07-10-grading-ribbon-consolidation-design.md`）；Ribbon 圖示重生（balance／duplicate 新增，compare／set／view 移除）。
- **domain 與 skill**：新增 `domain/hillside-site-review.md`（山坡地基地檢討總流程）、`earthwork-balance.md`、`earthwork-gridsheet.md`、`parcel-slope-analysis.md`，更新 `grading-slope-classification.md`；新增 `/hillside-site-review` skill。
- **測試**：`MCP.Tests/Grading/` 新增 ParcelSlopeMath、ParcelLimitCheck、AnnotationLayout、TitleBlockChoice 測試並擴充既有案例；MCP-Server 的 `grading-tools.test.ts` 同步。
- **開發日誌**：`log/2026-07.md` 補入 2026-07-08 ～ 07-11 的整地實機驗證與逐輪修正紀錄。
- 註冊與通訊層見同批次後續 commit（`CommandExecutor.cs` 指令分派、`tools/index.ts`）；單獨此 commit 不含分派註冊。

> ✅ **2026-10-25 更新**：上方各節與下方走動展示、整地 Ribbon 等節的檔案已分批 commit（見 `docs/commit_handoff_2026-10-25.md`）。分支 `grading-workflow-v2` 與 `main` 的分歧未處理：2026-10-25 實測 `git rev-list --left-right --count main...HEAD` 為落後 230、領先 51（原 2026-09-21 記載為 192／42），整併前請先處理，見 [ROADMAP.md](ROADMAP.md) R-01。已追蹤的 `MCP/bin/Release/*`、`MCP/obj/Release/*` 建置產物未納入這些 commit。

### 🆕 新增 — `export_web_compare`：網頁檢視器 × Revit 顯示模式比對包（2026-10-17）

一次呼叫為 issue-manager S1 驗證產出同視圖、同相機的檔案：六種顯示模式 PNG（`revit_wireframe`／`hiddenLine`／`shaded`／`shadedEdges`／`consistent`／`realistic`）與 `cpc_consistent.png`／`cpc_realistic.png`、`cpc.ifc`（IFC4 Reference View，失敗退 IFC2x3 CV2.0）、`cpc.glb`（每元素一節點，`extras` 帶 elementId／uniqueId／category／family）、`camera.txt` 與 `camera.json`（Revit 與 glTF 座標、原點偏移、FOV／正交尺寸、PNG 像素）。回傳每檔路徑、大小、耗時、warnings，超過 500 MB 提醒。

- 新增 `MCP/Core/Commands/CommandExecutor.WebCompare.cs`、`MCP-Server/src/tools/web-compare-tools.ts`（full／architect profile）、`docs/WEB_COMPARE_EXPORT.md`
- `GltfBuilder` 新增 `BeginElement`／`EndElement`（逐元素節點，未使用時行為不變）；`RevitGltfExportContext` 新增 `TrackElements`（預設 false，走動展示不受影響）
- 測試：NUnit +3（逐元素節點、空元素、合併與逐元素並存）、MCP Server +4
- 2026-10-18 實機驗收（CPC 案）並補強：新增 `sectionBox`（auto＝基地線外框＋10 m，Z 取建築與地形表面再加邊距）、`camera`（匯出前設相機）、`linkedTerrain`（CustomExporter 不匯出連結 Toposolid，改自行三角化並依 section box 裁切後補進 GLB）；不再另存與 `revit_*.png` 相同的 `cpc_consistent.png`／`cpc_realistic.png`；新增 `WebCompareMath`（裁切與範圍運算，NUnit +6）。圖片空白的兩個原因（遠裁剪、Revit 硬體加速異常）見 `docs/WEB_COMPARE_EXPORT.md`
- 部署需先關閉 Revit

### 🆕 新增 — 建築走動展示交付包（7 個 MCP 工具）

把 Revit 排好的透視相機站點，變成雙擊即開的離線四視窗（影格／平面／剖面／3D）簡報網頁，四窗以「相機路徑上的位置」連動。

- **`get_camera_stations`** — 列出 `WALK-NN` 透視站點，回傳眼點／方向／視角與排站 warnings
- **`export_walkthrough_package`** — 建立交付包（平面/剖面 PNG、manifest.json、data.js、離線 viewer），含像素對位
- **`export_walkthrough_frames`** — 沿路徑逐格匯出影格 PNG，自動分批、可中斷續跑
- **`export_model_gltf`** — 匯出 glTF binary（僅材質顏色，無貼圖），可嵌入 data.js
- **`apply_camera_stations`** — 把網頁「站點編輯模式」調過的站點整批寫回 Revit（`preview`／`apply` 兩段式）
- **`export_walkthrough_depth`** — 外部 Node.js + headless Chrome 離線渲染逐格深度圖（AI 風格化前置）
- **`create_camera_stations`** — 讓 AI 從零建立 `WALK-NN` 透視站點，不需使用者先手動放相機

配套：`domain/walkthrough-package.md` SOP、`viewer/walkthrough/` 離線四視窗 viewer 與 `CONTRACT.md` 資料契約、`bridge/node/walkthrough/render-depth.mjs` 深度渲染器、`MCP/Core/Walkthrough/CameraPathMath.cs`（含 NUnit round-trip 測試）。

- 2026-10-25 本批次檔案（`CommandExecutor.Walkthrough.cs`、viewer、深度渲染 bridge、`walkthrough-tools.ts`／`walkthrough-batch.ts` 自動分批與測試、`install-addon.ps1` 的 viewer／bridge 部署步驟、`domain/walkthrough-package.md`、設計檔）整批 commit；指令分派註冊在後續「通訊層強化與指令／工具註冊」commit。`log/2026-09.md` 一併入庫。viewer 離線測試 `viewer/walkthrough/pathmath.test.cjs` 20 通過（不在 `npm test` 內，需手動 `node --test`）。

### 🐛 Bug 修正 — 走動展示（2026-09-21 實機發現）

- **影格全白**：`create_camera_stations` 把相機座標系的裁剪框 Min/Max 寫進 `Transform.Identity` 的新 `BoundingBoxXYZ`，與讀取端經 `crop.Transform.Inverse` 反推眼點的約定不一致，導致匯出的 73 張影格全是空白。改為沿用 `view.CropBox` 既有 Transform（`SetOrientation` 後即相機框）就地替換 Min/Max。此修正同時解釋了先前被誤判為誤報的「裁剪區域未置中於視線」警告。
- **glTF 截斷導致 3D 視窗不可用**：大模型撞 200 萬三角面上限時匯出提前停止，產出不完整的 glb。`export_model_gltf` 新增 `bounds`（預設 `"path"`）／`marginMeters`／`belowMeters`／`aboveMeters`，依交付包相機路徑自動計算剖面框，**以空間裁切取代類別裁切**，保留門窗、帷幕牆、家具等全部類別。決策理由見 [docs/ADR.md](docs/ADR.md) D-005。

### 💄 改善 — 離線 viewer

- 播放軸由裸 `<input type="range">` 改為**影格縮圖列 ＋ 可點選的 `WALK-NN` 站點刻度 ＋ 播放頭 ＋ hover 預覽**；讀數加上所在段落（如「WALK-02 → WALK-03」）。

### 📄 文件與治理

- **新增 [docs/ADR.md](docs/ADR.md)**：專案首份架構決策紀錄，D-001 ~ D-004 回溯補記既有決策的理由，D-005 ~ D-008 記錄走動展示本輪的決策，另列 3 條待決議題。
- **新增 [ROADMAP.md](ROADMAP.md)**：現況快照、R-01 ~ R-06 待辦與阻塞點。
- **新增 `docs/superpowers/specs/2026-09-21-walkthrough-package-design.md`**：走動展示設計檔（先前 specs 目錄只有整地工作流）。
- **本檔補記 2026-04 ~ 07 的成果**（含已打 tag 但未記錄的 `v1.6.0`）。

### 🔧 QA/QC — 由 FAILED（60 餘項）修復為 PASSED（54 PASS / 0 FAIL）

- **修正掃描器兩處誤判**：`verify-qaqc.ps1` 的過時路徑掃描現在排除 `references/qa-checklist.md`（打包進 skill 的檢查清單副本，本就刻意列舉禁用檔名，正本早已排除）與 worktree 目錄（其他分支的 checkout，理由同 `node_modules`）。
- **更正 BIM_MCP 公開站的過時計數**：8 份頁面的 Domain 45/46 → 50、Skill 22 → 23，含 og:title、hero 文案、分類標題與三層架構卡片。這些**不是**複本雜訊，是對外公開頁面上的真實錯誤。
- **補上兩張缺漏索引卡**：`domain-flow-visualization`（domain-index）與 `domain-diagram`（skills-index），並互加反查連結。

### 🆕 新增 — 整地工作流 UI（Ribbon）

- `GradingRibbonCommands`、`SchemeOverviewWindow` 等 WPF UI 與圖示；三鈕任務流（新整地方案／方案總覽／比較出圖）。
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

## [fork-1.6.0] - 2026-07-31（fork 自有版本線；與 upstream 同號 1.6.0 為不同內容）

> 涵蓋 2026-04 ~ 2026-07 的社群協作、工具擴充與整地工作流三階段。此版本已打 tag（`v1.6.0`，commit `fcb61a5`）但先前未寫入本檔，於 2026-09-21 補記。

### 🆕 新增 — Toposolid 整地工作流（V1 → V3）

- **基礎**：`grade_toposolid_to_floors` 三種銜接模式（投影直貼 Footprint／外延漸變 Offset／放坡銜接 Slope）。
- **Phase 1–4**：放坡銜接框架與警告自動消化、方案登記簿與 Excel 比較表、平衡高程反求與鬆實方三本帳、表現與送審層（方案視圖／坡度熱區／標註／還原／計算書）。
- **Phase 5 + V2**：業主整地圖說五工具、六級坡 domain、三鈕任務流、A0 單張總比較圖（C-00c）。
- **V3**：板高程／斜板工具、逐邊坡率（無障礙坡道）、邊界政策模型、非控制樓板覆蓋告警（`FloorBurialCheck`，TDD）。
- **範圍線重寫**：改為「量測差異場邊界的單一封閉曲線」（TDD），並支援貼地形。
- 對應 domain：`grading-slope-classification.md`、`earthwork-balance.md`、`earthwork-gridsheet.md`。

### 🆕 新增 — 工具與能力

- **DWG 圖層批次建柱**（`create_columns_from_dwg`、`preview_dwg_columns`、`get_dwg_column_layers`）＋ `create_level`；倒角 polyline 通用解析；收編 Roy-y111 的柱號對應（`textLayerName`）成為三模式之一。
- **`analyze_floor_slopes`** 樓板坡度分析（Issue #45）。
- **MEP vs CSA 碰撞偵測**完整整合。
- **族群盤點清整**（`family-inventory-cleanup` domain + skill）。
- **`domain-diagram` skill**：腳本驅動產出 Mermaid 流程圖並回嵌 domain。
- 收編社群 fork：`modify_element_parameter` 支援 `Element.Name` 重命名（taiwanbanana）、`move_element`／`flip_element`／門窗 `sourceElementId`／牆原生屬性（poisonsam）。

### 🆕 新增 — 文件與治理

- **BIM_MCP 公開站**上線（`docs/BIM_MCP/`），含個人 LLM Wiki 專題、domain／skills 索引頁。
- **個人 vault 機制**：`vault/`（gitignored）＋ `/ingest`、`/lint`、`/wiki` 三個轉接指令。
- **`docs/PROJECT_INTRO.md`** 社群介紹文件。
- **skill 可分享化**：`.claude-plugin/marketplace.json`，`hj-pr-proposal` 可經 `/plugin install` 安裝。
- **CLAUDE.md 憲法規則**：Tool Call Data Honesty、Domain Method Compliance、Active State Re-Anchoring。
- **CLAUDE.md 英文化重構** ＋ `docs/DOCUMENT_AUDIENCE_INVENTORY.md` 文件受眾盤點。

### 🔧 QA/QC

- `verify-qaqc.ps1` 擴充至 8 階段，新增：本地 markdown 連結腐化、文件受眾分類、亂碼風險、計數宣告一致性、client config 可攜性、快照橫幅檢查。

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
