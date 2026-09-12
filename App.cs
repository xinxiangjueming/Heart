using Heart.Models;
using Heart.Services;
using Heart.Ui;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.XamlTypeInfo;

namespace Heart;

/// <summary>
/// 纯代码（无 App.xaml）WinUI 3 应用：
/// 必须实现 IXamlMetadataProvider 并转发到框架的 XamlControlsXamlMetaDataProvider，
/// XamlControlsResources 只能在 OnLaunched 中挂载（构造函数中会抛 COMException）。
/// </summary>
public partial class App : Application, IXamlMetadataProvider
{
    private readonly XamlControlsXamlMetaDataProvider _metadataProvider = new();

    public static MainWindow MainWindow { get; private set; } = null!;
    public static BluetoothHeartRateService Ble { get; private set; } = null!;

    /// <summary>主窗口句柄（文件选择器等需要挂接窗口）。</summary>
    public static nint WindowHandle { get; set; }

    public App()
    {
        UnhandledException += OnUnhandledException;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            // 主题在首个窗口创建前应用（RequestedTheme 仅允许设置一次）
            LocalizationService.Instance.Initialize();
            HeartRateZones.Initialize();
            ThemeManager.Instance.Initialize();
            if (ThemeManager.Instance.Mode == "light") RequestedTheme = ApplicationTheme.Light;
            else if (ThemeManager.Instance.Mode == "dark") RequestedTheme = ApplicationTheme.Dark;

            // WinUI 控件主题资源 + miuix 画笔（应用级资源字典）
            Resources.MergedDictionaries.Add(new XamlControlsResources());
            Miuix.Register(Resources);
            Miuix.ApplyTheme(ThemeManager.Instance.ResolvedTheme);

            Ble = new BluetoothHeartRateService(DispatcherQueue.GetForCurrentThread());
            ThemeManager.Instance.StartPolling(DispatcherQueue.GetForCurrentThread());

            MainWindow = new MainWindow();
            MainWindow.Activate();
        }
        catch (Exception ex)
        {
            ReportCrash(ex);
            throw;
        }
    }

    private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        ReportCrash(e.Exception);
        e.Handled = true;
    }

    /// <summary>诊断日志：追加到 %LOCALAPPDATA%\Heart\debug.log（临时排查用）。</summary>
    public static void DebugLog(string message)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Heart");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "debug.log"),
                $"[{DateTime.Now:HH:mm:ss.fff}] {message}\n");
        }
        catch
        {
            // 日志写入失败不影响运行
        }
    }

    /// <summary>把启动期 / XAML 线程的致命异常写入磁盘，便于无控制台时诊断。</summary>
    public static void ReportCrash(Exception ex)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Heart");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "crash.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}]\n{ex}\n\n");
        }
        catch
        {
            // 日志写入失败时静默
        }
    }

    public IXamlType GetXamlType(Type type) => _metadataProvider.GetXamlType(type);

    public IXamlType GetXamlType(string fullName) => _metadataProvider.GetXamlType(fullName);

    public XmlnsDefinition[] GetXmlnsDefinitions() => _metadataProvider.GetXmlnsDefinitions();
}
