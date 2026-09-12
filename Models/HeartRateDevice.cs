using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Heart.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Heart.Models
{
    public readonly record struct HeartRateSample(double T, int Hr);

    /// <summary>
    /// 一台蓝牙心率设备（以蓝牙地址为唯一标识）。
    /// 既是设备页列表项，也是图表页的数据源。
    /// </summary>
    public partial class HeartRateDevice : ObservableObject
    {
        private const double MaxWindowSeconds = 700; // 内存中最多保留 ~10 分钟样本

        private static readonly SolidColorBrush s_greenBrush =
            new(Color.FromArgb(0xFF, 0x30, 0xC8, 0x5A));
        private static readonly SolidColorBrush s_grayBrush =
            new(Color.FromArgb(0xFF, 0x8C, 0x8C, 0x93));
        private static readonly SolidColorBrush s_primaryBrush =
            new(Color.FromArgb(0xFF, 0x34, 0x82, 0xFF));
        private static readonly SolidColorBrush s_dangerBrush =
            new(Color.FromArgb(0xFF, 0xFA, 0x51, 0x51));

        private readonly object _sampleGate = new();
        private readonly List<HeartRateSample> _samples = new();

        public HeartRateDevice(ulong address)
        {
            Address = address;
            AddressText = FormatAddress(address);
            ChartColor = SettingsStore.TryGetColor(AddressText, out var hex)
                ? Palette.FromHex(hex)
                : Palette.Next();
            _dotBrush = new SolidColorBrush(ChartColor);
        }

        public ulong Address { get; }
        public string AddressText { get; }
        public bool InList { get; set; }

        [ObservableProperty] private string _name = "";
        [ObservableProperty] private int _heartRate;
        [ObservableProperty] private int? _battery;
        [ObservableProperty] private string _rrText = "--";
        [ObservableProperty] private bool _isConnected;
        [ObservableProperty] private bool _isConnecting;
        [ObservableProperty] private string _statusMessage = "";
        [ObservableProperty] private bool _hasData;
        [ObservableProperty] private Color _chartColor;

        private SolidColorBrush _dotBrush;
        public SolidColorBrush DotBrush => _dotBrush;

        partial void OnChartColorChanged(Color value)
        {
            _dotBrush = new SolidColorBrush(value);
            OnPropertyChanged(nameof(DotBrush));
            SettingsStore.SetColor(AddressText, Palette.ToHex(value));
        }

        partial void OnNameChanged(string value) => OnPropertyChanged(nameof(DisplayName));

        partial void OnBatteryChanged(int? value) => OnPropertyChanged(nameof(BatteryText));

        partial void OnHeartRateChanged(int value)
        {
            OnPropertyChanged(nameof(HeartRateText));
            OnPropertyChanged(nameof(ZoneName));
            OnPropertyChanged(nameof(ZoneVisibility));
            OnPropertyChanged(nameof(ZoneChipBrush));
            OnPropertyChanged(nameof(ZoneTextBrush));
        }

        partial void OnIsConnectedChanged(bool value) => RaiseStatusChanged();
        partial void OnIsConnectingChanged(bool value) => RaiseStatusChanged();

        partial void OnStatusMessageChanged(string value) => OnPropertyChanged(nameof(StatusVisibility));

        private void RaiseStatusChanged()
        {
            OnPropertyChanged(nameof(ConnectText));
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(StatusBrush));
            OnPropertyChanged(nameof(ConnectBrush));
            OnPropertyChanged(nameof(CanToggle));
        }

        public void SetChartColor(Color color) => ChartColor = color;

        // ===== 展示属性 =====

        /// <summary>设备名（广播名为空时显示本地化的占位名称）。</summary>
        public string DisplayName => string.IsNullOrWhiteSpace(Name) ? T("unknown_device") : Name;

        public string HeartRateText => HeartRate > 0 ? HeartRate.ToString() : "--";
        // 卡片数值只显示数字（单位不展示），百分比号不再拼接
        public string BatteryText => Battery is int b ? b.ToString() : "--";
        public bool CanToggle => !IsConnecting;
        public string ConnectText => IsConnected ? T("disconnect") : (IsConnecting ? T("status_connecting") : T("connect"));
        public string StatusText => IsConnected ? T("status_connected") : (IsConnecting ? T("status_connecting") : T("status_disconnected"));
        public Visibility StatusVisibility =>
            string.IsNullOrEmpty(StatusMessage) ? Visibility.Collapsed : Visibility.Visible;
        public SolidColorBrush StatusBrush => IsConnected ? s_greenBrush : s_grayBrush;
        public SolidColorBrush ConnectBrush => IsConnected ? s_dangerBrush : s_primaryBrush;

        // 数据卡片标签（跟随语言切换）
        public string LabelHeartRate => T("label_hr");
        public string UnitBpm => T("bpm");
        public string LabelBattery => T("label_battery");
        public string LabelRR => T("label_rr");

        private static string T(string key) => LocalizationService.Instance.T(key);

        /// <summary>语言切换后调用：重新通知所有本地化属性。</summary>
        public void RefreshLocalized()
        {
            OnPropertyChanged(nameof(DisplayName));
            OnPropertyChanged(nameof(ConnectText));
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(StatusMessage));
            OnPropertyChanged(nameof(LabelHeartRate));
            OnPropertyChanged(nameof(UnitBpm));
            OnPropertyChanged(nameof(LabelBattery));
            OnPropertyChanged(nameof(LabelRR));
            OnPropertyChanged(nameof(ZoneName));
            OnPropertyChanged(nameof(ZoneVisibility));
            OnPropertyChanged(nameof(ZoneChipBrush));
            OnPropertyChanged(nameof(ZoneTextBrush));
        }

        public string ZoneName
        {
            get
            {
                int idx = HeartRateZones.IndexOf(HeartRate, HeartRateZones.MaxHeartRate);
                return idx < 0 ? "" : $"Z{idx + 1} {HeartRateZones.NameOf(idx)}";
            }
        }

        public Visibility ZoneVisibility =>
            string.IsNullOrEmpty(ZoneName) ? Visibility.Collapsed : Visibility.Visible;

        public SolidColorBrush ZoneChipBrush
        {
            get
            {
                int idx = HeartRateZones.IndexOf(HeartRate, HeartRateZones.MaxHeartRate);
                var c = idx < 0 ? Color.FromArgb(0xFF, 0x80, 0x80, 0x80) : HeartRateZones.ColorOf(idx);
                return new SolidColorBrush(Color.FromArgb(36, c.R, c.G, c.B));
            }
        }

        public SolidColorBrush ZoneTextBrush
        {
            get
            {
                int idx = HeartRateZones.IndexOf(HeartRate, HeartRateZones.MaxHeartRate);
                var c = idx < 0 ? Color.FromArgb(0xFF, 0x80, 0x80, 0x80) : HeartRateZones.ColorOf(idx);
                return new SolidColorBrush(c);
            }
        }

        // ===== 心率样本（供图表绘制） =====

        public void RecordSample(double t, int hr)
        {
            lock (_sampleGate)
            {
                _samples.Add(new HeartRateSample(t, hr));
                TrimLocked(t);
            }
        }

        /// <summary>设备未上报新数据时，用最近一次心率补点，保持曲线连续。</summary>
        public void HoldSample(double now)
        {
            if (HeartRate <= 0)
                return;
            lock (_sampleGate)
            {
                if (_samples.Count == 0 || now - _samples[^1].T >= 0.9)
                {
                    _samples.Add(new HeartRateSample(now, HeartRate));
                    TrimLocked(now);
                }
            }
        }

    /// <summary>缓冲中最早一条样本的时间（无样本时为 null）：图表回看滑块的可用范围左端。</summary>
    public double? EarliestSampleT()
    {
        lock (_sampleGate)
            return _samples.Count == 0 ? null : _samples[0].T;
    }

    /// <summary>取窗口内的样本点（线程安全）。</summary>
    public HeartRateSample[] Snapshot(double fromT, double toT)
    {
        lock (_sampleGate)
        {
            return _samples
                .Where(s => s.T >= fromT && s.T <= toT)
                .ToArray();
        }
    }

    /// <summary>清空样本缓冲：开始记录时调用，保证曲线从记录起点开始绘制。</summary>
    public void ClearSamples()
    {
        lock (_sampleGate)
        {
            _samples.Clear();
        }
    }

        /// <summary>UI 线程调用：更新最新一次测量结果。</summary>
        public void ApplyMeasurement(int hr, string rrText)
        {
            HasData = true;
            HeartRateUpdateCount++;
            HeartRate = hr;
            RrText = string.IsNullOrEmpty(rrText) ? "--" : rrText;
        }

        /// <summary>累计收到心率上报的次数：RecordingService 用它区分「这一秒有新上报」还是「沿用旧值」。</summary>
        public ulong HeartRateUpdateCount { get; private set; }

        private void TrimLocked(double t)
        {
            while (_samples.Count > 0 && _samples[0].T < t - MaxWindowSeconds)
                _samples.RemoveAt(0);
            while (_samples.Count > 1800)
                _samples.RemoveAt(0);
        }

        private static string FormatAddress(ulong a) =>
            string.Format("{0:X2}:{1:X2}:{2:X2}:{3:X2}:{4:X2}:{5:X2}",
                (a >> 40) & 0xFF, (a >> 32) & 0xFF, (a >> 24) & 0xFF,
                (a >> 16) & 0xFF, (a >> 8) & 0xFF, a & 0xFF);
    }
}
