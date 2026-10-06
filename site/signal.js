(() => {
  "use strict";

  const body = document.body;
  const reducedMotion = window.matchMedia("(prefers-reduced-motion: reduce)");
  const finePointer = window.matchMedia("(any-pointer: fine)");
  const connection = navigator.connection || navigator.mozConnection || navigator.webkitConnection;

  const entry = document.querySelector("[data-entry-sequence]");
  const entrySkip = document.querySelector("[data-entry-skip]");
  let entryTimer = 0;

  const finishEntry = () => {
    if (!entry || entry.classList.contains("is-dismissed")) return;

    window.clearTimeout(entryTimer);
    entry.classList.add("is-dismissed");
    body.classList.remove("has-intro");
    window.setTimeout(() => {
      entry.hidden = true;
    }, reducedMotion.matches ? 0 : 650);
  };

  if (entry) {
    body.classList.add("has-intro");
    entryTimer = window.setTimeout(finishEntry, reducedMotion.matches ? 80 : 2350);
    entrySkip?.addEventListener("click", finishEntry);
    entry.addEventListener("click", (event) => {
      if (event.target === entry) finishEntry();
    });
    document.addEventListener("keydown", (event) => {
      if (!entry.hidden && ["Escape", "Enter", " "].includes(event.key)) {
        event.preventDefault();
        finishEntry();
      }
    });
  }

  const cinema = document.querySelector("[data-cinema]");
  const cinemaScenes = Array.from(document.querySelectorAll("[data-cinema-scene]"));
  const hero = cinema?.closest(".hero");
  let sceneIndex = 0;
  let sceneTimer = 0;
  let cinemaVisible = true;

  const motionAllowed = () => !reducedMotion.matches && !connection?.saveData;

  const scheduleScene = () => {
    window.clearTimeout(sceneTimer);
    if (!motionAllowed() || !cinemaVisible || document.hidden || cinemaScenes.length < 2) return;

    sceneTimer = window.setTimeout(() => {
      cinemaScenes[sceneIndex]?.classList.remove("is-active");
      sceneIndex = (sceneIndex + 1) % cinemaScenes.length;
      cinemaScenes[sceneIndex]?.classList.add("is-active");
      scheduleScene();
    }, 8800);
  };

  const resetCinema = () => {
    window.clearTimeout(sceneTimer);
    cinemaScenes.forEach((scene, index) => scene.classList.toggle("is-active", index === 0));
    sceneIndex = 0;
    scheduleScene();
  };

  if (cinema && hero) {
    const updateFilmPosition = (event) => {
      if (!motionAllowed() || !finePointer.matches || event.pointerType === "touch") return;

      const rect = hero.getBoundingClientRect();
      const x = ((event.clientX - rect.left) / rect.width - 0.5) * 2;
      const y = ((event.clientY - rect.top) / rect.height - 0.5) * 2;
      cinema.style.setProperty("--film-x", `${(x * 6).toFixed(2)}px`);
      cinema.style.setProperty("--film-y", `${(y * 4).toFixed(2)}px`);
    };

    const resetFilmPosition = () => {
      cinema.style.setProperty("--film-x", "0px");
      cinema.style.setProperty("--film-y", "0px");
    };

    hero.addEventListener("pointermove", updateFilmPosition, { passive: true });
    hero.addEventListener("pointerleave", resetFilmPosition, { passive: true });
    reducedMotion.addEventListener?.("change", resetCinema);
    connection?.addEventListener?.("change", resetCinema);
    document.addEventListener("visibilitychange", scheduleScene);

    const observer = typeof IntersectionObserver === "function"
      ? new IntersectionObserver(([entryState]) => {
          cinemaVisible = Boolean(entryState?.isIntersecting);
          scheduleScene();
        }, { rootMargin: "80px" })
      : null;
    observer?.observe(hero);
    scheduleScene();
  }

  const dock = document.querySelector("[data-download-dock]");
  const dockPanel = dock?.querySelector(".download-dock__panel");
  const dockStatus = dock?.querySelector("[data-download-status]");
  const openButtons = Array.from(document.querySelectorAll("[data-download-open]"));
  const closeButtons = Array.from(document.querySelectorAll("[data-download-close]"));
  let returnFocus = null;

  const dockFocusable = () => Array.from(
    dock?.querySelectorAll('a[href]:not([hidden]), button:not([disabled]):not([tabindex="-1"])') || []
  ).filter((element) => element.getClientRects().length > 0);

  const openDock = (source) => {
    if (!dock) return;

    returnFocus = source instanceof HTMLElement ? source : document.activeElement;
    dock.hidden = false;
    dock.setAttribute("aria-hidden", "false");
    body.classList.add("download-open");
    dockStatus.textContent = "Выберите файл.";
    window.requestAnimationFrame(() => {
      dock.classList.add("is-open");
      dockPanel?.querySelector("[data-download-link]")?.focus({ preventScroll: true });
    });
  };

  const closeDock = () => {
    if (!dock || dock.getAttribute("aria-hidden") === "true") return;

    dock.classList.remove("is-open", "is-transmitting");
    dock.setAttribute("aria-hidden", "true");
    body.classList.remove("download-open");
    window.setTimeout(() => {
      dock.hidden = true;
      if (returnFocus instanceof HTMLElement) returnFocus.focus({ preventScroll: true });
    }, reducedMotion.matches ? 0 : 420);
  };

  openButtons.forEach((button) => button.addEventListener("click", () => openDock(button)));
  closeButtons.forEach((button) => button.addEventListener("click", closeDock));

  dock?.querySelectorAll("[data-download-link], [data-portable-link]").forEach((link) => {
    link.addEventListener("click", () => {
      dock.classList.add("is-transmitting");
      if (dockStatus) {
        dockStatus.textContent = "Передано браузеру. Если загрузка не началась — откройте страницу релиза.";
      }
      window.setTimeout(() => dock.classList.remove("is-transmitting"), 1400);
    });
  });

  document.addEventListener("keydown", (event) => {
    if (!dock || dock.getAttribute("aria-hidden") === "true") return;

    if (event.key === "Escape") {
      event.preventDefault();
      closeDock();
      return;
    }

    if (event.key !== "Tab") return;
    const focusable = dockFocusable();
    if (!focusable.length) return;

    const first = focusable[0];
    const last = focusable[focusable.length - 1];
    if (event.shiftKey && document.activeElement === first) {
      event.preventDefault();
      last.focus();
    } else if (!event.shiftKey && document.activeElement === last) {
      event.preventDefault();
      first.focus();
    }
  });

  const revealTargets = Array.from(document.querySelectorAll(
    ".origin-note, .section-heading, .capability-list, .app-frame, .lists-visual, .lists-copy, .update-route, .install-card, .android-card, .faq-list"
  ));

  if (!reducedMotion.matches && typeof IntersectionObserver === "function") {
    revealTargets.forEach((target) => target.classList.add("signal-reveal"));
    const revealObserver = new IntersectionObserver((entries, observer) => {
      entries.forEach((entryState) => {
        if (!entryState.isIntersecting) return;
        entryState.target.classList.add("is-revealed");
        observer.unobserve(entryState.target);
      });
    }, { threshold: 0.12, rootMargin: "0px 0px -8%" });
    revealTargets.forEach((target) => revealObserver.observe(target));
  }

  window.addEventListener("pagehide", () => {
    window.clearTimeout(entryTimer);
    window.clearTimeout(sceneTimer);
  });
})();
