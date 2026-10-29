using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Typedown.Uno.Services;

namespace Typedown.Uno.ViewModels;

/// <summary>Document tabs of one window over a single live <see cref="DocumentViewModel"/> (ported from Typedown).</summary>
public sealed class TabsViewModel : INotifyPropertyChanged
{
    private readonly DocumentViewModel document;
    private readonly AppSettings settings;
    private bool switching;
    private bool transitionPending;
    public bool RestoringSession { get; private set; }

    public ObservableCollection<DocumentTab> Tabs { get; } = new();

    private DocumentTab activeTab;
    public DocumentTab ActiveTab { get => activeTab; private set { activeTab = value; OnPropertyChanged(); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? TabsChanged;

    public TabsViewModel(DocumentViewModel document, AppSettings settings)
    {
        this.document = document;
        this.settings = settings;
        activeTab = new DocumentTab();
        activeTab.DocumentId = document.DocumentId;
        Tabs.Add(activeTab);
        document.PropertyChanged += (_, e) =>
        {
            if (switching) return;
            if (e.PropertyName == nameof(DocumentViewModel.FilePath)) ActiveTab.FilePath = document.FilePath;
            if (e.PropertyName == nameof(DocumentViewModel.Saved))
            {
                ActiveTab.IsDirty = !document.Saved;
                if (ActiveTab.IsDirty) ActiveTab.IsPreview = false;
            }
        };
        Tabs.CollectionChanged += (_, _) => TabsChanged?.Invoke();
    }

    public DocumentTab? FindByPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        return Tabs.FirstOrDefault(t => PathIdentity.Same(t.FilePath, path));
    }

    /// <summary>Opens a file: an already open one becomes active; otherwise it gets a tab (reusing a blank or preview tab).</summary>
    public async Task<bool> OpenFileAsync(string path, bool preview = false)
    {
        if (RestoringSession || transitionPending) return false;
        if (FindByPath(path) is { } existing)
        {
            await SwitchToAsync(existing);
            if (!preview) existing.IsPreview = false;
            return existing == ActiveTab;
        }
        // "Open files in a new tab" off: an open replaces the current document whenever it has nothing to lose.
        var reuse = document.IsBlank || (preview && document.Saved && ActiveTab.IsPreview) || (!settings.OpenFilesInNewTab && document.Saved);
        DocumentTab? started = null;
        if (!reuse) started = BeginNewTab();
        var ok = await document.LoadAsync(path);
        if (!ok && started != null) AbortNewTab(started);
        if (ok)
        {
            // A file read into a reused blank or preview tab is another document than the one it replaced.
            if (reuse)
            {
                var id = Guid.NewGuid().ToString("N");
                document.BecomeNewDocument(id);
                ActiveTab.DocumentId = id;
            }
            ActiveTab.IsPreview = preview;
            settings.AddRecent(document.FilePath!);
        }
        return ok;
    }

    public async Task NewTabAsync()
    {
        if (RestoringSession || transitionPending) return;
        if (document.IsBlank) return;
        BeginNewTab();
        await document.ResetToUntitledAsync(ActiveTab.DocumentId, discardCurrent: false);
    }

    private DocumentTab BeginNewTab(string? documentId = null)
    {
        document.Capture(ActiveTab);
        var tab = new DocumentTab { DocumentId = documentId ?? Guid.NewGuid().ToString("N") };
        Tabs.Add(tab);
        ActiveTab = tab;
        document.PrepareNewTab(tab.DocumentId);
        return tab;
    }

    private void AbortNewTab(DocumentTab tab)
    {
        if (!Tabs.Contains(tab) || tab != ActiveTab) return;
        var index = Tabs.IndexOf(tab);
        Tabs.Remove(tab);
        var back = Tabs[Math.Max(0, index - 1)];
        ActiveTab = back;
        _ = document.Restore(back);
    }

    /// <summary>The tab that was active before this one, for the shortcut that jumps back and forth.</summary>
    private DocumentTab? previousTab;

    /// <summary>
    /// Back to the tab used before this one — two tabs out of many, switched between the way Alt+Tab switches
    /// between two windows. Falls back to the next tab when there is no history yet.
    /// </summary>
    public Task SwitchToLastUsedAsync()
    {
        if (previousTab != null && previousTab != ActiveTab && Tabs.Contains(previousTab))
            return SwitchToAsync(previousTab);
        return SwitchRelativeAsync(1);
    }

    public async Task SwitchToAsync(DocumentTab tab)
    {
        if (tab == ActiveTab || !Tabs.Contains(tab)) return;
        if (RestoringSession || transitionPending)
        {
            OnPropertyChanged(nameof(ActiveTab));
            return;
        }
        transitionPending = true;
        try
        {
            // Bring the outgoing document's latest edits in and let its pending change reports resolve before we
            // snapshot it and load the next one, or a throttled MarkdownChange for the tab being left can arrive
            // after the switch and land on the tab now shown — one document's text crossing into another.
            if (!await document.FlushContentAsync())
            {
                document.ShowStatus(Loc.Get("EditorNotResponding"));
                OnPropertyChanged(nameof(ActiveTab)); // reset a TabView selection that already moved visually
                return;
            }
            // Looked at again after the wait: closing a tab removes it from the strip, the strip selects the neighbour and
            // asks for it here, and the close then shows that very tab itself - the same document loaded twice, the first
            // load's reports dropped as stale and the second not reported again: the outline stayed on the closed
            // document (Typedown 00ff51d, E2E TC02).
            if (tab == ActiveTab || !Tabs.Contains(tab)) return;
            switching = true;
            try
            {
                document.Capture(ActiveTab);
                previousTab = ActiveTab;
                ActiveTab = tab;
                if (tab.IsDeferred) await LoadDeferredAsync(tab);
                else await document.Restore(tab);
            }
            finally
            {
                switching = false;
            }
        }
        finally { transitionPending = false; }
    }

    /// <summary>
    /// The tab at a position, counted from zero; a position past the last tab means the last one, which is what
    /// the modifier+9 shortcut is for. Nothing happens when that tab is already the active one.
    /// </summary>
    public Task SwitchToIndexAsync(int index)
    {
        if (Tabs.Count == 0) return Task.CompletedTask;
        var tab = index >= Tabs.Count ? Tabs[^1] : Tabs[Math.Max(index, 0)];
        return tab == ActiveTab ? Task.CompletedTask : SwitchToAsync(tab);
    }

    public Task SwitchRelativeAsync(int delta)
    {
        if (Tabs.Count < 2) return Task.CompletedTask;
        var index = (Tabs.IndexOf(ActiveTab) + delta + Tabs.Count) % Tabs.Count;
        return SwitchToAsync(Tabs[index]);
    }

    /// <summary>Closes a tab after the save prompt; closing the last tab leaves an empty untitled document.</summary>
    public async Task<bool> CloseTabAsync(DocumentTab tab)
    {
        if (RestoringSession || transitionPending || !Tabs.Contains(tab)) return false;
        if (tab != ActiveTab) await SwitchToAsync(tab);
        if (tab != ActiveTab) return false;
        if (!await document.AskToSaveAsync()) return false;
        if (Tabs.Count == 1)
        {
            await document.ResetToUntitledAsync(tab.DocumentId);
            tab.FilePath = null;
            tab.IsDirty = false;
            return true;
        }
        var index = Tabs.IndexOf(tab);
        Tabs.Remove(tab);
        var next = Tabs[Math.Min(index, Tabs.Count - 1)];
        switching = true;
        try
        {
            ActiveTab = next;
            if (next.IsDeferred) await LoadDeferredAsync(next);
            else await document.Restore(next);
        }
        finally
        {
            switching = false;
        }
        return true;
    }

    /// <summary>Save prompt for every dirty tab; false when the user cancelled.</summary>
    public async Task<bool> AskToSaveAllAsync()
    {
        foreach (var tab in Tabs.ToList())
        {
            var dirty = tab == ActiveTab ? !document.Saved : !tab.Saved;
            if (!dirty) continue;
            await SwitchToAsync(tab);
            if (!await document.AskToSaveAsync()) return false;
        }
        return true;
    }

    public void SaveSession(string? folder)
    {
        var documents = Tabs.Select(t => new SessionMemory.DocumentEntry
        {
            FilePath = t == ActiveTab ? document.FilePath : t.FilePath,
            DocumentId = t == ActiveTab ? document.DocumentId : t.DocumentId,
        }).ToList();
        var activeId = document.DocumentId;
        documents = documents.Where(d => d.FilePath != null || AutoBackup.Exists(null, d.DocumentId)).ToList();
        SessionMemory.Save(documents, Math.Max(0, documents.FindIndex(d => d.DocumentId == activeId)), folder);
    }

    /// <summary>Shows every remembered tab immediately, then reads only the active document.</summary>
    public async Task<string?> RestoreSessionAsync()
    {
        var session = SessionMemory.Load();
        if (session == null || session.Documents.Count == 0) return session?.Folder;
        // A missing crash backup must not turn into an empty draft tab. Named files have already been filtered
        // by SessionMemory.Load. Keeping their document IDs preserves each file's own backup on first open.
        var entries = session.Documents.Where(e => e.FilePath != null || AutoBackup.Exists(null, e.DocumentId)).ToList();
        if (entries.Count == 0) return session.Folder;
        var activeId = session.Documents[Math.Clamp(session.ActiveIndex, 0, session.Documents.Count - 1)].DocumentId;
        var restored = entries.Select(e => new DocumentTab
        {
            DocumentId = e.DocumentId,
            FilePath = e.FilePath,
            IsDeferred = true,
            IsDirty = AutoBackup.Exists(e.FilePath, e.DocumentId),
        }).ToList();
        RestoringSession = true;
        try
        {
            ActiveTab = restored.FirstOrDefault(t => t.DocumentId == activeId) ?? restored[0];
            Tabs.Clear();
            foreach (var tab in restored) Tabs.Add(tab);
            await LoadDeferredAsync(ActiveTab);
        }
        finally
        {
            RestoringSession = false;
            OnPropertyChanged(nameof(ActiveTab));
        }
        return session.Folder;
    }

    private async Task LoadDeferredAsync(DocumentTab tab)
    {
        document.PrepareNewTab(tab.DocumentId);
        if (tab.FilePath is { } path && await document.LoadAsync(path))
        {
            tab.IsDeferred = false;
            tab.IsDirty = !document.Saved;
            return;
        }
        // An untitled tab has a crash backup; a named file can disappear after SessionMemory.Load checked it.
        var backup = tab.FilePath == null ? AutoBackup.Read(null, tab.DocumentId) : null;
        if (tab.FilePath != null) tab.FilePath = null;
        await document.ResetToUntitledAsync(tab.DocumentId, discardCurrent: false, recoveredText: backup);
        tab.IsDeferred = false;
        tab.IsDirty = !document.Saved;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
