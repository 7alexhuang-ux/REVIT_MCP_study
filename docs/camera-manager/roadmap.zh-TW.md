# 相機管理面板 路線圖

> 狀態標記：✅ 完成（已編譯、已部署或待部署）｜🔄 進行中｜⏳ 規劃中。「完成」不等於「已實機驗證」——實機驗證狀態看最後的「未驗證清單」。
> 現行行為見 [spec.zh-TW.md](spec.zh-TW.md)；決策見 [adr.zh-TW.md](adr.zh-TW.md)；歷史見 [changelog.zh-TW.md](changelog.zh-TW.md)。

## 原始三階段（2026-09-24 使用者拍板）

| 階段 | 內容 | 狀態 |
|---|---|---|
| 1. 停靠面板 | 面板、清單、就地編輯、雙向連動、人視高預設值、複製到其他樓層、匯出 Excel、③ 位置圖＋視野扇形、並排／還原、預覽視窗 | ✅ 完成（使用者實機回饋多輪，未完成最終驗收） |
| 2. Key Plan 與相機標記 | 一鍵 4 視圖（平面／剖面／等角）、B 型標記、表現法、室內／室外、每層總覽、A 型族群標記、拖標記連動相機 | ✅ 完成（Wave 2 於 2026-09-25 補齊；未實機驗證） |
| 3. 出圖與版本 | 一鍵排圖紙、相機視角版本、視覺精修 | ✅ 完成（Wave 3 於 2026-09-25；未實機驗證） |

## 收尾波次（2026-09-25 起）

### Wave 1｜MCP 測試工具　✅ 完成（2026-09-25）

讓 AI 能自動測試面板背後的同一套服務程式碼（D-023）。

| 工具 | 狀態 |
|---|---|
| `list_cameras` | ✅ |
| `run_camera_keyplan` | ✅ |
| `set_camera_pose` | ✅ |
| `copy_camera_to_level` | ✅ |
| `get_view_extents` | ✅ |

- ✅ 面板內「開啟並排」「編輯請求組裝」移到服務層共用。
- ✅ 規格書拆成四份文件。
- ⚠️ 工具本身尚未在 Revit 內實際呼叫過（見未驗證清單）。

### Wave 2｜Key Plan 完整版　✅ 完成（2026-09-25，已編譯、未實機驗證）

| 項目 | 狀態 |
|---|---|
| 1. 每層總覽平面 `Key Plan-總覽-<樓層名>`（④ ▾ 更新樓層總覽、一鍵 4 視圖自動更新、MCP `run_camera_overview`） | ✅（spec §10.10、D-028） |
| 2. A 型相機標記（族群「RevitMCP 相機標記」＋扇形 DirectShape、透視圖隱藏子類別、⋯ 相機標記 B／A／A+B） | ✅ 部分：扇形**不是**族群參數驅動，改用 DirectShape；族群不隨俯仰角傾斜（D-026） |
| 3. 拖標記 → 相機 | ✅ 部分：只支援 A 型實例；B 型詳圖標記沒有群組，不支援（D-027） |

- MCP 工具數 217 → 218（`run_camera_overview`）。

### Wave 3｜出圖與版本　✅ 完成（2026-09-25，已編譯、未實機驗證）

| 項目 | 狀態 |
|---|---|
| 1. 一鍵出圖紙 `Key Plan-<相機名>`（④ ▾、右鍵、MCP `create_camera_sheet`；④ ▾ 圖紙圖框 ▸） | ✅（spec §10.13、D-029） |
| 2. 視角版本（右鍵 視角版本 ▸ 儲存／還原／刪除；MCP `camera_version`） | ✅（spec §11.5、D-030） |
| 3. 視覺精修：過時 ToolTip 全面更新、右鍵選單加 ToolTip、8 px 間距、清單提示縮短、⑥ 狀態列保留最近一次動作＋時間 | ✅ 部分：字體沿用既有 `Segoe UI, Microsoft JhengHei UI` 12／11／13 px，未另調；主要／次要按鈕沿用既有 Primary／Secondary 樣式 |

- MCP 工具數 218 → 220（`create_camera_sheet`、`camera_version`）。
- 版面比例（左 60%、右欄三等分）與圖紙邊界 15 mm 是常數／本機設定，沒有 UI；實測後再決定要不要開放。

### MCP 自測第四輪修正　✅ 已編譯、待 MCP 複測（2026-09-26 01:54）

第三輪實測：移動路徑方向／原點正確但深度偏差 2374 mm → 退到刪除重建 → `InvalidObjectException` 整筆回滾。第四輪 3 項已改、已編譯；**新 DLL 尚未部署**（Revit 執行中）。

| # | 情境 | 複測方式 |
|---|---|---|
| a | 相機移 1 m＋轉 180°（移動路徑） | 用 `set_camera_pose` 把相機水平移 1 m、yaw 轉 180° → `run_camera_keyplan`：`Decisions.SectionApplyLog` 含「已旋轉／已移動／已沿深度移動」、**不含「移動路徑失敗」「刪除重建」**；`SectionExtentDeviationMm` ≤ 5（深度偏差 0）；剖面 Id 不變 |
| b | 刪除重建路徑（不得加除錯旗標） | 觸發方式：讓移動路徑失敗且剖面不在圖紙上——在 Key Plan 平面選取該剖面標記按「釘選（Pin）」（被釘選的元素 Rotate/MoveElement 會失敗；此行為未實測，若沒失敗請記下 log 回報），再移動／旋轉相機並 `run_camera_keyplan`（**不要**用刪除剖面標記來觸發——那會把剖面視圖一起刪掉）：`SectionApplyLog` 含「已刪除舊剖面 … 並重建為 …」、**沒有 `InvalidObjectException`、沒有回滾**；`Result.SectionViewId`＝新 Id；新剖面有 ES 連結（再跑一次會走移動路徑而非新建第二張）、B 標記畫在新剖面上 |
| c | 圖框線讀不到的圖紙 | `create_camera_sheet`（E1 30 x 42）：`DrawAreaSource` 含「未偵測到圖框線 → 四邊內縮改為 … ≈43 mm」；截圖透視圖不壓左側內框 |

### MCP 自測第三輪修正　✅ 已編譯、第四輪修正中（2026-09-26 01:45）

第二輪已由主線 MCP 驗證通過：最近房間 fallback、樣板層隱藏子類別、B 模式刪 A、不再誤判「手動移過」、圖紙不 Overflow、標題欄 20 % 退路、透視複本無標記、總覽裁剪讀回＝預期。第三輪 5 項已改、已編譯；**新 DLL 尚未部署**（Revit 執行中）。

| # | 問題 | 修正 | 複測方式 |
|---|---|---|---|
| 1 | （回歸）更新既有剖面：CropBox 保留舊 Transform、只換範圍 → 內容跑到樓梯核／下層 | 旋轉／移動 viewer → 讀回座標系設範圍 → 後檢 5 mm；做不到且不在圖紙上才刪除重建（D-037） | 對「Unit 304 - Toward Core」連跑兩次 `run_camera_keyplan`（第二次換 `sectionMode`）：`Decisions.SectionApplyLog` 含「已旋轉／已移動」或「新建」、`SectionExtentDeviationMm` ≤ 5；`Section.Extents.CropBox` 的 Transform 原點＝新原點；`capture_view_image` 看不到鄰近樓梯核與下層 |
| 2 | 圖框幾何 0 條線 | 三組 Options、SymbolGeometry 退路、實體邊、類型統計 | `create_camera_sheet`：`DrawAreaSource` 的「圖框幾何線 N 條」N > 0，且列出採用的 Options 與幾何類型；N 仍為 0 時記下類型統計回報 |
| 3 | 圖紙約 40 % 空白、Key Plan 被迫 1:75 | 透視格＝實際視埠寬；右側自動版面（D-035） | `create_camera_sheet`：`DrawAreaSource` 含「透視格寬」與「右側版面＝…（試過 …）」；Key Plan 比例比 1:75 細或版面空白明顯減少；`Overflow=false`；截圖檢查 |
| 4 | 總覽裁剪切到「Unit 302 - Toward Interior」扇形 | 共用半徑函式＋讀回標記外接框外擴（D-036） | `run_camera_overview(L3)`：`ExtentsMode` 列每台半徑與「標記讀回外接框…皆在裁剪內」或「已外擴」；截圖扇形完整 |
| 5 | A 扇形子類別讀回「讀不到扇形幾何」 | Regenerate 後讀、Fine＋NonVisible、展開實例 | `run_camera_keyplan(markerMode="A")`：`AMarkerLog`「不符 0」、沒有「讀不到扇形幾何」 |

使用者實測加一項：圖紙上的透視圖大小在 Revit 內以「裁剪區域大小 → 縮放（鎖定比例）」手動調整後，重跑一鍵出圖紙時右側 Key Plan 會跟著重排（見下方使用者實測 #7）。

### MCP 自測第二輪修正　✅ 已由 MCP 複測（2026-09-25 22:50）

第一輪已由主線 MCP 驗證通過：Scope Box 解除、房間 Level 當樓層、圖紙透視複本、視埠類型挑選。第二輪 6 項全部已改、已編譯；**新 DLL 尚未部署**（Revit 執行中）。

| # | 問題 | 修正 | 複測方式 |
|---|---|---|---|
| 1 | 相機在未放房間的區域（Loft Unit 旁），`Room.Log`＝`none (bboxHits=0, xyHits=0)` | 最近房間 fallback：水平 ≤ 1.5 m、垂直含眼睛或 ≤ 2 m（spec §10.2 步驟 3a） | `list_cameras`：該相機 `IndoorOutdoor`＝室內、`Room.Log` 含「最近房間 d=」且 d ≤ 1500 mm |
| 2 | 16 張透視圖因樣板控制 V/G 無法隱藏「相機標記」 | 改在樣板隱藏子類別（D-033） | `run_camera_keyplan(markerMode="A")` 的 `AMarkerLog` 含「改在視圖樣板隱藏：「…」」、失敗 0；`capture_view_image`（該透視圖與 `…-Key Plan 圖紙`）看不到相機體與扇形 |
| 3 | 圖紙透視複本看得到扇形 | GraphicsStyle 取得＋讀回驗證 | `AMarkerLog`「子類別樣式 gs=<正數>，不符 0」；複本截圖無紅色扇形 |
| 4 | `markerMode=B` 留著 A 實例／扇形 | 一鍵 4 視圖刪除本相機 A 標記 | 先 `markerMode="A"` 再 `markerMode="B"`：`AMarkerLog`「已刪除本相機 A 實例／扇形 2 個」、三張視圖 `TaggedElements` 無 `MarkerA`／`MarkerAFan` |
| 5 | 第二次跑同一相機 `SectionPreserved=true` | ES 存讀回值、容許 5 mm／0.5° | 同一相機連跑兩次 `run_camera_keyplan`：都 `SectionPreserved=false`；手動移剖面後再跑＝true 且 log 有兩組值 |
| 6 | 圖紙壓標題欄、透視圖太小、平面超出 | 扣標題欄、55 % 版面、列高依內容、Key Plan 自動比例、透視複本等比放大（D-034） | `create_camera_sheet`（G002 那台）：`DrawAreaSource` 有標題欄判定、右緣在標題欄線左側；透視視埠約佔格子 90 %、`Note` 寫「FOV 不變」；平面 `Note` 有比例變更；四個視埠 `Overflow=false`；截圖檢查 |

### MCP 自測第一輪修正　✅ 已由 MCP 複測（2026-09-25 22:20）

主線以 MCP 在 Revit 2024（公開範例 Snowdon Towers）自測後回報 6 項，全部已改、已編譯；**新 DLL 尚未部署**（Revit 執行中），以下皆「待 MCP 複測」。

| # | 問題 | 修正 | 複測方式 |
|---|---|---|---|
| 1 | 複製來的 Key Plan 平面／樓層總覽鎖在 Scope Box，裁剪停在 78.6×64.0 m、比例被迫 1:200、圖紙 Overflow；圖紙標題沿用來源 | 每次先解除 Scope Box、專案北、轉正、圖紙標題改自己的，再設裁剪；讀回比對 > 1 % 記 WARNING；總覽回報讀回值（spec §10.4、§10.10） | `run_camera_keyplan` → `get_view_extents(平面)`：`ScopeBox=null`、`TitleOnSheet`＝「Key Plan 平面」、裁剪≈房間 +0.6 m；`Warnings` 為空；`run_camera_overview`：`ExtentsMm`≈`IntendedMm` |
| 2 | 室內相機（閣樓、廚房眼睛低於房間底、大廳）被判室外 | 房間搜尋改輪廓＋垂直範圍（≤ 2 m 容差）（D-031） | `list_cameras`：三台 `IndoorOutdoor`＝室內、`Room.Log` 的 `foundBy` |
| 3 | 室內相機樓層被判成女兒牆 | 室內樓層＝房間的 Level（D-031） | `list_cameras`：`LevelSource=room`、`Level`＝房間樓層；`HeightAboveLevelCm` 以該樓層計算 |
| 4 | 剖面 Key Plan 出現其他剖面標頭（「-/---」） | 平面／剖面／總覽逐元素隱藏其他 viewer，隱藏立面／詳圖索引類別 | `get_view_extents(剖面)`：`VisibleViewers` 全 0；平面：`Sections`＝1（本相機） |
| 5 | 一鍵出圖紙略過已在 3D 圖紙上的透視圖；`ViewportTypeLog` 自相矛盾 | 出圖用透視複本並同步（D-032）；視埠類型候選改聯集、列「實際使用」 | `create_camera_sheet`：`Perspective.Placed`＝透視複本（新建）、4 個視埠「新放置」；重跑＝沿用並同步；`list_cameras` 不含複本 |
| 6 | 測試工具不足 | `run_camera_keyplan` 加 `markerMode`；`get_view_extents` 加 Scope Box／方位／圖紙標題／可見 viewer 數（工具數仍 220） | `run_camera_keyplan(markerMode="A")`：`MarkerModeUsed=A`、`AMarker` 非 null |

### 下一步｜MCP 自測　⏳（Revit 重新啟動、面板按過「重新整理」、MCP 已連線後，由主線依序執行）

前置：Revit 2024 開一個有透視相機、有圖框類型的測試模型（公開範例或「實測專案」的複本，**不**用正式檔）；確認 MCP 工具清單含本功能 8 支工具。每一步的「預期」不符就停下來記錄，不往下跑。

| # | 呼叫 | 預期結果 |
|---|---|---|
| 1 | `list_cameras` | `Count` ≥ 1；每台有 `Level`、`HeightAboveLevelCm`、`Fov.Source`（記下 C／A／B）、`IndoorOutdoor`；`BaseMap.Step` 0–4 之一。選一台室內相機 X、一台室外相機 Y（若有）供後續使用 |
| 2 | `run_camera_keyplan(cameraId=X)` | `Success=true`；`Plan`／`Section`／`Iso` 都有 id；`Decisions.IndoorOutdoor`＝室內、`Room.Id` 非 null；平面 `TaggedElements` 含 MarkerPlan；`OverviewViewId` 非 null |
| 3 | `get_view_extents(viewId=步驟 2 的平面)` | `CropBox.Active=true`；裁剪框約＝房間外接框 ±600 mm（與步驟 2 的 `Room.Log` 對照）；`HiddenCategories` 含軸線／尺寸；`ViewTemplate=null` |
| 4 | `get_view_extents(viewId=步驟 2 的剖面)` | 裁剪框垂直範圍包含平面剖切面高程；`ViewOwnedElements.DetailLines` > 0（標記＋樓層記號） |
| 5 | `run_camera_keyplan(cameraId=X, sectionMode="sight")` 再 `sectionMode="room"` | 兩次 `Decisions.SectionModeUsed` 分別為 AlongSightLine／AlongRoomLongAxis；`SectionPreserved=false` |
| 6 | `run_camera_keyplan(cameraId=X, isoMode="current")` 再 `"full"` | `IsoExtentMode` 對應；current 的 `Iso.Extents.SectionBox` 上緣 ≤ full 的上緣 |
| 7 | `set_camera_pose(cameraId=X, heightAboveLevelCm=150, yawDeg=原值+30)` | `After.HeightAboveLevelCm=150`、`After.YawDeg`＝原值+30（±0.1）；XY 不變 |
| 8 | `list_cameras(nameContains=X 名稱)` | 同步驟 7 的值（面板服務層讀回一致） |
| 9 | `camera_version(cameraId=X, action="save", name="MCP 測試 A")` | `Versions` 含「MCP 測試 A」，`YawDeg` ＝步驟 7 之後的值 |
| 10 | `set_camera_pose(cameraId=X, yawDeg=原值)` → `camera_version(action="restore", name="MCP 測試 A")` | restore 後 `Camera.Current.YawDeg` ＝步驟 9 存的值（±0.1）、`EyeMm` 相同（±1 mm）；`Warnings` 為空（有值＝記下裁剪框／遠裁剪還原問題） |
| 11 | `camera_version(cameraId=X, action="save", name="MCP 測試 A")`（同名） | `Message` 為「已覆蓋」；版本數不增加 |
| 12 | `create_camera_sheet(cameraId=X)` | `Sheet.Created=true`、`Name`＝「Key Plan-<X 名>」；`TitleBlock.Source` 為「最常用…」或「面板設定」；4 個 `Viewports` 都是「新放置」；每個 `CenterMm` 落在自己的 `CellMm` 內；記下 `Overflow` 與 `ViewportTypeLog` |
| 13 | `create_camera_sheet(cameraId=X)`（重跑） | 同一個 `Sheet.Id`、`Created=false`；4 個都是「保留原位」；不新增圖紙 |
| 14 | `get_all_sheets` | 只多一張「Key Plan-<X 名>」 |
| 15 | 新相機 Z（`copy_camera_to_level(cameraId=X, levelName=另一層)`）→ `create_camera_sheet(cameraId=Z)` | `RanFourViews=true`（自動先建 Key Plan）；新圖紙 4 個視埠「新放置」 |
| 16 | `create_camera_sheet(cameraId=X, titleBlockTypeId=另一個圖框類型)` | `TitleBlock.Source`＝指定；`Warnings` 含「已把既有圖紙的圖框換成…」 |
| 17 | 室外相機 Y：`run_camera_keyplan(cameraId=Y)` | `IndoorOutdoor`＝室外、`SectionModeUsed`＝AlongSightLine、`StoreyTop.Outdoor=true` |
| 18 | `run_camera_overview(all=true)` | 每個有相機的樓層一筆、`Error=null`；`TaggedElements` 的 MarkerName 數＝該層相機數 |
| 19 | `camera_version(cameraId=X, action="delete", name="MCP 測試 A")` → `list` | 版本清單不含「MCP 測試 A」；相機姿態不變 |
| 20 | 收尾 | 用 Revit 復原或關檔不存，測試模型不保留本輪變更 |

### 最後｜使用者實測清單（只有人能判斷的項目）　⏳

1. 面板手感：清單點名稱開 4 視圖、點數字直接改（Enter／Esc）、樓層下拉複製、右鍵選單，四種操作不會互相搶。
2. ③ 位置圖：滾輪縮放、拖曳平移、點相機圓點，手勢不衝突；標記對位目視無偏移（開「框」檢查）。
3. 300 px 寬：① 工具列、④ 動作列、② 下方提示都不被截斷、不出現水平捲軸；8 px 間距看起來整齊。
4. ⑥ 狀態列：做完一個動作後，結果訊息會留著（不被自動刷新蓋掉）；時間戳看得懂。
5. ToolTip：滑過每個按鈕／選單項目，說明與實際行為一致（特別是 Key Plan 樣式、剖面方向、等角範圍）。
6. Key Plan 畫面：淡化程度、外牆輪廓清楚度、B 標記大小與透明度、簡化樓層記號好不好讀。
7. **一鍵出圖紙**：用自己常用的圖框出一張——透視圖大小是否合適、右欄三張是否壓到圖框標題欄、視埠標題是否顯示、整體是否可以直接列印。**透視圖大小程式改不動**（放大裁剪框會改變 FOV）：請選透視視埠 → 裁剪區域大小（Size Crop）→「縮放（鎖定比例）」手動放大，再重跑一鍵出圖紙讓右側 Key Plan 重排，確認版面合理。
8. 圖紙圖框下拉：換一個圖框再出圖，既有相機圖紙的圖框是否正確換掉、視埠位置是否仍合理。
9. **視角版本**：在透視圖裡轉一個角度 → 右鍵存成「A 案」→ 再轉 → 存「B 案」→ 切回 A 案：畫面（含裁剪框範圍）是否完全回到原樣；Key Plan 標記有沒有跟著動。
10. 視角版本存檔後關閉再開啟（或請同事開同一檔）：版本還在、可以切換。
11. A 型標記（⋯ 相機標記 → A）：族群能否建立、平面／3D 看得到、透視圖看不到、拖 A 標記相機跟著動。
12. **Enscape**：A 型標記（相機體＋扇形）是否出現在 Enscape 畫面（程式只在透視圖隱藏子類別）。
13. 並排視窗在「使用索引標籤檢視」開／關兩種設定下的實際表現。
14. 長時間使用是否再崩潰（開檔時面板開著／關著兩種情況）；若崩潰，把 `%APPDATA%\RevitMCP\Logs\CameraManagerCrash_*.log` 交給主線。

## 未驗證清單（2026-09-25，Wave 3 後）

以下行為已寫成程式並通過編譯，但**沒有在 Revit 內實際確認過**：

| 項目 | 備註 |
|---|---|
| 8 個 MCP 測試工具的實際回傳（含 Wave 3 的 `create_camera_sheet`、`camera_version`） | 三波交付時 Revit 都未連線 MCP，只做了編譯與註冊檢查 → 上方「MCP 自測」 |
| FOV 候選 C／A／B 哪個等於 Revit 真正的視野角 | 需以新放相機（預設 50° 鏡頭）比對 log |
| 透視圖內滑鼠導覽是否觸發 DocumentChanged | 保留「從透視圖同步」按鈕 |
| 拖曳透視裁剪框後平面扇形是否真的更新 | 09-25 log 證實事件會觸發；扇形變化未確認 |
| `ExportImage` 在 TransactionGroup 開著時是否可用 | 有備援路徑 |
| 底圖對位是否完全無偏移 | 只驗證長寬比；以「框」模式目視 |
| 並排（TileViews）在索引標籤檢視模式下 | Revit 設定限制，可能只切分頁 |
| `ZoomToFit` 會不會移動透視相機 | 採防護式還原 |
| `FilledRegion` 表面透明度的元素取代 | 依社群已解答帖推斷 |
| 剖面比例門檻參數值的語意（5000 → … → 200） | 依名稱解析參數 |
| Display Model 數值 0／1／2 對應 | 以 `AsValueString` 記 log |
| 房間 BoundingBox 頂是否反映上限＋上限偏移 | log 另記 UnboundedHeight 供核對 |
| 平面剖面線看不到的真正原因（比例門檻／類別／隱藏／半色調） | 逐項記 log，待實機 |
| 連結模型「依主體視圖」後可見樓層計數是否反映實際顯示 | Revit 2024+ API |
| 簡化樓層記號在 Level 改高程／改名後自動重畫 | 去抖交易「更新 Key Plan 樓層線」 |
| 相機放置平面（步驟 0）的記錄時序 | DocumentChanged 讀作用中視圖 |
| 平面扇形在俯仰 ≠ 0 時與 Revit 視錐線的投影差 | 未實機比對 |
| 標記在極端俯仰角（接近 ±90°）的退化處理 | 有 try/catch 跳過 |
| 清單常駐 TextBox 在停靠面板內是否確實收到鍵盤輸入 | log 記第一個按鍵／文字輸入 |
| 300 px 寬度下提示文字與欄寬是否不出現水平捲軸 | 只做常數估算（Wave 3 提示縮短到約 20 字） |
| 開檔後無操作崩潰是否已根治 | 直接起因已修（ComboBox 樣式）；診斷 log 常駐 |
| **Enscape 是否排除 A 型標記**（相機體＋扇形 DirectShape） | **使用者實測**：程式只在透視圖隱藏子類別「相機標記」；Enscape 讀的是視圖可見性還是整個模型，未查證 |
| A 型族群能否在實機建立（`NewFamilyDocument`、`AddParameter`、`NewExtrusion`、`SaveAs`、`LoadFamily`） | 本機 2024 樣板只有 `English\Metric Generic Model.rft`（任務指定的 `English\Generic Model.rft` 不存在），程式會自動退到它 |
| A 型實例放在眼睛高度、在 Key Plan 平面上是否可見（高於剖切面） | 一般模型在剖切面以上、視圖範圍頂以下通常會顯示，未實機確認 |
| 扇形 DirectShape 指定子類別 GraphicsStyle 後，透視圖隱藏子類別是否也隱藏扇形 | 依 API 推斷 |
| 視圖樣板控制 V/G 的透視圖 | 無法隱藏子類別，只記 log |
| 拖 A 實例 → 相機（反向同步）與「相機→標記」是否互相觸發 | 以 1 mm／0.1° 門檻＋交易名稱防護，未實機確認 |
| 樓層總覽：底圖挑選、裁剪 +3 m、名稱文字位置、比例（可能退到 1:200） | 未實機 |
| `run_camera_overview` 實際回傳 | 未在 Revit 內呼叫 |
| 刪除相機後孤兒標記清理 | 依賴面板 live sync 已啟用 |
| 一鍵出圖紙：圖框外接框內縮後，右欄視埠是否壓到圖框標題欄 | 可繪區不扣標題欄（D-029 後果）→ 使用者實測 7 |
| `VIEWPORT_ATTR_SHOW_LABEL` 的整數與「是／否／多個視埠時」對應 | 先看顯示字串，取不到才以 0＝否；log 記原始值 |
| 透視圖放上圖紙後的視埠大小（裁剪框決定）與 60% 格子是否相稱 | 不改比例，只回報 Overflow |
| `ViewSheet.Create` 自動編號、`sheet.Name` 設定在實機的結果 | 未實機 |
| 視角版本還原：perspective 相機 `CropBox` 設定（含 Transform）是否被 Revit 接受 | 失敗只列警告、眼睛與方向照常套用 |
| 視角版本還原：`VIEWER_BOUND_ACTIVE_FAR` 參數在透視相機上是否存在／可寫 | 失敗只列警告 |
| 還原視角後 DocumentChanged → 標記去抖更新是否觸發 | 交易名稱「還原相機視角：…」不在忽略名單 |
| 圖紙圖框下拉清單只在「重新整理」時更新 | 新載入的圖框要再按一次重新整理 |

### 2026-09-25 22:20 MCP 自測第一輪修正（待 MCP 複測）

| 項目 | 備註 |
|---|---|
| 解除 Scope Box 後 CropBox 是否照設定生效；`PLAN_VIEW_NORTH` 設 0 是否＝專案北 | 讀回比對有 WARNING；待複測 |
| 旋轉裁剪框轉正（以 Identity Transform 設 CropBox）是否被 Revit 接受 | log 記「已轉正／未接受」 |
| 新房間搜尋對三台相機（閣樓、廚房、大廳）的判定；`ROOM_PHASE_ID` 參數是否存在 | 同 Phase 無候選會不分 Phase 重找並記 log |
| 室內樓層改房間 Level 後，清單樓層下拉（非建築樓層 Level）與 ③ 位置圖底圖挑選是否正常 | 待複測＋使用者實測 |
| 逐元素隱藏 viewer 是否誤藏本相機剖面線或視圖自身裁剪框 | 以名稱排除自身裁剪框元素（依社群做法推斷） |
| 出圖用透視複本的同步（方向／裁剪框／遠裁剪）與平面上多出的相機圖示 | 待複測 |
| 視埠類型候選聯集後是否挑到有標題的類型 | 看 `ViewportTypeLog` 的「實際使用」 |
| 大模型（數百房間）下每次幾何計算都跑房間搜尋的效能 | 未實測 |

### 2026-09-25 22:50 MCP 自測第二輪修正（待 MCP 複測）

| 項目 | 備註 |
|---|---|
| 最近房間 fallback 會不會把真正在室外（陽台外 1 m）的相機判成室內 | 1.5 m 水平容差是假設值；待實例驗證 |
| 在視圖樣板上隱藏子類別是否被 Revit 接受（`CanCategoryBeHidden` 對樣板的回傳） | log 會列「樣板也不可隱藏」 |
| DirectShape 幾何的 `GraphicsStyleId` 讀回是否等於子類別樣式 | `AMarkerLog` 會列不符數 |
| 剖面 CropBox 套用後 Revit 實際調整了多少（原點位移） | log「套用後原點被 Revit 調整 N mm」 |
| 標題欄偵測（長直線啟發式）對 Snowdon 以外圖框的準確度 | 見 `DrawAreaSource` |
| 透視複本裁剪框放大：「X/Y/Z×k」或「X/Y×k」哪個能維持 FOV | 見透視視埠 `Note`；兩者都不行會還原並警告 |
| 改 Key Plan 視圖比例時是否被樣板擋住 | 列警告 |

## 已知限制（非本功能缺陷）

- R22／R23 編譯有 11 個既有錯誤（`CommandExecutor.LinkDisplay.cs` 使用 2024+ API），另案處理。
- Project Browser「顯示相機」無對應 API，不做（D-022）。
