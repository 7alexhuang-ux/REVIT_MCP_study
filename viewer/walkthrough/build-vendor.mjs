#!/usr/bin/env node
// Rebuilds viewer/walkthrough/vendor/three-bundle.js from npm `three`.
//
// Why this script exists: the walkthrough viewer must run from a double-clicked
// index.html over file:// with zero network access and zero <script type="module">
// (Chrome blocks ES module scripts on file://). So we take the npm `three` package,
// plus its GLTFLoader and OrbitControls examples, and bundle them with esbuild into
// one classic IIFE script that assigns `window.THREE_BUNDLE = { THREE, GLTFLoader, OrbitControls }`.
// The bundled file is committed to the repo (vendor/three-bundle.js); node_modules is not.
//
// Usage:
//   node build-vendor.mjs [--workdir <dir>] [--three-version <semver>]
//
// By default this npm-installs `three` + `esbuild` into a throwaway directory under
// the OS temp folder (never inside the repo), bundles, and writes the result to
// viewer/walkthrough/vendor/three-bundle.js. Pass --workdir to reuse a cache dir
// across rebuilds (e.g. the Claude scratchpad) and speed up repeat runs.
//
// Pinned version: see THREE_VERSION below. Bump deliberately and re-run this script
// (do not hand-edit vendor/three-bundle.js).

import { execFileSync } from 'node:child_process';
import { mkdtempSync, mkdirSync, writeFileSync, readFileSync, statSync, existsSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const THREE_VERSION = '0.170.0'; // pinned; bump deliberately

function parseArgs(argv) {
  const out = { workdir: null, threeVersion: THREE_VERSION };
  for (let i = 0; i < argv.length; i++) {
    if (argv[i] === '--workdir') out.workdir = argv[++i];
    else if (argv[i] === '--three-version') out.threeVersion = argv[++i];
  }
  return out;
}

async function main() {
  const args = parseArgs(process.argv.slice(2));
  const workdir = args.workdir
    ? path.resolve(args.workdir)
    : mkdtempSync(path.join(tmpdir(), 'walkthrough-vendor-'));
  mkdirSync(workdir, { recursive: true });
  console.log(`[build-vendor] workdir: ${workdir}`);

  const pkgPath = path.join(workdir, 'package.json');
  if (!existsSync(pkgPath)) {
    writeFileSync(pkgPath, JSON.stringify({ name: 'walkthrough-vendor-build', private: true }, null, 2));
  }

  const installedThreePkg = path.join(workdir, 'node_modules', 'three', 'package.json');
  const needsInstall = !existsSync(installedThreePkg)
    || JSON.parse(readFileSync(installedThreePkg, 'utf8')).version !== args.threeVersion;

  if (needsInstall) {
    console.log(`[build-vendor] npm install three@${args.threeVersion} esbuild (this may take a minute)...`);
    execFileSync('npm', ['install', '--no-save', `three@${args.threeVersion}`, 'esbuild@latest'], {
      cwd: workdir,
      stdio: 'inherit',
      shell: process.platform === 'win32',
    });
  } else {
    console.log('[build-vendor] three + esbuild already present in workdir, skipping install.');
  }

  const entryPath = path.join(workdir, 'entry.mjs');
  writeFileSync(
    entryPath,
    [
      "import * as THREE from 'three';",
      "import { GLTFLoader } from 'three/examples/jsm/loaders/GLTFLoader.js';",
      "import { OrbitControls } from 'three/examples/jsm/controls/OrbitControls.js';",
      'export { THREE, GLTFLoader, OrbitControls };',
      '',
    ].join('\n')
  );

  const esbuildPkg = JSON.parse(
    readFileSync(path.join(workdir, 'node_modules', 'esbuild', 'package.json'), 'utf8')
  );
  console.log(`[build-vendor] esbuild ${esbuildPkg.version}`);

  const outFile = path.join(__dirname, 'vendor', 'three-bundle.js');
  mkdirSync(path.dirname(outFile), { recursive: true });

  // Invoke esbuild's JS API from within workdir's node_modules so resolution works
  // without touching the repo's node_modules (there isn't one).
  const buildScript = path.join(workdir, '__do_build.mjs');
  writeFileSync(
    buildScript,
    [
      "import esbuild from 'esbuild';",
      `const result = await esbuild.build({`,
      `  entryPoints: [${JSON.stringify(entryPath)}],`,
      `  bundle: true,`,
      `  minify: true,`,
      `  format: 'iife',`,
      `  globalName: 'THREE_BUNDLE',`,
      `  target: ['chrome110'],`,
      `  outfile: ${JSON.stringify(outFile)},`,
      `  legalComments: 'none',`,
      `});`,
      `console.log('[build-vendor] wrote', ${JSON.stringify(outFile)});`,
    ].join('\n')
  );
  execFileSync('node', [buildScript], { cwd: workdir, stdio: 'inherit' });

  const stat = statSync(outFile);
  console.log(`[build-vendor] done: ${outFile} (${(stat.size / 1024).toFixed(1)} KiB), three@${args.threeVersion}`);
}

main().catch((err) => {
  console.error('[build-vendor] FAILED:', err);
  process.exit(1);
});
