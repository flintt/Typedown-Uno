using System.Runtime.InteropServices;
using System.Text;

namespace Typedown.Uno.Services;

/// <summary>
/// The clipboard on Linux, through GTK: a copy in two forms, its text and the same content formatted (text/html), so a
/// word processor (LibreOffice Writer, a mail client) pastes it with its bold, lists and pictures. Uno's clipboard on
/// X11 offers text only: the editor's HTML was dropped, and Copy was the same as Copy as Markdown everywhere.
///
/// GTK serves the clipboard from its main loop (where every GTK call has to happen, hence the g_idle_add hop), for as
/// long as the app owns it.
/// </summary>
public static class GtkClipboard
{
    private const string LibGtk = "libgtk-3.so.0";
    private const string LibGdk = "libgdk-3.so.0";
    private const string LibGLib = "libglib-2.0.so.0";

    private delegate bool GSourceFunc(IntPtr data);
    private delegate void GetFunc(IntPtr clipboard, IntPtr selectionData, uint info, IntPtr userData);
    private delegate void ClearFunc(IntPtr clipboard, IntPtr userData);

    [DllImport(LibGLib)] private static extern uint g_idle_add(GSourceFunc function, IntPtr data);
    [DllImport(LibGdk)] private static extern IntPtr gdk_atom_intern(string name, bool onlyIfExists);
    [DllImport(LibGtk)] private static extern IntPtr gtk_clipboard_get(IntPtr selection);
    [DllImport(LibGtk)] private static extern IntPtr gtk_target_list_new(IntPtr targets, uint count);
    [DllImport(LibGtk)] private static extern void gtk_target_list_add(IntPtr list, IntPtr target, uint flags, uint info);
    [DllImport(LibGtk)] private static extern void gtk_target_list_add_text_targets(IntPtr list, uint info);
    [DllImport(LibGtk)] private static extern IntPtr gtk_target_table_new_from_list(IntPtr list, out int count);
    [DllImport(LibGtk)] private static extern void gtk_target_table_free(IntPtr targets, int count);
    [DllImport(LibGtk)] private static extern void gtk_target_list_unref(IntPtr list);
    [DllImport(LibGtk)] private static extern bool gtk_clipboard_set_with_data(IntPtr clipboard, IntPtr targets, uint count, GetFunc get, ClearFunc clear, IntPtr userData);
    [DllImport(LibGtk)] private static extern IntPtr gtk_selection_data_get_target(IntPtr selectionData);
    [DllImport(LibGtk)] private static extern void gtk_selection_data_set(IntPtr selectionData, IntPtr type, int format, byte[] data, int length);
    [DllImport(LibGtk)] private static extern bool gtk_selection_data_set_text(IntPtr selectionData, byte[] utf8, int length);
    [DllImport(LibGtk)] private static extern void gtk_clipboard_request_targets(IntPtr clipboard, TargetsReceived callback, IntPtr userData);
    [DllImport(LibGtk)] private static extern void gtk_clipboard_request_contents(IntPtr clipboard, IntPtr target, ContentsReceived callback, IntPtr userData);
    [DllImport(LibGtk)] private static extern IntPtr gtk_selection_data_get_data_with_length(IntPtr selectionData, out int length);
    [DllImport(LibGdk)] private static extern IntPtr gdk_atom_name(IntPtr atom);
    [DllImport(LibGLib)] private static extern void g_free(IntPtr memory);

    private delegate void TargetsReceived(IntPtr clipboard, IntPtr atoms, int count, IntPtr userData);
    private delegate void ContentsReceived(IntPtr clipboard, IntPtr selectionData, IntPtr userData);

    private const uint HtmlTarget = 1, TextTarget = 2;

    // Called from GTK for as long as the clipboard is ours: the delegates and the content have to outlive the call.
    private static readonly GetFunc get = OnGet;
    private static readonly ClearFunc clear = (_, _) => { };
    private static GSourceFunc? pending;
    private static string text = "";
    private static string? html;

    public static bool IsAvailable => OperatingSystem.IsLinux();

    /// <summary>Puts the copy on the clipboard: its text, and its HTML when there is any.</summary>
    public static void SetTextAndHtml(string newText, string? newHtml)
    {
        pending = _ =>
        {
            try
            {
                text = newText;
                html = string.IsNullOrEmpty(newHtml) ? null : newHtml;
                var list = gtk_target_list_new(IntPtr.Zero, 0);
                if (html != null) gtk_target_list_add(list, gdk_atom_intern("text/html", false), 0, HtmlTarget);
                gtk_target_list_add_text_targets(list, TextTarget);
                var table = gtk_target_table_new_from_list(list, out var count);
                var clipboard = gtk_clipboard_get(gdk_atom_intern("CLIPBOARD", false));
                if (!gtk_clipboard_set_with_data(clipboard, table, (uint)count, get, clear, IntPtr.Zero))
                    Log.Write("gtk clipboard: not taken");
                gtk_target_table_free(table, count);
                gtk_target_list_unref(list);
            }
            catch (Exception ex)
            {
                Log.Error("gtk clipboard", ex);
            }
            return false; // once
        };
        g_idle_add(pending, IntPtr.Zero);
    }

    // The picture types a paste takes, in the order it prefers them.
    private static readonly string[] ImageTypes = { "image/png", "image/jpeg", "image/gif", "image/webp", "image/bmp" };
    private static TargetsReceived? targetsReceived;
    private static ContentsReceived? contentsReceived;

    /// <summary>
    /// The picture on the clipboard and its type, or null when it holds none (or no answer within 5 s). Uno's clipboard
    /// on X11 hands out no pictures, so a picture copied in a browser or an image viewer pasted as nothing.
    /// </summary>
    public static async Task<(byte[] bytes, string type)?> ReadImageAsync()
    {
        var done = new TaskCompletionSource<(byte[] bytes, string type)?>(TaskCreationOptions.RunContinuationsAsynchronously);
        string? chosen = null;
        contentsReceived = (_, selectionData, _) =>
        {
            try
            {
                var length = 0;
                var data = selectionData == IntPtr.Zero ? IntPtr.Zero : gtk_selection_data_get_data_with_length(selectionData, out length);
                if (data == IntPtr.Zero || length <= 0) { done.TrySetResult(null); return; }
                var bytes = new byte[length];
                Marshal.Copy(data, bytes, 0, length);
                done.TrySetResult((bytes, chosen!));
            }
            catch (Exception ex) { Log.Error("gtk clipboard (picture)", ex); done.TrySetResult(null); }
        };
        targetsReceived = (clipboard, atoms, count, _) =>
        {
            try
            {
                var offered = new List<string>();
                for (var i = 0; i < count; i++)
                {
                    var name = gdk_atom_name(Marshal.ReadIntPtr(atoms, i * IntPtr.Size));
                    if (name == IntPtr.Zero) continue;
                    offered.Add(Marshal.PtrToStringUTF8(name) ?? "");
                    g_free(name);
                }
                chosen = ImageTypes.FirstOrDefault(offered.Contains);
                Log.Write($"clipboard offers: {string.Join(", ", offered)}");
                if (chosen == null) { done.TrySetResult(null); return; }
                gtk_clipboard_request_contents(clipboard, gdk_atom_intern(chosen, false), contentsReceived, IntPtr.Zero);
            }
            catch (Exception ex) { Log.Error("gtk clipboard (targets)", ex); done.TrySetResult(null); }
        };
        pending = _ =>
        {
            try { gtk_clipboard_request_targets(gtk_clipboard_get(gdk_atom_intern("CLIPBOARD", false)), targetsReceived, IntPtr.Zero); }
            catch (Exception ex) { Log.Error("gtk clipboard (request)", ex); done.TrySetResult(null); }
            return false;
        };
        g_idle_add(pending, IntPtr.Zero);
        var finished = await Task.WhenAny(done.Task, Task.Delay(5000));
        return finished == done.Task ? done.Task.Result : null;
    }

    private static void OnGet(IntPtr clipboard, IntPtr selectionData, uint info, IntPtr userData)
    {
        try
        {
            if (info == HtmlTarget && html != null)
            {
                // The charset in the content itself: LibreOffice and others read text/html as Latin-1 otherwise.
                var bytes = Encoding.UTF8.GetBytes("<meta http-equiv=\"content-type\" content=\"text/html; charset=utf-8\">" + html);
                gtk_selection_data_set(selectionData, gtk_selection_data_get_target(selectionData), 8, bytes, bytes.Length);
            }
            else
            {
                var bytes = Encoding.UTF8.GetBytes(text);
                gtk_selection_data_set_text(selectionData, bytes, bytes.Length);
            }
        }
        catch (Exception ex)
        {
            Log.Error("gtk clipboard (serving)", ex);
        }
    }
}
