(() => {
  "use strict";

  const body = document.body;
  const reducedMotion = window.matchMedia("(prefers-reduced-motion: reduce)");
  const connection = navigator.connection || navigator.mozConnection || navigator.webkitConnection;

  const entry = document.querySelector("[data-entry-sequence]");
  const entrySkip = document.querySelector("[data-entry-skip]");
  const introSurfaces = Array.from(document.querySelectorAll(".skip-link, [data-header], main, footer"));
  let entryTimer = 0;
  let entrySeen = false;

  try {
    entrySeen = window.sessionStorage.getItem("zapret-intro-seen") === "1";
  } catch (_) {
    entrySeen = false;
  }

  const finishEntry = (moveFocus = false) => {
    if (!entry || entry.classList.contains("is-dismissed")) return;

    window.clearTimeout(entryTimer);
    entry.classList.add("is-dismissed");
    entry.setAttribute("aria-hidden", "true");
    body.classList.remove("has-intro");
    introSurfaces.forEach((surface) => { surface.inert = false; });
    try {
      window.sessionStorage.setItem("zapret-intro-seen", "1");
    } catch (_) {
      // Storage can be unavailable in strict privacy modes.
    }
    window.setTimeout(() => {
      entry.hidden = true;
      if (moveFocus) {
        document.querySelector("[data-header] a")?.focus({ preventScroll: true });
      }
    }, reducedMotion.matches ? 0 : 650);
  };

  if (entry) {
    if (entrySeen || reducedMotion.matches) {
      entry.hidden = true;
      entry.setAttribute("aria-hidden", "true");
    } else {
      body.classList.add("has-intro");
      introSurfaces.forEach((surface) => { surface.inert = true; });
      entryTimer = window.setTimeout(() => finishEntry(false), 1750);
      entrySkip?.addEventListener("click", () => finishEntry(true));
      entry.addEventListener("click", (event) => {
        if (event.target === entry) finishEntry(true);
      });
      document.addEventListener("keydown", (event) => {
        if (!entry.hidden && ["Escape", "Enter", " "].includes(event.key)) {
          event.preventDefault();
          finishEntry(true);
        }
      });
    }
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
    }, 12500);
  };

  const resetCinema = () => {
    window.clearTimeout(sceneTimer);
    cinemaScenes.forEach((scene, index) => scene.classList.toggle("is-active", index === 0));
    sceneIndex = 0;
    scheduleScene();
  };

  if (cinema && hero) {
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

  const relayStory = document.querySelector("[data-relay-story]");
  const relayBits = Array.from(relayStory?.querySelectorAll(".relay-bit") || []);
  const relaySteps = Array.from(relayStory?.querySelectorAll("[data-relay-step]") || []);
  const terminalScreen = relayStory?.querySelector("[data-terminal-screen]");
  const terminalOutput = relayStory?.querySelector("[data-terminal-output]");
  const terminalForm = relayStory?.querySelector("[data-terminal-form]");
  const terminalInput = relayStory?.querySelector("[data-terminal-input]");
  const terminalTitle = relayStory?.querySelector("[data-terminal-title]");
  const terminalEgg = relayStory?.querySelector("[data-terminal-egg]");
  const terminalExit = relayStory?.querySelector("[data-terminal-exit]");
  const compactRelay = window.matchMedia("(max-width: 820px)");
  let relayVisible = true;
  let relayFrame = 0;
  let terminalEnabled = false;

  const clamp = (value, minimum = 0, maximum = 1) => Math.min(maximum, Math.max(minimum, value));
  const smooth = (value) => value * value * (3 - 2 * value);
  const phase = (value, start, end) => smooth(clamp((value - start) / (end - start)));
  const mix = (start, end, amount) => start + (end - start) * amount;

  const setTerminalEnabled = (enabled) => {
    if (!terminalInput || !terminalScreen || terminalEnabled === enabled) return;
    terminalEnabled = enabled;
    terminalInput.disabled = !enabled;
    terminalScreen.setAttribute("aria-disabled", String(!enabled));
  };

  const renderRelay = () => {
    relayFrame = 0;
    if (!relayStory) return;

    const staticScene = reducedMotion.matches || compactRelay.matches;
    const rect = relayStory.getBoundingClientRect();
    const travel = Math.max(1, relayStory.offsetHeight - window.innerHeight);
    const progress = staticScene ? 0.88 : clamp(-rect.top / travel);
    const open = phase(progress, 0.06, 0.68);
    const flow = phase(progress, 0.25, 0.96);
    const turn = phase(progress, 0.04, 0.93);
    const arrive = phase(progress, 0.03, 0.86);
    setTerminalEnabled(staticScene || progress >= 0.38);

    relayStory.style.setProperty("--relay-flow", flow.toFixed(4));
    relayStory.style.setProperty("--relay-turn", `${mix(-46, 18, turn).toFixed(2)}deg`);
    relayStory.style.setProperty("--relay-pitch", `${mix(24, -6, turn).toFixed(2)}deg`);
    relayStory.style.setProperty("--relay-roll", `${mix(-5, 1, turn).toFixed(2)}deg`);
    relayStory.style.setProperty("--relay-scale", mix(0.7, 1.06, arrive).toFixed(4));
    relayStory.style.setProperty("--relay-lift", `${mix(82, -24, arrive).toFixed(2)}px`);
    relayStory.style.setProperty("--relay-depth", `${mix(-180, 24, arrive).toFixed(2)}px`);
    relayStory.style.setProperty("--relay-laptop-opacity", mix(0.52, 0.96, arrive).toFixed(3));
    relayStory.style.setProperty("--relay-lid", `${mix(-82, 0, open).toFixed(2)}deg`);
    relayStory.style.setProperty("--relay-code-y", `${mix(0, -26, flow).toFixed(2)}px`);
    relayStory.style.setProperty("--relay-cloud-y", `${mix(-30, 0, flow).toFixed(2)}px`);
    relayStory.style.setProperty("--relay-cloud-opacity", mix(0.12, 0.9, flow).toFixed(3));
    relayStory.style.setProperty("--relay-screen-glow", mix(0.02, 0.24, flow).toFixed(3));

    const activeStep = Math.min(relaySteps.length - 1, Math.floor(Math.min(progress, 0.999) * relaySteps.length));
    relaySteps.forEach((step, index) => {
      const active = index === activeStep;
      step.classList.toggle("is-active", active);
      if (active) step.setAttribute("aria-current", "step");
      else step.removeAttribute("aria-current");
    });

    const visibleFlow = phase(flow, 0.01, 0.12);
    relayBits.forEach((bit, index) => {
      const distance = (flow * 1.58 + index / relayBits.length) % 1;
      bit.style.offsetDistance = `${(distance * 100).toFixed(2)}%`;
      bit.style.opacity = (Math.sin(distance * Math.PI) * visibleFlow * 0.96).toFixed(3);
    });

    relayStory.classList.toggle("is-active", relayVisible && !staticScene);
  };

  const requestRelayRender = () => {
    if (!relayStory || relayFrame || (!relayVisible && !compactRelay.matches)) return;
    relayFrame = window.requestAnimationFrame(renderRelay);
  };

  if (relayStory) {
    const relayObserver = typeof IntersectionObserver === "function"
      ? new IntersectionObserver(([entryState]) => {
          relayVisible = Boolean(entryState?.isIntersecting);
          requestRelayRender();
        }, { rootMargin: "20% 0px" })
      : null;

    relayObserver?.observe(relayStory);
    window.addEventListener("scroll", requestRelayRender, { passive: true });
    window.addEventListener("resize", requestRelayRender, { passive: true });
    reducedMotion.addEventListener?.("change", requestRelayRender);
    compactRelay.addEventListener?.("change", requestRelayRender);
    connection?.addEventListener?.("change", requestRelayRender);
    renderRelay();
  }

  if (terminalScreen && terminalOutput && terminalForm && terminalInput && terminalEgg) {
    const terminalHistory = [];
    let historyIndex = 0;

    const appendTerminalLine = (text, tone = "") => {
      const line = document.createElement("span");
      line.textContent = text;
      if (tone) line.className = tone;
      terminalOutput.append(line);
      while (terminalOutput.children.length > 11) terminalOutput.firstElementChild?.remove();
    };

    const closeTerminalEgg = (refocus = true) => {
      terminalScreen.classList.remove("is-fsociety");
      terminalEgg.setAttribute("aria-hidden", "true");
      if (terminalTitle) terminalTitle.textContent = "winws / route.trace";
      if (refocus && !terminalInput.disabled) terminalInput.focus({ preventScroll: true });
    };

    const openTerminalEgg = () => {
      terminalScreen.classList.remove("is-fsociety");
      void terminalScreen.offsetWidth;
      terminalScreen.classList.add("is-fsociety");
      terminalEgg.setAttribute("aria-hidden", "false");
      if (terminalTitle) terminalTitle.textContent = "fsociety / local.session";
    };

    const runTerminalCommand = (rawCommand) => {
      const command = rawCommand.trim().toLowerCase();
      appendTerminalLine(`guest@zapret:~$ ${rawCommand.trim()}`, "is-command");

      switch (command) {
        case "help":
          appendTerminalLine("status · trace · whoami · version · clear");
          break;
        case "status":
          appendTerminalLine("service active · route local · strategy general", "is-success");
          break;
        case "trace":
          appendTerminalLine("LOCAL > SPLIT > CHECK > READY", "is-success");
          break;
        case "whoami":
          appendTerminalLine("visitor · access local");
          break;
        case "version":
          appendTerminalLine("zapret control center · web build 4");
          break;
        case "clear":
          closeTerminalEgg(false);
          terminalOutput.replaceChildren();
          break;
        case "fsociety":
          appendTerminalLine("access granted · local session", "is-success");
          openTerminalEgg();
          break;
        default:
          appendTerminalLine("неизвестная команда · help", "is-error");
      }
    };

    terminalForm.addEventListener("submit", (event) => {
      event.preventDefault();
      const value = terminalInput.value.trim();
      if (!value) return;
      terminalHistory.push(value);
      if (terminalHistory.length > 16) terminalHistory.shift();
      historyIndex = terminalHistory.length;
      terminalInput.value = "";
      runTerminalCommand(value);
    });

    terminalInput.addEventListener("keydown", (event) => {
      if (event.key === "Escape") {
        if (terminalScreen.classList.contains("is-fsociety")) closeTerminalEgg();
        else terminalInput.blur();
        return;
      }

      if (event.key !== "ArrowUp" && event.key !== "ArrowDown") return;
      event.preventDefault();
      historyIndex = event.key === "ArrowUp"
        ? Math.max(0, historyIndex - 1)
        : Math.min(terminalHistory.length, historyIndex + 1);
      terminalInput.value = terminalHistory[historyIndex] || "";
      terminalInput.setSelectionRange(terminalInput.value.length, terminalInput.value.length);
    });

    terminalScreen.addEventListener("click", (event) => {
      if (!terminalEnabled || event.target.closest("button, input")) return;
      terminalInput.focus({ preventScroll: true });
    });

    terminalExit?.addEventListener("click", () => closeTerminalEgg());
  }

  const appViewport = document.querySelector("[data-app-viewport]");
  const appInspectLabel = document.querySelector("[data-app-inspect-label]");

  if (appViewport) {
    let appZoomed = false;

    const positionAppInspector = (event) => {
      const rect = appViewport.getBoundingClientRect();
      if (!rect.width || !rect.height) return;
      const x = clamp(((event.clientX - rect.left) / rect.width) * 100, 8, 92);
      const y = clamp(((event.clientY - rect.top) / rect.height) * 100, 10, 90);
      appViewport.style.setProperty("--inspect-x", `${x.toFixed(2)}%`);
      appViewport.style.setProperty("--inspect-y", `${y.toFixed(2)}%`);
    };

    const setAppZoom = (zoomed) => {
      appZoomed = zoomed;
      appViewport.classList.toggle("is-zoomed", zoomed);
      appViewport.setAttribute("aria-pressed", String(zoomed));
      appViewport.setAttribute("aria-label", zoomed ? "Вернуть полный снимок интерфейса" : "Увеличить снимок интерфейса");
      if (appInspectLabel) appInspectLabel.textContent = zoomed ? "Двигайте курсор · нажмите, чтобы вернуть" : "Нажмите на экран · увеличить";
    };

    appViewport.addEventListener("pointermove", (event) => {
      if (event.pointerType !== "touch") positionAppInspector(event);
    }, { passive: true });
    appViewport.addEventListener("click", (event) => {
      if (event.detail > 0) positionAppInspector(event);
      else {
        appViewport.style.setProperty("--inspect-x", "50%");
        appViewport.style.setProperty("--inspect-y", "50%");
      }
      setAppZoom(!appZoomed);
    });
  }

  const listsPaper = document.querySelector(".lists-visual__paper");
  const listButtons = Array.from(document.querySelectorAll("[data-list-focus]"));

  if (listsPaper && listButtons.length) {
    const frames = {
      tabs: { x: 17.5, y: 25, w: 29, h: 9 },
      guard: { x: 59, y: 80.5, w: 24, h: 9 },
      storage: { x: 17, y: 47.5, w: 66, h: 27.5 },
    };
    let selectedFrame = listButtons.find((button) => button.getAttribute("aria-pressed") === "true")?.dataset.listFocus || "tabs";
    let listChangeTimer = 0;

    const showListFrame = (name) => {
      const frame = frames[name] || frames.tabs;
      listsPaper.style.setProperty("--focus-x", `${frame.x}%`);
      listsPaper.style.setProperty("--focus-y", `${frame.y}%`);
      listsPaper.style.setProperty("--focus-w", `${frame.w}%`);
      listsPaper.style.setProperty("--focus-h", `${frame.h}%`);
      listsPaper.classList.remove("is-changing");
      window.clearTimeout(listChangeTimer);
      window.requestAnimationFrame(() => listsPaper.classList.add("is-changing"));
      listChangeTimer = window.setTimeout(() => listsPaper.classList.remove("is-changing"), 560);
    };

    const selectListFrame = (button) => {
      selectedFrame = button.dataset.listFocus || "tabs";
      listButtons.forEach((candidate) => candidate.setAttribute("aria-pressed", String(candidate === button)));
      showListFrame(selectedFrame);
    };

    listButtons.forEach((button) => {
      button.addEventListener("click", () => selectListFrame(button));
      button.addEventListener("pointerenter", () => showListFrame(button.dataset.listFocus));
      button.addEventListener("focus", () => showListFrame(button.dataset.listFocus));
      button.addEventListener("pointerleave", () => showListFrame(selectedFrame));
      button.addEventListener("blur", () => showListFrame(selectedFrame));
    });

    showListFrame(selectedFrame);
    window.addEventListener("pagehide", () => window.clearTimeout(listChangeTimer), { once: true });
  }

  const updateRun = document.querySelector("[data-update-run]");
  const updateRunLabel = document.querySelector("[data-update-run-label]");
  const updateRoute = document.querySelector("[data-update-route]");
  const updateSteps = Array.from(document.querySelectorAll("[data-update-step]"));

  if (updateRun && updateRunLabel && updateRoute && updateSteps.length) {
    let updateTimers = [];

    const clearUpdateTimers = () => {
      updateTimers.forEach((timer) => window.clearTimeout(timer));
      updateTimers = [];
    };

    const finishUpdateCheck = () => {
      updateSteps.forEach((step) => step.classList.replace("is-active", "is-done"));
      updateRoute.classList.remove("is-running");
      updateRoute.setAttribute("aria-busy", "false");
      updateRun.classList.remove("is-running");
      updateRun.classList.add("is-complete");
      updateRun.disabled = false;
      updateRunLabel.textContent = "Проверено · повторить";
    };

    updateRun.addEventListener("click", () => {
      clearUpdateTimers();
      updateSteps.forEach((step) => step.classList.remove("is-active", "is-done"));
      updateRun.classList.remove("is-complete");
      updateRun.classList.add("is-running");
      updateRun.disabled = true;
      updateRoute.classList.add("is-running");
      updateRoute.setAttribute("aria-busy", "true");

      if (reducedMotion.matches) {
        updateSteps.forEach((step) => step.classList.add("is-done"));
        finishUpdateCheck();
        return;
      }

      updateSteps.forEach((step, index) => {
        updateTimers.push(window.setTimeout(() => {
          updateSteps.forEach((candidate, candidateIndex) => {
            candidate.classList.toggle("is-active", candidateIndex === index);
            candidate.classList.toggle("is-done", candidateIndex < index);
          });
          updateRunLabel.textContent = `Проверка ${index + 1}/${updateSteps.length}`;
        }, index * 520));
      });
      updateTimers.push(window.setTimeout(finishUpdateCheck, updateSteps.length * 520 + 180));
    });

    window.addEventListener("pagehide", clearUpdateTimers, { once: true });
  }

  const androidMode = document.querySelector("[data-android-mode]");
  const androidModeLabel = document.querySelector("[data-android-mode-label]");

  if (androidMode && androidModeLabel) {
    const modes = [
      { id: "vpn", label: "VPN" },
      { id: "proxy", label: "PROXY" },
      { id: "auto", label: "AUTO" },
    ];
    let modeIndex = 0;

    androidMode.addEventListener("click", () => {
      modeIndex = (modeIndex + 1) % modes.length;
      const mode = modes[modeIndex];
      androidMode.dataset.mode = mode.id;
      androidModeLabel.textContent = mode.label;
      androidMode.setAttribute("aria-label", `Режим ${mode.label}. Переключить режим`);
    });
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
    ".origin-note, .section-heading, .app-frame, .lists-visual, .lists-copy, .update-route, .install-card, .android-card, .faq-list"
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
    window.cancelAnimationFrame(relayFrame);
  });
})();
