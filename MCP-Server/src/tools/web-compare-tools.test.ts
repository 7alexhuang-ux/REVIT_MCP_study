import test from "node:test";
import assert from "node:assert/strict";
import { webCompareTools } from "./web-compare-tools.js";
import { registerRevitTools } from "./revit-tools.js";

test("export_web_compare 全部參數選填且有預設值", () => {
    const tool = webCompareTools.find(item => item.name === "export_web_compare");
    assert.ok(tool);
    assert.equal(tool.inputSchema.required, undefined);

    const properties = tool.inputSchema.properties as Record<string, any>;
    assert.equal(properties.viewName.default, "WEB_COMPARE");
    assert.equal(properties.outputDir.default, "C:\\Project\\issue-manager\\spikes\\s1-glb\\input");
    assert.equal(properties.pixelSize.default, 1920);
    assert.equal(properties.pixelSize.minimum, 256);
    for (const key of ["includeImages", "includeIfc", "includeGlb"]) {
        assert.equal(properties[key].type, "boolean");
        assert.equal(properties[key].default, true);
    }
    assert.equal(properties.maxTriangles.default, 3000000);
});

test("export_web_compare 有 sectionBox 與 linkedTerrain 參數且預設 auto", () => {
    const tool = webCompareTools.find(item => item.name === "export_web_compare");
    assert.ok(tool);
    const properties = tool.inputSchema.properties as Record<string, any>;
    assert.deepEqual(properties.sectionBox.properties.mode.enum, ["auto", "manual", "off"]);
    assert.equal(properties.sectionBox.properties.mode.default, "auto");
    assert.equal(properties.sectionBox.properties.marginMeters.default, 10);
    assert.deepEqual(properties.linkedTerrain.enum, ["auto", "always", "off"]);
    assert.equal(properties.linkedTerrain.default, "auto");
});

test("export_web_compare 說明列出模式對照與輸出檔", () => {
    const tool = webCompareTools.find(item => item.name === "export_web_compare");
    assert.ok(tool);
    const description = String(tool.description);
    for (const text of ["cpc.ifc", "cpc.glb", "camera.json", "camera.txt", "consistent↔FlatColors", "shaded↔Shading", "hiddenLine↔HiddenLine", "500 MB"]) {
        assert.ok(description.includes(text), text);
    }
});

test("export_web_compare 只註冊於 full 與 architect profile", () => {
    const originalProfile = process.env.MCP_PROFILE;
    try {
        for (const profile of ["full", "architect"]) {
            process.env.MCP_PROFILE = profile;
            assert.ok(registerRevitTools().some(tool => tool.name === "export_web_compare"), profile);
        }
        for (const profile of ["mep", "structural", "fire-safety"]) {
            process.env.MCP_PROFILE = profile;
            assert.ok(!registerRevitTools().some(tool => tool.name === "export_web_compare"), profile);
        }
    } finally {
        if (originalProfile === undefined) {
            delete process.env.MCP_PROFILE;
        } else {
            process.env.MCP_PROFILE = originalProfile;
        }
    }
});
