#!/bin/bash
# Refuse to ship an editor page that cannot talk to the host, or that points at a file that is not there.
set -eu
HERE=$(cd "$(dirname "$0")/.." && pwd)
DEST=${1:-$HERE/Typedown.Uno/Assets/Editor}
fail=0

grep -q 'uno-bridge\.js' "$DEST/index.html" || {
  echo "index.html does not load uno-bridge.js — the host cannot post to the editor and it stays blank" >&2; fail=1; }
[ -f "$DEST/uno-bridge.js" ] || { echo "uno-bridge.js is missing" >&2; fail=1; }
# This branch selects either renderer through the local adapter; both scripts must ship.
grep -q 'src="\./pele-mermaid-adapter\.js"' "$DEST/index.html" &&
  ! grep -Eq 'src="\./(lazy-mermaid|mermaid\.min|uno-lazy-mermaid)\.js"' "$DEST/index.html" || {
  echo "index.html must load the Pele adapter in place of Mermaid" >&2; fail=1; }
for f in pele-mermaid-adapter.js pele.global.js pele.LICENSE.txt mermaid.min.js; do [ -f "$DEST/$f" ] || { echo "$f is missing" >&2; fail=1; }; done
for f in lazy-mermaid.js uno-lazy-mermaid.js; do [ ! -e "$DEST/$f" ] || { echo "$f should not be in the Pele experiment" >&2; fail=1; }; done

for ref in $(grep -o 'static/[a-z]*/main\.[a-f0-9]*\.\(js\|css\)' "$DEST/index.html" | sort -u); do
  [ -f "$DEST/$ref" ] || { echo "index.html references $ref, which is not in the package" >&2; fail=1; }
done

# Every UI string the editor's floats/tooltips need must be provided by the host, or it ships untranslated.
if command -v python3 >/dev/null 2>&1 && [ -f "$HERE/Tools/check-editor-strings.py" ]; then
  python3 "$HERE/Tools/check-editor-strings.py" || fail=1
fi

[ $fail -eq 0 ] && echo "editor assets look right: bridge loaded, every referenced file present"
exit $fail
