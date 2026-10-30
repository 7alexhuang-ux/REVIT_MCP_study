import fs from 'node:fs/promises';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

// 僅支援本 repo 的逐元素、平坦節點 GLB，遇到其他格式直接拒絕。
export function readGlb(bytes) {
    if (bytes.length < 20 || bytes.readUInt32LE(0) !== 0x46546c67 || bytes.readUInt32LE(4) !== 2 || bytes.readUInt32LE(8) !== bytes.length)
        throw new Error('不是完整的 GLB 2.0');
    const chunks = [];
    for (let pos = 12; pos < bytes.length;) {
        if (pos + 8 > bytes.length) throw new Error('GLB chunk 標頭截斷');
        const len = bytes.readUInt32LE(pos), type = bytes.readUInt32LE(pos + 4);
        if (len % 4 || pos + 8 + len > bytes.length) throw new Error('GLB chunk 長度錯誤');
        chunks.push({ type, data: bytes.subarray(pos + 8, pos + 8 + len) });
        pos += 8 + len;
    }
    if (chunks.length === 1 && chunks[0].type === 0x4e4f534a) {
        const json = JSON.parse(chunks[0].data.toString('utf8').trim());
        if ((json.nodes?.length ?? 0) || json.buffers?.length) throw new Error('非空 GLB 缺少 BIN');
        return { json, bin: Buffer.alloc(0) };
    }
    if (chunks.length !== 2 || chunks[0].type !== 0x4e4f534a || chunks[1].type !== 0x004e4942)
        throw new Error('需要 JSON + BIN 兩個 chunk');
    const json = JSON.parse(chunks[0].data.toString('utf8').trim());
    if (json.buffers?.length !== 1 || json.buffers[0].uri || json.buffers[0].byteLength > chunks[1].data.length)
        throw new Error('需要單一內嵌 buffer');
    return { json, bin: chunks[1].data.subarray(0, json.buffers[0].byteLength) };
}

export function writeGlb(json, binary) {
    if (binary.length) json.buffers = [{ byteLength: binary.length }];
    else delete json.buffers;
    const raw = Buffer.from(JSON.stringify(json));
    const text = Buffer.alloc((raw.length + 3) & ~3, 0x20); raw.copy(text);
    const bin = Buffer.alloc((binary.length + 3) & ~3); binary.copy(bin);
    const bytes = Buffer.alloc(20 + text.length + (bin.length ? 8 + bin.length : 0));
    bytes.writeUInt32LE(0x46546c67, 0); bytes.writeUInt32LE(2, 4); bytes.writeUInt32LE(bytes.length, 8);
    bytes.writeUInt32LE(text.length, 12); bytes.writeUInt32LE(0x4e4f534a, 16); text.copy(bytes, 20);
    if (bin.length) { bytes.writeUInt32LE(bin.length, 20 + text.length); bytes.writeUInt32LE(0x004e4942, 24 + text.length); bin.copy(bytes, 28 + text.length); }
    return bytes;
}

function requireIndex(list, index, label) {
    if (!Number.isInteger(index) || index < 0 || index >= (list?.length ?? 0)) throw new Error(`${label} 索引無效：${index}`);
    return list[index];
}

export function mergeParts(parts, manifest, originOffsetMeters) {
    const optionMap = new Map();
    for (const set of manifest.sets ?? []) for (const option of set.options ?? []) {
        if (!set.setKey || !option.optionKey || optionMap.has(option.optionKey)) throw new Error('選項 UniqueId 遺失或重複');
        optionMap.set(option.optionKey, { setKey: set.setKey, setName: set.setName, optionKey: option.optionKey, optionName: option.optionName, isPrimary: option.isPrimary });
    }
    if (!Array.isArray(originOffsetMeters) || originOffsetMeters.length !== 3 || !originOffsetMeters.every(Number.isFinite)) throw new Error('缺少共用 originOffsetMeters');
    const out = { asset: { version: '2.0', generator: 'RevitMCP merge-option-glbs' }, scene: 0, scenes: [{ nodes: [], extras: { originOffsetMeters } }], nodes: [], meshes: [], accessors: [], bufferViews: [], materials: [], textures: [], images: [], samplers: [], extras: { originOffsetMeters } };
    const binaries = [], identities = new Map(); let length = 0, duplicates = 0;
    for (const { json, bin, part } of parts) {
        if ((json.extensionsUsed?.length ?? 0) || (json.extensionsRequired?.length ?? 0) || json.animations?.length || json.skins?.length || json.cameras?.length)
            throw new Error('不支援 extensions／動畫／骨架／相機，請使用本 repo 的逐元素 GLB');
        const findExtensions = value => {
            if (!value || typeof value !== 'object') return;
            if (value.extensions && Object.keys(value.extensions).length) throw new Error('不支援內嵌 extension');
            for (const [key, child] of Object.entries(value)) if (key !== 'extras') findExtensions(child);
        };
        findExtensions(json);
        const roots = requireIndex(json.scenes, json.scene ?? 0, 'scene').nodes ?? [];
        if (roots.length !== (json.nodes?.length ?? 0) || new Set(roots).size !== roots.length) throw new Error('只支援全部節點直接位於單一 scene 的平坦 GLB');
        const maps = Object.fromEntries(['meshes', 'accessors', 'bufferViews', 'materials', 'textures', 'images', 'samplers'].map(key => [key, new Map()]));
        const copy = (key, index) => {
            if (maps[key].has(index)) return maps[key].get(index);
            const value = structuredClone(requireIndex(json[key], index, key));
            const next = out[key].length; maps[key].set(index, next); out[key].push(value);
            if (key === 'meshes') for (const primitive of value.primitives) {
                for (const semantic of Object.keys(primitive.attributes)) primitive.attributes[semantic] = copy('accessors', primitive.attributes[semantic]);
                if (primitive.indices !== undefined) primitive.indices = copy('accessors', primitive.indices);
                if (primitive.material !== undefined) primitive.material = copy('materials', primitive.material);
                if (primitive.targets) throw new Error('不支援 morph targets');
            }
            if (key === 'accessors') {
                if (value.sparse) throw new Error('不支援 sparse accessor');
                value.bufferView = copy('bufferViews', value.bufferView);
            }
            if (key === 'bufferViews') {
                const start = value.byteOffset ?? 0;
                if (value.buffer !== 0 || !Number.isInteger(start) || !Number.isInteger(value.byteLength) || start < 0 || value.byteLength < 0 || start + value.byteLength > bin.length) throw new Error('bufferView 超出 BIN');
                const pad = (4 - length % 4) % 4;
                if (pad) { binaries.push(Buffer.alloc(pad)); length += pad; }
                value.buffer = 0; value.byteOffset = length;
                binaries.push(bin.subarray(start, start + value.byteLength)); length += value.byteLength;
            }
            if (key === 'materials') {
                for (const info of [value.pbrMetallicRoughness?.baseColorTexture, value.pbrMetallicRoughness?.metallicRoughnessTexture, value.normalTexture, value.occlusionTexture, value.emissiveTexture])
                    if (info) info.index = copy('textures', info.index);
            }
            if (key === 'textures') {
                if (value.source !== undefined) value.source = copy('images', value.source);
                if (value.sampler !== undefined) value.sampler = copy('samplers', value.sampler);
            }
            if (key === 'images') {
                if (value.uri) throw new Error('不接受外部貼圖 URI');
                value.bufferView = copy('bufferViews', value.bufferView);
            }
            return next;
        };
        for (const index of roots) {
            const node = structuredClone(requireIndex(json.nodes, index, 'node'));
            if (node.children || node.skin !== undefined || node.camera !== undefined) throw new Error('只支援平坦逐元素 mesh nodes');
            const meta = node.extras;
            if (!meta?.uniqueId || !meta.source || node.mesh === undefined) throw new Error('節點缺少 uniqueId／source／mesh');
            for (const key of ['elementId', 'category', 'family']) if (!(key in meta)) throw new Error(`節點缺少 ${key}`);
            if (part.kind === 'main' && meta.designOption) throw new Error('主模型分件含有設計選項');
            if (part.kind === 'option' && !meta.designOption) throw new Error('選項分件缺少 designOption');
            if (meta.designOption) {
                const expected = optionMap.get(meta.designOption.optionKey);
                if (!expected || Object.entries(expected).some(([key, value]) => meta.designOption[key] !== value)) throw new Error('節點 designOption 與 design_options.json 不一致');
                if (part.optionKey && part.optionKey !== meta.designOption.optionKey) throw new Error('選項分件含其他選項');
            }
            const identity = JSON.stringify([meta.source, meta.uniqueId]);
            if (identities.has(identity)) {
                // 相同身分但不同 mesh 可能是不同 link instance；不可默默刪除。
                throw new Error(`重複元素身分，需先釐清連結 instance 或重複匯出：${identity}`);
            }
            identities.set(identity, meta.designOption?.optionKey ?? null);
            node.mesh = copy('meshes', node.mesh);
            out.scenes[0].nodes.push(out.nodes.length); out.nodes.push(node);
        }
    }
    for (const key of ['textures', 'images', 'samplers', 'materials', 'accessors', 'bufferViews', 'meshes']) if (!out[key].length) delete out[key];
    return { glb: writeGlb(out, Buffer.concat(binaries)), nodes: out.nodes.length, optionNodes: out.nodes.filter(node => node.extras.designOption).length, duplicates };
}

export async function mergePackage(manifestPath, outputPath) {
    const folder = path.dirname(path.resolve(manifestPath));
    const report = JSON.parse((await fs.readFile(manifestPath, 'utf8')).replace(/^\uFEFF/, ''));
    if (report.complete !== true) throw new Error('拒絕合併未完成的匯出，請檢查 design_options_export.json');
    const manifest = JSON.parse((await fs.readFile(path.join(folder, 'design_options.json'), 'utf8')).replace(/^\uFEFF/, ''));
    if (!Array.isArray(report.parts) || !report.parts.length) throw new Error('匯出沒有 parts');
    if (report.parts.filter(part => part.kind === 'main').length !== 1) throw new Error('需要恰好一份主模型');
    const expectedOptions = new Map((manifest.sets ?? []).flatMap(set => (set.options ?? []).map(option => [option.optionKey, set.setKey])));
    const actualOptions = new Set();
    for (const part of report.parts) {
        if (part.ok !== true || !['main', 'option'].includes(part.kind)) throw new Error('分件未成功或 kind 無效');
        if (part.kind === 'option') {
            if (!expectedOptions.has(part.optionKey) || actualOptions.has(part.optionKey) || (part.setKey && part.setKey !== expectedOptions.get(part.optionKey))) throw new Error('選項分件與 manifest 不一致或重複');
            actualOptions.add(part.optionKey);
        }
    }
    if (actualOptions.size !== expectedOptions.size) throw new Error('manifest 有選項缺少幾何分件');
    const parts = [];
    for (const part of report.parts) {
        const file = path.resolve(folder, part.file ?? part.path);
        if (path.relative(folder, file).startsWith('..') || path.isAbsolute(path.relative(folder, file))) throw new Error('分件位於交付包外');
        parts.push({ ...readGlb(await fs.readFile(file)), part });
    }
    const result = mergeParts(parts, manifest, report.originOffsetMeters);
    const output = path.resolve(outputPath ?? path.join(folder, 'cpc.glb'));
    const samePath = (a, b) => path.resolve(a).toLowerCase() === path.resolve(b).toLowerCase();
    if (path.extname(output).toLowerCase() !== '.glb') throw new Error('輸出必須是 .glb');
    if ([manifestPath, path.join(folder, 'design_options.json'), ...report.parts.map(part => path.resolve(folder, part.file ?? part.path))].some(file => samePath(file, output))) throw new Error('輸出不可覆寫輸入檔案');
    await fs.mkdir(path.dirname(output), { recursive: true });
    await fs.writeFile(output, result.glb);
    const sidecar = path.join(path.dirname(output), 'design_options.json');
    if (path.resolve(sidecar) !== path.resolve(folder, 'design_options.json')) await fs.writeFile(sidecar, JSON.stringify(manifest, null, 2));
    return { output, nodes: result.nodes, optionNodes: result.optionNodes, bytes: result.glb.length, designOptions: sidecar };
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) {
    try {
        if (!process.argv[2]) throw new Error('用法：node tools/merge-option-glbs.mjs <design_options_export.json> [output.glb]');
        console.log(JSON.stringify(await mergePackage(process.argv[2], process.argv[3]), null, 2));
    } catch (error) { console.error(error.message); process.exitCode = 1; }
}
