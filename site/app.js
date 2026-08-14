(() => {
  "use strict";

  const repository = "lolososka/zapret-discord-youtube";
  const releasePage = `https://github.com/${repository}/releases/latest`;
  const releaseApi = `https://api.github.com/repos/${repository}/releases/latest`;
  const releaseSnapshot = "./release.json";

  const header = document.querySelector("[data-header]");
  const menu = document.querySelector("[data-menu]");
  const menuToggle = document.querySelector("[data-menu-toggle]");

  const setMenuState = (open) => {
    if (!menu || !menuToggle) return;

    menu.classList.toggle("is-open", open);
    menuToggle.setAttribute("aria-expanded", String(open));
    header?.classList.toggle("is-open", open);

    const label = menuToggle.querySelector(".sr-only");
    if (label) {
      label.textContent = open ? "Закрыть меню" : "Открыть меню";
    }
  };

  menuToggle?.addEventListener("click", () => {
    setMenuState(menuToggle.getAttribute("aria-expanded") !== "true");
  });

  menu?.addEventListener("click", (event) => {
    if (event.target.closest("a")) {
      setMenuState(false);
    }
  });

  document.addEventListener("keydown", (event) => {
    if (event.key === "Escape") {
      setMenuState(false);
      menuToggle?.focus();
    }
  });

  const updateHeader = () => {
    header?.classList.toggle("is-scrolled", window.scrollY > 12);
  };

  updateHeader();
  window.addEventListener("scroll", updateHeader, { passive: true });

  const initializePacketField = () => {
    const canvas = document.querySelector("[data-packet-field]");
    const surface = canvas?.closest("[data-field-surface]");
    if (!canvas || !surface) return;

    const context = canvas.getContext("2d", { alpha: true });
    if (!context) return;

    const colors = ["#5B63FF", "#2667FF", "#F04F78", "#F59E0B", "#7856B8"];
    const reducedMotion = window.matchMedia("(prefers-reduced-motion: reduce)");
    const finePointer = window.matchMedia("(any-pointer: fine)");
    const connection = navigator.connection || navigator.mozConnection || navigator.webkitConnection;
    const hasAnimationFrame = typeof window.requestAnimationFrame === "function";
    const quietElements = Array.from(surface.querySelectorAll("[data-field-quiet]"));
    const pointer = {
      active: false,
      targetX: 0,
      targetY: 0,
      x: 0,
      y: 0,
      strength: 0
    };

    let width = 0;
    let height = 0;
    let particles = [];
    let quietZones = [];
    let elapsed = 0;
    let lastFrame = 0;
    let frameRequest = 0;
    let resizeTimer = 0;
    let fieldIsVisible = true;
    let staticField = false;

    const clamp = (value, minimum, maximum) => Math.min(maximum, Math.max(minimum, value));

    const createRandom = (seed) => {
      let state = seed >>> 0;
      return () => {
        state += 0x6d2b79f5;
        let value = state;
        value = Math.imul(value ^ (value >>> 15), value | 1);
        value ^= value + Math.imul(value ^ (value >>> 7), value | 61);
        return ((value ^ (value >>> 14)) >>> 0) / 4294967296;
      };
    };

    const shouldUseStaticField = () =>
      !hasAnimationFrame || reducedMotion.matches || !finePointer.matches || Boolean(connection?.saveData);

    const particleCount = () => {
      const area = width * height;
      return staticField
        ? clamp(Math.round(area / 26000), 36, 54)
        : clamp(Math.round(area / 5600), 90, 220);
    };

    const buildParticles = () => {
      const dimensionSeed = (Math.round(width) * 73856093) ^ (Math.round(height) * 19349663);
      const random = createRandom(0x5a17c0de ^ dimensionSeed);
      const count = particleCount();
      const trackWidth = width + 96;

      particles = Array.from({ length: count }, (_, index) => ({
        originX: random() * trackWidth - 48,
        laneY: random() * height,
        speed: 11 + random() * 21,
        length: 4.5 + random() * 9,
        lineWidth: 1.2 + random() * 1.35,
        alpha: 0.28 + random() * 0.38,
        wave: 1.5 + random() * 7,
        frequency: 0.004 + random() * 0.006,
        phase: random() * Math.PI * 2,
        routeSide: index % 2 === 0 ? -1 : 1,
        color: colors[Math.floor(random() * colors.length)]
      }));
    };

    const measureQuietZones = (canvasRect) => {
      quietZones = quietElements
        .filter((element) => element.getClientRects().length > 0)
        .map((element) => {
          const rect = element.getBoundingClientRect();
          return {
            left: rect.left - canvasRect.left,
            right: rect.right - canvasRect.left,
            top: rect.top - canvasRect.top,
            bottom: rect.bottom - canvasRect.top
          };
        });
    };

    const baseYAt = (particle, x) =>
      particle.laneY + Math.sin(x * particle.frequency + particle.phase) * particle.wave;

    const routeAroundQuietZones = (x, startingY, particle) => {
      let y = startingY;

      quietZones.forEach((zone) => {
        const shoulder = clamp((zone.right - zone.left) * 0.16, 52, 116);
        const routeStart = zone.left - shoulder;
        const routeEnd = zone.right + shoulder;
        if (x <= routeStart || x >= routeEnd) return;

        const envelope = x < zone.left
          ? Math.sin(((x - routeStart) / shoulder) * (Math.PI / 2)) ** 2
          : x > zone.right
            ? Math.sin(((routeEnd - x) / shoulder) * (Math.PI / 2)) ** 2
            : 1;
        const padding = 22;
        const top = zone.top - padding;
        const bottom = zone.bottom + padding;
        if (y <= top - 26 || y >= bottom + 26) return;

        const center = (top + bottom) / 2;
        const laneDistance = particle.laneY - center;
        const side = Math.abs(laneDistance) > 10 ? Math.sign(laneDistance) : particle.routeSide;
        const target = side < 0 ? top : bottom;
        y += (target - y) * envelope;
      });

      return y;
    };

    const routeAroundPointer = (x, startingY, particle) => {
      if (pointer.strength <= 0.001) return startingY;

      const horizontalReach = 190;
      const distanceX = Math.abs(x - pointer.x);
      if (distanceX >= horizontalReach) return startingY;

      const channelRadius = 125;
      const envelope = Math.cos((distanceX / horizontalReach) * (Math.PI / 2));
      const clearance = (channelRadius + 8) * envelope ** 0.72 * pointer.strength;
      const routedDistanceY = startingY - pointer.y;
      if (Math.abs(routedDistanceY) >= clearance) return startingY;

      const laneDistanceY = particle.laneY - pointer.y;
      const side = Math.abs(laneDistanceY) > 9 ? Math.sign(laneDistanceY) : particle.routeSide;
      return pointer.y + side * clearance;
    };

    const routedYAt = (particle, x) => {
      const baseY = baseYAt(particle, x);
      return routeAroundPointer(x, routeAroundQuietZones(x, baseY, particle), particle);
    };

    const draw = () => {
      context.clearRect(0, 0, width, height);
      context.save();
      context.globalCompositeOperation = "multiply";
      context.lineCap = "round";

      const trackWidth = width + 96;
      particles.forEach((particle) => {
        const travel = staticField ? 0 : elapsed * particle.speed;
        const x = ((particle.originX + travel + 48) % trackWidth) - 48;
        const endX = x + particle.length;
        const y = routedYAt(particle, x);
        const endY = routedYAt(particle, endX);

        context.beginPath();
        context.moveTo(x, y);
        context.lineTo(endX, endY);
        context.strokeStyle = particle.color;
        context.lineWidth = particle.lineWidth;
        context.globalAlpha = particle.alpha;
        context.stroke();
      });

      context.restore();
    };

    const updatePointer = (deltaSeconds) => {
      const targetStrength = pointer.active && !staticField ? 1 : 0;
      const strengthBlend = 1 - Math.exp(-8 * deltaSeconds);
      const positionBlend = 1 - Math.exp(-11 * deltaSeconds);
      pointer.strength += (targetStrength - pointer.strength) * strengthBlend;

      if (pointer.active && !staticField) {
        pointer.x += (pointer.targetX - pointer.x) * positionBlend;
        pointer.y += (pointer.targetY - pointer.y) * positionBlend;
      }
    };

    const frame = (timestamp) => {
      frameRequest = 0;
      if (staticField || !fieldIsVisible || document.hidden) return;

      let deltaSeconds = 1 / 60;
      if (lastFrame) {
        const deltaMilliseconds = timestamp - lastFrame;
        if (deltaMilliseconds < 15.5) {
          frameRequest = window.requestAnimationFrame(frame);
          return;
        }
        deltaSeconds = Math.min(deltaMilliseconds / 1000, 0.05);
        elapsed += deltaSeconds;
      }
      lastFrame = timestamp;
      updatePointer(deltaSeconds);
      draw();
      frameRequest = window.requestAnimationFrame(frame);
    };

    const syncPlayback = () => {
      const shouldPlay = !staticField && fieldIsVisible && !document.hidden;
      if (shouldPlay && !frameRequest) {
        lastFrame = 0;
        frameRequest = window.requestAnimationFrame(frame);
      } else if (!shouldPlay && frameRequest) {
        window.cancelAnimationFrame(frameRequest);
        frameRequest = 0;
        lastFrame = 0;
      }
    };

    const resize = () => {
      resizeTimer = 0;
      const canvasRect = canvas.getBoundingClientRect();
      const nextWidth = Math.round(canvasRect.width);
      const nextHeight = Math.round(canvasRect.height);
      if (nextWidth < 1 || nextHeight < 1) return;

      width = nextWidth;
      height = nextHeight;
      const cssPixels = width * height;
      const pixelBudgetRatio = Math.sqrt(6000000 / Math.max(cssPixels, 1));
      const pixelRatio = Math.max(0.75, Math.min(window.devicePixelRatio || 1, 1.5, pixelBudgetRatio));
      canvas.width = Math.round(width * pixelRatio);
      canvas.height = Math.round(height * pixelRatio);
      context.setTransform(pixelRatio, 0, 0, pixelRatio, 0, 0);

      staticField = shouldUseStaticField();
      measureQuietZones(canvasRect);
      buildParticles();
      draw();
      surface.classList.add("field-ready");
      surface.classList.toggle("field-interactive", !staticField);
      syncPlayback();
    };

    const scheduleResize = () => {
      window.clearTimeout(resizeTimer);
      resizeTimer = window.setTimeout(resize, 100);
    };

    const updateMotionMode = () => {
      const nextStaticField = shouldUseStaticField();
      if (nextStaticField === staticField) return;
      staticField = nextStaticField;
      buildParticles();
      pointer.active = false;
      pointer.strength = 0;
      draw();
      surface.classList.toggle("field-interactive", !staticField);
      syncPlayback();
    };

    const updatePointerTarget = (event) => {
      if (staticField || !event.isPrimary || event.pointerType === "touch") return;

      const rect = canvas.getBoundingClientRect();
      pointer.targetX = event.clientX - rect.left;
      pointer.targetY = event.clientY - rect.top;
      if (!pointer.active) {
        pointer.x = pointer.targetX;
        pointer.y = pointer.targetY;
      }
      pointer.active = true;
    };

    const clearPointer = () => {
      pointer.active = false;
    };

    surface.addEventListener("pointermove", updatePointerTarget, { passive: true });
    surface.addEventListener("pointerleave", clearPointer, { passive: true });
    surface.addEventListener("pointercancel", clearPointer, { passive: true });
    document.addEventListener("visibilitychange", syncPlayback);

    reducedMotion.addEventListener?.("change", updateMotionMode);
    finePointer.addEventListener?.("change", updateMotionMode);
    connection?.addEventListener?.("change", updateMotionMode);

    const resizeObserver = typeof ResizeObserver === "function"
      ? new ResizeObserver(scheduleResize)
      : null;
    resizeObserver?.observe(surface);
    quietElements.forEach((element) => resizeObserver?.observe(element));
    window.addEventListener("resize", scheduleResize, { passive: true });

    const intersectionObserver = typeof IntersectionObserver === "function"
      ? new IntersectionObserver(([entry]) => {
          fieldIsVisible = Boolean(entry?.isIntersecting);
          syncPlayback();
        }, { rootMargin: "120px" })
      : null;
    intersectionObserver?.observe(surface);

    document.fonts?.ready.then(scheduleResize).catch(() => {});
    resize();

    window.addEventListener("pagehide", (event) => {
      window.clearTimeout(resizeTimer);
      if (frameRequest) window.cancelAnimationFrame(frameRequest);
      frameRequest = 0;
      lastFrame = 0;
      if (!event.persisted) {
        resizeObserver?.disconnect();
        intersectionObserver?.disconnect();
      }
    });

    window.addEventListener("pageshow", (event) => {
      if (!event.persisted) return;
      scheduleResize();
      syncPlayback();
    });
  };

  try {
    initializePacketField();
  } catch {
    // The download and release UI remains fully functional without the ambient canvas.
  }

  const faqItems = Array.from(document.querySelectorAll(".faq-list details"));
  faqItems.forEach((item) => {
    item.addEventListener("toggle", () => {
      if (!item.open) return;

      faqItems.forEach((otherItem) => {
        if (otherItem !== item) {
          otherItem.open = false;
        }
      });
    });
  });

  const setText = (selector, value) => {
    document.querySelectorAll(selector).forEach((element) => {
      element.textContent = value;
    });
  };

  const formatSize = (bytes) => {
    if (!Number.isFinite(bytes) || bytes <= 0) return null;

    const mebibytes = bytes / 1024 / 1024;
    return `${new Intl.NumberFormat("ru-RU", {
      maximumFractionDigits: 1,
      minimumFractionDigits: mebibytes < 10 ? 1 : 0
    }).format(mebibytes)} МБ`;
  };

  const parseVersions = (tagName) => {
    const match = /^gui-v((?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*))-flowseal-v([0-9a-z][0-9a-z._+\-]{0,63})-u[0-9a-f]{12}$/i.exec(
      tagName || ""
    );
    return match ? { gui: match[1], flowseal: match[2] } : {};
  };

  const findAssetByName = (assets, expectedName) =>
    expectedName
      ? assets.find((asset) => asset.name === expectedName)
      : undefined;

  const choosePortableAsset = (assets, versions) =>
    findAssetByName(
      assets,
      versions.gui && versions.flowseal
        ? `zapret-control-center-${versions.gui}-flowseal-${versions.flowseal}-win-x64.zip`
        : null
    );

  const chooseInstallerAsset = (assets, versions) =>
    findAssetByName(
      assets,
      versions.gui && versions.flowseal
        ? `zapret-control-center-setup-${versions.gui}-flowseal-${versions.flowseal}-win-x64.exe`
        : null
    );

  const applyRelease = (release) => {
    if (!release || release.draft || release.prerelease || !Array.isArray(release.assets)) {
      return;
    }

    const versions = parseVersions(release.tag_name);
    const installerAsset = chooseInstallerAsset(release.assets, versions);
    const portableAsset = choosePortableAsset(release.assets, versions);
    const primaryAsset = installerAsset || portableAsset;
    const checksumAsset = release.assets.find((asset) => /^sha256sums\.txt$/i.test(asset.name || ""));
    const downloadUrl = primaryAsset?.browser_download_url || release.html_url || releasePage;
    const portableUrl = portableAsset?.browser_download_url || release.html_url || releasePage;
    const checksumUrl = checksumAsset?.browser_download_url || release.html_url || releasePage;
    const size = formatSize(primaryAsset?.size);
    const artifactKind = installerAsset
      ? "Установщик EXE"
      : portableAsset
        ? "Portable ZIP"
        : "GitHub Release";
    const downloadLabel = installerAsset
      ? "Скачать установщик"
      : portableAsset
        ? "Скачать portable ZIP"
        : "Открыть релиз";

    document.querySelectorAll("[data-download-link]").forEach((link) => {
      link.href = downloadUrl;
      link.setAttribute(
        "aria-label",
        primaryAsset ? `Скачать ${primaryAsset.name}` : "Открыть последний релиз"
      );
    });
    setText("[data-download-label]", downloadLabel);

    document.querySelectorAll("[data-portable-link]").forEach((link) => {
      link.href = portableUrl;
      link.hidden = !(installerAsset && portableAsset);
      link.setAttribute(
        "aria-label",
        portableAsset ? `Скачать portable-сборку ${portableAsset.name}` : "Открыть файлы последнего релиза"
      );
    });
    setText(
      "[data-portable-label]",
      portableAsset ? "Portable ZIP без установки" : "Открыть файлы релиза"
    );

    document.querySelectorAll("[data-checksum-link]").forEach((link) => {
      link.href = checksumUrl;
    });

    if (versions.gui) {
      setText("[data-gui-version]", versions.gui);
      setText("[data-release-title]", `Zapret Control Center ${versions.gui}`);
    } else if (release.name) {
      setText("[data-release-title]", release.name);
    }

    if (versions.flowseal) {
      setText("[data-flowseal-version]", versions.flowseal);
    }

    setText("[data-download-size]", size || "—");
    setText("[data-release-meta]", `Windows x64 · ${artifactKind}${size ? ` · ${size}` : ""}`);

    if (release.published_at) {
      const publishedAt = new Date(release.published_at);
      if (!Number.isNaN(publishedAt.valueOf())) {
        const date = new Intl.DateTimeFormat("ru-RU", {
          day: "numeric",
          month: "long",
          year: "numeric"
        }).format(publishedAt);

        setText("[data-published-note]", `Опубликовано ${date}`);
      }
    }

    setText("[data-release-state]", versions.gui ? `GUI ${versions.gui} · VERIFIED` : "RELEASE VERIFIED");

    const schema = document.querySelector('script[type="application/ld+json"]');
    if (schema && versions.gui) {
      try {
        const data = JSON.parse(schema.textContent);
        data.softwareVersion = versions.gui;
        data.downloadUrl = downloadUrl;
        schema.textContent = JSON.stringify(data);
      } catch {
        // Static metadata remains valid if the optional update cannot be applied.
      }
    }
  };

  const loadLatestRelease = async () => {
    try {
      const snapshotResponse = await fetch(releaseSnapshot, { cache: "no-store" });
      if (snapshotResponse.ok) {
        applyRelease(await snapshotResponse.json());
      }
    } catch {
      // The hard-coded release remains a complete fallback for local/offline use.
    }

    const controller = new AbortController();
    const timeout = window.setTimeout(() => controller.abort(), 7000);

    try {
      const response = await fetch(releaseApi, {
        headers: {
          Accept: "application/vnd.github+json"
        },
        signal: controller.signal
      });

      if (!response.ok) {
        throw new Error(`GitHub API returned ${response.status}`);
      }

      applyRelease(await response.json());
    } catch {
      // Some privacy tools block api.github.com; the local snapshot is used in that case.
    } finally {
      window.clearTimeout(timeout);
    }
  };

  const embeddedRelease = document.querySelector("#release-data");
  if (embeddedRelease) {
    try {
      applyRelease(JSON.parse(embeddedRelease.textContent));
    } catch {
      // The visible fallback stays usable if embedded metadata is malformed.
    }
  }

  loadLatestRelease();
})();
