# 相機管理面板 規格書（Camera Manager Spec）

> 狀態：草案 v1（2026-09-24）｜適用：Revit 2024（程式碼需可編譯 R24–R26）
> 性質：Revit 原生外掛功能（停靠面板），**不是** MCP 工具、也不是 MCP App；MCP 工具數不受影響。
> 決策紀錄：三階段交付、Revit 內視窗、真的 Revit 視圖、相機標記 A＋B 都做、命名含「Key Plan」、加相機視角版本——皆由使用者於 2026-09-24 拍板。

---

## 1. 為什麼要做

| 問題 | 實際狀況（AR_CPC 實測） |
|---|---|
| 相機高度看不懂 | 性質面板的 Eye Elevation 從**內部原點**量起，不是從樓層量起。AR_CPC 的 GL 在內部原點下方 304.8 cm、1F 在下方 284.8 cm，所以 1F 上方 175 cm 的相機顯示「-109.8」 |
| 相機平常看不到 | Revit 原生相機只有在專案瀏覽器右鍵「顯示相機」時暫時出現，點別處就消失，不能列印 |
| 沒有總覽 | 無法一次看到所有相機在哪、多高、朝哪，也無法排成圖紙交給同事或渲染 |

**目標**：讓使用者能清楚定位每台相機、了解它和整體空間的關係，並且能調整、連動、儲存、出圖。

## 2. 名詞

| 名詞 | 意思 |
|---|---|
| 相機 | 專案中 `IsPerspective = true`、非樣板的 3D 視圖 |
| 離樓板高度 | 眼睛高度 − 眼睛下方最近樓層的高程（cm）。**面板上所有高度一律用這個** |
| Eye Elevation 原始值 | 性質面板顯示的值（從內部原點量起），面板上只唯讀顯示 |
| 相機標記 | 讓相機在平面／剖面／3D 中持續看得到的元素（A：模型族群；B：註解符號） |
| Key Plan | 為某台相機產生的輔助視圖（平面、剖面、等角） |
| 視角版本 | 同一台相機存下的具名視角（例如「A 案」），可切回 |

## 3. 範圍與階段

| 階段 | 內容 | 交付條件 |
|---|---|---|
| **1. 停靠面板** | 停靠面板、相機清單、數值編輯、雙向連動、人視高預設值、複製相機到其他樓層、匯出 Excel、迷你 key plan＋視野扇形 | 使用者在 Revit 2024 實測通過 |
| **2. Key Plan** | 每台相機產生平面／剖面／等角視圖、每層總覽平面、相機標記 A＋B | 第 1 階段通過後才開始 |
| **3. 出圖與版本** | 一鍵排圖紙、相機視角版本、視覺精修 | 第 2 階段通過後才開始 |

**不做**：MCP 工具化、MCP App、渲染設定（Enscape 參數等）、相機動畫路徑。

## 4. 第 1 階段：停靠面板

### 4.1 面板
- 以 `RegisterDockablePane` 在 Revit 啟動時註冊；預設停靠在右側。
- 功能區「MCP Tools → 相機管理」按鈕負責顯示／啟動面板。
- 面板寬度 300–600 px 都要能用。
- **2026-09-24 版面第四次改版（拿掉「選取中相機」卡片、DataGrid 單擊即編輯）**：實機測試
  發現雙擊儲存格會被「雙擊列＝跳轉透視圖」搶走，編輯功能形同無法使用；同時使用者要求把
  「選取中相機」卡片拿掉、空間讓給地圖。改動後固定垂直順序：
  **區名（2026-09-24 追加：每個區塊上方有 11px semibold 次要色小標，讓使用者能用名稱指稱；
  每個按鈕／下拉／勾選框的 ToolTip 都以「區號＋名稱」開頭，例如「④ 一鍵 4 視圖：…」）**：
  「① 工具列」「② 相機清單」「③ 位置圖」「④ 視圖操作」「⑤ 人視高預設值」「⑥ 狀態列」
  （⑥ 不另加標題列，只在狀態文字前加一個小「⑥」以節省高度）。面板仍不出現外層捲軸，
  小標增加的高度由 ③ 位置圖吸收。
  1. ① 工具列：搜尋、重新整理、從透視圖同步、匯出 Excel（圖示按鈕）、開啟預覽視窗（圖示按鈕）。
  2. ② 相機清單：相機 DataGrid＋操作提示列（單擊儲存格即進入編輯模式；唯一允許內部捲動的區塊）。
  3. `GridSplitter`（可拖曳，位置存在 `CameraManagerSettings.CameraTabListFraction`，
     預設清單區 35%／地圖區 65%——「選取中相機」卡片拿掉後把騰出的空間讓給地圖，只影響
     新安裝的預設值，已經拖過的使用者維持自己存的比例）。
  4. ③ 位置圖（迷你 key plan）：底圖平面 `ComboBox`＋「顯示其他樓層」勾選框疊在地圖右上角，左下角另有
     「符合畫面」按鈕；支援滾輪縮放（游標為中心）／左鍵或中鍵拖曳平移／雙擊空白處回到
     符合畫面（見 4.8）。
  5. ④ 視圖操作：一鍵 4 視圖＋等角範圍 `ComboBox`＋「重設剖面到視線」、並排、還原、複製到
     其他樓層（按鈕開 `Popup` 小卡：樓層勾選清單 + 確定/取消）、全部符合畫面、標記顏色
     （6 色色票＋「自訂…」，見 5.3）。
  6. ⑤ 人視高預設值（[160] cm ＋ 套用到選取／套用到全部）。
  7. ⑥ 狀態列。
  面板太矮時，地圖區先縮（最小 120px），清單區其次（最小約 140px，容納 DataGrid 約 3
  列）。`PillTabControl.cs` 這次沒有用到，保留檔案但面板不再組裝它；`CameraDetailSection.cs`
  本次整個刪除（原「選取中相機」卡片，欄位搬進 DataGrid 選用欄位，見 4.2／4.3）。
  4.9 B「面板內嵌 PreviewControl」實驗區塊整段移除，「開啟預覽視窗」收斂成標題列的
  唯一預覽入口。色彩／字型/圓角/間距集中在 `MCP/UI/CameraManagerTheme.cs` 一個檔案。

- **2026-09-24 版面第五次改版（精簡、③ 位置圖佔下半，取代上面第四次改版的 1–7 順序）**：
  1. ① 工具列：**單列**——搜尋框（吃剩餘寬度）｜重新整理｜從透視圖同步｜「⋯」（選單：匯出
     Excel、開啟預覽視窗）。
  2. ② 相機清單：預設約佔「清單＋位置圖」高度的 1/3（新設定鍵
     `CameraManagerSettings.CameraListFractionV3`，預設 1/3；舊鍵 `CameraTabListFraction` 屬舊版面、
     保留相容但不再讀取），`GridSplitter` 仍可拖曳並記住。
  3. ③ 位置圖：吃掉剩下全部高度；控制項改成地圖右上角一條半透明浮動工具列：小型底圖
     `ComboBox`＋三顆圖示切換鈕「層」（顯示其他樓層）、「⤢」（符合畫面）、「框」（顯示對位框），
     皆有「③ …」ToolTip；不再佔獨立列，也不再用右鍵地圖叫除錯選單。
  4. ④ 視圖操作：**單列**——分割按鈕「一鍵 4 視圖 ▾」（主鈕＝執行；▾ 選單＝等角範圍兩個單選項、
     重設剖面到視線、全部符合畫面）｜並排｜還原｜「⋯ 更多」（選單：複製到其他樓層…、標記顏色 ▸
     〔6 色色票＋自訂…〕、⑤ 人視高預設值…、匯出 Excel、開啟預覽視窗）。
  5. ⑤ 人視高預設值：不再佔一列，改由「⋯ 更多 → ⑤ 人視高預設值…」開啟主題化小 Popup（數值 cm
     ＋套用到選取＋套用到全部）。
  6. ⑥ 狀態列：不變。
  選單與 Popup 一律用主題色（palette B）、點外面即關閉（`StaysOpen=false`／ContextMenu 預設）；
  每個選單只建立一次重複使用（相機右鍵選單亦同，不再每次右鍵 new 一個），關閉時 WPF 釋放
  Popup HWND，不累積。從選單項目開 Popup 一律延到選單關閉後（Dispatcher Background），避免同一
  次點擊把 Popup 立刻關掉。所有處理器 try/catch＋log；不套任何 `Style`（避免 TargetType 與元素
  型別不符——前次崩潰根因）。面板仍無外層捲軸。

### 4.2 相機清單
**預設欄位**（300–600 px 面板寬度下不出現水平捲軸）：

| 欄位 | 說明 | 寬度 |
|---|---|---|
| 名稱 | 第一欄，凍結（`FrozenColumnCount=1`），永遠看得到；文字超長時橢圓省略＋ToolTip 顯示全名 | 星號（剩餘寬度），最小 45px |
| 樓層 | 眼睛下方最近的**建築樓層**（2026-09-25：只認 Level 的 Building Story＝是，`BuiltInParameter.LEVEL_IS_BUILDING_STORY`；一個都沒勾才退回全部 Level；跳過非樓層 Level 時寫 log。同一規則用於地圖跟隨與 Key Plan 產生，見 `CameraMath.GetStoreyLevelsSorted`）；寬度依目前模型內最長樓層名稱動態量出（`FormattedText` 量字寬），下限 64px。**改樓層＝複製（2026-09-25 使用者決策，取代同日「就地換樓層＋退役舊 Key Plan 平面」）**：相機的樓層不就地修改。下拉展開中選到另一個樓層（`SelectionChanged`）或下拉關閉（`DropDownClosed`）先到者提交一次（ElementId 數值比對）→ 沿用 §4.6「複製到其他樓層」邏輯（單一目標樓層）複製一台新相機；原相機與它的 Key Plan 視圖完全不動，不建立／刪除任何 Key Plan 視圖。完成後刷新清單、選取新相機，原列樓層格還原顯示原樓層。狀態列「已複製「X」到 L5 → 「X - L5」（原相機不變）」；log「清單編輯：<相機> 樓層 → 複製到 <樓層> 新相機 <名稱>」。欄標題與 ComboBox 工具提示「選另一個樓層＝在同位置複製一台相機到該樓層（原相機不變）」 | 動態，64–140px |
| 離樓板 cm | 小數 1 位，右對齊；低於 120 或高於 200 時該列背景標色＋ToolTip 說明 | ~50px |
| 水平角 ° | 0 = 專案北（+Y），順時針，右對齊 | ~50px |
| 俯仰角 ° | 0 = 水平，正值往上看，右對齊 | ~50px |

原本常駐顯示的「視野 °（估算）」與「狀態」兩欄拿掉：視野角改列入下方選用欄位（預設隱藏）；
「狀態」原本只是把 `Grid_LoadingRow` 已經用列背景色＋ToolTip 表達過的同一份資訊
（高度示警／不在底圖範圍）又重複列成一欄純文字，在窄面板的欄寬預算下優先犧牲，資訊沒有
真的消失。

**名稱欄單擊＝開啟動作（2026-09-24 v5，取代原本的「↗」欄）**：名稱欄改成唯讀
（`IsReadOnly=true`），單擊該儲存格不會進編輯模式，而是直接觸發開啟動作——預設「一鍵 4
視圖」（跟動作列按鈕同一條路徑，見 4.9 C）。原本的「↗」小按鈕欄整個拿掉，騰出的寬度讓給
其他欄位；重新命名改到右鍵選單（見下方、4.9 C）。DataGrid 下方新增一行提示文字（次要色）：
「點名稱開 4 視圖・點數字直接修改・改樓層＝複製・右鍵更多」（2026-09-25 第二次更新；300px 寬度下以字數估算放得下、未經實機像素驗證，另加省略號＋工具提示保險）。

可用名稱搜尋、篩選（標題列搜尋框）。

**選用欄位**（右鍵任一欄位標題開啟勾選選單切換；狀態存
`CameraManagerSettings.ShowColumnPlaneX/Y/FarClip/Fov/EyeElevation`，本機記憶、下次開面板
記得；預設全部隱藏）：

| 欄位 | 說明 | 可編輯 |
|---|---|---|
| 平面 X cm、平面 Y cm | 以專案基準點 (Project Base Point) 為水平原點 | 可編輯 |
| 遠裁剪 cm | 找不到可寫入的遠裁剪參數時（`FarClipEditable=false`）該列自動變唯讀，`BeginningEdit` 攔截取消，不彈訊息 | 視相機而定 |
| 視野角 °（估算） | 唯讀估算值，未經實機驗證，絕不寫回模型 | 唯讀 |
| Eye Elevation 原始值 | 唯讀；欄位標題本身帶 ToolTip「Revit 原始值，從內部原點量起」 | 唯讀 |

原本這五個欄位在「選取中相機」卡片，卡片整個拿掉後搬進這裡；卡片拿掉的空間讓給地圖區
（見 4.1 GridSplitter 預設比例下修）。

### 4.3 就地編輯：單擊即編輯
2026-09-24 第四次改版拿掉「選取中相機」卡片後，所有可編輯欄位都在 DataGrid 上，單擊儲存格
（不必先點兩次或按 F2）即可進入編輯模式。**名稱欄例外（第五次改版新增）**：名稱欄已改
`IsReadOnly=true`，不參與這套「單擊即編輯」機制——單擊名稱儲存格改由
`Grid_PreviewMouseLeftButtonDown` 直接判斷點在哪個 `DataGridCell`，觸發
`RowOpenActionRequested`（開啟動作，見 4.9 C），不會進編輯模式；重新命名改到右鍵選單的
主題化輸入 Popup（`CameraManagerPanelControl.ShowRenamePopup`）。樓層／離樓板 cm／水平角／
俯仰角／選用欄位仍走下面這套機制：

> **2026-09-25 第三次修正：改成常駐控制項，不再走 DataGrid 編輯模式。** 前兩輪（單擊 `BeginEdit`、
> `CellEditEnding` 不設 Cancel）實機都失敗，而且測試期間 RevitMCP log 完全沒有編輯紀錄＝DataGrid 根本沒進編輯
> 模式；DataGrid「進編輯 ↔ 鍵盤焦點」握手在 Revit 停靠面板內不可靠（Revit 主視窗搶焦點、live sync 每隔幾秒
> 換 `ItemsSource`）。以下為現行機制，取代本節舊的「單擊 BeginEdit／CellEditEnding」描述。

- **DataGrid 全唯讀**（`IsReadOnly=true`），可編輯欄（樓層、離樓板 cm、水平角 °、俯仰角 °、選用的平面 X/Y、
  遠裁剪）改為 `DataGridTemplateColumn`，`CellTemplate` 以 `FrameworkElementFactory` 在程式碼內建（無 XAML）。
- **數字欄**：儲存格內是常駐 `TextBox`（`CameraManagerTheme.GridCellTextBoxStyle`，TargetType＝TextBox：平常無框
  透明、hover 淡框、取得焦點白底＋accent 框，右對齊、1 位小數），`Text` 單向繫結到列值。點一下＝
  `Keyboard.Focus`＋全選，並選取該列（地圖高亮；**不**觸發一鍵 4 視圖）。Enter 或失焦＝解析（Invariant 後
  CurrentCulture，接受全形逗點／句點）→ 與列值比對（±0.05 視為沒變，不開交易）→ 送
  `NumericFieldEditCommitted`／`PlaneFieldEditCommitted`／`FarClipEditCommitted`；Esc＝還原原值。Enter/Esc 後焦點
  交回 DataGrid。遠裁剪不可寫入時 `IsReadOnly`（灰字）。
- **樓層欄**：常駐 `ComboBox`（`GridCellComboBoxStyle`，TargetType＝ComboBox），`ItemsSource` 繫結面板的樓層清單
  DependencyProperty；使用者開的下拉關閉時若選值≠列值就送 `LevelEditCommitted`。下拉未展開時滾輪／上下鍵不換
  樓層（滾輪轉給清單捲動），避免「顯示值變了但沒提交」。
- **清單重建延後**：任何儲存格 TextBox 有鍵盤焦點或樓層下拉展開中，`SetRows`／`SetLevels`／`Items.Refresh`
  一律延後到焦點離開。
- **log**：每次提交「清單編輯：<相機> <欄位> <舊>→<新>」、每次解析失敗、取得焦點、第一個按鍵與第一個文字輸入
  （用來判斷鍵盤輸入是否真的送達 TextBox）。
- **停靠面板鍵盤輸入（查證）**：Autodesk 論壇上「dockable pane 內 WPF TextBox 收不到鍵盤輸入」的案例是 WPF 包在
  WinForms `ElementHost` 裡的情形（解法 `ElementHost.EnableModelessKeyboardInterop` 只適用 WPF Window）；本面板是
  `IDockablePaneProvider` 直接給 WPF `FrameworkElement`（官方建議路徑），不屬該情形，未套用額外 Hwnd 處理。實機
  若仍收不到字，看 log 是否有「收到按鍵／收到文字輸入」判斷斷在哪一層。

| 群組 | 欄位 | 編輯位置 | 備註 |
|---|---|---|---|
| 位置 | 平面 X、Y（cm） | DataGrid 選用欄位 | 以專案基準點為水平原點 |
| | 所在樓層 | DataGrid「樓層」欄（ComboBox） | 改樓層＝複製新相機到該樓層（保持離樓板高度），原相機不變（見 §4.2） |
| | 離樓板高度（cm） | DataGrid「離樓板 cm」欄 | |
| 方向 | 水平角、俯仰角（°） | DataGrid「水平角 °」／「俯仰角 °」欄 | 改角度時保持眼睛位置不動 |
| 視野 | 遠裁剪距離 | DataGrid 選用欄位「遠裁剪 cm」 | 若無法可靠寫入，該列自動唯讀 |
| | 水平視野角（°） | DataGrid 選用欄位「視野角 °（估算）」 | 唯讀估算值 |
| 參考 | Eye Elevation 原始值 | DataGrid 選用欄位 | 唯讀，欄位標題 ToolTip 說明 |

所有欄位的送出邏輯共用同一段組裝程式（`CameraManagerPanelControl.OnGridFieldEditCommitted`）：
用目前這一列的既有值補齊一份完整 `CameraEditRequest`，只覆寫剛剛改的那個欄位再送出（一個
Revit 交易，一個中文復原名稱）。

### 4.4 雙向連動
- 面板改數值 → 相機立即更新。
- 在 Revit 裡改相機（平面上拖、改性質）→ 面板在 1 秒內更新。
- 避免面板自己的修改又觸發一次更新而形成迴圈。
- ⚠️ **未查證**：在透視圖內用滑鼠環繞／平移，Revit 是否會通知外掛。實測後決定；若不會，面板加「從透視圖同步」按鈕。
- **視野扇形即時更新（2026-09-24 第五次改版）**：使用者拖曳透視圖裁剪框邊（改變水平／垂直
  FOV）、或移動／旋轉相機時，Key Plan 平面的水平扇形、Key Plan 剖面的垂直扇形，以及 ③ 位置圖
  的扇形都要跟著更新。實作：
  - `Application.OnCameraManagerDocumentChanged`（僅在面板已「重新整理」啟用 live sync 時）收集
    這次被修改的透視 `View3D` Id，交給 `CameraManagerPanelControl.QueueCameraMarkerRefresh`；
    DocumentChanged 內**不修改模型**。
  - 面板以 `DispatcherTimer` 去抖 500ms、同一相機合併（`HashSet`），時間到才排入
    `CameraManagerEventQueue` 的 `UpdateCameraMarkers` 工作 →
    `CameraKeyPlanService.UpdateMarkersForCameras`：以 ES 連結找該相機的 Key Plan 平面／剖面
    （沒有就略過），在**單一交易「更新相機標記」**內只重畫標記（刪舊標記＋重畫，水平／垂直視野
    扇形照新 FOV），**不動**任何裁剪框、剖面範圍、等角 SectionBox，也不並排。剖面自己的裁剪框
    若已包不住扇形就維持原樣（扇形半徑本來就截到剖面裁剪邊）。一鍵 4 視圖進行中時延後再試。
  - 回饋迴圈防護：`CameraKeyPlanService.IsUpdatingMarkers` 旗標（交易期間，含 Commit 觸發的
    DocumentChanged）＋交易名稱比對（`GetTransactionNames()` 全部是「更新相機標記」）→
    DocumentChanged 直接忽略。
  - 清單的視野角欄（選用欄位）與 ③ 位置圖扇形沿用既有 live sync（同一次 DocumentChanged
    命中 View3D → 下次 Idling 輕量讀快照 → `RedrawMarkers`），不另外刷新。
  - ⚠️ 未實機驗證：拖曳透視圖裁剪框是否產生 View3D 的 Modified（依 API 行為推斷應會）。
    2026-09-25 log 已證實會觸發（「更新標記」每次拖曳都執行），但 hFOV 一律「?」、平面扇形從不變。
  - **FOV 讀取修正（2026-09-25）**：舊 `EstimateHorizontalFovDeg` 用 `CropBox.Max.Z` 當深度且要求 >0；依
    Revit API 開發者指南〈View3D〉，視圖座標「Z 朝向使用者」、透視裁剪框是以觀察者為頂點的角錐介於前後
    裁剪面之間的部分、`CropBox.Max` 在前裁剪面上 → Max.Z 在眼睛前方為負值，舊版必回 null（實機 log 的
    「hFOV=?」即此），垂直扇形則一直用預設 60° 乘長寬比。新 `CameraMath.ComputePerspectiveFov`：
    C＝裁剪形狀（`GetCropRegionShapeManager().GetCropShape()`）每個頂點相對眼睛分解為 right/up/forward，
    角度＝atan2(x, d)（與裁剪矩形所在深度無關）；A＝CropBox 寬高在前裁剪面深度（Transform.OfPoint(Max)−eye）·
    forward；B＝同寬高在後裁剪面深度。採用 C→A→B，全部候選、CropBox Min/Max、裁剪形狀範圍每次更新都寫 log，
    C 與 A 差 >2° 另記 NOTE。⚠️ 哪個候選等於 Revit 內部真正視野角尚未實機確認——以新放相機（預設 50° 鏡頭）
    與拖曳前後的 log 對照判定。

### 4.5 人視高預設值
- 預設 160 cm，存在外掛設定檔（只在本機）。
- 「套用到選取」、「套用到全部」（後者要先確認）。

### 4.6 複製相機
- 把一台相機複製到其他樓層：同一平面位置、同離樓板高度、同方向、同視野與遠裁剪。
- 新名稱 `<原名> - <樓層名>`，重名自動加序號；全部在一個交易內。
- 入口：「④ 更多」／右鍵選單「複製到其他樓層…」（勾選多個樓層），以及 2026-09-25 起清單「樓層」欄選另一個樓層（單一目標樓層，`CameraManagerService.CopyCameraToLevels` 多載回傳新相機 Id／名稱，完成後選取新相機）。
- 原相機與其 Key Plan 視圖不受影響；新相機沒有 Key Plan 視圖（需要時再按一鍵 4 視圖）。

### 4.7 匯出相機清單
- Excel（ClosedXML）：名稱、樓層、離樓板高度、X/Y、水平角、俯仰角、視野角、遠裁剪、Eye Elevation 原始值。
- 預設檔名 `相機清單_<yyyyMMdd>.xlsx`。

### 4.8 迷你 key plan
- 底圖只提供有啟用裁剪框的平面；該樓層沒有的話，改用中性格線、依相機分布自動縮放。
- **底圖清單精簡（2026-09-25）**：預設只列「至少有一台相機以它為下方樓層」的樓層，每層一張（沿用預設底圖的挑法：
  依名稱排序第一張已裁剪平面），標籤「<樓層名> — <平面名>（N 台相機）」。浮動工具列新增「全」切換鈕＝顯示全部
  平面（`CameraManagerSettings.ShowAllPlansInBaseMap`，本機記憶）。目前選取的底圖不在精簡清單時保留並附加在最後。
- **跟隨作用中透視圖（2026-09-25）**：作用中視圖變成透視相機（專案瀏覽器開啟、一鍵 4 視圖、僅打開透視圖、點
  並排視窗）→ `ViewActivated` 只設旗標 → 下一次 Idling（live sync 已啟用、同一份文件、面板顯示中）→ 面板在 UI
  執行緒：程式化選取 ② 那一列（無 Revit 副作用）；目前底圖樓層≠該相機下方樓層時切到該樓層底圖（只有樓層真的
  換才匯出，沿用 in-flight 合併）；地圖平移置中該相機（已在可視範圍內則維持目前縮放／位置；換底圖時等底圖畫好
  再置中）。~~清單裡沒有的新相機不跟隨~~ → **2026-09-25 修**：作用中透視圖不在清單（新相機；同一次 Idling
  的 live sync 快照還沒套用）→ 記為待跟隨，快照套用後自動跟隨；600ms 內沒有快照就自己排一次輕量刷新（只讀清單、
  不匯出底圖）。新放置的相機本來就由 DocumentChanged（新增 View3D）→ 下次 Idling live sync 帶進清單。
  **底圖樓層退回（2026-09-25）**：相機所在樓層沒有已裁剪平面時，改用最近的下方樓層、再沒有才用最近的上方樓層
  （在全部已裁剪平面裡找，精簡清單沒有就附加），並寫 log。
- **底圖平面挑選規則（2026-09-25，取代「依名稱排序第一張」）**：實測相機「3D View 1」在 L1 - Block 37，預設
  底圖卻挑到裁剪只有 8.3×16.7 m 的放大平面、相機在圖外。凡是自動挑底圖（預設底圖、跟隨作用中透視圖、選取相機
  自動切換、樓層退回、精簡清單每層一張）一律用 `BaseMapPlanChooser.ChooseForLevel`：同樓層已裁剪平面中先限
  樓層平面（沒有才用天花／面積平面）→ 裁剪框（`CropBox`＋Transform，快照時攤平成純 double）**包含相機眼睛
  XY** 者優先 → 非從屬視圖優先 → **裁剪面積最大**；都不包含就取面積最大（非從屬優先）；該層沒有才走既有的
  下方／上方樓層退回。預設底圖（多台相機）＝相機最多的樓層中「包含最多相機」者、同分取面積最大。精簡清單每層
  一張＝該層面積最大者（不看相機）。跟隨／選取相機時，若目前底圖同樓層且已包含該相機就不換，否則重挑。log 記
  選中平面名稱、id、裁剪寬×高（m）與理由。
- **底圖挑選第二版（2026-09-25，取代上一條；Snowdon Towers 實測：一樓由多個高程相近的「L1 - Block xx」組成，相機所在樓層的平面都不含相機、Site Plan 掛在高約 1 m 的另一個 L1 樓層）**：預設／跟隨／選取自動切換／精簡清單一律呼叫 `BaseMapPlanChooser.Choose`——**步驟 0** 相機放置時的平面（放置當下由 DocumentChanged 讀作用中視圖、Idling 以 ES `RevitMCP_CameraSourcePlan` 記在相機上；已裁剪直接用，未裁剪改用其父／從屬／同主視圖且包含相機的已裁剪平面，log「底圖＝相機放置時的平面「…」」；Revit 本身不存這個資訊，舊相機沒有記錄就走下列規則）→ **1** 同層包含眼睛的樓層平面（非從屬、面積最大）→ **2** 其他樓層高程在 ±1.5 m 內且包含眼睛（高程最近、面積最大）→ **3** 眼睛下方任一建築樓層且包含眼睛（最高樓層、面積最大）→ **4** 同層面積最大（舊規則），再下方／上方樓層退回。log 記步驟與理由；精簡清單多個樓層挑到同一張時合併、相機數相加；底圖取自別的樓層時，共用挑法會挑到這張底圖的相機樓層視為同層（不淡化）；相機標記落在底圖外時 ③ 左下顯示「相機不在這張底圖範圍內」。
- 底圖平面 `ComboBox`＋「顯示其他樓層」勾選框（2026-09-24 追加）疊在地圖區右上角的緊湊小
  卡片。2026-09-24 第五次改版：改成半透明浮動工具列（小 combo＋「層／⤢／框」三顆圖示切換鈕，
  見 4.1），「顯示對位框」從右鍵選單移到這裡。
- **底圖 DPI 對位根因（2026-09-24 第五次改版，實測 Loft Unit 被畫到建築下方）**：Revit
  `ExportImage` 的 PNG 帶 `pHYs`＝5905 px/m（150 DPI；以本機既有 `capture_view_image` 匯出檔實讀
  確認）。`BitmapImage` 因此 `DpiX=150`，而 `ImageBrush` 用 `Stretch.None` 會以 DIP 尺寸
  `像素×96/150`（1600px → 1024 DIP）畫在左上角；畫布寬高與標記座標卻都用「像素」——底圖被縮成
  64%、標記沒縮，越往右下偏越多。模型→像素公式本身（`local = CropBox.Transform⁻¹·eye`；
  `px = (local.X−Min.X)/(Max.X−Min.X)·W`；`py = (Max.Y−local.Y)/(Max.Y−Min.Y)·H`，Min/Max 取自暫存
  複本視圖的同一個 CropBox）原本就正確：log 顯示該視圖 `extentsLocalMm X[-39319,39320]
  Y[-32004,32006]`，Min/Max 剛好對稱於 Transform 原點，所以像素恰好＝800＋x·6.2／651−y·6.2，這是
  資料本身對稱，不是程式把原點當中心。修法：`ImageBrush.Stretch=Fill`，底圖一律填滿寬高＝像素數
  的畫布，與 DPI 中繼資料脫鉤；剖面線與扇形都用同一個 `project` 函式，一併修正。log 追加
  `dpi`、`cropLocalFt Min/Max`、`tfOrigin`、basis 與每台相機的 `localFt`。
- 每台相機畫：圓點（選取中＝accent、其他＝次要色，白色 1.5px 外框）＋方向桿＋實心三角形
  箭頭（`Polygon`）＋視野扇形（低透明度 ~15% 填色＋細邊線）；選取中的相機即時重上色（不必
  整個重畫）。
- 在底圖上畫出裁剪框外框，用來目視檢查底圖和相機位置有沒有對齊。
- **底圖對位換算含註記裁剪外擴（2026-09-24 實測回報「標記位置壞了」的根因修復）**：
  `CameraManagerService.ExportBaseMap` 原本只用 `view.CropBox`（純模型裁剪框）換算像素座標，
  但平面視圖若啟用了「註記裁剪」(Annotation Crop)，`ImageExportOptions`／`ZoomFitType.
  FitToPage` 匯出的實際範圍應是「模型裁剪框 + 註記裁剪外擴量」（圖名泡、標籤等會伸出模型
  裁剪框外），兩者不一致就會讓所有標記系統性偏移。修法：用
  `View.GetCropRegionShapeManager()` 的 `TopAnnotationCropOffset`／`BottomAnnotationCropOffset`／
  `LeftAnnotationCropOffset`／`RightAnnotationCropOffset` 把換算用的範圍往外擴。是否啟用註記
  裁剪，`ViewCropRegionShapeManager` 本身沒有對應的旗標屬性（只有 `CanHaveAnnotationCrop`
  這個「視圖類型是否支援」的能力旗標）；「目前有沒有打開」在 UI 上是 View 的一個叫
  「Annotation Crop」的內建參數，用顯示名稱 `LookupParameter` 在非英文語系 Revit 會找不到，
  所以雙重保險：先試 `LookupParameter("Annotation Crop")`，找不到就退回「四個外擴量是否有
  非零值」當代理判斷（停用時外擴量通常維持 0，不會誤觸發外擴）。⚠️ **未經 Revit 實機驗證**：
  這套規則是依 API 文件與症狀推斷寫成，尚未在 AR_CPC 對照「有／沒有開註記裁剪」兩種平面
  實測比對過。
- **確定性底圖匯出（2026-09-24 第三次對位修正，取代上面「推測匯出範圍」的做法）**：
  `CameraManagerService.ExportBaseMap` 不再直接匯出使用者的平面，而是在 `TransactionGroup`
  內：Transaction 1 複製該平面（`ViewDuplicateOption.Duplicate`，不含詳圖）、拿掉視圖樣板、
  `CropBoxActive=true`／`CropBoxVisible=false`、裁剪形狀還原成矩形、開啟註記裁剪
  （`VIEWER_ANNOTATION_CROP_ACTIVE`）且四邊外擴設到 Revit 允許的最小值（先試 0，被拒逐步
  加大；以讀回值為準）、以視圖層級類別隱藏會伸出裁剪框的註記（格線、樓層、剖面／立面／
  詳圖索引／視圖標記、尺寸、高程／座標點、文字、參考平面、範圍框、接圖線，以及所有名稱
  以 Tags 結尾的標籤類別）→ Commit → 在沒有 Transaction 開啟的狀態下用與
  `capture_view_image` 相同的 `ImageExportOptions`（FitToPage、PNG、1600px、SetOfViews＝暫存
  視圖）匯出 → RollBack 整個 group（暫存視圖消失、不留復原紀錄）。⚠️「group 開著時可否
  ExportImage」API 文件未寫明（未查證）：若丟例外改走備援「建立 Commit → 匯出 → finally 用新
  交易刪除暫存視圖」；暫存視圖本身建不出來（例如文件唯讀）才退回舊版推測範圍匯出。
  匯出後讀 PNG 檔頭取得實際像素，換算範圍在「模型裁剪框」與「模型裁剪框＋最小註記外擴」
  兩者之間挑長寬比較接近圖片者，每次匯出都寫 log（像素、兩種範圍的 mm 與長寬比、選用者）；
  每台相機畫到位置圖時也寫一行 log（eye 座標 → 像素）。匯出期間
  `CameraManagerService.IsExportingBaseMap=true`，Application 的 DocumentChanged 忽略這段期間
  的變更，避免「匯出→刷新→再匯出」迴圈。
- **除錯疊圖「顯示對位框」**：③ 位置圖右上角浮動工具列「框」切換鈕（2026-09-24 第五次改版前為右鍵選單；預設關閉、不存
  設定），畫出換算範圍（藍虛線＝圖片外框）與模型裁剪框（紅虛線），圖面內容若沒有剛好落在
  紅框內即為對位誤差，一眼可見。原本常駐的裁剪框外框改為只在這個模式顯示。
- **對位健檢（狀態列警示，2026-09-24 新增）**：`CameraManagerPanelControl.OnBaseMapReady`
  每次換算完都比對「換算範圍的長寬比」與「匯出圖片本身的像素長寬比」，差超過 1% 就在狀態列
  附加「（底圖對位可能有誤差：換算範圍長寬比與圖片不一致）」，不讓對位假設失準時看起來一切
  正常。這只驗證長寬比一致，不能證明位移量為零；仍建議目視比對裁剪框外框是否貼齊底圖四邊。
- **只畫底圖所在樓層的相機（2026-09-24 實測回報：不同樓層相機混在一張底圖上位置容易誤判）**：
  `BaseMapSnapshot.GenLevelId`（該平面視圖的 `ViewPlan.GenLevel.Id`）與每台相機的
  `CameraRow.LevelId` 比對，不同樓層的相機預設不畫；勾選「顯示其他樓層」後改成淡化顯示
  （不透明度降到 ~55%、圓點加上 0.45 透明度、標籤字尾附「（樓層名）」），且不參與選取變色。
  中性格線模式（沒有任何已裁剪平面時）沒有「底圖所在樓層」的概念，不做過濾，全部相機一起畫。
  勾選狀態存 `CameraManagerSettings.ShowOtherLevelCamerasOnMap`（本機記憶）。
- **選到相機自動切換底圖到該相機的樓層**：`CameraManagerPanelControl.OnSelectionChanged` →
  `MaybeAutoSwitchBaseMapForSelection` → `KeyPlanSection.SelectPlanByLevel`，找到該樓層對應的
  已裁剪平面就切換 `ComboBox`（沿用既有 `PlanSelected`→`RefreshBaseMap` 管線，不重複刷新
  邏輯）；找不到就維持目前底圖不動。
- **選取相機的剖面線疊圖（簡化版）**：若選取的相機已經用「一鍵 4 視圖」產生過 Key Plan
  剖面，迷你地圖會疊畫一條虛線（accent 色）代表該剖面目前實際的水平位置與方向——讀的是
  剖面視圖目前的 `CropBox.Transform`（`CameraKeyPlanService.TryGetSectionLineForCamera`），
  所以使用者在 Revit 內手動調整過剖面之後，這條線會跟著反映調整後的位置，不是程式原本算出
  來的視線。⚠️ **已知簡化，非本次規格原文的完整版**：只畫一條實體線段，沒有畫兩端刻度／
  箭頭表示視線方向；線段跟著底圖一起縮放平移，但線寬（1.5px）不像相機標記一樣做反向縮放，
  縮放倍率差很多時線會變得很粗或很細，這是視覺上的已知限制，不影響線的位置正確性。
- **縮放／平移（2026-09-24 使用者追加需求，取代原本的 `ScrollViewer`）**：
  - 預設「符合畫面」：整張底圖＋標記等比例縮放置中在地圖區內，無捲軸；地圖區大小改變時
    （分割線拖曳、面板縮放）若使用者沒有手動調整過視角，自動重新符合畫面
    （`KeyPlanSection` 的 `_viewport.SizeChanged` 事件）；每次換底圖／重畫都會重設「使用者
    調整過」旗標並重新符合畫面一次。
  - 滑鼠滾輪縮放，以游標位置為縮放中心，每格 ×1.2，縮放範圍夾在「符合畫面」倍率的
    0.5×–20× 之間。
  - 左鍵在地圖空白處按住拖曳＝平移；中鍵拖曳同樣可以平移；點在相機圓點上優先觸發選取，不
    會被誤判成平移起點（用 `e.OriginalSource` 是否為相機圓點來分辨，不是用像素位移門檻）；
    真正的拖曳判定門檻是位移 4px（未達門檻視為單純點擊，不觸發平移）。
  - **標記本身的左右鍵語意（2026-09-24 v5）**：左鍵單擊圓點＝選取＋直接觸發開啟動作（預設
    一鍵 4 視圖，見 4.9 C）；右鍵單擊圓點＝彈出跟 DataGrid 列右鍵相同的選單（一鍵 4 視圖／
    僅打開透視圖／重新命名…／複製到其他樓層…／重設剖面到視線）。`KeyPlanSection` 把
    `MouseLeftButtonDown`／`MouseRightButtonDown` 分開掛在圓點上（原本共用不分左右鍵的
    `MouseDown`），避免右鍵同時誤觸發左鍵那條路徑；空白處單擊仍然什麼都不做，雙擊空白處
    仍是「符合畫面」，兩者不受影響。
  - 右上角浮動工具列「⤢」符合畫面（第五次改版前在左下角）；雙擊地圖空白處效果相同。
  - **實作方式**：單一 `MatrixTransform` 套在整個地圖內容層（`_mapLayer`，含底圖／裁剪框
    外框／剖面線／相機標記群組），縮放/平移只改這一個 Matrix；每個相機標記另外包一層獨立
    的 `Canvas`（錨點＝該相機在內容座標系的位置），套用 `ScaleTransform(1/scale, 1/scale,
    centerX:0, centerY:0)` 反向抵銷外層縮放，讓標記（圓點／方向桿／箭頭／視野扇形／標籤文字）
    在任何縮放層級下永遠是固定螢幕像素大小，只有錨點位置隨底圖縮放/平移移動——這是規格
    追加需求裡「單一 transform + 反向縮放標記」的作法（另一個選項是「每次縮放都用 model→
    screen 重新算一次標記座標」，這裡選反向縮放是因為標記形狀（扇形弧線、箭頭三角形）本來
    就已經是用固定螢幕像素常數畫的，加一層反向縮放比重寫整套繪圖成 screen-space 版本改動
    更小）。底圖本身（點陣圖／中性格線）不做反向縮放，縮放時自然跟著變大變小是預期行為。

### 4.9 同時看到多張視圖（簡化版）

第 1 階段交付範圍內、5.2 完整版之前的簡化版，只用既有視圖、不新建任何 Key Plan。
2026-09-24：原本規劃的「面板內嵌 PreviewControl」實驗區塊（B 分支）已整段移除，「開啟
預覽視窗」收斂成標題列的唯一預覽入口（獨立無模式視窗，保證能動）。

**A. 並排檢視／還原**

- 「並排檢視」：對清單選取的相機，依序切到 (1) 該相機的透視圖 (2) 該相機所在樓層（level-below）
  的一張既有平面視圖——選擇順序：已經開啟中的優先；同分再優先已啟用 CropBox 的；否則依名稱排序
  取第一個。找不到任何既有平面視圖就顯示錯誤，**不會新建視圖**。兩張視圖都切換完成後，最後一步
  呼叫 `uiApp.PostCommand(RevitCommandId.LookupPostableCommandId(PostableCommand.TileViews))`；
  PostCommand 是排入佇列、要等這次 ExternalEvent 的 `Execute()` 整段結束、控制權還給 Revit 訊息
  迴圈後才會真的執行，所以程式碼刻意把它放在視圖切換之後最後一步。
- **只留要並排的視圖（2026-09-24）**：一鍵 4 視圖與並排在啟動目標視圖（作用中已是其中一張）
  之後、`TileViews` 之前，對作用中文件的 `GetOpenUIViews()` 逐一 `UIView.Close()` 掉所有不在
  目標清單的視圖（每張獨立 try/catch＋log；只處理作用中文件），避免視窗愈開愈多。
- **並排後縮放到符合（2026-09-24）**：`TileViews` 在本次 ExternalEvent 結束後才改變視窗大小，
  同一事件內的 `ZoomToFit` 會失效。`CameraViewZoomService.ScheduleZoomToFit` 先記下各視窗的
  `GetWindowRectangle()`，訂閱一次 `UIApplication.Idling`；等目標視圖都開著、視窗大小已變動
  （或已過 3 個 tick）且連續兩個 tick 相同（穩定）時，對每個 `UIView` 呼叫 `ZoomToFit()` 後取消
  訂閱；最多等 20 個 tick，逾時照做一次。平面／剖面框住裁剪框、等角框住 Section Box。透視圖：
  API 文件沒有說明 `ZoomToFit` 在透視圖上是否移動相機（未查證），所以採防護式——前後比對
  `GetOrientation()`，有變動就用交易還原並記 log，本工作階段之後跳過透視圖。④「全部符合畫面」
  按鈕立即對選取相機已開啟的 4 張視圖再做一次。
  ⚠️ **已知限制（未實機驗證，僅能講清楚機制）**：Revit「並排視窗」(WT) 只在使用者關掉「使用索引
  標籤檢視」(Tabbed Views) 時才會真的把視圖排成多個浮動視窗；如果目前是標籤頁模式，PostCommand
  送出後畢竟還是會執行，但畫面上可能只是切分頁、不是真正並排。這是 Revit UI 設定造成的限制，不是
  這裡程式碼的 bug。
- 「還原」：只關閉「並排檢視」這次額外開啟的視圖（Revit 不允許關掉最後一個視圖，遇到時會跳過），
  並把作用中視圖切回並排檢視之前的那一個。2026-09-24 起並排／一鍵 4 視圖會先關閉其他視圖，
  那些被關掉的視圖**不會**被還原重新打開（ToolTip 已註明）。
- **查證結果：Project Browser 右鍵「顯示相機」在平面上對應的功能，沒有找到對應的公開 API 或
  Postable Command**（`PostableCommand` 列舉裡沒有「顯示相機」；相機圖示在平面上的暫時顯示是
  Revit UI 內部行為，不是一般 API 能觸發的操作）。依規格要求，這裡選擇**跳過**，不用暫時性模型
  元素假裝這個功能。

**B. 預覽（唯一入口，2026-09-24 收斂）**

- 獨立無模式視窗「開啟預覽視窗」（`MCP/UI/CameraPreviewWindow.cs`，標題列圖示按鈕），內含
  兩個 `Autodesk.Revit.UI.PreviewControl`（透視圖＋所在樓層平面），保證能動、與停靠面板宿主
  環境無關；視窗關閉時會呼叫兩個 PreviewControl 的 `Dispose()`。
- 原本規劃、實驗性質的「面板內嵌 PreviewControl」分支（收合式區塊，嘗試把 PreviewControl
  直接嵌進停靠面板）已**整段移除**（`MCP/UI/MultiViewSection.cs` 現在只剩並排／還原兩顆
  按鈕）：面板拿掉分頁改成單頁版面後，這塊實驗區塊沒有位置可放，且未經實機驗證能否嵌入
  成功，收斂成只保留這一個保證能動的路徑，降低維護面。

### 4.9 C：快速開啟動作與右鍵選單（2026-09-24 v5）

DataGrid 名稱欄單擊、迷你 key plan 標記左鍵單擊、兩處的右鍵選單，三個入口共用同一套「開啟
動作」與「更多動作」邏輯（`CameraManagerPanelControl.RunConfiguredOpenAction` /
`ShowCameraContextMenu`）：

- **開啟動作（單擊觸發，寫死為一鍵 4 視圖，沒有可切換的設定）**：先呼叫
  `CameraListSection.SelectByViewId` 讓 DataGrid 選取狀態與地圖高亮同步，再呼叫
  `RunOneClickFourViews(forceResetSection: false)`——跟動作列「一鍵 4 視圖」按鈕完全同一條
  程式路徑，沿用目前的等角範圍設定（`CameraManagerSettings.IsoExtentMode`）。
- **右鍵選單（DataGrid 列、地圖標記都共用）**：一鍵 4 視圖／僅打開透視圖／重新命名…／
  複製到其他樓層…／重設剖面到視線。「重新命名…」彈出一個小型主題化輸入 Popup
  （`ShowRenamePopup`／`EnsureRenamePopupBuilt`，樣式沿用 `CameraManagerTheme`，跟「複製到
  其他樓層」的既有 Popup 同一套視覺語言），取代原本 DataGrid 名稱欄的就地編輯；「複製到其他
  樓層…」直接叫用 `PresetsSection.OpenCopyToLevelsPopup()`，跟動作列按鈕共用同一個 Popup，
  不重複實作選樓層 UI。
- **重入防護**：一鍵 4 視圖會開交易＋建立/更新 3 張視圖＋切換作用中視圖，`_fourViewsInFlight`
  旗標確保同一時間只有一個在跑；連續快速點名稱／標記／選單只會執行第一次，其餘忽略並在
  狀態列提示「正在建立 4 視圖，請稍候...」。這面旗標無論成功、失敗（`doc==null`／
  `CreateOrUpdateFourViews` 回錯誤／後續拋例外）都保證會被清掉（`try/finally`），不會卡死。
- **不影響原有手勢**：樓層／離樓板 cm／水平角／俯仰角欄仍是單擊即編輯；地圖標記左鍵拖曳
  （平移）、雙擊空白處（符合畫面）、滾輪縮放都不受影響——右鍵選單是全新手勢，不佔用任何
  既有的左鍵語意。
- ⚠️ **未經 Revit 實機驗證**：本次交付時 Revit 程序不能重啟部署新 DLL（見 §9），只能靠
  程式碼閱讀與 QA/QC 靜態檢查確認邏輯，尚未在 AR_CPC 實測「單擊名稱／單擊標記／右鍵選單各
  項」的實際手感與時序。

## 5. 第 2 階段：Key Plan 與相機標記

### 4.9 補充：「一鍵 4 視圖」已落地（2026-09-24）

第 1 階段停靠面板重排版面時，動作列的主要按鈕就是「一鍵 4 視圖」，把下面
5.1／5.3 的核心功能提前交付（不必等到使用者手動按「產生 Key Plan」——這裡就是那個按鈕）：
對選取的相機建立或更新平面／剖面／等角三張 Key Plan 視圖＋B 型標記，然後依序啟動透視／
平面／剖面／等角四張視圖，最後 `PostCommand(TileViews)` 並排。實作在
`MCP/Core/CameraKeyPlanService.cs`，由 `CameraManagerPanelControl.DoOneClickFourViews()` 呼叫。
細節與已知限制見下方 5.1／5.3 逐項標記。

### 5.1 產生的視圖（按「一鍵 4 視圖」時建立/更新；每層總覽仍待做）
| 視圖 | 名稱 | 內容 | 狀態 |
|---|---|---|---|
| 平面 | `Key Plan-<相機名>-平面` | 相機所在樓層平面；裁剪框自動框住相機位置＋視野扇形（含固定 margin），刻意不旋轉、保持來源平面原本的座標系；建立/更新時強制清除繼承的 ViewTemplate 並確保 Sections 類別可見（見下方原生剖面線說明） | ✅ 已落地（`GetOrCreatePlanView`／`ConfigurePlanCropBox`／`EnsureSectionsVisible`） |
| 剖面 | `Key Plan-<相機名>-剖面` | 方向依「剖面方向」設定（2026-09-25：預設「對齊房間長邊」，另有「沿視線」，見下方第五輪修訂）；以下為「沿視線」的舊描述——沿視線水平方向的剖面：cut plane 精確包含相機水平視線；範圍＝樓層下方 1m 到上層樓板底 +0.5m（垂直，2026-09-24 修訂；本層頂算不出來時退回眼睛 +3m），讓垂直視野扇形完整落在裁剪框內、相機背後一點到遠裁剪之外一點（水平）、前後各 1.5m（厚度，未查證假設值）；重跑「一鍵4視圖」時若偵測到使用者手動調整過剖面位置/方向，CropBox 不會被覆寫（見下方「保留手動調整」） | ✅ 已落地（`GetOrCreateSectionView`／`BuildSectionBox`），保留手動調整邏輯 2026-09-24 追加 |
| 等角 | `Key Plan-<相機名>-等角` | 方向（2026-09-25 第六輪，取代第五輪「相機精確 yaw 往下俯 35.26°」——那不是標準等角）＝**真正的標準等角**：在 ViewCube 上方四角（水平 NE/SE/SW/NW＝yaw 45°/135°/225°/315°，專案 X/Y 軸，0°=+Y 順時針）中挑水平方向與相機水平前方向最接近者，forward＝(±1, ±1, −1)/√3（俯角 atan(1/√2)≈35.264°），`CameraMath.SnapToIsoCorner`；log 記「相機yaw → ViewCube 角 yaw」。眼睛放在 SectionBox 中心沿 −forward 退到框外（相機後上方），up 用 `CameraMath.BuildLevelUp` 算出與 forward 垂直、不滾動的上方向——從相機起點那一側看向房間。範圍：XY＝相機眼睛＋視野扇形（同平面 Key Plan 的算法，遠裁剪先夾到 30m 上限再加平面同款 margin——XY 範圍公式與平面 Key Plan 完全共用，兩者理論上涵蓋同一塊區域）；Z 下限＝相機所在樓層，往下找支撐樑延伸（規則同 `set_3d_section_box` 的 `includeSupportingBeams`，抽到共用的 `SectionBoxLevelHelper`）；Z 上限依「等角範圍」ComboBox 兩個模式（2026-09-24 修訂，兩者都必須能從上方看見本層）：「等角：整層（見上層樑）」（預設）＝上層樓板底 −10mm；「等角：只到當層（不含上層）」＝min(上層樓板底, 上層樑底) −10mm。上層樓板底＝XY 重疊、底面高於本層 +300mm、頂面高於眼睛的樓板**或屋頂**（天花板不算，屬於本層）中底面最低者；找不到 → 上一樓層高程 − 該樓層樓板代表厚度（該樓層所有樓板 `FLOOR_ATTR_THICKNESS_PARAM` 中位數，沒有樓板則 300mm）；連上一樓層都沒有 → 眼睛 +3m。上層樑底＝XY 重疊、底面高於本層 +300mm、頂面高於眼睛的結構構架中底面最低者。啟動這張視圖後額外呼叫 `UIView.ZoomToFit()`（見下方） | ✅ 已落地（`GetOrCreateIsoView`／`BuildIsoSectionBox`／`BuildIsoOrientation`），⚠️ 未經 Revit 實機驗證 |
| 每層總覽 | `Key Plan-總覽-<樓層名>` | 該層所有相機 | ⏳ 未做（原規格範圍，尚未排入） |

**等角範圍 ComboBox**：動作列「一鍵 4 視圖」旁的小 `ComboBox`（「等角：整層（見上層樑）」／
「等角：只到當層（不含上層）」，2026-09-24 改名；舊名「只到當層頂」＝下一樓層高程，會把整片
上層樓板包進 Section Box 蓋住本層，已修正），存在 `CameraManagerSettings.IsoExtentMode`（本機設定，即改即存）。
XY／Z 下限（支撐樑）兩種模式共用同一套規則，差異只在 Z 上限怎麼決定。

**等角範圍修訂（2026-09-24 第五次改版，使用者回報 Loft Unit 等角圖框到整排住宅單元、且看不到樓板）**——
取代上表等角列的 XY 與 Z 下限規則（Z 上限不變）：
- **XY＝相機看得到的房間**：房間 phase＝透視圖的 `VIEW_PHASE`；取樣高度＝樓層 + clamp((眼高−樓層)/2,
  0.3m, 1.2m)；`doc.GetRoomAtPoint` 取眼睛所在房間，並沿水平視線與左右 FOV 邊線每 0.5m 取樣、最遠
  min(遠裁剪, 15m)，射線連續離開所有房間超過 1m 即停。XY＝房間 BoundingBox 聯集 +0.5m，再與舊
  扇形框（30m 上限＋2m margin）取交集；一個房間都沒有 → 扇形框、遠端夾到 10m。支撐樑不再把整根樑
  的 XY 聯集進來（只用來往下延伸 Z）。用了哪些房間寫進 log。平面 Key Plan 裁剪維持扇形規則，只有
  房間聯集超出時才外擴包住它。
- **Z 下限＝看得到本層樓板**：舊規則下限＝樓層高程，而樓板頂剛好在樓層高程、板身在下方 → 整片被
  切掉（實測「沒看到樓板」）。新規則：min(本層樓板底 −50mm, 支撐樑底)；本層樓板＝XY 重疊、
  BoundingBox 頂面在樓層高程 ±300mm 內的 Floors 取最低 Z；找不到 → 樓層 −300mm。選用值與來源寫
  log。剖面 Key Plan 垂直下限原本就是樓層 −1m，不受影響。
- **剖面框外框**：三個分支（ES 連結／同名既有／新建）每次一鍵 4 視圖都執行
  `EnsureSectionBoxVisible`（拿掉樣板、取消隱藏 `OST_SectionBox`），並 log 隱藏前後狀態與
  DisplayStyle；DisplayStyle 不變更。⚠️ 外框不可見的根因尚未實機確認（主線以 MCP 取消隱藏
  `OST_SectionBox` 測試中）。

**Section Box 共用邏輯抽出**：`MCP/Core/SectionBoxLevelHelper.cs` 把 `set_3d_section_box`
（`MCP/Core/Commands/CommandExecutor.ViewCropBox.cs`）原本內嵌的「往下找支撐樑」「往上找
最近上層樓板」兩段迴圈抽成共用靜態方法（`IncludeSupportingBeams`／`FindNearestUpperSlab`／
`CountUpperStructureBeams`／`TryGetWorldBounds`；2026-09-24 為相機管理另外「新增」
`FindNearestUpperSlabOrRoof`／`FindLowestUpperBeamBottom`／`EstimateTypicalSlabThickness`，
既有方法未改動），等角 Key Plan 視圖與 `set_3d_section_box`
現在呼叫同一份程式碼；`set_3d_section_box`／`align_view_cropbox_to_element` 的輸出格式與
篩選條件皆未變動（純重構，逐段對照原迴圈搬移，未改變任何比較條件或欄位）。

- 三個視圖與相機的對應關係用 Extensible Storage 記在**視圖元素本身**（Schema
  `RevitMCP_CameraKeyPlanLink`，欄位 `CameraUniqueId` + `Role`），不是靠名稱猜——相機改名
  後重跑「一鍵 4 視圖」仍認得同一批視圖，直接更新、不會生出重複視圖；找不到 ES 連結時退回
  精確名稱比對，兩者都找不到才新建。
- 專案瀏覽器集中在「Key Plan」分類、套用統一的視圖樣板——**未做**（超出本次交付範圍）。
- 相機移動後自動更新——**未做**：目前只在按「一鍵 4 視圖」時重新計算一次，不會隨相機移動
  即時跟著動；要做到「自動」需要掛在 4.4 的雙向連動事件上，之後再評估。
- ⚠️ 未經 Revit 實機驗證（環境內 Revit 使用中，本次交付無法開檔測試）：三個視圖的幾何是否
  真的如預期框住範圍、剖面切面是否真的看得到樓板與人視高，都只能保證程式邏輯自洽與可編譯，
  下一步請在 AR_CPC 實測後回報。

**Key Plan 平面上看不到原生剖面線（2026-09-24 實測回報，已修復根因）**：Revit 原生的剖面線
符號（可直接拖曳調整剖切位置、拖曳即連動剖面視圖）在 Key Plan 平面上完全看不到。根因：
Key Plan 平面是從既有來源平面 `Duplicate` 出來的（`FindSourcePlanForDuplication`），若來源
套用了 ViewTemplate，複製出來的視圖會直接繼承同一個樣板；樣板若控制了 V/G 的「Sections」
類別可見性（很多專案樣板會關掉剖面符號以免出圖雜亂），複製出來的 Key Plan 平面就跟著看
不到剖面線，且面板自己呼叫 `SetCategoryHidden` 也會被樣板蓋掉。修法（`EnsureSectionsVisible`，
建立/更新 Key Plan 平面時都會呼叫一次，含既有的舊 Key Plan 平面）：明確把 `ViewTemplateId`
清成 `ElementId.InvalidElementId`，再 `SetCategoryHidden(OST_Sections, false)`。Key Plan 平面
是本工具自建的輔助/診斷視圖，不是專案出圖用的平面，拿掉樣板不影響使用者原本的樣板本身
（只影響這張新視圖）。兩步都包 try/catch，任何版本差異或視圖類型不支援都不讓「一鍵4視圖」
中斷。

**保留使用者手動調整過的剖面（2026-09-24 追加需求）**：重跑「一鍵 4 視圖」不會再無條件把
剖面 CropBox 蓋回程式算出來的視線位置——`CameraKeyPlanService` 新增第二個 Extensible
Storage Schema（`RevitMCP_CameraSectionState`，固定 GUID
`1A7E4C3B-9D2F-4A6E-8B3C-5E7F2A9D4C1B`，刻意不動 `RevitMCP_CameraKeyPlanLink` 那個 GUID——
ES Schema 一旦建立就不能再增減欄位，同一個 GUID 改欄位定義會弄壞已經寫進使用者 .rvt 的
既有連結），記下「上次程式自動產生剖面時」的視線原點與水平方向。下次執行時比對剖面目前
實際的 `CropBox.Transform`：原點偏差 > 5mm 或方向偏差 > 2° 視為「使用者手動調整過」，這次
只補畫相機標記、CropBox 完全不動，狀態列顯示「剖面位置已保留（你曾手動調整）」；沒有存過
狀態（例如舊版本產生的剖面）視為沒動過，照常更新一次並補存狀態。動作列新增「重設剖面到
視線」按鈕（`CameraKeyPlanService.ResetSectionToSightLine`），無條件把剖面強制覆蓋回目前
相機的視線位置，供使用者想放棄手動調整、還原成自動算出來的視線時使用。

**等角視圖縮放置中（2026-09-24 實測回報：等角視圖開出來整個縮得很小、Section Box 偏在
一角）**：等角視圖的 XY 範圍本來就已經跟平面 Key Plan 共用同一套「相機視野扇形 + margin，
遠裁剪先夾到 30m」算法（見上方等角列的說明），理論範圍應該合理；真正的問題是視圖本身開
出來時沒有被縮放置中到這個範圍。修法：`CameraManagerPanelControl.DoOneClickFourViews` 在
把等角設為作用中視圖、啟用 Section Box 之後，額外取得對應的 `UIView` 呼叫一次
`ZoomToFit()`——3D 視圖啟用 Section Box 時，`ZoomToFit()` 縮放的基準就是目前的裁切範圍。
縮放失敗（例如取不到對應的 `UIView`）會被 catch 吞掉，不讓整個「一鍵4視圖」流程中斷。

#### Key Plan 表現法（2026-09-25）
只作用在本工具自建的 `Key Plan-<相機>-平面／剖面`（等角另見最後一條），每次「一鍵 4 視圖」重新套用
（冪等），每一步獨立 try/catch＋log，失敗不影響建立視圖。④「⋯ 更多 → Key Plan 樣式 ▸ 淡化背景（預設）
／原始」存於 `CameraManagerSettings.KeyPlanFadedStyle`；「原始」＝下次執行時略過 ①–③ 並還原（Display
Model 一般、取消隱藏同一組類別、清掉房間標籤類別取代、清掉 1a 的模型類別取代與視圖內牆的元素取代、取消
隱藏剖面樓層並清其元素取代；比例不還原）。
1. **淡化背景**：先拿掉視圖樣板；`VIEW_MODEL_DISPLAY_MODE`＝0（**一般**；2026-09-25 第七輪起不再設半色調——
   使用者回饋「平面淡化可以，但建築外輪廓要清楚，剖面也是」，Display Model 半色調會連切到的牆一起變灰）。
   log 記 `AsValueString` 供核對，字樣不符時試 0..2。
   **1a. 類別／元素取代**（取代原半色調，平面與剖面皆同，每類別每面牆獨立 try/catch，冪等）：
   - 切到要清楚：牆、結構柱、柱、樓板、屋頂、結構框架、帷幕嵌板／豎框／帷幕系統、樓梯——剖切線黑
     (0,0,0)；剖切線寬牆 5、柱 4、樓板／屋頂僅剖面 5，其餘不改；不換剖切填充圖案，只把剖切前景／背景圖案
     色改中灰 128；投影線改中灰 128（API 的半色調是整個元素一起淡，無法只淡投影線，以顏色模擬）。
   - 其他模型類別：迭代 `doc.Settings.Categories` 中 `CategoryType.Model` 且 `IsCategoryOverridable` 者，一律
     類別取代半色調（門窗、家具、衛浴、燈具、地形、植栽…不寫死清單）。**不淡化**：`OST_Lines`（含子類別
     「相機標記線」）、`OST_DetailComponents`（標記的 FilledRegion）、`OST_Viewers`、`OST_SectionBox`、
     `OST_Cameras`——相機標記維持全彩。
   - 連結模型：`OST_RvtLinks` 類別取代半色調（`RevitLinkGraphicsSettings` 沒有半色調屬性）。
   - 外輪廓加強：視圖內（`FilteredElementCollector(doc, view.Id)`）每面牆設元素取代——外牆＝帷幕牆
     （`WallKind.Curtain`，一律視為外牆）或 `WallType.Function == Exterior`：剖切線黑、線寬 7；其他牆：剖切線
     深灰 (64,64,64)、線寬 5。只看牆類型的「功能」參數，不做幾何外周判定（牆類型功能設錯會判錯）。
   - log 記：強調類別數、半色調類別數、不可取代略過數、外牆／內牆元素取代面數、失敗數。
   - 詳圖線／填滿區域屬視圖專屬元素，不在模型類別內，不受影響。
2. **註解減量**（`CanCategoryBeHidden` 才動）：隱藏軸線、樓層（僅平面）、尺寸、所有 `*Tags` 類別（房間
   標籤除外：保留但類別取代半色調）、文字、參考平面、範圍框、立面標記、詳圖索引、高程點／座標／坡度、
   接圖線、修訂雲形。**不隱藏 Sections 類別**（會連本相機剖面一起消失）。
   - 平面：其他剖面逐元素隱藏，只留本相機剖面（見下方「平面只顯示本相機剖面」）。
   - 剖面：樓層不整類隱藏；逐元素隱藏所有樓層，只留相機下方樓層與其上一層（元素取代半色調）。
3. **比例**：平面取 1:50／1:100／1:200、剖面取 1:20／1:50／1:100／1:200 中最細且裁剪範圍放得進 A3 橫式
   約 400×270mm 者；都放不下用 1:200。比例在標記繪製之前設定（標記尺寸依比例換算）。
4. **裁剪（兩種樣式皆適用）**：平面＝看得到的房間聯集（已含 +0.5m），必要時外擴包住完整扇形（弧上 16
   點取樣，外留 0.5m）；沒有房間聯集退回舊規則（扇形 +2m）。剖面水平範圍＝水平視線穿過的房間（眼睛所
   在房間＋中線取樣命中的房間，BoundingBox 與視線求交）+0.5m，眼睛後方至少 1m、前方至少 1m；算不出來
   退回舊規則（遠裁剪）；垂直範圍不變。手動移過的剖面仍保留不覆寫。
5. **標記加強**：「相機標記線」投影線寬 3→5（既有線型也改）；平面水平扇形與剖面垂直扇形各加一塊半透明
   實心 `FilledRegion`（標記色、表面透明度 70%，先建立、位於邊線與箭頭之下），箭頭見 §5.3。
6. **等角**：`EnsureSectionBoxVisible` 另外檢查 `AreAnnotationCategoriesHidden`（剖面框屬註解類別，總開
   關關閉時整類不顯示），為 true 時改 false 並 log 前後值；再逐類別隱藏等角不需要的註解（同上清單＋
   Sections），只留剖面框外框。`AreAnalyticalModelCategoriesHidden` 只記 log、不變更。⚠️ 根因假設待實機確認。

**平面只顯示本相機剖面（2026-09-25）**：Key Plan 平面內收集 `OST_Viewers`，找出本相機剖面的 viewer
（依序：`ViewSection.GetDependentElements(OST_Viewers)` → viewer 名稱＝剖面視圖名稱 → viewer 的
`VIEW_NAME` 參數；log 記錄實際比對依據與 id），其餘「對應到 ViewType＝Section 視圖」的 viewer 以
`HideElements` 逐元素隱藏（只動 `CanBeHidden` 者），立面／詳圖索引不動；本相機 viewer 若曾被藏會先取消
隱藏；找不到本相機 viewer 時為避免誤藏，本次不隱藏任何剖面。一鍵 4 視圖與「更新相機標記」（裁剪拖曳
即時更新）都會重跑，之後新增的剖面也會被藏；其他視圖與迷你地圖不受影響。

**第五輪修訂（2026-09-25，Snowdon Towers「Loft Unit」挑高夾層房間實測）**——取代上文所有「看得到的房間
聯集」「上層樓板底」「遠裁剪」相關規則：
- **只用眼睛所在房間**：`doc.GetRoomAtPoint`（透視圖 `VIEW_PHASE`）依序試「眼睛高度」「樓層 +1m」「樓層
  +0.3m」（挑高房間跨兩層時眼睛高度仍在房間體積內）。輪廓＝房間邊界線段（Tessellate；取不到用房間
  BoundingBox）。等角 XY、平面裁剪、剖面水平範圍一律＝這個房間 +0.6m（不再沿視線取樣、不再與扇形框
  取交集——舊取樣會穿牆進到鄰戶，Loft Unit 實測框到 4 個房間、剖面 20m）。眼睛不在任何房間 → 扇形
  框（距離夾到 8m）。房間 id／名稱／找到方式寫 log。
- **房間頂為主（挑高＋夾層）**：房間頂＝房間 BoundingBox Max.Z（反映上限＋上限偏移，log 另記
  基準＋`UnboundedHeight` 供核對）。上層樓板只認「底面或頂面 ≥ 房間頂 −0.1m」的 Floors／Roofs
  （`SectionBoxLevelHelper.FindUpperSlabOrRoofAboveRoomTop`，新增方法；房間內整片低於房間頂的夾層
  不算。註：協調者規則原文為「底面 ≥ 房間頂 −0.1m」，另加「或頂面 ≥ 房間頂 −0.1m」是為了讓跨過房間
  頂面的上層樓板仍被認得——房間上限常設在上一樓層高程，而樓板底在其下方）。上層樑只認頂面 ≥ 房間頂
  −0.1m 者。等角頂：整層＝min(房間頂 +0.3m, 上層樓板底 −10mm)；只到當層＝再與上層樑底 −10mm 取小。
  剖面垂直：下限＝樓層 −1m，上限＝房間頂 +0.5m。無房間時沿用舊規則。
- **剖面垂直範圍必含平面剖切面**：讀 Key Plan 平面 View Range 的剖切面絕對高程，若不在剖面垂直範圍
  內就外擴 0.5m 並記 log（剖面不跨剖切面時 Revit 不在平面上畫剖面線）。
- **剖面方向**（使用者拍板）：④「一鍵 4 視圖 ▾」單選「剖面方向：對齊房間長邊（預設）／沿視線」，存於
  `CameraManagerSettings.SectionOrientationMode`。
  - 對齊房間長邊：候選方向＝平面 X 軸＋每條邊界線段方向，取投影外接矩形面積最小者（最小面積外接矩形
    必有一邊與某邊界共線；矩形房間精確）；沒比軸對齊框小 10% 以上就用軸對齊框（剖面線在平面上保持
    水平／垂直）。長短邊差 <15%（近正方形）→ 取與相機水平前方向較接近的軸。剖面通過眼睛、方向號誌取與
    相機前方向同側（剖面上視線箭頭朝右）。水平範圍＝房間沿該軸 ±0.6m；垂直同上。找不到房間 → 退回沿視線。
  - 沿視線：舊規則（剖面包含水平視線），水平範圍＝視線穿越本房間外接框的區段 ±0.6m，眼睛後方至少 1m。
  - 手動拖過的剖面仍保留；「重設剖面位置」（原「重設剖面到視線」改名，動作列與右鍵選單）依目前選的
    方向重算覆蓋。ES 剖面狀態記的是剖面 X 軸（沿視線＝視線、對齊房間＝房間軸）。
- **等角方向跟相機**：見上表等角列（取代固定 TOP-BACK-LEFT）。
- **平面上看不到本相機剖面線（2026-09-25 回報，1:100）**：`EnsureOurSectionMarkerVisible` 每次逐項檢查並記
  log 哪一項成立：①剖面「在比例粗於…時隱藏」（`SECTION_COARSER_SCALE_PULLDOWN_METRIC／_IMPERIAL`，執行期
  以名稱解析）比平面比例細 → 設成可接受的最粗值（5000→2500→…→200 依序嘗試）；②平面 Sections 類別被隱藏 →
  取消；③本相機 viewer 逐元素隱藏 → 取消（原本就有）；④平面半色調讓標記變灰 → viewer 元素取代：非半色調、
  標記色、線寬 5。⚠️ 哪一項是真正根因待實機 log 確認。

#### 樓層／軸線標頭（2026-09-25）
使用者回報剖面 Key Plan 樓層文字的白底色塊蓋住內容——那是樓層標頭族群的不透明背景。**不改族群／類型**（會影響
所有視圖），改為在淡化樣式下：剖面保留的樓層（下方樓層＋上一層）兩端標頭都 `HideBubbleInView(End0/End1, view)`
（先 `IsBubbleVisibleInView`，每次呼叫獨立 try/catch），樓層線本身維持半色調；平面 Key Plan 對視圖內仍可見的樓層／
軸線做同樣處理（正常已被整類隱藏，只是保險）。log 記基準面數、本次隱藏、原已隱藏、失敗數。「原始」樣式不恢復
標頭（不知道原本狀態）。**2026-09-25 修訂**：剖面保留樓層改為「一端標頭伸出裁剪框外 1.5m 顯示」，見 5.1 室外段
下方「剖面保留樓層的標頭」；連結模型樓層見「連結模型樓層」。

#### 室內／室外判定（2026-09-25）
實測：室外相機（眼睛不在任何房間）套用「無房間 → 扇形夾到 8m」後，平面裁剪／等角／剖面範圍都太小。
房間為主的規則只在室內有意義，因此先判定、再套不同規則：
- **判定**：沿用第五輪「眼睛所在房間」查詢（`FindEyeRoom`：眼睛高度 → 樓層 +1m → 樓層 +0.3m）。找得到
  房間＝**室內**，否則＝**室外**。判定結果寫 log；相機清單以**列 ToolTip 前綴**顯示「室內／室外」（300px
  寬已無空間再加一欄，避免橫向捲動），室外另附扇形半徑。
- **室內**：規則不變（上文第五輪＋表現法）。
- **室外**：
  - 扇形半徑（平面標記、剖面標記、迷你地圖）＝min(遠裁剪, 室外範圍半徑)，下限 1.5m。室外範圍半徑為新
    設定 `CameraManagerSettings.OutdoorRadiusM`（預設 60m，有效 5～1000m），④「一鍵 4 視圖 ▾ → 室外
    範圍半徑… (m)」小型主題化 Popup 修改、即改即存。迷你地圖：室外相機另畫一個**模型比例**的虛線扇形
    （隨底圖縮放）；室內相機維持固定像素大小的扇形符號。
  - 平面 Key Plan 裁剪＝非對稱扇形（眼睛＋左右緣＋弧上 16 點取樣）外接框 +2m（平面自身座標系）。
  - 等角：XY＝同一扇形外接框 +2m（模型座標）；Z 下限＝min(下方樓層, 最低樓層) −1m；Z 上限＝XY 範圍內
    牆／樓板／屋頂／結構構架／結構柱／柱／一般模型／樓梯／欄杆／帷幕嵌板／地形（R24+ 含地形實體）／基地／
    植栽的 BoundingBox 最高點（與眼睛取大）+2m（`SectionBoxLevelHelper.FindMaxModelTopInXY`，新增方法，
    `BoundingBoxIntersectsFilter` 超高 Outline）；找不到任何元素 → 眼睛 +3m。**不做樓層切、不找上層樓板
    ／支撐樑**。方向同室內（相機後上方）。
  - 剖面：方向**一律沿視線**（忽略「對齊房間長邊」設定，因為需要房間）；水平＝眼睛後 2m … 扇形半徑；垂直＝
    與等角相同 Z 範圍（仍保證包含平面剖切面）。
  - 表現法（淡化）不變；剖面樓層標頭**只留下方樓層**（見下條）。
- **剖面樓層標頭（2026-09-25，Snowdon Towers 室外相機剖面實測：大量近同高程樓層重疊、部分未隱藏）**：保留＝
  下方樓層＋高差 ≥ 1.0m 的下一個上層樓層（近重複樓層不算；室外只留下方樓層）；其餘樓層取「視圖內
  collector ∪ 全模型 Level」過濾 `CanBeHidden`、未隱藏者，**一次** `HideElements`，批次例外時逐一隱藏並記
  失敗；保留者元素取代半色調。log 記：全模型／視圖內樓層數、處理前可見數、本次隱藏數、保留名稱與高程、
  失敗、處理後仍可見者。Display Model 另以 `AsValueString` 驗證（log 前後字串），不是「半色調」字樣時逐一
  試 0..2 找出符合者。
- **剖面保留樓層的標頭（2026-09-25 修訂，取代上文「兩端標頭都隱藏」）**：使用者要看得到樓層標頭、但不能蓋住
  內容；樓層類型是全域的，不能為單一視圖換類型。改用**視圖專屬 2D 範圍**（只影響這張 Key Plan 剖面，正式視圖
  不動）：保留樓層兩端 `SetDatumExtentType(End0/End1, view, ViewSpecific)` → 取 `GetCurvesInView(ViewSpecific)`
  的線，`SetCurveInView` 改成沿剖面 X 軸橫跨裁剪寬度、**遠離相機眼睛那一側**再伸出裁剪框外 1.5m（模型）→ 該端
  `ShowBubbleInView`、另一端 `HideBubbleInView`。剖面若開了註解裁剪（會切掉框外標頭）則關閉。`SetCurveInView`
  失敗的樓層才退回兩端標頭隱藏。「原始」樣式把 ViewSpecific 端還原成 Model。log 記每層 2D 範圍與標頭端。
  主體樓層的保留／隱藏規則不變（不整類隱藏 Levels、不自畫樓層線——使用者要真的 Revit 樓層，日後改樓層才同步）。
- **剖面樓層改「簡化樓層記號」（2026-09-25 第二版，取代上面三條的淡化樣式行為；使用者拍板：6 個連結都已是「依主體視圖」，連結自己的樓層仍會顯示，且無法逐元素隱藏連結內元素）**：淡化樣式下剖面整類隱藏 `OST_Levels`（主體＋連結樓層基準都不顯示），改畫**簡化記號、會自動跟真的主體樓層同步**：保留樓層（室內＝下方樓層＋高差 ≥1 m 的下一個建築樓層；室外＝下方樓層）各一條橫跨裁剪寬度的詳圖線（線型「相機Key Plan樓層線」灰 160、線寬 1、有虛線樣式就用虛線，只建立一次）＋左端 TextNote（類型「相機Key Plan樓層字」約 2 mm、背景透明、灰 128；內容「樓層名  +高程」依專案單位格式），每個元素以 ES（相機 UniqueId＋Role＝LevelMark＋Level UniqueId）連結 Level，重跑整批替換不重複；主體 Level 改高程／改名 → DocumentChanged 去抖排入交易「更新 Key Plan 樓層線」依目前 Level 值重畫，「更新相機標記」也會重畫。因文字是 TextNote，剖面的文字類別改為不整類隱藏、其他文字逐元素隱藏。舊的逐元素樓層隱藏／半色調／2D 範圍／標頭擺放已移除；「原始」樣式取消隱藏樓層類別、刪除這些記號並還原舊版留下的逐元素設定。log 記刪除／建立數。⚠️ 未經 Revit 實機驗證。
- **連結模型樓層（2026-09-25）**：Snowdon Towers 剖面裡堆疊的「L5 TOS」「R2 TOS」「WV_Cut at 20'」、重複的
  「Parking」等是**連結模型**的樓層，主體逐元素隱藏管不到。淡化樣式下，Key Plan 平面與剖面中每個
  `RevitLinkInstance` 的顯示設定改為「依主體視圖」（`View.SetLinkOverrides(id, new RevitLinkGraphicsSettings
  { LinkVisibilityType = ByHostView })`；已是 ByHostView 或無取代者不動）——依主體視圖時 Revit 不顯示連結自己
  的樓層／軸線／註解，只顯示模型幾何，且照主體視圖的類別取代（連結半色調）。log 記每個連結前→後設定，並以
  `FilteredElementCollector(doc, viewId, linkId).OfClass(Level)` 計數連結中此視圖可見的樓層（此計數是否完全反映
  顯示模式待實機確認）。`Get/SetLinkOverrides` 是 Revit 2024+ API；R22/R23 只記 log、不處理。
- **剖面通過畫面中心（2026-09-25）**：「畫面中心線」＝水平視錐左右緣的角平分線（相機 yaw 偏 (leftDeg+rightDeg)/2；
  非對稱裁剪時≠相機正前方；對稱時相同）。①「沿視線」（含室外）：剖切平面包含通過眼睛的中心線。②「對齊房間長邊」
  （預設）：方向仍是房間軸，但剖切線改通過**可見範圍中心**＝中心線上半個扇形半徑處（半徑已夾在房間 +0.6m 內，
  Z＝眼睛高），X 範圍改為相對此原點；眼睛若超出剖面深度 ±1.5m，深度外擴到含眼睛（+0.5m）讓眼睛標記仍在範圍內。
  扇形半徑（視線離開房間距離）、等角/剖面「視線穿越房間區段」也改沿中心線；平面標記的中央視線與剖面標記的
  水平視線／垂直扇形投影都用中心線。ES 剖面狀態記實際剖面原點（不再固定是眼睛），手動拖曳剖面的保留行為不變，
  「重設剖面位置」依新規則重算。log 記中心線 yaw（與相機 yaw、偏角）及剖面原點。
- 所有範圍（半徑、裁剪、等角 XYZ、剖面 X/Z）與選用模式都寫 log；任一步失敗只記 log、退回較簡單規則，不中斷
  視圖建立。⚠️ 未經 Revit 實機驗證。

### 5.2 同時看到各視圖（排圖紙之前）
| | A. 並排檢視（唯一路徑） |
|---|---|
| 做法 | 面板按「並排檢視」→ 開啟該相機的平面／剖面／等角／透視 4 張視圖，並執行 Revit「並排視窗」（等同快速鍵 WT） |
| 互動 | 4 張都是真的視圖，可直接拖標記、即時看透視圖變化；可拖到第二螢幕 |
| 其他 | 「還原」按鈕回到原本的分頁狀態 |

「面板內嵌 2×2 預覽」分支已於 2026-09-24 移除（見 4.9 B），不再是這裡的選項。

### 5.3 相機標記（A、B 都做，面板可切換）
| | A：模型族群 | B：註解符號 |
|---|---|---|
| 元素 | 一般模型族群（小相機體＋視線箭頭＋視野扇形，視野角與遠裁剪為參數），專屬子類別「相機標記」 | 每張 Key Plan 視圖放一個註解符號 |
| 平面／剖面／3D | ✅／✅／✅ | ✅／✅／❌ |
| 風險 | 可能被算進渲染（Enscape）、數量表 | 每張視圖各一份，要分別同步 |
| 處理 | 透視圖本身用篩選器隱藏；實測 Enscape 是否排除 | 由面板統一同步 |
| 狀態（2026-09-24） | ⏳ 未做（族群型標記留給之後） | ✅ **第一版已落地，視覺已升級**（詳見下方） |

**B 型視覺升級（2026-09-24）**（`CameraKeyPlanService.DrawPlanMarker` / `DrawSectionMarker`）：
- 平面／剖面都改成：眼睛位置圓圈（兩個 180° `Arc` 拼成整圈，避開 `Arc.Create` 對「剛好一整
  圈」角度的退化情形）＋方向桿（`DetailCurve` 線段）＋實心三角形箭頭。
- 箭頭優先用實心 `FilledRegion`（前景填滿樣式＝`FillPattern.IsSolidFill`，找不到現成類型時
  自動複製一個既有 `FilledRegionType` 並命名「相機標記-實心」、套用實心填滿樣式＋Accent 色
  `#3B82C4`，與面板配色一致）；找不到可用的實心填滿樣式等意外狀況會 catch 住、退回一條細線
  頂替箭頭，不讓整個標記流程中斷。
- 圓圈／方向桿／箭頭三角形大小改成「依 `view.Scale` 換算的列印比例固定」（circle 半徑
  1mm、箭頭全長 3mm、箭頭三角形 1.4mm×0.9mm，全部乘上 `view.Scale` 再轉 feet），取代舊版
  「模型空間固定 15cm/100cm」——不同比例的 Key Plan 視圖印出來，標記大小都一樣。
- **箭頭放大＋半透明（2026-09-25，取代上一條的箭頭尺寸）**：箭頭總長（眼睛→箭尖）＝
  max(紙上 8mm × `view.Scale`, 15% × 畫出來的扇形半徑)——平面扇形半徑＝遠裁剪，剖面＝垂直扇形
  （截到裁剪框後）的半徑；頭寬 ≈ 0.6 × 總長、頭長 ≈ 0.467 × 總長（沿用舊比例），桿仍是線。箭頭
  `FilledRegion` 在所屬視圖設「依元素取代」：表面前景＝實心填滿、顏色＝標記色、表面透明度 50
  （`OverrideGraphicSettings.SetSurfaceTransparency`；依據 Autodesk 社群已解答帖「How to change
  transparency of a filled region」＝Override Graphics in View › By Element › Surface Transparency
  對填充區域有效，⚠️ 未實機驗證）；設定失敗退回 Halftone＋淡化 50% 的標記色並記 log。「標記顏色」變更時，
  同一交易內把所有既有箭頭／扇形填滿（Role＝MarkerPlan/MarkerSection 的 FilledRegion）的取代顏色一併更新
  （保留各自的透明度）。一鍵 4 視圖與「更新相機標記」共用同一段繪製程式，每次先刪舊再畫，不重複。
- **扇形不蓋住其他東西（2026-09-25）**：詳圖元素在 Revit 一律畫在模型之上，只能做到最接近——扇形填滿透明度 70%→**90%**（平面與剖面共用常數 `MarkerFanTransparency`，眼睛楔形維持 30%）；每次建立／更新標記後以 `DetailElementOrderUtils.SendToBack` 把扇形填滿送到該視圖詳圖最底層，再把眼睛楔形與標記線 `BringToFront`，失敗只記 log。
- **剖面垂直視野扇形（2026-09-24）**：`Key Plan-<相機>-剖面` 在同一組標記內加畫垂直視野——
  剖面平面包含水平視線，所以在該平面內從眼睛沿「相機俯仰角 ± vFOV/2」畫兩條視錐邊線＋兩線
  之間的弧（與平面扇形同樣是細線＋弧，無填色）。vFOV = 2·atan( tan(hFOV/2) · (cropHeight/
  cropWidth) )，hFOV 與長寬比都取透視圖 CropBox（**估算值**，`CameraMath.EstimateVerticalFovDeg`）。
  邊線長度＝遠裁剪距離，若先碰到剖面裁剪框邊則截短（兩條取較短者，扇形半徑一致）。使用者
  手動移過剖面時，所有點先投影到剖面目前的實際平面。
- **標記線型與顏色（2026-09-24）**：所有標記 DetailCurve（圓圈、方向桿、扇形邊線與弧、剖面垂直
  扇形）套用線型「相機標記線」（`OST_Lines` 子類別，不存在時建立一次：顏色取設定、投影線寬 3、
  實線；已存在則不覆寫，使用者也可在「物件型式 → 線 → 相機標記線」改），箭頭 FilledRegion 的
  外框也套同一線型；箭頭填滿類型固定用「相機標記-實心」（不再借用任意既有實心類型）。
  ④「標記顏色」：6 色色票（藍 #3B82C4 預設／紅 #D9534F／橘 #E8A33D／綠 #4CAF7A／紫 #8E6BBF／
  黑 #1C2B3A）＋「自訂…」（`System.Windows.Forms.ColorDialog`，以反射呼叫，專案不新增 WinForms
  參考）；選色後在單一交易「變更相機標記顏色」內同時改線型 `LineColor` 與「相機標記-實心」前景
  色，既有標記一次全部變色；選擇存在 `CameraManagerSettings.MarkerColorHex`。
- **扇形半徑與 FOV（2026-09-25 第五輪，取代上文「平面扇形半徑＝遠裁剪」「剖面截到裁剪框」）**：平面水平扇形與
  剖面垂直扇形用同一個半徑＝水平視線離開眼睛所在房間的距離（視線與房間邊界線段求交）+0.6m，夾在
  [1.5m, 遠裁剪]；眼睛不在房間＝min(遠裁剪, 8m)。剖面扇形不再截到剖面裁剪框。剖面可能不沿視線（對齊房間
  長邊），因此視線、扇形邊線一律投影到剖面平面（水平分量去掉剖面法向分量、不正規化＝斜交時自然縮短），
  弧為三點弧（斜交時為橢圓弧近似）；視線與剖面平面夾角 >60° 記 log，接近 90° 只畫眼睛圓圈。FOV 改由
  `CameraMath.ComputePerspectiveFov` 計算（見 §4.4），清單視野角欄、③ 位置圖、平面／剖面扇形共用。
- **非對稱視錐＋眼睛楔形（2026-09-25，取代上文「±hFOV/2」「俯仰角 ± vFOV/2」「實心三角形箭頭」）**：
  使用者只拖透視裁剪框的一邊時，Revit 視錐對正前方不對稱（實機 log：`xMm[-29,14] dMm[30,30]` → 左緣
  −44.0°、右緣 +25.0°），對稱扇形會跟 Revit 自己的藍色視錐線對不上。`ComputePerspectiveFov` 另外回傳四個
  相對正前方的帶號邊界角（裁剪形狀頂點的 atan2 極值）：`LeftDeg`／`RightDeg`（沿相機 right＝forward×up，
  右為正）、`DownDeg`／`UpDeg`（上為正）；退回 CropBox 候選 A/B 時為對稱 ±h/2、±v/2，總角 h/v 仍供清單欄。
  平面扇形＝`RotateHorizontal(forward, −edge)`（逆時針為正、相機右在順時針側），從右緣逆時針掃到左緣；
  剖面扇形＝pitch+Down … pitch+Up；③ 位置圖＝畫布角 planar+edge（Y 向下＝順時針為相機右）。中央視線沿相機
  正前方（平面／位置圖畫到扇形半徑）。原本朝前的三角形箭頭改成「眼睛楔形」：頂點在眼睛、沿同樣兩條
  （非對稱）邊界向外張開的實心小扇形，長度沿用箭頭規則 max(紙上 8mm, 15% 扇形半徑)，同標記色、表面
  透明度 30%（扇形填滿 70%，所以根部讀成較深一塊）；③ 位置圖同樣以 alpha≈70% 的小楔形取代箭頭。眼睛圓圈保留。
  每次更新 log `leftDeg/rightDeg/downDeg/upDeg`。⚠️ 平面用的是相機座標系的水平邊界角，俯仰不為 0 時與 Revit
  視錐線在平面上的投影會有小差異（未實機比對）。
- **標記失敗不影響建立視圖**：平面／剖面標記各自包在 `SubTransaction`＋try/catch，失敗只回滾
  標記並記 log，一鍵 4 視圖照常建立視圖。
- 視野扇形（僅平面）維持原本的物理範圍算法（左右邊界線＋弧線，跟著 `farClipFeet`／
  `halfFovRad`），不受這次視覺升級影響。
- 每個標記元素（含新加的 `FilledRegion`）都貼了 Extensible Storage 標記
  （`Role = MarkerPlan` / `MarkerSection`），每次「一鍵 4 視圖」先刪除同一台相機、同一 Role
  的舊標記（含 FilledRegion）再重畫，不會愈跑愈多。
- 3D 等角視圖刻意不畫標記（比對規格表格：B 型本來就不支援 3D）。
- ⏳ 未做：雙向拖標記連動（「在 Key Plan 上拖標記 → 相機跟著動」）、族群化的 A 型標記、
  Enscape 排除實測。
- ⚠️ **未經 Revit 實機驗證**（本次交付 Revit 使用中，無法開檔測試）：`FilledRegionType`
  的 `Duplicate`／`ForegroundPatternId`／`ForegroundPatternColor` 賦值行為、`FilledRegion.
  Create` 的三角形 CurveLoop 是否真的落在該視圖平面上、圓弧兩個半圓拼接處是否有可見縫隙，
  都只依 API 文件推斷寫成，下一步請在 AR_CPC 實測後回報。半視角接近/超過 180° 時已在程式
  內 try/catch 跳過扇形弧線，不會讓整個標記失敗，但仍建議實機測試涵蓋這類極端相機角度。

- 雙向：在 Key Plan 上拖標記 → 相機跟著動；相機改了 → 標記跟著動。**（仍未做，見上）**

## 6. 第 3 階段：出圖與版本

### 6.1 一鍵排圖紙
- 每台相機一張圖紙：透視圖＋Key Plan 平面＋剖面＋等角。
- 使用專案現有圖框；版面位置固定，之後可調。
- 圖紙上的視埠就是同一張視圖，在圖紙上啟動視埠拖標記也會連動。

### 6.2 相機視角版本
- 面板上「儲存視角」並取名（例如「A 案」），清單可切回任一版本。
- 存在 .rvt 內（建議 Extensible Storage），同事開檔也看得到、也能切換。
- 儲存內容：眼睛位置、方向、上方向、視野、遠裁剪。

### 6.3 視覺精修
- 統一字體（Microsoft JhengHei UI 12–13 px）、8 px 間距、主要／次要按鈕區分、狀態列顯示最後動作與錯誤。

## 7. 儲存位置

| 資料 | 存在哪 | 誰看得到 |
|---|---|---|
| 相機、相機標記、Key Plan、圖紙 | .rvt（存檔即保存） | 所有開這個檔的人 |
| 相機視角版本 | .rvt（Extensible Storage） | 所有開這個檔的人 |
| 人視高預設值 | 外掛設定檔 | 只有本機 |

## 8. 技術約束

- 面板發出的所有 Revit API 呼叫都經過 `ExternalEventManager`；結果用 Dispatcher 送回介面。
- 模型修改一律在 Transaction 內，每次操作一個可復原步驟。
- ElementId 使用 `RevitCompatibility` 輔助（R24 為 Int32、R25+ 為 Int64）。
- `get_camera_info` 的輸出欄位不可改變（共用 `MCP/Core/CameraMath.cs`）。
- 只部署到 Revit 2024 前要先關閉 Revit；部署後比對 DLL SHA256。

## 9. 已知問題與待查證

| 項目 | 狀態 |
|---|---|
| 透視圖內滑鼠導覽是否觸發連動 | 未查證，第 1 階段實測 |
| 視野角能否可靠讀寫 | 未查證，第 1 階段實作時驗證 |
| 匯出底圖是否剛好等於裁剪框範圍 | 未查證；以裁剪框外框疊圖目視檢查 |
| 相機標記 A 是否被 Enscape 算圖 | 未查證，第 2 階段實測 |
| `PreviewControl` 能否嵌入停靠面板 | 未查證（無法在無 Revit 環境下測）。第 1 階段已實作雙保險：主要交付是保證能動的獨立無模式視窗（4.9 B），面板內嵌版包 try/catch、失敗自動降級顯示中文訊息，絕不會讓例外波及 Revit。實機驗證後再決定要不要把面板內嵌版升級成主要交付 |
| Project Browser「顯示相機」在平面上的對應功能 | 已查證：找不到對應的公開 API／Postable Command，第 1 階段（4.9 A）選擇跳過，不用暫時模型元素假裝 |
| 並排視窗 (WT/TileViews) 在「使用索引標籤檢視」模式下的實際表現 | 未查證。程式碼已依規格送出 PostCommand，但 Revit 在標籤頁模式下可能只切分頁、不是真正並排；使用者實測時請先確認 Revit 選項是否關閉標籤頁模式 |
| R22/R23 編譯失敗（`CommandExecutor.LinkDisplay.cs` 使用新版 API） | 既有問題，與本功能無關，另案處理 |
| 迷你 key plan 滾輪縮放／拖曳平移／符合畫面（`KeyPlanSection` 2026-09-24 改寫） | 未查證：邏輯與座標數學已寫成並通過編譯，但滑鼠事件的手感（拖曳門檻、選取相機圓點與拖曳平移的手勢是否真的不衝突、縮放中心是否真的貼著游標）無法在無 Revit 環境下驗證，需 AR_CPC 實測 |
| 註記裁剪外擴的底圖對位換算（`ExportBaseMap` 2026-09-24 新增） | 未查證：`ViewCropRegionShapeManager` 沒有直接的「是否啟用」旗標，用 `LookupParameter("Annotation Crop")`（找不到時退回外擴量非零的代理判斷）——這套判斷邏輯本身未在真實 .rvt 對照「開／關註記裁剪」兩種平面實測過 |
| **Key Plan 剖面視圖整片灰色（2026-09-24 AR_CPC 實測回報，`Key Plan-3D View 2-剖面`）** | **未查證，本次交付刻意沒有猜測性改程式碼**。剖面厚度只有前後各 1.5m（`SectionDepthHalfCm`），若該相機位置恰好落在連結地形 `TOPO_CPC`（基地在坡地上）或某片樓板/牆內，這 3m 厚的切片整段落在實體內部，畫面就會呈現整片灰／土色——這是幾何上完全可能發生、且與症狀吻合的解釋，但沒有 Revit 環境可以實際查證該相機當下的地形高程與剖面厚度是否真的重疊，所以**不猜測調整深度或排除連結顯示**。使用者可先自行判斷：把該剖面暫時的 Far Clip／連結顯示改成半透明/隱藏 `TOPO_CPC`（`set_link_display_settings`／既有的 Revit V/G 對話框）試看看畫面是否恢復正常；若確認是地形造成，下一步再回來決定要不要把「剖面厚度」或「是否自動把連結地形設為半色調」做成可調參數。**未實作**「把連結模型設為半色調」：`RevitLinkGraphicsSettings` 只有 `LinkVisibilityType`（ByHostView/ByLinkView/Custom）與 `LinkedViewId` 兩個已查證可用的屬性（見 `CommandExecutor.LinkDisplay.cs` 既有用法），沒有找到／驗證到可以直接對單一連結設定「半色調」的 API，貿然猜一個屬性名稱寫下去有很高機率編譯失敗或行為不對，故本次選擇如實回報、不動這部分程式碼。 |
| **開檔後 ~5–10 秒無操作自動崩潰（`journal.0436`／`journal.0438.txt` `ExceptionCode=0xe0434352`；事件記錄檔兩次 `SxSActivationContext.DeactivateActCtx`→`Finalize()` 的 `SEHException`）** | **第二輪（2026-09-24 21:12，claude-opus-5）：根因仍未取得堆疊證明，但已改成「面板預設不做任何事」並加上必定留下堆疊的診斷**。第一輪（20:45，Idling 延後＋try/catch）部署後仍在 20:57 崩潰，journal.0438 顯示崩潰前 5 秒內沒有任何 RevitMCP 的 `Jrn.ExternalEventExecution`。讀碼＋反射查到的事實：①共用的 `ExternalEventManager` 只有一個 `_action` 欄位，連續 `ExecuteCommand` 會覆寫前一個動作（讀快照→匯出底圖→讀剖面線三連發只剩最後一個，`_baseMapRefreshInFlight` 永遠清不掉；且與 MCP WebSocket 命令共用同一欄位），其 catch 還會跳 `TaskDialog`；②反射掃描 Revit 2024 `AdWindows.dll` IL：整個組件只有 `Autodesk.Windows.TaskDialog.PrivateShow` 會建立 `SxSActivationContext`（包在 try/finally 內），所以事件記錄檔那兩筆 finalizer 堆疊是「某個 TaskDialog 在 Dispose 時失敗」——很可能是 Revit 崩潰保護自己的「unrecoverable error」對話框的後果，而不是起因（未證實）；③`BaseMapSnapshot.CropTransform` 把 Revit `Transform`（APIObject）帶到 WPF 執行緒呼叫 `.Inverse/.OfPoint`；④程式化設定底圖 combo（`SetPlanOptions`）與預設選取都會觸發底圖匯出／讀剖面線；⑤`RunOnUiThread` 在 Idling／ExternalEvent 內是同步執行，WPF 版面工作直接跑在 Revit API 回呼裡。修正：面板懶載入——開檔、`ViewActivated`、面板 `Loaded` 一律不讀、不匯出、不畫，停在「按『重新整理』載入相機」；只有使用者按重新整理（或點清單／地圖區）才讀快照＋匯出預設底圖；之後同一份文件的 `DocumentChanged` 只做輕量清單更新（沿用現有底圖重畫標記，不匯出）；文件切換／關閉自動清回中性狀態；底圖只在「重新整理／使用者選底圖 combo／使用者點選相機」時匯出；新增 `CameraManagerEventQueue`（FIFO、不丟動作、不跳 TaskDialog）；`Transform` 攤平成純 double；`RunOnUiThread` 一律非同步；地圖 NaN/Infinity 防護。診斷：`CameraManagerDiagnostics`（AppDomain Unhandled／FirstChance（操作中、觀察窗內或 RevitMCP 丟出）／UnobservedTask／Dispatcher 全記錄／版面風暴偵測），同步直寫 `%APPDATA%\RevitMCP\Logs\CameraManagerCrash_yyyyMMdd.log`。⚠️ **未經 Revit 實機驗證**；若再崩潰，該檔會有例外型別與堆疊。 |
| **一鍵 4 視圖沒反應（2026-09-24 21:33 AR_CPC）** | **已修（21:45 第三輪，未實機驗證）**。根因（crash log 實證）：剖面狀態 ES Schema 的 double 欄位沒設 Spec，`SchemaBuilder.Finish()` 丟「Units are required for field OriginX」（前面的 SEHException 是同一例外在原生層的第一機會紀錄），整筆「建立相機 Key Plan」交易回滾，錯誤只寫在狀態列、沒進 log。修正：換新 GUID `5B2D8E61-…`（`RevitMCP_CameraSectionStateV2`；舊 GUID 從未註冊成功，不讀），座標欄位 `SpecTypeId.Length`＋`UnitTypeId.Feet`、方向分量 `SpecTypeId.Number`＋`UnitTypeId.General`；Schema 在交易外先建；剖面狀態寫入/讀取失敗只記 log、不中斷建立視圖；失敗時 log＋狀態列都有原因。`RevitMCP_CameraKeyPlanLink`（純字串欄位）不受影響、GUID 不動。面板第一次「重新整理」時做一次 Schema 自我檢查，結果寫 RevitMCP log（OK/FAIL）。 |
| **清單數字欄／樓層欄無法直接編輯（2026-09-24 21:34 AR_CPC）** | **已修（21:45 第三輪，未實機驗證）**。根因：①`DataGrid.SelectionChanged` 是冒泡事件，樓層欄每列的顯示用 ComboBox 在 `Items.Refresh()` 時各自冒泡上來，被當成使用者選取→每台相機排一次 `RefreshSelectedSectionLine`（log 每次「底圖匯出結束」後同秒 10–15 筆＝相機數）；②`CellEditEnding` 每次提交都設 `e.Cancel=true`＝「取消結束編輯」，格子卡在編輯中；③單擊編輯靠 `CurrentCellChanged`＋`IsKeyboardFocusWithin`，停靠面板首次點擊常進不了編輯，Enter 後又把下一列拉進編輯；④樓層欄預設以 `ElementId` 排序→IComparable 例外。修正：只轉發 `OriginalSource==DataGrid` 且選取集合真的變了的選取；讀剖面線同時最多一個、合併重複，沒有 Key Plan 剖面的相機（快照新增 `HasKeyPlanSection`）完全不排；單擊可編輯格＝聚焦→選列→BeginEdit；Enter 提交一格並結束列交易、Esc 取消、選完樓層（下拉關閉）立即提交；編輯中延後 `SetRows`／`Items.Refresh()`／`SetLevels`，編輯結束後套用；排序改用 `LevelName` 與原始 double。 |

## 10. 驗收（每階段）

1. R24／R25／R26 建置 0 錯誤；R22／R23 不新增錯誤。
2. 部署到 Revit 2024，DLL SHA256 一致。
3. `scripts/verify-qaqc.ps1 -SkipBuild -SkipDeploy` 無新增 FAIL。
4. 使用者依該階段的實測步驟在 AR_CPC 測過，截圖確認。
