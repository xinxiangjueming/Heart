using System.Collections.Specialized;
using System.ComponentModel;
using Heart.Models;
using Heart.Services;
using Heart.Ui;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Heart.Views;

public sealed class DevicesPage : PageBase
{
    private TextBlock _title = null!;
    private TextBlock _summary = null!;
    private Button _scanButton = null!;
    private TextBlock _emptyTitle = null!;
    private TextBlock _emptySub = null!;
    private StackPanel _emptyIconPanel = null!;
    private StackPanel _listPanel = null!;
    private readonly Dictionary<ulong, DeviceCard> _cards = new();

    public DevicesPage()
    {
        BuildUi();
        ApplyLocalization();

        // 页面被主窗口缓存复用，Loaded / Unloaded 会在每次导航时反复触发，订阅必须成对
        Loaded += (_, _) =>
        {
            App.Ble.ScanningChanged += OnScanningChanged;
            App.Ble.Devices.CollectionChanged += OnDevicesChanged;
            LocalizationService.Instance.LanguageChanged += ApplyLocalization;
        };
        Unloaded += (_, _) =>
        {
            App.Ble.ScanningChanged -= OnScanningChanged;
            App.Ble.Devices.CollectionChanged -= OnDevicesChanged;
            LocalizationService.Instance.LanguageChanged -= ApplyLocalization;
        };

        UpdateSummary();
    }

    private void BuildUi()
    {
        _title = Miuix.Title("");
        _summary = Miuix.Caption("");
        _scanButton = Miuix.PrimaryButton("");
        _scanButton.Click += OnScanClick;
        _scanButton.HorizontalAlignment = HorizontalAlignment.Left;
        _scanButton.Margin = new Thickness(0, 2, 0, 0);

        var listHost = new Grid();
        listHost.Children.Add(BuildListHost());

        var root = new Grid
        {
            RowDefinitions =
            {
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = new GridLength(1, GridUnitType.Star) },
            },
            RowSpacing = 10,
        };
        var titles = Miuix.Vertical(2, _title, _summary);
        Grid.SetRow(titles, 0);
        Grid.SetRow(_scanButton, 1);
        Grid.SetRow(listHost, 2);
        root.Children.Add(titles);
        root.Children.Add(_scanButton);
        root.Children.Add(listHost);

        Content = root;
    }

    private Grid BuildListHost()
    {
        _listPanel = new StackPanel { Spacing = 12 };
        var listScroll = new ScrollViewer
        {
            Content = _listPanel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };

        _emptyIconPanel = Miuix.Vertical(8,
            new TextBlock
            {
                Text = "\uE702",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 40,
                Foreground = Miuix.Brush("MiuixTextSecondary"),
                HorizontalAlignment = HorizontalAlignment.Center,
            });
        _emptyTitle = new TextBlock
        {
            FontSize = 16,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = Miuix.Brush("MiuixTextPrimary"),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _emptySub = Miuix.Caption("");
        _emptySub.HorizontalAlignment = HorizontalAlignment.Center;
        _emptySub.MaxWidth = 560;
        _emptyIconPanel.Children.Add(_emptyTitle);
        _emptyIconPanel.Children.Add(_emptySub);
        _emptyIconPanel.HorizontalAlignment = HorizontalAlignment.Center;
        _emptyIconPanel.VerticalAlignment = VerticalAlignment.Center;
        _emptyIconPanel.Visibility = Visibility.Collapsed;

        var listHost = new Grid();
        listHost.Children.Add(listScroll);
        listHost.Children.Add(_emptyIconPanel);
        return listHost;
    }

    private void OnScanningChanged() => DispatcherQueue.TryEnqueue(UpdateSummary);

    private void OnDevicesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (e.Action == NotifyCollectionChangedAction.Add)
            {
                foreach (var item in e.NewItems!.OfType<HeartRateDevice>())
                    AddCard(item);
            }
            else if (e.Action == NotifyCollectionChangedAction.Remove)
            {
                foreach (var item in e.OldItems!.OfType<HeartRateDevice>())
                    RemoveCard(item);
            }
            else if (e.Action == NotifyCollectionChangedAction.Reset)
            {
                foreach (var card in _cards.Values) DetachCard(card);
                _cards.Clear();
                _listPanel.Children.Clear();
            }
            UpdateSummary();
        });
    }

    private void AddCard(HeartRateDevice device)
    {
        var card = new DeviceCard(device);
        _cards[device.Address] = card;
        _listPanel.Children.Add(Miuix.Card(card));
    }

    private void RemoveCard(HeartRateDevice device)
    {
        if (_cards.Remove(device.Address, out var card))
        {
            DetachCard(card);
            _listPanel.Children.Remove(card);
        }
    }

    private static void DetachCard(DeviceCard card) => card.Detach();

    private void UpdateSummary()
    {
        var ble = App.Ble;
        var loc = LocalizationService.Instance;
        int count = ble.Devices.Count;

        _scanButton.Content = ble.IsScanning ? loc.T("stop_scan") : loc.T("scan_devices");
        // 停止扫描态用危险红（与断开连接按钮一致），白色前景保证对比度
        _scanButton.Background = ble.IsScanning ? Miuix.Brush("MiuixDanger") : Miuix.Brush("MiuixAccent");
        _scanButton.Foreground = Miuix.Brush("MiuixAccentForeground");

        if (!string.IsNullOrEmpty(ble.ScanError))
            _summary.Text = ble.ScanError;
        else if (ble.IsScanning)
            _summary.Text = loc.T("hint_scanning", count);
        else if (count > 0)
            _summary.Text = loc.T("hint_found", count);
        else
            _summary.Text = loc.T("hint_scan");

        _emptyIconPanel.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _listPanel.Visibility = count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>主题切换时刷新扫描按钮等状态色（由 MainWindow 回调，UI 线程）。</summary>
    public override void ApplyTheme() => UpdateSummary();

    private void OnScanClick(object sender, RoutedEventArgs e)
    {
        if (App.Ble.IsScanning)
            App.Ble.StopScan();
        else
            App.Ble.StartScan();
        UpdateSummary();
    }

    public override void ApplyLocalization()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var loc = LocalizationService.Instance;
            _title.Text = loc.T("devices_title");
            _emptyTitle.Text = loc.T("empty_title");
            _emptySub.Text = loc.T("empty_sub");
            UpdateSummary();

            foreach (var card in _cards.Values)
                card.ApplyLocalization();
        });
    }

    /// <summary>单张设备卡片：手动绑定设备属性，语言 / 状态变化时刷新。（外层由 Miuix.Card 提供卡片外观）</summary>
    private sealed class DeviceCard : Grid
    {
        private readonly HeartRateDevice _device;
        private readonly TextBlock _name = null!;
        private readonly TextBlock _address = null!;
        private readonly TextBlock _message = null!;
        private readonly TextBlock _heartRate = null!;
        private readonly TextBlock _battery = null!;
        private readonly TextBlock _rr = null!;
        private readonly FrameworkElement _rrBlock = null!;
        private readonly TextBlock _hrCaption = null!;
        private readonly TextBlock _batteryCaption = null!;
        private readonly TextBlock _rrCaption = null!;
        private readonly Button _connectButton = null!;
        private readonly Button _dot = null!;
        private bool _attached;

        public DeviceCard(HeartRateDevice device)
        {
            _device = device;

            _name = new TextBlock
            {
                FontSize = 17,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = Miuix.Brush("MiuixTextPrimary"),
            };
            _address = Miuix.Caption("");
            _message = new TextBlock
            {
                FontSize = 12,
                Foreground = Miuix.Brush("MiuixDanger"),
                TextWrapping = TextWrapping.Wrap,
                Visibility = Visibility.Collapsed,
            };

            _heartRate = new TextBlock
            {
                FontSize = 20,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                FontFamily = Miuix.MonoFont,
                // 固定列宽内居中：数字始终居中于上方标签，位数增减不引起晃动
                TextAlignment = TextAlignment.Center,
                Foreground = Miuix.Brush("MiuixTextPrimary"),
            };
            _battery = new TextBlock
            {
                FontSize = 20,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                FontFamily = Miuix.MonoFont,
                TextAlignment = TextAlignment.Center,
                Foreground = Miuix.Brush("MiuixTextPrimary"),
            };
            _rr = new TextBlock
            {
                FontSize = 20,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                FontFamily = Miuix.MonoFont,
                TextAlignment = TextAlignment.Right,
                TextWrapping = TextWrapping.Wrap,
                MaxLines = 2,
                Foreground = Miuix.Brush("MiuixTextPrimary"),
            };

            _hrCaption = Miuix.Caption("");
            _batteryCaption = Miuix.Caption("");
            _rrCaption = Miuix.Caption("");
            // 心率 / 电量标签在固定列宽内居中，数字才能居中于标签正下方
            _hrCaption.TextAlignment = TextAlignment.Center;
            _batteryCaption.TextAlignment = TextAlignment.Center;

            var info = Miuix.Vertical(3,
                _name,
                Miuix.Horizontal(10, _address),
                _message);
            info.VerticalAlignment = VerticalAlignment.Center;

            var hrBlock = Miuix.Vertical(0, _hrCaption, _heartRate);
            hrBlock.MinWidth = Miuix.MeasureValueWidth("888");
            var batteryBlock = Miuix.Vertical(0, _batteryCaption, _battery);
            batteryBlock.MinWidth = Miuix.MeasureValueWidth("888");
            _rrBlock = Miuix.Vertical(0, _rrCaption, _rr);
            _rrBlock.MinWidth = Miuix.MeasureValueWidth("8888  ms");

            _connectButton = Miuix.PrimaryButton("");
            _connectButton.MinWidth = 88;
            _connectButton.VerticalAlignment = VerticalAlignment.Center;
            _connectButton.Click += OnConnectClick;

            _dot = Miuix.DotButton(device.DotBrush, (_, _) => Miuix.ShowColorMenu(_dot, device));
            _dot.Width = 36;
            _dot.Height = 36;
            _dot.CornerRadius = new CornerRadius(18);
            _dot.BorderThickness = new Thickness(2);
            _dot.BorderBrush = Miuix.Brush("MiuixCardBorder");
            _dot.VerticalAlignment = VerticalAlignment.Center;

            var stats = Miuix.Horizontal(30, hrBlock, batteryBlock, _rrBlock);
            stats.VerticalAlignment = VerticalAlignment.Center;

            var grid = new Grid
            {
                ColumnSpacing = 18,
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = GridLength.Auto },
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                    new ColumnDefinition { Width = GridLength.Auto },
                    new ColumnDefinition { Width = GridLength.Auto },
                }
            };
            Grid.SetColumn(_dot, 0);
            Grid.SetColumn(info, 1);
            Grid.SetColumn(stats, 2);
            Grid.SetColumn(_connectButton, 3);
            grid.Children.Add(_dot);
            grid.Children.Add(info);
            grid.Children.Add(stats);
            grid.Children.Add(_connectButton);

            Children.Add(grid);

            Attach();
            Update();
        }

        private void Attach()
        {
            if (_attached) return;
            _attached = true;
            _device.PropertyChanged += OnDevicePropertyChanged;
        }

        public void Detach()
        {
            _attached = false;
            _device.PropertyChanged -= OnDevicePropertyChanged;
        }

        private void OnDevicePropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            DispatcherQueue.TryEnqueue(Update);
        }

        private void OnConnectClick(object sender, RoutedEventArgs e)
        {
            if (_device.IsConnected)
                App.Ble.Disconnect(_device);
            else
                _ = App.Ble.ConnectAsync(_device);
        }

        public void ApplyLocalization() => DispatcherQueue.TryEnqueue(Update);

        private void Update()
        {
            var loc = LocalizationService.Instance;

            _name.Text = _device.DisplayName;
            _address.Text = _device.AddressText;
            _message.Text = _device.StatusMessage;
            _message.Visibility = string.IsNullOrEmpty(_device.StatusMessage)
                ? Visibility.Collapsed
                : Visibility.Visible;

            // 数值只显示数字（bpm / % 不再展示），居中于上方标签
            _heartRate.Text = _device.HeartRateText;
            _battery.Text = _device.BatteryText;
            _rr.Text = _device.RrText;
            // 不支持 RR 间期的设备不会上报数据（RrText 保持占位符 "--"），整列隐藏不占位
            _rrBlock.Visibility = _device.RrText == "--"
                ? Visibility.Collapsed
                : Visibility.Visible;

            _hrCaption.Text = loc.T("label_hr");
            _batteryCaption.Text = loc.T("label_battery");
            _rrCaption.Text = loc.T("label_rr");

            _connectButton.Content = _device.ConnectText;
            _connectButton.IsEnabled = _device.CanToggle;
            _connectButton.Background = _device.IsConnected
                ? Miuix.Brush("MiuixDanger")
                : Miuix.Brush("MiuixAccent");
            _dot.Background = _device.DotBrush;
        }
    }
}
