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
| `maxTriangles` | 3000000 | GLB 三角面上限 |

## 輸出檔

| 檔案 | 內容 |
|---|---|
| `revit_<mode>.png` | 六種顯示模式各一張，同視圖同相機 |
| `cpc.ifc` | IFC4 Reference View（失敗退 IFC2x3 CV2.0，見 warnings）；`FilterViewId` = 該視圖；含 Revit 與 IFC common property sets |
| `cpc.glb` | 每個元素一個節點；`node.extras` = `elementId`、`uniqueId`、`category`、`family`；Y-up、公尺 |
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

## 連結模型與地形（2026-10-18 實測）

- Revit 的 `CustomExporter` 會進入連結模型（`OnLinkBegin`），但**不匯出連結模型裡的 Toposolid**（CPC 案：TOPO_CPC 的地形完全沒進 GLB）。匯出器因此用 `linkedTerrain` 後備路徑：逐個載入中的連結取 Toposolid／Topography 幾何、三角化、套連結變換、依 section box 以 Sutherland–Hodgman 裁切後寫成節點（`category` 為 Toposolid，`family` 為連結檔名），略過朝下的底面。
- GLB 節點 `extras.family` ＝ 連結檔名者來自後備路徑。
- 回傳 `linkDiagnostics.glbTerrain` 列出 CustomExporter 各連結／類別的節點數、各連結三角面、後備是否啟用。
- `CustomExporter` 在 section box ＋ 兩個大連結的案子上實測約 10 分鐘（CPC 案，6.17 MB GLB）；用戶端逾時請設 ≥ 15 分鐘，或把 GLB 與圖片分次呼叫（`includeGlb:false`／`includeImages:false`）。

## 圖片空白的兩個原因（2026-10-18 實測）

1. **視圖帶著遠裁剪**：從有「Far Clip Active」的視圖（例：A-2 鳥瞰圖）複製來的 WEB_COMPARE，遠裁剪距離 154 m，眼點離模型更遠時整個模型被切掉。匯出前請關閉 Far Clip Active、Crop View（可用 `modify_element_parameter`，參數名 `Far Clip Active`／`Crop View`／`Crop Region Visible`，值 0）。
2. **Revit 圖形硬體加速異常**：重新啟動 Revit 後，3D 畫布與 `ExportImage` 都輸出全白（PNG 約 7.8 KB，連平面圖也空白）。在「選項 → 硬體」關閉「使用硬體加速」並重開檔案後恢復（PNG 約 0.5–1.8 MB）。根因（RTX 4090 Laptop 驅動／顯示設定）未查明。

## 已知限制

- 匯出期間會切換視圖顯示樣式與作用中視圖，結束後還原；視圖若被視圖樣板控制顯示樣式，該張圖樣式不可信（warnings 會說）。
- 單位視圖樣板、材質貼圖：GLB 只含材質顏色，不含貼圖，所以 realistic 與網頁會有外觀差異，這正是比對要看的。
- IFC 匯出若報需要 Transaction，會自動在 Transaction 內重試並 RollBack。
