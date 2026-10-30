import test from "node:test";
import assert from "node:assert/strict";
import { registerRevitTools, executeRevitTool } from "./revit-tools.js";
import { RevitSocketClient } from "../socket.js";

test("選項與命名視圖工具註冊，所有文件管理工具可跨 profile 使用", () => {
    const previous = process.env.MCP_PROFILE;
    try {
        for (const profile of ["full", "architect", "mep", "structural", "fire-safety"]) {
            process.env.MCP_PROFILE = profile;
            const tools = registerRevitTools();
            assert.equal(new Set(tools.map(t => t.name)).size, tools.length);
            for (const name of ["open_document", "list_open_documents", "close_document", "list_links", "reload_links"]) assert.ok(tools.some(t => t.name === name), `${profile}:${name}`);
            for (const name of ["list_design_options", "export_design_options", "export_named_views", "export_view_positions"]) assert.equal(tools.some(t => t.name === name), ["full", "architect"].includes(profile));
        }
        process.env.MCP_PROFILE = "full";
        const tools = registerRevitTools();
        const properties = (name: string) => tools.find(t => t.name === name)!.inputSchema.properties as Record<string, any>;
        assert.equal(properties("open_document").detach.default, true);
        assert.equal(properties("open_document").audit.default, false);
        assert.equal(properties("close_document").discard.default, false);
        assert.equal(properties("export_named_views").originOffsetMeters.minItems, 3);
        assert.equal(properties("export_named_views").originOffsetMeters.maxItems, 3);
        assert.equal(properties("export_view_positions").originOffsetMeters.minItems, 3);
        assert.equal(properties("export_view_positions").onlyOnSheets.default, true);
        assert.equal(properties("export_view_positions").viewIds.items.type, "integer");
        assert.equal(properties("export_web_compare").designOptions.default, false);
        assert.deepEqual(properties("export_design_options").excludeElements, properties("export_web_compare").excludeElements);
    } finally { if (previous === undefined) delete process.env.MCP_PROFILE; else process.env.MCP_PROFILE = previous; }
});

test("新工具參數透過正式 bridge 原樣傳遞，失敗不偽裝成成功", async () => {
    const args = { optionKeys: ["unique-option"], excludeElements: ["ID_CPC:4513008"], originOffsetMeters: [100, 200, 0] };
    const client = { async sendCommand(name: string, parameters: unknown) {
        assert.equal(name, "export_design_options"); assert.equal(parameters, args);
        return { success: true, data: { complete: true, parts: ["real.glb"] } };
    } } as unknown as RevitSocketClient;
    assert.deepEqual(await executeRevitTool("export_design_options", args, client), { complete: true, parts: ["real.glb"] });
    const failed = { async sendCommand() { return { success: false, error: "option visibility unavailable" }; } } as unknown as RevitSocketClient;
    await assert.rejects(executeRevitTool("export_design_options", args, failed), /visibility unavailable/);
});
