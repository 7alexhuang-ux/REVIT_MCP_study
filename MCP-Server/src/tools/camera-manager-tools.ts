import { Tool } from "@modelcontextprotocol/sdk/types.js";

/**
 * 相機管理（Camera Manager）MCP 測試工具（2026-09-25 Wave 1）。
 *
 * 這些工具呼叫的是 Revit 相機管理停靠面板背後「同一套」服務程式碼
 * （CameraManagerService / CameraKeyPlanService），用途是讓 AI 自動測試面板的行為，
 * 不必每次都靠人工點面板。C# 實作：MCP/Core/Commands/CommandExecutor.CameraManager.cs。
 * 規格：docs/camera-manager/spec.zh-TW.md。
 */
export const cameraManagerTools: Tool[] = [
    {
        name: "list_cameras",
        title: "列出透視相機（相機管理）",
        description: "列出專案中所有透視相機，逐台回報相機管理面板看到的同一份資料：所在建築樓層、離樓板高度（cm 與 mm）、水平角（0°=+Y 順時針）、俯仰角、視野角（左／右／下／上邊界與水平／垂直總角）、遠裁剪、室內／室外與眼睛所在房間、相機放置時記錄的平面、③ 位置圖底圖挑選器會選哪張平面與步驟、已連結的 Key Plan 平面／剖面／等角視圖 id、剖面方向設定、剖面是否被手動移過。唯讀。觸發條件：相機清單、相機管理、Key Plan、人視高檢查、camera list。",
        inputSchema: {
            type: "object",
            properties: {
                nameContains: {
                    type: "string",
                    description: "只列名稱包含此字串的相機（不分大小寫；省略＝全部）"
                }
            },
            required: []
        }
    },
    {
        name: "run_camera_keyplan",
        title: "執行相機一鍵 4 視圖（Key Plan）",
        description: "對一台透視相機執行相機管理面板的「一鍵 4 視圖」核心（建立或更新 Key Plan 平面／剖面／等角三張視圖＋相機標記），與面板同一段程式碼，不經面板 UI。回傳三張視圖的 id、名稱、比例、範圍（平面裁剪框、剖面裁剪框、等角 Section Box，mm）、各視圖內本相機標記元素數（依角色分組），以及關鍵判定（室內／室外、使用的房間、本層頂、扇形半徑、實際剖面方向、等角範圍模式）。寫入模型（一個交易「建立相機 Key Plan」），不刪除任何使用者元素。觸發條件：一鍵 4 視圖、Key Plan、相機平面剖面等角、run keyplan。",
        inputSchema: {
            type: "object",
            properties: {
                cameraId: {
                    type: "number",
                    description: "相機 3D 視圖（或平面上的相機元件）的 ElementId；省略＝目前作用中的透視圖"
                },
                tile: {
                    type: "boolean",
                    description: "true＝完成後依序開啟透視／平面／剖面／等角並並排（同面板）；預設 false（不動視窗）"
                },
                sectionMode: {
                    type: "string",
                    enum: ["room", "sight"],
                    description: "只影響這次執行的剖面方向：room＝對齊房間長邊、sight＝沿視線；省略＝沿用面板設定"
                },
                isoMode: {
                    type: "string",
                    enum: ["full", "current"],
                    description: "只影響這次執行的等角範圍：full＝整層（見上層樑）、current＝只到當層；省略＝沿用面板設定"
                },
                resetSection: {
                    type: "boolean",
                    description: "true＝即使剖面被手動移過也強制重算覆蓋（同面板「重設剖面位置」）；預設 false"
                }
            },
            required: []
        }
    },
    {
        name: "set_camera_pose",
        title: "修改相機姿態（相機管理）",
        description: "修改透視相機的姿態，與相機管理面板清單就地編輯走同一條程式路徑（一個交易「編輯相機」）：可給離樓板高度 cm、水平角、俯仰角、平面 X/Y（cm，內部原點座標）、遠裁剪 cm，至少一項；未給的欄位維持原值。回傳修改前後的值。不會自動重算 Key Plan 視圖。觸發條件：調相機高度、改相機角度、移動相機、set camera pose。",
        inputSchema: {
            type: "object",
            properties: {
                cameraId: {
                    type: "number",
                    description: "相機 3D 視圖（或相機元件）的 ElementId；省略＝目前作用中的透視圖"
                },
                heightAboveLevelCm: { type: "number", description: "離下方建築樓層高度（cm）" },
                yawDeg: { type: "number", description: "水平角（度，0°=+Y 專案北、順時針為正）" },
                pitchDeg: { type: "number", description: "俯仰角（度，0°=水平、正值往上看）" },
                planXcm: { type: "number", description: "眼睛平面 X（cm，內部原點座標系）" },
                planYcm: { type: "number", description: "眼睛平面 Y（cm，內部原點座標系）" },
                farClipCm: { type: "number", description: "遠裁剪距離（cm）；該相機遠裁剪不可寫時會回錯誤" }
            },
            required: []
        }
    },
    {
        name: "copy_camera_to_level",
        title: "複製相機到其他樓層",
        description: "把一台透視相機複製到另一個樓層（同平面位置、同離樓板高度、同方向與視野），與相機管理面板「複製到其他樓層」／清單改樓層同一段程式碼；新名稱「原名 - 樓層名」，重名自動加序號。原相機與其 Key Plan 視圖不變。回傳新相機 id 與名稱。觸發條件：複製相機、相機複製到樓層、copy camera。",
        inputSchema: {
            type: "object",
            properties: {
                cameraId: {
                    type: "number",
                    description: "來源相機 3D 視圖（或相機元件）的 ElementId；省略＝目前作用中的透視圖"
                },
                levelId: { type: "number", description: "目標樓層 ElementId（與 levelName 擇一）" },
                levelName: { type: "string", description: "目標樓層名稱（完全相符、不分大小寫；與 levelId 擇一）" }
            },
            required: []
        }
    },
    {
        name: "get_view_extents",
        title: "讀取視圖範圍與表現設定",
        description: "讀取任一視圖的範圍與表現設定：視圖類型、比例、裁剪框（啟用／可見、Min/Max 與 Transform，mm）、註解裁剪與四邊外擴、3D 視圖的 Section Box／眼睛／方向／透視遠裁剪、剖面遠裁剪、Display Model、詳細等級、視覺型式、視圖樣板、被隱藏的類別（名稱與數量）、各連結模型在此視圖的顯示設定，以及此視圖專屬的詳圖線／填滿區域／文字註記數量。唯讀。觸發條件：視圖範圍、裁剪框、Section Box、視圖為什麼看不到、view extents。",
        inputSchema: {
            type: "object",
            properties: {
                viewId: {
                    type: "number",
                    description: "視圖 ElementId（省略＝目前作用中視圖）"
                }
            },
            required: []
        }
    },
    {
        name: "run_camera_overview",
        title: "建立／更新樓層相機總覽平面",
        description: "建立或更新相機管理的每層總覽平面「Key Plan-總覽-<樓層名>」（與面板「一鍵 4 視圖 ▾ → 更新樓層總覽」同一段程式碼）：複製該樓層由底圖挑選器選出的平面、套淡化表現法，裁剪＝該層室內相機視野扇形聯集 +3 m（有室外相機時再聯集底圖的建築範圍），把該層每台相機的 B 型標記（眼睛圓圈、視線、扇形、眼睛楔形）與名稱文字畫在同一張平面上；重跑更新同一張、不重複。回傳每層的視圖 id、名稱、相機數與名稱、底圖來源、裁剪範圍（mm）與比例。寫入模型（一個交易「更新樓層總覽」），不刪除任何使用者元素。觸發條件：樓層總覽、相機總覽平面、所有相機畫在一張平面、camera overview。",
        inputSchema: {
            type: "object",
            properties: {
                levelId: { type: "number", description: "樓層 ElementId（與 levelName 擇一）" },
                levelName: { type: "string", description: "樓層名稱（完全相符、不分大小寫；與 levelId 擇一）" },
                all: {
                    type: "boolean",
                    description: "true＝所有有相機的建築樓層各建立／更新一張（忽略 levelId／levelName）；預設 false"
                }
            },
            required: []
        }
    },
    {
        name: "create_camera_sheet",
        title: "相機一鍵出圖紙",
        description: "為一台透視相機建立或更新圖紙「Key Plan-<相機名>」（與相機管理面板「一鍵出圖紙」同一段程式碼）：左側約 60% 放透視圖，右欄由上而下放 Key Plan 平面／剖面／等角；可繪區＝圖框在圖紙上的外接框內縮邊界。只調整視埠位置（置中於格子），不改任何視圖比例；視埠類型沿用專案既有、會顯示標題的類型，不新建。相機還沒有 Key Plan 視圖時先執行一鍵 4 視圖。已在本相機圖紙上的視埠保留原位；在其他圖紙上的視圖回報並略過。回傳圖紙 id／編號／名稱、圖框來源、可繪區與每個視埠的 id、狀態、中心與尺寸（圖紙座標 mm）、是否超出格子。寫入模型（交易「建立相機圖紙」），不刪除任何使用者元素。觸發條件：相機出圖、一鍵出圖紙、相機圖紙、camera sheet。",
        inputSchema: {
            type: "object",
            properties: {
                cameraId: {
                    type: "number",
                    description: "相機 3D 視圖（或平面上的相機元件）的 ElementId；省略＝目前作用中的透視圖"
                },
                titleBlockTypeId: {
                    type: "number",
                    description: "圖框類型（FamilySymbol）的 ElementId；省略＝面板設定，未設定時用專案內最常用的圖框"
                }
            },
            required: []
        }
    },
    {
        name: "camera_version",
        title: "相機視角版本（列出／儲存／還原／刪除）",
        description: "管理相機的具名「視角版本」（與相機管理面板右鍵「視角版本 ▸」同一段程式碼）。版本存在相機視圖的 Extensible Storage（JSON：眼睛位置、前方向、上方向、裁剪框與其 Transform、遠裁剪），同事開同一個 .rvt 也能切換。action=list 唯讀列出；save 以 name 儲存目前視角（同名覆蓋）；restore 以一個交易「還原相機視角：<名稱>」套回該版本（面板已載入時相機標記會自動更新）；delete 只移除該筆版本資料，不刪除任何模型元素。回傳目前相機姿態與全部版本（眼睛 mm、水平角、俯仰角、裁剪框尺寸、遠裁剪）。觸發條件：相機版本、儲存視角、還原視角、A 案 B 案視角、camera version。",
        inputSchema: {
            type: "object",
            properties: {
                cameraId: {
                    type: "number",
                    description: "相機 3D 視圖（或相機元件）的 ElementId；省略＝目前作用中的透視圖"
                },
                action: {
                    type: "string",
                    enum: ["list", "save", "restore", "delete"],
                    description: "list（預設，唯讀）／save／restore／delete"
                },
                name: {
                    type: "string",
                    description: "版本名稱（save／restore／delete 必填；最多 60 字，區分大小寫）"
                }
            },
            required: []
        }
    }
];
