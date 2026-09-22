using Microsoft.UI.Xaml.Input;
using Typedown.Uno.Services;

namespace Typedown.Uno.Views;

/// <summary>
/// File chooser drawn by the app itself. The platform pickers on Skia/Linux go through the XDG desktop portal,
/// which is missing on plenty of desktops (and in any headless session) — there the native dialog never appears
/// and opening or saving a file is impossible. This dialog has no such dependency.
/// </summary>
public sealed partial class FilePickerDialog : ContentDialog
{
    public enum PickerMode { OpenFile, SaveFile, Folder }

    private sealed class Entry
    {
        public string Path { get; init; } = "";
        public string Display { get; init; } = "";
        public bool IsFolder { get; init; }
        public override string ToString() => Display;
    }

    private readonly PickerMode mode;
    private readonly string[] extensions;
    private string directory = "";

    public string? SelectedPath { get; private set; }

    public FilePickerDialog(PickerMode mode, string? startDirectory, string? suggestedName = null, IEnumerable<string>? extensions = null)
    {
        this.mode = mode;
        this.extensions = extensions?.Select(e => e.ToLowerInvariant()).ToArray() ?? Array.Empty<string>();
        this.InitializeComponent();

        Title = Loc.Get(mode switch { PickerMode.SaveFile => "SaveAs", PickerMode.Folder => "OpenFolder", _ => "Open" });
        PrimaryButtonText = Loc.Get(mode == PickerMode.SaveFile ? "Save" : "OK");
        CloseButtonText = Loc.Get("Cancel");
        NameLabel.Text = Loc.Get(mode == PickerMode.Folder ? "FolderName" : "FileName");
        NameBox.Text = suggestedName ?? "";
        NameBox.Visibility = mode == PickerMode.Folder ? Visibility.Collapsed : Visibility.Visible;
        NameLabel.Visibility = NameBox.Visibility;
        Navigate(Directory.Exists(startDirectory) ? startDirectory! : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        PrimaryButtonClick += OnPrimaryClick;
        // Saving starts in the name box (the name is usually edited), opening starts in the list (keyboard navigation).
        Opened += (_, _) => (mode == PickerMode.SaveFile ? (Control)NameBox : Entries).Focus(FocusState.Programmatic);
    }

    private void Navigate(string path)
    {
        try
        {
            directory = Path.GetFullPath(path);
            PathBox.Text = directory;
            var items = new List<Entry>();
            var info = new DirectoryInfo(directory);
            foreach (var dir in info.EnumerateDirectories().Where(Visible).OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
                items.Add(new Entry { Path = dir.FullName, Display = "📁  " + dir.Name, IsFolder = true });
            if (mode != PickerMode.Folder)
                foreach (var file in info.EnumerateFiles().Where(Visible).Where(Matches).OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
                    items.Add(new Entry { Path = file.FullName, Display = "　  " + file.Name, IsFolder = false });
            Entries.ItemsSource = items;
        }
        catch (Exception ex)
        {
            Services.Log.Error($"picker navigate {path}", ex);
        }
    }

    private static bool Visible(FileSystemInfo entry) =>
        !entry.Name.StartsWith('.') && !entry.Attributes.HasFlag(System.IO.FileAttributes.Hidden);

    private bool Matches(FileInfo file) =>
        extensions.Length == 0 || extensions.Contains(file.Extension.ToLowerInvariant());

    private void OnUpClick(object sender, RoutedEventArgs e)
    {
        var parent = Path.GetDirectoryName(directory.TrimEnd(Path.DirectorySeparatorChar));
        if (parent != null) Navigate(parent);
    }

    private void OnPathKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter) return;
        e.Handled = true;
        var text = PathBox.Text.Trim();
        if (Directory.Exists(text)) Navigate(text);
        else if (File.Exists(text)) { SelectedPath = text; Hide(); }
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Entries.SelectedItem is Entry { IsFolder: false } entry) NameBox.Text = Path.GetFileName(entry.Path);
    }

    private void OnItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is Entry { IsFolder: false } entry) NameBox.Text = Path.GetFileName(entry.Path);
    }

    private void OnItemDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (Entries.SelectedItem is not Entry entry) return;
        if (entry.IsFolder) Navigate(entry.Path);
        else if (mode != PickerMode.Folder) { SelectedPath = entry.Path; Hide(); }
    }

    private void OnNameKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter) return;
        e.Handled = true;
        Confirm();
        if (SelectedPath != null) Hide();
    }

    private void OnPrimaryClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        Confirm();
        if (SelectedPath == null) args.Cancel = true;
    }

    private void Confirm()
    {
        if (mode == PickerMode.Folder)
        {
            SelectedPath = Entries.SelectedItem is Entry { IsFolder: true } selected ? selected.Path : directory;
            return;
        }
        var name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            if (Entries.SelectedItem is Entry { IsFolder: false } selected) SelectedPath = selected.Path;
            return;
        }
        var path = Path.IsPathRooted(name) ? name : Path.Combine(directory, name);
        if (mode == PickerMode.OpenFile && !File.Exists(path)) return;
        SelectedPath = path;
    }
}
