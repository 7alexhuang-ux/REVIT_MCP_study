import { Tool } from "@modelcontextprotocol/sdk/types.js";

export const viewCropBoxTools: Tool[] = [
    {
        name: "align_view_cropbox_to_element",
        description: "將指定視圖對齊到目標元素的 BoundingBox。2D 使用 CropBox（只調整 XY，保留原 Z 深度與 Transform）；3D 為舊客戶端相容入口，會轉交 set_3d_section_box 並使用真正的 View3D Section Box，不操作 CropBox。3D 可分別設定 XY、上方與下方 padding。viewId 不指定時使用 active view。",
        inputSchema: {
            type: "object",
            properties: {
                elementId: { type: "number", description: "要對齊的目標元素 ID（例如 Detail Group 的 ID）" },
                viewId: { type: "number", description: "目標視圖 ID（選填）；不填則使用當前 active view" },
                padding_mm: { type: "number", description: "向外擴大的邊距（公釐），預設 0", default: 0 },
                padding_xy_mm: { type: "number", description: "3D Section Box 的水平外擴距離（mm）；省略時沿用 padding_mm" },
                padding_bottom_mm: { type: "number", description: "3D Section Box 向下外擴距離（mm）；省略時沿用 padding_mm" },
                padding_top_mm: { type: "number", description: "3D Section Box 向上外擴距離（mm）；省略時沿用 padding_mm" },
                includeSupportingBeams: { type: "boolean", description: "3D 時將與目標底面相接且 XY 相交的結構構架完整納入 Section Box", default: false },
                supportingBeamTolerance_mm: { type: "number", description: "判定樑頂貼近目標底面的容許值（mm），預設 300", default: 300 },
            },
            required: ["elementId"],
        },
    },
    {
        name: "set_3d_section_box",
        description: "將非樣板 3D 視圖的真正 Section Box 對齊到指定元素的完整 XYZ BoundingBox，並關閉 CropBox 避免雙重裁切。可分別設定水平、上方與下方 padding，並可自動納入與目標底面相接的完整支承樑，同時排除上一層樓板。若只需清理由舊流程誤啟用的 3D CropBox，設 clearCropOnly=true，此時可省略 elementId。",
        inputSchema: {
            type: "object",
            properties: {
                viewId: { type: "number", description: "目標 3D 視圖 Element ID；省略時使用目前作用中的 3D 視圖" },
                elementId: { type: "number", description: "Section Box 要包覆的元素 ID；clearCropOnly=true 時可省略" },
                padding_mm: { type: "number", description: "Section Box 在 X/Y/Z 三方向向外擴大的距離（mm），預設 1000", default: 1000 },
                padding_xy_mm: { type: "number", description: "水平 X/Y 外擴距離（mm）；省略時沿用 padding_mm" },
                padding_bottom_mm: { type: "number", description: "底面向下外擴距離（mm）；省略時沿用 padding_mm" },
                padding_top_mm: { type: "number", description: "頂面向上外擴距離（mm）；省略時沿用 padding_mm" },
                includeSupportingBeams: { type: "boolean", description: "將與目標底面相接且 XY 相交的結構構架完整納入 Section Box；房間檢討建議 true", default: false },
                supportingBeamTolerance_mm: { type: "number", description: "判定樑頂貼近目標底面的容許值（mm），預設 300", default: 300 },
                setActive: { type: "boolean", description: "完成後切換至目標 3D 視圖，預設 true", default: true },
                selectElement: { type: "boolean", description: "完成後選取目標元素，預設 true；clearCropOnly 時忽略", default: true },
                clearCropOnly: { type: "boolean", description: "只關閉 3D 視圖的 CropBox，不修改 Section Box；用於清理舊錯誤狀態", default: false },
            },
        },
    },
    {
        name: "shift_view_cropbox",
        description: "在 CropBox 自身座標系中平移視圖的 CropBox。dx 正值往右、dy 正值往上（單位公釐）。viewId 不指定時使用 active view。",
        inputSchema: {
            type: "object",
            properties: {
                viewId: { type: "number", description: "目標視圖 ID（選填）；不填則使用當前 active view" },
                dx_mm: { type: "number", description: "X 方向位移（公釐，正值往右）", default: 0 },
                dy_mm: { type: "number", description: "Y 方向位移（公釐，正值往上）", default: 0 },
            },
        },
    },
];
