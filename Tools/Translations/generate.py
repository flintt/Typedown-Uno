#!/usr/bin/env python3
"""Regenerates the localization tables from Tools/Translations/tables/<lang>.json.

    python3 Tools/Translations/generate.py

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


def main():
    reference = json.load(open(os.path.join(TABLES, 'en.json'), encoding='utf-8'))
    # English lives in Loc.cs and is the fallback for every language, so a key that never reached it shows up
    # as its own name in the interface — which is how "ThemeDocument" once appeared in a menu.
    loc = open(os.path.join(os.path.dirname(TABLES), '..', '..', 'Typedown.Uno', 'Services', 'Loc.cs'), encoding='utf-8').read()
    in_loc = set(re.findall(r'\["([A-Za-z0-9_]+)"\]\s*=', loc))
    absent = [k for k in reference if k not in in_loc]
    if absent:
        raise SystemExit('these keys are missing from the English fallback in Loc.cs: ' + ', '.join(absent))
    langs = sorted(os.path.basename(p)[:-5] for p in glob.glob(os.path.join(TABLES, '*.json')))
    langs = [l for l in langs if l != 'en']
    os.makedirs(OUT, exist_ok=True)
    for lang in langs:
        strings = json.load(open(os.path.join(TABLES, lang + '.json'), encoding='utf-8'))
        unknown = [k for k in strings if k not in reference]
        if unknown:
            print(f"{lang}: unknown keys {unknown}", file=sys.stderr)
            return 1
        body = ''.join(f'        ["{k}"] = "{v}",\n' for k, v in strings.items())
        open(os.path.join(OUT, lang + '.cs'), 'w', encoding='utf-8').write(
            "using System.Collections.Generic;\n\nnamespace Typedown.Uno.Services;\n\n"
            "public static partial class LocaleTables\n{\n"
            f"    internal static readonly Dictionary<string, string> {identifier(lang)} = new()\n    {{\n{body}    }};\n}}\n")
        missing = len(reference) - len(strings)
        print(f"{lang}: {len(strings)}/{len(reference)}" + (f" ({missing} fall back to English)" if missing else " complete"))
    registry = ''.join(f'        ["{l}"] = {identifier(l)},\n' for l in langs)
    names = ''.join(f'        ["{l}"] = "{NAMES.get(l, l)}",\n' for l in langs)
    open(os.path.join(OUT, 'LocaleTables.cs'), 'w', encoding='utf-8').write(
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
        f"    public static readonly Dictionary<string, string> Names = new()\n    {{\n{names}    }};\n}}\n")
    print(f"registry: {len(langs)} languages")
    return 0


if __name__ == '__main__':
    sys.exit(main())
