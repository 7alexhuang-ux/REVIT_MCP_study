import { Tool } from "@modelcontextprotocol/sdk/types.js";

/**
 * Revit 跳轉（docs/REVIT_LINK.md、ADR D-015）：Revit → Issue Manager 的兩個反向入口，與功能區按鈕共用同一段程式（OpenIssueDraft）。
 * 唯讀：只讀 Revit 目前選取與視圖截圖，然後用預設瀏覽器開 Issue Manager 網頁；不修改 Revit 模型、不建立視圖。
 * 網頁要靠 Revit 外掛已配對的 WebSocket 領草稿，所以需先在 Revit 啟動 MCP 服務並完成「網頁配對」。
 * 回傳的 url 不含一次性代碼（代碼只交給瀏覽器）。
 */
export const revitLinkTools: Tool[] = [
    {
        name: "open_issue_draft",
        description:
            "把 Revit 目前選取的元素與目前視圖截圖，帶到 Issue Manager 的「建立議題」預填頁（用預設瀏覽器開啟；使用者在網頁按送出才會真的建立議題，關掉頁面就全部丟棄）。需要 Revit 已開專案且至少選了一個元素；需要使用者已在 Revit 啟動 MCP 服務並完成網頁配對。唯讀，不修改 Revit 模型。觸發條件：使用者說「幫我建立議題」「把這個元件開成議題」「這個牆有問題，開個議題」「在 IM 建立議題」。回傳 { opened, kind, url, elementCount, pairedWebDevices }；pairedWebDevices 為 0 代表還沒有網頁配對，網頁會領不到草稿。",
        inputSchema: {
            type: "object",
            properties: {},
        },
    },
    {
        name: "open_element_issues",
        description:
            "查 Issue Manager 上與 Revit 目前選取元素相關的議題（用預設瀏覽器開啟「這個元素有哪些議題」頁）。需要 Revit 已開專案且至少選了一個元素；需要使用者已在 Revit 啟動 MCP 服務並完成網頁配對。唯讀，不修改 Revit 模型。觸發條件：使用者說「這個元素有哪些議題」「W-12 牆有沒有人開過議題」「看一下這個元件的議題」。回傳 { opened, kind, url, elementCount, pairedWebDevices }。",
        inputSchema: {
            type: "object",
            properties: {},
        },
    },
];
