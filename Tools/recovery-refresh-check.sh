#!/bin/bash
# A dirty restored document writes a crash backup. After a forced process exit, accepting recovery restores the
# unsaved text without overwriting the original file. Runs under Xvfb + xfwm4 with an isolated XDG profile;
# needs xdotool, ImageMagick, and python3.
#
#   Tools/recovery-refresh-check.sh [path to Typedown.Uno] [display number]
set -eu
REPO=$(cd "$(dirname "$0")/.." && pwd)
APP=${1:-$REPO/Typedown.Uno/bin/Debug/net9.0-desktop/Typedown.Uno}
T=$(mktemp -d /tmp/tdu-recovery.XXXXXX)
D=${2:-95}
cleanup() {
  status=$?
  [ -n "${PID:-}" ] && kill "$PID" 2>/dev/null || true
  [ -n "${WM:-}" ] && kill "$WM" 2>/dev/null || true
  [ -n "${XV:-}" ] && kill "$XV" 2>/dev/null || true
  [ -n "${PID:-}" ] && wait "$PID" 2>/dev/null || true
  [ -n "${WM:-}" ] && wait "$WM" 2>/dev/null || true
  [ -n "${XV:-}" ] && wait "$XV" 2>/dev/null || true
  if [ "$status" -eq 0 ]; then rm -rf "$T"; else printf 'failure artifacts=%s\n' "$T"; tail -30 "$T/app2.log" 2>/dev/null || true; fi
}
trap cleanup EXIT
mkdir -p "$T/data/Typedown.Uno" "$T/config" "$T/run" "$T/docs"
chmod 700 "$T/run"
printf '# Recovery\n\nOriginal.\n' > "$T/docs/recovery.md"
python3 - "$T" <<'PY'
import json,os,sys
root=sys.argv[1]; data=os.path.join(root,'data','Typedown.Uno')
with open(os.path.join(data,'settings.json'),'w') as f: json.dump({'AllowLocalAutomation':True,'AutoSave':False,'Language':'en','FileStartupAction':2},f)
with open(os.path.join(data,'session.json'),'w') as f: json.dump({'Documents':[{'FilePath':os.path.join(root,'docs','recovery.md'),'DocumentId':'recovery'}],'ActiveIndex':0},f)
PY
Xvfb ":$D" -screen 0 1200x800x24 > "$T/xvfb.log" 2>&1 & XV=$!
sleep 1
DISPLAY=":$D" xfwm4 > "$T/wm.log" 2>&1 & WM=$!
export XDG_DATA_HOME="$T/data" XDG_CONFIG_HOME="$T/config" XDG_RUNTIME_DIR="$T/run" DISPLAY=":$D" DOTNET_ROOT=/root/.dotnet
"$APP" > "$T/app1.log" 2>&1 & PID=$!
sleep 7
xdotool mousemove 350 300 click 1 key ctrl+End type --clearmodifiers X
python3 - "$REPO/docs/automation-examples" "$T" <<'PY'
import glob,os,sys,time
sys.path.insert(0,sys.argv[1]); from typedown_client import Client
root=sys.argv[2]; sock=os.path.join(root,'run','typedown','automation.v1.sock')
c=Client.connect(socket_path=sock); c.initialize(['document.read'],name='recovery-trial')
for _ in range(60):
  text=c.call('document.get',{'documentId':'recovery','consistency':'latest','include':['text']})['text']
  backups=glob.glob(os.path.join(root,'data','Typedown.Uno','Backup','*.md'))
  if 'X' in text and backups and 'X' in open(backups[0]).read():
    print('PASS unsaved text and backup',flush=True); break
  time.sleep(.3)
else: raise SystemExit('FAIL backup not written')
c.close()
PY
kill -9 "$PID" 2>/dev/null || true
wait "$PID" 2>/dev/null || true
PID=
"$APP" > "$T/app2.log" 2>&1 & PID=$!
prompt_ready=false
for _ in $(seq 1 40); do
  import -window root "$T/prompt.png"
  shade=$(convert "$T/prompt.png" -format '%[fx:int(255*p{100,200}.r)]' info:)
  button=$(convert "$T/prompt.png" -format '%[fx:int(255*p{393,412}.r)]' info:)
  if [ "$shade" -gt 130 ] && [ "$shade" -lt 210 ] && [ "$button" -gt 225 ]; then prompt_ready=true; break; fi
  sleep 0.5
done
if [ "$prompt_ready" != true ]; then echo 'FAIL recovery dialog did not appear'; exit 1; fi
xdotool mousemove 393 412 click 1
sleep 2
python3 - "$REPO/docs/automation-examples" "$T" <<'PY'
import os,sys,time
sys.path.insert(0,sys.argv[1]); from typedown_client import Client
root=sys.argv[2]; sock=os.path.join(root,'run','typedown','automation.v1.sock')
c=Client.connect(socket_path=sock); c.initialize(['document.read'],name='recovery-trial')
for _ in range(40):
 text=c.call('document.get',{'documentId':'recovery','consistency':'latest','include':['text']})['text']
 if 'X' in text:
  print('PASS recovered text',repr(text),flush=True); break
 time.sleep(.25)
else: raise SystemExit('FAIL text not recovered')
assert open(os.path.join(root,'docs','recovery.md')).read()=='# Recovery\n\nOriginal.\n','disk was overwritten before save'
print('PASS disk content unchanged before save',flush=True)
c.close()
PY
