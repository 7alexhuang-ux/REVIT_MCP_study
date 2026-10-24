// scene3d.js — three.js pane: loads model.glb (parsed straight from an
// ArrayBuffer, never fetch()), draws the path + station markers, and follows
// the walkthrough camera pose every time `t` changes. Also renders a second,
// transparent copy of the same scene into the frames pane's overlay canvas
// while calibration mode is active (see app.js #calib-scene3d-canvas), sized
// and positioned to exactly match the <img>'s object-fit:contain display
// rect so the overlay lines up with the frame instead of the whole pane.
(function () {
  'use strict';

  var MARKER_HIDE_RADIUS = 2.0; // meters; hide station markers this close to the follow-camera

  var WalkScene3D = {
    init: function (ctx) {
      this.ctx = ctx;
      var bundle = window.THREE_BUNDLE;
      if (!bundle) {
        console.error('[scene3d] THREE_BUNDLE missing — vendor/three-bundle.js did not load.');
        return;
      }
      var THREE = (this.THREE = bundle.THREE);
      var GLTFLoader = bundle.GLTFLoader;
      var OrbitControls = bundle.OrbitControls;

      this.canvas = document.getElementById('scene3d-canvas');
      this.calibCanvas = document.getElementById('calib-scene3d-canvas');
      this.body = this.canvas.parentElement;
      this.readout = document.getElementById('scene3d-readout');

      this.scene = new THREE.Scene();
      this.scene.background = null;

      var cam = ctx.effectiveCamera;
      // Main pane camera: aspect follows the pane's own on-screen box (resize()).
      this.camera = new THREE.PerspectiveCamera(cam.fovYDeg, cam.aspect || 1.78, cam.near || 0.1, cam.far || 1000);

      // Calibration overlay camera: aspect is FIXED to the exported frame's own
      // width/height (never the pane box) so the overlay's projection exactly
      // matches the frame image it's compared against.
      var frames = ctx.manifest.frames || { width: 1280, height: 720 };
      var frameAspect = frames.width / frames.height;
      this.calibCamera = new THREE.PerspectiveCamera(cam.fovYDeg, frameAspect, cam.near || 0.1, cam.far || 1000);

      // preserveDrawingBuffer on both: validation tooling reads these canvases'
      // pixels back (readPixels/getImageData/toDataURL) right after a render call.
      this.renderer = new THREE.WebGLRenderer({ canvas: this.canvas, antialias: true, preserveDrawingBuffer: true });
      this.renderer.setClearColor(0x14161a, 1);
      this.calibRenderer = new THREE.WebGLRenderer({ canvas: this.calibCanvas, antialias: true, alpha: true, preserveDrawingBuffer: true });
      this.calibRenderer.setClearColor(0x000000, 0);

      this.scene.add(new THREE.AmbientLight(0xffffff, 0.6));
      var dirLight = new THREE.DirectionalLight(0xffffff, 0.8);
      dirLight.position.set(10, 20, 10);
      this.scene.add(dirLight);

      this.scene.add(new THREE.GridHelper(60, 60, 0x445266, 0x2a2f3a));

      this._buildPathLine();
      this._buildStationMarkers();

      if (ctx.glbBase64) {
        this._loadGlb(ctx.glbBase64, GLTFLoader);
      }

      if (OrbitControls) {
        this.controls = new OrbitControls(this.camera, this.canvas);
        this.controls.enabled = false;
        this.controls.enableDamping = true;
      }

      this._freeCamLoopActive = false;
      this.resize();
    },

    // Path line + station markers are drawn at floor level, not eye height —
    // otherwise the camera (which travels at eye height, right through where
    // a station's own sample sits) ends up with a marker mesh practically
    // inside the near clip plane, filling the whole 3D pane (see 2026-09
    // review: WALK-03 turned the pane solid yellow).
    _groundZ: function (sampleP) {
      var plan = this.ctx.manifest.plan;
      if (plan && typeof plan.levelElevation === 'number') return plan.levelElevation + 0.02;
      return sampleP[2] - 1.5;
    },

    _buildPathLine: function () {
      var THREE = this.THREE;
      var samples = this.ctx.samples;
      if (!samples.length) return;
      var self = this;
      var pts = samples.map(function (s) {
        var groundP = [s.p[0], s.p[1], self._groundZ(s.p)];
        var v = window.WalkPathMath.toThree(groundP);
        return new THREE.Vector3(v.x, v.y, v.z);
      });
      var geo = new THREE.BufferGeometry().setFromPoints(pts);
      var mat = new THREE.LineBasicMaterial({ color: 0x4ea1ff });
      this.scene.add(new THREE.Line(geo, mat));
    },

    _buildStationMarkers: function () {
      var THREE = this.THREE;
      var ctx = this.ctx;
      var samples = ctx.samples;
      // Small flat disc (radius <= 0.15m) at floor level instead of a sphere at
      // eye height — see _groundZ comment above.
      var geo = new THREE.CylinderGeometry(0.15, 0.15, 0.04, 16);
      var mat = new THREE.MeshBasicMaterial({ color: 0xffd98a });
      this.stationMarkers = [];
      (ctx.manifest.stations || []).forEach(function (st) {
        var sample = samples[st.sampleIndex];
        if (!sample) return;
        var groundP = [sample.p[0], sample.p[1], this._groundZ(sample.p)];
        var v = window.WalkPathMath.toThree(groundP);
        var mesh = new THREE.Mesh(geo, mat);
        mesh.position.set(v.x, v.y + 0.03, v.z); // small lift to avoid z-fighting with the floor
        this.scene.add(mesh);
        this.stationMarkers.push({ mesh: mesh, sampleIndex: st.sampleIndex });
      }, this);
    },

    _loadGlb: function (base64, GLTFLoader) {
      try {
        var binary = atob(base64);
        var len = binary.length;
        var bytes = new Uint8Array(len);
        for (var i = 0; i < len; i++) bytes[i] = binary.charCodeAt(i);
        var loader = new GLTFLoader();
        var self = this;
        loader.parse(
          bytes.buffer,
          '',
          function (gltf) {
            self.scene.add(gltf.scene);
            self._renderCurrent();
          },
          function (err) {
            console.error('[scene3d] GLTFLoader.parse failed', err);
          }
        );
      } catch (e) {
        console.error('[scene3d] failed to decode glbBase64', e);
      }
    },

    // Computes the on-screen rect of <img id="frame-img"> as CSS object-fit:contain
    // would place it inside its pane-body, using the manifest's declared frame
    // width/height (falls back to the <img>'s natural size). The calibration
    // overlay canvas is sized/positioned to exactly this rect so it lines up
    // with the visible image instead of covering the whole (possibly letterboxed) pane.
    _updateCalibRect: function () {
      if (!this.calibRenderer) return;
      var frameImg = document.getElementById('frame-img');
      var container = frameImg.parentElement; // #pane-frames .pane-body
      var cw = container.clientWidth, ch = container.clientHeight;
      if (cw <= 0 || ch <= 0) return;
      var frames = this.ctx.manifest.frames;
      var fw = (frames && frames.width) || frameImg.naturalWidth || cw;
      var fh = (frames && frames.height) || frameImg.naturalHeight || ch;
      // The frame <img> is laid out with `max-width/max-height:100%` inside a
      // flex-centered box — i.e. it SHRINKS to fit but never enlarges past its
      // own natural (fw x fh) size. That's different from CSS object-fit:contain
      // (which also scales up); match the actual rule exactly (scale <= 1) or
      // the overlay is sized wrong whenever the pane is bigger than the frame.
      var scale = Math.min(1, cw / fw, ch / fh);
      var dispW = fw * scale, dispH = fh * scale;
      var offX = (cw - dispW) / 2, offY = (ch - dispH) / 2;
      this._calibRect = { left: offX, top: offY, width: dispW, height: dispH };
      this.calibCanvas.style.left = Math.round(offX) + 'px';
      this.calibCanvas.style.top = Math.round(offY) + 'px';
      this.calibRenderer.setSize(Math.max(1, Math.round(dispW)), Math.max(1, Math.round(dispH)), true);
      // calibCamera's aspect is intentionally NOT touched here — it stays fixed
      // to manifest.frames.width/height regardless of the on-screen rect size.
    },

    resize: function () {
      if (!this.renderer) return;
      var w = this.body.clientWidth, h = this.body.clientHeight;
      if (w > 0 && h > 0) {
        this.renderer.setSize(w, h, true);
        this.camera.aspect = w / h;
        this.camera.updateProjectionMatrix();
      }
      this._updateCalibRect();
      this._renderCurrent();
    },

    setFovY: function (fovYDeg) {
      this.camera.fov = fovYDeg;
      this.camera.updateProjectionMatrix();
      this.calibCamera.fov = fovYDeg;
      this.calibCamera.updateProjectionMatrix();
      this._renderCurrent();
    },

    setCalibMode: function (enabled) {
      this.calibMode = enabled;
      if (enabled) this._updateCalibRect();
      this._renderCurrent();
    },

    setFreeCam: function (enabled) {
      if (!this.controls) return;
      this.controls.enabled = enabled;
      if (enabled) {
        this._startFreeCamLoop();
      } else {
        this._stopFreeCamLoop();
        this._applyPose(this.ctx.state.t);
        this._updateMarkerVisibility();
        this._renderCurrent();
      }
    },

    _startFreeCamLoop: function () {
      if (this._freeCamLoopActive) return;
      this._freeCamLoopActive = true;
      var self = this;
      function tick() {
        if (!self._freeCamLoopActive) return;
        self.controls.update();
        self._updateMarkerVisibility();
        self._renderCurrent();
        self._freeCamRaf = requestAnimationFrame(tick);
      }
      this._freeCamRaf = requestAnimationFrame(tick);
    },

    _stopFreeCamLoop: function () {
      this._freeCamLoopActive = false;
      if (this._freeCamRaf) cancelAnimationFrame(this._freeCamRaf);
      this._freeCamRaf = null;
    },

    // Updates calibCamera (always follows the timeline path, unaffected by
    // station-edit preview) and, unless free-cam is active, the main pane
    // camera too. The main camera uses the EDITED preview pose of the
    // currently-selected station instead of the timeline pose while edit mode
    // is on and a station is selected (ctx.getStationPreviewPose) — this is
    // what lets the 3D pane show "what the new station would look like"
    // before anything is written back to Revit.
    _applyPose: function (t) {
      var ctx = this.ctx;
      var samples = ctx.samples;
      if (!samples.length) return;
      var M = window.WalkPathMath;
      var pathPose = M.poseAtT(samples, t);

      var mainPose = pathPose;
      var usingPreview = false;
      if (ctx.getStationPreviewPose) {
        var preview = ctx.getStationPreviewPose();
        if (preview) { mainPose = preview; usingPreview = true; }
      }
      this._currentPose = mainPose;

      var pathThree = M.toThree(pathPose.p);
      var pathUpThree = M.toThree(pathPose.up);
      var pathLookThree = M.toThree(M.add3(pathPose.p, pathPose.fwd));
      this.calibCamera.position.set(pathThree.x, pathThree.y, pathThree.z);
      this.calibCamera.up.set(pathUpThree.x, pathUpThree.y, pathUpThree.z);
      this.calibCamera.lookAt(pathLookThree.x, pathLookThree.y, pathLookThree.z);

      var pThree = usingPreview ? M.toThree(mainPose.p) : pathThree;
      var upThree = usingPreview ? M.toThree(mainPose.up) : pathUpThree;
      var lookThree = usingPreview ? M.toThree(M.add3(mainPose.p, mainPose.fwd)) : pathLookThree;

      if (!ctx.state.freeCam) {
        this.camera.position.set(pThree.x, pThree.y, pThree.z);
        this.camera.up.set(upThree.x, upThree.y, upThree.z);
        this.camera.lookAt(lookThree.x, lookThree.y, lookThree.z);
      }

      if (this.readout) {
        this.readout.textContent = 'pos (' + pThree.x.toFixed(2) + ', ' + pThree.y.toFixed(2) + ', ' + pThree.z.toFixed(2) + ')' +
          (usingPreview ? '　[站點預覽]' : '');
      }
    },

    // Hides station markers within MARKER_HIDE_RADIUS of the follow-camera so
    // the 3D pane never gets blotted out by a marker the camera is passing
    // right next to. In free-cam mode every marker is shown (per spec).
    _updateMarkerVisibility: function () {
      var freeCam = this.ctx.state.freeCam;
      var camPos = this.camera.position;
      (this.stationMarkers || []).forEach(function (m) {
        if (freeCam) {
          m.mesh.visible = true;
        } else {
          m.mesh.visible = camPos.distanceTo(m.mesh.position) > MARKER_HIDE_RADIUS;
        }
      });
    },

    _renderCurrent: function () {
      if (!this.renderer) return;
      this.renderer.render(this.scene, this.camera);
      if (this.calibMode) {
        this.calibRenderer.render(this.scene, this.calibCamera);
      }
    },

    render: function (state) {
      if (!this.renderer) return;
      this._applyPose(state.t);
      this._updateMarkerVisibility();
      this._renderCurrent();
    },
  };

  window.WalkScene3D = WalkScene3D;
})();
