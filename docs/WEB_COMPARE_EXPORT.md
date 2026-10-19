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
