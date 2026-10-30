import test from "node:test";
import assert from "node:assert/strict";
import { windowFamilyTools } from "./window-family-tools.js";
import { registerRevitTools } from "./revit-tools.js";

function getTool() {
    const tool = windowFamilyTools.find(item => item.name === "generate_window_family");
    assert.ok(tool, "generate_window_family 應存在於 windowFamilyTools");
    return tool!;
}

test("generate_window_family 頂層必填欄位與描述", () => {
    const tool = getTool();
    assert.deepEqual(tool.inputSchema.required, ["familyName", "typeName", "generation", "typeParams", "instanceParams"]);
    assert.match(String(tool.description), /cm/);
    assert.match(String(tool.description), /生成時決定死/);
});

test("generation 規格：拓樸邊界與扇物件 schema", () => {
    const tool = getTool();
    const properties = tool.inputSchema.properties as Record<string, any>;
    const generation = properties.generation;
    assert.deepEqual(generation.required, ["rows", "cols", "cells"]);
    assert.equal(generation.properties.rows.minimum, 1);
    assert.equal(generation.properties.rows.maximum, 3);
    assert.equal(generation.properties.cols.minimum, 1);
    assert.equal(generation.properties.cols.maximum, 4);

    const cellSchema = generation.properties.cells.items.items;
    assert.deepEqual(cellSchema.required, ["id", "sashes"]);
    const sashSchema = cellSchema.properties.sashes.items;
    assert.deepEqual(sashSchema.required, ["type"]);
    assert.deepEqual(sashSchema.properties.type.enum, [
        "fixed", "casement", "sliding", "awning", "hopper", "sliding_v", "screen",
    ]);
    assert.deepEqual(sashSchema.properties.hinge.enum, ["L", "R"]);
});

test("v0.2 窗型擴充：swing 列舉、track 改整數軌序並註明相容舊字串", () => {
    const tool = getTool();
    const properties = tool.inputSchema.properties as Record<string, any>;
    const sashSchema = properties.generation.properties.cells.items.items.properties.sashes.items;

    assert.deepEqual(sashSchema.properties.swing.enum, ["out", "in"]);
    assert.match(String(sashSchema.properties.swing.description), /hopper/);

    const track = sashSchema.properties.track;
    assert.equal(track.type, "integer");
    assert.equal(track.minimum, 0);
    assert.match(String(track.description), /outer/);
    assert.match(String(track.description), /inner/);

    assert.match(String(tool.description), /sliding_v/);
    assert.match(String(tool.description), /screen/);
    assert.match(String(tool.description), /不能單獨成格/);
});

test("typeParams 預設值符合 SPEC §5 表列值", () => {
    const tool = getTool();
    const properties = tool.inputSchema.properties as Record<string, any>;
    const typeParams = properties.typeParams.properties;
    assert.equal(typeParams.frameOuter.default, 4.5);
    assert.equal(typeParams.mullion.default, 4.4);
    assert.equal(typeParams.sash.default, 5);
    // ADR D-010：框深預設 8 → 7（YKK AP YRB-A70）
    assert.equal(typeParams.frameDepth.default, 7);
    assert.equal(typeParams.glass_mm.default, 8);
    // 交疊量無固定 default 值，依 ADR D-005 說明「未提供時預設＝扇框寬」
    assert.equal(typeParams.meetingRailOverlap_mm.default, undefined);
    assert.match(String(typeParams.meetingRailOverlap_mm.description), /扇框寬/);
});

test("instanceParams 必填 width/height，mo 與 sillHeight 預設值正確", () => {
    const tool = getTool();
    const properties = tool.inputSchema.properties as Record<string, any>;
    const instanceParams = properties.instanceParams;
    assert.deepEqual(instanceParams.required, ["width", "height"]);
    assert.equal(instanceParams.properties.sillHeight.default, 90);

    const mo = instanceParams.properties.mo.properties;
    assert.equal(mo.gapPerSide.default, 2.5);
    assert.equal(mo.cutBeyondExterior.default, 5);
    assert.equal(mo.cutBeyondInterior.default, 5);
});

test("symbols、outputDir、loadIntoProject、overwrite 選填欄位存在", () => {
    const tool = getTool();
    const properties = tool.inputSchema.properties as Record<string, any>;
    assert.equal(properties.symbols.properties.apexAtHinge.default, true);
    assert.equal(properties.symbols.properties.planSwingAngle.default, 30);
    assert.equal(properties.outputDir.type, "string");
    assert.equal(properties.loadIntoProject.default, true);
    assert.equal(properties.overwrite.default, false);
});

test("generate_window_family 只註冊於 full 與 architect Profile", () => {
    const originalProfile = process.env.MCP_PROFILE;
    try {
        for (const profile of ["full", "architect"]) {
            process.env.MCP_PROFILE = profile;
            const registered = registerRevitTools();
            assert.ok(registered.some(tool => tool.name === "generate_window_family"), profile);
        }

        for (const profile of ["mep", "structural", "fire-safety"]) {
            process.env.MCP_PROFILE = profile;
            const registered = registerRevitTools();
            assert.ok(!registered.some(tool => tool.name === "generate_window_family"), profile);
        }
    } finally {
        if (originalProfile === undefined) {
            delete process.env.MCP_PROFILE;
        } else {
            process.env.MCP_PROFILE = originalProfile;
        }
    }
});

test("generate_window_family 名稱不重複註冊", () => {
    const originalProfile = process.env.MCP_PROFILE;
    try {
        process.env.MCP_PROFILE = "full";
        const registered = registerRevitTools();
        const matches = registered.filter(tool => tool.name === "generate_window_family");
        assert.equal(matches.length, 1);
    } finally {
        if (originalProfile === undefined) {
            delete process.env.MCP_PROFILE;
        } else {
            process.env.MCP_PROFILE = originalProfile;
        }
    }
});
