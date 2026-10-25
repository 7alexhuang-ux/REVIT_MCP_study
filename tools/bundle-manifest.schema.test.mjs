// 版本包 manifest schema 的離線驗證：同一份 fixture（docs/schemas/fixtures/）同時給 C# 測試（golden 比對）
// 與這裡的 ajv 使用，避免外掛與網站端 schema 漂移（revit-addin-export.md 風險 A7）。
import test from 'node:test';
import assert from 'node:assert/strict';
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
