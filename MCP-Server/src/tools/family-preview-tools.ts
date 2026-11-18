import { Tool } from "@modelcontextprotocol/sdk/types.js";

/** 族群預覽圖匯出：開啟 .rfa 族群文件（不載入目前專案），逐類型匯出平面／立面／3D PNG。 */
export const familyPreviewTools: Tool[] = [
    {
        name: "export_family_previews",
        description:
            "匯出 Revit 族群（.rfa）每個類型的預覽圖：平面、立面、3D（Realistic）各一張 PNG，並在 outputDir 寫 manifest.json。族群以獨立族群文件開啟並於結束後不存檔關閉，不會載入或修改目前專案。單一類型或單一族群失敗只記錄並繼續。適用於在網頁上檢視植栽等族群在 Revit 內的實際樣貌。",
        inputSchema: {
            type: "object",
            properties: {
                familyPaths: {
                    type: "array",
                    items: { type: "string" },
                    description: ".rfa 族群檔的絕對路徑清單",
                },
                outputDir: { type: "string", description: "輸出資料夾（不存在會建立）" },
                pixelSize: { type: "number", default: 800, description: "輸出圖片長邊像素，預設 800" },
                typeFilter: { type: "string", description: "選填；只匯出類型名稱包含此字串的類型" },
            },
            required: ["familyPaths", "outputDir"],
        },
    },
];
