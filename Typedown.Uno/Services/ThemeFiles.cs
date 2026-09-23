using System.Text.RegularExpressions;

namespace Typedown.Uno.Services;

/// <summary>
/// A custom theme is one CSS file in the themes folder. Its first comment block carries the metadata (the name
/// to show, which built-in theme it builds on, an optional accent colour); everything after it is applied to
/// the editor on top of that built-in theme, so a theme only has to state what it changes.
/// See docs/custom-theme.md for the format and the variables.
/// </summary>
/// <param name="Background">Window surface behind the editor, from the metadata; null keeps the built-in one.</param>
/// <param name="Surface">Panels: side pane, tab bar, status bar.</param>
/// <param name="Foreground">Text in the shell.</param>
/// <param name="Border">Separators between the panels.</param>
public sealed record CustomTheme(string Id, string Name, AppTheme Base, string? Accent, string? Author, string Path,
    string? Background = null, string? Surface = null, string? Foreground = null, string? Border = null);

public static class ThemeFiles
{
    public static string Folder => System.IO.Path.Combine(CursorMemory.DataFolder, "themes");

    /// <summary>Every readable theme in the folder, by file name. Missing metadata falls back to the file name.</summary>
    public static IReadOnlyList<CustomTheme> List()
    {
        var themes = new List<CustomTheme>();
        try
        {
            if (!Directory.Exists(Folder)) return themes;
            foreach (var path in Directory.EnumerateFiles(Folder, "*.css").OrderBy(p => p))
            {
                try
                {
                    themes.Add(Parse(path, File.ReadAllText(path)));
                }
                catch (Exception ex)
                {
                    Log.Error($"theme {System.IO.Path.GetFileName(path)}", ex);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("theme folder", ex);
        }
        return themes;
    }

    public static CustomTheme? Find(string? id) =>
        string.IsNullOrEmpty(id) ? null : List().FirstOrDefault(t => t.Id == id);

    /// <summary>The CSS to hand the editor, or an empty string when the theme has gone missing.</summary>
    public static string Read(string? id)
    {
        var theme = Find(id);
        if (theme == null) return "";
        try
        {
            return File.ReadAllText(theme.Path);
        }
        catch (Exception ex)
        {
            Log.Error("read theme", ex);
            return "";
        }
    }

    /// <summary>Creates the folder and, the first time, an example theme to start from.</summary>
    public static void EnsureFolder()
    {
        try
        {
            if (Directory.Exists(Folder)) return;
            Directory.CreateDirectory(Folder);
            File.WriteAllText(System.IO.Path.Combine(Folder, "example.css"), Example);
        }
        catch (Exception ex)
        {
            Log.Error("create theme folder", ex);
        }
    }

    private static CustomTheme Parse(string path, string css)
    {
        var id = System.IO.Path.GetFileNameWithoutExtension(path);
        var header = Regex.Match(css, @"/\*(.*?)\*/", RegexOptions.Singleline);
        var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (header.Success)
            foreach (Match m in Regex.Matches(header.Groups[1].Value, @"^[\s*]*([A-Za-z]+)\s*:\s*(.+?)\s*$", RegexOptions.Multiline))
                meta[m.Groups[1].Value] = m.Groups[2].Value;
        var baseTheme = meta.TryGetValue("base", out var b) ? b.Trim().ToLowerInvariant() switch
        {
            "dark" => AppTheme.Dark,
            "black" => AppTheme.Black,
            _ => AppTheme.Light,
        } : AppTheme.Light;
        string? Value(string key) => meta.TryGetValue(key, out var v) && v.Length > 0 ? v : null;
        return new CustomTheme(
            id,
            Value("name") ?? id,
            baseTheme,
            Value("accent"),
            Value("author"),
            path,
            Value("background"),
            Value("surface"),
            Value("foreground"),
            Value("border"));
    }

    private const string Example = """
/* Typedown theme
 * name: Example
 * base: light
 * accent: #268bd2
 * author: you
 *
 * These four colour the window around the editor (side pane, tab bar, status bar, separators). Leave them
 * out and the window keeps the colours of the built-in theme named by "base".
 * background: #fdf6e3
 * surface: #f2ead7
 * foreground: #073642
 * border: #e0dbc8
 *
 * Every declaration below overrides the built-in theme named by "base", so a theme only states what it
 * changes. The full list of variables is in docs/custom-theme.md.
 */
:root {
  --editorBgColor: #fdf6e3;
  --editorColor: #073642;
  --codeBgColor: #eee8d5;
  --codeBlockBgColor: #eee8d5;
  --tableBorderColor: #e0dbc8;
  --floatBgColor: #fdf6e3;
  --itemBgColor: #eee8d5;
}
""";
}
