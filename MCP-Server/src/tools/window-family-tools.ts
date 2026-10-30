import { Tool } from "@modelcontextprotocol/sdk/types.js";

/**
 * 萬用窗族生成器（Phase 1）。
 *
 * 規格見 docs/universal-window/SPEC.md §8；決策見 docs/ADR.md D-001～D-007。
 * Phase 1 範圍：拓樸（排數/樘數/每格扇數/每扇開啟方式）在生成時決定死，
 * 尺寸寫入為固定的 Type 參數值（幾何不跟動，Phase 2 才做參考平面活拉）。
 * 所有長度單位皆為公分（cm）；C# 端負責換算為 Revit 內部單位（ft）。
 */
export const windowFamilyTools: Tool[] = [
    {
        name: "generate_window_family",
        description:
            "萬用窗族生成器（Phase 1，固定尺寸）。依規格 JSON（幾排 × 幾樘、每格幾扇、每扇怎麼開）在 Revit 2024 生成一個可直接出圖、可上門窗表、帶台灣送審參數（UW_ 系列共用參數）的鋁窗族（.rfa），並可選擇載入目前專案。" +
            "拓樸（rows/cols/cells 的扇數與開啟方式）在生成時決定死，不是活參數——換組合需重新呼叫本工具生成新族或新型別。" +
            "typeParams（框料尺寸、玻璃厚、交疊量）寫入為 Type 參數值；instanceParams（尺寸、窗台高、MO 切透）Phase 1 為固定值＋型別參數紀錄，幾何不會隨後續修改參數而跟動（Phase 2 才會）。" +
            "所有長度單位為公分（cm）。" +
            "窗型（v0.2）：fixed 固定、casement 外推、sliding 橫拉、awning 上懸、hopper 下懸／內倒、sliding_v 垂直上下拉、screen 紗窗。" +
            "滑動類（sliding／sliding_v／screen）不可與開啟類同格、sliding 與 sliding_v 不可同格、screen 不能單獨成格（至少一個玻璃滑動扇），違反即回錯誤。",
        inputSchema: {
            type: "object",
            properties: {
                familyName: {
                    type: "string",
                    minLength: 1,
                    description: "族名稱（不含副檔名），例如「UW-上固定下外推-2樘」。",
                },
                typeName: {
                    type: "string",
                    minLength: 1,
                    description: "型別名稱，通常以尺寸命名（例如「240x180」），對應門窗表型號。",
                },
                generation: {
                    type: "object",
                    description: "拓樸規格：幾排 × 幾樘、每格幾扇、每扇怎麼開。生成時決定死，不是活參數。",
                    properties: {
                        rows: {
                            type: "integer",
                            minimum: 1,
                            maximum: 3,
                            description: "排數 R，1–3，由上而下。",
                        },
                        cols: {
                            type: "integer",
                            minimum: 1,
                            maximum: 4,
                            description: "樘數 C，1–4，自室外看由左而右。",
                        },
                        cells: {
                            type: "array",
                            minItems: 1,
                            maxItems: 3,
                            description: "格子矩陣，外層為排（由上而下），內層為該排的樘（由左而右）。",
                            items: {
                                type: "array",
                                minItems: 1,
                                maxItems: 4,
                                items: {
                                    type: "object",
                                    description: "單一格 R#C#。",
                                    properties: {
                                        id: {
                                            type: "string",
                                            description: "格子代號，例如「R1C1」。",
                                        },
                                        sashes: {
                                            type: "array",
                                            minItems: 1,
                                            maxItems: 4,
                                            description:
                                                "該格內的扇，依序排列。一格內若全部是滑動類：全是 sliding（可加 screen）則水平交疊、全是 sliding_v（可加 screen）則垂直交疊，紗窗與所罩的玻璃扇同尺寸且佔自己的軌道；否則等分。滑動類與開啟類混用、sliding 與 sliding_v 同格、screen 單獨成格皆為無效規格。",
                                            items: {
                                                type: "object",
                                                properties: {
                                                    type: {
                                                        type: "string",
                                                        enum: ["fixed", "casement", "sliding", "awning", "hopper", "sliding_v", "screen"],
                                                        description:
                                                            "扇的開啟方式：fixed 固定、casement 外推、sliding 橫拉、awning 上懸、hopper 下懸／內倒（鉸鏈在下，預設內倒）、sliding_v 垂直上下拉、screen 紗窗（滑動類，佔一軌，不計玻璃／採光／可開口面積）。",
                                                    },
                                                    hinge: {
                                                        type: "string",
                                                        enum: ["L", "R"],
                                                        description: "鉸鏈側，僅 type=casement 時使用。",
                                                    },
                                                    swing: {
                                                        type: "string",
                                                        enum: ["out", "in"],
                                                        description:
                                                            "開啟方向，適用 casement／awning／hopper。未提供時 hopper 預設 in（內倒），casement／awning 預設 out。in 時平面開啟線畫向室內側、立面符號改虛線。",
                                                    },
                                                    track: {
                                                        type: "integer",
                                                        minimum: 0,
                                                        maximum: 3,
                                                        description:
                                                            "軌序（整數，0＝最外軌），適用 sliding／sliding_v／screen。軌數＝格內最大 track＋1。未指定時：玻璃扇依序交錯 0／1，screen 放最內軌（玻璃最大軌＋1）。相容 v0.1 舊字串值 \"outer\"（＝0）、\"inner\"（＝1），但新規格請用整數。",
                                                    },
                                                },
                                                required: ["type"],
                                            },
                                        },
                                    },
                                    required: ["id", "sashes"],
                                },
                            },
                        },
                    },
                    required: ["rows", "cols", "cells"],
                },
                typeParams: {
                    type: "object",
                    description: "Type 層參數（框料與玻璃尺寸），單位 cm；同一族的不同型別可各自設定。",
                    properties: {
                        frameOuter: {
                            type: "number",
                            exclusiveMinimum: 0,
                            default: 4.5,
                            description: "外框正面寬（cm）。預設 4.5 為未查證估值，待事務所鋁窗廠斷面。",
                        },
                        mullion: {
                            type: "number",
                            exclusiveMinimum: 0,
                            default: 4.4,
                            description: "中框（樘間直料／排間橫料）正面寬（cm），深度同外框。",
                        },
                        sash: {
                            type: "number",
                            exclusiveMinimum: 0,
                            default: 5,
                            description: "扇框正面寬（cm）；固定扇實際使用 sash × 0.5（壓條）。",
                        },
                        frameDepth: {
                            type: "number",
                            exclusiveMinimum: 0,
                            default: 7,
                            description: "外框／中框深度（cm）。預設 7＝YKK AP YRB-A70（事務所慣用，ADR D-010）；A100 為 10。",
                        },
                        glass_mm: {
                            type: "number",
                            exclusiveMinimum: 0,
                            default: 8,
                            description: "玻璃厚度（mm），置於框深中心。預設 8mm 單層（膠合 5+5 為替代）。",
                        },
                        meetingRailOverlap_mm: {
                            type: "number",
                            exclusiveMinimum: 0,
                            description:
                                "橫拉交疊量（mm）。未提供時預設＝扇框寬（sash，換算 mm），依 ADR D-005 事務所慣例；扇寬 = (格寬 + (n−1)·交疊) / n。",
                        },
                    },
                },
                instanceParams: {
                    type: "object",
                    description: "Instance 層參數（整組窗尺寸與安裝資訊），單位 cm；Phase 1 為固定值＋型別參數紀錄，幾何不跟動。",
                    properties: {
                        width: {
                            type: "number",
                            exclusiveMinimum: 0,
                            description: "整組窗總寬 W（cm）。",
                        },
                        height: {
                            type: "number",
                            exclusiveMinimum: 0,
                            description: "整組窗總高 H（cm）。",
                        },
                        sillHeight: {
                            type: "number",
                            default: 90,
                            description:
                                "窗台高（cm），對應 Revit 內建 Sill Height。預設 90（媒體來源）；建技規 §45 特定類組須 ≥110（10 層以上 ≥120），本工具不自動檢核。",
                        },
                        rowRatios: {
                            type: "array",
                            items: { type: "number", exclusiveMinimum: 0 },
                            description: "各排高度佔比（%），由上而下，總和應為 100；未提供時等分。",
                        },
                        colRatios: {
                            type: "array",
                            items: { type: "number", exclusiveMinimum: 0 },
                            description: "各樘寬度佔比（%），由左而右，總和應為 100；未提供時等分。",
                        },
                        frameOffsetFromWallCentre: {
                            type: "number",
                            default: 0,
                            description: "框深中心相對牆心的偏移（cm），正值朝室外。",
                        },
                        mo: {
                            type: "object",
                            description: "MO（開口）切透設定，依 ADR D-006 以空心擠出定義，套用於外飾板／內飾板。",
                            properties: {
                                gapPerSide: {
                                    type: "number",
                                    exclusiveMinimum: 0,
                                    default: 2.5,
                                    description: "MO 每側預留（cm，框外緣加此值）。示意值，未查證。",
                                },
                                cutBeyondExterior: {
                                    type: "number",
                                    exclusiveMinimum: 0,
                                    default: 5,
                                    description: "空心深度自外飾板外再外切的量（cm）。",
                                },
                                cutBeyondInterior: {
                                    type: "number",
                                    exclusiveMinimum: 0,
                                    default: 5,
                                    description: "空心深度自內飾板內再內切的量（cm）。",
                                },
                                voidDepth: {
                                    type: "number",
                                    exclusiveMinimum: 0,
                                    description:
                                        "選填。MO 空心總深度（cm），以框心對稱。省略時 Revit 端用 max(50, 框深+外切透+內切透+20)；" +
                                        "Phase 1 族在生成時不知道主體牆厚，牆特別厚時請明填。",
                                },
                            },
                        },
                    },
                    required: ["width", "height"],
                },
                symbols: {
                    type: "object",
                    description: "圖面符號慣例旗標（ADR D-004）。",
                    properties: {
                        apexAtHinge: {
                            type: "boolean",
                            default: true,
                            description: "外推扇立面三角形頂點是否在鉸鏈側（並頂到扇框外緣的實際鉸鏈位置）。",
                        },
                        planSwingAngle: {
                            type: "number",
                            default: 30,
                            description: "外推扇平面示意角度（度），中等以上詳細程度以實線畫出。",
                        },
                    },
                },
                outputDir: {
                    type: "string",
                    description: "族檔輸出資料夾。未提供時預設 %APPDATA%\\RevitMCP\\families。",
                },
                loadIntoProject: {
                    type: "boolean",
                    default: true,
                    description: "生成後是否載入目前開啟的 Revit 專案。",
                },
                overwrite: {
                    type: "boolean",
                    default: false,
                    description: "載入時若同名族已存在是否覆蓋；預設 false，衝突時工具回報並要求明確指定此旗標。",
                },
            },
            required: ["familyName", "typeName", "generation", "typeParams", "instanceParams"],
        },
    },
];
