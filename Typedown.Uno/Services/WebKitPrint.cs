using System.Runtime.InteropServices;

namespace Typedown.Uno.Services;

/// <summary>
/// Printing and PDF export on Linux, straight through WebKitGTK.
///
/// Uno's web view exposes nothing of the kind, so the page is loaded into a second, offscreen WebKitGTK view
/// and that view's print operation is used: with a print dialog for "Print…", and with the settings pointed at
/// a file for "Export PDF", which is what GTK's own "Print to File" backend does.
///
/// Everything here has to run on the GTK main loop, hence the g_idle_add hop; the result comes back through a
/// TaskCompletionSource once WebKit says the operation finished.
/// </summary>
public static class WebKitPrint
{
    private const string LibWebKit = "libwebkit2gtk-4.1.so";
    private const string LibGtk = "libgtk-3.so.0";
    private const string LibGLib = "libglib-2.0.so.0";
    private const string LibGObject = "libgobject-2.0.so.0";

    [DllImport(LibGLib)] private static extern uint g_idle_add(GSourceFunc function, IntPtr data);
    [DllImport(LibGObject)] private static extern ulong g_signal_connect_data(IntPtr instance, string signal, Delegate handler, IntPtr data, IntPtr destroy, int flags);
    [DllImport(LibGObject)] private static extern void g_object_unref(IntPtr obj);
    [DllImport(LibGtk)] private static extern IntPtr gtk_offscreen_window_new();
    [DllImport(LibGtk)] private static extern void gtk_container_add(IntPtr container, IntPtr widget);
    [DllImport(LibGtk)] private static extern void gtk_widget_show_all(IntPtr widget);
    [DllImport(LibGtk)] private static extern void gtk_widget_destroy(IntPtr widget);
    [DllImport(LibGtk)] private static extern void gtk_widget_set_size_request(IntPtr widget, int width, int height);
    [DllImport(LibGtk)] private static extern IntPtr gtk_print_settings_new();
    [DllImport(LibGtk)] private static extern void gtk_print_settings_set(IntPtr settings, string key, string value);
    [DllImport(LibGtk)] private static extern void gtk_print_settings_set_printer(IntPtr settings, string printer);
    [DllImport(LibGtk)] private static extern void gtk_print_settings_set_print_pages(IntPtr settings, int pages);
    [DllImport(LibGtk)] private static extern void gtk_print_settings_set_n_copies(IntPtr settings, int copies);
    [DllImport(LibGtk)] private static extern IntPtr gtk_page_setup_new();
    [DllImport(LibGtk)] private static extern IntPtr gtk_paper_size_new(IntPtr name);
    [DllImport(LibGtk)] private static extern void gtk_page_setup_set_paper_size_and_default_margins(IntPtr setup, IntPtr paperSize);
    [DllImport(LibGtk)] private static extern void gtk_page_setup_set_top_margin(IntPtr setup, double margin, int unit);
    [DllImport(LibGtk)] private static extern void gtk_page_setup_set_bottom_margin(IntPtr setup, double margin, int unit);
    [DllImport(LibGtk)] private static extern void gtk_page_setup_set_left_margin(IntPtr setup, double margin, int unit);
    [DllImport(LibGtk)] private static extern void gtk_page_setup_set_right_margin(IntPtr setup, double margin, int unit);
    [DllImport(LibWebKit)] private static extern IntPtr webkit_web_view_new();
    [DllImport(LibWebKit)] private static extern void webkit_web_view_load_uri(IntPtr webView, string uri);
    [DllImport(LibWebKit)] private static extern IntPtr webkit_print_operation_new(IntPtr webView);
    [DllImport(LibWebKit)] private static extern void webkit_print_operation_set_print_settings(IntPtr op, IntPtr settings);
    [DllImport(LibWebKit)] private static extern void webkit_print_operation_set_page_setup(IntPtr op, IntPtr setup);
    [DllImport(LibWebKit)] private static extern void webkit_print_operation_print(IntPtr op);
    [DllImport(LibWebKit)] private static extern int webkit_print_operation_run_dialog(IntPtr op, IntPtr parent);

    private delegate bool GSourceFunc(IntPtr data);
    private delegate void LoadChangedHandler(IntPtr webView, int loadEvent, IntPtr data);
    private delegate void PrintFinishedHandler(IntPtr op, IntPtr data);
    private delegate void PrintFailedHandler(IntPtr op, IntPtr error, IntPtr data);

    private const int LoadFinished = 3;      // WEBKIT_LOAD_FINISHED
    private const int UnitMillimetre = 3;    // GtkUnit: none, points, inch, mm
    private const int PagesAll = 0;          // GTK_PRINT_PAGES_ALL

    public static bool Available => OperatingSystem.IsLinux();

    /// <summary>Renders an HTML file to a PDF. Returns false if WebKitGTK could not do it.</summary>
    public static Task<bool> ExportPdfAsync(string htmlPath, string pdfPath, double marginMm = 12)
        => RunAsync(htmlPath, pdfPath, marginMm, dialog: false);

    /// <summary>Opens the system print dialog for an HTML file.</summary>
    public static Task<bool> PrintAsync(string htmlPath, double marginMm = 12)
        => RunAsync(htmlPath, null, marginMm, dialog: true);

    // The delegates are called from the GTK main loop and must outlive this method, so they are held for the
    // duration of the operation rather than left to the collector.
    private static readonly List<object> alive = new();

    private static Task<bool> RunAsync(string htmlPath, string? pdfPath, double marginMm, bool dialog)
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!Available || !File.Exists(htmlPath))
        {
            done.TrySetResult(false);
            return done.Task;
        }

        GSourceFunc start = _ =>
        {
            IntPtr window = IntPtr.Zero;
            try
            {
                window = gtk_offscreen_window_new();
                var view = webkit_web_view_new();
                gtk_widget_set_size_request(view, 900, 1200); // a page-ish width, so media queries see a document
                gtk_container_add(window, view);
                gtk_widget_show_all(window);

                var closedWindow = window;
                LoadChangedHandler? onLoad = null;
                onLoad = (_, loadEvent, __) =>
                {
                    if (loadEvent != LoadFinished) return;
                    try
                    {
                        var op = webkit_print_operation_new(view);

                        if (marginMm > 0)
                        {
                            var setup = gtk_page_setup_new();
                            // A page setup with no paper size prints nothing at all — the operation reports
                            // itself finished and no file appears — so start from the default paper.
                            gtk_page_setup_set_paper_size_and_default_margins(setup, gtk_paper_size_new(IntPtr.Zero));
                            gtk_page_setup_set_top_margin(setup, marginMm, UnitMillimetre);
                            gtk_page_setup_set_bottom_margin(setup, marginMm, UnitMillimetre);
                            gtk_page_setup_set_left_margin(setup, marginMm, UnitMillimetre);
                            gtk_page_setup_set_right_margin(setup, marginMm, UnitMillimetre);
                            webkit_print_operation_set_page_setup(op, setup);
                        }

                        PrintFinishedHandler onFinished = (_, __) =>
                        {
                            Log.Write($"webkit print finished; target {(pdfPath == null ? "(dialog)" : pdfPath)} exists = {pdfPath != null && File.Exists(pdfPath)}");
                            done.TrySetResult(pdfPath == null || File.Exists(pdfPath));
                            Cleanup(closedWindow);
                        };
                        PrintFailedHandler onFailed = (_, error, ___) =>
                        {
                            Log.Write($"webkit print operation failed: {ErrorMessage(error)}");
                            done.TrySetResult(false);
                            Cleanup(closedWindow);
                        };
                        lock (alive) { alive.Add(onFinished); alive.Add(onFailed); }
                        g_signal_connect_data(op, "finished", onFinished, IntPtr.Zero, IntPtr.Zero, 0);
                        g_signal_connect_data(op, "failed", onFailed, IntPtr.Zero, IntPtr.Zero, 0);

                        if (pdfPath != null)
                        {
                            // GTK's own "print to a file" backend: the printer is the file writer, and the
                            // destination and format come from the settings.
                            var settings = gtk_print_settings_new();
                            // Fresh settings carry no page range, and WebKit refuses to print without one.
                            gtk_print_settings_set_print_pages(settings, PagesAll);
                            gtk_print_settings_set_n_copies(settings, 1);
                            gtk_print_settings_set_printer(settings, "Print to File");
                            gtk_print_settings_set(settings, "output-uri", new Uri(pdfPath).AbsoluteUri);
                            gtk_print_settings_set(settings, "output-file-format", "pdf");
                            webkit_print_operation_set_print_settings(op, settings);
                            webkit_print_operation_print(op);
                        }
                        else
                        {
                            // The dialog reports its own outcome; "print" means the operation is running and
                            // "finished" will follow, anything else means the user backed out.
                            var response = webkit_print_operation_run_dialog(op, IntPtr.Zero);
                            if (response != 1) // WEBKIT_PRINT_OPERATION_RESPONSE_PRINT
                            {
                                done.TrySetResult(false);
                                Cleanup(closedWindow);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error("webkit print", ex);
                        done.TrySetResult(false);
                        Cleanup(closedWindow);
                    }
                };
                lock (alive) { alive.Add(onLoad); }
                g_signal_connect_data(view, "load-changed", onLoad, IntPtr.Zero, IntPtr.Zero, 0);
                webkit_web_view_load_uri(view, new Uri(htmlPath).AbsoluteUri);
            }
            catch (Exception ex)
            {
                Log.Error("webkit print setup", ex);
                done.TrySetResult(false);
                Cleanup(window);
            }
            return false; // once
        };

        lock (alive) { alive.Add(start); }
        g_idle_add(start, IntPtr.Zero);

        // A page that never finishes loading must not leave the caller waiting for ever.
        _ = Task.Delay(TimeSpan.FromSeconds(60)).ContinueWith(_ => done.TrySetResult(false));
        return done.Task;
    }

    /// <summary>The text of a GError (domain, code, then the message pointer).</summary>
    private static string ErrorMessage(IntPtr error)
    {
        if (error == IntPtr.Zero) return "(no error given)";
        try
        {
            var message = Marshal.ReadIntPtr(error, 8);
            return Marshal.PtrToStringUTF8(message) ?? "(empty)";
        }
        catch { return "(unreadable)"; }
    }

    private static void Cleanup(IntPtr window)
    {
        try { if (window != IntPtr.Zero) gtk_widget_destroy(window); } catch { }
        lock (alive) { alive.Clear(); }
    }
}
