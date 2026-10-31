# 變更日誌 (Changelog)

所有重要的專案變更都會記錄在此檔案中。

格式基於 [Keep a Changelog](https://keepachangelog.com/zh-TW/1.0.0/)。

---

## [Unreleased]

### 🔀 合併 — 同步 upstream/main（93365e7，2026-10-31）

- 在分支 `merge/upstream-2026-10-30` 把 `upstream/main`（原作者 shuotao，領先 327 個 commit）合併進 `grading-workflow-v2`（領先 110 個 commit），解 42 個衝突檔；整地（Toposolid／坵塊法／限建核對）、ViewSync 前身、socket 強化、3D 網頁匯出（設計選項／命名視圖／視圖位置）功能全數保留。
- **整地**：`MCP/Core/Grading/*`、`CommandExecutor.ToposolidGrading.cs`、`grading-tools.ts` 全取我方（upstream 版是 07-03 初版 `0db7c6f`，我方為其後續演進的超集；upstream 僅有的 R26 `AddSplitLine` 修正我方已有等效 `#if` 護欄）。
- **文件／連結生命週期**：`open_document`／`list_open_documents`／`close_document`／`reload_links` 同名工具兩邊各有實作，保留我方 `CommandExecutor.DocumentLifecycle.cs`（`path`／`detach`／`discard` 參數、中央檔保護、`list_links`）；upstream 獨有的 `save_document` 保留在 `CommandExecutor.DocumentOps.cs`（該檔其餘方法已刪）。**upstream 版 `open_document` 的參數名 `filePath`／`detachFromCentral`、`close_document` 的 `documentTitle`／`discardChanges`、`reload_links` 的 `linkTypeIds`／`linkNames` 不再支援。**
- **通訊層**：`SocketService` 以 upstream 的獨占鎖機制（`ExclusiveLock`）、Origin 防護（403）、`SwitchConnection`、`_sendLock` 序列化送出為底，疊上我方的 `ConnectionId`（重連後舊連線的回應不送到新連線）、`StartAsync(showDialogs, repairPortConflicts)`；`ExternalEventManager` 取 upstream 的 `ConcurrentQueue` 版。**連線行為維持 fork 原樣：`ExclusiveLock` 預設改為 false（新連線接手舊連線，舊連線 Abort）；設 true 才是 upstream 的獨占拒絕 409（使用者 2026-11-01 拍板）。** `socket.ts` 以 upstream 的單例重連、`?client=` 為底，疊上我方的逾時分級（預設 5 分鐘、長任務 10 分鐘）、`responseRejecters`（斷線立即讓 pending 失敗）。
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
# 變更日誌 (Changelog)

所有重要的專案變更都會記錄在此檔案中。

格式基於 [Keep a Changelog](https://keepachangelog.com/zh-TW/1.0.0/)。

---

## [Unreleased]

### 🔧 修正 — 上傳到 Issue Manager 外掛端：獨立審查（opus）後的三項必修與建議項（2026-10-25，分支 v4b3，從 v4b2 開出）

- 起因：v4b2 的獨立審查指出三個 🟡 與數個建議；細節、測試對照與變異驗證見[交班單 §11](docs/commit_handoff_v4b_2026-10-25.md)。
- **未完成上傳不再占名額**（網站端每人最多 5 個未完成 bundle、24 小時才過期）：匯出失敗、500 `finalize_failed` 後重建、簽名網址失效重建、新上傳取代待重試的舊 bundle 時，對舊 bundle 盡力 `DELETE` 一次（失敗不阻擋、不顯示，日誌一行不含編號）；`DELETE` 回 409 已 finalize 時不重建、改取回同一個 setId；429 `too_many_open_bundles` 訊息說明 24 小時自動清除。網站端沒有列出自己未完成 bundle 的端點，所以沒做「清除我的未完成上傳」。
- **上傳分段被拒也能重建**：分段 PUT 回 410／409 `bundle_failed`／`bundle_closed`／404，或簽名網址主機回 4xx（Supabase 簽名 2 小時失效），同一次上傳內自動重建一次並整批重傳；分段本身的 400（雜湊不符）仍續傳同一個 bundle；過期以伺服器回應為準，移除本機時鐘判斷。
- **`bundle_closed` 先確認再作廢**：收到 409 `bundle_closed` 時先對同一個 bundle 再送一次 finalize（冪等回同一個 setId），成功就完成、不會多出第二個工作版本；仍失敗才重建。
- 建議項：未知伺服器錯誤代碼顯示前過濾並截斷（≤ 120 字元）；`ADDIN_PUBLIC_URL` 指到不同主機／埠時給明確訊息（不再被當成 426「請更新外掛」）；上傳網址主機比對改為嚴格（`localhost`／`127.0.0.1`／`[::1]` 互為不同主機）；每次按上傳清掉先前量到的視口大小，匯出時重量（視窗開著時改版面不再用舊快取）；測試：偶發失敗 `WhileTheServerIsProcessingTheWindowSaysSo`（根因＝`Progress<T>` 讓最後一段上傳進度晚於 Stage 文字送達而蓋掉它，已改為直接呼叫）與 `CancellingWhileWaitingForBrowserLogin…`（`Cancel()` 同步執行延續後才設「正在取消」文字，改為先設再取消）、`FinalizeErrorsAreExplicitlySplitIntoRetryableAndNot` 明確列出可重試／不可重試的錯誤、schema 測試新增 `.sha256` 把關與略過原因。
- 測試：`dotnet test MCP.Tests` 562 → 596（594 通過、2 略過同基線，連跑 3 次穩定；視窗流程測試另連跑 20 次無失敗）；MCP-Server `npm test` 38＋Node 70；R24 建置 0 錯誤、警告 1184 同基線。**僅離線驗證，未啟動 Revit、未連真實 Issue Manager。**

### 🔧 修正 — 上傳到 Issue Manager 外掛端：開視窗效能（2026-10-25，分支 v4b2）

- 起因：實機在 55 張圖紙的範例專案按功能區按鈕後 Revit 凍結 12～15 分鐘。讀碼確認 `Viewport.GetBoxOutline()` 在開視窗路徑的「圖紙 × 視口」迴圈內（推測的熱點，**耗時尚未實機量測**）。
- **開視窗只讀便宜欄位**：名稱、識別碼、種類、圖紙→視口→視圖對應；視口外框（`GetBoxOutline`）不再於開窗時讀取。新增 `IProjectSource`／`SnapshotBuilder`（純邏輯，可單元測試；介面上沒有任何量測成員）。
- **昂貴資料延後到匯出**：只對「勾選後同一張圖紙仍有兩個以上視口」的視口量外框（單一視口必然是主要視口），每 6 個一批、每批一次 ExternalEvent，顯示「讀取視口大小 n/m」並可取消；量過的不重量。
- **計時日誌**：開窗三階段與量測批次各記數量與毫秒（不含圖紙或專案名稱），供實機計時；步驟見[交班單 §10](docs/commit_handoff_v4b_2026-10-25.md)。
- 測試：`dotnet test MCP.Tests` 547 → 562（560 通過、2 略過同基線）；MCP-Server `npm test` 38＋Node 68 通過；R24 建置 0 錯誤、警告 1184 同基線。**僅離線驗證，未啟動 Revit。**

### 🔧 修正 — 上傳到 Issue Manager 外掛端：對齊網站端（imv4a）版本包 API 的實際合約（2026-10-25，分支 v4b2，commit `a31fd2b`、`37415a8`）

- 起因：網站端 4A（Issue Manager `imv4a`）實作後，交班單列出 16 項與客戶端副本的差異；本批逐項處理，細節與測試對照見 [交班單 §9](docs/commit_handoff_v4b_2026-10-25.md)。
- **finalize 驗證失敗即作廢**：400／413／409 `bundle_failed`／410／500 `finalize_failed`／404 時，重試改為「重新建立 bundle、整批重傳、再 finalize」（不重新匯出，先確認本機檔未被改動）；版本包過期同樣自動重建；`invalid_manifest`／`page_count_mismatch`／`invalid_pdf`／413（內容本身被拒）不提供重試並清掉本機暫存。
- **finalize 逾時與處理中**：finalize 專用 `HttpClient` 逾時 330 秒（其餘 60 秒）；逾時、502／504、409 `finalize_in_progress` 時顯示「伺服器處理中」，每 5 秒重送 finalize（冪等），上限 10 分鐘（網站端沒有 `GET /bundles/:id`）。
- **取消**：`DELETE` 回 409 `already_finalized`／`finalize_in_progress` 時如實顯示「已經上傳完成，無法取消；請到 Issue Manager 網站刪除該版本」，不假裝取消成功。
- **勾選條件**：只勾視圖（沒有圖紙也沒有模型）不能上傳（伺服器回 400 `invalid_manifest`）；圖紙 > 1,000 或視圖 > 5,000 時停用並說明。
- **manifest schema 與網站端權威檔逐欄一致**（`warnings` ≤ 200 等）；`ManifestBuilder` 截斷顯示用文字、警告超量收成一項、不能截斷的在匯出前失敗；不再輸出 `viewPositions`；新增 `docs/schemas/web-authoritative/` 原樣副本與兩邊（Node ajv、C#）差異清單測試。
- **API 細節**：建立成功 201／200 都接受並讀取 `limits`（超過上限上傳前中止）；`invalid_input`（`field:"stage"`）等 14 個錯誤碼補白話訊息；分段大小以回應 `partBytes` 為準；磁碟模式分段帶 `X-Part-Sha256`；伺服器回 `http://localhost` 上傳網址而 Issue Manager 不在本機時中止並提示設定 `ADDIN_PUBLIC_URL`。
- 假伺服器（Node）與 C# 假 API 改成網站端行為（201、`invalid_input`、三種 kind、作廢狀態機、409／410／429、finalize 延遲與失敗注入）。
- 測試：`dotnet test MCP.Tests` 479 → 547（545 通過、2 略過與基線相同）；MCP-Server `npm test` 38＋Node 68 通過；R24 建置 0 錯誤、警告 1184 同基線。**僅離線驗證（含對 Node 假伺服器的真實 HTTP 逾時／取消／作廢測試）；未連 Revit、未安裝外掛、未連真實 Issue Manager、未 push。**

### 🔧 修正 — 上傳到 Issue Manager 外掛端：獨立安全審查後修正（2026-10-25，分支 v4b，commit `fba7a60`）

- **登出／切換帳號真的撤銷伺服器端授權**：access 已過期或被 401 時改用 `refresh_token` 走撤銷端點（舊版拿過期 access 當 Bearer、伺服器端一族權杖沒被撤銷，審查探針 `revokedTokens` 0 → 2）；`LogoutAsync` 回傳 `LogoutOutcome`，失敗時視窗如實顯示「伺服器端撤銷未完成」並指引到網站帳號頁手動撤銷。
- **只執行絕對路徑的 node.exe**：不再用裸名 `node`（工作目錄／專案資料夾放假 `node.exe` 不會被執行）；往上找 `tools` 資料夾的開發路徑預設關閉（`REVITMCP_DEV_TOOLS=1` 才啟用）。
- **ExternalEvent 等待可取消、可逾時**：新增 `QueuedApiThreadRunner`（排隊取消、`Raise()` 回傳值檢查、開始前 120 秒逾時＋白話訊息、放棄的工作不會稍後被執行）；所有結束路徑釋放 `UploadFlow` 的 `_running`，不需重開 Revit。`ExternalEventManager` 新增 `TryExecuteCommand`。
- **關閉視窗（含 X）清理未完成的 bundle**（詢問「放棄這次上傳並清除？」，先刪本機檔再通知伺服器）；外掛啟動時背景清除超過 3 天的舊暫存（`BundleTempCleaner`）。
- **移除巢狀訊息迴圈**：匯出每個 Revit 步驟各自一次 ExternalEvent，步驟之間回到 UI 執行緒；移除 `IExportProgress.Pump`。取捨與「需實機觀察」項見 [ADR D-011 審查後修正](docs/adr/D-011-issue-manager-upload-addin.md) 與交班單。
- 加固：loopback `ExclusiveAddressUse` 與讀取逾時；續傳前重新計算磁碟 sha256；PDF 1 MB 串流掃描、頁數不符中止（含物件串流且頁數偏少時只警告）；登入中取消的文案；功能區面板建立包 `try/catch`。
- 測試：`dotnet test MCP.Tests` 429 → 479（+50）；MCP-Server `npm test` 38＋Node 34 通過；R24 建置 0 錯誤、警告 1184 同基線。**僅離線驗證；未連 Revit、未安裝外掛、未連真實 Issue Manager。**

### 新增 — 上傳到 Issue Manager：功能區按鈕、勾選視窗、版本包、瀏覽器授權登入（2026-10-25，分支 v4b，任務書 V4 的 4B）

- 功能區新面板「Issue Manager」＋按鈕「上傳到 Issue Manager」（`IExternalCommand`＋`ExternalEventManager` 佇列，**不經 MCP WebSocket**，MCP 服務關閉時可用）。勾選視窗：專案、設計階段（第一項「請選擇階段」、無預設值）、版本說明、圖紙集（Revit `ViewSheetSet` 為預設勾選來源）、圖紙／視口視圖樹、其他視圖分組、模型與設計選項、摘要與缺項提示。
- 一次匯出：PDF（`export_sheets_pdf` 合併單檔）、GLB（`export_design_options`，有選項分件時以 `merge-option-glbs.mjs` 合併）、3D 視角（`export_named_views`，與 GLB 同原點）→ `manifest.json`（圖號取自 Revit、UniqueId 對應、sha256）→ 背景分段上傳（可重試、已完成的檔案不重傳、可取消並 `DELETE` 伺服器暫存）→ finalize。視圖位置檔屬 V5，本案不匯出。
- 登入：瀏覽器授權碼＋PKCE（S256）＋loopback（`TcpListener` 127.0.0.1 隨機埠）；refresh token 以 Windows DPAPI 加密落檔、輪替且單鎖序列化；權杖／授權碼／verifier／state 不寫日誌。
- Revit 端接線：`RevitSelectionCollector`（圖紙、視口面積、可匯出視圖、`ViewSheetSet`）、`RevitBundleExportSteps`（經 `CommandExecutor.ExecuteCommand` 分派既有匯出指令；有設計選項分件時以 `merge-option-glbs.mjs` 合併；匯出報告 `complete:false` 即中止，不上傳缺漏的模型）、`IssueManagerSession`（共用登入狀態與 HttpClient，不自動轉址）；視窗在每個 `await` 之後切回 UI 執行緒，進度回呼帶執行代號（流程結束後的舊進度不蓋掉最後狀態）。網址設定：環境變數 `ISSUE_MANAGER_URL` 或 `%APPDATA%\RevitMCP\issue-manager.json`（無預設值、僅 https，http 限本機）。
- 新增 `docs/schemas/bundle-manifest.schema.json`（客戶端副本，待與網站端 4A 對齊）與共用 fixture、假 Issue Manager（`tools/fake-issue-manager/server.mjs`）、`scripts/install-addon.ps1` 部署 GLB 合併腳本。詳 [docs/ISSUE_MANAGER_UPLOAD.md](docs/ISSUE_MANAGER_UPLOAD.md)、[docs/adr/D-011-issue-manager-upload-addin.md](docs/adr/D-011-issue-manager-upload-addin.md)。
- 測試：`dotnet test MCP.Tests` 314 → 429（新增 115，含 Node 假伺服器整合測試、WPF 視窗綁定與整段流程測試）；`tools/*.test.mjs` 10 → 34。R24 建置 0 錯誤、警告數 1184 與基線相同。**僅離線驗證；未連 Revit、未安裝外掛、未連真實 Issue Manager。**
- 文件：新增 [docs/ISSUE_MANAGER_UPLOAD.md](docs/ISSUE_MANAGER_UPLOAD.md)（設定、流程、登入、檔案地圖、假伺服器、與網站端 schema 的對齊）、[ADR D-011](docs/adr/D-011-issue-manager-upload-addin.md)、LESSONS L-18～L-20、ROADMAP R-08、交班單 [docs/commit_handoff_v4b_2026-10-25.md](docs/commit_handoff_v4b_2026-10-25.md)（含給 Alex 的實機測試清單 T1～T12）；SPEC、文件清單、PROJECT_INTRO 同步。

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

---

## [1.6.0] - 2026-07-31

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
