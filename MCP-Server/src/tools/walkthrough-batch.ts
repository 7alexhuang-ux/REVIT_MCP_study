/**
 * export_walkthrough_frames 的分批續傳邏輯（純函式，與 socket/MCP SDK 解耦以便單元測試）。
 * C# 端每批回應形如：{ exported, nextIndex, total, elapsedMs, variant, packageDir }，
 * nextIndex === null 代表全部影格已匯出完成。
 */

export interface WalkthroughFrameBatchResponse {
    exported: number;
    nextIndex: number | null;
    total: number;
    elapsedMs: number;
    variant: string;
    packageDir: string;
    warnings?: string[];
    framesDir?: string;
}

export interface WalkthroughFrameBatchArgs {
    packageDir: string;
    variant?: string;
    startIndex?: number;
    count?: number;
    exportAll?: boolean;
    [key: string]: unknown;
}

export interface WalkthroughFrameBatchResult {
    packageDir: string;
    variant: string;
    total: number;
    exported: number;
    batches: number;
    elapsedMs: number;
    nextIndex: null;
    framesDir?: string;
    warnings: string[];
}

export type SendWalkthroughFrameBatch = (
    args: Record<string, unknown>
) => Promise<WalkthroughFrameBatchResponse>;

/**
 * exportAll !== false（含未指定，預設 true）時，自動以 startIndex 續跑直到 nextIndex === null，
 * 回傳彙整結果；exportAll === false 時只送出一批，原樣回傳該批回應。
 *
 * 任一批失敗會 throw，訊息帶「本次已匯出幾格、可用 startIndex=N 續跑」；各批 warnings 會合併去重回傳；
 * 若某批回應的 nextIndex 未大於該批的 startIndex（未前進），視為防呆觸發並中止，避免無限迴圈。
 */
export async function runBatchedFrames(
    send: SendWalkthroughFrameBatch,
    args: WalkthroughFrameBatchArgs
): Promise<WalkthroughFrameBatchResponse | WalkthroughFrameBatchResult> {
    if (args.exportAll === false) {
        return send(args);
    }

    let nextIndex: number | null = args.startIndex ?? 0;
    let totalExported = 0;
    let totalElapsedMs = 0;
    let batches = 0;
    let total = 0;
    let variant = args.variant ?? "base";
    let packageDir = args.packageDir;
    let framesDir: string | undefined;
    const warnings: string[] = [];

    while (nextIndex !== null) {
        const currentStart = nextIndex;
        let response: WalkthroughFrameBatchResponse;
        try {
            response = await send({ ...args, startIndex: currentStart });
        } catch (error) {
            const message = error instanceof Error ? error.message : String(error);
            throw new Error(
                `影格匯出中斷於第 ${batches + 1} 批：${message}（本次已匯出 ${totalExported} 格，可用 startIndex=${currentStart} 續跑）`
            );
        }

        batches += 1;
        totalExported += response.exported;
        totalElapsedMs += response.elapsedMs;
        total = response.total;
        variant = response.variant;
        packageDir = response.packageDir;
        framesDir = response.framesDir ?? framesDir;
        for (const warning of response.warnings ?? []) {
            if (!warnings.includes(warning)) {
                warnings.push(warning);
            }
        }

        if (response.nextIndex !== null && response.nextIndex <= currentStart) {
            throw new Error(
                `影格匯出防呆中止：第 ${batches} 批 nextIndex(${response.nextIndex}) 未前進於 startIndex(${currentStart})，` +
                    `本次已匯出 ${totalExported} 格，可用 startIndex=${currentStart} 續跑`
            );
        }

        nextIndex = response.nextIndex;
    }

    return {
        packageDir,
        variant,
        total,
        exported: totalExported,
        batches,
        elapsedMs: totalElapsedMs,
        nextIndex: null,
        framesDir,
        warnings,
    };
}
