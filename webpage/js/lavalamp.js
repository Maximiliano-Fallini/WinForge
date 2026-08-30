/* ============================================================
   WinForge — lavalamp.js (v5 · Goo DOM)
   Lámpara de lava tipo Netlify/Awwwards:
   - Blobs con gradientes VECTORIALES (cero pixelado)
   - Fusión orgánica real mediante filtro SVG "goo"
     (feGaussianBlur + feColorMatrix que afila SOLO el alfa)
   - Ciclo de vida de gotas + charco abajo
   - Reacción al mouse con resorte amortiguado + parallax global
   - Auto-calidad según FPS · pausa en pestaña oculta
   Depuración: window.__LAVA_DEBUG {frames, blobRects()}
   ============================================================ */
(() => {
  "use strict";

  const scene = document.getElementById("lavaScene");
  if (!scene) return;

  const mqlReduced = window.matchMedia("(prefers-reduced-motion: reduce)");
  let motionK = mqlReduced.matches ? 0.45 : 1;      // nunca 0: jamás se congela
  mqlReduced.addEventListener?.("change", () => {
    motionK = mqlReduced.matches ? 0.45 : 1;
  });

  /* ---------- filtro goo inyectado ---------- */
  const NS = "http://www.w3.org/2000/svg";
  const svg = document.createElementNS(NS, "svg");
  svg.setAttribute("width", "0");
  svg.setAttribute("height", "0");
  svg.style.position = "absolute";
  const filt = document.createElementNS(NS, "filter");
  filt.id = "wf-goo";
  const blur = document.createElementNS(NS, "feGaussianBlur");
  blur.setAttribute("in", "SourceGraphic");
  blur.setAttribute("stdDeviation", "16");
  blur.setAttribute("result", "b");
  const cm = document.createElementNS(NS, "feColorMatrix");
  cm.setAttribute("in", "b");
  // Última fila multiplica alfa x26 y resta 11 → bordes nítidos SIN tocar color
  cm.setAttribute("values",
    "1 0 0 0 0  0 1 0 0 0  0 0 1 0 0  0 0 0 26 -11");
  filt.append(blur, cm);
  svg.append(filt);
  document.body.prepend(svg);

  /* ---------- utilidades ---------- */
  const rand = (a, b) => a + Math.random() * (b - a);
  const clamp = (v, a, b) => (v < a ? a : v > b ? b : v);
  const easeIO = (s) => s * s * (3 - 2 * s);

  /* Paletas de cera (centro caliente → borde) */
  const P_AMBER   = ["#ffe3b0", "#ffbd57", "#ff8a3d", "rgba(255,96,60,.88)", "rgba(255,70,60,0)"];
  const P_MAGENTA = ["#ffc6ea", "#fa85c4", "#ee4f9d", "rgba(216,60,142,.9)", "rgba(190,52,128,0)"];
  const P_VIOLET  = ["#ecd6ff", "#cf9dff", "#ab72ff", "rgba(158,92,255,.85)", "rgba(140,80,240,0)"];
  const PALETTES = [P_AMBER, P_AMBER, P_MAGENTA, P_VIOLET];

  /* ---------- construcción de blobs ---------- */
  let VW = innerWidth, VH = innerHeight;

  const blobs = [];
  function addBlob(cfg) {
    const el = document.createElement("div");
    el.className = "wf-blob" + (cfg.kind ? " " + cfg.kind : "");
    scene.appendChild(el);
    const b = Object.assign({ el, ox: 0, oy: 0, ovx: 0, ovy: 0 }, cfg);
    b.pal = cfg.p || cfg.pal;          // unifica nombre de paleta
    b.y0 = cfg.y;                       // base vertical (para charcos)
    paint(b);
    blobs.push(b);
    return b;
  }
  function paint(b) {
    b.el.style.width = b.w + "px";
    b.el.style.height = b.h + "px";
    b.el.style.background =
      `radial-gradient(circle at 36% 32%, ${b.pal[0]} 0%, ${b.pal[1]} 22%, ${b.pal[2]} 47%, ${b.pal[3]} 61%, ${b.pal[4]} 73%)`;
  }

  /* Charco de cera abajo (2 masas anchas + 1 apoyo) */
  addBlob({ kind: "puddle", p: PALETTES[0], x: 0.50, y: 1.00, fw: 0.58, fh: 0.17, sp1: rand(0, 6.28) });
  addBlob({ kind: "puddle", p: PALETTES[0], x: 0.20, y: 1.02, fw: 0.34, fh: 0.115, sp1: rand(0, 6.28) });
  addBlob({ kind: "puddle", p: PALETTES[2], x: 0.78, y: 1.03, fw: 0.30, fh: 0.105, sp1: rand(0, 6.28) });

  /* Gotas: ciclo de vida completo */
  function addDrop(sizes, i, n, perA, perB) {
    addBlob({
      kind: sizes,
      p: PALETTES[(i + (sizes === "sm" ? 1 : 0)) % PALETTES.length],
      x: rand(0.14, 0.86),
      yBot: rand(0.86, 0.94),
      yTop: sizes === "lg" ? rand(0.15, 0.33) : sizes === "md" ? rand(0.24, 0.44) : rand(0.22, 0.5),
      dsz: sizes,
      period: rand(perA, perB) / Math.max(motionK, 0.35),
      phase: i / n + rand(-0.06, 0.06),
      swayA: rand(0.014, 0.04), swayF: rand(0.07, 0.17), sp1: rand(0, 6.28)
    });
  }
  for (let i = 0; i < 3; i++) addDrop("lg", i, 3, 32, 46);
  for (let i = 0; i < 3; i++) addDrop("md", i, 3, 23, 33);
  for (let i = 0; i < 4; i++) addDrop("sm", i, 4, 18, 27);

  /* Dimensionado en píxeles (según viewport) */
  const BASE = () => Math.min(VW, VH);
  function layout() {
    VW = innerWidth; VH = innerHeight;
    for (const b of blobs) {
      if (b.kind === "puddle") {
        b.w = Math.round(VW * b.fw);
        b.h = Math.round(VH * b.fh);
      } else if (!b.w) {                // tamaño estable tras el primer cálculo
        const k = b.dsz === "lg" ? rand(0.21, 0.29)
                : b.dsz === "md" ? rand(0.135, 0.185)
                : rand(0.085, 0.125);
        b.w = b.h = Math.round(BASE() * k);
      }
      paint(b);
    }
  }
  addEventListener("resize", layout);
  layout();

  window.__LAVA_DEBUG = {
    frames: 0,
    blobRects: () => blobs.map((b) => { const r = b.el.getBoundingClientRect(); return [+r.x.toFixed(1), +r.y.toFixed(1)]; })
  };

  /* ---------- ciclo de vida de una gota ----------
     u∈[0,1): calienta abajo → sube estirada → flota → baja compacta */
  function lifecycle(b, sec) {
    const u = ((sec / b.period) + b.phase) % 1;
    if (u < 0.14) return { y: b.yBot + Math.sin(sec * 2.1 + b.sp1) * 0.004, stretch: 0.97 };
    if (u < 0.52) {
      const q = (u - 0.14) / 0.38;
      return { y: b.yBot + (b.yTop - b.yBot) * easeIO(q), stretch: 1 + 0.5 * Math.sin(Math.PI * q) };
    }
    if (u < 0.66) return { y: b.yTop + Math.sin(sec * 2.3 + b.sp1) * 0.005, stretch: 0.96 };
    const q = (u - 0.66) / 0.34;
    return { y: b.yTop + (b.yBot - b.yTop) * easeIO(q), stretch: 1 + 0.22 * Math.sin(Math.PI * q) };
  }

  /* ---------- bucle principal ---------- */
  let rafId = 0, paused = false;
  let perfAcc = 0, perfN = 0, lite = false;
  const t0 = performance.now();
  let lastT = t0;
  let parX = 0, parY = 0;
  const mouse = { x: 0.5, y: 0.6, active: false };

  function frame(tNow) {
    D.frames++;
    const dtRaw = (tNow - lastT) / 1000;
    lastT = tNow;
    if (!paused) {
      const dt = clamp(dtRaw, 0.001, 0.05);
      const sec = (tNow - t0) / 1000;

      for (const b of blobs) {
        /* movimiento orgánico */
        if (b.kind === "puddle") {
          b.y = b.y0 + Math.sin(sec * 1.4 + (b.sp1 || 0)) * 0.0045 * motionK;
        } else {
          const lf = lifecycle(b, sec);
          b.y = lf.y;
          b.stretch = lf.stretch * (1 + 0.04 * Math.sin(sec * 1.7 + b.sp1));
        }

        /* balanceo lateral lento */
        if (b.kind !== "puddle") {
          b.x += Math.sin(sec * b.swayF * 6.283 + b.sp1) * b.swayA * dt * 8 * motionK;
          b.x = clamp(b.x, 0.05, 0.95);
        }

        /* resorte: el cursor aparta la cera suavemente */
        let tx = 0, ty = 0;
        if (mouse.active) {
          const dxp = (b.x + b.ox) - mouse.x;
          const dyp = (b.y + b.oy) - mouse.y;
          const R = 0.17;
          const d2 = dxp * dxp + dyp * dyp;
          if (d2 < R * R && d2 > 1e-7) {
            const d = Math.sqrt(d2) + 1e-4;
            const f = ((R - d) / R) ** 2 * 0.085;
            tx = (dxp / d) * f;
            ty = (dyp / d) * f * 0.85;
          }
        }
        b.ovx += ((tx - b.ox) * 24 - b.ovx * 7.2) * dt;
        b.ovy += ((ty - b.oy) * 24 - b.ovy * 7.2) * dt;
        b.ox += b.ovx * dt;
        b.oy += b.ovy * dt;

        /* aplicamos transform (una sola escritura por frame) */
        const px = (b.x + b.ox) * VW - b.w / 2;
        const py = (b.y + b.oy) * VH - b.h / 2;
        const st = b.stretch || 1;
        const sx = 1 / Math.sqrt(st), sy = st;
        b.el.style.transform =
          `translate3d(${px.toFixed(1)}px,${py.toFixed(1)}px,0) scale(${sx.toFixed(3)},${sy.toFixed(3)})`;
      }

      /* parallax global sutil opuesto al cursor */
      const txp = mouse.active ? (mouse.x - 0.5) * -16 : 0;
      const typ = mouse.active ? (mouse.y - 0.5) * -10 : 0;
      parX += (txp - parX) * Math.min(dt * 3.2, 1);
      parY += (typ - parY) * Math.min(dt * 3.2, 1);
      scene.style.setProperty("--parx", parX.toFixed(2) + "px");
      scene.style.setProperty("--pary", parY.toFixed(2) + "px");

      quality(dtRaw);
    }
    schedule();
  }
  function schedule() { rafId = requestAnimationFrame(frame); }

  const D = window.__LAVA_DEBUG;

  /* ---------- auto-calidad ---------- */
  function quality(dtRaw) {
    perfAcc += dtRaw; perfN++;
    if (perfN >= 120) {
      const avg = perfAcc / perfN; perfAcc = 0; perfN = 0;
      if (!lite && avg > 0.03) {          // <33 fps → rebajar
        lite = true;
        blur.setAttribute("stdDeviation", "11");
        scene.classList.add("lite");
        document.querySelectorAll(".wf-blob.sm").forEach((el, i) => {
          if (i % 2 === 0) el.style.display = "none";
        });
      }
    }
  }

  /* arranque y ciclo de vida de la página */
  schedule();

  document.addEventListener("visibilitychange", () => {
    if (document.hidden) { paused = true; cancelAnimationFrame(rafId); }
    else { paused = false; lastT = performance.now(); cancelAnimationFrame(rafId); schedule(); }
  });

  addEventListener("mousemove", (e) => {
    mouse.x = e.clientX / VW;
    mouse.y = e.clientY / VH;
    mouse.active = true;
  });
  document.addEventListener("mouseleave", () => { mouse.active = false; });
})();
