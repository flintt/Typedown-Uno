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
    // Which key presses the shell wants back; replaced by the host (see "ShortcutMap") once it knows the bindings.
    var forwarded = [];
    var findShortcut = { key: 'f', ctrl: true, shift: false, alt: false };
    window.addEventListener('keydown', function (e) {
        var ctrl = e.ctrlKey || e.metaKey;
        var key = e.key === 'Tab' ? 'tab' : e.key.toLowerCase();
        if (matches(findShortcut, key, ctrl, e.shiftKey, e.altKey)) { e.preventDefault(); e.stopPropagation(); showFind(); return; }
        if (key === 'escape' && !ctrl && findBar && findBar.style.display !== 'none') { e.preventDefault(); e.stopPropagation(); hideFind(); return; }
        for (var i = 0; i < forwarded.length; i++) {
            if (!matches(forwarded[i], key, ctrl, e.shiftKey, e.altKey)) continue;
            e.preventDefault();
            e.stopPropagation();
            send(JSON.stringify({ type: 'message', name: 'Shortcut', args: { key: key, ctrl: ctrl, shift: e.shiftKey, alt: e.altKey } }));
            return;
        }
    }, true);

    function matches(binding, key, ctrl, shift, alt) {
        return binding && binding.key === key && !!binding.ctrl === ctrl && !!binding.shift === shift && !!binding.alt === alt;
    }

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
    var findOptions = { searchIsCaseSensitive: false, searchIsWholeWord: false, searchIsRegexp: false };
    function searchOptions() { return Object.assign({ selection: {} }, findOptions); }
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

    // Files dropped on the editor: the page is a native web view, so the host never sees the drop. WebKit exposes
    // the dropped paths as text/uri-list, which is enough for the host to open documents and insert images.
    window.addEventListener('dragover', function (e) { e.preventDefault(); e.dataTransfer.dropEffect = 'copy'; }, true);
    window.addEventListener('drop', function (e) {
        var list = e.dataTransfer && e.dataTransfer.getData('text/uri-list');
        if (!list) return;
        var paths = list.split(/\r?\n/)
            .filter(function (l) { return l && l.indexOf('#') !== 0 && l.indexOf('file://') === 0; })
            .map(function (l) { return decodeURIComponent(l.replace(/^file:\/\//, '')); });
        if (!paths.length) return;
        e.preventDefault();
        e.stopPropagation();
        send(JSON.stringify({ type: 'message', name: 'FilesDropped', args: { paths: paths } }));
    }, true);

    function hasImage(items) {
        for (var i = 0; i < items.length; i++) if (items[i].kind === 'file' && items[i].type.indexOf('image/') === 0) return true;
        return false;
    }

    // Pasted images are handed to the host, which stores them next to the document and inserts a link; without
    // this the editor would embed a multi-megabyte data: URL in the Markdown.
    window.addEventListener('paste', function (e) {
        var items = e.clipboardData && e.clipboardData.items;
        var hasText = false;
        for (var k = 0; items && k < items.length; k++) if (items[k].kind === 'string') hasText = true;
        if (!items || !items.length || (!hasText && !hasImage(items))) {
            // WebKit does not always expose clipboard images to the page (it sees nothing at all in some
            // sessions); let the host look at the system clipboard instead.
            send(JSON.stringify({ type: 'message', name: 'ClipboardImageRequest', args: {} }));
            return;
        }
        for (var i = 0; i < items.length; i++) {
            if (items[i].kind !== 'file' || items[i].type.indexOf('image/') !== 0) continue;
            var file = items[i].getAsFile();
            if (!file) continue;
            e.preventDefault();
            e.stopPropagation();
            var reader = new FileReader();
            reader.onload = function () {
                send(JSON.stringify({ type: 'message', name: 'ImagePasted', args: { dataUrl: reader.result } }));
            };
            reader.readAsDataURL(file);
            return;
        }
    }, true);

    window.__unoDeliver = function (data) {
        try {
            var msg = JSON.parse(data);
            if (msg && msg.name === 'ShortcutMap') {
                forwarded = (msg.args && msg.args.keys) || [];
                for (var i = 0; i < forwarded.length; i++)
                    if (forwarded[i].find) findShortcut = forwarded[i];
                return;
            }
            if (msg && msg.name === 'ShowFind') { if (msg.args && msg.args.opt) findOptions = msg.args.opt; showFind(msg.args && msg.args.value); return; }
            if (msg && msg.name === 'HideFind') { hideFind(); return; }
        } catch (e) { }
        deliver(data);
    };
})();
