using System.Globalization;
using Heart.Services;
using Windows.UI;

namespace Heart.Models
{
    /// <summary>
    /// 心率五区间：默认按最大心率百分比划分（Z1 50–60% … Z5 90–100%），
    /// 也支持用户自定义每个区间的上下限（持久化到 settings.json）。
    /// </summary>
    public static class HeartRateZones
    {
        public const int ZoneCount = 5;

        public static int MaxHeartRate { get; set; } = 190;

        private static double[]? _custom; // [lo0, hi0, lo1, hi1, ... lo4, hi4]

        private static readonly Color[] s_colors =
        {
            Color.FromArgb(0xFF, 0xA5, 0xB1, 0xC2),
            Color.FromArgb(0xFF, 0x38, 0xBD, 0xF8),
            Color.FromArgb(0xFF, 0x34, 0xD3, 0x99),
            Color.FromArgb(0xFF, 0xFB, 0xBF, 0x24),
            Color.FromArgb(0xFF, 0xF8, 0x71, 0x71),
        };

        /// <summary>启动时恢复上次的设置。</summary>
        public static void Initialize()
        {
            if (int.TryParse(SettingsStore.GetSetting("max_hr", "190"), out var maxHr) &&
                maxHr is >= 100 and <= 230)
                MaxHeartRate = maxHr;

            if (SettingsStore.GetSetting("zones_custom", "0") != "1")
                return;
            try
            {
                var parts = SettingsStore.GetSetting("zones", "").Split(',');
                if (parts.Length != ZoneCount * 2)
                    return;
                _custom = parts.Select(p => double.Parse(p, CultureInfo.InvariantCulture)).ToArray();
            }
            catch
            {
                _custom = null;
            }
        }

        /// <summary>应用自定义区间（flat = [lo0, hi0, lo1, hi1, ...]）。</summary>
        public static void SetCustomBounds(double[] flat)
        {
            if (flat.Length != ZoneCount * 2)
                throw new ArgumentException("bounds");
            _custom = flat.ToArray();
            SettingsStore.SetSetting("zones",
                string.Join(",", _custom.Select(v => v.ToString("0.#", CultureInfo.InvariantCulture))));
            SettingsStore.SetSetting("zones_custom", "1");
        }

        /// <summary>恢复按最大心率百分比的默认区间。</summary>
        public static void ResetToDefaults()
        {
            _custom = null;
            SettingsStore.SetSetting("zones_custom", "0");
        }

        public static bool IsCustom => _custom is not null;

        public static string NameOf(int index) =>
            LocalizationService.Instance.T("zone" + (index + 1));

        public static Color ColorOf(int index) => s_colors[index];

        public static (double Lo, double Hi) BoundsOf(int index, int maxHr) =>
            _custom is null
                ? (maxHr * (0.5 + 0.1 * index), maxHr * (0.6 + 0.1 * index))
                : (_custom[index * 2], _custom[index * 2 + 1]);

        public static double LowerOf(int index, int maxHr) => BoundsOf(index, maxHr).Lo;

        public static double UpperOf(int index, int maxHr) => BoundsOf(index, maxHr).Hi;

        /// <summary>返回心率所在区间下标（0..4），不在任何区间返回 -1。</summary>
        public static int IndexOf(int heartRate, int maxHr)
        {
            if (heartRate <= 0 || maxHr <= 0)
                return -1;
            for (var i = 0; i < ZoneCount; i++)
            {
                var (lo, hi) = BoundsOf(i, maxHr);
                if (heartRate >= lo && heartRate < hi)
                    return i;
            }
            // 高于最后一个区间上限时归入 Z5
            var (lastLo, _) = BoundsOf(ZoneCount - 1, maxHr);
            return heartRate >= lastLo ? ZoneCount - 1 : -1;
        }
    }
}
