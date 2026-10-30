# 萬用窗族生成器：add-in 市場調查（2026-10-30）

查證標記：【抓】＝該 URL 實際 WebFetch 成功；【搜】＝只有 WebSearch 摘要（頁面 403/404/PDF 無文字，未直接抓到），內容待人工複核。價格查不到寫「未查證」。

## 1. 產品表

| # | 產品 | 做什麼 | 操作流程／UI | 可組態範圍 | 輸出形式 | 門窗表 | 價格 | Revit 版本 | 評價／缺點 | URL | 查證 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| 1 | id:door&window（id:arq） | 圖形介面建立參數化門窗 | 選預設型（門窗各 10+）→ 設框／扇／格／窗台／飾板 → 指派材質 → 加入專案；日後可用同介面回改 | 自訂窗最多 6 格、各格可不同扇型；v3.2.0 加橫楣與側窗；公制／英制；3 種細緻度 | 專案內參數化族；所有族帶 shared parameters | 有（shared parameters 可入表） | USD 15/月 或 120/年，有試用 | 2022–2025 | 無評價資料；McNeel 論壇有人批評同類外掛「只是 10+ 預設族調參數」（搜） | https://revitaddons.blogspot.com/2024/07/iddoor-create-fully-configurable.html | 抓 |
| 2 | id:window（前代） | 同上，僅窗 | 6 步：選型→設參數→自訂格→材質→加入專案→回改 | 同上，v1.1.0 加雙開窗反向開啟線 | 專案內參數化族 | 有（shared parameters） | USD 15/月 或 120/年 | 2018–2021 | 版本過舊 | https://revitaddons.blogspot.com/2021/09/idwindow-create-add-and-modify-windows.html | 抓 |
| 3 | UniversalDW（uBIM）— 與本專案定位最近的對手 | 格網式視覺化窗／門族生成器，免進族編輯器 | 選開啟類型→設格網（等分／自訂寬高）→即時預覽→框寬／框厚／灰縫→可合併或拆格→指派材質→產生族檔 | 窗：左右推開、橫拉、固定、上懸、下懸、百葉；門：單／雙開、雙推；框、玻璃、百葉、灰縫材質 | 族檔（保留材質，供算量）；是否多型別未說明 | 未說明（稱可供 take-off） | USD 1/月 | 未說明 | 頁面無評價內容 | https://marketplace.autodesk.com/apps/1c52714e-c17a-4b21-a5d4-d6593e1eb0dd | 抓 |
| 4 | LogiKal Revit Plugin（Orgadata／Forterro） | 與門窗廠 LogiKal 軟體雙向交換：洞口尺寸與數量送廠商，廠商設計完回寫 | Revit 出洞口與數量→廠商在 LogiKal BIM 模組補資料→匯回 Revit；Revit 內有零件表、詳圖剖面等功能（搜） | 由廠商系統（>450 供應商、>700 萬品項）決定 | 回寫 BIM 模型資料；是否生成族未說明 | 有（零件表）；細節未查證 | 外掛免費，但需 LogiKal ≥11.4 與 BIM 授權（LogiKal 價格未查證） | 近三版 Revit（搜） | — | https://www.logikal-software.com/en/product/revit-plugin（抓到的內容是 LogiKal 概述，未列 Revit 版本與價格） | 抓（部分） |
| 5 | Archicad PARAM-O（Graphisoft，對照組） | 內建節點式視覺化參數物件工具，不寫 GDL | 節點圖（Surface／Move／Number Series）→ 調數值 → 即時 3D → 存成庫物件 | 任意參數物件，非專窗 | Archicad 庫物件（GDL） | 走 Archicad 自身表單 | 含於 Archicad SSA／Forward 訂閱，另價未查證 | 非 Revit | 社群說：自訂窗不寫 GDL 就不參數化（搜） | https://graphisoft.com/param-o | 抓 |
| 6 | Autodesk Assistant in Revit | Revit 2027 內建 LLM，呼叫 Revit API 查詢／建立／改屬性 | Revit 內側邊面板，工具呼叫即時顯示（getRooms、createSheet…）；有 Prompt Library | 單檔內多步驟；新建元素、改表單格式、原生圖例仍需手動 | 直接改專案 | 可建門表（官方範例，搜） | 未查證 | 2027（Tech Preview） | 預覽版；成果需人工審 | https://blog.bimsmith.com/Revit-2027-How-Autodesk-Assistant-Bridges-the-Gap-Between-Intent-and-Execution | 抓（第三方部落格） |
| 7 | GEALAN Plug-in for Revit（Planersoftware 3.0） | 廠商外掛：生成幾乎任何窗／門形狀的族 | 選預設窗門，或用「Free Construction」自由構築；自動算 U 值 | 標準窗、角窗、入戶門、陽台門、推拉門等 | Revit 族 | 未查證 | 對建築師免費 | 未查證 | BIMobject 評分 4.0／5（90 則）（搜） | https://www.bimobject.com/sv/gealan/product/inspiring-window-and-door-designs-in-the-gealan-building-model-created-with-the-gealan-plug-in-for-revit | 搜（403） |
| 8 | PrefBIM／K.LINE window generator（Preference，西班牙） | 網頁組態器＋Revit 外掛，產生詳細窗族 | 組態系列、顏色、玻璃、防盜、遮陽；即時熱／聲學值；可新建或改既有族，產 3D 與剖面（DXF） | 廠商系統為限 | Revit 族＋DXF 剖面 | 未查證 | 未查證 | 未查證 | — | https://www.bimobject.com/da/k-line/product/prefbim | 搜（403） |
| 9 | Schüco planning module windows／PolyPlan／BIM catalogue | 廠商 Revit 設計 App：逐步組態幾何與構造，一鍵載入族 | BIMobject 下載；PolyPlan 型錄可嵌入 Revit 外掛 | 廠商 AWS 系統為限 | 族載入模型 | 未查證 | 未查證（需 BIMobject 註冊） | 未查證 | BIMobject 英文頁標示已封存 | https://www.bimobject.com/en/schueco/product/planning-module-windows | 搜（403） |
| 10 | EvolveLAB Window Library／Glyph | 參數化窗族庫（非生成器）；Glyph 為其 Revit 外掛 | 買庫、載入；附參數化標籤與 shared parameter 檔供門窗表 | 庫內預設類型 | 族庫 | 有（shared parameter 檔） | 庫約 USD 995–1,497（各頁不一）；Glyph 未查證 | 庫為 Revit 2020 版；Glyph 2019–2025 | — | https://www.evolvelab.io/product-page/window-library（現 404） | 搜 |
| 11 | VELUX BIM Object Generator | 天窗專用：功能區分頁開啟組態器產生 BIM 物件 | 功能區按鈕→組態器→產物件 | 天窗，非一般窗 | BIM 物件 | 未查證 | 未查證 | 未查證 | — | https://www.bimobject.com/sv/velux-c/product/bim-object-generator-for-vms | 搜（403） |

未找到：Reynaers、Wicona、YKK AP 現行 Revit 組態外掛（Reynaers 僅見 2016 年 Revit 2014 靜態族下載，cadforum.cz）；Kiwi Codes、DiRoots、BIMLOGiQ、Ideate 的「生成窗族」產品（Kiwi Codes 僅有 Family Categorizer 小工具）；Dynamo／pyRevit 沒有現成「Excel 門窗表→生成窗族」套件，近似物是 CTC Family Processor（批次改既有族）與 APS 的 Excel 批次改參數範例（https://aps.autodesk.com/node/1414 ，搜）。以上皆「未查證」。

## 2. AI／LLM 驅動 Revit 現況

| 案例 | 對話入口 vs 傳統 UI 切分 | URL | 查證 |
|---|---|---|---|
| Autodesk Assistant in Revit（2027 Tech Preview） | 內建面板；呼叫 API 取代多層對話框；Prompt Library；自承無法建原生圖例、版面需人工 | https://blog.bimsmith.com/Revit-2027-How-Autodesk-Assistant-Bridges-the-Gap-Between-Intent-and-Execution | 抓 |
| mcp-server-for-revit-python（pyRevit Routes，MIT，220 星） | 主介面是 AI 客戶端；Revit 內僅 MCP>Bridge 連線選單；改模型或跑 Python 的請求會在 Revit 內顯示 payload 供審；有 place_family、list family，無「生成族」 | https://github.com/mcp-servers-for-revit/mcp-server-for-revit-python | 抓 |
| ArchSmarterRevitMcpServer | 3 專案：MCP Server（stdio）／Addin（named pipe + ExternalEvent）／Core（共享契約與 handler，不依賴 MCP SDK 或 Revit UI）；Addin 只有狀態、重連等四顆按鈕 | https://github.com/kilkellym/ArchSmarterRevitMcpServer | 抓 |
| revit-mcp-plugin（已封存，218 星） | Core／UI(WPF 設定視窗)；設定視窗只管載入哪些指令集，指令集以 command.json 定義；無族生成 | https://github.com/mcp-servers-for-revit/revit-mcp-plugin | 抓 |
| ArchiLabs | 瀏覽器端 AI 原生參數化 CAD，Studio Mode 聊天產生 Python Recipes；與 Revit 切開；族處理未說明 | https://archilabs.ai/posts/diroots-familyreviser-alternatives | 抓 |
| Bimwright rvt-mcp／dtducas RevitMCPSDK | 共享 src/shared 核心、各年份薄殼；JSON-RPC 指令登錄；WPF 作設定／除錯 | https://nuget.org/packages/bimwright.rvt.server ；https://www.nuget.org/packages/revitmcpsdk | 搜 |

觀察：開源 Revit MCP 專案都沒有「生成族（.rfa）」功能，Autodesk Assistant 也把新建元素列為手動；窗族生成的 AI 入口目前空白。反過來，id:door&window、UniversalDW 等有 UI 的產品都沒有 AI。

## 3. 架構建議依據：UI 與 AI 共用核心

- ArchSmarterRevitMcpServer 的分層是最清楚的先例：Core 放 command 名稱、ICommandHandler、BridgeRequest/Response，無 MCP／UI 依賴；Server 與 Addin 都是薄殼。（抓）
- Bimwright 以單一 shared 原始碼夾編出多年份殼。（搜）
- 推論（非來源）：窗規格定為一份 JSON（排×樘×格×扇型），WPF 對話框與 MCP 工具都只是「產生 JSON → 呼叫同一個 Generate(spec)」；Excel 匯入即 JSON 陣列。mcp-server-for-revit-python 把「變更前在 Revit 內顯示 payload 供確認」做在 Revit 內，可作兩種入口共用的預覽／確認步驟。

## 4. 功能分配建議表

| 功能 | 建議 | 理由（依據） |
|---|---|---|
| 挑窗型生成族 | 兩者都要（UI 為主） | UniversalDW、id:door&window 全是 UI 格網預覽，同事不用 AI；MCP 負責「三排兩樘、中間上懸」自然語言→JSON |
| 批次從 Excel 門窗表生成 | add-in UI 為主，MCP 可呼叫同引擎 | 市面無現成產品（僅見 APS Excel 改參數、CTC 批次改族），是差異化；選檔、對欄位、預覽、錯誤列表需要對話框；AI 可輔助欄位對映 |
| 放窗後切割飾板 | MCP 為主，UI 一鍵為輔 | 需先查牆／窗關係再決策，屬一次性修補；固定規則再給按鈕 |
| 門窗表與圖例產生 | 兩者都要，核心走既有工具 | Autodesk Assistant 範例可建門表但原生圖例做不到；本專案已有 door-window-legend 工具，直接重用 |
| 送審檢核（採光／通風／排煙） | MCP 為主 | 方法在 domain SOP，結果需解釋與整理；UI 只放「跑檢核」按鈕與結果表 |
| 窗型庫管理 | add-in UI | 瀏覽、縮圖、改名是點選操作；EvolveLAB 賣的是庫而非對話 |
| 競圖快速變體 | MCP 為主 | 「所有 W2 改 4 格、窗台加高 10cm」類批量變體適合自然語言；UI 提供參數面板 |

## 5. 意外與限制
- 多個廠商頁（BIMobject、Schüco、EvolveLAB）WebFetch 回 403／404，#7–#11 只依搜尋摘要，需人工複核。
- UniversalDW（月費 USD 1）與本專案定位幾乎相同，輸出是否多型別、是否支援 Revit 2024 頁面未載明，建議實測。
