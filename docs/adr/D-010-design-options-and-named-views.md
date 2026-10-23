# D-010：主模型一次＋逐選項實體幾何，命名視圖共用 GLB 原點

> 文件性質：架構決策紀錄（人類與 AI 共用）。日期：2026-10-23。
> 延續 [D-009 高面數處理](D-009-web-compare-heavy-elements.md)。本輪僅建置，不部署；AR_CPC 實機驗收尚未完成。

**背景**

Issue Manager 需要在瀏覽器切換多個 Design Option Set，並載入 Revit 的命名 3D 相機。只在目前可見 GLB 節點加 extras，不會產生未顯示選項的實體。跨集合匯出所有組合則會重複主模型與其他集合的幾何。

**決策**

1. 新增 `list_design_options`、`export_design_options`、`export_named_views`。`export_web_compare` 預設路徑保持既有行為；只有 `designOptions:true` 分流到分件匯出，不再產生原先的 PNG／IFC／camera 套件。
2. 主模型以全新暫存等角 3D 視圖的 CustomExporter 匯出一次，細分前排除所有選項成員。主文件各選項由 `ElementDesignOptionFilter` 列出實際成員，抽取 `Element.Geometry` 的 Solid／Mesh／GeometryInstance；不依目前視圖是否看得到、不更改主選項。
3. 不把 `VIEWER_OPTION_VISIBILITY` 猜成完整 V/G 設定。視圖選項可見性回 `null` 與 notes。主文件選項幾何採明示的 `elementGeometry` 後備模式；連結文件內的選項暫不支援，回 warnings。
4. 暫存視圖在 `TransactionGroup` 中建立，`finally` 一律 RollBack。不修改、複製或儲存模型原檔；不以切換主選項再復原的方式匯出。
5. `setKey`／`optionKey` 使用 Revit UniqueId；名稱僅用於顯示。每個有選項的元素節點帶完整 `designOption`，主模型不帶該欄位。輸出 `design_options.json` 與逐件結果 `design_options_export.json`。
6. 外部 Node 工具合併實際幾何、重編 glTF 索引並保留 extras。只接受完整成功的分件與一致的 option keys；重複 `source+uniqueId` 拒絕合併，避免錯刪不同 link instances。主模型在生產端只產一份，因此不靠合併器猜測重複幾何。
7. 所有分件共用同一個 Revit XYZ 公尺原點；命名視圖沿用它。點轉 `(x−ox,z−oz,−(y−oy))`，方向轉 `(x,z,−y)`。透視 FOV 尚未校準，輸出 `null` 與 warnings；不把裁剪框經驗式升格為真值。

**理由**

[Autodesk 2024 View Templates API 說明](https://help.autodesk.com/cloudhelp/2024/DEU/Revit-API/files/Revit_API_Developers_Guide/Basic_Interaction_with_Revit_Elements/Views/Revit_API_Revit_API_Developers_Guide_Basic_Interaction_with_Revit_Elements_Views_View_Templates_html.html) 明列 Design Options 的 V/G 設定無 API 存取。本機 Revit API 2024.3.60 XML 與編譯確認 `OPTION_SET_ID`、`DesignOption.IsPrimary`、`Element.DesignOption`、`ElementDesignOptionFilter` 可用；不能從可讀的成員關係推論可控制視圖 V/G。

元素幾何能提供實際選項面網格，代價是無法保證與視圖 CustomExporter 的外觀完全一致。合併遵守 [Khronos glTF 2.0 規格](https://registry.khronos.org/glTF/specs/2.0/glTF-2.0.html) 的 buffer alignment、accessor、material、texture 索引，不直接串接 GLB bytes。

**後果**

- 主模型與選項以全模型範圍交付；`viewName`、來源 section box、隱藏狀態、相機及 V/G 覆寫不套用。後備選項只有材質顏色／透明度，沒有外觀貼圖與等高線。這些差異在每次回傳 warnings 列出。
- 任何未知幾何、無幾何成員、例外、共享子元件重疊或預算截斷都產生 `complete:false`，合併器拒絕當成完整交付。群組容器按成員匯出；明確的排除條件另列。
- `maxTriangles` 是每份 GLB 的預算。先以 `excludeElements` 排除已知超慢網格做約三十秒量級的目標 smoke；這是驗收目標，不是已測得的效能保證。
- 命名視圖 section box 使用八角點套 Transform 後的 glTF AABB，同時保留原始旋轉資料。正交尺寸僅可讀已開啟 UIView；尚未校準 pan／zoom，不能宣稱像素對齊。
- 自動啟動採使用者預先建立 marker 的 opt-in；程式碼不建立 marker、不部署 Add-ins。文件開關與連結載入沿用 ExternalEvent UI 執行緒，無靜默保存或猜路徑。
- 現有 `GetIdValue()` 相容層仍把 64-bit 分界放在 Revit 2025；本次不全面更動舊工具。超過 Int32 的 Revit 2024 ElementId 仍是既有限制，穩定關聯以 UniqueId 為準。

操作與驗收步驟見 [WEB_COMPARE_EXPORT.md](../WEB_COMPARE_EXPORT.md)。
