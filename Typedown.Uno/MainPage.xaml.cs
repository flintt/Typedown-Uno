using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using Typedown.Uno.Services;

namespace Typedown.Uno;

public sealed partial class MainPage : Page
{
    private const string EditorHost = "typedown.editor";

    private EditorTransport? transport;
    private string markdown = "# Typedown on Uno\n\nType here. Changes are reported to the host through the message bridge.\n";
    private int loadId;

    public MainPage()
    {
        this.InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await EditorView.EnsureCoreWebView2Async();
            var core = EditorView.CoreWebView2;
            transport = new EditorTransport(js => PostToEditor(js));
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
        await EditorView.ExecuteScriptAsync(script);
    }

    // The functions the editor invokes at startup (see Dev/Typedown.Core upstream: RemoteInvoke handlers).
    private void RegisterHostFunctions(EditorTransport t)
    {
        t.Handle("GetSettings", _ => new
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
            markdown,
            basePath = Environment.CurrentDirectory,
            cursor = (object?)null,
            scrollTop = (object?)null,
            loadId = ++loadId,
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
                SetStatus($"editor ready (loadId {args?["loadId"]}), {args?["text"]?.GetValue<string>()?.Length ?? 0} chars");
                break;
            case "MarkdownChange":
                markdown = args?["text"]?.GetValue<string>() ?? markdown;
                SetStatus($"changed: {markdown.Length} chars");
                break;
            case "StateChange":
                var words = args?["state"]?["wordCount"]?["word"];
                if (words != null) SetStatus($"words: {words}");
                break;
        }
    }

    private void SetStatus(string text)
    {
        _ = DispatcherQueue.TryEnqueue(() => StatusText.Text = text);
        Console.WriteLine($"[Typedown.Uno] {text}");
    }
}
