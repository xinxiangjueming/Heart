using System.Globalization;

namespace Heart.Services
{
using Heart.Models;

    /// <summary>
    /// 轻量本地化服务：内置 8 种语言的字符串字典，
    /// 支持运行时切换语言（无需重启），可自动跟随系统语言。
    /// </summary>
    public sealed class LocalizationService
    {
        public static LocalizationService Instance { get; } = new();

        /// <summary>语言切换事件（UI 线程触发）。</summary>
        public event Action? LanguageChanged;

        /// <summary>用户选择："auto" 或语言代码，如 "zh-CN"。</summary>
        public string Language { get; private set; } = "auto";

        /// <summary>当前实际生效的语言代码。</summary>
        public string EffectiveLanguage { get; private set; } = "en-US";

        /// <summary>支持的语言（代码 + 该语言的自我称呼）。</summary>
        public static readonly (string Code, string NativeName)[] Languages =
        {
            ("zh-CN", "简体中文"),
            ("zh-TW", "繁體中文"),
            ("en-US", "English"),
            ("ja-JP", "日本語"),
            ("ko-KR", "한국어"),
            ("fr-FR", "Français"),
            ("de-DE", "Deutsch"),
            ("es-ES", "Español"),
        };

        public void Initialize()
        {
            Language = SettingsStore.GetSetting("language", "auto");
            EffectiveLanguage = Resolve();
        }

        public void SetLanguage(string code)
        {
            Language = code;
            SettingsStore.SetSetting("language", code);
            EffectiveLanguage = Resolve();
            LanguageChanged?.Invoke();
        }

        /// <summary>取本地化字符串，回退链：当前语言 → 英文 → key 本身。</summary>
        public string T(string key)
        {
            if (Strings.All.TryGetValue(EffectiveLanguage, out var dict) &&
                dict.TryGetValue(key, out var value))
                return value;
            if (Strings.All.TryGetValue("en-US", out var en) &&
                en.TryGetValue(key, out var enValue))
                return enValue;
            return key;
        }

        public string T(string key, params object[] args) =>
            string.Format(CultureInfo.CurrentCulture, T(key), args);

        private string Resolve()
        {
            if (Language != "auto")
                return Language;
            var system = DetectSystemLanguage();
            // 精确匹配 → 前缀匹配（如 zh-HK → zh-TW）→ 英文
            if (Strings.All.ContainsKey(system))
                return system;
            var prefix = system.Split('-')[0];
            var byPrefix = Strings.All.Keys.FirstOrDefault(k =>
                k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            return byPrefix ?? "en-US";
        }

        private static string DetectSystemLanguage()
        {
            try
            {
                var first = Windows.System.UserProfile.GlobalizationPreferences.Languages.FirstOrDefault();
                if (!string.IsNullOrEmpty(first))
                    return first;
            }
            catch
            {
                // 某些环境拿不到用户配置
            }
            return CultureInfo.CurrentUICulture.Name;
        }
    }
}
