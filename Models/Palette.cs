using Windows.UI;

namespace Heart.Models
{
    /// <summary>设备曲线的预设颜色盘（Miuix 风格高饱和色）。</summary>
    public static class Palette
    {
        public static readonly (string Name, Windows.UI.Color Color)[] NamedColors =
        {
            ("蓝色",   Windows.UI.Color.FromArgb(0xFF, 0x34, 0x82, 0xFF)),
            ("青绿",   Windows.UI.Color.FromArgb(0xFF, 0x00, 0xBF, 0xA5)),
            ("紫色",   Windows.UI.Color.FromArgb(0xFF, 0x7C, 0x4D, 0xFF)),
            ("粉色",   Windows.UI.Color.FromArgb(0xFF, 0xFF, 0x40, 0x81)),
            ("橙色",   Windows.UI.Color.FromArgb(0xFF, 0xFF, 0x9F, 0x0A)),
            ("绿色",   Windows.UI.Color.FromArgb(0xFF, 0x30, 0xD1, 0x58)),
            ("天蓝",   Windows.UI.Color.FromArgb(0xFF, 0x64, 0xD2, 0xFF)),
            ("玫紫",   Windows.UI.Color.FromArgb(0xFF, 0xBF, 0x5A, 0xF2)),
            ("红色",   Windows.UI.Color.FromArgb(0xFF, 0xFF, 0x37, 0x5F)),
            ("黄色",   Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xD6, 0x0A)),
        };

        private static int _next;

        /// <summary>按顺序给新设备分配颜色。</summary>
        public static Color Next()
        {
            var entry = NamedColors[_next % NamedColors.Length];
            _next++;
            return entry.Color;
        }

        public static Color FromHex(string hex)
        {
            hex = hex.TrimStart('#');
            return Color.FromArgb(
                hex.Length >= 8 ? byte.Parse(hex[..2], System.Globalization.NumberStyles.HexNumber) : (byte)0xFF,
                byte.Parse(hex[^6..^4], System.Globalization.NumberStyles.HexNumber),
                byte.Parse(hex[^4..^2], System.Globalization.NumberStyles.HexNumber),
                byte.Parse(hex[^2..], System.Globalization.NumberStyles.HexNumber));
        }

        public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
    }
}
