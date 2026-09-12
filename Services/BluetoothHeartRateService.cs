using System.Collections.ObjectModel;
using Heart.Models;
using Microsoft.UI.Dispatching;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Radios;
using Windows.Storage.Streams;

namespace Heart.Services
{
    /// <summary>
    /// BLE 心率服务：扫描广播心率服务 (0x180D) 的设备，
    /// 通过 GATT 连接获取心率通知 (0x2A37：心率 + RR 间期) 与电量 (0x2A19)。
    /// </summary>
    public sealed class BluetoothHeartRateService : IDisposable
    {
        private static string L(string key, params object[] args) => LocalizationService.Instance.T(key, args);

        private readonly object _gate = new();
        private readonly Dictionary<ulong, HeartRateDevice> _known = new();
        private readonly Dictionary<ulong, DeviceLink> _links = new();
        private BluetoothLEAdvertisementWatcher? _watcher;
        private bool _startPending;
        private DispatcherQueueTimer? _scanTimer;
        private bool _resumeScanAfterConnect; // 连接前扫描在跑，连接落定后自动续上
        private int _connectsInFlight;        // 正在建 GATT 连接的数量，归零后才续扫

        /// <summary>单次扫描时长，到时自动停止扫描。</summary>
        private static readonly TimeSpan ScanDuration = TimeSpan.FromSeconds(10);

        internal DispatcherQueue Dq { get; }

        public ObservableCollection<HeartRateDevice> Devices { get; } = new();

        public bool IsScanning { get; private set; }
        public string ScanError { get; private set; } = "";
        public event Action? ScanningChanged;

        public BluetoothHeartRateService(DispatcherQueue dq) => Dq = dq;

        // ===== 扫描 =====

        public void StartScan()
        {
            // 用户主动开扫时，取消"连接后自动续扫"的挂起标记，避免落定后又凭空重启一轮
            _resumeScanAfterConnect = false;
            lock (_gate)
            {
                // IsScanning 要到 watcher 真正启动后才置位，用 _startPending 挡住连点造成的重复 watcher
                if (IsScanning || _startPending)
                    return;
                _startPending = true;
            }

            // 蓝牙开关状态检查（仅诊断，不影响扫描过滤规则）
            _ = CheckRadioAndStartAsync();
        }

        private async Task CheckRadioAndStartAsync()
        {
            try
            {
                var radios = await Radio.GetRadiosAsync();
                var bt = radios.FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);
                if (bt is null)
                {
                    AbortStart(L("err_no_radio"));
                    return;
                }
                if (bt.State != RadioState.On)
                {
                    AbortStart(L("err_radio_off"));
                    return;
                }
            }
            catch
            {
                // 拿不到蓝牙状态时不拦截，继续尝试扫描
            }

            try
            {
                var watcher = new BluetoothLEAdvertisementWatcher
                {
                    ScanningMode = BluetoothLEScanningMode.Active,
                };
                // 原生心率服务过滤（与参考实现一致，减少事件流量）
                watcher.AdvertisementFilter.Advertisement.ServiceUuids.Add(GattServiceUuids.HeartRate);
                watcher.Received += OnAdvertisementReceived;
                watcher.Stopped += OnWatcherStopped;
                watcher.Start();
                _watcher = watcher;
                IsScanning = true;
                ScanError = "";
                StartScanAutoStopTimer();
            }
            catch (Exception ex)
            {
                IsScanning = false;
                ScanError = L("hint_scan_error", ex.Message);
            }
            finally
            {
                lock (_gate)
                {
                    _startPending = false;
                }
            }
            ScanningChanged?.Invoke();
        }

        /// <summary>启动前的诊断检查未通过：清除 pending 标记并给出提示。</summary>
        private void AbortStart(string error)
        {
            ScanError = error;
            lock (_gate)
            {
                _startPending = false;
            }
            ScanningChanged?.Invoke();
        }

        public void StopScan()
        {
            StopScanAutoStopTimer();
            BluetoothLEAdvertisementWatcher? watcher;
            lock (_gate)
            {
                watcher = _watcher;
                _watcher = null;
                IsScanning = false;
                _startPending = false;
            }
            if (watcher != null)
            {
                try
                {
                    watcher.Received -= OnAdvertisementReceived;
                    watcher.Stop();
                }
                catch
                {
                    // 停止失败忽略
                }
            }
            ScanningChanged?.Invoke();
        }

        /// <summary>启动一次性自动停止计时器：扫描满 10 秒后调用 StopScan（手动停止 / 重新扫描时会被重置）。</summary>
        private void StartScanAutoStopTimer()
        {
            StopScanAutoStopTimer();
            var timer = Dq.CreateTimer();
            timer.Interval = ScanDuration;
            timer.IsRepeating = false;
            timer.Tick += (_, _) =>
            {
                _scanTimer = null;
                StopScan();
            };
            _scanTimer = timer;
            timer.Start();
        }

        private void StopScanAutoStopTimer()
        {
            if (_scanTimer is not { } timer)
                return;
            _scanTimer = null;
            // OnWatcherStopped 可能在蓝牙后台线程触发，Stop 越线程失败时静默忽略
            try { timer.Stop(); }
            catch { }
        }

        private void OnWatcherStopped(BluetoothLEAdvertisementWatcher sender,
            BluetoothLEAdvertisementWatcherStoppedEventArgs args)
        {
            IsScanning = false;
            StopScanAutoStopTimer();
            if (args.Error != BluetoothError.Success)
                ScanError = L("hint_scan_stopped", args.Error);
            ScanningChanged?.Invoke();
        }

        private void OnAdvertisementReceived(BluetoothLEAdvertisementWatcher sender,
            BluetoothLEAdvertisementReceivedEventArgs args)
        {
            // 心率服务过滤由 watcher 原生 AdvertisementFilter 完成
            string? name = args.Advertisement.LocalName;
            ulong address = args.BluetoothAddress;

            // 本回调运行在蓝牙的后台线程上，而 HeartRateDevice 构造函数会创建
            // SolidColorBrush（DependencyObject，仅限 UI 线程），因此整个建档
            // 流程必须调度到 UI 线程执行，否则 COMException 会被事件边界吞掉，
            // 设备永远进不了列表
            Dq.TryEnqueue(() =>
            {
                if (!_known.TryGetValue(address, out var device))
                {
                    device = new HeartRateDevice(address);
                    _known[address] = device;
                }

                if (!string.IsNullOrWhiteSpace(name) && device.Name != name)
                    device.Name = name;
                if (!device.InList)
                {
                    device.InList = true;
                    Devices.Add(device);
                }
            });
        }

        // ===== 连接 / 断开 =====

        public async Task ConnectAsync(HeartRateDevice device)
        {
            lock (_gate)
            {
                if (_links.ContainsKey(device.Address) || device.IsConnecting)
                    return;
            }

            // 连接前停止扫描释放蓝牙资源（参考实现的关键实践，避免扫描干扰 GATT 建连）；
            // 是否在扫描先记下来，连接落定（无论成败）后自动续上，连多台设备不必手动重扫
            if (IsScanning)
            {
                _resumeScanAfterConnect = true;
                StopScan();
            }
            _connectsInFlight++;

            device.IsConnecting = true;
            device.StatusMessage = "";
            DeviceLink? link = null;
            try
            {
                var bt = await BluetoothLEDevice.FromBluetoothAddressAsync(device.Address);
                if (bt is null)
                    throw new InvalidOperationException(L("err_not_found"));

                var session = await GattSession.FromDeviceIdAsync(bt.BluetoothDeviceId);
                if (session is null)
                    throw new InvalidOperationException(L("err_gatt"));
                session.MaintainConnection = true; // 维持连接

                // 心率服务 0x180D
                var hrServiceResult = await bt.GetGattServicesForUuidAsync(
                    GattServiceUuids.HeartRate, BluetoothCacheMode.Uncached);
                var hrService = hrServiceResult.Services.FirstOrDefault()
                    ?? throw new InvalidOperationException(L("err_no_hr_service"));
                var hrCharResult = await hrService.GetCharacteristicsForUuidAsync(
                    GattCharacteristicUuids.HeartRateMeasurement, BluetoothCacheMode.Uncached);
                var hrChar = hrCharResult.Characteristics.FirstOrDefault()
                    ?? throw new InvalidOperationException(L("err_no_hr_char"));

                // 电量服务 0x180F（可选）
                GattCharacteristic? batChar = null;
                try
                {
                    var batServiceResult = await bt.GetGattServicesForUuidAsync(
                        GattServiceUuids.Battery, BluetoothCacheMode.Uncached);
                    var batService = batServiceResult.Services.FirstOrDefault();
                    if (batService != null)
                    {
                        var batCharResult = await batService.GetCharacteristicsForUuidAsync(
                            GattCharacteristicUuids.BatteryLevel, BluetoothCacheMode.Uncached);
                        batChar = batCharResult.Characteristics.FirstOrDefault();
                    }
                }
                catch
                {
                    // 电量是可选能力，读不到就显示 --
                }

                lock (_gate)
                {
                    if (_links.ContainsKey(device.Address))
                        throw new InvalidOperationException(L("err_already"));
                    link = new DeviceLink(device, bt, session, hrChar, batChar, this);
                    _links[device.Address] = link;
                }

                bt.ConnectionStatusChanged += link.OnConnectionStatusChanged;
                hrChar.ValueChanged += link.OnHeartRateValueChanged;

                var status = await hrChar.WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.Notify);
                if (status != GattCommunicationStatus.Success)
                    throw new InvalidOperationException(L("err_subscribe", status));

                if (batChar != null)
                {
                    try
                    {
                        var read = await batChar.ReadValueAsync();
                        if (read.Status == GattCommunicationStatus.Success && read.Value.Length > 0)
                            SetBattery(device, DeviceLink.ToBytes(read.Value)[0]);

                        batChar.ValueChanged += link.OnBatteryValueChanged;
                        await batChar.WriteClientCharacteristicConfigurationDescriptorAsync(
                            GattClientCharacteristicConfigurationDescriptorValue.Notify);
                    }
                    catch
                    {
                        // 电量订阅失败不影响心率
                    }
                }

                Dq.TryEnqueue(() =>
                {
                    if (_links.ContainsKey(device.Address))
                        device.IsConnected = true; // GATT 订阅已成功；ConnectionStatus 此刻可能尚未同步为 Connected
                });
            }
            catch (Exception ex)
            {
                Disconnect(device);
                Dq.TryEnqueue(() => device.StatusMessage = ex.Message);
            }
            finally
            {
                device.IsConnecting = false;
                _ = link; // 生命周期由 _links 管理
                _connectsInFlight--;
                TryResumeScan();
            }
        }

        /// <summary>连接落定且没有其他连接在建时，把连接前暂停的扫描续上（重新计满 10 秒自动停止）。</summary>
        private void TryResumeScan()
        {
            if (!_resumeScanAfterConnect || _connectsInFlight > 0)
                return;
            _resumeScanAfterConnect = false;
            StartScan();
        }

        /// <summary>断开并释放该设备的所有 GATT 资源。</summary>
        public void Disconnect(HeartRateDevice device)
        {
            DeviceLink? link;
            lock (_gate)
            {
                _links.Remove(device.Address, out link);
            }
            if (link != null)
                Task.Run(link.Dispose);
            Dq.TryEnqueue(() =>
            {
                device.IsConnected = false;
            });
        }

        internal void SetBattery(HeartRateDevice device, byte value)
        {
            var percent = Math.Min(100, (int)value);
            Dq.TryEnqueue(() => device.Battery = percent);
        }

        public void Dispose()
        {
            StopScan();
            DeviceLink[] links;
            lock (_gate)
            {
                links = _links.Values.ToArray();
                _links.Clear();
            }
            foreach (var link in links)
                link.Dispose();
        }
    }

    /// <summary>一台设备的活动 GATT 连接与订阅。</summary>
    internal sealed class DeviceLink : IDisposable
    {
        private readonly HeartRateDevice _device;
        private readonly BluetoothHeartRateService _owner;
        private readonly BluetoothLEDevice _bt;
        private readonly GattSession _session;
        private readonly GattCharacteristic _hrChar;
        private readonly GattCharacteristic? _batChar;
        private int _disposed;

        public DeviceLink(HeartRateDevice device, BluetoothLEDevice bt, GattSession session,
            GattCharacteristic hrChar, GattCharacteristic? batChar, BluetoothHeartRateService owner)
        {
            _device = device;
            _bt = bt;
            _session = session;
            _hrChar = hrChar;
            _batChar = batChar;
            _owner = owner;
        }

        public void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
        {
            if (_bt.ConnectionStatus == BluetoothConnectionStatus.Connected)
                return;
            _owner.Dq.TryEnqueue(() =>
            {
                _device.StatusMessage = LocalizationService.Instance.T("status_link_lost");
                _owner.Disconnect(_device);
            });
        }

        public void OnHeartRateValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
        {
            // 标准心率测量格式：flags(1B) + 心率(1/2B) + [能耗(2B)] + [RR 间期(N×2B, 单位 1/1024 s)]
            var data = ToBytes(args.CharacteristicValue);
            if (data.Length < 2)
                return;

            byte flags = data[0];
            int index, hr;
            if ((flags & 0x01) != 0)
            {
                hr = data[1] | (data.Length > 2 ? data[2] << 8 : 0);
                index = 3;
            }
            else
            {
                hr = data[1];
                index = 2;
            }
            if ((flags & 0x08) != 0)
                index += 2; // 跳过能耗字段

            var rrList = new List<double>();
            if ((flags & 0x10) != 0)
            {
                while (index + 1 < data.Length)
                {
                    ushort raw = (ushort)(data[index] | (data[index + 1] << 8));
                    index += 2;
                    // RR 间期单位 1/1024 秒；过滤明显异常的值
                    var rrMs = Math.Round(raw * 1000.0 / 1024.0);
                    if (rrMs is >= 300 and <= 2000)
                        rrList.Add(rrMs);
                }
            }

            if (hr <= 0)
                return;

            _device.RecordSample(NowSeconds(), hr);
            string rrText = rrList.Count > 0
                ? string.Join("  ", rrList.Take(8).Select(v => $"{v:0}")) + " ms"
                : "";
            _owner.Dq.TryEnqueue(() => _device.ApplyMeasurement(hr, rrText));
        }

        public void OnBatteryValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
        {
            var data = ToBytes(args.CharacteristicValue);
            if (data.Length == 0)
                return;
            _owner.SetBattery(_device, data[0]);
        }

        internal static byte[] ToBytes(IBuffer buffer)
        {
            var reader = DataReader.FromBuffer(buffer);
            var bytes = new byte[buffer.Length];
            reader.ReadBytes(bytes);
            return bytes;
        }

        private static double NowSeconds() =>
            DateTimeOffset.UtcNow.Subtract(DateTimeOffset.UnixEpoch).TotalSeconds;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            try { _hrChar.ValueChanged -= OnHeartRateValueChanged; } catch { }
            if (_batChar != null) { try { _batChar.ValueChanged -= OnBatteryValueChanged; } catch { } }
            try
            {
                // 显式取消订阅通知，确保设备侧真正断开
                _hrChar.WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.None);
            }
            catch { }
            if (_batChar != null)
            {
                try
                {
                    _batChar.WriteClientCharacteristicConfigurationDescriptorAsync(
                        GattClientCharacteristicConfigurationDescriptorValue.None);
                }
                catch { }
            }
            try { _bt.ConnectionStatusChanged -= OnConnectionStatusChanged; } catch { }
            try { _session.MaintainConnection = false; } catch { }
            try { _session.Dispose(); } catch { }
            try { _bt.Dispose(); } catch { }
        }
    }
}
