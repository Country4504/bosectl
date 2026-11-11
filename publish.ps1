# 发布 Bose Earbuds 为可直接运行的 exe
#
# 用法（在仓库根目录执行）：
#   .\publish.ps1                 # 文件夹版（推荐，启动快）  -> dist\BoseApp-win-x64\BoseApp.exe
#   .\publish.ps1 -SingleFile     # 单文件版（一个 exe）      -> dist\BoseApp-win-x64-single\BoseApp.exe
#   .\publish.ps1 -Arch arm64     # ARM64 设备
#
# 两种方式都是自包含发布：目标电脑无需安装 .NET 或 Windows App SDK。

param(
    [ValidateSet("x64", "arm64")] [string]$Arch = "x64",
    [switch]$SingleFile,
    [switch]$WithCli
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$rid = "win-$Arch"
$platform = if ($Arch -eq "arm64") { "ARM64" } else { "x64" }
$name = if ($SingleFile) { "BoseApp-$rid-single" } else { "BoseApp-$rid" }
$out = Join-Path $root "dist\$name"

# 正在运行的实例会锁住 dll，先关闭
Get-Process BoseApp -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host "关闭正在运行的 BoseApp (PID $($_.Id))..." -ForegroundColor Yellow
    $_ | Stop-Process -Force
    Start-Sleep -Milliseconds 500
}

if (Test-Path $out) { Remove-Item $out -Recurse -Force }

Write-Host "发布 BoseApp -> $out" -ForegroundColor Cyan
$pubArgs = @(
    "publish", (Join-Path $root "src\BoseApp\BoseApp.csproj"),
    "-c", "Release",
    "-r", $rid,
    "-p:Platform=$platform",
    "-o", $out
)
if ($SingleFile) { $pubArgs += "-p:SingleFile=true" }
& dotnet @pubArgs
if ($LASTEXITCODE -ne 0) { throw "BoseApp 发布失败" }

if ($WithCli) {
    Write-Host "发布 bosectl -> $out" -ForegroundColor Cyan
    & dotnet publish (Join-Path $root "src\BoseCtl\BoseCtl.csproj") -c Release -r $rid --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o $out
    if ($LASTEXITCODE -ne 0) { throw "bosectl 发布失败" }
}

# 打 zip，方便拷贝到其他电脑
$zip = "$out.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path "$out\*" -DestinationPath $zip

$exe = Join-Path $out "BoseApp.exe"
$size = "{0:N1} MB" -f ((Get-ChildItem $out -Recurse | Measure-Object Length -Sum).Sum / 1MB)
Write-Host ""
Write-Host "完成！" -ForegroundColor Green
Write-Host "  程序: $exe"
Write-Host "  大小: $size"
Write-Host "  压缩包: $zip"
