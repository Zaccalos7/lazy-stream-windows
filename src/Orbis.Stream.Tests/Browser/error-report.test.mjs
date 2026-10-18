// Run with: node src/Orbis.Stream.Tests/Browser/error-report.test.mjs
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const site = readFileSync(new URL('../../Orbis.Stream.App/wwwroot/js/site.js', import.meta.url), 'utf8');
const source = site.slice(site.indexOf('// Error reports are generated locally'));
const tick = () => new Promise(resolve => setImmediate(resolve));

function setup(answers) {
  const element = () => ({
    hidden: true, textContent: '', dataset: {}, listeners: new Map(),
    addEventListener(name, fn) { this.listeners.set(name, fn); },
    removeAttribute(name) { delete this[name]; },
    emit(name, event = {}) { return this.listeners.get(name)?.(event); }
  });
  const nodes = Object.fromEntries([
    'status', 'qr', 'download', 'qr-help', 'download-help', 'text'
  ].map(name => [`[data-report-${name}]`, element()]));
  nodes['[data-report-status]'].dataset = { loading: 'Loading', failed: 'Failed' };
  const dialog = element();
  dialog.open = false;
  dialog.querySelector = selector => nodes[selector];
  dialog.showModal = () => { dialog.open = true; };
  const document = element();
  document.getElementById = () => dialog;
  const requests = [];
  const fetch = async url => {
    requests.push(url);
    const answer = answers.shift();
    if (typeof answer === 'function') return await answer();
    if (answer instanceof Error) throw answer;
    return { ok: true, json: async () => answer };
  };
  vm.runInNewContext(source, { document, fetch, encodeURIComponent });
  return {
    nodes, dialog, requests,
    open: async id => {
      const button = { dataset: { errorReport: id } };
      await document.emit('click', { target: { closest: () => button } });
    },
    close: () => { dialog.open = false; dialog.emit('close'); }
  };
}

test('large reports offer a downloadable text attachment instead of QR pages', async () => {
  const report = { text: 'Errore 日本語 🎥\n<script>plain text</script>', images: [], downloadUrl: '/video/7/error-report.txt' };
  const ui = setup([report]);
  await ui.open('7');
  assert.equal(ui.nodes['[data-report-download]'].hidden, false);
  assert.equal(ui.nodes['[data-report-download]'].href, report.downloadUrl);
  assert.equal(ui.nodes['[data-report-download-help]'].hidden, false);
  assert.equal(ui.nodes['[data-report-qr]'].hidden, true);
  assert.equal(ui.nodes['[data-report-qr-help]'].hidden, true);
  assert.equal(ui.nodes['[data-report-text]'].textContent, report.text);
});

test('opening a different short report clears the previous attachment before loading', async () => {
  let finish;
  const ui = setup([
    { text: 'long report', images: [], downloadUrl: '/video/7/error-report.txt' },
    () => new Promise(resolve => { finish = resolve; })
  ]);
  await ui.open('7');
  const opening = ui.open('8');
  assert.equal(ui.nodes['[data-report-download]'].hidden, true);
  assert.equal(ui.nodes['[data-report-download]'].href, undefined);
  assert.equal(ui.nodes['[data-report-text]'].textContent, '');
  finish({ ok: true, json: async () => ({ text: 'short report', images: ['data:image/png;base64,QR'], downloadUrl: null }) });
  await opening;
  assert.equal(ui.nodes['[data-report-qr]'].src, 'data:image/png;base64,QR');
  assert.equal(ui.nodes['[data-report-qr]'].hidden, false);
  assert.equal(ui.nodes['[data-report-qr-help]'].hidden, false);
  assert.equal(ui.nodes['[data-report-download-help]'].hidden, true);
});

test('a failed report request cannot offer an old file', async () => {
  const ui = setup([
    { text: 'long report', images: [], downloadUrl: '/video/7/error-report.txt' },
    new Error('network')
  ]);
  await ui.open('7');
  await ui.open('8');
  assert.equal(ui.nodes['[data-report-status]'].textContent, 'Failed');
  assert.equal(ui.nodes['[data-report-download]'].hidden, true);
  assert.equal(ui.nodes['[data-report-download]'].href, undefined);
});

test('a late response from a closed dialog cannot replace the current report', async () => {
  let finish;
  const ui = setup([
    () => new Promise(resolve => { finish = resolve; }),
    { text: 'current', images: ['data:image/png;base64,QR'], downloadUrl: null }
  ]);
  const old = ui.open('7');
  ui.close();
  await ui.open('8');
  finish({ ok: true, json: async () => ({ text: 'old', images: [], downloadUrl: '/video/7/error-report.txt' }) });
  await old;
  await tick();
  assert.equal(ui.nodes['[data-report-text]'].textContent, 'current');
  assert.equal(ui.nodes['[data-report-download]'].hidden, true);
  assert.equal(ui.nodes['[data-report-download]'].href, undefined);
});
