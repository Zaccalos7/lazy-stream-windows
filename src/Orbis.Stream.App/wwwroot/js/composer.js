// Orbis Stream: the scene composer. A blank canvas the size of the output, the sources of the
// machine to drag onto it, and the layers they end up in. Everything is kept in output pixels, the
// unit the encoder is given, and only drawn as a percentage of the stage: the layout on screen is
// the layout on air whatever size the window is.
//
// It runs in two modes. The layout page draws skeletons: slots, rectangles with nothing in them,
// saved to start lives from. The live wizard opens one of them as its empty slots, the sources are
// dropped into them, and what is on the canvas when the live starts is saved as the scene of that
// live alone: it is what the live restarts from, and it never joins the layouts.
(() => {
  const root = document.querySelector("[data-composer]");
  if (!root) return;

  const layoutMode = root.dataset.mode === "layout";

  // The kinds as the API numbers them (Orbis.Stream.Core.Domain.SourceKind).
  const Kind = { File: 0, Screen: 1, Camera: 2, Microphone: 3 };
  // A slot has no kind: it is a rectangle, so it takes room on the canvas like a picture does.
  const hasPicture = kind => kind !== Kind.Microphone;
  const isSlot = item => item.slot === true;
  // A camera opened as video=… has no sound of its own and a screen never has any: the sound of a
  // webcam is its microphone, which is a source of its own.
  const canCarrySound = kind => kind === Kind.File || kind === Kind.Microphone;

  const glyphs = { [Kind.File]: "\uE714", [Kind.Screen]: "\uE7F4", [Kind.Camera]: "\uE960", [Kind.Microphone]: "\uE720" };
  const slotGlyph = "\uE80A";
  const groups = [
    { kind: Kind.Screen, word: "screens" },
    { kind: Kind.Camera, word: "cameras" },
    { kind: Kind.Microphone, word: "microphones" },
    { kind: Kind.File, word: "files" }
  ];

  const word = key => root.dataset["w" + key.replace(/(^|-)(\w)/g, (_, __, c) => c.toUpperCase())] || "";

  const stage = root.querySelector("[data-composer-stage]");
  const blank = root.querySelector("[data-composer-blank]");
  const catalogBox = root.querySelector("[data-composer-catalog]");
  const usedBox = root.querySelector("[data-composer-used]");
  const layersBox = root.querySelector("[data-composer-layers]");
  const scenesBox = root.querySelector("[data-composer-layouts]");
  const nameBox = root.querySelector("[data-composer-name]");
  const sizeBox = root.querySelector("[data-composer-size]");
  const dirtyMark = root.querySelector("[data-composer-dirty]");
  const deleteButton = root.querySelector("[data-composer-delete]");
  const saveButton = root.querySelector("[data-composer-save]");
  const startButton = root.querySelector("[data-composer-start]");
  const pathBox = root.querySelector("[data-composer-path]");
  const guides = { x: stage.querySelector('[data-guide="x"]'), y: stage.querySelector('[data-guide="y"]') };
  // The live wizard sends this form once the layout is saved: the setting and the destination
  // were picked on the step before, so the start goes out with nothing else to ask.
  const startForm = document.querySelector("[data-composer-start-form]");

  // How close, in screen pixels, an edge has to come to a guide line before it sticks to it.
  const snapDistance = 8;
  const minimumSize = 48;

  // The scene: items are stacked in array order, the first one at the bottom. That is the order
  // the server stores them in and the order ffmpeg overlays them in. In the live wizard the layout
  // it was opened from is kept apart from it: the scene of a live is never saved over a layout.
  let scene = { pkid: null, name: "", width: 1920, height: 1080, items: [] };
  let layoutPkid = null;
  let catalog = [];
  let scenes = [];
  let selected = null;
  let dirty = false;
  let nextUid = 1;
  const tiles = new Map();

  const notify = (kind, text) => {
    // The one infobar of a page that answers with a fetch lives in site.js.
    const heading = { error: "ko", warning: "warning" }[kind] || "ok";
    if (typeof say === "function") say(kind, word(heading), text, "");
  };

  const sameSource = (a, b) => a.kind === b.kind && a.target === b.target;
  const clamp = (value, low, high) => Math.min(Math.max(value, low), Math.max(low, high));
  // Everything that takes room on the canvas, the empty slots included: what a tile snaps to.
  const pictures = () => scene.items.filter(item => hasPicture(item.kind));
  const slots = () => scene.items.filter(isSlot);
  const sources = () => scene.items.filter(item => !isSlot(item));

  // Only the layout page says so: in the live wizard the start button saves the scene itself.
  const markDirty = () => {
    dirty = true;
    if (dirtyMark) dirtyMark.hidden = false;
  };

  const markClean = () => {
    dirty = false;
    if (dirtyMark) dirtyMark.hidden = true;
  };

  // ---------- Geometry ----------

  // The aspect a source really has, when it is known: from the catalog for a screen, from the
  // still once it arrives for the others. Unknown, a webcam and a video are assumed 16:9.
  const aspectOf = item => isSlot(item) && item.w > 0 && item.h > 0
    ? item.w / item.h
    : (item.naturalWidth > 0 && item.naturalHeight > 0)
      ? item.naturalWidth / item.naturalHeight
      : 16 / 9;

  // The largest rectangle of that aspect that fits the canvas, centred: what "fill" means for a
  // source that is not the shape of the output (ffmpeg pads the rest, it never stretches).
  const fitted = aspect => {
    let width = scene.width;
    let height = Math.round(width / aspect);
    if (height > scene.height) {
      height = scene.height;
      width = Math.round(height * aspect);
    }
    return { x: Math.round((scene.width - width) / 2), y: Math.round((scene.height - height) / 2), w: width, h: height };
  };

  const keepInside = item => {
    item.w = clamp(Math.round(item.w), minimumSize, scene.width);
    item.h = clamp(Math.round(item.h), minimumSize, scene.height);
    item.x = clamp(Math.round(item.x), 0, scene.width - item.w);
    item.y = clamp(Math.round(item.y), 0, scene.height - item.h);
    return item;
  };

  // Screen pixels to output pixels: the stage is drawn at whatever size the window gives it.
  const scale = () => scene.width / stage.getBoundingClientRect().width;

  const pointOnCanvas = event => {
    const box = stage.getBoundingClientRect();
    return {
      x: (event.clientX - box.left) * scene.width / box.width,
      y: (event.clientY - box.top) * scene.height / box.height
    };
  };

  // Whether a point lands inside a rectangle.
  const covers = (item, point) =>
    point.x >= item.x && point.x <= item.x + item.w &&
    point.y >= item.y && point.y <= item.y + item.h;

  const isFullFrame = item => item.x <= 0 && item.y <= 0 && item.w >= scene.width && item.h >= scene.height;

  // What a drop at that point is aimed at, worked out on the rectangles and not on what the
  // browser happened to hit: the tiles are positioned with percentages and stacked in array
  // order, so hit-testing would hand every drop to whichever tile happens to be painted last.
  //
  // 1. A box smaller than the frame under the pointer is aimed at on purpose: it wins, an empty
  //    sagoma before a filled one.
  // 2. Otherwise the pointer is only over the full frame, which covers everything: the shape the
  //    user clicked last is what the drag is meant for (select sagoma 2, drag, it fills sagoma 2).
  // 3. Otherwise the full-frame sagoma takes it. A full-frame source that is not a sagoma is the
  //    backdrop of a free composition: a drop on it opens a new layer instead of replacing it.
  const dropTarget = point => {
    const under = pictures().filter(item => covers(item, point) && (isShape(item) || !isFullFrame(item)));
    const inner = under.filter(item => !isFullFrame(item));
    return inner.filter(isSlot).pop() || inner.pop() || selectedShape() || under.pop() || null;
  };

  // ---------- Items ----------

  // The rectangle a source takes when it lands at that point: the first thing on the canvas is
  // what the viewer expects to fill the frame; a webcam, or anything laid over something already
  // there, starts as a third of the width. Drawn as a ghost while the drag is in the air, so the
  // drop lands where the picture said it would.
  const boxFor = (option, at, aspect) => {
    const fills = pictures().length === 0 && option.kind !== Kind.Camera;
    if (fills) return fitted(aspect);
    const w = Math.round(scene.width / 3);
    const h = Math.round(w / aspect);
    const centre = at || { x: scene.width / 2, y: scene.height / 2 };
    return keepInside({ x: centre.x - w / 2, y: centre.y - h / 2, w, h });
  };

  const itemFrom = (option, at) => {
    const item = {
      uid: nextUid++,
      kind: option.kind,
      target: option.target,
      label: option.label || option.name || option.target,
      naturalWidth: option.width || 0,
      naturalHeight: option.height || 0,
      // The pixels the source really has (ffprobe for a file, the catalog for a screen): what the
      // output resolution is capped to. The still cannot say it, it is scaled down.
      sourceWidth: option.width || 0,
      sourceHeight: option.height || 0,
      // A file brings its sound along by default: that is what playing a video means. A
      // microphone is only there to be heard.
      audio: option.kind === Kind.File || option.kind === Kind.Microphone,
      // Until the user sizes it, a tile may still take the aspect of its still when it arrives.
      autoSized: !(option.width > 0),
      x: 0, y: 0, w: 0, h: 0
    };

    if (hasPicture(item.kind)) Object.assign(item, boxFor(option, at, aspectOf(item)));
    return item;
  };

  // A slot is a rectangle and a name: the first one fills the canvas, the next ones start as a
  // third of it, the way a source laid over another one does.
  const slotFrom = at => {
    const first = pictures().length === 0;
    const item = {
      uid: nextUid++,
      slot: true,
      kind: null,
      target: "",
      label: `${word("slot")} ${slots().length + 1}`,
      audio: false,
      autoSized: false,
      x: 0, y: 0,
      w: first ? scene.width : Math.round(scene.width / 3),
      h: first ? scene.height : Math.round(scene.width / 3 * 9 / 16)
    };
    if (!first) {
      const centre = at || { x: scene.width / 2, y: scene.height / 2 };
      item.x = centre.x - item.w / 2;
      item.y = centre.y - item.h / 2;
    }
    keepInside(item);
    return item;
  };

  const addSlot = at => {
    const item = slotFrom(at);
    scene.items.push(item);
    select(item.uid);
    markDirty();
    render();
  };

  // Something new on the canvas, or something gone: the tile is drawn, and the resolution follows
  // the biggest shape, once the real size of its video is known.
  const changed = item => {
    markDirty();
    fitResolution();
    render();
    if (item) learnSize(item);
  };

  // A source on bare canvas: a new layer where the pointer is.
  const addLayer = (option, at) => {
    const existing = scene.items.find(item => sameSource(item, option));
    if (existing) {
      // One device cannot be opened twice, and the same file twice is never what was meant.
      notify("warning", word("twice"));
      select(existing.uid);
      return;
    }

    const item = itemFrom(option, at);
    scene.items.push(item);
    select(item.uid);
    changed(item);
  };

  // A source that goes into a rectangle takes it whole: the box keeps its place in the stack, so
  // a full frame one stays under the boxes laid on top of it instead of jumping over them.
  const occupies = (target, item) => {
    Object.assign(item, { x: target.x, y: target.y, w: target.w, h: target.h, autoSized: false });
    // The source remembers the slot it went into, so taking it off gives the skeleton back.
    item.slotLabel = isSlot(target) ? target.label : target.slotLabel;
    return item;
  };

  // The shape the user clicked last: the outline is coloured, so the drag that follows is meant for
  // it even if the pointer is let go on the full frame around it.
  const selectedShape = () => {
    const item = scene.items.find(entry => entry.uid === selected);
    return item && isShape(item) ? item : null;
  };

  // Picked from the list rather than dropped: the sagoma that was clicked if it is still empty,
  // otherwise the skeleton is filled in order, then the clicked one has its video replaced.
  const pickedTarget = () => {
    const chosen = selectedShape();
    return (chosen && isSlot(chosen) ? chosen : null) || slots()[0] || chosen;
  };

  // The one way a source reaches the canvas, whether it was dropped or picked: the target box is
  // filled (see dropTarget), and with no box to fill a new layer opens where the pointer is. A
  // source already on the canvas is moved to the new box rather than refused: dropping it in
  // another box is how a box changes its mind.
  const place = (option, at) => {
    // A microphone has no picture to put in a rectangle: it joins the mix and every sagoma stays.
    if (!hasPicture(option.kind)) {
      addLayer(option);
      return;
    }

    const target = at ? dropTarget(at) : pickedTarget();
    if (!target) {
      addLayer(option, at);
      return;
    }

    const taken = scene.items.find(item => sameSource(item, option));
    if (taken) {
      if (taken.uid === target.uid) {
        select(taken.uid);
        return;
      }
      // On its way to another box: the one it leaves behind comes back as an empty sagoma.
      removeItem(taken.uid);
    }

    const index = scene.items.findIndex(item => item.uid === target.uid);
    if (index < 0) return;
    const item = occupies(target, itemFrom(option));
    scene.items[index] = item;
    tiles.get(target.uid)?.remove();
    tiles.delete(target.uid);

    select(item.uid);
    changed(item);
  };

  const removeItem = (uid, { giveSlotBack = true } = {}) => {
    const index = scene.items.findIndex(item => item.uid === uid);
    if (index < 0) return;
    const item = scene.items[index];
    tiles.get(uid)?.remove();
    tiles.delete(uid);
    if (selected === uid) selected = null;

    // A source taken out of a slot gives the slot back; the slot itself is what goes for good.
    if (giveSlotBack && item.slotLabel !== undefined) {
      scene.items[index] = { ...slotFrom(), label: item.slotLabel, x: item.x, y: item.y, w: item.w, h: item.h };
    } else {
      scene.items.splice(index, 1);
    }
    changed();
  };

  const moveInStack = (uid, step) => {
    const index = scene.items.findIndex(item => item.uid === uid);
    const target = index + step;
    if (index < 0 || target < 0 || target >= scene.items.length) return;
    [scene.items[index], scene.items[target]] = [scene.items[target], scene.items[index]];
    markDirty();
    render();
  };

  const fill = uid => {
    const item = scene.items.find(entry => entry.uid === uid);
    if (!item || !hasPicture(item.kind)) return;
    // A slot has no shape of its own: filling it is the whole frame.
    Object.assign(item, isSlot(item) ? { x: 0, y: 0, w: scene.width, h: scene.height } : fitted(aspectOf(item)));
    item.autoSized = false;
    markDirty();
    render();
  };

  const select = uid => {
    selected = uid;
    for (const [key, tile] of tiles) tile.classList.toggle("is-selected", key === uid);
    for (const row of layersBox.children) row.classList.toggle("is-selected", Number(row.dataset.uid) === uid);
  };

  // ---------- Drawing ----------

  // Every still is asked for at its own URL: the address of a source never changes, so without
  // this the WebView would answer a scene saved yesterday with the answer it gave then.
  let stillAsked = 0;
  // A tile and a catalog entry both say what they are with a kind and a target, which is all the
  // still endpoint asks for.
  const snapshotUrl = item =>
    `/preview/sources/snapshot?kind=${item.kind}&target=${encodeURIComponent(item.target)}&v=${++stillAsked}`;

  // How many times a tile asks again before settling for its icon. A source that cannot be grabbed
  // on the first try is normal rather than broken: ffmpeg may still be listing the devices, and a
  // camera can be busy in another application for a moment.
  const stillAttempts = 2;

  const glyphIcon = glyph => {
    const icon = document.createElement("i");
    icon.className = "icon";
    icon.textContent = glyph;
    return icon;
  };

  const iconOf = kind => glyphIcon(glyphs[kind] || "\uE714");

  // A shape is the rectangle a layout is made of, and it outlives whatever is dropped into it: the
  // dashed outline and the name stay on the canvas with the video inside, so the composition reads
  // at a glance (shape 1 is pippo, shape 2 is piero) and the same layout can be filled again for
  // the next live.
  const shapeNameOf = item => (isSlot(item) ? item.label : (item.slotLabel ?? null));

  const isShape = item => shapeNameOf(item) !== null;

  // What a tile says about itself: the shape it is, and inside it what it is showing.
  const titleOf = item => {
    const shape = shapeNameOf(item);
    return shape && !isSlot(item) ? shape + " · " + item.label : item.label;
  };

  // A tile is made once and then only moved: its still is asked for when it is created, and
  // redrawing the canvas must not open the webcam again every time a tile is nudged.
  const tileOf = item => {
    let tile = tiles.get(item.uid);
    if (tile) return tile;

    tile = document.createElement("div");
    tile.className = "composer-tile";
    tile.dataset.uid = item.uid;

    if (isSlot(item)) {
      // An empty slot is its outline, its name and, in the live wizard, what to do with it.
      tile.classList.add("is-slot");
      const hollow = document.createElement("div");
      hollow.className = "composer-slot";
      hollow.append(glyphIcon(layoutMode ? slotGlyph : "\uE710"));
      if (!layoutMode) {
        const hint = document.createElement("span");
        hint.textContent = word("slot-hint");
        hollow.append(hint);
      }
      const name = document.createElement("span");
      name.className = "composer-tile-label";
      name.append(glyphIcon(slotGlyph), document.createTextNode(item.label));
      tile.append(hollow, name);
      appendHandles(tile, item);
      return tile;
    }

    // Filled, and still the rectangle of the layout: the outline and the name of the shape stay.
    if (isShape(item)) tile.classList.add("is-shape");

    const cover = document.createElement("div");
    cover.className = "composer-tile-cover";
    cover.append(iconOf(item.kind));

    const still = document.createElement("img");
    still.alt = "";
    still.draggable = false;
    still.hidden = true;
    tile.still = still;

    let tries = 0;
    // The picture that is already there stays up until the new one arrives: a tile never blinks
    // back to its icon because a frame is on its way.
    const ask = () => { still.src = snapshotUrl(item); };
    ask();

    still.addEventListener("load", () => {
      still.hidden = false;
      cover.hidden = true;
      // The still is the first time a video or a webcam says what shape it is.
      if (item.autoSized && still.naturalWidth > 0) {
        item.naturalWidth = still.naturalWidth;
        item.naturalHeight = still.naturalHeight;
        item.h = Math.round(item.w / aspectOf(item));
        keepInside(item);
        item.autoSized = false;
        render();
      }
    });

    // The server answers "no content" when it cannot open the source, which the browser reads as
    // a broken image. That is a moment, not a verdict: the tile asks again, and the tile of a
    // scene that was saved on a machine that was asleep still opens with pictures on it.
    still.addEventListener("error", () => {
      if (!still.isConnected || tries++ >= stillAttempts) return;
      setTimeout(() => still.isConnected && ask(), 500 * tries);
    });

    const label = document.createElement("span");
    label.className = "composer-tile-label";
    label.append(iconOf(item.kind), document.createTextNode(titleOf(item)));

    tile.append(cover, still, label);
    appendHandles(tile, item);
    return tile;
  };

  const appendHandles = (tile, item) => {
    for (const corner of ["nw", "ne", "sw", "se"]) {
      const handle = document.createElement("span");
      handle.className = "composer-handle " + corner;
      handle.dataset.corner = corner;
      tile.append(handle);
    }

    tile.addEventListener("pointerdown", event => startGesture(event, item));
    tile.addEventListener("dblclick", () => fill(item.uid));
    tiles.set(item.uid, tile);
  };

  // A new frame of everything already on the canvas: a still is a moment, and the source that
  // could not be grabbed a minute ago is usually there now.
  const refreshStills = () => {
    for (const item of sources()) {
      const tile = tiles.get(item.uid);
      if (tile?.still) tile.still.src = snapshotUrl(item);
    }
  };

  const placeTile = (tile, item) => {
    tile.style.left = (item.x / scene.width * 100) + "%";
    tile.style.top = (item.y / scene.height * 100) + "%";
    tile.style.width = (item.w / scene.width * 100) + "%";
    tile.style.height = (item.h / scene.height * 100) + "%";
    tile.title = `${titleOf(item)} · ${item.w}×${item.h} @ ${item.x},${item.y}`;
  };

  const layerButton = (glyph, title, action, pressed) => {
    const button = document.createElement("button");
    button.type = "button";
    button.className = "btn subtle icon-only";
    button.title = title;
    button.setAttribute("aria-label", title);
    if (pressed !== undefined) button.setAttribute("aria-pressed", pressed ? "true" : "false");
    button.append(glyphIcon(glyph));
    button.addEventListener("click", event => {
      event.stopPropagation();
      action();
    });
    return button;
  };

  const renderLayers = () => {
    layersBox.replaceChildren();

    // Top of the list is top of the stack: the order people read layers in any editor.
    const ordered = [...scene.items].reverse();
    for (const item of ordered) {
      const row = document.createElement("li");
      row.className = "composer-layer";
      row.dataset.uid = item.uid;
      row.classList.toggle("is-selected", item.uid === selected);
      row.classList.toggle("is-audio", !hasPicture(item.kind));
      row.classList.toggle("is-slot", isSlot(item));
      row.classList.toggle("is-shape", isShape(item) && !isSlot(item));

      const text = document.createElement("span");
      text.className = "grow";
      const name = document.createElement("strong");
      name.className = "truncate";
      // A filled shape says both things: the rectangle it is, and the video inside it.
      name.textContent = titleOf(item);
      const facts = document.createElement("span");
      facts.className = "caption";
      const where = `${item.w}×${item.h} · ${item.x}, ${item.y}`;
      facts.textContent = !hasPicture(item.kind) ? word("audio-only")
        : isSlot(item) && !layoutMode ? `${word("slot-empty")} · ${where}`
        : where;
      text.append(name, facts);

      const actions = document.createElement("span");
      actions.className = "composer-layer-actions";
      if (!isSlot(item) && canCarrySound(item.kind)) {
        actions.append(layerButton(item.audio ? "\uE767" : "\uE74F", word("sound"), () => {
          item.audio = !item.audio;
          markDirty();
          render();
        }, item.audio));
      }
      if (hasPicture(item.kind)) {
        actions.append(
          layerButton("\uE70E", word("up"), () => moveInStack(item.uid, 1)),
          layerButton("\uE70D", word("down"), () => moveInStack(item.uid, -1)),
          layerButton("\uE740", word("fill"), () => fill(item.uid)));
      }
      actions.append(layerButton("\uE711", word("remove"), () => removeItem(item.uid)));

      row.append(isSlot(item) ? glyphIcon(slotGlyph) : iconOf(item.kind), text, actions);
      row.addEventListener("click", () => select(item.uid));
      layersBox.append(row);
    }
  };

  const renderCatalog = () => {
    if (!catalogBox) return;
    catalogBox.replaceChildren();
    usedBox.replaceChildren();

    const usedOptions = [];
    const availableOptions = [];

    for (const option of catalog) {
      const isUsed = scene.items.some(item => sameSource(item, option));
      if (isUsed) {
        usedOptions.push(option);
      } else {
        availableOptions.push(option);
      }
    }

    // --- Used Sources ---
    if (usedOptions.length === 0) {
      const empty = document.createElement("p");
      empty.className = "caption composer-none";
      empty.textContent = word("empty-used") || "No sources are currently used.";
      usedBox.append(empty);
    } else {
      for (const option of usedOptions) {
        usedBox.append(createEntry(option, true));
      }
    }

    // --- Available Sources ---
    if (catalog.length === 0) {
      const empty = document.createElement("p");
      empty.className = "caption composer-none";
      empty.textContent = word("empty");
      catalogBox.append(empty);
      return;
    }

    if (availableOptions.length === 0) {
      const empty = document.createElement("p");
      empty.className = "caption composer-none";
      empty.textContent = word("empty-available") || "No other sources available.";
      catalogBox.append(empty);
      return;
    }

    const availableAudio = availableOptions.filter(o => o.kind === Kind.Microphone);
    const availableVideo = availableOptions.filter(o => o.kind !== Kind.Microphone);

    const appendGroup = (options, title) => {
      if (options.length === 0) return;
      const heading = document.createElement("h4");
      heading.className = "caption composer-group";
      heading.textContent = title;
      catalogBox.append(heading);
      for (const option of options) {
        catalogBox.append(createEntry(option, false));
      }
    };

    appendGroup(availableVideo, word("video-group") || "Video");
    appendGroup(availableAudio, word("audio-group") || "Audio");
  };

  const render = () => {
    stage.style.aspectRatio = `${scene.width} / ${scene.height}`;

    // The DOM order of the tiles is their stacking order, the same one ffmpeg will use.
    for (const item of scene.items) {
      if (!hasPicture(item.kind)) continue;
      const tile = tileOf(item);
      placeTile(tile, item);
      tile.classList.toggle("is-selected", item.uid === selected);
      stage.append(tile);
    }

    blank.hidden = pictures().length > 0;
    if (deleteButton) deleteButton.hidden = scene.pkid === null;
    renderLayers();
    renderCatalog();
  };

  // ---------- Moving and resizing ----------

  // The lines worth sticking to: the edges and the middle of the canvas, and the edges of every
  // other tile, which is how two webcams end up side by side instead of almost side by side.
  const snapLines = (except, axis) => {
    const size = axis === "x" ? scene.width : scene.height;
    const lines = [0, size / 2, size];
    for (const item of pictures()) {
      if (item.uid === except) continue;
      const start = axis === "x" ? item.x : item.y;
      const length = axis === "x" ? item.w : item.h;
      lines.push(start, start + length);
    }
    return lines;
  };

  // Moves a span [start, start + length] so that one of its edges or its middle sits on the
  // nearest line, if one is close enough. Returns the new start and the line it stuck to.
  const snapSpan = (start, length, lines, tolerance) => {
    let best = null;
    for (const line of lines) {
      for (const offset of [0, length / 2, length]) {
        const distance = Math.abs(start + offset - line);
        if (distance <= tolerance && (!best || distance < best.distance)) {
          best = { distance, start: line - offset, line };
        }
      }
    }
    return best ? { start: best.start, line: best.line } : { start, line: null };
  };

  const showGuide = (axis, line) => {
    const guide = guides[axis];
    guide.hidden = line === null;
    if (line === null) return;
    const size = axis === "x" ? scene.width : scene.height;
    guide.style[axis === "x" ? "left" : "top"] = (line / size * 100) + "%";
  };

  const startGesture = (event, item) => {
    if (event.button !== 0) return;
    event.preventDefault();
    select(item.uid);
    stage.focus({ preventScroll: true });

    const corner = event.target.dataset?.corner || null;
    const origin = { x: event.clientX, y: event.clientY };
    const from = { x: item.x, y: item.y, w: item.w, h: item.h };
    const ratio = from.w / from.h;
    const unit = scale();
    const tolerance = snapDistance * unit;
    const target = event.currentTarget;
    target.setPointerCapture(event.pointerId);
    let moved = false;

    const onMove = move => {
      const dx = (move.clientX - origin.x) * unit;
      const dy = (move.clientY - origin.y) * unit;
      if (!moved && Math.abs(dx) + Math.abs(dy) < 2 * unit) return;
      moved = true;

      if (!corner) {
        const x = snapSpan(from.x + dx, from.w, snapLines(item.uid, "x"), move.altKey ? 0 : tolerance);
        const y = snapSpan(from.y + dy, from.h, snapLines(item.uid, "y"), move.altKey ? 0 : tolerance);
        item.x = x.start;
        item.y = y.start;
        keepInside(item);
        showGuide("x", x.line);
        showGuide("y", y.line);
      } else {
        // The corner that is dragged moves; the opposite one stays where it was.
        const west = corner.includes("w");
        const north = corner.includes("n");
        let width = from.w + (west ? -dx : dx);
        let height = from.h + (north ? -dy : dy);
        if (!move.shiftKey) {
          // Keep the proportions, following whichever direction the pointer moved more in.
          if (Math.abs(dx) * ratio >= Math.abs(dy)) height = width / ratio;
          else width = height * ratio;
        }
        width = clamp(width, minimumSize, west ? from.x + from.w : scene.width - from.x);
        height = clamp(height, minimumSize, north ? from.y + from.h : scene.height - from.y);
        if (!move.shiftKey) {
          // The clamp may have cut one side: cut the other one to match.
          if (width / height > ratio) width = height * ratio;
          else height = width / ratio;
        }
        item.w = width;
        item.h = height;
        item.x = west ? from.x + from.w - width : from.x;
        item.y = north ? from.y + from.h - height : from.y;
        item.autoSized = false;
        keepInside(item);
      }

      placeTile(target, item);
    };

    const onEnd = () => {
      target.removeEventListener("pointermove", onMove);
      target.removeEventListener("pointerup", onEnd);
      target.removeEventListener("pointercancel", onEnd);
      showGuide("x", null);
      showGuide("y", null);
      if (moved) {
        markDirty();
        render();
      }
    };

    target.addEventListener("pointermove", onMove);
    target.addEventListener("pointerup", onEnd);
    target.addEventListener("pointercancel", onEnd);
  };

  stage.addEventListener("pointerdown", event => {
    if (event.target === stage || event.target === blank || blank.contains(event.target)) select(null);
  });

  stage.addEventListener("keydown", event => {
    const item = scene.items.find(entry => entry.uid === selected);
    if (!item) return;

    if (event.key === "Delete" || event.key === "Backspace") {
      event.preventDefault();
      removeItem(item.uid);
      return;
    }

    const step = event.shiftKey ? 10 : 1;
    const moves = { ArrowLeft: [-step, 0], ArrowRight: [step, 0], ArrowUp: [0, -step], ArrowDown: [0, step] };
    const move = moves[event.key];
    if (!move || !hasPicture(item.kind)) return;
    event.preventDefault();
    item.x += move[0];
    item.y += move[1];
    keepInside(item);
    markDirty();
    render();
  });

  // ---------- Dragging sources onto the canvas ----------

  const sourceType = "application/x-orbis-source";
  let pendingDrop = null;
  // What the drag in the air is carrying. The payload of a drag cannot be read while it is still
  // travelling (the browser hands it over on drop only), so the entry that started the drag
  // leaves the option here: it is what the ghost is measured from and what the drop resolves to.
  let carried = null;

  // The ghost: the rectangle the source will take, drawn where it will take it, with the words
  // that say what letting go is about to do. It is the whole difference between a drag that goes
  // where you aimed it and one that quietly swaps what was already there.
  const ghost = document.createElement("div");
  ghost.className = "composer-drop";
  ghost.hidden = true;
  // The first frame of what is being dragged, inside the rectangle it is about to take: the sagoma
  // fills with the picture of the video while the pointer is still moving, not after it is let go.
  const ghostFrame = document.createElement("img");
  ghostFrame.alt = "";
  ghostFrame.draggable = false;
  ghostFrame.hidden = true;
  const ghostLabel = document.createElement("span");
  ghostLabel.className = "composer-drop-label";
  ghost.append(ghostFrame, ghostLabel);
  stage.append(ghost);

  // The frame is asked for once per source, and only while the drag lasts: a tile that is already
  // on the canvas has its own, and a source that cannot be grabbed keeps the hollow ghost.
  let ghostAsked = 0;
  let ghostSrc = "";

  const showGhostFrame = option => {
    const src = snapshotUrl(option);
    if (src === ghostSrc) return;
    ghostSrc = src;
    ghostFrame.hidden = true;
    ghostFrame.src = src;
  };

  // A file dragged in from Explorer is only known to be a video until the host answers with its
  // path: until then the ghost is measured on a plain 16:9.
  const fileShape = { kind: Kind.File, name: "", width: 0, height: 0 };
  const inFlight = () => carried || fileShape;

  const carriesSource = event => {
    const types = [...(event.dataTransfer?.types || [])];
    return types.includes(sourceType) || types.includes("Files");
  };

  // What a drop at that point will do, and where it will land. A microphone has no rectangle to
  // land in: it joins the sound of the canvas wherever it is let go, so the ghost stays away
  // rather than promising a box it will never take.
  const previewAt = (option, at) => {
    if (!hasPicture(option.kind)) return null;
    // The same answer the drop will give: the ghost never promises a box the drop then misses.
    const target = dropTarget(at);
    if (target) {
      return {
        box: { x: target.x, y: target.y, w: target.w, h: target.h },
        target,
        label: isSlot(target) ? `${word("drop-into-slot")} ${target.label}` : `${word("drop-into-tile")} ${titleOf(target)}`
      };
    }
    // Bare canvas. On a canvas with nothing on it yet the source takes the frame, and the ghost
    // says so rather than calling it a layer.
    const fills = pictures().length === 0 && option.kind !== Kind.Camera;
    const shape = { kind: option.kind, w: 0, h: 0, naturalWidth: option.width || 0, naturalHeight: option.height || 0 };
    return { box: boxFor(shape, at, aspectOf(shape)), target: null, label: fills ? word("fill") : word("drop-as-layer") };
  };

  const clearDrop = () => {
    ghost.hidden = true;
    ghostFrame.hidden = true;
    ghostSrc = "";
    stage.classList.remove("is-over");
    for (const tile of stage.querySelectorAll(".composer-tile.is-drop-on")) tile.classList.remove("is-drop-on");
  };

  const showDrop = (option, at) => {
    for (const tile of stage.querySelectorAll(".composer-tile.is-drop-on")) tile.classList.remove("is-drop-on");
    const preview = previewAt(option, at);
    if (!preview) {
      ghost.hidden = true;
      return;
    }

    ghost.style.left = (preview.box.x / scene.width * 100) + "%";
    ghost.style.top = (preview.box.y / scene.height * 100) + "%";
    ghost.style.width = (preview.box.w / scene.width * 100) + "%";
    ghost.style.height = (preview.box.h / scene.height * 100) + "%";
    ghostLabel.textContent = preview.label;
    ghost.hidden = false;
    showGhostFrame(option);
    if (preview.target) tiles.get(preview.target.uid)?.classList.add("is-drop-on");
  };

  ghostFrame.addEventListener("load", () => { if (!ghost.hidden) ghostFrame.hidden = false; });
  ghostFrame.addEventListener("error", () => { ghostFrame.hidden = true; });

  stage.addEventListener("dragover", event => {
    if (layoutMode || !carriesSource(event)) return;
    event.preventDefault();
    event.dataTransfer.dropEffect = "copy";
    stage.classList.add("is-over");
    showDrop(inFlight(), pointOnCanvas(event));
  });

  stage.addEventListener("dragleave", event => {
    // Leaving for the sidebar and not for another tile: there is nothing under the pointer any more.
    if (stage.contains(event.relatedTarget)) return;
    clearDrop();
  });

  // A drag let go outside the canvas, or on the catalog, still has to take its ghost with it.
  document.addEventListener("dragend", () => {
    carried = null;
    clearDrop();
  });

  stage.addEventListener("drop", event => {
    if (layoutMode) return;

    const id = event.dataTransfer?.getData(sourceType);
    const files = event.dataTransfer?.files;
    // Anything else dragged in (a word of text, a link) is none of this canvas's business.
    if (!id && !(files?.length && window.chrome?.webview)) return;
    event.preventDefault();

    const at = pointOnCanvas(event);
    carried = null;
    clearDrop();

    if (id) {
      const option = catalog.find(candidate => candidate.id === id);
      if (option) place(option, at);
      return;
    }

    // A video dragged from Explorer: the browser never tells a path, so the file goes to the
    // WebView2 host, which answers with it (the same route the path fields of the settings use).
    pendingDrop = at;
    window.chrome.webview.postMessageWithAdditionalObjects("dropPath", files);
  });

  window.chrome?.webview?.addEventListener("message", event => {
    if (typeof event.data === "string") {
      try {
        const payload = JSON.parse(event.data);
        if (payload.type === "browseVideo" && payload.path) {
          addFile(payload.path, pendingDrop);
          pendingDrop = null;
          return;
        }
      } catch {
        // Plain string from drop
        if (!pendingDrop) return;
        const at = pendingDrop;
        pendingDrop = null;
        addFile(event.data, at);
      }
    }
  });

  const fileOption = path => ({
    id: path,
    name: path.split(/[\\/]/).pop() || path,
    kind: Kind.File,
    target: path
  });

  const addFile = (path, at) => {
    const trimmed = path.trim().replace(/^"(.*)"$/, "$1");
    if (!trimmed) return;
    let option = catalog.find(candidate => candidate.kind === Kind.File && candidate.target === trimmed);
    if (!option) {
      option = fileOption(trimmed);
      catalog.push(option);
      drawCatalog();
    }
    place(option, at);
  };

  root.querySelector("[data-composer-add-path]")?.addEventListener("click", () => {
    if (!pathBox.value.trim() && window.chrome?.webview) {
      window.chrome.webview.postMessage("browseVideo");
    } else {
      addFile(pathBox.value);
      pathBox.value = "";
    }
  });

  // A file dropped on the field comes back from the host as a scripted input event: that one
  // goes on the canvas straight away, while a path being typed waits for Enter or the button.
  pathBox?.addEventListener("input", event => {
    if (event.isTrusted) return;
    addFile(pathBox.value);
    pathBox.value = "";
  });

  pathBox?.addEventListener("keydown", event => {
    if (event.key !== "Enter") return;
    event.preventDefault();
    addFile(pathBox.value);
    pathBox.value = "";
  });

  // ---------- The catalog ----------

  const sizeText = option => option.width > 0 ? `${option.width}×${option.height}` : "";


  const createEntry = (option, isUsedItem = false) => {
    const entry = document.createElement("button");
    entry.type = "button";
    entry.className = "composer-source";
    if (isUsedItem) entry.classList.add("is-used");
    // A source already on the canvas drags as well as the others: dragging it into another
    // sagoma is how a box changes its mind without going back to the list first.
    entry.draggable = true;
    entry.dataset.sourceId = option.id;
    entry.title = isUsedItem ? word("remove") : word("add");

    const text = document.createElement("span");
    text.className = "grow";
    const name = document.createElement("strong");
    name.className = "truncate";
    name.textContent = option.name;
    text.append(name);
    const size = sizeText(option);
    if (size) {
      const caption = document.createElement("span");
      caption.className = "caption";
      caption.textContent = size;
      text.append(caption);
    }

    const actionIcon = document.createElement("i");
    actionIcon.className = "icon composer-used";
    actionIcon.textContent = isUsedItem ? "\uE711" : "\uE73E"; // Remove icon or Used tick

    entry.append(iconOf(option.kind), text, actionIcon);

    entry.addEventListener("dragstart", event => {
      event.dataTransfer.setData(sourceType, option.id);
      event.dataTransfer.effectAllowed = "copy";
      carried = option;
    });

    if (!isUsedItem) {
      entry.addEventListener("click", () => place(option));
    } else {
      entry.addEventListener("click", () => {
        const item = scene.items.find(i => sameSource(i, option));
        if (item) removeItem(item.uid);
      });
    }

    return entry;
  };
  const drawCatalog = () => {
    delete catalogBox.dataset.state;
    renderCatalog();
  };

  const loadCatalog = async () => {
    catalogBox.dataset.state = "loading";
    try {
      const response = await fetch("/preview/sources");
      if (!response.ok) throw new Error(String(response.status));
      const listed = await response.json();
      // Files added by hand stay in the list across a refresh: they are not in any folder.
      const added = catalog.filter(option => option.kind === Kind.File && !listed.some(entry => entry.id === option.id));
      catalog = [...listed, ...added];
      drawCatalog();
    } catch {
      catalogBox.dataset.state = "failed";
      catalogBox.replaceChildren();
      const failed = document.createElement("p");
      failed.className = "caption composer-none";
      failed.textContent = word("failed");
      catalogBox.append(failed);
    }
  };

  // Asking again is for the sources the page does not know yet, and for the pictures of the ones
  // already on the canvas: a still that failed once is worth another try.
  root.querySelector("[data-composer-refresh]")?.addEventListener("click", () => {
    refreshStills();
    loadCatalog();
  });

  // ---------- Scenes ----------

  const clearStage = () => {
    for (const tile of tiles.values()) tile.remove();
    tiles.clear();
    selected = null;
  };

  // ---------- Resolution ----------

  // The outputs on offer, smallest first.
  const sizes = [[1280, 720], [1920, 1080], [2560, 1440], [3840, 2160]];

  // The real resolution of a file, asked of ffprobe once per file: the catalog does not probe a
  // whole folder for it, and the still is scaled down so it cannot tell.
  const probes = new Map();
  const probeSize = target => {
    if (!probes.has(target)) {
      probes.set(target, fetch(`/preview/sources/probe?target=${encodeURIComponent(target)}`)
        .then(response => (response.status === 200 ? response.json() : null))
        .catch(() => null)
        .then(size => {
          // A file that could not be read now may be readable on the next drop.
          if (!size) probes.delete(target);
          return size;
        }));
    }
    return probes.get(target);
  };

  // A file learns its size once it is on the canvas. The catalog entry keeps it, so the list shows
  // it and the next drop of the same file knows it straight away.
  const learnSize = async item => {
    if (item.kind !== Kind.File || item.sourceWidth > 0) return;
    const size = await probeSize(item.target);
    if (!(size?.width > 0 && size?.height > 0)) return;
    for (const option of catalog) {
      if (option.kind === Kind.File && option.target === item.target) Object.assign(option, { width: size.width, height: size.height });
    }
    item.sourceWidth = size.width;
    item.sourceHeight = size.height;
    fitResolution();
    render();
  };

  // The biggest shape on the canvas is the frame the eye reads, and its video says how many pixels
  // there are to fill: the source whose size is known that takes the most room.
  const biggestSource = () => sources()
    .filter(item => hasPicture(item.kind) && item.sourceWidth > 0 && item.sourceHeight > 0)
    .sort((a, b) => b.w * b.h - a.w * a.h || a.uid - b.uid)[0] || null;

  // Nothing above the video is offered: the live would only upscale it. A portrait video is
  // measured on its long side the same way. A video smaller than every size still gets the first.
  const allowedSizes = source => {
    if (!source) return sizes;
    const long = Math.max(source.sourceWidth, source.sourceHeight);
    const short = Math.min(source.sourceWidth, source.sourceHeight);
    const allowed = sizes.filter(([w, h]) => w <= long && h <= short);
    return allowed.length ? allowed : sizes.slice(0, 1);
  };

  // Every tile is scaled with the canvas, so a scene drawn for 1080p is the same scene in 720p.
  const resizeCanvas = (width, height) => {
    if (width === scene.width && height === scene.height) return;
    const sx = width / scene.width;
    const sy = height / scene.height;
    for (const item of scene.items) {
      if (!hasPicture(item.kind)) continue;
      item.x *= sx;
      item.y *= sy;
      item.w *= sx;
      item.h *= sy;
    }
    scene.width = width;
    scene.height = height;
    for (const item of scene.items) if (hasPicture(item.kind)) keepInside(item);
    markDirty();
  };

  // The output follows the video of the biggest shape: when that video changes, the canvas takes
  // the largest size it fills (a 720p video in sagoma 1 makes a 1280 × 720 live), and the list
  // offers nothing above it. A smaller size picked by hand afterwards is kept.
  let decidedBy = "";
  const fitResolution = () => {
    const source = biggestSource();
    const allowed = allowedSizes(source);
    const key = source ? `${source.uid}:${source.sourceWidth}x${source.sourceHeight}` : "";
    const offered = allowed.some(([w, h]) => w === scene.width && h === scene.height);
    if (source && (key !== decidedBy || !offered)) resizeCanvas(...allowed[allowed.length - 1]);
    decidedBy = key;

    sizeBox.replaceChildren(...allowed.map(([w, h]) => new Option(`${w} × ${h}`, `${w}x${h}`)));
    // A layout drawn at a size that is not in the list keeps it until a video says otherwise.
    if (!allowed.some(([w, h]) => w === scene.width && h === scene.height)) {
      sizeBox.append(new Option(`${scene.width} × ${scene.height}`, `${scene.width}x${scene.height}`));
    }
    sizeBox.value = `${scene.width}x${scene.height}`;
  };

  const slotOf = (entry, index) => ({
    uid: nextUid++,
    slot: true,
    kind: null,
    target: "",
    label: entry.label || `${word("slot")} ${index + 1}`,
    audio: false,
    autoSized: false,
    x: entry.x, y: entry.y, w: entry.width, h: entry.height
  });

  const sourceOf = entry => {
    const option = catalog.find(candidate => candidate.kind === entry.sourceKind && candidate.target === entry.sourceTarget);
    return {
      uid: nextUid++,
      kind: entry.sourceKind,
      target: entry.sourceTarget,
      label: entry.label || option?.name || entry.sourceTarget,
      naturalWidth: option?.width || entry.width,
      naturalHeight: option?.height || entry.height,
      sourceWidth: option?.width || 0,
      sourceHeight: option?.height || 0,
      audio: !!entry.audioEnabled,
      autoSized: false,
      x: entry.x, y: entry.y, w: entry.width, h: entry.height
    };
  };

  // A layout always opens as its slots. On the layout page it is the thing being edited; in the
  // live wizard it is only where the new scene starts from, so the scene has no id until the live
  // starts, and saving it can never write over the layout.
  const loadScene = saved => {
    clearStage();
    // In compose mode, when loading a layout (asSlots is true), we load it as slots
    // In layout mode, it's the layout being edited
    const asSlots = !!saved?.isLayout;
    const items = (saved?.items || []).filter(entry => !asSlots || (entry.width > 0 && entry.height > 0));
    layoutPkid = asSlots ? saved.pkid : null;
    scene = {
      pkid: asSlots && !layoutMode ? null : saved?.pkid ?? null,
      name: saved?.name || "",
      width: saved?.width || 1920,
      height: saved?.height || 1080,
      items: items.map((entry, index) => asSlots ? slotOf(entry, index) : sourceOf(entry))
    };
    nameBox.value = scene.name;
    if (scenesBox) scenesBox.value = layoutPkid === null ? "" : String(layoutPkid);
    decidedBy = "";
    fitResolution();
    markClean();
    render();
    for (const item of sources()) learnSize(item);
  };

  // The first entry is the one the page was drawn with: "new layout" on the layout page, "no
  // layout" in the live wizard.
  const noLayout = scenesBox?.options[0]?.text || "";

  const drawScenes = () => {
    if (!scenesBox) return;
    scenesBox.replaceChildren(new Option(noLayout, ""), ...scenes.map(saved => new Option(saved.name, String(saved.pkid))));
    scenesBox.value = layoutPkid === null ? "" : String(layoutPkid);
  };

  // Only the layouts: the scene a live went on air with belongs to that live.
  const loadScenes = async () => {
    try {
      const response = await fetch("/scene/layouts");
      if (response.ok) scenes = await response.json();
    } catch {
      scenes = [];
    }
    drawScenes();
  };

  // A saved layout opens on the canvas as its shapes: on the layout page to be edited, in the live
  // wizard to be filled. The empty entry starts again from a blank canvas.
  scenesBox?.addEventListener("change", () => {
    const pkid = scenesBox.value;
    loadScene(pkid ? scenes.find(entry => String(entry.pkid) === pkid) || null : null);
  });

  nameBox.addEventListener("input", () => {
    scene.name = nameBox.value;
    markDirty();
  });

  // A new output size keeps the layout: every tile is scaled with the canvas.
  sizeBox.addEventListener("change", () => {
    resizeCanvas(...sizeBox.value.split("x").map(Number));
    render();
  });

  // A layout sends its slots and nothing else. The scene of a live sends its sources: a slot left
  // empty is a rectangle with nothing in it, which on air is the black of the canvas anyway.
  const requestOf = () => ({
    pkid: scene.pkid,
    name: scene.name.trim(),
    description: null,
    width: scene.width,
    height: scene.height,
    isLayout: layoutMode,
    items: (layoutMode ? slots() : sources()).map(item => ({
      sourceKind: isSlot(item) ? Kind.File : item.kind,
      sourceTarget: item.target,
      label: item.label,
      x: hasPicture(item.kind) ? item.x : 0,
      y: hasPicture(item.kind) ? item.y : 0,
      width: hasPicture(item.kind) ? item.w : 0,
      height: hasPicture(item.kind) ? item.h : 0,
      audioEnabled: !isSlot(item) && canCarrySound(item.kind) && item.audio
    }))
  });

  // The answer of a refused save is either the { response, message } envelope or a map of field
  // errors: the first text found is the one worth showing.
  const messageOf = payload => payload?.message || Object.values(payload || {}).find(value => typeof value === "string") || "";

  const setBusy = busy => {
    if (saveButton) saveButton.disabled = busy;
    if (startButton) startButton.disabled = busy;
  };

  const save = async () => {
    if (layoutMode && slots().length === 0) {
      notify("error", word("needs-slot"));
      return false;
    }
    if (!layoutMode && !sources().some(item => hasPicture(item.kind))) {
      notify("error", word("needs-picture"));
      return false;
    }
    // The scene of a live goes by the layout it was filled from unless it was given a name: it is
    // what the row of the live is called, not something to pick from a list.
    if (layoutMode) {
      if (!scene.name.trim()) {
        nameBox.focus();
        notify("error", word("needs-name"));
        return false;
      }
    } else {
      if (!scene.name.trim()) {
        scene.name = nameBox.value = nameBox.placeholder;
      }
    }
    setBusy(true);
    try {
      const response = await fetch("/scene/save", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(requestOf())
      });
      const payload = await response.json().catch(() => ({}));
      if (!response.ok) {
        notify("error", messageOf(payload) || response.statusText);
        return false;
      }
      scene.pkid = payload.pkid ?? scene.pkid;
      markClean();
      if (layoutMode) {
        layoutPkid = scene.pkid;
        await loadScenes();
        notify("success", messageOf(payload));
      }
      render();
      return true;
    } catch {
      notify("error", word("ko"));
      return false;
    } finally {
      setBusy(false);
    }
  };

  saveButton?.addEventListener("click", save);

  root.querySelector("[data-composer-add-slot]")?.addEventListener("click", () => addSlot());

  // Starting always streams what is on screen: the scene is saved first, as the scene of this live
  // only, so the live cannot go out with the version the user was looking at before the last drag.
  // The live goes out at the resolution of the biggest shape: a video still being probed is waited
  // for, so a click right after the drop does not start at the size of the empty layout.
  startButton?.addEventListener("click", async () => {
    setBusy(true);
    await Promise.all([...probes.values()]);
    setBusy(false);
    fitResolution();
    if ((dirty || scene.pkid === null) && !(await save())) return;
    document.querySelector("[data-composer-start-pkid]").value = String(scene.pkid);
    startForm?.requestSubmit();
  });

  deleteButton?.addEventListener("click", () => {
    if (scene.pkid === null) return;
    const dialog = document.getElementById("confirm-dialog");
    dialog.querySelector("[data-text]").textContent = deleteButton.dataset.confirmText;
    dialog.returnValue = "";
    dialog.onclose = async () => {
      if (dialog.returnValue !== "ok") return;
      const response = await fetch(`/scene/${scene.pkid}`, { method: "DELETE" }).catch(() => null);
      const payload = await response?.json().catch(() => ({}));
      if (!response?.ok) {
        notify("error", messageOf(payload) || word("ko"));
        return;
      }
      notify("success", messageOf(payload));
      await loadScenes();
      loadScene(null);
    };
    dialog.showModal();
  });

  window.addEventListener("beforeunload", event => {
    if (!dirty || scene.items.length === 0) return;
    event.preventDefault();
    event.returnValue = "";
  });

  // ---------- Start ----------

  // The scene asked for by the address: a layout on the layout page, and in the live wizard the
  // scene of a start that was refused, which is not among the layouts and is read on its own.
  const wantedScene = async wanted => {
    if (!wanted) return null;
    const listed = scenes.find(entry => entry.pkid === wanted);
    if (listed || layoutMode) return listed || null;
    try {
      const response = await fetch(`/scene/${wanted}`);
      return response.ok ? await response.json() : null;
    } catch {
      return null;
    }
  };

  const boot = async () => {
    render();
    await Promise.all([layoutMode ? null : loadCatalog(), loadScenes()]);
    const saved = await wantedScene(Number(root.dataset.scene || 0));
    if (saved) loadScene(saved);
  };

  // Listing the cameras and the microphones runs ffmpeg: a composer inside a closed dialog waits
  // for the dialog to open, so the page that holds it does not pay for a canvas nobody asked for.
  const host = root.closest("dialog");
  if (host && !host.open) {
    const opened = () => {
      if (!host.open) return;
      host.removeEventListener("toggle", opened);
      boot();
    };
    host.addEventListener("toggle", opened);
  } else {
    boot();
  }
})();
