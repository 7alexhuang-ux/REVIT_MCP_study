import test from "node:test";
import assert from "node:assert/strict";
import { gradingTools } from "./grading-tools.js";
import { registerRevitTools } from "./revit-tools.js";

test("整地工具只暴露本次核准模式", () => {
    const tool = gradingTools.find(item => item.name === "grade_toposolid_to_floors");
    assert.ok(tool);
    assert.deepEqual(tool.inputSchema.required, ["toposolidId", "floorIds"]);
    assert.deepEqual((tool.inputSchema.properties?.mode as { enum: string[] }).enum, ["footprint_only"]);
    assert.deepEqual((tool.inputSchema.properties?.targetFace as { enum: string[] }).enum, ["bottom"]);
});

test("整地工具限制整數 ID、非空樓板清單與安全預設值", () => {
    const tool = gradingTools.find(item => item.name === "grade_toposolid_to_floors");
    assert.ok(tool);

    const properties = tool.inputSchema.properties as Record<string, Record<string, unknown>>;
    assert.equal(properties.toposolidId.type, "integer");
    assert.equal(properties.floorIds.type, "array");
    assert.equal(properties.floorIds.minItems, 1);
    assert.deepEqual(properties.floorIds.items, { type: "integer" });
    assert.equal(properties.allowPhaseSetup.default, false);
    assert.match(String(properties.allowPhaseSetup.description), /設為 true 可能調整原地形階段，執行前請先儲存模型/);
    assert.equal(properties.updateExisting.default, false);
    assert.match(String(properties.updateExisting.description), /只接受 false/);
});

test("整地工具只註冊於核准的 Profile", () => {
    const originalProfile = process.env.MCP_PROFILE;

    try {
        for (const profile of ["full", "architect", "structural"]) {
            process.env.MCP_PROFILE = profile;
            assert.ok(registerRevitTools().some(tool => tool.name === "grade_toposolid_to_floors"), profile);
        }

        for (const profile of ["mep", "fire-safety"]) {
            process.env.MCP_PROFILE = profile;
            assert.ok(!registerRevitTools().some(tool => tool.name === "grade_toposolid_to_floors"), profile);
        }
    } finally {
        if (originalProfile === undefined) {
            delete process.env.MCP_PROFILE;
        } else {
            process.env.MCP_PROFILE = originalProfile;
        }
    }
});

test("未知 Profile 回退 full 時仍包含整地工具", () => {
    const originalProfile = process.env.MCP_PROFILE;

    try {
        process.env.MCP_PROFILE = "unknown-profile";
        assert.ok(registerRevitTools().some(tool => tool.name === "grade_toposolid_to_floors"));
    } finally {
        if (originalProfile === undefined) {
            delete process.env.MCP_PROFILE;
        } else {
            process.env.MCP_PROFILE = originalProfile;
        }
    }
});
