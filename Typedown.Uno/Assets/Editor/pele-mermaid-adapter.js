// Uno experiment: choose Mermaid or Pele for Mermaid fences without changing the shared editor bundle.
// Both engines are loaded on demand. The wrapper is stable because the editor caches window.mermaid.
(function () {
    'use strict';

    var peleLoading = null;
    var mermaidLoading = null;
    var mermaidEngine = null;
    var renderer = 'mermaid';
    var options = { theme: 'default' };
    var nextId = 0;
    var palettes = {
        default: {
            '--pele-bg': '#ffffff', '--pele-fg': '#24292e', '--pele-muted': '#6b7280',
            '--pele-line': '#374151', '--pele-surface': '#f3f4f6', '--pele-surface-alt': '#e5e7eb',
            '--pele-border': '#9ca3af', '--pele-accent': '#2563eb'
        },
        dark: {
            '--pele-bg': '#282828', '--pele-fg': '#f5f5f5', '--pele-muted': '#b0b0b0',
            '--pele-line': '#e5e7eb', '--pele-surface': '#383838', '--pele-surface-alt': '#454545',
            '--pele-border': '#888888', '--pele-accent': '#75b8ff'
        }
    };

    function loadPele() {
        if (window.pele) return Promise.resolve(window.pele);
        if (!peleLoading) {
            peleLoading = new Promise(function (resolve, reject) {
                var script = document.createElement('script');
                script.src = './pele.global.js';
                script.onload = function () {
                    if (window.pele && typeof window.pele.render === 'function') resolve(window.pele);
                    else reject(new Error('Pele script did not expose render()'));
                };
                script.onerror = function () {
                    script.remove();
                    reject(new Error('Pele script could not be loaded'));
                };
                document.head.appendChild(script);
            }).catch(function (error) {
                peleLoading = null;
                throw error;
            });
        }
        return peleLoading;
    }

    function loadMermaid() {
        if (mermaidEngine) return Promise.resolve(mermaidEngine);
        if (!mermaidLoading) {
            mermaidLoading = new Promise(function (resolve, reject) {
                var script = document.createElement('script');
                script.src = './mermaid.min.js';
                script.onload = function () {
                    if (mermaidEngine && typeof mermaidEngine.render === 'function') resolve(mermaidEngine);
                    else reject(new Error('Mermaid script did not expose render()'));
                };
                script.onerror = function () {
                    script.remove();
                    reject(new Error('Mermaid script could not be loaded'));
                };
                document.head.appendChild(script);
            }).catch(function (error) {
                mermaidLoading = null;
                throw error;
            });
        }
        return mermaidLoading;
    }

    function renderSvg(pele, source, id, config, target) {
        var fontTarget = target || document.getElementById('ag-editor-id') || document.body;
        var fontFamily = getComputedStyle(fontTarget).fontFamily || 'sans-serif';
        var prefix = String(id).replace(/[^a-zA-Z0-9_-]/g, '_');
        var result = pele.render(source, {
            idPrefix: prefix,
            fontFamily: fontFamily,
            // Mermaid's securityLevel: strict disables clickable links. Keep that behavior in the experiment.
            links: false
        });
        var holder = document.createElement('div');
        holder.innerHTML = result.svg;
        var svg = holder.firstElementChild;
        if (!svg || svg.localName !== 'svg') throw new Error('Pele returned no SVG');
        var palette = palettes[config.theme === 'dark' ? 'dark' : 'default'];
        // The export and clipboard clone the SVG into an image. Put literal colors on the SVG itself: inherited page
        // variables disappear there, while Pele's SVG still reads the variables after the clone.
        Object.keys(palette).forEach(function (key) { svg.style.setProperty(key, palette[key]); });
        return svg.outerHTML;
    }

    var wrapper = {
        initialize: function (config) {
            options = Object.assign({}, config || {});
            if (renderer === 'mermaid' && mermaidEngine) mermaidEngine.initialize(options);
        },
        render: async function (id, source) {
            var config = options;
            if (renderer === 'mermaid') {
                var mermaid = await loadMermaid();
                mermaid.initialize(config);
                return mermaid.render(id, source);
            }
            var pele = await loadPele();
            return { svg: renderSvg(pele, source, id || 'pele-' + nextId++, config) };
        },
        run: async function (config) {
            if (renderer === 'mermaid') {
                var mermaid = await loadMermaid();
                mermaid.initialize(options);
                return mermaid.run(config);
            }
            var settings = options;
            var nodes = config && config.nodes ? Array.from(config.nodes) : Array.from(document.querySelectorAll('.mermaid'));
            if (!nodes.length) return;
            var pele = await loadPele();
            for (var node of nodes) {
                var source = node.textContent;
                node.innerHTML = renderSvg(pele, source, 'pele-' + nextId++, settings, node);
            }
        }
    };

    // Mermaid's UMD bundle assigns globalThis.mermaid. Capture the real engine while keeping the editor's
    // already cached reference to this wrapper, including after a setting change.
    Object.defineProperty(window, 'mermaid', {
        configurable: true,
        enumerable: true,
        get: function () { return wrapper; },
        set: function (value) { if (value !== wrapper) mermaidEngine = value; }
    });

    window.__unoSetMermaidRenderer = function (value) {
        var next = value === 'pele' ? 'pele' : 'mermaid';
        if (next === renderer) return;
        renderer = next;
        var muya = window.__typedownMuya;
        var state = muya && muya.contentState;
        if (!state || !state.stateRender) return;
        // The editor caches SVGs by theme and source, so changing engines must clear those caches and redraw.
        var render = state.stateRender;
        render.mermaidRenderCache.clear();
        render.mermaidRenderTargets = new WeakMap();
        render.mermaidRenderRequests = new WeakMap();
        state.render(true, true);
    };
})();
