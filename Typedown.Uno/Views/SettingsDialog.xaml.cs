using Typedown.Uno.Services;
using Windows.Storage.Pickers;

namespace Typedown.Uno.Views;

public sealed partial class SettingsDialog : ContentDialog
{
    private readonly AppSettings settings;
    private bool loading = true;

    private static readonly (string value, string label)[] Languages = { ("", "LangSystem"), ("zh-Hans", "中文（简体）"), ("en", "English") };
    private static readonly string[] Indentations = { "1", "2", "4", "tab" };
    private static readonly string[] Directions = { "auto", "ltr", "rtl" };

    public SettingsDialog(AppSettings settings)
    {
        this.settings = settings;
        this.InitializeComponent();
        Title = Loc.Get("SettingsTitle");
        CloseButtonText = Loc.Get("Close");
        Build();
        loading = false;
    }

    private void Build()
    {
        Header("General");
        Combo("Language", Languages.Select(l => l.value == "" ? Loc.Get(l.label) : l.label), Math.Max(0, Array.FindIndex(Languages, l => l.value == settings.Language)),
            i => settings.Language = Languages[i].value);
        Combo("Theme", Enum.GetValues<AppTheme>().Select(t => Loc.Get("Theme" + t)), (int)settings.Theme, i => settings.Theme = (AppTheme)i);
        Combo("StartupAction", Enum.GetValues<FileStartupAction>().Select(a => Loc.Get("Startup" + a)), (int)settings.FileStartupAction, i => settings.FileStartupAction = (FileStartupAction)i);
        Combo("FolderStartup", Enum.GetValues<FolderStartupAction>().Select(a => Loc.Get("FolderStartup" + a)), (int)settings.FolderStartupAction, i => settings.FolderStartupAction = (FolderStartupAction)i);
        FolderRow("StartupFolder", () => settings.StartupFolder, v => settings.StartupFolder = v);
        Toggle("OpenFilesInNewTab", settings.OpenFilesInNewTab, v => settings.OpenFilesInNewTab = v);
        Toggle("AlwaysShowTabBar", settings.AlwaysShowTabBar, v => settings.AlwaysShowTabBar = v);
        Toggle("StatusBar", settings.StatusBarOpen, v => settings.StatusBarOpen = v);
        Combo("WordCountMethod", Enum.GetValues<WordCountMethod>().Select(m => Loc.Get("Count" + m)), (int)settings.WordCountMethod, i => settings.WordCountMethod = (WordCountMethod)i);

        Header("FilesSection");
        Toggle("AutoSave", settings.AutoSave, v => settings.AutoSave = v);
        Toggle("RememberPosition", settings.RememberPosition, v => settings.RememberPosition = v);
        Toggle("AutoReload", settings.AutoReload, v => settings.AutoReload = v, "AutoReloadDescription");
        Toggle("AskBeforeReload", settings.AskBeforeReload, v => settings.AskBeforeReload = v);
        Toggle("OpenFolderAfterExport", settings.OpenFolderAfterExport, v => settings.OpenFolderAfterExport = v);

        Header("Editor");
        Number("FontSize", settings.FontSize, 8, 48, 1, v => settings.FontSize = (int)v);
        Number("LineHeight", settings.LineHeight, 1, 3, 0.1, v => settings.LineHeight = Math.Round(v, 2));
        Text("EditorWidth", settings.EditorAreaWidth, v => settings.EditorAreaWidth = v);
        Text("FontFamily", settings.FontFamily, v => settings.FontFamily = v, "FontFamilyPlaceholder");
        Number("TabSize", settings.TabSize, 1, 8, 1, v => settings.TabSize = (int)v);
        Combo("TextDirection", Directions.Select(d => Loc.Get("Dir" + d)), Math.Max(0, Array.IndexOf(Directions, settings.TextDirection)), i => settings.TextDirection = Directions[i]);
        Combo("ListIndentation", Indentations, Math.Max(0, Array.IndexOf(Indentations, settings.ListIndentation)), i => settings.ListIndentation = Indentations[i]);
        Toggle("TableAlign", settings.TableAlignColumns, v => settings.TableAlignColumns = v);
        Toggle("LooseList", settings.PreferLooseListItem, v => settings.PreferLooseListItem = v);
        Toggle("TrimCodeBlock", settings.TrimUnnecessaryCodeBlockEmptyLines, v => settings.TrimUnnecessaryCodeBlockEmptyLines = v);
        Toggle("ParagraphMarker", settings.ShowParagraphMarker, v => settings.ShowParagraphMarker = v);
        Toggle("Spellcheck", settings.SpellcheckEnabled, v => settings.SpellcheckEnabled = v);
        Toggle("AutoPairBracket", settings.AutoPairBracket, v => settings.AutoPairBracket = v);
        Toggle("AutoPairQuote", settings.AutoPairQuote, v => settings.AutoPairQuote = v);
        Toggle("AutoPairMarkdown", settings.AutoPairMarkdownSyntax, v => settings.AutoPairMarkdownSyntax = v);
        MultilineText("CustomCss", settings.CustomCss, v => settings.CustomCss = v);

        Header("ImageSection");
        Combo("ImageAction", new[] { Loc.Get("ImageCopy"), Loc.Get("ImageKeep") }, (int)settings.ImageAction, i => settings.ImageAction = (ImageInsertAction)i);
        Text("ImageCopyPath", settings.ImageCopyPath, v => settings.ImageCopyPath = v, hint: "./${filename}.assets");
        Container.Children.Add(new TextBlock { Text = Loc.Get("ImageCopyPathHint"), Opacity = 0.6, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) });
        Toggle("PreferRelativeImagePaths", settings.PreferRelativeImagePaths, v => settings.PreferRelativeImagePaths = v);
        Toggle("EncodeImageLinks", settings.EncodeImageLinks, v => settings.EncodeImageLinks = v);

        Header("FindSection");
        Toggle("FindCaseSensitive", settings.FindCaseSensitive, v => settings.FindCaseSensitive = v);
        Toggle("FindWholeWord", settings.FindWholeWord, v => settings.FindWholeWord = v);
        Toggle("FindRegex", settings.FindRegex, v => settings.FindRegex = v);

        Header("HedgeDoc");
        Text("Server", settings.HedgeDocServer, v => settings.HedgeDocServer = v, placeholder: null, hint: "https://md.example.com");
        Text("Email", settings.HedgeDocEmail, v => settings.HedgeDocEmail = v);
        Password("Password", settings.HedgeDocPassword, v => settings.HedgeDocPassword = v);
        Toggle("PublishReadOnly", settings.HedgeDocPublishReadOnly, v => settings.HedgeDocPublishReadOnly = v);
        TestRow();
    }

    // ---- row builders -------------------------------------------------------------------------------------

    private void Header(string key) => Container.Children.Add(new TextBlock
    {
        Text = Loc.Get(key),
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        Margin = new Thickness(0, Container.Children.Count == 0 ? 0 : 14, 0, 6),
    });

    private void Toggle(string key, bool value, Action<bool> set, string? descriptionKey = null)
    {
        var toggle = new ToggleSwitch { IsOn = value, OnContent = "", OffContent = "", MinWidth = 0 };
        toggle.Toggled += (_, _) => { if (!loading) set(toggle.IsOn); };
        Container.Children.Add(Row(key, toggle, descriptionKey));
    }

    private void Combo(string key, IEnumerable<string> items, int selected, Action<int> set)
    {
        var combo = new ComboBox { MinWidth = 200 };
        foreach (var item in items) combo.Items.Add(item);
        combo.SelectedIndex = selected;
        combo.SelectionChanged += (_, _) => { if (!loading && combo.SelectedIndex >= 0) set(combo.SelectedIndex); };
        Container.Children.Add(Row(key, combo));
    }

    private void Number(string key, double value, double min, double max, double step, Action<double> set)
    {
        var box = new NumberBox { Value = value, Minimum = min, Maximum = max, SmallChange = step, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, MinWidth = 140 };
        box.ValueChanged += (_, args) => { if (!loading && !double.IsNaN(args.NewValue)) set(args.NewValue); };
        Container.Children.Add(Row(key, box));
    }

    private void Text(string key, string value, Action<string> set, string? placeholder = null, string? hint = null)
    {
        var box = new TextBox { Text = value, MinWidth = 220, PlaceholderText = hint ?? (placeholder != null ? Loc.Get(placeholder) : "") };
        box.TextChanged += (_, _) => { if (!loading) set(box.Text.Trim()); };
        Container.Children.Add(Row(key, box));
    }

    private void MultilineText(string key, string value, Action<string> set)
    {
        var box = new TextBox { Text = value, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 90, MinWidth = 220 };
        box.TextChanged += (_, _) => { if (!loading) set(box.Text); };
        Container.Children.Add(Row(key, box, stacked: true));
    }

    private void Password(string key, string value, Action<string> set)
    {
        var box = new PasswordBox { Password = value, MinWidth = 220 };
        box.PasswordChanged += (_, _) => { if (!loading) set(box.Password); };
        Container.Children.Add(Row(key, box));
    }

    private void FolderRow(string key, Func<string?> get, Action<string?> set)
    {
        var text = new TextBlock { Text = get() ?? Loc.Get("NoFolder"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 220 };
        var button = new Button { Content = Loc.Get("Browse") };
        button.Click += async (_, _) =>
        {
            // Same reason as in MainPage: the platform folder picker needs a desktop portal on Linux.
            string? path;
            if (OperatingSystem.IsLinux())
            {
                var dialog = new FilePickerDialog(FilePickerDialog.PickerMode.Folder, get() ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) { XamlRoot = XamlRoot, RequestedTheme = RequestedTheme };
                await dialog.ShowAsync();
                path = dialog.SelectedPath;
            }
            else
            {
                var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
                picker.FileTypeFilter.Add("*");
                path = (await picker.PickSingleFolderAsync())?.Path;
            }
            if (path == null) return;
            set(path);
            text.Text = path;
        };
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { text, button } };
        Container.Children.Add(Row(key, panel));
    }

    private void TestRow()
    {
        var status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7, TextWrapping = TextWrapping.Wrap, MaxWidth = 260 };
        var button = new Button { Content = Loc.Get("Test") };
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            status.Text = "…";
            try { status.Text = await HedgeDocService.TestAsync(settings.HedgeDocServer, settings.HedgeDocEmail, settings.HedgeDocPassword); }
            catch (Exception ex) { status.Text = ex.Message; }
            finally { button.IsEnabled = true; }
        };
        Container.Children.Add(Row("TestConnection", new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { button, status } }));
    }

    private static FrameworkElement Row(string key, FrameworkElement control, string? descriptionKey = null, bool stacked = false)
    {
        var label = new TextBlock { Text = Loc.Get(key), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        if (stacked)
        {
            var stack = new StackPanel { Spacing = 4, Margin = new Thickness(0, 4, 0, 4) };
            stack.Children.Add(label);
            stack.Children.Add(control);
            return stack;
        }
        var grid = new Grid { ColumnSpacing = 12, Margin = new Thickness(0, 4, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (descriptionKey != null)
        {
            var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(label);
            text.Children.Add(new TextBlock { Text = Loc.Get(descriptionKey), Opacity = 0.6, FontSize = 12, TextWrapping = TextWrapping.Wrap });
            grid.Children.Add(text);
        }
        else
        {
            grid.Children.Add(label);
        }
        Grid.SetColumn(control, 1);
        control.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(control);
        return grid;
    }
}
