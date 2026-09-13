# Heart 项目规定

## 构建 / 发布（必须遵守）

- **唯一交付物是 `dist\Heart.exe`**：用 `powershell -ExecutionPolicy Bypass -File publish.ps1` 做单文件自包含发布（含 WinUI 运行时，双击即可运行）。
- **不要编译 / 生成 / 保留 `bin\` 下的构建产物**。发布过程若产生 `bin\` 中间目录，发布完成后删除它，工作区只保留源码和 `dist\`。
- **需要运行验证时，直接运行 `dist\Heart.exe`**，依据日志（`%LOCALAPPDATA%\Heart\debug.log`）判断结果，不要为验证单独构建 bin 包。
- **exe 会按系统语言自我改名**（2026-09-13 起）：程序启动时由 `Services/SelfRename.cs` 把自身改名为当前语言的名称（zh-CN → `心率对比.exe`、en → `HeartRateComparison.exe` 等，表见 `Services/Strings.cs` 的 `app_file_name`）。所以运行过一次后 `dist\` 里的产物可能不叫 `Heart.exe`——**重新发布即恢复**（`publish.ps1` 会清理 dist 下非 `Heart.exe` 的 exe）。需要临时关闭改名时设环境变量 `HEART_NO_SELF_RENAME=1`。

## 其他备忘

- `publish.ps1` 必须保持 UTF-8 **带 BOM**，否则 Windows PowerShell 5.1 按 GBK 解码会报"字符串缺少终止符"。

## 单文件打包排障备忘（2026-09-13）

- **症状**：`publish.ps1` 报 `error MSB4018: The "GenerateBundle" task failed unexpectedly` + `System.ArgumentException: Stream must support read and seek operations. (Parameter 'peStream')`，栈为 `Bundler.IsAssembly → InferType → GetFilteredFileSpecs → GenerateBundle`。
- **根因**：项目根目录存在文件名恰为 Windows 保留设备名 `nul` 的杂散文件（在 PowerShell 里写 `xxx 2>nul` 会真的建出这个文件，内容是重定向的 stderr）。WindowsAppSDK 的 MSIX 工具链在 `PublishSingleFile=true` 时启用 `AutoIncludeCopyToPublishDirectoryItems`（`Microsoft.Build.Msix.Packaging.targets:1130`），会把项目目录下所有默认项（`**/*` 通配到的文件，含 `.gitignore`、`AGENTS.md`、`out-folder/**` 等）统统标记为 `CopyToPublishDirectory`；打包器对每个待打包文件做 PE 校验时，`nul` 被解析成设备路径 `\\.\nul`，拿到的是不可 seek 的字符设备句柄，`PEReader` 直接抛异常。
- **已加固**：`Heart.csproj` 中设 `AutoIncludeCopyToPublishDirectoryItems=false`，并用 `DefaultItemExcludes` 排除 `out-folder/**;out-tmp/**;dist/**`。加固后即使 `nul` 仍在，`publish.ps1` 也能正常出包（已实测）。
- **注意**：`nul` 无法用常规方式删除——`DeleteFileW` / `MoveFileW`（含 `\\?\` 扩展路径）一律返回 `ERROR_ACCESS_DENIED(5)`，本卷未启用 8.3 短名。如需彻底清掉，用**管理员 cmd** 执行 `del "\\?\D:\Heart\nul"`，或从 WSL 执行 `rm /mnt/d/Heart/nul`。留着也不影响构建。
