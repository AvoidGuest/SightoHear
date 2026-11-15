<#
.SYNOPSIS
    临时测试构建：产出「免安装、可直接运行」的独立文件夹，用于改动后的快速验证。

.DESCRIPTION
    与 build-msix.ps1（正式打包 + 签名 + 安装）的区别：
      - 不改任何版本号（不动 csproj / app.manifest / Package.appxmanifest / AboutPage）
      - 不签名、不打 MSIX、不安装、不卸载
      - 输出到独立临时目录 src\.temp-preview\<平台>\，与 artifacts\、bin\ 完全隔离
      - 编译后可直接运行 SightoHear.exe（unpackaged 自包含模式，无需安装）

    为什么普通编译的 exe 跑不起来，而本脚本可以：
      本项目默认 WindowsPackageType=MSIX，产物依赖 MSIX 身份（resources.pri + VFS 资源映射）
      才能加载 XAML 与 ms-appx:///Assets/Fonts/*.ttf 字体；直接 dotnet build 出来的产物缺少
      这些，运行即 0xC0000409 快速失败（实测：Application.Start 回调执行后崩溃）。
      本脚本覆盖以下属性切换到 unpackaged 自包含模式：
        WindowsPackageType=None           取消 MSIX 打包身份
        WindowsAppSDKSelfContained=true    Windows App SDK 运行时随包带出
        SelfContained=true                 .NET 运行时随包带出（无需系统预装 .NET 8 Desktop Runtime）

    典型用法（改完代码看一眼效果）：
        .\src\build-temp.ps1                  构建到 .temp-preview\x64
        .\src\build-temp.ps1 -Run             构建并立即启动
        .\src\build-temp.ps1 -Platform ARM64  换架构

    注意 - 互斥量提醒：
      临时版与已安装的 MSIX 版共用进程名 SightoHear 与单实例互斥量
      SightoHear_SingleInstance_Mutex。本脚本会先结束正在运行的 SightoHear 进程，
      因此验证临时版前请先关闭 MSIX 版窗口（反之 MSIX 版启动时也会把临时版顶掉）。

    注意 - 满足效果后的正式发版流程：
      递增 4 处版本号（csproj / app.manifest / Package.appxmanifest / AboutPage）
      然后执行 .\src\build-msix.ps1 -Install
#>

[CmdletBinding()]
param(
    [ValidateSet('x64', 'x86', 'ARM64')]
    [string] $Platform = 'x64',

    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Debug',

    # 构建完成后直接启动
    [switch] $Run,

    # 启动前的额外等待秒数（用于先手动关掉 MSIX 版窗口）
    [int] $RunWaitSeconds = 0
)

$ErrorActionPreference = 'Stop'

$project = Join-Path $PSScriptRoot 'SightoHear.csproj'
$previewRoot = Join-Path $PSScriptRoot '.temp-preview'
$outDir = Join-Path $previewRoot $Platform.ToLowerInvariant()
$objDir = Join-Path $previewRoot ('obj\' + $Platform.ToLowerInvariant())

if (-not (Test-Path -LiteralPath $project)) {
    throw ('未找到项目文件：' + $project)
}

Write-Host '=================================================='
Write-Host ' 临时测试构建（不改版本号 / 不打包 / 不安装）'
Write-Host (' 平台：' + $Platform + '    配置：' + $Configuration)
Write-Host (' 输出：' + $outDir)
Write-Host '=================================================='

# ── 1. 结束正在运行的 SightoHear ────────────────────────────────
$running = @(Get-Process -Name 'SightoHear' -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    Write-Host ('[1/3] 结束运行中的 SightoHear 进程（' + $running.Count + ' 个）...')
    $running | ForEach-Object { try { Stop-Process -Id $_.Id -Force -ErrorAction Stop } catch { } }
    Start-Sleep -Milliseconds 600
}
else {
    Write-Host '[1/3] 未检测到运行中的 SightoHear 进程'
}

# ── 2. 构建（unpackaged 自包含模式）────────────────────────────
Write-Host '[2/3] 编译中...'
$arguments = @(
    'build', $project
    '-c', $Configuration
    ('/p:Platform=' + $Platform)
    '-v', 'minimal'
    '-nologo'
    '/p:WindowsPackageType=None'
    '/p:WindowsAppSDKSelfContained=true'
    '/p:WindowsAppSdkBootstrapInitialize=false'
    '/p:SelfContained=true'
    ('/p:OutputPath=' + $outDir + '\')
    ('/p:IntermediateOutputPath=' + $objDir + '\')
)

& dotnet @arguments
if ($LASTEXITCODE -ne 0) {
    Write-Host ''
    Write-Host ('构建失败（退出码 ' + $LASTEXITCODE + '）。')
    Write-Host '临时测试文件夹内容可能已过期，请勿运行其中的旧 exe。'
    exit $LASTEXITCODE
}

# ── 3. 结果 ────────────────────────────────────────────────────
$exe = Join-Path $outDir 'SightoHear.exe'
if (-not (Test-Path -LiteralPath $exe)) {
    Write-Host ('构建报告成功但未找到产物：' + $exe)
    Write-Host '实际输出目录内容：'
    Get-ChildItem -LiteralPath $outDir -ErrorAction SilentlyContinue | ForEach-Object { Write-Host ('  ' + $_.Name) }
    exit 1
}

$sizeBytes = (Get-ChildItem -LiteralPath $outDir -Recurse -File | Measure-Object -Property Length -Sum).Sum
$sizeMb = [math]::Round($sizeBytes / 1MB, 0)
$version = [regex]::Match((Get-Content $project -Raw), '<Version>([^<]+)</Version>').Groups[1].Value

Write-Host ''
Write-Host '[3/3] 构建完成'
Write-Host (' 版本号：未改动（仍为 ' + $version + '）')
Write-Host (' 可执行：' + $exe)
Write-Host (' 文件夹大小：约 ' + $sizeMb + ' MB')
Write-Host ''
Write-Host (' 直接运行：' + $exe)
Write-Host ' 满足效果后再走正式发版：递增 4 处版本号 -> .\src\build-msix.ps1 -Install'
Write-Host ''

if ($Run) {
    Write-Host '正在启动临时测试版...'
    if ($RunWaitSeconds -gt 0) { Start-Sleep -Seconds $RunWaitSeconds }
    Start-Process -FilePath $exe | Out-Null
    Write-Host '已启动。若窗口未出现，请检查是否有 MSIX 版窗口占用（同名互斥量）。'
}
