// plan.js — top-view plan pane: draws plan.png, the path polyline, station
// markers, the current position + view-cone, and lets the user drag/click to
// scrub `t` (dragging the current-position dot, or clicking near the path).
// Independent of that: wheel-zoom and background-drag pan the view itself.
(function () {
  'use strict';

  var HIT_RADIUS_PX = 16; // marker drag hit radius
  var PATH_HIT_RADIUS_PX = 10; // click-near-path jump radius

  var WalkPlan = {
    init: function (ctx) {
      this.ctx = ctx;
      this.canvas = document.getElementById('plan-canvas');
      this.c2d = this.canvas.getContext('2d');
      this.body = this.canvas.parentElement;
      this.plan = ctx.manifest.plan;
      this.userScale = 1;
      this.userOffset = { x: 0, y: 0 };
      this.dragMode = null; // null | 'marker' | 'pan' | 'station'
      this.dragStationName = null;
      this.panStart = null;

      if (!this.plan) {
        this._showEmpty('未匯出平面');
        return;
      }

      this.img = new Image();
      var self = this;
      this.img.onload = function () {
        self.imgLoaded = true;
        self.resize();
      };
      this.img.onerror = function () {
        self._showEmpty('平面圖載入失敗：' + self.plan.image);
      };
      this.img.src = this.plan.image;

      this._wireEvents();
      this.resize();
    },

    _showEmpty: function (msg) {
      var d = document.createElement('div');
      d.className = 'pane-empty';
      d.textContent = msg;
      this.body.appendChild(d);
      this.canvas.style.display = 'none';
    },

    resize: function () {
      if (!this.plan) return;
      var dpr = window.devicePixelRatio || 1;
      var w = this.body.clientWidth, h = this.body.clientHeight;
      this.canvas.width = Math.max(1, Math.round(w * dpr));
      this.canvas.height = Math.max(1, Math.round(h * dpr));
      this.canvas.style.width = w + 'px';
      this.canvas.style.height = h + 'px';
      if (this.imgLoaded) {
        this.fitScale = Math.min(this.canvas.width / this.img.width, this.canvas.height / this.img.height);
      }
      this._draw();
    },

    _totalScale: function () {
      return (this.fitScale || 1) * this.userScale;
    },

    // image-pixel (px,py) -> canvas-pixel
    _toCanvas: function (px, py) {
      var s = this._totalScale();
      var cx = (this.canvas.width - this.img.width * s) / 2 + this.userOffset.x + px * s;
      var cy = (this.canvas.height - this.img.height * s) / 2 + this.userOffset.y + py * s;
      return [cx, cy];
    },

    // canvas-pixel -> image-pixel
    _fromCanvas: function (cx, cy) {
      var s = this._totalScale();
      var px = (cx - ((this.canvas.width - this.img.width * s) / 2 + this.userOffset.x)) / s;
      var py = (cy - ((this.canvas.height - this.img.height * s) / 2 + this.userOffset.y)) / s;
      return [px, py];
    },

    _canvasPosFromEvent: function (ev) {
      var rect = this.canvas.getBoundingClientRect();
      var dpr = window.devicePixelRatio || 1;
      return [(ev.clientX - rect.left) * dpr, (ev.clientY - rect.top) * dpr];
    },

    _wireEvents: function () {
      var self = this;
      this.canvas.addEventListener('wheel', function (ev) {
        ev.preventDefault();
        var cpos = self._canvasPosFromEvent(ev);
        var factor = ev.deltaY < 0 ? 1.1 : 1 / 1.1;
        self._zoomAt(cpos[0], cpos[1], factor);
      }, { passive: false });

      this.canvas.addEventListener('mousedown', function (ev) {
        if (!self.imgLoaded) return;
        var cpos = self._canvasPosFromEvent(ev);
        var dpr = window.devicePixelRatio || 1;

        // Station markers take priority over path-scrub hit-testing: clicking one
        // always selects it; in edit mode it also starts an XY drag.
        var hitStation = self._hitTestStations(cpos, dpr);
        if (hitStation) {
          if (self.ctx.state.editMode) {
            self.dragMode = 'station';
            self.dragStationName = hitStation;
            if (self.ctx.selectStationSilent) self.ctx.selectStationSilent(hitStation);
            self._draw();
          } else if (self.ctx.seekToStationSample) {
            self.ctx.seekToStationSample(hitStation);
          }
          return;
        }

        var M = self.ctx.WalkPathMath;
        var pose = M.poseAtT(self.ctx.samples, self.ctx.state.t);
        var curPx = M.applyAffine(self.plan.modelToPixel, pose.p[0], pose.p[1]);
        var curCanvas = self._toCanvas(curPx[0], curPx[1]);
        var distToMarker = Math.hypot(cpos[0] - curCanvas[0], cpos[1] - curCanvas[1]);

        if (distToMarker < HIT_RADIUS_PX * dpr) {
          self.dragMode = 'marker';
          self._dragToT(cpos);
        } else if (self._distanceToPathCanvas(cpos) < PATH_HIT_RADIUS_PX * dpr) {
          self.dragMode = 'marker';
          self._dragToT(cpos);
        } else {
          self.dragMode = 'pan';
          self.panStart = { cx: cpos[0], cy: cpos[1], ox: self.userOffset.x, oy: self.userOffset.y };
        }
      });

      window.addEventListener('mousemove', function (ev) {
        if (!self.dragMode || !self.imgLoaded) return;
        var cpos = self._canvasPosFromEvent(ev);
        if (self.dragMode === 'station') {
          var M = self.ctx.WalkPathMath;
          var imgPx = self._fromCanvas(cpos[0], cpos[1]);
          var inv = M.invertAffine(self.plan.modelToPixel);
          var modelXY = M.applyAffine(inv, imgPx[0], imgPx[1]);
          if (self.ctx.setStationXY) self.ctx.setStationXY(self.dragStationName, modelXY[0], modelXY[1]);
        } else if (self.dragMode === 'marker') {
          self._dragToT(cpos);
        } else if (self.dragMode === 'pan') {
          self.userOffset.x = self.panStart.ox + (cpos[0] - self.panStart.cx);
          self.userOffset.y = self.panStart.oy + (cpos[1] - self.panStart.cy);
          self._draw();
        }
      });

      window.addEventListener('mouseup', function () {
        self.dragMode = null;
        self.dragStationName = null;
        self.panStart = null;
      });
    },

    // Hit-tests station markers at their EFFECTIVE (possibly edited) position.
    _hitTestStations: function (cpos, dpr) {
      var ctx = this.ctx;
      var stations = ctx.manifest.stations || [];
      if (!ctx.stationEffective) return null;
      for (var i = 0; i < stations.length; i++) {
        var eff = ctx.stationEffective(stations[i].name);
        if (!eff) continue;
        var px = this._modelToImgPx(eff.eye);
        var c = this._toCanvas(px[0], px[1]);
        var d = Math.hypot(cpos[0] - c[0], cpos[1] - c[1]);
        if (d < HIT_RADIUS_PX * dpr) return stations[i].name;
      }
      return null;
    },

    _distanceToPathCanvas: function (cpos) {
      var samples = this.ctx.samples;
      var best = Infinity;
      for (var i = 0; i < samples.length - 1; i++) {
        var a = this._toCanvas.apply(this, this._modelToImgPx(samples[i].p));
        var b = this._toCanvas.apply(this, this._modelToImgPx(samples[i + 1].p));
        var d = this._pointSegDist(cpos, a, b);
        if (d < best) best = d;
      }
      return best;
    },

    _modelToImgPx: function (p) {
      return this.ctx.WalkPathMath.applyAffine(this.plan.modelToPixel, p[0], p[1]);
    },

    _pointSegDist: function (p, a, b) {
      var dx = b[0] - a[0], dy = b[1] - a[1];
      var lenSq = dx * dx + dy * dy;
      var u = lenSq < 1e-9 ? 0 : Math.max(0, Math.min(1, ((p[0] - a[0]) * dx + (p[1] - a[1]) * dy) / lenSq));
      var px = a[0] + dx * u, py = a[1] + dy * u;
      return Math.hypot(p[0] - px, p[1] - py);
    },

    _dragToT: function (cpos) {
      var imgPx = this._fromCanvas(cpos[0], cpos[1]);
      var M = this.ctx.WalkPathMath;
      var inv = M.invertAffine(this.plan.modelToPixel);
      var modelXY = M.applyAffine(inv, imgPx[0], imgPx[1]);
      var t = M.tFromPlanPoint(this.ctx.samples, modelXY[0], modelXY[1]);
      this.ctx.setT(t, 'plan');
    },

    _zoomAt: function (cx, cy, factor) {
      var before = this._fromCanvas(cx, cy);
      this.userScale = Math.max(0.2, Math.min(20, this.userScale * factor));
      var s = this._totalScale();
      this.userOffset.x = cx - (this.canvas.width - this.img.width * s) / 2 - before[0] * s;
      this.userOffset.y = cy - (this.canvas.height - this.img.height * s) / 2 - before[1] * s;
      this._draw();
    },

    render: function (state) {
      this._draw();
    },

    _draw: function () {
      if (!this.plan || !this.imgLoaded) return;
      var g = this.c2d;
      var W = this.canvas.width, H = this.canvas.height;
      g.clearRect(0, 0, W, H);
      g.fillStyle = '#1c1f26';
      g.fillRect(0, 0, W, H);

      var topLeft = this._toCanvas(0, 0);
      var s = this._totalScale();
      g.drawImage(this.img, topLeft[0], topLeft[1], this.img.width * s, this.img.height * s);

      var ctx = this.ctx;
      var M = ctx.WalkPathMath;
      var samples = ctx.samples;

      // path polyline
      if (samples.length > 1) {
        g.strokeStyle = '#4ea1ff';
        g.lineWidth = 2;
        g.beginPath();
        for (var i = 0; i < samples.length; i++) {
          var px = this._modelToImgPx(samples[i].p);
          var c = this._toCanvas(px[0], px[1]);
          if (i === 0) g.moveTo(c[0], c[1]); else g.lineTo(c[0], c[1]);
        }
        g.stroke();
      }

      // stations (drawn at their EFFECTIVE position: base, or the edited
      // preview position while a station's XY has been dragged)
      g.font = '12px sans-serif';
      (ctx.manifest.stations || []).forEach(function (st) {
        var eff = ctx.stationEffective ? ctx.stationEffective(st.name) : null;
        var p = eff ? eff.eye : (samples[st.sampleIndex] && samples[st.sampleIndex].p);
        if (!p) return;
        var px = this._modelToImgPx(p);
        var c = this._toCanvas(px[0], px[1]);
        var isSelected = ctx.state.selectedStationName === st.name;
        var isEdited = !!(eff && eff.edited);
        g.fillStyle = isEdited ? '#ff8a4e' : (isSelected ? '#ffffff' : '#ffd98a');
        g.beginPath();
        g.arc(c[0], c[1], isSelected ? 7 : 5, 0, Math.PI * 2);
        g.fill();
        if (isSelected) {
          g.strokeStyle = '#4ea1ff';
          g.lineWidth = 2;
          g.stroke();
        }
        g.fillStyle = isEdited ? '#ff8a4e' : '#ffd98a';
        g.fillText(st.name + (isEdited ? ' *' : ''), c[0] + 8, c[1] - 8);
      }, this);

      // current position + view cone
      if (samples.length) {
        var pose = M.poseAtT(samples, ctx.state.t);
        var curPx = this._modelToImgPx(pose.p);
        var curC = this._toCanvas(curPx[0], curPx[1]);

        var fovXDeg = ctx.effectiveCamera.fovXDeg || 80;
        var half = (fovXDeg / 2) * Math.PI / 180;
        var fwdAngle = Math.atan2(pose.fwd[1], pose.fwd[0]);
        var coneLenMeters = 6;
        // meters -> image pixels: modelToPixel's linear part gives px-per-model-unit
        var mm = this.plan.modelToPixel;
        var pxPerMeter = Math.hypot(mm[0], mm[3]); // magnitude of transformed x-basis vector
        var coneLenPx = coneLenMeters * pxPerMeter;

        var leftAngle = fwdAngle + half, rightAngle = fwdAngle - half;
        var leftPt = [pose.p[0] + Math.cos(leftAngle) * coneLenMeters, pose.p[1] + Math.sin(leftAngle) * coneLenMeters];
        var rightPt = [pose.p[0] + Math.cos(rightAngle) * coneLenMeters, pose.p[1] + Math.sin(rightAngle) * coneLenMeters];
        var leftC = this._toCanvas.apply(this, this._modelToImgPx([leftPt[0], leftPt[1], 0]));
        var rightC = this._toCanvas.apply(this, this._modelToImgPx([rightPt[0], rightPt[1], 0]));

        g.fillStyle = 'rgba(78, 161, 255, 0.25)';
        g.beginPath();
        g.moveTo(curC[0], curC[1]);
        g.lineTo(leftC[0], leftC[1]);
        g.lineTo(rightC[0], rightC[1]);
        g.closePath();
        g.fill();
        void coneLenPx; // (kept for clarity / potential future pixel-space cone math)

        g.fillStyle = '#4ea1ff';
        g.strokeStyle = '#ffffff';
        g.lineWidth = 1.5;
        g.beginPath();
        g.arc(curC[0], curC[1], 7, 0, Math.PI * 2);
        g.fill();
        g.stroke();
      }
    },
  };

  window.WalkPlan = WalkPlan;
})();
