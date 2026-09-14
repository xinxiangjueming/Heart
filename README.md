# Heart / 心率对比

基于 **WinUI 3 + .NET 8** 的桌面端蓝牙心率监测与对比软件，视觉体系为自研 **Miuix（小米 HyperOS 风）**，
纯 C# 代码构建 UI（无 XAML 编译器）。**发布产物是单个自包含 exe**（内嵌 WinUI 运行时），双击即可运行，无需安装任何依赖。

- 适用场景：同时佩戴 2 台以上心率设备（手表 / 手环 / 心率带），在电脑上实时对比同一时刻的心率差异；
  也可把设备端导出的记录拖进软件里离线复盘（曲线对齐 + 误差计算）。
- 运行环境：Windows 10 1809（10.0.17763）及以上 / 64 位 / 带蓝牙 LE 适配器。

---

## 一、功能总览

### 1. 设备（`Views/DevicesPage.cs`）

| 能力 | 说明 |
| --- | --- |
| 扫描 | 只列出广播**标准心率服务 0x180D** 的设备；单次扫描 10 秒自动停止，可手动停止 |
| 多路连接 | **支持同时连接多台设备**，每台独立建立 GATT 会话并订阅心率通知 |
| 实时读数 | 心率、电量（0x180F，设备不支持时显示 `--`）、RR 间期（最近 8 个，已换算为 ms） |
| 曲线颜色 | 卡片左侧色点可点选颜色，按蓝牙地址持久化 |
| 诊断提示 | 扫描前检查蓝牙适配器是否存在 / 是否开启；连接失败逐项给出原因（未找到设备 / 无 GATT 会话 / 无心率服务 0x180D / 无测量特征 0x2A37 / 订阅失败 / 已连接） |
| 断链处理 | 连接意外丢失时自动清理该设备的 GATT 资源并提示「设备连接已断开」 |

### 2. 实时图表（`Views/ChartPage.cs`）

- 多设备曲线同图实时绘制，曲线下方同色半透明填充；每秒刷新。
- **心率五区间背景带**：Z1 热身 / Z2 燃脂 / Z3 有氧 / Z4 无氧 / Z5 极限，区间上下限可编辑（支持恢复默认），最大心率可调。
- **时间窗口**：30 秒 / 1 / 3 / 5 / 10 / 15 / 30 分钟 / 全程（选择会记忆）。
- **图例**：每条曲线一个芯片（设备名 + 当前读数 + 区间）；点圆点改色，点芯片显示 / 隐藏该曲线（至少保留一条可见）。
- **长按数值卡**：按住图表任意位置出现参考线 + 各设备在该时刻的心率数值。
- **回看滑块**：非全程窗口时出现在底部，可向后拖动查看缓冲内的历史（内存缓冲约 10 分钟）。
- **Y 轴**：默认按窗口内数据自动贴合（留 5% 余量），也可手动设定上下限（设置会记忆，可一键「恢复自动」）。

### 3. 记录与导出（`Services/RecordingService.cs`）

- 「开始记录」后按**每秒**采样一次所有已连接设备；设备这一秒没有新上报则留空（不沿用上一秒的值，避免伪造数据）。
- 记录中关闭窗口会**自动最小化到托盘**继续记录；从托盘退出时会先提示保存本次记录。
- 「导出 CSV」写出本应用格式（全英文内容，CRLF 换行，带 UTF-8 BOM 便于 Excel/WPS 识别）：

```csv
Time,Amazfit Balance 2,Xiaomi Watch S4 F4DC,BRT25305
2026-09-14 20:41:03,81,89,86

Summary:
Samples, 335, 191, 335
Start Battery, 88%, , 92%
End Battery, 85%, , 90%
Avg HR, 81, 89, 86
Duration, 23:40
Start Time, 2026-09-14 20:41:03
End Time, 2026-09-14 21:04:43
```

> 表头设备名含非 ASCII 或非法字符时会替换为 `Device_N`，保证文件在任意环境都能被正确解析。

### 4. 数据查看模式（拖放导入，只读）

把数据文件**拖到图表页**即可进入查看模式（标题栏显示文件名与「退出查看」按钮）；记录进行中禁止导入。

| 兼容格式 | 说明 |
| --- | --- |
| 本应用导出的 CSV | 表头 `Time` + 设备名，行 `yyyy-MM-dd HH:mm:ss`，尾部 `Summary:` 段自动跳过 |
| 设备端导出的 Excel（`.xlsx`） | 表头 `timestamp` + 每设备一列，1 Hz；用自研 `Services/XlsxLite.cs` 只读解析（不引第三方包），自动取「最后一个非空表头列」为有效列宽 |
| 设备导出的 `heart_rate_data_*.csv` | 表头 `timestamp` + 每设备一列，行为带毫秒的完整时间戳 |
| 第三方工具 `heart_yyyyMMdd_HHmmss.csv` | 首列小写 `time`、行为 `HH:mm:ss`、空格=该秒未上报、尾部 `Summary:` 段；日期锚点三级回落：文件内 `Start Time` → 文件名日期 → 当天，跨午夜自动进位 |

查看模式下可用的分析工具：

- **图例**：三行信息（设备名 / 平均值 / 有效数据点数）；点圆点改色（只影响显示），点芯片显示 / 隐藏曲线。
- **曲线偏移**：把某条曲线整体左移 0~3600 秒，用于对齐两台设备的时间戳偏差（只改绘制时间，不动文件数据）。
- **误差计算（MAE）**：选「被测设备」与「对标设备」，按整秒对齐后计算**平均绝对误差**，并给出有效数据点 / 舍弃数据点（对标设备该秒无读数即舍弃）/ 最大绝对误差。
- **数值卡 / 时间窗口 / Y 轴**等与实时模式一致；导入的曲线颜色、偏移、显隐状态只作用于本次查看，退出即恢复实时模式。

### 5. 设置（`Views/SettingsPage.cs`）

- **语言**：简体中文 / 繁體中文 / English / 日本語 / 한국어 / Français / Deutsch / Español，默认自动跟随系统，切换即时生效。
- **主题**：自动跟随系统 / 浅色 / 深色，即时生效。
- **关于**：软件定位一句话说明。

### 6. 其他

- **托盘**：最小化 / 关闭窗口时驻留托盘，菜单提供「回到界面」「退出软件」。
- **exe 按系统语言自我改名**：启动时把自身改名为当前语言的名称（zh-CN → `心率对比.exe`，en → `HeartRateComparison.exe` 等，表见 `Services/Strings.cs` 的 `app_file_name`）。
  需要临时关闭时设环境变量 `HEART_NO_SELF_RENAME=1`；重新发布即恢复 `Heart.exe`。

---

## 二、使用步骤

1. 在设备上开启**心率广播**（如小米手表在健康 App 内开启、Amazfit 在 Zepp App 内开启），并**断开它与手机的连接**（BLE 心率服务同一时间只服务一个中心设备）。
2. 双击 `dist\Heart.exe`，在「设备」页点「扫描设备」→ 对每台设备点「连接」（可连多台）。
3. 切到「实时图表」，按需要调整时间窗口 / 最大心率 / 区间显示，点「开始记录」。
4. 结束后点「停止记录」→「导出 CSV」，用保存对话框选择位置。
5. 想比较离线数据时，把导出的 CSV 或设备端导出的 Excel **拖到图表页**，再用「曲线偏移」「误差计算」做对齐与误差分析。

---

## 三、数据文件与本地存储

| 路径 | 内容 |
| --- | --- |
| `%LOCALAPPDATA%\Heart\settings.json` | 曲线颜色（按蓝牙地址）、语言、主题、心率区间、时间窗口、Y 轴范围 |
| `%LOCALAPPDATA%\Heart\debug.log` | 诊断日志（**UTF-8** 编码）：图表布局、导入解析（文件名 / 曲线数 / **各列设备名** / 时间跨度 / 均值）、时间锚点来源、Y 轴刻度等 |
| `%LOCALAPPDATA%\Heart\crash.log` | 未处理异常记录（存在即说明上次运行崩过） |

> 排查导入问题时先看 `debug.log` 的 `chart import enter ... names=[...]` 一行：它能直接给出「这个文件被解析成了哪几条曲线、列名叫什么」。

---

## 四、从源码构建

```powershell
# 推荐：一键发布单文件 exe 到 dist\
powershell -ExecutionPolicy Bypass -File publish.ps1

# 等价命令
dotnet publish Heart.csproj -c Release -r win-x64 --self-contained true -o dist
```

- **发布前先关掉正在运行的软件**（`dist` 里的 exe 被占用会让打包报 `MSB4018 / Access to the path ... is denied`）。
- `dotnet publish` 是**增量**构建：`obj\` 判定「已最新」时会跳过重编改动过的 `.cs`，出现「exit 0 但产物是旧的」而不报错。
  因此改完代码建议**先删 `obj\` 与 `bin\` 做干净重建**，发布后核对 `dist\*.exe` 的时间戳是否为本次构建时间。
- 发布完成后删除 `bin\`，工作区只保留源码与 `dist\`（详见 `AGENTS.md`）。

---

## 五、源码结构

| 路径 | 职责 |
| --- | --- |
| `Program.cs` / `App.cs` | 手工初始化 WinUI（不使用 XAML 编译器）、挂载 `XamlControlsResources` 与 Miuix 调色板、注册托盘与自我改名 |
| `MainWindow.cs` | NavigationView 外壳（设备 / 实时图表 / 设置）、标题栏与页面切换动效、关闭即最小化到托盘 |
| `Views/DevicesPage.cs` | 设备页：扫描、设备卡片（读数 / 状态 / 连接按钮 / 颜色） |
| `Views/ChartPage.cs` | 图表页：实时绘制、图例、区间带、长按数值卡、回看滑块、数据查看模式（拖放导入 / 曲线偏移 / 误差计算） |
| `Views/SettingsPage.cs` | 设置页：语言、主题、关于 |
| `Services/BluetoothHeartRateService.cs` | BLE 扫描（0x180D 过滤）、GATT 连接与订阅、心率 / 电量解析、断链清理 |
| `Services/RecordingService.cs` | 每秒采样、CSV 生成（含英文 Summary 段） |
| `Services/XlsxLite.cs` | 最小只读 xlsx 解析（ZipArchive + XDocument） |
| `Services/LocalizationService.cs` / `Strings.cs` | 8 种语言的运行时文案表与语言切换广播 |
| `Services/ThemeManager.cs` / `Ui/Miuix.cs` | 深浅色主题切换；自研控件工厂与共享画笔（颜色常量集中在 Miuix 一处） |
| `Models/HeartRateDevice.cs` | 一台设备的状态与样本缓冲（内存窗口约 10 分钟） |
| `Models/SettingsStore.cs` / `Palette.cs` / `HeartRateZones.cs` | 设置持久化、曲线色板、心率区间 |

---

## 六、实现说明（单文件 WinUI 3 的关键点）

- **UI 纯 C# 代码构建（零 XAML 文件）**：不经过 XAML 编译器、无 XBF 产物；`Program.Main` 手动初始化并设置
  `MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY`，`App` 实现 `IXamlMetadataProvider` 并在 `OnLaunched` 挂载 `XamlControlsResources`。
- `EnableMsixTooling=true` 生成并嵌入 `resources.pri`（项目需含至少一个 `.resw` 触发 PRI 管线）。
- `PublishSingleFile + IncludeNativeLibrariesForSelfExtract + WindowsAppSDKSelfContained + SelfContained`。
- 多语言字符串内置在 `Services/Strings.cs`（运行时解析，不依赖 MRT）；主题画笔为共享实例，切色即全局生效。
- 图表用内置 XAML Shapes 手绘（区间带 / 网格线 / 曲线 / 填充 / 图例 / 坐标轴），不依赖第三方图表控件。

---

## 七、常见问题

- **扫描不到设备**：确认设备已开启心率广播、**未连接手机**、电脑蓝牙已开启（软件会直接提示蓝牙开关状态）。
- **图例 / 曲线对不上**：确认拖入的文件与实际查看的是同一份；`debug.log` 的 `chart import enter ... names=[...]` 会列出本次解析出的列名。
- **电量显示 `--`**：设备没有电池服务 (0x180F)，属正常现象。
- **提示「无法导入数据」**：只接受「表头首列为 `Time` / `timestamp` / `time` + 其后每列一台设备」的宽表文件；空单元格按「该秒未上报」处理，不补 0 也不插值。
- **中文在 Excel 里乱码**：导出的 CSV 带 UTF-8 BOM，若仍乱码请用「数据 → 自文本」按 UTF-8 导入。
- **发布报 `MSB4018` / `Stream must support read and seek operations`**：项目根目录混入了 Windows 保留设备名文件（如 PowerShell 里 `2>nul` 误建的 `nul`），
  详见 `AGENTS.md`（已通过 `AutoIncludeCopyToPublishDirectoryItems=false` + 目录排除加固）。
- **PowerShell 里重定向 stderr 请写 `2>$null`**，不要写 `2>nul`（会真的建出名为 `nul` 的文件）。

---

## 八、近期变更

- **2026-09-14**：修复数据查看模式下**图例设备名串档**——导入图例的芯片缓存以「列序号」为键，两份文件曲线条数相同时签名不变，
  复用了上一份文件的名称 / 平均值 / 点数（表现为「图例里 OPPO 那条曲线写着上一份文件 `Amazfit Balance 2`」，与「曲线偏移 / 误差计算」弹窗显示不一致）。
  现在换文件一律重建按 `imp:` 键缓存的图例芯片，且复用时就地刷新三行文字；导入日志新增 `names=[...]` 便于核对列名。
- **2026-09-13**：数据查看模式新增拖入设备端 Excel（`.xlsx`）、曲线偏移、误差计算（MAE）；图例支持点击显示 / 隐藏曲线；
  应用更名「心率对比」并支持 exe 按系统语言自我改名；修正 X 轴标签越界与 Y 轴数值重叠。
- **更早**：导入纯时间 CSV（`HH:mm:ss` + `Summary:`）；多设备曲线与区间带；深浅色主题与 8 语言。
