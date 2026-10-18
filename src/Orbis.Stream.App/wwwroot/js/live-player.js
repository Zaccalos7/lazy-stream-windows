// Load the viewer's player only while requested and visible. Local files and encoder frames
// are never a substitute for what the destination is actually broadcasting.
(() => {
  const preview = document.querySelector('[data-preview][data-live="1"]');
  if (!preview) return;
  let player = preview.querySelector('[data-platform-embed]');
  const toggle = preview.querySelector('[data-platform-toggle]');
  if (!player || !toggle) return;

  const cover = preview.querySelector('[data-preview-cover]');
  const status = preview.querySelector('[data-embed-text]');
  const watchLink = preview.querySelector('[data-platform-watch]');
  const urlInput = preview.querySelector('[data-platform-url]');
  const playButton = preview.querySelector('[data-platform-play]');
  const notice = preview.querySelector('[data-platform-notice]');
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
  let activeUrl = null;
  let youtubePlayer = null;
  let youtubeRound = 0;
  let startTimer;
  let apiPromise;

  const playerNotice = (message, canPlay = false) => {
    if (notice) {
      notice.textContent = message;
      notice.hidden = !message;
    }
    if (playButton) playButton.hidden = !canPlay;
  };

  // The API is loaded only after a preview was requested. A URL with autoplay=1 alone does
  // not tell us whether YouTube actually started or the browser refused playback.
  const youtubeApi = () => {
    if (window.YT?.Player) return Promise.resolve(window.YT);
    if (apiPromise) return apiPromise;
    apiPromise = new Promise((resolve, reject) => {
      const script = document.createElement('script');
      const previous = window.onYouTubeIframeAPIReady;
      let timeout;
      const ready = () => {
        clearTimeout(timeout);
        resolve(window.YT);
        previous?.();
      };
      const fail = () => {
        clearTimeout(timeout);
        if (window.onYouTubeIframeAPIReady === ready) window.onYouTubeIframeAPIReady = previous;
        script.remove();
        apiPromise = null;
        reject(new Error('YouTube API unavailable'));
      };
      window.onYouTubeIframeAPIReady = ready;
      script.src = 'https://www.youtube.com/iframe_api';
      script.addEventListener('error', fail, { once: true });
      timeout = setTimeout(fail, 15000);
      document.head.append(script);
    });
    return apiPromise;
  };

  const releaseYouTube = () => {
    youtubeRound++;
    clearTimeout(startTimer);
    playerNotice('');
    const current = youtubePlayer;
    youtubePlayer = null;
    if (!current) return;
    // destroy() removes the iframe. Keep a fresh iframe for the next preview, including after
    // returning from a hidden window; retaining old API objects leaks listeners and players.
    const replacement = player.cloneNode(false);
    replacement.removeAttribute('src');
    replacement.removeAttribute('id');
    replacement.hidden = true;
    player.before(replacement);
    const id = player.id;
    try { current.destroy(); } finally {
      player.remove();
      player = replacement;
      if (id) player.id = id;
    }
  };

  const showPlayer = embed => {
    if (activeUrl === embed.url) return;
    releaseYouTube();
    activeUrl = embed.url;
    // Make the player visible before navigating it, so playback is never requested on a
    // hidden iframe. The native YouTube Play control remains accessible if a click is needed.
    player.hidden = false;
    cover.hidden = true;
    if (embed.platform !== 'youtube') {
      player.setAttribute('src', embed.url);
      return;
    }

    const url = new URL(embed.url);
    url.searchParams.set('enablejsapi', '1');
    url.searchParams.set('origin', window.location.origin);
    url.searchParams.set('autoplay', '1');
    url.searchParams.set('mute', '1');
    player.setAttribute('src', url.href);
    const round = youtubeRound;
    const active = () => round === youtubeRound && requested && !document.hidden && !ended;
    const blocked = () => { if (active()) playerNotice(words.playRequired, !!youtubePlayer); };
    youtubeApi().then(api => {
      if (!active()) return;
      youtubePlayer = new api.Player(player, {
        events: {
          onReady: event => {
            if (!active()) return;
            startTimer = setTimeout(() => {
              if (active() && [-1, 2, 5].includes(event.target.getPlayerState())) blocked();
            }, 8000);
            event.target.mute();
            event.target.playVideo();
          },
          onStateChange: event => {
            if (active() && event.data === 1) {
              clearTimeout(startTimer);
              playerNotice('');
            }
          },
          onAutoplayBlocked: blocked,
          onError: event => {
            if (!active()) return;
            clearTimeout(startTimer);
            playerNotice(`${words.playerError} (${event.data})`);
          }
        }
      });
    }).catch(() => {
      if (active()) playerNotice(words.playRequired);
    });
  };

  const unload = () => {
    generation++;
    clearTimeout(timer);
    pending?.abort();
    pending = null;
    releaseYouTube();
    activeUrl = null;
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
        // The same broadcast is not reloaded or forced to play again by background checks.
        showPlayer(answer.embed);
        if (watchLink) {
          if (answer.embed.watchUrl) watchLink.href = answer.embed.watchUrl;
          else if (answer.embed.platform === 'youtube' && answer.embed.videoId) {
            watchLink.href = `https://www.youtube.com/watch?v=${encodeURIComponent(answer.embed.videoId)}`;
          }
          watchLink.hidden = false;
        }
      } else {
        releaseYouTube();
        activeUrl = null;
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
  playButton?.addEventListener('click', () => {
    if (!youtubePlayer || !requested || document.hidden || ended) return;
    // Run directly in the click handler, without a fetch or await losing the user gesture.
    youtubePlayer.mute();
    youtubePlayer.playVideo();
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
