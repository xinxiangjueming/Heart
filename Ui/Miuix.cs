using Heart.Models;
using Heart.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Heart.Ui;

/// <summary>
/// miuix / HyperOS 视觉体系：配色、圆角、卡片与常用控件工厂。
/// 所有画笔为共享实例，切换深浅色时直接改 Color，全界面实时生效。
/// </summary>
public static class Miuix
{
    // 圆角（miuix 风格：卡片 18、控件 16、按钮胶囊 14）
    public const double CardRadius = 18;
    public const double ControlRadius = 16;
    public const double ButtonRadius = 14;
    public const double ButtonHeight = 40;

    /// <summary>数值等宽字体：心率 / 电量 / RR 间期等持续跳动的读数用，位数变化时宽度稳定不抖动。</summary>
    public static readonly FontFamily MonoFont = new("Cascadia Mono, Consolas, Segoe UI");

    /// <summary>
    /// 按数值读数样式（20px SemiBold 等宽）实测样例文本宽度，用于给跳动的数值预留固定列宽，
    /// 配合右对齐使用：位数增减时数值向左扩展，单位保持不动。
    /// </summary>
    public static double MeasureValueWidth(string sample)
    {
        var probe = new TextBlock
        {
            Text = sample,
            FontSize = 20,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            FontFamily = MonoFont,
        };
        probe.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        return Math.Ceiling(probe.DesiredSize.Width);
    }

    private static readonly (string Key, Color Light, Color Dark)[] _palette =
    {
        ("MiuixPageBackground", Color.FromArgb(255, 243, 244, 246), Color.FromArgb(255, 25, 25, 25)),
        ("MiuixCardBackground", Color.FromArgb(255, 255, 255, 255), Color.FromArgb(255, 36, 36, 39)),
        // 图表长按数值卡等浮层专用：比卡片背景深 / 亮一档，叠在卡片上能明显区分
        ("MiuixPopupBackground", Color.FromArgb(255, 240, 242, 246), Color.FromArgb(255, 54, 54, 60)),
        ("MiuixCardBorder",     Color.FromArgb(255, 235, 237, 241), Color.FromArgb(255, 52, 52, 56)),
        ("MiuixAccent",         Color.FromArgb(255, 52, 130, 255),  Color.FromArgb(255, 75, 139, 255)),
        ("MiuixAccentForeground", Color.FromArgb(255, 255, 255, 255), Color.FromArgb(255, 255, 255, 255)),
        ("MiuixTextPrimary",    Color.FromArgb(255, 26, 26, 26),    Color.FromArgb(255, 242, 242, 244)),
        ("MiuixTextSecondary",  Color.FromArgb(255, 140, 140, 147), Color.FromArgb(255, 154, 154, 162)),
        ("MiuixSubtle",         Color.FromArgb(255, 241, 242, 245), Color.FromArgb(255, 46, 46, 50)),
        ("MiuixDivider",        Color.FromArgb(255, 237, 238, 240), Color.FromArgb(255, 44, 44, 48)),
        ("MiuixDanger",         Color.FromArgb(255, 250, 81, 81),   Color.FromArgb(255, 255, 107, 107)),
        ("MiuixSuccess",        Color.FromArgb(255, 48, 200, 90),   Color.FromArgb(255, 61, 217, 104)),
    };

    private static readonly Dictionary<string, SolidColorBrush> Brushes = new();

    /// <summary>把 miuix 画笔注册到目标资源字典（通常是 Application.Resources）。</summary>
    public static void Register(ResourceDictionary target)
    {
        foreach (var (key, light, dark) in _palette)
        {
            var brush = new SolidColorBrush(light);
            Brushes[key] = brush;
            target[key] = brush;
        }
    }

    /// <summary>根据当前主题刷新画笔颜色（切换深浅色时调用）。</summary>
    public static void ApplyTheme(ElementTheme theme)
    {
        var dark = theme == ElementTheme.Dark;
        foreach (var (key, light, darkColor) in _palette)
        {
            if (Brushes.TryGetValue(key, out var brush))
                brush.Color = dark ? darkColor : light;
        }
    }

    /// <summary>
    /// 取共享画笔。key 未命中时回退到 MiuixTextPrimary 的共享实例，而不是新建一支
    /// 游离画笔——游离实例不会进入 Brushes 字典，Miuix.ApplyTheme 永远刷不到它，
    /// 表现为该处颜色在切换主题后"卡"在旧值。回退同时写日志，便于暴露 key 拼写错误。
    /// </summary>
    public static Brush Brush(string key)
    {
        if (Brushes.TryGetValue(key, out var brush))
            return brush;
        App.DebugLog($"[Miuix] unknown brush key: {key}");
        return Brushes["MiuixTextPrimary"];
    }

    // ---------------- 控件工厂 ----------------

    public static Border Card(UIElement? child = null, double padding = 18)
    {
        var border = new Border
        {
            Background = Brush("MiuixCardBackground"),
            BorderBrush = Brush("MiuixCardBorder"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(CardRadius),
            Padding = new Thickness(padding),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        if (child is not null) border.Child = child;
        return border;
    }

    public static TextBlock Title(string text) => new()
    {
        Text = text,
        FontSize = 26,
        FontWeight = Microsoft.UI.Text.FontWeights.Bold,
        Foreground = Brush("MiuixTextPrimary")
    };

    public static TextBlock Caption(string text) => new()
    {
        Text = text,
        FontSize = 12,
        Foreground = Brush("MiuixTextSecondary"),
        TextWrapping = TextWrapping.Wrap
    };

    public static TextBlock Body(string text, bool secondary = false) => new()
    {
        Text = text,
        FontSize = 14,
        Foreground = Brush(secondary ? "MiuixTextSecondary" : "MiuixTextPrimary"),
        TextWrapping = TextWrapping.Wrap
    };

    private static ControlTemplate? _buttonTemplate;

    /// <summary>
    /// 自定义按钮模板：任何交互状态都保持用户设置的背景/前景色（默认模板会在
    /// PointerOver/Pressed 时替换成系统色，导致文字"消失"），仅以透明度反馈悬停/按下。
    /// </summary>
    private static ControlTemplate ButtonTemplate =>
        _buttonTemplate ??= (ControlTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                             TargetType="Button">
                <Grid>
                    <Border x:Name="Root"
                            Background="{TemplateBinding Background}"
                            BorderBrush="{TemplateBinding BorderBrush}"
                            BorderThickness="{TemplateBinding BorderThickness}"
                            CornerRadius="{TemplateBinding CornerRadius}"
                            Padding="{TemplateBinding Padding}">
                        <ContentPresenter HorizontalAlignment="Center"
                                          VerticalAlignment="Center"
                                          Foreground="{TemplateBinding Foreground}" />
                    </Border>
                    <VisualStateManager.VisualStateGroups>
                        <VisualStateGroup x:Name="CommonStates">
                            <VisualState x:Name="Normal" />
                            <VisualState x:Name="PointerOver">
                                <Storyboard>
                                    <DoubleAnimation Storyboard.TargetName="Root" Storyboard.TargetProperty="Opacity" To="0.85" Duration="0:0:0.08" />
                                </Storyboard>
                            </VisualState>
                            <VisualState x:Name="Pressed">
                                <Storyboard>
                                    <DoubleAnimation Storyboard.TargetName="Root" Storyboard.TargetProperty="Opacity" To="0.7" Duration="0:0:0.04" />
                                </Storyboard>
                            </VisualState>
                            <VisualState x:Name="Disabled">
                                <Storyboard>
                                    <DoubleAnimation Storyboard.TargetName="Root" Storyboard.TargetProperty="Opacity" To="0.35" Duration="0" />
                                </Storyboard>
                            </VisualState>
                        </VisualStateGroup>
                    </VisualStateManager.VisualStateGroups>
                </Grid>
            </ControlTemplate>
            """);

    private static Button CreateButton(string text, Brush background)
    {
        var button = new Button
        {
            Content = text,
            Height = ButtonHeight,
            CornerRadius = new CornerRadius(ButtonRadius),
            Padding = new Thickness(18, 0, 18, 0),
            Background = background,
            Foreground = Brush("MiuixAccentForeground"),
            FontSize = 14,
            BorderThickness = new Thickness(0),
            Template = ButtonTemplate,
        };
        return button;
    }

    public static Button PrimaryButton(string text) => CreateButton(text, Brush("MiuixAccent"));

    public static Button DangerButton(string text) => CreateButton(text, Brush("MiuixDanger"));

    public static Button GhostButton(string text)
    {
        var button = CreateButton(text, Brush("MiuixSubtle"));
        button.Foreground = Brush("MiuixTextPrimary");
        return button;
    }

    /// <summary>对话框内的幽灵按钮：ContentDialog 底色与 MiuixSubtle 几乎相同，换用页面底色拉开层次。</summary>
    public static Button DialogGhostButton(string text)
    {
        var button = GhostButton(text);
        button.Background = Brush("MiuixPageBackground");
        return button;
    }

    /// <summary>圆形色点按钮（曲线颜色入口）。</summary>
    public static Button DotButton(Brush background, RoutedEventHandler onClick)
    {
        var button = new Button
        {
            Width = 30,
            Height = 30,
            CornerRadius = new CornerRadius(15),
            Padding = new Thickness(0),
            Background = background,
            BorderThickness = new Thickness(0),
            Template = ButtonTemplate,
        };
        button.Click += onClick;
        return button;
    }

    public static StackPanel Vertical(double spacing, params UIElement[] children)
    {
        var panel = new StackPanel { Spacing = spacing };
        foreach (var child in children) panel.Children.Add(child);
        return panel;
    }

    public static StackPanel Horizontal(double spacing, params UIElement[] children)
    {
        var panel = new StackPanel { Spacing = spacing, Orientation = Orientation.Horizontal };
        foreach (var child in children) panel.Children.Add(child);
        return panel;
    }

    /// <summary>曲线颜色选择弹层（Miuix 风格圆点色板）。返回弹窗实例，供调用方在其打开期间暂停相关刷新。</summary>
    public static Flyout ShowColorMenu(FrameworkElement target, HeartRateDevice device)
        => ShowColorMenu(target, device.SetChartColor);

    /// <summary>
    /// 同上，但把选中的颜色交给回调：数据查看等没有设备对象的场景也能复用同一套色板
    /// （颜色由调用方自己保存，例如写回导入的曲线描述）。
    /// </summary>
    public static Flyout ShowColorMenu(FrameworkElement target, Action<Color> onPick)
    {
        var flyout = new Flyout { Placement = FlyoutPlacementMode.Top };
        var root = Vertical(6, Caption(T("pick_color")));
        for (var row = 0; row < 2; row++)
        {
            var line = Horizontal(6);
            for (var i = row * 5; i < row * 5 + 5; i++)
            {
                var color = Palette.NamedColors[i].Color;
                line.Children.Add(DotButton(new SolidColorBrush(color), (_, _) =>
                {
                    onPick(color);
                    flyout.Hide();
                }));
            }
            root.Children.Add(line);
        }

        flyout.Content = root;
        flyout.ShowAt(target);
        return flyout;
    }

    private static string T(string key) => LocalizationService.Instance.T(key);
}
