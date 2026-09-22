using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Typedown.Uno.Services;

namespace Typedown.Uno.ViewModels;

/// <summary>
/// One document in the editor (ported from Typedown's FileViewModel/EditorViewModel, single document for M2):
/// path, content, saved state, load handshake, caret/scroll memory, external change detection, atomic save.
/// UI interactions (pickers, dialogs) are injected so the view model stays platform-neutral.
/// </summary>
public sealed class DocumentViewModel : INotifyPropertyChanged, IDisposable
{
    public enum AskResult { Yes, No, Cancel }

    public interface IHostUi
    {
        Task<string?> PickOpenFileAsync();
        Task<string?> PickSaveFileAsync(string? suggestedName);
        Task<AskResult> AskSaveAsync(string fileName);
        Task<bool> ConfirmAsync(string title, string message, string yes, string no);
        Task ShowErrorAsync(string title, string message);
    }

    public static readonly string DefaultMarkdown = "\n";

    private readonly EditorTransport transport;
    private readonly IHostUi ui;
    private FileSystemWatcher? watcher;
    private DateTime lastWatcherEvent;
    private bool handlingExternalChange;

    public event PropertyChangedEventHandler? PropertyChanged;

    private string? filePath;
    public string? FilePath { get => filePath; private set { filePath = value; OnPropertyChanged(); OnPropertyChanged(nameof(Title)); } }

    private bool saved = true;
    public bool Saved { get => saved; private set { if (saved == value) return; saved = value; OnPropertyChanged(); OnPropertyChanged(nameof(Title)); } }

    public string Markdown { get; private set; } = DefaultMarkdown;
    public ulong FileHash { get; private set; } = SafeFile.Hash(DefaultMarkdown);
    public ulong CurrentHash { get; private set; } = SafeFile.Hash(DefaultMarkdown);
    /// <summary>Hash of the raw bytes-as-text last seen on disk; the editor's normalized text can differ from it.</summary>
    public ulong DiskHash { get; private set; }
    public bool FileLoaded { get; private set; }
    public int LoadId { get; private set; }
    /// <summary>False until the editor page asked for its settings; before that content is only staged for GetSettings.</summary>
    public bool EditorReady { get; set; }
    public bool RememberPosition { get; set; } = true;

    public string FileName => FilePath == null ? "Untitled" : Path.GetFileName(FilePath);
    public string Title => (Saved ? "" : "• ") + FileName + " - Typedown";

    public DocumentViewModel(EditorTransport transport, IHostUi ui)
    {
        this.transport = transport;
        this.ui = ui;
        transport.MessageReceived += OnEditorMessage;
    }

    // ---- editor reports -------------------------------------------------------------------------------------

    private bool IsStale(JsonNode? args)
    {
        var id = args?["loadId"];
        return id != null && id.GetValueKind() == System.Text.Json.JsonValueKind.Number && id.GetValue<int>() != LoadId;
    }

    private void OnEditorMessage(string name, JsonNode? args)
    {
        switch (name)
        {
            case "FileLoaded":
                if (IsStale(args) || FileLoaded) return;
                FileLoaded = true;
                var text = args?["text"]?.GetValue<string>() ?? "";
                FileHash = SafeFile.Hash(text); // the editor's normalized form is the "saved" baseline
                Markdown = text;
                CurrentHash = FileHash;
                Saved = true;
                break;
            case "MarkdownChange":
                if (IsStale(args)) return;
                Markdown = args?["text"]?.GetValue<string>() ?? Markdown;
                CurrentHash = SafeFile.Hash(Markdown);
                Saved = FileHash == CurrentHash;
                break;
            case "CursorChange":
                if (IsStale(args) || !FileLoaded) return;
                if (RememberPosition) CursorMemory.SetCursor(FilePath, args?["cursor"]);
                break;
            case "OnScroll":
                if (!FileLoaded) return;
                var y = args?["scrollY"]?.GetValue<double?>();
                if (y != null && RememberPosition) CursorMemory.SetScroll(FilePath, y.Value);
                break;
        }
    }

    // ---- content into the editor ---------------------------------------------------------------------------

    private Task PostLoadFile(string text)
    {
        FileLoaded = false;
        if (!EditorReady) return Task.CompletedTask; // the initial GetSettings reply carries the document
        return transport.PostMessage("LoadFile", new
        {
            text,
            basePath = FilePath == null ? Environment.CurrentDirectory : Path.GetDirectoryName(FilePath),
            cursor = RememberPosition ? CursorMemory.GetCursor(FilePath) : null,
            scrollTop = RememberPosition ? CursorMemory.GetScroll(FilePath) : null,
            loadId = ++LoadId,
        });
    }

    /// <summary>Payload part for the editor's initial GetSettings call.</summary>
    public object GetLoadPayload() => new
    {
        markdown = Markdown,
        basePath = FilePath == null ? Environment.CurrentDirectory : Path.GetDirectoryName(FilePath),
        cursor = RememberPosition ? CursorMemory.GetCursor(FilePath) : null,
        scrollTop = RememberPosition ? CursorMemory.GetScroll(FilePath) : null,
        loadId = ++LoadId,
    };

    // ---- commands ---------------------------------------------------------------------------------------------

    public async Task<bool> NewAsync()
    {
        if (!await AskToSaveAsync()) return false;
        StopWatching();
        FilePath = null;
        Markdown = DefaultMarkdown;
        FileHash = CurrentHash = SafeFile.Hash(DefaultMarkdown);
        DiskHash = 0;
        Saved = true;
        await PostLoadFile(Markdown);
        return true;
    }

    public async Task<bool> OpenAsync(string? path = null)
    {
        path ??= await ui.PickOpenFileAsync();
        if (path == null) return false;
        return await LoadAsync(path);
    }

    /// <summary>Reads the file and hands it to the editor; the saved baseline comes back through FileLoaded.</summary>
    public async Task<bool> LoadAsync(string path, bool skipSavedCheck = false)
    {
        try
        {
            if (!skipSavedCheck && !await AskToSaveAsync()) return false;
            if (!File.Exists(path)) throw new FileNotFoundException("File does not exist.", path);
            var text = await File.ReadAllTextAsync(path);
            StopWatching();
            FilePath = Path.GetFullPath(path);
            Markdown = text;
            FileHash = CurrentHash = SafeFile.Hash(text);
            DiskHash = FileHash;
            Saved = true;
            await PostLoadFile(text);
            StartWatching();
            return true;
        }
        catch (Exception ex)
        {
            await ui.ShowErrorAsync("Cannot open file", ex.Message);
            return false;
        }
    }

    public async Task<bool> SaveAsync()
    {
        if (FilePath == null) return await SaveAsAsync();
        return await WriteAsync(FilePath);
    }

    public async Task<bool> SaveAsAsync()
    {
        var path = await ui.PickSaveFileAsync(FilePath == null ? "Untitled.md" : Path.GetFileName(FilePath));
        if (path == null) return false;
        StopWatching();
        FilePath = Path.GetFullPath(path);
        var ok = await WriteAsync(FilePath);
        StartWatching();
        return ok;
    }

    private async Task<bool> WriteAsync(string path)
    {
        try
        {
            var text = Markdown;
            var hash = SafeFile.Hash(text);
            handlingExternalChange = true; // our own write must not look like an external change
            await SafeFile.WriteAllTextAtomicAsync(path, text);
            FileHash = hash;
            DiskHash = hash;
            Saved = CurrentHash == hash;
            CursorMemory.Flush();
            return true;
        }
        catch (Exception ex)
        {
            await ui.ShowErrorAsync("Cannot save file", ex.Message);
            return false;
        }
        finally
        {
            lastWatcherEvent = DateTime.UtcNow;
            handlingExternalChange = false;
        }
    }

    /// <summary>Asks about unsaved changes; true when the caller may proceed (saved, discarded, or nothing to save).</summary>
    public async Task<bool> AskToSaveAsync()
    {
        if (Saved) return true;
        switch (await ui.AskSaveAsync(FileName))
        {
            case AskResult.Yes: return await SaveAsync();
            case AskResult.No: return true;
            default: return false;
        }
    }

    // ---- external changes ------------------------------------------------------------------------------------

    private void StartWatching()
    {
        if (FilePath == null) return;
        try
        {
            watcher = new FileSystemWatcher(Path.GetDirectoryName(FilePath)!, Path.GetFileName(FilePath))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            };
            watcher.Changed += OnFileEvent;
            watcher.Created += OnFileEvent;
            watcher.Renamed += OnFileEvent;
            watcher.EnableRaisingEvents = true;
        }
        catch
        {
            watcher = null;
        }
    }

    private void StopWatching()
    {
        watcher?.Dispose();
        watcher = null;
    }

    public event Action<Func<Task>>? RunOnUi;

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        if (handlingExternalChange || (DateTime.UtcNow - lastWatcherEvent).TotalMilliseconds < 500) return;
        lastWatcherEvent = DateTime.UtcNow;
        RunOnUi?.Invoke(HandleExternalChangeAsync);
    }

    private async Task HandleExternalChangeAsync()
    {
        if (FilePath == null || handlingExternalChange) return;
        handlingExternalChange = true;
        try
        {
            await Task.Delay(200); // let the writer finish
            if (!File.Exists(FilePath)) return;
            string text;
            try { text = await File.ReadAllTextAsync(FilePath); } catch { return; }
            var diskHash = SafeFile.Hash(text);
            if (diskHash == DiskHash || diskHash == FileHash) return; // nothing really changed
            if (Saved)
            {
                await ApplyDiskTextAsync(text);
                return;
            }
            var reload = await ui.ConfirmAsync("File changed on disk", $"{FileName} was modified outside Typedown. Reload it and lose your unsaved changes?", "Reload", "Keep mine");
            if (reload) await ApplyDiskTextAsync(text);
            else DiskHash = diskHash;
        }
        finally
        {
            handlingExternalChange = false;
        }
    }

    private async Task ApplyDiskTextAsync(string text)
    {
        Markdown = text;
        FileHash = CurrentHash = SafeFile.Hash(text);
        DiskHash = FileHash;
        Saved = true;
        await PostLoadFile(text);
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose()
    {
        transport.MessageReceived -= OnEditorMessage;
        StopWatching();
        CursorMemory.Flush();
    }
}
