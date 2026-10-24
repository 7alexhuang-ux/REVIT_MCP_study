import test from "node:test";
import assert from "node:assert/strict";
import { runBatchedFrames, WalkthroughFrameBatchResponse } from "./walkthrough-batch.js";

function makeResponse(overrides: Partial<WalkthroughFrameBatchResponse>): WalkthroughFrameBatchResponse {
    return {
        exported: 24,
        nextIndex: null,
        total: 72,
        elapsedMs: 1000,
        variant: "base",
        packageDir: "C:/pkg",
        ...overrides,
    };
}

test("exportAll 預設(未指定)時自動分批直到完成，回傳彙整結果", async () => {
    const calls: unknown[] = [];
    const send = async (args: Record<string, unknown>) => {
        calls.push(args);
        const startIndex = args.startIndex as number;
        if (startIndex === 0) return makeResponse({ exported: 24, nextIndex: 24, elapsedMs: 300 });
        if (startIndex === 24) return makeResponse({ exported: 24, nextIndex: 48, elapsedMs: 300 });
        return makeResponse({ exported: 24, nextIndex: null, elapsedMs: 300 });
    };

    const result = await runBatchedFrames(send, { packageDir: "C:/pkg", count: 24 });

    assert.equal(calls.length, 3);
    assert.deepEqual(result, {
        packageDir: "C:/pkg",
        variant: "base",
        total: 72,
        exported: 72,
        batches: 3,
        elapsedMs: 900,
        nextIndex: null,
        framesDir: undefined,
        warnings: [],
    });
});

test("各批 warnings 合併去重、framesDir 取最後一批", async () => {
    const send = async (args: Record<string, unknown>) => {
        const startIndex = args.startIndex as number;
        if (startIndex === 0) {
            return makeResponse({ nextIndex: 24, warnings: ["A", "B"], framesDir: "C:/pkg/frames/base" });
        }
        return makeResponse({ nextIndex: null, warnings: ["B", "C"], framesDir: "C:/pkg/frames/base" });
    };

    const result = await runBatchedFrames(send, { packageDir: "C:/pkg" });
    assert.ok("warnings" in result);
    assert.deepEqual(result.warnings, ["A", "B", "C"]);
    assert.equal((result as { framesDir?: string }).framesDir, "C:/pkg/frames/base");
});

test("exportAll=true 明確指定時行為同預設，三批完成", async () => {
    let call = 0;
    const send = async (args: Record<string, unknown>) => {
        call += 1;
        const startIndex = args.startIndex as number;
        if (startIndex === 0) return makeResponse({ nextIndex: 24 });
        if (startIndex === 24) return makeResponse({ nextIndex: 48 });
        return makeResponse({ nextIndex: null });
    };

    const result = await runBatchedFrames(send, { packageDir: "C:/pkg", exportAll: true });
    assert.equal(call, 3);
    assert.equal((result as { batches: number }).batches, 3);
});

test("第二批失敗時 throw，訊息帶已完成格數與續跑 startIndex", async () => {
    let call = 0;
    const send = async (args: Record<string, unknown>) => {
        call += 1;
        if (call === 1) return makeResponse({ exported: 24, nextIndex: 24 });
        throw new Error("Revit 無回應");
    };

    await assert.rejects(
        () => runBatchedFrames(send, { packageDir: "C:/pkg" }),
        (error: Error) => {
            assert.match(error.message, /Revit 無回應/);
            assert.match(error.message, /本次已匯出 24 格/);
            assert.match(error.message, /startIndex=24/);
            return true;
        }
    );
    assert.equal(call, 2);
});

test("exportAll=false 只呼叫一次，直接回傳該批原始回應", async () => {
    let call = 0;
    const send = async (_args: Record<string, unknown>) => {
        call += 1;
        return makeResponse({ exported: 24, nextIndex: 24 });
    };

    const result = await runBatchedFrames(send, { packageDir: "C:/pkg", startIndex: 0, exportAll: false });
    assert.equal(call, 1);
    assert.equal((result as WalkthroughFrameBatchResponse).exported, 24);
    assert.equal((result as WalkthroughFrameBatchResponse).nextIndex, 24);
});

test("防呆：nextIndex 未前進於 startIndex 時中止，避免無限迴圈", async () => {
    let call = 0;
    const send = async (args: Record<string, unknown>) => {
        call += 1;
        const startIndex = args.startIndex as number;
        // 模擬異常回應：回報的 nextIndex 沒有超過本批的 startIndex
        return makeResponse({ exported: 0, nextIndex: startIndex });
    };

    await assert.rejects(
        () => runBatchedFrames(send, { packageDir: "C:/pkg" }),
        /防呆中止/
    );
    assert.equal(call, 1);
});
