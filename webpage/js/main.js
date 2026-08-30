/* ============================================================
   WinForge — main.js
   - Navbar con scroll + menú móvil
   - Animaciones de aparición al hacer scroll
   - Efecto hover con posición del mouse en tarjetas
   - (La lámpara de lava real vive en lavalamp.js)
   ============================================================ */
(() => {
  "use strict";

  const prefersReducedMotion = window.matchMedia("(prefers-reduced-motion: reduce)").matches;

  /* ---------------------------------------------------------
     2) NAVBAR — scroll + menú móvil
     --------------------------------------------------------- */
  const nav = document.getElementById("nav");
  const burger = document.getElementById("navBurger");
  const navLinks = document.getElementById("navLinks");

  const onScroll = () => {
    nav.classList.toggle("scrolled", window.scrollY > 10);
  };
  window.addEventListener("scroll", onScroll, { passive: true });
  onScroll();

  burger.addEventListener("click", () => {
    const open = navLinks.classList.toggle("open");
    burger.classList.toggle("open", open);
    document.body.classList.toggle("nav-open", open);
  });

  navLinks.querySelectorAll("a").forEach((a) => {
    a.addEventListener("click", () => {
      navLinks.classList.remove("open");
      burger.classList.remove("open");
      document.body.classList.remove("nav-open");
    });
  });

  /* ---------------------------------------------------------
     3) ANIMACIONES DE APARICIÓN (Intersection Observer)
     --------------------------------------------------------- */
  const revealEls = document.querySelectorAll(".reveal");
  if (!("IntersectionObserver" in window) || prefersReducedMotion) {
    revealEls.forEach((el) => el.classList.add("visible"));
  } else {
    const io = new IntersectionObserver(
      (entries) => {
        entries.forEach((entry) => {
          if (entry.isIntersecting) {
            entry.target.classList.add("visible");
            io.unobserve(entry.target);
          }
        });
      },
      { threshold: 0.12, rootMargin: "0px 0px -60px 0px" }
    );
    revealEls.forEach((el) => io.observe(el));
  }

  /* ---------------------------------------------------------
     4) HOVER CON POSICIÓN DEL MOUSE EN TARJETAS
     (iluminación que sigue al cursor sobre la tarjeta)
     --------------------------------------------------------- */
  document.querySelectorAll(".feature-card").forEach((card) => {
    card.addEventListener("mousemove", (e) => {
      const rect = card.getBoundingClientRect();
      card.style.setProperty("--mx", ((e.clientX - rect.left) / rect.width) * 100 + "%");
      card.style.setProperty("--my", ((e.clientY - rect.top) / rect.height) * 100 + "%");
    });
  });

  /* ---------------------------------------------------------
     5) LINK DESCARGA — feedback visual al hacer click
     --------------------------------------------------------- */
  document.querySelectorAll('a[href$=".msi"]').forEach((link) => {
    link.addEventListener("click", function () {
      const original = this.innerHTML;
      this.innerHTML = "Descargando…";
      setTimeout(() => {
        this.innerHTML = original;
      }, 2500);
    });
  });
})();