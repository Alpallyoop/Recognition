/* ThemeForge motion runtime 2.0.0 - generated, deterministic per frame */
(function installMotion(global) {
  "use strict";
  var TAU = Math.PI * 2;

  function mulberry32(a) {
    return function () {
      a |= 0; a = (a + 0x6d2b79f5) | 0;
      var t = Math.imul(a ^ (a >>> 15), 1 | a);
      t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
      return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
    };
  }

  function hexRgb(h) {
    h = String(h || "#000000");
    return [parseInt(h.slice(1, 3), 16) || 0, parseInt(h.slice(3, 5), 16) || 0, parseInt(h.slice(5, 7), 16) || 0];
  }
  function rgba(h, a) { var c = hexRgb(h); return "rgba(" + c[0] + "," + c[1] + "," + c[2] + "," + a + ")"; }

  function fitRect(iw, ih, cw, ch, fit, position) {
    var s;
    if (fit === "contain") s = Math.min(cw / iw, ch / ih);
    else if (fit === "center") s = Math.min(1, Math.max(cw / iw, ch / ih));
    else s = Math.max(cw / iw, ch / ih);
    var w = iw * s, h = ih * s;
    var x = (cw - w) / 2, y = (ch - h) / 2;
    if (position === "top") y = 0;
    if (position === "bottom") y = ch - h;
    if (position === "left") x = 0;
    if (position === "right") x = cw - w;
    return { x: x, y: y, w: w, h: h };
  }

  function makeParticles(seed, count) {
    var rnd = mulberry32(seed >>> 0);
    var list = [];
    for (var i = 0; i < count; i++) {
      list.push({ x: rnd(), y: rnd(), r: 0.6 + rnd() * 2.2, sp: 1 + Math.floor(rnd() * 3), ph: rnd(), sw: rnd(), a: 0.35 + rnd() * 0.6, k: Math.floor(rnd() * 3) });
    }
    return list;
  }

  function create(canvas, cfg) {
    var ctx = canvas.getContext("2d");
    var state = { cfg: cfg, raf: 0, running: false, px: 0.5, py: 0.5, tx: 0.5, ty: 0.5, start: 0 };
    var I = Math.max(0, Math.min(100, cfg.intensity_pct == null ? 50 : cfg.intensity_pct)) / 100;
    var D = Math.max(2000, cfg.duration_ms || 24000);
    var effect = cfg.effect || "none";
    var colors = cfg.colors || {};
    var accent = colors.accent || "#38bdf8", text = colors.text || "#ffffff", bgc = colors.background || "#000000";
    var particles = makeParticles(cfg.seed || 1, Math.round(30 + 110 * I));
    var reduced = false;
    try { reduced = cfg.respect_reduced_motion !== false && global.matchMedia && global.matchMedia("(prefers-reduced-motion: reduce)").matches; } catch (e) { reduced = false; }

    function size() {
      if (cfg.fixedSize) { canvas.width = cfg.fixedSize[0]; canvas.height = cfg.fixedSize[1]; return; }
      var dpr = Math.min(2, global.devicePixelRatio || 1);
      var w = Math.max(1, Math.round(canvas.clientWidth * dpr)), h = Math.max(1, Math.round(canvas.clientHeight * dpr));
      if (canvas.width !== w || canvas.height !== h) { canvas.width = w; canvas.height = h; }
    }

    function drawImage(phi, W, H) {
      var img = cfg.image;
      if (!img) return;
      var iw = img.naturalWidth || img.width, ih = img.naturalHeight || img.height;
      if (!iw || !ih) return;
      var s = 1, dx = 0, dy = 0, c = Math.cos(TAU * phi), sn = Math.sin(TAU * phi);
      switch (effect) {
        case "kenburns": s = 1 + 0.16 * I * (0.5 - 0.5 * c); dx = -0.04 * I * (0.5 - 0.5 * c); dy = -0.03 * I * sn; break;
        case "pan": s = 1 + 0.18 * I; dx = 0.07 * I * sn; dy = 0.015 * I * Math.sin(TAU * 2 * phi); break;
        case "drift": s = 1 + 0.07 * I; dx = 0.03 * I * sn; dy = 0.022 * I * Math.sin(TAU * 2 * phi); break;
        case "breathe": s = 1 + 0.06 * I * (0.5 - 0.5 * c); break;
        case "parallax": s = 1 + 0.1 * I; dx = (0.5 - state.px) * 0.08 * I + 0.012 * I * sn; dy = (0.5 - state.py) * 0.06 * I + 0.01 * I * Math.sin(TAU * 2 * phi); break;
        case "none": break;
        default: s = 1 + 0.04 * I; dx = 0.01 * I * sn; dy = 0.008 * I * Math.sin(TAU * 2 * phi);
      }
      ctx.save();
      if (cfg.blur_px) { try { ctx.filter = "blur(" + Math.round(cfg.blur_px * (W / 1600)) + "px)"; } catch (e) {} }
      if (cfg.fit === "tile") {
        var tw = Math.max(16, iw * (W / 1600)), th = ih * (tw / iw);
        var ox = (((dx * W) % tw) + tw) % tw - tw, oy = (((dy * H) % th) + th) % th - th;
        for (var yy = oy; yy < H; yy += th) for (var xx = ox; xx < W; xx += tw) ctx.drawImage(img, xx, yy, tw, th);
      } else {
        var r = fitRect(iw, ih, W, H, cfg.fit || "cover", cfg.position || "center");
        var cx = r.x + r.w / 2, cy = r.y + r.h / 2;
        var w = r.w * s, h = r.h * s;
        ctx.drawImage(img, cx - w / 2 + dx * W, cy - h / 2 + dy * H, w, h);
      }
      ctx.restore();
    }

    function drawOverlayFx(phi, W, H) {
      var u = Math.min(W, H) / 900;
      var i, p, x, y;
      if (effect === "particles" || effect === "fireflies") {
        for (i = 0; i < particles.length; i++) {
          p = particles[i];
          y = (((p.y - phi * p.sp) % 1) + 1) % 1;
          x = p.x + 0.015 * Math.sin(TAU * (phi * p.sp + p.ph));
          var tw = 0.5 + 0.5 * Math.sin(TAU * (phi * (p.sp + 1) + p.ph));
          var col = p.k === 0 ? text : accent;
          if (effect === "fireflies") {
            x = p.x + 0.05 * Math.sin(TAU * (phi + p.ph)); y = p.y + 0.04 * Math.sin(TAU * (2 * phi + p.sw));
            var R = (4 + p.r * 4) * u;
            var g = ctx.createRadialGradient(x * W, y * H, 0, x * W, y * H, R);
            g.addColorStop(0, rgba(accent, (0.25 + 0.7 * tw) * p.a));
            g.addColorStop(1, rgba(accent, 0));
            ctx.fillStyle = g; ctx.beginPath(); ctx.arc(x * W, y * H, R, 0, TAU); ctx.fill();
          } else {
            ctx.fillStyle = rgba(col, (0.25 + 0.75 * tw) * p.a * (0.5 + I / 2));
            ctx.beginPath(); ctx.arc(x * W, y * H, p.r * u * 1.4, 0, TAU); ctx.fill();
          }
        }
      } else if (effect === "snow") {
        for (i = 0; i < particles.length; i++) {
          p = particles[i];
          y = (((p.y + phi * p.sp) % 1) + 1) % 1;
          x = p.x + 0.02 * Math.sin(TAU * (phi * 2 + p.ph));
          ctx.fillStyle = "rgba(255,255,255," + (0.45 + 0.5 * p.a) * (0.6 + I * 0.4) + ")";
          ctx.beginPath(); ctx.arc(x * W, y * H, (1 + p.r) * u * 1.6, 0, TAU); ctx.fill();
        }
      } else if (effect === "rain") {
        ctx.strokeStyle = rgba(text, 0.18 + 0.25 * I); ctx.lineWidth = Math.max(1, u * 1.2);
        ctx.beginPath();
        for (i = 0; i < particles.length; i++) {
          p = particles[i];
          y = (((p.y + phi * (p.sp + 3) * 2) % 1) + 1) % 1;
          x = p.x;
          var len = (18 + p.r * 10) * u;
          ctx.moveTo(x * W, y * H); ctx.lineTo(x * W - len * 0.18, y * H + len);
        }
        ctx.stroke();
      } else if (effect === "aurora") {
        ctx.save(); ctx.globalCompositeOperation = "screen";
        var blobs = [[accent, 0.0, 0.25, 0.3], [text, 0.33, 0.7, 0.22], [accent, 0.66, 0.5, 0.26]];
        for (i = 0; i < blobs.length; i++) {
          var b = blobs[i];
          x = 0.5 + 0.32 * Math.sin(TAU * (phi + b[1]));
          y = b[2] + 0.12 * Math.sin(TAU * (2 * phi + b[1]));
          var rr = Math.max(W, H) * 0.55;
          var gg = ctx.createRadialGradient(x * W, y * H, 0, x * W, y * H, rr);
          gg.addColorStop(0, rgba(b[0], b[3] * (0.4 + I)));
          gg.addColorStop(1, rgba(b[0], 0));
          ctx.fillStyle = gg; ctx.fillRect(0, 0, W, H);
        }
        ctx.restore();
      } else if (effect === "shimmer") {
        ctx.save(); ctx.globalCompositeOperation = "screen";
        var pos = -0.5 + 2 * phi;
        var lg = ctx.createLinearGradient((pos - 0.3) * W, 0, (pos + 0.3) * W, H);
        lg.addColorStop(0, "rgba(255,255,255,0)");
        lg.addColorStop(0.5, rgba(text, 0.08 + 0.22 * I));
        lg.addColorStop(1, "rgba(255,255,255,0)");
        ctx.fillStyle = lg; ctx.fillRect(0, 0, W, H);
        ctx.restore();
      } else if (effect === "breathe") {
        ctx.fillStyle = rgba(bgc, 0.12 * I * (0.5 + 0.5 * Math.cos(TAU * phi)));
        ctx.fillRect(0, 0, W, H);
      }
    }

    function renderAt(ms) {
      size();
      var W = canvas.width, H = canvas.height;
      var phi = ((ms % D) + D) % D / D;
      state.px += (state.tx - state.px) * 0.05; state.py += (state.ty - state.py) * 0.05;
      ctx.setTransform(1, 0, 0, 1, 0, 0);
      ctx.fillStyle = bgc; ctx.fillRect(0, 0, W, H);
      drawImage(phi, W, H);
      if (cfg.overlay_opacity_pct) { ctx.fillStyle = rgba(cfg.overlay_color || bgc, cfg.overlay_opacity_pct / 100); ctx.fillRect(0, 0, W, H); }
      drawOverlayFx(phi, W, H);
    }

    function frame(now) {
      if (!state.running) return;
      if (!state.start) state.start = now;
      renderAt(now - state.start);
      state.raf = global.requestAnimationFrame(frame);
    }
    function onMove(e) { state.tx = e.clientX / (global.innerWidth || 1); state.ty = e.clientY / (global.innerHeight || 1); }
    function onVis() { if (global.document && global.document.hidden) stop(); else if (state.wanted) start(); }

    function start() {
      state.wanted = true;
      if (reduced || effect === "none") { renderAt(0); return api; }
      if (state.running) return api;
      state.running = true; state.start = 0;
      state.raf = global.requestAnimationFrame(frame);
      return api;
    }
    function stop() { state.running = false; if (state.raf) global.cancelAnimationFrame(state.raf); state.raf = 0; return api; }
    function destroy() { state.wanted = false; stop(); try { global.removeEventListener("pointermove", onMove); global.document.removeEventListener("visibilitychange", onVis); } catch (e) {} }
    try { global.addEventListener("pointermove", onMove); global.document.addEventListener("visibilitychange", onVis); } catch (e) {}

    var api = { start: start, stop: stop, renderAt: renderAt, destroy: destroy, duration: D, reduced: reduced };
    return api;
  }

  global.ThemeForgeMotion = { create: create, version: "2.0.0" };
  return global.ThemeForgeMotion;
})(typeof window !== "undefined" ? window : globalThis);
