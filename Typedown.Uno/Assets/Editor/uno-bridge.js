// Host bridge shim for the Typedown editor inside an Uno Platform WebView2.
// The editor only knows window.chrome.webview (Microsoft WebView2). On Linux/macOS Uno exposes the
// WebKit message handler "unoWebView" instead; on Windows the real chrome.webview is present and is
// simply passed through. Host -> editor always goes through window.__unoDeliver(json) (ExecuteScriptAsync).
(function () {
    var native = window.chrome && window.chrome.webview;
    var listeners = [];
    function send(raw) {
        if (native) { native.postMessage(raw); return; }
        var wk = window.webkit && window.webkit.messageHandlers && window.webkit.messageHandlers.unoWebView;
        if (wk) { wk.postMessage(raw); return; }
        if (window.unoWebView && window.unoWebView.postMessage) { window.unoWebView.postMessage(raw); return; }
        console.warn('uno-bridge: no host bridge available', raw);
    }
    if (!native) {
        window.chrome = window.chrome || {};
        window.chrome.webview = {
            addEventListener: function (type, listener) { if (type === 'message') listeners.push(listener); },
            removeEventListener: function (type, listener) { listeners = listeners.filter(function (l) { return l !== listener; }); },
            postMessage: send
        };
    }
    // Shell shortcuts pressed while the (native) web view has focus never reach the host window on Linux/macOS,
    // so forward the ones the shell handles as a "Shortcut" message. The editor keeps its own (Ctrl+B/I/…).
    var forwarded = { 's': true, 'o': true, 'n': true, 'w': true, 'tab': true, ',': true, '/': true, 'b': 'shift', 'r': 'shift', 'f': 'shift' };
    window.addEventListener('keydown', function (e) {
        var ctrl = e.ctrlKey || e.metaKey;
        var key = e.key === 'Tab' ? 'tab' : e.key.toLowerCase();
        var rule = forwarded[key];
        if (ctrl && key === 'f' && !e.shiftKey) { e.preventDefault(); e.stopPropagation(); showFind(); return; }
        if (key === 'escape' && !ctrl && findBar && findBar.style.display !== 'none') { e.preventDefault(); e.stopPropagation(); hideFind(); return; }
        var wanted = ctrl && rule && (rule !== 'shift' || e.shiftKey);
        if (!wanted) return;
        e.preventDefault();
        e.stopPropagation();
        send(JSON.stringify({ type: 'message', name: 'Shortcut', args: { key: key, ctrl: ctrl, shift: e.shiftKey, alt: e.altKey } }));
    }, true);

    function deliver(data) {
        if (native && typeof native.dispatchEvent === 'function') {
            native.dispatchEvent(new MessageEvent('message', { data: data }));
            return;
        }
        listeners.forEach(function (l) { try { l({ data: data }); } catch (e) { console.error(e); } });
    }
    function local(name, args) { deliver(JSON.stringify({ name: name, args: args })); }

    // In-page find bar. Keyboard focus cannot be moved from the native web view to the host window on Linux/macOS,
    // so the find UI lives in the page and drives the editor's own Search/Find handlers directly.
    var findBar = null, findInput = null, findCount = null;
    // `selection` must be truthy or the editor ignores the search; an empty object means "from the start".
    function searchOptions() { return { searchIsCaseSensitive: false, searchIsWholeWord: false, searchIsRegexp: false, selection: {} }; }
    function runSearch() {
        local('Search', { value: findInput.value, opt: searchOptions() });
        setTimeout(function () {
            var muya = window.__typedownMuya;
            var matches = muya && muya.contentState && muya.contentState.searchMatches && muya.contentState.searchMatches.matches;
            var n = matches ? matches.length : document.querySelectorAll('.ag-selection, .ag-highlight').length;
            findCount.textContent = findInput.value ? String(n) : '';
        }, 80);
    }
    function ensureFindBar() {
        if (findBar) return;
        findBar = document.createElement('div');
        findBar.id = 'uno-find-bar';
        findBar.style.cssText = 'position:fixed;top:8px;right:24px;z-index:100000;display:none;align-items:center;gap:6px;padding:6px 8px;border-radius:6px;' +
            'background:rgba(128,128,128,0.16);backdrop-filter:blur(12px);border:1px solid rgba(128,128,128,0.3);font:13px system-ui,sans-serif;color:inherit;';
        findInput = document.createElement('input');
        findInput.type = 'text';
        findInput.placeholder = 'Find';
        findInput.style.cssText = 'width:220px;padding:4px 6px;border:1px solid rgba(128,128,128,0.4);border-radius:4px;background:rgba(255,255,255,0.6);color:#222;outline:none;';
        findCount = document.createElement('span');
        findCount.style.cssText = 'min-width:24px;opacity:0.8;color:#888;';
        function button(text, title, onClick) {
            var b = document.createElement('button');
            b.textContent = text; b.title = title;
            b.style.cssText = 'padding:2px 8px;border:1px solid rgba(128,128,128,0.4);border-radius:4px;background:rgba(255,255,255,0.7);color:#222;cursor:pointer;';
            b.addEventListener('click', function (e) { e.preventDefault(); onClick(); });
            return b;
        }
        findBar.appendChild(findInput);
        findBar.appendChild(findCount);
        findBar.appendChild(button('\u2191', 'Previous (Shift+Enter)', function () { local('Find', { action: 'prev' }); }));
        findBar.appendChild(button('\u2193', 'Next (Enter)', function () { local('Find', { action: 'next' }); }));
        findBar.appendChild(button('\u2715', 'Close (Esc)', hideFind));
        findInput.addEventListener('input', runSearch);
        findInput.addEventListener('keydown', function (e) {
            if (e.key === 'Enter') { e.preventDefault(); local('Find', { action: e.shiftKey ? 'prev' : 'next' }); }
            else if (e.key === 'Escape') { e.preventDefault(); hideFind(); }
            e.stopPropagation();
        });
        document.body.appendChild(findBar);
    }
    function showFind(value) {
        ensureFindBar();
        findBar.style.display = 'flex';
        if (typeof value === 'string') findInput.value = value;
        local('SearchOpenChange', { open: 1 });
        findInput.focus();
        findInput.select();
        if (findInput.value) runSearch();
    }
    function hideFind() {
        if (!findBar) return;
        findBar.style.display = 'none';
        local('Search', { value: '', opt: searchOptions() });
        local('SearchOpenChange', { open: 0 });
        var editor = document.getElementById('ag-editor-id') || document.querySelector('.CodeMirror textarea');
        if (editor) editor.focus();
    }

    window.__unoDeliver = function (data) {
        try {
            var msg = JSON.parse(data);
            if (msg && msg.name === 'ShowFind') { showFind(msg.args && msg.args.value); return; }
            if (msg && msg.name === 'HideFind') { hideFind(); return; }
        } catch (e) { }
        deliver(data);
    };
})();
