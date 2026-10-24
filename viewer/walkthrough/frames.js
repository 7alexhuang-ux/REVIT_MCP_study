// frames.js — the top-left "video" pane: swaps <img src> between pre-rendered
// frame PNGs as t changes. Missing frames show a placeholder, never an exception
// (a variant can be partially exported while Revit-side rendering is still running).
(function () {
  'use strict';

  function formatFrameName(pattern, index) {
    // pattern like "frame_%04d.png" — only %0Nd is supported (that's all the
    // CONTRACT specifies), good enough without pulling in a sprintf lib.
    var m = /%0(\d)d/.exec(pattern);
    if (!m) return pattern.replace('%d', String(index));
    var width = parseInt(m[1], 10);
    var digits = String(index);
    while (digits.length < width) digits = '0' + digits;
    return pattern.replace(m[0], digits);
  }

  var WalkFrames = {
    init: function (ctx) {
      this.ctx = ctx;
      this.img = document.getElementById('frame-img');
      this.placeholder = document.getElementById('frame-placeholder');
      this.placeholderName = document.getElementById('frame-placeholder-name');
      this.readout = document.getElementById('frame-readout');
      this._lastRequestedSrc = null;

      var self = this;
      this.img.addEventListener('load', function () {
        if (self.img.src.indexOf(self._lastRequestedSrc) === -1) return; // stale
        self.placeholder.classList.remove('visible');
        self.img.style.visibility = 'visible';
      });
      this.img.addEventListener('error', function () {
        self.img.style.visibility = 'hidden';
        self.placeholder.classList.add('visible');
        self.placeholderName.textContent = self._lastRequestedName || '';
      });

      // Preheat only the initially-selected variant. Preheating every variant
      // up front would eagerly probe (and log failed-resource console entries
      // for) any variant that is intentionally incomplete — see CONTRACT.md's
      // "missing frames show a placeholder, never an error" plus the brief's
      // separate "console must stay error-free on initial load" bar. Other
      // variants get preheated lazily the moment they're actually selected
      // (see preheatVariant call from app.js's variant-select handler).
      if (this.ctx.state.variantId) this.preheatVariant(this.ctx.state.variantId);
    },

    frameFileName: function (index) {
      var pattern = (this.ctx.manifest.frames && this.ctx.manifest.frames.fileNamePattern) || 'frame_%04d.png';
      return formatFrameName(pattern, index);
    },

    render: function (state) {
      var ctx = this.ctx;
      var frameIndex = ctx.WalkPathMath.frameIndexFromT(state.t, ctx.sampleCount);
      var variant = ctx.getVariant(state.variantId);
      var name = this.frameFileName(frameIndex);
      this._lastRequestedName = name;

      if (!variant) {
        this.img.style.visibility = 'hidden';
        this.placeholder.classList.add('visible');
        this.placeholderName.textContent = '（沒有可用版本）';
        this.readout.textContent = '';
        return;
      }

      var src = variant.dir.replace(/\/$/, '') + '/' + name;
      this._lastRequestedSrc = src;
      if (this.img.getAttribute('src') !== src) {
        this.img.src = src;
      }
      this.readout.textContent = variant.label + ' · ' + name;
    },

    // Preheats every frame index (0..sampleCount-1) of one variant by creating
    // detached Image() objects — warms the browser's disk cache for file://
    // reads ahead of scrubbing. Indices the variant doesn't have simply 404;
    // that's expected for an incomplete variant and handled by the placeholder
    // logic in render(), not here.
    preheatVariant: function (variantId) {
      if (this._preheated && this._preheated[variantId]) return;
      this._preheated = this._preheated || {};
      this._preheated[variantId] = true;
      var ctx = this.ctx;
      var variant = ctx.getVariant(variantId);
      if (!variant) return;
      var self = this;
      for (var i = 0; i < ctx.sampleCount; i++) {
        var img = new Image();
        img.src = variant.dir.replace(/\/$/, '') + '/' + self.frameFileName(i);
        // fire-and-forget; load/error results are irrelevant here.
      }
    },
  };

  window.WalkFrames = WalkFrames;
})();
