// Uno experiment: keep the bundled editor's Mermaid API while rendering its Mermaid fences with Pele.
// The renderer is loaded on the first diagram, as Mermaid was. This file belongs to Uno only; the shared editor
// source and the Windows editions still use Mermaid.
(function () {
    'use strict';

    var loading = null;
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
        if (!loading) {
            loading = new Promise(function (resolve, reject) {
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
                loading = null;
                throw error;
            });
        }
        return loading;
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

    window.mermaid = {
        initialize: function (config) {
            options = Object.assign({}, config || {});
        },
        render: async function (id, source) {
            var config = options;
            var pele = await loadPele();
            return { svg: renderSvg(pele, source, id || 'pele-' + nextId++, config) };
        },
        run: async function (config) {
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
})();
