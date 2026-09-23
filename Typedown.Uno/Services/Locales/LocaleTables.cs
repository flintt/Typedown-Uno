using System.Collections.Generic;

namespace Typedown.Uno.Services;

/// <summary>
/// The translated strings, one table per language, generated from Tools/Translations/tables
/// by Tools/Translations/generate.py. English lives in Loc.cs and fills anything a table leaves out.
/// </summary>
public static partial class LocaleTables
{
    private static Dictionary<string, Dictionary<string, string>>? all;

    /// <summary>Built on first use: the tables live in other files of this partial class, and static
    /// field initializers across those files run in no particular order — a field initializer here
    /// would capture them while they are still null.</summary>
    public static Dictionary<string, Dictionary<string, string>> All => all ??= new()
    {
        ["de"] = de,
        ["es"] = es,
        ["fr"] = fr,
        ["it"] = it,
        ["ja"] = ja,
        ["ko"] = ko,
        ["pt"] = pt,
        ["ru"] = ru,
        ["zh-Hans"] = zhHans,
        ["zh-Hant"] = zhHant,
    };

    /// <summary>Language names for the settings dropdown, each written in its own language.</summary>
    public static readonly Dictionary<string, string> Names = new()
    {
        ["de"] = "Deutsch",
        ["es"] = "Español",
        ["fr"] = "Français",
        ["it"] = "Italiano",
        ["ja"] = "日本語",
        ["ko"] = "한국어",
        ["pt"] = "Português",
        ["ru"] = "Русский",
        ["zh-Hans"] = "中文（简体）",
        ["zh-Hant"] = "中文（繁體）",
    };
}
