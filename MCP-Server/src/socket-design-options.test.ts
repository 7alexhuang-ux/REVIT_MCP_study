import test from "node:test";
import assert from "node:assert/strict";
import { RevitSocketClient } from "./socket.js";


test("選項匯出與文件載入 timeout 至少十分鐘", () => {
    const client = new RevitSocketClient();
    for (const command of ["export_design_options", "open_document", "reload_links"])
        assert.ok(client.getCommandTimeout(command) >= 600000);
});

test("sendCommand 使用 PascalCase wire envelope 並在斷線清除 pending", async () => {
    const client = new RevitSocketClient();
    let sent: any;
    (client as any).ws = { readyState: 1, send(payload: string, callback: (error?: Error) => void) { sent = JSON.parse(payload); callback(); }, close() {} };
    const pending = client.sendCommand("list_design_options", { includeViews: true });
    assert.equal(sent.CommandName, "list_design_options");
    assert.deepEqual(sent.Parameters, { includeViews: true });
    assert.match(sent.RequestId, /^req_/);
    client.disconnect();
    await assert.rejects(pending, /Disconnected/);
    assert.equal((client as any).responseHandlers.size, 0);
    assert.equal((client as any).responseRejecters.size, 0);
});
