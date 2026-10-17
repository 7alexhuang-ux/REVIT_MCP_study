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
                        "匯出前先把視圖相機設為指定位置（Revit 內部座標、公尺、Z 向上）與垂直視角。只支援透視視圖；不給就沿用視圖目前相機。",
                    properties: {
                        eye: { type: "array", items: { type: "number" }, description: "眼點 [x, y, z]" },
                        forward: { type: "array", items: { type: "number" }, description: "視線方向 [x, y, z]" },
                        up: { type: "array", items: { type: "number" }, description: "上向量 [x, y, z]，需與視線垂直" },
                        fovVerticalDeg: { type: "number", default: 40, description: "垂直視角（度）" },
                        aspect: { type: "number", default: 1.4612, description: "寬高比（預設 1600/1095）" },
                    },
                    required: ["eye", "forward", "up"],
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
