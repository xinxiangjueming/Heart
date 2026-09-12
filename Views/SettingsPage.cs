using Heart.Services;
using Heart.Ui;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Heart.Views;

public sealed class SettingsPage : PageBase
{
    private TextBlock _title = null!;
    private TextBlock _languageCaption = null!;
    private TextBlock _hintText = null!;
    private TextBlock _themeCaption = null!;
    private TextBlock _aboutText = null!;
    private ComboBox _languageCombo = null!;
    private ComboBox _themeCombo = null!;
    private bool _suppress;

    public SettingsPage()
    {
        BuildUi();
        ApplyLocalization();

        // 页面被主窗口缓存复用，Loaded / Unloaded 会在每次导航时反复触发，订阅必须成对
        Loaded += (_, _) => LocalizationService.Instance.LanguageChanged += OnLanguageChanged;
        Unloaded += (_, _) => LocalizationService.Instance.LanguageChanged -= OnLanguageChanged;
    }

    private void BuildUi()
    {
        _title = Miuix.Title("");

        _languageCaption = new TextBlock
        {
            FontSize = 15,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = Miuix.Brush("MiuixTextPrimary"),
        };
        _languageCombo = new ComboBox { MinWidth = 240 };
        _languageCombo.Items.Add(new ComboBoxItem());
        foreach (var _ in LocalizationService.Languages)
            _languageCombo.Items.Add(new ComboBoxItem());
        _languageCombo.SelectionChanged += OnLanguageSelectionChanged;

        _hintText = Miuix.Caption("");

        _themeCaption = new TextBlock
        {
            FontSize = 15,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = Miuix.Brush("MiuixTextPrimary"),
        };
        _themeCombo = new ComboBox { MinWidth = 240 };
        _themeCombo.Items.Add(new ComboBoxItem());
        _themeCombo.Items.Add(new ComboBoxItem());
        _themeCombo.Items.Add(new ComboBoxItem());
        _themeCombo.SelectionChanged += OnThemeSelectionChanged;

        _aboutText = Miuix.Caption("");

        var root = Miuix.Vertical(14,
            _title,
            Miuix.Card(Miuix.Vertical(10, _languageCaption, _languageCombo, _hintText)),
            Miuix.Card(Miuix.Vertical(10, _themeCaption, _themeCombo)),
            Miuix.Card(Miuix.Vertical(6, new TextBlock
            {
                Text = "Heart",
                FontSize = 15,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = Miuix.Brush("MiuixTextPrimary"),
            }, _aboutText)));
        root.MaxWidth = 760;
        root.HorizontalAlignment = HorizontalAlignment.Stretch;

        Content = root;
    }

    private void SyncSelection()
    {
        _suppress = true;
        var loc = LocalizationService.Instance;

        var langItems = _languageCombo.Items.OfType<ComboBoxItem>().ToList();
        langItems[0].Content = loc.T("language_auto");
        for (var i = 0; i < LocalizationService.Languages.Length; i++)
            langItems[i + 1].Content = LocalizationService.Languages[i].NativeName;
        _languageCombo.SelectedIndex = loc.Language == "auto"
            ? 0
            : 1 + Array.FindIndex(LocalizationService.Languages, l => l.Code == loc.Language);

        var themeItems = _themeCombo.Items.OfType<ComboBoxItem>().ToList();
        string[] themeKeys = { "theme_auto", "theme_light", "theme_dark" };
        for (var i = 0; i < themeKeys.Length; i++)
            themeItems[i].Content = loc.T(themeKeys[i]);
        _themeCombo.SelectedIndex = ThemeManager.Instance.Mode switch
        {
            "light" => 1,
            "dark" => 2,
            _ => 0,
        };
        _suppress = false;
    }

    public override void ApplyLocalization()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var loc = LocalizationService.Instance;
            _title.Text = loc.T("settings_title");
            _languageCaption.Text = loc.T("language");
            _themeCaption.Text = loc.T("theme");
            _hintText.Text = loc.T("settings_hint");
            _aboutText.Text = loc.T("settings_about");
            SyncSelection();
        });
    }

    private void OnLanguageChanged()
    {
        DispatcherQueue.TryEnqueue(ApplyLocalization);
    }

    private void OnLanguageSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || _languageCombo.SelectedIndex < 0)
            return;
        var index = _languageCombo.SelectedIndex;
        var code = index == 0 ? "auto" : LocalizationService.Languages[index - 1].Code;
        if (code == LocalizationService.Instance.Language)
            return;

        LocalizationService.Instance.SetLanguage(code);
        // SetLanguage 触发 LanguageChanged → ApplyLocalization 刷新全部页面文案
    }

    private void OnThemeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress)
            return;
        var mode = _themeCombo.SelectedIndex switch
        {
            1 => "light",
            2 => "dark",
            _ => "auto",
        };
        if (mode == ThemeManager.Instance.Mode)
            return;
        ThemeManager.Instance.SetMode(mode);
    }
}
