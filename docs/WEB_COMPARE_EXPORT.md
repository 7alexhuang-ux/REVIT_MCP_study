# export_web_compare：網頁檢視器 × Revit 顯示模式比對包

給 issue-manager S1 驗證用。一次呼叫從 Revit 產出同一視圖、同一相機的圖、IFC、GLB 與相機資料，供網頁端以相同相機重現並兩兩對照。

## 呼叫

在 Claude Code 對 revit-mcp 說：「用 export_web_compare 匯出 WEB_COMPARE 視圖到預設資料夾」。

| 參數 | 預設 | 說明 |
|---|---|---|
| `viewName` | `WEB_COMPARE` | 不存在時，從目前作用中的 3D 視圖複製並命名（同時移除視圖樣板） |
| `outputDir` | `C:\Project\issue-manager\spikes\s1-glb\input` | 不存在會建立，同名檔覆寫 |
| `pixelSize` | 1920 | PNG 寬度；只匯出視圖可見區域 |
| `camera` | 不設 | `{eye, forward, up, fovVerticalDeg?, aspect?}`（Revit 內部座標、公尺、Z 向上）：匯出前把透視視圖相機設到指定位置。`up` 必須與 `forward` 幾乎完全垂直（誤差大會回「view basis is corrupted」，請在呼叫端先正交化並給足小數位數） |
| `sectionBox` | `{mode:"auto"}` | 匯出前在該 3D 視圖開 section box，PNG／IFC／GLB 共用。`auto`：基地線（Property Lines，含連結模型）XY 外框 ＋ `marginMeters`（10）；沒有基地線改用建築範圍。Z＝（建築與範圍內地形表面的最低）− `belowMeters`（2）～（最高）＋ `aboveMeters`（5）。`manual`：給 `min`／`max`（Revit 內部座標公尺）。`off`：不動視圖現有設定。`apply:false` 只算不改視圖。範圍會留在視圖上，回傳 `sectionBox` 欄位列出依據、基地線／建築範圍、地形表面 Z、最後數值 |
| `linkedTerrain` | `auto` | 連結模型的 Toposolid／Topography 如何進 GLB。`auto`：CustomExporter 沒有匯出連結地形時，自行三角化並依 section box 裁切後補進；`always`：一律補；`off`：不補 |
| `includeImages` / `includeIfc` / `includeGlb` | true | 可分開重跑 |
| `maxTriangles` | 3000000 | GLB 三角面總預算（超過就停止匯出）。CPC 案含連結要設 ≥ 4,000,000，否則會在 ID_CPC 連結內被截斷（見 LESSONS L-01） |
| `contourIntervalMeters` | 1 | 地形頂面等高線間距（公尺）；0 不產生 |
| `excludeCategories` | 無 | 整個類別不匯出（Revit 介面語言的類別名稱） |
| `excludeElements` | 無 | 依「來源:元件ID」不匯出，例如 `ID_CPC:4513008`；在進入 Revit 細分**之前**跳過，是唯一能省匯出時間的做法 |
| `maxElementTriangles` | 100000 | 單一元件三角面門檻；0 不設 |
| `overCapMode` | `simplify` | 超過門檻：`simplify`（二次誤差邊收縮簡化，不改 Revit 模型）／`box`（外框盒佔位）／`drop`（不匯出） |
| `simplifyRatio` | 0.1 | `simplify` 保留比例；0 表示簡化到門檻 |
| `levelOfDetail` | -1 | Revit 曲面細分等級 0–15（`ViewNode.LevelOfDetail`），-1 沿用 Revit。全域生效、不影響匯入網格 |
| `dumpAppearance` | false | 診斷用：把材質外觀資產寫進 `cpc.diag.log` |

## 輸出檔

| 檔案 | 內容 |
|---|---|
| `revit_<mode>.png` | 六種顯示模式各一張，同視圖同相機 |
| `cpc.ifc` | IFC4 Reference View（失敗退 IFC2x3 CV2.0，見 warnings）；`FilterViewId` = 該視圖；含 Revit 與 IFC common property sets |
| `cpc.glb` | 每個元素一個節點；`node.extras` = `elementId`、`uniqueId`、`category`、`family`、`source`（`(host)` 或連結文件標題）；被簡化者另有 `sourceTriangles`，外框盒佔位者有 `placeholder:true`；Y-up、公尺 |
| `cpc.elements.json` | 每個有幾何的元件：來源:ID、名稱、類別、族群、原始三角面、保留三角面、處理方式（重到輕排序） |
| `cpc.diag.log` | 即時寫入的匯出診斷（連結進出、三角面、例外、貼圖），MCP 用戶端逾時也不會遺失 |
| `export_report.json` | 完整回傳內容（含 `linkDiagnostics.glbTerrain.heavyElements` 前 40 名） |
| `camera.txt` | 給人看，最後一行「描述」留給使用者補 |
| `camera.json` | 給 Playwright |

## Revit 模式 ↔ 網頁模式

| 檔名片段 | Revit DisplayStyle | 網頁模式 | 網頁截圖檔名 |
|---|---|---|---|
| `consistent` | FlatColors（一致色彩） | consistent | `web_consistent.png` |
| `shaded` | Shading | shaded | `web_shaded.png` |
| `realistic` | Realistic | realistic | `web_realistic.png` |
| `wireframe` | Wireframe | wireframe | `web_wireframe.png` |
| `hiddenLine` | HLR（隱藏線） | hiddenLine | `web_hiddenLine.png` |
| `shadedEdges` | ShadingWithEdges | （無對應） | — |

## 座標系對應（camera.json ↔ cpc.glb）

- `revit.*`：Revit **內部座標**（Z 向上，右手系），公尺。不是共用座標／測量座標。
- `gltf.*`：與 `cpc.glb` 同一空間（Y 向上，右手系，公尺，已扣原點偏移）。
- 轉換：點 `gltf = (x − ox, z − oz, −(y − oy))`，方向 `gltf = (x, z, −y)`；`(ox, oy, oz)` = `gltf.originOffsetMeters`（取眼點水平位置、高程不扣，讓 float32 座標保持小）。
- 網頁端直接用 `gltf.eye` / `gltf.target` / `gltf.up` 設相機，不需再換算。`target` = 眼點沿視線 10 m（Revit 沒有目標點的概念）。
- `projection`：透視給 `fovVerticalDeg`（three.js `PerspectiveCamera.fov` 直接用）、`fovHorizontalDeg`、`aspect`，算法同走動展示（裁剪框 Max 角在前裁剪面）；正交給 `widthMeters` / `heightMeters`（視窗可見範圍）。
- `image.widthPx` / `heightPx`：Revit PNG 實際像素，網頁截圖 viewport 請設同尺寸。若 PNG 長寬比與相機 aspect 差超過 2%，warnings 會提醒。

## 連結模型與地形（2026-10-18 實測，2026-10-20 更正）

- **更正**：原先寫「CustomExporter 不匯出連結裡的 Toposolid」是錯的。實際原因是三角面總預算在 ID_CPC 連結內用完、匯出被截斷，所有連結內容一起遺失（LESSONS L-01）。預算足夠時原生匯出正常：TOPO_CPC 24 個 Toposolid 逐面帶材質，`fallbackUsed:false`。
- 以下後備路徑只在原生沒有匯出任何連結地形時（`auto`）才啟用：逐個載入中的連結取 Toposolid／Topography 幾何、三角化、套連結變換、依 section box 以 Sutherland–Hodgman 裁切後寫成節點（`category` 為 Toposolid，`family` 為連結檔名），略過朝下的底面。
- GLB 節點 `extras.family` ＝ 連結檔名者來自後備路徑。
- 回傳 `linkDiagnostics.glbTerrain` 列出 CustomExporter 各連結／類別的節點數、各連結三角面、後備是否啟用。
- 匯出時間：CPC 案 GLB 631–1,175 秒，幾乎全花在 Revit 細分 ID_CPC 的佛像（`ID_CPC:4513008`，匯入網格 1,304,136 三角面）；`excludeElements` 排除它後 GLB 只要約 10 秒。簡化／外框盒在細分之後才發生，不省時間。MCP 用戶端 10 分鐘逾時後 Revit 仍會做完，結果看 `export_report.json`。

## 高面數元件（2026-10-20，S1 第 3 輪）

- 預設：門檻 100k、超過者簡化到 10%。CPC 案 3,621,834 → 1,575,800 三角面、113.9 → 68.4 MB、瀏覽器載入 9.3 → 3.0 秒，佛像與沙發並排對照肉眼無差。數據與取捨見 `docs/adr/D-009-web-compare-heavy-elements.md`。
- FPS 主要受 draw call（每元件一個 mesh，約 3,245 個）限制，不是三角面；要提升 FPS 需在檢視器端合併。

## 圖片空白的兩個原因（2026-10-18 實測）

1. **視圖帶著遠裁剪**：從有「Far Clip Active」的視圖（例：A-2 鳥瞰圖）複製來的 WEB_COMPARE，遠裁剪距離 154 m，眼點離模型更遠時整個模型被切掉。匯出前請關閉 Far Clip Active、Crop View（可用 `modify_element_parameter`，參數名 `Far Clip Active`／`Crop View`／`Crop Region Visible`，值 0）。
2. **Revit 圖形硬體加速異常**：重新啟動 Revit 後，3D 畫布與 `ExportImage` 都輸出全白（PNG 約 7.8 KB，連平面圖也空白）。在「選項 → 硬體」關閉「使用硬體加速」並重開檔案後恢復（PNG 約 0.5–1.8 MB）。根因（RTX 4090 Laptop 驅動／顯示設定）未查明。

## 已知限制

- 匯出期間會切換視圖顯示樣式與作用中視圖，結束後還原；視圖若被視圖樣板控制顯示樣式，該張圖樣式不可信（warnings 會說）。
- 材質貼圖：可在本機找到的外觀貼圖會嵌進 GLB（上限 80 MB）；找不到的（例如指向 Z:、W: 槽的廠商貼圖）列在 `textureProblems`，該材質退回外觀顏色。
- 相機：`camera.json.projection.frustumTan` 是**未驗證的經驗式**，CPC 案網頁構圖仍比 Revit 放大約 2 倍（LESSONS L-06）。
- IFC 匯出若報需要 Transaction，會自動在 Transaction 內重試並 RollBack。


## 設計選項與命名視圖（2026-10-23）

架構取捨見 [D-010](adr/D-010-design-options-and-named-views.md)。本輪程式已通過建置與離線測試；尚未部署、尚未取得 AR_CPC 的實機選項數及匯出證據。

- `list_design_options({includeViews:true})`：盤點主文件各 set／option 的 ElementId、UniqueId、名稱、isPrimary、elementCount；另回 `mainModelElementCount`（`Element.DesignOption == null` 的非類型元素數，與匯出器同判準）。views 的 `designOptionVisibility:null` 與 notes 表示公開 API 缺口，不表示全部可見。
- `export_design_options`：主模型一次＋每個 host option 一份實體幾何 GLB，產 `main_model.glb`、`option_<UniqueId>.glb`、`design_options.json`、`design_options_export.json`。`optionKeys` 可指定 UniqueId 子集合。`export_web_compare({designOptions:true,...})` 是這個流程的別名；`false`／省略保持原匯出。
- 主模型採暫存全模型等角視圖；option 採真實成員 `Element.Geometry`。這個模式不套用 `viewName`／相機／來源 sectionBox／隱藏狀態，與原比對匯出的範圍不同。option 沒有材質外觀貼圖與等高線；連結內選項暫不支援。
- `maxTriangles` 為每份 GLB 預算；延續 D-009 的 simplify 預設。`excludeElements` 在主模型細分前排除，在選項抽幾何前排除。省時間的快速驗收例排除 `ID_CPC:4513008`；正式交付可移除此排除條件。
- `complete:false` 代表有失敗、未知／無幾何、共享子元件重疊或預算截斷。先看 parts 的 `noGeometry`／`failed`／`diagnostics`；不得僅因有 GLB 檔就視為完成。`excluded` 為呼叫者明確排除的元素，`containers` 為改由成員匯出的群組。

每個選項元素保留原本 `elementId`／`uniqueId`／`category`／`family`／`source`，新增：

```json
{"designOption":{"setKey":"<set UniqueId>","setName":"集合名稱","optionKey":"<option UniqueId>","optionName":"選項名稱","isPrimary":true}}
```

主模型節點省略 `designOption`。`design_options.json` 契約（實作另保留 setId／optionId 便於診斷）：

```json
{"sets":[{"setKey":"<set UniqueId>","setName":"集合名稱","options":[{"optionKey":"<option UniqueId>","optionName":"選項名稱","isPrimary":true,"elementCount":123}]}],"generatedAt":"<ISO 8601>","revitDoc":"文件名稱"}
```

`elementCount` 是 Revit 成員盤點數；實際有網格的數量看分件報告的 `elementNodeCount`。元素與選項關聯一律比 UniqueId，名稱不作 key。

`export_design_options` 回傳另含頂層 `textureProblems`：`[{material,file,path,problem}]`，列出主模型匯出時找不到（或讀不了）的貼圖檔名與原路徑；該材質退回外觀顏色，**不偽造貼圖**。空陣列代表主模型貼圖都找得到。

### 一鍵驗收

```powershell
npm --prefix MCP-Server run build
node tools/verify-design-options.mjs
```

依序呼叫 `list_design_options` → `export_design_options`（請求檔預設 `tools/requests/export-design-options-fast.json`，可用第一個參數換檔）→ 合併 GLB 並檢查 → `export_named_views`（原點取自匯出報告 `originOffsetMeters`，與 GLB 同一組）。檢查項：必要欄位、座標有限數、每個選項 `elementCount > 0`、`complete:true`、GLB 內每個 `designOption.setKey／optionKey` 都在 `design_options.json`、named view 的 target 距眼點 10 m。最後一行 `VERIFY_DESIGN_OPTIONS_OK` 或 `VERIFY_DESIGN_OPTIONS_FAILED`。驗證函式有離線測試（`tools/verify-design-options.test.mjs`，`npm test` 會跑）。

### 可重跑的快速驗收（分步）

先建置 MCP-Server。以下工具使用 repo 正式 `RevitSocketClient`／工具註冊與 dispatcher，不載入個人 `rc.cjs`。連線的 wire 格式是 `{CommandName,Parameters,RequestId}`／`{Success,Data,Error,RequestId}`；參數從 UTF-8 JSON 檔讀取，避免 shell 引號問題。連線預設 localhost:8964，可用 `REVIT_MCP_PORT` 改變。一次只讓一個 MCP client 連到 Revit。

```powershell
npm --prefix MCP-Server run build
node tools/revit-command.mjs list_design_options tools/requests/list-design-options.json C:/Temp/options-list-result.json
node tools/revit-command.mjs export_design_options tools/requests/export-design-options-fast.json C:/Temp/options-export-result.json
node tools/merge-option-glbs.mjs C:/Temp/revit-design-options-fast/design_options_export.json
node tools/inspect-option-glb.mjs C:/Temp/revit-design-options-fast/cpc.glb C:/Temp/revit-design-options-fast/design_options.json
```

快速請求檔不聲稱能保證三十秒；應記錄實際時間、排除條件及 parts。合併器拒絕不完整報告、失敗／遺漏／重複分件、未知選項 key、重複元素身分及未支援 glTF extensions。它會重編 mesh／accessor／bufferView／material／texture／image／sampler 索引；不是只合併 metadata。正常輸出 `cpc.glb` 與同目錄的 `design_options.json`。

### named_views.json

`export_named_views` 唯讀匯出所有非樣板 View3D；`uniqueIds` 可篩選。`outputDir` 沿用既有預設；共用原點來自明示 `originOffsetMeters`、`cameraPath` 或 outputDir/camera.json。沒有原點就拒絕，不對每個相機另選眼點。若設計選項包沒有 camera.json，請從 `design_options_export.json.originOffsetMeters` 複製同一組值建立請求檔。

```json
{"outputDir":"C:/Temp/revit-design-options-fast","originOffsetMeters":[0,0,0],"uniqueIds":[]}
```

```powershell
node tools/revit-command.mjs export_named_views C:/Temp/named-views-request.json C:/Temp/named-views-result.json
```

輸出 `{originOffsetMeters,views,warnings}`；每個 view 含 viewId、uniqueId、name、isPerspective、displayStyle、viewerMode、gltf、sectionBox、revit。gltf 含 eye／target／up、fovVerticalDeg、orthoWidthMeters／orthoHeightMeters。點／向量轉換沿用上節；target 沿 forward 十公尺。viewerMode 無對應時為 null。

- `fovVerticalDeg:null`：目前沒有可靠校準，詳 L-06。正交尺寸僅讀已開 UIView；未開啟則 null，匯出不開視圖、不切相機。raw orientation 與 UIView pan／zoom 尚未驗證像素對齊。
- sectionBox 的 min／max 已是 glTF 座標；旋轉框取八角點 AABB，另帶 corners。`revit.sectionBox` 保留 localMinMeters／localMaxMeters 與 transform，不會假裝 AABB 就是精確旋轉裁切框。

## 無人操作前置：自動啟動、文件、連結

這裡描述新 DLL 部署後的能力。本輪不修改 `%APPDATA%\Autodesk`、不部署、不建立 opt-in marker，也不重啟 Revit。

- 使用者預先建立 `%USERPROFILE%\.revit-mcp\autostart` **檔案**後，外掛在 OnStartup 註冊單次 Idling 啟動 WebSocket。沒有 marker 保持手動 ribbon。auto 模式不彈啟動對話框、不搶占 port、不殺程序、不執行 netsh；失敗寫日誌。port 取合法 `REVIT_MCP_PORT`，否則既有設定（預設 8964）。
- `open_document`：`path` 為現有絕對 RVT 路徑；預設 `detach:true,preserveWorksets:true,activate:true,audit:false`。非工作共享檔明示 detach 不適用。**工作共享中央檔（`BasicFileInfo.IsCentral`）搭配 `detach:false` 一律拒絕**；其他檔的 `detach:false` 使用 DoNotDetach，開檔失敗不偷偷改為分離。已開檔的 detach／worksets 要求衝突會拒絕。不儲存原檔。
- `list_open_documents`：列出 title、真實 revitTitle、path／sourcePath、isActive、isDetached、isModified 等。背景 detached 文件可能沒有 PathName；會保留來源映射，但無安全 API 可重新啟用時明確拒絕。
- `close_document({title,discard:false})`：有未儲存變更就拒絕，不無聲保存或丟棄。`discard:true` 才允許捨棄；Revit API 禁止關閉作用中文件，需先啟用另一份文件。名稱不唯一時拒絕。
- `list_links`：列既有 RevitLinkType、載入狀態、原始與絕對路徑；不可讀的雲端參照回 null＋notes。
- `reload_links({names?,pathOverrides?})`：names 省略或空陣列＝全部頂層連結，此時只重載「未載入／找不到」者，已載入者標 `skipped`；明確列名者一律重載。`list_links` 另回 `referenceType`（Overlay／Attachment）與 `instanceCount`。pathOverrides 為既有連結名稱到現有絕對 RVT 路徑的 object。整批驗證名稱／路徑後逐項回報；不下載、不猜檔、不新建連結。Load／LoadFrom 必須在 Transaction 外，**會清除 Undo history，並可能丟棄連結未儲存的共享座標變更**；此 API 副作用無法 RollBack。此工具不保存主檔或連結原檔。

Revit API 呼叫仍經 ExternalEventManager；MCP-Server 不能在 Revit 沒開、外掛沒部署或存在 Revit modal dialog 時憑空完成操作。
