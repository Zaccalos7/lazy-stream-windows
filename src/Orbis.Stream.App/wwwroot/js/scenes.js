// Orbis Stream: the scene deck. The buttons that put a video, a banner or an image on air in place of
// a live, the way Streamlabs switches to a scene: they are set once for every live and pressed while
// one is on air, from the panel, from the strip of the preview or from the keyboard.
//
// Two rules keep a live safe from a stray key. The keys only work on the two pages that load this
// script, and only once the panel has been opened for the live they act on: opening it is what turns
// them on, for that live alone, until the window closes or they are turned off. And every press,
// button or key, asks before it goes on air.
(() => {
  const deck = document.querySelector("[data-scene-deck]");
  if (!deck) return;

  const word = key => deck.dataset["w" + key.replace(/(^|-)(\w)/g, (_, __, c) => c.toUpperCase())] || "";
  const format = (text, ...values) => text.replace(/\{(\d)\}/g, (_, index) => values[Number(index)] ?? "");
  const messageOf = payload => payload?.message
    || Object.values(payload || {}).find(value => typeof value === "string" && value !== "error" && value !== "success")
    || "";

  // The one infobar of a page that answers with a fetch lives in site.js.
  const notify = (kind, text) => {
    if (typeof say === "function") say(kind, word(kind === "error" ? "ko" : "ok"), text, "");
  };

  const maxButtons = Number(deck.dataset.max || 6);
  const maxBytes = Number(deck.dataset.maxBytes || 0);

  const liveLine = deck.querySelector("[data-scene-deck-live]");
  const onAirBar = deck.querySelector("[data-scene-onair]");
  const onAirText = deck.querySelector("[data-scene-onair-text]");
  const onAirHint = deck.querySelector("[data-scene-onair-hint]");
  const onAirResume = deck.querySelector("[data-scene-resume]");
  const offline = deck.querySelector("[data-scene-offline]");
  const grid = deck.querySelector("[data-scene-grid]");
  const editor = deck.querySelector("[data-scene-editor]");
  const editorTitle = editor.querySelector("[data-scene-editor-title]");
  const labelField = editor.querySelector("[data-scene-label]");
  const mediaZone = editor.querySelector("[data-scene-media-zone]");
  const mediaStill = editor.querySelector("[data-scene-media-still]");
  const mediaEmpty = editor.querySelector("[data-scene-media-empty]");
  const mediaLabel = editor.querySelector("[data-scene-media-label]");
  const mediaKind = editor.querySelector("[data-scene-media-kind]");
  const mediaProgress = editor.querySelector("[data-scene-media-progress]");
  const mediaFile = editor.querySelector("[data-scene-media-file]");
  const keyCapture = editor.querySelector("[data-scene-key-capture]");
  const keyLabel = editor.querySelector("[data-scene-key-label]");
  const deleteButton = editor.querySelector("[data-scene-delete]");
  const saveButton = editor.querySelector("[data-scene-save]");
  const displayModeSelect = editor.querySelector("[data-scene-display-mode]");
  const placementField = editor.querySelector("[data-scene-placement-field]");
  const placementSelect = editor.querySelector("[data-scene-placement]");
  const durationField = editor.querySelector("[data-scene-duration-field]");
  const durationInput = editor.querySelector("[data-scene-duration]");
  const durationForever = editor.querySelector("[data-scene-duration-forever]");
  const customCoordsField = editor.querySelector("[data-scene-custom-coords]");
  const coordX = editor.querySelector("[data-scene-coord-x]");
  const coordY = editor.querySelector("[data-scene-coord-y]");
  const coordW = editor.querySelector("[data-scene-coord-w]");
  const coordH = editor.querySelector("[data-scene-coord-h]");

  const syncEditorMode = () => {
    if (!displayModeSelect) return;
    const isOverlay = displayModeSelect.value === "in_scene";
    if (placementField) placementField.hidden = !isOverlay;
    if (durationField) durationField.hidden = !isOverlay;
    if (customCoordsField) customCoordsField.hidden = !isOverlay || placementSelect?.value !== "custom";
    if (durationInput && durationForever) durationInput.disabled = durationForever.checked;
  };

  displayModeSelect?.addEventListener("change", syncEditorMode);
  placementSelect?.addEventListener("change", syncEditorMode);
  durationForever?.addEventListener("change", () => {
    if (durationInput && durationForever) {
      durationInput.disabled = durationForever.checked;
      if (durationForever.checked) durationInput.value = "";
    }
  });

  const strip = document.querySelector("[data-scene-strip]");
  const stripGrid = strip?.querySelector("[data-scene-strip-grid]");
  const stripState = strip?.querySelector("[data-scene-strip-state]");
  const stripResume = strip?.querySelector("[data-scene-strip-resume]");
  const stripLocked = strip?.querySelector("[data-scene-strip-locked]");
  const keysChip = document.querySelector("[data-scene-keys]");
  const keysText = keysChip?.querySelector("[data-scene-keys-text]");

  let buttons = [];
  let loaded = false;
  // The live the panel is open for: { video, history, live, title }, and what is on air in place of
  // its program. The strip of the preview has its own, which the push channel brings every second.
  let target = null;
  let targetScene = null;
  let stripScene = null;
  let stripLive = !!strip;
  let editing = null;
  let capturing = false;
  let polling = 0;
  // The button pressed last, until the live says it is on air: a press takes a moment to be taken.
  let pressed = null;

  // ---------- The keys: on for one live at a time, for as long as this window is open ----------
  //
  // sessionStorage keeps them across the two pages and forgets them with the window. A window that
  // refuses storage keeps them for this page only.
  const armedKey = "orbis-scene-armed";
  const readArmed = () => {
    try {
      return JSON.parse(sessionStorage.getItem(armedKey) || "null");
    } catch {
      return null;
    }
  };
  let armed = readArmed();

  const setArmed = value => {
    armed = value;
    try {
      if (value) sessionStorage.setItem(armedKey, JSON.stringify(value));
      else sessionStorage.removeItem(armedKey);
    } catch {
      // Storage refused: the keys stay on for this page only.
    }
    paintKeys();
    paintStrip();
  };

  // On the preview the keys act on the live it watches, and only when they were turned on for it.
  const armedHere = () => armed && (!strip || Number(strip.dataset.history) === armed.history);
  const keysVideo = () => !armedHere() ? null : strip ? Number(strip.dataset.video) : armed.video;

  const paintKeys = () => {
    if (!keysChip) return;
    keysChip.hidden = !armedHere();
    if (keysText && armed) keysText.textContent = format(word("keys-armed"), armed.title || "");
  };

  // ---------- The names of the keys ----------
  //
  // A key is stored as the physical key (KeyboardEvent.code) after its modifiers, so a button
  // answers to the same key whatever the layout. It is shown as the layout prints it, when the
  // browser can tell (the Keyboard Map API), and by its code otherwise.
  const modifierCode = /^(Control|Alt|Shift|Meta|OS)(Left|Right)?$/;
  const validCombo = /^(Ctrl\+)?(Alt\+)?(Shift\+)?(Key[A-Z]|Digit[0-9]|Numpad[0-9]|Numpad(Add|Subtract|Multiply|Divide|Decimal)|F([1-9]|1[0-9]|2[0-4])|Backquote|Minus|Equal|BracketLeft|BracketRight|Backslash|IntlBackslash|Semicolon|Quote|Comma|Period|Slash|Insert|Delete|Home|End|PageUp|PageDown|Arrow(Up|Down|Left|Right))$/;
  // The keys the page needs for itself, the same the server refuses (SceneHotkey).
  const reserved = new Set([
    "Ctrl+KeyR", "Ctrl+KeyW", "Ctrl+KeyF", "Ctrl+KeyP", "Ctrl+KeyN", "Ctrl+KeyT",
    "Ctrl+Shift+KeyI", "Ctrl+Shift+KeyJ", "Ctrl+F5", "Shift+F5", "Alt+F4", "F5", "F11", "F12"
  ]);

  const comboOf = event => {
    if (!event.code || modifierCode.test(event.code) || event.metaKey) return null;
    return (event.ctrlKey ? "Ctrl+" : "") + (event.altKey ? "Alt+" : "") + (event.shiftKey ? "Shift+" : "") + event.code;
  };

  let layout = null;
  const glyphs = {
    ArrowUp: "↑", ArrowDown: "↓", ArrowLeft: "←", ArrowRight: "→", PageUp: "PgUp", PageDown: "PgDn",
    Insert: "Ins", Delete: "Del", NumpadAdd: "Num +", NumpadSubtract: "Num −", NumpadMultiply: "Num ×",
    NumpadDivide: "Num ÷", NumpadDecimal: "Num ."
  };
  const keyName = code => glyphs[code]
    || (code.startsWith("Numpad") ? "Num " + code.slice(6) : null)
    || (/^F\d+$/.test(code) ? code : null)
    || layout?.get(code)?.toUpperCase()
    || (code.startsWith("Key") ? code.slice(3) : code.startsWith("Digit") ? code.slice(5) : code);
  const labelOf = combo => combo
    ? combo.split("+").map((part, index, parts) => index < parts.length - 1 ? part : keyName(part)).join("+")
    : "";

  navigator.keyboard?.getLayoutMap?.().then(map => {
    layout = map;
    paintAll();
  }).catch(() => {});

  // ---------- Talking to the server ----------

  const loadButtons = async () => {
    try {
      const response = await fetch("/scene-buttons");
      if (!response.ok) throw new Error(String(response.status));
      buttons = await response.json();
      loaded = true;
    } catch {
      loaded = false;
      buttons = [];
      grid.dataset.state = "failed";
    }
    paintAll();
  };

  const stateOf = async video => {
    try {
      const response = await fetch(`/live/${video}/scene`);
      return response.ok ? await response.json() : null;
    } catch {
      return null;
    }
  };

  const refreshTarget = async () => {
    if (!target) return;
    const state = await stateOf(target.video);
    if (!target || !state) return;
    target.live = !!state.live;
    target.history = state.history ?? target.history;
    targetScene = state.current || null;
    if (pressed && targetScene?.buttonPkid === pressed) pressed = null;
    paintDeck();
  };

  const confirmThen = (text, action) => {
    const dialog = document.getElementById("confirm-dialog");
    if (!dialog || dialog.open) return;
    dialog.querySelector("[data-text]").textContent = text;
    dialog.returnValue = "";
    dialog.onclose = () => {
      dialog.onclose = null;
      if (dialog.returnValue === "ok") action();
    };
    dialog.showModal();
  };

  // A press goes on air once the live takes it, a moment later: the button says so meanwhile.
  const play = (video, button) => {
    if (!button.available) {
      notify("error", format(word("missing"), button.label));
      return;
    }

    const currentScene = targetScene || stripScene;
    const isOverlayOnAir = button.displayMode === "in_scene" && currentScene?.buttonPkid === button.pkid;
    const confirmPrompt = isOverlayOnAir
      ? format(word("stop-overlay") || word("confirm"), button.label)
      : button.displayMode === "in_scene"
        ? format(word("confirm-in-scene") || word("confirm"), button.label)
        : format(word("confirm"), button.label);

    confirmThen(confirmPrompt, async () => {
      pressed = button.pkid;
      paintAll();
      const response = await fetch(`/live/${video}/scene/${button.pkid}`, { method: "POST" }).catch(() => null);
      const payload = await response?.json().catch(() => ({}));
      if (!response?.ok) {
        pressed = null;
        notify("error", messageOf(payload) || word("failed"));
        // A live that is over takes its keys with it.
        const state = await stateOf(video);
        if (state && !state.live && video === keysVideo()) setArmed(null);
      }

      setTimeout(() => { if (pressed === button.pkid) pressed = null; paintAll(); }, 6000);
      soon();
    });
  };

  const resume = async video => {
    const response = await fetch(`/live/${video}/scene/resume`, { method: "POST" }).catch(() => null);
    if (!response?.ok) {
      const payload = await response?.json().catch(() => ({}));
      notify("error", messageOf(payload) || word("failed"));
    }

    soon();
  };

  // What changes on air shows within a second or two: the panel asks again a little later.
  const soon = () => {
    setTimeout(refreshTarget, 700);
    setTimeout(refreshTarget, 1800);
  };

  // ---------- The panel ----------

  const openDeck = async opener => {
    target = {
      video: Number(opener.dataset.video),
      history: Number(opener.dataset.history) || null,
      live: opener.dataset.live === "1",
      title: opener.dataset.title || ""
    };
    targetScene = null;
    liveLine.textContent = target.title;
    closeEditor();
    if (!deck.open) deck.showModal();
    paintDeck();

    await Promise.all([loadButtons(), refreshTarget()]);

    // Opening the panel for a live on air is what turns its keys on.
    if (target?.live && target.history) setArmed({ video: target.video, history: target.history, title: target.title });

    clearInterval(polling);
    polling = setInterval(refreshTarget, 1500);
  };

  deck.addEventListener("close", () => {
    clearInterval(polling);
    polling = 0;
    closeEditor();
    target = null;
    targetScene = null;
  });

  for (const close of deck.querySelectorAll("[data-scene-deck-close]")) {
    close.addEventListener("click", () => deck.close());
  }

  onAirResume.addEventListener("click", () => target && resume(target.video));

  const tileOf = (button, options) => {
    const onAir = options.scene?.buttonPkid === button.pkid;
    const isOverlay = button.displayMode === "in_scene";
    const canToggleOff = isOverlay && onAir;
    const tile = document.createElement("div");
    tile.className = "scene-tile";
    tile.dataset.kind = String(button.kind).toLowerCase();
    tile.classList.toggle("is-on-air", onAir);
    tile.classList.toggle("is-pressed", !onAir && pressed === button.pkid);
    tile.classList.toggle("is-missing", !button.available);
    tile.classList.toggle("is-locked", !!options.locked);

    const face = document.createElement("button");
    face.type = "button";
    face.className = "scene-tile-face";
    face.disabled = !options.locked && (!options.live || !button.available || (onAir && !canToggleOff));
    face.title = !button.available
      ? word("missing")
      : options.locked
        ? word("locked")
        : (onAir && isOverlay)
          ? word("stop-overlay")
          : isOverlay
            ? word("mode-in-scene")
            : (button.kind === "IMAGE" ? word("image-behaviour") : word("video-behaviour"));

    const art = document.createElement("span");
    art.className = "scene-tile-art";
    if (button.available) {
      const still = new Image();
      still.alt = "";
      still.loading = "lazy";
      still.decoding = "async";
      still.src = button.still;
      still.addEventListener("error", () => still.remove());
      art.append(still);
    } else {
      const warning = document.createElement("i");
      warning.className = "icon";
      warning.textContent = "\uE7BA";
      art.append(warning);
    }

    const kind = document.createElement("span");
    kind.className = "scene-tile-kind";
    const kindLabel = button.kind === "IMAGE" ? word("image") : word("video");
    if (isOverlay) {
      const placeWord = word("placement-" + button.placement) || button.placement;
      kind.textContent = `${kindLabel} · ${placeWord}`;
    } else {
      kind.textContent = kindLabel;
    }
    art.append(kind);

    if (button.hotkey) {
      const key = document.createElement("kbd");
      key.className = "scene-tile-key";
      key.textContent = labelOf(button.hotkey);
      art.append(key);
    }

    if (onAir) {
      const badge = document.createElement("span");
      badge.className = "scene-tile-badge";
      badge.textContent = isOverlay ? word("stop-overlay") : word("on-air");
      art.append(badge);
    }

    const name = document.createElement("span");
    name.className = "scene-tile-name truncate";
    name.textContent = button.label;

    face.append(art, name);
    face.addEventListener("click", () => options.press(button));
    tile.append(face);

    if (options.tools) {
      const tools = document.createElement("span");
      tools.className = "scene-tile-tools";
      const edit = toolOf("\uE70F", word("edit"), () => openEditor(button));
      const remove = toolOf("\uE74D", word("delete"), () => confirmThen(format(word("delete-confirm"), button.label), () => deleteButtonOf(button)));
      remove.classList.add("danger");
      tools.append(edit, remove);
      tile.append(tools);
    }

    return tile;
  };

  const toolOf = (glyph, label, action) => {
    const tool = document.createElement("button");
    tool.type = "button";
    tool.className = "btn subtle icon-only";
    tool.title = label;
    tool.setAttribute("aria-label", label);
    const icon = document.createElement("i");
    icon.className = "icon";
    icon.textContent = glyph;
    tool.append(icon);
    tool.addEventListener("click", event => {
      event.stopPropagation();
      action();
    });
    return tool;
  };

  const paintDeck = () => {
    if (!deck.open) return;

    const live = !!target?.live;
    offline.hidden = live || !target;
    onAirBar.hidden = !live || !targetScene;
    if (targetScene) {
      const isOverlay = targetScene.mode === "in_scene";
      onAirText.textContent = isOverlay
        ? format(word("on-air-in-scene") || word("on-air-now"), targetScene.label)
        : format(word("on-air-now"), targetScene.label);
      onAirHint.textContent = isOverlay
        ? (targetScene.durationSeconds ? `${targetScene.durationSeconds}s` : word("duration-forever"))
        : (targetScene.holds ? word("image-behaviour") : word("video-behaviour"));
      const resumeSpan = onAirResume.querySelector("span");
      if (resumeSpan) resumeSpan.textContent = isOverlay ? word("stop-overlay") : word("resume");
      onAirResume.classList.toggle("is-pulsing", !!targetScene.holds || isOverlay);
    }

    if (!editor.hidden) return;

    grid.replaceChildren();
    if (!loaded) {
      const note = document.createElement("p");
      note.className = "caption scene-loading";
      note.textContent = grid.dataset.state === "failed" ? word("failed") : word("loading");
      grid.append(note);
      return;
    }

    for (const button of buttons) {
      grid.append(tileOf(button, {
        live,
        scene: targetScene,
        tools: true,
        press: picked => target && play(target.video, picked)
      }));
    }

    if (buttons.length < maxButtons) {
      const add = document.createElement("button");
      add.type = "button";
      add.className = "scene-tile scene-tile-add";
      const plus = document.createElement("i");
      plus.className = "icon";
      plus.textContent = "\uE710";
      const text = document.createElement("span");
      text.textContent = buttons.length ? word("add") : word("empty");
      add.append(plus, text);
      add.title = word("add");
      add.addEventListener("click", () => openEditor(null));
      grid.append(add);
    } else {
      const full = document.createElement("p");
      full.className = "caption scene-limit";
      full.textContent = format(word("limit"), maxButtons);
      grid.append(full);
    }
  };

  // ---------- The editor of a button ----------

  const errorOf = field => editor.querySelector(`[data-scene-error="${field}"]`);
  const clearErrors = () => {
    for (const error of editor.querySelectorAll("[data-scene-error]")) error.textContent = "";
  };
  const showErrors = payload => {
    clearErrors();
    let shown = false;
    for (const [field, text] of Object.entries(payload || {})) {
      const error = errorOf(field);
      if (error && typeof text === "string") {
        error.textContent = text;
        shown = true;
      }
    }

    if (!shown) notify("error", messageOf(payload) || word("ko"));
  };

  const openEditor = button => {
    editing = button
      ? {
          pkid: button.pkid,
          media: { name: button.mediaName, label: button.mediaLabel, kind: button.kind, still: button.still, available: button.available },
          hotkey: button.hotkey,
          displayMode: button.displayMode || "fullscreen",
          placement: button.placement || "bottom-right",
          durationSeconds: button.durationSeconds,
          x: button.x,
          y: button.y,
          width: button.width,
          height: button.height
        }
      : {
          pkid: null,
          media: null,
          hotkey: null,
          displayMode: "fullscreen",
          placement: "bottom-right",
          durationSeconds: null,
          x: null,
          y: null,
          width: null,
          height: null
        };
    editorTitle.textContent = button ? word("edit") : word("add");
    labelField.value = button?.label || "";
    if (displayModeSelect) displayModeSelect.value = editing.displayMode || "fullscreen";
    if (placementSelect) placementSelect.value = editing.placement || "bottom-right";
    const hasDuration = editing.durationSeconds != null && editing.durationSeconds > 0;
    if (durationForever) durationForever.checked = !hasDuration;
    if (durationInput) durationInput.value = hasDuration ? editing.durationSeconds : "";
    if (coordX) coordX.value = editing.x ?? "";
    if (coordY) coordY.value = editing.y ?? "";
    if (coordW) coordW.value = editing.width ?? "";
    if (coordH) coordH.value = editing.height ?? "";
    syncEditorMode();
    deleteButton.hidden = !button;
    clearErrors();
    paintMedia();
    paintKey();
    grid.hidden = true;
    editor.hidden = false;
    labelField.focus();
  };

  const closeEditor = () => {
    stopCapture();
    editing = null;
    editor.hidden = true;
    grid.hidden = false;
    mediaProgress.hidden = true;
    paintDeck();
  };

  const paintMedia = () => {
    const media = editing?.media;
    mediaStill.hidden = !media?.available;
    mediaEmpty.hidden = !!media?.available;
    if (media?.available) mediaStill.src = media.still;
    else mediaStill.removeAttribute("src");
    mediaLabel.textContent = media ? media.label : "";
    mediaKind.textContent = !media
      ? ""
      : !media.available
        ? word("missing")
        : (media.kind === "IMAGE" ? word("image") + " · " + word("image-behaviour") : word("video") + " · " + word("video-behaviour"));
  };

  const paintKey = () => {
    keyLabel.textContent = capturing ? word("key-press") : editing?.hotkey ? labelOf(editing.hotkey) : word("key-none");
    keyCapture.classList.toggle("is-capturing", capturing);
  };

  const stopCapture = () => {
    if (!capturing) return;
    capturing = false;
    paintKey();
  };

  keyCapture.addEventListener("click", () => {
    capturing = !capturing;
    errorOf("hotkey").textContent = "";
    paintKey();
  });
  keyCapture.addEventListener("blur", stopCapture);
  keyCapture.addEventListener("keydown", event => {
    if (!capturing) return;
    // The key is the answer, not a command: nothing else on the page sees it, not even the dialog.
    event.preventDefault();
    event.stopPropagation();
    if (event.code === "Escape") {
      stopCapture();
      return;
    }

    const combo = comboOf(event);
    if (!combo) return;
    if (reserved.has(combo) || !validCombo.test(combo)) {
      errorOf("hotkey").textContent = format(word("key-reserved"), labelOf(combo));
      return;
    }

    const taken = buttons.find(button => button.hotkey === combo && button.pkid !== editing?.pkid);
    if (taken) {
      errorOf("hotkey").textContent = format(word("key-taken"), labelOf(combo), taken.label);
      return;
    }

    errorOf("hotkey").textContent = "";
    editing.hotkey = combo;
    stopCapture();
  });

  editor.querySelector("[data-scene-key-clear]").addEventListener("click", () => {
    if (!editing) return;
    editing.hotkey = null;
    errorOf("hotkey").textContent = "";
    stopCapture();
    paintKey();
  });

  editor.querySelector("[data-scene-editor-cancel]").addEventListener("click", closeEditor);

  // The file is sent as it is, as the body of the request, so a clip of a few hundred megabytes
  // costs one copy; XMLHttpRequest rather than fetch, because only it says how far the upload got.
  const upload = file => new Promise((resolve, reject) => {
    const request = new XMLHttpRequest();
    request.open("POST", "/scene-buttons/media?name=" + encodeURIComponent(file.name));
    request.setRequestHeader("Content-Type", file.type || "application/octet-stream");
    request.upload.addEventListener("progress", event => {
      if (!event.lengthComputable) return;
      const percent = Math.round(event.loaded * 100 / event.total);
      mediaProgress.value = percent;
      mediaKind.textContent = format(word("uploading"), percent);
    });
    request.addEventListener("load", () => {
      let payload = {};
      try {
        payload = JSON.parse(request.responseText || "{}");
      } catch {
        // Not JSON: the generic message says it.
      }

      if (request.status === 201) resolve(payload);
      else reject(payload);
    });
    request.addEventListener("error", () => reject({ file: word("upload-failed") }));
    request.send(file);
  });

  const take = async file => {
    if (!file || !editing) return;
    clearErrors();
    if (maxBytes && file.size > maxBytes) {
      errorOf("file").textContent = format(word("too-large"), file.name, Math.round(maxBytes / (1024 * 1024)));
      return;
    }

    mediaProgress.hidden = false;
    mediaProgress.value = 0;
    mediaKind.textContent = format(word("uploading"), 0);
    saveButton.disabled = true;
    try {
      const media = await upload(file);
      if (!editing) return;
      editing.media = { name: media.name, label: media.label, kind: media.kind, still: media.still, available: true };
      // A button with no name yet is named after its file, which is what most of them end up as.
      if (!labelField.value.trim()) labelField.value = media.label.replace(/\.[^.]+$/, "").slice(0, Number(deck.dataset.maxLabel || 40));
    } catch (payload) {
      showErrors(payload);
    } finally {
      mediaProgress.hidden = true;
      saveButton.disabled = false;
      paintMedia();
    }
  };

  editor.querySelector("[data-scene-media-choose]").addEventListener("click", () => mediaFile.click());
  mediaFile.addEventListener("change", () => {
    take(mediaFile.files?.[0]);
    mediaFile.value = "";
  });

  // A file dragged from Explorer onto the editor. The page never learns its path, and does not
  // need to: the file itself is sent. The document swallows every other drop (site.js).
  mediaZone.addEventListener("dragover", event => {
    if (!event.dataTransfer?.types?.includes("Files")) return;
    event.preventDefault();
    mediaZone.classList.add("is-over");
  });
  mediaZone.addEventListener("dragleave", event => {
    if (!mediaZone.contains(event.relatedTarget)) mediaZone.classList.remove("is-over");
  });
  mediaZone.addEventListener("drop", event => {
    mediaZone.classList.remove("is-over");
    const file = event.dataTransfer?.files?.[0];
    if (!file) return;
    event.preventDefault();
    event.stopPropagation();
    take(file);
  });

  editor.addEventListener("submit", async event => {
    event.preventDefault();
    if (!editing) return;
    clearErrors();
    saveButton.disabled = true;
    const isOverlay = displayModeSelect?.value === "in_scene";
    const placementVal = isOverlay ? (placementSelect?.value || "bottom-right") : "bottom-right";
    const durationVal = (!isOverlay || durationForever?.checked) ? null : (parseInt(durationInput?.value, 10) || null);
    const body = {
      label: labelField.value.trim(),
      mediaName: editing.media?.name || "",
      hotkey: editing.hotkey || null,
      displayMode: displayModeSelect?.value || "fullscreen",
      placement: placementVal,
      durationSeconds: durationVal,
      x: isOverlay && placementVal === "custom" && coordX?.value !== "" ? parseInt(coordX.value, 10) : null,
      y: isOverlay && placementVal === "custom" && coordY?.value !== "" ? parseInt(coordY.value, 10) : null,
      width: isOverlay && placementVal === "custom" && coordW?.value !== "" ? parseInt(coordW.value, 10) : null,
      height: isOverlay && placementVal === "custom" && coordH?.value !== "" ? parseInt(coordH.value, 10) : null
    };
    const response = await fetch(editing.pkid ? `/scene-buttons/${editing.pkid}` : "/scene-buttons", {
      method: editing.pkid ? "PUT" : "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body)
    }).catch(() => null);
    const payload = await response?.json().catch(() => ({}));
    saveButton.disabled = false;
    if (!response?.ok) {
      showErrors(response?.status === 400 ? payload : { message: messageOf(payload) || word("ko") });
      return;
    }

    closeEditor();
    await loadButtons();
  });

  deleteButton.addEventListener("click", () => {
    const button = buttons.find(candidate => candidate.pkid === editing?.pkid);
    if (button) confirmThen(format(word("delete-confirm"), button.label), () => deleteButtonOf(button));
  });

  const deleteButtonOf = async button => {
    const response = await fetch(`/scene-buttons/${button.pkid}`, { method: "DELETE" }).catch(() => null);
    if (!response?.ok) {
      const payload = await response?.json().catch(() => ({}));
      notify("error", messageOf(payload) || word("ko"));
      return;
    }

    if (editing?.pkid === button.pkid) closeEditor();
    await loadButtons();
  };

  // ---------- The strip of the preview ----------
  //
  // The buttons above the volumes of the live the page watches, with "Resume live" while a picture
  // stands in for it. Until the keys are on for this live the buttons are locked, and pressing one
  // opens the panel, which is what turns them on.

  const opener = strip?.querySelector("[data-scene-deck-open]");

  const paintStrip = () => {
    if (!strip) return;
    const locked = !armedHere();
    strip.classList.toggle("is-locked", locked);
    if (stripLocked) stripLocked.hidden = !locked || !buttons.length;
    const isOverlay = stripScene?.mode === "in_scene";
    stripResume.hidden = !stripLive || (!stripScene?.holds && !isOverlay);
    const stripResumeSpan = stripResume?.querySelector("span");
    if (stripResumeSpan) stripResumeSpan.textContent = isOverlay ? word("stop-overlay") : word("resume");
    stripState.textContent = stripLive && stripScene
      ? (isOverlay ? format(word("on-air-in-scene") || word("on-air-now"), stripScene.label) : format(word("on-air-now"), stripScene.label))
      : "";
    stripState.hidden = !stripState.textContent;

    if (!stripGrid) return;
    stripGrid.replaceChildren();
    for (const button of buttons) {
      stripGrid.append(tileOf(button, {
        live: stripLive,
        locked,
        scene: stripScene,
        tools: false,
        press: picked => locked ? opener && openDeck(opener) : play(Number(strip.dataset.video), picked)
      }));
    }

    // No button yet: the way to the panel, where they are made.
    if (!buttons.length && loaded && opener) {
      const add = document.createElement("button");
      add.type = "button";
      add.className = "scene-tile scene-tile-add";
      const plus = document.createElement("i");
      plus.className = "icon";
      plus.textContent = "\uE710";
      const text = document.createElement("span");
      text.textContent = word("empty");
      add.append(plus, text);
      add.addEventListener("click", () => openDeck(opener));
      stripGrid.append(add);
    }
  };

  stripResume?.addEventListener("click", () => resume(Number(strip.dataset.video)));

  // The push channel of the preview carries what is on air, once a second (site.js).
  document.addEventListener("orbis:live", event => {
    const state = event.detail;
    if (!strip || !state) return;
    const before = JSON.stringify([stripLive, stripScene]);
    stripLive = !!state.isLive;
    stripScene = state.scene || null;
    if (pressed && stripScene?.buttonPkid === pressed) pressed = null;
    if (JSON.stringify([stripLive, stripScene]) !== before) paintStrip();
  });

  const paintAll = () => {
    paintDeck();
    paintStrip();
    paintKeys();
  };

  // ---------- Opening, resuming and the keys, wherever the buttons are ----------

  document.addEventListener("click", event => {
    const open = event.target.closest?.("[data-scene-deck-open]");
    if (open) {
      event.preventDefault();
      openDeck(open);
      return;
    }

    const back = event.target.closest?.("[data-scene-row-resume]");
    if (back) {
      event.preventDefault();
      back.disabled = true;
      resume(Number(back.dataset.video));
    }
  });

  keysChip?.querySelector("[data-scene-keys-off]")?.addEventListener("click", () => setArmed(null));

  const isTyping = element => element instanceof Element
    && !!element.closest("input, textarea, select, [contenteditable=''], [contenteditable='true']");

  document.addEventListener("keydown", event => {
    if (capturing || event.repeat || event.defaultPrevented) return;
    const video = keysVideo();
    if (!video || isTyping(event.target)) return;

    // Another dialog on top (the confirmation, above all) or a button being edited: the key is theirs.
    if ([...document.querySelectorAll("dialog[open]")].some(dialog => dialog !== deck)) return;
    if (deck.open && !editor.hidden) return;

    const combo = comboOf(event);
    const button = combo && buttons.find(candidate => candidate.hotkey === combo);
    if (!button) return;

    event.preventDefault();
    play(video, button);
  });

  // ---------- Start ----------

  const start = async () => {
    if (armed || strip) await loadButtons();

    // Keys turned on for a live that is over are turned off: they would only ever be refused.
    if (armed) {
      const state = await stateOf(armed.video);
      if (state && !state.live) setArmed(null);
    }

    paintAll();
  };

  start();
})();
