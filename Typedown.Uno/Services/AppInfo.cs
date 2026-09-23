using System.Reflection;
using System.Runtime.InteropServices;

namespace Typedown.Uno.Services;

/// <summary>
/// Versions of the app and of everything it is built on. A bug report without them is guesswork: the same
/// symptom can come from the editor bundle, from Uno, from the system WebKitGTK or from the desktop session,
/// so the About box shows them all and the log records them at startup.
/// </summary>
public static class AppInfo
{
    /// <summary>The app version, with the build's commit when one was stamped in.</summary>
    public static string Version
    {
        get
        {
            var assembly = typeof(AppInfo).Assembly;
            var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (string.IsNullOrEmpty(informational)) return assembly.GetName().Version?.ToString(3) ?? "?";
            // "1.1.0+<40 char commit>" from SourceLink: the short commit is what a bug report needs
            var plus = informational.IndexOf('+');
            if (plus < 0) return informational;
            var commit = informational[(plus + 1)..];
            return $"{informational[..plus]} ({(commit.Length > 7 ? commit[..7] : commit)})";
        }
    }

    /// <summary>Label/value pairs for the About box; the same lines go into the log.</summary>
    public static List<(string Label, string Value)> Lines() => new()
    {
        ("Typedown", Version),
        (Loc.Get("AboutEditor"), $"Muya (MarkText) · {EditorBundle()}"),
        ("Uno Platform", AssemblyVersion(typeof(Microsoft.UI.Xaml.Application))),
        ("SkiaSharp", AssemblyVersion(typeof(SkiaSharp.SKBitmap))),
        (".NET", RuntimeInformation.FrameworkDescription),
        (Loc.Get("AboutWebEngine"), WebEngine()),
        (Loc.Get("AboutSystem"), $"{RuntimeInformation.OSDescription.Trim()} · {RuntimeInformation.OSArchitecture}"),
        (Loc.Get("AboutSession"), Session()),
    };

    public static string Summary() => string.Join(Environment.NewLine, Lines().Select(l => $"{l.Label}: {l.Value}"));

    private static string AssemblyVersion(Type type)
    {
        var assembly = type.Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        // strip the commit suffix NuGet packages carry ("6.7.30+abcdef")
        if (!string.IsNullOrEmpty(informational)) return informational.Split('+')[0];
        return assembly.GetName().Version?.ToString() ?? "?";
    }

    /// <summary>The editor bundle's content hash, which identifies the editor build exactly.</summary>
    private static string EditorBundle()
    {
        try
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "Assets", "Editor", "static", "js");
            var file = Directory.Exists(dir) ? Directory.GetFiles(dir, "main.*.js").FirstOrDefault() : null;
            return file == null ? "?" : Path.GetFileName(file);
        }
        catch
        {
            return "?";
        }
    }

    [DllImport("libwebkit2gtk-4.1.so.0")] private static extern uint webkit_get_major_version();
    [DllImport("libwebkit2gtk-4.1.so.0")] private static extern uint webkit_get_minor_version();
    [DllImport("libwebkit2gtk-4.1.so.0")] private static extern uint webkit_get_micro_version();

    [DllImport("WebView2Loader.dll", CharSet = CharSet.Unicode)]
    private static extern int GetAvailableCoreWebView2BrowserVersionString(string? browserExecutableFolder, out IntPtr versionInfo);

    /// <summary>The web engine actually rendering the editor: WebKitGTK on Linux, the WebView2 runtime on Windows.</summary>
    private static string WebEngine()
    {
        if (OperatingSystem.IsLinux())
        {
            try
            {
                return $"WebKitGTK {webkit_get_major_version()}.{webkit_get_minor_version()}.{webkit_get_micro_version()}";
            }
            catch (Exception ex)
            {
                return $"WebKitGTK ({ex.GetType().Name})";
            }
        }
        if (OperatingSystem.IsWindows())
        {
            try
            {
                if (GetAvailableCoreWebView2BrowserVersionString(null, out var text) == 0 && text != IntPtr.Zero)
                {
                    var value = Marshal.PtrToStringUni(text);
                    Marshal.FreeCoTaskMem(text);
                    if (!string.IsNullOrEmpty(value)) return $"WebView2 {value}";
                }
                return "WebView2 (not installed)";
            }
            catch (Exception ex)
            {
                return $"WebView2 ({ex.GetType().Name})";
            }
        }
        return "WKWebView";
    }

    /// <summary>How the window is displayed — the first thing to ask about on a Linux rendering problem.</summary>
    private static string Session()
    {
        if (!OperatingSystem.IsLinux()) return Environment.OSVersion.VersionString;
        var type = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE");
        var desktop = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP");
        var gdk = Environment.GetEnvironmentVariable("GDK_BACKEND");
        var display = Environment.GetEnvironmentVariable("DISPLAY");
        var wayland = Environment.GetEnvironmentVariable("WAYLAND_DISPLAY");
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(desktop)) parts.Add(desktop);
        if (!string.IsNullOrEmpty(type)) parts.Add(type);
        if (!string.IsNullOrEmpty(display)) parts.Add($"DISPLAY={display}");
        if (!string.IsNullOrEmpty(wayland)) parts.Add($"WAYLAND_DISPLAY={wayland}");
        if (!string.IsNullOrEmpty(gdk)) parts.Add($"GDK_BACKEND={gdk}");
        return parts.Count == 0 ? "?" : string.Join(" · ", parts);
    }
}
