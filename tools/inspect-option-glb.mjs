import fs from 'node:fs/promises';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import { readGlb } from './merge-option-glbs.mjs';

export function inspectOptions(json, manifest) {
    const expected = new Map();
    for (const set of manifest.sets ?? []) for (const option of set.options ?? [])
        expected.set(option.optionKey, { ...option, setKey: set.setKey, setName: set.setName });
    const counts = new Map(), identities = new Set(); let mainNodes = 0, triangles = 0;
    for (const node of json.nodes ?? []) {
        const extras = node.extras;
        if (!extras?.uniqueId || !extras.source || !('elementId' in extras) || !('category' in extras) || !('family' in extras)) throw new Error('缺少元素 extras');
        const identity = JSON.stringify([extras.source, extras.uniqueId]);
        if (identities.has(identity)) throw new Error('重複 source+uniqueId');
        identities.add(identity);
        const mesh = json.meshes?.[node.mesh];
        if (!mesh?.primitives?.length) throw new Error('元素沒有實體 mesh');
        let nodeTriangles = 0;
        for (const primitive of mesh.primitives) {
            const pos = json.accessors?.[primitive.attributes?.POSITION];
            if (!pos || pos.count < 1) throw new Error('缺少 POSITION 幾何');
            if ((primitive.mode ?? 4) === 4) {
                const count = primitive.indices === undefined ? pos.count : json.accessors?.[primitive.indices]?.count;
                if (!Number.isInteger(count) || count < 3 || count % 3) throw new Error('三角形索引數量錯誤');
                nodeTriangles += count / 3;
            }
        }
        triangles += nodeTriangles;
        const option = extras.designOption;
        if (!option) { mainNodes++; continue; }
        const expectedOption = expected.get(option.optionKey);
        if (!expectedOption || ['setKey', 'setName', 'optionKey', 'optionName', 'isPrimary'].some(key => option[key] !== expectedOption[key])) throw new Error('GLB designOption 與 sidecar 不一致');
        if (nodeTriangles === 0) throw new Error('選項節點沒有實際三角面');
        counts.set(option.optionKey, (counts.get(option.optionKey) ?? 0) + 1);
    }
    if (!counts.size) throw new Error('GLB 沒有任何 designOption 實體幾何節點');
    for (const [key, option] of expected) {
        // 只有呼叫者明確排除的選項也可能無幾何；驗收仍須明示失敗，不能視為模型完整。
        if (!counts.has(key)) throw new Error(`manifest 選項沒有幾何節點：${option.setKey}/${key}`);
    }
    return { valid: true, mainNodes, optionNodes: [...counts.values()].reduce((a, b) => a + b, 0), triangles, sets: new Set([...expected.values()].map(o => o.setKey)).size, options: Object.fromEntries(counts) };
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) {
    try {
        if (!process.argv[2] || !process.argv[3]) throw new Error('用法：node tools/inspect-option-glb.mjs <cpc.glb> <design_options.json>');
        const { json } = readGlb(await fs.readFile(process.argv[2]));
        const manifest = JSON.parse((await fs.readFile(process.argv[3], 'utf8')).replace(/^\uFEFF/, ''));
        console.log(JSON.stringify(inspectOptions(json, manifest), null, 2));
    } catch (error) { console.error(error.message); process.exitCode = 1; }
}
