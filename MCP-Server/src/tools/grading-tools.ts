import { Tool } from "@modelcontextprotocol/sdk/types.js";

/** Toposolid 依樓板底面投影範圍進行整地。 */
export const gradingTools: Tool[] = [
    {
        name: "grade_toposolid_to_floors",
        description: "依指定樓板底面的水平投影範圍整平 Toposolid；本次僅支援 footprint_only 模式。",
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
                    default: false,
                    description: "是否允許建立整地所需階段；設為 true 可能調整原地形階段，執行前請先儲存模型",
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
