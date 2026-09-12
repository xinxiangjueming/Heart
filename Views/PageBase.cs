using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Heart.Views;

/// <summary>纯代码页面的公共基类：语言 / 主题切换时由主窗口回调。</summary>
public abstract class PageBase : Page
{
    /// <summary>页面统一外边距：NavigationView 内容区默认零留白，不设会贴着窗口边框。</summary>
    protected PageBase()
    {
        Margin = new Thickness(28, 20, 28, 24);
    }

    /// <summary>语言切换时刷新全部文案（UI 线程）。</summary>
    public virtual void ApplyLocalization()
    {
    }

    /// <summary>主题切换时刷新非画笔类的主题相关元素（UI 线程）。</summary>
    public virtual void ApplyTheme()
    {
    }
}
