import { Tool } from "@modelcontextprotocol/sdk/types.js";

/** Toposolid 依樓板底面投影範圍進行整地。 */
export const gradingTools: Tool[] = [
    {
        name: "grade_toposolid_to_floors",
        description:
            "依指定樓板底面的水平投影範圍整平 Toposolid；支援 footprint_only 直貼、offset_transition 外延漸變、slope_transition 指定坡度放坡三種模式。",
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
                    enum: ["footprint_only", "offset_transition", "slope_transition"],
                    default: "footprint_only",
                    description:
                        "整地模式：footprint_only 投影內直貼板底（邊界垂直落差）；offset_transition 投影外再外延 offsetDistance 漸變回原地形；slope_transition 由邊界以 slopeRatio 固定坡度放坡回原地形",
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
                offsetDistance: {
                    type: "number",
                    exclusiveMinimum: 0,
                    description: "offset_transition 模式的外延距離（公尺）；僅該模式接受此參數",
                },
                slopeRatio: {
                    type: "string",
                    description:
                        "slope_transition 模式的目標坡度，格式 1:n（垂直:水平，例如 1:12）；僅該模式接受此參數",
                },
                maxExtension: {
                    type: "number",
                    exclusiveMinimum: 0,
                    description:
                        "slope_transition 模式的放坡延伸距離上限（公尺），未提供時預設 20；放坡在上限截止時回報警告；僅該模式接受此參數",
                },
                updateExisting: {
                    type: "boolean",
                    enum: [false],
                    default: false,
                    description: "是否更新既有整地結果；本次只接受 false",
                },
                schemeName: {
                    type: "string",
                    description:
                        "方案名稱（寫入登記簿與設計地形 Comments 標籤）；未提供時預設「方案N」（N 為既有方案數+1）",
                },
            },
            required: ["toposolidId", "floorIds"],
        },
    },
    {
        name: "list_grading_schemes",
        description:
            "列出目前模型的整地方案登記簿：每筆含方案名稱、時間、模式與參數、元素 ID、CUT/FILL/淨土方、最大挖深/填高、擾動面積、樓板指標與警告。",
        inputSchema: {
            type: "object",
            properties: {},
        },
    },
    {
        name: "export_grading_comparison",
        description:
            "把整地方案登記簿匯出為 Excel 比較表（.xlsx，一列一方案，數值版）；未指定 outputPath 時存於專案目錄。",
        inputSchema: {
            type: "object",
            properties: {
                outputPath: {
                    type: "string",
                    description: "輸出檔完整路徑（.xlsx）；未提供時預設專案目錄＋時間戳檔名",
                },
            },
        },
    },
];
