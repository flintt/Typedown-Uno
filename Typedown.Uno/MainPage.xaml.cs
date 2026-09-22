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
        startupFile = Environment.GetCommandLineArgs().Skip(1).FirstOrDefault(a => !a.StartsWith('-') && File.Exists(a));
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // The document model is built first and never depends on the web view: if the editor fails to come up
        // (missing WebKitGTK, a stalled native initialization) the shell must still be usable and say why.
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
        if (startupFile != null)
        {
            await tabs.OpenFileAsync(startupFile);
        }
        else if (settings.RestoreSession)
        {
            var folder = await tabs.RestoreSessionAsync();
            if (folder != null && Directory.Exists(folder)) SetWorkFolder(folder);
        }
        if (workFolder == null && settings.LastFolder != null && Directory.Exists(settings.LastFolder)) SetWorkFolder(settings.LastFolder);
        if (workFolder == null && document.FilePath != null) SetWorkFolder(Path.GetDirectoryName(document.FilePath)!);
        UpdateTitle();
        UpdateTabBar();
        HookWindowClosing();

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

    // ---- editor bridge ------------------------------------------------------------------------------------------

    private async Task PostToEditor(string json)
    {
        var script = "window.__unoDeliver(" + JsonSerializer.Serialize(json) + ")";
        try { await EditorView.ExecuteScriptAsync(script); }
        catch (Exception ex) { Console.Error.WriteLine($"[Typedown.Uno] post to editor failed: {ex.Message}"); }
    }

    private Task Post(string name, object? args) => transport?.PostMessage(name, args) ?? Task.CompletedTask;

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
            var package = new DataPackage();
            var type = args?["type"]?.GetValue<string>();
            var data = args?["data"]?.ToString() ?? "";
            if (type == "text/html") package.SetHtmlFormat(data); else package.SetText(data);
            try { Clipboard.SetContent(package); } catch { }
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
                SetStatus(Loc.Format("Exported", path));
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
                var words = args?["state"]?["wordCount"]?["word"];
                var toc = args?["state"]?["toc"]?.DeepClone();
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (words != null) WordCountText.Text = Loc.Format("Words", words);
                    OutlineList.ItemsSource = OutlineItem.FromToc(toc);
                });
                break;
            case "OpenURI":
                var uri = args?["uri"]?.GetValue<string>();
                if (uri != null) DispatcherQueue.TryEnqueue(async () => { try { await Launcher.LaunchUriAsync(new Uri(uri)); } catch { } });
                break;
            case "Shortcut":
                var key = args?["key"]?.GetValue<string>() ?? "";
                var ctrl = args?["ctrl"]?.GetValue<bool>() ?? false;
                var shift = args?["shift"]?.GetValue<bool>() ?? false;
                DispatcherQueue.TryEnqueue(async () => await HandleShortcutAsync(key, ctrl, shift));
                break;
        }
    }

    // ---- menus ------------------------------------------------------------------------------------------------------

    private MenuFlyoutSubItem? recentMenu;

    private void BuildMenus()
    {
        MainMenu.Items.Clear();
        var file = new MenuBarItem { Title = Loc.Get("File") };
        file.Items.Add(Item("New", async () => { if (tabs != null) await tabs.NewTabAsync(); }, VirtualKey.N, ctrl: true));
        file.Items.Add(Item("Open", async () => await OpenFileDialogAsync(), VirtualKey.O, ctrl: true));
        file.Items.Add(Item("OpenFolder", async () => await OpenFolderDialogAsync(), VirtualKey.O, ctrl: true, shift: true));
        recentMenu = new MenuFlyoutSubItem { Text = Loc.Get("Recent") };
        file.Items.Add(recentMenu);
        FillRecent();
        file.Items.Add(new MenuFlyoutSeparator());
        file.Items.Add(Item("Save", async () => { if (document != null) await document.SaveAsync(); }, VirtualKey.S, ctrl: true));
        file.Items.Add(Item("SaveAs", async () => { if (document != null) await document.SaveAsAsync(); }, VirtualKey.S, ctrl: true, shift: true));
        file.Items.Add(Item("ExportHtml", async () => await ExportHtmlAsync()));
        file.Items.Add(Item("ShareHedgeDoc", async () => await ShareToHedgeDocAsync()));
        file.Items.Add(new MenuFlyoutSeparator());
        file.Items.Add(Item("Settings", async () => await ShowSettingsAsync(), (VirtualKey)188, ctrl: true));
        file.Items.Add(Item("CloseTab", async () => { if (tabs != null) await tabs.CloseTabAsync(tabs.ActiveTab); }, VirtualKey.W, ctrl: true));
        file.Items.Add(Item("Exit", async () => await ExitAsync()));
        MainMenu.Items.Add(file);

        var edit = new MenuBarItem { Title = Loc.Get("Edit") };
        edit.Items.Add(Item("Find", () => ShowFind(true), VirtualKey.F, ctrl: true));
        edit.Items.Add(Item("FindNext", async () => await Post("Find", new { action = "next" }), VirtualKey.F3));
        edit.Items.Add(Item("FindPrevious", async () => await Post("Find", new { action = "prev" }), VirtualKey.F3, shift: true));
        edit.Items.Add(new MenuFlyoutSeparator());
        edit.Items.Add(Item("SelectAll", async () => await Post("SelectAll", null)));
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
        format.Items.Add(new MenuFlyoutSeparator());
        format.Items.Add(Item("ClearFormat", async () => await Post("Format", "clear")));
        MainMenu.Items.Add(format);

        var view = new MenuBarItem { Title = Loc.Get("View") };
        view.Items.Add(Toggle("SourceCode", () => settings.SourceCode, v => settings.SourceCode = v, (VirtualKey)191, ctrl: true));
        view.Items.Add(Toggle("FocusMode", () => settings.FocusMode, v => settings.FocusMode = v));
        view.Items.Add(Toggle("Typewriter", () => settings.Typewriter, v => settings.Typewriter = v));
        view.Items.Add(Toggle("ReadOnly", () => settings.ReadOnly, v => settings.ReadOnly = v, VirtualKey.R, ctrl: true, shift: true));
        view.Items.Add(new MenuFlyoutSeparator());
        view.Items.Add(Toggle("SidePane", () => settings.SidePaneOpen, v => settings.SidePaneOpen = v, VirtualKey.B, ctrl: true, shift: true));
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
        help.Items.Add(Item("About", async () => await ShowErrorAsync(Loc.Get("About"), Loc.Get("AboutText"))));
        MainMenu.Items.Add(help);
    }

    private MenuFlyoutItem Item(string key, Action action, VirtualKey? accelerator = null, bool ctrl = false, bool shift = false)
    {
        var item = new MenuFlyoutItem { Text = Loc.Get(key) };
        item.Click += (_, _) => action();
        if (accelerator != null) AddAccelerator(item, accelerator.Value, ctrl, shift, action);
        return item;
    }

    private ToggleMenuFlyoutItem Toggle(string key, Func<bool> get, Action<bool> set, VirtualKey? accelerator = null, bool ctrl = false, bool shift = false)
    {
        var item = new ToggleMenuFlyoutItem { Text = Loc.Get(key), IsChecked = get() };
        item.Click += (_, _) => set(item.IsChecked);
        void Flip() { set(!get()); item.IsChecked = get(); }
        if (accelerator != null) AddAccelerator(item, accelerator.Value, ctrl, shift, Flip);
        settings.PropertyChanged += (_, _) => DispatcherQueue.TryEnqueue(() => item.IsChecked = get());
        return item;
    }

    private void AddAccelerator(MenuFlyoutItemBase item, VirtualKey key, bool ctrl, bool shift, Action action)
    {
        var modifiers = (ctrl ? VirtualKeyModifiers.Control : VirtualKeyModifiers.None) | (shift ? VirtualKeyModifiers.Shift : VirtualKeyModifiers.None);
        // Page-scoped accelerator: works while focus is in the shell. Inside the editor (a native web view on Linux)
        // the same combination is forwarded by uno-bridge.js as a "Shortcut" message (see HandleShortcutAsync).
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
        accelerator.Invoked += (_, e) => { e.Handled = true; action(); };
        KeyboardAccelerators.Add(accelerator);
        if (item is MenuFlyoutItem mi) mi.KeyboardAcceleratorTextOverride = (ctrl ? "Ctrl+" : "") + (shift ? "Shift+" : "") + KeyText(key);
    }

    private static string KeyText(VirtualKey key) => key switch
    {
        (VirtualKey)188 => ",",
        (VirtualKey)191 => "/",
        VirtualKey.Tab => "Tab",
        _ => key.ToString(),
    };

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

    private async Task HandleShortcutAsync(string key, bool ctrl, bool shift)
    {
        if (tabs == null || document == null) return;
        switch ((key.ToLowerInvariant(), ctrl, shift))
        {
            case ("s", true, false): await document.SaveAsync(); break;
            case ("s", true, true): await document.SaveAsAsync(); break;
            case ("o", true, false): await OpenFileDialogAsync(); break;
            case ("o", true, true): await OpenFolderDialogAsync(); break;
            case ("n", true, false): await tabs.NewTabAsync(); break;
            case ("w", true, false): await tabs.CloseTabAsync(tabs.ActiveTab); break;
            case ("f", true, false): ShowFind(true); break;
            case ("f", true, true): settings.SidePaneOpen = true; settings.SidePanePage = 2; ApplySidePane(); SearchBox.Focus(FocusState.Programmatic); break;
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
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add("*");
        InitPicker(picker);
        var folder = await picker.PickSingleFolderAsync();
        if (folder?.Path == null) return;
        SetWorkFolder(folder.Path, explicitChoice: true);
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

    private async void OnOutlineItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is OutlineItem item) await Post("ScrollTo", new { slug = item.Slug });
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
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

    private void ShowFind(bool show, string? text = null) => _ = show ? Post("ShowFind", new { value = text }) : Post("HideFind", null);

    // ---- side pane, theme, settings -----------------------------------------------------------------------------------

    private void OnSidePageClick(object sender, RoutedEventArgs e)
    {
        settings.SidePanePage = int.Parse((string)((ToggleButton)sender).Tag);
        ApplySidePane();
    }

    private void ApplySidePane()
    {
        var open = settings.SidePaneOpen;
        SidePane.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        SidePaneColumn.Width = open ? new GridLength(Math.Max(180, settings.SidePaneWidth)) : new GridLength(0);
        var page = settings.SidePanePage;
        FilesToggle.IsChecked = page == 0;
        OutlineToggle.IsChecked = page == 1;
        SearchToggle.IsChecked = page == 2;
        FileTree.Visibility = page == 0 ? Visibility.Visible : Visibility.Collapsed;
        OutlineList.Visibility = page == 1 ? Visibility.Visible : Visibility.Collapsed;
        SearchPanel.Visibility = page == 2 ? Visibility.Visible : Visibility.Collapsed;
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
        if (App.MainWindow?.Content is FrameworkElement root)
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
                case nameof(AppSettings.SidePaneOpen) or nameof(AppSettings.SidePanePage) or nameof(AppSettings.SidePaneWidth): ApplySidePane(); break;
                case nameof(AppSettings.AlwaysShowTabBar): UpdateTabBar(); break;
                case nameof(AppSettings.RecentFiles): FillRecent(); break;
                case nameof(AppSettings.Language): Loc.Apply(settings.Language); ApplyStrings(); BuildMenus(); UpdateTitle(); break;
            }
        });
    }

    private void ApplyStrings()
    {
        FilesToggle.Content = Loc.Get("Files");
        OutlineToggle.Content = Loc.Get("Outline");
        SearchToggle.Content = Loc.Get("Search");
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
        var picker = new FileSavePicker { SuggestedFileName = Path.GetFileNameWithoutExtension(document.FileName) + ".html" };
        picker.FileTypeChoices.Add("HTML", new List<string> { ".html" });
        InitPicker(picker);
        var file = await picker.PickSaveFileAsync();
        if (file?.Path == null) return;
        await Post("Export", new { type = "html", context = new { filePath = file.Path }, basePath = document.BasePath, title = Path.GetFileNameWithoutExtension(document.FileName), options = new { } });
    }

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
            if (App.MainWindow?.AppWindow is { } appWindow)
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
        Application.Current.Exit();
    }

    private void UpdateTitle()
    {
        var title = document?.Title ?? "Typedown";
        if (App.MainWindow != null) App.MainWindow.Title = title;
        StatusText.Text = document?.FilePath ?? Loc.Get("Untitled");
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

    public async Task<string?> PickOpenFileAsync()
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        foreach (var ext in FolderItem.MarkdownExtensions) picker.FileTypeFilter.Add(ext);
        InitPicker(picker);
        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    public async Task<string?> PickSaveFileAsync(string? suggestedName)
    {
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
