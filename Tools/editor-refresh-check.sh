#!/bin/bash
# A restored session shows both tabs but reads only the active file. An automation read of the other tab still gives
# its disk text. Switching to it, typing, undo/redo and saving must preserve the original document. An automation
# edit of another deferred tab must load the disk text before changing it. Covers FileLoaded {same:true} and history.
#
# Runs under Xvfb + xfwm4 in an isolated profile. Needs Xvfb, xfwm4, xdotool and python3.
#
#   Tools/editor-refresh-check.sh [path to Typedown.Uno] [display number]
set -eu
HERE=$(cd "$(dirname "$0")/.." && pwd)
APP=${1:-$HERE/Typedown.Uno/bin/Debug/net9.0-desktop/Typedown.Uno}
D=${2:-79}
T=$(mktemp -d /tmp/typedown-editor-refresh.XXXXXX)
cleanup() {
  [ -n "${PID:-}" ] && kill "$PID" 2>/dev/null || true
  [ -n "${WM:-}" ] && kill "$WM" 2>/dev/null || true
  [ -n "${XV:-}" ] && kill "$XV" 2>/dev/null || true
  [ -n "${PID:-}" ] && wait "$PID" 2>/dev/null || true
  [ -n "${WM:-}" ] && wait "$WM" 2>/dev/null || true
  [ -n "${XV:-}" ] && wait "$XV" 2>/dev/null || true
  rm -rf "$T"
}
trap cleanup EXIT
mkdir -p "$T/home" "$T/data/Typedown.Uno" "$T/run" "$T/docs"
chmod 700 "$T/run"
printf '# Alpha\n\nOriginal alpha.\n' > "$T/docs/alpha.md"
printf '# Beta\n\nOriginal beta.\n' > "$T/docs/beta.md"
printf '# Gamma\n\nOriginal gamma.\n' > "$T/docs/gamma.md"
python3 - "$T" <<'PY'
import json, os, sys
root = sys.argv[1]
data = os.path.join(root, 'data', 'Typedown.Uno')
with open(os.path.join(data, 'settings.json'), 'w') as f:
    json.dump({'AllowLocalAutomation': True, 'FileStartupAction': 2, 'Language': 'en'}, f)
with open(os.path.join(data, 'session.json'), 'w') as f:
    json.dump({'Documents': [
        {'FilePath': os.path.join(root, 'docs', 'alpha.md'), 'DocumentId': 'alpha'},
        {'FilePath': os.path.join(root, 'docs', 'beta.md'), 'DocumentId': 'beta'},
        {'FilePath': os.path.join(root, 'docs', 'gamma.md'), 'DocumentId': 'gamma'},
    ], 'ActiveIndex': 1}, f)
PY

Xvfb ":$D" -screen 0 1200x800x24 > "$T/xvfb.log" 2>&1 & XV=$!
sleep 1
DISPLAY=":$D" xfwm4 > "$T/wm.log" 2>&1 & WM=$!
export DOTNET_ROOT=${DOTNET_ROOT:-/root/.dotnet}
export XDG_DATA_HOME="$T/data" XDG_RUNTIME_DIR="$T/run" XDG_CONFIG_HOME="$T/home/.config" DISPLAY=":$D"
"$APP" > "$T/app.log" 2>&1 & PID=$!
SOCK="$T/run/typedown/automation.v1.sock"
for _ in $(seq 1 60); do [ -S "$SOCK" ] && break; sleep 0.5; done
[ -S "$SOCK" ] || { tail -30 "$T/app.log"; echo 'FAIL automation did not start'; exit 1; }

python3 - "$HERE/docs/automation-examples" "$SOCK" "$T" <<'PY'
import os, subprocess, sys, time
sys.path.insert(0, sys.argv[1])
from typedown_client import Client

socket_path, root = sys.argv[2:]
client = Client.connect(socket_path=socket_path)
client.initialize(['document.read', 'document.write', 'document.save'], name='editor-refresh-check')

def read(doc):
    return client.call('document.get', {'documentId': doc, 'consistency': 'latest', 'include': ['text']})['text']

def wait_for(predicate, label, seconds=15):
    until = time.monotonic() + seconds
    while time.monotonic() < until:
        try:
            if predicate():
                print('PASS', label, flush=True)
                return
        except Exception:
            pass
        time.sleep(.25)
    raise SystemExit('FAIL ' + label)

def key(*keys):
    subprocess.run(['xdotool', 'key', *keys], check=True)

wait_for(lambda: len(client.call('document.list')['documents']) == 3, 'all session tabs are visible')
wait_for(lambda: read('beta') == '# Beta\n\nOriginal beta.\n', 'active load keeps its text')
wait_for(lambda: read('alpha') == '# Alpha\n\nOriginal alpha.\n', 'deferred tab is readable through automation')
window = subprocess.check_output(['xdotool', 'search', '--name', 'Beta']).decode().splitlines()[0]
subprocess.run(['xdotool', 'windowactivate', '--sync', window], check=True)
key('alt+1')
wait_for(lambda: next(d for d in client.call('document.list')['documents'] if d['documentId'] == 'alpha')['active'], 'first switch loads deferred tab')
wait_for(lambda: read('alpha') == '# Alpha\n\nOriginal alpha.\n', 'first switch keeps the file text')
subprocess.run(['xdotool', 'mousemove', '350', '300', 'click', '1'], check=True)
key('ctrl+End')
subprocess.run(['xdotool', 'type', '--clearmodifiers', 'X'], check=True)
wait_for(lambda: 'X' in read('alpha'), 'typing reaches restored tab')
key('ctrl+z')
wait_for(lambda: read('alpha') == '# Alpha\n\nOriginal alpha.\n', 'editor undo uses document history')
key('ctrl+y')
wait_for(lambda: 'X' in read('alpha'), 'editor redo uses document history')
key('ctrl+s')
wait_for(lambda: 'X' in open(os.path.join(root, 'docs', 'alpha.md')).read(), 'save writes edited text')
saved_alpha = open(os.path.join(root, 'docs', 'alpha.md')).read()
assert open(os.path.join(root, 'docs', 'beta.md')).read() == '# Beta\n\nOriginal beta.\n', 'other tab changed'
print('PASS other file remains unchanged', flush=True)
gamma = client.call('document.get', {'documentId': 'gamma', 'consistency': 'latest', 'include': ['text']})
assert gamma['text'] == '# Gamma\n\nOriginal gamma.\n', 'deferred automation read lost the disk text'
edited = client.call('document.replaceText', {'documentId': 'gamma', 'baseRevision': gamma['revision'],
    'find': 'Original gamma', 'replacement': 'Automated gamma', 'expectedCount': 1})
wait_for(lambda: read('gamma') == '# Gamma\n\nAutomated gamma.\n', 'automation edit loads deferred text before writing')
wait_for(lambda: next(d for d in client.call('document.list')['documents'] if d['documentId'] == 'gamma')['active'], 'deferred automation edit activates the tab')
client.call('document.save', {'documentId': 'gamma', 'baseRevision': edited['revision']})
wait_for(lambda: open(os.path.join(root, 'docs', 'gamma.md')).read() == '# Gamma\n\nAutomated gamma.\n', 'automation save writes the intended tab')
assert open(os.path.join(root, 'docs', 'alpha.md')).read() == saved_alpha, 'automation edit changed the previous tab'
print('PASS previous tab remains unchanged after automation edit', flush=True)
client.close()
PY
if grep -q 'editor handshake done (unchanged)' "$T/app.log"; then
  echo 'PASS unchanged load used the compact FileLoaded handshake'
else
  echo 'FAIL no compact FileLoaded handshake was observed'
  tail -25 "$T/app.log"
  exit 1
fi
