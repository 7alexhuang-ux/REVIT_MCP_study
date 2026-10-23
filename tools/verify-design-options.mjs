import fs from 'node:fs/promises';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import { runCommand } from './revit-command.mjs';
import { mergePackage, readGlb } from './merge-option-glbs.mjs';
import { inspectOptions } from './inspect-option-glb.mjs';

/** 一鍵實測：list_design_options → export_design_options → 合併 GLB → 檢查 → export_named_views。
 *  純驗證函式（validate*）可離線測試；跑 Revit 的部分只有 main() 會呼叫。 */

const isVec3 = v => Array.isArray(v) && v.length === 3 && v.every(Number.isFinite);
const assert = (cond, msg) => { if (!cond) throw new Error(msg); };

export function validateListDesignOptions(data) {
    assert(Array.isArray(data?.sets) && data.sets.length > 0, 'list_design_options：sets 為空');
    const problems = [];
    for (const set of data.sets) {
        assert(set.setKey && set.setName !== undefined, 'set 缺 setKey/setName');
        assert(Array.isArray(set.options) && set.options.length > 0, `set ${set.setName} 沒有 options`);
        for (const o of set.options) {
            assert(o.optionKey && o.optionName !== undefined && typeof o.isPrimary === 'boolean', `option 欄位不全：${set.setName}`);
            assert(Number.isInteger(o.elementCount), `option ${o.optionName} elementCount 非整數`);
            if (o.elementCount <= 0) problems.push(`${set.setName}/${o.optionName} elementCount=0`);
        }
        assert(set.options.filter(o => o.isPrimary).length === 1, `set ${set.setName} 主選項數不是 1`);
    }
    return { sets: data.sets.length, options: data.sets.reduce((n, s) => n + s.options.length, 0), emptyOptions: problems };
}

export function validateDesignOptionsManifest(manifest) {
    assert(Array.isArray(manifest?.sets) && manifest.sets.length > 0, 'design_options.json：sets 為空');
    assert(typeof manifest.generatedAt === 'string' && !Number.isNaN(Date.parse(manifest.generatedAt)), 'generatedAt 不是 ISO 時間');
    assert(typeof manifest.revitDoc === 'string' && manifest.revitDoc.length > 0, 'revitDoc 缺失');
    for (const set of manifest.sets) {
        assert(set.setKey && typeof set.setName === 'string', 'manifest set 缺 setKey/setName');
        for (const o of set.options ?? []) {
            assert(o.optionKey && typeof o.optionName === 'string' && typeof o.isPrimary === 'boolean', 'manifest option 欄位不全');
            assert(Number.isInteger(o.elementCount) && o.elementCount > 0, `option ${o.optionName} elementCount 必須 > 0`);
        }
    }
    return true;
}

export function validateNamedViews(data) {
    assert(isVec3(data?.originOffsetMeters), 'named_views：originOffsetMeters 非有限 3 維');
    assert(Array.isArray(data.views) && Array.isArray(data.warnings), 'named_views：views/warnings 缺失');
    const seen = new Set();
    for (const v of data.views) {
        assert(Number.isFinite(v.viewId) && v.uniqueId && typeof v.name === 'string', `view 缺 viewId/uniqueId/name：${v.name}`);
        assert(!seen.has(v.uniqueId), `uniqueId 重複：${v.uniqueId}`); seen.add(v.uniqueId);
        assert(typeof v.isPerspective === 'boolean' && typeof v.displayStyle === 'string', `view ${v.name} 缺 isPerspective/displayStyle`);
        assert(v.viewerMode === null || ['consistent', 'shaded', 'realistic', 'wireframe', 'hiddenLine'].includes(v.viewerMode), `view ${v.name} viewerMode 無效`);
        const g = v.gltf ?? {};
        assert(isVec3(g.eye) && isVec3(g.target) && isVec3(g.up), `view ${v.name} gltf eye/target/up 非有限 3 維`);
        for (const k of ['fovVerticalDeg', 'orthoWidthMeters', 'orthoHeightMeters'])
            assert(g[k] === null || Number.isFinite(g[k]), `view ${v.name} gltf.${k} 必須是有限數或 null`);
        // target 必須是眼點沿視線 10 m
        const dist = Math.hypot(g.target[0] - g.eye[0], g.target[1] - g.eye[1], g.target[2] - g.eye[2]);
        assert(Math.abs(dist - 10) < 1e-3, `view ${v.name} target 距離眼點 ${dist}，應為 10 m`);
        const s = v.sectionBox;
        assert(s && typeof s.enabled === 'boolean', `view ${v.name} 缺 sectionBox.enabled`);
        if (s.enabled) assert(isVec3(s.min) && isVec3(s.max), `view ${v.name} sectionBox 啟用但 min/max 非有限`);
        assert(v.revit && typeof v.revit === 'object', `view ${v.name} 缺 revit 欄位`);
    }
    return { views: data.views.length, perspective: data.views.filter(v => v.isPerspective).length };
}

/** GLB 內每個 designOption.setKey/optionKey 都必須出現在 design_options.json。 */
export function validateGlbAgainstManifest(glbJson, manifest) {
    const sets = new Map(manifest.sets.map(s => [s.setKey, new Set(s.options.map(o => o.optionKey))]));
    for (const node of glbJson.nodes ?? []) {
        const d = node.extras?.designOption;
        if (!d) continue;
        assert(sets.has(d.setKey), `GLB setKey 不在 design_options.json：${d.setKey}`);
        assert(sets.get(d.setKey).has(d.optionKey), `GLB optionKey 不在 design_options.json：${d.optionKey}`);
    }
    return true;
}

async function readJson(file) { return JSON.parse((await fs.readFile(file, 'utf8')).replace(/^﻿/, '')); }

async function main() {
    const exportRequest = process.argv[2] ?? path.join(import.meta.dirname, 'requests', 'export-design-options-fast.json');
    const steps = [];
    const step = async (name, fn) => {
        const t = Date.now();
        try { const r = await fn(); steps.push({ name, ok: true, seconds: (Date.now() - t) / 1000, ...r }); }
        catch (e) { steps.push({ name, ok: false, seconds: (Date.now() - t) / 1000, error: e.message }); throw e; }
    };
    let outputDir, report, mergeResult;
    try {
        await step('list_design_options', async () => {
            const r = await runCommand('list_design_options', { includeViews: true });
            assert(r.Success, r.Error);
            return validateListDesignOptions(r.Data);
        });
        await step('export_design_options', async () => {
            const params = await readJson(exportRequest);
            const r = await runCommand('export_design_options', params);
            assert(r.Success, r.Error);
            report = r.Data; outputDir = report.outputDir ?? params.outputDir;
            assert(report.complete === true, `匯出 complete=false（state=${report.state}）；看 parts 的 failed/noGeometry/diagnostics`);
            validateDesignOptionsManifest(await readJson(path.join(outputDir, 'design_options.json')));
            return { outputDir, elapsedMs: report.elapsedMs, parts: report.parts.length, textureProblems: (report.textureProblems ?? []).length };
        });
        await step('merge + inspect GLB', async () => {
            mergeResult = await mergePackage(path.join(outputDir, 'design_options_export.json'));
            const manifest = await readJson(path.join(outputDir, 'design_options.json'));
            const { json } = readGlb(await fs.readFile(mergeResult.output));
            validateGlbAgainstManifest(json, manifest);
            return { output: mergeResult.output, ...inspectOptions(json, manifest) };
        });
        await step('export_named_views', async () => {
            const r = await runCommand('export_named_views', { outputDir, originOffsetMeters: report.originOffsetMeters });
            assert(r.Success, r.Error);
            assert(r.Data.views.length > 0, '沒有任何非樣板 3D 視圖');
            return validateNamedViews(r.Data);
        });
    } catch { /* 錯誤已記在 steps */ }
    console.log(JSON.stringify(steps, null, 2));
    const allOk = steps.length === 4 && steps.every(s => s.ok);
    console.log(allOk ? 'VERIFY_DESIGN_OPTIONS_OK' : 'VERIFY_DESIGN_OPTIONS_FAILED');
    if (!allOk) process.exitCode = 1;
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) await main();
