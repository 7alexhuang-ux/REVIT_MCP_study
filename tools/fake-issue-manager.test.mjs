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
        assert.equal((await createBundle(fake, tokens, { stage: undefined })).status, 400);
        assert.equal((await createBundle(fake, tokens, { stage: 'XX' })).status, 400);
        assert.equal((await createBundle(fake, tokens, { description: '   ' })).status, 400);
        assert.equal((await createBundle(fake, tokens, { projectId: 'nope' })).status, 404);
        const old = await fetch(`${fake.url}/api/addin/v1/bundles`, { method: 'POST', headers: { Authorization: `Bearer ${tokens.access_token}` }, body: '{}' });
        assert.equal(old.status, 426);
        const anon = await fetch(`${fake.url}/api/addin/v1/bundles`, { method: 'POST', headers: { 'X-Addin-Api': '1' }, body: '{}' });
        assert.equal(anon.status, 401);
        assert.equal(fake.state.bundles.size, 0);
    } finally { await fake.close(); }
});

test('bundles：分段上傳、finalize 檢查雜湊與標頭、冪等、DELETE', async () => {
    const fake = await startFakeServer({ partBytes: 8 });
    try {
        const { tokens } = await login(fake);
        const created = await (await createBundle(fake, tokens)).json();
        const pdf = Buffer.from('%PDF-1.7 twelve bytes+');
        const put = (kind, index, data) => fetch(`${fake.url}/upload/${created.bundleId}/${kind}/${index}`, { method: 'PUT', headers: { Authorization: `Bearer ${tokens.access_token}` }, body: data });
        const ticket = created.files.find(f => f.kind === 'pdf');
        assert.equal(ticket.partBytes, 8);
        for (let i = 0; i * 8 < pdf.length; i++) assert.equal((await put('pdf', i, pdf.subarray(i * 8, (i + 1) * 8))).status, 200);

        const finalize = body => fetch(`${fake.url}/api/addin/v1/bundles/${created.bundleId}/finalize`, { method: 'POST', headers: { Authorization: `Bearer ${tokens.access_token}`, 'X-Addin-Api': '1' }, body: JSON.stringify(body) });
        const manifest = { schemaVersion: 1, sheets: [], views: [], files: { pdf: { name: 'sheets.pdf', bytes: pdf.length, sha256: 'f'.repeat(64) } } };
        assert.equal((await finalize({ manifest })).status, 400, 'sha256 不符');
        manifest.files.pdf.sha256 = sha256Hex(pdf);
        const ok = await (await finalize({ manifest })).json();
        assert.ok(ok.setId);
        const again = await (await finalize({ manifest })).json();
        assert.equal(again.setId, ok.setId, 'finalize 冪等');
        assert.equal(fake.state.bundles.size, 1);

        const second = await (await createBundle(fake, tokens)).json();
        const del = await fetch(`${fake.url}/api/addin/v1/bundles/${second.bundleId}`, { method: 'DELETE', headers: { Authorization: `Bearer ${tokens.access_token}`, 'X-Addin-Api': '1' } });
        assert.equal(del.status, 200);
        assert.deepEqual(fake.state.deleted, [second.bundleId]);
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
