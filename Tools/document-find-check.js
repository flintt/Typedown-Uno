#!/usr/bin/env node
// Browser check for the Uno find bar and its document-results messages. No npm dependencies.
const fs = require('fs');
const http = require('http');
const os = require('os');
const path = require('path');
const { spawn } = require('child_process');

const root = path.resolve(__dirname, '../Typedown.Uno/Assets/Editor');
const temp = fs.mkdtempSync(path.join(os.tmpdir(), 'typedown-find-'));
const server = http.createServer((req, res) => {
  const file = path.resolve(root, '.' + decodeURIComponent(req.url.split('?')[0]));
  if (!file.startsWith(root + path.sep) || !fs.existsSync(file) || !fs.statSync(file).isFile()) {
    res.statusCode = 404;
    res.end();
    return;
  }
  fs.createReadStream(file).pipe(res);
});

const preload = `(() => {
  const listeners = [], previous = {};
  window.__last = {}; window.__messages = [];
  const dispatchEvent = event => listeners.forEach(fn => fn(event));
  const replies = {
    GetSettings: { markdown: '', sourceCode: false, readOnly: false, fontSize: 16, lineHeight: 1.6,
      editorAreaWidth: '900px', tabSize: 4, textDirection: 'auto', preferLooseListItem: true,
      listIndentation: '1', tableAlignColumns: false, basePath: '/tmp', loadId: 1 },
    GetCurrentTheme: { theme: 'Light', accentColor: { r: 0, g: 120, b: 212, a: 1 },
      background: { R: 249, G: 249, B: 249, A: 1 } },
    ContentLoaded: '', GetStringResources: {}
  };
  window.chrome = { webview: {
    addEventListener: (_type, fn) => listeners.push(fn), dispatchEvent,
    postMessage: raw => {
      const m = JSON.parse(raw);
      if (m.type === 'invoke') {
        setTimeout(() => dispatchEvent({ data: JSON.stringify({ name: m.id, args: { code: 0, data: replies[m.name] ?? null } }) }), 0);
      } else if (m.type === 'diffmsg') {
        const old = previous[m.name] || '';
        previous[m.name] = m.diff ? old.slice(0, m.start) + m.args + old.slice(m.end) : m.args;
        window.__last[m.name] = JSON.parse(previous[m.name]);
      } else if (m.type === 'message') {
        window.__messages.push(m);
      }
    }
  } };
})();`;

(async () => {
  let chrome, socket;
  try {
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    chrome = spawn(process.env.CHROME || '/opt/google/chrome/chrome', [
      '--headless=new', '--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage',
      '--remote-debugging-port=0', '--user-data-dir=' + temp, 'about:blank'
    ], { stdio: 'ignore' });
    const activePort = path.join(temp, 'DevToolsActivePort');
    for (let i = 0; i < 100 && !fs.existsSync(activePort); i++) await new Promise(r => setTimeout(r, 100));
    if (!fs.existsSync(activePort)) throw new Error('Chrome did not start');
    const port = Number(fs.readFileSync(activePort, 'utf8').split('\n')[0]);
    const targets = await (await fetch('http://127.0.0.1:' + port + '/json/list')).json();
    const page = targets.find(t => t.type === 'page');
    socket = new WebSocket(page.webSocketDebuggerUrl);
    await new Promise((resolve, reject) => { socket.onopen = resolve; socket.onerror = reject; });
    let serial = 0;
    const pending = new Map();
    socket.onmessage = event => {
      const m = JSON.parse(event.data);
      if (!m.id) return;
      const p = pending.get(m.id);
      pending.delete(m.id);
      if (m.error) p.reject(new Error(m.error.message)); else p.resolve(m.result);
    };
    const cdp = (method, params = {}) => new Promise((resolve, reject) => {
      const id = ++serial;
      pending.set(id, { resolve, reject });
      socket.send(JSON.stringify({ id, method, params }));
    });
    const evalJs = async expression => {
      const r = await cdp('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true });
      if (r.exceptionDetails) throw new Error(r.exceptionDetails.text);
      return r.result.value;
    };
    const wait = async (label, expression) => {
      for (let i = 0; i < 150; i++) {
        if (await evalJs(expression)) return;
        await new Promise(r => setTimeout(r, 100));
      }
      throw new Error('Timed out: ' + label + ' (' + JSON.stringify(await evalJs('window.__messages?.slice(-3)')) + ')');
    };
    const deliver = (name, args) => evalJs('window.__unoDeliver(' + JSON.stringify(JSON.stringify({ name, args })) + ')');
    await cdp('Page.enable');
    await cdp('Runtime.enable');
    await cdp('Page.addScriptToEvaluateOnNewDocument', { source: preload });
    await cdp('Page.navigate', { url: 'http://127.0.0.1:' + server.address().port + '/index.html' });
    await wait('editor ready', '!!window.__last?.FileLoaded');
    await deliver('LoadFile', { text: '# Kiwi\n\nA kiwi and another kiwi.\n', loadId: 2, basePath: '/tmp' });
    await wait('document loaded', 'window.__last?.FileLoaded?.loadId === 2');
    await deliver('FindDocument', { documentId: 'check' });
    await deliver('ShowFind', { value: 'kiwi', opt: { searchIsCaseSensitive: false, searchIsWholeWord: false, searchIsRegexp: false } });
    await deliver('FindResultsOpen', { open: true });
    await wait('visual results', 'window.__messages?.some(m => m.name === "DocumentFindResults" && m.args.total === 3)');
    let results = await evalJs('window.__messages.filter(m => m.name === "DocumentFindResults").at(-1).args');
    if (results.entries.length !== 3 || !results.entries[1].preview.includes('[kiwi]')) throw new Error('Bad visual previews: ' + JSON.stringify(results));
    await deliver('FindActivate', { index: 2, query: 'kiwi' });
    await wait('visual activation', 'window.__typedownMuya?.contentState?.searchMatches?.index === 2');
    const beforeSource = await evalJs('window.__messages.filter(m => m.name === "DocumentFindResults").length');
    await deliver('SettingsChanged', { sourceCode: true });
    await wait('source editor', '!!document.querySelector(".CodeMirror")');
    await deliver('FindRefresh', null);
    await wait('source results', 'window.__messages?.filter(m => m.name === "DocumentFindResults").length > ' + beforeSource);
    results = await evalJs('window.__messages.filter(m => m.name === "DocumentFindResults").at(-1).args');
    if (results.entries.length !== 3) throw new Error('Bad source previews: ' + JSON.stringify(results));
    await deliver('FindActivate', { index: 1, query: 'kiwi' });
    await wait('source activation', '(() => { const cm = document.querySelector(".CodeMirror")?.CodeMirror; if (!cm) return false; const text = cm.getValue(); return cm.getSelection().toLowerCase() === "kiwi" && cm.indexFromPos(cm.getCursor("from")) === text.indexOf("kiwi"); })()');
    console.log('PASS visual and source search previews, count, and result activation');
  } finally {
    if (socket) socket.close();
    if (chrome && chrome.exitCode === null) {
      const stopped = new Promise(resolve => { chrome.once('exit', resolve); setTimeout(resolve, 3000); });
      chrome.kill();
      await stopped;
    }
    server.close();
    for (let i = 0; i < 20; i++) {
      try { fs.rmSync(temp, { recursive: true, force: true }); break; }
      catch (error) {
        if (i === 19 || error.code !== 'ENOTEMPTY') throw error;
        await new Promise(r => setTimeout(r, 100));
      }
    }
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
