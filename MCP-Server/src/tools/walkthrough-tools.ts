import { Tool } from "@modelcontextprotocol/sdk/types.js";

/**
 * 建築走動展示交付包：讀取使用者排好的 WALK-01、WALK-02… 透視 3D 視圖站點，
 * 匯出平面／剖面圖、逐格影格、glTF 模型與離線網頁交付包。
 * 資料契約見 viewer/walkthrough/CONTRACT.md（schemaVersion 1）。
 */
export const walkthroughTools: Tool[] = [
    {
        name: "create_camera_stations",
        description:
            "讓 AI 直接在 Revit 裡從零建立人視高透視相機站點（WALK-NN），不需要使用者先手動排站。每筆站點給眼點（公尺）與朝向（target 目標點或 fwd 方向向量，二擇一）即可；名稱未指定時依 stationPrefix/startNumber 自動編號兩位數（WALK-01…）。視角由 fovYDeg/aspect 換算成裁剪框（近似值，實際以出圖影格比例為準）。overwrite=false（預設）時同名視圖已存在會略過並在 skipped 說明原因；overwrite=true 會改寫既有視圖的相機方位。建完站點務必接著呼叫 get_camera_stations 檢查排站規則（間距、眼高、視角一致、跳號），再進 export_walkthrough_package。",
        inputSchema: {
            type: "object",
            properties: {
                stations: {
                    type: "array",
                    description: "要建立/更新的站點陣列",
                    items: {
                        type: "object",
                        properties: {
                            name: {
                                type: "string",
                                description: "站點視圖名稱；未提供時依 stationPrefix + 流水號自動命名（例如 WALK-03）",
                            },
                            eye: {
                                type: "array",
                                items: { type: "number" },
                                minItems: 3,
                                maxItems: 3,
                                description: "相機眼點座標 [x, y, z]，單位公尺，座標系依 coordinates 參數",
                            },
                            target: {
                                type: "array",
                                items: { type: "number" },
                                minItems: 3,
                                maxItems: 3,
                                description: "相機朝向的目標點 [x, y, z]（公尺）；與 fwd 擇一提供",
                            },
                            fwd: {
                                type: "array",
                                items: { type: "number" },
                                minItems: 3,
                                maxItems: 3,
                                description: "相機前方向向量 [x, y, z]；與 target 擇一提供",
                            },
                            up: {
                                type: "array",
                                items: { type: "number" },
                                minItems: 3,
                                maxItems: 3,
                                description: "相機上方向向量 [x, y, z]；未提供時預設世界 Z 正向，會與 forward 正交化",
                            },
                        },
                        required: ["eye"],
                    },
                },
                stationPrefix: {
                    type: "string",
                    default: "WALK-",
                    description: "自動命名用的站點名稱前綴，同 get_camera_stations",
                },
                startNumber: {
                    type: "integer",
                    default: 1,
                    minimum: 1,
                    description: "自動命名的起始流水號（兩位數格式，例如 1 → WALK-01）",
                },
                coordinates: {
                    type: "string",
                    enum: ["revit", "package"],
                    default: "revit",
                    description: "\"revit\"（預設）：Revit 專案座標，公尺；\"package\"：交付包模型座標（需搭配 packageDir 取得 originOffset 加回）",
                },
                packageDir: {
                    type: "string",
                    description: "coordinates=\"package\" 時必填：交付包資料夾完整路徑，用於讀取 manifest 的 originOffset",
                },
                fovYDeg: {
                    type: "number",
                    default: 50,
                    exclusiveMinimum: 0,
                    maximum: 170,
                    description: "垂直視角（度），換算成裁剪框套用到每個新建/改寫的站點",
                },
                aspect: {
                    type: "number",
                    default: 1.7778,
                    exclusiveMinimum: 0,
                    description: "畫面寬高比（寬/高），預設約 16:9",
                },
                copySettingsFromViewName: {
                    type: "string",
                    description: "指定一個既有 3D 視圖，新視圖沿用它的視覺樣式與可見性設定（用複製視圖建立）；未提供或找不到時改用預設 3D 視圖類型新建",
                },
                overwrite: {
                    type: "boolean",
                    default: false,
                    description: "false（預設）：同名視圖已存在時略過並在 skipped 說明；true：改寫既有同名視圖的相機方位與裁剪框",
                },
            },
            required: ["stations"],
        },
    },
    {
        name: "get_camera_stations",
        description:
            "列出命名為「前綴＋數字」（預設 WALK-，例如 WALK-01、WALK-02）的 3D 透視視圖站點，依數字排序，回傳每站的眼點/方向/視角與排站相關 warnings（例如缺號、視圖非透視）。用於出包前檢查站點是否排好，不會匯出任何檔案。",
        inputSchema: {
            type: "object",
            properties: {
                stationPrefix: {
                    type: "string",
                    default: "WALK-",
                    description: "站點視圖名稱前綴；視圖名稱需為「前綴＋數字」才會被視為站點",
                },
            },
        },
    },
    {
        name: "export_walkthrough_package",
        description:
            "建立走動展示交付包資料夾（平面/剖面 PNG、manifest.json、data.js、離線 viewer）：讀取 stationPrefix 排定的站點串成路徑，依 framesPerSegment 在每兩站之間取樣。本工具不匯出逐格影格與 3D 模型，需分別呼叫 export_walkthrough_frames 與 export_model_gltf。回傳的 packageDir 供後續兩個工具使用。",
        inputSchema: {
            type: "object",
            properties: {
                planViewName: {
                    type: "string",
                    description: "作為交付包平面圖底圖的平面視圖名稱（必填）",
                },
                sectionViewName: {
                    type: "string",
                    description: "作為交付包剖面圖底圖的剖面視圖名稱（選填，未提供則不含剖面資料）",
                },
                stationPrefix: {
                    type: "string",
                    default: "WALK-",
                    description: "站點視圖名稱前綴，同 get_camera_stations",
                },
                framesPerSegment: {
                    type: "integer",
                    default: 24,
                    minimum: 2,
                    maximum: 240,
                    description: "每兩站之間的取樣格數（每段皆相同）",
                },
                frameWidth: {
                    type: "integer",
                    default: 1280,
                    minimum: 320,
                    maximum: 3840,
                    description: "匯出影格寬度（像素），高度依站點視圖視角自動換算",
                },
                outputPath: {
                    type: "string",
                    description: "交付包的上層資料夾；未提供時預設專案檔所在資料夾",
                },
                packageName: {
                    type: "string",
                    description: "交付包資料夾名稱；未提供時預設 WalkPackage_yyyyMMdd_HHmm",
                },
            },
            required: ["planViewName"],
        },
    },
    {
        name: "export_walkthrough_frames",
        description:
            "逐格設定相機並匯出交付包的路徑影格 PNG（存至 packageDir/frames/<variant>/）。exportAll=true（預設）時 MCP server 會自動分批呼叫直到全部完成；若中斷可用 startIndex 從中斷處續跑。Revit 在匯出期間會暫時無回應，屬預期行為。呼叫前需先用 export_walkthrough_package 建立 packageDir。",
        inputSchema: {
            type: "object",
            properties: {
                packageDir: {
                    type: "string",
                    description: "交付包資料夾完整路徑（export_walkthrough_package 回傳的 packageDir）",
                },
                variant: {
                    type: "string",
                    default: "base",
                    pattern: "^[a-zA-Z0-9_-]+$",
                    description: "影格變體 ID（用於 frames/<variant> 子資料夾與 manifest.variants）",
                },
                startIndex: {
                    type: "integer",
                    default: 0,
                    minimum: 0,
                    description: "起始影格索引；續跑中斷的匯出時填上次的 nextIndex",
                },
                count: {
                    type: "integer",
                    default: 24,
                    minimum: 1,
                    maximum: 120,
                    description: "本批要匯出的影格數",
                },
                exportAll: {
                    type: "boolean",
                    default: true,
                    description: "true（預設）：MCP server 自動分批呼叫直到匯出完成；false：只執行這一批",
                },
            },
            required: ["packageDir"],
        },
    },
    {
        name: "apply_camera_stations",
        description:
            "把使用者在離線網頁「站點編輯模式」調整過的相機站點（眼點/朝向/上方向）寫回 Revit 的 WALK-NN 透視視圖。來源是網頁下載的 stations.json（每筆 {name, eye, fwd, up}），不是手動編的資料。預設 mode=\"preview\"：只計算並回報每站的位移量（moveMeters/heightDeltaMeters/headingDeltaDeg），不改動 Revit 模型；確認位移合理後再以 mode=\"apply\" 呼叫一次才會真的改視圖方位。寫回後，交付包裡已匯出的影格與路徑資料是舊的（相機已變但影格沒重繪），必須重新執行 export_walkthrough_package 再重跑 export_walkthrough_frames，否則網頁畫面與新相機不符。",
        inputSchema: {
            type: "object",
            properties: {
                stations: {
                    type: "array",
                    description: "站點陣列（網頁下載的 stations.json 內容），每筆對應一個 WALK-NN 視圖",
                    items: {
                        type: "object",
                        properties: {
                            name: {
                                type: "string",
                                description: "站點視圖名稱（需與 Revit 裡的 3D 視圖名稱相符，例如 WALK-03）",
                            },
                            eye: {
                                type: "array",
                                items: { type: "number" },
                                minItems: 3,
                                maxItems: 3,
                                description: "相機眼點座標 [x, y, z]，單位依 coordinates 參數而定",
                            },
                            fwd: {
                                type: "array",
                                items: { type: "number" },
                                minItems: 3,
                                maxItems: 3,
                                description: "相機前方向向量 [x, y, z]",
                            },
                            up: {
                                type: "array",
                                items: { type: "number" },
                                minItems: 3,
                                maxItems: 3,
                                description: "相機上方向向量 [x, y, z]",
                            },
                        },
                        required: ["name", "eye", "fwd", "up"],
                    },
                },
                packageDir: {
                    type: "string",
                    description: "交付包資料夾完整路徑；提供時可用交付包座標（stations.json 通常就是這個座標系），並會在寫回後把交付包內的影格標記為過期",
                },
                coordinates: {
                    type: "string",
                    enum: ["package", "revit"],
                    description: "站點座標系：\"package\"＝交付包模型座標（已扣除 originOffset，需搭配 packageDir 才能還原）；\"revit\"＝Revit 專案座標（公尺，未扣 offset）。未指定時，提供 packageDir 則預設 \"package\"，否則預設 \"revit\"",
                },
                mode: {
                    type: "string",
                    enum: ["preview", "apply"],
                    default: "preview",
                    description: "\"preview\"（預設）：只回報每站會移動多少，不改模型；\"apply\"：實際把相機寫回 Revit 視圖方位",
                },
                createMissing: {
                    type: "boolean",
                    default: false,
                    description: "stations 裡的名稱在 Revit 裡找不到對應視圖時，是否複製既有站點視圖自動建立新站",
                },
                unlockLockedViews: {
                    type: "boolean",
                    default: false,
                    description: "遇到已鎖定方位的視圖時，是否強制解鎖並覆寫（預設為 false，遇鎖定視圖僅略過並在 warnings 提示）",
                },
            },
            required: ["stations"],
        },
    },
    {
        name: "export_model_gltf",
        description:
            "匯出目前可見模型為 glTF binary（.glb，只含材質顏色，不含貼圖）存至 packageDir，並可選擇同時嵌入 data.js（embedInDataJs=true，預設）讓離線網頁不需額外載入 model.glb 即可顯示 3D 模型。預設 bounds=\"path\" 會依交付包的相機路徑自動算出剖面框只匯出走動看得到的範圍，這是大模型撞三角面上限時的首選解法——用空間裁切換取「類別完整」，不要改用只含部分類別的來源視圖（那會讓 3D 視窗失去門窗與家具，看不出室內外）。",
        inputSchema: {
            type: "object",
            properties: {
                packageDir: {
                    type: "string",
                    description: "交付包資料夾完整路徑（export_walkthrough_package 回傳的 packageDir）",
                },
                sourceViewName: {
                    type: "string",
                    description: "決定匯出模型可見性設定的來源視圖名稱；未提供時使用第一站視圖的可見性設定",
                },
                bounds: {
                    type: "string",
                    enum: ["path", "none"],
                    default: "path",
                    description: "\"path\"（預設）：依 manifest 的相機路徑自動算出剖面框套到匯出用暫存視圖，只匯出走動看得到的範圍——門窗、帷幕牆、家具等類別全部保留，不必為了三角面上限犧牲類別；\"none\"：完全沿用來源視圖的範圍（大模型容易撞三角面上限被截斷）",
                },
                marginMeters: {
                    type: "number",
                    default: 25,
                    minimum: 0,
                    description: "bounds=\"path\" 時路徑水平外擴距離（公尺），預設 25 m（與深度圖預設遠平面一致）；模型仍太大就調小",
                },
                belowMeters: {
                    type: "number",
                    default: 2.5,
                    minimum: 0,
                    description: "bounds=\"path\" 時相機高度往下延伸的距離（公尺），要涵蓋腳下樓板與其構造厚度",
                },
                aboveMeters: {
                    type: "number",
                    default: 8,
                    minimum: 0,
                    description: "bounds=\"path\" 時相機高度往上延伸的距離（公尺），要涵蓋天花板與上一層樓板；挑空空間可調大",
                },
                embedInDataJs: {
                    type: "boolean",
                    default: true,
                    description: "是否將 glb 內容以 base64 嵌入 data.js（window.WALK_DATA.glbBase64），讓網頁離線載入不需額外抓檔",
                },
            },
            required: ["packageDir"],
        },
    },
    {
        name: "export_walkthrough_depth",
        description:
            "呼叫本機 Node.js＋headless Chrome，從交付包的 model.glb 與相機路徑離線渲染逐格深度圖（存至 packageDir/depth/），供 AI 影片風格化工具鎖住鏡頭運動。相機路徑或模型變更後（重新執行 export_walkthrough_package 或 export_model_gltf）必須重跑本工具，否則深度圖會與新路徑不符；本工具純外部程序，不會修改 Revit 模型或既有的 frames/ 影格。執行環境需要本機安裝 Node.js 與 Chrome（找不到會在錯誤訊息附安裝指引）；依影格數與解析度，執行可能需數分鐘到數十分鐘。",
        inputSchema: {
            type: "object",
            properties: {
                packageDir: {
                    type: "string",
                    description: "交付包資料夾完整路徑（export_walkthrough_package 回傳的 packageDir，需已用 export_model_gltf 匯出 model.glb）",
                },
                width: {
                    type: "integer",
                    minimum: 64,
                    maximum: 3840,
                    description: "深度圖輸出寬度（像素）；未提供時由腳本決定預設值",
                },
                start: {
                    type: "integer",
                    minimum: 0,
                    description: "起始影格索引；未提供時由腳本決定（通常為 0，或接續上次未完成的進度）",
                },
                count: {
                    type: "integer",
                    minimum: 1,
                    description: "本次要渲染的影格數；未提供時由腳本渲染全部剩餘影格",
                },
                encoding: {
                    type: "string",
                    enum: ["gray8", "gray16"],
                    description: "深度圖編碼位元深度；未提供時使用腳本預設值",
                },
                alsoNormals: {
                    type: "boolean",
                    default: false,
                    description: "是否同時輸出法線圖",
                },
                overwrite: {
                    type: "boolean",
                    default: false,
                    description: "是否覆蓋已存在的深度圖檔案；預設略過已渲染過的影格（可用於中斷續跑）",
                },
                chromePath: {
                    type: "string",
                    description: "自訂 Chrome/Chromium 執行檔路徑；未提供時由腳本自動偵測本機安裝的 Chrome",
                },
            },
            required: ["packageDir"],
        },
    },
];
