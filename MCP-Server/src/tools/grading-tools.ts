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
                looseFactor: {
                    type: "number",
                    exclusiveMinimum: 0,
                    description:
                        "鬆方係數（如 1.25）；與 compactionFactor 成對提供時，登記簿與回應加記鬆實方三本帳",
                },
                compactionFactor: {
                    type: "number",
                    exclusiveMinimum: 0,
                    description: "壓實係數（如 0.9）；須與 looseFactor 成對提供",
                },
            },
            required: ["toposolidId", "floorIds"],
        },
    },
    {
        name: "solve_balanced_elevation",
        description:
            "平衡高程反求：以二分法試算控制樓板的統一升降量，使整地淨土方逼近目標（預設 0＝挖填平衡）。每次試算完整跑整地管線後回滾、不留痕；apply=true 且收斂時把偏移寫入樓板並落成方案（含登記簿）。",
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
                    description: "整地模式（同 grade_toposolid_to_floors）",
                },
                offsetDistance: {
                    type: "number",
                    exclusiveMinimum: 0,
                    description: "offset_transition 模式的外延距離（公尺）",
                },
                slopeRatio: {
                    type: "string",
                    description: "slope_transition 模式的目標坡度，格式 1:n（例如 1:12）",
                },
                maxExtension: {
                    type: "number",
                    exclusiveMinimum: 0,
                    description: "slope_transition 模式的放坡延伸上限（公尺），預設 20",
                },
                targetNetCubicMeters: {
                    type: "number",
                    default: 0,
                    description: "目標淨土方（m³，Fill−Cut，負值＝餘土）；預設 0 求挖填平衡",
                },
                toleranceCubicMeters: {
                    type: "number",
                    exclusiveMinimum: 0,
                    default: 10,
                    description: "收斂容差（m³）；淨土方與目標差距小於此值即停",
                },
                maxAdjustMeters: {
                    type: "number",
                    exclusiveMinimum: 0,
                    default: 10,
                    description: "樓板升降試算區間 ±此值（公尺）；區間端點無法夾住目標時誠實回報並中止",
                },
                maxIterations: {
                    type: "integer",
                    minimum: 1,
                    maximum: 30,
                    default: 10,
                    description: "二分法最大迭代次數（每次迭代重跑一次整地試算，約數秒）",
                },
                apply: {
                    type: "boolean",
                    default: false,
                    description: "true 且收斂時：把解出的偏移寫入樓板並落成整地方案；false 僅試算不動模型",
                },
                schemeName: {
                    type: "string",
                    description: "apply=true 時落成方案的名稱；未提供時預設「平衡方案（±X.XX m）」",
                },
                looseFactor: {
                    type: "number",
                    exclusiveMinimum: 0,
                    description: "apply=true 時傳遞給落成方案的鬆方係數（與 compactionFactor 成對）",
                },
                compactionFactor: {
                    type: "number",
                    exclusiveMinimum: 0,
                    description: "apply=true 時傳遞給落成方案的壓實係數（與 looseFactor 成對）",
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
