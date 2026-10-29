using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Typedown.Uno.Services;

namespace Typedown.Uno.ViewModels;

/// <summary>A file or folder in the side-pane tree; folder children are read on first expansion.</summary>
public sealed class FolderItem : INotifyPropertyChanged
{
    public static readonly HashSet<string> MarkdownExtensions = new(StringComparer.OrdinalIgnoreCase) { ".md", ".markdown", ".mdown", ".mkd", ".mkdn", ".mdwn", ".mdtxt", ".mdtext", ".rmd", ".txt", ".text" };

    public string FullPath { get; }
    public string Name => Path.GetFileName(FullPath);
    public bool IsFolder { get; }
    public string Glyph => IsFolder ? "" : "";
    public ObservableCollection<FolderItem> Children { get; } = new();
    private bool loaded;

    private bool isExpanded;
    public bool IsExpanded
    {
        get => isExpanded;
        set { if (isExpanded == value) return; isExpanded = value; OnPropertyChanged(); if (value) EnsureChildren(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public FolderItem(string fullPath, bool isFolder)
    {
        FullPath = fullPath;
        IsFolder = isFolder;
        if (isFolder) Children.Add(new FolderItem(Path.Combine(fullPath, "…"), false)); // placeholder so the expander shows
    }

    public void EnsureChildren()
    {
        if (!IsFolder || loaded) return;
        loaded = true;
        Refresh();
    }

    public void Refresh()
    {
        if (!IsFolder) return;
        Children.Clear();
        try
        {
            var dir = new DirectoryInfo(FullPath);
            foreach (var d in dir.EnumerateDirectories().Where(d => !d.Name.StartsWith('.') && !d.Attributes.HasFlag(System.IO.FileAttributes.Hidden)).OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
                Children.Add(new FolderItem(d.FullName, true));
            foreach (var f in dir.EnumerateFiles().Where(f => MarkdownExtensions.Contains(f.Extension) && !f.Attributes.HasFlag(System.IO.FileAttributes.Hidden)).OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
                Children.Add(new FolderItem(f.FullName, false));
        }
        catch
        {
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>A heading from the editor's StateChange toc: {content, lvl, slug}.</summary>
public sealed class OutlineItem
{
    public string Content { get; init; } = "";
    public int Level { get; init; }
    public string Slug { get; init; } = "";
    public Thickness Indent => new((Level - 1) * 14, 0, 0, 0);

    public static List<OutlineItem> FromToc(JsonNode? toc)
    {
        var list = new List<OutlineItem>();
        if (toc is not JsonArray array) return list;
        foreach (var item in array)
        {
            if (item == null) continue;
            list.Add(new OutlineItem
            {
                Content = item["content"]?.GetValue<string>() ?? "",
                Level = item["lvl"]?.GetValue<int>() ?? 1,
                Slug = item["slug"]?.ToString() ?? "",
            });
        }
        return list;
    }
}

public enum SearchMatchKind { Name, Content, AlsoContent }

public sealed class SearchResultItem : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public string FullPath { get; init; } = "";
    public string FileName => Path.GetFileName(FullPath);
    public string RelativePath { get; init; } = "";
    public SearchMatchKind Kind { get; init; }
    public bool IsFolder { get; init; }
    public string GroupHeading { get; init; } = "";
    public Microsoft.UI.Xaml.Visibility GroupHeaderVisibility => GroupHeading.Length == 0 ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;
    public string Snippet { get; init; } = "";
    private string? alsoContentSnippet;
    public bool ContentMatched => Kind == SearchMatchKind.Content || alsoContentSnippet != null;
    public string DisplaySnippet => Kind == SearchMatchKind.Name
        ? alsoContentSnippet == null ? Loc.Get(IsFolder ? "FolderName" : "FileName") : Loc.Get("SearchAlsoInContent") + " · " + alsoContentSnippet
        : Snippet;

    public void MarkAlsoContent(string snippet)
    {
        if (alsoContentSnippet != null) return;
        alsoContentSnippet = snippet;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplaySnippet)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ContentMatched)));
    }
}

public static class FolderSearch
{
    private const int NameCap = 100;
    private const int ContentCap = 200;
    private const long MaxFileBytes = 8 * 1024 * 1024;

    /// <summary>Names first (including folders), then file contents. A file matching both is one name row.</summary>
    public static (int files, int hits) Run(string root, string query, Action<SearchResultItem> report, CancellationToken token)
    {
        var namedFiles = new HashSet<string>(StringComparer.Ordinal);
        var toRead = new List<string>();
        var pending = new Queue<string>();
        pending.Enqueue(root);
        var names = 0;
        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var folder = pending.Dequeue();
            IEnumerable<FileSystemInfo> entries;
            try { entries = new DirectoryInfo(folder).EnumerateFileSystemInfos().OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList(); }
            catch { continue; }
            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested();
                if ((entry.Attributes & (System.IO.FileAttributes.Hidden | System.IO.FileAttributes.System | System.IO.FileAttributes.ReparsePoint)) != 0) continue;
                var isFolder = entry is DirectoryInfo;
                if (isFolder)
                {
                    if (entry.Name.StartsWith('.') || entry.Name == "node_modules") continue;
                    pending.Enqueue(entry.FullName);
                }
                else
                {
                    if (!FolderItem.MarkdownExtensions.Contains(entry.Extension)) continue;
                    try { if (((FileInfo)entry).Length <= MaxFileBytes) toRead.Add(entry.FullName); }
                    catch { }
                }
                if (names >= NameCap || !entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
                if (!isFolder) namedFiles.Add(entry.FullName);
                report(new SearchResultItem { Kind = SearchMatchKind.Name, IsFolder = isFolder, FullPath = entry.FullName,
                    RelativePath = Relative(root, entry.FullName), GroupHeading = names == 0 ? Loc.Get("SearchNames") : "" });
                names++;
            }
        }
        var contents = 0;
        var filesRead = 0;
        foreach (var path in toRead)
        {
            if (contents >= ContentCap) break;
            token.ThrowIfCancellationRequested();
            filesRead++;
            string? snippet = null;
            try
            {
                foreach (var line in File.ReadLines(path))
                {
                    token.ThrowIfCancellationRequested();
                    var index = line.IndexOf(query, StringComparison.OrdinalIgnoreCase);
                    if (index < 0) continue;
                    var start = Math.Max(0, index - 40);
                    snippet = (start > 0 ? "…" : "") + line.Substring(start, Math.Min(line.Length - start, 120)).Trim();
                    break;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { continue; }
            if (snippet == null) continue;
            if (namedFiles.Contains(path))
                report(new SearchResultItem { Kind = SearchMatchKind.AlsoContent, FullPath = path, Snippet = snippet });
            else
            {
                report(new SearchResultItem { Kind = SearchMatchKind.Content, FullPath = path, RelativePath = Relative(root, path),
                    Snippet = snippet, GroupHeading = contents == 0 ? Loc.Get("SearchContents") : "" });
                contents++;
            }
        }
        return (filesRead, names + contents);
    }

    private static string Relative(string root, string path) =>
        path.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            ? path[root.Length..].TrimStart('\\', '/') : path;
}
