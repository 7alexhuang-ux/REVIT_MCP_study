// Tests for pathmath.js using Node's built-in test runner.
// Run: node --test viewer/walkthrough/pathmath.test.cjs
'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const path = require('node:path');
const M = require(path.join(__dirname, 'pathmath.js'));

function makeStraightSamples(n) {
  // straight line along +x, 0..10m, uniform t
  const samples = [];
  for (let i = 0; i < n; i++) {
    const t = i / (n - 1);
    samples.push({
      s: t * 10,
      t: t,
      p: [t * 10, 0, 1.6],
      fwd: [1, 0, 0],
      up: [0, 0, 1],
    });
  }
  return samples;
}

function makeCornerSamples() {
  // L-shaped corridor: (0,0)->(5,0)->(5,5), uniformly arc-length sampled every
  // 0.25m (41 samples, matching how the real exporter densely samples a path so
  // a 90-degree turn only blends across one short segment, not the whole leg).
  // fwd[i] = normalized forward difference p[i+1]-p[i] (fwd[last] repeats fwd[last-1]).
  const pts = [];
  for (let i = 0; i <= 20; i++) pts.push([i * 0.25, 0, 1.6]); // leg 1: +x
  for (let i = 1; i <= 20; i++) pts.push([5, i * 0.25, 1.6]); // leg 2: +y
  const n = pts.length; // 41
  const samples = [];
  let s = 0;
  const sAt = [0];
  for (let i = 1; i < n; i++) {
    const d = M.length3(M.sub3(pts[i], pts[i - 1]));
    s += d;
    sAt.push(s);
  }
  const total = s;
  for (let i = 0; i < n; i++) {
    const fwdSrc = i < n - 1 ? M.normalize3(M.sub3(pts[i + 1], pts[i])) : null;
    samples.push({
      s: sAt[i],
      t: sAt[i] / total,
      p: pts[i],
      fwd: fwdSrc, // filled below (last one copies previous)
      up: [0, 0, 1],
    });
  }
  samples[n - 1].fwd = samples[n - 2].fwd;
  return samples;
}

test('frameIndexFromT: basic rounding and clamping', () => {
  assert.equal(M.frameIndexFromT(0, 97), 0);
  assert.equal(M.frameIndexFromT(1, 97), 96);
  assert.equal(M.frameIndexFromT(0.5, 97), 48);
  // clamps out-of-range t
  assert.equal(M.frameIndexFromT(-0.5, 97), 0);
  assert.equal(M.frameIndexFromT(1.5, 97), 96);
  // sampleCount=1 edge case
  assert.equal(M.frameIndexFromT(0.5, 1), 0);
});

test('poseAtT: endpoints clamp to first/last sample', () => {
  const samples = makeStraightSamples(11);
  const atStart = M.poseAtT(samples, -1);
  assert.deepEqual(atStart.p, samples[0].p);
  const atEnd = M.poseAtT(samples, 2);
  assert.deepEqual(atEnd.p, samples[10].p);
});

test('poseAtT: midpoint between two samples lerps position', () => {
  const samples = makeStraightSamples(11); // t steps of 0.1
  const pose = M.poseAtT(samples, 0.15); // halfway between sample 1 (t=0.1) and 2 (t=0.2)
  assert.ok(Math.abs(pose.p[0] - 1.5) < 1e-9, `expected x=1.5, got ${pose.p[0]}`);
  assert.ok(Math.abs(pose.s - 1.5) < 1e-9);
});

test('poseAtT: picks the correct enclosing segment on each side of a corner', () => {
  const samples = makeCornerSamples(); // corner sample is index 20, t=0.5, 0.25m spacing
  // deep inside leg 1 (well away from the corner segment) -> exactly +x, no drift
  const deepLeg1 = M.poseAtT(samples, 0.2);
  assert.deepEqual(deepLeg1.fwd, [1, 0, 0]);
  // deep inside leg 2 -> exactly +y
  const deepLeg2 = M.poseAtT(samples, 0.8);
  assert.deepEqual(deepLeg2.fwd, [0, 1, 0]);
  // exactly at the corner sample's own t: matches that sample's fwd exactly (clamped, not blended)
  const at = M.poseAtT(samples, 0.5);
  assert.deepEqual(at.fwd, samples[20].fwd);
  assert.deepEqual(at.fwd, [0, 1, 0]);
  // just before the corner sample, inside the single short segment that spans the
  // turn (samples[19]->samples[20]): interpolation blends the two directions, but
  // landing close to sample 19 keeps it dominantly +x.
  const justBefore = M.poseAtT(samples, samples[19].t + (samples[20].t - samples[19].t) * 0.1);
  assert.ok(justBefore.fwd[0] > justBefore.fwd[1], `expected still x-dominant, got ${JSON.stringify(justBefore.fwd)}`);
});

test('poseAtT: interpolated fwd/up stay unit length (nlerp)', () => {
  const samples = makeCornerSamples();
  for (const t of [0.3, 0.4, 0.45, 0.6, 0.7]) {
    const pose = M.poseAtT(samples, t);
    const len = M.length3(pose.fwd);
    assert.ok(Math.abs(len - 1) < 1e-9, `fwd not unit length at t=${t}: ${len}`);
  }
});

test('affine: forward/inverse round-trip within 1e-9', () => {
  // arbitrary non-trivial affine: rotation-ish + scale + translation
  const m = [12.3, -0.7, 150.2, 0.4, 11.9, -30.1];
  const inv = M.invertAffine(m);
  const samplesXY = [
    [0, 0],
    [10, 5],
    [-3.5, 7.25],
    [100, -50],
  ];
  for (const [x, y] of samplesXY) {
    const [px, py] = M.applyAffine(m, x, y);
    const [x2, y2] = M.applyAffine(inv, px, py);
    assert.ok(Math.abs(x2 - x) < 1e-9, `x round-trip error: ${x} -> ${x2}`);
    assert.ok(Math.abs(y2 - y) < 1e-9, `y round-trip error: ${y} -> ${y2}`);
  }
});

test('invertAffine throws on singular matrix', () => {
  assert.throws(() => M.invertAffine([1, 2, 0, 2, 4, 0]));
});

test('tFromPlanPoint: point on the path returns matching t', () => {
  const samples = makeStraightSamples(11);
  const t = M.tFromPlanPoint(samples, 5, 0); // exactly at x=5 -> t=0.5
  assert.ok(Math.abs(t - 0.5) < 1e-9, `expected t=0.5, got ${t}`);
});

test('tFromPlanPoint: point off to the side snaps to nearest segment, not averaged', () => {
  const samples = makeCornerSamples();
  // point near (5, 1) is close to the vertical segment (5,0)->(5,5); should map to t just past 0.5
  const t = M.tFromPlanPoint(samples, 6, 1);
  assert.ok(t > 0.5 && t < 0.75, `expected t in (0.5, 0.75), got ${t}`);
});

test('tFromPlanPoint: clamps to path ends for points beyond the endpoints', () => {
  const samples = makeStraightSamples(11);
  assert.ok(Math.abs(M.tFromPlanPoint(samples, -100, 0) - 0) < 1e-9);
  assert.ok(Math.abs(M.tFromPlanPoint(samples, 100, 0) - 1) < 1e-9);
});

test('projectToSectionFrame: sign of depth matches normal direction', () => {
  const frame = {
    origin: [0, 0, 0],
    right: [1, 0, 0],
    up: [0, 0, 1],
    normal: [0, -1, 0], // normal points toward -y (viewer stands at -y looking +y)
  };
  const inFront = M.projectToSectionFrame([2, -3, 1], frame); // -y side -> positive depth
  assert.ok(inFront.depth > 0, `expected positive depth, got ${inFront.depth}`);
  const behind = M.projectToSectionFrame([2, 3, 1], frame); // +y side -> negative depth
  assert.ok(behind.depth < 0, `expected negative depth, got ${behind.depth}`);
  assert.ok(Math.abs(inFront.u - 2) < 1e-9);
  assert.ok(Math.abs(inFront.v - 1) < 1e-9);
});

test('toThree: axis swap matches CONTRACT.md (x, z, -y)', () => {
  const v = M.toThree([1, 2, 3]);
  assert.deepEqual(v, { x: 1, y: 3, z: -2 });
});

test('headingDegFromFwd: cardinal directions (0=north/+Y, 90=east/+X, clockwise)', () => {
  assert.ok(Math.abs(M.headingDegFromFwd([0, 1, 0]) - 0) < 1e-9);
  assert.ok(Math.abs(M.headingDegFromFwd([1, 0, 0]) - 90) < 1e-9);
  assert.ok(Math.abs(M.headingDegFromFwd([0, -1, 0]) - 180) < 1e-9);
  assert.ok(Math.abs(M.headingDegFromFwd([-1, 0, 0]) - 270) < 1e-9);
});

test('headingDegFromFwd: ignores the vertical (z) component', () => {
  const flat = M.headingDegFromFwd([1, 0, 0]);
  const tilted = M.headingDegFromFwd([0.7, 0, 0.7]);
  assert.ok(Math.abs(flat - tilted) < 1e-9);
});

test('fwdFromHeadingDeg / headingDegFromFwd round-trip within 1e-9', () => {
  for (const deg of [0, 45, 90, 135, 180, 225, 270, 315, 359.5]) {
    const fwd = M.fwdFromHeadingDeg(deg, [0, 1, 0]);
    assert.ok(Math.abs(M.length3(fwd) - 1) < 1e-9, `not unit length at ${deg}`);
    const back = M.headingDegFromFwd(fwd);
    const diff = Math.min(Math.abs(back - deg), Math.abs(back - deg + 360), Math.abs(back - deg - 360));
    assert.ok(diff < 1e-9, `round-trip failed: ${deg} -> fwd -> ${back}`);
  }
});

test('fwdFromHeadingDeg: preserves pitch of the reference vector', () => {
  const pitchedRef = M.normalize3([0, 1, 1]); // 45 degree upward tilt
  const fwd = M.fwdFromHeadingDeg(90, pitchedRef);
  assert.ok(Math.abs(fwd[2] - pitchedRef[2]) < 1e-9, `pitch not preserved: ${JSON.stringify(fwd)}`);
  assert.ok(Math.abs(M.length3(fwd) - 1) < 1e-9);
});

test('orthonormalizeUp: returns unit vector orthogonal to fwd, close to hint', () => {
  const fwd = [1, 0, 0];
  const up = M.orthonormalizeUp(fwd, [0, 0, 1]);
  assert.ok(Math.abs(M.length3(up) - 1) < 1e-9);
  assert.ok(Math.abs(M.dot3(up, M.normalize3(fwd))) < 1e-9);
  assert.deepEqual(up.map((v) => Number(v.toFixed(9))), [0, 0, 1]);
});

test('orthonormalizeUp: falls back to a world axis when upHint is parallel to fwd', () => {
  const fwd = [0, 0, 1];
  const up = M.orthonormalizeUp(fwd, [0, 0, 5]); // upHint parallel to fwd -> degenerate
  assert.ok(Math.abs(M.length3(up) - 1) < 1e-9);
  assert.ok(Math.abs(M.dot3(up, M.normalize3(fwd))) < 1e-9);
});

test('orthonormalizeUp: never returns a zero-length vector for arbitrary fwd/upHint pairs', () => {
  const pairs = [
    [[1, 0, 0], [1, 0, 0]],
    [[0, 1, 0], [0, -1, 0]],
    [[0.6, 0.8, 0], [0.6, 0.8, 0]],
  ];
  for (const [fwd, hint] of pairs) {
    const up = M.orthonormalizeUp(fwd, hint);
    assert.ok(M.length3(up) > 0.99 && M.length3(up) < 1.01);
  }
});

test('eyeZFromHeight: anchors to the original level elevation', () => {
  // original eye z=1.6, 1.6m above a level at z=0 -> new height 1.2 -> z=1.2
  assert.ok(Math.abs(M.eyeZFromHeight(1.6, 1.6, 1.2) - 1.2) < 1e-9);
  // level at z=10 (eye z=11.6, 1.6m above it) -> new height 2.0 -> z=12.0
  assert.ok(Math.abs(M.eyeZFromHeight(11.6, 1.6, 2.0) - 12.0) < 1e-9);
});
