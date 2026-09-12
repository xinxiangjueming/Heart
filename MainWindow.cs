using System.Reflection;
using System.Runtime.InteropServices;
using Heart.Services;
using Heart.Ui;
using Heart.Views;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI;

namespace Heart;

public sealed class MainWindow : Window
{
    private readonly NavigationView _navigation = new()
    {
        PaneDisplayMode = NavigationViewPaneDisplayMode.LeftCompact,
        IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed,
        IsSettingsVisible = false,
        OpenPaneLength = 180,
        CompactPaneLength = 60,
    };

    private readonly Grid _root = new();
    private readonly Dictionary<string, PageBase> _pages = new();
    private PageBase? _current;
    private string _currentTag = "";
    private SolidColorBrush? _paneBrush;
    private TrayIcon? _trayIcon;
    private bool _exitFromTray;

    private static readonly (string Tag, string Glyph, string TitleKey)[] NavItems =
    {
        ("devices", "\uE702", "nav_devices"),
        ("chart", "\uE9D9", "nav_chart"),
        ("settings", "\uE713", "nav_settings"),
    };

    public MainWindow()
    {
        Title = LocalizationService.Instance.T("app_title");
        App.WindowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        ApplyWindowIcon();

        try
        {
            AppWindow.Resize(new Windows.Graphics.SizeInt32(1220, 800));
            if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
            {
                presenter.PreferredMinimumWidth = 1000;
                presenter.PreferredMinimumHeight = 660;
            }
        }
        catch
        {
            // 个别环境下 presenter 调整失败不影响使用
        }

        BuildShell();
        ApplyTitleBarTheme();

        // 记录期间点 X（或 Alt+F4）不退出：取消关闭并隐藏到托盘继续记录；
        // 未在记录时 X 仍然正常退出
        AppWindow.Closing += OnAppWindowClosing;

        ThemeManager.Instance.EffectiveThemeChanged += OnEffectiveThemeChanged;
        LocalizationService.Instance.LanguageChanged += ApplyLocalization;
        // 侧栏 / 标题等窗口级文案平时只在语言切换时刷新，启动时必须先填充一次，
        // 否则导航项只创建过图标（Tag + Icon），展开侧栏没有任何文字
        ApplyLocalization();
        Closed += (_, _) =>
        {
            ThemeManager.Instance.EffectiveThemeChanged -= OnEffectiveThemeChanged;
            LocalizationService.Instance.LanguageChanged -= ApplyLocalization;
            App.Ble.Dispose();
        };

        Navigate("devices");
    }

    private void BuildShell()
    {
        foreach (var (tag, glyph, _) in NavItems)
        {
            _navigation.MenuItems.Add(new NavigationViewItem
            {
                Tag = tag,
                Icon = new FontIcon { Glyph = glyph },
            });
        }
        _navigation.SelectionChanged += OnNavigationChanged;
        SetupPaneBackground();

        // 窗口底色必须由 miuix 共享画笔提供：NavigationView 内容区的默认背景来自
        // Application.Resources 里的 XamlControlsResources 主题字典，而 _root.RequestedTheme
        // 只作用于窗口子树、改不到应用级字典，于是切主题时页面底会停在旧值（"半切换"）。
        // 用共享实例则随 Miuix.ApplyTheme 直接改色，不依赖任何主题字典机制。
        _root.Background = Miuix.Brush("MiuixPageBackground");

        _root.Children.Add(_navigation);
        Content = _root;
    }

    private void OnNavigationChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem item && item.Tag is string tag)
            Navigate(tag);
    }

    private void Navigate(string tag)
    {
        App.DebugLog($"navigate {tag}");
        var page = GetOrCreatePage(tag);
        if (_current == page)
            return;
        var previousTag = _currentTag;
        _current = page;
        _currentTag = tag;
        _navigation.Content = page;
        PlayPageEnter(page, SlideDirectionOf(previousTag, tag));
        // 页面被缓存复用，离场期间语言可能已切换，重新进入时刷新文案
        page.ApplyLocalization();
        _navigation.SelectedItem = _navigation.MenuItems
            .OfType<NavigationViewItem>()
            .FirstOrDefault(i => (string?)i.Tag == tag);
    }

    /// <summary>
    /// 滑动方向：目标页在导航中更靠后 → +1（从右侧滑入），更靠前 → −1（从左侧滑入）；
    /// 没有上一页（启动首屏）→ 0（只淡入，不滑动）。
    /// </summary>
    private static int SlideDirectionOf(string previousTag, string tag)
    {
        if (string.IsNullOrEmpty(previousTag))
            return 0;
        var previous = Array.FindIndex(NavItems, item => item.Tag == previousTag);
        var next = Array.FindIndex(NavItems, item => item.Tag == tag);
        return Math.Sign(next - previous);
    }

    /// <summary>
    /// 页面切换进场动画：随导航方向水平滑入 + 淡入。页面被缓存复用，每次切换都要重播。
    /// 只动 RenderTransform / Opacity，不触发布局。
    /// </summary>
    private static void PlayPageEnter(PageBase page, int direction)
    {
        var transform = page.RenderTransform as CompositeTransform ?? new CompositeTransform();
        page.RenderTransform = transform;

        var storyboard = new Storyboard();

        var fade = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(220)),
        };
        Storyboard.SetTarget(fade, page);
        Storyboard.SetTargetProperty(fade, "Opacity");
        storyboard.Children.Add(fade);

        if (direction != 0)
        {
            var slide = new DoubleAnimation
            {
                From = 24 * direction, // 往右切从右侧(+24)滑入，往左切从左侧(−24)滑入
                To = 0,
                Duration = new Duration(TimeSpan.FromMilliseconds(220)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            Storyboard.SetTarget(slide, page);
            Storyboard.SetTargetProperty(slide, "(UIElement.RenderTransform).(CompositeTransform.TranslateX)");
            storyboard.Children.Add(slide);
        }
        else
        {
            transform.TranslateX = 0; // 启动首屏不做位移，停留在原位
        }

        storyboard.Begin();
    }

    /// <summary>
    /// 侧栏底色：收起时融入页面底色，展开时用卡片色。默认模板在收起/展开两个
    /// 视觉状态间直接换资源画笔，颜色瞬间跳变，这里让两个状态共用同一支画笔
    /// （覆盖对应主题资源键），再借 PaneOpening / Closing 事件把颜色做成渐变。
    /// </summary>
    private void SetupPaneBackground()
    {
        _paneBrush = new SolidColorBrush(PaneColor(expanded: false));
        _navigation.Resources["NavigationViewDefaultPaneBackground"] = _paneBrush;
        _navigation.Resources["NavigationViewExpandedPaneBackground"] = _paneBrush;

        // 内容区（右侧页面区）默认背景同样是 XamlControlsResources 主题字典提供的，
        // 不跟随 _root.RequestedTheme 切换，必须显式覆盖成共享页面底色画笔。
        _navigation.Resources["NavigationViewContentBackground"] = Miuix.Brush("MiuixPageBackground");

        _navigation.PaneOpening += (_, _) => AnimatePaneColor(expanded: true);
        _navigation.PaneClosing += (_, _) => AnimatePaneColor(expanded: false);
    }

    private static Color PaneColor(bool expanded)
    {
        var brush = (SolidColorBrush)(expanded
            ? Miuix.Brush("MiuixCardBackground")
            : Miuix.Brush("MiuixPageBackground"));
        return brush.Color;
    }

    private void AnimatePaneColor(bool expanded)
    {
        if (_paneBrush is null || _paneBrush.Color == PaneColor(expanded))
            return;

        var anim = new ColorAnimation
        {
            To = PaneColor(expanded),
            Duration = new Duration(TimeSpan.FromMilliseconds(240)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(anim, _paneBrush);
        Storyboard.SetTargetProperty(anim, "Color");

        var storyboard = new Storyboard();
        storyboard.Children.Add(anim);
        storyboard.Begin();
    }

    private PageBase GetOrCreatePage(string tag)
    {
        if (_pages.TryGetValue(tag, out var page))
            return page;
        page = tag switch
        {
            "chart" => new ChartPage(),
            "settings" => new SettingsPage(),
            _ => new DevicesPage(),
        };
        _pages[tag] = page;
        return page;
    }

    private void OnEffectiveThemeChanged(ElementTheme theme)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _root.RequestedTheme = theme;
            Miuix.ApplyTheme(theme);
            // 侧栏画笔不参与颜色渐变，切主题时直接对齐当前展开状态的底色
            if (_paneBrush is not null)
                _paneBrush.Color = PaneColor(_navigation.IsPaneOpen);
            ApplyTitleBarTheme();
            foreach (var page in _pages.Values)
                page.ApplyTheme();
        });
    }

    private void ApplyLocalization()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            Title = LocalizationService.Instance.T("app_title");
            var items = _navigation.MenuItems.OfType<NavigationViewItem>().ToList();
            for (var i = 0; i < NavItems.Length && i < items.Count; i++)
                items[i].Content = LocalizationService.Instance.T(NavItems[i].TitleKey);

            foreach (var page in _pages.Values)
                page.ApplyLocalization();
        });
    }

    private void ApplyTitleBarTheme()
    {
        try
        {
            var dark = ThemeManager.Instance.ResolvedTheme == ElementTheme.Dark;
            var tb = AppWindow.TitleBar;
            // 直接取 miuix 共享画笔的当前值，不再复制一份硬编码色号——
            // 两份常量各自维护必然漂移（改一处忘了另一处，标题栏与页面底就对不上）
            var bg = ((SolidColorBrush)Miuix.Brush("MiuixPageBackground")).Color;
            var fg = ((SolidColorBrush)Miuix.Brush("MiuixTextPrimary")).Color;
            tb.BackgroundColor = bg;
            tb.ForegroundColor = fg;
            tb.ButtonBackgroundColor = bg;
            tb.ButtonForegroundColor = fg;
            tb.ButtonHoverBackgroundColor = dark
                ? ((SolidColorBrush)Miuix.Brush("MiuixSubtle")).Color
                : Color.FromArgb(0xFF, 0xE8, 0xEA, 0xEE);
            tb.ButtonHoverForegroundColor = fg;
        }
        catch
        {
            // 个别环境设置失败时使用系统默认
        }
    }

    /// <summary>
    /// 关闭拦截：记录中取消关闭并隐藏到托盘；托盘里点「退出软件」保存完成后
    /// 会置 _exitFromTray 放行。未在记录时直接放行（正常退出）。
    /// </summary>
    private void OnAppWindowClosing(Microsoft.UI.Windowing.AppWindow sender,
        Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        if (_exitFromTray || !RecordingService.Instance.IsRecording)
            return;
        args.Cancel = true;
        try
        {
            HideToTray();
        }
        catch (Exception ex)
        {
            // 隐藏托盘失败只记日志不拦截关闭流程：窗口保持打开，记录继续
            App.ReportCrash(ex);
        }
    }

    private void HideToTray()
    {
        if (_trayIcon is null)
        {
            ApplyWindowIcon(); // 确保 %LOCALAPPDATA% 的 app.ico 已释放，托盘直接复用
            _trayIcon = new TrayIcon(
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Heart", "app.ico"),
                RestoreFromTray,
                () => _ = ExitFromTrayAsync());
        }
        _trayIcon.Show(LocalizationService.Instance.T("app_title"));
        AppWindow.Hide();
        App.DebugLog("tray: hidden (recording continues)");
    }

    private void RestoreFromTray()
    {
        _trayIcon?.Dispose();
        _trayIcon = null;
        ShowWindow(App.WindowHandle, SW_RESTORE); // 兼容先最小化再隐藏的情况
        AppWindow.Show();
        SetForegroundWindow(App.WindowHandle);
        Activate();
        App.DebugLog("tray: window restored");
    }

    /// <summary>
    /// 托盘菜单「退出软件」：先恢复窗口（保存路径选择器需要可见、前台的主窗口），
    /// 有记录数据时弹出 CSV 保存框；用户取消保存则放弃退出、记录继续，保存成功后退出。
    /// </summary>
    private async Task ExitFromTrayAsync()
    {
        try
        {
            RestoreFromTray();
            if (RecordingService.Instance.Rows.Count > 0)
            {
                var file = await PickSaveFileAsync();
                if (file is null)
                    return;
                await Windows.Storage.FileIO.WriteTextAsync(file, RecordingService.Instance.BuildCsv());
            }
            _exitFromTray = true;
            RecordingService.Instance.Stop();
            Close();
        }
        catch (Exception ex)
        {
            App.ReportCrash(ex);
        }
    }

    private async Task<Windows.Storage.StorageFile?> PickSaveFileAsync()
    {
        var picker = new Windows.Storage.Pickers.FileSavePicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);
        picker.SuggestedFileName = "heart_rate_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
        picker.FileTypeChoices.Add("CSV", new List<string> { ".csv" });
        picker.DefaultFileExtension = ".csv";
        return await picker.PickSaveFileAsync();
    }

    /// <summary>把嵌入的应用图标释放到本地并应用到窗口 / 任务栏。</summary>
    private void ApplyWindowIcon()
    {
        try
        {
            var iconPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Heart", "app.ico");

            if (!File.Exists(iconPath))
            {
                using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.ico");
                if (stream is null)
                    return;
                Directory.CreateDirectory(Path.GetDirectoryName(iconPath)!);
                using var file = File.Create(iconPath);
                stream.CopyTo(file);
            }

            AppWindow.SetIcon(iconPath);
        }
        catch
        {
            // 图标加载失败不影响运行
        }
    }

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private const int SW_RESTORE = 9;
}
