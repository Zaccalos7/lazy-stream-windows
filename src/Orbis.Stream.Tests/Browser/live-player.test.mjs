// Run with: node --test src/Orbis.Stream.Tests/Browser/live-player.test.mjs
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const source = readFileSync(new URL('../../Orbis.Stream.App/wwwroot/js/live-player.js', import.meta.url), 'utf8');
const tick = () => new Promise(resolve => setImmediate(resolve));
const live = id => ({ embed: { platform: 'youtube', videoId: id, url: `https://www.youtube.com/embed/${id}` } });

function setup(answers = [], platform = 'youtube', facebookUrl = null, options = {}) {
  const element = (dataset = {}) => ({
    dataset, hidden: true, textContent: '', attributes: new Map(), listeners: new Map(), writes: 0,
    addEventListener(name, fn) { this.listeners.set(name, fn); },
    setAttribute(name, value) { this.attributes.set(name, value); this.writes++; },
    getAttribute(name) { return this.attributes.get(name) ?? null; },
    removeAttribute(name) { this.attributes.delete(name); if (name === 'id') this.id = ''; },
    cloneNode() { const copy = element({ ...this.dataset }); copy.attributes = new Map(this.attributes); return copy; },
    before(replacement) { nodes['[data-platform-embed]'] = replacement; },
    remove() { this.removed = true; },
    emit(name, detail) { return this.listeners.get(name)?.({ detail }); }
  });
  const player = element(), toggle = element(), cover = element(), status = element(), watch = element(), play = element(), notice = element();
  player.id = 'preview-platform-player';
  const nodes = {
    '[data-platform-embed]': player, '[data-platform-toggle]': toggle,
    '[data-preview-cover]': cover, '[data-embed-text]': status, '[data-platform-watch]': watch,
    '[data-platform-play]': play, '[data-platform-notice]': notice,
    '[data-platform-words]': element({ start: 'Start', stop: 'Stop', idle: 'Save data', loading: 'Loading', offair: 'Offline', failed: 'Unavailable', facebook: 'Paste public video URL', playRequired: 'Click Play', playerError: 'Open the live page' })
  };
  const input = facebookUrl === null ? null : element();
  if (input) {
    input.value = facebookUrl;
    input.reportValidity = () => input.value.length > 0;
    nodes['[data-platform-url]'] = input;
  }
  const preview = element({ pkid: '7', history: '42', platform });
  preview.querySelector = selector => nodes[selector];
  const document = element(), window = element();
  document.hidden = false;
  document.querySelector = () => preview;
  window.location = { origin: 'http://localhost:1200' };
  let timerId = 0;
  const timers = new Map(), requests = [];
  const controllers = [], scripts = [];
  const api = {
    Player: class {
      constructor(frame, { events }) {
        this.frame = frame;
        this.events = events;
        this.calls = [];
        this.state = -1;
        this.block = !!options.block;
        controllers.push(this);
        queueMicrotask(() => events.onReady({ target: this }));
      }
      mute() { this.calls.push('mute'); }
      playVideo() {
        this.calls.push('play');
        if (this.block) this.events.onAutoplayBlocked({ target: this });
        else { this.state = 1; this.events.onStateChange({ target: this, data: 1 }); }
      }
      getPlayerState() { return this.state; }
      destroy() { this.destroyed = true; this.frame.remove(); }
    }
  };
  if (!options.delayedApi) window.YT = api;
  document.createElement = () => element();
  document.head = { append(script) { scripts.push(script); } };
  const stored = new Map();
  const sessionStorage = {
    getItem: key => stored.get(key),
    setItem: (key, value) => stored.set(key, value),
    removeItem: key => stored.delete(key)
  };
  const fetch = async (url, options) => {
    requests.push({ url, options });
    const answer = answers.shift();
    if (typeof answer === 'function') return answer(options);
    if (answer instanceof Error) throw answer;
    return { ok: true, json: async () => answer };
  };
  vm.runInNewContext(source, {
    document, window, fetch, AbortController, encodeURIComponent, sessionStorage, URL,
    setTimeout(fn, delay) { const id = ++timerId; timers.set(id, { fn, delay }); return id; },
    clearTimeout(id) { timers.delete(id); }
  });
  return { get player() { return nodes['[data-platform-embed]']; }, toggle, cover, status, watch, input, stored, document, window, timers, requests, controllers, scripts, play, notice,
    readyApi: async () => { window.YT = api; window.onYouTubeIframeAPIReady(); await tick(); },
    click: async () => { toggle.emit('click'); await tick(); },
    poll: async () => { const [id, timer] = [...timers].find(([, timer]) => timer.delay === 30000); timers.delete(id); timer.fn(); await tick(); },
    visible: async value => { document.hidden = !value; document.emit('visibilitychange'); await tick(); }
  };
}

test('downloads no player or encoder frames until requested, then shows the public broadcast', async () => {
  const ui = setup([live('firstLive01'), live('firstLive01'), live('secondLive2')]);
  assert.equal(ui.requests.length, 0);
  assert.equal(ui.player.getAttribute('src'), null);
  await ui.click();
  assert.equal(new URL(ui.player.getAttribute('src')).pathname, '/embed/firstLive01');
  assert.equal(ui.watch.href, 'https://www.youtube.com/watch?v=firstLive01');
  assert.equal(ui.cover.hidden, true);
  assert.equal([...ui.timers.values()][0].delay, 30000);
  await ui.poll();
  assert.equal(ui.player.writes, 1, 'unchanged broadcast is not reloaded');
  await ui.poll();
  assert.equal(new URL(ui.player.getAttribute('src')).pathname, '/embed/secondLive2');
  assert(ui.requests.every(request => request.url === '/preview/live/embed?live=7'));
});

test('offline preview retries and later switches to the public live', async () => {
  const ui = setup([{ embed: null }, live('firstLive01')]);
  await ui.click();
  assert.equal(ui.status.textContent, 'Offline');
  assert.equal(ui.player.getAttribute('src'), null);
  await ui.poll();
  assert.equal(ui.player.hidden, false);
  assert.equal(ui.cover.hidden, true);
});

test('hidden windows unload media and stop polling; returning resolves a fresh broadcast', async () => {
  const ui = setup([live('firstLive01'), live('secondLive2')]);
  await ui.click();
  await ui.visible(false);
  assert.equal(ui.player.getAttribute('src'), null);
  assert.equal(ui.timers.size, 0);
  assert.equal(ui.requests.length, 1);
  await ui.visible(true);
  assert.equal(new URL(ui.player.getAttribute('src')).pathname, '/embed/secondLive2');
  await ui.click();
  assert.equal(ui.player.getAttribute('src'), null);
  assert.equal(ui.timers.size, 0);
  await ui.visible(false);
  await ui.visible(true);
  assert.equal(ui.requests.length, 2, 'stopped preview stays stopped');
});

test('late lookup responses cannot restart media after hiding or stopping', async () => {
  let finish;
  const ui = setup([() => new Promise(resolve => { finish = resolve; })]);
  await ui.click();
  await ui.visible(false);
  assert.equal(ui.requests[0].options.signal.aborted, true);
  finish({ ok: true, json: async () => live('firstLive01') });
  await tick();
  assert.equal(ui.player.getAttribute('src'), null);
  assert.equal(ui.timers.size, 0);
});

test('stopping a live or leaving the page releases the player', async () => {
  const ui = setup([live('firstLive01')]);
  await ui.click();
  ui.document.emit('orbis:live', { isLive: false, videoPkid: 7 });
  assert.equal(ui.player.getAttribute('src'), null);
  assert.equal(ui.timers.size, 0);
  await ui.visible(false);
  await ui.visible(true);
  assert.equal(ui.requests.length, 1);

  const next = setup([live('firstLive01')]);
  await next.click();
  next.window.emit('pagehide');
  assert.equal(next.player.getAttribute('src'), null);
  assert.equal(next.timers.size, 0);
});

test('a network failure retries without inventing a local preview', async () => {
  const ui = setup([new Error('network'), live('firstLive01')]);
  await ui.click();
  assert.equal(ui.status.textContent, 'Unavailable');
  assert.equal(ui.player.getAttribute('src'), null);
  await ui.poll();
  assert.equal(ui.player.hidden, false);
});

for (const [platform, url, watchUrl] of [
  ['kick', 'https://player.kick.com/channel?autoplay=true&muted=true', 'https://kick.com/channel'],
  ['facebook', 'https://www.facebook.com/plugins/video.php?href=public-video', 'https://www.facebook.com/watch/?v=123']
]) {
  test(`${platform} loads on demand and releases media while hidden`, async () => {
    const answer = { embed: { platform, url, watchUrl } };
    const ui = setup([answer, answer], platform, platform === 'facebook' ? watchUrl : null);
    assert.equal(ui.requests.length, 0);
    await ui.click();
    assert.equal(ui.player.getAttribute('src'), url);
    assert.equal(ui.watch.href, watchUrl);
    assert.equal(ui.watch.hidden, false);
    if (platform === 'facebook') {
      assert(ui.requests[0].url.endsWith(`&watchUrl=${encodeURIComponent(watchUrl)}`));
      assert.equal(ui.stored.get('orbis-live-watch-42'), watchUrl);
    }
    await ui.visible(false);
    assert.equal(ui.player.getAttribute('src'), null);
    assert.equal(ui.timers.size, 0);
    await ui.visible(true);
    assert.equal(ui.player.getAttribute('src'), url);
  });
}

test('Facebook requires a video link and editing it stops the previous broadcast', async () => {
  const ui = setup([{ embed: { platform: 'facebook', url: 'https://www.facebook.com/plugins/video.php?href=video' } }], 'facebook', '');
  await ui.click();
  assert.equal(ui.requests.length, 0);
  ui.input.value = 'https://www.facebook.com/page/videos/123/';
  await ui.click();
  assert.equal(ui.player.hidden, false);
  ui.input.value = 'https://www.facebook.com/page/videos/456/';
  ui.input.emit('input');
  assert.equal(ui.player.getAttribute('src'), null);
  assert.equal(ui.toggle.textContent, 'Start');
  assert.equal(ui.watch.hidden, true);
  assert.equal(ui.stored.size, 0);
  assert.equal(ui.timers.size, 0);
});

test('YouTube starts muted through the API and receives the exact page origin', async () => {
  const ui = setup([live('firstLive01'), live('firstLive01')]);
  assert.equal(ui.controllers.length, 0);
  await ui.click();
  const url = new URL(ui.player.getAttribute('src'));
  assert.equal(url.searchParams.get('origin'), 'http://localhost:1200');
  assert.equal(url.searchParams.get('enablejsapi'), '1');
  assert.equal(url.searchParams.get('mute'), '1');
  assert.equal(ui.player.hidden, false);
  assert.deepEqual(ui.controllers[0].calls, ['mute', 'play']);
  await ui.poll();
  assert.equal(ui.controllers.length, 1);
  assert.deepEqual(ui.controllers[0].calls, ['mute', 'play'], 'polling respects playback and pause choices');
});

test('blocked autoplay shows an actionable Play button without hiding or reloading YouTube', async () => {
  const ui = setup([live('firstLive01')], 'youtube', null, { block: true });
  await ui.click();
  assert.equal(ui.notice.textContent, 'Click Play');
  assert.equal(ui.play.hidden, false);
  assert.equal(ui.cover.hidden, true);
  const controller = ui.controllers[0];
  controller.block = false;
  ui.play.emit('click');
  assert.deepEqual(controller.calls, ['mute', 'play', 'mute', 'play'], 'play command runs synchronously in the click');
  assert.equal(ui.play.hidden, true);
  assert.equal(ui.notice.hidden, true);
  assert.equal(ui.requests.length, 1);
  assert.equal(ui.player.writes, 1);
});

test('hiding YouTube destroys its API instance and a fresh iframe resumes safely', async () => {
  const ui = setup([live('firstLive01'), live('secondLive2')]);
  await ui.click();
  const old = ui.controllers[0], frame = ui.player;
  await ui.visible(false);
  assert.equal(old.destroyed, true);
  assert.equal(frame.removed, true);
  assert.notEqual(ui.player, frame);
  assert.equal(ui.player.getAttribute('src'), null);
  await ui.visible(true);
  assert.equal(ui.controllers.length, 2);
  assert.deepEqual(ui.controllers[1].calls, ['mute', 'play']);
  old.events.onAutoplayBlocked({ target: old });
  old.events.onError({ target: old, data: 153 });
  assert.equal(ui.notice.hidden, true, 'events from the destroyed player are ignored');
});

test('a late API script cannot create a YouTube player after preview was stopped', async () => {
  const ui = setup([live('firstLive01')], 'youtube', null, { delayedApi: true });
  assert.equal(ui.scripts.length, 0);
  await ui.click();
  assert.equal(ui.scripts.length, 1);
  await ui.click();
  await ui.readyApi();
  assert.equal(ui.controllers.length, 0);
  assert.equal(ui.player.getAttribute('src'), null);
});

test('if the YouTube API cannot load, its native Play control stays visible', async () => {
  const ui = setup([live('firstLive01')], 'youtube', null, { delayedApi: true });
  await ui.click();
  ui.scripts[0].emit('error');
  await tick();
  assert.equal(ui.player.hidden, false);
  assert.equal(ui.cover.hidden, true);
  assert.equal(ui.notice.textContent, 'Click Play');
  assert.equal(ui.player.writes, 1);
});

test('YouTube embed failures are distinct from a request to press Play', async () => {
  const ui = setup([live('firstLive01')]);
  await ui.click();
  ui.controllers[0].events.onError({ data: 101 });
  assert.equal(ui.notice.textContent, 'Open the live page (101)');
  assert.equal(ui.play.hidden, true);
});
