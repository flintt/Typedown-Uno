#!/bin/bash
# Reading mode, with real keys and the page's own context menu: Ctrl+Z after an edit changes nothing (the history's
# undo replaced the text there), Ctrl+A selects the document, Copy puts the Markdown on the clipboard and Copy as plain
# text the text without it. The menu's rows are clicked where they are drawn: Copy, Copy as plain text, then Select all.
#
# Runs the app under Xvfb + xfwm4 in an isolated profile. Needs Xvfb, xfwm4, xdotool, xclip.
#
#   Tools/reading-copy-check.sh [path to Typedown.Uno] [display number]
set -u
HERE=$(cd "$(dirname "$0")/.." && pwd)
APP=${1:-$HERE/Typedown.Uno/bin/Debug/net9.0-desktop/Typedown.Uno}
D=${2:-84}
T=$(mktemp -d /tmp/typedown-reading-copy-check.XXXXXX)
ok=1; pass() { echo "PASS $1"; }; fail() { echo "FAIL $1"; ok=0; }
cleanup() { [ -n "${PID:-}" ] && kill $PID 2>/dev/null; sleep 1; [ -n "${XV:-}" ] && kill $XV 2>/dev/null; rm -rf "$T"; }

mkdir -p $T/home $T/data/Typedown.Uno $T/run $T/docs && chmod 700 $T/run
printf '{ "AllowLocalAutomation": false, "FileStartupAction": 0, "Language": "en" }' > $T/data/Typedown.Uno/settings.json
printf '# Title **bold**\n\nAlpha *beta* ![p](pic.png).\n\n1. one\n2. two\n' > $T/docs/doc.md
printf '\x89PNG\r\n\x1a\n\x00\x00\x00\rIHDR\x00\x00\x00\x01\x00\x00\x00\x01\x08\x06\x00\x00\x00\x1f\x15\xc4\x89\x00\x00\x00\rIDATx\xdac\xfc\xcf\xc0P\x0f\x00\x04\x85\x01\x80\x84\xa9\x8c!\x00\x00\x00\x00IEND\xaeB`\x82' > $T/docs/pic.png

Xvfb :$D -screen 0 1100x750x24 >/dev/null 2>&1 & XV=$!; sleep 1
DISPLAY=:$D xfwm4 >/dev/null 2>&1 & sleep 1
export DOTNET_ROOT=${DOTNET_ROOT:-$HOME/.dotnet}
export HOME=$T/home XDG_DATA_HOME=$T/data XDG_RUNTIME_DIR=$T/run XDG_CONFIG_HOME=$T/home/.config DISPLAY=:$D
"$APP" $T/docs/doc.md > $T/app.log 2>&1 & PID=$!
sleep 14
W=$(xdotool search --name "doc.md - Typedown" | head -1)
[ -n "$W" ] || { echo "FAIL no window"; tail -15 $T/app.log; cleanup; exit 1; }
xdotool windowmove $W 0 0; xdotool windowactivate --sync $W; sleep 0.5

# An edit in the visual editor, then reading mode (Ctrl+Shift+R) and Ctrl+Z, then save.
xdotool mousemove 300 300 click 1; sleep 0.3; xdotool key ctrl+End; sleep 0.3; xdotool type X; sleep 0.8
xdotool key ctrl+shift+r; sleep 1.5
xdotool key ctrl+z; sleep 1; xdotool key ctrl+s; sleep 1.5
case "$(cat $T/docs/doc.md)" in *X*) pass "Ctrl+Z in reading mode leaves the document as it was";; *) fail "Ctrl+Z in reading mode undid the edit: $(cat $T/docs/doc.md | tr '\n' '|')";; esac

# Ctrl+A, then the context menu over the text.
menu() { # $1: row to click (1 Copy, 2 Copy as plain text)
  # xclip stays to serve what it was given until another program takes the clipboard: it must not hold this
  # function's output open (a run where the app never copies would wait for it for good).
  printf '' | xclip -selection clipboard >/dev/null 2>&1
  xdotool key ctrl+a; sleep 0.5
  xdotool mousemove 200 160 click 3; sleep 0.8
  xdotool mousemove 240 $((160 + 4 + ($1 - 1) * 29 + 14)) click 1; sleep 1
  timeout 5 xclip -o -selection clipboard 2>/dev/null
}
COPY=$(menu 1)
case "$COPY" in *'**bold**'*Alpha*) pass "Copy gives the Markdown";; *) fail "Copy gave $(printf '%s' "$COPY" | head -c 200 | tr '\n' '|')";; esac
PLAIN=$(menu 2)
EXPECTED=$(printf 'Title bold\n\nAlpha beta p.\n\n1. one\n2. twoX')
[ "$PLAIN" = "$EXPECTED" ] && pass "Copy as plain text leaves the Markdown out" || fail "Copy as plain text gave $(printf '%s' "$PLAIN" | tr '\n' '|')"

echo "OVERALL $([ $ok = 1 ] && echo PASS || echo FAIL)"
cleanup
[ $ok = 1 ]
