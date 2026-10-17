# export_web_compare：網頁檢視器 × Revit 顯示模式比對包

給 issue-manager S1 驗證用。一次呼叫從 Revit 產出同一視圖、同一相機的圖、IFC、GLB 與相機資料，供網頁端以相同相機重現並兩兩對照。

## 呼叫

在 Claude Code 對 revit-mcp 說：「用 export_web_compare 匯出 WEB_COMPARE 視圖到預設資料夾」。

| 參數 | 預設 | 說明 |
|---|---|---|
| `viewName` | `WEB_COMPARE` | 不存在時，從目前作用中的 3D 視圖複製並命名（同時移除視圖樣板） |
| `outputDir` | `C:\Project\issue-manager\spikes\s1-glb\input` | 不存在會建立，同名檔覆寫 |
| `pixelSize` | 1920 | PNG 寬度；只匯出視圖可見區域 |
| `includeImages` / `includeIfc` / `includeGlb` | true | 可分開重跑 |
| `maxTriangles` | 3000000 | GLB 三角面上限 |

## 輸出檔

| 檔案 | 內容 |
|---|---|
| `revit_<mode>.png` | 六種顯示模式各一張，同視圖同相機 |
| `cpc_consistent.png` / `cpc_realistic.png` | 等同 `revit_consistent` / `revit_realistic` 的副本 |
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

## 已知限制

- 匯出期間會切換視圖顯示樣式與作用中視圖，結束後還原；視圖若被視圖樣板控制顯示樣式，該張圖樣式不可信（warnings 會說）。
- 單位視圖樣板、材質貼圖：GLB 只含材質顏色，不含貼圖，所以 realistic 與網頁會有外觀差異，這正是比對要看的。
- IFC 匯出若報需要 Transaction，會自動在 Transaction 內重試並 RollBack。
