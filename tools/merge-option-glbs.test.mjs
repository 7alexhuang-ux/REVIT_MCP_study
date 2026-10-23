import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { inspectOptions } from './inspect-option-glb.mjs';
import { readGlb, writeGlb, mergeParts, mergePackage } from './merge-option-glbs.mjs';

const designOption = { setKey: 'set-guid', setName: '選項集', optionKey: 'option-guid', optionName: '方案甲', isPrimary: true };
const manifest = { sets: [{ setKey: designOption.setKey, setName: designOption.setName, options: [{ optionKey: designOption.optionKey, optionName: designOption.optionName, isPrimary: true, elementCount: 1 }] }], generatedAt: '2026-10-23T00:00:00Z', revitDoc: '測試模型' };

function fixture(id, option = null, textured = false) {
    const bin = Buffer.alloc(40);
    [0, 0, 0, 1, 0, 0, 0, 1, 0].forEach((v, i) => bin.writeFloatLE(v, i * 4));
    bin.writeUInt32LE(0x12345678, 36);
    const extras = { elementId: 123, uniqueId: id, category: '牆', family: '基本牆', source: '(host)' };
    if (option) extras.designOption = option;
    const json = {
        asset: { version: '2.0' }, scene: 0, scenes: [{ nodes: [0] }],
        nodes: [{ mesh: 0, extras }], meshes: [{ primitives: [{ attributes: { POSITION: 0 }, material: 0 }] }],
        accessors: [{ bufferView: 0, componentType: 5126, count: 3, type: 'VEC3', min: [0, 0, 0], max: [1, 1, 0] }],
        bufferViews: [{ buffer: 0, byteOffset: 0, byteLength: 36, target: 34962 }],
        materials: [{ pbrMetallicRoughness: { baseColorFactor: [1, 1, 1, 1] } }],
    };
    if (textured) {
        json.bufferViews.push({ buffer: 0, byteOffset: 36, byteLength: 4 });
        json.images = [{ bufferView: 1, mimeType: 'image/png' }];
        json.samplers = [{ magFilter: 9729 }]; json.textures = [{ source: 0, sampler: 0 }];
        json.materials[0].pbrMetallicRoughness.baseColorTexture = { index: 0 };
    }
    return { ...readGlb(writeGlb(json, bin)), part: { kind: option ? 'option' : 'main', ...(option ? { optionKey: option.optionKey } : {}) } };
}

test('合併實際三角形與內嵌貼圖，重編所有索引並保留逐元素 extras', () => {
    const result = mergeParts([fixture('main', null, true), fixture('option', designOption, true)], manifest, [100, 200, 0]);
    const { json, bin } = readGlb(result.glb);
    assert.equal(result.nodes, 2); assert.equal(result.optionNodes, 1);
    assert.equal(json.nodes[0].extras.designOption, undefined);
    assert.deepEqual(json.nodes[1].extras.designOption, designOption);
    assert.equal(json.nodes[1].extras.uniqueId, 'option');
    assert.deepEqual(json.extras.originOffsetMeters, [100, 200, 0]);
    assert.equal(json.meshes[1].primitives[0].attributes.POSITION, 1);
    assert.equal(json.meshes[1].primitives[0].material, 1);
    assert.equal(json.materials[1].pbrMetallicRoughness.baseColorTexture.index, 1);
    assert.equal(json.textures[1].source, 1); assert.equal(json.textures[1].sampler, 1);
    const positionView = json.bufferViews[json.accessors[1].bufferView];
    assert.equal(bin.readFloatLE(positionView.byteOffset + 12), 1);
    const imageView = json.bufferViews[json.images[1].bufferView];
    assert.equal(bin.readUInt32LE(imageView.byteOffset), 0x12345678);
    for (const view of json.bufferViews) assert.equal(view.byteOffset % 4, 0);
});

test('不能把 metadata 偽裝成選項幾何、不能靠名稱配對或吞掉重複元素', () => {
    const main = fixture('main');
    const missing = fixture('missing'); missing.part = { kind: 'option', optionKey: 'option-guid' };
    assert.throws(() => mergeParts([main, missing], manifest, [0, 0, 0]), /缺少 designOption/);
    assert.throws(() => mergeParts([main, fixture('option', { ...designOption, setKey: 'wrong' })], manifest, [0, 0, 0]), /不一致/);
    assert.throws(() => mergeParts([main, fixture('main')], manifest, [0, 0, 0]), /重複元素身分/);
    assert.throws(() => mergeParts([main], manifest, [0, 0]), /originOffsetMeters/);
});

test('拒絕截斷 GLB 與未支援的索引來源', () => {
    const valid = writeGlb(fixture('a').json, fixture('a').bin);
    assert.throws(() => readGlb(valid.subarray(0, valid.length - 1)), /完整/);
    const part = fixture('a'); part.json.extensionsUsed = ['KHR_draco_mesh_compression'];
    assert.throws(() => mergeParts([part], manifest, [0, 0, 0]), /extensions/);
    delete part.json.extensionsUsed; part.json.meshes[0].primitives[0].attributes.POSITION = 50;
    assert.throws(() => mergeParts([part], manifest, [0, 0, 0]), /索引無效/);
});

test('檔案交付包需 complete=true，輸出 GLB 與原始 sidecar 一致', async () => {
    const folder = await fs.mkdtemp(path.join(os.tmpdir(), 'revit-options-'));
    try {
        const main = fixture('main'), option = fixture('option', designOption);
        await fs.writeFile(path.join(folder, 'main.glb'), writeGlb(main.json, main.bin));
        await fs.writeFile(path.join(folder, 'option.glb'), writeGlb(option.json, option.bin));
        await fs.writeFile(path.join(folder, 'design_options.json'), JSON.stringify(manifest));
        const reportPath = path.join(folder, 'design_options_export.json');
        const report = { complete: false, originOffsetMeters: [0, 0, 0], parts: [{ kind: 'main', file: 'main.glb', ok: true }, { kind: 'option', file: 'option.glb', ok: true, optionKey: designOption.optionKey }] };
        await fs.writeFile(reportPath, JSON.stringify(report));
        await assert.rejects(mergePackage(reportPath), /未完成/);
        report.complete = true; await fs.writeFile(reportPath, JSON.stringify(report));
        await assert.rejects(mergePackage(reportPath, path.join(folder, 'design_options.json')), /必須是 .glb/);
        const incomplete = { ...report, parts: [report.parts[0]] };
        await fs.writeFile(reportPath, JSON.stringify(incomplete));
        await assert.rejects(mergePackage(reportPath), /缺少幾何分件/);
        await fs.writeFile(reportPath, JSON.stringify(report));
        const result = await mergePackage(reportPath, path.join(folder, 'merged', 'cpc.glb'));
        assert.equal(readGlb(await fs.readFile(result.output)).json.nodes.length, 2);
        assert.deepEqual(JSON.parse(await fs.readFile(result.designOptions, 'utf8')), manifest);
    } finally { await fs.rm(folder, { recursive: true, force: true }); }
});


test('全為選項的模型可有合法的空主模型分件', () => {
    const empty = readGlb(writeGlb({ asset: { version: '2.0' }, scene: 0, scenes: [{ nodes: [] }] }, Buffer.alloc(0)));
    const merged = mergeParts([{ ...empty, part: { kind: 'main' } }, fixture('option', designOption)], manifest, [0, 0, 0]);
    assert.equal(readGlb(merged.glb).json.nodes.length, 1);
    assert.equal(merged.optionNodes, 1);
});


test('驗收工具檢查實際三角面與每個穩定選項 key', () => {
    const { json } = readGlb(mergeParts([fixture('main'), fixture('option', designOption)], manifest, [0, 0, 0]).glb);
    const report = inspectOptions(json, manifest);
    assert.equal(report.optionNodes, 1); assert.equal(report.mainNodes, 1); assert.equal(report.triangles, 2);
    const missing = structuredClone(manifest); missing.sets[0].options.push({ optionKey: 'missing' });
    assert.throws(() => inspectOptions(json, missing), /沒有幾何節點/);
    json.meshes[1].primitives[0].mode = 1;
    assert.throws(() => inspectOptions(json, manifest), /沒有實際三角面/);
});
