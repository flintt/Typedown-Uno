#!/usr/bin/env node
// Exercise the real bundled editor and the Uno bridge, with the host clipboard protocol mocked.
const fs = require('fs');
const http = require('http');
const os = require('os');
const path = require('path');
const { spawn } = require('child_process');

const root = path.resolve(__dirname, '../Typedown.Uno/Assets/Editor');
const profile = fs.mkdtempSync(path.join(os.tmpdir(), 'typedown-copy-'));
const server = http.createServer((req, res) => {
  const file = path.resolve(root, '.' + decodeURIComponent(req.url.split('?')[0]));
  if (!file.startsWith(root + path.sep) || !fs.existsSync(file) || !fs.statSync(file).isFile()) {
    res.statusCode = 404; res.end(); return;
  }
  fs.createReadStream(file).pipe(res);
});

const preload = `(() => {
  const listeners = [], previous = {};
  window.__last = {}; window.__rich = []; window.__copiedText = ''; window.__copyId = null; window.__messages = []; window.__invokes = [];
  const deliver = value => listeners.forEach(fn => fn({ data: JSON.stringify(value) }));
  const answers = {
    GetSettings: { markdown: '# Blocks\\n\\n| Name | Value |\\n| --- | --- |\\n| Alpha | **Bold** |\\n\\n\`\`\`mermaid\\ngraph TD\\nA-->B\\n\`\`\`\\n', sourceCode: false, readOnly: false, fontSize: 16, lineHeight: 1.6,
      editorAreaWidth: '900px', tabSize: 4, textDirection: 'auto', preferLooseListItem: true,
      listIndentation: '1', tableAlignColumns: false, basePath: '/tmp', loadId: 1 },
    GetCurrentTheme: { theme: 'Light', accentColor: { r: 0, g: 120, b: 212, a: 1 },
      background: { R: 249, G: 249, B: 249, A: 1 } },
    ContentLoaded: '', GetStringResources: {}
  };
  window.chrome = { webview: {
    addEventListener: (_type, fn) => listeners.push(fn),
    removeEventListener: (_type, fn) => { const i = listeners.indexOf(fn); if (i >= 0) listeners.splice(i, 1); },
    dispatchEvent: event => listeners.forEach(fn => fn(event)),
    postMessage: raw => {
      const m = JSON.parse(raw);
      if (m.type === 'invoke') {
        window.__invokes.push({ name: m.name, args: m.args });
        let value = answers[m.name] ?? null;
        if (m.name === 'BeginRichCopy') { window.__copyId = m.args.id; window.__copiedText = ''; value = true; }
        if (m.name === 'SetClipboard' && m.args.type === 'text/plain') window.__copiedText = m.args.data;
        if (m.name === 'CopiedBlockSource') { value = m.args.id === window.__copyId ? window.__copiedText : ''; window.__copyId = null; }
        if (m.name === 'CopyRichBlock') { window.__rich.push(m.args); value = true; }
        setTimeout(() => deliver({ name: m.id, args: { code: 0, data: value } }), 0);
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
      '--remote-debugging-port=0', '--user-data-dir=' + profile, 'about:blank'
    ], { stdio: 'ignore' });
    const portFile = path.join(profile, 'DevToolsActivePort');
    for (let i = 0; i < 100 && !fs.existsSync(portFile); i++) await new Promise(r => setTimeout(r, 100));
    if (!fs.existsSync(portFile)) throw new Error('Chrome did not start');
    const port = Number(fs.readFileSync(portFile, 'utf8').split('\n')[0]);
    const pages = await (await fetch('http://127.0.0.1:' + port + '/json/list')).json();
    socket = new WebSocket(pages.find(t => t.type === 'page').webSocketDebuggerUrl);
    await new Promise((resolve, reject) => { socket.onopen = resolve; socket.onerror = reject; });
    let serial = 0;
    const pending = new Map();
    socket.onmessage = event => {
      const m = JSON.parse(event.data);
      if (!m.id) return;
      const p = pending.get(m.id); pending.delete(m.id);
      if (m.error) p.reject(new Error(m.error.message)); else p.resolve(m.result);
    };
    const cdp = (method, params = {}) => new Promise((resolve, reject) => {
      const id = ++serial; pending.set(id, { resolve, reject });
      socket.send(JSON.stringify({ id, method, params }));
    });
    const run = async expression => {
      const r = await cdp('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true });
      if (r.exceptionDetails) throw new Error(r.exceptionDetails.text);
      return r.result.value;
    };
    const wait = async (label, expression) => {
      for (let i = 0; i < 80; i++) {
        if (await run(expression)) return;
        await new Promise(r => setTimeout(r, 100));
      }
      throw new Error('Timed out: ' + label + ' (' + JSON.stringify(await run('({ invokes:window.__invokes?.slice(-5), copyError:window.__unoRichCopyError, trace:window.__unoRichCopyTrace, copied:window.__copiedText, rich:window.__rich })')) + ')');
    };
    const contextCopy = async selector => {
      await run(`(() => {
        const el = document.querySelector(${JSON.stringify(selector)});
        if (!el) throw new Error('missing figure');
        el.dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, cancelable: true, clientX: 100, clientY: 100 }));
        const row = document.querySelector('#uno-context-menu > div');
        if (!row || row.style.opacity !== '1') throw new Error('copy content unavailable');
        row.dispatchEvent(new MouseEvent('mouseup', { bubbles: true, cancelable: true }));
      })()`);
    };
    await cdp('Page.enable'); await cdp('Runtime.enable');
    await cdp('Page.addScriptToEvaluateOnNewDocument', { source: preload });
    await cdp('Page.navigate', { url: 'http://127.0.0.1:' + server.address().port + '/index.html' });
    await wait('editor ready', '!!window.__last?.FileLoaded');
    await wait('table rendered', '!!document.querySelector("figure[data-role=TABLE] table")');
    await contextCopy('figure[data-role=TABLE] table');
    await wait('rich table copy', 'window.__rich.length === 1');
    const table = await run('window.__rich[0]');
    if (!table.text.includes('| Alpha | **Bold** |') || !table.html.includes('<table') || !table.html.includes('<strong') || table.png)
      throw new Error('Incorrect table formats: ' + JSON.stringify(table));
    console.log('PASS table Markdown and HTML');
    await wait('diagram preview', '!!document.querySelector("figure .ag-container-preview svg")');
    await run(`document.querySelector('figure .ag-container-preview svg').dispatchEvent(
      new MouseEvent('mouseover', { bubbles: true, cancelable: true }))`);
    await wait('diagram copy button', `(() => {
      const button = document.querySelector('#uno-diagram-copy');
      const codeButton = document.querySelector('figure[data-role="MERMAID"] pre .ag-code-copy');
      return button && codeButton && button.style.display === 'block' && button.getAttribute('aria-label') === 'Copy content'
        && button.classList.contains('ag-code-copy') && getComputedStyle(button).width === getComputedStyle(codeButton).width
        && button.querySelector('svg rect')?.getAttribute('x') === codeButton.querySelector('svg rect')?.getAttribute('x');
    })()`);
    await run(`document.querySelector('#uno-diagram-copy').dispatchEvent(
      new MouseEvent('click', { bubbles: true, cancelable: true }))`);
    await wait('rich diagram copy', 'window.__rich.length === 2');
    await wait('diagram copy confirmation', `document.querySelector('#uno-diagram-copy').classList.contains('ag-code-copied')`);
    const diagram = await run('({ text: window.__rich[1].text, html: window.__rich[1].html, pngPrefix: window.__rich[1].png?.slice(0, 24) })');
    if (!diagram.text.includes('```mermaid') || !diagram.pngPrefix?.startsWith('iVBORw0KGgo') || diagram.html)
      throw new Error('Incorrect diagram formats: ' + JSON.stringify(diagram));
    console.log('PASS diagram hover button, Markdown and PNG');
  } finally {
    if (socket) socket.close();
    if (chrome && chrome.exitCode === null) {
      const stopped = new Promise(resolve => { chrome.once('exit', resolve); setTimeout(resolve, 3000); });
      chrome.kill(); await stopped;
    }
    server.close();
    for (let i = 0; i < 20; i++) {
      try { fs.rmSync(profile, { recursive: true, force: true }); break; }
      catch (error) { if (i === 19 || error.code !== 'ENOTEMPTY') throw error; await new Promise(r => setTimeout(r, 100)); }
    }
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
