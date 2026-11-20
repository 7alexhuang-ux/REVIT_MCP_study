import { Tool } from "@modelcontextprotocol/sdk/types.js";

/** Toposolid 依樓板底面投影範圍進行整地。 */
export const gradingTools: Tool[] = [
    {
        name: "grade_toposolid_to_floors",
        description: "依指定樓板底面的水平投影範圍整平 Toposolid；本次僅支援 footprint_only 模式。樓板開口（例如外圈地坪挖空建築範圍）不計入投影；以形狀編輯點做出的斜板，折面每塊平面各自計算、平滑曲面則先三角化再逐片計算；多片樓板相鄰或重疊時，每一點取涵蓋該點之樓板中最低的底面，高程落差處自動加一排內縮控制點，重疊會列在 Warnings。",
        inputSchema: {
            type: "object",
            properties: {
                toposolidId: {
                    type: "integer",
                    description: "要整地的 Toposolid 元素 ID",
                },
                floorIds: {
                    type: "array",
                    minItems: 1,
                    items: { type: "integer" },
                    description: "作為整地範圍來源的樓板元素 ID；至少提供一筆",
                },
                mode: {
                    type: "string",
                    enum: ["footprint_only"],
                    default: "footprint_only",
                    description: "整地模式；本次只接受 footprint_only",
                },
                targetFace: {
                    type: "string",
                    enum: ["bottom"],
                    default: "bottom",
                    description: "樓板目標面；本次只接受 bottom",
                },
                allowPhaseSetup: {
                    type: "boolean",
                    default: true,
                    description:
                        "是否自動設定整地所需階段（預設 true）：原地形會改為較早階段建立、於目前階段拆除，設計副本建立於目前階段——與 Revit 原生整地行為一致。設為 false 時不修改階段，僅回報所需變更。",
                },
                updateExisting: {
                    type: "boolean",
                    enum: [false],
                    default: false,
                    description: "是否更新既有整地結果；本次只接受 false",
                },
            },
            required: ["toposolidId", "floorIds"],
        },
    },
];
