#!/bin/bash
# Files dragged in from a file manager (a GTK drag source here, as Thunar or Nautilus are): two images dropped on the
# editor go in at the caret (copied next to the document, as Settings > Images says), a document dropped on the tab
# strip opens, and typing still reaches the editor afterwards (the drop layer over the editor takes no keys).
# Nothing used to happen: the window, Uno's, gets the drop, and over the native web view nothing took it.
#
# Runs the app under Xvfb + xfwm4 in an isolated profile. Needs Xvfb, xfwm4, xdotool, python3 with GTK 3 (gi).
#
#   Tools/drop-check.sh [path to Typedown.Uno] [display number]
set -u
HERE=$(cd "$(dirname "$0")/.." && pwd)
APP=${1:-$HERE/Typedown.Uno/bin/Debug/net9.0-desktop/Typedown.Uno}
D=${2:-83}
T=$(mktemp -d /tmp/typedown-drop-check.XXXXXX)
fail() { echo "FAIL: $*"; echo "--- app log:"; grep -E "drop|Error" "$T/app.log" | tail -8; cleanup; exit 1; }
cleanup() { [ -n "${SRC:-}" ] && kill $SRC 2>/dev/null; [ -n "${PID:-}" ] && kill $PID 2>/dev/null; sleep 1; [ -n "${XV:-}" ] && kill $XV 2>/dev/null; rm -rf "$T"; }

cat > $T/source.py <<'PY'
import sys, gi
gi.require_version('Gtk', '3.0')
from gi.repository import Gtk, Gdk
uris = ['file://' + p for p in sys.argv[1:]]
w = Gtk.Window(title='DragSource'); w.set_default_size(200, 80)
b = Gtk.Button(label='drag me')
b.drag_source_set(Gdk.ModifierType.BUTTON1_MASK, [], Gdk.DragAction.COPY)
b.drag_source_add_uri_targets()
b.connect('drag-data-get', lambda wid, ctx, data, info, t: data.set_uris(uris))
w.add(b); w.connect('destroy', Gtk.main_quit); w.show_all(); Gtk.main()
PY
mkdir -p $T/home $T/data/Typedown.Uno $T/run $T/docs $T/pics && chmod 700 $T/run
printf '{ "AllowLocalAutomation": false, "FileStartupAction": 0, "Language": "en" }' > $T/data/Typedown.Uno/settings.json
printf '# Drop\n\nText\n' > $T/docs/doc.md
printf '# Other\n\nAnother document\n' > $T/docs/other.md
printf '\x89PNG\r\n\x1a\n\x01' > "$T/pics/one.png"  # not real pictures: the editor shows "Failed to Load Image", which is fine here
printf '\x89PNG\r\n\x1a\n\x02' > "$T/pics/two two.png"

Xvfb :$D -screen 0 1100x750x24 >/dev/null 2>&1 & XV=$!; sleep 1
DISPLAY=:$D xfwm4 >/dev/null 2>&1 & sleep 1
export DOTNET_ROOT=${DOTNET_ROOT:-$HOME/.dotnet}
export HOME=$T/home XDG_DATA_HOME=$T/data XDG_RUNTIME_DIR=$T/run XDG_CONFIG_HOME=$T/home/.config DISPLAY=:$D
"$APP" $T/docs/doc.md > $T/app.log 2>&1 & PID=$!
sleep 14
W=$(xdotool search --name "doc.md - Typedown" | head -1); [ -n "$W" ] || fail "no window"
xdotool windowmove $W 0 0; xdotool windowactivate --sync $W; sleep 0.5
# The caret at the end of the document.
xdotool mousemove 300 300 click 1; sleep 0.3; xdotool key ctrl+End; sleep 0.3

drag() { # $1 $2: where to drop; the rest: files
  local x=$1 y=$2; shift 2
  python3 $T/source.py "$@" & SRC=$!; sleep 2
  local S=$(xdotool search --name DragSource | head -1); xdotool windowmove $S 800 560; sleep 0.5
  xdotool mousemove 860 590; sleep 0.3; xdotool mousedown 1; sleep 0.3
  for i in $(seq 1 20); do xdotool mousemove $((860 - i*25)) $((590 - i*15)); sleep 0.05; done
  xdotool mousemove $x $y; sleep 0.6; xdotool mousemove $((x+5)) $((y+2)); sleep 0.6; xdotool mouseup 1; sleep 2
  kill $SRC 2>/dev/null; SRC=; sleep 0.5
}

drag 400 300 "$T/pics/one.png" "$T/pics/two two.png"
xdotool windowactivate --sync $W; sleep 0.3; xdotool key ctrl+s; sleep 1.5
TEXT=$(cat $T/docs/doc.md)
echo "$TEXT" | grep -q "one.png" && echo "$TEXT" | grep -q "two" || { echo "$TEXT"; fail "the dropped images are not in the document"; }
[ "$(echo "$TEXT" | grep -c '!\[')" = 2 ] || { echo "$TEXT"; fail "not two images"; }
ls $T/docs/doc.assets/one.png >/dev/null 2>&1 || fail "the image was not copied next to the document"
echo "PASS: two images dropped on the editor are in the document and copied next to it"

# On the "Text" line (below it are the two image blocks now), at its end.
xdotool mousemove 120 175 click 1; sleep 0.3; xdotool key End; xdotool type --delay 60 "Z"; sleep 0.3; xdotool key ctrl+s; sleep 1.5
grep -q "TextZ" $T/docs/doc.md || fail "typing did not reach the editor after the drop"
echo "PASS: typing still reaches the editor"

drag 300 50 "$T/docs/other.md"
sleep 1.5
NAME=$(xdotool getwindowname $W)
case "$NAME" in *other.md*) echo "PASS: a document dropped on the tab strip opens ($NAME)";; *) fail "the dropped document did not open ($NAME)";; esac
cleanup
