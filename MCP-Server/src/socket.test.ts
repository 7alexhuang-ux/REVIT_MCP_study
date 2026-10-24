import test from "node:test";
import assert from "node:assert/strict";
import { RevitSocketClient } from "./socket.js";

test("一般命令 timeout 預設為 5 分鐘", () => {
    const previous = process.env.REVIT_MCP_COMMAND_TIMEOUT_MS;
    delete process.env.REVIT_MCP_COMMAND_TIMEOUT_MS;
    try {
        const client = new RevitSocketClient();
        assert.equal(client.getCommandTimeout("get_active_schema"), 300000);
    } finally {
        if (previous === undefined) {
            delete process.env.REVIT_MCP_COMMAND_TIMEOUT_MS;
        } else {
            process.env.REVIT_MCP_COMMAND_TIMEOUT_MS = previous;
        }
    }
});

test("整地出圖長任務 timeout 至少 10 分鐘", () => {
    const previous = process.env.REVIT_MCP_COMMAND_TIMEOUT_MS;
    process.env.REVIT_MCP_COMMAND_TIMEOUT_MS = "120000";
    try {
        const client = new RevitSocketClient();
        assert.equal(client.getCommandTimeout("create_grading_comparison_sheet"), 600000);
        assert.equal(client.getCommandTimeout("create_grading_presentation"), 600000);
        assert.equal(client.getCommandTimeout("grade_toposolid_to_floors"), 600000);
    } finally {
        if (previous === undefined) {
            delete process.env.REVIT_MCP_COMMAND_TIMEOUT_MS;
        } else {
            process.env.REVIT_MCP_COMMAND_TIMEOUT_MS = previous;
        }
    }
});

test("走動展示交付包長任務 timeout 至少 10 分鐘", () => {
    const previous = process.env.REVIT_MCP_COMMAND_TIMEOUT_MS;
    process.env.REVIT_MCP_COMMAND_TIMEOUT_MS = "120000";
    try {
        const client = new RevitSocketClient();
        assert.equal(client.getCommandTimeout("export_walkthrough_package"), 600000);
        assert.equal(client.getCommandTimeout("export_walkthrough_frames"), 600000);
        assert.equal(client.getCommandTimeout("export_model_gltf"), 600000);
        assert.equal(client.getCommandTimeout("export_web_compare"), 600000);
        assert.equal(client.getCommandTimeout("get_camera_stations"), 120000);
    } finally {
        if (previous === undefined) {
            delete process.env.REVIT_MCP_COMMAND_TIMEOUT_MS;
        } else {
            process.env.REVIT_MCP_COMMAND_TIMEOUT_MS = previous;
        }
    }
});

test("環境變數可拉高所有命令 timeout", () => {
    const previous = process.env.REVIT_MCP_COMMAND_TIMEOUT_MS;
    process.env.REVIT_MCP_COMMAND_TIMEOUT_MS = "900000";
    try {
        const client = new RevitSocketClient();
        assert.equal(client.getCommandTimeout("get_active_schema"), 900000);
        assert.equal(client.getCommandTimeout("create_grading_comparison_sheet"), 900000);
    } finally {
        if (previous === undefined) {
            delete process.env.REVIT_MCP_COMMAND_TIMEOUT_MS;
        } else {
            process.env.REVIT_MCP_COMMAND_TIMEOUT_MS = previous;
        }
    }
});
