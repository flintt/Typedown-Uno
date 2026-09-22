using System.Text;

namespace Typedown.Uno.Services;

/// <summary>
/// Timestamped log in the app data folder (`debug.log`, like the Windows edition). Diagnosing a deployed app
/// cannot rely on the console: started from a desktop launcher there is none.
/// </summary>
public static class Log
{
    private static readonly object sync = new();
    private static string? path;
    private static bool failed;

    public static string Path => path ??= System.IO.Path.Combine(CursorMemory.DataFolder, "debug.log");

    public static void Write(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} {message}";
        Console.WriteLine($"[Typedown.Uno] {line}");
        if (failed) return;
        lock (sync)
        {
            try
            {
                var file = new FileInfo(Path);
                if (file.Exists && file.Length > 512 * 1024) file.Delete(); // keep it small; one rollover is enough
                File.AppendAllText(Path, line + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                failed = true; // never let logging break the app
            }
        }
    }

    public static void Error(string context, Exception ex) => Write($"{context}: {ex.GetType().Name}: {ex.Message}");

    /// <summary>One line describing the environment, written at startup.</summary>
    public static void WriteStartup()
    {
        Write($"startup: os={Environment.OSVersion} arch={System.Runtime.InteropServices.RuntimeInformation.OSArchitecture} " +
              $"base={AppContext.BaseDirectory} cwd={Environment.CurrentDirectory} " +
              $"display={Environment.GetEnvironmentVariable("DISPLAY")} wayland={Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")} " +
              $"gdk={Environment.GetEnvironmentVariable("GDK_BACKEND")}");
        var missing = MissingLinuxLibraries();
        if (missing.Count > 0)
            Write($"missing unversioned libraries (the GTK web view P/Invokes these names; run install-linux.sh or install the -dev packages): {string.Join(", ", missing)}");
    }

    /// <summary>
    /// Uno's GTK web view binds unversioned SONAMEs, which ship only in the -dev packages. Without them the
    /// native view never finishes initializing and the editor area stays blank, so name them explicitly.
    /// </summary>
    public static List<string> MissingLinuxLibraries()
    {
        var missing = new List<string>();
        if (!OperatingSystem.IsLinux()) return missing;
        string[] names = { "libwebkit2gtk-4.1.so", "libjavascriptcoregtk-4.1.so", "libgdk-3.so", "libgtk-3.so", "libsoup-3.0.so" };
        string[] dirs = { "/usr/lib/x86_64-linux-gnu", "/usr/lib64", "/usr/lib", "/usr/lib/aarch64-linux-gnu" };
        foreach (var name in names)
            if (!dirs.Any(d => File.Exists(System.IO.Path.Combine(d, name))))
                missing.Add(name);
        return missing;
    }
}
