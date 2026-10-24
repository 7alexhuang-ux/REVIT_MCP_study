// pathmath.js — pure math helpers shared by all walkthrough viewer panes.
// UMD: exposes `window.WalkPathMath` in browsers (classic <script>, no bundler),
// and `module.exports` under Node (used directly by pathmath.test.cjs).
// No DOM, no THREE.js dependency — keeps this file testable head-less and reusable
// from the sample-package generator (dev/make-sample-package.mjs) too.
(function (root, factory) {
  var mod = factory();
  if (typeof module === 'object' && module.exports) {
    module.exports = mod;
  }
  if (root) {
    root.WalkPathMath = mod;
  }
})(typeof self !== 'undefined' ? self : this, function () {
  'use strict';

  // ---- vec3 helpers -------------------------------------------------------

  function sub3(a, b) {
    return [a[0] - b[0], a[1] - b[1], a[2] - b[2]];
  }
  function add3(a, b) {
    return [a[0] + b[0], a[1] + b[1], a[2] + b[2]];
  }
  function scale3(a, s) {
    return [a[0] * s, a[1] * s, a[2] * s];
  }
  function dot3(a, b) {
    return a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
  }
  function length3(a) {
    return Math.sqrt(dot3(a, a));
  }
  function normalize3(a) {
    var len = length3(a);
    if (len < 1e-12) return [0, 0, 0];
    return [a[0] / len, a[1] / len, a[2] / len];
  }
  function lerp3(a, b, u) {
    return [a[0] + (b[0] - a[0]) * u, a[1] + (b[1] - a[1]) * u, a[2] + (b[2] - a[2]) * u];
  }
  function nlerp3(a, b, u) {
    return normalize3(lerp3(a, b, u));
  }
  function lerp(a, b, u) {
    return a + (b - a) * u;
  }
  function clamp(v, lo, hi) {
    return Math.max(lo, Math.min(hi, v));
  }

  // ---- affine 2D (plan / section pixel <-> model/uv mapping) -------------
  // Forward: px = a*x + b*y + tx ; py = c*x + d*y + ty   ==  m = [a,b,tx,c,d,ty]

  function applyAffine(m, x, y) {
    return [m[0] * x + m[1] * y + m[2], m[3] * x + m[4] * y + m[5]];
  }

  function invertAffine(m) {
    var a = m[0], b = m[1], tx = m[2], c = m[3], d = m[4], ty = m[5];
    var det = a * d - b * c;
    if (Math.abs(det) < 1e-15) {
      throw new Error('WalkPathMath.invertAffine: singular matrix (det=0)');
    }
    var ai = d / det;
    var bi = -b / det;
    var ci = -c / det;
    var di = a / det;
    var txi = (-d * tx + b * ty) / det;
    var tyi = (c * tx - a * ty) / det;
    return [ai, bi, txi, ci, di, tyi];
  }

  // ---- path sampling -------------------------------------------------------

  function frameIndexFromT(t, sampleCount) {
    if (sampleCount <= 1) return 0;
    var tc = clamp(t, 0, 1);
    return clamp(Math.round(tc * (sampleCount - 1)), 0, sampleCount - 1);
  }

  // Find the sample-index i such that samples[i].t <= t <= samples[i+1].t.
  // Returns -1 if t is before samples[0].t, samples.length-1 if at/after the end.
  function findSegment(samples, t) {
    var n = samples.length;
    if (n === 0) return -1;
    if (t <= samples[0].t) return -1;
    if (t >= samples[n - 1].t) return n - 1;
    // linear scan is fine (path sample counts are in the low hundreds); binary
    // search would be premature optimization here.
    for (var i = 0; i < n - 1; i++) {
      if (t >= samples[i].t && t <= samples[i + 1].t) return i;
    }
    return n - 1;
  }

  function poseAtT(samples, t) {
    if (!samples || samples.length === 0) {
      throw new Error('WalkPathMath.poseAtT: empty samples');
    }
    var tc = clamp(t, 0, 1);
    var n = samples.length;
    if (n === 1) {
      var only = samples[0];
      return { p: only.p.slice(), fwd: only.fwd.slice(), up: only.up.slice(), s: only.s, t: tc };
    }
    var seg = findSegment(samples, tc);
    if (seg === -1) {
      var first = samples[0];
      return { p: first.p.slice(), fwd: first.fwd.slice(), up: first.up.slice(), s: first.s, t: tc };
    }
    if (seg === n - 1) {
      var last = samples[n - 1];
      return { p: last.p.slice(), fwd: last.fwd.slice(), up: last.up.slice(), s: last.s, t: tc };
    }
    var a = samples[seg];
    var b = samples[seg + 1];
    var span = b.t - a.t;
    var u = span > 1e-12 ? (tc - a.t) / span : 0;
    u = clamp(u, 0, 1);
    return {
      p: lerp3(a.p, b.p, u),
      fwd: nlerp3(a.fwd, b.fwd, u),
      up: nlerp3(a.up, b.up, u),
      s: lerp(a.s, b.s, u),
      t: tc,
    };
  }

  // Project a plan-space point (x,y in model coordinates, z ignored) onto the
  // path polyline (samples[].p, xy only) and return the t of the nearest point.
  function tFromPlanPoint(samples, x, y) {
    if (!samples || samples.length === 0) return 0;
    if (samples.length === 1) return samples[0].t;
    var bestDistSq = Infinity;
    var bestT = samples[0].t;
    for (var i = 0; i < samples.length - 1; i++) {
      var ax = samples[i].p[0], ay = samples[i].p[1];
      var bx = samples[i + 1].p[0], by = samples[i + 1].p[1];
      var dx = bx - ax, dy = by - ay;
      var lenSq = dx * dx + dy * dy;
      var u;
      if (lenSq < 1e-12) {
        u = 0;
      } else {
        u = ((x - ax) * dx + (y - ay) * dy) / lenSq;
        u = clamp(u, 0, 1);
      }
      var px = ax + dx * u, py = ay + dy * u;
      var ddx = x - px, ddy = y - py;
      var distSq = ddx * ddx + ddy * ddy;
      if (distSq < bestDistSq) {
        bestDistSq = distSq;
        bestT = lerp(samples[i].t, samples[i + 1].t, u);
      }
    }
    return bestT;
  }

  // Section frame: { origin:[x,y,z], right:[x,y,z], up:[x,y,z], normal:[x,y,z] }
  // u = dot(p-origin, right), v = dot(p-origin, up), depth = dot(p-origin, normal).
  function projectToSectionFrame(p, frame) {
    var rel = sub3(p, frame.origin);
    return {
      u: dot3(rel, frame.right),
      v: dot3(rel, frame.up),
      depth: dot3(rel, frame.normal),
    };
  }

  // three.js is the only place the project swaps axes: model (Revit, Z-up)
  // -> three.js (Y-up). toThree([x,y,z]) = new THREE.Vector3(x, z, -y).
  // Returned as a plain {x,y,z} object so this file stays THREE-free/testable.
  function toThree(p) {
    return { x: p[0], y: p[2], z: -p[1] };
  }

  // ---- station-editing helpers (walkthrough station panel) ----------------

  // Compass-style heading in degrees, horizontal bearing only (ignores fwd's
  // vertical/z component): 0 = north (+Y), 90 = east (+X), measured clockwise,
  // range [0, 360). Chosen because it reads naturally to an architect (compass
  // bearing) and because Revit's plan north is +Y in this project's convention.
  function headingDegFromFwd(fwd) {
    var deg = (Math.atan2(fwd[0], fwd[1]) * 180) / Math.PI;
    return (deg + 360) % 360;
  }

  // Inverse of headingDegFromFwd: rebuilds a unit forward vector at the given
  // compass heading (any real degree value; trig is periodic so out-of-range
  // input is harmless), preserving `fwdForPitch`'s original vertical tilt.
  function fwdFromHeadingDeg(headingDeg, fwdForPitch) {
    var pitch = fwdForPitch ? Math.asin(clamp(fwdForPitch[2], -1, 1)) : 0;
    var horiz = Math.cos(pitch);
    var rad = (headingDeg * Math.PI) / 180;
    return [Math.sin(rad) * horiz, Math.cos(rad) * horiz, Math.sin(pitch)];
  }

  // Gram-Schmidt: returns a unit `up` orthogonal to unit-ish `fwd`, as close as
  // possible to `upHint`. Falls back to a world axis when upHint is (near-)
  // parallel to fwd (degenerate input), so this never returns a zero vector.
  function orthonormalizeUp(fwd, upHint) {
    var f = normalize3(fwd);
    var h = upHint && length3(upHint) > 1e-9 ? normalize3(upHint) : [0, 0, 1];
    var proj = dot3(h, f);
    var rest = sub3(h, scale3(f, proj));
    if (length3(rest) < 1e-6) {
      var fallback = Math.abs(f[2]) < 0.9 ? [0, 0, 1] : [0, 1, 0];
      proj = dot3(fallback, f);
      rest = sub3(fallback, scale3(f, proj));
    }
    return normalize3(rest);
  }

  // Recomputes an eye's z from a new eye-height-above-level, anchored to the
  // level implied by (originalEyeZ - originalEyeHeightAboveLevel) — i.e. the
  // level elevation itself never moves, only the height above it.
  function eyeZFromHeight(originalEyeZ, originalEyeHeightAboveLevel, newEyeHeightAboveLevel) {
    var levelZ = originalEyeZ - originalEyeHeightAboveLevel;
    return levelZ + newEyeHeightAboveLevel;
  }

  return {
    // vec3
    sub3: sub3,
    add3: add3,
    scale3: scale3,
    dot3: dot3,
    length3: length3,
    normalize3: normalize3,
    lerp3: lerp3,
    nlerp3: nlerp3,
    lerp: lerp,
    clamp: clamp,
    // affine
    applyAffine: applyAffine,
    invertAffine: invertAffine,
    // path
    frameIndexFromT: frameIndexFromT,
    poseAtT: poseAtT,
    tFromPlanPoint: tFromPlanPoint,
    projectToSectionFrame: projectToSectionFrame,
    toThree: toThree,
    // station editing
    headingDegFromFwd: headingDegFromFwd,
    fwdFromHeadingDeg: fwdFromHeadingDeg,
    orthonormalizeUp: orthonormalizeUp,
    eyeZFromHeight: eyeZFromHeight,
  };
});
