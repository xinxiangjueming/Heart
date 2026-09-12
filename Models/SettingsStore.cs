using System.IO;
using System.Text.Json;

namespace Heart.Models
{
    /// <summary>把每台设备的曲线颜色持久化到 %LOCALAPPDATA%\Heart\settings.json。</summary>
    public static class SettingsStore
    {
        private static readonly object _gate = new();
        private static readonly string _dir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Heart");
        private static readonly string _file = Path.Combine(_dir, "settings.json");

        private static Dictionary<string, string> _colors = new();
        private static bool _loaded;

        private static void EnsureLoaded()
        {
            if (_loaded)
                return;
            _loaded = true;
            try
            {
                if (File.Exists(_file))
                {
                    var data = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_file));
                    if (data != null)
                        _colors = data;
                }
            }
            catch
            {
                // 配置损坏时按空配置处理
            }
        }

        public static bool TryGetColor(string address, out string hex)
        {
            lock (_gate)
            {
                EnsureLoaded();
                return _colors.TryGetValue("color:" + address, out hex!);
            }
        }

        public static string GetSetting(string key, string defaultValue)
        {
            lock (_gate)
            {
                EnsureLoaded();
                return _colors.TryGetValue("set:" + key, out var value) ? value : defaultValue;
            }
        }

        public static void SetSetting(string key, string value)
        {
            lock (_gate)
            {
                EnsureLoaded();
                _colors["set:" + key] = value;
                Save();
            }
        }

        public static void SetColor(string address, string hex)
        {
            lock (_gate)
            {
                EnsureLoaded();
                _colors["color:" + address] = hex;
                Save();
            }
        }

        private static void Save()
        {
            try
            {
                Directory.CreateDirectory(_dir);
                File.WriteAllText(_file, JsonSerializer.Serialize(_colors));
            }
            catch
            {
                // 保存失败不影响运行
            }
        }
    }
}
