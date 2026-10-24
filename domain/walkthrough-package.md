---
name: walkthrough-package
description: "建築走動展示交付包 SOP：在 Revit 排好 WALK-01、WALK-02… 透視相機站點（可手動排站，也可用 create_camera_stations 讓 AI 直接從零建立），匯出沿路徑逐格影格、平面圖與剖面圖（含像素對位）、glTF 3D 模型、逐格深度圖與離線網頁，讓影片、平面、剖面、3D 四視窗連動拖曳講解；並可把網頁站點編輯模式調整過的站點整批寫回 Revit。涵蓋排站規則、AI 直接建站、四步驟匯出流程、視角校準、站點微調與回寫、深度圖匯出（AI 風格化前置）、跨樓層與效能限制、第二階段 AI 風格化接口。觸發於走動展示、漫遊、walkthrough、相機路徑、四視窗連動、建立相機站點、create_camera_stations、影格匯出、glTF 匯出、站點回寫、apply_camera_stations、深度圖、depth map、export_walkthrough_depth。"
metadata:
  version: "1.3"
  updated: "2026-09-20"
  references:
    - "Autodesk Revit API Developer's Guide — View3D（透視視圖裁剪框為以眼點為頂點的角錐體）"
    - "https://registry.khronos.org/glTF/specs/2.0/glTF-2.0.html"
    - "viewer/walkthrough/CONTRACT.md（交付包資料契約）"
  related:
    - sheet-viewport-management.md
    - tool-capability-boundary.md
  referenced_by: []
  tags: [walkthrough, 走動展示, 漫遊, 相機路徑, camera, perspective, glTF, 影格, 四視窗連動, 離線網頁, three.js, Revit, depth map, 深度圖, create_camera_stations]
---

# 建築走動展示交付包 SOP

把 Revit 裡排好的相機站點，變成一個「雙擊就能開」的網頁資料夾：拖曳平面圖上的相機標記，影格即時跳到那個位置，3D 視窗與剖面標記同步移動。適合業主簡報、自我設計檢討、對施工或顧問說明。

- 對應工具：（排站可用 `create_camera_stations` 讓 AI 直接建立，見第 1 節「由 AI 直接建站」）`get_camera_stations` → `export_walkthrough_package` → `export_walkthrough_frames` → `export_model_gltf`；站點微調回寫另有 `apply_camera_stations`（見第 3 節），深度圖匯出另有 `export_walkthrough_depth`（見第 4 節）。
- 對應實作：`MCP/Core/Commands/CommandExecutor.Walkthrough.cs`、`MCP/Core/Walkthrough/*.cs`、`viewer/walkthrough/`。
- 資料契約：`viewer/walkthrough/CONTRACT.md`（manifest.json 欄位、座標約定）。

> **核心觀念**：四個視窗連動的骨幹是「相機路徑上的位置 t」。影片的每一格都記下相機位置，所以拖平面等於換格、換格等於移動 3D 相機。這也是為什麼影格要從 Revit 沿路徑逐格匯出，而不是丟給 AI 自己生成一支影片：自己生成的影片沒有相機資料，無法和平面對位。

---

## 1. 排站規則（在 Revit 裡先做好）

| # | 規則 | 為什麼 |
|---|---|---|
| 1 | 每站建一個 3D 視圖，命名 `WALK-01`、`WALK-02`…（前綴加數字，不跳號） | 工具依數字排序；改順序＝改名 |
| 2 | 一定要是**透視**視圖（用「相機」工具放，或 3D 視圖切換為透視） | 等角視圖沒有眼點與視角，會被略過 |
| 3 | 各站眼高建議一致（樓板面 +1.55 m），必要時可不一致（樓梯、坡道、看挑空的站本來就該變） | 平路上下晃是缺陷；樓梯/坡道/挑空的眼高變化是真實的，硬拉平反而失真 |
| 4 | 各站裁剪區域大小與比例相同：先做好 `WALK-01`，其餘站「複製視圖再改方向」，建議 16:9 | 所有影格共用同一個視角；各站視角不同會讓 3D 與影格對不上 |
| 5 | 直線段每 5–8 m 一站，**轉角前後各放一站** | 轉角只放一站，路徑會切內角 |
| 6 | 第一階段請排在**同一樓層** | 平面視窗只有一張平面圖；跨層請拆成兩包 |
| 7 | 在 `WALK-01` 把視覺樣式（著色或寫實）、陰影、剖面框、隱藏類別調到最終狀態 | 影格用的暫存視圖複製自第一站 |
| 8 | 另外準備一個平面視圖（裁剪框框住走動範圍）與一個剖面視圖，記下視圖名稱 | 匯出時用名稱指定 |
| 9 | 排完存檔 | 交付包預設放在專案檔同一個資料夾 |

建議單段 8–15 站。每段預設 24 格，10 站約 217 格。

### 由 AI 直接建站（`create_camera_stations`）

上面 9 條規則預設使用者已經在 Revit 裡手動排好站。若專案還沒有任何站點（例如全新專案、或使用者直接說「幫我排幾個人視高的相機站點」），AI 可以改用 `create_camera_stations` 從零建立，不需要使用者先手動放相機：

- **輸入**：每站給眼點 `eye`（公尺）＋朝向（`target` 目標點或 `fwd` 方向向量，二擇一）；`name` 不給就依 `stationPrefix`／`startNumber` 自動編號（`WALK-01`、`WALK-02`…，符合規則 1 的命名格式）。
- **眼高預設**：本工具不自動加眼高，`eye.z` 就是最終相機高度——請依規則 3 自行算好（樓板面 +1.55 m 是常見基準，樓梯／坡道／挑空可依實際高程給不同值）。
- **視角預設**：`fovYDeg=50`、`aspect≈16:9`（1.7778），對應規則 4「各站視角一致」——批次建立時所有站點會套用同一組 FOV，天生滿足這條規則，不需要再手動複製視圖對齊。
- **與手動排站的關係**：`create_camera_stations` 建立的視圖與手動用「相機」工具放的站點是同一種東西（`View3D`、`IsPerspective=true`、名稱符合前綴＋數字），兩者可以混用——例如 AI 先批次建一輪初稿，使用者再手動微調個別站點，或用第 3 節的 `apply_camera_stations` 整批回寫。
- **`copySettingsFromViewName`**：若專案已有一個調好視覺樣式（著色/寫實、陰影、隱藏類別）的 3D 視圖，指定它可以讓新站沿用同一套顯示設定，呼應規則 7；不指定則用預設 3D 視圖類型新建，仍要事後手動調整顯示設定。
- **`overwrite`**：預設 `false`，同名視圖已存在時略過並在 `skipped` 附原因，不會誤改使用者已經調好的站點；確認要用新座標覆蓋既有站點才設 `true`。
- **建完務必接著跑**：`create_camera_stations` 只建立/改寫相機，**不會**自動檢查間距、跳號、跨層——建完第一件事永遠是回到步驟 1 的 `get_camera_stations`，走一次完整的 warnings 檢查清單，再進步驟 2 的匯出流程。

---

## 2. 匯出流程（四步，逐步確認）

### 步驟 1：檢查站點 `get_camera_stations`

先看回傳的 `warnings`，常見訊息與處理：

| warnings 內容 | 處理 |
|---|---|
| 不是透視視圖 | 用相機工具重建該站，或切換為透視 |
| 各站垂直視角不一致 | 回到規則 4，把各站裁剪區域設成一樣大 |
| 各站眼高差 N m | 平路就統一眼點高度；樓梯、坡道、挑空的高低差是正常的，可忽略 |
| 站號跳號 | 確認沒有漏站，或重新命名 |
| 相距超過 15 m | 中間補站 |
| 裁剪區域未置中 | 重設相機；或匯出後用網頁校準模式修正 |

> AI 協作斷點：把站數、路徑概略長度與 warnings 複述給使用者，確認後再進下一步。

### 步驟 2：建立交付包 `export_walkthrough_package`

必填 `planViewName`，建議填 `sectionViewName`。會產生：

```text
WalkPackage_YYYYMMDD_HHMM/
  index.html 與網頁程式     雙擊開啟
  plan.png  section.png    匯出時暫時關閉裁剪框線與註解裁剪，讓圖邊界等於裁剪框
  manifest.json  data.js   路徑、相機、平面與剖面的像素對位
```

這一步還沒有影格與 3D 模型，網頁會顯示「缺格」占位圖，這是正常的。

### 步驟 3：逐格匯出影格 `export_walkthrough_frames`

- 預設一次跑完：MCP server 每 24 格送一批給 Revit，直到完成。
- **Revit 在匯出期間會暫時沒有回應**；每格約數秒，200 格可能要 10 分鐘以上。進度寫在 `%APPDATA%\RevitMCP\Logs\RevitMCP_日期.log`。
- 中斷後用 `startIndex` 續跑，已完成的格不會重做。
- 匯出時會建立暫存視圖 `WalkRender_時間戳`，全部完成後自動刪除；中途失敗會保留供續跑。
- 每一批（而非每一格）留下一筆復原紀錄；單批最多跑 4 分鐘，超過就自動分到下一批。

### 步驟 4：匯出 3D 模型 `export_model_gltf`

- 預設沿用第一站視圖的可見性（隱藏的類別不會匯出），暫時切成等角並關閉裁剪後匯出。
- 只有材質顏色與透明度，**沒有貼圖**。3D 視窗的角色是定位與講解；寫實畫面看影格視窗。
- **預設 `bounds="path"`：依交付包的相機路徑自動算出剖面框**（水平外擴 `marginMeters` 預設 25 m、垂直 `belowMeters` 2.5 m／`aboveMeters` 8 m），只匯出走動看得到的範圍。這是大模型的標準解法——用**空間裁切**換取**類別完整**，門窗、帷幕牆、家具全部保留。挑空或大廳可調大 `aboveMeters`；模型仍太大就調小 `marginMeters`。
- `bounds="none"` 才會完全沿用來源視圖範圍（舊行為），只在刻意要整棟模型時使用。

> 🔴 **撞到三角面上限時不要改用「只含部分類別」的來源視圖**（例如某個只有樓板與牆的協調視圖）。那確實能壓低面數，但 3D 視窗會變成一堆灰盒子——沒有門窗就看不出開口、沒有家具就分不出室內外，等於讓 3D 視窗失去它唯一的用途。先調 `bounds` 的範圍參數；真的還是太大，才在來源視圖隱藏家具／植栽／機電等**單一類別**，並在回報時明講少了什麼。

---

## 3. 站點微調與回寫（`apply_camera_stations`）

排站當下很難一次到位；更常見的做法是先出一版交付包、在網頁上邊看邊調站點，調完再整批寫回 Revit。

### 流程

1. 在離線網頁的「站點編輯模式」拖調站點位置／朝向（與第 5 節的 FOV 校準模式是不同功能：校準模式調的是視角換算，站點編輯模式調的是站點本身的眼點與朝向）。
2. 網頁「下載站點」，存成 `stations.json`（每筆 `{name, eye:[x,y,z], fwd:[x,y,z], up:[x,y,z]}`，座標預設為交付包模型座標，即已扣除 `manifest.units.originOffset`）。
3. 呼叫 `apply_camera_stations`，先用預設 `mode="preview"`：只回報每站的 `moveMeters`／`heightDeltaMeters`／`headingDeltaDeg`，**不改動 Revit 模型**。把位移量複述給使用者確認，離譜的位移（例如整段跳到另一個房間）通常代表座標系或站名對錯。
4. 確認合理後，帶 `mode="apply"` 再呼叫一次，才會真的把 `WALK-NN` 視圖的相機方位寫回去。
5. 寫回後**交付包裡的影格與路徑資料是舊的**——相機動了，但已匯出的 PNG 影格還是照舊相機拍的，manifest 也會被標記警告。必須重新執行 `export_walkthrough_package`（重新取樣路徑、重算平面/剖面對位）與 `export_walkthrough_frames`（重出影格），網頁畫面才會跟新相機一致。

### 參數判讀

| 參數 | 判讀 |
|---|---|
| `coordinates` | 提供 `packageDir` 時預設 `"package"`（交付包模型座標，`stations.json` 的原生座標系）；沒有 `packageDir` 就只能用 `"revit"`（Revit 專案座標，公尺，未扣 offset）——手動湊 Revit 座標容易出錯，優先走 `packageDir` 這條路 |
| `createMissing` | 網頁編輯模式一般不會新增站，只調整既有站；只有刻意在網頁上多加一個站名才需要 `true`，且需要 Revit 裡至少存在一個既有站點視圖當樣板 |
| `unlockLockedViews` | 站點視圖若曾被鎖定方位（例如手動精修過），預設會略過並在 `warnings` 提示；確認要用網頁版覆蓋才開 |

### 眼高不一致的實務判讀（呼應第 1 節規則 3 的修正）

- **平路走動**：眼高忽高忽低是缺陷，代表排站時沒對齊，`get_camera_stations` 的「各站眼高差」warning 要處理。
- **樓梯、坡道、看挑空**：眼高本來就該隨梯級/坡度變化，這種差異是寫實的一部分，不要為了消除 warning 硬拉平——拉平了反而看起來像懸浮。判讀原則：先看站點之間是否有實體高程變化（樓梯、坡道、跳層看挑空），有就允許眼高差；沒有就當缺陷修。

### 已知限制

- 只認透視視圖（`View3D.IsPerspective`）；等角視圖會被略過並記在 `warnings`。
- 一次呼叫只處理 `stations` 陣列裡列出的站；沒列到的站點視圖不受影響。
- `mode="apply"` 是整批一個 `TransactionGroup`，中途失敗會整批復原，不會留下半套修改。

---

## 4. 深度圖匯出（AI 風格化前置）（`export_walkthrough_depth`）

第二階段 AI 風格化（見第 7 節）需要逐格深度圖才能讓 video-to-video 工具鎖住鏡頭運動、避免物件邊緣漂移。本工具是純外部程序：呼叫本機 Node.js＋headless Chrome，讀取交付包的 `model.glb` 與相機路徑，離線渲染逐格深度圖存到 `packageDir/depth/`，寫回 manifest 的 `depth` 區塊。不碰 Revit API，也不會動到 `frames/` 既有影格。

### 前置條件

- 交付包需已用 `export_model_gltf` 匯出 `model.glb`（深度圖是從 glTF 幾何離線渲染，不是從 Revit 截圖）。
- 本機需安裝 Node.js（建議 LTS）與 Chrome／Chromium；找不到時錯誤訊息會附安裝指引。

### 何時要重跑

- **相機路徑變更**：重新執行 `export_walkthrough_package`（例如站點微調回寫後，見第 3 節步驟 5）之後。
- **模型變更**：重新執行 `export_model_gltf` 匯出新的 `model.glb` 之後。
- 只要上述兩者之一動過，舊的深度圖就與新路徑／新模型不對齊，必須重新執行 `export_walkthrough_depth`。

### 參數判讀

| 參數 | 判讀 |
|---|---|
| `start` / `count` | 大量影格時可分批跑，避免單次執行時間過長；不填則從頭渲染到全部完成 |
| `encoding` | `gray8`（8 位元，檔案小）或 `gray16`（16 位元，深度精度高，適合大範圍場景）；未指定用腳本預設值 |
| `overwrite` | 預設 `false`，只補渲染缺格，可用於中斷續跑；確認要整批重渲染才設 `true` |
| `chromePath` | 本機有多個 Chrome/Chromium 安裝、或自動偵測失敗時才需要指定 |

### 已知限制

- 執行時間依影格數與解析度而定，可能需數分鐘到數十分鐘；單次呼叫有 30 分鐘逾時上限，超過會強制中止並在錯誤訊息附最後幾行 stderr，供判斷卡在哪一步。
- 渲染品質取決於 `model.glb` 的完整度：來源 3D 視圖若隱藏了某些類別（見第 6 節「大模型」的減量建議），深度圖也會缺少那些物件的深度資訊。

---

## 5. 網頁操作

| 操作 | 效果 |
|---|---|
| 拖時間軸 / 空白鍵播放 / 左右鍵 | 換格，四窗同步 |
| 時間軸上的 `WALK-NN` 刻度 | 直接點一下跳到該站；目前所在站點會高亮，讀數會顯示「WALK-02 → WALK-03」這樣的所在段落 |
| 滑過時間軸 | 浮出該位置的影格縮圖與距離，找位置不必盲拖 |
| 拖平面上的相機圓點，或點路徑附近 | 跳到最近的路徑位置 |
| 平面滾輪 / 拖曳空白處 | 縮放平移 |
| 3D「自由視角」 | 暫時離開路徑自由旋轉，不影響時間軸 |
| 影格版本下拉選單 | 切換 base 與其他版本（例如夜景、AI 風格） |
| 按 C 進入校準模式 | 3D 半透明疊在影格上，拖滑桿調視角到門窗邊線重合，可儲存與下載校準後 manifest |

剖面視窗在第一版是唯讀：相機不在剖面平面上時，標記是投影位置，旁邊會標「距剖面 N m」。

---

## 6. 已知限制與判讀

1. **視角換算**：透視視角由裁剪框推算（manifest `camera.source = cropbox-derived`）。若 3D 與影格邊線有偏移，用校準模式修正一次即可。
2. **平面對位前提**：矩形裁剪框。非矩形裁剪會出 warnings，標記可能偏移。
3. **樓層高程**：`levelElevation` 以專案基準點為準，僅供顯示；若專案基準點高程不為 0，數值與內部座標會差一個常數。
4. **大模型**：三角面超過 200 萬會**提前停止**——注意這代表 glb **不完整且截斷點由收集順序決定**（可能砍掉相機正前方的牆），不是「只是少一點細節」，必須重匯而不是將就。首選解法是步驟 4 的 `bounds="path"` 空間裁切；glTF 超過 150 MB 則不會嵌入網頁（3D 視窗會空白）。
5. **跨樓層**：網頁會顯示「路徑跨樓層，平面標記僅供參考」橫幅。

---

## 7. 第二階段接口（本版未實作）

- **AI 風格化**：把 `frames/base/` 的影格（必要時加上第 4 節 `export_walkthrough_depth` 產出的深度圖）送到保留鏡頭運動的 video-to-video 工具，輸出同名同數量圖片放 `frames/<版本名>/`，並在 manifest `variants` 加一筆，網頁就能切換。深度圖匯出本身已實作（第 4 節）；本版未實作的是「呼叫 video-to-video 工具＋寫回 variants」這段串接。2026-09-15 調研時官方文件明確支援「保留鏡頭換風格」的候選：本機 Wan2.2 + VACE（需 24 GB 以上顯示卡）、雲端 Luma Modify Video、Runway Aleph。只吃參考圖片、不吃參考影片的工具（例如 Google Flow）無法保持相機一致，不適用。價格會變動，採用前請重新查證。
- **Revit 跟著跳**、**設計方案 A/B 左右對照**、**DWG 向量平面**：規劃中。
