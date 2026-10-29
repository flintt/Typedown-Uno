#!/bin/bash
# Copy the editor bundle from the Windows repository into Assets/Editor.
#
# Do this with the script, not by hand. The bundle's index.html carries one line that only exists here —
# the tag that loads uno-bridge.js, which is what the host posts messages through — and copying the
# Windows index.html over it drops that line. Nothing then reaches the editor and it stays blank, while
# the app still logs "editor page loaded". That shipped in five packages before anyone noticed.
set -eu
FROM=${1:-../typedown/Dev/Typedown/Resources/Statics}
HERE=$(cd "$(dirname "$0")/.." && pwd)
DEST=$HERE/Typedown.Uno/Assets/Editor

[ -f "$FROM/index.html" ] || { echo "no bundle at $FROM" >&2; exit 1; }

cp -a "$FROM/." "$DEST/"
rm -f "$DEST"/static/js/*.map "$DEST"/static/css/*.map

# The experimental branch keeps the Windows editor bundle but replaces its Mermaid global with the local Pele adapter.
# The adapter itself loads Pele only on the first diagram. Keep it before both the Uno bridge and the editor bundle.
python3 - "$DEST/index.html" <<'PY'
import sys
p = sys.argv[1]
s = open(p, encoding='utf-8').read()
adapter = '<script src="./pele-mermaid-adapter.js"></script>'
for old in ('<script src="./lazy-mermaid.js"></script>', '<script src="./mermaid.min.js"></script>', '<script src="./uno-lazy-mermaid.js"></script>'):
    if old in s:
        s = s.replace(old, adapter, 1)
        print('  Pele adapter loads on demand')
if adapter not in s:
    raise SystemExit('index.html does not load a known diagram renderer')
if 'uno-bridge.js' not in s:
    s = s.replace(adapter, adapter + '<script src="./uno-bridge.js"></script>', 1)
    print('  put the uno-bridge tag back')
open(p, 'w', encoding='utf-8').write(s)
PY

rm -f "$DEST/lazy-mermaid.js" "$DEST/mermaid.min.js" "$DEST/uno-lazy-mermaid.js"

# Drop bundles the page no longer references.
keep=$(grep -o 'main\.[a-f0-9]*\.\(js\|css\)' "$DEST/index.html" | sort -u)
for f in "$DEST"/static/js/main.*.js "$DEST"/static/css/main.*.css; do
  [ -e "$f" ] || continue
  base=$(basename "$f")
  echo "$keep" | grep -q "^$base$" || { rm -f "$f" "$f.LICENSE.txt"; echo "  removed superseded $base"; }
done

bash "$HERE/packaging/check-editor-assets.sh"
echo "editor assets synced from $FROM"
