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

// The light picture of the live, drawn frame by frame on a canvas.
//
// It used to be an <img src="...multipart/x-mixed-replace">, which is the obvious way to show motion
// JPEG and the wrong one here: a browser has no clock for a multipart image, so when the decode does
// not keep up the frames queue up instead of being dropped, the picture drifts behind the live and
// keeps drifting. That is the juddering, and the slow motion, this canvas is here to end. The page
// keeps no queue either: it holds the newest picture and nothing else, and draws it on a clock of
// its own that nothing but the picture can hurry or hold back.
const previewFrame = preview?.querySelector("[data-preview-frame]");
let previewFrameUrl = previewFrame ? `/preview/live/${Number(preview.dataset.pkid || 0)}/frame` : null;

// The platform player (Twitch/YouTube): what the viewers see, in place of the local picture.
//
// It is asked for once the page is open and not written into the page, because on YouTube the
// address of the player is not known before the platform has been asked which video is on air.
// Until it answers, the light picture of the live is on the stage: it is the truth of the encoder
// and it is there at once, so a platform that cannot be reached costs a missing picture and not an
// empty stage.
const previewPlatformPlayer = preview?.querySelector("[data-platform-embed]");

// How long a question waits for its answer. ffmpeg writes the next picture every thirty third of a
// second, and the server holds the request open until that picture exists instead of being asked
// again on a timer of the page's own. That is the whole trick against the judder: two clocks, one
// for the writing and one for the drawing, drift apart, and once they do, half the questions land
// inside a frame that has already been sent and come back with nothing while the frame after them
// waits. A picture is then held on the canvas for one beat and for three, and that uneven beat is
// what judder is. The server answers with a picture as soon as there is one that is new, so the
// page is on the clock of the live. The wait is capped there as well: an answer without a picture
// costs nothing here but another question, which goes out at once.
const previewWait = 250;

// The gap between two questions when the answer said there was nothing new yet, and the gap when
// there is no live to write pictures at all. Without the first one, an answer that says "nothing
// new" straight away would turn the loop into a spin; the second keeps a page left open on a live
// that is over from asking thirty times a second for a picture nobody writes.
const previewFloor = 4;
const previewIdle = 1000;

// The stamp of the picture the page holds. The server compares it with the one it has: the same
// picture comes back as 304 without the JPEG, which is what a live that is still writing but has
// nothing new for this page costs.
let previewStamp = null;

// An in-flight question, so the loop never has two open at once.
let previewPending = false;

// Set while the tab is hidden. A background tab is one the browser stops servicing, and a loop that
// kept asking through it would only pile requests up behind a window nobody is looking at.
let previewPaused = false;

// The picture that has been decoded and is waiting for its turn on the canvas, and the one that is
// on it. Only one picture is ever kept: the one before it is already on the screen and the one after
// it is the newest, so a frame that is overtaken while it is being decoded is closed rather than
// queued behind the others - a queue of pictures is a preview in slow motion.
let previewPicture = null;
let previewPainted = null;

// Puts the newest decoded picture on the canvas, on the refresh of the screen rather than in the
// middle of one.
//
// No timer decides this and nothing is held back: the picture goes up as soon as the screen is
// ready for it, which is the least the page can do about when a frame is due. The evenness of the
// motion is not bought here, it comes from the other side - every question is answered with a
// picture ffmpeg has just written, so there is never a frame to wait for and never the same picture
// twice, and the only thing left to time is the screen's own refresh.
//
// The canvas is the size it is looked at, not the size the picture arrives in: ffmpeg shrinks the
// preview to 640 pixels wide because that is what a frame costs almost nothing at, and the browser
// is the one that stretches it over the stage. Painting it here at the size of the stage, with the
// smoothing the browser does at its best, costs the same and keeps the picture as sharp as a scaled
// frame can be.
const paintPreviewPicture = () => {
  if (!previewFrame) return;

  requestAnimationFrame(paintPreviewPicture);

  // Nothing decoded yet, or the picture on the canvas is the newest one: the screen is left alone.
  const picture = previewPicture;
  if (!picture || picture === previewPainted) return;

  previewPainted = picture;

  // The canvas is measured rather than set from the picture: setting its size clears it, and a
  // canvas cleared thirty times a second is a canvas that flickers.
  if (previewFrame.hidden) previewFrame.hidden = false;

  const scale = window.devicePixelRatio || 1;
  const width = Math.round(previewFrame.clientWidth * scale);
  const height = Math.round(previewFrame.clientHeight * scale);
  if (width > 0 && height > 0 && (previewFrame.width !== width || previewFrame.height !== height)) {
    previewFrame.width = width;
    previewFrame.height = height;
  }

  const context = previewFrame.getContext("2d");
  if (!context) return;

  context.imageSmoothingEnabled = true;
  context.imageSmoothingQuality = "high";
  context.drawImage(picture, 0, 0, previewFrame.width, previewFrame.height);
};

// Asks for the picture the page does not have and waits for it. What it answers with is what the
// loop does next: a picture means another question at once, nothing new means another question
// shortly, and no live at all means the page can rest until something changes.
const fetchPreviewFrame = async () => {
  if (previewPending || previewPaused || !previewFrameUrl) return "idle";
  previewPending = true;
  try {
    const headers = previewStamp ? { "If-None-Match": previewStamp } : {};
    const response = await fetch(`${previewFrameUrl}?wait=${previewWait}`, { headers, cache: "no-store" });

    // 304 is the answer to a page that is ahead of the live: nothing new has been written while it
    // was asking, and the question goes out again straight away.
    if (response.status === 304) return "waiting";

    // The live is over, or it has not written a frame yet: the page covers the picture and the push
    // channel is what brings it back when there is something to see.
    if (!response.ok) return "idle";

    previewStamp = response.headers.get("X-Orbis-Frame") || previewStamp;

    // The bitmaps are decoded off the main thread, so a frame is turned into pixels while the page
    // keeps painting and the numbers keep arriving.
    try {
      const picture = await createImageBitmap(await response.blob());
      const overtaken = previewPicture;
      previewPicture = picture;
      overtaken?.close();
    } catch {
      // A frame that cannot be decoded is the one the page goes on without; the next question asks
      // for a newer one anyway.
    }

    return "picture";
  } catch {
    // The server is not answering (the live ended, the page is closing): the picture keeps the last
    // frame it drew and the next tick tries again.
    return "idle";
  } finally {
    previewPending = false;
  }
};

// The loop is a chain of waits rather than a timer, and what it waits for is the answer: the next
// question goes out as soon as the last one has been dealt with, and the server decides when there
// is a picture to draw. Nothing here decides when the live moves. It ends when the platform player
// takes the stage: there is no longer a picture on the canvas to keep fed.
const previewLoop = async () => {
  while (previewFrameUrl) {
    const outcome = await fetchPreviewFrame();
    await new Promise(resolve => setTimeout(resolve,
      outcome === "idle" ? previewIdle : outcome === "waiting" ? previewFloor : 0));
  }
};

// Coming back to the tab: the frame the page was holding is a moment old and there is a good chance
// the live has ended while nobody was looking, so the stamp is forgotten and the picture asked for
// again from scratch. The cover under the canvas hides anything stale in the meantime.
const armPreviewFrame = () => {
  if (!previewFrame) return;
  previewStamp = null;
  previewPaused = false;
  previewPicture?.close();
  previewPicture = null;
  previewPainted = null;
  previewFrame.hidden = true;
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

// Puts the platform player on the stage in place of the local picture, once the server has said
// which one the live can be watched on.
const showPlatformPlayer = url => {
  if (!previewPlatformPlayer) return;
  if (previewPlatformPlayer.getAttribute("src") !== url) previewPlatformPlayer.setAttribute("src", url);
  previewPlatformPlayer.hidden = false;
  if (previewFrame) previewFrame.hidden = true;
  const cover = preview?.querySelector(".preview-cover");
  if (cover) cover.hidden = true;
  // The canvas is not drawn on any more: the questions for its frames stop with the loop that asks
  // for them, so an encoder nobody is watching does not keep writing a preview for a hidden canvas.
  // Don't stop fetching local preview frames - keep continuous stream
  // previewFrameUrl = null;
};

// Asks where this live can be watched. A page that is told there is nowhere to watch it keeps the
// local picture, which is the right answer for a platform this application has no player for and
// for a channel that is not on air yet.
//
// The question is asked again for as long as there is no answer: ffmpeg starts pushing before the
// platform calls the channel live, and a live started on another machine while this page is open is
// not on air at all when the page is drawn. A live that is on air keeps its player until it ends,
// so the question stops there and not one moment before.
const previewRetry = 15000;

const embedStatus = preview?.querySelector(".preview-embed-status");
const embedText = embedStatus?.querySelector("[data-embed-text]");

const resolvePlatformPlayer = async () => {
  if (!previewPlatformPlayer || previewPkid <= 0) return;
  if (embedStatus) embedStatus.hidden = false;
  try {
    const response = await fetch(`/preview/live/embed?live=${previewPkid}`, { cache: "no-store" });
    if (response.ok) {
      const embed = await response.json();
      if (embed?.url) {
        showPlatformPlayer(embed.url);
        if (embedStatus) embedStatus.hidden = true;
        // Keep polling to stay updated
        if (previewIsLive) setTimeout(resolvePlatformPlayer, previewRetry);
        return;
      }
    }
  } catch {
    // The server is not answering: the local picture stays, which is what it is for.
  }
  if (embedStatus && embedText) {
    const platform = previewMark("embedFailed", "Unable to load {0} player: channel is not live");
    embedText.textContent = platform.replace("{0}", "Twitch/YouTube");
  }
  if (previewIsLive) setTimeout(resolvePlatformPlayer, previewRetry);
};

if (previewIsLive) {
  if (previewFrame) {
    // The light picture of the live, on the stage from the first frame and until the platform
    // player takes it over.
    previewLoop();
    requestAnimationFrame(paintPreviewPicture);
    document.addEventListener("visibilitychange", () => {
      // A tab nobody is looking at is not asked for frames: the browser stops servicing its work
      // anyway, and what would pile up behind it is not a preview but a queue of stale pictures.
      previewPaused = document.hidden;
      if (document.hidden) return;
      armPreviewFrame();
    });
  }
  resolvePlatformPlayer();
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

// The About dialog: the version and who wrote the application are in the foot of the side bar, and
// this is where they are said in full.
document.addEventListener("click", event => {
  const shortcut = event.target.closest?.("[data-open-about], #about-button");
  const about = document.getElementById("about-dialog");
  if (!shortcut || !about || about.open) return;
  event.preventDefault();
  about.showModal();
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

document.addEventListener("click", event => {
  const browseFolderBtn = event.target.closest?.("[data-playlist-browse-folder]");
  if (browseFolderBtn && window.chrome?.webview) {
    const field = browseFolderBtn.parentElement?.querySelector("[data-drop-path]");
    if (field && !field.value.trim()) {
      dropTarget = field;
      dropZone = field.closest(".dropzone");
      window.chrome.webview.postMessage("browseFolder");
    }
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

// Auto-updater Logic
document.addEventListener("DOMContentLoaded", async () => {
    const currentVersion = document.documentElement.dataset.appVersion;
    if (!currentVersion) return;

    try {
        const response = await fetch("https://sourceforge.net/projects/lazy-stream-windows/best_release.json");
        const data = await response.json();
        const latestRelease = data?.release?.filename;
        if (!latestRelease) return;

        // Extracts version from "/2.0.5/OrbisStream-2.0.5-win-x64-setup.exe" -> "2.0.5"
        const match = latestRelease.match(/\/([0-9\.]+)\//);
        if (!match) return;
        const latestVersion = match[1];

        // Basic version comparison (assumes semver-like format)
        const v1 = currentVersion.split('.').map(Number);
        const v2 = latestVersion.split('.').map(Number);
        let isNewer = false;

        for (let i = 0; i < Math.max(v1.length, v2.length); i++) {
            const num1 = v1[i] || 0;
            const num2 = v2[i] || 0;
            if (num2 > num1) {
                isNewer = true;
                break;
            } else if (num2 < num1) {
                break;
            }
        }

        if (isNewer) {
            const updateUrl = data.release.url;
            const dialog = document.getElementById("update-dialog");
            const updateText = document.getElementById("update-dialog-text");
            const bell = document.getElementById("update-bell");

            if (!dialog || !updateText || !bell) return;

            bell.hidden = false;

            dialog.onclose = () => {
                if (dialog.returnValue === "ok") {
                    window.open(updateUrl, "_blank");
                }
            };

            bell.addEventListener("click", () => {
                updateText.textContent = document.documentElement.dataset.updateAvailableDialogText || "c’è una nuova versione vuoi scaricarla?";
                dialog.showModal();
            });
        }
    } catch (e) {
        console.error("Failed to check for updates", e);
    }
});
