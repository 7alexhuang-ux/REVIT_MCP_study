/**
 * 工具註冊中心 — 根據 MCP_PROFILE 篩選載入的工具模組
 *
 * Profile 設定方式（AI Client config）：
 *   "env": { "MCP_PROFILE": "architect" }
 *
 * 可用 Profile：full（預設）, architect, mep, structural, fire-safety
 */

import { Tool } from "@modelcontextprotocol/sdk/types.js";
import { documentTools } from "./document-tools.js";
import { baseTools } from "./base-tools.js";
import { wallTools } from "./wall-tools.js";
import { roomTools } from "./room-tools.js";
import { corridorAnalysisTools } from "./corridor-analysis-tools.js";
import { visualizationTools } from "./visualization-tools.js";
import { scheduleTools } from "./schedule-tools.js";
import { mepTools } from "./mep-tools.js";

import { curtainWallTools } from "./curtain-wall-tools.js";
import { smokeExhaustTools } from "./smoke-exhaust-tools.js";
import { STAIR_COMPLIANCE_TOOLS } from "./stair-compliance-tools.js";
import { sheetTools } from "./sheet-tools.js";
import { detailComponentTools } from "./detail-component-tools.js";
import { dimensionTools } from "./dimension-tools.js";
import { dependentViewTools } from "./dependent-view-tools.js";
import { clashTools } from "./clash-tools.js";
import { doorWindowLegendTools } from "./door-window-legend-tools.js";
import { listSeedsTools } from "./list-seeds-tools.js";
import { dwgColumnTools } from "./dwg-column-tools.js";
import { walkthroughTools } from "./walkthrough-tools.js";
import { webCompareTools } from "./web-compare-tools.js";
import { cameraTools } from "./camera-tools.js";
import { cameraManagerTools } from "./camera-manager-tools.js";

/**
 * Profile 對照表：每個 profile 包含哪些模組
 */
const PROFILE_MODULES: Record<string, Tool[][]> = {
    full: [documentTools, baseTools, wallTools, roomTools, corridorAnalysisTools, visualizationTools, scheduleTools, mepTools, curtainWallTools, smokeExhaustTools, STAIR_COMPLIANCE_TOOLS, sheetTools, detailComponentTools, dimensionTools, dependentViewTools, dwgColumnTools, clashTools, doorWindowLegendTools, listSeedsTools, walkthroughTools, webCompareTools],
    architect: [documentTools, baseTools, wallTools, roomTools, corridorAnalysisTools, visualizationTools, scheduleTools, curtainWallTools, STAIR_COMPLIANCE_TOOLS, sheetTools, detailComponentTools, dimensionTools, dependentViewTools, dwgColumnTools, doorWindowLegendTools, listSeedsTools, walkthroughTools, webCompareTools],
    mep: [documentTools, baseTools, mepTools, scheduleTools, visualizationTools, smokeExhaustTools, clashTools],
    structural: [documentTools, baseTools, wallTools, visualizationTools, dwgColumnTools, clashTools],
    "fire-safety": [documentTools, baseTools, roomTools, corridorAnalysisTools, visualizationTools, smokeExhaustTools],
};

// 相機管理工具（get_camera_info + 8 支相機管理工具）對每個 profile 都補上。
// 用迴圈而不是往五個陣列各貼一次，避免日後新增 profile 時漏掉（漏掉不會報錯，只會安靜地少工具）。
const CAMERA_TOOLS = [cameraTools, cameraManagerTools];
for (const profileName of Object.keys(PROFILE_MODULES)) {
    PROFILE_MODULES[profileName] = [...PROFILE_MODULES[profileName], ...CAMERA_TOOLS];
}

/**
 * 根據 MCP_PROFILE 環境變數註冊工具
 */
export function registerRevitTools(): Tool[] {
    const profile = process.env.MCP_PROFILE || "full";
    const modules = PROFILE_MODULES[profile];

    if (!modules) {
        console.error(`[Tools] Unknown MCP_PROFILE="${profile}", falling back to "full"`);
        return PROFILE_MODULES.full.flat();
    }

    const tools = modules.flat();
    console.error(`[Tools] Profile="${profile}", loaded ${tools.length} tools`);
    return tools;
}
