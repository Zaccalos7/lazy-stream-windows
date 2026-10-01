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
  try {
    const response = await fetch(region.element.dataset.refresh);
    if (response.ok) region.element.innerHTML = await response.text();
    else region.stale = true;
  } catch {
    // The server is restarting or the window is closing: the next change tries again.
    region.stale = true;
  } finally {
    region.coming = false;
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

// The light picture of the live: a frame that cannot be had (the live ended between the page and
// the request) leaves the cover under it, instead of a broken image.
const previewFrame = preview?.querySelector("[data-preview-frame]");
previewFrame?.addEventListener("error", event => { event.target.hidden = true; });

// A tab in the background is one the browser stops reading the stream for, and the server gives the
// answer up on rather than hold a page open that nobody is watching. Coming back to the page would
// then leave the last frame frozen for good, so the stream is asked for again: same picture, only
// the newest frame of it, and nothing about it has to be remembered.
const armPreviewFrame = () => {
  if (!previewFrame) return;
  previewFrame.hidden = false;
  previewFrame.src = `${previewFrame.src.split("?")[0]}?t=${Date.now()}`;
};
const previewVideo = preview?.querySelector("[data-preview-video]");
const previewForm = preview?.querySelector("[data-preview-form]");

// A player is behind the encoder by whatever the machine did in the last second, and it falls
// further behind every time a frame is late. A little drift is not worth a seek: a seek costs a
// keyframe, and a keyframe during a live is a freeze the viewer sees. So the player is corrected
// only once it is more than a second behind, and never pushed back: running ahead is not a drift
// worth undoing, the file does not have anything past the encoder to show.
const previewTolerance = 1.2;

// How long a control the user has just touched is left alone by the repaint, so the value that
// comes back from the server does not land in the middle of what is being typed.
const previewSettle = 2500;

let previewPkid = Number(preview?.dataset.pkid || 0);
let previewIsLive = preview?.dataset.live === "1";
let previewPosition = Number(preview?.dataset.position || 0);
let previewBusy = false;
let previewDropped = false;
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

const setPreviewText = (selector, value) => {
  for (const node of preview.querySelectorAll(selector)) node.textContent = value;
};

const setPreviewField = (name, value) => {
  for (const field of previewForm.querySelectorAll("[data-param]")) {
    if (field.dataset.param !== name) continue;
    if (field === document.activeElement || (previewDirty.get(name) || 0) > Date.now()) continue;
    field.value = value ?? "";
  }
};

// The player follows the encoder instead of the wall clock: every sample says where ffmpeg really
// is, and the video is asked to be there. A player that has nothing loaded yet, or that is seeking,
// is left alone: its currentTime is not the truth while it is looking for a frame.
const followEncoder = position => {
  previewPosition = position;
  if (!previewVideo) return;
  if (previewVideo.readyState === 0 || previewVideo.seeking) return;
  const target = position / 1000;
  if (target - previewVideo.currentTime > previewTolerance) previewVideo.currentTime = target;
};

if (previewVideo) {
  // Muted and inline, so the autoplay of a file the browser has no user gesture for is allowed.
  previewVideo.play().catch(() => {});
  // A player that was paused because the window went to the background, or because a seek left it
  // without a buffer, is put back on the edge instead of showing the last decoded frame.
  previewVideo.addEventListener("stalled", () => { previewDropped = true; });
  previewVideo.addEventListener("playing", () => { previewDropped = false; });
  previewVideo.addEventListener("error", () => {
    preview.querySelector("[data-preview-cover]")?.removeAttribute("hidden");
    previewVideo.hidden = true;
  });
  document.addEventListener("visibilitychange", () => {
    if (document.hidden || previewDropped) return;
    previewVideo.play().then(() => followEncoder(previewPosition)).catch(() => {});
  });
}

if (previewFrame) {
  document.addEventListener("visibilitychange", () => {
    if (document.hidden || !previewIsLive) return;
    armPreviewFrame();
  });
}

const paintPreview = state => {
  if (!preview) return;
  const position = state.positionMilliseconds;

  // A live that stopped, or a page that was left on one that stopped and now another is running:
  // what the page shows is not what the snapshot describes, so it is drawn again. The stream stays
  // open, and the reloaded page asks for the same live again. A page with nothing on air has no
  // form, and is only waiting for this.
  if (state.isLive !== previewIsLive || (state.isLive && state.videoPkid !== previewPkid)) {
    location.reload();
    return;
  }

  paintLivePicker(state.running || []);

  if (!previewForm) return;

  if (!state.isLive) return;

  followEncoder(position);
  setPreviewText("[data-preview-position]", previewClock(position) + " / " + previewClock(state.durationMilliseconds));
  for (const spinner of preview.querySelectorAll("[data-preview-restart]")) spinner.hidden = !state.reconfiguring;

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
  });
}

// "As the source" and the two halves of a resolution are one decision: the halves are hidden and
// skipped while it is on, which is also why they travel as nothing at all in the request.
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
      if (key === "videoWidth" || key === "videoHeight") {
        // Zero is how the request says "as the source": the two halves travel together, and both
        // travel only when the decision is to drop the resolution the setting carried.
        if (!keep) body[key] = value === "" ? 0 : Number(value);
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
      const value = form?.querySelector(`[name="${name}"]:checked, select[name="${name}"]`)?.value ?? "";
      for (const field of target.querySelectorAll(`[name="${name}"]`)) field.value = value;
    }
    next.closest("dialog")?.close();
    target.showModal();
  } else if (back) {
    const target = document.getElementById(back.dataset.wizardBack);
    back.closest("dialog")?.close();
    target?.showModal();
  }
});

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
// unless the user typed a custom ingest (anything that is not one of the platform presets).
document.addEventListener("change", event => {
  const url = event.target.dataset?.streamUrl;
  if (!url) return;
  const field = event.target.form.elements.streamUrl;
  const presets = [...event.target.form.querySelectorAll("[data-stream-url]")].map(radio => radio.dataset.streamUrl);
  if (!field.value.trim() || presets.includes(field.value.trim())) field.value = url;
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

window.chrome?.webview?.addEventListener("message", event => {
  if (!dropTarget || typeof event.data !== "string") return;
  dropTarget.value = event.data;
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

// Sidebar toggle
document.addEventListener("click", event => {
  const toggle = event.target.closest?.("#sidebar-toggle");
  if (!toggle) return;
  const closed = document.documentElement.classList.toggle("sidebar-closed");
  localStorage.setItem("orbis-sidebar", closed ? "closed" : "open");
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
