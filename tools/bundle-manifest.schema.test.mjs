// 版本包 manifest schema 的離線驗證：同一份 fixture（docs/schemas/fixtures/）同時給 C# 測試（golden 比對）
// 與這裡的 ajv 使用，避免外掛與網站端 schema 漂移（revit-addin-export.md 風險 A7）。
// 另外比對客戶端 schema 與網站端權威 schema（Issue Manager repo 的 docs/spec/schemas/bundle-manifest.schema.json）：
//   ①客戶端副本 vs docs/schemas/web-authoritative/ 內的原樣副本（一定執行）；
//   ②原樣副本 vs 本機找得到的網站端工作樹（找不到就略過，不算通過）。差異清單必須為空（已知差異只有 $id 與 description）。
import test from 'node:test';
import assert from 'node:assert/strict';
import crypto from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(here, '..');
// ajv 是 @modelcontextprotocol/sdk 的傳遞相依（MCP-Server/package-lock.json 鎖 8.17.1），不另外新增相依。
const require = createRequire(path.join(root, 'MCP-Server', 'package.json'));
const Ajv = require('ajv');

const schema = JSON.parse(fs.readFileSync(path.join(root, 'docs', 'schemas', 'bundle-manifest.schema.json'), 'utf8'));
const sample = JSON.parse(fs.readFileSync(path.join(root, 'docs', 'schemas', 'fixtures', 'bundle-manifest.sample.json'), 'utf8'));
const webCopyPath = path.join(root, 'docs', 'schemas', 'web-authoritative', 'bundle-manifest.schema.json');
const webCopy = JSON.parse(fs.readFileSync(webCopyPath, 'utf8'));
const validate = new Ajv({ allErrors: true, strict: true }).compile(schema);
const clone = value => JSON.parse(JSON.stringify(value));

test('fixture 通過 schema', () => {
    assert.equal(validate(sample), true, JSON.stringify(validate.errors));
});

test('省略 sheetSet、glb、namedViews、viewPositions 仍然合法（缺項留空）', () => {
    const manifest = clone(sample);
    delete manifest.sheetSet;
    delete manifest.files.glb;
    delete manifest.files.namedViews;
    assert.equal(validate(manifest), true, JSON.stringify(validate.errors));
    manifest.files = {};
    manifest.sheets = [];
    manifest.views = [];
    assert.equal(validate(manifest), true, JSON.stringify(validate.errors));
});

const mutations = {
    'schemaVersion 不是 1': m => { m.schemaVersion = 2; },
    '缺少 files': m => { delete m.files; },
    '缺少 options.designOptions': m => { m.options = {}; },
    'sha256 不是 64 位小寫十六進位': m => { m.files.pdf.sha256 = 'ABC'; },
    'bytes 為 0': m => { m.files.pdf.bytes = 0; },
    'pageNumber 為 0': m => { m.sheets[0].pageNumber = 0; },
    'sheetNumber 為空': m => { m.sheets[0].sheetNumber = ''; },
    'viewKind 不在列舉內': m => { m.sheets[0].viewports[0].viewKind = 'legend'; },
    'views[].kind 不在列舉內': m => { m.views[0].kind = 'walkthrough'; },
    'originOffsetMeters 長度不是 3': m => { m.originOffsetMeters = [0, 0]; },
    'exportedAt 不是 UTC ISO 8601': m => { m.exportedAt = '2026/10/25'; },
    '多出未定義欄位': m => { m.extra = true; },
    'files 多出未定義的檔案種類': m => { m.files.other = m.files.pdf; }
};
for (const [name, mutate] of Object.entries(mutations)) {
    test(`schema 拒絕：${name}`, () => {
        const manifest = clone(sample);
        mutate(manifest);
        assert.equal(validate(manifest), false);
    });
}

// ── 與網站端權威 schema 的差異清單 ─────────────────────────────

// 已知、刻意的差異：只有 $id（各自的網域）與 description（各自的說明文字）。其餘一律不得有差異。
const KNOWN_DIFFERENCES = ['$id', 'description'];

// 遞迴列出兩個 JSON 值的差異路徑（陣列比對索引，物件比對 key）。
function diffPaths(a, b, at = '') {
    if (JSON.stringify(a) === JSON.stringify(b)) return [];
    const isObject = value => value !== null && typeof value === 'object';
    if (!isObject(a) || !isObject(b) || Array.isArray(a) !== Array.isArray(b)) return [at || '(root)'];
    const keys = [...new Set([...Object.keys(a), ...Object.keys(b)])];
    return keys.flatMap(key => diffPaths(a[key], b[key], at ? `${at}.${key}` : key));
}

test('客戶端 schema 與網站端權威 schema 的差異清單只有已知項目（$id、description）', () => {
    assert.deepEqual(diffPaths(schema, webCopy).sort(), [...KNOWN_DIFFERENCES].sort());
});

test('diffPaths 自測：改一個上限就會出現在差異清單', () => {
    const changed = clone(schema);
    changed.properties.warnings.maxItems = 100;
    changed.definitions.glbFile.properties.bytes.minimum = 1;
    assert.deepEqual(diffPaths(changed, webCopy).sort(), ['$id', 'definitions.glbFile.properties.bytes.minimum', 'description', 'properties.warnings.maxItems']);
});

const liveCandidates = [
    process.env.IM_WEB_SCHEMA,
    path.resolve(root, '..', 'issue-manager', 'docs', 'spec', 'schemas', 'bundle-manifest.schema.json'),
    'C:/temp/imv4a/wt/docs/spec/schemas/bundle-manifest.schema.json',
    'C:/Project/issue-manager/docs/spec/schemas/bundle-manifest.schema.json'
].filter(Boolean);
const livePath = liveCandidates.find(candidate => fs.existsSync(candidate));

// 雜湊一律以 LF 換行正規化後計算（Windows 的 autocrlf 會讓同一個檔案在不同機器的位元組不同）。
const hashOf = text => crypto.createHash('sha256').update(text.replace(/\r\n/g, '\n')).digest('hex');
const recordedHash = fs.readFileSync(`${webCopyPath}.sha256`, 'utf8').split('\n').find(line => /^[0-9a-f]{64}\s/.test(line))?.slice(0, 64);

test('原樣副本的雜湊與記錄相符（沒有網站端工作樹時的最低把關：偵測副本被誤改）', () => {
    assert.ok(recordedHash, 'docs/schemas/web-authoritative/bundle-manifest.schema.json.sha256 缺少 64 位十六進位雜湊');
    assert.equal(hashOf(fs.readFileSync(webCopyPath, 'utf8')), recordedHash,
        '原樣副本被改動，或重新複製後沒有更新 .sha256；請以網站端檔案為準重新複製並更新雜湊');
});

test('客戶端 schema 與原樣副本的「語意雜湊」一致（扣掉已知差異 $id、description）', () => {
    const normalized = value => {
        const copy = clone(value);
        for (const key of KNOWN_DIFFERENCES) delete copy[key];
        return hashOf(JSON.stringify(copy));
    };
    assert.equal(normalized(schema), normalized(webCopy));
});

// 網站端改版後本檔不會自己知道：只有本機找得到網站端工作樹時才能偵測副本過期。找不到時明確標示略過原因，不假裝通過。
const liveSkipReason = livePath ? false
    : '略過原因：本機找不到網站端工作樹，無法偵測網站端 schema 是否已改版（只剩上面的雜湊把關）；可用 IM_WEB_SCHEMA 指定 bundle-manifest.schema.json 的路徑';
if (!livePath) console.log(`# ${liveSkipReason}`);

test('原樣副本與本機的網站端工作樹一致（找不到網站端 repo 就略過；可用 IM_WEB_SCHEMA 指定路徑）', { skip: liveSkipReason }, () => {
    const liveText = fs.readFileSync(livePath, 'utf8');
    assert.deepEqual(diffPaths(webCopy, JSON.parse(liveText)), [], `原樣副本已過期，請重新複製：${livePath}`);
    assert.equal(hashOf(liveText), recordedHash, `網站端檔案的雜湊與記錄不同，請更新 .sha256：${livePath}`);
});

// ── 網站端 schema 的新增上限（外掛端 ManifestBuilder 也依此裁切）─────────────

const limitMutations = {
    'warnings 超過 200 項': m => { m.warnings = Array.from({ length: 201 }, () => 'w'); },
    'warnings 單項超過 500 字': m => { m.warnings = ['x'.repeat(501)]; },
    'sheets 超過 1,000 張': m => { m.sheets = Array.from({ length: 1001 }, (_, i) => ({ ...m.sheets[0], pageNumber: Math.min(i + 1, 1000), sheetNumber: `S${i}` })); },
    '單張圖紙視口超過 200 個': m => { m.sheets[0].viewports = Array.from({ length: 201 }, () => m.sheets[0].viewports[0]); },
    'views 超過 5,000 個': m => { m.views = Array.from({ length: 5001 }, () => m.views[0]); },
    'sheetNumber 超過 100 字': m => { m.sheets[0].sheetNumber = 'S'.repeat(101); },
    'title 超過 500 字': m => { m.sheets[0].title = 't'.repeat(501); },
    'uniqueId 超過 200 字': m => { m.sheets[0].uniqueId = 'u'.repeat(201); },
    'viewName 超過 300 字': m => { m.sheets[0].viewports[0].viewName = 'n'.repeat(301); },
    'views[].name 超過 300 字': m => { m.views[0].name = 'n'.repeat(301); },
    'revitDoc.title 超過 300 字': m => { m.revitDoc.title = 't'.repeat(301); },
    'revitDoc.versionNumber 超過 100 字': m => { m.revitDoc.versionNumber = 'v'.repeat(101); },
    'sheetSet.name 超過 300 字': m => { m.sheetSet.name = 'n'.repeat(301); },
    'originOffsetMeters 絕對值超過 1e9': m => { m.originOffsetMeters = [1e9 + 1, 0, 0]; },
    'pageNumber 超過 1,000': m => { m.sheets[0].pageNumber = 1001; },
    'PDF 超過 300 MiB': m => { m.files.pdf.bytes = 300 * 1024 * 1024 + 1; },
    'GLB 超過 500 MiB': m => { m.files.glb.bytes = 500 * 1024 * 1024 + 1; },
    'GLB 小於 20 bytes': m => { m.files.glb.bytes = 19; },
    '命名視圖檔超過 4 MiB': m => { m.files.namedViews.bytes = 4 * 1024 * 1024 + 1; },
    '命名視圖檔小於 2 bytes': m => { m.files.namedViews.bytes = 1; },
    'PDF 檔名不是 .pdf': m => { m.files.pdf.name = 'sheets.txt'; },
    '檔名含路徑': m => { m.files.glb.name = '../model.glb'; },
    '檔名含反斜線': m => { m.files.namedViews.name = 'a' + String.fromCharCode(92) + 'b.json'; }
};
for (const [name, mutate] of Object.entries(limitMutations)) {
    test(`網站端 schema 拒絕：${name}`, () => {
        const manifest = clone(sample);
        mutate(manifest);
        assert.equal(validate(manifest), false);
    });
}

test('網站端 schema 的邊界值仍然合法（上限當值）', () => {
    const manifest = clone(sample);
    manifest.warnings = Array.from({ length: 200 }, () => 'x'.repeat(500));
    manifest.sheets[0].sheetNumber = 'S'.repeat(100);
    manifest.sheets[0].title = 't'.repeat(500);
    manifest.sheets[0].uniqueId = 'u'.repeat(200);
    manifest.originOffsetMeters = [1e9, -1e9, 0];
    manifest.files.glb.bytes = 20;
    manifest.files.namedViews.bytes = 2;
    manifest.files.pdf.bytes = 300 * 1024 * 1024;
    assert.equal(validate(manifest), true, JSON.stringify(validate.errors));
});
