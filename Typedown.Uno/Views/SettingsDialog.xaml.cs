using Typedown.Uno.Services;

namespace Typedown.Uno.Views;

public sealed partial class SettingsDialog : ContentDialog
{
    private readonly AppSettings settings;
    private bool loading = true;

    private static readonly (string value, string key)[] Languages = { ("", "LangSystem"), ("zh-Hans", "中文（简体）"), ("en", "English") };
    private static readonly string[] Indentations = { "1", "2", "4", "tab" };

    public SettingsDialog(AppSettings settings)
    {
        this.settings = settings;
        this.InitializeComponent();
        Title = Loc.Get("SettingsTitle");
        CloseButtonText = Loc.Get("Close");
        GeneralHeader.Text = Loc.Get("General");
        EditorHeader.Text = Loc.Get("Editor");
        HedgeDocHeader.Text = Loc.Get("HedgeDoc");

        LanguageBox.Header = Loc.Get("Language");
        foreach (var (value, key) in Languages) LanguageBox.Items.Add(value == "" ? Loc.Get(key) : key);
        LanguageBox.SelectedIndex = Math.Max(0, Array.FindIndex(Languages, l => l.value == settings.Language));

        ThemeBox.Header = Loc.Get("Theme");
        foreach (var t in Enum.GetValues<AppTheme>()) ThemeBox.Items.Add(Loc.Get("Theme" + t));
        ThemeBox.SelectedIndex = (int)settings.Theme;

        Bind(AutoSaveSwitch, "AutoSave", settings.AutoSave);
        Bind(RememberPositionSwitch, "RememberPosition", settings.RememberPosition);
        Bind(RestoreSessionSwitch, "RestoreSession", settings.RestoreSession);
        Bind(AlwaysShowTabBarSwitch, "AlwaysShowTabBar", settings.AlwaysShowTabBar);

        FontSizeBox.Header = Loc.Get("FontSize"); FontSizeBox.Value = settings.FontSize;
        LineHeightBox.Header = Loc.Get("LineHeight"); LineHeightBox.Value = settings.LineHeight;
        EditorWidthBox.Header = Loc.Get("EditorWidth"); EditorWidthBox.Text = settings.EditorAreaWidth;
        TabSizeBox.Header = Loc.Get("TabSize"); TabSizeBox.Value = settings.TabSize;
        ListIndentationBox.Header = Loc.Get("ListIndentation");
        foreach (var i in Indentations) ListIndentationBox.Items.Add(i);
        ListIndentationBox.SelectedIndex = Math.Max(0, Array.IndexOf(Indentations, settings.ListIndentation));
        Bind(TableAlignSwitch, "TableAlign", settings.TableAlignColumns);
        Bind(LooseListSwitch, "LooseList", settings.PreferLooseListItem);
        Bind(ParagraphMarkerSwitch, "ParagraphMarker", settings.ShowParagraphMarker);
        Bind(SpellcheckSwitch, "Spellcheck", settings.SpellcheckEnabled);

        HedgeDocServerBox.Header = Loc.Get("Server"); HedgeDocServerBox.Text = settings.HedgeDocServer;
        HedgeDocEmailBox.Header = Loc.Get("Email"); HedgeDocEmailBox.Text = settings.HedgeDocEmail;
        HedgeDocPasswordBox.Header = Loc.Get("Password"); HedgeDocPasswordBox.Password = settings.HedgeDocPassword;
        Bind(PublishReadOnlySwitch, "PublishReadOnly", settings.HedgeDocPublishReadOnly);
        loading = false;
    }

    private static void Bind(ToggleSwitch toggle, string key, bool value)
    {
        toggle.Header = Loc.Get(key);
        toggle.OnContent = "";
        toggle.OffContent = "";
        toggle.IsOn = value;
    }

    private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (loading) return;
        settings.Language = Languages[Math.Max(0, LanguageBox.SelectedIndex)].value;
    }

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (loading || ThemeBox.SelectedIndex < 0) return;
        settings.Theme = (AppTheme)ThemeBox.SelectedIndex;
    }

    private void OnListIndentationChanged(object sender, SelectionChangedEventArgs e)
    {
        if (loading || ListIndentationBox.SelectedIndex < 0) return;
        settings.ListIndentation = Indentations[ListIndentationBox.SelectedIndex];
    }

    private void OnToggled(object sender, RoutedEventArgs e)
    {
        if (loading) return;
        var on = ((ToggleSwitch)sender).IsOn;
        if (sender == AutoSaveSwitch) settings.AutoSave = on;
        else if (sender == RememberPositionSwitch) settings.RememberPosition = on;
        else if (sender == RestoreSessionSwitch) settings.RestoreSession = on;
        else if (sender == AlwaysShowTabBarSwitch) settings.AlwaysShowTabBar = on;
        else if (sender == TableAlignSwitch) settings.TableAlignColumns = on;
        else if (sender == LooseListSwitch) settings.PreferLooseListItem = on;
        else if (sender == ParagraphMarkerSwitch) settings.ShowParagraphMarker = on;
        else if (sender == SpellcheckSwitch) settings.SpellcheckEnabled = on;
        else if (sender == PublishReadOnlySwitch) settings.HedgeDocPublishReadOnly = on;
    }

    private void OnNumberChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (loading || double.IsNaN(args.NewValue)) return;
        if (sender == FontSizeBox) settings.FontSize = (int)args.NewValue;
        else if (sender == LineHeightBox) settings.LineHeight = Math.Round(args.NewValue, 2);
        else if (sender == TabSizeBox) settings.TabSize = (int)args.NewValue;
    }

    private void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (loading) return;
        if (sender == EditorWidthBox) settings.EditorAreaWidth = EditorWidthBox.Text.Trim();
        else if (sender == HedgeDocServerBox) settings.HedgeDocServer = HedgeDocServerBox.Text.Trim();
        else if (sender == HedgeDocEmailBox) settings.HedgeDocEmail = HedgeDocEmailBox.Text.Trim();
    }

    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (loading) return;
        settings.HedgeDocPassword = HedgeDocPasswordBox.Password;
    }

    private async void OnTestClick(object sender, RoutedEventArgs e)
    {
        TestButton.IsEnabled = false;
        TestStatus.Text = "…";
        try { TestStatus.Text = await HedgeDocService.TestAsync(settings.HedgeDocServer, settings.HedgeDocEmail, settings.HedgeDocPassword); }
        catch (Exception ex) { TestStatus.Text = ex.Message; }
        finally { TestButton.IsEnabled = true; }
    }
}
