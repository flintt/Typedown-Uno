using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

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

public sealed class SearchResultItem
{
    public string FullPath { get; init; } = "";
    public string FileName => Path.GetFileName(FullPath);
    public string RelativePath { get; init; } = "";
    public string Snippet { get; init; } = "";
}

public static class FolderSearch
{
    private const int MaxResults = 200;
    private const long MaxFileBytes = 4 * 1024 * 1024;

    /// <summary>Case-insensitive substring search over the Markdown files of a folder (content and file names).</summary>
    public static (int files, int hits) Run(string root, string query, Action<SearchResultItem> report, CancellationToken token)
    {
        int files = 0, hits = 0;
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0 && hits < MaxResults)
        {
            token.ThrowIfCancellationRequested();
            var folder = pending.Pop();
            IEnumerable<FileSystemInfo> entries;
            try { entries = new DirectoryInfo(folder).EnumerateFileSystemInfos(); } catch { continue; }
            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested();
                if (entry.Attributes.HasFlag(System.IO.FileAttributes.Hidden)) continue;
                if (entry is DirectoryInfo)
                {
                    if (!entry.Name.StartsWith('.') && entry.Name != "node_modules") pending.Push(entry.FullName);
                    continue;
                }
                if (!FolderItem.MarkdownExtensions.Contains(entry.Extension) || ((FileInfo)entry).Length > MaxFileBytes) continue;
                files++;
                string? snippet = null;
                try
                {
                    foreach (var line in File.ReadLines(entry.FullName))
                    {
                        var index = line.IndexOf(query, StringComparison.OrdinalIgnoreCase);
                        if (index < 0) continue;
                        var start = Math.Max(0, index - 40);
                        snippet = (start > 0 ? "…" : "") + line.Substring(start, Math.Min(line.Length - start, 120)).Trim();
                        break;
                    }
                }
                catch { continue; }
                if (snippet == null && entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase)) snippet = "(file name)";
                if (snippet == null) continue;
                hits++;
                var relative = entry.FullName.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? entry.FullName[root.Length..].TrimStart('\\', '/') : entry.FullName;
                report(new SearchResultItem { FullPath = entry.FullName, RelativePath = relative, Snippet = snippet });
                if (hits >= MaxResults) break;
            }
        }
        return (files, hits);
    }
}
