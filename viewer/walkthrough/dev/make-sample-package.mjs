#!/usr/bin/env node
// Generates a synthetic walkthrough package that satisfies CONTRACT.md, for
// exercising the viewer end-to-end without a real Revit export. Everything is
// produced from scratch with Node built-ins only (no npm deps at runtime):
//   - a minimal hand-rolled PNG encoder (zlib for the IDAT deflate stream)
//   - a minimal hand-rolled glTF 2.0 binary (.glb) writer
//   - path samples for a synthetic L-shaped corridor
//
// Usage:
//   node dev/make-sample-package.mjs [outDir]
//   node dev/make-sample-package.mjs [outDir] --render-frames [--chrome <path>] [--puppeteer-dir <dir>]
//
// Default outDir: <scratchpad>/viewer-work/sample-package (see SCRATCH_DEFAULT below).
//
// --render-frames replaces the solid-color placeholder "base" frames with real
// renders: a headless Chrome page loads the SAME vendor/three-bundle.js and the
// generated model.glb, and for every sample renders camera pose -> toThree ->
// fovY/aspect exactly the way viewer's scene3d.js does — see renderBaseFramesHeadless()
// below. This proves the viewer's camera chain end-to-end (poseAtT/toThree/fov/
// aspect/calibration overlay), which flat color frames can't. Needs a local
// Chrome/Edge and puppeteer-core installed OUTSIDE the repo (see --puppeteer-dir);
// neither is a repo dependency. Without --render-frames, behavior is unchanged.

import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import fs from 'node:fs';
import os from 'node:os';
import zlib from 'node:zlib';

const require = createRequire(import.meta.url);
const __dirname = path.dirname(fileURLToPath(import.meta.url));
const VIEWER_DIR = path.resolve(__dirname, '..');
const PM = require(path.join(VIEWER_DIR, 'pathmath.js'));

const SCRATCH_DEFAULT =
  process.env.REVIT_MCP_SAMPLE_DIR ||
  path.join(process.env.TEMP || process.env.TMPDIR || '.', 'revit-mcp-viewer-work', 'sample-package');

// Defaults used only by --render-frames; both point at machine-local, non-repo
// installs (a local browser binary, and a scratchpad npm install of puppeteer-core).
const DEFAULT_CHROME_CANDIDATES = [
  'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe',
  'C:\\Program Files (x86)\\Google\\Chrome\\Application\\chrome.exe',
  'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe',
  'C:\\Program Files\\Microsoft\\Edge\\Application\\msedge.exe',
];
const DEFAULT_PUPPETEER_DIR =
  process.env.REVIT_MCP_PUPPETEER_DIR ||
  path.join(process.env.TEMP || process.env.TMPDIR || '.', 'revit-mcp-viewer-work', 'puppeteer-run');

function parseArgs(argv) {
  const out = { outDir: null, renderFrames: false, chrome: null, puppeteerDir: null };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a === '--render-frames') out.renderFrames = true;
    else if (a === '--chrome') out.chrome = argv[++i];
    else if (a === '--puppeteer-dir') out.puppeteerDir = argv[++i];
    else if (!out.outDir) out.outDir = a;
  }
  return out;
}
const ARGS = parseArgs(process.argv.slice(2));

function resolveOutDir() {
  return ARGS.outDir ? path.resolve(ARGS.outDir) : SCRATCH_DEFAULT;
}

// =========================================================================
// PNG encoder (RGBA8, filter-none, zlib via node:zlib)
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
  const len = Buffer.alloc(4);
  len.writeUInt32BE(data.length, 0);
  const crcBuf = Buffer.alloc(4);
  crcBuf.writeUInt32BE(crc32(Buffer.concat([typeBuf, data])), 0);
  return Buffer.concat([len, typeBuf, data, crcBuf]);
}

function encodePNG(width, height, rgba) {
  const stride = width * 4;
  const raw = Buffer.alloc((stride + 1) * height);
  for (let y = 0; y < height; y++) {
    raw[y * (stride + 1)] = 0; // filter type 0 (None)
    rgba.copy(raw, y * (stride + 1) + 1, y * stride, y * stride + stride);
  }
  const idatData = zlib.deflateSync(raw, { level: 9 });

  const sig = Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]);
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(width, 0);
  ihdr.writeUInt32BE(height, 4);
  ihdr[8] = 8; // bit depth
  ihdr[9] = 6; // color type RGBA
  ihdr[10] = 0; // compression
  ihdr[11] = 0; // filter
  ihdr[12] = 0; // interlace

  return Buffer.concat([sig, pngChunk('IHDR', ihdr), pngChunk('IDAT', idatData), pngChunk('IEND', Buffer.alloc(0))]);
}

function hslToRgb(h, s, l) {
  h = ((h % 360) + 360) % 360;
  const c = (1 - Math.abs(2 * l - 1)) * s;
  const x = c * (1 - Math.abs(((h / 60) % 2) - 1));
  const m = l - c / 2;
  let r, g, b;
  if (h < 60) [r, g, b] = [c, x, 0];
  else if (h < 120) [r, g, b] = [x, c, 0];
  else if (h < 180) [r, g, b] = [0, c, x];
  else if (h < 240) [r, g, b] = [0, x, c];
  else if (h < 300) [r, g, b] = [x, 0, c];
  else [r, g, b] = [c, 0, x];
  return [Math.round((r + m) * 255), Math.round((g + m) * 255), Math.round((b + m) * 255)];
}

// Frame image: background hue sweeps with frame index (so every frame is a
// visually distinct color), plus a growing white progress bar along the
// bottom. `night` variant gets a darkened/blue-shifted tint.
function renderFramePNG(width, height, index, count, variantId) {
  let hue = (index / Math.max(1, count - 1)) * 300;
  let sat = 0.65, light = 0.42;
  if (variantId === 'night') {
    hue = (hue + 210) % 360;
    light = 0.2;
    sat = 0.55;
  }
  const [r, g, b] = hslToRgb(hue, sat, light);
  const rgba = Buffer.alloc(width * height * 4);
  for (let i = 0; i < width * height; i++) {
    rgba[i * 4] = r;
    rgba[i * 4 + 1] = g;
    rgba[i * 4 + 2] = b;
    rgba[i * 4 + 3] = 255;
  }
  // progress bar
  const barH = Math.max(6, Math.round(height * 0.06));
  const barY0 = height - barH - 4;
  const barW = Math.round((width - 8) * (index / Math.max(1, count - 1)));
  for (let y = barY0; y < barY0 + barH; y++) {
    for (let x = 4; x < 4 + barW; x++) {
      const o = (y * width + x) * 4;
      rgba[o] = 255; rgba[o + 1] = 255; rgba[o + 2] = 255; rgba[o + 3] = 255;
    }
  }
  // top-left index "tick block" grid: draws `index` small ticks (capped) so
  // adjacent frames are distinguishable even at a glance without reading numbers
  const ticks = Math.min(40, index);
  for (let i = 0; i < ticks; i++) {
    const tx = 6 + i * 6;
    if (tx + 4 > width) break;
    for (let y = 6; y < 16; y++) {
      for (let x = tx; x < tx + 4; x++) {
        const o = (y * width + x) * 4;
        rgba[o] = 20; rgba[o + 1] = 20; rgba[o + 2] = 20; rgba[o + 3] = 255;
      }
    }
  }
  return encodePNG(width, height, rgba);
}

function makeSolidRegionsPNG(width, height, bg, regions) {
  const rgba = Buffer.alloc(width * height * 4);
  for (let i = 0; i < width * height; i++) {
    rgba[i * 4] = bg[0]; rgba[i * 4 + 1] = bg[1]; rgba[i * 4 + 2] = bg[2]; rgba[i * 4 + 3] = 255;
  }
  regions.forEach((r) => {
    const x0 = Math.max(0, Math.round(r.x0)), x1 = Math.min(width, Math.round(r.x1));
    const y0 = Math.max(0, Math.round(r.y0)), y1 = Math.min(height, Math.round(r.y1));
    for (let y = y0; y < y1; y++) {
      for (let x = x0; x < x1; x++) {
        const o = (y * width + x) * 4;
        rgba[o] = r.color[0]; rgba[o + 1] = r.color[1]; rgba[o + 2] = r.color[2]; rgba[o + 3] = 255;
      }
    }
  });
  return encodePNG(width, height, rgba);
}

// =========================================================================
// Minimal glTF 2.0 binary (.glb) writer — axis-aligned boxes only.
// =========================================================================

function buildBoxMesh(min, max) {
  const [x0, y0, z0] = min, [x1, y1, z1] = max;
  const faces = [
    { n: [1, 0, 0], v: [[x1, y0, z0], [x1, y1, z0], [x1, y1, z1], [x1, y0, z1]] },
    { n: [-1, 0, 0], v: [[x0, y0, z1], [x0, y1, z1], [x0, y1, z0], [x0, y0, z0]] },
    { n: [0, 1, 0], v: [[x0, y1, z0], [x0, y1, z1], [x1, y1, z1], [x1, y1, z0]] },
    { n: [0, -1, 0], v: [[x0, y0, z1], [x0, y0, z0], [x1, y0, z0], [x1, y0, z1]] },
    { n: [0, 0, 1], v: [[x0, y0, z1], [x1, y0, z1], [x1, y1, z1], [x0, y1, z1]] },
    { n: [0, 0, -1], v: [[x0, y1, z0], [x1, y1, z0], [x1, y0, z0], [x0, y0, z0]] },
  ];
  const positions = [];
  const normals = [];
  const indices = [];
  faces.forEach((f, fi) => {
    const base = fi * 4;
    f.v.forEach((p) => {
      // model (Z-up) -> three.js (Y-up), the project's one axis swap (CONTRACT.md).
      const tp = PM.toThree(p);
      positions.push(tp.x, tp.y, tp.z);
      const tn = PM.toThree(f.n);
      normals.push(tn.x, tn.y, tn.z);
    });
    indices.push(base, base + 1, base + 2, base, base + 2, base + 3);
  });
  return { positions, normals, indices };
}

function buildGlb(boxes) {
  // boxes: [{ min, max, color:[r,g,b,a] (0..1), name }]
  const buffers = [];
  let byteOffset = 0;
  const bufferViews = [];
  const accessors = [];
  const meshes = [];
  const materials = [];
  const nodes = [];

  function pushFloatArray(arr, target) {
    const buf = Buffer.from(new Float32Array(arr).buffer);
    buffers.push(buf);
    const bv = { buffer: 0, byteOffset, byteLength: buf.length, target };
    byteOffset += buf.length;
    bufferViews.push(bv);
    return bufferViews.length - 1;
  }
  function pushIndexArray(arr) {
    const buf = Buffer.from(new Uint16Array(arr).buffer);
    buffers.push(buf);
    const bv = { buffer: 0, byteOffset, byteLength: buf.length, target: 34963 };
    byteOffset += buf.length;
    bufferViews.push(bv);
    return bufferViews.length - 1;
  }
  function minMax3(flat) {
    const min = [Infinity, Infinity, Infinity], max = [-Infinity, -Infinity, -Infinity];
    for (let i = 0; i < flat.length; i += 3) {
      for (let k = 0; k < 3; k++) {
        min[k] = Math.min(min[k], flat[i + k]);
        max[k] = Math.max(max[k], flat[i + k]);
      }
    }
    return { min, max };
  }

  boxes.forEach((box, idx) => {
    const { positions, normals, indices } = buildBoxMesh(box.min, box.max);
    const posBvIdx = pushFloatArray(positions, 34962);
    const { min, max } = minMax3(positions);
    const posAccIdx = accessors.length;
    accessors.push({ bufferView: posBvIdx, componentType: 5126, count: positions.length / 3, type: 'VEC3', min, max });

    const normBvIdx = pushFloatArray(normals, 34962);
    const normAccIdx = accessors.length;
    accessors.push({ bufferView: normBvIdx, componentType: 5126, count: normals.length / 3, type: 'VEC3' });

    const idxBvIdx = pushIndexArray(indices);
    const idxAccIdx = accessors.length;
    accessors.push({ bufferView: idxBvIdx, componentType: 5123, count: indices.length, type: 'SCALAR' });

    materials.push({
      name: box.name + '-material',
      pbrMetallicRoughness: { baseColorFactor: box.color, roughnessFactor: 0.9, metallicFactor: 0.05 },
      doubleSided: true,
    });

    meshes.push({
      name: box.name,
      primitives: [{ attributes: { POSITION: posAccIdx, NORMAL: normAccIdx }, indices: idxAccIdx, material: idx, mode: 4 }],
    });

    nodes.push({ mesh: idx, name: box.name });
  });

  const binBuffer = Buffer.concat(buffers);
  const gltf = {
    asset: { version: '2.0', generator: 'walkthrough-sample-package-generator' },
    scene: 0,
    scenes: [{ nodes: nodes.map((_, i) => i) }],
    nodes,
    meshes,
    materials,
    accessors,
    bufferViews,
    buffers: [{ byteLength: binBuffer.length }],
  };

  const jsonBuf = Buffer.from(JSON.stringify(gltf), 'utf8');
  const jsonPad = (4 - (jsonBuf.length % 4)) % 4;
  const jsonChunkData = Buffer.concat([jsonBuf, Buffer.alloc(jsonPad, 0x20)]);

  const binPad = (4 - (binBuffer.length % 4)) % 4;
  const binChunkData = Buffer.concat([binBuffer, Buffer.alloc(binPad, 0x00)]);

  const jsonChunkHeader = Buffer.alloc(8);
  jsonChunkHeader.writeUInt32LE(jsonChunkData.length, 0);
  jsonChunkHeader.writeUInt32LE(0x4e4f534a, 4); // 'JSON'

  const binChunkHeader = Buffer.alloc(8);
  binChunkHeader.writeUInt32LE(binChunkData.length, 0);
  binChunkHeader.writeUInt32LE(0x004e4942, 4); // 'BIN\0'

  const totalLength = 12 + 8 + jsonChunkData.length + 8 + binChunkData.length;
  const header = Buffer.alloc(12);
  header.writeUInt32LE(0x46546c67, 0); // 'glTF'
  header.writeUInt32LE(2, 4);
  header.writeUInt32LE(totalLength, 8);

  return Buffer.concat([header, jsonChunkHeader, jsonChunkData, binChunkHeader, binChunkData]);
}

// =========================================================================
// Synthetic L-shaped corridor path + geometry
// =========================================================================

const LEG1_LEN = 8; // meters, along +x from (0,0)
const LEG2_LEN = 6; // meters, along +y from (8,0)
const TOTAL_LEN = LEG1_LEN + LEG2_LEN;
const EYE_HEIGHT = 1.6;
const SAMPLE_COUNT = 97;

function centerlinePointAtS(s) {
  if (s <= LEG1_LEN) return [s, 0, EYE_HEIGHT];
  return [LEG1_LEN, s - LEG1_LEN, EYE_HEIGHT];
}
function tangentAtS(s) {
  return s <= LEG1_LEN ? [1, 0, 0] : [0, 1, 0];
}

function buildSamples() {
  const samples = [];
  for (let i = 0; i < SAMPLE_COUNT; i++) {
    const t = i / (SAMPLE_COUNT - 1);
    const s = t * TOTAL_LEN;
    samples.push({ s, t, p: centerlinePointAtS(s), fwd: tangentAtS(s), up: [0, 0, 1] });
  }
  return samples;
}

// levelElevation matches plan.levelElevation (0 in this synthetic package) so
// eyeHeightAboveLevel == sample eye z here; kept as an explicit parameter
// (rather than hardcoding EYE_HEIGHT) so this stays correct if either changes.
const LEVEL_ELEVATION = 0;

function buildStations(samples) {
  const targets = [0, 0.2, 0.4, 0.6, 0.8, 1.0];
  return targets.map((t, i) => {
    let best = 0, bestDiff = Infinity;
    samples.forEach((s, idx) => {
      const d = Math.abs(s.t - t);
      if (d < bestDiff) { bestDiff = d; best = idx; }
    });
    const s = samples[best];
    return {
      name: 'WALK-' + String(i + 1).padStart(2, '0'),
      viewId: 100000 + i,
      sampleIndex: best,
      levelName: '1F',
      eye: s.p.slice(),
      fwd: s.fwd.slice(),
      up: s.up.slice(),
      eyeHeightAboveLevel: s.p[2] - LEVEL_ELEVATION,
      // fovYDeg intentionally null on the first two stations to exercise the
      // viewer's "fovY may be null -> show —" display rule (CONTRACT.md);
      // remaining stations get an arbitrary-but-deterministic synthetic value.
      fovYDeg: i < 2 ? null : 50 + i,
    };
  });
}

// =========================================================================
// --render-frames: headless-Chrome GLB renderer for the "base" variant
// =========================================================================

function fileUrl(p) {
  return 'file:///' + p.replace(/\\/g, '/').replace(/^\/*/, '');
}

function findDefaultChrome() {
  for (const p of DEFAULT_CHROME_CANDIDATES) {
    if (fs.existsSync(p)) return p;
  }
  return null;
}

function loadPuppeteer(puppeteerDir) {
  const dir = puppeteerDir || DEFAULT_PUPPETEER_DIR;
  const pkgJsonProbe = path.join(dir, 'package.json');
  if (!fs.existsSync(pkgJsonProbe) || !fs.existsSync(path.join(dir, 'node_modules', 'puppeteer-core'))) {
    throw new Error(
      '[render-frames] no node_modules/puppeteer-core found under ' + dir + '\n' +
      'Install it once (outside the repo), e.g.:\n' +
      '  npm init -y --prefix "' + dir + '"\n' +
      '  npm install puppeteer-core --prefix "' + dir + '"\n' +
      'then rerun with --puppeteer-dir "' + dir + '" (or use the default path above).'
    );
  }
  const req = createRequire(pkgJsonProbe);
  return req('puppeteer-core');
}

// Standalone offscreen render page: loads the REAL, committed vendor/three-bundle.js
// (not a re-implementation) so this exercises the exact artifact the viewer ships.
// No path line / station markers — "只要模型＋單色背景" per the review note; this
// is purely for validating the camera chain, not a feature of the viewer itself.
function buildRenderPageHtml(threeBundlePath) {
  return [
    '<!DOCTYPE html><html><head><meta charset="utf-8"></head>',
    '<body style="margin:0;background:#000;">',
    '<canvas id="rf-canvas"></canvas>',
    '<script src="' + fileUrl(threeBundlePath) + '"></script>',
    '<script>',
    '(function () {',
    '  var THREE = THREE_BUNDLE.THREE;',
    '  var GLTFLoader = THREE_BUNDLE.GLTFLoader;',
    '  var canvas = document.getElementById("rf-canvas");',
    '  var renderer = new THREE.WebGLRenderer({ canvas: canvas, antialias: true, preserveDrawingBuffer: true });',
    '  renderer.setClearColor(0x1a2230, 1);',
    '  var scene = new THREE.Scene();',
    '  scene.add(new THREE.AmbientLight(0xffffff, 0.6));',
    '  var dl = new THREE.DirectionalLight(0xffffff, 0.8);',
    '  dl.position.set(10, 20, 10);',
    '  scene.add(dl);',
    '  var camera = new THREE.PerspectiveCamera(50, 1, 0.1, 1000);',
    '  window.RF = {',
    '    init: function (glbBase64, w, h) {',
    '      renderer.setSize(w, h, false);',
    '      camera.aspect = w / h;',
    '      camera.updateProjectionMatrix();',
    '      if (!glbBase64) return Promise.resolve();',
    '      return new Promise(function (resolve, reject) {',
    '        var binary = atob(glbBase64);',
    '        var bytes = new Uint8Array(binary.length);',
    '        for (var i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);',
    '        new GLTFLoader().parse(bytes.buffer, "", function (gltf) {',
    '          scene.add(gltf.scene);',
    '          resolve();',
    '        }, reject);',
    '      });',
    '    },',
    '    render: function (px, py, pz, ux, uy, uz, lx, ly, lz, fovYDeg, aspect) {',
    '      camera.fov = fovYDeg;',
    '      camera.aspect = aspect;',
    '      camera.updateProjectionMatrix();',
    '      camera.position.set(px, py, pz);',
    '      camera.up.set(ux, uy, uz);',
    '      camera.lookAt(lx, ly, lz);',
    '      renderer.render(scene, camera);',
    '      return canvas.toDataURL("image/png");',
    '    },',
    '  };',
    '})();',
    '</script></body></html>',
  ].join('\n');
}

async function renderBaseFramesHeadless(opts) {
  const { samples, glbBase64, camera, frameW, frameH, outDir, chromePath, puppeteerDir } = opts;
  const puppeteer = loadPuppeteer(puppeteerDir);
  const chrome = chromePath || findDefaultChrome();
  if (!chrome) {
    throw new Error('[render-frames] no local Chrome/Edge found in the usual install paths; pass --chrome "<path to chrome.exe>"');
  }

  const tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'walkthrough-render-'));
  const pagePath = path.join(tmpDir, 'render.html');
  fs.writeFileSync(pagePath, buildRenderPageHtml(path.join(VIEWER_DIR, 'vendor', 'three-bundle.js')));

  console.log('[render-frames] launching', chrome);
  const browser = await puppeteer.launch({
    executablePath: chrome,
    headless: 'new',
    args: ['--use-gl=swiftshader', '--enable-webgl', '--ignore-gpu-blocklist', '--disable-gpu-sandbox', '--no-sandbox'],
  });
  try {
    const page = await browser.newPage();
    page.on('pageerror', (e) => console.error('[render-frames][pageerror]', e));
    await page.setViewport({ width: frameW, height: frameH });
    await page.goto(fileUrl(pagePath), { waitUntil: 'load' });
    await page.evaluate((glb, w, h) => window.RF.init(glb, w, h), glbBase64, frameW, frameH);

    const aspect = frameW / frameH;
    for (let i = 0; i < samples.length; i++) {
      // samples[i] IS poseAtT(samples, samples[i].t) at an exact sample point
      // (no interpolation needed), but we go through poseAtT anyway so this
      // exercises the same function the live viewer calls every frame.
      const pose = PM.poseAtT(samples, samples[i].t);
      const pThree = PM.toThree(pose.p);
      const upThree = PM.toThree(pose.up);
      const lookThree = PM.toThree(PM.add3(pose.p, pose.fwd));
      const dataUrl = await page.evaluate(
        (px, py, pz, ux, uy, uz, lx, ly, lz, fovYDeg, asp) =>
          window.RF.render(px, py, pz, ux, uy, uz, lx, ly, lz, fovYDeg, asp),
        pThree.x, pThree.y, pThree.z,
        upThree.x, upThree.y, upThree.z,
        lookThree.x, lookThree.y, lookThree.z,
        camera.fovYDeg, aspect
      );
      const buf = Buffer.from(dataUrl.split(',')[1], 'base64');
      fs.writeFileSync(path.join(outDir, 'frames', 'base', 'frame_' + String(i).padStart(4, '0') + '.png'), buf);
      if (i % 20 === 0) console.log('[render-frames] frame', i, '/', samples.length);
    }
    console.log('[render-frames] wrote', samples.length, 'GLB-rendered base frames (replacing the solid-color placeholders)');
  } finally {
    await browser.close();
    fs.rmSync(tmpDir, { recursive: true, force: true });
  }
}

// =========================================================================
// main
// =========================================================================

async function main() {
  const outDir = resolveOutDir();
  console.log('[make-sample-package] outDir =', outDir);
  fs.rmSync(outDir, { recursive: true, force: true });
  fs.mkdirSync(outDir, { recursive: true });
  fs.mkdirSync(path.join(outDir, 'frames', 'base'), { recursive: true });
  fs.mkdirSync(path.join(outDir, 'frames', 'night'), { recursive: true });
  fs.mkdirSync(path.join(outDir, 'vendor'), { recursive: true });

  const samples = buildSamples();
  const stations = buildStations(samples);

  // ---- model.glb (built early: --render-frames needs glbBase64 before frames) ----
  const glb = buildGlb([
    { name: 'floor', min: [-1, -1, -0.05], max: [9, 7, 0], color: [0.55, 0.55, 0.58, 1] },
    { name: 'wall-leg1', min: [-1, 0.9, 0], max: [9, 1.1, 3], color: [0.25, 0.5, 0.9, 1] },
    { name: 'wall-leg2', min: [8.9, -1, 0], max: [9.1, 7, 3], color: [0.25, 0.75, 0.4, 1] },
  ]);
  fs.writeFileSync(path.join(outDir, 'model.glb'), glb);
  const glbBase64 = glb.toString('base64');

  // ---- camera (built early: --render-frames needs fovYDeg before frames) ----
  const FRAME_W = 640, FRAME_H = 360;
  const aspect = FRAME_W / FRAME_H;
  const fovYDeg = 50;
  const fovYRad = (fovYDeg * Math.PI) / 180;
  const fovXDeg = (2 * Math.atan(Math.tan(fovYRad / 2) * aspect) * 180) / Math.PI;
  const cameraInfo = { fovYDeg, fovXDeg, aspect, near: 0.1, far: 1000, source: 'cropbox-derived', calibratedAt: null };

  // ---- frames ----
  for (let i = 0; i < SAMPLE_COUNT; i++) {
    const png = renderFramePNG(FRAME_W, FRAME_H, i, SAMPLE_COUNT, 'base');
    fs.writeFileSync(path.join(outDir, 'frames', 'base', 'frame_' + String(i).padStart(4, '0') + '.png'), png);
  }
  let nightCount = 0;
  for (let i = 0; i < SAMPLE_COUNT; i += 2) {
    const png = renderFramePNG(FRAME_W, FRAME_H, i, SAMPLE_COUNT, 'night');
    fs.writeFileSync(path.join(outDir, 'frames', 'night', 'frame_' + String(i).padStart(4, '0') + '.png'), png);
    nightCount++;
  }
  console.log('[make-sample-package] wrote', SAMPLE_COUNT, 'base frames,', nightCount, 'night frames (intentionally partial)');

  if (ARGS.renderFrames) {
    await renderBaseFramesHeadless({
      samples,
      glbBase64,
      camera: cameraInfo,
      frameW: FRAME_W,
      frameH: FRAME_H,
      outDir,
      chromePath: ARGS.chrome,
      puppeteerDir: ARGS.puppeteerDir,
    });
  }

  // ---- plan.png ----
  const planW = 880, planH = 720;
  const planModelToPixel = [80, 0, 100, 0, -80, 580];
  function m2p(x, y) { return PM.applyAffine(planModelToPixel, x, y); }
  const leg1Corners = [m2p(-1, -1), m2p(9, 1)];
  const leg2Corners = [m2p(7, -1), m2p(9, 7)];
  const planPng = makeSolidRegionsPNG(planW, planH, [30, 32, 38], [
    { x0: leg1Corners[0][0], y0: leg1Corners[1][1], x1: leg1Corners[1][0], y1: leg1Corners[0][1], color: [60, 90, 130] },
    { x0: leg2Corners[0][0], y0: leg2Corners[1][1], x1: leg2Corners[1][0], y1: leg2Corners[0][1], color: [60, 120, 80] },
  ]);
  fs.writeFileSync(path.join(outDir, 'plan.png'), planPng);

  // ---- section.png ----
  const sectionW = 450, sectionH = 600;
  const sectionUvToPixel = [150, 0, 225, 0, -150, 525];
  function uv2p(u, v) { return PM.applyAffine(sectionUvToPixel, u, v); }
  const corridorCorners = [uv2p(-1, 0), uv2p(1, 3)];
  const sectionPng = makeSolidRegionsPNG(sectionW, sectionH, [24, 26, 30], [
    { x0: corridorCorners[0][0], y0: corridorCorners[1][1], x1: corridorCorners[1][0], y1: corridorCorners[0][1], color: [110, 110, 120] },
  ]);
  fs.writeFileSync(path.join(outDir, 'section.png'), sectionPng);

  // ---- manifest ----
  const manifest = {
    schemaVersion: 1,
    generator: 'RevitMCP-SampleGenerator',
    createdAt: new Date().toISOString(),
    project: { name: 'Synthetic L-Corridor (sample package)', revitFile: '(none — synthetic)', revitVersion: 'n/a' },
    units: { length: 'm', angle: 'deg', axis: 'revit-z-up', originOffset: [0, 0, 0] },
    stations,
    // Simulates a Revit-side apply_camera_stations write that happened after
    // these frames were rendered — exercises the viewer's stale-frame notice
    // (see variants[].stale below and CONTRACT.md "Station editing").
    stationsAppliedAt: new Date(Date.now() - 5 * 60 * 1000).toISOString(),
    path: {
      sampleCount: SAMPLE_COUNT,
      totalLengthMeters: TOTAL_LEN,
      interpolation: 'catmull-rom-centripetal-arclength',
      samples,
    },
    camera: cameraInfo,
    frames: { count: SAMPLE_COUNT, width: FRAME_W, height: FRAME_H, fileNamePattern: 'frame_%04d.png' },
    variants: [
      { id: 'base', label: 'Revit', dir: 'frames/base', frameCount: SAMPLE_COUNT, complete: true },
      // stale:true simulates frames rendered BEFORE stationsAppliedAt above —
      // the viewer must show the "回 Revit 重出" notice for this variant.
      { id: 'night', label: 'Night', dir: 'frames/night', frameCount: nightCount, complete: false, stale: true },
    ],
    plan: { viewName: '1F Plan', image: 'plan.png', imageSize: [planW, planH], modelToPixel: planModelToPixel, levelElevation: 0 },
    section: {
      viewName: 'A-A',
      image: 'section.png',
      imageSize: [sectionW, sectionH],
      frame: { origin: [4, 0, 0], right: [0, 1, 0], up: [0, 0, 1], normal: [-1, 0, 0] },
      uvToPixel: sectionUvToPixel,
      depthRangeMeters: [-8, 8],
    },
    model: { glb: 'model.glb', yUpConverted: true, triangleCount: 36, materialCount: 3 },
    warnings: [
      '合成測試包（dev/make-sample-package.mjs 產生）— 非真實 Revit 匯出',
      'night 版本僅匯出一半影格，用於測試缺格 placeholder 顯示',
    ],
  };

  fs.writeFileSync(path.join(outDir, 'manifest.json'), JSON.stringify(manifest, null, 2));
  fs.writeFileSync(
    path.join(outDir, 'data.js'),
    'window.WALK_DATA = ' + JSON.stringify({ manifest, glbBase64 }) + ';\n'
  );

  // ---- copy viewer files ----
  const viewerFiles = ['index.html', 'style.css', 'app.js', 'pathmath.js', 'plan.js', 'section.js', 'frames.js', 'scene3d.js'];
  viewerFiles.forEach((f) => fs.copyFileSync(path.join(VIEWER_DIR, f), path.join(outDir, f)));
  fs.copyFileSync(path.join(VIEWER_DIR, 'vendor', 'three-bundle.js'), path.join(outDir, 'vendor', 'three-bundle.js'));

  console.log('[make-sample-package] done. glb size =', (glb.length / 1024).toFixed(1), 'KiB, base64 length =', glbBase64.length);
  console.log('[make-sample-package] package at', outDir);
}

main().catch((err) => {
  console.error('[make-sample-package] FAILED:', err);
  process.exit(1);
});
