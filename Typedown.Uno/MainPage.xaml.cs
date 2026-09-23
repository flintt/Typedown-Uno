using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.Web.WebView2.Core;
using Typedown.Uno.Services;
using Typedown.Uno.ViewModels;
using Typedown.Uno.Views;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using Windows.System;

namespace Typedown.Uno;

public sealed partial class MainPage : Page, DocumentViewModel.IHostUi
{
    private const string EditorHost = "typedown.editor";

    /// <summary>What a freshly created window should open.</summary>
    public sealed record StartupOptions(string? File, bool RestoreSession, string Marker);

    private StartupOptions options = new(null, false, "");
    private Window? window;
    private IntPtr nativeWindow;
    private bool nativeIconApplied;
    private DataPackage? clipboardBatch;
    private DateTime clipboardBatchTime;

    private readonly AppSettings settings = AppSettings.Current;
    private EditorTransport? transport;
    private DocumentViewModel? document;
    private TabsViewModel? tabs;
    private string? startupFile;
    private string? workFolder;
    private FolderItem? folderRoot;
    private CancellationTokenSource? pendingSingleClick;
    private CancellationTokenSource? searchCts;
    private bool suppressTabSelection;
    private bool closing;

    public MainPage()
    {
        Loc.Apply(settings.Language);
        this.InitializeComponent();
        Loaded += OnLoaded;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is StartupOptions startup) options = startup;
        startupFile = options.File;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // The document model is built first and never depends on the web view: if the editor fails to come up
        // (missing WebKitGTK, a stalled native initialization) the shell must still be usable and say why.
        window = App.WindowFor(XamlRoot) ?? App.MainWindow;
        // This window's X11 id (Uno exposes no handle). It may not exist yet, so the lookup is retried later.
        NativeWindow();
        if (window != null) window.Activated += (_, _) => PublishNativeChrome();
        ApplyStrings();
        BuildMenus();
        ApplyTheme(post: false);
        ApplySidePane();
        transport = new EditorTransport(PostToEditor);
        document = new DocumentViewModel(transport, this, settings);
        tabs = new TabsViewModel(document, settings);
        document.PropertyChanged += (_, _) => DispatcherQueue.TryEnqueue(UpdateTitle);
        document.RunOnUi += work => DispatcherQueue.TryEnqueue(async () => await work());
        document.FileOpened += path => DispatcherQueue.TryEnqueue(() => OnFileOpened(path));
        tabs.TabsChanged += () => DispatcherQueue.TryEnqueue(UpdateTabBar);
        tabs.PropertyChanged += (_, _) => DispatcherQueue.TryEnqueue(UpdateTabBar);
        TabBar.TabItemsSource = tabs.Tabs;
        settings.PropertyChanged += OnSettingChanged;
        RegisterHostFunctions(transport);
        transport.MessageReceived += OnEditorMessage;

        // Documents are staged before the page loads: the editor's first GetSettings carries the active one.
        string? sessionFolder = null;
        if (startupFile != null)
        {
            await tabs.OpenFileAsync(startupFile);
        }
        else
        {
            // Only the window opened at startup restores the session; further windows start empty.
            switch (options.RestoreSession ? settings.FileStartupAction : FileStartupAction.NewFile)
            {
                case FileStartupAction.RestoreSession:
                    sessionFolder = await tabs.RestoreSessionAsync();
                    break;
                case FileStartupAction.OpenLast:
                    var last = settings.RecentFiles.FirstOrDefault(File.Exists);
                    if (last != null) await tabs.OpenFileAsync(last);
                    break;
            }
        }
        var startFolder = settings.FolderStartupAction switch
        {
            FolderStartupAction.OpenFixed => settings.StartupFolder,
            FolderStartupAction.OpenLast => sessionFolder ?? settings.LastFolder,
            _ => null,
        };
        if (startFolder != null && Directory.Exists(startFolder)) SetWorkFolder(startFolder);
        if (workFolder == null && document.FilePath != null) SetWorkFolder(Path.GetDirectoryName(document.FilePath)!);
        ApplyStatusBar();
        UpdateTitle();
        UpdateTabBar();
        HookWindowClosing();
        PublishNativeChrome();
        HookWheelFallback();

        await StartEditorAsync();
    }

    /// <summary>
    /// Brings the web view up. <c>EnsureCoreWebView2Async</c> is only awaited with a timeout: on some Linux setups
    /// it never completes even though the native view works, so the code falls back to waiting for the control to
    /// publish its CoreWebView2 and, failing that, still navigates (Uno creates the view when Source is set).
    /// </summary>
    private async Task StartEditorAsync()
    {
        Services.Log.Write("shell ready, initializing web view");
        try
        {
            var ensure = EditorView.EnsureCoreWebView2Async().AsTask();
            if (await Task.WhenAny(ensure, Task.Delay(TimeSpan.FromSeconds(5))) != ensure)
                Services.Log.Write("EnsureCoreWebView2Async did not complete in 5s, continuing");
            else
                await ensure;
        }
        catch (Exception ex)
        {
            Services.Log.Error("EnsureCoreWebView2Async failed", ex);
        }

        var core = EditorView.CoreWebView2;
        for (var i = 0; core == null && i < 50; i++)
        {
            await Task.Delay(200);
            core = EditorView.CoreWebView2;
        }
        if (core == null)
        {
            var missing = Services.Log.MissingLinuxLibraries();
            Services.Log.Write($"no CoreWebView2 after 15s; the editor cannot start (missing libs: {(missing.Count == 0 ? "none" : string.Join(", ", missing))})");
            SetStatus(missing.Count > 0
                ? $"Editor failed to start: missing {string.Join(", ", missing)} — run install-linux.sh"
                : $"Editor failed to start — see {Services.Log.Path}");
            return;
        }

        Services.Log.Write("web view initialized");
        core.WebMessageReceived += (_, args) =>
        {
            var raw = EditorTransport.GetRawMessage(args);
            if (raw != null) transport!.OnWebMessage(raw);
        };
        core.NavigationCompleted += (_, args) =>
        {
            editorPageLoaded |= args.IsSuccess;
            Services.Log.Write(args.IsSuccess ? "editor page loaded" : $"navigation failed: {args.WebErrorStatus}");
        };
        // The folder is relative to the app directory (Uno's X11 WebView joins it onto the base directory),
        // so it must stay relative — an absolute path would be concatenated onto the base directory.
        core.SetVirtualHostNameToFolderMapping(EditorHost, "Assets/Editor", CoreWebView2HostResourceAccessKind.Allow);
        core.NavigationCompleted += async (_, args) => { if (args.IsSuccess) { await PostShortcutMap(); await PostContextMenuStrings(); } };
        EditorView.Source = new Uri($"http://{EditorHost}/index.html");
        Services.Log.Write("navigating to the editor page");

        // Fallback: where the virtual host mapping does not take effect, load the page straight from disk. The
        // editor only needs same-origin access to its own folder, which a file:// URL gives it.
        await Task.Delay(TimeSpan.FromSeconds(8));
        if (editorPageLoaded || transport == null) return;
        var indexPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Editor", "index.html");
        Services.Log.Write($"virtual host did not load; falling back to {indexPath} (exists={File.Exists(indexPath)})");
        try
        {
            EditorView.Source = new Uri(indexPath);
        }
        catch (Exception ex)
        {
            Services.Log.Error("file fallback failed", ex);
        }
        await Task.Delay(TimeSpan.FromSeconds(8));
        if (editorPageLoaded) return;
        try
        {
            var probe = await EditorView.ExecuteScriptAsync("document.readyState + '|' + location.href");
            Services.Log.Write($"web view probe: {probe}");
        }
        catch (Exception ex)
        {
            Services.Log.Error("web view probe failed", ex);
        }
        var missingLibs = Services.Log.MissingLinuxLibraries();
        SetStatus(missingLibs.Count > 0
            ? $"Editor did not load: missing {string.Join(", ", missingLibs)} — run install-linux.sh"
            : $"Editor did not load — see {Services.Log.Path}");
    }

    private bool editorPageLoaded;
    private string? lastTocJson;
    private JsonNode? latestToc;

    /// <summary>Fills the outline from the most recent editor state (it is only tracked while visible).</summary>
    private void RefreshOutline()
    {
        lastTocJson = latestToc?.ToJsonString();
        OutlineList.ItemsSource = OutlineItem.FromToc(latestToc);
    }
    private string? pendingWordCount;
    private string? shownWordCount;
    private DispatcherTimer? wordCountTimer;

    private void StartWordCountTimer()
    {
        if (wordCountTimer == null)
        {
            wordCountTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            wordCountTimer.Tick += (_, _) =>
            {
                if (pendingWordCount == shownWordCount)
                {
                    wordCountTimer!.Stop();
                    return;
                }
                shownWordCount = pendingWordCount;
                WordCountText.Text = shownWordCount ?? "";
            };
        }
        if (!wordCountTimer.IsEnabled) wordCountTimer.Start();
    }

    // ---- editor bridge ------------------------------------------------------------------------------------------

    private async Task PostToEditor(string json)
    {
        var script = "window.__unoDeliver(" + JsonSerializer.Serialize(json) + ")";
        try { await EditorView.ExecuteScriptAsync(script); }
        catch (Exception ex) { Console.Error.WriteLine($"[Typedown.Uno] post to editor failed: {ex.Message}"); }
    }

    private Task Post(string name, object? args) => transport?.PostMessage(name, args) ?? Task.CompletedTask;

    /// <summary>The web view swallows key presses, so it must know which combinations to hand back.</summary>
    private Task PostShortcutMap() => Post("ShortcutMap", new { keys = System.Text.Json.Nodes.JsonNode.Parse(settings.Shortcuts.ForwardedKeysJson()) });

    /// <summary>Labels for the page's own context menu (a host flyout would be drawn behind the native view).</summary>
    private Task PostContextMenuStrings() => Post("ContextMenuStrings", new
    {
        copy = Loc.Get("Copy"),
        cut = Loc.Get("Cut"),
        paste = Loc.Get("Paste"),
        selectAll = Loc.Get("SelectAll"),
    });

    /// <summary>Puts the clipboard's text through the editor's paste handler, as Ctrl+V would.</summary>
    private async Task PasteClipboardTextAsync()
    {
        try
        {
            var view = Clipboard.GetContent();
            if (view == null || !view.Contains(StandardDataFormats.Text)) return;
            var text = await view.GetTextAsync();
            if (string.IsNullOrEmpty(text)) return;
            // Text copied from the editor also carries HTML; handing it over keeps bold, links and the rest,
            // exactly as Ctrl+V does.
            string? html = null;
            if (view.Contains(StandardDataFormats.Html))
            {
                try { html = await view.GetHtmlFormatAsync(); } catch { }
                // Windows wraps the fragment in a CF_HTML header; the editor wants the markup only.
                var start = html?.IndexOf("<!--StartFragment-->", StringComparison.OrdinalIgnoreCase) ?? -1;
                var end = html?.IndexOf("<!--EndFragment-->", StringComparison.OrdinalIgnoreCase) ?? -1;
                if (start >= 0 && end > start) html = html![(start + "<!--StartFragment-->".Length)..end];
            }
            await Post("Paste", new { type = "normal", text, html });
        }
        catch (Exception ex)
        {
            Services.Log.Error("paste from clipboard", ex);
        }
    }

    private void RegisterHostFunctions(EditorTransport t)
    {
        t.Handle("GetSettings", _ =>
        {
            document!.EditorReady = true;
            var options = settings.EditorOptions();
            foreach (var (key, value) in JsonSerializer.SerializeToNode(document.GetLoadPayload(), EditorTransport.JsonOptions)!.AsObject())
                options[key] = value?.DeepClone();
            return (object?)options;
        });
        t.Handle("GetCurrentTheme", _ => (object?)ThemePayload());
        t.Handle("ContentLoaded", _ => (object?)"");
        t.Handle("GetStringResources", _ => new { });
        t.Handle("LoadImage", args => new { url = args?["url"]?.GetValue<string>() ?? "" });
        t.Handle("ResizeTable", args => new { row = args?["row"]?.GetValue<int>() ?? 2, column = args?["column"]?.GetValue<int>() ?? 2 });
        t.Handle("SetClipboard", args =>
        {
            // One copy arrives as two calls (text/html then text/plain). A fresh package per call would leave
            // only the last format on the clipboard, so calls close together go into the same package.
            var type = args?["type"]?.GetValue<string>();
            var data = args?["data"]?.ToString() ?? "";
            var now = DateTime.UtcNow;
            if (clipboardBatch == null || now - clipboardBatchTime > TimeSpan.FromMilliseconds(500))
                clipboardBatch = new DataPackage();
            clipboardBatchTime = now;
            if (type == "text/html") clipboardBatch.SetHtmlFormat(data); else clipboardBatch.SetText(data);
            try { Clipboard.SetContent(clipboardBatch); } catch (Exception ex) { Services.Log.Error("set clipboard", ex); }
            return (object?)true;
        });
        t.Handle("OpenNewWindow", async args =>
        {
            var href = args?.GetValue<string>();
            if (!string.IsNullOrEmpty(href) && tabs != null)
            {
                var path = Path.IsPathRooted(href) ? href : Path.GetFullPath(Path.Combine(document!.BasePath, href));
                if (File.Exists(path)) await tabs.OpenFileAsync(path);
            }
            return null;
        });
        t.Handle("ExportCallback", async args =>
        {
            var html = args?["html"]?.GetValue<string>() ?? "";
            var path = args?["context"]?["filePath"]?.GetValue<string>();
            if (path != null)
            {
                await SafeFile.WriteAllTextAtomicAsync(path, html);
                if (path == printAfterExport)
                {
                    printAfterExport = null;
                    OpenPath(path); // the browser's print dialog does the printing / "save as PDF"
                    SetStatus(Loc.Get("PrintOpened"));
                }
                else
                {
                    SetStatus(Loc.Format("Exported", path));
                    if (settings.OpenFolderAfterExport) OpenContainingFolder(path);
                }
            }
            return (object?)true;
        });
        t.Handle("PrintHTML", _ => (object?)false);
        t.Handle("UnhandledException", args => { Console.Error.WriteLine($"[editor] {args}"); return (object?)null; });
    }

    private void OnEditorMessage(string name, JsonNode? args)
    {
        switch (name)
        {
            case "FileLoaded":
                Services.Log.Write($"editor handshake done ({args?["text"]?.GetValue<string>()?.Length ?? 0} chars)");
                DispatcherQueue.TryEnqueue(UpdateTitle);
                break;
            case "StateChange":
                // Fires on every keystroke: only touch the UI for what is actually visible, and never rebuild the
                // outline for an unchanged table of contents (rebuilding a ListView per keystroke is expensive).
                var state = args?["state"];
                if (settings.StatusBarOpen)
                {
                    var count = state?["wordCount"]?[settings.WordCountMethod switch
                    {
                        WordCountMethod.Characters => "character",
                        WordCountMethod.Paragraphs => "paragraph",
                        _ => "word",
                    }];
                    // Writing the status bar on every keystroke repaints the window; the count is only informative,
                    // so it is coalesced (a full repaint per character is the single most expensive thing here).
                    if (count != null)
                    {
                        pendingWordCount = Loc.Format("Words", count);
                        DispatcherQueue.TryEnqueue(StartWordCountTimer);
                    }
                }
                // Keep the last table of contents so switching to the outline shows it without waiting for an edit.
                latestToc = state?["toc"];
                if (settings.SidePaneOpen && settings.SidePanePage == 1 && !searchOpen)
                {
                    var tocJson = latestToc?.ToJsonString();
                    if (tocJson != null && tocJson != lastTocJson)
                    {
                        lastTocJson = tocJson;
                        var items = OutlineItem.FromToc(latestToc);
                        DispatcherQueue.TryEnqueue(() => OutlineList.ItemsSource = items);
                    }
                }
                break;
            case "OpenURI":
                var uri = args?["uri"]?.GetValue<string>();
                if (uri != null) DispatcherQueue.TryEnqueue(async () => { try { await Launcher.LaunchUriAsync(new Uri(uri)); } catch { } });
                break;
            case "FilesDropped":
                var dropped = args?["paths"]?.AsArray().Select(p => p?.GetValue<string>()).Where(p => p != null).Select(p => p!).ToList();
                if (dropped is { Count: > 0 }) DispatcherQueue.TryEnqueue(async () => await OpenDroppedAsync(dropped));
                break;
            case "ClipboardImageRequest":
                DispatcherQueue.TryEnqueue(async () => await InsertClipboardImageAsync());
                break;
            case "ClipboardSetText":
                var copyText = args?["text"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(copyText)) DispatcherQueue.TryEnqueue(() =>
                {
                    var package = new DataPackage();
                    package.SetText(copyText);
                    try { Clipboard.SetContent(package); } catch (Exception ex) { Services.Log.Error("copy to clipboard", ex); }
                });
                break;
            case "ClipboardTextRequest":
                // Paste from the page's context menu: WebKit refuses execCommand('paste'), so the host reads the
                // clipboard and hands the text to the editor's own paste handler.
                DispatcherQueue.TryEnqueue(async () => await PasteClipboardTextAsync());
                break;
            case "ImagePasted":
                var dataUrl = args?["dataUrl"]?.GetValue<string>();
                if (dataUrl != null) DispatcherQueue.TryEnqueue(async () => await InsertPastedImageAsync(dataUrl));
                break;
            case "Shortcut":
                var key = args?["key"]?.GetValue<string>() ?? "";
                var ctrl = args?["ctrl"]?.GetValue<bool>() ?? false;
                var shift = args?["shift"]?.GetValue<bool>() ?? false;
                var alt = args?["alt"]?.GetValue<bool>() ?? false;
                DispatcherQueue.TryEnqueue(async () => await HandleShortcutAsync(key, ctrl, shift, alt));
                break;
        }
    }

    // ---- images and drops ---------------------------------------------------------------------------------------

    /// <summary>Documents are opened as tabs; images are stored next to the document and linked.</summary>
    private async Task OpenDroppedAsync(IReadOnlyList<string> paths)
    {
        if (tabs == null || document == null) return;
        foreach (var path in paths)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    SetWorkFolder(path, explicitChoice: true);
                    settings.SidePaneOpen = true;
                    settings.SidePanePage = 0;
                    ApplySidePane();
                }
                else if (ImagePaths.IsImageFile(path))
                {
                    await InsertImageFileAsync(path);
                }
                else if (File.Exists(path))
                {
                    await tabs.OpenFileAsync(path);
                }
            }
            catch (Exception ex)
            {
                Services.Log.Error($"drop {path}", ex);
                await ShowErrorAsync(Loc.Get("Error"), ex.Message);
            }
        }
    }

    private async Task InsertImageFileAsync(string path)
    {
        if (document == null) return;
        try
        {
            var link = ImagePaths.PlaceImage(path, document.FilePath, settings);
            await PostInsertImage(link, Path.GetFileNameWithoutExtension(path));
            SetStatus(link);
        }
        catch (Exception ex)
        {
            Services.Log.Error("insert image", ex);
            await ShowErrorAsync(Loc.Get("Error"), ex.Message);
        }
    }

    /// <summary>Reads an image from the system clipboard (the page could not) and inserts it.</summary>
    private async Task InsertClipboardImageAsync()
    {
        if (document == null) return;
        try
        {
            var view = Clipboard.GetContent();
            if (view == null) return;
            byte[]? bytes = null;
            var extension = ".png";
            if (view.Contains(StandardDataFormats.Bitmap))
            {
                var reference = await view.GetBitmapAsync();
                using var stream = await reference.OpenReadAsync();
                bytes = await ReadAllAsync(stream);
            }
            else
            {
                // X11 clipboards hand out the raw MIME type instead of the WinRT bitmap format.
                var format = view.AvailableFormats.FirstOrDefault(f => f.StartsWith("image/", StringComparison.OrdinalIgnoreCase));
                if (format == null)
                {
                    Services.Log.Write($"clipboard has no image (formats: {string.Join(", ", view.AvailableFormats)})");
                    return;
                }
                extension = format switch { "image/jpeg" => ".jpg", "image/gif" => ".gif", "image/webp" => ".webp", "image/bmp" => ".bmp", _ => ".png" };
                var data = await view.GetDataAsync(format);
                bytes = data switch
                {
                    byte[] raw => raw,
                    Windows.Storage.Streams.IRandomAccessStream stream => await ReadAllAsync(stream),
                    Windows.Storage.Streams.IBuffer buffer => buffer.ToArray(),
                    string text when text.StartsWith("data:", StringComparison.Ordinal) => ImagePaths.DecodeDataUrl(text)?.bytes,
                    _ => null,
                };
                if (bytes == null)
                {
                    Services.Log.Write($"clipboard image format {format} returned {data?.GetType().Name ?? "null"}");
                    return;
                }
            }
            var link = ImagePaths.SaveImageBytes(bytes, extension, document.FilePath, settings);
            await PostInsertImage(link, "image");
            SetStatus(link);
        }
        catch (Exception ex)
        {
            Services.Log.Error("clipboard image", ex);
        }
    }

    private static async Task<byte[]> ReadAllAsync(Windows.Storage.Streams.IRandomAccessStream stream)
    {
        var bytes = new byte[stream.Size];
        using var reader = new Windows.Storage.Streams.DataReader(stream.GetInputStreamAt(0));
        await reader.LoadAsync((uint)stream.Size);
        reader.ReadBytes(bytes);
        return bytes;
    }

    private async Task InsertPastedImageAsync(string dataUrl)
    {
        if (document == null) return;
        try
        {
            var decoded = ImagePaths.DecodeDataUrl(dataUrl);
            if (decoded == null) return;
            var link = ImagePaths.SaveImageBytes(decoded.Value.bytes, decoded.Value.extension, document.FilePath, settings);
            await PostInsertImage(link, "image");
            SetStatus(link);
        }
        catch (Exception ex)
        {
            Services.Log.Error("paste image", ex);
            await ShowErrorAsync(Loc.Get("Error"), ex.Message);
        }
    }

    private static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".svg" };

    /// <summary>The editor's insertImage takes {alt, src, title} — a bare string inserts an empty image.</summary>
    private Task PostInsertImage(string link, string alt) =>
        Post("InsertImage", new { src = settings.EncodeImageLinks ? ImagePaths.EncodeLink(link) : link, alt, title = "" });

    private async Task InsertImageDialogAsync()
    {
        string? path;
        if (UseBuiltInPicker)
        {
            path = await ShowBuiltInPickerAsync(FilePickerDialog.PickerMode.OpenFile, null, ImageExtensions);
        }
        else
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
            foreach (var ext in ImageExtensions) picker.FileTypeFilter.Add(ext);
            InitPicker(picker);
            path = (await picker.PickSingleFileAsync())?.Path;
        }
        if (path != null) await InsertImageFileAsync(path);
    }

    // ---- menus ------------------------------------------------------------------------------------------------------

    private MenuFlyoutSubItem? recentMenu;

    private readonly List<System.ComponentModel.PropertyChangedEventHandler> menuToggleHandlers = new();

    private void BuildMenus()
    {
        foreach (var handler in menuToggleHandlers) settings.PropertyChanged -= handler;
        menuToggleHandlers.Clear();
        KeyboardAccelerators.Clear(); // rebuilt below from the current bindings
        commandActions.Clear();
        MainMenu.Items.Clear();
        var file = new MenuBarItem { Title = Loc.Get("File") };
        file.Items.Add(Item("New", async () => { if (tabs != null) await tabs.NewTabAsync(); }, ShortcutCommand.NewTab));
        file.Items.Add(Item("NewWindow", () => NewWindow(), ShortcutCommand.NewWindow));
        file.Items.Add(Item("Open", async () => await OpenFileDialogAsync(), ShortcutCommand.Open));
        file.Items.Add(Item("OpenFolder", async () => await OpenFolderDialogAsync(), ShortcutCommand.OpenFolder));
        recentMenu = new MenuFlyoutSubItem { Text = Loc.Get("Recent") };
        file.Items.Add(recentMenu);
        FillRecent();
        file.Items.Add(new MenuFlyoutSeparator());
        file.Items.Add(Item("Save", async () => { if (document != null) await document.SaveAsync(); }, ShortcutCommand.Save));
        file.Items.Add(Item("SaveAs", async () => { if (document != null) await document.SaveAsAsync(); }, ShortcutCommand.SaveAs));
        file.Items.Add(Item("ExportHtml", async () => await ExportHtmlAsync(), ShortcutCommand.ExportHtml));
        file.Items.Add(Item("PrintPdf", async () => await PrintAsync(), ShortcutCommand.Print));
        file.Items.Add(Item("ShareHedgeDoc", async () => await ShareToHedgeDocAsync(), ShortcutCommand.ShareHedgeDoc));
        file.Items.Add(new MenuFlyoutSeparator());
        file.Items.Add(Item("Settings", async () => await ShowSettingsAsync(), ShortcutCommand.Settings));
        file.Items.Add(Item("CloseTab", async () => { if (tabs != null) await tabs.CloseTabAsync(tabs.ActiveTab); }, ShortcutCommand.CloseTab));
        file.Items.Add(Item("Exit", async () => await ExitAsync(), ShortcutCommand.Exit));
        MainMenu.Items.Add(file);

        var edit = new MenuBarItem { Title = Loc.Get("Edit") };
        edit.Items.Add(Item("Find", () => ShowFind(true), ShortcutCommand.Find));
        edit.Items.Add(Item("FindNext", async () => await Post("Find", new { action = "next" }), ShortcutCommand.FindNext));
        edit.Items.Add(Item("FindPrevious", async () => await Post("Find", new { action = "prev" }), ShortcutCommand.FindPrevious));
        edit.Items.Add(new MenuFlyoutSeparator());
        edit.Items.Add(Item("SearchInFolder", () => OnSearchButtonClick(this, new RoutedEventArgs()), ShortcutCommand.SearchInFolder));
        edit.Items.Add(new MenuFlyoutSeparator());
        edit.Items.Add(Item("SelectAll", async () => await Post("SelectAll", null), ShortcutCommand.SelectAll));
        MainMenu.Items.Add(edit);

        var paragraph = new MenuBarItem { Title = Loc.Get("Paragraph") };
        var heading = new MenuFlyoutSubItem { Text = Loc.Get("Heading") };
        for (var i = 1; i <= 6; i++)
        {
            var level = i;
            heading.Items.Add(new MenuFlyoutItem { Text = Loc.Format("HeadingN", level) }.OnClick(async () => await Post("UpdateParagraph", $"heading {level}")));
        }
        paragraph.Items.Add(heading);
        paragraph.Items.Add(Item("ParagraphPlain", async () => await Post("UpdateParagraph", "paragraph")));
        paragraph.Items.Add(Item("IncreaseHeading", async () => await Post("UpdateParagraph", "upgrade heading")));
        paragraph.Items.Add(Item("DecreaseHeading", async () => await Post("UpdateParagraph", "degrade heading")));
        paragraph.Items.Add(new MenuFlyoutSeparator());
        paragraph.Items.Add(Item("Table", async () => await InsertTableAsync()));
        paragraph.Items.Add(Item("CodeFences", async () => await Post("UpdateParagraph", "pre")));
        paragraph.Items.Add(Item("MathBlock", async () => await Post("UpdateParagraph", "mathblock")));
        paragraph.Items.Add(Item("Quote", async () => await Post("UpdateParagraph", "blockquote")));
        paragraph.Items.Add(Item("QuoteIncrease", async () => await Post("UpdateParagraph", "blockquote-increase")));
        paragraph.Items.Add(Item("QuoteDecrease", async () => await Post("UpdateParagraph", "blockquote-decrease")));
        paragraph.Items.Add(new MenuFlyoutSeparator());
        paragraph.Items.Add(Item("OrderedList", async () => await Post("UpdateParagraph", "ol-order")));
        paragraph.Items.Add(Item("UnorderedList", async () => await Post("UpdateParagraph", "ul-bullet")));
        paragraph.Items.Add(Item("TaskList", async () => await Post("UpdateParagraph", "ul-task")));
        paragraph.Items.Add(new MenuFlyoutSeparator());
        var chart = new MenuFlyoutSubItem { Text = Loc.Get("Chart") };
        foreach (var (key, type) in new[] { ("Mermaid", "mermaid"), ("FlowChart", "flowchart"), ("Sequence", "sequence"), ("VegaLite", "vega-lite"), ("PlantUml", "plantuml") })
            chart.Items.Add(Item(key, async () => await Post("UpdateParagraph", type)));
        paragraph.Items.Add(chart);
        paragraph.Items.Add(Item("HorizontalLine", async () => await Post("UpdateParagraph", "hr")));
        paragraph.Items.Add(Item("Toc", async () => await Post("UpdateParagraph", "toc")));
        paragraph.Items.Add(Item("FrontMatter", async () => await Post("UpdateParagraph", "front-matter")));
        paragraph.Items.Add(Item("Footnote", async () => await Post("UpdateParagraph", "footnote")));
        paragraph.Items.Add(Item("LinkReference", async () => await Post("UpdateParagraph", "linkref")));
        paragraph.Items.Add(new MenuFlyoutSeparator());
        paragraph.Items.Add(Item("InsertBefore", async () => await Post("InsertParagraph", "before")));
        paragraph.Items.Add(Item("InsertAfter", async () => await Post("InsertParagraph", "after")));
        MainMenu.Items.Add(paragraph);

        var format = new MenuBarItem { Title = Loc.Get("Format") };
        foreach (var (key, type) in new[] { ("Strong", "strong"), ("Emphasis", "em"), ("Underline", "u"), ("InlineCode", "inline_code"), ("InlineMath", "inline_math"), ("Strikethrough", "del"), ("Highlight", "mark"), ("Hyperlink", "link"), ("Image", "image") })
            format.Items.Add(Item(key, async () => await Post("Format", type)));
        format.Items.Add(Item("InsertImage", async () => await InsertImageDialogAsync(), ShortcutCommand.InsertImage));
        format.Items.Add(new MenuFlyoutSeparator());
        format.Items.Add(Item("ClearFormat", async () => await Post("Format", "clear")));
        MainMenu.Items.Add(format);

        var view = new MenuBarItem { Title = Loc.Get("View") };
        view.Items.Add(Toggle("SourceCode", () => settings.SourceCode, v => settings.SourceCode = v, ShortcutCommand.SourceCode));
        // Focus and typewriter mode both follow the caret, so they mean nothing in reading mode (no caret) or in
        // source mode (a plain text editor): grey them out rather than let them look enabled and do nothing.
        var focusItem = Toggle("FocusMode", () => settings.FocusMode, v => settings.FocusMode = v);
        var typewriterItem = Toggle("Typewriter", () => settings.Typewriter, v => settings.Typewriter = v);
        void UpdateModeAvailability()
        {
            var caretModes = !settings.ReadOnly && !settings.SourceCode;
            focusItem.IsEnabled = caretModes;
            typewriterItem.IsEnabled = caretModes;
        }
        UpdateModeAvailability();
        void OnModeChanged(object? _, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(AppSettings.ReadOnly) or nameof(AppSettings.SourceCode))
                DispatcherQueue.TryEnqueue(UpdateModeAvailability);
        }
        settings.PropertyChanged += OnModeChanged;
        menuToggleHandlers.Add(OnModeChanged);
        view.Items.Add(focusItem);
        view.Items.Add(typewriterItem);
        view.Items.Add(Toggle("ReadOnly", () => settings.ReadOnly, v => settings.ReadOnly = v, ShortcutCommand.ReadingMode));
        view.Items.Add(new MenuFlyoutSeparator());
        view.Items.Add(Toggle("SidePane", () => settings.SidePaneOpen, v => settings.SidePaneOpen = v, ShortcutCommand.SidePane));
        var theme = new MenuFlyoutSubItem { Text = Loc.Get("Theme") };
        foreach (var t in Enum.GetValues<AppTheme>())
        {
            var value = t;
            var item = new RadioMenuFlyoutItem { Text = Loc.Get("Theme" + t), IsChecked = settings.Theme == t, GroupName = "theme" };
            item.Click += (_, _) => settings.Theme = value;
            theme.Items.Add(item);
        }
        view.Items.Add(theme);
        MainMenu.Items.Add(view);

        var help = new MenuBarItem { Title = Loc.Get("Help") };
        help.Items.Add(Item("About", async () => await ShowAboutAsync()));
        MainMenu.Items.Add(help);
    }

    private MenuFlyoutItem Item(string key, Action action, ShortcutCommand? command = null)
    {
        var item = new MenuFlyoutItem { Text = Loc.Get(key) };
        item.Click += (_, _) => action();
        if (command != null) BindShortcut(item, command.Value, action);
        return item;
    }

    /// <summary>Shows the binding on the menu item and registers the matching page accelerator.</summary>
    private void BindShortcut(MenuFlyoutItemBase item, ShortcutCommand command, Action action)
    {
        commandActions[command] = action;
        var shortcut = settings.Shortcuts.Get(command);
        if (item is MenuFlyoutItem menuItem) menuItem.KeyboardAcceleratorTextOverride = shortcut.ToString();
        if (shortcut.ToAccelerator() is not { } accelerator) return;
        // Page-scoped: works while the shell has focus. Inside the editor (a native web view) uno-bridge.js
        // forwards the key press instead — see OnEditorMessage("Shortcut").
        var keyboardAccelerator = new KeyboardAccelerator { Key = accelerator.key, Modifiers = accelerator.modifiers };
        keyboardAccelerator.Invoked += (_, e) => { e.Handled = true; action(); };
        KeyboardAccelerators.Add(keyboardAccelerator);
    }

    private readonly Dictionary<ShortcutCommand, Action> commandActions = new();

    private ToggleMenuFlyoutItem Toggle(string key, Func<bool> get, Action<bool> set, ShortcutCommand? command = null)
    {
        var item = new ToggleMenuFlyoutItem { Text = Loc.Get(key), IsChecked = get() };
        item.Click += (_, _) => set(item.IsChecked);
        void Flip() { set(!get()); item.IsChecked = get(); }
        if (command != null) BindShortcut(item, command.Value, Flip);
        void OnChanged(object? _, System.ComponentModel.PropertyChangedEventArgs __) => DispatcherQueue.TryEnqueue(() => item.IsChecked = get());
        settings.PropertyChanged += OnChanged;
        menuToggleHandlers.Add(OnChanged); // dropped when the menus are rebuilt (see BuildMenus)
        return item;
    }

    private void FillRecent()
    {
        if (recentMenu == null) return;
        recentMenu.Items.Clear();
        if (settings.RecentFiles.Count == 0)
        {
            recentMenu.Items.Add(new MenuFlyoutItem { Text = Loc.Get("NoRecent"), IsEnabled = false });
            return;
        }
        foreach (var path in settings.RecentFiles)
        {
            var p = path;
            recentMenu.Items.Add(new MenuFlyoutItem { Text = Path.GetFileName(p) + "  —  " + Path.GetDirectoryName(p) }.OnClick(async () => { if (tabs != null) await tabs.OpenFileAsync(p); }));
        }
        recentMenu.Items.Add(new MenuFlyoutSeparator());
        recentMenu.Items.Add(Item("ClearRecent", () => settings.RecentFiles = new()));
    }

    private async Task HandleShortcutAsync(string key, bool ctrl, bool shift, bool alt = false)
    {
        if (tabs == null || document == null) return;
        // Bindings first: the editor forwards the keys the map asked for.
        if (settings.Shortcuts.Find(key, ctrl, shift, alt) is { } command && commandActions.TryGetValue(command, out var action))
        {
            action();
            return;
        }
        switch ((key.ToLowerInvariant(), ctrl, shift))
        {
            case ("s", true, false): await document.SaveAsync(); break;
            case ("s", true, true): await document.SaveAsAsync(); break;
            case ("o", true, false): await OpenFileDialogAsync(); break;
            case ("o", true, true): await OpenFolderDialogAsync(); break;
            case ("n", true, false): await tabs.NewTabAsync(); break;
            case ("w", true, false): await tabs.CloseTabAsync(tabs.ActiveTab); break;
            case ("p", true, false): await PrintAsync(); break;
            case ("f", true, false): ShowFind(true); break;
            case ("f", true, true): OnSearchButtonClick(this, new RoutedEventArgs()); break;
            case ("b", true, true): settings.SidePaneOpen = !settings.SidePaneOpen; break;
            case ("r", true, true): settings.ReadOnly = !settings.ReadOnly; break;
            case ("/", true, false): settings.SourceCode = !settings.SourceCode; break;
            case (",", true, false): await ShowSettingsAsync(); break;
            case ("tab", true, false): await tabs.SwitchRelativeAsync(1); break;
            case ("tab", true, true): await tabs.SwitchRelativeAsync(-1); break;
        }
    }

    // ---- files & folders --------------------------------------------------------------------------------------------

    private async Task OpenFileDialogAsync()
    {
        var path = await PickOpenFileAsync();
        if (path != null && tabs != null) await tabs.OpenFileAsync(path);
    }

    private async Task OpenFolderDialogAsync()
    {
        string? path;
        if (UseBuiltInPicker)
        {
            path = await ShowBuiltInPickerAsync(FilePickerDialog.PickerMode.Folder);
        }
        else
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            picker.FileTypeFilter.Add("*");
            InitPicker(picker);
            path = (await picker.PickSingleFolderAsync())?.Path;
        }
        if (path == null) return;
        SetWorkFolder(path, explicitChoice: true);
        settings.SidePaneOpen = true;
        settings.SidePanePage = 0;
        ApplySidePane();
    }

    private bool folderIsExplicit;

    private void SetWorkFolder(string folder, bool explicitChoice = false)
    {
        workFolder = folder;
        folderIsExplicit = explicitChoice || folderIsExplicit;
        settings.LastFolder = folder;
        folderRoot = new FolderItem(folder, true);
        folderRoot.EnsureChildren();
        FileTree.ItemsSource = folderRoot.Children;
        SidePaneTitle.Text = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar));
        SidePaneStatus.Text = folderRoot.Children.Count == 0 ? Loc.Get("NoResults") : "";
    }

    private void OnFileOpened(string path)
    {
        settings.AddRecent(path);
        FillRecent();
        var dir = Path.GetDirectoryName(path)!;
        if (workFolder == null || (!folderIsExplicit && !dir.StartsWith(workFolder, StringComparison.OrdinalIgnoreCase)))
            SetWorkFolder(dir);
    }

    private void OnFileTreeItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is not FolderItem item || item.IsFolder) return;
        pendingSingleClick?.Cancel();
        var cts = pendingSingleClick = new CancellationTokenSource();
        _ = Task.Delay(250, cts.Token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            DispatcherQueue.TryEnqueue(async () => { if (tabs != null) await tabs.OpenFileAsync(item.FullPath, preview: true); });
        }, TaskScheduler.Default);
    }

    private async void OnFileTreeDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        pendingSingleClick?.Cancel();
        if ((e.OriginalSource as FrameworkElement)?.DataContext is FolderItem item && !item.IsFolder && tabs != null)
            await tabs.OpenFileAsync(item.FullPath, preview: false);
    }

    private async void OnOpenFolderClick(object sender, RoutedEventArgs e) => await OpenFolderDialogAsync();

    private void OnFileTreeRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var item = (e.OriginalSource as FrameworkElement)?.DataContext as FolderItem;
        var target = item ?? (workFolder != null ? folderRoot : null);
        if (target == null) return;
        var folder = target.IsFolder ? target.FullPath : Path.GetDirectoryName(target.FullPath)!;
        var menu = new MenuFlyout();
        void Add(string key, Action action) { var i = new MenuFlyoutItem { Text = Loc.Get(key) }; i.Click += (_, _) => action(); menu.Items.Add(i); }
        if (!target.IsFolder)
        {
            Add("Open", async () => { if (tabs != null) await tabs.OpenFileAsync(target.FullPath); });
            Add("OpenInNewWindow", () => NewWindow(target.FullPath));
        }
        Add("NewFileHere", async () => await CreateInFolderAsync(folder, file: true));
        Add("NewFolderHere", async () => await CreateInFolderAsync(folder, file: false));
        menu.Items.Add(new MenuFlyoutSeparator());
        if (item != null)
        {
            Add("Rename", async () => await RenameAsync(item));
            Add("Delete", async () => await DeleteAsync(item));
            Add("CopyPath", () => { var p = new DataPackage(); p.SetText(item.FullPath); Clipboard.SetContent(p); });
        }
        Add("RevealInFileManager", () => OpenPath(folder));
        Add("Refresh", () => RefreshTree());
        menu.ShowAt((FrameworkElement)sender, e.GetPosition((UIElement)sender));
        e.Handled = true;
    }

    private void RefreshTree()
    {
        if (workFolder == null) return;
        SetWorkFolder(workFolder, folderIsExplicit);
    }

    private async Task<string?> AskNameAsync(string titleKey, string initial)
    {
        var box = new TextBox { Text = initial, SelectionStart = 0, SelectionLength = Path.GetFileNameWithoutExtension(initial).Length };
        var dialog = new ContentDialog
        {
            Title = Loc.Get(titleKey),
            Content = box,
            PrimaryButtonText = Loc.Get("OK"),
            CloseButtonText = Loc.Get("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
            RequestedTheme = DialogTheme,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
        var name = box.Text.Trim();
        return string.IsNullOrEmpty(name) ? null : name;
    }

    private async Task CreateInFolderAsync(string folder, bool file)
    {
        var name = await AskNameAsync(file ? "NewFileHere" : "NewFolderHere", file ? "untitled.md" : "folder");
        if (name == null) return;
        try
        {
            var path = Path.Combine(folder, name);
            if (file)
            {
                if (!File.Exists(path)) await SafeFile.WriteAllTextAtomicAsync(path, "");
                RefreshTree();
                if (tabs != null) await tabs.OpenFileAsync(path);
            }
            else
            {
                Directory.CreateDirectory(path);
                RefreshTree();
            }
        }
        catch (Exception ex)
        {
            await ShowErrorAsync(Loc.Get("Error"), ex.Message);
        }
    }

    private async Task RenameAsync(FolderItem item)
    {
        var name = await AskNameAsync("Rename", item.Name);
        if (name == null || name == item.Name) return;
        try
        {
            var target = Path.Combine(Path.GetDirectoryName(item.FullPath)!, name);
            if (item.IsFolder) Directory.Move(item.FullPath, target);
            else File.Move(item.FullPath, target);
            // A renamed open document keeps its tab: point it at the new path.
            if (!item.IsFolder && tabs?.FindByPath(item.FullPath) is { } tab) tab.FilePath = target;
            RefreshTree();
        }
        catch (Exception ex)
        {
            await ShowErrorAsync(Loc.Get("Error"), ex.Message);
        }
    }

    private async Task DeleteAsync(FolderItem item)
    {
        if (!await ConfirmAsync(Loc.Get("Delete"), Loc.Format("DeleteConfirm", item.Name), Loc.Get("Delete"), Loc.Get("Cancel"))) return;
        try
        {
            if (item.IsFolder) Directory.Delete(item.FullPath, recursive: true);
            else File.Delete(item.FullPath);
            RefreshTree();
        }
        catch (Exception ex)
        {
            await ShowErrorAsync(Loc.Get("Error"), ex.Message);
        }
    }

    // ---- side pane width ------------------------------------------------------------------------------------------

    private bool resizingSidePane;
    private double resizeStartX, resizeStartWidth;

    private void OnSplitterPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        resizingSidePane = true;
        resizeStartX = e.GetCurrentPoint(this).Position.X;
        resizeStartWidth = SidePaneColumn.Width.Value;
        SidePaneSplitter.CapturePointer(e.Pointer);
    }

    private void OnSplitterPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!resizingSidePane) return;
        var width = Math.Clamp(resizeStartWidth + (e.GetCurrentPoint(this).Position.X - resizeStartX), 160, Math.Max(200, ActualWidth - 320));
        SidePaneColumn.Width = new GridLength(width);
    }

    private void OnSplitterPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!resizingSidePane) return;
        resizingSidePane = false;
        SidePaneSplitter.ReleasePointerCapture(e.Pointer);
        settings.SidePaneWidth = SidePaneColumn.Width.Value;
    }

    private void OnSplitterPointerEntered(object sender, PointerRoutedEventArgs e) =>
        ProtectedCursor = Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.SizeWestEast);

    private void OnSplitterPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (!resizingSidePane) ProtectedCursor = Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.Arrow);
    }

    private async void OnOutlineItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is OutlineItem item) await Post("ScrollTo", new { slug = item.Slug });
    }

    private void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs e)
    {
        searchCts?.Cancel();
        var query = SearchBox.Text.Trim();
        var root = workFolder;
        if (query.Length == 0 || root == null) { SearchResults.ItemsSource = null; SearchStatus.Text = root == null ? Loc.Get("NoFolder") : ""; return; }
        var cts = searchCts = new CancellationTokenSource();
        var results = new System.Collections.ObjectModel.ObservableCollection<SearchResultItem>();
        SearchResults.ItemsSource = results;
        SearchStatus.Text = "…";
        _ = Task.Run(async () =>
        {
            await Task.Delay(300, cts.Token);
            try
            {
                var (files, hits) = FolderSearch.Run(root, query, item => DispatcherQueue.TryEnqueue(() => { if (!cts.IsCancellationRequested) results.Add(item); }), cts.Token);
                DispatcherQueue.TryEnqueue(() => { if (!cts.IsCancellationRequested) SearchStatus.Text = hits == 0 ? Loc.Get("NoResults") : Loc.Format("Results", files, hits); });
            }
            catch (OperationCanceledException) { }
        }, cts.Token);
    }

    private async void OnSearchResultClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not SearchResultItem item || tabs == null) return;
        var query = SearchBox.Text.Trim();
        await tabs.OpenFileAsync(item.FullPath, preview: true);
        await Task.Delay(400);
        ShowFind(true, query);
    }

    // ---- tabs ------------------------------------------------------------------------------------------------------

    private void UpdateTabBar()
    {
        if (tabs == null) return;
        TabBar.Visibility = tabs.Tabs.Count > 1 || settings.AlwaysShowTabBar ? Visibility.Visible : Visibility.Collapsed;
        suppressTabSelection = true;
        try { TabBar.SelectedItem = tabs.ActiveTab; }
        finally { suppressTabSelection = false; }
        UpdateTitle();
    }

    private async void OnTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (suppressTabSelection || tabs == null) return;
        if (TabBar.SelectedItem is DocumentTab tab && tab != tabs.ActiveTab) await tabs.SwitchToAsync(tab);
    }

    private async void OnTabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        if (args.Item is DocumentTab tab && tabs != null) await tabs.CloseTabAsync(tab);
    }

    private async void OnAddTabClick(TabView sender, object args)
    {
        if (tabs != null) await tabs.NewTabAsync();
    }

    // ---- find (the bar itself lives in the page: uno-bridge.js) ------------------------------------------------

    private void ShowFind(bool show, string? text = null) => _ = show ? Post("ShowFind", new { value = text, opt = SearchOptions() }) : Post("HideFind", null);

    /// <summary>Search options the in-page find bar passes to the editor (see Settings → Find).</summary>
    private object SearchOptions() => new
    {
        searchIsCaseSensitive = settings.FindCaseSensitive,
        searchIsWholeWord = settings.FindWholeWord,
        searchIsRegexp = settings.FindRegex,
    };

    // ---- side pane, theme, settings -----------------------------------------------------------------------------------

    private void OnSideNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (updatingSideNav || args.SelectedItem is not NavigationViewItem item) return;
        settings.SidePanePage = int.Parse((string)item.Tag);
        ApplySidePane();
        if (settings.SidePanePage == 1) RefreshOutline();
    }

    private bool updatingSideNav;

    /// <summary>Folder search takes over the pane (and comes back with its close button), as on Windows.</summary>
    private void OnSearchButtonClick(object sender, RoutedEventArgs e)
    {
        settings.SidePaneOpen = true;
        searchOpen = true;
        ApplySidePane();
        // Focus only sticks once the panel has been realized and laid out.
        DispatcherQueue.TryEnqueue(async () =>
        {
            await Task.Delay(80);
            SearchBox.Focus(FocusState.Programmatic);
        });
    }

    private void OnSearchCloseClick(object sender, RoutedEventArgs e)
    {
        searchOpen = false;
        ApplySidePane();
    }

    private bool searchOpen;

    private void ApplyStatusBar() => StatusBar.Visibility = settings.StatusBarOpen ? Visibility.Visible : Visibility.Collapsed;

    private void ApplySidePane()
    {
        var open = settings.SidePaneOpen;
        SidePane.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        SidePaneSplitter.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        SidePaneColumn.Width = open ? new GridLength(Math.Max(180, settings.SidePaneWidth)) : new GridLength(0);
        var page = Math.Clamp(settings.SidePanePage, 0, 1);
        updatingSideNav = true;
        try
        {
            SideNav.SelectedItem = page == 0 ? FilesNavItem : OutlineNavItem;
        }
        finally
        {
            updatingSideNav = false;
        }
        SearchPanel.Visibility = searchOpen ? Visibility.Visible : Visibility.Collapsed;
        SideNav.Visibility = searchOpen ? Visibility.Collapsed : Visibility.Visible;
        FileTree.Visibility = page == 0 ? Visibility.Visible : Visibility.Collapsed;
        OutlineList.Visibility = page == 1 ? Visibility.Visible : Visibility.Collapsed;
        SidePaneTitle.Visibility = page == 0 ? Visibility.Visible : Visibility.Collapsed;
        OpenFolderButton.Visibility = page == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (page == 0 && workFolder == null) SidePaneStatus.Text = Loc.Get("NoFolder");
    }

    private bool IsDarkTheme => settings.Theme switch
    {
        AppTheme.Light => false,
        AppTheme.Dark or AppTheme.Black => true,
        _ => Application.Current.RequestedTheme == ApplicationTheme.Dark,
    };

    private object ThemePayload()
    {
        var name = settings.Theme == AppTheme.Black ? "Black" : IsDarkTheme ? "Dark" : "Light";
        var bg = name switch { "Black" => (0, 0, 0), "Dark" => (32, 32, 32), _ => (249, 249, 249) };
        // Dictionary keys keep their case (the editor reads background.R/G/B/A but accentColor.r/g/b/a).
        return new { theme = name, accentColor = new { r = 0, g = 120, b = 212, a = 1 }, background = new Dictionary<string, int> { ["R"] = bg.Item1, ["G"] = bg.Item2, ["B"] = bg.Item3, ["A"] = 1 } };
    }

    private void ApplyTheme(bool post)
    {
        if (window?.Content is FrameworkElement root)
            root.RequestedTheme = settings.Theme == AppTheme.System ? ElementTheme.Default : IsDarkTheme ? ElementTheme.Dark : ElementTheme.Light;
        if (post) _ = Post("ThemeChanged", ThemePayload());
    }

    private void OnSettingChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        var name = e.PropertyName ?? "";
        DispatcherQueue.TryEnqueue(() =>
        {
            if (AppSettings.IsEditorOption(name))
            {
                var value = typeof(AppSettings).GetProperty(name)!.GetValue(settings);
                _ = Post("SettingsChanged", new Dictionary<string, object?> { [char.ToLowerInvariant(name[0]) + name[1..]] = value });
            }
            switch (name)
            {
                case nameof(AppSettings.Theme): ApplyTheme(post: true); break;
                case nameof(AppSettings.SidePaneOpen) or nameof(AppSettings.SidePanePage) or nameof(AppSettings.SidePaneWidth):
                    ApplySidePane();
                    if (settings.SidePaneOpen && settings.SidePanePage == 1) RefreshOutline();
                    break;
                case nameof(AppSettings.AlwaysShowTabBar): UpdateTabBar(); break;
                case nameof(AppSettings.StatusBarOpen): ApplyStatusBar(); break;
                case nameof(AppSettings.WordCountMethod): lastTocJson = null; break;
                case nameof(AppSettings.RecentFiles): FillRecent(); break;
                case nameof(AppSettings.Language): Loc.Apply(settings.Language); ApplyStrings(); BuildMenus(); UpdateTitle(); _ = PostContextMenuStrings(); break;
                case nameof(AppSettings.Shortcuts): BuildMenus(); _ = PostShortcutMap(); break;
            }
        });
    }

    private void ApplyStrings()
    {
        FilesNavItem.Content = Loc.Get("Files");
        OutlineNavItem.Content = Loc.Get("Outline");
        ToolTipService.SetToolTip(SearchButton, Loc.Get("SearchInFolder"));
        SearchBox.PlaceholderText = Loc.Get("SearchPlaceholder");
        OpenFolderButton.Content = Loc.Get("OpenFolder");
    }

    private ElementTheme DialogTheme => settings.Theme == AppTheme.System ? ElementTheme.Default : IsDarkTheme ? ElementTheme.Dark : ElementTheme.Light;

    private async Task ShowSettingsAsync()
    {
        var dialog = new SettingsDialog(settings) { XamlRoot = XamlRoot, RequestedTheme = DialogTheme };
        await dialog.ShowAsync();
    }

    private async Task InsertTableAsync()
    {
        var rows = new NumberBox { Header = "Rows", Value = 3, Minimum = 1, Maximum = 50, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline };
        var cols = new NumberBox { Header = "Columns", Value = 3, Minimum = 1, Maximum = 20, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline };
        var dialog = new ContentDialog { Title = Loc.Get("Table"), Content = new StackPanel { Spacing = 8, Children = { rows, cols } }, PrimaryButtonText = Loc.Get("OK"), CloseButtonText = Loc.Get("Cancel"), XamlRoot = XamlRoot, RequestedTheme = DialogTheme };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            await Post("InsertTable", new { rows = (int)rows.Value, columns = (int)cols.Value });
    }

    private async Task ExportHtmlAsync()
    {
        if (document == null) return;
        var suggested = Path.GetFileNameWithoutExtension(document.FileName) + ".html";
        string? path;
        if (UseBuiltInPicker)
        {
            path = await ShowBuiltInPickerAsync(FilePickerDialog.PickerMode.SaveFile, suggested, new[] { ".html", ".htm" });
        }
        else
        {
            var picker = new FileSavePicker { SuggestedFileName = suggested };
            picker.FileTypeChoices.Add("HTML", new List<string> { ".html" });
            InitPicker(picker);
            path = (await picker.PickSaveFileAsync())?.Path;
        }
        if (path == null) return;
        await Post("Export", new { type = "html", context = new { filePath = path }, basePath = document.BasePath, title = Path.GetFileNameWithoutExtension(document.FileName), options = new { } });
    }

    /// <summary>
    /// Renders the document to a temporary HTML file and opens it in the default browser, where the system print
    /// dialog prints it or saves it as PDF (WebKitGTK's print API is not exposed through Uno).
    /// </summary>
    private async Task PrintAsync()
    {
        if (document == null) return;
        var path = Path.Combine(Path.GetTempPath(), $"typedown-print-{Guid.NewGuid():N}.html");
        printAfterExport = path;
        await Post("Export", new { type = "html", context = new { filePath = path, print = true }, basePath = document.BasePath, title = Path.GetFileNameWithoutExtension(document.FileName), options = new { } });
    }

    private string? printAfterExport;

    private async Task ShareToHedgeDocAsync()
    {
        if (document == null) return;
        if (string.IsNullOrWhiteSpace(settings.HedgeDocServer))
        {
            await ShowErrorAsync(Loc.Get("ShareHedgeDoc"), Loc.Get("NotConfigured"));
            await ShowSettingsAsync();
            return;
        }
        try
        {
            SetStatus("HedgeDoc…");
            var result = await HedgeDocService.ShareAsync(settings.HedgeDocServer, document.Markdown, settings.HedgeDocEmail, settings.HedgeDocPassword, settings.HedgeDocPublishReadOnly);
            var panel = new StackPanel { Spacing = 8, MinWidth = 420 };
            if (result.PublishedUrl != null) { panel.Children.Add(new TextBlock { Text = Loc.Get("ReadOnlyLink") }); panel.Children.Add(new TextBox { Text = result.PublishedUrl, IsReadOnly = true }); }
            panel.Children.Add(new TextBlock { Text = Loc.Get("EditLink") });
            panel.Children.Add(new TextBox { Text = result.NoteUrl, IsReadOnly = true });
            if (result.PublishedUrl == null) panel.Children.Add(new TextBlock { Text = Loc.Get("EditableWarning"), Opacity = 0.7, TextWrapping = TextWrapping.Wrap });
            var dialog = new ContentDialog { Title = Loc.Get("Shared"), Content = panel, PrimaryButtonText = Loc.Get("CopyLink"), SecondaryButtonText = Loc.Get("OpenInBrowser"), CloseButtonText = Loc.Get("Close"), XamlRoot = XamlRoot, RequestedTheme = DialogTheme };
            var choice = await dialog.ShowAsync();
            if (choice == ContentDialogResult.Primary) { var p = new DataPackage(); p.SetText(result.ShareUrl); Clipboard.SetContent(p); }
            else if (choice == ContentDialogResult.Secondary) await Launcher.LaunchUriAsync(new Uri(result.ShareUrl));
            SetStatus(result.ShareUrl);
        }
        catch (Exception ex)
        {
            await ShowErrorAsync(Loc.Get("Error"), ex.Message);
        }
    }

    // ---- window ----------------------------------------------------------------------------------------------------

    private void HookWindowClosing()
    {
        try
        {
            if (window?.AppWindow is { } appWindow)
                appWindow.Closing += (_, args) =>
                {
                    if (closing) return;
                    args.Cancel = true;
                    DispatcherQueue.TryEnqueue(async () => await ExitAsync());
                };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Typedown.Uno] AppWindow.Closing unavailable: {ex.Message}");
        }
    }

    private async Task ExitAsync()
    {
        if (closing) return;
        if (tabs != null && !await tabs.AskToSaveAllAsync()) return;
        closing = true;
        tabs?.SaveSession(workFolder);
        settings.Save();
        document?.Dispose();
        // Closing the window is enough: the app exits once the last one is gone.
        if (window != null) window.Close();
        else Application.Current.Exit();
    }

    /// <summary>Opens another window, optionally with a file already loaded.</summary>
    private void NewWindow(string? file = null) => App.CreateWindow(file);

    private void UpdateTitle()
    {
        var title = document?.Title ?? "Typedown";
        if (window != null) window.Title = title;
        // Uno publishes the title as Latin-1 and, under a window manager, not at all after the window is up;
        // non-ASCII titles need UTF-8, and each window gets its own.
        PublishNativeChrome();
        StatusText.Text = document?.FilePath ?? Loc.Get("Untitled");
    }

    /// <summary>
    /// About: what this build is, and the versions a bug report needs — the editor bundle, Uno, the .NET
    /// runtime and the system web engine all change what the same document looks like. One button puts the
    /// whole block on the clipboard so it can be pasted into an issue.
    /// </summary>
    private async Task ShowAboutAsync()
    {
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock { Text = Loc.Get("AboutText"), TextWrapping = TextWrapping.Wrap });
        var details = new StackPanel { Spacing = 2 };
        foreach (var (label, value) in Services.AppInfo.Lines())
        {
            var row = new Grid { ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var name = new TextBlock { Text = label, Opacity = 0.7 };
            var text = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
            Grid.SetColumn(text, 1);
            row.Children.Add(name);
            row.Children.Add(text);
            details.Children.Add(row);
        }
        panel.Children.Add(details);
        panel.Children.Add(new TextBlock { Text = Loc.Get("AboutIssue"), Opacity = 0.7, TextWrapping = TextWrapping.Wrap });
        var dialog = new ContentDialog
        {
            Title = Loc.Get("About"),
            Content = panel,
            PrimaryButtonText = Loc.Get("CopyInfo"),
            CloseButtonText = Loc.Get("OK"),
            XamlRoot = XamlRoot,
            RequestedTheme = DialogTheme,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            var package = new DataPackage();
            package.SetText(Services.AppInfo.Summary());
            Clipboard.SetContent(package);
            SetStatus(Loc.Get("Copied"));
        }
    }

    /// <summary>
    /// The X11 window backing this page. Looking it up can fail while the window is still coming up (and it
    /// always failed on a normal desktop before the search became recursive), so it is retried until found.
    /// </summary>
    private IntPtr NativeWindow()
    {
        if (nativeWindow == IntPtr.Zero) nativeWindow = Services.X11Window.FindByTitle(options.Marker);
        return nativeWindow;
    }

    private DateTime lastXamlWheel;

    /// <summary>
    /// Scrolling with the wheel over the shell (the file tree, the outline, a dialog) does nothing in a VNC
    /// session: Uno reads the wheel from XInput2 scroll valuators, which a pointer driven through XTEST does
    /// not have. <see cref="Services.X11Window.ListenForWheel"/> reports the legacy wheel buttons instead, and
    /// what arrives here is applied to the scroll viewer under the pointer — but only while Uno itself is not
    /// delivering wheel events, so a normal mouse keeps its own (smooth) scrolling and never scrolls twice.
    /// </summary>
    private void HookWheelFallback()
    {
        AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler((_, _) => lastXamlWheel = DateTime.UtcNow), true);
        Services.X11Window.WheelScrolled += OnNativeWheel;
        Unloaded += (_, _) => Services.X11Window.WheelScrolled -= OnNativeWheel;
    }

    private void OnNativeWheel(IntPtr source, int x, int y, int notches)
    {
        if (source != nativeWindow) return;
        DispatcherQueue.TryEnqueue(async () =>
        {
            // Give Uno's own wheel handling a moment: when it delivers the notch (a mouse with a scroll axis)
            // this fallback stays out of the way. The suppression is per notch rather than permanent, so one
            // stray wheel event cannot switch the fallback off for the rest of the session.
            await Task.Delay(60);
            if ((DateTime.UtcNow - lastXamlWheel).TotalMilliseconds < 400) return;
            var scale = XamlRoot?.RasterizationScale ?? 1;
            if (scale <= 0) scale = 1;
            var scroller = ScrollerAt(new Windows.Foundation.Point(x / scale, y / scale));
            // three lines per notch, the usual step for a ScrollViewer
            scroller?.ChangeView(null, scroller.VerticalOffset - notches * 48, null, true);
        });
    }

    /// <summary>
    /// The innermost scrollable ScrollViewer under a point. Hit testing through FindElementsInHostCoordinates
    /// comes back empty here, so the visual tree is walked instead and each scroll viewer's bounds are mapped
    /// into window coordinates. Open dialogs and flyouts live in their own popups and are checked first.
    /// </summary>
    private ScrollViewer? ScrollerAt(Windows.Foundation.Point point)
    {
        if (XamlRoot?.Content is not UIElement reference) return null;
        var roots = new List<DependencyObject>();
        foreach (var popup in Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(XamlRoot))
            if (popup.Child != null) roots.Add(popup.Child);
        roots.Add(this);
        foreach (var root in roots)
        {
            var found = DeepestScroller(root, point, reference);
            if (found != null) return found;
        }
        return null;
    }

    private static ScrollViewer? DeepestScroller(DependencyObject root, Windows.Foundation.Point point, UIElement reference)
    {
        ScrollViewer? best = null;
        var bestDepth = -1;
        void Walk(DependencyObject node, int depth)
        {
            if (depth > 32) return;
            // A hidden branch keeps its layout: the file tree and the outline sit on top of each other and only
            // one is visible, so without this the hidden one's scroll viewer could win the hit test and the
            // wheel would appear to do nothing.
            if (node is UIElement { Visibility: Visibility.Collapsed }) return;
            if (node is ScrollViewer { ScrollableHeight: > 0 } scroller && depth > bestDepth && Contains(scroller, point, reference))
            {
                best = scroller;
                bestDepth = depth;
            }
            var count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(node);
            for (var i = 0; i < count; i++) Walk(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(node, i), depth + 1);
        }
        Walk(root, 0);
        return best;
    }

    private static bool Contains(FrameworkElement element, Windows.Foundation.Point point, UIElement reference)
    {
        try
        {
            var origin = element.TransformToVisual(reference).TransformPoint(new Windows.Foundation.Point(0, 0));
            return point.X >= origin.X && point.X <= origin.X + element.ActualWidth
                && point.Y >= origin.Y && point.Y <= origin.Y + element.ActualHeight;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Publishes the UTF-8 title and, once, the app icon on this window.</summary>
    private void PublishNativeChrome()
    {
        var handle = NativeWindow();
        if (handle == IntPtr.Zero) return;
        Services.X11Window.SetTitle(handle, document?.Title ?? "Typedown");
        // The handle can arrive late, so the wheel listener is started here rather than once at load time.
        Services.X11Window.ListenForWheel(handle);
        if (nativeIconApplied) return;
        nativeIconApplied = true;
        Services.X11Window.SetIcon(handle, Path.Combine(AppContext.BaseDirectory, "Assets", "typedown.png"));
    }

    private static void OpenContainingFolder(string path) => OpenPath(Path.GetDirectoryName(path));

    private static void OpenPath(string? path)
    {
        if (path == null) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Services.Log.Error($"open {path} failed", ex);
        }
    }

    private void SetStatus(string text)
    {
        _ = DispatcherQueue.TryEnqueue(() => StatusText.Text = text);
        Services.Log.Write(text);
    }

    // ---- IHostUi ------------------------------------------------------------------------------------------------------

    private static void InitPicker(object picker)
    {
#if WINDOWS
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
#endif
    }

    /// <summary>
    /// Linux gets the app's own chooser: the platform pickers there go through the XDG desktop portal, which is
    /// absent on many desktops, and then the dialog simply never appears. Windows and macOS keep the native one.
    /// </summary>
    private static bool UseBuiltInPicker => OperatingSystem.IsLinux();

    private string PickerStartDirectory => workFolder
        ?? (document?.FilePath != null ? Path.GetDirectoryName(document.FilePath) : null)
        ?? settings.LastFolder
        ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private async Task<string?> ShowBuiltInPickerAsync(FilePickerDialog.PickerMode mode, string? suggestedName = null, IEnumerable<string>? extensions = null)
    {
        var dialog = new FilePickerDialog(mode, PickerStartDirectory, suggestedName, extensions) { XamlRoot = XamlRoot, RequestedTheme = DialogTheme };
        await dialog.ShowAsync();
        return dialog.SelectedPath;
    }

    public async Task<string?> PickOpenFileAsync()
    {
        if (UseBuiltInPicker) return await ShowBuiltInPickerAsync(FilePickerDialog.PickerMode.OpenFile, null, FolderItem.MarkdownExtensions);
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        foreach (var ext in FolderItem.MarkdownExtensions) picker.FileTypeFilter.Add(ext);
        InitPicker(picker);
        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    public async Task<string?> PickSaveFileAsync(string? suggestedName)
    {
        if (UseBuiltInPicker) return await ShowBuiltInPickerAsync(FilePickerDialog.PickerMode.SaveFile, suggestedName ?? "Untitled.md");
        var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary, SuggestedFileName = suggestedName ?? "Untitled.md" };
        picker.FileTypeChoices.Add("Markdown", new List<string> { ".md" });
        picker.FileTypeChoices.Add("Text", new List<string> { ".txt" });
        InitPicker(picker);
        var file = await picker.PickSaveFileAsync();
        return file?.Path;
    }

    public async Task<DocumentViewModel.AskResult> AskSaveAsync(string fileName)
    {
        var dialog = new ContentDialog
        {
            Title = Loc.Get("SaveChanges"),
            Content = Loc.Format("HasUnsaved", fileName),
            PrimaryButtonText = Loc.Get("Save"),
            SecondaryButtonText = Loc.Get("DontSave"),
            CloseButtonText = Loc.Get("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot, RequestedTheme = DialogTheme,
        };
        return await dialog.ShowAsync() switch
        {
            ContentDialogResult.Primary => DocumentViewModel.AskResult.Yes,
            ContentDialogResult.Secondary => DocumentViewModel.AskResult.No,
            _ => DocumentViewModel.AskResult.Cancel,
        };
    }

    public async Task<bool> ConfirmAsync(string title, string message, string yes, string no)
    {
        var dialog = new ContentDialog { Title = title, Content = message, PrimaryButtonText = yes, CloseButtonText = no, XamlRoot = XamlRoot, RequestedTheme = DialogTheme };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    public async Task ShowErrorAsync(string title, string message)
    {
        var dialog = new ContentDialog { Title = title, Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, CloseButtonText = Loc.Get("OK"), XamlRoot = XamlRoot, RequestedTheme = DialogTheme };
        await dialog.ShowAsync();
    }
}

internal static class MenuExtensions
{
    public static MenuFlyoutItem OnClick(this MenuFlyoutItem item, Action action)
    {
        item.Click += (_, _) => action();
        return item;
    }
}
