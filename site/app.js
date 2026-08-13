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
