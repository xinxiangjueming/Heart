using Heart.Models;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Heart.Services;

/// <summary>
/// 主题管理：auto（跟随系统，注册表轮询）/ light / dark。
/// 运行中切换通过根元素 RequestedTheme + Miuix.ApplyTheme 实现实时生效。
/// </summary>
public sealed class ThemeManager
{
    public static ThemeManager Instance { get; } = new();

    /// <summary>实际生效的主题变化（模式切换或系统主题变化），UI 线程触发。</summary>
    public event Action<ElementTheme>? EffectiveThemeChanged;

    private const string PersonalizeKey =
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private DispatcherQueueTimer? _pollTimer;
    private bool _lastSystemDark;

    /// <summary>用户选择："auto" | "light" | "dark"。</summary>
    public string Mode { get; private set; } = "auto";

    public void Initialize()
    {
        Mode = SettingsStore.GetSetting("theme", "auto");
        _lastSystemDark = SystemIsDark();
    }

    /// <summary>在 UI 线程调用：启动 2 秒轮询兜底检测系统主题变化。</summary>
    public void StartPolling(DispatcherQueue dispatcher)
    {
        _pollTimer = dispatcher.CreateTimer();
        _pollTimer.Interval = TimeSpan.FromSeconds(2);
        _pollTimer.Tick += (_, _) =>
        {
            var dark = SystemIsDark();
            if (dark == _lastSystemDark)
                return;
            _lastSystemDark = dark;
            if (Mode == "auto")
                EffectiveThemeChanged?.Invoke(ResolvedTheme);
        };
        _pollTimer.Start();
    }

    public void SetMode(string mode)
    {
        Mode = mode;
        SettingsStore.SetSetting("theme", mode);
        EffectiveThemeChanged?.Invoke(ResolvedTheme);
    }

    /// <summary>解析当前应使用的元素主题。</summary>
    public ElementTheme ResolvedTheme =>
        Mode switch
        {
            "light" => ElementTheme.Light,
            "dark" => ElementTheme.Dark,
            _ => SystemIsDark() ? ElementTheme.Dark : ElementTheme.Light,
        };

    public static bool SystemIsDark()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch
        {
            return false;
        }
    }
}
