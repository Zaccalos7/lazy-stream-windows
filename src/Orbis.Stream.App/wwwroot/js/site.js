// Orbis Stream: the only script of the Razor pages. Dialogs open with the native
// command/commandfor attributes; this file adds the confirmation dialog, the push channel of the
// server and the few things a page can fill in on its own.

// Theme Manager: Light or Dark theme toggle with persistence
const initTheme = () => {
  let storedTheme = localStorage.getItem('orbis-theme') || 'dark';
  // If a previous session stored 'system', convert it to 'dark'
  if (storedTheme === 'system') {
    storedTheme = 'dark';
    localStorage.setItem('orbis-theme', 'dark');
  }

  const applyTheme = (mode) => {
    document.documentElement.setAttribute('data-theme', mode);
    document.querySelectorAll('[data-theme-set]').forEach(btn => {
      btn.setAttribute('aria-pressed', btn.dataset.themeSet === mode ? 'true' : 'false');
    });
  };

  applyTheme(storedTheme);

  document.addEventListener('click', event => {
    const btn = event.target.closest?.('[data-theme-set]');
    if (!btn) return;
    const mode = btn.dataset.themeSet;
    localStorage.setItem('orbis-theme', mode);
    applyTheme(mode);
  });
};

initTheme();

// The push channel: the server says when something changed instead of waiting to be asked. One
// connection per page, opened only for what the page actually watches.
const watchRows = !!document.querySelector("[data-refresh]");
const watchStats = !!document.querySelector("[data-stats]");
const watchLive = !!document.querySelector("[data-preview]");
const watchList = [watchRows && "rows", watchStats && "stats", watchLive && "live"].filter(Boolean).join(",");
const stream = watchList
  ? new EventSource("/updates?watch=" + watchList + (watchLive ? liveQuery() : ""))
  : null;

// Which live the stream is about. The page carries it as a query parameter of the channel and not
// only of the endpoint: the server has to know it while it pushes, and a channel that asked for
// "whatever is live" would swap the file under a player that is already playing.
function liveQuery() {
  const pkid = document.querySelector("[data-preview]")?.dataset.pkid;
  return pkid ? "&live=" + encodeURIComponent(pkid) : "";
}

// <div data-refresh="url" data-since="7">: the server sends a message when a row moved and only then
// the partial is asked again. A message that arrives while the user is inside a dialog of the rows,
// or on another window, is kept instead of dropped: with a trigger there is no later tick to fix
// the page, so the region stays marked to be redone until the moment it can be. A page can have more
// than one region (the rows, and the videos of an open playlist dialog): each is redone on its own.
const regions = [...document.querySelectorAll("[data-refresh]")].map(element => ({ element, stale: false, coming: false }));

const redraw = async region => {
  if (region.coming || !region.stale) return;
  if (document.hidden || region.element.querySelector("dialog[open]")) return;
  region.coming = true;
  region.stale = false;
  let refreshed = false;
  try {
    const response = await fetch(region.element.dataset.refresh);
    if (response.ok) {
      region.element.innerHTML = await response.text();
      refreshed = true;
    }
    else region.stale = true;
  } catch {
    // The server is restarting or the window is closing: the next change tries again.
    region.stale = true;
  } finally {
    region.coming = false;
    // A start/stop received during this request must not leave the sidebar behind.
    if (refreshed && region.stale) redraw(region);
  }
};

const redrawAll = () => regions.forEach(redraw);

if (regions.length && stream) {
  stream.addEventListener("rows", () => {
    for (const region of regions) region.stale = true;
    redrawAll();
  });
  document.addEventListener("visibilitychange", () => {
    if (!document.hidden) redrawAll();
  });
  // Closing a dialog of the rows is the moment the user is free to see them change.
  document.addEventListener("close", redrawAll, true);
}

// A ring shows its value in the middle and fills itself with a CSS variable, the way the server
// drew it the first time. -1 is how a provider says the machine has no sensor for it, and 0 means
// the same for the temperature: then the card leaves the page, because a card that can only say
// it knows nothing is noise between the meters that do know something.
const paintRing = (ring, value) => {
  const available = value >= 0 && (ring.dataset.unit === "%" || value > 0);
  ring.dataset.label = available ? value + ring.dataset.unit : "–";
  ring.style.setProperty("--value", Math.max(0, Math.min(100, value)));
  ring.classList.toggle("warn", available && value >= 40 && value < 75);
  ring.classList.toggle("bad", available && value >= 75);
  ring.setAttribute("aria-label", ring.dataset.name + (available ? ", " + ring.dataset.label : ""));
  const note = ring.parentElement?.querySelector("[data-no-data]");
  if (note) note.hidden = available;
  const card = ring.closest(".meter");
  if (card) card.hidden = !available;
};

// <div data-stats>: the counters arrive in the channel already measured, under the name each card
// carries, so the page paints them where they are, with no request and nothing else re-rendered.
if (watchStats && stream) {
  stream.addEventListener("stats", event => {
    let values;
    try {
      values = JSON.parse(event.data);
    } catch {
      return;
    }

    for (const ring of document.querySelectorAll("[data-stats] .ring[data-key]")) {
      const value = values[ring.dataset.key];
      if (typeof value === "number") paintRing(ring, value);
    }

    // The first sample ends the loading the page was drawn with.
    for (const region of document.querySelectorAll("[data-stats][data-state]")) delete region.dataset.state;

    if (values.ffmpeg_processes) {
      const container = document.getElementById("ffmpeg-stats-container");
      if (container) {
        if (values.ffmpeg_processes.length === 0) {
          container.innerHTML = `<p class='caption'>${container.dataset.noFfmpegMessage || "Nessun processo FFmpeg attivo."}</p>`;
        } else {
          container.innerHTML = "<ul style='list-style: none; padding: 0; margin: 0;'>" + values.ffmpeg_processes.map(p =>
            "<li style='padding: 8px 0; border-bottom: 1px solid var(--gray-lighter); display: flex; justify-content: space-between;'>" +
            "<span><strong>PID:</strong> " + p.Pid + "</span> <span><strong>CPU:</strong> " + p.Cpu + "%</span> <span><strong>RAM:</strong> " + p.Ram + "%</span>" +
            "</li>").join("") + "</ul>";
        }
      }
    }
  });
}

// ---------- Live preview ----------
// The player, the position the encoder is at, and the parameters that can still be changed while
// the live runs. All of it arrives on the push channel once a second: the page asks the server
// nothing while it watches.
const preview = document.querySelector("[data-preview]");

const previewForm = preview?.querySelector("[data-preview-form]");

// How long a control the user has just touched is left alone by the repaint, so the value that
// comes back from the server does not land in the middle of what is being typed.
const previewSettle = 2500;

let previewPkid = Number(preview?.dataset.pkid || 0);
let previewIsLive = preview?.dataset.live === "1";
let previewBusy = false;
const previewDirty = new Map();

const previewWords = preview?.querySelector("[data-preview-words]") || {};
const previewMark = (key, fallback) => previewWords.dataset?.[key] || fallback;

// The lives on air, when there is more than one: a list to pick the watched one from, repainted by
// the push channel so a live that starts while the page is open is in it at once, and hidden again
// when the others are over, since there is nothing left to pick.
const livePicker = document.querySelector("[data-live-picker]");
const livePickerSelect = livePicker?.querySelector("select");

const paintLivePicker = running => {
  if (!livePicker || !livePickerSelect) return;
  if (running.length <= 1) {
    livePicker.hidden = true;
    return;
  }

  livePicker.hidden = false;
  livePickerSelect.replaceChildren(...running.map(option => {
    const entry = document.createElement("option");
    entry.value = String(option.videoPkid);
    entry.textContent = `${option.channelName} · ${option.videoName}`;
    entry.selected = option.videoPkid === previewPkid;
    return entry;
  }));
};

livePickerSelect?.addEventListener("change", event => {
  const pkid = Number(event.target.value);
  if (pkid > 0 && pkid !== previewPkid) location.href = `/orbis/mainPreview?live=${pkid}`;
});

const previewClock = milliseconds => {
  const total = Math.max(0, Math.floor(milliseconds / 1000));
  const pad = number => String(number).padStart(2, "0");
  const hours = Math.floor(total / 3600);
  return hours >= 1
    ? `${hours}:${pad(Math.floor((total % 3600) / 60))}:${pad(total % 60)}`
    : `${Math.floor(total / 60)}:${pad(total % 60)}`;
};

const previewFps = value => value > 0
  ? (Number.isInteger(value) ? String(value) : value.toFixed(3)) + " fps"
  : "–";

const previewSize = media => media && media.width > 0 ? `${media.width}×${media.height}` : "–";

// ---- Bitrate ---------------------------------------------------------------------
// A bitrate is kept in bits per second, and 5000000 is not a number anybody reads at a glance.
// The box speaks Mbit/s and kbit/s and spells out underneath the exact number the setting stores:
// what is typed is only ever another way of writing that value, never another value. [data-bitrate]
// marks the box, [data-bitrate-box] the line around it, and the hidden input inside that line is
// what the forms post, while the preview panel reads the same number from data-bitrate-bps.

const bitrateUnits = [
  { suffix: "M", factor: 1e6, label: "Mbit/s" },
  { suffix: "k", factor: 1e3, label: "kbit/s" },
  { suffix: "", factor: 1, label: "bit/s" }
];

const bitrateGrouped = value => String(value).replace(/\B(?=(\d{3})+(?!\d))/g, " ");

// "5", "5.5", "5M", "128k", "5 Mbit/s", "5 000 000", "5,000,000" and "6,5M" all end up as one
// count of bits per second. A dot is a decimal point; a space or a comma between groups of three
// is a thousands separator, which is how a value pasted from elsewhere arrives.
const parseBitrate = (raw, fallback) => {
  const typed = String(raw ?? "").trim().toLowerCase();
  if (!typed) return null;
  // A value already written out in full says so, and is not read through the unit in the box: a
  // "128 000" pasted into a box sitting on Mbit/s is 128 000 bit/s, not 128 000 of them.
  const grouped = /^\d{1,3}(?:[ ,]\d{3})+$/.test(typed);
  const text = grouped ? typed.replace(/[ ,]/g, "") : typed.replace(/[\s_]/g, "");
  const match = /^(\d+)(?:[.,](\d+))?(mbit\/s|kbit\/s|bit\/s|mbps|kbps|bps|m|k)?$/.exec(text);
  if (!match) return null;
  const named = match[3] && bitrateUnits.find(unit => unit.suffix && match[3].startsWith(unit.suffix.toLowerCase()));
  const spelled = match[3] === "bps" || match[3] === "bit/s";
  const unit = named || (spelled || grouped ? bitrateUnits[2] : fallback);
  const fraction = match[2] ? Number(match[2]) / 10 ** match[2].length : 0;
  return Math.round((Number(match[1]) + fraction) * unit.factor);
};

// The way the box writes a bitrate back: the largest unit it divides evenly, so 5000000 comes back
// as "5M" and 128000 as "128k" rather than as a row of digits. Anything finer than that keeps them.
const bitrateWritten = value => {
  for (const unit of bitrateUnits) {
    const scaled = value / unit.factor;
    const rounded = Math.round(scaled * 100) / 100;
    if (scaled >= 1 && Math.abs(scaled - rounded) < 1e-9) return { text: `${rounded}${unit.suffix}`, unit };
  }
  return { text: String(value), unit: bitrateUnits[2] };
};

// The unit a bare number in the box is read in: the one shown last, or the largest the field can
// reasonably hold (an audio bitrate is read in kbit/s, a video one in Mbit/s).
const bitrateUnitOf = field => {
  const shown = field.dataset.bitrateUnit;
  if (shown !== undefined) return bitrateUnits.find(unit => unit.suffix === shown) || bitrateUnits[1];
  const maximum = Number(field.dataset.bitrateMax || 0);
  return maximum >= 1e7 ? bitrateUnits[0] : maximum >= 1e3 ? bitrateUnits[1] : bitrateUnits[2];
};

const bitrateParts = field => {
  const box = field.closest("[data-bitrate-box]");
  return {
    box,
    unit: box?.querySelector("[data-bitrate-unit]"),
    hint: box?.parentElement?.querySelector("[data-bitrate-hint]"),
    sink: box?.querySelector('input[type="hidden"]')
  };
};

const paintBitrate = (field, bps, canonical = false) => {
  const { box, unit, hint, sink } = bitrateParts(field);
  if (!box) return;
  const maximum = Number(field.dataset.bitrateMax || 0);
  const word = field.dataset.bitrateInvalid ?? "";
  const written = bps > 0 ? bitrateWritten(bps) : null;

  // An empty box is a form that has not been filled in, not a wrong number: the browser speaks of
  // it through the required attribute and the line underneath stays quiet.
  if (!String(field.value).trim()) {
    box.dataset.bitrateState = "";
    field.setCustomValidity("");
    delete field.dataset.bitrateBps;
    unit.textContent = bitrateUnitOf(field).label;
    if (hint) hint.textContent = "";
    if (sink) sink.value = "";
    return;
  }

  if (!written || (maximum > 0 && bps > maximum)) {
    box.dataset.bitrateState = "bad";
    field.setCustomValidity(word);
    unit.textContent = written ? written.unit.label : bitrateUnitOf(field).label;
    if (hint) hint.textContent = written ? `${word} · 1–${bitrateGrouped(maximum)} bit/s` : word;
    delete field.dataset.bitrateBps;
    if (sink) sink.value = "";
    return;
  }

  box.dataset.bitrateState = "";
  field.setCustomValidity("");
  field.dataset.bitrateBps = String(bps);
  field.dataset.bitrateUnit = written.unit.suffix;
  unit.textContent = written.unit.label;
  if (hint) hint.textContent = `= ${bitrateGrouped(bps)} bit/s`;
  if (sink) sink.value = String(bps);
  if (canonical && field.value !== written.text) field.value = written.text;
};

const readBitrate = field => paintBitrate(field, parseBitrate(field.value, bitrateUnitOf(field)));

for (const field of document.querySelectorAll("[data-bitrate]")) {
  readBitrate(field);
  field.addEventListener("input", () => readBitrate(field));
  // Leaving the box is what settles the number: what was typed folds into the friendliest exact
  // form, so what is saved is visible in the same line as what it means.
  field.addEventListener("blur", () => paintBitrate(field, parseBitrate(field.value, bitrateUnitOf(field)), true));
}

const setPreviewText = (selector, value) => {
  for (const node of preview.querySelectorAll(selector)) node.textContent = value;
};

const setPreviewField = (name, value) => {
  for (const field of previewForm.querySelectorAll("[data-param]")) {
    if (field.dataset.param !== name) continue;
    if (field === document.activeElement || (previewDirty.get(name) || 0) > Date.now()) continue;
    // A bitrate is written in the unit it reads best in, so the number the snapshot carries is put
    // through the same folding the user's own typing goes through.
    if (field.dataset.bitrate !== undefined) {
      paintBitrate(field, Number(value) || null, true);
      continue;
    }
    field.value = value ?? "";
  }
};

// The colour of a level in the mix: green up to the sound of the source, yellow while it is pushed
// past it, red once the boost is loud enough to clip. The one place the thresholds are written.
const volumeLoud = 100;
const volumeHot = 150;

const volumeLevel = value =>
  value === 0 ? "mute" : value <= volumeLoud ? "ok" : value <= volumeHot ? "high" : "hot";

// A row is painted from its slider alone: the number, the fill of the track, the colour and the
// state of the mute button all follow the value, whoever moved it.
const paintVolume = slider => {
  const value = Number(slider.value);
  const max = Number(slider.max) || 200;
  const row = slider.closest("[data-volume-row]");
  const shown = preview?.querySelector(`[data-volume-value="${slider.dataset.volume}"]`);
  if (shown) shown.textContent = value + "%";
  if (!row) return;
  row.style.setProperty("--p", String(value / max));
  row.style.setProperty("--mark", String(volumeLoud / max));
  row.dataset.level = volumeLevel(value);

  const mute = row.querySelector("[data-volume-mute]");
  if (!mute) return;
  const words = row.closest("[data-mixer]")?.dataset || {};
  const label = value === 0 ? words.unmute : words.mute;
  if (label) {
    mute.title = label;
    mute.setAttribute("aria-label", label);
  }
  mute.setAttribute("aria-pressed", value === 0 ? "true" : "false");
  const icon = mute.querySelector(".icon");
  if (icon) icon.textContent = value === 0 ? "" : value <= volumeLoud / 2 ? "" : value <= volumeLoud ? "" : "";
};

// How many samples in a row disagreed with the page. A live hands over from one ffmpeg to the next
// (a spot, a scene of the deck, a change of parameters) in a moment with nothing running, and a
// sample that lands in it is not a live that ended: only two in a row are.
let previewMismatch = 0;

const paintPreview = state => {
  if (!preview) return;
  const position = state.positionMilliseconds;

  // A live that stopped, or a page that was left on one that stopped and now another is running:
  // what the page shows is not what the snapshot describes, so it is drawn again. The stream stays
  // open, and the reloaded page asks for the same live again. A page with nothing on air has no
  // form, and is only waiting for this.
  if (state.isLive !== previewIsLive || (state.isLive && state.videoPkid !== previewPkid)) {
    if (++previewMismatch >= 2 || !previewIsLive) location.reload();
    return;
  }

  if (!state.isLive && (state.status || "") !== (preview.dataset.status || "")) {
    location.reload();
    return;
  }

  previewMismatch = 0;

  paintLivePicker(state.running || []);

  if (!previewForm) return;

  if (!state.isLive) return;

  paintYouTubeVerification(state);
  setPreviewText("[data-preview-position]", previewClock(position) + " / " + previewClock(state.durationMilliseconds));
  for (const spinner of preview.querySelectorAll("[data-preview-restart]")) spinner.hidden = !state.reconfiguring;
  for (const bar of preview.querySelectorAll("[data-preview-offair]")) bar.hidden = !state.platformOffAir;
  for (const bar of preview.querySelectorAll("[data-preview-unverified]")) bar.hidden = !state.platformUnverified;

  setPreviewText('[data-preview-fact="output.size"]', previewSize(state.output));
  setPreviewText('[data-preview-fact="output.fps"]', previewFps(state.output.frameRate));
  setPreviewText('[data-preview-fact="output.codec"]', state.parameters.videoCodecName || "–");
  setPreviewText('[data-preview-fact="source.size"]', previewSize(state.source));
  setPreviewText('[data-preview-fact="source.fps"]', previewFps(state.source.frameRate));
  setPreviewText('[data-preview-fact="audio.channels"]',
    state.source.audioChannels > 0 ? String(state.source.audioChannels) : previewMark("noAudio", "–"));

  const parameters = state.parameters;
  setPreviewField("videoCodec", parameters.videoCodec);
  setPreviewField("videoCodecName", parameters.videoCodecName);
  setPreviewField("pixelFormat", parameters.pixelFormat);
  setPreviewField("videoBitrate", parameters.videoBitrate);
  setPreviewField("gopSize", parameters.gopSize);
  setPreviewField("videoWidth", parameters.videoWidth);
  setPreviewField("videoHeight", parameters.videoHeight);
  setPreviewField("frameRate", parameters.frameRate);
  setPreviewField("audioBitrate", parameters.audioBitrate);
  for (const box of preview.querySelectorAll('[data-param="keepSource"]')) {
    if (Date.now() < (previewDirty.get("keepSource") || 0)) continue;
    box.checked = parameters.videoWidth === null || parameters.videoWidth === undefined;
    setSourceFields(box.checked);
  }
  for (const source of state.sources || []) {
    if (Date.now() < (previewDirty.get("volume" + source.pkid) || 0)) continue;
    const slider = preview.querySelector(`[data-volume="${source.pkid}"]`);
    if (!slider) continue;
    slider.value = String(source.volume);
    paintVolume(slider);
  }
};

if (watchLive && stream) {
  stream.addEventListener("live", event => {
    let state;
    try {
      state = JSON.parse(event.data);
    } catch {
      return;
    }
    paintPreview(state);
    // The scene deck of the preview (scenes.js) draws what is on air from the same sample.
    document.dispatchEvent(new CustomEvent("orbis:live", { detail: state }));
  });
}

// "As the source" and the two halves of a resolution are one decision: the halves are hidden
// while it is on; both travel as zero to clear a previously saved resolution.
const sourceFields = () => preview.querySelectorAll("[data-source-off]");

const setSourceFields = keep => {
  for (const field of sourceFields()) field.hidden = keep;
};

if (previewForm) {
  const keepSource = previewForm.querySelector('[data-param="keepSource"]');
  setSourceFields(keepSource?.checked === true);
  keepSource?.addEventListener("change", () => setSourceFields(keepSource.checked));

  // The encoder that goes with a codec is the server's decision: clearing the control leaves the
  // request with the codec alone, and the sample that comes back fills the encoder in. Sending the
  // encoder that was on screen would undo the choice the user just made.
  previewForm.querySelector('[data-param="videoCodec"]')?.addEventListener("change", event => {
    const encoder = previewForm.querySelector('[data-param="videoCodecName"]');
    if (!encoder) return;
    encoder.value = "";
    previewDirty.delete("videoCodecName");
  });

  for (const field of previewForm.querySelectorAll("[data-param]")) {
    field.addEventListener("input", () => previewDirty.set(field.dataset.param, Date.now() + previewSettle));
  }

  // One control is one request. The whole form travels, not only what changed: the server compares
  // the values with the setting it has and answers a request that changes nothing without touching
  // ffmpeg, which is what keeps a mistyped-then-corrected pair of resolutions from restarting the
  // live twice.
  previewForm.addEventListener("change", async event => {
    const field = event.target.closest?.("[data-param]");
    if (!field || field === previewForm.querySelector('[data-param="gopSize"]')) return;

    const keep = previewForm.querySelector('[data-param="keepSource"]')?.checked === true;
    const body = {};
    for (const control of previewForm.querySelectorAll("[data-param]")) {
      const key = control.dataset.param;
      if (key === "keepSource" || key === "gopSize") continue;
      const value = control.value.trim();
      if (control.dataset.bitrate !== undefined) {
        // The box holds "5M", the request holds 5000000: what the setting stores never changes
        // because of how it is written, and a half typed box sends nothing rather than a NaN.
        const bps = Number(control.dataset.bitrateBps);
        if (bps > 0) body[key] = bps;
        continue;
      }
      if (key === "videoWidth" || key === "videoHeight") {
        // Zero is how the request says "as the source": the two halves travel together, and both
        // travel only when the decision is to drop the resolution the setting carried.
        body[key] = keep || value === "" ? 0 : Number(value);
        continue;
      }
      if (key === "frameRate") {
        // An empty frame rate means the source keeps its own, which is the same decision as above.
        body[key] = value === "" ? 0 : Number(value);
        continue;
      }
      if (value === "") continue;
      body[key] = control.type === "number" ? Number(value) : value;
    }

    if (!Object.keys(body).length) return;

    if (previewBusy) return;
    previewBusy = true;
    previewForm.dataset.busy = "1";
    try {
      const response = await fetch(`/preview/live/${previewPkid}/parameters`, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body)
      });
      const payload = await response.json().catch(() => ({}));
      if (response.ok) {
        say("success", previewMark("ok", ""), payload.message || previewMark("applied", ""), "");
      } else {
        // A refused change comes back as { field: message }, the way every other form of the
        // application answers, and the control that was refused is the one that goes back to the
        // value the setting holds.
        const first = Object.values(payload)[0] || response.statusText;
        say("error", previewMark("ko", ""), first, field.dataset.param);
        previewDirty.delete(field.dataset.param);
      }
    } catch {
      say("error", previewMark("ko", ""), previewMark("failed", ""), "");
    } finally {
      delete previewForm.dataset.busy;
      previewBusy = false;
    }
  });
}

// The level of each source of a canvas. The row follows the slider while it moves, and the live
// is asked once, when the slider is let go: every change starts the pass of the canvas again. The
// mute button and the value (which puts the source back to its own sound) are the same request.
const sendVolume = async slider => {
  const key = "volume" + slider.dataset.volume;
  previewDirty.set(key, Date.now() + previewSettle);
  try {
    const response = await fetch(`/preview/live/${previewPkid}/volume`, {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ sourcePkid: Number(slider.dataset.volume), volume: Number(slider.value) })
    });
    const payload = await response.json().catch(() => ({}));
    if (response.ok) {
      say("success", previewMark("ok", ""), payload.message || previewMark("applied", ""), "");
    } else {
      previewDirty.delete(key);
      say("error", previewMark("ko", ""), Object.values(payload)[0] || response.statusText, key);
    }
  } catch {
    say("error", previewMark("ko", ""), previewMark("failed", ""), "");
  }
};

const setVolume = (slider, value) => {
  if (Number(slider.value) === value) return;
  slider.value = String(value);
  paintVolume(slider);
  sendVolume(slider);
};

for (const slider of preview?.querySelectorAll("[data-volume]") || []) {
  const pkid = slider.dataset.volume;
  paintVolume(slider);
  slider.addEventListener("input", () => {
    paintVolume(slider);
    previewDirty.set("volume" + pkid, Date.now() + previewSettle);
  });
  slider.addEventListener("change", () => sendVolume(slider));

  // Muting remembers the level it took away, so the second press gives back that level and not a
  // generic one. A source that was already silent when the page opened comes back at 100%.
  preview.querySelector(`[data-volume-mute="${pkid}"]`)?.addEventListener("click", () => {
    const value = Number(slider.value);
    if (value > 0) {
      slider.dataset.before = String(value);
      setVolume(slider, 0);
    } else {
      setVolume(slider, Number(slider.dataset.before) || volumeLoud);
    }
  });
  preview.querySelector(`[data-volume-reset="${pkid}"]`)?.addEventListener("click", () => setVolume(slider, volumeLoud));
}

// A video setting without low latency is tuned by hand: its preset and tune become required, and
// the browser stops the save until both are chosen. The server asks the same (IsTuningComplete).
const paintTuning = box => {
  const form = box.closest("form");
  if (!form) return;
  for (const select of form.querySelectorAll("[data-tuning]")) {
    select.required = !box.checked;
    select.disabled = box.checked;
  }
  for (const hint of form.querySelectorAll("[data-tuning-hint]")) hint.hidden = box.checked;
};
for (const box of document.querySelectorAll("[data-low-latency]")) {
  paintTuning(box);
  box.addEventListener("change", () => paintTuning(box));
}

// Closing a YouTube warning closes it for the whole live, on the server: it would otherwise come
// back with the next sample of the push channel, a second later.
for (const button of preview?.querySelectorAll("[data-air-dismiss]") || []) {
  button.addEventListener("click", async () => {
    const bar = button.closest(".infobar");
    if (bar) bar.hidden = true;
    try {
      await fetch(`/preview/live/${previewPkid}/air/dismiss`, { method: "POST" });
    } catch {
      // The next sample says what the server holds, whichever way this went.
    }
  });
}

// The one infobar of a page that answers with a fetch: it goes above the header, where the server
// would have put it after a post.
const say = (kind, heading, text, backTo) => {
  // Inside a modal the page is under the backdrop: the answer goes where the user is looking.
  const page = document.querySelector("dialog[open] [data-say-here]") || document.querySelector(".page");
  if (!page) return;
  for (const bar of page.querySelectorAll(".infobar.preview-say")) bar.remove();
  const bar = document.createElement("div");
  bar.className = "infobar preview-say " + kind;
  bar.setAttribute("role", "status");
  const icon = document.createElement("i");
  icon.className = "icon";
  icon.textContent = kind === "error" ? "\uEA39" : "\uE73E";
  const body = document.createElement("p");
  const strong = document.createElement("strong");
  strong.textContent = heading;
  body.append(strong, " ", text);
  bar.append(icon, body);
  page.prepend(bar);
  if (backTo) {
    // The control that was refused goes back to what the setting holds, so the page stops showing
    // a value the encoder is not using.
    fetch(`/preview/live?live=${previewPkid}`).then(r => r.json()).then(paintPreview).catch(() => {});
  }
};

// <div data-load="url" data-delay="400">: a panel filled from the server on its own, because
// waiting for it would hold the page that contains it. The loading appears only after the delay: on
// a machine that answers straight away, a flash of loading is slower than the wait it hides.
for (const panel of document.querySelectorAll("[data-load]")) {
  const load = async () => {
    const late = setTimeout(() => { panel.dataset.state = "loading"; }, Number(panel.dataset.delay || 400));
    try {
      const response = await fetch(panel.dataset.load);
      if (!response.ok) throw new Error(String(response.status));
      const html = await response.text();
      clearTimeout(late);
      delete panel.dataset.state;
      for (const note of panel.querySelectorAll("[data-while]")) note.remove();
      panel.insertAdjacentHTML("beforeend", html);
    } catch {
      clearTimeout(late);
      panel.dataset.state = "failed";
    }
  };

  panel.querySelector("[data-reload]")?.addEventListener("click", load);
  load();
}

// A start, a stop or a restart runs on the server, ffmpeg included, so the answer takes a moment:
// the row that is working says so, next to the icons that asked for it, and its buttons stop
// accepting a second click. A form that is only waiting for the confirmation is not working yet.
document.addEventListener("submit", event => {
  const form = event.target;
  if (!(form instanceof HTMLFormElement) || (form.dataset.confirm && !form.dataset.confirmed)) return;
  if (form.dataset.askKey && !form.elements.namedItem("streamKey")?.value) return;
  const host = form.closest("[data-busy-host], [data-row]");
  if (!host) return;
  host.dataset.busy = "1";
  for (const button of host.querySelectorAll("button[type=submit], button:not([type])")) button.disabled = true;

  if (host.matches("[data-row]")) {
      const initText = document.documentElement.dataset.initializing || "Initializing...";

      const page = document.querySelector(".page");
      if (page) {
          const toast = document.createElement("div");
          toast.className = "infobar success toast-init";
          toast.setAttribute("role", "status");
          toast.innerHTML = `<i class="icon">\uE73E</i><p><strong>${initText}</strong></p>`;
          page.insertBefore(toast, page.firstChild);

          setTimeout(() => {
              toast.style.transition = "opacity 0.3s ease";
              toast.style.opacity = "0";
              setTimeout(() => toast.remove(), 300);
          }, 7000);
      }

      setTimeout(() => {
          for (const td of Array.from(host.children)) {
              if (td.tagName === "TD") {
                  td.style.display = "none";
              }
          }
          host.insertAdjacentHTML("beforeend", `
              <td class="skeleton-col"><div class="skeleton-badge"></div></td>
              <td class="skeleton-col path">
                  <div class="skeleton-text medium"></div>
                  <div class="skeleton-text long"></div>
              </td>
              <td class="skeleton-col"><div class="skeleton-text short"></div></td>
              <td class="skeleton-col"><div class="skeleton-text medium"></div></td>
              <td class="skeleton-col">
                  <div class="actions">
                      <div class="skeleton-icon"></div>
                      <div class="skeleton-icon"></div>
                      <div class="skeleton-icon"></div>
                  </div>
              </td>
          `);
          host.classList.add("skeleton-row");
      }, 10);
  }
});

// ---------- The shell: the hamburger of the side bar, and the foot of it ----------
//
// The hamburger closes the side bar and, clicked again, brings it back. The state is a class on
// <html>, the same one the layout reads before the first paint, and it is kept in localStorage so a
// page that opens again finds the side bar as the user left it. The button carries the state too
// (aria-expanded), so what the window shows and what the page says are never two different things.
//
// The listener sits on the button itself rather than on the document: the icon inside it is what
// the pointer is really over, and a click that has to travel to the root to be understood is a
// click a handler registered later on the same root can lose. The flag on the element makes a
// second load of this file harmless: two listeners on the same button would undo each other, and
// the side bar would never move.
const sidebarButton = document.getElementById("sidebar-toggle");

if (sidebarButton && !sidebarButton.dataset.sidebarBound) {
  sidebarButton.dataset.sidebarBound = "1";

  const paintSidebar = closed => {
    document.documentElement.classList.toggle("sidebar-closed", closed);
    sidebarButton.setAttribute("aria-expanded", closed ? "false" : "true");
    try {
      localStorage.setItem("orbis-sidebar", closed ? "closed" : "open");
    } catch {
      // A window with no storage of its own still toggles the side bar, it just forgets it.
    }
  };

  // The state the page was drawn with, said out loud to the button that changes it.
  paintSidebar(document.documentElement.classList.contains("sidebar-closed"));

  sidebarButton.addEventListener("click", event => {
    event.preventDefault();
    paintSidebar(!document.documentElement.classList.contains("sidebar-closed"));
  });
}

// The About dialog: the version and who wrote the application are in the foot of the side bar, and
// this is where they are said in full.
const aboutButton = document.getElementById("about-button");
const aboutDialog = document.getElementById("about-dialog");

if (aboutDialog && !aboutDialog.dataset.aboutBound) {
  aboutDialog.dataset.aboutBound = "1";

  const openAbout = event => {
    event?.preventDefault();
    if (!aboutDialog.open) aboutDialog.showModal();
  };

  // The foot of the side bar, which is where the two facts already are, is the way in.
  aboutButton?.addEventListener("click", openAbout);

  // Anywhere else on a page that says it with data-open-about opens the same dialog.
  document.addEventListener("click", event => {
    const shortcut = event.target.closest?.("[data-open-about]");
    if (!shortcut || shortcut === aboutButton) return;
    openAbout(event);
  });
}

// The side bar starts a live from anywhere: on this page the dialog is already there, elsewhere
// the link goes to the page with ?start=1, which draws it open.
document.addEventListener("click", event => {
  const shortcut = event.target.closest?.("[data-open-start]");
  const wizard = document.getElementById("start-dialog");
  if (!shortcut || !wizard || wizard.open) return;
  event.preventDefault();
  wizard.showModal();
});

// <button data-wizard-next="dialog-id" data-wizard-fields="a b">: a step of a wizard that lives in
// two dialogs. The picks of this step are checked here, copied into the fields of the same names in
// the next dialog, and the next dialog takes the place of this one; data-wizard-back goes back.
document.addEventListener("click", event => {
  const next = event.target.closest?.("[data-wizard-next]");
  const back = event.target.closest?.("[data-wizard-back]");
  if (next) {
    const form = next.closest("form");
    if (form && !form.reportValidity()) return;
    const target = document.getElementById(next.dataset.wizardNext);
    if (!target) return;
    for (const name of (next.dataset.wizardFields || "").split(" ").filter(Boolean)) {
      // A pick (a radio, a select) or a typed field, such as the key of a live to TikTok; a field
      // that is switched off carries nothing.
      const field = form?.querySelector(`[name="${name}"]:checked`)
        || form?.querySelector(`select[name="${name}"], textarea[name="${name}"], input[name="${name}"]:not([type=radio]):not([type=checkbox])`);
      const value = field && !field.disabled ? field.value : "";
      for (const slot of target.querySelectorAll(`[name="${name}"]`)) slot.value = value;
    }
    next.closest("dialog")?.close();
    target.showModal();
  } else if (back) {
    const target = document.getElementById(back.dataset.wizardBack);
    back.closest("dialog")?.close();
    target?.showModal();
  }
});

// A wizard of several steps inside one dialog, one step on screen at a time: the body of a step is
// [data-wizard-panel="n"], the dialog says which one is on screen in data-wizard-step, and the
// controls of a step carry its number in data-wizard-show. <button data-wizard-go="n"> brings that
// step in, after the picks of the one on screen have been checked (a step that is off screen has
// nothing to focus, so the whole form cannot be asked); closing the dialog starts it over.
const showWizardStep = (dialog, step) => {
  for (const panel of dialog.querySelectorAll("[data-wizard-panel]")) {
    panel.hidden = panel.dataset.wizardPanel !== step;
  }
  for (const part of dialog.querySelectorAll("[data-wizard-show]")) {
    part.hidden = part.dataset.wizardShow !== step;
  }
  dialog.dataset.wizardStep = step;
  const panel = dialog.querySelector(`[data-wizard-panel="${step}"]`);
  panel?.querySelector("input, a, button")?.focus({ preventScroll: true });
  dialog.querySelector(".dialog-body")?.scrollTo({ top: 0 });
};

document.addEventListener("click", event => {
  const go = event.target.closest?.("[data-wizard-go]");
  if (!go) return;
  const dialog = go.closest("dialog");
  if (!dialog) return;
  const onScreen = dialog.querySelector(`[data-wizard-panel="${dialog.dataset.wizardStep}"]`);
  if (go.dataset.wizardGo > dialog.dataset.wizardStep) {
    for (const field of onScreen?.querySelectorAll("input, select, textarea") ?? []) {
      if (field.checkValidity()) continue;
      field.reportValidity();
      return;
    }
  }
  showWizardStep(dialog, go.dataset.wizardGo);
});

for (const dialog of document.querySelectorAll("dialog[data-wizard-step]")) {
  dialog.addEventListener("close", () => showWizardStep(dialog, "1"));
}

// <form data-confirm="question">: asks in the shared ContentDialog before submitting.
document.addEventListener("submit", event => {
  const form = event.target;
  if (!form.dataset.confirm || form.dataset.confirmed) return;
  event.preventDefault();
  const dialog = document.getElementById("confirm-dialog");
  dialog.querySelector("[data-text]").textContent = form.dataset.confirm;
  dialog.returnValue = "";
  dialog.onclose = () => {
    if (dialog.returnValue !== "ok") return;
    form.dataset.confirmed = "1";
    form.requestSubmit(event.submitter);
  };
  dialog.showModal();
});

// <form data-ask-key="question">: a live to a platform that hands out a key for every live
// (TikTok) is started again with the key of that live. The dialog asks what the button asks and
// takes the key, which goes in the hidden streamKey field of the form: kept nowhere else.
document.addEventListener("submit", event => {
  const form = event.target;
  if (!(form instanceof HTMLFormElement) || !form.dataset.askKey) return;
  const field = form.elements.namedItem("streamKey");
  if (!field || field.value.trim()) return;
  event.preventDefault();
  const dialog = document.getElementById("live-key-dialog");
  if (!dialog) return;
  const input = dialog.querySelector("input[name=liveKey]");
  dialog.querySelector("[data-text]").textContent = form.dataset.askKey;
  input.value = "";
  dialog.returnValue = "";
  dialog.onclose = () => {
    dialog.onclose = null;
    const key = input.value.trim();
    input.value = "";
    if (dialog.returnValue !== "ok" || !key) return;
    field.value = key;
    form.requestSubmit(event.submitter);
  };
  dialog.showModal();
  input.focus();
});

// <div data-countdown="seconds" data-href="url">: counts down, then navigates.
for (const counter of document.querySelectorAll("[data-countdown]")) {
  let seconds = Number(counter.dataset.countdown);
  const tick = () => {
    counter.textContent = seconds;
    if (seconds-- <= 0) location.href = counter.dataset.href;
    else setTimeout(tick, 1000);
  };
  tick();
}

// <dialog data-open>: dialogs the server rendered for an edit (?edit=, ?link=) open as modal.
// data-open="0" is a dialog that could have been asked open and was not: Razor writes a data-*
// attribute even when its value is null, so "closed" has to be a value of its own.
for (const dialog of document.querySelectorAll('dialog[data-open]:not([data-open="0"])')) dialog.showModal();

// <input type="radio" data-stream-url="rtmp://…">: picking a platform fills its form's streamUrl,
// Facebook always starts with its Live Producer endpoint. Other platforms preserve custom ingests.
document.addEventListener("change", event => {
  const url = event.target.dataset?.streamUrl;
  if (!url) return;
  const field = event.target.form.elements.streamUrl;
  const presets = [...event.target.form.querySelectorAll("[data-stream-url]")].map(radio => radio.dataset.streamUrl);
  if (event.target.value === "facebook" || !field.value.trim() || presets.includes(field.value.trim())) field.value = url;
});

// <input data-drop-path>: dropping a file or folder on it fills its absolute path. The browser
// never exposes paths, so the dropped File goes to the WebView2 host, which answers with its path.
// The whole field is the target (so the hint under it counts too) and lights up while the drag is
// over it, because a drop has no other visible sign until the path appears.
// Drops anywhere else are swallowed: by default WebView2 would navigate away to the file.
let dropTarget = null;
let dropZone = null;
let dropHint = "";
let dropHintTimer = 0;

const dropFieldOf = event => {
  const hit = event.target;
  return hit.closest?.("[data-drop-path]") || hit.closest?.(".dropzone")?.querySelector("[data-drop-path]");
};

document.addEventListener("dragover", event => {
  event.preventDefault();
  const field = dropFieldOf(event);
  if (!field) return;
  const zone = field.closest(".dropzone");
  if (zone === dropZone) return;
  dropZone?.classList.remove("is-over");
  dropZone = zone;
  dropZone?.classList.add("is-over");
});

document.addEventListener("dragleave", event => {
  if (!dropZone || dropZone.contains(event.relatedTarget)) return;
  dropZone.classList.remove("is-over");
});

document.addEventListener("drop", event => {
  event.preventDefault();
  const field = dropFieldOf(event);
  const files = event.dataTransfer?.files;
  dropZone?.classList.remove("is-over");
  dropZone = null;
  if (!field || !files?.length || !window.chrome?.webview) return;
  dropTarget = field;
  dropZone = field.closest(".dropzone");
  window.chrome.webview.postMessageWithAdditionalObjects("dropPath", files);
});

// The picker of a video file is the browser's own, the one the overlay upload uses, not a dialog
// opened by the host: that one ran in the process of the application and on some machines froze
// blank ("Not responding") while listing the folder, where the browser's, run by the WebView2
// process, opens fine. Its File has no path either, so it takes the route of a drop to the host.
const videoPicker = Object.assign(document.createElement("input"), {
  type: "file",
  hidden: true,
  accept: ".mp4,.mkv,.avi,.mov,.wmv,.flv,.webm,.m4v,.mpeg,.mpg"
});
videoPicker.addEventListener("change", () => {
  if (videoPicker.files?.length) window.chrome.webview.postMessageWithAdditionalObjects("dropPath", videoPicker.files);
  // Emptied, so picking the same file again still reports a change.
  videoPicker.value = "";
});
// Nothing picked: no path will come back, and a field left waiting would take the next one,
// meant for someone else (a video dropped on the canvas of the composer).
videoPicker.addEventListener("cancel", () => {
  dropTarget = null;
  dropZone = null;
});
document.body.append(videoPicker);

/** Asks for a video file; its path fills the field as if the file had been dropped on it. */
window.orbisPickVideo = field => {
  if (!window.chrome?.webview) return;
  dropTarget = field;
  dropZone = field.closest(".dropzone");
  videoPicker.click();
};

document.addEventListener("click", event => {
  const browseFolderBtn = event.target.closest?.("[data-playlist-browse-folder]");
  if (browseFolderBtn && window.chrome?.webview) {
    const field = browseFolderBtn.parentElement?.querySelector("[data-drop-path]") || browseFolderBtn.closest(".field")?.querySelector("input");
    if (field) {
      dropTarget = field;
      dropZone = field.closest(".dropzone");
      window.chrome.webview.postMessage("browseFolder");
    }
  }

  const browseVideoBtn = event.target.closest?.("[data-browse-video]");
  if (browseVideoBtn && window.chrome?.webview) {
    const field = browseVideoBtn.parentElement?.querySelector("[data-drop-path]") || browseVideoBtn.closest(".field")?.querySelector("input");
    if (field) window.orbisPickVideo(field);
  }
});

window.chrome?.webview?.addEventListener("message", event => {
  if (!dropTarget || typeof event.data !== "string") return;
  let path = event.data;

  try {
    const payload = JSON.parse(event.data);
    if (payload.type === "browseFolder" && payload.path) {
      path = payload.path;
    } else {
      return; // Ignore other JSON messages we don't handle here
    }
  } catch {
    // Plain string from drop, handled correctly
  }

  dropTarget.value = path;
  dropTarget.dispatchEvent(new Event("input", { bubbles: true }));

  // The path is in the field, but nothing says it came from the drop: say it for a couple of seconds.
  const zone = dropZone;
  const hint = zone?.querySelector("small span");
  if (hint) {
    if (!dropHint) dropHint = hint.textContent;
    hint.textContent = dropTarget.dataset.dropDone || hint.textContent;
    zone.classList.add("is-dropped");
    clearTimeout(dropHintTimer);
    dropHintTimer = setTimeout(() => {
      hint.textContent = dropHint;
      zone.classList.remove("is-dropped");
    }, 3000);
  }

  dropTarget = null;
  dropZone = null;
});

// <button data-reveal> glued to a password input: shows or hides the value. It is always in the
// markup, so the key of a stored configuration can be checked in the edit form too, not only while
// it is being typed for the first time.
document.addEventListener("click", event => {
  const button = event.target.closest?.("[data-reveal]");
  if (!button) return;
  const field = button.parentElement?.querySelector("input");
  if (!field) return;

  const showing = field.type === "text";
  field.type = showing ? "password" : "text";
  button.setAttribute("aria-pressed", showing ? "false" : "true");
  const label = showing ? field.dataset.revealLabelShow : field.dataset.revealLabelHide;
  if (label) {
    button.title = label;
    button.setAttribute("aria-label", label);
  }
});

// Dismiss infobar/toast notifications after 7 seconds
document.addEventListener("DOMContentLoaded", () => {
  // Use a MutationObserver to catch infobars that are added dynamically
  const observer = new MutationObserver(mutations => {
    mutations.forEach(mutation => {
      mutation.addedNodes.forEach(node => {
        if (node.nodeType === 1 && (node.classList?.contains("infobar") || node.querySelector?.(".infobar"))) {
          const bars = node.classList?.contains("infobar") ? [node] : Array.from(node.querySelectorAll(".infobar"));
          bars.forEach(bar => {
            setTimeout(() => {
              bar.style.transition = "opacity 0.3s ease";
              bar.style.opacity = "0";
              setTimeout(() => { bar.style.display = "none"; }, 300);
            }, 7000);
          });
        }
      });
    });
  });

  observer.observe(document.body, { childList: true, subtree: true });

  // Also dismiss any infobars that are already in the DOM on load
  for (const bar of document.querySelectorAll(".infobar")) {
    setTimeout(() => {
      bar.style.transition = "opacity 0.3s ease";
      bar.style.opacity = "0";
      setTimeout(() => { bar.style.display = "none"; }, 300);
    }, 7000);
  }
});

// Auto-updater Logic
document.addEventListener("DOMContentLoaded", async () => {
    const currentVersion = document.documentElement.dataset.appVersion;
    if (!currentVersion) return;

    try {
        const proxyUrl = "https://r.jina.ai/http://sourceforge.net/projects/lazy-stream-windows/best_release.json";
        const response = await fetch(proxyUrl);
        const text = await response.text();
        const jsonMatch = text.match(/\{[\s\S]*\}/);
        if (!jsonMatch) return;
        const data = JSON.parse(jsonMatch[0]);
        
        const latestRelease = data?.release?.filename;
        if (!latestRelease) return;

        const match = latestRelease.match(/\/([0-9\.]+)\//);
        if (!match) return;
        const latestVersion = match[1];

        const v1 = currentVersion.split('.').map(Number);
        const v2 = latestVersion.split('.').map(Number);
        let isNewer = false;

        for (let i = 0; i < Math.max(v1.length, v2.length); i++) {
            const num1 = v1[i] || 0;
            const num2 = v2[i] || 0;
            if (num2 > num1) { isNewer = true; break; }
            else if (num2 < num1) break;
        }

        if (isNewer) {
            const updateUrl = data.release.url;
            const dialog = document.getElementById("update-dialog");
            const updateText = document.getElementById("update-dialog-text");
            const bell = document.getElementById("update-bell");
            if (!dialog || !updateText || !bell) return;

            bell.hidden = false;

            dialog.onclose = () => { if (dialog.returnValue === "ok") window.open(updateUrl, "_blank"); };

            bell.addEventListener("click", () => {
                updateText.textContent = document.documentElement.dataset.updateAvailableDialogText || "c'è una nuova versione vuoi scaricarla?";
                dialog.showModal();
            });
        }
    } catch (e) { /* silent fail */ }
});

// Locks an option to a given value while a given radio is chosen.
for (const force of document.querySelectorAll("input[type=checkbox][data-force-true-when]")) {
  const form = force.closest("form");
  if (!form) continue;

  const target = force.dataset.forceTrueWhen;
  const update = () => {
    const radio = form.querySelector(`input[type=radio][value="${target}"]`);
    if (radio && radio.checked) {
      force.checked = true;
      force.disabled = true;
    } else {
      force.disabled = false;
    }
  };

  form.addEventListener("change", event => {
    if (event.target.type === "radio") update();
  });
  update();
}

// The opposite of data-show-when: the element goes away for the values it names, and its fields
// with it - switched off, so a hidden field is neither asked for nor sent (the stream key of a
// configuration of TikTok, which keeps none).
for (const hide of document.querySelectorAll("[data-hide-when]")) {
  const form = hide.closest("form");
  if (!form) continue;

  const targets = hide.dataset.hideWhen.split(/\s+/).filter(Boolean);
  const update = () => {
    const picked = targets.some(target => form.querySelector(`input[type=radio][value="${target}"]`)?.checked);
    hide.hidden = picked;
    for (const field of hide.querySelectorAll("input, select, textarea")) field.disabled = picked;
  };

  form.addEventListener("change", event => {
    if (event.target.type === "radio") update();
  });
  update();
}

// [data-live-key]: the key of this live, asked for when the destination picked hands out a key for
// every live (data-key-each-live on its radio). Required then, switched off otherwise.
for (const block of document.querySelectorAll("[data-live-key]")) {
  const form = block.closest("form");
  const input = block.querySelector("input");
  if (!form || !input) continue;

  const update = () => {
    const needs = form.querySelector('input[name="configurationId"]:checked')?.dataset.keyEachLive === "1";
    block.hidden = !needs;
    input.disabled = !needs;
    input.required = needs;
  };

  form.addEventListener("change", event => {
    if (event.target.name === "configurationId") update();
  });
  update();
}

// Show element based on radio selection: data-show-when names one value, or several separated by
// spaces (the hint of the platforms whose ingest is the account's own shows for all of them).
for (const show of document.querySelectorAll("[data-show-when]")) {
  const form = show.closest("form");
  if (!form) continue;

  const targets = show.dataset.showWhen.split(/\s+/).filter(Boolean);
  const update = () => {
    const picked = targets.some(target => form.querySelector(`input[type=radio][value="${target}"]`)?.checked);
    show.style.display = picked ? "" : "none";
  };

  form.addEventListener("change", event => {
    if (event.target.type === "radio") update();
  });
  update();
}

// Error reports are generated locally and opened only when the user asks.
{
  const dialog = document.getElementById("error-report-dialog");
  let generation = 0;
  document.addEventListener("click", async event => {
    const button = event.target.closest("[data-error-report]");
    if (!button || !dialog) return;
    const request = ++generation;
    const status = dialog.querySelector("[data-report-status]");
    const qr = dialog.querySelector("[data-report-qr]");
    const download = dialog.querySelector("[data-report-download]");
    qr.hidden = true;
    qr.removeAttribute("src");
    download.hidden = true;
    download.removeAttribute("href");
    dialog.querySelector("[data-report-qr-help]").hidden = true;
    dialog.querySelector("[data-report-download-help]").hidden = true;
    dialog.querySelector("[data-report-text]").textContent = "";
    status.textContent = status.dataset.loading;
    if (!dialog.open) dialog.showModal();
    try {
      const response = await fetch(`/video/${encodeURIComponent(button.dataset.errorReport)}/error-report`, { cache: "no-store" });
      if (!response.ok) throw new Error(String(response.status));
      const report = await response.json();
      if (request !== generation || !dialog.open) return;
      const hasQr = report.images.length > 0;
      if (hasQr) qr.src = report.images[0];
      qr.hidden = !hasQr;
      dialog.querySelector("[data-report-qr-help]").hidden = !hasQr;
      if (report.downloadUrl) download.href = report.downloadUrl;
      download.hidden = !report.downloadUrl;
      dialog.querySelector("[data-report-download-help]").hidden = !report.downloadUrl;
      dialog.querySelector("[data-report-text]").textContent = report.text;
      status.textContent = "";
    } catch {
      if (request === generation) status.textContent = status.dataset.failed;
    }
  });
  dialog?.addEventListener("close", () => { generation++; });
}


function paintYouTubeVerification(state) {
  if (!preview || preview.dataset.platform !== "youtube") return;
  const panel = preview.querySelector("[data-youtube-verification]");
  if (!panel) return;
  const key = state.platformVerified ? "verified" : state.platformOffAir ? "offair" : state.platformUnverified ? "unverified" : "checking";
  panel.querySelector("[data-youtube-status]").textContent = state.startupMessage || panel.dataset[key];
  for (const link of document.querySelectorAll("[data-platform-manage]")) {
    if (state.platformManagementUrl) link.href = state.platformManagementUrl;
  }
  const indicator = document.querySelector("[data-youtube-indicator]");
  if (indicator) {
    indicator.classList.toggle("is-checking", !state.platformVerified);
    indicator.classList.toggle("is-live", !!state.platformVerified);
    indicator.querySelector("[data-live-indicator-text]").textContent = panel.dataset[key];
    indicator.setAttribute("aria-label", panel.dataset[key]);
  }
  if (!state.platformVerified && (state.platformOffAir || state.platformUnverified)) {
    panel.querySelector("details").open = true;
  }
}

for (const button of document.querySelectorAll("[data-youtube-recheck]")) {
  button.addEventListener("click", async () => {
    button.disabled = true;
    const panel = button.closest("[data-youtube-verification]");
    try {
      const response = await fetch(`/preview/live/${previewPkid}/air/check`, { method: "POST" });
      if (!response.ok) throw new Error("YouTube check failed");
      paintYouTubeVerification(await response.json());
    } catch {
      panel.querySelector("[data-youtube-status]").textContent = panel.dataset.failed;
      panel.querySelector("details").open = true;
    } finally { button.disabled = false; }
  });
}

for (const block of document.querySelectorAll("[data-youtube-preflight]")) {
  const form = block.closest("form");
  const update = () => {
    const selected = form.querySelector('input[name="configurationId"]:checked');
    const url = selected?.dataset.youtubeStudio;
    block.hidden = !url;
    if (url) {
      block.querySelector("[data-youtube-studio-link]").href = url;
      block.querySelector("details").open = true;
    }
  };
  form.addEventListener("change", update);
  update();
}
