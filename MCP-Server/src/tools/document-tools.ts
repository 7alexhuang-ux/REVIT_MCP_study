import { Tool } from "@modelcontextprotocol/sdk/types.js";

/** 文件生命週期與連結管理；所有 Revit API 均由 ExternalEvent 執行。 */
export const documentTools: Tool[] = [
    {
        name: "list_open_documents",
        description: "列出目前開啟的 Revit 文件及作用中文件、修改狀態；不開檔、不儲存。",
        inputSchema: { type: "object", properties: {} },
    },
    {
        name: "open_document",
        description: "開啟本機 RVT。預設 detach=true，保留工作集並啟用文件；不儲存原檔。工作共享中央檔（IsCentral）搭配 detach=false 一律拒絕；detach=false 不會自動退回分離模式，失敗會明確回報。",
        inputSchema: {
            type: "object",
            properties: {
                path: { type: "string", description: "現有 RVT 的絕對本機路徑" },
                detach: { type: "boolean", default: true },
                preserveWorksets: { type: "boolean", default: true },
                activate: { type: "boolean", default: true },
                audit: { type: "boolean", default: false },
            },
            required: ["path"],
        },
    },
    {
        name: "close_document",
        description: "依唯一 title 關閉文件。discard 預設 false：已修改文件會拒絕關閉，不會無提示儲存或丟棄。discard=true 明確允許捨棄；作用中文件受 Revit API 限制。",
        inputSchema: {
            type: "object",
            properties: {
                title: { type: "string" },
                discard: { type: "boolean", default: false },
            },
            required: ["title"],
        },
    },
    {
        name: "list_links",
        description: "列出作用中文件的 Revit 連結類型、外部路徑及 Loaded／Unloaded／NotFound 等原始載入狀態、referenceType（Overlay／Attachment）與 instanceCount。",
        inputSchema: { type: "object", properties: {} },
    },
    {
        name: "reload_links",
        description: "重新載入既有 Revit 連結。names 省略或空陣列＝全部頂層連結，此時只重載未載入／找不到者（已載入者略過）；明確列名者一律重載。pathOverrides 只接受既有連結名稱到現有絕對 RVT 路徑的對照，不猜路徑、不下載。逐項回報結果，不儲存主文件。",
        inputSchema: {
            type: "object",
            properties: {
                names: { type: "array", items: { type: "string" }, uniqueItems: true },
                pathOverrides: { type: "object", additionalProperties: { type: "string" } },
            },
        },
    },
    {
        name: "save_document",
        description: "儲存作用中的 Revit 文件，或依文件標題儲存已開啟的指定文件。此工具不支援另存新檔，唯讀文件無法儲存。（upstream 收編；與 close_document 的 discard 保護互補：關閉前如需保留變更，先用此工具存檔。）",
        inputSchema: {
            type: "object",
            properties: {
                documentTitle: {
                    type: "string",
                    description: "已開啟文件的標題；省略時儲存作用文件。",
                },
            },
        },
    },
];
