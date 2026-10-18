// Load the viewer's player only while requested and visible. Local files and encoder frames
// are never a substitute for what the destination is actually broadcasting.
(() => {
  const preview = document.querySelector('[data-preview][data-live="1"]');
  if (!preview) return;
  const player = preview.querySelector('[data-platform-embed]');
  const toggle = preview.querySelector('[data-platform-toggle]');
  if (!player || !toggle) return;

  const cover = preview.querySelector('[data-preview-cover]');
  const status = preview.querySelector('[data-embed-text]');
  const watchLink = preview.querySelector('[data-platform-watch]');
  const urlInput = preview.querySelector('[data-platform-url]');
  const words = preview.querySelector('[data-platform-words]').dataset;
  const pkid = Number(preview.dataset.pkid);
  // A Facebook video URL belongs to this broadcast, not to the next live on the same channel.
  const storageKey = `orbis-live-watch-${preview.dataset.history || pkid}`;
  if (urlInput) {
    try { urlInput.value = sessionStorage.getItem(storageKey) || urlInput.value; } catch { }
  }
  let requested = false;
  let timer;
  let pending;
  let generation = 0;
  let ended = false;

  const unload = () => {
    generation++;
    clearTimeout(timer);
    pending?.abort();
    pending = null;
    // Hiding an iframe alone does not stop its media downloads.
    player.removeAttribute('src');
    player.hidden = true;
    cover.hidden = false;
  };

  const resolve = async () => {
    clearTimeout(timer);
    if (!requested || document.hidden || ended || pending) return;
    const round = generation;
    const controller = new AbortController();
    pending = controller;
    try {
      const address = `/preview/live/embed?live=${pkid}` +
        (urlInput ? `&watchUrl=${encodeURIComponent(urlInput.value.trim())}` : '');
      const response = await fetch(address, {
        cache: 'no-store', signal: controller.signal
      });
      if (!response.ok) throw new Error('Player lookup failed');
      const answer = await response.json();
      if (round !== generation) return;
      if (answer.embed?.url) {
        // Rechecking YouTube may discover a replacement broadcast. The same URL never reloads.
        if (player.getAttribute('src') !== answer.embed.url) player.setAttribute('src', answer.embed.url);
        player.hidden = false;
        cover.hidden = true;
        if (watchLink) {
          if (answer.embed.watchUrl) watchLink.href = answer.embed.watchUrl;
          else if (answer.embed.platform === 'youtube' && answer.embed.videoId) {
            watchLink.href = `https://www.youtube.com/watch?v=${encodeURIComponent(answer.embed.videoId)}`;
          }
          watchLink.hidden = false;
        }
      } else {
        player.removeAttribute('src');
        player.hidden = true;
        cover.hidden = false;
        status.textContent = answer.reason
          ? (urlInput ? words.facebook : words.failed) : words.offair;
        if (urlInput && watchLink) watchLink.hidden = true;
      }
    } catch {
      if (round === generation && player.hidden) status.textContent = words.failed;
    } finally {
      if (pending === controller) pending = null;
      if (round === generation && requested && !document.hidden && !ended) {
        timer = setTimeout(resolve, 30000);
      }
    }
  };

  toggle.addEventListener('click', () => {
    if (!requested && urlInput && !urlInput.reportValidity()) return;
    if (urlInput) {
      try { sessionStorage.setItem(storageKey, urlInput.value.trim()); } catch { }
    }
    requested = !requested;
    toggle.textContent = requested ? words.stop : words.start;
    toggle.setAttribute('aria-pressed', String(requested));
    if (requested) {
      status.textContent = words.loading;
      resolve();
    } else {
      unload();
      status.textContent = words.idle;
    }
  });
  urlInput?.addEventListener('input', () => {
    // Editing the destination must not leave the previous broadcast playing.
    requested = false;
    unload();
    toggle.textContent = words.start;
    toggle.setAttribute('aria-pressed', 'false');
    status.textContent = words.idle;
    if (watchLink) watchLink.hidden = true;
    try { sessionStorage.removeItem(storageKey); } catch { }
  });
  document.addEventListener('visibilitychange', () => {
    if (document.hidden) unload();
    else if (requested) {
      status.textContent = words.loading;
      resolve();
    }
  });
  window.addEventListener('pagehide', unload);
  window.addEventListener('pageshow', () => { if (requested) resolve(); });
  document.addEventListener('orbis:live', event => {
    const state = event.detail;
    if (!state.isLive || state.videoPkid !== pkid) {
      ended = true;
      unload();
    }
  });
})();
