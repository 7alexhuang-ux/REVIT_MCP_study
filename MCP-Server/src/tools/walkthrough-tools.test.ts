import test from "node:test";
import assert from "node:assert/strict";
import { walkthroughTools } from "./walkthrough-tools.js";
import { registerRevitTools } from "./revit-tools.js";

test("create_camera_stations 必填 stations，座標/視角/覆寫參數有預設值與 enum", () => {
    const tool = walkthroughTools.find(item => item.name === "create_camera_stations");
    assert.ok(tool);
    assert.deepEqual(tool.inputSchema.required, ["stations"]);

    const properties = tool.inputSchema.properties as Record<string, any>;

    assert.equal(properties.stations.type, "array");
    const stationItems = properties.stations.items;
    assert.equal(stationItems.type, "object");
    assert.deepEqual(stationItems.required, ["eye"]);
    assert.equal(stationItems.properties.name.type, "string");
    for (const key of ["eye", "target", "fwd", "up"]) {
        assert.equal(stationItems.properties[key].type, "array");
        assert.equal(stationItems.properties[key].items.type, "number");
        assert.equal(stationItems.properties[key].minItems, 3);
        assert.equal(stationItems.properties[key].maxItems, 3);
    }

    assert.equal(properties.stationPrefix.default, "WALK-");
    assert.equal(properties.startNumber.type, "integer");
    assert.equal(properties.startNumber.default, 1);
    assert.equal(properties.startNumber.minimum, 1);

    assert.deepEqual(properties.coordinates.enum, ["revit", "package"]);
    assert.equal(properties.coordinates.default, "revit");
    assert.equal(properties.packageDir.type, "string");

    assert.equal(properties.fovYDeg.type, "number");
    assert.equal(properties.fovYDeg.default, 50);
    assert.equal(properties.fovYDeg.maximum, 170);

    assert.equal(properties.aspect.type, "number");
    assert.equal(properties.aspect.default, 1.7778);

    assert.equal(properties.copySettingsFromViewName.type, "string");

    assert.equal(properties.overwrite.type, "boolean");
    assert.equal(properties.overwrite.default, false);

    assert.match(String(tool.description), /get_camera_stations/);
});

test("get_camera_stations 存在且參數帶預設前綴", () => {
    const tool = walkthroughTools.find(item => item.name === "get_camera_stations");
    assert.ok(tool);
    const properties = tool.inputSchema.properties as Record<string, Record<string, unknown>>;
    assert.equal(properties.stationPrefix.type, "string");
    assert.equal(properties.stationPrefix.default, "WALK-");
    assert.ok(!tool.inputSchema.required);
});

test("export_walkthrough_package 必填 planViewName 且參數範圍正確", () => {
    const tool = walkthroughTools.find(item => item.name === "export_walkthrough_package");
    assert.ok(tool);
    assert.deepEqual(tool.inputSchema.required, ["planViewName"]);

    const properties = tool.inputSchema.properties as Record<string, Record<string, unknown>>;
    assert.equal(properties.sectionViewName.type, "string");
    assert.equal(properties.stationPrefix.default, "WALK-");

    assert.equal(properties.framesPerSegment.type, "integer");
    assert.equal(properties.framesPerSegment.default, 24);
    assert.equal(properties.framesPerSegment.minimum, 2);
    assert.equal(properties.framesPerSegment.maximum, 240);

    assert.equal(properties.frameWidth.type, "integer");
    assert.equal(properties.frameWidth.default, 1280);
    assert.equal(properties.frameWidth.minimum, 320);
    assert.equal(properties.frameWidth.maximum, 3840);

    assert.equal(properties.outputPath.type, "string");
    assert.equal(properties.packageName.type, "string");
    assert.match(String(properties.packageName.description), /WalkPackage_yyyyMMdd_HHmm/);
});

test("export_walkthrough_frames 必填 packageDir，variant 有 pattern，數值範圍正確", () => {
    const tool = walkthroughTools.find(item => item.name === "export_walkthrough_frames");
    assert.ok(tool);
    assert.deepEqual(tool.inputSchema.required, ["packageDir"]);

    const properties = tool.inputSchema.properties as Record<string, Record<string, unknown>>;
    assert.equal(properties.variant.default, "base");
    assert.equal(properties.variant.pattern, "^[a-zA-Z0-9_-]+$");

    assert.equal(properties.startIndex.type, "integer");
    assert.equal(properties.startIndex.default, 0);

    assert.equal(properties.count.type, "integer");
    assert.equal(properties.count.default, 24);
    assert.equal(properties.count.minimum, 1);
    assert.equal(properties.count.maximum, 120);

    assert.equal(properties.exportAll.type, "boolean");
    assert.equal(properties.exportAll.default, true);
    assert.match(String(tool.description), /startIndex/);
});

test("apply_camera_stations 必填 stations，mode/coordinates 有 enum 與預設值，stations items 結構正確", () => {
    const tool = walkthroughTools.find(item => item.name === "apply_camera_stations");
    assert.ok(tool);
    assert.deepEqual(tool.inputSchema.required, ["stations"]);

    const properties = tool.inputSchema.properties as Record<string, any>;

    assert.equal(properties.stations.type, "array");
    const stationItems = properties.stations.items;
    assert.equal(stationItems.type, "object");
    assert.deepEqual(stationItems.required, ["name", "eye", "fwd", "up"]);
    assert.equal(stationItems.properties.name.type, "string");
    for (const key of ["eye", "fwd", "up"]) {
        assert.equal(stationItems.properties[key].type, "array");
        assert.equal(stationItems.properties[key].items.type, "number");
        assert.equal(stationItems.properties[key].minItems, 3);
        assert.equal(stationItems.properties[key].maxItems, 3);
    }

    assert.equal(properties.packageDir.type, "string");

    assert.deepEqual(properties.coordinates.enum, ["package", "revit"]);
    assert.equal(properties.coordinates.default, undefined);

    assert.deepEqual(properties.mode.enum, ["preview", "apply"]);
    assert.equal(properties.mode.default, "preview");

    assert.equal(properties.createMissing.type, "boolean");
    assert.equal(properties.createMissing.default, false);

    assert.equal(properties.unlockLockedViews.type, "boolean");
    assert.equal(properties.unlockLockedViews.default, false);

    assert.match(String(tool.description), /stations\.json/);
    assert.match(String(tool.description), /preview/);
    assert.match(String(tool.description), /apply/);
});

test("export_model_gltf 必填 packageDir 且 embedInDataJs 預設 true", () => {
    const tool = walkthroughTools.find(item => item.name === "export_model_gltf");
    assert.ok(tool);
    assert.deepEqual(tool.inputSchema.required, ["packageDir"]);

    const properties = tool.inputSchema.properties as Record<string, Record<string, unknown>>;
    assert.equal(properties.sourceViewName.type, "string");
    assert.equal(properties.embedInDataJs.type, "boolean");
    assert.equal(properties.embedInDataJs.default, true);
});

test("export_model_gltf 預設依相機路徑裁切，邊界參數有預設值且不可為負", () => {
    const tool = walkthroughTools.find(item => item.name === "export_model_gltf");
    assert.ok(tool);

    const properties = tool.inputSchema.properties as Record<string, any>;
    // bounds="path" must stay the default: falling back to the source view's full
    // extent is what made a large model overrun the triangle budget and ship a
    // silently truncated glb.
    assert.deepEqual(properties.bounds.enum, ["path", "none"]);
    assert.equal(properties.bounds.default, "path");

    for (const [name, expected] of [["marginMeters", 25], ["belowMeters", 2.5], ["aboveMeters", 8]] as const) {
        assert.equal(properties[name].type, "number", `${name} 應為 number`);
        assert.equal(properties[name].default, expected, `${name} 預設值不符`);
        assert.equal(properties[name].minimum, 0, `${name} 不可為負`);
    }
});

test("export_walkthrough_depth 必填 packageDir，數值範圍與 enum 正確", () => {
    const tool = walkthroughTools.find(item => item.name === "export_walkthrough_depth");
    assert.ok(tool);
    assert.deepEqual(tool.inputSchema.required, ["packageDir"]);

    const properties = tool.inputSchema.properties as Record<string, any>;
    assert.equal(properties.packageDir.type, "string");

    assert.equal(properties.width.type, "integer");
    assert.equal(properties.width.minimum, 64);
    assert.equal(properties.width.maximum, 3840);

    assert.equal(properties.start.type, "integer");
    assert.equal(properties.start.minimum, 0);

    assert.equal(properties.count.type, "integer");
    assert.equal(properties.count.minimum, 1);

    assert.deepEqual(properties.encoding.enum, ["gray8", "gray16"]);

    assert.equal(properties.alsoNormals.type, "boolean");
    assert.equal(properties.alsoNormals.default, false);

    assert.equal(properties.overwrite.type, "boolean");
    assert.equal(properties.overwrite.default, false);

    assert.equal(properties.chromePath.type, "string");

    assert.match(String(tool.description), /Node\.js/);
    assert.match(String(tool.description), /Chrome/);
});

test("走動展示工具只註冊於 full 與 architect profile", () => {
    const originalProfile = process.env.MCP_PROFILE;
    const walkthroughToolNames = [
        "create_camera_stations",
        "get_camera_stations",
        "export_walkthrough_package",
        "export_walkthrough_frames",
        "apply_camera_stations",
        "export_model_gltf",
        "export_walkthrough_depth",
    ];

    try {
        for (const profile of ["full", "architect"]) {
            process.env.MCP_PROFILE = profile;
            const registered = registerRevitTools();
            for (const name of walkthroughToolNames) {
                assert.ok(registered.some(tool => tool.name === name), `${profile}:${name}`);
            }
        }

        for (const profile of ["mep", "structural", "fire-safety"]) {
            process.env.MCP_PROFILE = profile;
            const registered = registerRevitTools();
            for (const name of walkthroughToolNames) {
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
