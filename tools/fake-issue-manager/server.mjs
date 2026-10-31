// 假的 Issue Manager（只給外掛端單元／整合測試與人工試跑用）。
// 依 Issue Manager repo 的 docs/spec/addin-auth.md 與 revit-addin-export.md §4、§5 的合約模擬：
//   授權頁（auto-approve 或手動按鈕）、授權碼＋PKCE、權杖換發與 refresh 輪替／重用偵測、撤銷、
//   /api/addin/me、/api/addin/projects、版本包 bundles（建立、分段 PUT、finalize、DELETE）。
// 不含任何真實權杖或網址；資料只存在記憶體；只綁 127.0.0.1。
// 版本包合約依 Issue Manager 網站端 imv4a 實作（revit-addin-export.md §5.2）：建立回 201、階段錯誤碼 invalid_input、
//   files 只有 pdf／glb／namedViews、finalize 驗證失敗即作廢（之後 409 bundle_failed）、finalize 進行中 409 finalize_in_progress、
//   DELETE 已 finalize 的 bundle 回 409 already_finalized、過期 410 bundle_expired、只有 namedViews（沒有 PDF 也沒有 GLB）回 400 invalid_manifest。
// 用法：node tools/fake-issue-manager/server.mjs [--port 8799] [--auto-approve] [--part-bytes 4194304]
import http from 'node:http';
import crypto from 'node:crypto';
import { pathToFileURL } from 'node:url';
import path from 'node:path';

const b64url = buffer => buffer.toString('base64url');
const sha256Hex = buffer => crypto.createHash('sha256').update(buffer).digest('hex');
const randomToken = prefix => prefix + b64url(crypto.randomBytes(32));
const KINDS = ['pdf', 'glb', 'namedViews'];
const LIMITS = { pdfBytes: 300 * 1024 * 1024, glbBytes: 500 * 1024 * 1024, namedViewsBytes: 4 * 1024 * 1024, pdfPages: 1000 };
const LIMIT_OF_KIND = { pdf: 'pdfBytes', glb: 'glbBytes', namedViews: 'namedViewsBytes' };
// 假伺服器為了輕量，每個 kind 最多核發 256 個網址（真實伺服器依「大小上限 ÷ partBytes」給足）；options.urlsPerFile 可覆寫。
const MAX_FAKE_URLS = 256;

export async function startFakeServer(options = {}) {
    const config = {
        autoApprove: options.autoApprove ?? true,
        partBytes: options.partBytes ?? 4 * 1024 * 1024,
        accessTtlSec: options.accessTtlSec ?? 3600,
        refreshTtlSec: options.refreshTtlSec ?? 30 * 24 * 3600,
        codeTtlSec: options.codeTtlSec ?? 60,
        urlsPerFile: options.urlsPerFile ?? null,
        finalizeDelayMs: options.finalizeDelayMs ?? 0,
        failNextFinalize: options.failNextFinalize ?? 0,
        maxOpenBundles: options.maxOpenBundles ?? Infinity,
        uploadOrigin: options.uploadOrigin ?? null,
        failNextPuts: options.failNextPuts ?? 0,
        failPutStatus: options.failPutStatus ?? 503,
        user: options.user ?? { id: 'user-1', email: 'fake.user@example.test', name: 'Fake User', accountRole: 'member', role: 'admin' },
        projects: options.projects ?? [{ id: 'project-1', code: 'CPC', name: '測試專案' }]
    };
    const state = {
        codes: new Map(),      // hash → {challenge, port, expiresAt, usedAt, familyId}
        tokens: new Map(),     // hash → {kind, familyId, expiresAt, revokedAt, revokedReason}
        csrf: new Set(),
        bundles: new Map(),    // id → {status, stage, description, projectId, parts, setId, files}
        deleted: [],
        authEvents: [],
        putCount: 0,
        finalizeCount: 0,
        refreshCount: 0
    };

    const hash = value => sha256Hex(Buffer.from(value));

    function sendJson(res, status, body, headers = {}) {
        const text = JSON.stringify(body);
        res.writeHead(status, { 'Content-Type': 'application/json; charset=utf-8', 'Cache-Control': 'no-store', ...headers });
        res.end(text);
    }
    const error = (res, status, code, headers = {}) => sendJson(res, status, { error: code }, headers);

    async function readBody(req, limit = 512 * 1024 * 1024) {
        const chunks = [];
        let size = 0;
        for await (const chunk of req) {
            size += chunk.length;
            if (size > limit) throw Object.assign(new Error('too large'), { status: 413 });
            chunks.push(chunk);
        }
        return Buffer.concat(chunks);
    }

    function validAuthorizeParams(p) {
        const portOk = /^[1-9][0-9]{3,4}$/.test(p.port ?? '') && Number(p.port) >= 1024 && Number(p.port) <= 65535;
        return /^[A-Za-z0-9._~-]{8,128}$/.test(p.state ?? '') && /^[A-Za-z0-9_-]{43}$/.test(p.code_challenge ?? '')
            && (p.code_challenge_method === undefined || p.code_challenge_method === 'S256') && portOk;
    }

    function issueCode(p) {
        const code = randomToken('imac_');
        state.codes.set(hash(code), { challenge: p.code_challenge, port: Number(p.port), expiresAt: Date.now() + config.codeTtlSec * 1000, usedAt: null, familyId: null });
        state.authEvents.push('authorized');
        return code;
    }

    function redirectToCallback(res, p, query) {
        res.writeHead(302, { Location: `http://127.0.0.1:${Number(p.port)}/callback?${query}&state=${encodeURIComponent(p.state)}`, 'Cache-Control': 'no-store' });
        res.end();
    }

    function issueTokenSet(familyId) {
        const access = randomToken('imat_');
        const refresh = randomToken('imrt_');
        state.tokens.set(hash(access), { kind: 'access', familyId, expiresAt: Date.now() + config.accessTtlSec * 1000, revokedAt: null });
        state.tokens.set(hash(refresh), { kind: 'refresh', familyId, expiresAt: Date.now() + config.refreshTtlSec * 1000, revokedAt: null });
        return {
            token_type: 'Bearer', scope: 'addin', access_token: access, expires_in: config.accessTtlSec,
            refresh_token: refresh, refresh_expires_in: config.refreshTtlSec, user: config.user
        };
    }

    function revokeFamily(familyId, reason) {
        for (const token of state.tokens.values()) {
            if (token.familyId === familyId && !token.revokedAt) { token.revokedAt = Date.now(); token.revokedReason = reason; }
        }
    }

    function bearer(req) {
        const header = req.headers.authorization ?? '';
        const match = /^Bearer (imat_[A-Za-z0-9_-]+)$/.exec(header);
        if (!match) return null;
        const entry = state.tokens.get(hash(match[1]));
        if (!entry || entry.kind !== 'access' || entry.revokedAt || entry.expiresAt <= Date.now()) return null;
        return entry;
    }

    async function handleToken(req, res) {
        let body;
        try {
            const raw = (await readBody(req, 8 * 1024)).toString('utf8');
            body = JSON.parse(raw);
        } catch { return error(res, 400, 'invalid_request'); }

        if (body.grant_type === 'authorization_code') {
            if (!/^[A-Za-z0-9._~-]{43,128}$/.test(body.code_verifier ?? '')) return error(res, 400, 'invalid_request');
            const entry = state.codes.get(hash(String(body.code ?? '')));
            if (!entry) return error(res, 400, 'invalid_grant');
            if (entry.usedAt) {
                if (entry.familyId) revokeFamily(entry.familyId, 'reuse');
                state.authEvents.push('code_replay');
                return error(res, 400, 'invalid_grant');
            }
            entry.usedAt = Date.now();
            if (entry.expiresAt <= Date.now()) return error(res, 400, 'invalid_grant');
            const challenge = b64url(crypto.createHash('sha256').update(body.code_verifier, 'ascii').digest());
            const a = Buffer.from(challenge), b = Buffer.from(entry.challenge);
            if (a.length !== b.length || !crypto.timingSafeEqual(a, b)) { state.authEvents.push('pkce_mismatch'); return error(res, 400, 'invalid_grant'); }
            entry.familyId = crypto.randomUUID();
            return sendJson(res, 200, issueTokenSet(entry.familyId), { Pragma: 'no-cache' });
        }

        if (body.grant_type === 'refresh_token') {
            const key = hash(String(body.refresh_token ?? ''));
            const entry = state.tokens.get(key);
            if (!entry || entry.kind !== 'refresh') return error(res, 400, 'invalid_grant');
            if (entry.revokedAt) {
                if (entry.revokedReason === 'rotated') { revokeFamily(entry.familyId, 'reuse'); state.authEvents.push('refresh_reuse'); }
                return error(res, 400, 'invalid_grant');
            }
            if (entry.expiresAt <= Date.now()) return error(res, 400, 'invalid_grant');
            entry.revokedAt = Date.now();
            entry.revokedReason = 'rotated';
            state.refreshCount++;
            return sendJson(res, 200, issueTokenSet(entry.familyId), { Pragma: 'no-cache' });
        }
        return error(res, 400, 'unsupported_grant_type');
    }

    async function handleRevoke(req, res) {
        const access = bearer(req);
        if (access) { revokeFamily(access.familyId, 'logout'); return sendJson(res, 200, { revoked: true }); }
        if (req.headers.authorization) return error(res, 401, 'unauthorized', { 'WWW-Authenticate': 'Bearer' });
        try {
            const body = JSON.parse((await readBody(req, 8 * 1024)).toString('utf8') || '{}');
            const entry = body.refresh_token ? state.tokens.get(hash(String(body.refresh_token))) : null;
            if (entry) revokeFamily(entry.familyId, 'logout');
            return sendJson(res, 200, { revoked: true });
        } catch { return error(res, 400, 'invalid_request'); }
    }

    function urlCount(kind) {
        if (config.urlsPerFile) return config.urlsPerFile;
        return Math.min(MAX_FAKE_URLS, Math.ceil(LIMITS[LIMIT_OF_KIND[kind]] / config.partBytes));
    }

    function describeBundle(id, bundle, origin) {
        const uploadOrigin = config.uploadOrigin ?? origin;
        return {
            bundleId: id,
            expiresAt: new Date(bundle.expiresAt).toISOString(),
            limits: LIMITS,
            files: KINDS.map(kind => ({
                kind, name: { pdf: 'sheets.pdf', glb: 'model.glb', namedViews: 'named_views.json' }[kind], partBytes: config.partBytes,
                urls: Array.from({ length: urlCount(kind) }, (_, i) => `${uploadOrigin}/upload/${id}/${kind}/${i}`)
            }))
        };
    }

    // 驗證 manifest：回傳 { status, code } 表示被拒，null 表示通過。只實作會影響外掛行為的規則（完整 schema 由 ajv 測試與網站端負責）。
    function validateManifest(manifest) {
        if (!manifest || manifest.schemaVersion !== 1 || !Array.isArray(manifest.sheets) || !Array.isArray(manifest.views)
            || typeof manifest.files !== 'object' || manifest.files === null || !Array.isArray(manifest.warnings)
            || typeof manifest.options !== 'object' || manifest.options === null) return { status: 400, code: 'invalid_manifest' };
        if (manifest.sheets.length > 1000 || manifest.views.length > 5000 || manifest.warnings.length > 200) return { status: 400, code: 'invalid_manifest' };
        if (manifest.warnings.some(w => typeof w !== 'string' || w.length > 500)) return { status: 400, code: 'invalid_manifest' };
        const numbers = new Set();
        for (const [index, sheet] of manifest.sheets.entries()) {
            if (sheet.pageNumber !== index + 1 || typeof sheet.sheetNumber !== 'string' || sheet.sheetNumber.length === 0 || sheet.sheetNumber.length > 100)
                return { status: 400, code: 'invalid_manifest' };
            if (numbers.has(sheet.sheetNumber.toLowerCase())) return { status: 400, code: 'invalid_manifest' };
            numbers.add(sheet.sheetNumber.toLowerCase());
        }
        const hasPdf = manifest.files.pdf !== undefined;
        const hasGlb = manifest.files.glb !== undefined;
        if ((manifest.sheets.length > 0) !== hasPdf) return { status: 400, code: 'invalid_manifest' };
        if (!hasPdf && !hasGlb) return { status: 400, code: 'invalid_manifest' }; // 只有命名視圖：PDF 與 GLB 至少要有一個
        for (const [kind, file] of Object.entries(manifest.files)) {
            if (!['pdf', 'glb', 'namedViews', 'viewPositions'].includes(kind)) return { status: 400, code: 'invalid_manifest' };
            if (!file || !Number.isInteger(file.bytes) || file.bytes < 1 || typeof file.sha256 !== 'string') return { status: 400, code: 'invalid_manifest' };
            const limit = LIMIT_OF_KIND[kind];
            if (limit && file.bytes > LIMITS[limit]) return { status: 413, code: 'payload_too_large' };
        }
        return null;
    }

    // 驗證失敗即作廢（B03）：之後再 finalize 一律 409 bundle_failed，暫存立即清除。
    function failBundle(bundle) {
        bundle.status = 'failed';
        bundle.parts.clear();
    }

    function expireIfDue(bundle) {
        if (bundle.status === 'open' && bundle.expiresAt <= Date.now()) { bundle.status = 'expired'; bundle.parts.clear(); }
    }

    async function handleBundles(req, res, url, origin) {
        if (req.headers['x-addin-api'] !== '1') return error(res, 426, 'upgrade_required');
        if (!bearer(req)) return error(res, 401, 'unauthorized', { 'WWW-Authenticate': 'Bearer' });
        const parts = url.pathname.split('/').filter(Boolean); // api addin v1 bundles [id] [finalize]
        if (req.method === 'POST' && parts.length === 4) {
            let body;
            try { body = JSON.parse((await readBody(req, 64 * 1024)).toString('utf8')); } catch { return error(res, 400, 'invalid_request'); }
            if (!['PD', 'SD', 'DD'].includes(body.stage)) return sendJson(res, 400, { error: 'invalid_input', field: 'stage' });
            if (typeof body.description !== 'string' || body.description.trim() === '') return error(res, 400, 'description_required');
            if (!config.projects.some(p => p.id === body.projectId)) return error(res, 404, 'not_found');
            if ([...state.bundles.values()].filter(b => b.status === 'open').length >= config.maxOpenBundles) return error(res, 429, 'too_many_open_bundles');
            const id = crypto.randomUUID();
            const bundle = { status: 'open', stage: body.stage, description: body.description, projectId: body.projectId, parts: new Map(), setId: null, expiresAt: Date.now() + 24 * 3600 * 1000 };
            state.bundles.set(id, bundle);
            return sendJson(res, 201, describeBundle(id, bundle, origin));
        }
        const id = parts[4];
        const bundle = state.bundles.get(id);
        if (!bundle) return error(res, 404, 'not_found');
        expireIfDue(bundle);
        if (req.method === 'DELETE' && parts.length === 5) {
            if (bundle.status === 'finalized') return error(res, 409, 'already_finalized'); // 版本已建立：什麼都不做
            if (bundle.status === 'finalizing') return error(res, 409, 'finalize_in_progress');
            if (bundle.status === 'open' || bundle.status === 'failed') { bundle.status = 'deleted'; bundle.parts.clear(); state.deleted.push(id); }
            return sendJson(res, 200, { deleted: true }); // deleted／expired 重送也回 200（冪等）
        }
        if (req.method === 'POST' && parts[5] === 'finalize') {
            if (bundle.status === 'finalized') return sendJson(res, 200, { setId: bundle.setId, url: `${origin}/projects/${bundle.projectId}/sets/${bundle.setId}`, warnings: bundle.warnings ?? [] });
            if (bundle.status === 'expired') return error(res, 410, 'bundle_expired');
            if (bundle.status === 'failed') return error(res, 409, 'bundle_failed');
            if (bundle.status === 'finalizing') return error(res, 409, 'finalize_in_progress');
            if (bundle.status !== 'open') return error(res, 404, 'not_found');
            let body;
            try { body = JSON.parse((await readBody(req, 8 * 1024 * 1024)).toString('utf8')); } catch { return error(res, 400, 'invalid_request'); }
            const manifest = body.manifest;
            const rejected = validateManifest(manifest);
            if (rejected) { failBundle(bundle); return error(res, rejected.status, rejected.code); }
            for (const [kind, file] of Object.entries(manifest.files)) {
                if (kind === 'viewPositions') continue; // V5：4A 不收，忽略並回 warning
                const stored = bundle.parts.get(kind);
                if (!stored) { failBundle(bundle); return error(res, 400, 'invalid_file'); }
                const joined = Buffer.concat([...stored.entries()].sort((a, b) => a[0] - b[0]).map(entry => entry[1]));
                if (joined.length !== file.bytes || sha256Hex(joined) !== file.sha256) { failBundle(bundle); return error(res, 400, 'checksum_mismatch'); }
                if (kind === 'pdf' && !joined.subarray(0, 5).equals(Buffer.from('%PDF-'))) { failBundle(bundle); return error(res, 400, 'invalid_file'); }
                if (kind === 'glb' && !joined.subarray(0, 4).equals(Buffer.from('glTF'))) { failBundle(bundle); return error(res, 400, 'invalid_file'); }
            }
            if (config.failNextFinalize > 0) {
                config.failNextFinalize--;
                failBundle(bundle);
                return error(res, 500, 'finalize_failed');
            }
            bundle.status = 'finalizing';
            if (config.finalizeDelayMs > 0) await new Promise(resolve => setTimeout(resolve, config.finalizeDelayMs));
            bundle.status = 'finalized';
            bundle.setId = crypto.randomUUID();
            bundle.manifest = manifest;
            bundle.warnings = manifest.files.viewPositions ? ['view_positions_not_supported'] : [];
            state.finalizeCount++;
            return sendJson(res, 200, { setId: bundle.setId, url: `${origin}/projects/${bundle.projectId}/sets/${bundle.setId}`, warnings: bundle.warnings });
        }
        return error(res, 404, 'not_found');
    }

    const server = http.createServer(async (req, res) => {
        try {
            const origin = `http://127.0.0.1:${server.address().port}`;
            const url = new URL(req.url, origin);

            if (req.method === 'GET' && url.pathname === '/addin/authorize') {
                const p = Object.fromEntries(url.searchParams);
                if (!validAuthorizeParams(p)) { res.writeHead(400, { 'Content-Type': 'text/html; charset=utf-8' }); return res.end('<p>授權參數不合法，沒有導向任何地方。</p>'); }
                if (config.autoApprove) return redirectToCallback(res, p, `code=${encodeURIComponent(issueCode(p))}`);
                const csrf = randomToken('csrf_');
                state.csrf.add(csrf);
                res.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8', 'Cache-Control': 'no-store' });
                const field = (name, value) => `<input type="hidden" name="${name}" value="${String(value).replace(/"/g, '&quot;')}">`;
                return res.end(`<!doctype html><meta charset="utf-8"><h1>Revit 外掛要以 ${config.user.email}（${config.user.role}）上傳版本</h1>`
                    + `<form method="post" action="/addin/authorize/approve">${field('state', p.state)}${field('code_challenge', p.code_challenge)}${field('port', p.port)}${field('csrf', csrf)}`
                    + `<button name="decision" value="approve">授權</button> <button name="decision" value="deny">取消</button></form>`);
            }
            if (req.method === 'POST' && url.pathname === '/addin/authorize/approve') {
                const form = Object.fromEntries(new URLSearchParams((await readBody(req, 8 * 1024)).toString('utf8')));
                if (!state.csrf.delete(form.csrf) || !validAuthorizeParams(form)) { res.writeHead(400); return res.end(); }
                if (form.decision === 'deny') return redirectToCallback(res, form, 'error=access_denied');
                return redirectToCallback(res, form, `code=${encodeURIComponent(issueCode(form))}`);
            }
            if (req.method === 'POST' && url.pathname === '/api/addin/token') return await handleToken(req, res);
            if (req.method === 'POST' && url.pathname === '/api/addin/token/revoke') return await handleRevoke(req, res);
            if (url.pathname === '/api/addin/me' && req.method === 'GET') {
                if (!bearer(req)) return error(res, 401, 'unauthorized', { 'WWW-Authenticate': 'Bearer' });
                return sendJson(res, 200, { user: config.user, apiVersion: 1 });
            }
            if (url.pathname === '/api/addin/projects' && req.method === 'GET') {
                if (!bearer(req)) return error(res, 401, 'unauthorized', { 'WWW-Authenticate': 'Bearer' });
                return sendJson(res, 200, { projects: config.projects });
            }
            if (url.pathname.startsWith('/api/addin/v1/bundles')) return await handleBundles(req, res, url, origin);

            const upload = /^\/upload\/([^/]+)\/([^/]+)\/(\d+)$/.exec(url.pathname);
            if (req.method === 'PUT' && upload) {
                if (!bearer(req)) return error(res, 401, 'unauthorized');
                if (config.failNextPuts > 0) { config.failNextPuts--; await readBody(req); return error(res, config.failPutStatus, 'temporarily_unavailable'); }
                const bundle = state.bundles.get(upload[1]);
                if (!bundle) return error(res, 404, 'not_found');
                expireIfDue(bundle);
                if (bundle.status === 'expired') { await readBody(req); return error(res, 410, 'bundle_expired'); }
                if (bundle.status !== 'open') { await readBody(req); return error(res, 409, bundle.status === 'finalizing' ? 'finalize_in_progress' : 'bundle_closed'); }
                if (!KINDS.includes(upload[2])) { await readBody(req); return error(res, 404, 'not_found'); }
                const data = await readBody(req);
                if (data.length > config.partBytes) return error(res, 413, 'payload_too_large');
                if (data.length === 0) return error(res, 400, 'invalid_part');
                const declared = req.headers['x-part-sha256'];
                if (declared !== undefined && (!/^[0-9a-f]{64}$/.test(declared) || declared !== sha256Hex(data))) return error(res, 400, 'checksum_mismatch');
                if (!bundle.parts.has(upload[2])) bundle.parts.set(upload[2], new Map());
                bundle.parts.get(upload[2]).set(Number(upload[3]), data);
                state.putCount++;
                return sendJson(res, 200, { ok: true });
            }
            if (req.method === 'POST' && url.pathname === '/__config') {
                const patch = JSON.parse((await readBody(req, 8 * 1024)).toString('utf8') || '{}');
                for (const key of ['failNextPuts', 'failPutStatus', 'accessTtlSec', 'partBytes', 'finalizeDelayMs', 'failNextFinalize', 'maxOpenBundles', 'uploadOrigin', 'urlsPerFile'])
                    if (patch[key] !== undefined) config[key] = patch[key];
                if (patch.expireOpenBundles) for (const b of state.bundles.values()) if (b.status === 'open') b.expiresAt = Date.now() - 1000;
                return sendJson(res, 200, { ok: true });
            }
            if (url.pathname === '/__state') {
                return sendJson(res, 200, {
                    bundles: [...state.bundles.entries()].map(([id, b]) => ({ id, status: b.status, setId: b.setId, stage: b.stage, description: b.description, manifest: b.manifest ?? null })),
                    deleted: state.deleted, putCount: state.putCount, finalizeCount: state.finalizeCount, refreshCount: state.refreshCount, authEvents: state.authEvents,
                    revokedTokens: [...state.tokens.values()].filter(t => t.revokedAt).length
                });
            }
            error(res, 404, 'not_found');
        } catch (e) {
            if (!res.headersSent) error(res, e.status ?? 500, e.status === 413 ? 'payload_too_large' : 'server_error');
            else res.destroy();
        }
    });

    await new Promise(resolve => server.listen(options.port ?? 0, '127.0.0.1', resolve));
    const port = server.address().port;
    return {
        port, url: `http://127.0.0.1:${port}`, config, state,
        close: () => new Promise(resolve => { server.closeAllConnections?.(); server.close(() => resolve()); })
    };
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) {
    const args = process.argv.slice(2);
    const value = name => { const i = args.indexOf(name); return i >= 0 ? args[i + 1] : undefined; };
    const fake = await startFakeServer({
        port: value('--port') ? Number(value('--port')) : 0,
        autoApprove: args.includes('--auto-approve'),
        partBytes: value('--part-bytes') ? Number(value('--part-bytes')) : undefined
    });
    console.log(`fake Issue Manager listening on ${fake.url}  (autoApprove=${fake.config.autoApprove})`);
    console.log('設定外掛：ISSUE_MANAGER_URL=' + fake.url);
}
