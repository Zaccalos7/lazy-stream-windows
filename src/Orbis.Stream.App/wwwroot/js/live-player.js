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
  const words = preview.querySelector('[data-platform-words]').dataset;
  const pkid = Number(preview.dataset.pkid);
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
      const response = await fetch(`/preview/live/embed?live=${pkid}`, {
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
        if (watchLink && answer.embed.platform === 'youtube' && answer.embed.videoId) {
          watchLink.href = `https://www.youtube.com/watch?v=${encodeURIComponent(answer.embed.videoId)}`;
        }
      } else {
        player.removeAttribute('src');
        player.hidden = true;
        cover.hidden = false;
        status.textContent = answer.reason ? words.failed : words.offair;
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
