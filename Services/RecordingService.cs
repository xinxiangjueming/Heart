using System.Globalization;
using System.Text;
using Heart.Models;
using Microsoft.UI.Dispatching;

namespace Heart.Services;

/// <summary>
/// 录制服务：每秒采样一次所有已连接设备的心率，
/// 生成 CSV（第一列时间，其后各列为设备心率的英文表头，末尾附英文 Summary 统计段）。
/// </summary>
public sealed class RecordingService
{
    public static RecordingService Instance { get; } = new();

    /// <summary>开始 / 停止时触发（UI 线程）。</summary>
    public event Action? RecordingChanged;

    public sealed class Row
    {
        public DateTime Time { get; init; }
        public Dictionary<ulong, int> Values { get; } = new();
        /// <summary>该行各设备的电量百分比（设备没有电量能力时缺项），供 Summary 段的 Start / End Battery 使用。</summary>
        public Dictionary<ulong, int> Batteries { get; } = new();
    }

    public sealed class Column
    {
        public ulong Address { get; init; }
        public string Name { get; set; } = "";
    }

    public bool IsRecording { get; private set; }
    public DateTime StartedAt { get; private set; }
    public List<Row> Rows { get; } = new();
    public List<Column> Columns { get; } = new();

    private DispatcherQueueTimer? _timer;

    /// <summary>每个设备上次采样时见到的上报计数，用于判断这一秒是否有新数据。</summary>
    private readonly Dictionary<ulong, ulong> _lastHrVersions = new();

    public void Start()
    {
        if (IsRecording)
            return;
        Rows.Clear();
        Columns.Clear();
        _lastHrVersions.Clear();
        // 记录起点前的旧值不算数据：先把各设备当前计数记为基线
        foreach (var device in App.Ble.Devices)
            _lastHrVersions[device.Address] = device.HeartRateUpdateCount;
        IsRecording = true;
        StartedAt = DateTime.Now;

        _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += (_, _) => Sample();
        _timer.Start();

        Sample();
        RecordingChanged?.Invoke();
    }

    public void Stop()
    {
        if (!IsRecording)
            return;
        IsRecording = false;
        _timer?.Stop();
        _timer = null;
        RecordingChanged?.Invoke();
    }

    private void Sample()
    {
        var row = new Row { Time = DateTime.Now };
        foreach (var device in App.Ble.Devices)
        {
            if (!device.IsConnected || device.HeartRate <= 0)
                continue;
            // 设备这一秒没有新上报时（如 5s 一报的设备中间几秒）留空，不沿用上一秒的值
            if (!_lastHrVersions.TryGetValue(device.Address, out var seen))
            {
                _lastHrVersions[device.Address] = device.HeartRateUpdateCount;
                continue; // 记录中途才出现的设备：先记基线，不计入开始记录前的旧值
            }
            if (seen == device.HeartRateUpdateCount)
                continue;
            _lastHrVersions[device.Address] = device.HeartRateUpdateCount;

            var column = Columns.FirstOrDefault(c => c.Address == device.Address);
            if (column is null)
                Columns.Add(new Column { Address = device.Address, Name = device.DisplayName });
            else if (column.Name != device.DisplayName)
                column.Name = device.DisplayName; // 设备改名后表头跟随更新
            row.Values[device.Address] = device.HeartRate;
            // 电量随该次上报一并记录：导出时才有记录首末的读数可写进 Summary
            if (device.Battery is int battery)
                row.Batteries[device.Address] = battery;
        }
        Rows.Add(row);
    }

    /// <summary>生成 CSV 文本：全英文内容，逗号分隔，CRLF 换行。</summary>
    public string BuildCsv()
    {
        // 列名 = 设备名；含非 ASCII 字符或非法字符时替换为 Device_N
        var headerNames = new List<string>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var columnIndex = new Dictionary<ulong, int>();
        foreach (var column in Columns)
        {
            headerNames.Add(SanitizeName(column.Name, used, headerNames.Count + 1));
            columnIndex[column.Address] = headerNames.Count - 1;
        }

        var sb = new StringBuilder();
        sb.Append("Time");
        foreach (var name in headerNames)
            sb.Append(',').Append(name);
        sb.Append("\r\n");

        foreach (var row in Rows)
        {
            sb.Append(row.Time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            var cells = new string?[headerNames.Count];
            foreach (var pair in row.Values)
            {
                if (columnIndex.TryGetValue(pair.Key, out var index))
                    cells[index] = pair.Value.ToString(CultureInfo.InvariantCulture);
            }
            foreach (var cell in cells)
            {
                sb.Append(',');
                if (cell is not null)
                    sb.Append(cell);
            }
            sb.Append("\r\n");
        }

        AppendSummary(sb, headerNames, columnIndex);
        return sb.ToString();
    }

    /// <summary>
    /// 数据行之后的 Summary 段（全英文，与第三方对比工具导出的收尾格式一致）：
    /// 每列给出样本数、记录首末电量与平均心率，末尾是时长与起止时间。
    /// 这些行的首列都是文字标签，不会被时间戳解析接受，因此重新导入时整段自动跳过。
    /// </summary>
    private void AppendSummary(StringBuilder sb, List<string> headerNames, Dictionary<ulong, int> columnIndex)
    {
        if (Rows.Count == 0)
            return;

        var samples = new int[headerNames.Count];
        var sums = new long[headerNames.Count];
        var startBattery = new int?[headerNames.Count];
        var endBattery = new int?[headerNames.Count];
        foreach (var row in Rows)
        {
            foreach (var pair in row.Values)
            {
                if (!columnIndex.TryGetValue(pair.Key, out var index))
                    continue;
                samples[index]++;
                sums[index] += pair.Value;
            }
            // 电量取记录期间最早 / 最晚一次的已知读数
            foreach (var pair in row.Batteries)
            {
                if (!columnIndex.TryGetValue(pair.Key, out var index))
                    continue;
                startBattery[index] ??= pair.Value;
                endBattery[index] = pair.Value;
            }
        }

        sb.Append("\r\n");
        sb.Append("Summary:\r\n");

        sb.Append("Samples");
        for (var i = 0; i < samples.Length; i++)
            sb.Append(',').Append(samples[i].ToString(CultureInfo.InvariantCulture));
        sb.Append("\r\n");

        AppendBatteryRow(sb, "Start Battery", startBattery);
        AppendBatteryRow(sb, "End Battery", endBattery);

        sb.Append("Avg HR");
        for (var i = 0; i < samples.Length; i++)
            sb.Append(',').Append(samples[i] == 0
                ? "0"
                : (sums[i] / samples[i]).ToString(CultureInfo.InvariantCulture));
        sb.Append("\r\n");

        var start = Rows[0].Time;
        var end = Rows[^1].Time;
        sb.Append("Duration,").Append(FormatDuration(end - start)).Append("\r\n");
        sb.Append("Start Time,")
            .Append(start.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append("\r\n");
        sb.Append("End Time,")
            .Append(end.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append("\r\n");
    }

    private static void AppendBatteryRow(StringBuilder sb, string label, int?[] batteries)
    {
        sb.Append(label);
        foreach (var battery in batteries)
        {
            sb.Append(',');
            // 设备没有电量能力时留空，与数据行的空单元格同一语义
            if (battery is int percent)
                sb.Append(percent.ToString(CultureInfo.InvariantCulture)).Append('%');
        }
        sb.Append("\r\n");
    }

    // 时长写法与第三方导出一致：不足 1 小时为 mm:ss，超过为 h:mm:ss
    private static string FormatDuration(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
            span = TimeSpan.Zero;
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
            : $"{span.Minutes}:{span.Seconds:00}";
    }

    private static string SanitizeName(string name, HashSet<string> used, int fallbackIndex)
    {
        var clean = new string((name ?? "").Where(c => c <= 127 && (char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' or '.')).ToArray()).Trim();
        if (clean.Length == 0)
            clean = "Device_" + fallbackIndex;
        var unique = clean;
        var suffix = 2;
        while (!used.Add(unique))
            unique = clean + "_" + suffix++;
        return unique;
    }
}
