// Run with: node --test src/Orbis.Stream.Tests/Browser/live-player.test.mjs
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const source = readFileSync(new URL('../../Orbis.Stream.App/wwwroot/js/live-player.js', import.meta.url), 'utf8');
const tick = () => new Promise(resolve => setImmediate(resolve));
const live = id => ({ embed: { platform: 'youtube', videoId: id, url: `https://www.youtube.com/embed/${id}` } });

function setup(answers = []) {
  const element = (dataset = {}) => ({
    dataset, hidden: true, textContent: '', attributes: new Map(), listeners: new Map(), writes: 0,
    addEventListener(name, fn) { this.listeners.set(name, fn); },
    setAttribute(name, value) { this.attributes.set(name, value); this.writes++; },
    getAttribute(name) { return this.attributes.get(name) ?? null; },
    removeAttribute(name) { this.attributes.delete(name); },
    emit(name, detail) { return this.listeners.get(name)?.({ detail }); }
  });
  const player = element(), toggle = element(), cover = element(), status = element(), watch = element();
  const nodes = {
    '[data-platform-embed]': player, '[data-platform-toggle]': toggle,
    '[data-preview-cover]': cover, '[data-embed-text]': status, '[data-platform-watch]': watch,
    '[data-platform-words]': element({ start: 'Start', stop: 'Stop', idle: 'Save data', loading: 'Loading', offair: 'Offline', failed: 'Unavailable' })
  };
  const preview = element({ pkid: '7', platform: 'youtube' });
  preview.querySelector = selector => nodes[selector];
  const document = element(), window = element();
  document.hidden = false;
  document.querySelector = () => preview;
  let timerId = 0;
  const timers = new Map(), requests = [];
  const fetch = async (url, options) => {
    requests.push({ url, options });
    const answer = answers.shift();
    if (typeof answer === 'function') return answer(options);
    if (answer instanceof Error) throw answer;
    return { ok: true, json: async () => answer };
  };
  vm.runInNewContext(source, {
    document, window, fetch, AbortController, encodeURIComponent,
    setTimeout(fn, delay) { const id = ++timerId; timers.set(id, { fn, delay }); return id; },
    clearTimeout(id) { timers.delete(id); }
  });
  return { player, toggle, cover, status, watch, document, window, timers, requests,
    click: async () => { toggle.emit('click'); await tick(); },
    poll: async () => { const [id, timer] = timers.entries().next().value; timers.delete(id); timer.fn(); await tick(); },
    visible: async value => { document.hidden = !value; document.emit('visibilitychange'); await tick(); }
  };
}

test('downloads no player or encoder frames until requested, then shows the public broadcast', async () => {
  const ui = setup([live('firstLive01'), live('firstLive01'), live('secondLive2')]);
  assert.equal(ui.requests.length, 0);
  assert.equal(ui.player.getAttribute('src'), null);
  await ui.click();
  assert.equal(ui.player.getAttribute('src'), 'https://www.youtube.com/embed/firstLive01');
  assert.equal(ui.watch.href, 'https://www.youtube.com/watch?v=firstLive01');
  assert.equal(ui.cover.hidden, true);
  assert.equal([...ui.timers.values()][0].delay, 30000);
  await ui.poll();
  assert.equal(ui.player.writes, 1, 'unchanged broadcast is not reloaded');
  await ui.poll();
  assert.equal(ui.player.getAttribute('src'), 'https://www.youtube.com/embed/secondLive2');
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
  assert.equal(ui.player.getAttribute('src'), 'https://www.youtube.com/embed/secondLive2');
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
