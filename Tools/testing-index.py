#!/usr/bin/env python3
"""The list of desktop checks in docs/testing.md comes from the Tools/*-check.sh scripts themselves: the first
sentence of the comment each one starts with.

    python3 Tools/testing-index.py           # fails, saying what differs, when the document is behind the scripts
    python3 Tools/testing-index.py --write   # brings the document up to date

The list sits between <!-- BEGIN generated: checks --> and <!-- END generated: checks -->; the rest of the
document is written by hand. A check script without a comment at its top is an error.
"""
import difflib, glob, os, re, sys

ROOT = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
DOC = "docs/testing.md"


def checks(errors):
    rows = []
    for path in sorted(glob.glob(os.path.join(ROOT, "Tools", "*-check.sh"))):
        name = os.path.basename(path)
        comment = []
        with open(path, encoding="utf-8") as f:
            for line in f.read().split("\n")[1:]:
                if not line.startswith("#"):
                    break
                text = line[1:].strip()
                if not text:
                    break
                comment.append(text)
        if not comment:
            errors.append(f"Tools/{name}: no comment at the top saying what it checks")
            continue
        sentence = re.split(r"(?<=\.)\s", " ".join(comment), maxsplit=1)[0]
        rows.append(f"- `{name}`: {sentence}")
    return rows


def main():
    errors = []
    rows = checks(errors)
    if errors:
        for e in errors:
            print("ERROR " + e)
        return 1
    with open(os.path.join(ROOT, DOC), encoding="utf-8") as f:
        doc = f.read()
    pattern = re.compile(r"(<!-- BEGIN generated: checks -->\n).*?(<!-- END generated: checks -->)", re.S)
    if not pattern.search(doc):
        print(f"ERROR {DOC}: no BEGIN/END generated: checks markers")
        return 1
    updated = pattern.sub(lambda m: m.group(1) + "\n".join(rows) + "\n" + m.group(2), doc)
    if updated == doc:
        print(f"OK {DOC} matches the scripts")
        return 0
    if "--write" in sys.argv[1:]:
        with open(os.path.join(ROOT, DOC), "w", encoding="utf-8", newline="\n") as f:
            f.write(updated)
        print(f"WROTE {DOC}")
        return 0
    print(f"FAIL {DOC} is behind the scripts; run python3 Tools/testing-index.py --write")
    sys.stdout.writelines(difflib.unified_diff(doc.splitlines(True), updated.splitlines(True), DOC, DOC + " (from the scripts)", n=0))
    return 1


if __name__ == "__main__":
    sys.exit(main())
