# LESSONS — Revit 匯出端踩坑紀錄（S1：Revit → GLB → 網頁檢視器）

> 文件性質：人類與 AI 共用的踩坑紀錄（繁中）。每條固定六欄：症狀／真正原因／怎麼查出來（含證據）／解法／下次怎麼避免／適用範圍。
> 網頁檢視器端的坑記在 issue-manager `spikes/s1-glb/LESSONS.md`。數據來源：`export_web_compare` 的 `export_report.json`、`cpc.diag.log`、`cpc.elements.json`。

---

## L-01 「連結 Toposolid 被 CustomExporter 略過」其實是「所有連結都沒匯出」，而且是預算截斷造成

- **症狀**：Round 1／2 的 cpc.glb 完全沒有 ID_CPC、TOPO_CPC 的內容（1552/1567 個元素 ID 都對得上只含主檔的 cpc.ifc），Round 2 結論寫「CustomExporter 會跳過連結的 Toposolid」，於是加了地形後備三角化。
- **真正原因**：CustomExporter 本來就會進入連結（OnLinkBegin 正常）。是 3,000,000 三角面預算在**第一個連結（ID_CPC）內部**就用完，`IsCanceled()` 回 true，當時還開著的 RVT Links 節點與其底下元素整批沒寫出；同時 MCP 客戶端 10 分鐘逾時，回傳的診斷數字遺失，所以沒人看到。
- **怎麼查出來（含證據）**：把診斷改成即時寫入 `cpc.diag.log`（不依賴 MCP 回傳）。`C:\Temp\s1_diag\cpc.diag.log`：`summary linkBegin=1 triangles=3002205 nodes=1567`（預算 3M、只進了一個連結、節點數＝主檔）。預算拉高後 `C:\Temp\s1_diag2`：`linkBegin=4 triangles=3621794 nodes=2683`，`OnLinkEnd doc=ID_CPC addedTriangles=3479769`、`TOPO_CPC addedTriangles=15114`——原生連結匯出完全正常。
- **解法**：診斷即時落檔（`cpc.diag.log`）＋完整報告寫 `export_report.json`；預算截斷時 `Flush()` 收尾開著的節點；加每元件三角面門檻（見 L-08），不再用整體預算硬砍。地形後備改為只在原生沒匯出地形時才用（auto）。
- **下次怎麼避免**：「某類東西沒匯出」先看**計數**（進入幾次連結、每個文件看到幾個元素），不要先猜 API 行為；長時間匯出的診斷一律寫檔，不要只靠 MCP 回傳。
- **適用範圍**：所有 CustomExporter／IExportContext 匯出（GLB、walkthrough），任何有預算或取消機制的長流程。

## L-02 顏色 sRGB → 線性轉換遺漏，網頁上整體偏亮

- **症狀**：Revit 灰 160 在網頁顯示成約 208。
- **真正原因**：glTF `baseColorFactor` 規格是線性值，舊碼直接把 Revit 的 sRGB 0–255／255 寫進去，three.js 再做一次 sRGB 編碼，等於提亮兩次。
- **怎麼查出來（含證據）**：同一材質取 Revit PNG 像素與網頁像素對照（160 → 208），與 sRGB 曲線一次的偏移量吻合。
- **解法**：`GltfBuilder` 寫入時 `SrgbToLinear()`；`material.extras.srgb` 另存原始 sRGB 給檢視器的「一致色彩」模式用。
- **下次怎麼避免**：任何寫進 glTF 的顏色先問「這個欄位是線性還是 sRGB」；單元測試鎖住 0.5 sRGB ↔ 0.214 線性。
- **適用範圍**：GLB／glTF 匯出、任何把 Revit 顏色交給 PBR 渲染器的流程。

## L-03 網頁檢視器過亮：brightness 提亮＋燈光＋曝光三層疊加

- **症狀**：修完 L-02 後網頁仍明顯比 Revit 亮。
- **真正原因**：正式檢視器為了「看起來亮一點」有 brightness 0.32 提亮、Lambert＋半球光 1.8＋方向光 1.5、寫實模式環境光 1.25 × ACES 曝光 1.3，三層各自提亮。
- **怎麼查出來（含證據）**：逐一關掉各層，對 Revit 圖算平均 L*（`compare.mjs` 的 lightness，排除白底），找出每層貢獻。
- **解法**：spike 檢視器（`viewer_s1.ts`）brightness 歸零，燈光／曝光改由 `tune.json` 依模式對 Revit 圖校準。
- **下次怎麼避免**：比對前先列出所有會改亮度的參數，一次只動一個，用 L* 數字而不是肉眼判斷。
- **適用範圍**：網頁檢視器與 Revit 顯示模式的對照、任何渲染校色。

## L-04 PNG 匯出全白：視圖開著「遠裁剪」，或用視角反推裁剪框

- **症狀**：`ExportImage` 產生的 revit_*.png 全白。
- **真正原因**：(a) 比對視圖 Far Clip Active，154 m 遠裁剪在 190 m 的基地上把整個模型裁掉；(b) 由 FOV 反推並寫回透視裁剪框（setCrop），Revit 的金字塔數值不是它預期的格式，也會全白。
- **怎麼查出來（含證據）**：`export_report.json.viewState` 回報 `farClipActive`／`farClipOffsetFeet`；關掉遠裁剪即恢復。寫入裁剪框的實驗重現全白。
- **解法**：匯出前自動關遠裁剪並在 warnings 說明；`camera` 參數只改方向（eye/forward/up），`setCrop` 預設 false。
- **下次怎麼避免**：PNG 全白先查裁剪（遠裁剪、裁剪框、剖面框），不要先懷疑硬體加速；目前硬體加速維持關閉，與此問題無關。
- **適用範圍**：所有 3D 透視視圖的 ExportImage。

## L-05 地形後備每個元件只有一種材質

- **症狀**：Round 2 地形是單色塊，頂面不是草地材質。
- **真正原因**：後備路徑自己三角化，一個元件只套一種顏色；原生 CustomExporter 是逐面（OnMaterial）給材質。
- **怎麼查出來（含證據）**：L-01 修好後原生地形 `TOPO_CPC|Toposolid` 24 個元件逐面帶材質（grass／gravel／asphalt／concrete walkway 貼圖都有載入紀錄），後備不再需要（`fallbackUsed: false`）。
- **解法**：原生連結匯出為主，後備只在 `nativeLinkTerrainElements == 0` 時啟用。
- **下次怎麼避免**：後備路徑是「原生壞掉」的補丁，原生修好就該退場；先修根因。
- **適用範圍**：連結地形、Toposolid／TopographySurface。

## L-06 Revit 透視裁剪框不是真正的視錐，鏡頭會偏

- **症狀**：網頁與 Revit 構圖有約 40 px 垂直偏移、範圍不一致。
- **真正原因（第 3 輪更正：仍未確定）**：crop box 確實不是視錐（直接用會出全白 PNG 或構圖錯）。上輪的經驗式「視錐切線 ＝ 裁剪框近平面半寬高 ÷ 近平面距離 ÷ tan(20°)，置中」**沒有被真正驗證**：本輪用它匯出後，網頁畫面仍比 Revit 放大約 2 倍。
- **怎麼查出來（含證據）**：上輪的證據是 `fit_camera.mjs` 的非白遮罩 IoU 0.945。本輪發現這個指標在模型塞滿畫面時沒有鑑別力（Revit 模型像素 1,408,999／1,678,400 ＝ 84%）：倍率 0.95–1.05 的 IoU 都 > 0.95，但圖明顯不同。改用 1/8 縮圖亮度相關係數後，倍率 1 只有 0.45；倍率 × 中心偏移網格的最佳值也只有 0.65（倍率 3、垂直偏移 −0.5，且多個解接近）——代表誤差不能只靠視錐解釋，眼點或視線方向也可能不一致。
- **解法**：未解。`camera.json.projection.frustumTan` 仍輸出經驗式並標註「empirical」。下一步要拿 Revit 端的真值：在模型中放已知座標的標記點，用 ExportImage 找出它們的像素位置，直接解相機（PnP），而不是用剪影擬合。
- **下次怎麼避免**：擬合指標要先確認有鑑別力（故意給錯的參數，分數要明顯變差）；「IoU 很高」不等於「對齊」。API 名詞（crop box）不等於數學定義（frustum）。
- **適用範圍**：Revit 透視視圖 ↔ three.js／任何外部相機對齊。

## L-07 玻璃 alpha 0：Revit 材質透明度 100%

- **症狀**：玻璃在網頁完全看不見。
- **真正原因**：`Glass` 材質 node.Transparency＝1、material.Transparency＝100%，直接換算 alpha＝0。
- **怎麼查出來（含證據）**：`transparentMaterials`：`"Glass": "node=1;material=1"`，另有 `Glass(Test)` 0.55、`Glass, Matte` 0.95、`Clear Glass Panel` 0.95。
- **解法**：alpha < 0.12 時改用材質透明度；材質也是全透明則給 0.30 下限，`alphaMode: BLEND`。
- **下次怎麼避免**：把「完全透明」視為資料問題而不是要照抄的值；匯出報告列出所有透明材質。
- **適用範圍**：GLB 匯出的玻璃、帷幕牆嵌板。

## L-08 高面數元件的時間成本在 Revit 細分，不在 GLB；事後簡化省不到匯出時間

- **症狀**：完整 GLB 匯出要 631–1175 秒；直覺以為「超過門檻就自動簡化」可以解決。
- **真正原因**：時間幾乎全花在 Revit 細分**佛像**（ID_CPC:4513008，1,304,136 三角面，匯入的網格）。CustomExporter 在把多邊形交給我們之前就已經付出這個成本；之後再簡化或換外框盒，只縮小檔案與三角面，不縮短匯出。
- **怎麼查出來（含證據）**：同一視圖 GLB-only 匯出：raw（不設門檻）631 秒；排除佛像＋4 張沙發 6.8 秒；只排除佛像 10.6 秒；simplify 本身全程 2.8–4.0 秒（`heavyElements.simplifySeconds`）。
- **解法**：預設路徑分兩層——「已知超重元件」用 `excludeElements` 在 OnElementBegin 直接 Skip（省時間），其他超過門檻的用 `overCapMode: simplify`（省檔案）。詳見 issue-manager `spikes/s1-glb/output/ROUND3.md` 與 ADR。
- **下次怎麼避免**：量「時間花在哪」要用排除實驗分段量，不要只看三角面數；三角面多不等於慢，慢的是 Revit 細分哪個元件。
- **適用範圍**：所有 CustomExporter 匯出，特別是含匯入網格（雕像、植栽、廠商家具）的連結模型。

## L-09 「4 張桌子」其實是沙發：元件型號名稱會誤導

- **症狀**：上輪記錄「4 張桌子各約 242k」。
- **真正原因**：類型名稱 `W2550_D1000_H770mm`／`W2750_D1000_H770mm` 看起來像桌子尺寸，實際族群是 `EXPORMIM_OBI_XL_SOFA_C064`（Furniture）。
- **怎麼查出來（含證據）**：`cpc.elements.json` 同時列 Name、Family、Category。
- **解法**：報告與清單一律同時列族群名稱。
- **下次怎麼避免**：描述元件時以族群名稱為準，不用類型名稱猜用途。
- **適用範圍**：所有元件盤點與報告。

## L-10 LevelOfDetail 只影響 Revit 自己細分的曲面，而且是全域的

- **症狀**：期待調粗 tessellation 能救佛像。
- **真正原因**：`ViewNode.LevelOfDetail` 控制 Revit 對 NURBS／曲面的細分；佛像是匯入網格，完全不受影響（LOD 0 仍 1,304,136）。同時它是整個視圖一個值，門、帷幕嵌板、柱、地形也一起變粗。
- **怎麼查出來（含證據）**：LOD 0／4／8 對照（排除佛像以加速）：沙發 5,237／50,371／242,531；`(host)|Doors` 15,244／23,652／65,016；`(host)|Curtain Panels` 4,248／5,464／13,051；LOD 8 與預設相同。
- **解法**：LOD 只作為選項（`levelOfDetail`），預設 -1（沿用 Revit）。
- **下次怎麼避免**：先分辨元件幾何來源（族群曲面 vs 匯入網格），再決定用細分還是簡化。
- **適用範圍**：CustomExporter、IExportContext 匯出。

## L-11 同一設定的匯出時間可差近 2 倍，單次量測不可靠

- **症狀**：含佛像的七次 GLB 匯出 631／652／836／840／854／905／1175 秒（raw、FINAL、LOD0、box、s10、s20、s05）。
- **真正原因**：佛像細分是單執行緒 CPU 工作，受同機其他工作（其他 Claude session、瀏覽器）影響；Revit 記憶體穩定（約 2.9 GB），不是累積洩漏。
- **怎麼查出來（含證據）**：每次 `export_report.json` 的 `cpc.glb.elapsedMs`；`tasklist` 看 Revit 工作集。
- **解法**：時間比較用「排除實驗」（6.8 s vs 600+ s 這種數量級差）下結論，不用 ±30% 的差異下結論；量瀏覽器效能時不與 Revit 匯出同時跑。
- **下次怎麼避免**：報告時間一律附次數與範圍。
- **適用範圍**：所有效能量測。

## L-12 報告沒記錄請求參數，事後無法追溯

- **症狀**：上輪 FINAL（10:39）ID_CPC 只有 8,726 三角面、沒有任何 Furniture，本輪同視圖是 1,956,403；無法確定上輪是否帶了 `excludeCategories`。
- **真正原因**：`export_report.json` 只記結果，不記收到的參數（且工具 schema 過期時參數會以字串傳入，見 L-13）。
- **怎麼查出來（含證據）**：比較兩次 `trianglesByKey`：上輪沒有 `ID_CPC|Furniture` 鍵，本輪 1,750,024。
- **解法**：待辦——`export_report.json` 加 `request` 欄位原樣回寫參數。
- **下次怎麼避免**：任何會被拿來比較的輸出都要自帶「怎麼產生的」。
- **適用範圍**：所有匯出工具的報告檔。

## L-13 MCP 工具 schema 過期時，新參數以字串送達 C#

- **症狀**：新加的整數／陣列參數在 C# 端讀不到或型別錯。
- **真正原因**：AI 客戶端快取的是舊的工具 schema，未宣告的參數一律當字串送出。
- **怎麼查出來（含證據）**：C# 端記錄收到的 JToken 型別為 String。
- **解法**：C# 讀參數容忍字串（`WebCompareStringList` 接受 JSON 字串陣列；數字用 `Value<int?>()` 轉）；schema 更新後重建 MCP-Server 並重開客戶端。
- **下次怎麼避免**：加參數時 C# 端一律容錯；改完 schema 記得 `npm run build`。
- **適用範圍**：所有 MCP 工具新增參數。


## L-14 設計選項的身分資料不等於未顯示選項的幾何（2026-10-23）

- **症狀／風險**：只替目前視圖 GLB 加 designOption extras，會讓選單看似完整、切換後卻缺幾何。
- **查證**：2024 SDK 有 Element.DesignOption、ElementDesignOptionFilter 和 OPTION_SET_ID；官方 View Templates 文件明列 V/G Design Options 無 API。不能把 VIEWER_OPTION_VISIBILITY 名稱猜成完整覆寫控制。
- **解法**：主模型一次 CustomExporter，各 host option 依實際成員抽 Solid／Mesh／GeometryInstance；在報告明示 elementGeometry 限制。群組改由成員匯出，未知或重疊幾何設 complete=false，不假成功。詳 D-010。
- **驗證範圍**：R24 編譯、真實二進位 GLB 離線 fixture 已跑；AR_CPC 沒有取得實機證據，不能宣稱選項數、幾何完整度或匯出速度。

## L-15 合併器需驗證完整交付與輸出路徑（2026-10-23）

- **發現**：fresh review 用 triangle GLB 重現：report 標 complete=true 但少 option part 仍能合併；outputPath 指到 design_options.json 會覆寫 manifest。
- **解法**：雙向比對 manifest options 與成功分件、拒絕重複 key；輸出限 .glb 且不得覆寫輸入檔。重編貼圖／幾何索引與 alignment，以實體三角形 fixture 驗證，並支援合法空主模型 GLB。
- **避免方式**：Success envelope、檔案存在、metadata 非空都不足以證明模型完整；驗收要檢查實際 mesh、選項集合、parts 完成狀態與同一共用原點。

## L-16 無人流程需涵蓋失敗路徑（2026-10-23）

- **發現**：既有 socket 啟動會彈 TaskDialog 及自動修 port；只加 OnStartup 自動呼叫會阻塞，或干擾別的 Revit。fresh review 另發現啟動 catch 未受 marker guard，以及 reload 有效名稱先執行後才發現下一個錯名。
- **解法**：opt-in 單次 Idling、auto模式關閉對話框與port修復；初始失敗也只記錄。reload 在任何 Load 前驗證整批目標。開檔第三參數固定 false，避免 API 忽略 DoNotDetach 而要求互動；dirty close=false 明確拒絕。
- **限制**：Revit 本身的缺連結、升版、工作共享或其他 modal dialogs 尚未實機驗證；本輪不部署、不建立marker，不能宣稱所有 Revit 提示都已消除。

## L-17 接手他人完成品要逐條對照原需求（2026-10-24）

- **發現**：前一輪建置通過編譯與測試，但逐條對照需求仍有偏離：中央檔搭配 `detach=false` 沒有拒絕、`list_design_options` 缺主模型元件數、`list_links` 缺參考類型與實例數、`reload_links` 空陣列語意相反、報告沒有「貼圖找不到」清單、缺一鍵驗收腳本。
- **解法**：以 `BasicFileInfo.IsCentral` 擋中央檔；補欄位；`names` 省略／空陣列＝全部且只重載未載入者；`textureProblems` 提到報告頂層；新增 `tools/verify-design-options.mjs`（驗證函式離線測試）。
- **避免方式**：「編譯過、測試綠」不等於符合規格；交付前把需求逐條列成清單，各自指到程式行或測試。
