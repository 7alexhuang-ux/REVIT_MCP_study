# Gemini Context & Project Map

此檔案旨在協助 Gemini/AI 快速理解專案結構與資源位置。

## 📁 專案結構地圖

| 路徑 | 說明 | 關鍵檔案 |
| :--- | :--- | :--- |
| **`MCP/`** | **C# Revit Add-in** 核心代碼 | `CommandExecutor.cs` (核心邏輯)<br>`RevitMCP.2024.csproj` |
| **`MCP-Server/`** | **Node.js MCP Server** 與工具腳本 | `src/tools/revit-tools.ts` (工具定義)<br>`index.ts` (伺服器入口)<br>`*.js` (執行腳本) |
| **`domain/`** | **業務流程與核心知識** (優先閱讀) | `element-coloring-workflow.md` (上色流程)<br>`room-boundary.md` |
| **`docs/tools/`** | **技術規格與 API 文檔** | `override_element_color_design.md`<br>`override_graphics_examples.md` |
| **`scripts/`** | **輔助腳本** | `install-addon.ps1` (安裝腳本) |

## 🚀 常用任務索引

### 1. 元素上色與視覺化
*   **流程文件**：`domain/element-coloring-workflow.md`
*   **執行腳本**：
    *   清除顏色：`node MCP-Server/clear_walls.js`
    *   取消接合：`node MCP-Server/step_unjoin.js`
    *   上色：`node MCP-Server/fire_rating_full.js`
    *   恢復接合：`node MCP-Server/step_rejoin.js`

### 2. 房間邊界處理
*   **流程文件**：`domain/room-boundary.md`

### 3. 建置與部署
*   **C# 建置**：`dotnet build -c Release MCP/RevitMCP.2024.csproj`
*   **部署 DLL**：使用 `scripts/install-addon.ps1` 或手動複製到 `C:\ProgramData\Autodesk\Revit\Addins\2024\RevitMCP\`

## ⚠️ 開發注意事項

1.  **修改 C# 後**：必須關閉 Revit -> 編譯 -> 部署 -> 開啟 Revit。
2.  **腳本路徑**：所有 Node.js 腳本預設在 `MCP-Server/` 目錄下執行。
3.  **依賴關係**：`MCP-Server` 透過 WebSocket (Port 8765) 與 Revit Add-in 通訊。
