// section.js — read-only section pane: draws section.png plus a marker showing
// where the current camera position projects onto the section plane. Depth
// away from the section plane fades the marker and is reported in meters.
(function () {
  'use strict';

  var WalkSection = {
    init: function (ctx) {
      this.ctx = ctx;
      this.canvas = document.getElementById('section-canvas');
      this.c2d = this.canvas.getContext('2d');
      this.body = this.canvas.parentElement;
      this.section = ctx.manifest.section;

      if (!this.section) {
        var d = document.createElement('div');
        d.className = 'pane-empty';
        d.textContent = '未匯出剖面';
        this.body.appendChild(d);
        this.canvas.style.display = 'none';
        return;
      }

      this.img = new Image();
      var self = this;
      this.img.onload = function () {
        self.imgLoaded = true;
        self.resize();
      };
      this.img.onerror = function () {
        var d = document.createElement('div');
        d.className = 'pane-empty';
        d.textContent = '剖面圖載入失敗：' + self.section.image;
        self.body.appendChild(d);
      };
      this.img.src = this.section.image;
      this.resize();
    },

    resize: function () {
      if (!this.section) return;
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

    render: function () {
      this._draw();
    },

    _toCanvas: function (px, py) {
      var s = this.fitScale || 1;
      var cx = (this.canvas.width - this.img.width * s) / 2 + px * s;
      var cy = (this.canvas.height - this.img.height * s) / 2 + py * s;
      return [cx, cy];
    },

    _draw: function () {
      if (!this.section || !this.imgLoaded) return;
      var g = this.c2d;
      var W = this.canvas.width, H = this.canvas.height;
      g.clearRect(0, 0, W, H);
      g.fillStyle = '#1c1f26';
      g.fillRect(0, 0, W, H);
      var topLeft = this._toCanvas(0, 0);
      var s = this.fitScale || 1;
      g.drawImage(this.img, topLeft[0], topLeft[1], this.img.width * s, this.img.height * s);

      var ctx = this.ctx;
      var M = ctx.WalkPathMath;
      var samples = ctx.samples;
      if (!samples.length) return;

      var pose = M.poseAtT(samples, ctx.state.t);
      var proj = M.projectToSectionFrame(pose.p, this.section.frame);
      var px = M.applyAffine(this.section.uvToPixel, proj.u, proj.v);
      var c = this._toCanvas(px[0], px[1]);

      var range = this.section.depthRangeMeters || [-10, 0];
      var maxAbs = Math.max(Math.abs(range[0]), Math.abs(range[1]), 1e-6);
      var fade = Math.max(0.15, 1 - Math.min(1, Math.abs(proj.depth) / maxAbs));

      g.fillStyle = 'rgba(255, 107, 107, ' + fade.toFixed(3) + ')';
      g.strokeStyle = 'rgba(255,255,255,' + fade.toFixed(3) + ')';
      g.lineWidth = 1.5;
      g.beginPath();
      g.arc(c[0], c[1], 7, 0, Math.PI * 2);
      g.fill();
      g.stroke();

      g.fillStyle = 'rgba(232,234,239,' + fade.toFixed(3) + ')';
      g.font = '12px sans-serif';
      g.fillText('距剖面 ' + Math.abs(proj.depth).toFixed(1) + ' m', c[0] + 10, c[1] - 10);
    },
  };

  window.WalkSection = WalkSection;
})();
