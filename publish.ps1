# Heart 一键发布脚本：产出单文件独立 exe（含 WinUI 运行时）
# 用法: powershell -ExecutionPolicy Bypass -File publish.ps1
$ErrorActionPreference = 'Stop'

$root    = $PSScriptRoot
$project = Join-Path $root 'Heart.csproj'
$outDir  = Join-Path $root 'dist'

Write-Host "==> 发布 $project"
& dotnet publish $project -c Release -r win-x64 --self-contained true -o $outDir
if ($LASTEXITCODE -ne 0) { throw "publish 失败，退出码 $LASTEXITCODE" }

$exe = Join-Path $outDir 'Heart.exe'

# 运行时自改名会按语言在 dist 里留下副本（如「心率对比.exe」），发布后清理，
# 保证 dist 只保留刚产出的 Heart.exe。正在运行的副本删不掉，静默跳过即可。
Get-ChildItem -Path $outDir -Filter *.exe -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -ne $exe } |
    Remove-Item -Force -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "==> 完成: $exe ($([math]::Round((Get-Item $exe).Length / 1MB)) MB)"
Write-Host "    双击即可运行（自包含 WinUI 运行时，无需安装任何依赖）"
