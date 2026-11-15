<#
.SYNOPSIS
    SightoHear 统一构建入口：一份代码，同时产出便携版与 MSIX 安装版。

.DESCRIPTION
    同一个仓库、同一个 csproj，靠 -Target 选择产物形态。默认 MSIX（兼容旧行为）。

    ┌──────────┬────────────────────────────────────────────────────────┐
    │ Target   │ 做什么                                                  │
    ├──────────┼────────────────────────────────────────────────────────┤
    │ Fast     │ 改完代码最快看到效果。跳过 30 秒的 XAML 编译链，        │
    │          │ 复用上一次生成的 *.g.cs，只重编 C#（实测约 7 秒）。     │
    │          │ 前提：上一次完整构建成功，且没有改动 .xaml。            │
    │          │ 不满足时会自动回退成完整构建，不会给出错误产物。        │
    │ Portable │ 便携版免安装产物（WindowsPackageType=None）→ …\Portable\ │
    │ Msix     │ MSIX 安装包 → bin\<配置>\<平台>\SightoHear_x.y.z_*.msix │
    │ All      │ 先 Portable 再 Msix，一次拿到两种产物。                │
    └──────────┴────────────────────────────────────────────────────────┘

    两种产物共享同一套源码与同一份 csproj，差异只由 -p:SightoHearMsix=true/false 派生：
      · MSIX    ：WindowsPackageType=MSIX，生成 AppxManifest.xml + resources.pri 后打包签名
      · Portable：WindowsPackageType=None，bin 目录可直接拷给别人双击运行
    因此修一处代码两边同时生效，不存在「只修了便携版 / 只修了打包版」的问题。

    输出目录：
      Portable → src\bin\<配置>\<平台>\Portable\  （整个文件夹可拷走）
      Fast     → src\bin\Debug\<平台>\Portable\   （托管，仅供本机调试）
      Msix     → src\bin\<配置>\<平台>\*.msix

    便携版分发形态（重要）：
      WindowsAppSDKSelfContained=false → 复用系统已安装的 Windows App Runtime，
      输出目录约 66 个文件 / 271MB。目标机器需装 Windows App Runtime 2.3+。
      需要「零依赖」时追加 -p:WindowsAppSDKSelfContained=true 重新发布。

.PARAMETER Target
    Fast / Portable / Msix / All，默认 Msix。

.PARAMETER Platform
    x64 / x86 / ARM64，默认 x64。
    ⚠ Native AOT 只支持 x64 与 ARM64（.NET 无 ILCompiler x86 运行时包），x86 自动回退托管。
    ⚠ 超分（libmpv/Anime4K）与运动补偿补帧同样仅 x64 可用。

.PARAMETER DebugConfig
    强制使用 Debug 配置（默认 Release 用于分发，Fast 固定 Debug）。

.PARAMETER Run
    构建完成后立即启动产物（Portable / Fast 有效；Msix 会提示改用安装后的入口）。

.PARAMETER Install
    仅 Msix 有效：打包完成后自动安装（含旧版卸载、证书信任）。

.PARAMETER NoAot
    关闭 Native AOT（仅对照实验用）。

.PARAMETER PreferSpeed
    AOT 优化方向改为速度优先（默认尺寸优先，exe 约 -20%）。

.PARAMETER KeepPdb
    把 .pdb 留在可运行目录（默认移到 bin\Symbols\，因为它是 165MB 纯调试符号）。

.PARAMETER Clean
    构建前清空输出目录（默认保留，加快增量）。

.EXAMPLE
    .\build.ps1 -Target Fast                 # 改完 C# 立刻看效果，约 7 秒
    .\build.ps1 -Target Fast -Run
    .\build.ps1 -Target Portable             # 便携版 Release + AOT
    .\build.ps1 -Target Msix                 # MSIX 安装包
    .\build.ps1 -Target All -Platform x64    # 两种产物一次做完
    .\build.ps1 -Target All -Platform ARM64
#>
[CmdletBinding()]
param(
    [ValidateSet('Fast', 'Portable', 'Msix', 'All')]
    [string] $Target = 'Msix',

    [ValidateSet('x64', 'x86', 'ARM64')]
    [string] $Platform = 'x64',

    [switch] $DebugConfig,
    [switch] $Run,
    [switch] $Install,
    [switch] $NoAot,
    [switch] $PreferSpeed,
    [switch] $KeepPdb,
    [switch] $Clean
)

$ErrorActionPreference = 'Stop'

$ProjectRoot    = $PSScriptRoot
$ProjectFile    = Join-Path $ProjectRoot 'SightoHear.csproj'
$ExeName        = 'SightoHear'
$PlatformFolder = $Platform.ToLowerInvariant()

if (-not (Test-Path -LiteralPath $ProjectFile)) {
    throw ('未找到项目文件：' + $ProjectFile)
}

# ── Native AOT 仅在 Release + x64/ARM64 下有意义 ────────────────────────────
$Configuration = if ($DebugConfig -or $Target -eq 'Fast') { 'Debug' } else { 'Release' }
$AotSupported  = $Platform -ne 'x86'
$UseAot        = ($Target -in @('Portable', 'Msix', 'All')) -and (-not $NoAot) -and
                 $AotSupported -and $Configuration -eq 'Release'

if ($UseAot -and -not $AotSupported) {
    Write-Warning ('平台 ' + $Platform + ' 不支持 Native AOT（.NET 无 ILCompiler x86 运行时包），已回退为托管构建。')
}

# 便携版可运行产物落 bin\<配置>\<平台>\Portable\，与同目录下的 .msix 安装包分层共存
$BinDir = Join-Path $ProjectRoot ('bin\' + $Configuration + '\' + $PlatformFolder + '\Portable')
$ObjDir = Join-Path $ProjectRoot ('obj\' + $Configuration + '\' + $PlatformFolder)

function Write-Banner([string]$Mode, [string]$Note) {
    Write-Host '==================================================' -ForegroundColor Cyan
    Write-Host (' SightoHear 构建 · ' + $Target)                  -ForegroundColor Cyan
    Write-Host (' 模式：' + $Mode)                                 -ForegroundColor Cyan
    Write-Host (' 配置：' + $Configuration + '    平台：' + $Platform) -ForegroundColor Cyan
    Write-Host (' 输出：' + $BinDir)                               -ForegroundColor Cyan
    if ($Note) { Write-Host (' 说明：' + $Note) -ForegroundColor DarkGray }
    Write-Host '==================================================' -ForegroundColor Cyan
}

function Stop-RunningApp {
    $running = @(Get-Process -Name $ExeName -ErrorAction SilentlyContinue)
    if ($running.Count -gt 0) {
        Write-Host ('结束运行中的 ' + $ExeName + ' 进程（' + $running.Count + ' 个）...')
        $running | ForEach-Object { try { Stop-Process -Id $_.Id -Force -ErrorAction Stop } catch { } }
        Start-Sleep -Milliseconds 800
    }
}

function Get-Version {
    return [regex]::Match((Get-Content $ProjectFile -Raw), '<Version>([^<]+)</Version>').Groups[1].Value
}

$script:ExitCode = 0

# ══════════════════════════════════════════════════════════════════════════
# Fast：跳过 XAML 编译链，复用上次生成的 *.g.cs，只重编 C#
#
# 为什么能快 5 倍：WinUI 的 MarkupCompilePass1 / Pass2 / XamlPreCompile
# 在 Microsoft.UI.Xaml.Markup.Compiler.interop.targets 里【没有定义 Inputs/Outputs】，
# MSBuild 无法判断可以跳过，于是每次 build 都把 28 个 XAML 页从头重编一遍，
# 空构建也要 30 秒（实测 Target Performance Summary：Pass2 20.9s + PreCompile 5.1s
# + Pass1 4.5s，而真正的 Csc 只有 5.1s）。
#
# 做法：用 -t:Compile 只跑 C# 编译，再用 -p:XamlFastPath=true 把上一次生成的
# *.g.cs / *.g.i.cs 重新链进编译（这两个文件是 Pass1/Pass2 的产物，内容没变就无需重生成）。
# 实测：37 秒 → 7 秒。
#
# 守卫：改了 .xaml、或生成的 .g.cs 不存在时，直接回退完整构建。宁可慢，不可错。
# ══════════════════════════════════════════════════════════════════════════
function Invoke-FastBuild {
    Write-Banner 'Fast（复用 XAML 产物，仅重编 C#）' '首次或改了 .xaml 时自动回退完整构建'

    Stop-RunningApp

    $generated = @(Get-ChildItem -LiteralPath $ObjDir -Filter '*.g.cs' -ErrorAction SilentlyContinue) +
                 @(Get-ChildItem -LiteralPath $ObjDir -Filter '*.g.i.cs' -ErrorAction SilentlyContinue)
    $xamlFiles = @(Get-ChildItem -LiteralPath $ProjectRoot -Filter '*.xaml' -Recurse -ErrorAction SilentlyContinue |
                   Where-Object { $_.FullName -notmatch '\\(bin|obj|artifacts|dist)\\' })

    # ★ 守卫必须用「上一次完整构建的时间戳标记」，不能用 *.g.cs 的 LastWriteTime 比较。
    #   原因：XAML 编译器只重新生成改动过的页面，其余 *.g.cs 保留旧时间戳。
    #   若拿「最旧的 .g.cs」和「最新的 .xaml」比，只要历史上碰过一次 XAML 就永久判定为
    #   「需要完整构建」，Fast 通道会彻底失效（实测连续 4 次全部回退）。
    $stampFile = Join-Path $ObjDir '.xaml-build-stamp'
    $needFull  = $false

    if ($generated.Count -eq 0) {
        Write-Host '生成代码缺失（首次构建或 obj 被清空）→ 走完整构建。' -ForegroundColor Yellow
        $needFull = $true
    }
    elseif (-not (Test-Path -LiteralPath $stampFile)) {
        Write-Host '缺少完整构建标记（尚未完整构建过）→ 走完整构建。' -ForegroundColor Yellow
        $needFull = $true
    }
    elseif ($xamlFiles.Count -gt 0) {
        $stampTime  = (Get-Item -LiteralPath $stampFile).LastWriteTime
        $newestXaml = ($xamlFiles | Sort-Object LastWriteTime -Descending | Select-Object -First 1)
        if ($newestXaml.LastWriteTime -gt $stampTime) {
            Write-Host ('检测到 ' + $newestXaml.Name + ' 在上次完整构建之后被修改 → 走完整构建。') -ForegroundColor Yellow
            $needFull = $true
        }
    }

    if ($needFull) {
        Invoke-FullBuild -Purpose 'Fast'
        return
    }

    Write-Host '编译 C#（复用已有 XAML 生成代码）...'
    & dotnet build $ProjectFile -c Debug "/p:Platform=$Platform" `
        '-p:XamlFastPath=true' -t:Compile -v:minimal -nologo
    if ($LASTEXITCODE -ne 0) {
        Write-Host ''
        Write-Host '复用路径编译失败 → 自动回退完整构建...' -ForegroundColor Yellow
        Invoke-FullBuild -Purpose 'Fast'
        return
    }

    # -t:Compile 只更新 obj 里的 dll，需要手动同步到 bin 才是可运行产物
    $objDll = Join-Path $ObjDir ($ExeName + '.dll')
    $binDll = Join-Path $BinDir ($ExeName + '.dll')
    if (-not (Test-Path -LiteralPath $objDll)) { throw ('未找到编译产物：' + $objDll) }
    New-Item -ItemType Directory -Path $BinDir -Force | Out-Null
    $copied = $false
    for ($i = 0; $i -lt 5 -and -not $copied; $i++) {
        try { Copy-Item -LiteralPath $objDll -Destination $binDll -Force -ErrorAction Stop; $copied = $true }
        catch { Start-Sleep -Milliseconds 300 }
    }
    if (-not $copied) { throw ('复制 dll 到输出目录失败（可能被占用）：' + $binDll) }

    $exe = Join-Path $BinDir ($ExeName + '.exe')
    if (-not (Test-Path -LiteralPath $exe)) { throw ('未找到可执行文件：' + $exe) }
    Write-Host ('完成 ✔  ' + $exe) -ForegroundColor Green
    if ($Run) { Start-Process -FilePath $exe | Out-Null }
}

# ══════════════════════════════════════════════════════════════════════════
# 完整构建（Portable / Fast 回退共用）
# ══════════════════════════════════════════════════════════════════════════
function Invoke-FullBuild {
    param([string]$Purpose = 'Portable')

    $sw = [Diagnostics.Stopwatch]::StartNew()
    Stop-RunningApp

    if ($Clean -and (Test-Path -LiteralPath $BinDir)) {
        Write-Host '清理输出目录...'
        try { Remove-Item -LiteralPath $BinDir -Recurse -Force -ErrorAction Stop }
        catch { Write-Warning ('清理失败（可能有句柄占用）：' + $_.Exception.Message) }
    }

    if ($UseAot) {
        # ★ AOT 三处硬约束（改任何一条都会得到「假 AOT」或启动即崩）：
        #   1) 不能把 OutputPath 指到 staging —— AOT 产物会在 Microsoft.UI.Xaml.dll 里崩。
        #   2) 不能用 -o 短参数传路径 —— 经 dotnet CLI 转发会被空格拆开（MSB1008）。
        #   3) PublishDir 必须与 OutputPath 不同，否则 publish 会判定为已是最新，
        #      跳过 ILCompiler，留下「看起来是 AOT、其实还带 SightoHear.dll」的假产物。
        # 因此：OutputPath 保持默认，PublishDir 指向 obj 下的隔离 staging，
        #       发布完成后再把 staging 拷进 bin。
        $stagingDir = Join-Path $ObjDir 'publish-staging'
        if (Test-Path -LiteralPath $stagingDir) {
            Remove-Item -LiteralPath $stagingDir -Recurse -Force -ErrorAction SilentlyContinue
        }

        Write-Host 'Native AOT 发布中（ILCompiler 生成本机代码，约 2 分钟）...'
        $a = @(
            'publish', $ProjectFile
            '-c', $Configuration
            '-r', ('win-' + $PlatformFolder)
            ("/p:Platform=$Platform")
            '/p:PublishAot=true'
            "/p:OptimizationPreference=$(if ($PreferSpeed) { 'Speed' } else { 'Size' })"
            ("/p:PublishDir=$stagingDir")
            '/p:SightoHearMsix=false'
            '-v', 'minimal', '-nologo'
        )
        if ($Purpose -eq 'Fast') { $a += '/p:Configuration=Debug' }

        & dotnet @a
        if ($LASTEXITCODE -ne 0) { throw ('AOT 发布失败（退出码 ' + $LASTEXITCODE + '）') }

        if (-not (Test-Path -LiteralPath $stagingDir)) { throw ('未找到 AOT 发布输出：' + $stagingDir) }

        # ★ 硬断言：真正的 AOT 产物不含托管运行时。命中任意一个就说明 ILCompiler 没跑，必须失败。
        $fakeMarkers = @('coreclr.dll', 'clrjit.dll', 'System.Private.CoreLib.dll',
                         'hostfxr.dll', 'hostpolicy.dll', ($ExeName + '.dll'))
        foreach ($m in $fakeMarkers) {
            if (Test-Path -LiteralPath (Join-Path $stagingDir $m)) {
                throw ('AOT 产物校验失败：staging 中出现 ' + $m + '，ILCompiler 未实际运行（假 AOT）。')
            }
        }

        Remove-Item -LiteralPath $BinDir -Recurse -Force -ErrorAction SilentlyContinue
        New-Item -ItemType Directory -Path $BinDir -Force | Out-Null
        Get-ChildItem -LiteralPath $stagingDir -Force |
            Copy-Item -Destination $BinDir -Recurse -Force
        Remove-Item -LiteralPath $stagingDir -Recurse -Force -ErrorAction SilentlyContinue
    }
    else {
        Write-Host '编译中（托管，完整构建）...'
        & dotnet build $ProjectFile -c $Configuration "/p:Platform=$Platform" `
            '/p:SightoHearMsix=false' -v:minimal -nologo
        if ($LASTEXITCODE -ne 0) { throw ('构建失败（退出码 ' + $LASTEXITCODE + '）') }
    }
    $sw.Stop()

    $exe = Join-Path $BinDir ($ExeName + '.exe')
    if (-not (Test-Path -LiteralPath $exe)) {
        throw ('构建报告成功但未找到产物：' + $exe)
    }

    # ★ 写入完整构建标记：build.ps1 -Target Fast 用它判断「XAML 生成代码是否与源码同步」。
    #   只在完整构建成功后写，所以标记时间 == XAML 链最近一次真正跑完的时间。
    if (-not (Test-Path -LiteralPath $ObjDir)) { New-Item -ItemType Directory -Path $ObjDir -Force | Out-Null }
    $stampFile = Join-Path $ObjDir '.xaml-build-stamp'
    try { Set-Content -LiteralPath $stampFile -Value ([DateTime]::Now.ToString('o')) -Encoding ascii -Force }
    catch { Write-Warning ('写入构建标记失败（不影响产物）：' + $_.Exception.Message) }

    # ── 符号分离：AOT 的 pdb 约 165MB，纯调试符号，不随分发包走 ──────────────
    $version    = Get-Version
    $pdbPath    = Join-Path $BinDir ($ExeName + '.pdb')
    $symbolsDir = Join-Path (Split-Path -Parent $BinDir) 'Symbols'
    $pdbMb      = 0
    if ((Test-Path -LiteralPath $pdbPath) -and -not $KeepPdb) {
        $pdbMb = [math]::Round((Get-Item -LiteralPath $pdbPath).Length / 1MB, 0)
        New-Item -ItemType Directory -Path $symbolsDir -Force | Out-Null
        $symbolTarget = Join-Path $symbolsDir ($ExeName + '-' + $Configuration + '-' + $PlatformFolder + '-' + $version + '.pdb')
        try { Move-Item -LiteralPath $pdbPath -Destination $symbolTarget -Force -ErrorAction Stop }
        catch { Write-Warning ('移动 pdb 失败（可能被占用）：' + $_.Exception.Message) }
    }

    $sizeBytes = (Get-ChildItem -LiteralPath $BinDir -Recurse -File |
                  Where-Object { $_.Extension -ne '.pdb' } |
                  Measure-Object -Property Length -Sum).Sum
    $sizeMb  = [math]::Round($sizeBytes / 1MB, 0)
    $fileCnt = (Get-ChildItem -LiteralPath $BinDir -Recurse -File).Count
    $modeTxt = if ($UseAot) { 'Native AOT' } else { '托管' }

    Write-Host ''
    Write-Host '便携版构建完成 ✔' -ForegroundColor Green
    Write-Host (' 模式：' + $modeTxt + '    耗时：' + [math]::Round($sw.Elapsed.TotalSeconds, 1) + ' s')
    Write-Host (' 版本：' + $version)
    Write-Host (' 可执行：' + $exe)
    Write-Host (' 体积：' + $fileCnt + ' 个文件 / 约 ' + $sizeMb + ' MB（不含 pdb；整个文件夹可拷给他人运行）')
    if ($pdbMb -gt 0) {
        Write-Host (' 符号：约 ' + $pdbMb + ' MB 已移到 ' + $symbolsDir) -ForegroundColor DarkGray
        Write-Host '       不随分发包发布，但请保留：解析用户崩溃转储时需要对应版本的完整符号。' -ForegroundColor DarkGray
    }
    Write-Host ''

    if ($Run) {
        Write-Host '正在启动...'
        Start-Process -FilePath $exe | Out-Null
    }
}

# ══════════════════════════════════════════════════════════════════════════
# MSIX：委托给 build-msix.ps1（它负责 AOT 发布 → AppxManifest → 瘦身 → 签名）
# ══════════════════════════════════════════════════════════════════════════
function Invoke-MsixBuild {
    $msixScript = Join-Path $ProjectRoot 'build-msix.ps1'
    if (-not (Test-Path -LiteralPath $msixScript)) { throw ('未找到 MSIX 打包脚本：' + $msixScript) }

    Write-Host ''
    Write-Host '==================================================' -ForegroundColor Cyan
    Write-Host ' SightoHear 构建 · MSIX 安装包'                     -ForegroundColor Cyan
    Write-Host (' 配置：' + $Configuration + '    平台：' + $Platform) -ForegroundColor Cyan
    Write-Host '==================================================' -ForegroundColor Cyan

    $msixArgs = @{ Configuration = $Configuration; Platform = $Platform }
    if (-not $UseAot) { $msixArgs['NoAot'] = $true }
    if ($Install) { $msixArgs['Install'] = $true }
    & $msixScript @msixArgs
    if ($LASTEXITCODE -ne 0) { throw ('MSIX 打包失败（退出码 ' + $LASTEXITCODE + '）') }
}

# ══════════════════════════════════════════════════════════════════════════
# 主流程
# ══════════════════════════════════════════════════════════════════════════
$total = [Diagnostics.Stopwatch]::StartNew()

switch ($Target) {
    'Fast'     { Invoke-FastBuild }
    'Portable' { Invoke-FullBuild -Purpose 'Portable' }
    'Msix'     { Invoke-MsixBuild }
    'All' {
        Invoke-FullBuild -Purpose 'Portable'
        Invoke-MsixBuild
        Write-Host ''
        Write-Host '两种产物已全部生成 ✔' -ForegroundColor Green
        Write-Host (' 便携版：' + $BinDir)
        $pkg = Get-ChildItem -LiteralPath $BinDir -Filter '*.msix' -ErrorAction SilentlyContinue |
               Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if ($pkg) { Write-Host (' 安装包：' + $pkg.FullName) }
    }
}

Write-Host ('总耗时：' + [math]::Round($total.Elapsed.TotalSeconds, 1) + ' s') -ForegroundColor DarkGray
