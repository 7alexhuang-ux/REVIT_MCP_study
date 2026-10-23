import { Tool } from "@modelcontextprotocol/sdk/types.js";

/**
 * 網頁檢視器 vs Revit 顯示模式比對包（issue-manager S1 驗證用）。
 * 方法說明見 docs/WEB_COMPARE_EXPORT.md。
 */
export const webCompareTools: Tool[] = [
    {
        name: "export_web_compare",
        description:
            "一次產出「Revit 顯示模式 × 網頁檢視器」兩兩對照所需的檔案到 outputDir：同一個 3D 視圖、同一相機，依序以 Wireframe、HiddenLine、Shading、ShadingWithEdges、FlatColors（一致色彩）、Realistic 各匯出 revit_<mode>.png（wireframe／hiddenLine／shaded／shadedEdges／consistent／realistic），同視圖匯出 cpc.ifc（IFC4 Reference View，失敗退 IFC2x3 CV2.0，只含該視圖可見元素，含 Revit 與 IFC common property sets）、cpc.glb（每個元素一個節點，node.extras 帶 elementId／uniqueId／category／family；Y-up、公尺、已扣原點偏移）、camera.txt（給人看）與 camera.json（給 Playwright：Revit 與 glTF 兩套座標、原點偏移、FOV 或正交尺寸、PNG 像素尺寸、Revit↔網頁模式對照表）。視圖不存在時會從目前作用中的 3D 視圖複製建立（並移除視圖樣板）；匯出期間會切換顯示樣式與作用中視圖，結束後還原。Revit 模式與網頁模式對照：consistent↔FlatColors、shaded↔Shading、realistic↔Realistic、wireframe↔Wireframe、hiddenLine↔HiddenLine（ShadingWithEdges 無網頁對應）。耗時可能數分鐘（6 張圖 + IFC + GLB），回傳每個檔的路徑、大小、耗時與 warnings；檔案超過 500 MB 會在 warnings 提醒。",
        inputSchema: {
            type: "object",
            properties: {
                viewName: {
                    type: "string",
                    default: "WEB_COMPARE",
                    description: "要匯出的 3D 視圖名稱，預設 WEB_COMPARE；不存在時從目前作用中的 3D 視圖複製並命名",
                },
                outputDir: {
                    type: "string",
                    default: "C:\\Project\\issue-manager\\spikes\\s1-glb\\input",
                    description: "輸出資料夾完整路徑（不存在會建立）；同名檔案會被覆寫",
                },
                pixelSize: {
                    type: "integer",
                    default: 1920,
                    minimum: 256,
                    maximum: 8000,
                    description: "PNG 寬度像素（水平方向 FitToPage），只匯出視圖可見區域",
                },
                includeImages: {
                    type: "boolean",
                    default: true,
                    description: "是否匯出 revit_<mode>.png",
                },
                includeIfc: {
                    type: "boolean",
                    default: true,
                    description: "是否匯出 cpc.ifc",
                },
                includeGlb: {
                    type: "boolean",
                    default: true,
                    description: "是否匯出 cpc.glb",
                },
                maxTriangles: {
                    type: "integer",
                    default: 3000000,
                    minimum: 1000,
                    description: "GLB 三角面上限，超過會提前停止並在 warnings 說明（保護 Revit 記憶體）",
                },
                sectionBox: {
                    type: "object",
                    description:
                        "匯出前先在該 3D 視圖開啟 section box（預設 auto：基地線外框＋marginMeters，沒有基地線改用建築範圍；Z 取建築與範圍內地形表面高低再加 belowMeters／aboveMeters）。PNG、IFC、GLB 全部共用這個範圍；範圍會留在視圖上。mode=off 不動視圖現有設定。回傳的 sectionBox 欄位列出依據與數值。",
                    properties: {
                        mode: { type: "string", enum: ["auto", "manual", "off"], default: "auto" },
                        marginMeters: { type: "number", default: 10, description: "基地外框往外多留的公尺數（XY）" },
                        belowMeters: { type: "number", default: 2, description: "最低地形／建築底再往下的公尺數" },
                        aboveMeters: { type: "number", default: 5, description: "最高地形／建築頂再往上的公尺數" },
                        min: { type: "array", items: { type: "number" }, description: "mode=manual：[x, y, z?]，Revit 內部座標公尺" },
                        max: { type: "array", items: { type: "number" }, description: "mode=manual：[x, y, z?]，Revit 內部座標公尺" },
                        apply: { type: "boolean", default: true, description: "false 時只計算並回報，不改視圖" },
                    },
                },
                camera: {
                    type: "object",
                    description:
                        "匯出前先把視圖相機設為指定位置（Revit 內部座標、公尺、Z 向上）。只改方向（eye/forward/up），不改裁剪框：Revit 透視裁剪框不是真正的視錐，由視角反推會出全白 PNG；fovVerticalDeg/aspect 只在 setCrop=true 時使用。只支援透視視圖；不給就沿用視圖目前相機。",
                    properties: {
                        eye: { type: "array", items: { type: "number" }, description: "眼點 [x, y, z]" },
                        forward: { type: "array", items: { type: "number" }, description: "視線方向 [x, y, z]" },
                        up: { type: "array", items: { type: "number" }, description: "上向量 [x, y, z]，需與視線垂直" },
                        fovVerticalDeg: { type: "number", default: 40, description: "垂直視角（度）" },
                        aspect: { type: "number", default: 1.4612, description: "寬高比（預設 1600/1095）" },
                    },
                    required: ["eye", "forward", "up"],
                },
                contourIntervalMeters: {
                    type: "number",
                    default: 1,
                    minimum: 0,
                    description: "GLB 內地形（Toposolid／Topography 朝上面）等高線間距，公尺，絕對高程的整數倍；0 表示不產生等高線。",
                },
                excludeCategories: {
                    type: "array",
                    items: { type: "string" },
                    description: "GLB 略過的類別（Revit 介面語言的類別名稱，例如 Planting、Entourage）；用來控制三角面數。",
                },
                excludeElements: {
                    type: "array",
                    items: { type: "string" },
                    description: "GLB 略過的元件，格式「來源:元件ID」，來源為 (host) 或連結文件標題，例如 \"ID_CPC:123456\"。",
                },
                maxElementTriangles: {
                    type: "integer",
                    default: 100000,
                    minimum: 0,
                    description: "單一元件三角面門檻；超過的元件依 overCapMode 處理。0 表示不設門檻。注意：門檻只省檔案與載入時間，不省 Revit 細分時間（要省時間用 excludeElements）。",
                },
                overCapMode: {
                    type: "string",
                    enum: ["simplify", "box", "drop"],
                    default: "simplify",
                    description: "超過門檻元件的處理：simplify＝二次誤差邊收縮簡化（不改 Revit 模型）；box＝外框盒佔位；drop＝不匯出。",
                },
                simplifyRatio: {
                    type: "number",
                    minimum: 0,
                    maximum: 1,
                    default: 0.1,
                    description: "simplify 的保留比例（預設 0.1＝保留 10%）；給 0 則簡化到 maxElementTriangles。",
                },
                levelOfDetail: {
                    type: "integer",
                    minimum: -1,
                    maximum: 15,
                    default: -1,
                    description: "Revit 曲面細分等級（ViewNode.LevelOfDetail，0 最粗、15 最細）；-1 沿用 Revit 預設。只影響 Revit 自己細分的曲面，不影響匯入的網格。",
                },
                linkedTerrain: {
                    type: "string",
                    enum: ["auto", "always", "off"],
                    default: "auto",
                    description: "連結模型的 Toposolid／Topography 寫入 GLB 的方式。auto：CustomExporter 沒匯出連結地形時，自己三角化補上並依 section box 裁切；always：一律補；off：不補。",
                },
            },
        },
    },
];


// 與既有匯出的重網格、排除條件保持同一份 schema。
const exportProperties = webCompareTools[0].inputSchema.properties!;
webCompareTools[0].inputSchema.properties!.designOptions = {
    type: "boolean", default: false,
    description: "true 改走 export_design_options 分件匯出（不產生六模式 PNG／IFC）；用 tools/merge-option-glbs.mjs 合成可切換的單一 GLB。",
};
webCompareTools.push(
    {
        name: "list_design_options",
        description: "唯讀盤點設計選項集與選項，含 ElementId、UniqueId、IsPrimary、元素數。3D 視圖選項可見性無可靠 API 時回 null 及 notes，不推測。",
        inputSchema: { type: "object", properties: { includeViews: { type: "boolean", default: true } } },
    },
    {
        name: "export_design_options",
        description: "把主模型及每個設計選項分別匯出 GLB，避免跨選項集組合爆炸；保留逐元素 extras 並加 designOption 穩定 UniqueId。暫存視圖與設定在 TransactionGroup RollBack 後復原；API 無法可靠顯示選項時明確失敗。產生 design_options.json 與 design_options_export.json，再用 merge-option-glbs 合併。",
        inputSchema: {
            type: "object",
            properties: {
                ...Object.fromEntries(Object.entries(exportProperties).filter(([key]) => !["designOptions", "includeImages", "includeIfc", "includeGlb", "pixelSize", "camera"].includes(key))),
                optionKeys: { type: "array", items: { type: "string" }, uniqueItems: true, description: "選項 UniqueId；省略或空陣列匯出全部" },
                originOffsetMeters: { type: "array", items: { type: "number" }, minItems: 3, maxItems: 3, description: "共用 Revit XYZ 公尺原點偏移；所有分件與命名視圖必須一致" },
            },
        },
    },
    {
        name: "export_named_views",
        description: "唯讀匯出非樣板 3D 命名視圖到 named_views.json，含 eye／target／up、顯示模式及 glTF sectionBox。與 GLB 共用單一原點；需明示 originOffsetMeters 或既有 camera.json。未驗證的透視 FOV 回 null 與 warnings。",
        inputSchema: {
            type: "object",
            properties: {
                outputDir: exportProperties.outputDir,
                uniqueIds: { type: "array", items: { type: "string" }, uniqueItems: true },
                originOffsetMeters: { type: "array", items: { type: "number" }, minItems: 3, maxItems: 3 },
                cameraPath: { type: "string", description: "camera.json 路徑；省略讀 outputDir/camera.json" },
            },
        },
    },
);
