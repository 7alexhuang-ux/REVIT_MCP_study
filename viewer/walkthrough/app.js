// app.js — top-level orchestration: single source of truth `state.t`, wiring the
// timeline / toolbar UI to the four panes (frames/plan/section/scene3d).
// Classic script, runs after vendor/data.js/pathmath.js/plan.js/section.js/frames.js/scene3d.js.
(function () {
  'use strict';

  var DATA = window.WALK_DATA || { manifest: null, glbBase64: null };
  var manifest = DATA.manifest;
  var M = window.WalkPathMath;

  if (!manifest) {
    document.body.innerHTML = '<div style="padding:24px;color:#ff6b6b;">找不到 window.WALK_DATA — data.js 未正確載入。</div>';
    return;
  }

  var samples = (manifest.path && manifest.path.samples) || [];
  var sampleCount = (manifest.path && manifest.path.sampleCount) || samples.length;

  function findFirstDisplayableVariant() {
    var variants = manifest.variants || [];
    for (var i = 0; i < variants.length; i++) {
      if (variants[i].frameCount > 0) return variants[i].id;
    }
    return variants.length ? variants[0].id : null;
  }

  // ---- calibration (localStorage) -----------------------------------------
  var calibKey = 'walk-calib:' + manifest.createdAt;
  var calib = null;
  try {
    var raw = window.localStorage.getItem(calibKey);
    if (raw) calib = JSON.parse(raw);
  } catch (e) {
    console.warn('[app] failed to read calibration from localStorage', e);
  }

  var baseCamera = manifest.camera || { fovYDeg: 50, fovXDeg: 80, aspect: 1.7778, near: 0.1, far: 1000 };
  var effectiveCamera = {
    fovYDeg: (calib && typeof calib.fovYDeg === 'number') ? calib.fovYDeg : baseCamera.fovYDeg,
    fovXDeg: baseCamera.fovXDeg,
    aspect: baseCamera.aspect,
    near: baseCamera.near || 0.1,
    far: baseCamera.far || 1000,
    calibrated: !!(calib && typeof calib.fovYDeg === 'number'),
  };

  var state = {
    t: 0,
    variantId: findFirstDisplayableVariant(),
    calibMode: false,
    freeCam: false,
    playing: false,
    calibOpacity: (calib && typeof calib.opacity === 'number') ? calib.opacity : 50,
    editMode: false,
    selectedStationName: null,
  };

  var ctx = {
    manifest: manifest,
    glbBase64: DATA.glbBase64,
    WalkPathMath: M,
    samples: samples,
    sampleCount: sampleCount,
    effectiveCamera: effectiveCamera,
    state: state,
    getVariant: function (id) {
      var variants = manifest.variants || [];
      for (var i = 0; i < variants.length; i++) {
        if (variants[i].id === id) return variants[i];
      }
      return null;
    },
    setT: function (t, source) {
      setT(t, source);
    },
  };

  // ---- station edit model ---------------------------------------------------
  // The exported frames are pre-rendered by Revit and do NOT update when a
  // station is dragged/adjusted here — this whole block only produces a
  // *preview* (3D pane + plan marker) and a stations.json to hand back to
  // Revit (`apply_camera_stations`). See CONTRACT.md "Station editing".
  var STATION_EDIT_KEY = 'walk-edit:' + manifest.createdAt;
  var stationEdits = {}; // name -> { eye:[x,y,z], fwd:[x,y,z], up:[x,y,z] }, only entries that differ from base

  (function loadStationEdits() {
    try {
      var raw = window.localStorage.getItem(STATION_EDIT_KEY);
      if (raw) {
        var parsed = JSON.parse(raw);
        if (parsed && typeof parsed === 'object') stationEdits = parsed;
      }
    } catch (e) {
      console.warn('[app] failed to read station edits from localStorage', e);
    }
  })();

  function saveStationEdits() {
    try {
      if (Object.keys(stationEdits).length === 0) {
        window.localStorage.removeItem(STATION_EDIT_KEY);
      } else {
        window.localStorage.setItem(STATION_EDIT_KEY, JSON.stringify(stationEdits));
      }
    } catch (e) {
      console.warn('[app] failed to save station edits', e);
    }
  }

  // Normalizes one manifest station into { eye, fwd, up, eyeHeightAboveLevel,
  // fovYDeg, levelName, sampleIndex }, falling back to the path sample at
  // sampleIndex for older/incomplete station records.
  function stationBase(station) {
    var sample = samples[station.sampleIndex] || null;
    var eye = Array.isArray(station.eye) ? station.eye.slice() : (sample ? sample.p.slice() : [0, 0, 0]);
    var fwd = Array.isArray(station.fwd) ? station.fwd.slice() : (sample ? sample.fwd.slice() : [1, 0, 0]);
    var up = Array.isArray(station.up) ? station.up.slice() : (sample ? sample.up.slice() : [0, 0, 1]);
    var eyeHeightAboveLevel = typeof station.eyeHeightAboveLevel === 'number' ? station.eyeHeightAboveLevel : null;
    var fovYDeg = typeof station.fovYDeg === 'number' ? station.fovYDeg : null;
    return {
      name: station.name, eye: eye, fwd: fwd, up: up,
      eyeHeightAboveLevel: eyeHeightAboveLevel, fovYDeg: fovYDeg,
      levelName: station.levelName, sampleIndex: station.sampleIndex,
    };
  }

  var stationBaseByName = {};
  (manifest.stations || []).forEach(function (st) { stationBaseByName[st.name] = stationBase(st); });

  function vecClose(a, b) {
    var eps = 1e-9;
    return Math.abs(a[0] - b[0]) < eps && Math.abs(a[1] - b[1]) < eps && Math.abs(a[2] - b[2]) < eps;
  }

  function pruneEditIfUnchanged(name) {
    var edit = stationEdits[name];
    var base = stationBaseByName[name];
    if (!edit || !base) return;
    if (vecClose(edit.eye, base.eye) && vecClose(edit.fwd, base.fwd) && vecClose(edit.up, base.up)) {
      delete stationEdits[name];
    }
  }

  function ensureEdit(name) {
    if (!stationEdits[name]) {
      var base = stationBaseByName[name];
      stationEdits[name] = { eye: base.eye.slice(), fwd: base.fwd.slice(), up: base.up.slice() };
    }
    return stationEdits[name];
  }

  // Effective (base + edit override) view of a station, plus derived fields
  // (headingDeg, edited flag, eyeHeightAboveLevel recomputed for the current eye z).
  function stationEffective(name) {
    var base = stationBaseByName[name];
    if (!base) return null;
    var edit = stationEdits[name];
    var eye = edit ? edit.eye : base.eye;
    var fwd = edit ? edit.fwd : base.fwd;
    var up = edit ? edit.up : base.up;
    var eyeHeightAboveLevel = null;
    if (typeof base.eyeHeightAboveLevel === 'number') {
      var levelZ = base.eye[2] - base.eyeHeightAboveLevel;
      eyeHeightAboveLevel = eye[2] - levelZ;
    }
    return {
      name: name, eye: eye, fwd: fwd, up: up,
      eyeHeightAboveLevel: eyeHeightAboveLevel,
      fovYDeg: base.fovYDeg, levelName: base.levelName, sampleIndex: base.sampleIndex,
      headingDeg: M.headingDegFromFwd(fwd),
      edited: !!edit,
    };
  }

  function _setStationEyeHeightInternal(name, newHeightAboveLevel) {
    var base = stationBaseByName[name];
    if (!base) return;
    var e = ensureEdit(name);
    if (typeof base.eyeHeightAboveLevel === 'number') {
      e.eye[2] = M.eyeZFromHeight(base.eye[2], base.eyeHeightAboveLevel, newHeightAboveLevel);
    } else {
      e.eye[2] = newHeightAboveLevel; // no known level elevation: slider controls absolute z directly
    }
    pruneEditIfUnchanged(name);
  }

  function onStationChanged() {
    saveStationEdits();
    renderStationTable();
    renderStationEditArea();
    updateFrameStaleBanner();
    if (window.WalkPlan) window.WalkPlan.render(state);
    if (window.WalkScene3D) window.WalkScene3D.render(state);
  }

  function setStationXY(name, x, y) {
    var e = ensureEdit(name);
    e.eye[0] = x; e.eye[1] = y;
    pruneEditIfUnchanged(name);
    onStationChanged();
  }

  function setStationEyeHeight(name, newHeightAboveLevel) {
    _setStationEyeHeightInternal(name, newHeightAboveLevel);
    onStationChanged();
  }

  function setStationHeading(name, headingDeg) {
    var e = ensureEdit(name);
    var newFwd = M.fwdFromHeadingDeg(headingDeg, e.fwd);
    e.up = M.orthonormalizeUp(newFwd, e.up);
    e.fwd = newFwd;
    pruneEditIfUnchanged(name);
    onStationChanged();
  }

  function resetStation(name) {
    delete stationEdits[name];
    onStationChanged();
  }

  function resetAllStations() {
    stationEdits = {};
    onStationChanged();
  }

  function applyUniformEyeHeight(newHeight) {
    (manifest.stations || []).forEach(function (st) { _setStationEyeHeightInternal(st.name, newHeight); });
    onStationChanged();
  }

  function nudgeAllEyeHeights(delta) {
    (manifest.stations || []).forEach(function (st) {
      var eff = stationEffective(st.name);
      var cur = (eff.eyeHeightAboveLevel != null) ? eff.eyeHeightAboveLevel : eff.eye[2];
      _setStationEyeHeightInternal(st.name, M.clamp(cur + delta, 0.5, 3.0));
    });
    onStationChanged();
  }

  // Selection: updates the highlighted row/marker and (when editing) the edit
  // panel's sliders. Does NOT move the timeline — see seekToStationSample.
  function selectStation(name) {
    state.selectedStationName = name;
    renderStationTable();
    renderStationEditArea();
    if (window.WalkPlan) window.WalkPlan.render(state);
    if (window.WalkScene3D) window.WalkScene3D.render(state);
  }

  function seekToStationSample(name) {
    selectStation(name);
    var base = stationBaseByName[name];
    if (base && samples[base.sampleIndex]) {
      setState_stopPlaying();
      setT(samples[base.sampleIndex].t, 'station');
    }
  }

  function updateFrameStaleBanner() {
    if (!el.frameStaleBanner) return;
    var variant = ctx.getVariant(state.variantId);
    var dirty = Object.keys(stationEdits).length > 0;
    var stale = state.editMode || dirty || (variant && variant.stale);
    el.frameStaleBanner.classList.toggle('visible', !!stale);
  }

  ctx.stationEffective = stationEffective;
  ctx.setStationXY = setStationXY;
  ctx.selectStationSilent = selectStation;
  ctx.seekToStationSample = seekToStationSample;
  ctx.getStationPreviewPose = function () {
    if (!state.editMode || !state.selectedStationName) return null;
    var eff = stationEffective(state.selectedStationName);
    if (!eff) return null;
    return { p: eff.eye, fwd: eff.fwd, up: eff.up };
  };

  window.WalkApp = ctx; // exposed for tests / debugging (page.evaluate)

  // ---- DOM refs -------------------------------------------------------------
  var el = {
    banner: document.getElementById('warning-banner'),
    warningsToggleBtn: document.getElementById('warnings-toggle-btn'),
    warningsDetail: document.getElementById('warnings-detail'),
    variantSelect: document.getElementById('variant-select'),
    calibToggleBtn: document.getElementById('calib-toggle-btn'),
    freecamToggleBtn: document.getElementById('freecam-toggle-btn'),
    timelineRange: document.getElementById('timeline-range'),
    timelineReadout: document.getElementById('timeline-readout'),
    timelineTrack: document.getElementById('timeline-track'),
    timelineStrip: document.getElementById('timeline-strip'),
    timelineStations: document.getElementById('timeline-stations'),
    timelinePlayhead: document.getElementById('timeline-playhead'),
    timelineHover: document.getElementById('timeline-hover'),
    timelineHoverImg: document.getElementById('timeline-hover-img'),
    timelineHoverLabel: document.getElementById('timeline-hover-label'),
    playBtn: document.getElementById('play-btn'),
    calibPanel: document.getElementById('calib-panel'),
    calibFovY: document.getElementById('calib-fovy'),
    calibFovYValue: document.getElementById('calib-fovy-value'),
    calibOpacity: document.getElementById('calib-opacity'),
    calibOpacityValue: document.getElementById('calib-opacity-value'),
    calibSaveBtn: document.getElementById('calib-save-btn'),
    calibDownloadBtn: document.getElementById('calib-download-btn'),
    calibStatus: document.getElementById('calib-status'),
    calibScene3dCanvas: document.getElementById('calib-scene3d-canvas'),
    frameStaleBanner: document.getElementById('frame-stale-banner'),
    stationPanel: document.getElementById('station-panel'),
    stationPanelToggleBtn: document.getElementById('station-panel-toggle-btn'),
    stationPanelCloseBtn: document.getElementById('station-panel-close-btn'),
    stationDirtyBadge: document.getElementById('station-dirty-badge'),
    stationTableBody: document.getElementById('station-table-body'),
    editToggleBtn: document.getElementById('edit-toggle-btn'),
    stationEditArea: document.getElementById('station-edit-area'),
    stationEditName: document.getElementById('station-edit-name'),
    stationResetBtn: document.getElementById('station-reset-btn'),
    stationResetAllBtn: document.getElementById('station-reset-all-btn'),
    editEyeHeight: document.getElementById('edit-eye-height'),
    editEyeHeightRange: document.getElementById('edit-eye-height-range'),
    editEyeHeightLabel: document.getElementById('edit-eye-height-label'),
    editHeading: document.getElementById('edit-heading'),
    editHeadingRange: document.getElementById('edit-heading-range'),
    editHeadingLabel: document.getElementById('edit-heading-label'),
    batchEyeHeight: document.getElementById('batch-eye-height'),
    batchEyeHeightBtn: document.getElementById('batch-eye-height-btn'),
    batchEyePlusBtn: document.getElementById('batch-eye-plus-btn'),
    batchEyeMinusBtn: document.getElementById('batch-eye-minus-btn'),
    exportAllCheckbox: document.getElementById('export-all-checkbox'),
    exportStationsBtn: document.getElementById('export-stations-btn'),
    exportHint: document.getElementById('export-hint'),
  };

  // ---- variant select ---------------------------------------------------
  (manifest.variants || []).forEach(function (v) {
    var opt = document.createElement('option');
    opt.value = v.id;
    opt.textContent = v.label || v.id;
    if (v.id === state.variantId) opt.selected = true;
    el.variantSelect.appendChild(opt);
  });
  el.variantSelect.addEventListener('change', function () {
    state.variantId = el.variantSelect.value;
    if (window.WalkFrames) window.WalkFrames.preheatVariant(state.variantId);
    renderAll();
    updateFrameStaleBanner();
  });

  // ---- level-mismatch banner + warnings ------------------------------------
  (function setupBanners() {
    var levels = {};
    (manifest.stations || []).forEach(function (s) {
      if (s.levelName) levels[s.levelName] = true;
    });
    if (Object.keys(levels).length > 1) {
      el.banner.textContent = '路徑跨樓層，平面標記僅供參考';
      el.banner.classList.add('visible');
    }
    var warnings = manifest.warnings || [];
    if (warnings.length > 0) {
      el.warningsToggleBtn.style.display = '';
      el.warningsToggleBtn.textContent = '警告 (' + warnings.length + ')';
      var ul = document.createElement('ul');
      warnings.forEach(function (w) {
        var li = document.createElement('li');
        li.textContent = w;
        ul.appendChild(li);
      });
      el.warningsDetail.appendChild(ul);
      el.warningsToggleBtn.addEventListener('click', function () {
        el.warningsDetail.classList.toggle('visible');
      });
    }
  })();

  // ---- pane init ------------------------------------------------------------
  if (window.WalkFrames) window.WalkFrames.init(ctx);
  if (window.WalkPlan) window.WalkPlan.init(ctx);
  if (window.WalkSection) window.WalkSection.init(ctx);
  if (window.WalkScene3D) window.WalkScene3D.init(ctx);

  function renderAll() {
    if (window.WalkFrames) window.WalkFrames.render(state);
    if (window.WalkPlan) window.WalkPlan.render(state);
    if (window.WalkSection) window.WalkSection.render(state);
    if (window.WalkScene3D) window.WalkScene3D.render(state);
    updateTimelineUI();
    updateFrameStaleBanner();
  }

  // ---- timeline track (thumbnail strip + clickable station ticks) ----------
  // Station list ordered along the path, each with the t its sampleIndex maps
  // to, so ticks and the "which leg am I on" readout share one source.
  function stationTicks() {
    var out = [];
    (manifest.stations || []).forEach(function (st) {
      var base = stationBaseByName[st.name];
      var sample = base && samples[base.sampleIndex];
      if (sample) out.push({ name: st.name, t: sample.t });
    });
    out.sort(function (a, b) { return a.t - b.t; });
    return out;
  }

  // Names the current position in station terms: exactly on a station, or the
  // leg between two. This is the part the bare range input could not express.
  function legLabel(t) {
    var ticks = stationTicks();
    if (!ticks.length) return '';
    var EPS = 1e-4;
    for (var i = 0; i < ticks.length; i++) {
      if (Math.abs(t - ticks[i].t) < EPS) return ticks[i].name;
    }
    if (t < ticks[0].t) return '→ ' + ticks[0].name;
    for (var j = 0; j < ticks.length - 1; j++) {
      if (t > ticks[j].t && t < ticks[j + 1].t) return ticks[j].name + ' → ' + ticks[j + 1].name;
    }
    return ticks[ticks.length - 1].name;
  }

  function buildTimelineStations() {
    if (!el.timelineStations) return;
    el.timelineStations.innerHTML = '';
    stationTicks().forEach(function (tick) {
      var node = document.createElement('div');
      node.className = 'tl-station';
      node.style.left = (tick.t * 100) + '%';
      node.dataset.station = tick.name;
      var label = document.createElement('span');
      label.className = 'tl-station-label';
      label.textContent = tick.name;
      node.appendChild(label);
      node.addEventListener('click', function (ev) {
        ev.stopPropagation(); // don't also treat it as a bare track seek
        seekToStationSample(tick.name);
      });
      el.timelineStations.appendChild(node);
    });
  }

  // One thumbnail per ~90px of track width, sampled evenly across the path.
  function buildTimelineStrip() {
    if (!el.timelineStrip) return;
    var variant = ctx.getVariant(state.variantId);
    var width = el.timelineTrack ? el.timelineTrack.clientWidth : 0;
    var count = Math.max(6, Math.min(24, Math.round(width / 90) || 12));
    var signature = (variant ? variant.id : 'none') + ':' + count;
    if (el.timelineStrip.dataset.signature === signature) return;
    el.timelineStrip.dataset.signature = signature;
    el.timelineStrip.innerHTML = '';
    for (var i = 0; i < count; i++) {
      var index = Math.round((i / Math.max(1, count - 1)) * (sampleCount - 1));
      if (!variant || !window.WalkFrames) {
        var blank = document.createElement('div');
        blank.className = 'strip-blank';
        el.timelineStrip.appendChild(blank);
        continue;
      }
      var img = document.createElement('img');
      img.alt = '';
      img.src = variant.dir.replace(/\/$/, '') + '/' + window.WalkFrames.frameFileName(index);
      // A variant can legitimately be missing frames — drop the broken image
      // rather than showing a browser placeholder icon in the strip.
      img.addEventListener('error', function () {
        var blankEl = document.createElement('div');
        blankEl.className = 'strip-blank';
        if (this.parentNode) this.parentNode.replaceChild(blankEl, this);
      });
      el.timelineStrip.appendChild(img);
    }
  }

  function tFromTrackEvent(ev) {
    var rect = el.timelineTrack.getBoundingClientRect();
    if (!rect.width) return 0;
    return M.clamp((ev.clientX - rect.left) / rect.width, 0, 1);
  }

  function updateTimelineUI() {
    el.timelineRange.value = String(state.t);
    var frameIndex = M.frameIndexFromT(state.t, sampleCount);
    var pose = samples.length ? M.poseAtT(samples, state.t) : null;
    var dist = pose ? pose.s.toFixed(1) : '0.0';
    var leg = legLabel(state.t);
    el.timelineReadout.textContent = (leg ? leg + '　' : '')
      + '格 ' + (frameIndex + 1) + '/' + sampleCount + '　距離 ' + dist + ' m';

    if (el.timelinePlayhead) el.timelinePlayhead.style.left = (state.t * 100) + '%';
    if (el.timelineTrack) {
      el.timelineTrack.setAttribute('aria-valuetext', leg + '，格 ' + (frameIndex + 1) + '/' + sampleCount);
    }
    if (el.timelineStations) {
      var EPS = 1e-4;
      Array.prototype.forEach.call(el.timelineStations.children, function (node) {
        var base = stationBaseByName[node.dataset.station];
        var sample = base && samples[base.sampleIndex];
        node.classList.toggle('current', !!sample && Math.abs(sample.t - state.t) < EPS);
      });
    }
    buildTimelineStrip();
  }

  function setT(t, source) {
    state.t = M.clamp(t, 0, 1);
    renderAll();
  }

  // ---- timeline / playback ----------------------------------------------
  el.timelineRange.addEventListener('input', function () {
    setState_stopPlaying();
    setT(parseFloat(el.timelineRange.value), 'timeline');
  });

  // Click anywhere on the track to jump there; drag to scrub. Station ticks
  // stopPropagation so clicking a label snaps exactly to that station instead
  // of to whatever pixel the label happened to cover.
  if (el.timelineTrack) {
    buildTimelineStations();

    var scrubbing = false;
    el.timelineTrack.addEventListener('pointerdown', function (ev) {
      scrubbing = true;
      el.timelineTrack.setPointerCapture(ev.pointerId);
      setState_stopPlaying();
      setT(tFromTrackEvent(ev), 'timeline');
    });
    el.timelineTrack.addEventListener('pointermove', function (ev) {
      if (scrubbing) setT(tFromTrackEvent(ev), 'timeline');
      showTimelineHover(ev);
    });
    function endScrub(ev) {
      if (!scrubbing) return;
      scrubbing = false;
      try { el.timelineTrack.releasePointerCapture(ev.pointerId); } catch (e) { /* already released */ }
    }
    el.timelineTrack.addEventListener('pointerup', endScrub);
    el.timelineTrack.addEventListener('pointercancel', endScrub);
    el.timelineTrack.addEventListener('pointerleave', function () {
      if (el.timelineHover) el.timelineHover.classList.remove('visible');
    });

    // Hover preview: the frame under the cursor plus the leg it belongs to, so
    // you can find a spot before committing to a click.
    function showTimelineHover(ev) {
      if (!el.timelineHover) return;
      var t = tFromTrackEvent(ev);
      var index = M.frameIndexFromT(t, sampleCount);
      var variant = ctx.getVariant(state.variantId);
      el.timelineHover.classList.add('visible');
      el.timelineHover.style.left = (t * 100) + '%';
      if (variant && window.WalkFrames) {
        el.timelineHoverImg.style.display = '';
        el.timelineHoverImg.src = variant.dir.replace(/\/$/, '') + '/' + window.WalkFrames.frameFileName(index);
      } else {
        el.timelineHoverImg.style.display = 'none';
      }
      var pose = samples.length ? M.poseAtT(samples, t) : null;
      el.timelineHoverLabel.textContent = legLabel(t) + '　格 ' + (index + 1)
        + (pose ? '　' + pose.s.toFixed(1) + ' m' : '');
    }

    window.addEventListener('resize', function () { buildTimelineStrip(); });
  }

  var TRAVERSAL_SECONDS = 18; // full 0..1 traversal duration when playing
  var rafHandle = null;
  var lastFrameTime = null;

  function setState_stopPlaying() {
    if (state.playing) {
      state.playing = false;
      el.playBtn.textContent = '播放';
      if (rafHandle) cancelAnimationFrame(rafHandle);
      rafHandle = null;
      lastFrameTime = null;
    }
  }

  function playTick(now) {
    if (!state.playing) return;
    if (lastFrameTime === null) lastFrameTime = now;
    var dt = (now - lastFrameTime) / 1000;
    lastFrameTime = now;
    var next = state.t + dt / TRAVERSAL_SECONDS;
    if (next >= 1) {
      setT(1, 'play');
      setState_stopPlaying();
      return;
    }
    setT(next, 'play');
    rafHandle = requestAnimationFrame(playTick);
  }

  el.playBtn.addEventListener('click', function () {
    if (state.playing) {
      setState_stopPlaying();
    } else {
      if (state.t >= 1) setT(0, 'play');
      state.playing = true;
      el.playBtn.textContent = '暫停';
      lastFrameTime = null;
      rafHandle = requestAnimationFrame(playTick);
    }
  });

  // ---- keyboard ---------------------------------------------------------
  document.addEventListener('keydown', function (ev) {
    var tag = (document.activeElement && document.activeElement.tagName) || '';
    if (tag === 'SELECT' || tag === 'INPUT' || tag === 'TEXTAREA') return;
    if (ev.key === 'ArrowRight' || ev.key === 'ArrowLeft') {
      setState_stopPlaying();
      var step = 1 / Math.max(1, sampleCount - 1);
      var dir = ev.key === 'ArrowRight' ? 1 : -1;
      setT(state.t + dir * step, 'keyboard');
      ev.preventDefault();
    } else if (ev.code === 'Space') {
      el.playBtn.click();
      ev.preventDefault();
    } else if (ev.key === 'c' || ev.key === 'C') {
      toggleCalibMode();
    } else if (ev.key === 'e' || ev.key === 'E') {
      toggleEditMode();
    }
  });

  // ---- calibration mode ---------------------------------------------------
  function toggleCalibMode() {
    state.calibMode = !state.calibMode;
    el.calibToggleBtn.classList.toggle('active', state.calibMode);
    el.calibPanel.classList.toggle('visible', state.calibMode);
    el.calibScene3dCanvas.classList.toggle('visible', state.calibMode);
    if (window.WalkScene3D) window.WalkScene3D.setCalibMode(state.calibMode);
    renderAll();
  }
  el.calibToggleBtn.addEventListener('click', toggleCalibMode);

  el.calibFovY.min = 20;
  el.calibFovY.max = 100;
  el.calibFovY.value = effectiveCamera.fovYDeg;
  el.calibFovYValue.textContent = effectiveCamera.fovYDeg.toFixed(1) + '°';
  el.calibFovY.addEventListener('input', function () {
    effectiveCamera.fovYDeg = parseFloat(el.calibFovY.value);
    el.calibFovYValue.textContent = effectiveCamera.fovYDeg.toFixed(1) + '°';
    if (window.WalkScene3D) window.WalkScene3D.setFovY(effectiveCamera.fovYDeg);
    el.calibStatus.textContent = '';
  });

  el.calibOpacity.value = state.calibOpacity;
  el.calibOpacityValue.textContent = state.calibOpacity + '%';
  el.calibOpacity.addEventListener('input', function () {
    state.calibOpacity = parseFloat(el.calibOpacity.value);
    el.calibOpacityValue.textContent = state.calibOpacity + '%';
    el.calibScene3dCanvas.style.opacity = String(state.calibOpacity / 100);
  });
  el.calibScene3dCanvas.style.opacity = String(state.calibOpacity / 100);

  el.calibSaveBtn.addEventListener('click', function () {
    try {
      window.localStorage.setItem(
        calibKey,
        JSON.stringify({ fovYDeg: effectiveCamera.fovYDeg, opacity: state.calibOpacity, savedAt: new Date().toISOString() })
      );
      effectiveCamera.calibrated = true;
      el.calibStatus.textContent = '已儲存 (calibrated)';
    } catch (e) {
      el.calibStatus.textContent = '儲存失敗: ' + e.message;
    }
  });

  el.calibDownloadBtn.addEventListener('click', function () {
    var patched = JSON.parse(JSON.stringify(manifest));
    patched.camera.fovYDeg = effectiveCamera.fovYDeg;
    patched.camera.source = 'calibrated';
    patched.camera.calibratedAt = new Date().toISOString();
    var blob = new Blob([JSON.stringify(patched, null, 2)], { type: 'application/json' });
    var url = URL.createObjectURL(blob);
    var a = document.createElement('a');
    a.href = url;
    a.download = 'manifest.json';
    document.body.appendChild(a);
    a.click();
    document.body.removeChild(a);
    setTimeout(function () { URL.revokeObjectURL(url); }, 1000);
  });

  // ---- station panel (read-only info + edit mode) --------------------------
  function escapeHtml(s) {
    return String(s).replace(/[&<>"']/g, function (c) {
      return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
    });
  }

  function renderStationTable() {
    var tbody = el.stationTableBody;
    if (!tbody) return;
    tbody.innerHTML = '';
    (manifest.stations || []).forEach(function (st) {
      var eff = stationEffective(st.name);
      if (!eff) return;
      var tr = document.createElement('tr');
      tr.className = 'station-row' + (eff.edited ? ' edited' : '') + (state.selectedStationName === st.name ? ' selected' : '');
      var heightCell = eff.eye[2].toFixed(2) + (eff.eyeHeightAboveLevel != null ? ' (距樓層 +' + eff.eyeHeightAboveLevel.toFixed(2) + ' m)' : '');
      var fovCell = (eff.fovYDeg != null) ? eff.fovYDeg.toFixed(1) + '°' : '—';
      tr.innerHTML =
        '<td>' + escapeHtml(st.name) + (eff.edited ? ' *' : '') + '</td>' +
        '<td>' + eff.eye[0].toFixed(2) + '</td>' +
        '<td>' + eff.eye[1].toFixed(2) + '</td>' +
        '<td>' + escapeHtml(heightCell) + '</td>' +
        '<td>' + eff.headingDeg.toFixed(1) + '</td>' +
        '<td>' + fovCell + '</td>';
      tr.addEventListener('click', function () { seekToStationSample(st.name); });
      tbody.appendChild(tr);
    });
    var dirtyCount = Object.keys(stationEdits).length;
    el.stationDirtyBadge.textContent = dirtyCount > 0 ? ('有未寫回的編輯（' + dirtyCount + ' 站）') : '';
  }

  function renderStationEditArea() {
    if (!el.stationEditArea) return;
    var name = state.selectedStationName;
    if (!name || !state.editMode) {
      el.stationEditArea.classList.add('hidden');
      return;
    }
    var eff = stationEffective(name);
    if (!eff) {
      el.stationEditArea.classList.add('hidden');
      return;
    }
    el.stationEditArea.classList.remove('hidden');
    el.stationEditName.textContent = name + (eff.edited ? '（已編輯）' : '');
    var heightVal = (eff.eyeHeightAboveLevel != null) ? eff.eyeHeightAboveLevel : eff.eye[2];
    heightVal = M.clamp(heightVal, 0.5, 3.0);
    el.editEyeHeight.value = heightVal.toFixed(2);
    el.editEyeHeightRange.value = String(heightVal);
    el.editEyeHeightLabel.textContent = '距樓層 +' + heightVal.toFixed(2) + ' m';
    // show heading in signed -180..180 form (matches the slider's range) while
    // the underlying value stays the 0..360 compass bearing from headingDegFromFwd.
    var signedHeading = eff.headingDeg > 180 ? eff.headingDeg - 360 : eff.headingDeg;
    el.editHeading.value = signedHeading.toFixed(1);
    el.editHeadingRange.value = String(signedHeading);
    el.editHeadingLabel.textContent = eff.headingDeg.toFixed(1) + '°';
  }

  function applyEyeHeightToSelected(v) {
    if (!state.selectedStationName) return;
    setStationEyeHeight(state.selectedStationName, M.clamp(v, 0.5, 3.0));
  }
  function applyHeadingToSelected(v) {
    if (!state.selectedStationName) return;
    setStationHeading(state.selectedStationName, v);
  }

  el.editEyeHeight.addEventListener('input', function () {
    var v = parseFloat(el.editEyeHeight.value);
    if (isNaN(v)) return;
    applyEyeHeightToSelected(v);
  });
  el.editEyeHeightRange.addEventListener('input', function () {
    applyEyeHeightToSelected(parseFloat(el.editEyeHeightRange.value));
  });
  el.editHeading.addEventListener('input', function () {
    var v = parseFloat(el.editHeading.value);
    if (isNaN(v)) return;
    applyHeadingToSelected(v);
  });
  el.editHeadingRange.addEventListener('input', function () {
    applyHeadingToSelected(parseFloat(el.editHeadingRange.value));
  });

  el.stationResetBtn.addEventListener('click', function () {
    if (state.selectedStationName) resetStation(state.selectedStationName);
  });
  el.stationResetAllBtn.addEventListener('click', function () { resetAllStations(); });
  el.batchEyeHeightBtn.addEventListener('click', function () {
    var v = M.clamp(parseFloat(el.batchEyeHeight.value) || 1.6, 0.5, 3.0);
    applyUniformEyeHeight(v);
  });
  el.batchEyePlusBtn.addEventListener('click', function () { nudgeAllEyeHeights(0.05); });
  el.batchEyeMinusBtn.addEventListener('click', function () { nudgeAllEyeHeights(-0.05); });

  el.stationPanelToggleBtn.addEventListener('click', function () {
    state.stationPanelOpen = !el.stationPanel.classList.contains('visible');
    el.stationPanel.classList.toggle('visible', state.stationPanelOpen);
    resizePanes(); // panel is a flex sibling of #grid, not an overlay — see resizePanes comment
  });
  el.stationPanelCloseBtn.addEventListener('click', function () {
    el.stationPanel.classList.remove('visible');
    resizePanes();
  });

  function toggleEditMode() {
    state.editMode = !state.editMode;
    el.editToggleBtn.classList.toggle('active', state.editMode);
    if (state.editMode) {
      el.stationPanel.classList.add('visible'); // opening edit mode always surfaces the panel
      if (!state.selectedStationName && manifest.stations && manifest.stations.length) {
        state.selectedStationName = manifest.stations[0].name;
      }
    }
    renderStationTable();
    renderStationEditArea();
    updateFrameStaleBanner();
    // #grid's height changes either way: the panel opening (if it wasn't
    // already) OR the edit-area block appearing/disappearing INSIDE an
    // already-open panel both change the panel's own height -> #grid shrinks
    // or grows to compensate (flex siblings), same "layout-without-a-resize-
    // event" case resizePanes() exists for.
    resizePanes();
  }
  el.editToggleBtn.addEventListener('click', toggleEditMode);

  // ---- stations.json export -------------------------------------------------
  el.exportStationsBtn.addEventListener('click', function () {
    var outputAll = el.exportAllCheckbox.checked;
    var list = [];
    (manifest.stations || []).forEach(function (st) {
      var eff = stationEffective(st.name);
      if (!eff) return;
      if (!outputAll && !eff.edited) return;
      var fwd = M.normalize3(eff.fwd);
      var up = M.orthonormalizeUp(fwd, eff.up);
      list.push({ name: st.name, eye: eff.eye.slice(), fwd: fwd, up: up });
    });
    var payload = { coordinates: 'package', packageCreatedAt: manifest.createdAt, stations: list };
    // Exposed for headless verification (page.evaluate) since the sandbox blocks
    // the <a download> click from actually saving a file.
    window.__lastStationsExport = payload;
    try {
      var blob = new Blob([JSON.stringify(payload, null, 2)], { type: 'application/json' });
      var url = URL.createObjectURL(blob);
      var a = document.createElement('a');
      a.href = url;
      a.download = 'stations.json';
      document.body.appendChild(a);
      a.click();
      document.body.removeChild(a);
      setTimeout(function () { URL.revokeObjectURL(url); }, 1000);
    } catch (e) {
      console.warn('[app] stations.json download failed', e);
    }
    el.exportHint.textContent = list.length
      ? '請在 Claude 對話輸入：用 apply_camera_stations 把這個 stations.json 寫回 Revit（先 mode=preview 確認位移，再 mode=apply）'
      : '沒有可輸出的變更（勾選「輸出全部站點」可強制輸出全部）';
  });

  // ---- free camera --------------------------------------------------------
  el.freecamToggleBtn.addEventListener('click', function () {
    state.freeCam = !state.freeCam;
    el.freecamToggleBtn.classList.toggle('active', state.freeCam);
    if (window.WalkScene3D) window.WalkScene3D.setFreeCam(state.freeCam);
  });

  // ---- resize ---------------------------------------------------------------
  // Shared by the actual window 'resize' event AND anything that changes the
  // flex layout without resizing the window itself — showing/hiding the
  // station panel shrinks/grows #grid (it's a flex sibling, not an overlay),
  // which changes each pane's clientWidth/clientHeight but fires no browser
  // resize event, so canvas backing-buffer sizes (and therefore all of plan.js's
  // canvas-pixel math) would otherwise go stale the moment the drawer opens.
  function resizePanes() {
    if (window.WalkPlan) window.WalkPlan.resize();
    if (window.WalkSection) window.WalkSection.resize();
    if (window.WalkScene3D) window.WalkScene3D.resize();
    renderAll();
  }
  window.addEventListener('resize', resizePanes);

  // ---- initial paint --------------------------------------------------------
  renderAll();
  renderStationTable();
  renderStationEditArea();
  updateFrameStaleBanner();
})();
