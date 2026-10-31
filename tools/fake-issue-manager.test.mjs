// 假 Issue Manager 本身的合約測試：它是外掛 C# 整合測試的對手，行為必須貼近 addin-auth.md／revit-addin-export.md。
import test from 'node:test';
import assert from 'node:assert/strict';
import crypto from 'node:crypto';
import { startFakeServer } from './fake-issue-manager/server.mjs';

const b64url = buffer => buffer.toString('base64url');
const newVerifier = () => b64url(crypto.randomBytes(32));
const challengeOf = verifier => b64url(crypto.createHash('sha256').update(verifier, 'ascii').digest());
const sha256Hex = buffer => crypto.createHash('sha256').update(buffer).digest('hex');

async function authorize(fake, { verifier = newVerifier(), state = b64url(crypto.randomBytes(16)), port = 54321 } = {}) {
    const url = `${fake.url}/addin/authorize?state=${state}&code_challenge=${challengeOf(verifier)}&code_challenge_method=S256&port=${port}&client_version=1.0.0&client_name=test`;
    const response = await fetch(url, { redirect: 'manual' });
    return { response, verifier, state, location: response.headers.get('location') };
}
const exchange = (fake, body) => fetch(`${fake.url}/api/addin/token`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
async function login(fake) {
    const { location, verifier } = await authorize(fake);
    const code = new URL(location).searchParams.get('code');
    const response = await exchange(fake, { grant_type: 'authorization_code', code, code_verifier: verifier });
    return { tokens: await response.json(), code, verifier };
}

test('授權頁：參數不合法回 400 且不導向', async () => {
    const fake = await startFakeServer();
    try {
        for (const port of ['80', '0123456', '1023', '65536', 'abc', '']) {
            const { response, location } = await authorize(fake, { port });
            assert.equal(response.status, 400, `port=${port}`);
            assert.equal(location, null);
        }
        const bad = await fetch(`${fake.url}/addin/authorize?state=x&code_challenge=short&port=54321`, { redirect: 'manual' });
        assert.equal(bad.status, 400);
    } finally { await fake.close(); }
});

test('授權頁：導向只會是 127.0.0.1 loopback 並帶 state', async () => {
    const fake = await startFakeServer();
    try {
        const { location, state } = await authorize(fake, { port: 54321 });
        const target = new URL(location);
        assert.equal(target.hostname, '127.0.0.1');
        assert.equal(target.port, '54321');
        assert.equal(target.pathname, '/callback');
        assert.equal(target.searchParams.get('state'), state);
        assert.match(target.searchParams.get('code'), /^imac_[A-Za-z0-9_-]{43}$/);
    } finally { await fake.close(); }
});

test('換權杖：成功、授權碼只能用一次、重放撤銷整族', async () => {
    const fake = await startFakeServer();
    try {
        const { tokens, code, verifier } = await login(fake);
        assert.match(tokens.access_token, /^imat_/);
        assert.match(tokens.refresh_token, /^imrt_/);
        assert.equal(tokens.user.email, 'fake.user@example.test');
        const replay = await exchange(fake, { grant_type: 'authorization_code', code, code_verifier: verifier });
        assert.equal(replay.status, 400);
        const me = await fetch(`${fake.url}/api/addin/me`, { headers: { Authorization: `Bearer ${tokens.access_token}` } });
        assert.equal(me.status, 401, '重放授權碼後整族權杖應已撤銷');
    } finally { await fake.close(); }
});

test('換權杖：verifier 不符與過期都回 400', async () => {
    const fake = await startFakeServer({ codeTtlSec: 0 });
    try {
        const { location } = await authorize(fake);
        const code = new URL(location).searchParams.get('code');
        const expired = await exchange(fake, { grant_type: 'authorization_code', code, code_verifier: newVerifier() });
        assert.equal(expired.status, 400);
    } finally { await fake.close(); }
    const fake2 = await startFakeServer();
    try {
        const { location } = await authorize(fake2);
        const code = new URL(location).searchParams.get('code');
        const wrong = await exchange(fake2, { grant_type: 'authorization_code', code, code_verifier: newVerifier() });
        assert.equal(wrong.status, 400);
    } finally { await fake2.close(); }
});

test('refresh：輪替成功，重用舊 refresh 撤銷整族', async () => {
    const fake = await startFakeServer();
    try {
        const { tokens } = await login(fake);
        const rotated = await (await exchange(fake, { grant_type: 'refresh_token', refresh_token: tokens.refresh_token })).json();
        assert.notEqual(rotated.refresh_token, tokens.refresh_token);
        const okMe = await fetch(`${fake.url}/api/addin/me`, { headers: { Authorization: `Bearer ${rotated.access_token}` } });
        assert.equal(okMe.status, 200);
        const reuse = await exchange(fake, { grant_type: 'refresh_token', refresh_token: tokens.refresh_token });
        assert.equal(reuse.status, 400);
        const dead = await exchange(fake, { grant_type: 'refresh_token', refresh_token: rotated.refresh_token });
        assert.equal(dead.status, 400, '整族已撤銷，新的 refresh 也不能用');
        assert.ok(fake.state.authEvents.includes('refresh_reuse'));
    } finally { await fake.close(); }
});

test('撤銷：Bearer 撤銷整族；不存在的 refresh 也回 200', async () => {
    const fake = await startFakeServer();
    try {
        const { tokens } = await login(fake);
        const revoke = await fetch(`${fake.url}/api/addin/token/revoke`, { method: 'POST', headers: { Authorization: `Bearer ${tokens.access_token}` } });
        assert.equal(revoke.status, 200);
        const me = await fetch(`${fake.url}/api/addin/me`, { headers: { Authorization: `Bearer ${tokens.access_token}` } });
        assert.equal(me.status, 401);
        const unknown = await fetch(`${fake.url}/api/addin/token/revoke`, { method: 'POST', body: JSON.stringify({ refresh_token: 'imrt_nope' }) });
        assert.equal(unknown.status, 200);
    } finally { await fake.close(); }
});

async function createBundle(fake, tokens, overrides = {}) {
    return fetch(`${fake.url}/api/addin/v1/bundles`, {
        method: 'POST',
        headers: { Authorization: `Bearer ${tokens.access_token}`, 'X-Addin-Api': '1', 'Content-Type': 'application/json' },
        body: JSON.stringify({ projectId: 'project-1', stage: 'SD', description: '測試版本', summary: { sheets: 1, views: 0, models: 0, bytes: 0 }, clientVersion: '1.0.0', ...overrides })
    });
}

test('bundles：階段與說明必填、版本標頭不符回 426、無權杖回 401', async () => {
    const fake = await startFakeServer();
    try {
        const { tokens } = await login(fake);
        for (const stage of [undefined, 'XX', 'pd', ' SD', null, 1]) {
            const response = await createBundle(fake, tokens, { stage });
            assert.equal(response.status, 400, `stage=${stage}`);
            assert.deepEqual(await response.json(), { error: 'invalid_input', field: 'stage' }, '網站端錯誤碼是 invalid_input（不是 stage_required）');
        }
        assert.equal((await createBundle(fake, tokens, { description: '   ' })).status, 400);
        assert.equal((await createBundle(fake, tokens, { projectId: 'nope' })).status, 404);
        const old = await fetch(`${fake.url}/api/addin/v1/bundles`, { method: 'POST', headers: { Authorization: `Bearer ${tokens.access_token}` }, body: '{}' });
        assert.equal(old.status, 426);
        const anon = await fetch(`${fake.url}/api/addin/v1/bundles`, { method: 'POST', headers: { 'X-Addin-Api': '1' }, body: '{}' });
        assert.equal(anon.status, 401);
        assert.equal(fake.state.bundles.size, 0);
    } finally { await fake.close(); }
});

test('bundles：建立回 201，files 只有 pdf／glb／namedViews，附 limits', async () => {
    const fake = await startFakeServer({ partBytes: 8 * 1024 * 1024 });
    try {
        const { tokens } = await login(fake);
        const response = await createBundle(fake, tokens);
        assert.equal(response.status, 201);
        const created = await response.json();
        assert.deepEqual(created.files.map(f => f.kind), ['pdf', 'glb', 'namedViews']);
        assert.deepEqual(created.limits, { pdfBytes: 300 * 1024 * 1024, glbBytes: 500 * 1024 * 1024, namedViewsBytes: 4 * 1024 * 1024, pdfPages: 1000 });
        assert.ok(created.files.every(f => f.partBytes === 8 * 1024 * 1024));
        assert.equal(created.files.find(f => f.kind === 'namedViews').urls.length, 1, '4 MiB ÷ 8 MiB＝1 段');
        assert.ok(created.files.find(f => f.kind === 'pdf').urls.length >= 38, '300 MiB ÷ 8 MiB 至少 38 段（假伺服器上限 256）');
    } finally { await fake.close(); }
});

const finalizeOf = (fake, tokens, bundleId) => body => fetch(`${fake.url}/api/addin/v1/bundles/${bundleId}/finalize`, { method: 'POST', headers: { Authorization: `Bearer ${tokens.access_token}`, 'X-Addin-Api': '1' }, body: JSON.stringify(body) });
const deleteOf = (fake, tokens, bundleId) => () => fetch(`${fake.url}/api/addin/v1/bundles/${bundleId}`, { method: 'DELETE', headers: { Authorization: `Bearer ${tokens.access_token}`, 'X-Addin-Api': '1' } });
const putOf = (fake, tokens, bundleId) => (kind, index, data) => fetch(`${fake.url}/upload/${bundleId}/${kind}/${index}`, { method: 'PUT', headers: { Authorization: `Bearer ${tokens.access_token}` }, body: data });
const oneSheet = { pageNumber: 1, sheetNumber: 'A-1', title: '封面', uniqueId: 'sheet-1', viewports: [] };
const pdfManifest = pdf => ({ schemaVersion: 1, sheets: [oneSheet], views: [], files: { pdf: { name: 'sheets.pdf', bytes: pdf.length, sha256: sha256Hex(pdf) } }, options: { designOptions: false }, warnings: [] });

test('bundles：分段上傳、finalize 檢查雜湊與標頭、冪等、DELETE', async () => {
    const fake = await startFakeServer({ partBytes: 8 });
    try {
        const { tokens } = await login(fake);
        const created = await (await createBundle(fake, tokens)).json();
        const pdf = Buffer.from('%PDF-1.7 twelve bytes+');
        const put = putOf(fake, tokens, created.bundleId);
        const ticket = created.files.find(f => f.kind === 'pdf');
        assert.equal(ticket.partBytes, 8);
        for (let i = 0; i * 8 < pdf.length; i++) assert.equal((await put('pdf', i, pdf.subarray(i * 8, (i + 1) * 8))).status, 200);
        assert.equal((await put('pdf', 0, Buffer.alloc(9))).status, 413, '分段超過 partBytes');
        assert.equal((await put('viewPositions', 0, Buffer.from('x'))).status, 404, 'viewPositions 不核發');
        const withHash = (hash, data) => fetch(`${fake.url}/upload/${created.bundleId}/pdf/0`, { method: 'PUT', headers: { Authorization: `Bearer ${tokens.access_token}`, 'X-Part-Sha256': hash }, body: data });
        assert.equal((await withHash('0'.repeat(64), Buffer.from('abc'))).status, 400, 'X-Part-Sha256 不符 → 400 checksum_mismatch');
        assert.equal((await withHash(sha256Hex(Buffer.from('%PDF-1.7')), Buffer.from('%PDF-1.7'))).status, 200);
        for (let i = 0; i * 8 < pdf.length; i++) assert.equal((await put('pdf', i, pdf.subarray(i * 8, (i + 1) * 8))).status, 200, '重送覆寫');

        const finalize = finalizeOf(fake, tokens, created.bundleId);
        const ok = await (await finalize({ manifest: pdfManifest(pdf) })).json();
        assert.ok(ok.setId);
        assert.match(ok.url, /\/projects\/project-1\/sets\//);
        const again = await (await finalize({ manifest: pdfManifest(pdf) })).json();
        assert.equal(again.setId, ok.setId, 'finalize 冪等');
        assert.equal(fake.state.bundles.size, 1);

        const second = await (await createBundle(fake, tokens)).json();
        const del = await deleteOf(fake, tokens, second.bundleId)();
        assert.equal(del.status, 200);
        assert.deepEqual(fake.state.deleted, [second.bundleId]);
        assert.equal((await deleteOf(fake, tokens, second.bundleId)()).status, 200, '已刪除重送仍 200（冪等）');
    } finally { await fake.close(); }
});

test('bundles：DELETE 已 finalize 的版本包回 409 already_finalized，什麼都不做', async () => {
    const fake = await startFakeServer({ partBytes: 64 });
    try {
        const { tokens } = await login(fake);
        const created = await (await createBundle(fake, tokens)).json();
        const pdf = Buffer.from('%PDF-1.7 abc');
        assert.equal((await putOf(fake, tokens, created.bundleId)('pdf', 0, pdf)).status, 200);
        assert.equal((await finalizeOf(fake, tokens, created.bundleId)({ manifest: pdfManifest(pdf) })).status, 200);
        const del = await deleteOf(fake, tokens, created.bundleId)();
        assert.equal(del.status, 409);
        assert.deepEqual(await del.json(), { error: 'already_finalized' });
        assert.deepEqual(fake.state.deleted, []);
        assert.equal(fake.state.bundles.get(created.bundleId).status, 'finalized');
    } finally { await fake.close(); }
});

test('bundles：驗證失敗即作廢，之後 finalize 一律 409 bundle_failed；500 finalize_failed 同樣作廢', async () => {
    const fake = await startFakeServer({ partBytes: 64 });
    try {
        const { tokens } = await login(fake);
        const created = await (await createBundle(fake, tokens)).json();
        const pdf = Buffer.from('%PDF-1.7 abc');
        await putOf(fake, tokens, created.bundleId)('pdf', 0, pdf);
        const manifest = pdfManifest(pdf);
        manifest.files.pdf.sha256 = 'f'.repeat(64);
        const finalize = finalizeOf(fake, tokens, created.bundleId);
        const bad = await finalize({ manifest });
        assert.equal(bad.status, 400);
        assert.deepEqual(await bad.json(), { error: 'checksum_mismatch' });
        const retry = await finalize({ manifest: pdfManifest(pdf) });
        assert.equal(retry.status, 409, '對作廢的版本包重送 finalize 得到 409');
        assert.deepEqual(await retry.json(), { error: 'bundle_failed' });
        assert.equal((await putOf(fake, tokens, created.bundleId)('pdf', 0, pdf)).status, 409, '作廢後不再接受分段');
        assert.equal((await deleteOf(fake, tokens, created.bundleId)()).status, 200, '作廢的版本包可以 DELETE');
    } finally { await fake.close(); }
    const fake2 = await startFakeServer({ partBytes: 64, failNextFinalize: 1 });
    try {
        const { tokens } = await login(fake2);
        const created = await (await createBundle(fake2, tokens)).json();
        const pdf = Buffer.from('%PDF-1.7 abc');
        await putOf(fake2, tokens, created.bundleId)('pdf', 0, pdf);
        const response = await finalizeOf(fake2, tokens, created.bundleId)({ manifest: pdfManifest(pdf) });
        assert.equal(response.status, 500);
        assert.deepEqual(await response.json(), { error: 'finalize_failed' });
        assert.equal(fake2.state.bundles.get(created.bundleId).status, 'failed');
    } finally { await fake2.close(); }
});

test('bundles：只勾視圖（沒有 PDF 也沒有 GLB）回 400 invalid_manifest', async () => {
    const fake = await startFakeServer({ partBytes: 64 });
    try {
        const { tokens } = await login(fake);
        const created = await (await createBundle(fake, tokens)).json();
        const json = Buffer.from('{"views":[]}');
        await putOf(fake, tokens, created.bundleId)('namedViews', 0, json);
        const manifest = { schemaVersion: 1, sheets: [], views: [], files: { namedViews: { name: 'named_views.json', bytes: json.length, sha256: sha256Hex(json) } }, options: { designOptions: false }, warnings: [] };
        const response = await finalizeOf(fake, tokens, created.bundleId)({ manifest });
        assert.equal(response.status, 400);
        assert.deepEqual(await response.json(), { error: 'invalid_manifest' });
    } finally { await fake.close(); }
});

test('bundles：finalize 進行中另一個 finalize／DELETE 回 409 finalize_in_progress，做完後重送回同一個 setId', async () => {
    const fake = await startFakeServer({ partBytes: 64, finalizeDelayMs: 600 });
    try {
        const { tokens } = await login(fake);
        const created = await (await createBundle(fake, tokens)).json();
        const pdf = Buffer.from('%PDF-1.7 abc');
        await putOf(fake, tokens, created.bundleId)('pdf', 0, pdf);
        const finalize = finalizeOf(fake, tokens, created.bundleId);
        const first = finalize({ manifest: pdfManifest(pdf) });
        await new Promise(resolve => setTimeout(resolve, 150));
        const concurrent = await finalize({ manifest: pdfManifest(pdf) });
        assert.equal(concurrent.status, 409);
        assert.deepEqual(await concurrent.json(), { error: 'finalize_in_progress' });
        const del = await deleteOf(fake, tokens, created.bundleId)();
        assert.equal(del.status, 409);
        assert.deepEqual(await del.json(), { error: 'finalize_in_progress' });
        const done = await (await first).json();
        const again = await (await finalize({ manifest: pdfManifest(pdf) })).json();
        assert.equal(again.setId, done.setId);
        assert.equal(fake.state.finalizeCount, 1);
    } finally { await fake.close(); }
});

test('bundles：過期的版本包 finalize／PUT 回 410 bundle_expired', async () => {
    const fake = await startFakeServer({ partBytes: 64 });
    try {
        const { tokens } = await login(fake);
        const created = await (await createBundle(fake, tokens)).json();
        await fetch(`${fake.url}/__config`, { method: 'POST', body: JSON.stringify({ expireOpenBundles: true }) });
        const pdf = Buffer.from('%PDF-1.7 abc');
        const put = await putOf(fake, tokens, created.bundleId)('pdf', 0, pdf);
        assert.equal(put.status, 410);
        const response = await finalizeOf(fake, tokens, created.bundleId)({ manifest: pdfManifest(pdf) });
        assert.equal(response.status, 410);
        assert.deepEqual(await response.json(), { error: 'bundle_expired' });
    } finally { await fake.close(); }
});

test('bundles：未完成的版本包太多回 429 too_many_open_bundles；uploadOrigin 可改上傳網址主機', async () => {
    const fake = await startFakeServer({ maxOpenBundles: 1, uploadOrigin: 'http://localhost:9' });
    try {
        const { tokens } = await login(fake);
        const first = await createBundle(fake, tokens);
        assert.equal(first.status, 201);
        assert.match((await first.json()).files[0].urls[0], /^http:\/\/localhost:9\/upload\//);
        const second = await createBundle(fake, tokens);
        assert.equal(second.status, 429);
        assert.deepEqual(await second.json(), { error: 'too_many_open_bundles' });
    } finally { await fake.close(); }
});

test('failNextPuts：前幾次 PUT 回 503，之後恢復', async () => {
    const fake = await startFakeServer({ failNextPuts: 2 });
    try {
        const { tokens } = await login(fake);
        const created = await (await createBundle(fake, tokens)).json();
        const put = () => fetch(`${fake.url}/upload/${created.bundleId}/pdf/0`, { method: 'PUT', headers: { Authorization: `Bearer ${tokens.access_token}` }, body: 'x' });
        assert.equal((await put()).status, 503);
        assert.equal((await put()).status, 503);
        assert.equal((await put()).status, 200);
    } finally { await fake.close(); }
});
