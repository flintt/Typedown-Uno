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
    // Script errors to the host's log: an error that unmounts the editor leaves a blank page that answers nothing, and
    // the log said only that the editor was not responding. At most 50 per page, so a loop cannot flood it.
    var errorsSent = 0;
    function reportError(message, source, line, column, stack) {
        if (errorsSent++ >= 50) return;
        try {
            send(JSON.stringify({ type: 'message', name: 'PageError', args: {
                message: String(message || ''), source: String(source || '').split('/').pop(), line: line || 0, column: column || 0,
                stack: String(stack || '').slice(0, 2000) } }));
        } catch (e) { }
    }
    window.addEventListener('error', function (e) {
        reportError(e.message, e.filename, e.lineno, e.colno, e.error && e.error.stack);
    });
    window.addEventListener('unhandledrejection', function (e) {
        var r = e.reason;
        reportError('unhandled rejection: ' + (r && r.message || r), '', 0, 0, r && r.stack);
    });
    var forwarded = [];
    var findShortcut = { key: 'f', ctrl: true, shift: false, alt: false };
    window.addEventListener('keydown', function (e) {
        // Vim keys (Settings > Vim keys): the Ctrl keys Vim needs in its current mode stay with the page.
        if (typeof window.__typedownVimWants === 'function' && window.__typedownVimWants(e)) return;
        var ctrl = e.ctrlKey || e.metaKey;
        var key = e.key === 'Tab' ? 'tab' : e.key.toLowerCase();
        if (matches(findShortcut, key, ctrl, e.shiftKey, e.altKey)) { e.preventDefault(); e.stopPropagation(); showFind(); return; }
        if (key === 'escape' && !ctrl && findBar && findBar.style.display !== 'none') { e.preventDefault(); e.stopPropagation(); hideFind(); return; }
        // Ctrl+C/Ctrl+X go through the editor so the clipboard carries the Markdown source, the same as the
        // context menu and as the Windows edition. The paste listener routes Ctrl+V through the host so text and
        // images use the same native clipboard paths as the menus.
        // Muya tracks its cursor separately from the browser selection. WebKit can therefore report an empty
        // window.getSelection() even while the editor has selected text. Always let Muya decide whether there is
        // anything to copy, except while a regular input (for example the find box) owns the keystroke.
        var target = e.target;
        var nativeTextInput = target && (target.tagName === 'INPUT' || target.tagName === 'TEXTAREA');
        if (ctrl && !e.shiftKey && !e.altKey && (key === 'c' || key === 'x') && window.__typedownMuya && !nativeTextInput) {
            if (key === 'x' && isReadOnly()) { e.preventDefault(); return; }
            e.preventDefault();
            e.stopPropagation();
            local(key === 'c' ? 'Copy' : 'Cut', { type: 'normal', copyInfo: null });
            return;
        }
        // Alt+1..9 belongs to the tab bar, which lives in the shell; the editor never does anything with it.
        if (e.altKey && !ctrl && !e.shiftKey && key.length === 1 && key >= '0' && key <= '9') {
            e.preventDefault();
            e.stopPropagation();
            send(JSON.stringify({ type: 'message', name: 'Shortcut', args: { key: key, ctrl: false, shift: false, alt: true } }));
            return;
        }
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

    var richCopySerial = 0;
    function invokeHost(name, args) {
        return new Promise(function (resolve, reject) {
            var id = 'uno_rich_copy_' + (++richCopySerial);
            var timer = setTimeout(function () { finish(); reject(new Error(name + ' timed out')); }, 10000);
            function finish() { clearTimeout(timer); window.chrome.webview.removeEventListener('message', reply); }
            function reply(event) {
                var message;
                try { message = typeof event.data === 'string' ? JSON.parse(event.data) : event.data; } catch (e) { return; }
                if (!message || message.name !== id) return;
                finish();
                if (message.args && message.args.code === 0) resolve(message.args.data);
                else reject(new Error(message.args && message.args.msg || name + ' failed'));
            }
            window.chrome.webview.addEventListener('message', reply);
            send(JSON.stringify({ type: 'invoke', id: id, name: name, args: args }));
        });
    }

    // UI the editor expects the host to draw. The editor's reports are sent diffed, so the host reassembles them
    // and hands each one back as "ShowFloat"; the drawing happens here because a host popup cannot cover the
    // native web view on Linux.
    var floatHandlers = {
        OpenFormatPicker: function (a) { showFormatPicker(a); },
        OpenImageToolbar: function (a) { showImageToolbar(a); },
        OpenFrontMenu: function (a) { showFrontMenu(a); },
        OpenTableTools: function (a) { showTableTools(a); },
        OpenToolTip: function (a) { showToolTip(a); }
    };

    // Home stays in this page: on Linux the editor is a native WebKit child above XAML overlays.
    // All path labels are text nodes, and the host validates a chosen path against its own recent/favorite lists.
    var homeOverlay = null, homeVersion = -1;
    function homeAction(action, path) {
        send(JSON.stringify({ type: 'message', name: 'HomeAction', args: { action: action, path: path || null } }));
    }
    function updateHome(args) {
        if (!args || typeof args.version !== 'number' || args.version < homeVersion) return;
        homeVersion = args.version;
        if (!args.show) {
            if (homeOverlay) {
                if (homeOverlay.contains(document.activeElement)) document.activeElement.blur();
                homeOverlay.style.display = 'none';
            }
            return;
        }
        hideDiagramCopyButton();
        var firstOpen = !homeOverlay || homeOverlay.style.display === 'none';
        var hadFocus = homeOverlay && homeOverlay.contains(document.activeElement);
        if (!homeOverlay) {
            homeOverlay = document.createElement('main');
            homeOverlay.id = 'uno-home';
            homeOverlay.style.cssText = 'position:fixed;inset:0;z-index:90000;overflow:auto;box-sizing:border-box;' +
                'background:var(--editorBgColor,#f9f9f9);color:var(--editorColor,#222);font:14px system-ui,sans-serif;';
            document.body.appendChild(homeOverlay);
        }
        homeOverlay.style.display = 'block';
        homeOverlay.replaceChildren();
        var content = document.createElement('div');
        content.style.cssText = 'max-width:980px;margin:0 auto;padding:clamp(28px,7vh,72px) 32px 64px;';
        homeOverlay.appendChild(content);
        var title = document.createElement('h1');
        title.textContent = args.strings.title;
        title.style.cssText = 'font-size:32px;font-weight:600;margin:0 0 26px;letter-spacing:-.03em;';
        content.appendChild(title);
        var actions = document.createElement('div');
        actions.style.cssText = 'display:flex;flex-wrap:wrap;gap:10px;margin-bottom:36px;';
        content.appendChild(actions);
        function quick(label, action) {
            var button = document.createElement('button');
            button.type = 'button'; button.textContent = label;
            button.style.cssText = 'min-width:145px;padding:12px 18px;border-radius:8px;border:1px solid var(--floatBorderColor,#ccc);' +
                'background:var(--itemBgColor,#fff);color:var(--editorColor,#222);font:inherit;text-align:left;cursor:pointer;';
            button.addEventListener('click', function () { homeAction(action); });
            actions.appendChild(button);
            return button;
        }
        var first = quick(args.strings.newFile, 'new');
        quick(args.strings.openFile, 'openFile');
        quick(args.strings.openFolder, 'openFolder');
        var groups = document.createElement('div');
        groups.style.cssText = 'display:grid;grid-template-columns:repeat(auto-fit,minmax(220px,1fr));gap:24px;';
        content.appendChild(groups);
        function homeIcon(folder) {
            var svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
            svg.setAttribute('viewBox', '0 0 24 24');
            svg.setAttribute('width', '17');
            svg.setAttribute('height', '17');
            svg.setAttribute('fill', 'none');
            svg.setAttribute('stroke', 'currentColor');
            svg.setAttribute('stroke-width', '1.7');
            svg.setAttribute('stroke-linejoin', 'round');
            svg.style.cssText = 'flex:none;opacity:.75;';
            var path = document.createElementNS('http://www.w3.org/2000/svg', 'path');
            path.setAttribute('d', folder
                ? 'M3 6.5a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v9a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z'
                : 'M6 3.5h8l4 4v13H6a2 2 0 0 1-2-2v-13a2 2 0 0 1 2-2zm8 0v4h4M8 12h6M8 16h6');
            svg.appendChild(path);
            return svg;
        }
        function section(label, items, action) {
            var group = document.createElement('section');
            var heading = document.createElement('h2');
            heading.textContent = label;
            heading.style.cssText = 'font-size:15px;font-weight:600;margin:0 0 12px;';
            group.appendChild(heading);
            if (!items.length) {
                var empty = document.createElement('p');
                empty.textContent = args.strings.empty;
                empty.style.cssText = 'opacity:.55;margin:0;';
                group.appendChild(empty);
            }
            items.forEach(function (item) {
                var button = document.createElement('button');
                button.type = 'button';
                button.title = item.path;
                button.style.cssText = 'display:block;width:100%;padding:9px 10px;margin-bottom:3px;border:0;border-radius:6px;' +
                    'background:transparent;color:var(--editorColor,#222);font:inherit;text-align:left;cursor:pointer;';
                var name = document.createElement('span');
                name.style.cssText = 'display:flex;align-items:center;gap:8px;min-width:0;';
                var label = document.createElement('span');
                label.textContent = item.name;
                label.style.cssText = 'display:block;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;';
                name.appendChild(homeIcon(item.folder));
                name.appendChild(label);
                var path = document.createElement('small');
                path.textContent = item.path;
                path.style.cssText = 'display:block;opacity:.55;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;';
                button.appendChild(name); button.appendChild(path);
                button.addEventListener('mouseenter', function () { button.style.background = 'var(--itemBgColor,#eee)'; });
                button.addEventListener('mouseleave', function () { button.style.background = 'transparent'; });
                button.addEventListener('click', function () { homeAction(action, item.path); });
                group.appendChild(button);
            });
            groups.appendChild(group);
        }
        section(args.strings.recentFiles, args.recentFiles || [], 'recentFile');
        section(args.strings.recentFolders, args.recentFolders || [], 'recentFolder');
        section(args.strings.favorites, args.favorites || [], 'favorite');
        if (firstOpen || hadFocus) setTimeout(function () { first.focus(); }, 0);
    }

    // In-page find bar. Keyboard focus cannot be moved from the native web view to the host window on Linux/macOS,
    // so the find UI lives in the page and drives the editor's own Search/Find handlers directly.
    var findBar = null, findInput = null, findCount = null, findRow = null, replaceRow = null, replaceInput = null;
    var findResultsOpen = false, findResultsTimer = null;
    // Labels for the find bar; replaced by the host (see "FindStrings") in the interface language.
    var findStrings = {
        find: 'Find', replaceWith: 'Replace with', replace: 'Replace', replaceAll: 'Replace all',
        previous: 'Previous (Shift+Enter)', next: 'Next (Enter)', close: 'Close (Esc)', results: 'Results',
    };
    var findOptions = { searchIsCaseSensitive: false, searchIsWholeWord: false, searchIsRegexp: false };
    // "Find next" starts after the caret, so the editor needs to be told where it is; with no caret the search
    // runs from the top of the document.
    function searchOptions() {
        var muya = window.__typedownMuya;
        var cursor = muya && muya.contentState && muya.contentState.cursor;
        return Object.assign({ selection: cursor || {} }, findOptions);
    }
    function runSearch() {
        local('Search', { value: findInput.value, opt: searchOptions() });
        clearTimeout(findResultsTimer);
        findResultsTimer = setTimeout(function () {
            var source = sourceEditor();
            var cs = findState();
            var matches = cs && cs.searchMatches && cs.searchMatches.matches;
            var n = source ? sourceMatches(source).length : matches ? matches.length : 0;
            findCount.textContent = findInput.value ? String(n) : '';
            // The editor reports model-based FindResults and FindState itself.
        }, 80);
    }
    function sourceEditor() {
        var wrapper = document.querySelector('.CodeMirror');
        return wrapper && wrapper.CodeMirror || null;
    }
    function sourceMatches(cm) {
        if (!findInput || !findInput.value) return [];
        var pattern = findOptions.searchIsRegexp ? findInput.value : findInput.value.replace(/[\[\]\\^$.|?*+()/]/g, '\\$&');
        if (findOptions.searchIsWholeWord) pattern = '\\b' + pattern + '\\b';
        var re;
        try { re = new RegExp(pattern, findOptions.searchIsCaseSensitive ? 'g' : 'gi'); }
        catch (e) { return []; }
        var text = cm.getValue(), matches = [], match;
        while ((match = re.exec(text)) !== null) {
            if (match[0].length) matches.push({ start: match.index, end: re.lastIndex });
            else re.lastIndex++;
        }
        return matches;
    }
    function findState() {
        if (sourceEditor()) return null;
        var muya = window.__typedownMuya;
        return muya && muya.contentState;
    }
    function stepFind(action) {
        local('Find', { action: action });
    }
    function ensureFindBar() {
        if (findBar) return;
        findBar = document.createElement('div');
        findBar.id = 'uno-find-bar';
        findBar.style.cssText = 'position:fixed;top:8px;right:24px;z-index:100000;display:none;flex-direction:column;gap:6px;padding:6px 8px;border-radius:6px;' +
            'background:var(--floatBgColor);border:1px solid var(--floatBorderColor);box-shadow:var(--floatShadow);' +
            'font:13px system-ui,sans-serif;color:var(--editorColor);';
        findRow = document.createElement('div');
        findRow.style.cssText = 'display:flex;align-items:center;gap:6px;';
        replaceRow = document.createElement('div');
        replaceRow.style.cssText = 'display:none;align-items:center;gap:6px;';
        findInput = document.createElement('input');
        findInput.type = 'text';
        findInput.placeholder = findStrings.find;
        findInput.style.cssText = 'width:220px;padding:4px 6px;border:1px solid var(--floatBorderColor);border-radius:4px;' +
            'background:var(--inputBgColor);color:var(--editorColor);outline:none;';
        findCount = document.createElement('span');
        findCount.style.cssText = 'min-width:24px;color:var(--editorColor60);';
        function button(text, title, onClick) {
            var b = document.createElement('button');
            b.textContent = text; b.title = title;
            b.style.cssText = 'padding:2px 8px;border:1px solid var(--floatBorderColor);border-radius:4px;' +
                'background:var(--itemBgColor);color:var(--editorColor);cursor:pointer;';
            b.tabIndex = -1; // Tab moves between the two text fields, not through the buttons
            b.addEventListener('click', function (e) { e.preventDefault(); onClick(); });
            return b;
        }
        replaceInput = document.createElement('input');
        replaceInput.type = 'text';
        replaceInput.placeholder = findStrings.replaceWith;
        replaceInput.style.cssText = findInput.style.cssText;
        findRow.appendChild(findInput);
        findRow.appendChild(findCount);
        findRow.appendChild(button('\u2191', findStrings.previous, function () { stepFind('prev'); }));
        findRow.appendChild(button('\u2193', findStrings.next, function () { stepFind('next'); }));
        findRow.appendChild(button('\u2630', findStrings.results, function () {
            send(JSON.stringify({ type: 'message', name: 'DocumentFindToggle', args: { open: !findResultsOpen } }));
        }));
        findRow.appendChild(button('\u21c4', findStrings.replace, toggleReplace));
        findRow.appendChild(button('\u2715', findStrings.close, hideFind));
        replaceRow.appendChild(replaceInput);
        replaceRow.appendChild(button(findStrings.replace, findStrings.replace, function () { runReplace(false); }));
        replaceRow.appendChild(button(findStrings.replaceAll, findStrings.replaceAll, function () { runReplace(true); }));
        replaceInput.addEventListener('keydown', function (e) {
            if (e.key === 'Enter') { e.preventDefault(); runReplace(e.ctrlKey || e.metaKey); }
            else if (e.key === 'Escape') { e.preventDefault(); hideFind(); }
            else if (e.key === 'Tab' && e.shiftKey) { e.preventDefault(); findInput.focus(); }
            e.stopPropagation();
        });
        findBar.appendChild(findRow);
        findBar.appendChild(replaceRow);
        findInput.addEventListener('input', runSearch);
        findInput.addEventListener('keydown', function (e) {
            if (e.key === 'Enter') { e.preventDefault(); stepFind(e.shiftKey ? 'prev' : 'next'); }
            else if (e.key === 'Escape') { e.preventDefault(); hideFind(); }
            else if (e.key === 'Tab' && !e.shiftKey && replaceRow.style.display !== 'none') { e.preventDefault(); replaceInput.focus(); }
            e.stopPropagation();
        });
        document.body.appendChild(findBar);
    }
    // Replacing works off the current search: the editor replaces what the last Search call matched, so the
    // search is re-run before and after, otherwise "replace all" would work from a stale set of matches.
    function runReplace(all) {
        if (isReadOnly()) return;
        runSearch();
        setTimeout(function () {
            local('Replace', { value: replaceInput.value, opt: Object.assign(searchOptions(), { isSingle: !all }) });
            setTimeout(runSearch, 60);
        }, 60);
    }

    function toggleReplace(show) {
        var on = typeof show === 'boolean' ? show : replaceRow.style.display === 'none';
        replaceRow.style.display = on ? 'flex' : 'none';
        if (on) replaceInput.focus();
    }

    function showFind(value, replace) {
        ensureFindBar();
        findBar.style.display = 'flex';
        toggleReplace(!!replace);
        if (typeof value === 'string') findInput.value = value;
        local('SearchOpenChange', { open: 1 });
        findInput.focus();
        findInput.select();
        if (findInput.value) runSearch();
    }
    // Vim's find commands in reading mode (the page's services/vim): this bridge draws the find bar, so it answers
    // them; :w and :q go on to the host as "VimCommand".
    window.__typedownBridgeVim = function (command) {
        if (command === 'find') { showFind(); return true; }
        if (command === 'findNext' || command === 'findPrevious') {
            if (!findBar || findBar.style.display === 'none' || !findInput.value) { showFind(); return true; }
            stepFind(command === 'findNext' ? 'next' : 'prev');
            return true;
        }
        return false;
    };

    function hideFind() {
        if (!findBar) return;
        findBar.style.display = 'none';
        local('Search', { value: '', opt: searchOptions() });
        local('SearchOpenChange', { open: 0 });
        if (findResultsOpen) send(JSON.stringify({ type: 'message', name: 'DocumentFindToggle', args: { open: false } }));
        var editor = document.getElementById('ag-editor-id') || document.querySelector('.CodeMirror textarea');
        if (editor) editor.focus();
    }


    // In-page context menu. The editor suppresses the native menu (it expects the host to show its own), and on
    // Linux the web view is a separate native window, so a host flyout would be drawn behind it: the menu lives
    // in the page, like the find bar. Labels come from the host ("ContextMenuStrings").
    var menuStrings = { copy: 'Copy', copyContent: 'Copy content', copyAsPlainText: 'Copy as plain text', cut: 'Cut', paste: 'Paste', selectAll: 'Select all' };
    var contextMenu = null;

    function isReadOnly() {
        if (document.body.classList.contains('read-only')) return true;
        var editable = document.querySelector('[contenteditable]');
        return !!editable && editable.getAttribute('contenteditable') === 'false';
    }

    function selectedText() {
        var s = window.getSelection();
        return s ? s.toString() : '';
    }

    function hideContextMenu() {
        if (contextMenu) contextMenu.style.display = 'none';
    }

    function selectAll() {
        var root = document.getElementById('ag-editor-id');
        if (isReadOnly()) {
            if (!root) return;
            var range = document.createRange();
            range.selectNodeContents(root);
            var sel = window.getSelection();
            sel.removeAllRanges();
            sel.addRange(range);
            return;
        }
        local('SelectAll');
    }

    function tableHtml(figure) {
        var table = figure.querySelector('table');
        if (!table) return null;
        var copy = table.cloneNode(true);
        copy.querySelectorAll('.ag-hide,.ag-remove,.ag-table-tool,[contenteditable="false"]:not(th):not(td)').forEach(function (node) { node.remove(); });
        copy.querySelectorAll('th,td').forEach(function (cell) {
            var align = cell.style.textAlign || cell.getAttribute('align');
            if (align === 'left' || align === 'center' || align === 'right') cell.setAttribute('align', align);
        });
        copy.querySelectorAll('*').forEach(function (node) {
            Array.from(node.attributes).forEach(function (attr) {
                if (attr.name === 'href' || attr.name === 'src') {
                    try { node.setAttribute(attr.name, new URL(attr.value, document.baseURI).href); } catch (e) { }
                } else if (!['colspan', 'rowspan', 'align', 'alt', 'title'].includes(attr.name)) node.removeAttribute(attr.name);
            });
        });
        copy.removeAttribute('id'); copy.removeAttribute('class');
        copy.setAttribute('border', '1');
        copy.setAttribute('cellspacing', '0');
        copy.setAttribute('cellpadding', '4');
        copy.setAttribute('style', 'border-collapse:collapse');
        return copy.outerHTML;
    }

    async function previewPng(figure) {
        var preview = figure.querySelector(':scope > .ag-container-preview');
        if (!preview || preview.classList.contains('ag-plantuml-off') || preview.classList.contains('ag-empty')) return null;
        var source = preview.querySelector('svg, img');
        if (!source) return null;
        var rect = source.getBoundingClientRect();
        var box = source.tagName.toLowerCase() === 'svg' && source.viewBox && source.viewBox.baseVal;
        var width = Math.ceil(rect.width || (box && box.width) || source.naturalWidth || 0);
        var height = Math.ceil(rect.height || (box && box.height) || source.naturalHeight || 0);
        if (!(width > 0 && height > 0)) return null;
        var scale = Math.min(2, 8000 / width, 8000 / height, Math.sqrt(16000000 / (width * height)));
        var canvas = document.createElement('canvas');
        canvas.width = Math.ceil(width * scale); canvas.height = Math.ceil(height * scale);
        var context = canvas.getContext('2d');
        if (!context) return null;
        context.fillStyle = '#fff'; context.fillRect(0, 0, canvas.width, canvas.height);
        var image = new Image(), url = null;
        try {
            if (source.tagName.toLowerCase() === 'svg') {
                var clone = source.cloneNode(true);
                clone.setAttribute('xmlns', 'http://www.w3.org/2000/svg');
                clone.setAttribute('width', width); clone.setAttribute('height', height);
                var originals = [source].concat(Array.from(source.querySelectorAll('*')));
                var clones = [clone].concat(Array.from(clone.querySelectorAll('*')));
                originals.forEach(function (node, index) {
                    var css = getComputedStyle(node);
                    var style = clones[index].style;
                    ['fill', 'stroke', 'stroke-width', 'color', 'font-family', 'font-size', 'font-weight', 'text-anchor', 'opacity'].forEach(function (key) {
                        style.setProperty(key, css.getPropertyValue(key));
                    });
                });
                // Chromium treats an SVG image with foreignObject (Mermaid's HTML labels) as a tainted canvas.
                // SVG text remains readable and can be rasterized into a clipboard PNG.
                clone.querySelectorAll('foreignObject').forEach(function (foreign) {
                    var label = foreign.textContent.trim();
                    var replacement = document.createElementNS('http://www.w3.org/2000/svg', 'text');
                    var x = parseFloat(foreign.getAttribute('x') || '0');
                    var y = parseFloat(foreign.getAttribute('y') || '0');
                    var w = parseFloat(foreign.getAttribute('width') || '0');
                    var h = parseFloat(foreign.getAttribute('height') || '0');
                    replacement.setAttribute('x', String(x + w / 2));
                    replacement.setAttribute('y', String(y + h / 2));
                    replacement.setAttribute('text-anchor', 'middle');
                    replacement.setAttribute('dominant-baseline', 'middle');
                    replacement.setAttribute('font-size', '14');
                    replacement.setAttribute('fill', '#333');
                    replacement.textContent = label;
                    foreign.replaceWith(replacement);
                });
                url = URL.createObjectURL(new Blob([new XMLSerializer().serializeToString(clone)], { type: 'image/svg+xml' }));
            }
            await new Promise(function (resolve, reject) {
                image.onload = resolve; image.onerror = reject;
                image.src = url || source.currentSrc || source.src;
            });
            context.drawImage(image, 0, 0, canvas.width, canvas.height);
            return canvas.toDataURL('image/png').slice('data:image/png;base64,'.length);
        } catch (e) {
            console.warn('copy picture failed', e);
            return null;
        } finally { if (url) URL.revokeObjectURL(url); }
    }

    async function copyRichFigure(figure) {
        if (!figure || !figure.id) return false;
        var state = window.__typedownMuya && window.__typedownMuya.contentState;
        if (state && typeof state.copyBlock === 'function') return !!(await state.copyBlock(figure));
        // Older bundles export the block Markdown first; the host replies after its text/plain clipboard call.
        var block = state && state.getBlock(figure.id);
        if (!block) return false;
        var sourceKey = null;
        function findSource(part) {
            if (sourceKey) return;
            if (part.type === 'span' && state.getAnchor(part) === block) { sourceKey = part.key; return; }
            (part.children || []).forEach(findSource);
        }
        findSource(block);
        if (!sourceKey) return false;
        var copyId = 'block-' + Date.now() + '-' + (++richCopySerial);
        try { await invokeHost('BeginRichCopy', { id: copyId }); }
        catch (e) { return false; }
        var selectedCells = state && state.selectedTableCells;
        var selectedImage = state && state.selectedImage;
        if (state) { state.selectedTableCells = null; state.selectedImage = null; }
        try { local('Copy', { type: 'copyBlock', copyInfo: sourceKey }); }
        finally { if (state) { state.selectedTableCells = selectedCells; state.selectedImage = selectedImage; } }
        try {
            var text = await invokeHost('CopiedBlockSource', { id: copyId });
            if (!text) return false;
            var html = figure.matches('[data-role="TABLE"]') ? tableHtml(figure) : null;
            var png = html ? null : await previewPng(figure);
            return html || png ? !!(await invokeHost('CopyRichBlock', { text: text, html: html, png: png })) : false;
        } catch (e) { console.warn('copy block failed', e); return false; }
    }

    // The editor's code-copy icon sits in the folded <pre>, underneath a rendered diagram. Draw one button
    // above the preview without inserting nodes into Muya's virtual DOM; Muya may replace the preview at any time.
    var diagramCopyButton = null, diagramCopyFigure = null, diagramCopyTimer = null;
    function hideDiagramCopyButton() {
        if (diagramCopyButton) diagramCopyButton.style.display = 'none';
        diagramCopyFigure = null;
    }
    function showDiagramCopyButton(preview) {
        if (preview.classList.contains('ag-empty') || preview.classList.contains('ag-plantuml-off')
            || !preview.querySelector('svg, img')) { hideDiagramCopyButton(); return; }
        var figure = preview.closest('figure[data-role]');
        if (!figure || !/^(MERMAID|PLANTUML|FLOWCHART|SEQUENCE|VEGA-LITE)$/.test(figure.dataset.role)) {
            hideDiagramCopyButton(); return;
        }
        // The current editor draws its own copy button for diagrams, tables and display formulas.
        if (figure.querySelector('.ag-diagram-copy')) { hideDiagramCopyButton(); return; }
        if (!diagramCopyButton) {
            diagramCopyButton = document.createElement('button');
            diagramCopyButton.type = 'button';
            diagramCopyButton.id = 'uno-diagram-copy';
            diagramCopyButton.className = 'ag-code-copy';
            diagramCopyButton.style.cssText = 'position:fixed;right:auto;z-index:100000;display:none;width:20px;height:20px;padding:0;' +
                'border:0;background:transparent;box-shadow:none;appearance:none;cursor:default;opacity:.5;';
            // Same icon, size and copied check as the editor's code-block button.
            diagramCopyButton.innerHTML = '<span class="icon" style="display:inline-flex;align-items:center;justify-content:center">' +
                '<svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" stroke-width="1.6"' +
                ' stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">' +
                '<rect x="8.5" y="8.5" width="11" height="11" rx="1.5"/>' +
                '<path d="M15.5 5.5H5.5a1 1 0 0 0-1 1v10"/></svg></span>';
            document.body.appendChild(diagramCopyButton);
            diagramCopyButton.addEventListener('mousedown', function (e) { e.preventDefault(); e.stopPropagation(); });
            diagramCopyButton.addEventListener('click', async function (e) {
                e.preventDefault(); e.stopPropagation();
                var target = diagramCopyFigure && document.getElementById(diagramCopyFigure.id);
                if (!target) return;
                diagramCopyButton.disabled = true;
                var copied = await copyRichFigure(target);
                diagramCopyButton.disabled = false;
                if (!copied || diagramCopyFigure !== target) return;
                diagramCopyButton.classList.add('ag-code-copied');
                clearTimeout(diagramCopyTimer);
                diagramCopyTimer = setTimeout(function () {
                    diagramCopyButton.classList.remove('ag-code-copied');
                    diagramCopyButton.style.opacity = diagramCopyButton.matches(':hover') ? '1' : '.5';
                }, 1500);
            });
            diagramCopyButton.addEventListener('mouseenter', function () { diagramCopyButton.style.opacity = '1'; });
            diagramCopyButton.addEventListener('mouseleave', function () { diagramCopyButton.style.opacity = '.5'; });
        }
        var rect = preview.getBoundingClientRect();
        if (rect.width < 36 || rect.bottom <= 0 || rect.top >= innerHeight) { hideDiagramCopyButton(); return; }
        var imageRect = preview.querySelector('svg, img').getBoundingClientRect();
        diagramCopyFigure = figure;
        diagramCopyButton.title = menuStrings.copyContent;
        diagramCopyButton.setAttribute('aria-label', menuStrings.copyContent);
        diagramCopyButton.style.left = Math.max(rect.left + 4, Math.min(imageRect.right + 8, rect.right - 24, innerWidth - 24)) + 'px';
        diagramCopyButton.style.top = Math.max(4, imageRect.top + 4) + 'px';
        diagramCopyButton.style.display = 'block';
    }
    document.addEventListener('mouseover', function (e) {
        if (diagramCopyButton && diagramCopyButton.contains(e.target)) return;
        var preview = e.target.closest && e.target.closest('#ag-editor-id figure[data-role] > .ag-container-preview');
        if (preview) showDiagramCopyButton(preview);
        else hideDiagramCopyButton();
    }, true);
    document.addEventListener('scroll', hideDiagramCopyButton, true);
    document.addEventListener('mouseleave', hideDiagramCopyButton);

    function showContextMenu(x, y, target) {
        if (!contextMenu) {
            contextMenu = document.createElement('div');
            contextMenu.id = 'uno-context-menu';
            contextMenu.style.cssText = 'position:fixed;z-index:100001;display:none;min-width:150px;padding:4px;border-radius:6px;' +
                'background:var(--floatBgColor);border:1px solid var(--floatBorderColor);box-shadow:var(--floatShadow);' +
                'font:13px system-ui,sans-serif;color:var(--editorColor);user-select:none;';
            document.body.appendChild(contextMenu);
            // A press on the menu must not take the selection away before its command runs.
            contextMenu.addEventListener('mousedown', function (e) { e.preventDefault(); });
        }
        var readOnly = isReadOnly();
        // Remember the selection now: clicking the menu can collapse it, and WebKit refuses execCommand('copy')
        // often enough that the host is the reliable way to reach the clipboard.
        var text = selectedText();
        var hasSelection = !!text;
        var figure = target && target.closest && target.closest('#ag-editor-id figure[data-role]');
        if (figure && !figure.matches('[data-role="TABLE"]') && !figure.querySelector(':scope > .ag-container-preview')) figure = null;
        function copyToHost() { send(JSON.stringify({ type: 'message', name: 'ClipboardSetText', args: { text: text } })); }
        // Clicking the menu collapses the selection, and cut/paste act on the editor's own cursor: remember it
        // while the menu opens and put it back before handing the command over.
        var muya = window.__typedownMuya;
        var savedCursor = muya && muya.contentState && muya.contentState.cursor
            ? JSON.parse(JSON.stringify(muya.contentState.cursor)) : null;
        function editorClipboard(name) {
            if (!muya) return false;
            restoreCursor();
            local(name, { type: 'normal', copyInfo: null });
            return true;
        }
        function restoreCursor() {
            // Reading mode has no caret: the selection on the page is the reader's, and putting a stale cursor back
            // would replace it.
            if (readOnly || !muya || !savedCursor) return;
            try {
                muya.contentState.cursor = savedCursor;
                muya.contentState.setCursor();
            } catch (e) { }
        }
        contextMenu.textContent = '';
        // Cut and paste change the document, so reading mode only offers copy and select all.
        var items = [
            { label: menuStrings.copyContent, enabled: !!figure, hidden: !figure, run: function () { copyRichFigure(figure); } },
            // The editor's own copy puts Markdown and HTML on the clipboard (the host's SetClipboard), which keeps
            // bold, code and links across a copy/paste; plain text is the fallback when there is no editor.
            { label: menuStrings.copy, enabled: hasSelection, run: function () { if (!editorClipboard('Copy')) copyToHost(); } },
            // The text as it reads, without Markdown (the editor's services/plainText); not in source mode, where the
            // text is the Markdown.
            { label: menuStrings.copyAsPlainText, enabled: hasSelection, hidden: !muya || !!document.querySelector('.CodeMirror'),
              run: function () { restoreCursor(); local('Copy', { type: 'copyAsPlainText', copyInfo: null }); } },
            { label: menuStrings.cut, enabled: hasSelection && !readOnly, hidden: readOnly, run: function () { if (!editorClipboard('Cut')) { copyToHost(); local('DeleteSelection'); } } },
            { label: menuStrings.paste, enabled: !readOnly, hidden: readOnly, run: function () { restoreCursor(); send(JSON.stringify({ type: 'message', name: 'ClipboardTextRequest', args: {} })); } },
            { separator: true },
            { label: menuStrings.selectAll, enabled: true, run: selectAll }
        ];
        items.forEach(function (item) {
            if (item.hidden) return;
            if (item.separator) {
                var line = document.createElement('div');
                line.style.cssText = 'height:1px;margin:4px 6px;background:var(--floatBorderColor);';
                contextMenu.appendChild(line);
                return;
            }
            var row = document.createElement('div');
            row.textContent = item.label;
            row.style.cssText = 'padding:6px 12px;border-radius:4px;cursor:' + (item.enabled ? 'pointer' : 'default') + ';opacity:' + (item.enabled ? '1' : '0.4') + ';';
            if (item.enabled) {
                row.addEventListener('mouseenter', function () { row.style.background = 'var(--floatHoverColor)'; });
                row.addEventListener('mouseleave', function () { row.style.background = 'transparent'; });
                row.addEventListener('mouseup', function (e) { e.preventDefault(); e.stopPropagation(); hideContextMenu(); item.run(); });
            }
            contextMenu.appendChild(row);
        });
        contextMenu.style.display = 'block';
        // keep it inside the window
        var rect = contextMenu.getBoundingClientRect();
        contextMenu.style.left = Math.min(x, Math.max(0, window.innerWidth - rect.width - 4)) + 'px';
        contextMenu.style.top = Math.min(y, Math.max(0, window.innerHeight - rect.height - 4)) + 'px';
    }

    window.addEventListener('contextmenu', function (e) {
        if (contextMenu && contextMenu.contains(e.target)) { e.preventDefault(); return; }
        e.preventDefault();
        // the editor's own handler runs too and keeps its cursor in step; only the native menu is suppressed
        showContextMenu(e.clientX, e.clientY, e.target);
        // capture: the editor stops the event from bubbling out of its container
    }, true);
    window.addEventListener('mousedown', function (e) {
        if (contextMenu && contextMenu.style.display !== 'none' && !contextMenu.contains(e.target)) hideContextMenu();
    }, true);
    window.addEventListener('keydown', function (e) { if (e.key === 'Escape') hideContextMenu(); }, true);
    window.addEventListener('scroll', hideContextMenu, true);
    window.addEventListener('blur', hideContextMenu);


    // Floating tools. The editor hands these to the host (the Windows edition draws them as XAML flyouts) and
    // this port had none of them, so selecting text or clicking an image showed nothing at all. Like the context
    // menu they live in the page, because a host popup cannot cover the native web view on Linux.
    var floatBox = null, floatKind = null;

    function hideFloat() {
        if (floatBox) { floatBox.style.display = 'none'; floatKind = null; }
    }

    function floatContainer() {
        if (!floatBox) {
            floatBox = document.createElement('div');
            floatBox.id = 'uno-float-tools';
            floatBox.style.cssText = 'position:fixed;z-index:100000;display:none;padding:3px;border-radius:6px;' +
                'background:var(--floatBgColor);border:1px solid var(--floatBorderColor);box-shadow:var(--floatShadow);' +
                'font:13px system-ui,sans-serif;color:var(--editorColor);user-select:none;white-space:nowrap;';
            document.body.appendChild(floatBox);
        }
        floatBox.textContent = '';
        return floatBox;
    }

    /// Places the box above the reference rectangle, or below it when there is no room.
    function placeFloat(rect) {
        floatBox.style.display = 'block';
        var box = floatBox.getBoundingClientRect();
        var left = Math.max(4, Math.min((rect.left + rect.width / 2) - box.width / 2, window.innerWidth - box.width - 4));
        var top = rect.top - box.height - 8;
        if (top < 4) top = rect.bottom + 8;
        floatBox.style.left = left + 'px';
        floatBox.style.top = top + 'px';
    }

    function toolButton(label, title, active, run) {
        var b = document.createElement('button');
        b.textContent = label;
        b.title = title || '';
        b.style.cssText = 'min-width:28px;height:26px;margin:0 1px;padding:0 6px;border:none;border-radius:4px;cursor:pointer;' +
            'font:13px system-ui,sans-serif;background:' + (active ? 'var(--selectionColor)' : 'transparent') + ';color:var(--editorColor);';
        b.addEventListener('mouseenter', function () { if (!active) b.style.background = 'var(--floatHoverColor)'; });
        b.addEventListener('mouseleave', function () { if (!active) b.style.background = 'transparent'; });
        b.addEventListener('mousedown', function (e) { e.preventDefault(); e.stopPropagation(); });
        b.addEventListener('mouseup', function (e) { e.preventDefault(); e.stopPropagation(); run(); });
        return b;
    }

    // Select text -> bold/italic/… bar. `formats` says which ones the selection already has.
    function showFormatPicker(args) {
        var active = {};
        (args.formats || []).forEach(function (f) { active[f.type] = true; });
        var box = floatContainer();
        [['B', 'strong', 'font-weight:700'], ['I', 'em', 'font-style:italic'], ['U', 'u', 'text-decoration:underline'],
         ['S', 'del', 'text-decoration:line-through'], ['</>', 'inline_code', ''], ['M', 'mark', 'background:rgba(255,235,0,0.5)'],
         ['∑', 'inline_math', ''], ['🔗', 'link', ''], ['⌫', 'clear', '']].forEach(function (item) {
            var b = toolButton(item[0], item[1], !!active[item[1]], function () { local('Format', item[1]); hideFloat(); });
            if (item[2]) b.style.cssText += ';' + item[2];
            box.appendChild(b);
        });
        floatKind = 'format';
        placeFloat(args.boundingClientRect);
    }

    // Click an image -> alignment and delete.
    function showImageToolbar(args) {
        var attrs = args.attrs || {};
        var align = attrs['data-align'];
        var box = floatContainer();
        [['⇤', 'left'], ['⇔', 'center'], ['⇥', 'right'], ['⇹', 'inline']].forEach(function (item) {
            box.appendChild(toolButton(item[0], item[1], align === item[1], function () {
                local('ImageEditToolbarClick', { type: item[1] });
                hideFloat();
            }));
        });
        // 'edit' would ask for the host's image selector, which this port does not draw; picking a replacement
        // file is what that button is for anyway.
        box.appendChild(toolButton('✎', 'edit', false, function () {
            hideFloat();
            send(JSON.stringify({ type: 'message', name: 'ReplaceImageRequest', args: {} }));
        }));
        box.appendChild(toolButton('🗑', 'delete', false, function () { local('ImageEditToolbarClick', { type: 'delete' }); hideFloat(); }));
        floatKind = 'image';
        placeFloat(args.boundingClientRect);
    }

    // Click the paragraph marker -> what to do with the block.
    function showFrontMenu(args) {
        var box = floatContainer();
        box.style.whiteSpace = 'normal';
        var items = [
            [menuStrings.duplicate, function () { local('Duplicate'); }],
            [menuStrings.insertBefore, function () { local('InsertParagraph', 'before'); }],
            [menuStrings.insertAfter, function () { local('InsertParagraph', 'after'); }],
            [menuStrings.deleteParagraph, function () { local('DeleteParagraph'); }]
        ];
        items.forEach(function (item) {
            var row = document.createElement('div');
            row.textContent = item[0];
            row.style.cssText = 'padding:6px 12px;border-radius:4px;cursor:pointer;';
            row.addEventListener('mouseenter', function () { row.style.background = 'var(--floatHoverColor)'; });
            row.addEventListener('mouseleave', function () { row.style.background = 'transparent'; });
            row.addEventListener('mouseup', function (e) { e.preventDefault(); e.stopPropagation(); hideFloat(); local('FrontMenuClosed'); item[1](); });
            box.appendChild(row);
        });
        floatKind = 'front';
        placeFloat(args.boundingClientRect);
    }

    // Click a table's drag bar -> rows and columns.
    function showTableTools(args) {
        var bar = (args.tableInfo || {}).barType;
        var box = floatContainer();
        var items = bar === 'left'
            ? [[menuStrings.insertRowAbove, 'insert', 'previous', 'row'], [menuStrings.insertRowBelow, 'insert', 'next', 'row'], [menuStrings.deleteRow, 'remove', 'current', 'row']]
            : [[menuStrings.insertColLeft, 'insert', 'left', 'column'], [menuStrings.insertColRight, 'insert', 'right', 'column'], [menuStrings.deleteCol, 'remove', 'current', 'column']];
        box.style.whiteSpace = 'normal';
        items.forEach(function (item) {
            var row = document.createElement('div');
            row.textContent = item[0];
            row.style.cssText = 'padding:6px 12px;border-radius:4px;cursor:pointer;';
            row.addEventListener('mouseenter', function () { row.style.background = 'var(--floatHoverColor)'; });
            row.addEventListener('mouseleave', function () { row.style.background = 'transparent'; });
            row.addEventListener('mouseup', function (e) {
                e.preventDefault(); e.stopPropagation(); hideFloat();
                local('EditTable', { action: item[1], location: item[2], target: item[3] });
            });
            box.appendChild(row);
        });
        floatKind = 'table';
        placeFloat(args.boundingClientRect);
    }

    // Hover tooltip for the editor's own icons.
    var tipBox = null;
    function showToolTip(args) {
        if (!args.open) { if (tipBox) tipBox.style.display = 'none'; return; }
        if (!tipBox) {
            tipBox = document.createElement('div');
            tipBox.id = 'uno-tooltip';
            tipBox.style.cssText = 'position:fixed;z-index:100002;display:none;padding:3px 8px;border-radius:4px;' +
                'background:var(--floatBgColor);color:var(--editorColor);border:1px solid var(--floatBorderColor);box-shadow:var(--floatShadow);' +
                'font:12px system-ui,sans-serif;pointer-events:none;white-space:nowrap;';
            document.body.appendChild(tipBox);
        }
        tipBox.textContent = menuStrings['tip_' + args.tooltip] || args.tooltip;
        tipBox.style.display = 'block';
        var rect = args.boundingClientRect, box = tipBox.getBoundingClientRect();
        tipBox.style.left = Math.max(4, Math.min(rect.left + rect.width / 2 - box.width / 2, window.innerWidth - box.width - 4)) + 'px';
        var top = rect.top - box.height - 6;
        tipBox.style.top = (top < 4 ? rect.bottom + 6 : top) + 'px';
    }

    window.addEventListener('mousedown', function (e) {
        if (floatBox && floatKind && !floatBox.contains(e.target)) hideFloat();
    }, true);
    window.addEventListener('keydown', function (e) { if (e.key === 'Escape') hideFloat(); }, true);
    window.addEventListener('scroll', hideFloat, true);

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
        // Text means plain text: a picture copied in a browser comes as image/png and text/html with no plain text,
        // and was taken for text, found none and pasted nothing.
        var hasText = false, types = [];
        for (var k = 0; items && k < items.length; k++) {
            types.push(items[k].kind + ':' + items[k].type);
            if (items[k].kind === 'string' && items[k].type === 'text/plain') hasText = true;
        }
        if (!items || !items.length || (!hasText && !hasImage(items))) {
            // WebKit does not always expose the X11 clipboard to the page. The host can distinguish text from
            // images and use the native clipboard API when the page receives no items.
            e.preventDefault();
            e.stopImmediatePropagation();
            send(JSON.stringify({ type: 'message', name: 'ClipboardPasteRequest', args: { types: types } }));
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
        if (hasText) {
            // Use the host path for text so keyboard and context-menu paste behave identically.
            e.preventDefault();
            e.stopImmediatePropagation();
            send(JSON.stringify({ type: 'message', name: 'ClipboardTextRequest', args: {} }));
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
            if (msg && msg.name === 'ContextMenuStrings') { menuStrings = Object.assign(menuStrings, msg.args || {}); return; }
            if (msg && msg.name === 'HomeState') { updateHome(msg.args); return; }
            if (msg && msg.name === 'FindStrings') {
                findStrings = Object.assign(findStrings, msg.args || {});
                if (findInput) findInput.placeholder = findStrings.find;
                if (replaceInput) replaceInput.placeholder = findStrings.replaceWith;
                return;
            }
            if (msg && msg.name === 'FindDocument') return;
            if (msg && msg.name === 'FindRefresh') { if (findResultsOpen && findInput && findInput.value) runSearch(); return; }
            if (msg && msg.name === 'FindResultsOpen') {
                findResultsOpen = !!(msg.args && msg.args.open);
                if (findResultsOpen) ensureFindBar();
                deliver(data);
                return;
            }
            if (msg && msg.name === 'FindActivate') {
                deliver(data);
                return;
            }
            if (msg && msg.name === 'ShowFloat') {
                var handler = floatHandlers[msg.args && msg.args.kind];
                if (handler) handler((msg.args && msg.args.args) || {});
                return;
            }
            if (msg && msg.name === 'ShowFind') { if (msg.args && msg.args.opt) findOptions = msg.args.opt; showFind(msg.args && msg.args.value, msg.args && msg.args.replace); return; }
            if (msg && msg.name === 'HideFind') { hideFind(); return; }
            if (msg && msg.name === 'Find') { deliver(data); return; }
        } catch (e) { }
        deliver(data);
    };
})();
