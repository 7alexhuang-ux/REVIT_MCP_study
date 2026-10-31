/**
 * 牆/門窗/結構工具 — architect, structural Profile
 */

import { Tool } from "@modelcontextprotocol/sdk/types.js";

export const wallTools: Tool[] = [
    {
        name: "create_wall",
        description: "建立非結構直線牆，座標與高度為公釐。可明確指定樓層與基本牆類型；未指定樓層時使用目前平面視圖的樓層，無樓層則報錯。定位線為牆中心線、基準偏移為零。回傳值會揭露實際採用的樓層與牆類型（LevelSource / WallTypeSource）。",
        inputSchema: {
            type: "object",
            properties: {
                startX: { type: "number", description: "起點 X 座標（公釐）" },
                startY: { type: "number", description: "起點 Y 座標（公釐）" },
                endX: { type: "number", description: "終點 X 座標（公釐）" },
                endY: { type: "number", description: "終點 Y 座標（公釐）" },
                height: { type: "number", exclusiveMinimum: 0, description: "牆不連續高度（公釐）", default: 3000 },
                levelId: { type: "integer", minimum: 1, description: "基準樓層 ID（選填；與 levelName 同時提供時必須一致）" },
                levelName: { type: "string", minLength: 1, description: "基準樓層完整名稱（選填；需唯一且完全相符，找不到就報錯）" },
                wallTypeId: { type: "integer", minimum: 1, description: "基本牆類型 ID（選填；與 wallType 同時提供時必須一致）" },
                wallType: { type: "string", minLength: 1, description: "基本牆類型完整名稱（選填；找不到或重名時報錯）" },
            },
            required: ["startX", "startY", "endX", "endY"],
        },
    },
    {
        name: "create_floor",
        description: "在 Revit 中建立樓板。需要指定邊界點座標。回傳值會揭露實際採用的樓層與樓板類型。",
        inputSchema: {
            type: "object",
            properties: {
                points: { type: "array", description: "樓板邊界點陣列", items: { type: "object", properties: { x: { type: "number" }, y: { type: "number" } } } },
                levelName: { type: "string", minLength: 1, description: "樓層名稱（找不到會退回最低樓層，並在回傳值的 Warning / LevelFellBackToLowest 回報）", default: "Level 1" },
                floorType: { type: "string", minLength: 1, description: "樓板類型名稱（選填；可用「族群名: 類型名」。指定了卻找不到或重名會報錯，未指定則用專案預設並以 FloorTypeSource 回報）" },
            },
            required: ["points"],
        },
    },
    {
        name: "create_door",
        description: "在指定的牆上建立門。可指定 sourceElementId 來複製現有門的類型、instance 參數與 facing/hand 朝向。",
        inputSchema: {
            type: "object",
            properties: {
                wallId: { type: "number", description: "要放置門的牆 ID" },
                locationX: { type: "number", description: "門在牆上的位置 X 座標（公釐）" },
                locationY: { type: "number", description: "門在牆上的位置 Y 座標（公釐）" },
                doorType: { type: "string", minLength: 1, description: "門類型名稱（選填；可用「族群名: 類型名」。指定了卻找不到或重名會報錯，不會自動換成別的類型）" },
                sourceElementId: { type: "number", description: "來源門 ID（選填，用於複製其類型、參數與朝向）" },
            },
            required: ["wallId", "locationX", "locationY"],
        },
    },
    {
        name: "create_window",
        description: "在指定的牆上建立窗。可指定 sourceElementId 來複製現有窗的類型、instance 參數與 facing/hand 朝向。",
        inputSchema: {
            type: "object",
            properties: {
                wallId: { type: "number", description: "要放置窗的牆 ID" },
                locationX: { type: "number", description: "窗在牆上的位置 X 座標（公釐）" },
                locationY: { type: "number", description: "窗在牆上的位置 Y 座標（公釐）" },
                windowType: { type: "string", minLength: 1, description: "窗類型名稱（選填；可用「族群名: 類型名」。指定了卻找不到或重名會報錯，不會自動換成別的類型）" },
                sourceElementId: { type: "number", description: "來源窗 ID（選填，用於複製其類型、參數與朝向）" },
            },
            required: ["wallId", "locationX", "locationY"],
        },
    },
    {
        name: "get_wall_info",
        description: "取得牆的詳細資訊，包含厚度、長度、高度、位置線座標等。",
        inputSchema: {
            type: "object",
            properties: { wallId: { type: "number", description: "牆的 Element ID" } },
            required: ["wallId"],
        },
    },
    {
        name: "create_dimension",
        description: "在指定視圖中建立尺寸標註。",
        inputSchema: {
            type: "object",
            properties: {
                viewId: { type: "number", description: "要建立標註的視圖 ID" },
                startX: { type: "number", description: "起點 X 座標（公釐）" },
                startY: { type: "number", description: "起點 Y 座標（公釐）" },
                endX: { type: "number", description: "終點 X 座標（公釐）" },
                endY: { type: "number", description: "終點 Y 座標（公釐）" },
                offset: { type: "number", description: "標註線偏移距離（公釐）", default: 500 },
            },
            required: ["viewId", "startX", "startY", "endX", "endY"],
        },
    },
    {
        name: "create_corridor_dimension",
        description: "走廊寬度標註 — 自動偵測房間邊界的平行牆對，建立精確的牆到牆尺寸標註。回傳每個區段的實測寬度與合規判定。",
        inputSchema: {
            type: "object",
            properties: {
                roomId: { type: "number", description: "走廊房間的 Element ID" },
                viewId: { type: "number", description: "要建立標註的平面視圖 ID" },
            },
            required: ["roomId", "viewId"],
        },
    },
    {
        name: "query_walls_by_location",
        description: "查詢指定座標附近的牆體，回傳牆厚度、位置線與牆面座標。",
        inputSchema: {
            type: "object",
            properties: {
                x: { type: "number", description: "搜尋中心 X 座標" },
                y: { type: "number", description: "搜尋中心 Y 座標" },
                searchRadius: { type: "number", description: "搜尋半徑 (mm)" },
                level: { type: "string", description: "樓層名稱 (選填)" },
            },
            required: ["x", "y", "searchRadius"],
        },
    },
    {
        name: "rejoin_wall_joins",
        description: "恢復先前由 unjoin_element_joins 取消的接合關係（共用 pair 儲存，限同一 Revit session）。",
        inputSchema: { type: "object", properties: {} },
    },
    {
        name: "unjoin_element_joins",
        description: "以指定來源類別為中心，解除其與鄰近 target 類別元素的幾何接合（BoundingBox 外擴 1 ft 搜尋）。預設 target 為 8 類（Walls、Floors、Columns、StructuralColumns、StructuralFraming、StructuralFoundation、Roofs、Ceilings）。常見用法：牆-柱（上色前置）＝ sourceCategory: Walls、targetCategories: [Columns, StructuralColumns]；柱本位 ＝ sourceCategories: [Columns, StructuralColumns]、targetCategories: [Walls, Floors, StructuralFraming]。解除的配對存在同一份 session 儲存，可用 rejoin_wall_joins 還原（限同一 Revit session）。",
        inputSchema: {
            type: "object",
            properties: {
                sourceCategory: { type: "string", description: "來源類別名稱（不含 OST_ 前綴，例如 StructuralFraming、Walls、Floors）。與 sourceCategories 擇一" },
                sourceCategories: {
                    type: "array",
                    items: { type: "string" },
                    description: "多個來源類別（例如 [\"Columns\", \"StructuralColumns\"] 一次涵蓋建築柱與結構柱）。與 sourceCategory 擇一",
                },
                targetCategories: {
                    type: "array",
                    items: { type: "string" },
                    description: "目標類別名稱陣列（選填，預設 8 類：Walls、Floors、Columns、StructuralColumns、StructuralFraming、StructuralFoundation、Roofs、Ceilings）",
                },
                elementIds: { type: "array", items: { type: "number" }, description: "要處理的來源 Element ID 列表（選填，預設為來源類別全部）" },
                viewId: { type: "number", description: "視圖 ID（選填，未給 elementIds 時用來限縮範圍）" },
            },
        },
    },
    {
        name: "join_wall_tops",
        description: "將指定樓層的牆，其頂部與上方的樓板、天花板、結構樑做幾何接合 (JoinGeometry)。回傳成功接合/已接合跳過/失敗的統計與各樓層明細。常用於樓層牆體建模後的批次頂接合。",
        inputSchema: {
            type: "object",
            properties: {
                levels: {
                    type: "array",
                    items: { type: "string" },
                    description: "要處理的樓層名稱列表，例如 [\"2F\", \"3F\"]。省略或空陣列代表處理全部樓層。",
                },
            },
        },
    },
    {
        name: "create_wall_sweeps",
        description: "在指定牆上用牆飾條（Wall Sweep）或分隔縫（Reveal）把牆等分：垂直方向沿牆長、水平方向沿牆高，divisions 等分會建立 divisions-1 條。可指定既有輪廓，或給 profileWidth/profileDepth 自動新建矩形輪廓族群。先用 listOnly 查專案有哪些飾條類型與輪廓，或用 dryRun 預覽等分位置。",
        inputSchema: {
            type: "object",
            properties: {
                wallId: { type: "number", description: "要加飾條的牆 Element ID" },
                divisions: { type: "number", description: "等分數（5 等分會建立 4 條）", default: 5 },
                orientation: { type: "string", enum: ["vertical", "horizontal"], description: "vertical=垂直飾條沿牆長等分；horizontal=水平飾條沿牆高等分", default: "vertical" },
                sweepKind: { type: "string", enum: ["sweep", "reveal"], description: "sweep=凸出的牆飾條；reveal=內凹的分隔縫", default: "sweep" },
                wallSide: { type: "string", enum: ["exterior", "interior"], description: "附著在牆的哪一側", default: "exterior" },
                distanceMeasuredFrom: { type: "string", enum: ["base", "top"], description: "水平飾條的距離基準（垂直飾條不適用）", default: "base" },
                profileTypeId: { type: "number", description: "指定既有輪廓類型的 Element ID（選填，優先於自動建輪廓）" },
                profileWidth: { type: "number", description: "自動建矩形輪廓的寬度 mm（沿牆面方向）；與 profileDepth 都給才會建立" },
                profileDepth: { type: "number", description: "自動建矩形輪廓的凸出深度 mm（垂直牆面方向）" },
                profileFamilyName: { type: "string", description: "自動建立的輪廓族群名稱（選填，預設 MCP-矩形輪廓-寬x深）" },
                baseTypeId: { type: "number", description: "要複製的來源飾條類型 Element ID（選填，預設取第一個）" },
                typeName: { type: "string", description: "新建飾條類型的名稱（選填）" },
                listOnly: { type: "boolean", description: "只回報專案現有的飾條類型與輪廓清單，不動模型", default: false },
                dryRun: { type: "boolean", description: "只預演等分位置，不建立元素", default: false },
            },
        },
    },
    {
        name: "get_all_grids",
        description: "取得專案中所有網格線（Grid）的資訊。",
        inputSchema: { type: "object", properties: {} },
    },
    {
        name: "get_column_types",
        description: "取得專案中所有可用的柱類型。",
        inputSchema: {
            type: "object",
            properties: { material: { type: "string", description: "篩選材質（選填）" } },
        },
    },
    {
        name: "create_column",
        description: "在指定位置建立柱子。",
        inputSchema: {
            type: "object",
            properties: {
                x: { type: "number", description: "X 座標（公釐）" },
                y: { type: "number", description: "Y 座標（公釐）" },
                bottomLevel: { type: "string", description: "底部樓層名稱", default: "Level 1" },
                topLevel: { type: "string", description: "頂部樓層名稱（選填）" },
                columnType: { type: "string", description: "柱類型名稱（選填）" },
            },
            required: ["x", "y"],
        },
    },
    {
        name: "get_furniture_types",
        description: "取得專案中已載入的家具類型清單。",
        inputSchema: {
            type: "object",
            properties: { category: { type: "string", description: "家具類別篩選（選填）" } },
        },
    },
    {
        name: "place_furniture",
        description: "在指定位置放置家具實例。",
        inputSchema: {
            type: "object",
            properties: {
                x: { type: "number", description: "X 座標（公釐）" },
                y: { type: "number", description: "Y 座標（公釐）" },
                furnitureType: { type: "string", description: "家具類型名稱" },
                level: { type: "string", description: "樓層名稱", default: "Level 1" },
                rotation: { type: "number", description: "旋轉角度（度）", default: 0 },
            },
            required: ["x", "y", "furnitureType"],
        },
    },
    {
        name: "get_wall_types",
        description: "取得專案中所有可用的牆類型，包含名稱和 Element ID。",
        inputSchema: {
            type: "object",
            properties: {
                search: { type: "string", description: "關鍵字篩選（選填）" },
            },
        },
    },
    {
        name: "change_element_type",
        description: "變更 Revit 元素的類型（例如將牆從 Type A 改為 Type B）。",
        inputSchema: {
            type: "object",
            properties: {
                elementId: { type: "number", description: "元素 ID" },
                elementIds: { type: "array", items: { type: "number" }, description: "元素 ID 列表（用於批量變更）" },
                typeId: { type: "number", description: "目標類型的 Element ID" },
            },
            required: ["typeId"],
        },
    },
    {
        name: "get_line_styles",
        description: "取得目前專案中可用的線型 (GraphicsStyles)，例如：虛線、細線等。",
        inputSchema: { type: "object", properties: {} },
    },
    {
        name: "trace_stair_geometry",
        description: "自動分析視圖中的樓梯幾何，偵測被牆、版等物件遮擋的邊緣線段，回傳座標以供後續繪製虛線。",
        inputSchema: { type: "object", properties: {} },
    },
];
