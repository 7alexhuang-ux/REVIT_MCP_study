#!/usr/bin/env node
// render-depth.mjs — offline depth-map (and optional world-normal) rendering for a
// walkthrough delivery package, so a video model can be given a per-frame structural
// condition (ControlNet/VACE-style) that forces its output to match the exact Revit
// camera path baked into the package. See CONTRACT.md "depth" / "normals" blocks.
//
// Renders headless Chrome on the GPU, loading the SAME vendor/three-bundle.js and
// model.glb the viewer itself uses (see dev/make-sample-package.mjs for the same
// Chrome-launch / puppeteer-core convention this file follows). No Revit round-trip
// needed: depth only depends on the GLB geometry + the already-exported camera path.
//
// Usage:
//   node bridge/node/walkthrough/render-depth.mjs --package <dir> [--width <px>]
//        [--start 0] [--count all] [--encoding gray8|gray16] [--chrome <path>]
//        [--puppeteer-dir <dir>] [--also-normals] [--overwrite]
//
// --width defaults to manifest.frames.width (height = manifest.frames.height,
// taken as-is — NOT re-derived from width/aspect, so it can't drift by a
// rounding pixel). Depth images match the exported frame size unless --width
// explicitly overrides it. Falls back to 1280 only when manifest.frames itself
// has no width (malformed/partial manifest).

import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import fs from 'node:fs';
import os from 'node:os';
import zlib from 'node:zlib';

const require = createRequire(import.meta.url);
const __dirname = path.dirname(fileURLToPath(import.meta.url));
const PROJECT_ROOT = path.resolve(__dirname, '..', '..', '..');
const VIEWER_DIR = path.join(PROJECT_ROOT, 'viewer', 'walkthrough');
const PM = require(path.join(VIEWER_DIR, 'pathmath.js'));

// Same machine-local, non-repo defaults dev/make-sample-package.mjs uses, so a
// puppeteer-core install already made for that script is reused here too.
const DEFAULT_CHROME_CANDIDATES = [
  'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe',
  'C:\\Program Files (x86)\\Google\\Chrome\\Application\\chrome.exe',
  'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe',
  'C:\\Program Files\\Microsoft\\Edge\\Application\\msedge.exe',
];
const DEFAULT_PUPPETEER_DIR =
  'C:\\Users\\womic\\AppData\\Local\\Temp\\claude\\c--Project-REVIT-MCP-study\\dcb377f3-5b7e-4780-bd27-7e31cee54fb9\\scratchpad\\viewer-work\\puppeteer-run';

const STATS_W = 96; // low-res stats-pass grid width (percentile + spot verification)
const NEAR_METERS_DEFAULT = 0.1;
const PERCENTILE = 0.99;

// =========================================================================
// CLI
// =========================================================================

function parseArgs(argv) {
  const out = {
    package: null, width: null, start: 0, count: 'all', encoding: 'gray8',
    chrome: null, puppeteerDir: null, alsoNormals: false, overwrite: false,
  };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a === '--package') out.package = argv[++i];
    else if (a === '--width') out.width = parseInt(argv[++i], 10);
    else if (a === '--start') out.start = parseInt(argv[++i], 10);
    else if (a === '--count') out.count = argv[++i];
    else if (a === '--encoding') out.encoding = argv[++i];
    else if (a === '--chrome') out.chrome = argv[++i];
    else if (a === '--puppeteer-dir') out.puppeteerDir = argv[++i];
    else if (a === '--also-normals') out.alsoNormals = true;
    else if (a === '--overwrite') out.overwrite = true;
  }
  return out;
}

function fail(msg) {
  console.error('[render-depth] ERROR:', msg);
  process.exit(1);
}

// =========================================================================
// Chrome / puppeteer-core bootstrap (same pattern as make-sample-package.mjs)
// =========================================================================

function fileUrl(p) {
  return 'file:///' + p.replace(/\\/g, '/').replace(/^\/*/, '');
}

function findDefaultChrome() {
  for (const p of DEFAULT_CHROME_CANDIDATES) if (fs.existsSync(p)) return p;
  return null;
}

function loadPuppeteer(puppeteerDir) {
  const dir = puppeteerDir || DEFAULT_PUPPETEER_DIR;
  const pkgJsonProbe = path.join(dir, 'package.json');
  if (!fs.existsSync(pkgJsonProbe) || !fs.existsSync(path.join(dir, 'node_modules', 'puppeteer-core'))) {
    throw new Error(
      '[render-depth] no node_modules/puppeteer-core found under ' + dir + '\n' +
      'Install it once (outside the repo), e.g.:\n' +
      '  npm init -y --prefix "' + dir + '"\n' +
      '  npm install puppeteer-core --prefix "' + dir + '"\n' +
      'then rerun with --puppeteer-dir "' + dir + '".'
    );
  }
  const req = createRequire(pkgJsonProbe);
  return req('puppeteer-core');
}

// =========================================================================
// frame filename pattern (same rule as viewer/walkthrough/frames.js)
// =========================================================================

function formatFrameName(pattern, index) {
  const m = /%0(\d)d/.exec(pattern || 'frame_%04d.png');
  if (!m) return (pattern || 'frame_%04d.png').replace('%d', String(index));
  const width = parseInt(m[1], 10);
  let digits = String(index);
  while (digits.length < width) digits = '0' + digits;
  return pattern.replace(m[0], digits);
}

// =========================================================================
// Minimal PNG decoder (filter types 0-4 fully supported; used for reading
// back our own depth/normals output and the sample-package's base frames
// for verification + the visual composite — no third-party image lib).
// =========================================================================

function paeth(a, b, c) {
  const p = a + b - c;
  const pa = Math.abs(p - a), pb = Math.abs(p - b), pc = Math.abs(p - c);
  if (pa <= pb && pa <= pc) return a;
  if (pb <= pc) return b;
  return c;
}

function decodePNG(buf) {
  if (buf.readUInt32BE(0) !== 0x89504e47) throw new Error('not a PNG');
  let off = 8;
  let ihdr = null;
  const idatParts = [];
  while (off < buf.length) {
    const len = buf.readUInt32BE(off);
    const type = buf.toString('ascii', off + 4, off + 8);
    const data = buf.subarray(off + 8, off + 8 + len);
    if (type === 'IHDR') {
      ihdr = {
        width: data.readUInt32BE(0), height: data.readUInt32BE(4),
        bitDepth: data[8], colorType: data[9],
      };
    } else if (type === 'IDAT') {
      idatParts.push(data);
    } else if (type === 'IEND') {
      break;
    }
    off += 8 + len + 4;
  }
  if (!ihdr) throw new Error('missing IHDR');
  const samplesPerPixel = { 0: 1, 2: 3, 3: 1, 4: 2, 6: 4 }[ihdr.colorType];
  const bpp = samplesPerPixel * (ihdr.bitDepth / 8);
  const inflated = zlib.inflateSync(Buffer.concat(idatParts));
  const stride = ihdr.width * bpp;
  const out = Buffer.alloc(stride * ihdr.height);
  let rawOff = 0, outOff = 0;
  for (let y = 0; y < ihdr.height; y++) {
    const filterType = inflated[rawOff++];
    for (let x = 0; x < stride; x++) {
      const rawByte = inflated[rawOff + x];
      const a = x >= bpp ? out[outOff + x - bpp] : 0;
      const b = y > 0 ? out[outOff - stride + x] : 0;
      const c = x >= bpp && y > 0 ? out[outOff - stride + x - bpp] : 0;
      let val;
      switch (filterType) {
        case 0: val = rawByte; break;
        case 1: val = (rawByte + a) & 0xff; break;
        case 2: val = (rawByte + b) & 0xff; break;
        case 3: val = (rawByte + ((a + b) >> 1)) & 0xff; break;
        case 4: val = (rawByte + paeth(a, b, c)) & 0xff; break;
        default: val = rawByte;
      }
      out[outOff + x] = val;
    }
    rawOff += stride;
    outOff += stride;
  }
  return { width: ihdr.width, height: ihdr.height, bitDepth: ihdr.bitDepth, colorType: ihdr.colorType, bpp, pixels: out };
}

// Returns grayscale intensity in [0,1] at (x,y) for a colorType-0 image (any bit depth).
function grayAt(png, x, y) {
  const stride = png.width * png.bpp;
  if (png.bitDepth === 16) {
    const o = y * stride + x * 2;
    return ((png.pixels[o] << 8) | png.pixels[o + 1]) / 65535;
  }
  return png.pixels[y * stride + x] / 255;
}

// Returns [r,g,b] (0..255) at (x,y) for an RGBA8 (colorType 6) or RGB8 (colorType 2) image.
function rgbAt(png, x, y) {
  const stride = png.width * png.bpp;
  const o = y * stride + x * png.bpp;
  return [png.pixels[o], png.pixels[o + 1], png.pixels[o + 2]];
}

// =========================================================================
// Node-side PNG encoder (RGBA8, filter-none) — used only for the human
// visual-check composite image, not for the depth/normals deliverables
// themselves (those are encoded in-browser, see depth-render-template.html).
// =========================================================================

const CRC_TABLE = (() => {
  const t = new Uint32Array(256);
  for (let n = 0; n < 256; n++) {
    let c = n;
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    t[n] = c >>> 0;
  }
  return t;
})();
function crc32(buf) {
  let c = 0xffffffff;
  for (let i = 0; i < buf.length; i++) c = CRC_TABLE[(c ^ buf[i]) & 0xff] ^ (c >>> 8);
  return (c ^ 0xffffffff) >>> 0;
}
function pngChunk(type, data) {
  const typeBuf = Buffer.from(type, 'ascii');
  const len = Buffer.alloc(4); len.writeUInt32BE(data.length, 0);
  const crcBuf = Buffer.alloc(4); crcBuf.writeUInt32BE(crc32(Buffer.concat([typeBuf, data])), 0);
  return Buffer.concat([len, typeBuf, data, crcBuf]);
}
function encodeRGBAPNG(width, height, rgba) {
  const stride = width * 4;
  const raw = Buffer.alloc((stride + 1) * height);
  for (let y = 0; y < height; y++) {
    raw[y * (stride + 1)] = 0;
    rgba.copy(raw, y * (stride + 1) + 1, y * stride, y * stride + stride);
  }
  const idatData = zlib.deflateSync(raw, { level: 9 });
  const sig = Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]);
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(width, 0); ihdr.writeUInt32BE(height, 4);
  ihdr[8] = 8; ihdr[9] = 6; ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
  return Buffer.concat([sig, pngChunk('IHDR', ihdr), pngChunk('IDAT', idatData), pngChunk('IEND', Buffer.alloc(0))]);
}

// =========================================================================
// stats packing decode (must mirror packFloatToRGB in the render page)
// =========================================================================

function unpackRGBToFloat(r, g, b) {
  return (r / 255) / 65536 + (g / 255) / 256 + (b / 255);
}

// =========================================================================
// main
// =========================================================================

async function main() {
  const args = parseArgs(process.argv.slice(2));
  if (!args.package) fail('--package <資料夾> is required');
  if (args.encoding !== 'gray8' && args.encoding !== 'gray16') fail('--encoding must be gray8 or gray16');

  const packageDir = path.resolve(args.package);
  if (!fs.existsSync(packageDir)) fail('package folder not found: ' + packageDir);

  const manifestPath = path.join(packageDir, 'manifest.json');
  if (!fs.existsSync(manifestPath)) fail('manifest.json not found in package: ' + manifestPath);
  const manifest = JSON.parse(fs.readFileSync(manifestPath, 'utf8'));

  if (!manifest.model || !manifest.model.glb) {
    fail('manifest.model is null (or missing .glb) — this package has no exported GLB; run the model export first.');
  }
  const glbPath = path.join(packageDir, manifest.model.glb);
  if (!fs.existsSync(glbPath)) fail('manifest.model.glb points at ' + manifest.model.glb + ' but the file is missing: ' + glbPath);

  const samples = manifest.path && manifest.path.samples;
  if (!samples || !samples.length) fail('manifest.path.samples is empty — nothing to render');

  // Default depth size MUST match the exported frames exactly (a mismatch forces
  // whatever consumes both depth + frames to rescale — wasted compute, and risks
  // misalignment). Only fall back to 1280x720 when manifest.frames itself is
  // missing the field. An explicit --width still overrides (aspect-derived height,
  // rounding is unavoidable there since it's a deliberate size change).
  const frameW = (manifest.frames && manifest.frames.width) || 1280;
  const frameH = (manifest.frames && manifest.frames.height) || 720;
  const aspect = frameW / frameH;
  const widthOverridden = args.width != null;
  const finalW = widthOverridden ? args.width : frameW;
  const finalH = widthOverridden ? Math.max(1, Math.round(finalW / aspect)) : frameH;
  const statsW = STATS_W;
  const statsH = Math.max(1, Math.round(statsW / aspect));

  const fovYDeg = (manifest.camera && manifest.camera.fovYDeg) || 50;
  const camNear = (manifest.camera && manifest.camera.near) || 0.1;
  const camFar = (manifest.camera && manifest.camera.far) || 1000;

  const outDepthDir = path.join(packageDir, 'depth');
  fs.mkdirSync(outDepthDir, { recursive: true });
  const outNormalsDir = path.join(packageDir, 'normals');
  if (args.alsoNormals) fs.mkdirSync(outNormalsDir, { recursive: true });

  const fileNamePattern = (manifest.frames && manifest.frames.fileNamePattern) || 'frame_%04d.png';
  const depthPath = (i) => path.join(outDepthDir, formatFrameName(fileNamePattern, i));
  const normalsPath = (i) => path.join(outNormalsDir, formatFrameName(fileNamePattern, i));

  let start = Math.max(0, args.start || 0);
  let count = args.count === 'all' || args.count == null ? samples.length - start : parseInt(args.count, 10);
  const end = Math.min(samples.length, start + count);

  console.log('[render-depth] package =', packageDir);
  console.log('[render-depth] samples =', samples.length, ' range [', start, ',', end, ')  encoding =', args.encoding, ' size =', finalW + 'x' + finalH);

  // ---- reuse existing near/far normalization if this package already has a depth block ----
  const nearMeters = (manifest.depth && typeof manifest.depth.nearMeters === 'number') ? manifest.depth.nearMeters : NEAR_METERS_DEFAULT;
  let farMeters = manifest.depth && typeof manifest.depth.farMeters === 'number' ? manifest.depth.farMeters : null;
  const reusedNormalization = farMeters !== null;

  // ---- launch chrome ----
  const puppeteer = loadPuppeteer(args.puppeteerDir);
  const chrome = args.chrome || findDefaultChrome();
  if (!chrome) fail('no local Chrome/Edge found; pass --chrome "<path to chrome.exe>"');
  console.log('[render-depth] launching', chrome);

  const packageVendor = path.join(packageDir, 'vendor', 'three-bundle.js');
  const vendorPath = fs.existsSync(packageVendor) ? packageVendor : path.join(VIEWER_DIR, 'vendor', 'three-bundle.js');

  const templateHtml = fs.readFileSync(path.join(__dirname, 'depth-render-template.html'), 'utf8')
    .replace('__THREE_BUNDLE_URL__', fileUrl(vendorPath));
  const tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'walkthrough-depth-'));
  const pagePath = path.join(tmpDir, 'render.html');
  fs.writeFileSync(pagePath, templateHtml);

  const browser = await puppeteer.launch({
    executablePath: chrome,
    headless: 'new',
    args: ['--enable-webgl', '--ignore-gpu-blocklist', '--disable-gpu-sandbox', '--no-sandbox'],
  });

  const timings = { statsMs: 0, renderMs: 0, rendered: 0, skipped: 0, normalsRendered: 0 };
  const verifyRecords = [];

  try {
    const page = await browser.newPage();
    page.on('pageerror', (e) => console.error('[render-depth][pageerror]', e));
    await page.setViewport({ width: Math.max(finalW, statsW), height: Math.max(finalH, statsH) });
    await page.goto(fileUrl(pagePath), { waitUntil: 'load' });

    const glbBase64 = fs.readFileSync(glbPath).toString('base64');
    await page.evaluate((glb) => window.DR.init(glb), glbBase64);
    await page.evaluate(
      (fw, fh, sw, sh, fov, asp, near, far) => window.DR.configure(fw, fh, sw, sh, fov, asp, near, far),
      finalW, finalH, statsW, statsH, fovYDeg, aspect, camNear, camFar
    );
    const rendererInfo = await page.evaluate(() => window.DR.rendererInfo());
    // "Microsoft Basic Render Driver" is Windows' WARP software D3D rasterizer —
    // Chrome/ANGLE falls back to it (instead of SwiftShader by name) when no real
    // GPU device is available, so it must count as software too.
    const isSoftware = /swiftshader|software|llvmpipe|basic render driver|warp/i.test(rendererInfo);
    console.log('[render-depth] WebGL renderer =', rendererInfo, isSoftware ? '(SOFTWARE RENDERING)' : '(hardware GPU)');

    function samplePose(sample) {
      const p = PM.toThree(sample.p);
      const up = PM.toThree(sample.up);
      const look = PM.toThree(PM.add3(sample.p, sample.fwd));
      return [p.x, p.y, p.z, up.x, up.y, up.z, look.x, look.y, look.z];
    }

    // ---- stats pass: full-precision low-res percentile over the WHOLE path,
    // even when only rendering a subset this invocation, so nearMeters/farMeters
    // stay consistent across repeated partial runs of the same package. ----
    const verifyIndices = [0, Math.floor((samples.length - 1) / 2), samples.length - 1];
    if (!reusedNormalization) {
      const t0 = Date.now();
      const assumedMax = camFar;
      const allDistances = [];
      for (let i = 0; i < samples.length; i++) {
        const pose = samplePose(samples[i]);
        const b64 = await page.evaluate((...a) => window.DR.renderStats(...a), ...pose, assumedMax);
        const buf = Buffer.from(b64, 'base64');
        const rec = verifyIndices.includes(i) ? [] : null;
        for (let py = 0; py < statsH; py++) {
          for (let px = 0; px < statsW; px++) {
            const o = (py * statsW + px) * 4;
            const r = buf[o], g = buf[o + 1], b = buf[o + 2], a = buf[o + 3];
            if (a < 128) continue; // no hit
            const dist = unpackRGBToFloat(r, g, b) * assumedMax;
            allDistances.push(dist);
            if (rec) rec.push({ px, py, dist });
          }
        }
        if (rec) verifyRecords.push({ frame: i, samples: rec });
        if (i % 20 === 0) console.log('[render-depth][stats] frame', i, '/', samples.length);
      }
      timings.statsMs = Date.now() - t0;
      if (allDistances.length === 0) {
        console.warn('[render-depth] WARNING: no geometry hit in any frame — falling back to farMeters = camera.far');
        farMeters = camFar;
      } else {
        allDistances.sort((a, b) => a - b);
        const idx = Math.min(allDistances.length - 1, Math.floor(PERCENTILE * (allDistances.length - 1)));
        farMeters = Math.max(nearMeters + 0.1, Math.ceil(allDistances[idx]));
      }
      console.log('[render-depth] computed normalization: nearMeters =', nearMeters, ' farMeters(p99) =', farMeters,
        ' from', allDistances.length, 'valid stats samples across', samples.length, 'frames in', timings.statsMs, 'ms');
    } else {
      console.log('[render-depth] reusing existing manifest.depth normalization: nearMeters =', nearMeters, ' farMeters =', farMeters);
      // still grab cheap ground-truth stats for the 3 verification frames only
      const assumedMax = camFar;
      for (const i of verifyIndices) {
        const pose = samplePose(samples[i]);
        const b64 = await page.evaluate((...a) => window.DR.renderStats(...a), ...pose, assumedMax);
        const buf = Buffer.from(b64, 'base64');
        const rec = [];
        for (let py = 0; py < statsH; py++) {
          for (let px = 0; px < statsW; px++) {
            const o = (py * statsW + px) * 4;
            const r = buf[o], g = buf[o + 1], b = buf[o + 2], a = buf[o + 3];
            if (a < 128) continue;
            rec.push({ px, py, dist: unpackRGBToFloat(r, g, b) * assumedMax });
          }
        }
        verifyRecords.push({ frame: i, samples: rec });
      }
    }

    // ---- final pass: only the requested [start,end) range, skip existing unless --overwrite ----
    const mode = args.encoding === 'gray16' ? 2 : 1;
    const t1 = Date.now();
    let renderedCount = 0;
    for (let i = start; i < end; i++) {
      const dPath = depthPath(i);
      const nPath = args.alsoNormals ? normalsPath(i) : null;
      const depthExists = fs.existsSync(dPath);
      const normalsOk = !args.alsoNormals || fs.existsSync(nPath);
      if (depthExists && normalsOk && !args.overwrite) { timings.skipped++; continue; }

      const pose = samplePose(samples[i]);
      const b64 = await page.evaluate((...a) => window.DR.renderFinal(...a), ...pose, nearMeters, farMeters, mode);
      fs.writeFileSync(dPath, Buffer.from(b64, 'base64'));
      renderedCount++;

      if (args.alsoNormals) {
        const nb64 = await page.evaluate((...a) => window.DR.renderNormals(...a), ...pose);
        fs.writeFileSync(nPath, Buffer.from(nb64, 'base64'));
        timings.normalsRendered++;
      }

      if (renderedCount % 20 === 0) {
        const elapsed = Date.now() - t1;
        console.log('[render-depth][final] frame', i, '/', end, ' avg', (elapsed / renderedCount).toFixed(1), 'ms/frame');
      }
    }
    timings.renderMs = Date.now() - t1;
    timings.rendered = renderedCount;

    await browser.close();
    fs.rmSync(tmpDir, { recursive: true, force: true });

    // ---- update manifest.json + data.js ----
    let existingCount = 0;
    for (let i = 0; i < samples.length; i++) if (fs.existsSync(depthPath(i))) existingCount++;

    manifest.depth = {
      dir: 'depth', encoding: args.encoding, nearMeters, farMeters,
      width: finalW, height: finalH, count: existingCount,
      convention: 'near-white-linear-viewz',
      generatedAt: new Date().toISOString(),
      source: 'glb-three',
      normalsDir: args.alsoNormals ? 'normals' : ((manifest.depth && manifest.depth.normalsDir) || null),
    };
    fs.writeFileSync(manifestPath, JSON.stringify(manifest, null, 2));

    const dataJsPath = path.join(packageDir, 'data.js');
    let preservedGlbBase64 = null;
    let prevGlbLen = null;
    if (fs.existsSync(dataJsPath)) {
      const prevSrc = fs.readFileSync(dataJsPath, 'utf8');
      const sandboxWindow = {};
      // eslint-disable-next-line no-new-func
      const fn = new Function('window', prevSrc + '\nreturn window.WALK_DATA;');
      const prevData = fn(sandboxWindow);
      preservedGlbBase64 = prevData && prevData.glbBase64 ? prevData.glbBase64 : null;
      prevGlbLen = preservedGlbBase64 ? preservedGlbBase64.length : 0;
    }
    if (!preservedGlbBase64) preservedGlbBase64 = fs.readFileSync(glbPath).toString('base64');
    fs.writeFileSync(dataJsPath, 'window.WALK_DATA = ' + JSON.stringify({ manifest, glbBase64: preservedGlbBase64 }) + ';\n');
    console.log('[render-depth] data.js glbBase64 length: before =', prevGlbLen, ' after =', preservedGlbBase64.length,
      prevGlbLen === null || prevGlbLen === preservedGlbBase64.length ? '(preserved)' : '(MISMATCH!)');

    // ---- spot-check report: compare final-image gray value vs stats ground truth ----
    console.log('[render-depth] --- spot-check (stats ground truth vs final PNG) ---');
    for (const rec of verifyRecords) {
      const dPath = depthPath(rec.frame);
      if (!fs.existsSync(dPath) || rec.samples.length === 0) continue;
      const png = decodePNG(fs.readFileSync(dPath));
      const scaleX = png.width / statsW, scaleY = png.height / statsH;
      const picks = [rec.samples[Math.floor(rec.samples.length * 0.2)], rec.samples[Math.floor(rec.samples.length * 0.5)], rec.samples[Math.floor(rec.samples.length * 0.8)]].filter(Boolean);
      for (const s of picks) {
        const fx = Math.min(png.width - 1, Math.round(s.px * scaleX));
        const fy = Math.min(png.height - 1, Math.round(s.py * scaleY));
        const intensity = grayAt(png, fx, fy);
        const decodedDist = nearMeters + (1 - intensity) * (farMeters - nearMeters);
        const errPct = s.dist > 0 ? Math.abs(decodedDist - s.dist) / s.dist * 100 : 0;
        console.log('  frame', rec.frame, 'px(' + fx + ',' + fy + ')', 'groundTruth=', s.dist.toFixed(3) + 'm',
          'decodedFromPNG=', decodedDist.toFixed(3) + 'm', 'err=', errPct.toFixed(2) + '%');
      }
    }

    console.log('[render-depth] --- summary ---');
    console.log('  renderer backend:', rendererInfo, isSoftware ? '(software)' : '(hardware)');
    console.log('  stats pass:', reusedNormalization ? 'reused existing normalization' : timings.statsMs + ' ms total');
    console.log('  final pass:', timings.rendered, 'rendered,', timings.skipped, 'skipped, ', timings.renderMs, 'ms total',
      timings.rendered > 0 ? ', avg ' + (timings.renderMs / timings.rendered).toFixed(2) + ' ms/frame' : '');
    if (args.alsoNormals) console.log('  normals rendered:', timings.normalsRendered);
    console.log('  nearMeters =', nearMeters, ' farMeters =', farMeters, ' encoding =', args.encoding);
    console.log('  depth dir:', outDepthDir, ' count on disk:', existingCount, '/', samples.length);
  } catch (e) {
    await browser.close().catch(() => {});
    throw e;
  }
}

main().catch((err) => {
  console.error('[render-depth] FAILED:', err);
  process.exit(1);
});
