# 建築走動展示交付包設計

> 2026-09-21 回溯補記。走動展示的 7 個 MCP 工具已於 2026-09-15 ~ 09-21 陸續實作完成（見 [CHANGELOG.md](../../../CHANGELOG.md) `[Unreleased]`），但 `docs/superpowers/specs/` 先前只有整地工作流的設計檔，走動展示一份都沒有。本檔補齊設計意圖、資料契約與已知邊界，作為後續修改的依據。
>
> 使用方法看 `domain/walkthrough-package.md`（SOP，人機共讀）；本檔談**為什麼這樣設計**與**元件如何組成**。決策理由見 [docs/ADR.md](../../ADR.md) D-004 ~ D-008。

## 這個功能要解的問題

建築師要跟業主、施工單位或顧問講一條動線時，手上通常有兩種東西：平面圖（大家看不懂空間感）與漫遊影片（看得懂但無法定位「現在在平面圖的哪裡」）。

目標是讓這兩者**同時存在且互相連動**：拖平面圖上的相機標記，影片就跳到那個位置；拖時間軸，平面與 3D 同步移動。

## 核心設計：一切綁在「相機路徑上的位置 t」

四個視窗連動的骨幹是單一參數 `t ∈ [0, 1]`，代表「沿相機路徑走了多遠」。

```
站點 WALK-01..NN（Revit 透視視圖）
        │
        ▼ CameraPathMath.BuildPath（centripetal Catmull-Rom，每段 framesPerSegment 格）
   路徑取樣點 samples[]（每點有 t、弧長 s、位置 p、方向 fwd/up）
        │
        ├──► 逐格設定 Revit 相機 → 匯出 frame_NNNN.png
        ├──► 平面圖像素對位（modelToPixel 矩陣）→ 平面標記位置
        ├──► 剖面圖投影（frame origin/right/up/normal）→ 剖面標記位置
        └──► 3D 視窗相機位姿（讀 model.glb）
```

**因此影格必須由 Revit 逐格匯出，不能交給 AI 生影片**——生成式影片沒有相機資料，`t` 對應不到平面座標，連動就不成立。這是 [D-004](../../ADR.md) 的核心理由，也是整個設計的地基，不可動搖。

## 元件組成

| 元件 | 位置 | 職責 |
|---|---|---|
| 站點讀寫 | `CommandExecutor.Walkthrough.cs` | `get_camera_stations` / `create_camera_stations` / `apply_camera_stations` |
| 路徑數學 | `MCP/Core/Walkthrough/CameraPathMath.cs` | Catmull-Rom 取樣、FOV ↔ 裁剪框互換（含 NUnit round-trip 測試） |
| 對位投影 | `MCP/Core/Walkthrough/PlanProjection.cs` | 模型座標 → 平面/剖面像素矩陣 |
| glTF 匯出 | `MCP/Core/Walkthrough/RevitGltfExportContext.cs`、`GltfBuilder.cs` | `CustomExporter` 串流成 glb（僅材質顏色，無貼圖） |
| 深度渲染 | `bridge/node/walkthrough/render-depth.mjs` | 外部 Node + headless Chrome，讀 glb 離線渲染逐格深度圖 |
| 離線 viewer | `viewer/walkthrough/` | 四窗連動網頁，`file://` 可直接開 |
| 資料契約 | `viewer/walkthrough/CONTRACT.md` | manifest.json 欄位與座標約定 |

## 座標系約定（最容易出錯的地方）

專案內同時存在三套座標，混用會產生**靜默錯誤**（畫面出得來但內容是錯的）：

| 座標系 | 單位 | 用在哪 | 備註 |
|---|---|---|---|
| Revit internal | 英尺 | Revit API 呼叫 | `WalkFeetToMeters = 0.3048` |
| Revit project | 公尺 | MCP 工具的對外參數（`coordinates="revit"`） | internal × 0.3048，未扣 offset |
| 交付包 model | 公尺 | manifest、viewer、`stations.json` | project 扣掉 `units.originOffset`（＝平面裁剪框中心，讓數字變小） |

**相機裁剪框另有第四套**：相機座標系（X 右、Y 上、Z 朝觀者、眼點在原點），由 `BoundingBoxXYZ.Transform` 承載。

> 🔴 **已踩過的坑**：寫入站點裁剪框時建立 `Transform.Identity` 的新 `BoundingBoxXYZ`，等於把相機座標系的數值當世界座標交給 Revit，結果 73 張影格全白。讀取端 `ComputeViewFov` 用 `crop.Transform.Inverse` 反推眼點，兩邊約定不一致。修正見 [D-006](../../ADR.md)：必須取 `view.CropBox` 就地替換 Min/Max。
>
> **任何新增的裁剪框寫入點都要遵守這條。**

## 三角面預算與空間裁切

`RevitGltfExportContext.TriangleBudget` 預設 200 萬，超過即**提前停止**。

關鍵認知：**超過預算時產出的是「被截斷的不完整模型」，不是「細節少一點的模型」**——截斷點由幾何收集順序決定，可能砍掉相機正前方的牆。不可將就，必須重匯。

正確的減量方式是**空間裁切**（`bounds="path"`，從相機路徑算剖面框），不是**類別裁切**（隱藏家具/門窗）。理由見 [D-005](../../ADR.md)：預算問題的本質是空間大小，用類別裁切會砍掉走動展示唯一需要的東西。

## 兩段式寫回（apply_camera_stations）

站點回寫 Revit 是**破壞性**操作，因此設計為兩段：

1. `mode="preview"`（預設）：只回報每站的 `moveMeters` / `heightDeltaMeters` / `headingDeltaDeg`，**不改模型**。離譜的位移通常代表座標系或站名對錯。
2. `mode="apply"`：整批包在一個 `TransactionGroup`，中途失敗整批復原，不留半套修改。

寫回後交付包內的影格與路徑資料即過期（相機動了但 PNG 沒重繪），manifest 會被標記，viewer 顯示過期橫幅。

## 為什麼是「雙向」而不只是匯出

同類做法多半是「Revit 匯出中介檔 → 外部工具處理」，匯出瞬間即與 Revit 斷開。本設計刻意保留雙向：

- 讀：`get_camera_stations`
- 建：`create_camera_stations`
- 寫回：`apply_camera_stations`（網頁上拖過的站點 → Revit 視圖）

這是本功能相對外部做法的核心差異，見 [D-007](../../ADR.md)。設計上的代價是必須維持 MCP 連線與 Revit 版本相容性，但「斷線」是架構性的損失，補不回來。

## 已知邊界

| 邊界 | 現況 | 追蹤 |
|---|---|---|
| 跨樓層 | 平面視窗只有一張平面圖，跨層僅顯示警告橫幅 | ROADMAP R-06 |
| 剖面對位 | 關閉註解裁剪導致圖面外框大於裁剪框，標記會偏 | ROADMAP R-04 |
| 非矩形裁剪 | 會出 warnings，標記可能偏移 | 未排程 |
| glb 嵌入上限 | 超過 150 MB 不嵌入 data.js，3D 視窗空白 | 由 `bounds="path"` 緩解 |
| 匯出期間 Revit 無回應 | 每格數秒，預期行為 | 已於 SOP 明寫 |
| AI 風格化 | 未實作，外接 video-to-video | ROADMAP R-03、[D-008](../../ADR.md) |

## 驗收標準

功能性修改後，**必須實際打開 `index.html` 看畫面**才算完成。工具回傳「零警告」不等於產出正確——2026-09-21 的影格全白與 3D 灰盒子兩個缺陷，工具回報都是零警告或僅有可忽略的警告。

最小驗收路徑：

1. `create_camera_stations` → `get_camera_stations` 檢查 warnings
2. `export_walkthrough_package` → 確認 `plan.png` / `section.png` 有內容
3. `export_walkthrough_frames` → **抽看至少一張影格 PNG**，檔案大小明顯大於空白圖
4. `export_model_gltf` → 確認**無**「三角面超過上限」警告（有＝模型不完整）
5. `export_walkthrough_depth` → 確認格數與影格數一致
6. 開網頁，四窗都有內容且拖動同步
