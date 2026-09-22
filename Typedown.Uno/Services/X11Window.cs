using System.Runtime.InteropServices;
using System.Text;

namespace Typedown.Uno.Services;

/// <summary>
/// Two things Uno's X11 backend does not do for us:
/// <list type="bullet">
/// <item>the window title is published as a Latin-1 <c>WM_NAME</c> only, so anything non-ASCII (a Chinese file
/// name, the default "未命名") shows up as mojibake and <c>_NET_WM_NAME</c> is missing entirely;</item>
/// <item>the GTK web view binds unversioned SONAMEs (<c>libgdk-3.so</c>, <c>libsoup-3.0.so</c>,
/// <c>libwebkit2gtk-4.1.so</c>) that only the -dev packages provide, so a plain desktop cannot load it.</item>
/// </list>
/// Both are fixed here so a deployed package needs no root-installed symlinks.
/// </summary>
public static class X11Window
{
    private const string LibX11 = "libX11.so.6";

    [DllImport(LibX11)] private static extern IntPtr XOpenDisplay(IntPtr display);
    [DllImport(LibX11)] private static extern int XCloseDisplay(IntPtr display);
    [DllImport(LibX11)] private static extern IntPtr XInternAtom(IntPtr display, string name, bool onlyIfExists);
    [DllImport(LibX11)] private static extern int XDefaultScreen(IntPtr display);
    [DllImport(LibX11)] private static extern IntPtr XRootWindow(IntPtr display, int screen);
    [DllImport(LibX11)] private static extern int XQueryTree(IntPtr display, IntPtr window, out IntPtr root, out IntPtr parent, out IntPtr children, out uint count);
    [DllImport(LibX11)] private static extern int XFree(IntPtr data);
    [DllImport(LibX11)] private static extern int XChangeProperty(IntPtr display, IntPtr window, IntPtr property, IntPtr type, int format, int mode, byte[] data, int elements);
    [DllImport(LibX11)] private static extern int XGetWindowProperty(IntPtr display, IntPtr window, IntPtr property, IntPtr offset, IntPtr length, bool delete,
        IntPtr requestedType, out IntPtr actualType, out int actualFormat, out IntPtr items, out IntPtr bytesAfter, out IntPtr data);
    [DllImport(LibX11)] private static extern int XFlush(IntPtr display);

    private static IntPtr display;
    private static IntPtr window;

    /// <summary>Publishes the title as UTF-8 on both WM_NAME and _NET_WM_NAME.</summary>
    public static void SetTitle(string title)
    {
        if (!OperatingSystem.IsLinux()) return;
        try
        {
            if (display == IntPtr.Zero) display = XOpenDisplay(IntPtr.Zero);
            if (display == IntPtr.Zero) return;
            if (window == IntPtr.Zero || !HasClass(window)) window = FindAppWindow();
            if (window == IntPtr.Zero) return;

            var bytes = Encoding.UTF8.GetBytes(title);
            var utf8 = XInternAtom(display, "UTF8_STRING", false);
            foreach (var name in new[] { "_NET_WM_NAME", "WM_NAME", "_NET_WM_ICON_NAME", "WM_ICON_NAME" })
                XChangeProperty(display, window, XInternAtom(display, name, false), utf8, 8, /* PropModeReplace */ 0, bytes, bytes.Length);
            XFlush(display);
        }
        catch (Exception ex)
        {
            Log.Error("set window title", ex);
        }
    }

    /// <summary>
    /// Publishes the app icon at several sizes as <c>_NET_WM_ICON</c>. Uno only sets a single small size, which
    /// looks blurry in docks and task switchers.
    /// </summary>
    public static void SetIcon(string pngPath)
    {
        if (!OperatingSystem.IsLinux() || !File.Exists(pngPath)) return;
        try
        {
            if (display == IntPtr.Zero) display = XOpenDisplay(IntPtr.Zero);
            if (display == IntPtr.Zero) return;
            if (window == IntPtr.Zero) window = FindAppWindow();
            if (window == IntPtr.Zero) return;

            using var original = SkiaSharp.SKBitmap.Decode(pngPath);
            if (original == null) return;
            // One property write per size (PropModeAppend): a single request with every size would exceed the
            // X maximum request length and be dropped. 128 is the largest size that fits comfortably.
            var mode = 0; // PropModeReplace for the first chunk, PropModeAppend afterwards
            var icon = XInternAtom(display, "_NET_WM_ICON", false);
            var cardinal = XInternAtom(display, "CARDINAL", false);
            foreach (var size in new[] { 128, 64, 48, 32, 16 })
            {
                var payload = new List<long>();
                using var scaled = new SkiaSharp.SKBitmap(new SkiaSharp.SKImageInfo(size, size, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Unpremul));
                using (var canvas = new SkiaSharp.SKCanvas(scaled))
                {
                    canvas.Clear(SkiaSharp.SKColors.Transparent);
                    canvas.DrawBitmap(original, new SkiaSharp.SKRect(0, 0, original.Width, original.Height), new SkiaSharp.SKRect(0, 0, size, size));
                }
                payload.Add(size);
                payload.Add(size);
                for (var y = 0; y < size; y++)
                    for (var x = 0; x < size; x++)
                    {
                        var color = scaled.GetPixel(x, y);
                        payload.Add((long)(uint)((color.Alpha << 24) | (color.Red << 16) | (color.Green << 8) | color.Blue));
                    }
                // format 32 means "long" in Xlib, i.e. 8 bytes per element on 64-bit.
                var bytes = new byte[payload.Count * sizeof(long)];
                Buffer.BlockCopy(payload.ToArray(), 0, bytes, 0, bytes.Length);
                XChangeProperty(display, window, icon, cardinal, 32, mode, bytes, payload.Count);
                mode = 2; // PropModeAppend
            }
            XFlush(display);
        }
        catch (Exception ex)
        {
            Log.Error("set window icon", ex);
        }
    }

    /// <summary>Our toplevel window, found by the WM_CLASS the app sets (there is no public handle in Uno).</summary>
    private static IntPtr FindAppWindow()
    {
        if (XQueryTree(display, XRootWindow(display, XDefaultScreen(display)), out _, out _, out var children, out var count) == 0 || children == IntPtr.Zero)
            return IntPtr.Zero;
        try
        {
            for (var i = 0; i < count; i++)
            {
                var child = Marshal.ReadIntPtr(children, i * IntPtr.Size);
                if (HasClass(child)) return child;
            }
        }
        finally
        {
            XFree(children);
        }
        return IntPtr.Zero;
    }

    /// <summary>WM_CLASS of the shell window (the app id from the project); the web view uses a different one.</summary>
    private const string AppWindowClass = "uk.mingdan.typedown";

    private static bool HasClass(IntPtr candidate)
    {
        var wmClass = XInternAtom(display, "WM_CLASS", false);
        if (XGetWindowProperty(display, candidate, wmClass, IntPtr.Zero, (IntPtr)64, false, IntPtr.Zero,
                out _, out _, out var items, out _, out var data) != 0 || data == IntPtr.Zero)
            return false;
        try
        {
            var text = Marshal.PtrToStringUTF8(data, (int)items) ?? "";
            return text.Contains(AppWindowClass, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            XFree(data);
        }
    }

    /// <summary>
    /// Lets Uno's web view load GTK/WebKit through the versioned SONAMEs, so the unversioned symlinks from the
    /// -dev packages are not needed. Registered for every assembly that P/Invokes them.
    /// </summary>
    public static void InstallLibraryResolver()
    {
        if (!OperatingSystem.IsLinux()) return;
        AppDomain.CurrentDomain.AssemblyLoad += (_, e) => TryRegister(e.LoadedAssembly);
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()) TryRegister(assembly);
    }

    private static readonly Dictionary<string, string[]> Alternatives = new(StringComparer.OrdinalIgnoreCase)
    {
        ["libwebkit2gtk-4.1.so"] = new[] { "libwebkit2gtk-4.1.so.0" },
        ["libwebkit2gtk-4.0.so"] = new[] { "libwebkit2gtk-4.0.so.37", "libwebkit2gtk-4.1.so.0" },
        ["libjavascriptcoregtk-4.1.so"] = new[] { "libjavascriptcoregtk-4.1.so.0" },
        ["libgdk-3.so"] = new[] { "libgdk-3.so.0" },
        ["libgtk-3.so"] = new[] { "libgtk-3.so.0" },
        ["libsoup-3.0.so"] = new[] { "libsoup-3.0.so.0" },
        ["libgio-2.0.so"] = new[] { "libgio-2.0.so.0" },
        ["libglib-2.0.so"] = new[] { "libglib-2.0.so.0" },
        ["libgobject-2.0.so"] = new[] { "libgobject-2.0.so.0" },
        ["libcairo.so"] = new[] { "libcairo.so.2" },
        ["libpango-1.0.so"] = new[] { "libpango-1.0.so.0" },
        ["libpangocairo-1.0.so"] = new[] { "libpangocairo-1.0.so.0" },
        ["libgdk_pixbuf-2.0.so"] = new[] { "libgdk_pixbuf-2.0.so.0" },
        ["libatk-1.0.so"] = new[] { "libatk-1.0.so.0" },
    };

    private static readonly HashSet<string> registered = new();

    private static void TryRegister(System.Reflection.Assembly assembly)
    {
        var name = assembly.GetName().Name ?? "";
        // Only the assemblies that bind GTK/WebKit: Uno's own SkiaSharp/HarfBuzzSharp set their own resolvers
        // and a second one throws (which took the whole app down when the filter was wider).
        var wanted = name == "Uno.UI.WebView.Skia.X11"
            || (name.EndsWith("Sharp", StringComparison.Ordinal)
                && name is not ("SkiaSharp" or "HarfBuzzSharp"));
        if (!wanted) return;
        lock (registered)
        {
            if (!registered.Add(name)) return;
        }
        try
        {
            NativeLibrary.SetDllImportResolver(assembly, (library, _, _) =>
            {
                if (NativeLibrary.TryLoad(library, out var handle)) return handle;
                if (Alternatives.TryGetValue(library, out var candidates))
                    foreach (var candidate in candidates)
                        if (NativeLibrary.TryLoad(candidate, out handle))
                            return handle;
                return IntPtr.Zero;
            });
        }
        catch (InvalidOperationException)
        {
            // a resolver was already set for this assembly
        }
    }
}
