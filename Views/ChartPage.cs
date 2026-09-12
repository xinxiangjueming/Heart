using Heart.Models;
using Heart.Services;
using Heart.Ui;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI;

namespace Heart.Views;

public sealed class ChartPage : PageBase
{
    private TextBlock _title = null!;
    private TextBlock _zoneCaption = null!;
    private Button _editZonesButton = null!;
    private Button _editYAxisButton = null!;
    private Button _offsetButton = null!;
    private Button _maeButton = null!;
    private int _offsetCurveIndex; // 曲线偏移弹窗里上次选择的曲线序号
    private int _maeTestIndex;             // 误差计算弹窗里上次选择的被测设备序号
    private int _maeRefIndex = 1;          // 误差计算弹窗里上次选择的对标设备序号（默认第二条）
    private TextBlock _timeWindowCaption = null!;
    private TextBlock _maxHrCaption = null!;
    private TextBlock _recordElapsed = null!;
    private Button _recordButton = null!;
    private Button _exportButton = null!;
    private Button _exitViewButton = null!;
    private ComboBox _windowCombo = null!;
    private NumberBox _maxHrBox = null!;
    private ToggleSwitch _zonesToggle = null!;
    private Canvas _plot = null!;
    private StackPanel _legendPanel = null!;
    private StackPanel _emptyPanel = null!;
    private TextBlock _emptyTitle = null!;
    private TextBlock _emptySub = null!;
    private Border _chartCard = null!;
    private Grid _rootLayout = null!;
    private ScrollViewer _legendScroll = null!;
    private Border _scrubCard = null!;
    private Rectangle _scrubLine = null!;
    private string _lastLayoutSig = "";
    private readonly Microsoft.UI.Xaml.DispatcherTimer _pressTimer;
    private bool _pressPending; // 已按下、等待长按判定
    private bool _scrubActive;  // 长按已触发，正在显示数值卡
    private double _scrubX, _scrubY;
    // 最近一次重绘的曲线数据与时间窗，供长按取值复用
    private List<ChartSeries> _lastSeries = new();
    private double _lastFrom, _lastSpan;
    // 非全程窗口底部的回看滑块：拖动可查看窗口之前的历史记录（受样本缓冲 ~10 分钟限制）
    private Grid _sliderBar = null!;        // 交互区（透明背景，负责命中与指针捕获）
    private Border _sliderTrack = null!;    // 背景横线轨道
    private Border _sliderThumb = null!;    // 圆角滑块
    private bool _sliderDrag;
    private double _sliderGrab;             // 按下点相对滑块左缘的偏移
    private double _lastThumbLeft, _lastThumbWidth; // 最近一次布局的滑块位置，供命中测试
    private double? _viewFrom;              // 回看时固定的窗口起点（unix 秒）；null = 跟随最新
    // 数据查看模式：拖入导出的 CSV / Excel 后非 null，图表只绘制文件里的历史数据
    private ImportedData? _imported;

    // 图例点击隐藏：被隐藏曲线的键（实时 = 设备蓝牙地址，数据查看 = "imp:<列序号>"）。
    // 只作用于绘制，样本 / 导入文件里的原始数据一概不动
    private readonly HashSet<string> _hiddenKeys = new();
    // 图例芯片的就地更新缓存：实时图例每秒要刷新读数，原先"每秒清空重建"会销毁正在按下的
    // 元素、把点击连同元素一起吞掉，改为设备集合变化时才重建（见 SyncLegend）
    private readonly Dictionary<string, LegendChip> _legendChips = new();
    private string _legendSignature = "";
    /// <summary>导入图例的曲线集合签名：只在变化时重建芯片，改色等就地刷新。</summary>
    private string _importedLegendSignature = "";
    private const string ImportedKeyPrefix = "imp:";
    /// <summary>
    /// 图表左侧给 Y 轴数值预留的宽度：数值画在绘图区之外，曲线与填充不会压到文字上。
    /// 标签按右对齐靠在这条带上，贴住绘图区左边缘。
    /// </summary>
    private const double YAxisWidth = 54;
    /// <summary>Y 轴数值相对绘图区左缘的留白，太小会贴住第一条刻度线。</summary>
    private const double YAxisGap = 6;
    /// <summary>
    /// Y 轴数值带上方那层淡化底板的不透明度：不挡刻度线，但让偶尔越界的曲线明显变淡，
    /// 数值始终可读（浅色卡片上近乎白色、深色卡片上近乎卡片色，都不显痕迹）。
    /// </summary>
    private const double AxisFadeOpacity = 0.82;
    // 隐藏态芯片的不透明度：一眼可辨，同时保留读数可读性
    private const double HiddenChipOpacity = 0.45;

    private double _windowSeconds = 60;
    private double _frozenNow; // 停止记录时冻结的时间原点，回看最后一帧时窗口不再随墙钟前移
    private bool _showZones = true;
    private bool _suppress;
    private Flyout? _colorFlyout;
    private double? _yMinCustom;
    private double? _yMaxCustom;
    private readonly Microsoft.UI.Xaml.DispatcherTimer _timer;

    public ChartPage()
    {
        _windowSeconds = LoadSavedWindowSeconds();
        _showZones = SettingsStore.GetSetting("show_zones", "1") != "0";
        (_yMinCustom, _yMaxCustom) = LoadSavedYAxis();
        BuildUi();
        ApplyLocalization();

        _timer = new Microsoft.UI.Xaml.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) =>
        {
            if (_imported is not null)
                return; // 数据查看模式内容是静态的，无需每秒重绘
            LogLayout("tick-before");
            Refresh();
            LogLayout("tick-after");
        };

        _pressTimer = new Microsoft.UI.Xaml.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _pressTimer.Tick += (_, _) =>
        {
            _pressTimer.Stop(); // 单次触发：判定完立即停，等下次按下时重启
            if (!_pressPending)
                return;
            _pressPending = false; // 长按判定成功
            _scrubActive = true;
            UpdateScrub();
        };

        // 页面被主窗口缓存复用，Loaded / Unloaded 会在每次导航时反复触发，订阅必须成对
        Loaded += (_, _) =>
        {
            App.DebugLog($"chart Loaded devices={App.Ble.Devices.Count} " +
                $"hasData={App.Ble.Devices.Count(d => d.HasData)}");
            LocalizationService.Instance.LanguageChanged += ApplyLocalization;
            RecordingService.Instance.RecordingChanged += OnRecordingChanged;
            Refresh();
            LogLayout("loaded-after-refresh");
            _timer.Start();
        };
        Unloaded += (_, _) =>
        {
            LogLayout("unloaded");
            LocalizationService.Instance.LanguageChanged -= ApplyLocalization;
            RecordingService.Instance.RecordingChanged -= OnRecordingChanged;
            _timer.Stop();
        };
    }

    // 临时诊断：输出三行布局与图表卡片的实际尺寸，定位进入页面后图表区变高的原因。
    // 每秒 tick 布局无变化时不重复记录，只有尺寸真的变了才落一条日志
    private void LogLayout(string reason)
    {
        var sig = $"{_rootLayout.RowDefinitions[0].ActualHeight:F1}/" +
                  $"{_rootLayout.RowDefinitions[1].ActualHeight:F1}/" +
                  $"{_rootLayout.RowDefinitions[2].ActualHeight:F1}/" +
                  $"{_chartCard.ActualHeight:F1}/{_plot.ActualHeight:F1}";
        var changed = sig != _lastLayoutSig;
        _lastLayoutSig = sig;
        if (!changed && reason.StartsWith("tick", StringComparison.Ordinal))
            return;

        App.DebugLog(
            $"chart[{reason}] rows(header/legend/chart)=(" +
            $"{_rootLayout.RowDefinitions[0].ActualHeight:F1}," +
            $"{_rootLayout.RowDefinitions[1].ActualHeight:F1}," +
            $"{_rootLayout.RowDefinitions[2].ActualHeight:F1}) " +
            $"card={_chartCard.ActualHeight:F1}(want {_chartCard.DesiredSize.Height:F1}) " +
            $"plot={_plot.ActualHeight:F1} emptyPanel={_emptyPanel.Visibility} " +
            $"plotChildren={_plot.Children.Count} page={ActualHeight:F1}");
    }

    /// <summary>主题切换时重绘图表（由 MainWindow 回调，UI 线程）。</summary>
    public override void ApplyTheme()
    {
        // 轴标签画笔由 AxisLabelBrush() 按当前主题现取现用，重绘即换成新色；
        // 图例芯片的圆点描边等静态画笔同样要在切主题后重新取一次，
        // 否则会停留在旧主题的颜色上（看曲线时切主题最容易发现）
        if (_imported is not null)
            SyncImportedLegend(_imported);
        Redraw();
    }

    private static double LoadSavedWindowSeconds()
    {
        var raw = SettingsStore.GetSetting("time_window", "60");
        // 0 = 全程（整个记录期间）
        return int.TryParse(raw, out var seconds) && seconds is 0 or 30 or 60 or 180 or 300 or 600
            ? seconds
            : 60;
    }

    // 自定义 Y 轴范围存成 "min,max"；未设置或格式无效时返回空，走自动计算
    private static (double? Lo, double? Hi) LoadSavedYAxis()
    {
        var raw = SettingsStore.GetSetting("y_axis_range", "");
        var parts = raw.Split(',');
        if (parts.Length == 2 &&
            double.TryParse(parts[0], System.Globalization.CultureInfo.InvariantCulture, out var lo) &&
            double.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var hi) &&
            hi > lo && lo >= 0 && hi <= 300)
            return (lo, hi);
        return (null, null);
    }

    private void BuildUi()
    {
        // 数据查看模式下标题会追加文件名，过长时省略显示
        _title = Miuix.Title("");
        _title.TextTrimming = TextTrimming.CharacterEllipsis;
        _zoneCaption = Miuix.Caption("");
        _editZonesButton = Miuix.GhostButton("");
        _editZonesButton.MinHeight = 28;
        _editZonesButton.Padding = new Thickness(10, 0, 10, 0);
        _editZonesButton.Click += OnEditZonesClick;
        _editYAxisButton = Miuix.GhostButton("");
        _editYAxisButton.MinHeight = 28;
        _editYAxisButton.Padding = new Thickness(10, 0, 10, 0);
        _editYAxisButton.Click += OnEditYAxisClick;
        // 曲线偏移：只在数据查看、且文件里有 2 条及以上曲线时出现（单条曲线无从对齐）
        // 用主色按钮（与「扫描设备」同款：蓝底白字），与同行的幽灵按钮区分开
        _offsetButton = Miuix.PrimaryButton("");
        _offsetButton.MinHeight = 28;
        _offsetButton.Padding = new Thickness(10, 0, 10, 0);
        _offsetButton.Click += OnCurveOffsetClick;
        _offsetButton.Visibility = Visibility.Collapsed;

        _maeButton = Miuix.PrimaryButton("");
        _maeButton.MinHeight = 28;
        _maeButton.Padding = new Thickness(10, 0, 10, 0);
        _maeButton.Click += OnMaeClick;
        _maeButton.Visibility = Visibility.Collapsed;

        var zoneRow = Miuix.Horizontal(10, _zoneCaption, _editZonesButton, _editYAxisButton, _offsetButton, _maeButton);
        _zoneCaption.VerticalAlignment = VerticalAlignment.Center;
        _editZonesButton.VerticalAlignment = VerticalAlignment.Center;
        _editYAxisButton.VerticalAlignment = VerticalAlignment.Center;
        _offsetButton.VerticalAlignment = VerticalAlignment.Center;
        _maeButton.VerticalAlignment = VerticalAlignment.Center;

        _timeWindowCaption = Miuix.Caption("");
        _windowCombo = new ComboBox { MinWidth = 104, SelectedIndex = 1, Height = Miuix.ButtonHeight };
        for (var i = 0; i < 6; i++)
            _windowCombo.Items.Add(new ComboBoxItem());
        _windowCombo.SelectionChanged += OnWindowChanged;

        _maxHrCaption = Miuix.Caption("");
        _maxHrBox = new NumberBox
        {
            Value = HeartRateZones.MaxHeartRate,
            Minimum = 100,
            Maximum = 230,
            SmallChange = 1,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            MinWidth = 124,
            Height = Miuix.ButtonHeight,
        };
        _maxHrBox.ValueChanged += OnMaxHrChanged;

        _zonesToggle = new ToggleSwitch { IsOn = _showZones, VerticalAlignment = VerticalAlignment.Top };
        _zonesToggle.Toggled += OnZonesToggled;

        _recordButton = Miuix.PrimaryButton("");
        _recordButton.Click += OnRecordClick;
        _recordElapsed = Miuix.Caption("");
        _recordElapsed.VerticalAlignment = VerticalAlignment.Center;

        _exportButton = Miuix.GhostButton("");
        _exportButton.Click += OnExportClick;
        _exportButton.IsEnabled = false;
        _exportButton.VerticalAlignment = VerticalAlignment.Top;

        _exitViewButton = Miuix.GhostButton("");
        _exitViewButton.Click += OnExitViewClick;
        _exitViewButton.Visibility = Visibility.Collapsed;
        // 与导出按钮同为工具栏直接子元素：必须同样顶部对齐，否则默认 Stretch 会被
        // 同行"控件+说明文字"的纵向布局撑高，两个按钮视觉高度不一致
        _exitViewButton.VerticalAlignment = VerticalAlignment.Top;

        // 控件统一 40 高、顶部对齐成一排，小字标签统一放控件下方
        var controls = Miuix.Horizontal(18,
            Miuix.Vertical(4, _recordButton, _recordElapsed),
            Miuix.Vertical(4, _windowCombo, _timeWindowCaption),
            Miuix.Vertical(4, _maxHrBox, _maxHrCaption));
        controls.VerticalAlignment = VerticalAlignment.Center;
        controls.Children.Insert(1, _exportButton);
        controls.Children.Insert(2, _exitViewButton);
        controls.Children.Add(_zonesToggle);

        // 标题与工具栏分两行排布，窄窗口下互不挤压
        var titles = Miuix.Vertical(2, _title, zoneRow);
        var header = Miuix.Vertical(10, titles, controls);

        _plot = new Canvas { Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)) };
        _plot.SizeChanged += (_, e) =>
        {
            App.DebugLog($"plot SizeChanged {e.PreviousSize.Width:F1}x{e.PreviousSize.Height:F1} " +
                $"> {e.NewSize.Width:F1}x{e.NewSize.Height:F1}");
            LogLayout("plot-size-changed");
            Redraw();
        };
        _plot.PointerPressed += OnPlotPointerPressed;
        _plot.PointerMoved += OnPlotPointerMoved;
        _plot.PointerReleased += OnPlotPointerReleased;
        _plot.PointerCanceled += OnPlotPointerReleased;
        _plot.PointerCaptureLost += OnPlotPointerReleased;

        // 长按查看数值的覆盖层：时间参考线 + 数值卡。
        // 画布每秒清空重绘，覆盖层必须放在画布之外的同一个 Grid 里
        _scrubLine = new Rectangle
        {
            Width = 1,
            Fill = new SolidColorBrush(Color.FromArgb(150, 128, 128, 128)),
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Visibility = Visibility.Collapsed,
        };
        _scrubCard = new Border
        {
            // 浮层专用底色：与图表卡片区分（浅色更灰、深色更亮），叠在上面不会融入背景
            Background = Miuix.Brush("MiuixPopupBackground"),
            BorderBrush = Miuix.Brush("MiuixCardBorder"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 8, 10, 8),
            Width = 190,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };

        _emptyTitle = new TextBlock
        {
            FontSize = 16,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = Miuix.Brush("MiuixTextPrimary"),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _emptySub = Miuix.Caption("");
        _emptySub.HorizontalAlignment = HorizontalAlignment.Center;
        _emptyPanel = Miuix.Vertical(8,
            new TextBlock
            {
                Text = "\uE9D9",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 40,
                Foreground = Miuix.Brush("MiuixTextSecondary"),
                HorizontalAlignment = HorizontalAlignment.Center,
            },
            _emptyTitle,
            _emptySub);
        _emptyPanel.HorizontalAlignment = HorizontalAlignment.Center;
        _emptyPanel.VerticalAlignment = VerticalAlignment.Center;
        _emptyPanel.IsHitTestVisible = false;
        _emptyPanel.Visibility = Visibility.Collapsed;

        var chartHost = new Grid();
        chartHost.Children.Add(_plot);
        chartHost.Children.Add(_emptyPanel);
        chartHost.Children.Add(_scrubLine);
        chartHost.Children.Add(_scrubCard);

        // 回看滑块：细横线轨道 + 圆角胶囊滑块，仅非全程窗口且历史超出窗口时出现。
        // 命中统一交给外层交互区，轨道与滑块本身不接收指针
        _sliderTrack = new Border
        {
            Height = 4,
            CornerRadius = new CornerRadius(2),
            Background = Miuix.Brush("MiuixCardBorder"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
        _sliderThumb = new Border
        {
            Height = 8,
            MinWidth = 36,
            CornerRadius = new CornerRadius(4),
            Background = Miuix.Brush("MiuixTextSecondary"),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
        _sliderBar = new Grid
        {
            Height = 18,
            Margin = new Thickness(0, 4, 0, 0),
            Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)), // 透明但可命中
            Visibility = Visibility.Collapsed,
        };
        _sliderBar.Children.Add(_sliderTrack);
        _sliderBar.Children.Add(_sliderThumb);
        _sliderBar.PointerPressed += OnSliderPressed;
        _sliderBar.PointerMoved += OnSliderMoved;
        _sliderBar.PointerReleased += OnSliderReleased;
        _sliderBar.PointerCanceled += OnSliderReleased;
        _sliderBar.PointerCaptureLost += OnSliderReleased;
        _sliderBar.SizeChanged += (_, _) => SyncSlider();

        // 曲线区占满第一行，滑块行按需占位：滑块隐藏时不占任何空间。
        // 滑块行左侧留出 Y 轴数值带的宽度（列宽在 SyncSlider 里同步），使滑块与绘图区左右对齐
        var chartInner = new Grid
        {
            RowDefinitions =
            {
                new RowDefinition { Height = new GridLength(1, GridUnitType.Star) },
                new RowDefinition { Height = GridLength.Auto },
            },
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(YAxisWidth, GridUnitType.Pixel) },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
            },
        };
        Grid.SetRow(chartHost, 0);
        Grid.SetColumnSpan(chartHost, 2);
        Grid.SetRow(_sliderBar, 1);
        Grid.SetColumn(_sliderBar, 1);
        chartInner.Children.Add(chartHost);
        chartInner.Children.Add(_sliderBar);

        _chartCard = Miuix.Card(chartInner, 10);
        _chartCard.VerticalAlignment = VerticalAlignment.Stretch;
        _chartCard.HorizontalAlignment = HorizontalAlignment.Stretch;

        _legendPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };

        var legendScroll = new ScrollViewer
        {
            Content = _legendPanel,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Auto,
            VerticalScrollMode = ScrollMode.Disabled,
            // 纵向滚动已禁用，滚动条本无意义；不显式隐藏时默认 Visible 的纵向滚动条
            // 模板会参与测量：首次布局行高虚高 12px，滚动条状态收敛后的再次布局又缩回，
            // 差值全部进了下方 Star 行的图表卡片，表现为进入页面后图表区自动变高一截
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
        };
        // 空图例时整行折叠：无设备时行高恒为 0，首次 / 再次布局结果一致，不再跳动
        legendScroll.Visibility = App.Ble.Devices.Any(d => d.HasData)
            ? Visibility.Visible
            : Visibility.Collapsed;
        _legendScroll = legendScroll;

        var root = new Grid
        {
            RowDefinitions =
            {
                new RowDefinition { Height = GridLength.Auto },  // 标题 + 工具栏
                new RowDefinition { Height = GridLength.Auto },  // 图例芯片
                new RowDefinition { Height = new GridLength(1, GridUnitType.Star) },  // 图表占满剩余空间
            },
            RowSpacing = 12,
        };
        _rootLayout = root;
        Grid.SetRow(header, 0);
        // 图例芯片放在图表上方，作为各设备实时读数条
        Grid.SetRow(legendScroll, 1);
        Grid.SetRow(_chartCard, 2);
        root.Children.Add(header);
        root.Children.Add(legendScroll);
        root.Children.Add(_chartCard);

        // 拖入导出的 CSV / Excel 文件即可查看历史数据（记录中不接受，见 OnPageDragOver）
        root.AllowDrop = true;
        root.DragOver += OnPageDragOver;
        root.Drop += OnPageDrop;

        Content = root;
    }

    // ===== 交互 =====

    private void OnWindowChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;
        _windowSeconds = _windowCombo.SelectedIndex switch
        {
            0 => 30,
            1 => 60,
            2 => 180,
            3 => 300,
            5 => 0, // 全程
            _ => 600,
        };
        SettingsStore.SetSetting("time_window", ((int)_windowSeconds).ToString());
        _viewFrom = null; // 切换时间窗后回到跟随最新
        Refresh();
    }

    private void OnMaxHrChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (double.IsNaN(args.NewValue))
            return;
        HeartRateZones.MaxHeartRate = (int)Math.Clamp(args.NewValue, 100, 230);
        SettingsStore.SetSetting("max_hr", HeartRateZones.MaxHeartRate.ToString());
        Refresh();
    }

    private void OnZonesToggled(object sender, RoutedEventArgs e)
    {
        _showZones = _zonesToggle.IsOn;
        SettingsStore.SetSetting("show_zones", _showZones ? "1" : "0");
        Redraw();
    }

    // ===== 数据 → 视图 =====

    private static double Now() =>
        DateTimeOffset.UtcNow.Subtract(DateTimeOffset.UnixEpoch).TotalSeconds;

    private void Refresh()
    {
        if (_plot is null)
            return;

        // 数据查看模式：数据是静态的，不补点也不重建实时图例，只按当前窗口重绘
        // （仍要重建导入图例，切换语言时平均 / 点数两行才会跟着换）
        if (_imported is not null)
        {
            SyncImportedLegend(_imported);
            Redraw();
            return;
        }

        // 曲线只在记录期间绘制，未记录时不补点
        if (RecordingService.Instance.IsRecording)
        {
            var now = Now();

            // 已连接但上一秒没有新上报的设备，用最近一次心率补点，让曲线连续
            foreach (var device in App.Ble.Devices)
            {
                if (device.IsConnected)
                    device.HoldSample(now);
            }
        }

        SyncLegend();
        RebuildZoneCaption();
        Redraw();
        // 画布上没有任何内容时才显示空状态引导；停止记录后保留的最后一帧不算空
        _emptyPanel.Visibility = _plot.Children.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        // 未连接设备且未在记录时，开始记录按钮置灰；连接任一设备后恢复
        _recordButton.IsEnabled = RecordingService.Instance.IsRecording
            || App.Ble.Devices.Any(d => d.IsConnected);
        UpdateRecordElapsed();
    }

    // ===== 录制 / 导出 =====

    private void OnRecordingChanged() => DispatcherQueue.TryEnqueue(() =>
    {
        UpdateRecordUi();
        Refresh();
    });

    private void OnRecordClick(object sender, RoutedEventArgs e)
    {
        // 未连接任何设备时开始记录没有意义（按钮此时也处于禁用态），点击不产生任何效果
        if (!RecordingService.Instance.IsRecording && !App.Ble.Devices.Any(d => d.IsConnected))
            return;

        if (RecordingService.Instance.IsRecording)
        {
            RecordingService.Instance.Stop();
            _frozenNow = Now(); // 冻结时间窗：停止后最后一帧停在停止时刻
        }
        else
        {
            // 曲线只呈现本次记录：清掉记录开始前的历史样本，并退出数据查看模式
            _imported = null;
            foreach (var device in App.Ble.Devices)
                device.ClearSamples();
            RecordingService.Instance.Start();
            _viewFrom = null; // 新记录从跟随最新开始
        }
        UpdateRecordUi();
        Refresh();
    }

    private void UpdateRecordUi()
    {
        var loc = LocalizationService.Instance;
        var recording = RecordingService.Instance.IsRecording;
        _recordButton.Content = recording ? loc.T("record_stop") : loc.T("record_start");
        _recordButton.Background = recording ? Miuix.Brush("MiuixDanger") : Miuix.Brush("MiuixAccent");
        _exportButton.Content = loc.T("export_csv");
        _exportButton.IsEnabled = RecordingService.Instance.Rows.Count > 0;
        ApplyModeChrome();
        UpdateRecordElapsed();
    }

    private void OnExitViewClick(object sender, RoutedEventArgs e) => ExitImportedView();

    private void UpdateRecordElapsed()
    {
        if (RecordingService.Instance.IsRecording)
        {
            var elapsed = DateTime.Now - RecordingService.Instance.StartedAt;
            _recordElapsed.Text = LocalizationService.Instance.T(
                "recording_elapsed", elapsed.ToString(@"hh\:mm\:ss"));
        }
        else
        {
            _recordElapsed.Text = "";
        }
    }

    private async void OnExportClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var csv = RecordingService.Instance.BuildCsv();
            var picker = new Windows.Storage.Pickers.FileSavePicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);
            picker.SuggestedFileName = "heart_rate_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
            picker.FileTypeChoices.Add("CSV", new List<string> { ".csv" });
            picker.DefaultFileExtension = ".csv";

            var file = await picker.PickSaveFileAsync();
            if (file is null)
                return;

            await Windows.Storage.FileIO.WriteTextAsync(file, csv);
        }
        catch (Exception ex)
        {
            App.ReportCrash(ex);
        }
    }

    // ===== 区间自定义 =====

    private async void OnEditZonesClick(object sender, RoutedEventArgs e)
    {
        var loc = LocalizationService.Instance;
        var maxHr = HeartRateZones.MaxHeartRate;
        var editors = new (NumberBox Lo, NumberBox Hi)[HeartRateZones.ZoneCount];

        var grid = new Grid
        {
            RowSpacing = 10,
            ColumnSpacing = 8,
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(110) },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
            }
        };

        for (var i = 0; i < HeartRateZones.ZoneCount; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var (lo, hi) = HeartRateZones.BoundsOf(i, maxHr);

            var label = new TextBlock
            {
                Text = $"Z{i + 1} {HeartRateZones.NameOf(i)}",
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Miuix.Brush("MiuixTextPrimary"),
            };
            Grid.SetRow(label, i);
            Grid.SetColumn(label, 0);

            var loBox = new NumberBox
            {
                Value = Math.Round(lo, 0),
                MinWidth = 80,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            };
            Grid.SetRow(loBox, i);
            Grid.SetColumn(loBox, 1);

            var dash = new TextBlock
            {
                Text = "\u2013",
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Miuix.Brush("MiuixTextSecondary"),
            };
            Grid.SetRow(dash, i);
            Grid.SetColumn(dash, 2);

            var hiBox = new NumberBox
            {
                Value = Math.Round(hi, 0),
                MinWidth = 80,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            };
            Grid.SetRow(hiBox, i);
            Grid.SetColumn(hiBox, 3);

            grid.Children.Add(label);
            grid.Children.Add(loBox);
            grid.Children.Add(dash);
            grid.Children.Add(hiBox);
            editors[i] = (loBox, hiBox);
        }

        var content = Miuix.Vertical(12, grid);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = loc.T("edit_zones"),
            Content = content,
            PrimaryButtonText = "OK",
            CloseButtonText = loc.T("cancel"),
        };

        var resetButton = Miuix.DialogGhostButton(loc.T("zones_reset"));
        resetButton.Click += (_, _) =>
        {
            HeartRateZones.ResetToDefaults();
            dialog.Hide();
            Refresh();
        };
        content.Children.Add(resetButton);

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
            return;

        var flat = new double[HeartRateZones.ZoneCount * 2];
        for (var i = 0; i < HeartRateZones.ZoneCount; i++)
        {
            var lo = Math.Clamp(editors[i].Lo.Value, 0, 300);
            var hi = Math.Clamp(editors[i].Hi.Value, 0, 300);
            if (lo > hi)
                (lo, hi) = (hi, lo);
            flat[i * 2] = lo;
            flat[i * 2 + 1] = hi;
        }
        HeartRateZones.SetCustomBounds(flat);
        Refresh();
    }

    // ===== Y 轴自定义 =====

    private async void OnEditYAxisClick(object sender, RoutedEventArgs e)
    {
        var loc = LocalizationService.Instance;
        // 以当前画面上的曲线为准（实时 / 数据查看两种模式都适用，且与重绘用的数据同源）
        var (autoMin, autoMax) = AutoYRange(_lastSeries);

        var minBox = new NumberBox
        {
            Value = Math.Round(_yMinCustom ?? autoMin),
            Minimum = 0,
            Maximum = 280,
            SmallChange = 5,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            MinWidth = 100,
        };
        var maxBox = new NumberBox
        {
            Value = Math.Round(_yMaxCustom ?? autoMax),
            Minimum = 20,
            Maximum = 300,
            SmallChange = 5,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            MinWidth = 100,
        };

        TextBlock Caption(string text) => new()
        {
            Text = text,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Miuix.Brush("MiuixTextPrimary"),
        };

        var content = Miuix.Vertical(12,
            Miuix.Horizontal(10, Caption(loc.T("y_min")), minBox),
            Miuix.Horizontal(10, Caption(loc.T("y_max")), maxBox));

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = loc.T("edit_y_axis"),
            Content = content,
            PrimaryButtonText = "OK",
            CloseButtonText = loc.T("cancel"),
        };

        var autoButton = Miuix.DialogGhostButton(loc.T("y_axis_auto"));
        autoButton.Click += (_, _) =>
        {
            // 清掉自定义范围即可回到自动：自动范围 = 数据最小值 ×0.95 ~ 最大值 ×1.05（见 AutoYRange）
            _yMinCustom = _yMaxCustom = null;
            SettingsStore.SetSetting("y_axis_range", "");
            dialog.Hide();
            Refresh();
        };
        content.Children.Add(autoButton);

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
            return;

        // NumberBox 输入非法时 Value 会变成 NaN，此时放弃本次修改
        if (double.IsNaN(minBox.Value) || double.IsNaN(maxBox.Value))
            return;

        var lo = Math.Clamp(minBox.Value, 0, 300);
        var hi = Math.Clamp(maxBox.Value, 0, 300);
        if (lo > hi)
            (lo, hi) = (hi, lo);
        // 量程过窄曲线没有分辨率，强制至少 20 bpm 跨度
        if (hi - lo < 20)
            hi = Math.Min(300, lo + 20);

        _yMinCustom = lo;
        _yMaxCustom = hi;
        SettingsStore.SetSetting("y_axis_range", $"{(int)Math.Round(lo)},{(int)Math.Round(hi)}");
        Refresh();
    }

    // ===== 曲线偏移（仅数据查看模式） =====

    // 两台设备的时间戳常常差几秒，把其中一条整体左移即可在画面上对齐。
    // 偏移只改绘制用的时间，不动原始数据，退出查看或重新导入即恢复
    private async void OnCurveOffsetClick(object sender, RoutedEventArgs e)
    {
        if (_imported is not { } data || data.Series.Count < 2)
            return;

        var loc = LocalizationService.Instance;
        var combo = new ComboBox { MinWidth = 180, Height = Miuix.ButtonHeight };
        foreach (var s in data.Series)
        {
            // 有偏移的曲线把数值直接标在名称后面（"-3s" = 已左移 3 秒），一眼能看出当前状态
            var text = s.ShiftSeconds == 0
                ? s.Name
                : $"{s.Name} · -{s.ShiftSeconds:0}s";
            combo.Items.Add(new ComboBoxItem { Content = text });
        }
        // 记住上次选中的曲线，重开弹窗时回到它并显示它当前的偏移
        var index = Math.Clamp(_offsetCurveIndex, 0, data.Series.Count - 1);
        combo.SelectedIndex = index;

        var secondsBox = new NumberBox
        {
            Value = data.Series[index].ShiftSeconds,
            Minimum = 0,
            Maximum = 3600,
            SmallChange = 1,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            MinWidth = 140,
            Height = Miuix.ButtonHeight,
        };
        // 切换曲线时回显该曲线当前的偏移，便于逐条微调
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedIndex >= 0)
                secondsBox.Value = data.Series[combo.SelectedIndex].ShiftSeconds;
        };

        TextBlock Caption(string text) => new()
        {
            Text = text,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Miuix.Brush("MiuixTextPrimary"),
        };

        var content = Miuix.Vertical(12,
            Miuix.Horizontal(10, Caption(loc.T("offset_curve")), combo),
            Miuix.Horizontal(10, Caption(loc.T("offset_seconds")), secondsBox));

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = loc.T("curve_offset"),
            Content = content,
            PrimaryButtonText = "OK",
            CloseButtonText = loc.T("cancel"),
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        // NumberBox 输入非法时 Value 为 NaN，放弃本次修改
        if (combo.SelectedIndex < 0 || double.IsNaN(secondsBox.Value))
            return;

        _offsetCurveIndex = combo.SelectedIndex;
        data.Series[combo.SelectedIndex].ShiftSeconds = Math.Clamp(secondsBox.Value, 0, 3600);
        Refresh(); // 导入模式下只重绘，图例的平均值 / 点数不受偏移影响
    }

    /// <summary>
    /// 平均绝对误差（MAE）计算：选「被测设备」与「对标设备」两条曲线，
    /// 按整秒对齐（各自叠加曲线偏移 ShiftSeconds），对标设备该秒无数值的点直接舍弃。
    /// </summary>
    private async void OnMaeClick(object sender, RoutedEventArgs e)
    {
        if (_imported is not { } data || data.Series.Count < 2)
            return;

        var loc = LocalizationService.Instance;

        var testCombo = new ComboBox { MinWidth = 180, Height = Miuix.ButtonHeight };
        var refCombo = new ComboBox { MinWidth = 180, Height = Miuix.ButtonHeight };
        for (var i = 0; i < data.Series.Count; i++)
        {
            testCombo.Items.Add(new ComboBoxItem { Content = data.Series[i].Name });
            refCombo.Items.Add(new ComboBoxItem { Content = data.Series[i].Name });
        }
        // 默认被测=第一条、对标=第二条；之后沿用上次的选择（同曲线偏移弹窗的 _offsetCurveIndex 做法），
        // 曲线数变少时钳制到有效范围
        testCombo.SelectedIndex = Math.Clamp(_maeTestIndex, 0, data.Series.Count - 1);
        refCombo.SelectedIndex = Math.Clamp(_maeRefIndex, 0, data.Series.Count - 1);

        var resultText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = Miuix.Brush("MiuixTextPrimary"),
            Margin = new Thickness(0, 6, 0, 0),
        };

        var calcButton = Miuix.PrimaryButton(loc.T("calculate"));
        calcButton.MinHeight = 32;
        calcButton.Click += (_, _) =>
        {
            if (testCombo.SelectedIndex < 0 || refCombo.SelectedIndex < 0)
                return;
            var test = data.Series[testCombo.SelectedIndex];
            var reference = data.Series[refCombo.SelectedIndex];
            var (mae, used, dropped, maxAbs) = ComputeMae(test, reference);
            if (mae is null)
            {
                resultText.Text = loc.T("mae_no_data");
                return;
            }
            var f = System.Globalization.CultureInfo.InvariantCulture;
            resultText.Text =
                loc.T("mae_mae", mae.Value.ToString("F1", f)) + "\n" +
                loc.T("mae_used", used) + "    " + loc.T("mae_dropped", dropped) + "\n" +
                loc.T("mae_max", maxAbs.ToString("F1", f));
        };

        TextBlock Caption(string text) => new()
        {
            Text = text,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Miuix.Brush("MiuixTextPrimary"),
        };

        var content = Miuix.Vertical(12,
            Miuix.Horizontal(10, Caption(loc.T("mae_test")), testCombo),
            Miuix.Horizontal(10, Caption(loc.T("mae_reference")), refCombo),
            Miuix.Horizontal(10, calcButton),
            resultText);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = loc.T("mae"),
            Content = content,
            CloseButtonText = loc.T("done"),
        };
        // 关闭时记住所选的被测 / 对标设备，下次进入直接沿用
        dialog.Closing += (_, _) =>
        {
            _maeTestIndex = testCombo.SelectedIndex;
            _maeRefIndex = refCombo.SelectedIndex;
        };
        // 只调整「完成」按钮的位置，按钮本身的样式 / 尺寸一概不动
        dialog.Opened += (_, _) => CenterDialogCommandButton(dialog);
        await dialog.ShowAsync();
    }

    /// <summary>
    /// 计算两条曲线的 MAE：以「秒」为对齐粒度，每条曲线先减去各自偏移 ShiftSeconds
    /// （与画面绘制一致），再按整秒取整比对。对标设备某秒无样本则该测试点舍弃。
    /// 返回：平均绝对误差（无重叠时为 null）、有效点数、舍弃点数、最大绝对误差。
    /// </summary>
    private static (double? Mae, int Used, int Dropped, double MaxAbs) ComputeMae(
        ChartSeries test, ChartSeries reference)
    {
        // 对标设备：秒 → 该秒所有样本心率均值（同一秒多次上报时取平均）
        var refBySec = new Dictionary<int, (double Sum, int N)>();
        foreach (var p in reference.Points)
        {
            var sec = (int)Math.Round(p.T - reference.ShiftSeconds);
            if (refBySec.TryGetValue(sec, out var v))
                refBySec[sec] = (v.Sum + p.Hr, v.N + 1);
            else
                refBySec[sec] = (p.Hr, 1);
        }

        double sum = 0, maxAbs = 0;
        var used = 0;
        var dropped = 0;
        foreach (var p in test.Points)
        {
            var sec = (int)Math.Round(p.T - test.ShiftSeconds);
            if (refBySec.TryGetValue(sec, out var rv))
            {
                var refHr = rv.Sum / rv.N;
                var abs = Math.Abs(p.Hr - refHr);
                sum += abs;
                if (abs > maxAbs) maxAbs = abs;
                used++;
            }
            else
            {
                dropped++;
            }
        }

        if (used == 0)
            return (null, 0, dropped, 0);
        return (sum / used, used, dropped, maxAbs);
    }

    // 曲线隐藏键：实时模式用蓝牙地址（同一设备跨记录 / 跨文件都指向同一条曲线）
    private static string KeyOf(HeartRateDevice device) => device.Address.ToString();

    private void SyncLegend()
    {
        // 弹窗锚定在图例圆点上，重建图例会把它连带关掉，打开期间跳过本轮重建
        if (_colorFlyout is { IsOpen: true })
            return;

        var devices = App.Ble.Devices.Where(d => d.HasData).ToList();

        // 只在设备集合（含顺序）变化时重建芯片：读数每秒都在变，逐秒重建会销毁正在按下的
        // 元素，点击随之丢失；其余时间就地改文本 / 颜色，元素不动，点击才会被稳定接住
        var signature = string.Join('|', devices.Select(d => d.Address));
        if (signature != _legendSignature)
        {
            _legendSignature = signature;
            _legendChips.Clear();
            _legendPanel.Children.Clear();
            foreach (var device in devices)
                _legendPanel.Children.Add(BuildLegendChip(device));
        }
        foreach (var device in devices)
            ApplyLegendChip(device);

        // 图例行是否占位跟随芯片数量，空行高度始终为 0
        _legendScroll.Visibility = _legendPanel.Children.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    /// <summary>构建一个实时图例芯片，并登记其可更新部件（读数、颜色等后续就地刷新）。</summary>
    private Border BuildLegendChip(HeartRateDevice device)
    {
        var key = KeyOf(device);
        var dot = (Button)null!;
        dot = Miuix.DotButton(device.DotBrush, (_, _) => _colorFlyout = Miuix.ShowColorMenu(dot, device));
        dot.Width = 34;
        dot.Height = 34;
        dot.CornerRadius = new CornerRadius(17);
        dot.BorderThickness = new Thickness(2);
        dot.BorderBrush = Miuix.Brush("MiuixCardBorder");

        // 区间色块常驻（无有效区间时折叠）：芯片内部结构保持稳定，就地更新不换元素
        var zoneText = new TextBlock
        {
            Text = "",
            FontSize = 11,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        };
        var zoneChip = new Border
        {
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(6, 2, 6, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
            Child = zoneText,
        };

        // 数值与单位同大小同行渲染，与设备页卡片保持一致；等宽 + 固定列宽 + 右对齐，
        // 位数增减（98→102）时读数向左扩展，"bpm" 和右侧的区间色块都不移动
        var value = new TextBlock
        {
            FontSize = 20,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            FontFamily = Miuix.MonoFont,
            MinWidth = Miuix.MeasureValueWidth("888 bpm"),
            TextAlignment = TextAlignment.Right,
            Foreground = Miuix.Brush("MiuixTextPrimary"),
        };
        var valueRow = Miuix.Horizontal(6, value, zoneChip);

        var name = new TextBlock
        {
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            MaxWidth = 120,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = Miuix.Brush("MiuixTextPrimary"),
        };

        var chip = new Border
        {
            CornerRadius = new CornerRadius(14),
            Background = Miuix.Brush("MiuixSubtle"),
            Padding = new Thickness(10, 7, 10, 7),
            // 显式顶对齐：即使外层容器给出多余高度，芯片也不会被纵向拉伸
            VerticalAlignment = VerticalAlignment.Top,
            Child = Miuix.Horizontal(8, dot, Miuix.Vertical(0, name, valueRow)),
        };
        AttachChipToggle(chip, dot, key);

        _legendChips[key] = new LegendChip(chip, dot, name, value, zoneChip, zoneText);
        return chip;
    }

    /// <summary>把设备当前状态刷进已有芯片（每秒调用，只改内容不换元素）。</summary>
    private void ApplyLegendChip(HeartRateDevice device)
    {
        if (!_legendChips.TryGetValue(KeyOf(device), out var chip))
            return;

        chip.Chip.Opacity = _hiddenKeys.Contains(KeyOf(device)) ? HiddenChipOpacity : 1;
        chip.Dot.Background = device.DotBrush;
        chip.Name.Text = device.DisplayName;
        chip.Value.Text = $"{device.HeartRateText} {LocalizationService.Instance.T("bpm")}";

        // 区间色块：颜色与文字都只由区间序号决定，序号没变就不重建画笔，避免每秒空转
        var zoneIndex = HeartRateZones.IndexOf(device.HeartRate, HeartRateZones.MaxHeartRate);
        var zoneLabel = zoneIndex < 0 ? "" : $"Z{zoneIndex + 1} {HeartRateZones.NameOf(zoneIndex)}";
        if (chip.ZoneText.Text == zoneLabel)
            return;

        chip.ZoneText.Text = zoneLabel;
        if (zoneIndex < 0)
        {
            chip.Zone.Visibility = Visibility.Collapsed;
            return;
        }
        var zoneColor = HeartRateZones.ColorOf(zoneIndex);
        chip.Zone.Background = new SolidColorBrush(Color.FromArgb(36, zoneColor.R, zoneColor.G, zoneColor.B));
        chip.ZoneText.Foreground = new SolidColorBrush(zoneColor);
        chip.Zone.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// 给图例芯片装上「点击切换隐藏」与悬停反馈。圆点是改色入口，落在它上面的点击让给弹层，
    /// 芯片其余区域（名称、读数、留白）点击即隐藏 / 显示该曲线。
    /// 用 Tapped 而非重建：芯片元素常驻，按下到抬起之间不会被替换掉。
    /// </summary>
    private void AttachChipToggle(Border chip, Button dot, string key)
    {
        chip.Tapped += (_, e) =>
        {
            if (IsWithin(e.OriginalSource, dot))
                return;
            ToggleCurve(key);
        };
        chip.PointerEntered += (_, _) =>
        {
            // 最后一条可见曲线不允许隐藏，也就不给「可点击」的悬停反馈
            if (!_hiddenKeys.Contains(key) && CanHideMore())
                chip.Opacity = 0.85;
        };
        chip.PointerExited += (_, _) =>
            chip.Opacity = _hiddenKeys.Contains(key) ? HiddenChipOpacity : 1;
    }

    /// <summary>
    /// 画面上还有多条曲线时才允许再隐藏一条。全部隐藏会让整屏空白、无从点回来，
    /// 因此至少锁住最后一条（判断依据是上次重绘实际画出的曲线数 _lastSeries）。
    /// </summary>
    private bool CanHideMore() => _lastSeries.Count > 1;

    // 点击图例：切换该曲线的显示 / 隐藏。隐藏只影响绘制与量程统计，原始数据不动
    private void ToggleCurve(string key)
    {
        if (_hiddenKeys.Contains(key))
        {
            _hiddenKeys.Remove(key);
        }
        else
        {
            if (!CanHideMore())
            {
                App.DebugLog($"chart legend hide blocked key={key} drawn={_lastSeries.Count}");
                return;
            }
            _hiddenKeys.Add(key);
        }
        App.DebugLog($"chart legend toggle key={key} hidden={_hiddenKeys.Contains(key)}");
        Refresh(); // 图例外观与画面一起立即更新
    }

    /// <summary>节点是否落在指定祖先（含自身）之内，用于区分圆点与芯片其余区域的点击。</summary>
    private static bool IsWithin(object? source, DependencyObject ancestor)
    {
        var node = source as DependencyObject;
        while (node is not null)
        {
            if (ReferenceEquals(node, ancestor))
                return true;
            node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node);
        }
        return false;
    }

    // 数据查看模式的图例：每条曲线一个芯片，三行分别为设备名称 / 平均心率 / 有效点数。
    // 圆点与实时模式一样可点击改色，颜色写回该曲线供重绘使用（只作用于本次查看，不改动文件）
    private void SyncImportedLegend(ImportedData data)
    {
        // 弹窗锚定在图例圆点上，重建图例会把它连带关掉，打开期间跳过本轮重建
        if (_colorFlyout is { IsOpen: true })
            return;

        var loc = LocalizationService.Instance;
        // 只有「曲线集合 + 顺序」变化时才整批重建（快速连续改色时能保住正在按下的圆点）；
        // 其余时间就地刷新颜色，与实时图例同一套策略。重建后清掉实时图例的标记，
        // 保证退出查看时实时图例一定会按当前设备重新构建
        var signature = string.Join("|", data.Series.Select(s => s.Key));
        _legendSignature = "";
        _legendPanel.Children.Clear();
        if (_importedLegendSignature != signature)
        {
            _importedLegendSignature = signature;
            _legendChips.Clear();
        }
        foreach (var s in data.Series)
        {
            // 芯片内容只在曲线集合变化时重建，颜色 / 读数就地刷新：
            // 否则每秒重建会销毁正被按下的元素，把点击一起吞掉
            if (_legendChips.TryGetValue(s.Key, out var existing))
            {
                // 芯片不重建，只就地刷新颜色（改色 / 切主题后都要跟上）
                existing.Dot.Background = new SolidColorBrush(s.Color);
                existing.Dot.BorderBrush = Miuix.Brush("MiuixCardBorder");
                existing.Chip.Opacity = _hiddenKeys.Contains(s.Key) ? HiddenChipOpacity : 1;
                _legendPanel.Children.Add(existing.Chip);
                continue;
            }

            var avg = (int)Math.Round(s.Points.Average(p => p.Hr));

            var dot = (Button)null!;
            dot = Miuix.DotButton(new SolidColorBrush(s.Color), (_, _) => _colorFlyout = Miuix.ShowColorMenu(dot, color =>
            {
                s.Color = color;
                dot.Background = new SolidColorBrush(color);
                Refresh(); // 曲线立即换色
            }));
            dot.Width = 34;
            dot.Height = 34;
            dot.CornerRadius = new CornerRadius(17);
            dot.BorderThickness = new Thickness(2);
            dot.BorderBrush = Miuix.Brush("MiuixCardBorder");
            dot.VerticalAlignment = VerticalAlignment.Center;

            var chip = new Border
            {
                CornerRadius = new CornerRadius(14),
                Background = Miuix.Brush("MiuixSubtle"),
                Padding = new Thickness(10, 7, 10, 7),
                // 显式顶对齐：即使外层容器给出多余高度，芯片也不会被纵向拉伸
                VerticalAlignment = VerticalAlignment.Top,
                Child = Miuix.Horizontal(8,
                    dot,
                    Miuix.Vertical(0,
                        new TextBlock
                        {
                            Text = s.Name,
                            FontSize = 13,
                            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                            MaxWidth = 200,
                            TextTrimming = TextTrimming.CharacterEllipsis,
                            Foreground = Miuix.Brush("MiuixTextPrimary"),
                        },
                        // 平均心率：该设备这一列所有有效上报的均值；点数为实际有效样本数
                        // （部分设备间隔数秒才上报一次，点数会明显少于文件的总行数）
                        Miuix.Caption(loc.T("legend_avg", avg)),
                        Miuix.Caption(loc.T("legend_points", s.Points.Count)))),
            };
            // 与实时图例同一套交互：点芯片切换该曲线的显示 / 隐藏，点圆点改色
            AttachChipToggle(chip, dot, s.Key);
            if (_hiddenKeys.Contains(s.Key))
                chip.Opacity = HiddenChipOpacity;
            _legendPanel.Children.Add(chip);

            // 记入缓存供就地刷新：改色后圆点底色、切主题后描边画笔都要跟着更新，
            // 因此必须持有圆点引用（芯片结构稳定才能就地改，见 §图例不每秒重建）
            _legendChips[s.Key] = new LegendChip(
                chip, dot, new TextBlock(), new TextBlock(), new Border(), new TextBlock());
        }
        _legendScroll.Visibility = _legendPanel.Children.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void RebuildZoneCaption()
    {
        int maxHr = HeartRateZones.MaxHeartRate;
        var parts = new List<string>();
        for (var i = 0; i < HeartRateZones.ZoneCount; i++)
        {
            var lo = (int)Math.Round(HeartRateZones.LowerOf(i, maxHr));
            var hi = (int)Math.Round(HeartRateZones.UpperOf(i, maxHr));
            parts.Add($"Z{i + 1} {HeartRateZones.NameOf(i)} {lo:0}-{hi:0}");
        }
        _zoneCaption.Text = string.Join("    ", parts);
    }

    private void Redraw()
    {
        var width = _plot.ActualWidth;
        var height = _plot.ActualHeight;
        if (width <= 0 || height <= 0 || _plot is null)
            return;

        // 未记录时保留最后一帧供回看：本次记录的数据还在（下一次开始记录才清空），
        // 按冻结的时间窗重绘，缩放窗口 / 切主题后画面仍正确；没有任何记录数据才清空画布。
        // 数据查看模式只画拖入 CSV / Excel 的历史数据，与上述实时数据互不影响
        var imported = _imported;
        if (imported is null && !RecordingService.Instance.IsRecording &&
            RecordingService.Instance.Rows.Count == 0)
        {
            _plot.Children.Clear();
            _sliderBar.Visibility = Visibility.Collapsed; // 没有记录数据时不存在回看
            return;
        }

        // 记录中跟随当前时间；停止后停在冻结时刻；数据查看固定在数据末尾，
        // 曲线都不随墙钟前移
        var now = ResolveNow();
        // 时间窗起点：常规窗口取 now - 窗口；"全程"从记录开始（数据查看为文件首条样本）算起。
        // 样本缓冲只保留 ~10 分钟，全程改用记录服务的完整行数据（见 BuildSeries）
        var startT = imported?.StartT ??
            new DateTimeOffset(RecordingService.Instance.StartedAt).ToUnixTimeMilliseconds() / 1000.0;
        var fullSpan = _windowSeconds <= 0;
        double from, span;
        if (fullSpan)
        {
            from = startT;
            span = Math.Max(1, now - from);
        }
        else
        {
            // 常规窗口默认跟随最新（贴右端）；拖动回看滑块后窗口起点固定在历史位置
            var (rangeStart, rangeEnd) = ViewRange(now);
            var maxPos = rangeEnd - rangeStart - _windowSeconds;
            if (maxPos > 0)
            {
                from = Math.Clamp(_viewFrom ?? rangeEnd - _windowSeconds, rangeStart, rangeStart + maxPos);
                if (_viewFrom.HasValue)
                    _viewFrom = from; // 固定起点被缓冲裁剪顶走时收敛，避免画面来回跳
            }
            else
            {
                _viewFrom = null; // 数据还不足一个窗口，无从回看
                from = now - _windowSeconds;
            }
            span = _windowSeconds;
        }
        var winEnd = fullSpan ? now : from + span;
        var visible = imported is not null
            ? FilterImported(imported, from, winEnd)
            : BuildSeries(from, winEnd, fullSpan);
        // 图例里被点击隐藏的曲线整条退出绘制：填充线、Y 轴自动量程与长按数值卡一并剔除
        // （数据仍在样本 / 文件里，再次点击图例即恢复）
        var series = visible.Where(s => !_hiddenKeys.Contains(s.Key)).ToList();
        _lastSeries = series;
        _lastFrom = from;
        _lastSpan = span;
        var maxHr = HeartRateZones.MaxHeartRate;

        var (autoMin, autoMax) = AutoYRange(series);
        // 设置了自定义 Y 轴范围时完全按设置绘制，出界数据由 PlotClip 裁掉
        var yMin = _yMinCustom ?? autoMin;
        var yMax = _yMaxCustom ?? autoMax;

        // 底部预留一条时间标签区；左侧预留 Y 轴数值带，数值画在绘图区外，不被曲线遮挡
        var labelStrip = 22;
        var plotLeft = YAxisWidth;
        var plotW = Math.Max(1, width - plotLeft);
        var plotH = Math.Max(1, height - labelStrip);

        double MapX(double t) => plotLeft + (t - from) / span * plotW;
        double MapY(double v) => plotH - (v - yMin) / (yMax - yMin) * plotH;

        _plot.Children.Clear();
        _plot.Clip = new Microsoft.UI.Xaml.Media.RectangleGeometry
        {
            Rect = new Windows.Foundation.Rect(0, 0, width, height),
        };

        // 心率区间背景带：只铺绘图区，不侵入左侧的 Y 轴数值带
        if (_showZones)
        {
            for (var i = 0; i < HeartRateZones.ZoneCount; i++)
            {
                var lo = HeartRateZones.LowerOf(i, maxHr);
                var hi = HeartRateZones.UpperOf(i, maxHr);
                var color = HeartRateZones.ColorOf(i);
                _plot.Children.Add(new Rectangle
                {
                    Width = plotW,
                    Height = Math.Max(0, MapY(lo) - MapY(hi)),
                    Fill = new SolidColorBrush(Color.FromArgb(46, color.R, color.G, color.B)),
                    IsHitTestVisible = false,
                });
                Canvas.SetTop(_plot.Children[^1], MapY(hi));
                Canvas.SetLeft(_plot.Children[^1], plotLeft);
            }
        }

        // Y 轴刻度线与标签：刻度线只画在绘图区内，数值画在绘图区外并右对齐贴住左边缘
        var step = yMax - yMin > 120 ? 40 : 20;
        for (var v = (int)Math.Ceiling(yMin / step) * step; v <= yMax; v += step)
        {
            var y = MapY(v);
            _plot.Children.Add(new Rectangle
            {
                Width = plotW,
                Height = 1,
                Fill = new SolidColorBrush(Color.FromArgb(50, 128, 128, 128)),
                IsHitTestVisible = false,
            });
            Canvas.SetTop(_plot.Children[^1], y);
            Canvas.SetLeft(_plot.Children[^1], plotLeft);

            var label = new TextBlock
            {
                Text = v.ToString(),
                FontSize = 13,
                TextAlignment = TextAlignment.Right,
                Foreground = AxisLabelBrush(),
            };
            _plot.Children.Add(label);
            // 标签垂直居中对齐刻度线：偏移量取字号的一半（13px 约 17px 行高）
            Canvas.SetTop(label, Math.Clamp(y - 9, 1, Math.Max(1, plotH - 18)));
            Canvas.SetLeft(label, 0);
            label.Width = YAxisWidth - YAxisGap; // 右对齐的落点 = 绘图区左缘减去留白
        }

        // X 轴时间标签（6 个刻度）：从录制开始计 0 起的相对时长，停止后随最后一帧保留
        for (var k = 0; k <= 5; k++)
        {
            var t = from + k * span / 5;
            var elapsed = t - startT;
            if (elapsed < 0)
                continue; // 窗口左端早于录制起点，不显示负时间
            var ts = TimeSpan.FromSeconds(elapsed);
            var label = new TextBlock
            {
                Text = ts.TotalHours >= 1 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"m\:ss"),
                FontSize = 13,
                Foreground = AxisLabelBrush(),
            };
            _plot.Children.Add(label);
            var x = MapX(t);
            // 底部标签条高 22，13px 行高约 17，顶部下移 2 保证不贴底裁切
            Canvas.SetTop(label, height - 20);
            Canvas.SetLeft(label, Math.Clamp(x - 24, 0, Math.Max(0, width - 60)));
        }

        // 各设备曲线（线 + 同色半透明填充）
        foreach (var curve in series)
        {
            var samples = curve.Points;
            if (samples.Count == 0)
                continue;

            var c = curve.Color;
            var points = new List<Windows.Foundation.Point>(samples.Count);
            foreach (var s in samples)
                points.Add(new Windows.Foundation.Point(Math.Clamp(MapX(s.T), plotLeft, width), MapY(s.Hr)));

            var fill = new Polygon
            {
                Fill = new SolidColorBrush(Color.FromArgb(56, c.R, c.G, c.B)),
                IsHitTestVisible = false,
            };
            var fillPoints = new PointCollection();
            foreach (var p in points) fillPoints.Add(p);
            fillPoints.Add(new Windows.Foundation.Point(points[^1].X, plotH));
            fillPoints.Add(new Windows.Foundation.Point(points[0].X, plotH));
            fill.Points = fillPoints;
            fill.Clip = PlotClip(plotLeft, width, plotH);
            _plot.Children.Add(fill);

            var line = new Polyline
            {
                Stroke = new SolidColorBrush(c),
                StrokeThickness = 2.6,
                StrokeLineJoin = Microsoft.UI.Xaml.Media.PenLineJoin.Round,
                IsHitTestVisible = false,
            };
            var linePoints = new PointCollection();
            foreach (var p in points) linePoints.Add(p);
            line.Points = linePoints;
            line.Clip = PlotClip(plotLeft, width, plotH);
            _plot.Children.Add(line);
        }

        // 长按查看数值时跟随最新数据刷新（时间窗每秒前移，同一像素对应的时刻会变）
        if (_scrubActive)
            UpdateScrub();

        // 最后铺一层「淡化底板」：曲线 / 填充即使越过绘图区左缘，压在轴标签上也已经明显变淡，
        // 文字始终清楚。视觉上与卡片背景融为一体，不是一块可见的色块
        _plot.Children.Add(new Rectangle
        {
            Width = plotLeft,
            Height = Math.Max(1, plotH),
            Fill = Miuix.Brush("MiuixCardBackground"),
            Opacity = AxisFadeOpacity,
            IsHitTestVisible = false,
        });
        Canvas.SetTop(_plot.Children[^1], 0);
        Canvas.SetLeft(_plot.Children[^1], 0);

        SyncSlider();
    }

    // 记录中跟随当前时间；停止后停在冻结时刻；数据查看固定在数据末尾
    // （重绘与回看滑块共用同一取时逻辑）
    private double ResolveNow() =>
        _imported?.EndT ??
        (RecordingService.Instance.IsRecording || _frozenNow == 0 ? Now() : _frozenNow);

    // 非全程窗口可回看的数据范围：实时模式左端取设备样本缓冲里最早的一条
    // （缓冲只保留 ~10 分钟，更早的样本已不存在），右端为当前/冻结时刻；
    // 数据查看模式即文件里数据的完整时间范围
    private (double Start, double End) ViewRange(double now)
    {
        if (_imported is not null)
            return (_imported.StartT, Math.Max(_imported.EndT, now));

        var startT = new DateTimeOffset(RecordingService.Instance.StartedAt).ToUnixTimeMilliseconds() / 1000.0;
        var earliest = EarliestSampleT();
        var hi = Math.Max(startT, now);
        return (earliest.HasValue ? Math.Clamp(earliest.Value, startT, hi) : startT, now);
    }

    // 各设备样本缓冲中最早的一条时间（都没有数据时为 null）
    private double? EarliestSampleT()
    {
        double? earliest = null;
        foreach (var device in App.Ble.Devices)
        {
            var t = device.EarliestSampleT();
            if (t.HasValue && (!earliest.HasValue || t.Value < earliest.Value))
                earliest = t;
        }
        return earliest;
    }

    // ===== 回看滑块 =====

    // 依据当前窗口与数据范围更新滑块的可见性、宽度与位置
    private void SyncSlider()
    {
        if (_sliderBar is null)
            return;
        if (_windowSeconds <= 0)
        {
            _sliderBar.Visibility = Visibility.Collapsed; // 全程模式下整段都在画面里
            return;
        }

        var (rangeStart, rangeEnd) = ViewRange(ResolveNow());
        var total = rangeEnd - rangeStart;
        if (total <= _windowSeconds + 1)
        {
            _sliderBar.Visibility = Visibility.Collapsed; // 历史没有超出窗口，无可回看
            return;
        }

        _sliderBar.Visibility = Visibility.Visible;
        // 滑块行与绘图区左缘对齐：左侧 Y 轴数值带的位置同宽留白
        if (_sliderBar.Parent is Grid sliderRow && sliderRow.ColumnDefinitions.Count >= 2)
        {
            sliderRow.ColumnDefinitions[0].Width = new GridLength(YAxisWidth, GridUnitType.Pixel);
            sliderRow.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);
        }

        var trackWidth = _sliderBar.ActualWidth;
        if (trackWidth <= 0)
            return; // 首次布局前无法定位，SizeChanged 后会再同步

        var maxPos = total - _windowSeconds;
        var thumbWidth = ThumbWidth(trackWidth, total);
        // 跟随最新时窗口贴右端；回看时窗口起点固定
        var from = Math.Clamp(_viewFrom ?? rangeEnd - _windowSeconds, rangeStart, rangeStart + maxPos);
        if (_viewFrom.HasValue)
            _viewFrom = from;
        var left = (from - rangeStart) / maxPos * (trackWidth - thumbWidth);
        _lastThumbLeft = left;
        _lastThumbWidth = thumbWidth;
        _sliderThumb.Width = thumbWidth;
        _sliderThumb.Margin = new Thickness(left, 0, 0, 0);
    }

    // 滑块宽度 = 窗口占可用历史的比例，最短 36 保证可抓取
    private double ThumbWidth(double trackWidth, double total) =>
        Math.Clamp(trackWidth * _windowSeconds / total, 36, Math.Max(36, trackWidth));

    private void OnSliderPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_windowSeconds <= 0)
            return;
        var x = e.GetCurrentPoint(_sliderBar).Position.X;
        _sliderBar.CapturePointer(e.Pointer); // 拖出滑块区域仍能继续收 Move / Release
        _sliderDrag = true;
        // 命中滑块（留少量容差）按住原位拖动；点在轨道空白处则让滑块中心跳到点击位置
        var onThumb = x >= _lastThumbLeft - 6 && x <= _lastThumbLeft + _lastThumbWidth + 6;
        _sliderGrab = onThumb ? x - _lastThumbLeft : _lastThumbWidth / 2;
        if (!onThumb)
            MoveSliderThumb(x - _sliderGrab);
        _sliderThumb.Opacity = 0.7;
    }

    private void OnSliderMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_sliderDrag)
            return;
        MoveSliderThumb(e.GetCurrentPoint(_sliderBar).Position.X - _sliderGrab);
    }

    private void OnSliderReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_sliderDrag)
            return;
        _sliderDrag = false;
        _sliderThumb.Opacity = 1;
        _sliderBar.ReleasePointerCapture(e.Pointer);
    }

    // 把滑块拖到指定左缘位置：换算成窗口起点后重绘；拖到最右端恢复跟随最新
    private void MoveSliderThumb(double thumbLeft)
    {
        var trackWidth = _sliderBar.ActualWidth;
        if (trackWidth <= 0 || _windowSeconds <= 0)
            return;
        var (rangeStart, rangeEnd) = ViewRange(ResolveNow());
        var maxPos = rangeEnd - rangeStart - _windowSeconds;
        if (maxPos <= 0)
            return;
        var thumbWidth = ThumbWidth(trackWidth, rangeEnd - rangeStart);
        var left = Math.Clamp(thumbLeft, 0, trackWidth - thumbWidth);
        var f = left / Math.Max(1, trackWidth - thumbWidth);
        _viewFrom = f >= 0.999 ? null : rangeStart + f * maxPos;
        Redraw();
    }

    // 当前时间窗：_windowSeconds <= 0 表示"全程"，从记录开始（数据查看为文件首条样本）到当前时刻
    private (double From, double Span) CurrentWindow(double now)
    {
        if (_windowSeconds > 0)
            return (now - _windowSeconds, _windowSeconds);

        if (_imported is not null)
            return (_imported.StartT, Math.Max(1, _imported.EndT - _imported.StartT));

        var started = RecordingService.Instance.StartedAt;
        if (started == default)
            return (now - 60, 60); // 从未记录过：退化为 1 分钟（无数据，仅用于坐标计算）
        var startT = new DateTimeOffset(started).ToUnixTimeMilliseconds() / 1000.0;
        return (startT, Math.Max(1, now - startT));
    }

    // 组装窗口内的曲线数据：常规窗口用各设备的样本缓冲（含每秒补点）；
    // "全程"用记录服务的完整行数据——样本缓冲只保留约 10 分钟，撑不起全程视图
    private List<ChartSeries> BuildSeries(double from, double to, bool full)
    {
        var series = new List<ChartSeries>();
        if (!full)
        {
            foreach (var device in App.Ble.Devices)
            {
                var samples = device.Snapshot(from, to);
                if (samples.Length > 0)
                    series.Add(new ChartSeries(device.DisplayName, samples.ToList())
                    {
                        Color = device.ChartColor,
                        Key = KeyOf(device),
                    });
            }
            return series;
        }

        // 按地址聚合记录行；行里只有地址与心率，曲线颜色取自对应的设备
        var byAddress = new Dictionary<ulong, List<HeartRateSample>>();
        foreach (var row in RecordingService.Instance.Rows)
        {
            var t = new DateTimeOffset(row.Time).ToUnixTimeMilliseconds() / 1000.0;
            if (t < from || t > to)
                continue;
            foreach (var pair in row.Values)
            {
                if (!byAddress.TryGetValue(pair.Key, out var points))
                    byAddress[pair.Key] = points = new List<HeartRateSample>();
                points.Add(new HeartRateSample(t, pair.Value));
            }
        }
        foreach (var pair in byAddress)
        {
            var device = App.Ble.Devices.FirstOrDefault(d => d.Address == pair.Key);
            if (device is null)
                continue;
            series.Add(new ChartSeries(device.DisplayName, Decimate(pair.Value))
            {
                Color = device.ChartColor,
                Key = KeyOf(device),
            });
        }
        return series;
    }

    // 数据查看模式：按当前窗口过滤拖入 CSV / Excel 里的历史样本（超长时抽稀，与全程视图同策略）
    private static List<ChartSeries> FilterImported(ImportedData data, double from, double to)
    {
        var series = new List<ChartSeries>();
        foreach (var s in data.Series)
        {
            // 偏移：整条曲线的时间减 ShiftSeconds，即向左平移（对齐设备间的时间戳偏差）
            var shift = s.ShiftSeconds;
            var points = shift == 0
                ? s.Points.Where(p => p.T >= from && p.T <= to).ToList()
                : s.Points.Where(p => p.T - shift >= from && p.T - shift <= to)
                    .Select(p => new HeartRateSample(p.T - shift, p.Hr)).ToList();
            if (points.Count == 0)
                continue;
            series.Add(s with { Points = Decimate(points) });
        }
        return series;
    }

    // 超长记录抽稀：每条曲线最多 ~3600 点（约一小时每秒一点），长记录下每秒重绘依然流畅
    private static List<HeartRateSample> Decimate(List<HeartRateSample> points)
    {
        const int maxPoints = 3600;
        if (points.Count <= maxPoints)
            return points;
        var stride = (int)Math.Ceiling(points.Count / (double)maxPoints);
        var result = new List<HeartRateSample>(points.Count / stride + 1);
        for (var i = 0; i < points.Count; i += stride)
            result.Add(points[i]);
        if ((points.Count - 1) % stride != 0)
            result.Add(points[^1]); // 末点保留，曲线画到最新
        return result;
    }

    // Y 轴自动范围（编辑 Y 轴的「恢复自动」按钮走这里）：贴着当前画面的数据取值——
    // 下限 = 数据最小值 × 0.95，上限 = 数据最大值 × 1.05，四舍五入取整。
    // 不再掺入心率区间界限，曲线的上下留白只由真实数据决定
    private static (double Lo, double Hi) AutoYRange(List<ChartSeries> series)
    {
        double lo = double.MaxValue, hi = double.MinValue;
        foreach (var line in series)
        {
            foreach (var s in line.Points)
            {
                lo = Math.Min(lo, s.Hr);
                hi = Math.Max(hi, s.Hr);
            }
        }
        // 一条样本都没有（刚开始记录的第一秒等）：退回到最大心率满量程，保证不出现除零
        if (lo > hi)
            return (30, HeartRateZones.MaxHeartRate);

        // MidpointRounding.AwayFromZero = 四舍五入（Math.Round 默认是银行家舍入）
        var min = Math.Clamp(Math.Round(lo * 0.95, MidpointRounding.AwayFromZero), 0, 300);
        var max = Math.Clamp(Math.Round(hi * 1.05, MidpointRounding.AwayFromZero), 0, 300);
        // 数据恒定（如全程 60 bpm）时上下限会重合，展开一点避免量程为 0
        if (max - min < 1)
        {
            min = Math.Max(0, min - 1);
            max = Math.Min(300, max + 1);
        }
        return (min, max);
    }

    // 心率超出 Y 轴范围时把曲线裁在绘图区内（左侧避开 Y 轴数值带、底部避开时间标签条），
    // 不压到轴标签上
    private static Microsoft.UI.Xaml.Media.RectangleGeometry PlotClip(double left, double width, double plotH) => new()
    {
        Rect = new Windows.Foundation.Rect(left, 0, Math.Max(0, width - left), plotH),
    };

    // 轴标签画笔：浅色模式纯黑、深色模式纯白（不跟随 Secondary 灰，读数更清楚）。
    // Miuix 画笔是共享实例，主题切换时会被原地改色，因此这里按主题取色返回新画笔
    private static SolidColorBrush AxisLabelBrush() =>
        ThemeManager.Instance.ResolvedTheme == ElementTheme.Dark
            ? new SolidColorBrush(Color.FromArgb(255, 255, 255, 255))
            : new SolidColorBrush(Color.FromArgb(255, 0, 0, 0));

    // ===== 长按查看数值 =====

    private void OnPlotPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(_plot);
        _scrubX = point.Position.X;
        _scrubY = point.Position.Y;
        _pressPending = true;
        _plot.CapturePointer(e.Pointer); // 按住移出画布仍能继续收 Move / Release
        _pressTimer.Stop();
        _pressTimer.Start();
    }

    private void OnPlotPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(_plot);
        if (_scrubActive)
        {
            _scrubX = point.Position.X;
            _scrubY = point.Position.Y;
            UpdateScrub();
            return;
        }
        if (!_pressPending)
            return;
        // 按下后移动太多视为滑动而非长按，取消本次判定
        var dx = point.Position.X - _scrubX;
        var dy = point.Position.Y - _scrubY;
        if (dx * dx + dy * dy > 144)
        {
            _pressPending = false;
            _pressTimer.Stop();
        }
    }

    private void OnPlotPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _pressPending = false;
        _pressTimer.Stop();
        if (!_scrubActive)
            return;
        _scrubActive = false;
        _scrubLine.Visibility = Visibility.Collapsed;
        _scrubCard.Visibility = Visibility.Collapsed;
    }

    // 长按触发：在按住的位置画时间参考线，弹出各曲线在该时刻的数值卡；
    // 按住期间移动指针可连续查看，松开即消失
    private void UpdateScrub()
    {
        var width = _plot.ActualWidth;
        var plotAreaH = _plot.ActualHeight;
        if (!_scrubActive || width <= 0 || plotAreaH <= 0 || _lastSeries.Count == 0)
        {
            _scrubLine.Visibility = Visibility.Collapsed;
            _scrubCard.Visibility = Visibility.Collapsed;
            return;
        }

        var x = Math.Clamp(_scrubX, 0, width);
        var t = _lastFrom + x / width * _lastSpan;

        _scrubLine.Height = Math.Max(1, plotAreaH - 22); // 与曲线区同高（扣除底部标签条）
        _scrubLine.Margin = new Thickness(x, 0, 0, 0);
        _scrubLine.Visibility = Visibility.Visible;

        var loc = LocalizationService.Instance;
        var panel = new StackPanel { Spacing = 4 };

        // 时间戳：与 X 轴一致，显示从录制开始（数据查看为数据首条）起的相对时长
        double? startT = _imported?.StartT;
        if (startT is null && RecordingService.Instance.StartedAt != default)
            startT = new DateTimeOffset(RecordingService.Instance.StartedAt).ToUnixTimeMilliseconds() / 1000.0;
        if (startT.HasValue)
        {
            var elapsed = TimeSpan.FromSeconds(t - startT.Value);
            var timeText = elapsed.TotalSeconds < 0
                ? "--:--"
                : elapsed.TotalHours >= 1 ? elapsed.ToString(@"h\:mm\:ss") : elapsed.ToString(@"m\:ss");
            panel.Children.Add(new TextBlock
            {
                Text = timeText,
                FontSize = 11,
                Foreground = Miuix.Brush("MiuixTextSecondary"),
            });
        }

        foreach (var s in _lastSeries)
        {
            // points 按时间升序：取 t 之前最近的一个采样点作为该时刻的数值
            var points = s.Points;
            int lo = 0, hi = points.Count - 1, index = -1;
            while (lo <= hi)
            {
                var mid = (lo + hi) / 2;
                if (points[mid].T <= t) { index = mid; lo = mid + 1; }
                else hi = mid - 1;
            }

            // 三列布局：圆点 | 名称（自适应宽，超长省略）| 数值（右对齐）
            var row = new Grid
            {
                ColumnSpacing = 8,
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = GridLength.Auto },
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                    new ColumnDefinition { Width = GridLength.Auto },
                }
            };
            var dot = new Border
            {
                Width = 8,
                Height = 8,
                CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(s.Color),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var name = new TextBlock
            {
                Text = s.Name,
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Miuix.Brush("MiuixTextPrimary"),
            };
            var value = new TextBlock
            {
                Text = index >= 0 ? $"{points[index].Hr} {loc.T("bpm")}" : "--",
                FontSize = 13,
                FontFamily = Miuix.MonoFont,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                TextAlignment = TextAlignment.Right,
                Foreground = Miuix.Brush("MiuixTextPrimary"),
            };
            Grid.SetColumn(dot, 0);
            Grid.SetColumn(name, 1);
            Grid.SetColumn(value, 2);
            row.Children.Add(dot);
            row.Children.Add(name);
            row.Children.Add(value);
            panel.Children.Add(row);
        }

        _scrubCard.Child = panel;
        _scrubCard.Visibility = Visibility.Visible;

        // 卡片默认出现在参考线右侧、指针上方；贴右 / 贴顶时翻转方向
        var cardWidth = 190;
        var cardHeight = 34 + _lastSeries.Count * 22;
        var left = x + 12;
        if (left + cardWidth > width)
            left = x - cardWidth - 12;
        var top = _scrubY - cardHeight - 12;
        if (top < 0)
            top = _scrubY + 12;
        _scrubCard.Margin = new Thickness(
            Math.Clamp(left, 0, Math.Max(0, width - cardWidth)),
            Math.Clamp(top, 0, Math.Max(0, plotAreaH - cardHeight)),
            0, 0);
    }

    // ===== 数据查看（拖入 CSV / Excel） =====

    // 拖拽悬停反馈：接受 CSV / Excel 文件拖入并提示松开导入；记录中不接受——
    // 实时曲线与导入的历史数据不能混在一张图里
    private void OnPageDragOver(object sender, DragEventArgs e)
    {
        var loc = LocalizationService.Instance;
        var recording = RecordingService.Instance.IsRecording;
        var hasFiles = e.DataView.Contains(StandardDataFormats.StorageItems);
        e.AcceptedOperation = !recording && hasFiles
            ? DataPackageOperation.Copy
            : DataPackageOperation.None;
        // 记录中给出原因提示；拖的不是文件时不提示（仅显示禁止光标）
        if (recording || hasFiles)
        {
            e.DragUIOverride.Caption = recording ? loc.T("view_drop_blocked") : loc.T("view_drop_hint");
            e.DragUIOverride.IsCaptionVisible = true;
        }
        e.Handled = true;
    }

    private async void OnPageDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (RecordingService.Instance.IsRecording)
            return;
        try
        {
            var dataView = e.DataView;
            if (!dataView.Contains(StandardDataFormats.StorageItems))
                return;
            var items = await dataView.GetStorageItemsAsync();
            var file = items.OfType<Windows.Storage.StorageFile>()
                .FirstOrDefault(f => f.FileType.Equals(".csv", StringComparison.OrdinalIgnoreCase)
                    || f.FileType.Equals(".xlsx", StringComparison.OrdinalIgnoreCase));
            if (file is null)
            {
                App.DebugLog("chart import drop: no csv/xlsx file");
                await ShowImportFailedAsync();
                return;
            }
            // xlsx 是 zip + xml 的二进制，不能用文本接口读；两条路径最终都归一化成
            // 同一套「表头 + 数据行」交给 BuildImportedData 解析
            var imported = file.FileType.Equals(".xlsx", StringComparison.OrdinalIgnoreCase)
                ? ParseImportedWorkbook(await ReadAllBytesAsync(file), file.Name)
                : ParseImportedCsv(await Windows.Storage.FileIO.ReadTextAsync(file), file.Name);
            if (imported is null)
            {
                App.DebugLog($"chart import parse failed file={file.Name}");
                await ShowImportFailedAsync();
                return;
            }
            EnterImportedView(imported);
        }
        catch (Exception ex)
        {
            App.ReportCrash(ex);
        }
    }

    private async Task ShowImportFailedAsync()
    {
        var loc = LocalizationService.Instance;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = loc.T("view_import_failed_title"),
            Content = loc.T("view_import_failed_sub"),
            CloseButtonText = "OK",
        };
        // 只调整「OK」按钮的位置，按钮本身的样式 / 尺寸一概不动
        dialog.Opened += (_, _) => CenterDialogCommandButton(dialog);
        await dialog.ShowAsync();
    }

    /// <summary>
    /// 把 ContentDialog 命令区里唯一的按钮挪到「左右等宽留白」的中间列，使其在弹窗里左右居中。
    ///
    /// WinUI 没有提供「命令区居中」的开关：模板把命令区排成五列
    /// （Primary * / FirstSpacer 0 / SecondaryColumn 0 / SecondSpacer 间距 / Close *），
    /// 只给一个按钮时该按钮落在最后一列并被拉伸占满右半区（见 WindowsAppSDK 的
    /// themes/generic.xaml 中 ContentDialog 模板，本机为 1.7.250909003 的 29605-29616 行）。
    /// 因此这里只改「按钮所在的列」与两侧空列的星号权重（0.5* / 1* / 0.5*）——
    /// 按钮自身的样式、宽度、内容、拉伸方式全部保持模板原样，视觉上只是从右半边挪到正中。
    /// 找不到模板元素时静默跳过（按钮保持默认右对齐，不影响功能）。
    /// </summary>
    private static void CenterDialogCommandButton(ContentDialog dialog)
    {
        if (FindChild<Grid>(dialog, "CommandSpace") is not { } commandSpace
            || commandSpace.ColumnDefinitions.Count < 5)
            return;
        if (FindChild<Button>(commandSpace, "CloseButton") is not { } closeButton)
            return;

        commandSpace.ColumnDefinitions[0].Width = new GridLength(0.5, GridUnitType.Star);
        commandSpace.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);
        commandSpace.ColumnDefinitions[4].Width = new GridLength(0.5, GridUnitType.Star);
        Grid.SetColumn(closeButton, 1);
    }

    /// <summary>模板内的子元素不在 XAML 命名域里，按名字走可视树查找（找不到返回 null）。</summary>
    private static T? FindChild<T>(DependencyObject parent, string name) where T : FrameworkElement
    {
        var count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T typed && typed.Name == name)
                return typed;
            if (FindChild<T>(child, name) is { } found)
                return found;
        }
        return null;
    }

    // 进入数据查看模式：图例、标题与画面全部切换到文件数据
    private void EnterImportedView(ImportedData data)
    {
        _imported = data;
        _viewFrom = null; // 从数据末尾（跟随最新）开始查看
        _offsetCurveIndex = 0; // 新文件的曲线序号与上一个文件无关，回到第一条
        _maeTestIndex = 0;     // 误差计算弹窗的所选设备同理，回到默认（第 1 条 / 第 2 条）
        _maeRefIndex = 1;
        // 图例隐藏状态按文件独立：新文件的列序号与上一份文件无关，先清掉上一份的
        _hiddenKeys.RemoveWhere(k => k.StartsWith(ImportedKeyPrefix, StringComparison.Ordinal));
        App.DebugLog($"chart import enter file={data.FileName} series={data.Series.Count} " +
            $"span={data.EndT - data.StartT:F0}s avg={string.Join(',', data.Series.Select(s => (int)Math.Round(s.Points.Average(p => p.Hr))))}");
        UpdateImportedUi();
    }

    private void ExitImportedView()
    {
        if (_imported is null)
            return;
        _imported = null;
        _viewFrom = null;
        App.DebugLog("chart import exit");
        UpdateRecordUi();   // 恢复标题 / 记录与导出按钮
        UpdateImportedUi(); // 重建实时图例并重绘
    }

    // 进入 / 退出数据查看模式后统一刷新：图例、外观差异、画面与空状态
    private void UpdateImportedUi()
    {
        if (_imported is ImportedData data)
            SyncImportedLegend(data);
        else
            SyncLegend();
        ApplyModeChrome();
        Redraw();
        _emptyPanel.Visibility = _plot.Children.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    // 数据查看 / 实时两种模式的外观差异：标题、退出查看按钮、记录 / 导出按钮可用性
    private void ApplyModeChrome()
    {
        var imported = _imported is not null;
        UpdateTitle();
        _exitViewButton.Visibility = imported ? Visibility.Visible : Visibility.Collapsed;
        // 曲线偏移 / 误差计算都需要至少两条曲线才有对齐意义
        var multiCurve = _imported is { } d && d.Series.Count >= 2;
        _offsetButton.Visibility = multiCurve ? Visibility.Visible : Visibility.Collapsed;
        _maeButton.Visibility = multiCurve ? Visibility.Visible : Visibility.Collapsed;
        // 导出对应"本次记录"的数据，查看历史文件时禁用以免误解
        _exportButton.IsEnabled = !imported && RecordingService.Instance.Rows.Count > 0;
        _recordButton.IsEnabled = RecordingService.Instance.IsRecording
            || App.Ble.Devices.Any(d => d.IsConnected);
    }

    // 标题：实时模式为页面名；数据查看模式追加文件名
    private void UpdateTitle()
    {
        _title.Text = _imported is null
            ? LocalizationService.Instance.T("chart_title")
            : $"{LocalizationService.Instance.T("chart_title")} · {_imported.FileName}";
    }

    // 解析「表头 + 数据行」这一通用形态（CSV 与设备端导出的 Excel 共用）：
    // 首列时间戳，其后每列一台设备，空单元格表示该秒无上报。返回 null 表示格式不认识
    private static ImportedData? BuildImportedData(string?[] header, IEnumerable<string?[]> rows, string fileName)
    {
        try
        {
            if (header.Length < 2 || !IsTimestampHeader(header[0]))
                return null;

            // 有效列宽 = 最后一个非空表头列：Excel 常把工作表声明得很宽（如 A1:Z336 而只用到 D 列），
            // 直接按声明宽度取会凭空多出一堆没有数据的空设备曲线
            var width = header.Length;
            while (width > 1 && string.IsNullOrWhiteSpace(header[width - 1]))
                width--;
            if (width < 2)
                return null;

            var names = new string[width - 1];
            for (var i = 0; i < names.Length; i++)
                names[i] = header[i + 1]?.Trim() ?? "";
            var points = new List<HeartRateSample>[names.Length];
            for (var i = 0; i < points.Length; i++)
                points[i] = new List<HeartRateSample>();

            // 行集合要过两遍（先扫元信息行取日期锚点，再逐行读时间），先物化：
            // xlsx 路径传进来的是惰性序列
            var rowList = rows as IReadOnlyList<string?[]> ?? rows.ToList();

            // 首列可能只写 HH:mm:ss（第三方导出的对比文件常见），这类文件没有日期：
            // 优先取文件自带的 "Start Time" 元信息行，其次文件名里的日期，最后退回当天
            var anchor = ResolveDateAnchor(rowList, fileName, out var anchorSource);
            var clock = new TimeColumnReader(anchor);

            foreach (var cells in rowList)
            {
                if (cells.Length < 2 || !clock.TryRead(cells[0], out var t))
                    continue;
                for (var c = 1; c < cells.Length && c <= names.Length; c++)
                {
                    if (int.TryParse(cells[c]?.Trim(), out var hr) && hr > 0 && hr <= 300)
                        points[c - 1].Add(new HeartRateSample(t, hr));
                }
            }

            var series = new List<ChartSeries>();
            for (var i = 0; i < names.Length; i++)
            {
                if (points[i].Count == 0)
                    continue;
                // 文件里没有颜色信息，按色盘顺序分配（与多设备实时曲线的区分方式一致）
                series.Add(new ChartSeries(names[i], points[i])
                {
                    Color = Palette.NamedColors[series.Count % Palette.NamedColors.Length].Color,
                    // 键取列序号：同一文件里出现同名列时也能各自独立隐藏
                    Key = ImportedKeyPrefix + i,
                });
            }
            if (series.Count == 0)
                return null;

            // 纯时间文件（缺日期）的锚点来源与跨天次数：出错时便于回溯是哪一步取错了
            App.DebugLog($"chart import time base={anchor:yyyy-MM-dd HH:mm} by={anchorSource} " +
                $"crossDays={clock.CrossDays}");

            return new ImportedData
            {
                FileName = fileName,
                Series = series,
            };
        }
        catch (Exception ex)
        {
            App.DebugLog($"chart import parse exception: {ex.Message}");
            return null;
        }
    }

    // 首列列名：本应用导出的 CSV 写 "Time"，设备端导出的 Excel 写 "timestamp"
    private static bool IsTimestampHeader(string? text)
    {
        var name = text?.Trim() ?? "";
        return name.Equals("Time", StringComparison.OrdinalIgnoreCase)
            || name.Equals("timestamp", StringComparison.OrdinalIgnoreCase);
    }

    // 解析本应用导出的 CSV（Time,设备1,设备2…）
    private static ImportedData? ParseImportedCsv(string content, string fileName)
    {
        var lines = content.Split('\n');
        if (lines.Length < 2)
            return null;

        var rows = new List<string?[]>();
        for (var line = 1; line < lines.Length; line++)
        {
            var text = lines[line].TrimEnd('\r');
            if (text.Length == 0)
                continue;
            rows.Add(text.Split(','));
        }
        return BuildImportedData(lines[0].TrimEnd('\r').Split(','), rows, fileName);
    }

    // 解析设备端导出的 Excel（.xlsx）：首个工作表、第 1 行表头，其余形态与 CSV 完全一致
    private static ImportedData? ParseImportedWorkbook(byte[] bytes, string fileName)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        var rows = XlsxLite.ReadFirstSheet(stream);
        if (rows is null || rows.Count < 2)
            return null;
        return BuildImportedData(rows[0], rows.Skip(1), fileName);
    }

    // 读取整个文件为字节：xlsx 是 zip 二进制，StorageFile 的文本读取接口不适用
    private static async Task<byte[]> ReadAllBytesAsync(Windows.Storage.StorageFile file)
    {
        var buffer = await Windows.Storage.FileIO.ReadBufferAsync(file);
        var bytes = new byte[buffer.Length];
        using (var reader = Windows.Storage.Streams.DataReader.FromBuffer(buffer))
            reader.ReadBytes(bytes);
        return bytes;
    }

    // 时间戳：文本日期（CSV 与设备端 Excel 都是 yyyy-MM-dd HH:mm:ss[.fff]，本地墙上时间）优先，
    // 兼容把时间存成 Excel 日期序列值（数值单元格）的文件
    private static bool TryParseAbsoluteTime(string? raw, out DateTime time)
    {
        time = default;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        if (DateTime.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeLocal, out time))
            return true;

        // 数值单元格：Excel 日期序列值（1899-12-30 起算的天数）
        if (!double.TryParse(raw, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var serial)
            || serial < 1 || serial > 2958465)
            return false;
        time = DateTime.FromOADate(serial);
        return true;
    }

    /// <summary>
    /// 首列时间的读取器。除本应用与设备端导出的「完整日期时间」外，还兼容只写 HH:mm:ss 的文件
    /// （第三方对比工具常见：表头写 time，文件名 heart_yyyyMMdd_HHmmss，尾部 Summary 段带 Start Time）。
    /// 这类时间没有日期，用锚点补；读到比上一行早 12 小时以上的值即判定跨天，自动进一天。
    /// </summary>
    private sealed class TimeColumnReader
    {
        // 只认「时分秒」形态：大写 F 表示小数位可有可无
        private static readonly string[] TimeOnlyFormats =
        {
            "H:mm:ss.FFFFFFF", "HH:mm:ss.FFFFFFF",
        };
        // 跨天与「设备时钟小幅回退」的分界：秒级采样的记录跨天必然回退近 24 小时
        private static readonly TimeSpan CrossDayThreshold = TimeSpan.FromHours(12);

        private readonly DateTime _anchorDate;
        private DateTime? _last;

        public TimeColumnReader(DateTime anchor)
        {
            _anchorDate = anchor.Date;
        }

        /// <summary>已判定的跨天次数，供导入日志核对。</summary>
        public int CrossDays { get; private set; }

        public bool TryRead(string? raw, out double unixSeconds)
        {
            unixSeconds = 0;
            if (string.IsNullOrWhiteSpace(raw))
                return false;

            var text = raw.Trim();

            // 纯时间：用锚点日期把它补成完整时间
            if (DateTime.TryParseExact(text, TimeOnlyFormats,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var timeOfDay))
            {
                var time = _anchorDate.AddDays(CrossDays).Add(timeOfDay.TimeOfDay);
                if (_last is { } last && time < last - CrossDayThreshold)
                {
                    CrossDays++; // 时间大幅回退 = 跨过零点，整体进一天
                    time = _anchorDate.AddDays(CrossDays).Add(timeOfDay.TimeOfDay);
                }
                _last = time;
                unixSeconds = new DateTimeOffset(time).ToUnixTimeMilliseconds() / 1000.0;
                return true;
            }

            if (!TryParseAbsoluteTime(text, out var absolute))
                return false;
            _last = absolute;
            unixSeconds = new DateTimeOffset(absolute).ToUnixTimeMilliseconds() / 1000.0;
            return true;
        }
    }

    /// <summary>
    /// 纯时间文件的日期锚点：优先用文件自带的 "Start Time, yyyy-MM-dd HH:mm:ss" 元信息行（最权威），
    /// 其次文件名里的 yyyyMMdd[_HHmmss]（如 heart_20260912_012857.csv），都没有才退回当天。
    /// </summary>
    private static DateTime ResolveDateAnchor(IEnumerable<string?[]> rows, string fileName, out string source)
    {
        foreach (var cells in rows)
        {
            if (cells.Length < 2 || !IsStartTimeKey(cells[0]))
                continue;
            if (TryParseAbsoluteTime(cells[1], out var start))
            {
                source = "file";
                return start;
            }
        }

        if (TryParseFileNameDate(fileName, out var fromName))
        {
            source = "name";
            return fromName;
        }

        source = "today";
        return DateTime.Today;
    }

    // 元信息行的键：容忍 "Start Time" / "StartTime" / "start_time" 等写法
    private static bool IsStartTimeKey(string? text)
    {
        var key = (text ?? "").Trim().Replace(" ", "").Replace("_", "");
        return key.Equals("StartTime", StringComparison.OrdinalIgnoreCase);
    }

    // 文件名里的日期：heart_20260912_012857.csv → 2026-09-12 01:28:57；
    // 只写日期（…_20260912）也可用，时间部分缺省为 00:00:00
    private static bool TryParseFileNameDate(string fileName, out DateTime date)
    {
        date = default;
        var match = System.Text.RegularExpressions.Regex.Match(
            fileName, @"(20\d{2})[-_]?(\d{2})[-_]?(\d{2})(?:[-_ T]?(\d{2})[-_:]?(\d{2})[-_:]?(\d{2}))?");
        if (!match.Success)
            return false;

        var y = int.Parse(match.Groups[1].Value);
        var mo = int.Parse(match.Groups[2].Value);
        var d = int.Parse(match.Groups[3].Value);
        var h = match.Groups[4].Success ? int.Parse(match.Groups[4].Value) : 0;
        var mi = match.Groups[5].Success ? int.Parse(match.Groups[5].Value) : 0;
        var s = match.Groups[6].Success ? int.Parse(match.Groups[6].Value) : 0;
        if (mo is < 1 or > 12 || d < 1 || d > DateTime.DaysInMonth(y, mo)
            || h > 23 || mi > 59 || s > 59)
            return false;

        date = new DateTime(y, mo, d, h, mi, s);
        return true;
    }

    /// <summary>图表绘制的中性曲线描述：实时模式来自设备 / 记录服务，数据查看模式来自拖入的文件。</summary>
    private sealed record ChartSeries(string Name, List<HeartRateSample> Points)
    {
        /// <summary>
        /// 图例隐藏用的稳定键：实时模式为设备蓝牙地址，数据查看模式为文件里的列序号。
        /// 抽稀 / 时间过滤产生的副本会带上同一个键（FilterImported 用 with 复制），
        /// 因此整条曲线在图例与画面之间始终对得上。
        /// </summary>
        public string Key { get; init; } = "";

        /// <summary>曲线颜色。实时模式取自设备；数据查看模式可在图例圆点上改色，故可写。</summary>
        public Color Color { get; set; }

        /// <summary>
        /// 时间偏移（秒）：数据查看模式下把整条曲线向左平移，用于对齐两台设备的时间戳偏差。
        /// 绘制时每个点的时间减掉该值（见 FilterImported），实时模式恒为 0。
        /// </summary>
        public double ShiftSeconds { get; set; }
    }

    /// <summary>
    /// 一个实时图例芯片里需要就地刷新的部件：读数每秒在变，芯片本身不重建，
    /// 因此把可更新的元素持有下来（见 BuildLegendChip / ApplyLegendChip）。
    /// </summary>
    private sealed record LegendChip(
        Border Chip, Button Dot, TextBlock Name, TextBlock Value, Border Zone, TextBlock ZoneText);

    /// <summary>拖入 CSV / Excel 解析出的完整数据：整段时间范围 + 各设备历史样本。</summary>
    private sealed class ImportedData
    {
        public required string FileName { get; init; }
        public required List<ChartSeries> Series { get; init; }

        // 时间范围按当前偏移实时计算：曲线被左移后可能超出原始范围，
        // 用实时值才能让全程视图与回看滑块把偏移后的曲线完整包进来
        public double StartT => Series.Count == 0 ? 0 : Series.Min(s => s.Points[0].T - s.ShiftSeconds);
        public double EndT => Series.Count == 0 ? 0 : Series.Max(s => s.Points[^1].T - s.ShiftSeconds);
    }

    // ===== 本地化 =====

    public override void ApplyLocalization()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _suppress = true;
            var loc = LocalizationService.Instance;
            UpdateTitle();
            _editZonesButton.Content = loc.T("edit_zones");
            _editYAxisButton.Content = loc.T("edit_y_axis");
            _offsetButton.Content = loc.T("curve_offset");
            _maeButton.Content = loc.T("mae");
            _exitViewButton.Content = loc.T("view_exit");
            _timeWindowCaption.Text = loc.T("time_window");
            _maxHrCaption.Text = loc.T("max_hr");
            _zonesToggle.OnContent = loc.T("show_zones");
            _zonesToggle.OffContent = loc.T("hide_zones");

            string[] windowKeys = { "win_30", "win_1", "win_3", "win_5", "win_10", "win_all" };
            for (var i = 0; i < windowKeys.Length && i < _windowCombo.Items.Count; i++)
                ((ComboBoxItem)_windowCombo.Items[i]!).Content = loc.T(windowKeys[i]);
            // 按当前实际窗口回显，避免语言切换时显示被重置为默认值
            _windowCombo.SelectedIndex = _windowSeconds switch
            {
                30 => 0,
                60 => 1,
                180 => 2,
                300 => 3,
                0 => 5, // 全程
                _ => 4,
            };

            _emptyTitle.Text = loc.T("chart_empty_title");
            _emptySub.Text = loc.T("chart_empty_sub");
            _suppress = false;

            UpdateRecordUi();
            Refresh();
        });
    }
}
