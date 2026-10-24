# Walkthrough Package Contract (schemaVersion 1)

Audience: developers and AI agents. Canonical data contract between the Revit add-in exporter
(`MCP/Core/Commands/CommandExecutor.Walkthrough.cs`) and the offline viewer in this folder.
Human-facing workflow lives in `domain/walkthrough-package.md`.

## Package folder layout

```text
WalkPackage_YYYYMMDD_HHMM/
  index.html  app.js  pathmath.js  plan.js  section.js  frames.js  scene3d.js  style.css
  vendor/three-bundle.js      three.js + GLTFLoader + OrbitControls, IIFE, global THREE_BUNDLE
  data.js                     window.WALK_DATA = { manifest: {...}, glbBase64: "..." | null };
  manifest.json               same manifest object as data.js (human / tool readable)
  model.glb                   optional, same bytes as glbBase64
  plan.png                    optional
  section.png                 optional
  frames/<variantId>/frame_0000.png ... frame_NNNN.png
```

The viewer reads ONLY `window.WALK_DATA` (loaded by a plain `<script src="data.js">`) and image files
via `<img>` / `Image()`. It never uses `fetch`/XHR, so it works when opened from `file://`.
No ES modules (`type="module"` is blocked on `file://` in Chrome); every viewer script is a classic script.

## Coordinate conventions

- Length unit: meters. Angles: degrees.
- "Model coordinates" = Revit internal coordinates (feet) x 0.3048, minus `units.originOffset`. Z is up.
- three.js conversion (the only axis swap in the project): `toThree([x, y, z]) = new Vector3(x, z, -y)`.
- `model.glb` vertices are already in three.js space (Y-up) with the same origin offset applied,
  so the GLB scene is added to the three.js scene with an identity transform.

## manifest object

```jsonc
{
  "schemaVersion": 1,
  "generator": "RevitMCP",
  "createdAt": "2026-09-16T10:00:00+08:00",   // ISO 8601; also the localStorage key seed
  "project": { "name": "string", "revitFile": "string", "revitVersion": "2024" },
  "units": { "length": "m", "angle": "deg", "axis": "revit-z-up", "originOffset": [0, 0, 0] },

  "stations": [
    {
      "name": "WALK-01", "viewId": 123456, "sampleIndex": 0, "levelName": "1F",
      "eye": [x, y, z],           // model coordinates, same convention as path.samples[].p
      "fwd": [x, y, z],           // unit forward direction
      "up": [x, y, z],            // unit up direction, orthogonal to fwd
      "eyeHeightAboveLevel": 1.6, // meters, eye.z minus this station's level elevation
      "fovYDeg": 50.0             // vertical FOV for this station specifically, or null if unknown
    }
  ],

  // Present only after a Revit-side apply_camera_stations write; absent otherwise.
  // Together with variants[].stale (below) this is what tells the viewer the
  // currently-loaded frames were rendered from an OLDER set of station poses.
  "stationsAppliedAt": "2026-09-20T00:07:31.146Z",

  "path": {
    "sampleCount": 97,                       // == frames.count; frame i shows samples[i]
    "totalLengthMeters": 42.5,
    "interpolation": "catmull-rom-centripetal-arclength",
    "samples": [
      { "s": 0.0,      // arc length from start, meters
        "t": 0.0,      // s / totalLengthMeters, 0..1 (equals i / (sampleCount-1))
        "p": [x, y, z],        // eye position, model coordinates
        "fwd": [x, y, z],      // unit forward direction
        "up": [x, y, z] }      // unit up direction, orthogonal to fwd
    ]
  },

  "camera": {
    "fovYDeg": 50.0,          // vertical field of view of the exported frames
    "fovXDeg": 80.0,
    "aspect": 1.7778,         // width / height of the exported frames
    "near": 0.1, "far": 1000,
    "source": "cropbox-derived",   // or "calibrated" / "default"
    "calibratedAt": null
  },

  "frames": { "count": 97, "width": 1280, "height": 720, "fileNamePattern": "frame_%04d.png",
              "renderViewName": "WalkRender_20260916_101500" },   // exporter-internal temp view name

  "variants": [
    {
      "id": "base", "label": "Revit", "dir": "frames/base", "frameCount": 97, "complete": false,
      "stale": false   // optional, default false; true means these frames predate the last
                       // apply_camera_stations write (stationsAppliedAt) and no longer match
                       // the stations above — see "Station editing" below
    }
  ],

  "plan": {                              // or null
    "viewName": "1F", "image": "plan.png", "imageSize": [2000, 1400],
    "modelToPixel": [a, b, tx, c, d, ty],   // px = a*x + b*y + tx ; py = c*x + d*y + ty (model x,y)
    "levelElevation": 0.0                   // model z of the plan level
  },

  "section": {                           // or null
    "viewName": "A-A", "image": "section.png", "imageSize": [2000, 900],
    "frame": { "origin": [x,y,z], "right": [x,y,z], "up": [x,y,z], "normal": [x,y,z] },
    // u = dot(p - origin, right), v = dot(p - origin, up), depth = dot(p - origin, normal)
    // normal points toward the viewer of the section (Revit CropBox BasisZ).
    "uvToPixel": [a, b, tx, c, d, ty],      // px = a*u + b*v + tx ; py = c*u + d*v + ty
    "depthRangeMeters": [-10.0, 0.0]        // section far-clip range along normal
  },

  "model": {                             // or null when no GLB exported yet
    "glb": "model.glb", "yUpConverted": true, "triangleCount": 0, "materialCount": 0,
    "sourceViewName": "WALK-01",
    "embedded": true          // true when data.js carries glbBase64 (GLB <= 150 MB)
  },

  // Present only after bridge/node/walkthrough/render-depth.mjs has run against this
  // package at least once; absent otherwise. Per-frame depth maps rendered OFFLINE
  // from model.glb + path.samples (headless Chrome, GPU), not from Revit — see
  // "Depth maps" below.
  "depth": {
    "dir": "depth",                      // depth/<fileNamePattern>, same index-per-file as frames
    "encoding": "gray8",                 // or "gray16" (16-bit grayscale PNG, R=hi byte/G=lo byte packed
                                          // during render, reassembled into one 16-bit sample per pixel)
    "nearMeters": 0.1,                   // normalization range floor (meters, view-space z along camera axis)
    "farMeters": 8.0,                    // normalization range ceiling; auto = 99th-percentile depth over
                                          // the WHOLE path (ceil'd), or reused from a prior run of this
                                          // same package so repeated partial re-renders stay comparable
    "width": 1280, "height": 720,        // depth image size; defaults to (and normally EQUALS) frames.width/
                                          // height exactly — only differs when render-depth.mjs's --width flag
                                          // explicitly overrides it (aspect-derived height in that case)
    "count": 97,                         // depth files currently present on disk (0..sampleCount)
    "convention": "near-white-linear-viewz", // near = white/high value, far = black/low value; distance is
                                          // VIEW-SPACE Z (distance along the camera's look axis), not
                                          // euclidean distance-to-point; no geometry hit = black
    "generatedAt": "2026-09-20T14:27:55.858Z", // ISO 8601, last render-depth.mjs run that touched this package
    "source": "glb-three",               // always "glb-three" — never a Revit-side render
    "normalsDir": "normals"              // or null; present only when --also-normals was used at least once
  },

  "warnings": ["string"]
}
```

## Depth maps

`bridge/node/walkthrough/render-depth.mjs` renders a linear depth map for every sample in
`path.samples`, entirely offline: it loads `model.glb` and the exported camera poses (the SAME
`toThree` conversion and coordinate conventions as everywhere else in this contract — see
"Coordinate conventions" above) into a headless-Chrome three.js scene and reads back a per-pixel
distance. No Revit connection or round-trip is involved; the tool only needs the package's own
`model.glb` and `manifest.json`.

- Depth value = distance from the camera to the surface **along the camera's forward (view-space
  z) axis**, in meters — not straight-line euclidean distance to the point.
- Encoded as grayscale, **near = white (high value), far = black (low value)** — the common
  MiDaS/ControlNet convention. Pixels with no geometry hit (sky, open space) are black.
- The visible range is `[nearMeters, farMeters]`; `farMeters` is picked automatically (99th
  percentile of all in-range depth samples across the whole path, rounded up) unless a `depth`
  block already exists for this package, in which case that same range is reused so a partial
  re-render (`--start`/`--overwrite`) stays visually consistent with frames rendered earlier.
- `depth/` files follow the same `frames.fileNamePattern` and per-sample indexing as the `frames/*`
  variants (`depth/frame_0000.png` == `path.samples[0]`, etc.), but are their own top-level folder,
  not a `variants[]` entry — they are not a video-frame variant and the viewer does not display them.
- Optional `normals/` (via `--also-normals`) holds the matching world-space normal (RGB = normal *
  0.5 + 0.5), same indexing, for future ControlNet/VACE conditioning beyond depth.
- **Depth maps share the frames' lifecycle, not the video model's**: they only need to be
  regenerated when the exported camera path or the GLB geometry changes (a new `apply_camera_stations`
  write, or a re-export of `model.glb`). Re-styling the walkthrough into a different look with the
  video model does **not** require re-running `render-depth.mjs` — the same depth/normal maps are
  reused as the structural condition for every style pass.

## Viewer state rules

- Single source of truth: `t` in [0, 1] (normalized arc length).
- `frameIndex = round(t * (sampleCount - 1))`. Camera pose for rendering interpolates between the two
  neighbouring samples (lerp position, nlerp directions) so the 3D pane is smooth while frames snap.
- Plan marker drag writes `t` by projecting the pointer (converted to model x,y via the inverse of
  `modelToPixel`) onto the path polyline of `samples[].p` (x,y only), nearest segment wins.
- Section pane is read-only in schemaVersion 1.
- A variant is displayable when its frame files exist; missing frames show a placeholder, never an error.
- Calibration overrides (`fovYDeg`) are stored in `localStorage` under `walk-calib:<createdAt>` and
  applied on load; the viewer can also download a patched `manifest.json` / `data.js`.

## Station editing

The exported frames are pre-rendered still images. Adjusting a station in the viewer (dragging its
plan marker, or moving its eye-height / heading sliders) **never** changes those frames — it can only
produce a *preview* (the plan marker's new position, and the 3D pane's camera when that station is
selected in edit mode) plus a `stations.json` file to hand back to Revit's `apply_camera_stations`
command, which re-renders the real frames from the new poses. Until that round-trip happens, the
frames on screen are stale relative to the edited stations.

- Toggling edit mode (`E` key, or the "編輯站點" toolbar button) surfaces the station panel and lets the
  currently-selected station's plan marker be dragged (XY only) and its eye height / heading be
  adjusted via the panel's sliders/number inputs. Batch operations (uniform eye height, +/-0.05 m nudge,
  reset one/all) apply to every station in `stations[]`.
- Edits are kept in memory as `{ name -> { eye, fwd, up } }` (only entries that actually differ from the
  manifest are kept) and persisted to `localStorage` under `walk-edit:<createdAt>` so a page reload does
  not lose them. They are never written into `manifest.json`/`data.js` themselves.
- Heading is displayed/edited as a compass-style bearing in degrees: 0° = north (+Y), 90° = east (+X),
  measured clockwise (`WalkPathMath.headingDegFromFwd` / `fwdFromHeadingDeg`). Editing heading rebuilds
  `fwd` at the new bearing (preserving the original vector's vertical tilt) and re-orthonormalizes `up`
  against it (`WalkPathMath.orthonormalizeUp`) so `up` stays a unit vector perpendicular to `fwd`.
  Editing eye height keeps the station's level elevation fixed (`eye.z - eyeHeightAboveLevel`) and only
  moves the height above it (`WalkPathMath.eyeZFromHeight`).
- The frames pane shows a non-blocking "影格為舊站點算出，需回 Revit 重出" banner whenever: edit mode is
  on, at least one station has an unsaved edit, or the currently-selected variant has `stale: true`.
- The 3D pane's main camera follows the currently-selected station's *edited* pose while edit mode is on
  (instead of the timeline's `t`); the calibration-overlay camera always keeps following the timeline
  path pose regardless of edit mode, since it exists to compare against the (unaffected) exported frame.
- "下載 stations.json" exports the stations to hand back to `apply_camera_stations`:

  ```jsonc
  {
    "coordinates": "package",
    "packageCreatedAt": "2026-09-16T10:00:00+08:00",  // == manifest.createdAt, ties the file to this package
    "stations": [
      { "name": "WALK-01", "eye": [x, y, z], "fwd": [x, y, z], "up": [x, y, z] }
    ]
  }
  ```

  By default only stations with an actual edit are included; the "輸出全部站點" checkbox forces every
  station in. `eye` is in model coordinates (same convention as everywhere else in this contract);
  `fwd`/`up` are re-normalized/re-orthonormalized at export time regardless of how they were produced.
