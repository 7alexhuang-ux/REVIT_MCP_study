import test from "node:test";
import assert from "node:assert/strict";
import { gradingTools } from "./grading-tools.js";
import { registerRevitTools } from "./revit-tools.js";

test("整地工具暴露三種模式與銜接參數", () => {
    const tool = gradingTools.find(item => item.name === "grade_toposolid_to_floors");
    assert.ok(tool);
    assert.deepEqual(tool.inputSchema.required, ["toposolidId", "floorIds"]);
    const properties = tool.inputSchema.properties as Record<string, Record<string, unknown>>;
    assert.deepEqual((properties.mode as { enum: string[] }).enum,
        ["footprint_only", "offset_transition", "slope_transition"]);
    assert.deepEqual((properties.targetFace as { enum: string[] }).enum, ["bottom"]);
    assert.equal(properties.offsetDistance.type, "number");
    assert.equal(properties.offsetDistance.exclusiveMinimum, 0);
    assert.match(String(properties.offsetDistance.description), /公尺/);
    assert.equal(properties.slopeRatio.type, "string");
    assert.match(String(properties.slopeRatio.description), /1:n/);
    assert.equal(properties.maxExtension.type, "number");
    assert.match(String(properties.maxExtension.description), /20/);
    assert.equal(properties.schemeName.type, "string");
    assert.match(String(properties.schemeName.description), /方案/);
});

test("登記簿工具存在且參數正確", () => {
    const listTool = gradingTools.find(item => item.name === "list_grading_schemes");
    assert.ok(listTool);

    const exportTool = gradingTools.find(item => item.name === "export_grading_comparison");
    assert.ok(exportTool);
    const properties = exportTool.inputSchema.properties as Record<string, Record<string, unknown>>;
    assert.equal(properties.outputPath.type, "string");
});

test("整地工具支援鬆實方係數（成對）", () => {
    const tool = gradingTools.find(item => item.name === "grade_toposolid_to_floors");
    assert.ok(tool);
    const properties = tool.inputSchema.properties as Record<string, Record<string, unknown>>;
    assert.equal(properties.looseFactor.type, "number");
    assert.equal(properties.looseFactor.exclusiveMinimum, 0);
    assert.match(String(properties.looseFactor.description), /成對/);
    assert.equal(properties.compactionFactor.type, "number");
    assert.match(String(properties.compactionFactor.description), /成對/);
});

test("Phase4 表現層工具存在且預設值正確", () => {
    const viewTool = gradingTools.find(item => item.name === "create_grading_scheme_view");
    assert.ok(viewTool);
    assert.deepEqual(viewTool.inputSchema.required, ["designToposolidId"]);
    const viewProps = viewTool.inputSchema.properties as Record<string, Record<string, unknown>>;
    assert.equal(viewProps.exportPng.default, true);

    const heatmapTool = gradingTools.find(item => item.name === "create_cutfill_heatmap");
    assert.ok(heatmapTool);
    const heatmapProps = heatmapTool.inputSchema.properties as Record<string, Record<string, unknown>>;
    assert.equal(heatmapProps.sampleStepMeters.default, 2);

    const annotateTool = gradingTools.find(item => item.name === "annotate_grading_scheme");
    assert.ok(annotateTool);
    const annotateProps = annotateTool.inputSchema.properties as Record<string, Record<string, unknown>>;
    assert.equal(annotateProps.annotateFloors.default, true);
    assert.equal(annotateProps.annotateDaylight.default, false);

    const restoreTool = gradingTools.find(item => item.name === "restore_grading_scheme");
    assert.ok(restoreTool);
    const restoreProps = restoreTool.inputSchema.properties as Record<string, Record<string, unknown>>;
    assert.equal(restoreProps.rerunGrading.default, false);

    const gridTool = gradingTools.find(item => item.name === "export_earthwork_gridsheet");
    assert.ok(gridTool);
    const gridProps = gridTool.inputSchema.properties as Record<string, Record<string, unknown>>;
    assert.equal(gridProps.cellSizeMeters.default, 10);
    assert.match(String(gridTool.description), /方格法/);

    const exportTool = gradingTools.find(item => item.name === "export_grading_comparison");
    assert.ok(exportTool);
    const exportProps = exportTool.inputSchema.properties as Record<string, Record<string, unknown>>;
    assert.equal(exportProps.includeScreenshots.default, true);
});

test("平衡高程反求工具存在且預設值正確", () => {
    const tool = gradingTools.find(item => item.name === "solve_balanced_elevation");
    assert.ok(tool);
    assert.deepEqual(tool.inputSchema.required, ["toposolidId", "floorIds"]);
    const properties = tool.inputSchema.properties as Record<string, Record<string, unknown>>;
    assert.equal(properties.targetNetCubicMeters.default, 0);
    assert.equal(properties.toleranceCubicMeters.default, 10);
    assert.equal(properties.maxAdjustMeters.default, 10);
    assert.equal(properties.maxIterations.default, 10);
    assert.equal(properties.apply.default, false);
    assert.match(String(tool.description), /二分法/);
});

test("整地工具限制整數 ID、非空樓板清單與預設值", () => {
    const tool = gradingTools.find(item => item.name === "grade_toposolid_to_floors");
    assert.ok(tool);

    const properties = tool.inputSchema.properties as Record<string, Record<string, unknown>>;
    assert.equal(properties.toposolidId.type, "integer");
    assert.equal(properties.floorIds.type, "array");
    assert.equal(properties.floorIds.minItems, 1);
    assert.deepEqual(properties.floorIds.items, { type: "integer" });
    // 2026-07-03 產品決策：一般使用者的地形建立於新建階段，工具預設自動設定階段。
    assert.equal(properties.allowPhaseSetup.default, true);
    assert.match(String(properties.allowPhaseSetup.description), /自動設定整地所需階段/);
    assert.equal(properties.updateExisting.default, false);
    assert.match(String(properties.updateExisting.description), /只接受 false/);
});

test("整地工具只註冊於核准的 Profile", () => {
    const originalProfile = process.env.MCP_PROFILE;
    const gradingToolNames = [
        "grade_toposolid_to_floors",
        "list_grading_schemes",
        "export_grading_comparison",
        "solve_balanced_elevation",
        "create_grading_scheme_view",
        "create_cutfill_heatmap",
        "annotate_grading_scheme",
        "restore_grading_scheme",
        "export_earthwork_gridsheet",
    ];

    try {
        for (const profile of ["full", "architect", "structural"]) {
            process.env.MCP_PROFILE = profile;
            const registered = registerRevitTools();
            for (const name of gradingToolNames) {
                assert.ok(registered.some(tool => tool.name === name), `${profile}:${name}`);
            }
        }

        for (const profile of ["mep", "fire-safety"]) {
            process.env.MCP_PROFILE = profile;
            const registered = registerRevitTools();
            for (const name of gradingToolNames) {
                assert.ok(!registered.some(tool => tool.name === name), `${profile}:${name}`);
            }
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
