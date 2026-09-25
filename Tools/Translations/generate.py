#!/usr/bin/env python3
"""Regenerates the localization tables from Tools/Translations/tables/<lang>.json.

    python3 Tools/Translations/generate.py
    python3 Tools/Translations/generate.py --check   # exit 1 if anything is out of sync (CI)

en.json is the reference: English is built into Loc.cs and is the fallback for every key a
table does not carry, so a table only needs the strings that differ. A key that is not in
en.json is a typo and stops the run.
"""
import json
import re, os, glob, sys

HERE = os.path.dirname(os.path.abspath(__file__))
TABLES = os.path.join(HERE, 'tables')
OUT = os.path.join(HERE, '..', '..', 'Typedown.Uno', 'Services', 'Locales')
# display name shown in the settings dropdown, in its own language
NAMES = {
    'zh-Hans': '中文（简体）', 'zh-Hant': '中文（繁體）', 'ja': '日本語', 'ko': '한국어',
    'de': 'Deutsch', 'fr': 'Français', 'es': 'Español', 'it': 'Italiano',
    'pt': 'Português', 'ru': 'Русский',
}


def identifier(lang):
    return lang.replace('-', '')


def write(path, text, check, stale):
    """Writes the file, or under --check only reports whether it would have changed."""
    current = open(path, encoding='utf-8').read() if os.path.exists(path) else None
    if check:
        if current != text:
            stale.add(os.path.basename(path))
        return False
    if current != text:
        open(path, 'w', encoding='utf-8').write(text)
    return True


def main():
    # --check writes nothing and fails if the generated files differ from what the tables say. Hand-editing
    # a Locales/*.cs file is invisible until the next run of this script silently throws the edit away —
    # which is how 27 translations per language were once lost.
    check = '--check' in sys.argv
    reference = json.load(open(os.path.join(TABLES, 'en.json'), encoding='utf-8'))
    # English lives in Loc.cs and is the fallback for every language, so a key that never reached it shows up
    # as its own name in the interface — which is how "ThemeDocument" once appeared in a menu.
    loc = open(os.path.join(os.path.dirname(TABLES), '..', '..', 'Typedown.Uno', 'Services', 'Loc.cs'), encoding='utf-8').read()
    in_loc = set(re.findall(r'\["([A-Za-z0-9_]+)"\]\s*=', loc))
    absent = [k for k in reference if k not in in_loc]
    if absent:
        raise SystemExit('these keys are missing from the English fallback in Loc.cs: ' + ', '.join(absent))
    # And the other way round, which is the one that bit: a string added to Loc.cs but never to en.json
    # cannot be translated at all — no table may carry a key the reference does not have — so it shows in
    # English whatever the language is set to, and nothing says so.
    untranslatable = [k for k in in_loc if k not in reference]
    if untranslatable:
        raise SystemExit('these keys are in Loc.cs but not in tables/en.json, so no language can carry '
                         'them: ' + ', '.join(untranslatable))
    langs = sorted(os.path.basename(p)[:-5] for p in glob.glob(os.path.join(TABLES, '*.json')))
    langs = [l for l in langs if l != 'en']
    os.makedirs(OUT, exist_ok=True)
    stale = set()
    for lang in langs:
        strings = json.load(open(os.path.join(TABLES, lang + '.json'), encoding='utf-8'))
        unknown = [k for k in strings if k not in reference]
        if unknown:
            print(f"{lang}: unknown keys {unknown}", file=sys.stderr)
            return 1
        body = ''.join(f'        ["{k}"] = "{v}",\n' for k, v in strings.items())
        text = ("using System.Collections.Generic;\n\nnamespace Typedown.Uno.Services;\n\n"
                "public static partial class LocaleTables\n{\n"
                f"    internal static readonly Dictionary<string, string> {identifier(lang)} = new()\n    {{\n{body}    }};\n}}\n")
        if not write(os.path.join(OUT, lang + '.cs'), text, check, stale):
            continue
        missing = len(reference) - len(strings)
        print(f"{lang}: {len(strings)}/{len(reference)}" + (f" ({missing} fall back to English)" if missing else " complete"))
    registry = ''.join(f'        ["{l}"] = {identifier(l)},\n' for l in langs)
    names = ''.join(f'        ["{l}"] = "{NAMES.get(l, l)}",\n' for l in langs)
    write(os.path.join(OUT, 'LocaleTables.cs'),
        "using System.Collections.Generic;\n\nnamespace Typedown.Uno.Services;\n\n"
        "/// <summary>\n/// The translated strings, one table per language, generated from Tools/Translations/tables\n"
        "/// by Tools/Translations/generate.py. English lives in Loc.cs and fills anything a table leaves out.\n"
        "/// </summary>\npublic static partial class LocaleTables\n{\n"
        "    private static Dictionary<string, Dictionary<string, string>>? all;\n\n"
        "    /// <summary>Built on first use: the tables live in other files of this partial class, and static\n"
        "    /// field initializers across those files run in no particular order — a field initializer here\n"
        "    /// would capture them while they are still null.</summary>\n"
        f"    public static Dictionary<string, Dictionary<string, string>> All => all ??= new()\n    {{\n{registry}    }};\n\n"
        "    /// <summary>Language names for the settings dropdown, each written in its own language.</summary>\n"
        f"    public static readonly Dictionary<string, string> Names = new()\n    {{\n{names}    }};\n}}\n",
        check, stale)
    print(f"registry: {len(langs)} languages")
    if stale:
        print("\nout of sync with the tables: " + ", ".join(sorted(stale)), file=sys.stderr)
        print("run python3 Tools/Translations/generate.py and commit the result", file=sys.stderr)
        return 1
    return 0


if __name__ == '__main__':
    sys.exit(main())
