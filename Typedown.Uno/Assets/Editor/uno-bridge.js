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
    var forwarded = { 's': true, 'o': true, 'n': true, 'w': true, 'f': true, 'tab': true, ',': true, '/': true, 'b': 'shift', 'r': 'shift' };
    window.addEventListener('keydown', function (e) {
        var ctrl = e.ctrlKey || e.metaKey;
        var key = e.key === 'Tab' ? 'tab' : e.key.toLowerCase();
        var rule = forwarded[key];
        var wanted = (ctrl && rule && (rule !== 'shift' || e.shiftKey)) || (key === 'escape' && !ctrl);
        if (!wanted) return;
        if (key === 'escape' && !document.querySelector('.ag-highlight')) return;
        e.preventDefault();
        e.stopPropagation();
        send(JSON.stringify({ type: 'message', name: 'Shortcut', args: { key: key, ctrl: ctrl, shift: e.shiftKey, alt: e.altKey } }));
    }, true);

    window.__unoDeliver = function (data) {
        if (native && typeof native.dispatchEvent === 'function') {
            native.dispatchEvent(new MessageEvent('message', { data: data }));
            return;
        }
        listeners.forEach(function (l) { try { l({ data: data }); } catch (e) { console.error(e); } });
    };
})();
