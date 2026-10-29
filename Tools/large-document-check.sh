#!/bin/bash
# Three large restored tabs: progressive drawing, tab switching, edits, saves, and external reload.
# Runs under Xvfb with an isolated profile. Usage: Tools/large-document-check.sh [app] [display].
set -eu
HERE=$(cd "$(dirname "$0")/.." && pwd)
APP=${1:-$HERE/Typedown.Uno/bin/Debug/net9.0-desktop/Typedown.Uno}
D=${2:-79}
T=$(mktemp -d /tmp/typedown-large-check.XXXXXX)
cleanup() {
  [ -n "${PID:-}" ] && kill "$PID" 2>/dev/null || true
  [ -n "${WM:-}" ] && kill "$WM" 2>/dev/null || true
  [ -n "${XV:-}" ] && kill "$XV" 2>/dev/null || true
  [ -n "${PID:-}" ] && wait "$PID" 2>/dev/null || true
  [ -n "${WM:-}" ] && wait "$WM" 2>/dev/null || true
  [ -n "${XV:-}" ] && wait "$XV" 2>/dev/null || true
  python3 - "$T" <<'PY'
import shutil, sys
shutil.rmtree(sys.argv[1], ignore_errors=True)
PY
}
trap cleanup EXIT
mkdir -p "$T/home" "$T/data/Typedown.Uno" "$T/run" "$T/docs"
chmod 700 "$T/run"
python3 - "$T" <<'PY'
import json, os, sys
root = sys.argv[1]
docs = []
for name in ('alpha', 'beta', 'gamma'):
    path = os.path.join(root, 'docs', name + '.md')
    text = '# Large ' + name + '\n\n' + ''.join(
        f'## Section {i}\n\nParagraph {i} in {name}: ' + ('document content and a little more text. ' * 3) + '\n\n'
        for i in range(700))
    with open(path, 'w') as f: f.write(text)
    docs.append({'FilePath': path, 'DocumentId': name})
with open(os.path.join(root, 'data', 'Typedown.Uno', 'settings.json'), 'w') as f:
    json.dump({'AllowLocalAutomation': True, 'FileStartupAction': 2, 'RememberPosition': True,
               'ProgressiveRendering': True, 'Language': 'en'}, f)
with open(os.path.join(root, 'data', 'Typedown.Uno', 'session.json'), 'w') as f:
    json.dump({'Documents': docs, 'ActiveIndex': 1}, f)
print('Large documents:', ', '.join(f'{os.path.getsize(d["FilePath"]):,} bytes' for d in docs), flush=True)
PY
Xvfb ":$D" -screen 0 1200x800x24 > "$T/xvfb.log" 2>&1 & XV=$!
sleep 1
DISPLAY=":$D" xfwm4 > "$T/wm.log" 2>&1 & WM=$!
export DOTNET_ROOT=${DOTNET_ROOT:-/root/.dotnet}
export XDG_DATA_HOME="$T/data" XDG_RUNTIME_DIR="$T/run" XDG_CONFIG_HOME="$T/home/.config" DISPLAY=":$D"
"$APP" > "$T/app.log" 2>&1 & PID=$!
SOCK="$T/run/typedown/automation.v1.sock"
for _ in $(seq 1 100); do [ -S "$SOCK" ] && break; sleep 0.5; done
[ -S "$SOCK" ] || { tail -30 "$T/app.log"; echo 'FAIL automation did not start'; exit 1; }
python3 - "$HERE/docs/automation-examples" "$SOCK" "$T" <<'PY'
import json, os, subprocess, sys, time
sys.path.insert(0, sys.argv[1])
from typedown_client import Client
socket_path, root = sys.argv[2:]
client = Client.connect(socket_path=socket_path)
client.initialize(['document.read', 'document.save'], name='large-document-check')
def read(name):
    return client.call('document.get', {'documentId': name, 'consistency': 'latest', 'include': ['text']})['text']
def active(name):
    return next(d for d in client.call('document.list')['documents'] if d['documentId'] == name)['active']
def wait_for(predicate, label, seconds=60):
    until = time.monotonic() + seconds
    while time.monotonic() < until:
        try:
            if predicate(): print('PASS', label, flush=True); return
        except Exception: pass
        time.sleep(.3)
    raise SystemExit('FAIL ' + label)
def key(*keys): subprocess.run(['xdotool', 'key', *keys], check=True)
expected = {name: open(os.path.join(root, 'docs', name + '.md')).read() for name in ('alpha', 'beta', 'gamma')}
wait_for(lambda: len(client.call('document.list')['documents']) == 3, 'three tabs restored')
wait_for(lambda: read('beta') == expected['beta'], 'active large document loaded')
window = subprocess.check_output(['xdotool', 'search', '--name', 'beta.md']).decode().splitlines()[0]
subprocess.run(['xdotool', 'windowactivate', '--sync', window], check=True)
key('alt+1')
wait_for(lambda: active('alpha') and read('alpha') == expected['alpha'], 'first deferred large document loaded')
subprocess.run(['xdotool', 'mousemove', '400', '300', 'click', '1'], check=True)
key('ctrl+End')
time.sleep(1)
subprocess.run(['xdotool', 'type', '--clearmodifiers', 'TAIL-ALPHA'], check=True)
wait_for(lambda: 'TAIL-ALPHA' in read('alpha'), 'typing at end of large document')
key('ctrl+s')
wait_for(lambda: 'TAIL-ALPHA' in open(os.path.join(root, 'docs', 'alpha.md')).read(), 'large document saved')
cursor_file = os.path.join(root, 'data', 'Typedown.Uno', 'cursors.json')
def cursor_saved():
    if not os.path.exists(cursor_file): return False
    item = json.load(open(cursor_file)).get(os.path.join(root, 'docs', 'alpha.md'))
    return bool(item and item.get('Cursor') and (item.get('ScrollY') or 0) > 0)
wait_for(cursor_saved, 'caret and scroll position persisted', 15)
subprocess.run(['xdotool', 'mousemove', '400', '300', 'click', '1'], check=True)
key('ctrl+Home')
time.sleep(.5)
subprocess.run(['import', '-window', 'root', os.path.join(root, 'top.png')], check=True)
key('ctrl+End')
time.sleep(.5)
subprocess.run(['import', '-window', 'root', os.path.join(root, 'bottom.png')], check=True)
from PIL import Image, ImageChops, ImageStat
import shutil
shutil.copy(os.path.join(root, 'top.png'), '/tmp/typedown-large-top.png')
shutil.copy(os.path.join(root, 'bottom.png'), '/tmp/typedown-large-bottom.png')
top = Image.open(os.path.join(root, 'top.png')).convert('RGB').crop((50, 150, 970, 600))
bottom = Image.open(os.path.join(root, 'bottom.png')).convert('RGB').crop((50, 150, 970, 600))
changed = max(ImageStat.Stat(ImageChops.difference(top, bottom)).mean)
assert changed > 1, f'large document screen did not change after Home/End ({changed})'
print('PASS long document scroll shows different content at top and bottom', flush=True)
key('alt+2')
wait_for(lambda: active('beta') and read('beta') == expected['beta'], 'switch back to active large document')
with open(os.path.join(root, 'docs', 'beta.md'), 'a') as f: f.write('\nExternal beta marker.\n')
wait_for(lambda: 'External beta marker.' in read('beta'), 'external file change reloaded')
key('alt+3')
wait_for(lambda: active('gamma') and read('gamma') == expected['gamma'], 'third deferred large document loaded')
key('alt+1')
wait_for(lambda: active('alpha') and 'TAIL-ALPHA' in read('alpha'), 'saved large document survives tab switches')
time.sleep(1)
subprocess.run(['import', '-window', 'root', os.path.join(root, 'restored.png')], check=True)
restored = Image.open(os.path.join(root, 'restored.png')).convert('RGB').crop((50, 150, 970, 600))
restored_change = max(ImageStat.Stat(ImageChops.difference(bottom, restored)).mean)
assert restored_change < changed / 2, f'large document returned at a different scroll position ({restored_change} vs {changed})'
print('PASS large document returns near its previous scroll position', flush=True)
assert open(os.path.join(root, 'docs', 'gamma.md')).read() == expected['gamma'], 'another large file changed'
print('PASS other large document remains unchanged', flush=True)
client.close()
PY
if grep -q 'page error:' "$T/app.log"; then
  grep 'page error:' "$T/app.log"
  echo 'FAIL editor page error'; exit 1
fi
grep 'editor first paint:' "$T/app.log" | tail -4
