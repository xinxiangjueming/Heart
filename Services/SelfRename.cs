using System.IO;

namespace Heart.Services
{
    /// <summary>
    /// 让 exe 文件名的「显示形态」跟随当前生效语言。
    ///
    /// 背景：未打包（unpackaged）WinUI 应用没有 MSIX 的 MUI 多语言资源机制，
    /// 资源管理器 / 任务管理器 / 文件属性里的名字只由磁盘上的文件名决定，编译期固定。
    /// 唯一能做到「文件名跟随语言」的办法，就是进程启动时把自己改名。
    ///
    /// 设计约束（全部为了保证改名失败也绝不影响启动）：
    ///  1. 幂等 —— 文件名已等于目标名时立即返回，因此每种语言最多触发一次改名，
    ///     改完之后再启动多少次都不会再动文件系统。
    ///  2. 不覆盖 —— 目标名已存在时放弃（避免覆盖用户手工留下的其它语言副本）。
    ///  3. 不抛异常 —— 全部异常吞掉并写 debug.log，改名失败只是名字不对，功能不受影响。
    ///  4. 有开关 —— 环境变量 HEART_NO_SELF_RENAME=1 时整体跳过（排障用）。
    ///
    /// 已知代价（用户明确接受）：改名后原快捷方式 / 任务栏固定项 / 防火墙按路径的规则会失效，
    /// 且单个文件发布的自解压目录名含 exe 名，改名后首次启动会重新解压一次运行库。
    /// </summary>
    public static class SelfRename
    {
        private const string DisableFlag = "HEART_NO_SELF_RENAME";

        /// <summary>把当前 exe 改名为 <paramref name="localizedName"/>.exe（同目录）。</summary>
        public static void Apply(string localizedName)
        {
            try
            {
                if (Environment.GetEnvironmentVariable(DisableFlag) == "1")
                {
                    App.DebugLog("rename: skipped (" + DisableFlag + "=1)");
                    return;
                }

                if (string.IsNullOrWhiteSpace(localizedName))
                    return;

                var current = Environment.ProcessPath;
                if (string.IsNullOrEmpty(current) ||
                    !current.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    App.DebugLog("rename: skipped, not a standalone exe (" + (current ?? "null") + ")");
                    return;
                }

                var directory = Path.GetDirectoryName(current);
                if (string.IsNullOrEmpty(directory))
                    return;

                // 文件名禁用字符兜底：语言包写错时不能让启动流程异常
                foreach (var bad in Path.GetInvalidFileNameChars())
                {
                    if (localizedName.Contains(bad))
                    {
                        App.DebugLog("rename: skipped, illegal char in name");
                        return;
                    }
                }

                var target = Path.Combine(directory, localizedName + ".exe");

                if (string.Equals(current, target, StringComparison.OrdinalIgnoreCase))
                    return; // 已匹配，不做任何文件系统写操作

                if (File.Exists(target))
                {
                    App.DebugLog("rename: skipped, target already exists -> " + Path.GetFileName(target));
                    return;
                }

                File.Move(current, target);
                App.DebugLog("rename: " + Path.GetFileName(current) + " -> " + Path.GetFileName(target));
            }
            catch (Exception ex)
            {
                // 常见失败：exe 所在目录只读、被安全软件拦截、目标名被占用
                App.DebugLog("rename: failed (" + ex.GetType().Name + ": " + ex.Message + ")");
            }
        }
    }
}
