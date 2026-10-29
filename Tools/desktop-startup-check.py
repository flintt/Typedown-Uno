#!/usr/bin/env python3
"""Start a packaged Windows or macOS app and require the real editor WebView handshake."""

import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import time


def data_folders():
    home = Path.home()
    if sys.platform == "win32":
        return [Path(os.environ["LOCALAPPDATA"]) / "Typedown.Uno"]
    return [home / ".local/share/Typedown.Uno", home / "Library/Application Support/Typedown.Uno"]


def main():
    executable = Path(sys.argv[1]).resolve()
    assert executable.is_file(), executable
    with tempfile.TemporaryDirectory(prefix="typedown-startup-") as temp:
        sample = Path(temp) / "pele-smoke.md"
        sample.write_text("# Pele smoke\n\n```mermaid\ngraph TD\nA-->B\n```\n", encoding="utf-8")
        folders = data_folders()
        # GitHub-hosted runners start with a fresh profile. Set Pele before the editor reads settings.
        for folder in folders:
            folder.mkdir(parents=True, exist_ok=True)
            (folder / "settings.json").write_text(json.dumps({"MermaidRenderer": "pele"}), encoding="utf-8")
            (folder / "debug.log").unlink(missing_ok=True)
        output = Path(temp) / "app.log"
        with output.open("w", encoding="utf-8") as stream:
            process = subprocess.Popen([str(executable), str(sample)], stdout=stream, stderr=subprocess.STDOUT)
            try:
                deadline = time.monotonic() + 70
                while time.monotonic() < deadline:
                    for folder in folders:
                        log = folder / "debug.log"
                        if log.exists() and "editor handshake done" in log.read_text(errors="replace"):
                            print(f"PASS packaged app opened a document and completed the editor handshake: {log}")
                            return 0
                    if process.poll() is not None:
                        break
                    time.sleep(1)
                print("FAIL packaged app did not complete the editor handshake", file=sys.stderr)
                for folder in folders:
                    log = folder / "debug.log"
                    if log.exists():
                        print(log.read_text(errors="replace")[-6000:], file=sys.stderr)
                print(output.read_text(errors="replace")[-6000:], file=sys.stderr)
                return 1
            finally:
                if process.poll() is None:
                    process.terminate()
                    try:
                        process.wait(timeout=8)
                    except subprocess.TimeoutExpired:
                        process.kill()
                        process.wait(timeout=8)


if __name__ == "__main__":
    sys.exit(main())
