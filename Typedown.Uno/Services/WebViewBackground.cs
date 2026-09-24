using System.Reflection;
using System.Runtime.InteropServices;

namespace Typedown.Uno.Services;

/// <summary>
/// Gives the editor's WebKitGTK view the colour the page itself uses.
///
/// The editor is a native web view inside the window, and it paints its own background — white by default —
/// wherever the page has not been drawn yet: the strip a window resize exposes, or the whole view for a moment
/// while a document is re-rendered. On a dark theme that shows up as a white band flashing past. WebKit takes a
/// background colour per view, but Uno's X11 web view neither sets it nor exposes the view, so it is reached
/// through the one field that holds it and set on the GTK thread, where every GTK call has to happen.
/// </summary>
public static class WebViewBackground
{
    private const string LibWebKit = "libwebkit2gtk-4.1.so";
    private const string LibGLib = "libglib-2.0.so.0";

    [StructLayout(LayoutKind.Sequential)]
    private struct GdkRGBA
    {
        public double Red, Green, Blue, Alpha;
    }

    private const string LibGtk = "libgtk-3.so.0";
    private const string LibGdk = "libgdk-3.so.0";

    [DllImport(LibWebKit)] private static extern void webkit_web_view_set_background_color(IntPtr webView, ref GdkRGBA rgba);
    [DllImport(LibGLib)] private static extern uint g_idle_add(GSourceFunc function, IntPtr data);
    [DllImport(LibGtk)] private static extern IntPtr gtk_css_provider_new();
    [DllImport(LibGtk)] private static extern bool gtk_css_provider_load_from_data(IntPtr provider, byte[] data, IntPtr length, out IntPtr error);
    [DllImport(LibGtk)] private static extern void gtk_style_context_add_provider_for_screen(IntPtr screen, IntPtr provider, uint priority);
    [DllImport(LibGdk)] private static extern IntPtr gdk_screen_get_default();

    private delegate bool GSourceFunc(IntPtr data);

    // The delegate is called from the GTK main loop, so it has to outlive this call.
    private static GSourceFunc? pending;

    public static void Apply(object? webViewControl, byte r, byte g, byte b)
    {
        if (!OperatingSystem.IsLinux() || webViewControl == null) return;
        try
        {
            var handle = FindWebKitHandle(webViewControl);
            if (handle == IntPtr.Zero)
            {
                Log.Write("web view background: no WebKit handle (Uno's field names may have changed)");
                return;
            }
            var colour = new GdkRGBA { Red = r / 255.0, Green = g / 255.0, Blue = b / 255.0, Alpha = 1.0 };
            pending = _ =>
            {
                try
                {
                    var rgba = colour;
                    webkit_web_view_set_background_color(handle, ref rgba);
                    // The web view's own colour is not the whole story: a resize exposes part of the GTK window
                    // before the view has been moved into it, and that part is painted by the window itself.
                    StyleGtkWindows(r, g, b);
                    Log.Write($"web view background set to #{r:x2}{g:x2}{b:x2}");
                }
                catch (Exception ex)
                {
                    Log.Error("web view background (gtk thread)", ex);
                }
                return false; // once
            };
            g_idle_add(pending, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            Log.Error("web view background", ex);
        }
    }

    private static IntPtr cssProvider;

    /// <summary>
    /// Paints every GTK window in the process, which is where a resize shows through before anything else has
    /// been drawn. One provider is kept and reloaded, so changing theme does not stack them up.
    /// </summary>
    private static void StyleGtkWindows(byte r, byte g, byte b)
    {
        try
        {
            var css = System.Text.Encoding.UTF8.GetBytes($"window, webkitwebview {{ background-color: #{r:x2}{g:x2}{b:x2}; }}");
            if (cssProvider == IntPtr.Zero)
            {
                cssProvider = gtk_css_provider_new();
                if (cssProvider == IntPtr.Zero) return;
                var screen = gdk_screen_get_default();
                if (screen == IntPtr.Zero) return;
                gtk_style_context_add_provider_for_screen(screen, cssProvider, 600); // GTK_STYLE_PROVIDER_PRIORITY_APPLICATION
            }
            gtk_css_provider_load_from_data(cssProvider, css, (IntPtr)css.Length, out _);
        }
        catch (Exception ex)
        {
            Log.Error("gtk window background", ex);
        }
    }

    /// <summary>
    /// WebView2 → its CoreWebView2 → Uno's X11NativeWebView → the WebKit.WebView wrapper → the GObject pointer.
    /// All of it is Uno's own plumbing, so a version that renames a field leaves the colour unset rather than
    /// throwing: the flash comes back, nothing else.
    /// </summary>
    private static IntPtr FindWebKitHandle(object webViewControl)
    {
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        var core = webViewControl.GetType().GetProperty("CoreWebView2", Flags)?.GetValue(webViewControl);
        var native = core?.GetType().GetField("_nativeWebView", Flags)?.GetValue(core);
        var view = native?.GetType().GetField("_webview", Flags)?.GetValue(native);
        var handle = view?.GetType().GetProperty("Handle", Flags)?.GetValue(view);
        return handle is IntPtr pointer ? pointer : IntPtr.Zero;
    }
}
