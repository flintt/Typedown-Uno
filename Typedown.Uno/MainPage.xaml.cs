using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using Typedown.Uno.Services;
using Typedown.Uno.ViewModels;
using Windows.Storage.Pickers;

namespace Typedown.Uno;

public sealed partial class MainPage : Page, DocumentViewModel.IHostUi
{
    private const string EditorHost = "typedown.editor";

    private EditorTransport? transport;
    private DocumentViewModel? document;
    private string? startupFile;

    public MainPage()
    {
        this.InitializeComponent();
        Loaded += OnLoaded;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        // A file given on the command line (double-click / "open with") is loaded once the editor is up.
        startupFile = Environment.GetCommandLineArgs().Skip(1).FirstOrDefault(a => !a.StartsWith("-") && File.Exists(a));
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await EditorView.EnsureCoreWebView2Async();
            var core = EditorView.CoreWebView2;
            transport = new EditorTransport(js => PostToEditor(js));
            document = new DocumentViewModel(transport, this);
            document.PropertyChanged += (_, _) => DispatcherQueue.TryEnqueue(UpdateTitle);
            document.RunOnUi += work => DispatcherQueue.TryEnqueue(async () => await work());
            if (startupFile != null)
            {
                // Pre-load the state so the editor's first GetSettings already carries the file.
                await document.LoadAsync(startupFile, skipSavedCheck: true);
            }
            RegisterHostFunctions(transport);
            transport.MessageReceived += OnEditorMessage;
            core.WebMessageReceived += (_, args) =>
            {
                var raw = EditorTransport.GetRawMessage(args);
                if (raw != null) transport.OnWebMessage(raw);
            };
            core.NavigationCompleted += (_, args) => SetStatus(args.IsSuccess ? "editor page loaded" : $"navigation failed: {args.WebErrorStatus}");
            // The editor build (Assets/Editor) is served from a virtual host so its relative asset paths resolve.
            core.SetVirtualHostNameToFolderMapping(EditorHost, "Assets/Editor", CoreWebView2HostResourceAccessKind.Allow);
            EditorView.Source = new Uri($"http://{EditorHost}/index.html");
            UpdateTitle();
        }
        catch (Exception ex)
        {
            SetStatus($"WebView2 failed: {ex.Message}");
        }
    }

    // Host -> editor: uno-bridge.js dispatches the JSON to the editor's chrome.webview listeners.
    private async Task PostToEditor(string json)
    {
        var script = "window.__unoDeliver(" + JsonSerializer.Serialize(json) + ")";
        try
        {
            await EditorView.ExecuteScriptAsync(script);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Typedown.Uno] post to editor failed: {ex.Message}");
        }
    }

    // The functions the editor invokes (see Dev/Typedown.Core upstream: RemoteInvoke handlers).
    private void RegisterHostFunctions(EditorTransport t)
    {
        t.Handle("GetSettings", _ =>
        {
            document!.EditorReady = true;
            var load = document.GetLoadPayload();
            var settings = JsonSerializer.SerializeToNode(new
            {
                focusMode = false,
                typewriter = false,
                sourceCode = false,
                fontSize = 16,
                lineHeight = 1.6,
                autoPairBracket = true,
                autoPairQuote = true,
                autoPairMarkdownSyntax = true,
                trimUnnecessaryCodeBlockEmptyLines = true,
                preferLooseListItem = true,
                listIndentation = "1",
                tableAlignColumns = true,
                showParagraphMarker = true,
                readOnly = false,
                spellcheckEnabled = false,
                customCss = "",
                editorAreaWidth = "1200px",
                fontFamily = "",
                textDirection = "auto",
                tabSize = 4,
            }, EditorTransport.JsonOptions)!.AsObject();
            foreach (var (key, value) in JsonSerializer.SerializeToNode(load, EditorTransport.JsonOptions)!.AsObject().ToList())
                settings[key] = value?.DeepClone();
            return (object?)settings;
        });
        t.Handle("GetCurrentTheme", _ => new
        {
            theme = "Light",
            accentColor = new { r = 0, g = 120, b = 212, a = 1 },
            background = new { R = 249, G = 249, B = 249, A = 1 },
        });
        t.Handle("ContentLoaded", _ => (object?)"");
        t.Handle("GetStringResources", _ => new { });
    }

    private void OnEditorMessage(string name, JsonNode? args)
    {
        switch (name)
        {
            case "FileLoaded":
                SetStatus(document?.FilePath ?? "Untitled");
                break;
            case "StateChange":
                var words = args?["state"]?["wordCount"]?["word"];
                if (words != null) DispatcherQueue.TryEnqueue(() => WordCountText.Text = $"{words} words");
                break;
        }
    }

    // ---- menu ----------------------------------------------------------------------------------------------

    private async void OnNewClick(object sender, RoutedEventArgs e) { if (document != null) await document.NewAsync(); }
    private async void OnOpenClick(object sender, RoutedEventArgs e) { if (document != null) await document.OpenAsync(); }
    private async void OnSaveClick(object sender, RoutedEventArgs e) { if (document != null) await document.SaveAsync(); }
    private async void OnSaveAsClick(object sender, RoutedEventArgs e) { if (document != null) await document.SaveAsAsync(); }

    private async void OnExitClick(object sender, RoutedEventArgs e)
    {
        if (document != null && !await document.AskToSaveAsync()) return;
        document?.Dispose();
        Application.Current.Exit();
    }

    private void UpdateTitle()
    {
        var title = document?.Title ?? "Typedown";
        if (App.MainWindow != null) App.MainWindow.Title = title;
        SetStatus(document?.FilePath ?? "Untitled");
    }

    private void SetStatus(string text)
    {
        _ = DispatcherQueue.TryEnqueue(() => StatusText.Text = text);
        Console.WriteLine($"[Typedown.Uno] {text}");
    }

    // ---- IHostUi: pickers and dialogs -------------------------------------------------------------------------

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
        foreach (var ext in new[] { ".md", ".markdown", ".mdown", ".mkd", ".txt" }) picker.FileTypeFilter.Add(ext);
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
            Title = "Save changes?",
            Content = $"{fileName} has unsaved changes.",
            PrimaryButtonText = "Save",
            SecondaryButtonText = "Don't save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
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
        var dialog = new ContentDialog { Title = title, Content = message, PrimaryButtonText = yes, CloseButtonText = no, XamlRoot = XamlRoot };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    public async Task ShowErrorAsync(string title, string message)
    {
        var dialog = new ContentDialog { Title = title, Content = message, CloseButtonText = "OK", XamlRoot = XamlRoot };
        await dialog.ShowAsync();
    }
}
