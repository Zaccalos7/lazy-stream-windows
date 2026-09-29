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
const stream = watchRows || watchStats
  ? new EventSource("/updates?watch=" + [watchRows && "rows", watchStats && "stats"].filter(Boolean).join(","))
  : null;

// <div data-refresh="url" data-since="7">: the server sends a message when a row moved and only then
// the partial is asked again. A message that arrives while the user is inside a dialog of the rows,
// or on another window, is kept instead of dropped: with a trigger there is no later tick to fix
// the page, so the region stays marked to be redone until the moment it can be.
const rows = document.querySelector("[data-refresh]");
let rowsAreStale = false;
let rowsAreComing = false;

const redrawRows = async () => {
  if (!rows || rowsAreComing || !rowsAreStale) return;
  if (document.hidden || rows.querySelector("dialog[open]")) return;
  rowsAreComing = true;
  rowsAreStale = false;
  try {
    const response = await fetch(rows.dataset.refresh);
    if (response.ok) rows.innerHTML = await response.text();
    else rowsAreStale = true;
  } catch {
    // The server is restarting or the window is closing: the next change tries again.
    rowsAreStale = true;
  } finally {
    rowsAreComing = false;
  }
};

if (rows && stream) {
  stream.addEventListener("rows", () => {
    rowsAreStale = true;
    redrawRows();
  });
  document.addEventListener("visibilitychange", () => {
    if (!document.hidden) redrawRows();
  });
  // Closing a dialog of the rows is the moment the user is free to see them change.
  document.addEventListener("close", redrawRows, true);
}

// A ring shows its value in the middle and fills itself with a CSS variable, the way the server
// drew it the first time. -1 is how a provider says it has no value, and 0 means the same for the
// temperature: then the ring keeps the dash, the fill empties and the note under it stays.
const paintRing = (ring, value) => {
  const available = value >= 0 && (ring.dataset.unit === "%" || value > 0);
  ring.dataset.label = available ? value + ring.dataset.unit : "–";
  ring.style.setProperty("--value", Math.max(0, Math.min(100, value)));
  ring.classList.toggle("warn", available && value >= 40 && value < 75);
  ring.classList.toggle("bad", available && value >= 75);
  ring.setAttribute("aria-label", ring.dataset.name + (available ? ", " + ring.dataset.label : ""));
  const note = ring.parentElement?.querySelector("[data-no-data]");
  if (note) note.hidden = available;
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
  });
}

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
for (const dialog of document.querySelectorAll("dialog[data-open]")) dialog.showModal();

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
