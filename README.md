# Heart 心率监测

基于 **WinUI 3 + .NET 8** 的桌面端蓝牙心率监测软件，界面为 Miuix（小米 HyperOS）设计语言。
**发布产物为单文件独立 exe**（自包含 WinUI 运行时，无需安装任何依赖），见 `dist\Heart.exe`。

## 功能

- **设备扫描 / 连接**：扫描广播心率服务 (0x180D) 的 BLE 设备，点击连接 / 断开；**支持同时连接多台设备**
- **数据展示**：心率、电量（设备不支持时显示 `--`）、RR 间期（最近 8 个，已换算为 ms）
- **实时图表**：多设备曲线同图实时绘制，曲线下方同色半透明填充；心率五区间背景带（Z1 热身 ~ Z5 极限，最大心率可调）；时间窗口 30 秒 ~ 10 分钟
- **曲线颜色**：点击设备卡片或图例上的色点即可更换，颜色持久化
- **多语言**：简体中文 / 繁體中文 / English / 日本語 / 한국어 / Français / Deutsch / Español，可在设置页切换（默认跟随系统）
- **深浅色主题**：浅色 / 深色 / 自动跟随系统，实时生效
- 扫描前自动检测蓝牙开关状态并给出排查提示

## 使用

直接双击 `dist\Heart.exe`。首次使用：佩戴心率带并开启其「心率广播」（如小米手表在健康 App 内开启）、断开与手机的连接，然后点「扫描设备」→「连接」。

## 从源码构建

```powershell
powershell -ExecutionPolicy Bypass -File publish.ps1   # 发布单文件 exe 到 dist\
# 或
dotnet publish Heart.csproj -c Release -r win-x64 --self-contained true
```

## 实现说明（单文件 WinUI 3 的关键点）

- **UI 纯 C# 代码构建（零 XAML 文件）**：不经过 XAML 编译器，无 XBF 产物；`Program.Main` 手动初始化并设置
  `MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY`，`App` 实现 `IXamlMetadataProvider` 并在 `OnLaunched` 挂载 `XamlControlsResources`
- `EnableMsixTooling=true` 生成并嵌入 `resources.pri`（项目需含至少一个 `.resw` 触发 PRI 管线）
- `PublishSingleFile + IncludeNativeLibrariesForSelfExtract + WindowsAppSDKSelfContained + SelfContained`
- 多语言字符串内置在 `Services/Strings.cs`（运行时解析，不依赖 MRT）；主题画笔为共享实例，切色即全局生效
- 图表用内置 XAML Shapes 手绘（区间带 / 网格线 / 曲线 / 填充 / 图例），不依赖第三方图表控件

## 常见问题

- **扫描不到设备**：确认设备已开启心率广播、未连接手机、电脑蓝牙已开启（应用内会提示蓝牙开关状态）
- **电量显示 `--`**：部分心率带没有电池服务 (0x180F)，属正常现象
