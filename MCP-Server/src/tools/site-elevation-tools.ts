import { Tool } from "@modelcontextprotocol/sdk/types.js";

/** 場地高程基準診斷（唯讀）：座標點、Toposolid 等高線設定、Toposolid 表面點高程。 */
export const siteElevationTools: Tool[] = [
    {
        name: "get_site_coordinates",
        description: "讀取作用中文件的高程與座標基準（唯讀）：內部原點、專案基準點、測量點各自的內部座標與共用座標（mm，未經顯示精度捨入）、測量點是否 Clipped、作用中位置的 E/W、N/S、Elevation 與正北角度，以及每個樓層的內部高程、專案高程、共用高程與其類型的 Elevation Base。排查「等高線或高程標籤帶奇怪小數（例如 X.048）」、「連結模型高程對不上」、「Elev 參數看起來是 0 但不確定是否被捨入」時使用。觸發條件：使用者提到測量點、專案基準點、內部原點、共用座標、shared coordinates、survey point、project base point、高程基準、樓層高程對不上。",
        inputSchema: {
            type: "object",
            properties: {},
            required: []
        }
    },
    {
        name: "get_toposolid_contour_settings",
        description: "讀取 Toposolid 的等高線設定（唯讀，Revit 2024+）：每個 Toposolid 的類型、主體樓層（內部與共用高程）、Height Offset From Level，以及類型 Contour Display 的每一列 Start / Stop / Step / 子品類。另外針對每一列，分別假設等高線從內部原點、專案基準點、測量點、主體樓層、主體樓層+偏移起算，算出第一條線的共用高程與對間距的尾數(mm)，讓你拿畫面標籤的尾數去比對 Revit 實際用哪個基準。省略 toposolidId 時列出文件內全部 Toposolid。觸發條件：使用者提到等高線、contour、等高線不是整數、等高線標籤小數、contour display、等高線起點、Start、Interval。",
        inputSchema: {
            type: "object",
            properties: {
                toposolidId: {
                    type: "number",
                    description: "Toposolid 的 ElementId（選填；省略 = 全部）"
                }
            },
            required: []
        }
    },
    {
        name: "get_toposolid_points",
        description: "讀取單一 Toposolid 的形狀點高程（唯讀，Revit 2024+）：形狀點總數、共用高程最小/最大值、所有點共用高程對 stepMm 的尾數分佈（找出整個地形面是否帶同一個系統性位移，例如每點都多 48 mm），以及均勻抽樣的點（內部座標 mm、共用高程 mm、點類型）。用來判斷「地形面本身高程對不對」，而不是只看等高線怎麼畫。觸發條件：使用者提到地形點高程、地形高度對不對、地形整體偏移、toposolid points、地形是不是低了。",
        inputSchema: {
            type: "object",
            properties: {
                toposolidId: {
                    type: "number",
                    description: "Toposolid 的 ElementId"
                },
                maxCount: {
                    type: "number",
                    default: 30,
                    description: "回傳抽樣點數上限（預設 30）"
                },
                stepMm: {
                    type: "number",
                    default: 1000,
                    description: "尾數統計用的間距(mm)，通常等於等高線間距（預設 1000 = 1 m）"
                }
            },
            required: ["toposolidId"]
        }
    }
];
