#!/bin/bash
# A picture copied in Chromium ("Copy image": the picture as image/png and an <img> as text/html, no plain text) and
# pasted with Ctrl+V goes into the document: saved next to it, as Settings > Images says, and linked. Nothing happened:
# the paste found no plain text and stopped, and Uno's clipboard on X11 hands out no pictures.
#
# Runs the app under Xvfb + xfwm4 in an isolated profile. Needs Xvfb, xfwm4, xdotool, python3 with GTK 3 (gi).
#
#   Tools/image-paste-check.sh [path to Typedown.Uno] [display number]
set -u
HERE=$(cd "$(dirname "$0")/.." && pwd)
APP=${1:-$HERE/Typedown.Uno/bin/Debug/net9.0-desktop/Typedown.Uno}
D=${2:-85}
T=$(mktemp -d /tmp/typedown-image-paste-check.XXXXXX)
ok=1; pass() { echo "PASS $1"; }; fail() { echo "FAIL $1"; ok=0; }
cleanup() { [ -n "${CB:-}" ] && kill $CB 2>/dev/null; [ -n "${PID:-}" ] && kill $PID 2>/dev/null; sleep 1; [ -n "${XV:-}" ] && kill $XV 2>/dev/null; rm -rf "$T"; }

mkdir -p $T/home $T/data/Typedown.Uno $T/run $T/docs $T/pics && chmod 700 $T/run
printf '{ "AllowLocalAutomation": false, "FileStartupAction": 0, "Language": "en" }' > $T/data/Typedown.Uno/settings.json
printf '# Paste\n\nText\n' > $T/docs/doc.md
python3 -c "import zlib,struct
def chunk(t,d): return struct.pack('>I',len(d))+t+d+struct.pack('>I',zlib.crc32(t+d))
raw=b''.join(b'\x00'+b'\x00\x80\xff'*40 for _ in range(30))
open('$T/pics/copied.png','wb').write(b'\x89PNG\r\n\x1a\n'+chunk(b'IHDR',struct.pack('>IIBBBBB',40,30,8,2,0,0,0))+chunk(b'IDAT',zlib.compress(raw))+chunk(b'IEND',b''))"

Xvfb :$D -screen 0 1100x750x24 >/dev/null 2>&1 & XV=$!; sleep 1
DISPLAY=:$D xfwm4 >/dev/null 2>&1 & sleep 1
export DOTNET_ROOT=${DOTNET_ROOT:-$HOME/.dotnet}
export HOME=$T/home XDG_DATA_HOME=$T/data XDG_RUNTIME_DIR=$T/run XDG_CONFIG_HOME=$T/home/.config DISPLAY=:$D
"$APP" $T/docs/doc.md > $T/app.log 2>&1 & PID=$!
sleep 14
W=$(xdotool search --name "doc.md - Typedown" | head -1)
[ -n "$W" ] || { echo "FAIL no window"; tail -15 $T/app.log; cleanup; exit 1; }
xdotool windowmove $W 0 0; xdotool windowactivate --sync $W; sleep 0.5

python3 "$HERE/Tools/chromium-image-clipboard.py" $T/pics/copied.png > $T/cb.log 2>&1 & CB=$!; sleep 2
xdotool mousemove 300 300 click 1; sleep 0.3; xdotool key ctrl+End; sleep 0.3; xdotool key Return; sleep 0.3
xdotool key ctrl+v; sleep 4
xdotool key ctrl+s; sleep 1.5
TEXT=$(cat $T/docs/doc.md)
LINK=$(printf '%s' "$TEXT" | sed -n 's/.*!\[[^]]*\](\([^)]*\)).*/\1/p' | head -1)
if [ -n "$LINK" ] && [ -f "$T/docs/$(printf '%s' "$LINK" | sed 's/%20/ /g')" ]; then
  cmp -s "$T/docs/$(printf '%s' "$LINK" | sed 's/%20/ /g')" $T/pics/copied.png && pass "the picture copied in Chromium is pasted: saved next to the document ($LINK) and linked" \
    || pass "the picture copied in Chromium is pasted: saved next to the document ($LINK, re-encoded) and linked"
else
  fail "nothing pasted: $(printf '%s' "$TEXT" | tr '\n' '|'); log: $(grep -iE 'clipboard|paste|image' $T/app.log | tail -3 | tr '\n' '|')"
fi

echo "OVERALL $([ $ok = 1 ] && echo PASS || echo FAIL)"
cleanup
[ $ok = 1 ]
