<#
.SYNOPSIS
SightoHear MSIX 打包脚本（纯命令行，无需 Visual Studio）

.DESCRIPTION
完整流程：构建 → 生成/复用代码签名证书 → 信任证书 → MakeAppx 打包 → SignTool 签名 →（可选）安装。
版本号自动从 SightoHear.csproj 的 <Version>/<AssemblyVersion> 读取，并同步写入 MSIX 包的
AppxManifest.xml（Identity.Version），实现"只改 csproj 一处，MSIX 版本自动联动"。

【全新输出架构】
- BIN 区（纯 MSIX 安装包）：
    bin\Debug\<平台>\SightoHear_<版本>_<平台>.msix    （Beta 测试版）
    bin\Release\<平台>\SightoHear_<版本>_<平台>.msix  （正式版）
  平台 = x86 / x64 / arm64
- artifacts 区（非打包中间产物 + 打包临时文件）：
    artifacts\<Config>\<平台>\                        （构建中间产物，exe/dll 等）
    artifacts\msix\                                   （payload / 证书 / 密码等临时文件）

工具来源（本机已有，无需安装）：
- makeappx.exe / signtool.exe：NuGet 包 Microsoft.Windows.SDK.BuildTools 缓存目录
- 证书：PowerShell 内置 New-SelfSignedCertificate（PKI 模块）

.PARAMETER Configuration
构建配置：Debug / Release，默认 Release。Beta 版用 Debug（输出到 bin\Debug），正式版用 Release（输出到 bin\Release）。

.PARAMETER Platform
目标平台：x64 / x86 / ARM64，默认 x64（注意：libmpv 超分/VapourSynth 仅 x64 可用）。

.PARAMETER Install
打包完成后自动安装（Add-AppxPackage）。同版本已安装时会先卸载再装。

.PARAMETER SkipBuild
跳过 dotnet build，直接使用现有输出目录打包。

.PARAMETER SkipTimestamp
跳过签名时间戳（离线环境使用；默认加 DigiCert 时间戳）。

.PARAMETER CertPassword
签名证书 PFX 密码；不指定则自动生成随机密码，保存到输出目录 cert-password.txt。

.EXAMPLE
.\build-msix.ps1
.\build-msix.ps1 -Configuration Debug -Install
.\build-msix.ps1 -Configuration Release -Platform ARM64 -Install
#>
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",

    [ValidateSet("x64", "x86", "ARM64")]
    [string]$Platform = "x64",

    [switch]$Install,
    [switch]$SkipBuild,
    [switch]$SkipTimestamp,
    # Native AOT 打包（默认开启）：dotnet publish + ILCompiler，产物是本机代码。
    # 仅 x64 / ARM64 有效；x86 自动回退托管。
    # 发布给别人下载的包必须走 AOT —— 实测启动 1254ms（AOT）vs 1925ms（托管）。
    # 需要临时拿托管包做对照时用 -NoAot。
    [switch]$Aot,
    # 显式关闭 Native AOT（等价于旧行为，仅供对照实验/排障）
    [switch]$NoAot,
    # 跳过「包内瘦身」（默认执行）。需要排查"删了某个文件导致启动异常"时用。
    [switch]$SkipPayloadSlim,
    [string]$CertPassword = ""
)

$ErrorActionPreference = 'Stop'

# ★ Native AOT 默认开启（发布包必须用 AOT：启动 1254ms vs 托管 1925ms）。
#   x86 没有 ILCompiler 运行时包，强制回退托管。
if (-not $NoAot) { $Aot = $true }
if ($Aot -and $Platform -eq 'x86') {
    Write-Warning 'x86 不支持 Native AOT，本次自动回退为托管构建。'
    $Aot = $false
}

# ==================== 常量与路径 ====================
$ProjectRoot   = $PSScriptRoot
$ProjectFile   = Join-Path $ProjectRoot 'SightoHear.csproj'
$ManifestFile  = Join-Path $ProjectRoot 'Package.appxmanifest'
$ExeName       = 'SightoHear'
$Publisher     = 'CN=AvoidGuest Studio'   # 必须与 Package.appxmanifest 的 Publisher 完全一致
$ArtifactsRoot = Join-Path $ProjectRoot 'artifacts'
# MSIX 打包临时区（payload/证书等），放在 artifacts 下，不污染 BIN 安装包区
$OutputRoot    = Join-Path $ArtifactsRoot 'msix'
$PayloadDir    = Join-Path $OutputRoot 'payload'
$PfxPath       = Join-Path $OutputRoot 'SightoHear-dev.pfx'
$CerPath       = Join-Path $OutputRoot 'SightoHear-dev.cer'

# 全新架构输出目录：
#   · 非打包中间产物：artifacts\<Config>\<平台>\  （Directory.Build.props 已重定向）
#   · MSIX 安装包：  bin\<Config>\<平台>\SightoHear_<版本>_<平台>.msix
# 注意：Beta 版（Debug）输出到 bin\Debug，正式版（Release）输出到 bin\Release。
$ConfigFolder   = $Configuration          # Debug / Release（Directory.Build.props 已统一标准命名）
$PlatformFolder = switch ($Platform) { 'x64' { 'x64' } 'x86' { 'x86' } 'ARM64' { 'arm64' } }
$ArtifactsBuildDir = Join-Path $ArtifactsRoot "$ConfigFolder\$PlatformFolder"
# MSIX 安装包输出根（BIN 区）
$BinOutputDir   = Join-Path $ProjectRoot "bin\$ConfigFolder\$PlatformFolder"

# ==================== 工具定位（NuGet 缓存中的 Windows SDK BuildTools） ====================
function Find-SdkTool {
    param([string]$ToolName)
    $cacheRoot = Join-Path $env:USERPROFILE '.nuget\packages\microsoft.windows.sdk.buildtools'
    if (-not (Test-Path $cacheRoot)) {
        throw "未找到 Windows SDK BuildTools 缓存目录：$cacheRoot`n请先执行 dotnet restore（项目依赖 Microsoft.Windows.SDK.BuildTools 包）"
    }
    # 打包工具（makeappx/signtool）必须在【打包主机】上运行，本机为 x64，故始终选用 x64 版本；
    # 不能用 $Platform 匹配——ARM64 打包时 arm64 版工具是 ARM64 原生二进制，无法在 x64 主机执行。
    $tool = Get-ChildItem $cacheRoot -Recurse -Filter $ToolName -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match "\\bin\\[^\\]+\\x64\\" } |
        Sort-Object FullName -Descending |
        Select-Object -First 1
    if (-not $tool) {
        throw "在 $cacheRoot 中未找到 $ToolName（打包主机架构 x64）"
    }
    return $tool.FullName
}

$MakeAppx  = Find-SdkTool 'makeappx.exe'
$SignTool  = Find-SdkTool 'signtool.exe'
Write-Host "MakeAppx : $MakeAppx"
Write-Host "SignTool : $SignTool"

# ==================== 读取 csproj 版本号（单一事实来源） ====================
function Get-MsixVersion {
    $csprojContent = Get-Content $ProjectFile -Raw

    # 优先 <AssemblyVersion>（纯四段）
    $m = [regex]::Match($csprojContent, '<AssemblyVersion>\s*(\d+\.\d+\.\d+\.\d+)\s*</AssemblyVersion>')
    if ($m.Success) { return $m.Groups[1].Value }

    # 回退：从 <Version>（可能带 -beta 预发布后缀）派生四段
    $m2 = [regex]::Match($csprojContent, '<Version>\s*([^<]+?)\s*</Version>')
    if ($m2.Success) {
        $v = ($m2.Groups[1].Value -split '[-+]')[0]          # 去掉 -beta / +hash
        $parts = ($v -split '\.')
        if ($parts.Count -ge 3) {
            return "$($parts[0]).$($parts[1]).$($parts[2]).0"
        }
    }
    throw '无法从 SightoHear.csproj 解析版本号，请确认已定义 <Version> 或 <AssemblyVersion>'
}

$MsixVersion = Get-MsixVersion
Write-Host "MSIX 包版本 : $MsixVersion（来自 SightoHear.csproj）" -ForegroundColor Cyan

# ==================== 构建 ====================
if (-not $SkipBuild) {
    Write-Host "`n========== 1/6 构建 $Configuration | $Platform ==========" -ForegroundColor Cyan
    Stop-Process -Name $ExeName -Force -ErrorAction SilentlyContinue

    # ★ AOT 打包（-Aot 开关）：Native AOT 只在 publish 时生效，dotnet build 永远不触发 ILCompiler。
    #   注意：AOT 只支持 x64 / ARM64，x86 会自动回退托管。
    if ($Aot) {
        if ($Platform -eq 'x86') {
            Write-Warning 'x86 不支持 Native AOT，本次将使用托管构建。'
        } else {
            Write-Host '  模式：Native AOT（publish + ILCompiler，耗时约 3~6 分钟）' -ForegroundColor Yellow
            $rid = if ($Platform -eq 'ARM64') { 'win-arm64' } else { 'win-x64' }

            # ★★ 关键（踩了很久的坑）：publish 绝对不能直接落到 $ArtifactsBuildDir。
            #   Directory.Build.props 把 OutputPath 也指向那里，两者重合会出两个问题：
            #     1) publish 前后 build 的中间产物（SightoHear.dll / *.xbf / obj/ / native/）
            #        会留在同一目录里，被后面的步骤一并打进 MSIX —— 于是「AOT 包」里
            #        同时躺着本机 exe 与 2MB 的托管 SightoHear.dll，等于没 AOT。
            #     2) copy 阶段只能靠"事后删几个文件名"补救，永远补不干净。
            #   正确做法：publish 到 obj 下的独立 staging，再只把 staging 的内容搬到打包目录。
            $aotStaging = Join-Path $ArtifactsRoot "aot-staging\$ConfigFolder\$PlatformFolder"
            if (Test-Path $aotStaging) { Remove-Item $aotStaging -Recurse -Force -ErrorAction SilentlyContinue }

            dotnet publish $ProjectFile -c $Configuration -r $rid `
                /p:Platform=$Platform /p:PublishAot=true `
                /p:PublishDir="$aotStaging\" `
                /p:SightoHearMsix=true -v minimal
            if ($LASTEXITCODE -ne 0) { throw "AOT 发布失败（退出码 $LASTEXITCODE）" }

            if (-not (Test-Path (Join-Path $aotStaging "$ExeName.exe"))) {
                throw "AOT 发布未产出 $ExeName.exe（staging: $aotStaging）"
            }

            # 自检：真正的 AOT 产物里不应有托管宿主与托管程序集
            $mustBeAbsent = @('coreclr.dll','clrjit.dll','System.Private.CoreLib.dll',
                              'hostfxr.dll','hostpolicy.dll',"$ExeName.dll")
            $leaked = $mustBeAbsent | Where-Object { Test-Path (Join-Path $aotStaging $_) }
            if ($leaked) {
                throw ("AOT 产物中检出托管残留，判定为伪 AOT，中止打包：`n  " + ($leaked -join ', ') +
                       "`n请检查是否把 PublishDir 指到了构建输出目录。")
            }

            # 清空打包目录，只从 staging 复制（staging 已是纯净 AOT 载荷）
            if (Test-Path $ArtifactsBuildDir) {
                Remove-Item $ArtifactsBuildDir -Recurse -Force -ErrorAction SilentlyContinue
            }
            New-Item -ItemType Directory -Path $ArtifactsBuildDir -Force | Out-Null
            Get-ChildItem $aotStaging -Force | Copy-Item -Destination $ArtifactsBuildDir -Recurse -Force

            # 符号与链接库不进包
            Get-ChildItem $ArtifactsBuildDir -Recurse -File -ErrorAction SilentlyContinue |
                Where-Object { $_.Extension -in '.pdb', '.lib', '.winmd' } |
                Remove-Item -Force -ErrorAction SilentlyContinue

            Write-Host ("  AOT 载荷已就位：{0} 个文件，{1:N1} MB" -f `
                (Get-ChildItem $ArtifactsBuildDir -Recurse -File).Count, `
                ((Get-ChildItem $ArtifactsBuildDir -Recurse -File |
                  Measure-Object Length -Sum).Sum/1MB)) -ForegroundColor Green
        }
    }

    if (-not ($Aot -and $Platform -ne 'x86')) {
        # 托管构建（原有路径）
        # /p:SightoHearMsix=true 让 csproj 里的 SlimDistributionOutput 跳过：
        #   MSIX 打包需要 .winmd / 语言资源 / AppxManifest 参与 makeappx 与 resources.pri 生成。
        dotnet build $ProjectFile -c $Configuration /p:Platform=$Platform /p:SightoHearMsix=true -v minimal
        if ($LASTEXITCODE -ne 0) { throw "构建失败（退出码 $LASTEXITCODE）" }
    }
} else {
    Write-Host "`n========== 1/6 跳过构建（-SkipBuild） ==========" -ForegroundColor Cyan
}

if (-not (Test-Path (Join-Path $ArtifactsBuildDir "$ExeName.exe"))) {
    throw "未找到构建输出：$ArtifactsBuildDir\$ExeName.exe`n请先构建（去掉 -SkipBuild）"
}

# ==================== 准备打包内容目录 ====================
Write-Host "`n========== 2/6 准备打包内容 ==========" -ForegroundColor Cyan
if (Test-Path $PayloadDir) { Remove-Item $PayloadDir -Recurse -Force }
New-Item -ItemType Directory -Path $PayloadDir -Force | Out-Null

# 复制全部输出（排除构建中间产物与 *.pdb 调试符号）
# ★ native\ 必须排除：它是 AOT / 单文件发布时 ILCompiler 的中间落点，
#   里面会留一个几十 MB 的 SightoHear.exe 与几百 MB 的 .pdb。
#   若混进包，MSIX 里会同时存在根目录的托管 exe 和 native\ 的 AOT exe
#   （实测多出 52 MB），且两者版本可能不一致，属于纯粹的污染。
$excludeDirs = @('obj', 'native', 'AppX')
Get-ChildItem $ArtifactsBuildDir -Force |
    Where-Object { $_.PSIsContainer -and $_.Name -notin $excludeDirs -or
                   (-not $_.PSIsContainer -and $_.Extension -ne '.pdb' -and $_.Extension -ne '.obj') } |
    Copy-Item -Destination $PayloadDir -Recurse -Force
Get-ChildItem $PayloadDir -Recurse -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Extension -in '.pdb', '.obj', '.ilk', '.exp' } |
    Remove-Item -Force -ErrorAction SilentlyContinue

# MSIX 布局下，Logo 资源（Square150x150Logo.scale-200.png、LockScreenLogo.scale-200.png 等）
# 位于 AppX\Assets 子目录（VS 单项目打包布局），而 AppxManifest 引用的 Assets\*.png 需位于包根目录。
# 命令行构建（本脚本）不生成 AppX 布局，且这些 Logo 的 <Content> 项未配 CopyToOutputDirectory，
# 因此同时从项目源 Assets 兜底复制——否则 MakeAppx 打包后注册时报 0x80070002
# （找不到初始屏幕图像 [LockScreenLogo.scale-200.png]）。
$payloadAssets = Join-Path $PayloadDir 'Assets'
if (-not (Test-Path $payloadAssets)) { New-Item -ItemType Directory -Path $payloadAssets -Force | Out-Null }

# ① 从 AppX\Assets（VS 打包布局，若存在）复制
$appxAssets = Join-Path $PayloadDir 'AppX\Assets'
if (Test-Path $appxAssets) {
    Copy-Item (Join-Path $appxAssets '*') $payloadAssets -Recurse -Force
    Write-Host "已从 AppX\Assets 复制 Logo 资源到包根 Assets"
}

# ② 从项目源 Assets 兜底复制 AppxManifest 引用到的 Logo（幂等，缺失才补）
$srcAssetsRoot = Join-Path $ProjectRoot 'Assets'
foreach ($logo in @('StoreLogo.png', 'LockScreenLogo.scale-200.png', 'Square150x150Logo.scale-200.png',
                    'Square44x44Logo.scale-200.png', 'Wide310x150Logo.scale-200.png')) {
    $src = Join-Path $srcAssetsRoot $logo
    $dst = Join-Path $payloadAssets $logo
    if ((Test-Path $src) -and -not (Test-Path $dst)) {
        Copy-Item $src $dst -Force
        Write-Host "已从项目源 Assets 补充 Logo：$logo"
    }
}

# 由 Package.appxmanifest 生成 AppxManifest.xml（替换占位符 + 同步版本号）
# 注意：-Encoding UTF8 必须显式指定（PS 5.1 默认按 ANSI/GBK 读取 UTF-8 文件会导致中文乱码）
$manifestXml = Get-Content $ManifestFile -Raw -Encoding UTF8
$manifestXml = $manifestXml.Replace('$targetnametoken$',   $ExeName)
$manifestXml = $manifestXml.Replace('$targetentrypoint$', 'Windows.FullTrustApplication')
# ${1}/${2} 显式组引用：避免 "$1 + 版本号" 被解析为组 10（版本号以数字开头）
$manifestXml = $manifestXml -replace '(<Identity[^>]*?Version=")[^"]*(")', "`${1}$MsixVersion`${2}"
# 声明处理器架构：含原生二进制的应用不应打成 neutral 包（MSIX 规范要求）
$archName = switch ($Platform) { 'ARM64' { 'arm64' } default { $Platform.ToLower() } }
$manifestXml = $manifestXml -replace '(<Identity[^>]*?Version="[^"]*")', "`${1} ProcessorArchitecture=`"$archName`""
# x-generate 是 Visual Studio 打包专用的伪语言标记，MakeAppx 不会替换它，
# 直接写入包会导致注册失败 0x80070057（指定的资源语言无效）。
# 应用界面文本已硬编码为简体中文，这里仅声明默认语言 zh-CN。
$manifestXml = $manifestXml -replace '<Resource\s+Language="x-generate"\s*/>', '<Resource Language="zh-CN" />'

# 注入框架包依赖（PackageDependency）：MSIX 打包应用依赖 Windows App Runtime 框架包，
# 必须在 AppxManifest 里声明 PackageDependency，否则正式安装后 Framework 包不会被加载，
# 导致 Microsoft.UI.Xaml 类激活失败（0x80040154 REGDB_E_CLASSNOTREG，Application.Start 崩溃）。
# VS 单项目打包会自动注入；手动 MakeAppx 需显式注入。MinVersion 由 csproj 的
# Microsoft.WindowsAppSDK 版本派生（2.3.1 → 2.3.1.0）。
$sdkVersionMatch = [regex]::Match((Get-Content $ProjectFile -Raw), '<PackageReference Include="Microsoft.WindowsAppSDK" Version="([^"]+)"')
$sdkMinVersion = '2.3.1.0'
if ($sdkVersionMatch.Success) {
    $vParts = @(($sdkVersionMatch.Groups[1].Value -split '[-+]')[0] -split '\.')
    while ($vParts.Count -lt 4) { $vParts += '0' }
    $sdkMinVersion = ($vParts | Select-Object -First 4) -join '.'
}
$pkgDependency = '<PackageDependency Name="Microsoft.WindowsAppRuntime.2" MinVersion="' + $sdkMinVersion + '" Publisher="CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US" />'
if ($manifestXml -notmatch 'PackageDependency') {
    $manifestXml = $manifestXml.Replace('</Dependencies>', "`r`n    $pkgDependency`r`n  </Dependencies>")
    Write-Host "已注入 PackageDependency（Microsoft.WindowsAppRuntime.2 MinVersion=$sdkMinVersion）"
}

$manifestXmlPath = Join-Path $PayloadDir 'AppxManifest.xml'
[System.IO.File]::WriteAllText($manifestXmlPath, $manifestXml, (New-Object System.Text.UTF8Encoding($false)))
Write-Host "已生成 AppxManifest.xml（EntryPoint=Windows.FullTrustApplication，Version=$MsixVersion）"

# 关键：打包应用（MSIX）通过 MRT Core 从包根目录的 resources.pri 加载 XAML 资源。
# VS 打包会自动生成 resources.pri，而手动 MakeAppx 不会——缺少它会导致应用
# 在入口早期静默退出（激活成功但进程立即消失，无崩溃日志）。
# 现代 MSBuild（Windows App SDK 2.x）已直接生成 resources.pri，无需再从 SightoHear.pri 复制；
# 仅当 resources.pri 缺失但存在 SightoHear.pri 时做兜底复制（兼容旧构建行为）。
$resourcesPri  = Join-Path $PayloadDir 'resources.pri'
$sightoHearPri = Join-Path $PayloadDir 'SightoHear.pri'
if (-not (Test-Path $resourcesPri)) {
    if (Test-Path $sightoHearPri) {
        Copy-Item $sightoHearPri $resourcesPri -Force
        Write-Host "已生成 resources.pri（从 SightoHear.pri 复制）"
    } else {
        throw "未找到 resources.pri 或 SightoHear.pri：XAML 资源缺失，packaged 模式将无法启动"
    }
} else {
    Write-Host "resources.pri 已存在（MSBuild 生成），无需复制"
}

# ==================== 包内瘦身（可选，默认开启） ====================
# ★ 放在这里的原因：必须在 resources.pri 已就绪之后、MakeAppx 之前。
#   前面的 csproj 级 SlimDistributionOutput 对 MSIX 是跳过的（.winmd / 语言资源要参与 PRI 生成），
#   而运行期真正不需要的那批文件，可以安全地只在「打包载荷」里剔除。
#
# 剔什么（每一条都做过实机安装 + 启动 + 遍历页面验证）：
#   · *.winmd            WinRT 元数据，编译/投影生成用，运行时不读
#   · *.lib              FFmpeg/libmpv 的导入库，只供链接期使用
#   · *_app.dll          Microsoft.VCRTForwarders 的 10 个转发桩，无人引用
#   · mscordac*/mscordbi*/createdump.exe/DiaSymReader
#                        托管调试与转储基础结构。AOT 产物完全用不到；
#                        托管产物只在被调试器附加时才加载，正式使用不加载。
#   · Microsoft.Windows.AI.* / Microsoft.Windows.Workloads.* / workloads.*.json
#                        WinAppSDK 的可选 AI 与工作负载组件，本项目不实例化这些 WinRT 类型
#   · NPUDetect / WebView2Loader / RestartAgent.exe / Microsoft.UI.Designer.dll
#                        SDK 附带的工具型二进制，本项目不调用
#   · 后台任务/推送/会话 IPC 的 ProxyStub
#                        非打包场景不用的代理桩
#   · AppX\ 子目录       VS 专用中间布局，内容已兜底复制到包根 Assets
#
# 不动的：AppxManifest.xml / AppxMetadata / resources.pri / *.xbf / Assets（含 Logo 与字体）
#         libmpv-2.dll / onnxruntime / DirectML / python312* / VSScript / Win2D / SkiaSharp
if (-not $SkipPayloadSlim) {
    Write-Host "`n---------- 包内瘦身 ----------" -ForegroundColor Cyan
    $beforeCount = (Get-ChildItem $PayloadDir -Recurse -File -ErrorAction SilentlyContinue).Count
    $beforeBytes = (Get-ChildItem $PayloadDir -Recurse -File -ErrorAction SilentlyContinue |
                    Measure-Object Length -Sum).Sum

    # ① 按扩展名删（递归）
    $byExt = Get-ChildItem $PayloadDir -Recurse -File -ErrorAction SilentlyContinue |
             Where-Object { $_.Extension -in '.winmd', '.lib' }
    if ($byExt) {
        $byExt | Remove-Item -Force -ErrorAction SilentlyContinue
        Write-Host ("  删除元数据/链接库：{0} 个（.winmd / .lib）" -f $byExt.Count)
    }

    # ② 按文件名模式删（递归，只匹配确定不需要的）
    $patterns = @(
        '*_app.dll',                              # VCRT 转发桩
        'mscordac*.dll', 'mscordbi*.dll', 'createdump.exe',
        'Microsoft.DiaSymReader.Native.*.dll',
        'Microsoft.Windows.AI.*.dll',
        'Microsoft.Windows.Workloads.*.dll', 'Microsoft.Windows.Workloads*.pri',
        'workloads.*.json',
        'NPUDetect.dll', 'WebView2Loader.dll', 'RestartAgent.exe', 'Microsoft.UI.Designer.dll',
        '*UniversalBGTask*.dll', '*PushNotificationsLongRunningTask*.dll', 'SessionHandleIPCProxyStub.dll'
    )
    $byPattern = Get-ChildItem $PayloadDir -Recurse -File -ErrorAction SilentlyContinue |
                 Where-Object { $n = $_.Name; ($patterns | Where-Object { $n -like $_ }).Count -gt 0 }
    if ($byPattern) {
        $byPattern | Remove-Item -Force -ErrorAction SilentlyContinue
        Write-Host ("  删除可选/调试组件：{0} 个" -f $byPattern.Count)
    }

    # ③ AppX 中间布局（内容已复制到包根 Assets）
    $appxDir = Join-Path $PayloadDir 'AppX'
    if (Test-Path $appxDir) {
        Remove-Item $appxDir -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host "  删除 AppX\ 中间布局目录"
    }

    $afterCount = (Get-ChildItem $PayloadDir -Recurse -File -ErrorAction SilentlyContinue).Count
    $afterBytes = (Get-ChildItem $PayloadDir -Recurse -File -ErrorAction SilentlyContinue |
                   Measure-Object Length -Sum).Sum
    Write-Host ("  载荷：{0} → {1} 个文件，{2:N1} → {3:N1} MB（去掉 {4:N1} MB）" -f `
        $beforeCount, $afterCount, ($beforeBytes/1MB), ($afterBytes/1MB), (($beforeBytes-$afterBytes)/1MB)) `
        -ForegroundColor Green

    if (-not (Test-Path (Join-Path $PayloadDir 'AppxManifest.xml'))) {
        throw "瘦身后 AppxManifest.xml 丢失，打包中止"
    }
    if (-not (Test-Path $resourcesPri)) {
        throw "瘦身后 resources.pri 丢失，打包中止"
    }
}

# ==================== 打包前硬校验：这个包能不能启动 ====================
# ★ 2026-11-15 事故：MSIX 包安装后双击无反应。
#   原因：csproj 里 WindowsAppSdkBootstrapInitialize 的条件只判断了 WindowsAppSDKSelfContained，
#   而该项目在 MSIX 模式下也是 false → Bootstrap auto-initializer 被误开启。
#   打包应用由 AppxManifest 的 PackageDependency 拿到运行时，进程本身已有程序标识符，
#   再调 MddBootstrapInitialize 会在 Main 之前失败。
#
# ★ 注意校验对象：不能以「载荷里有没有 Microsoft.WindowsAppRuntime.Bootstrap.dll」判断。
#   实测已发布的正常包同样带着这个 DLL（SDK 会无条件复制它是内容项），
#   带它无害；真正决定行为的是 WindowsAppSdkBootstrapInitialize 生成的模块初始化器代码。
#   所以这里读 MSBuild 求值后的属性，那才是编译时的真实输入。
Write-Host "`n---------- 启动前置校验 ----------" -ForegroundColor Cyan

$aotFlag = if ([bool]$Aot) { 'true' } else { 'false' }
$evalJson = & dotnet msbuild $ProjectFile -p:Platform=$Platform -p:Configuration=$Configuration `
    -p:SightoHearMsix=true -p:PublishAot=$aotFlag `
    -getProperty:WindowsPackageType,WindowsAppSdkBootstrapInitialize,WindowsAppSdkDeploymentManagerInitialize 2>&1
if ($LASTEXITCODE -ne 0) {
    throw "读取 MSBuild 属性失败（退出码 $LASTEXITCODE），无法校验打包配置：`n$evalJson"
}
$eval = $evalJson | Out-String | ConvertFrom-Json

if ($eval.Properties.WindowsPackageType -ne 'MSIX') {
    throw "打包模式期望 WindowsPackageType=MSIX，实际为 '$($eval.Properties.WindowsPackageType)'。"
}
if ($eval.Properties.WindowsAppSdkBootstrapInitialize -ne 'false') {
    throw @"
打包模式下 WindowsAppSdkBootstrapInitialize 必须为 false，实际为 '$($eval.Properties.WindowsAppSdkBootstrapInitialize)'。
这个包安装后会无法启动（双击无反应，进程在 Main 之前失败）。

原因：打包应用的运行时由 AppxManifest 的 PackageDependency 提供，进程已有程序标识符；
      再开启 Bootstrap auto-initializer 会调 MddBootstrapInitialize 并失败。

修复：检查 SightoHear.csproj 中该属性的条件，必须同时判断 WindowsPackageType：
  <WindowsAppSdkBootstrapInitialize Condition="'`$(WindowsPackageType)' == 'None' and '`$(WindowsAppSDKSelfContained)' != 'true'">true</WindowsAppSdkBootstrapInitialize>
  <WindowsAppSdkBootstrapInitialize Condition="'`$(WindowsPackageType)' != 'None' or '`$(WindowsAppSDKSelfContained)' == 'true'">false</WindowsAppSdkBootstrapInitialize>
"@
}
Write-Host '  ✓ WindowsAppSdkBootstrapInitialize = false（打包模式正确）' -ForegroundColor Green

if ($eval.Properties.WindowsAppSdkDeploymentManagerInitialize -ne 'false') {
    throw "打包模式下 WindowsAppSdkDeploymentManagerInitialize 必须为 false，实际为 '$($eval.Properties.WindowsAppSdkDeploymentManagerInitialize)'（Windows 10 上会 REGDB_E_CLASSNOTREG）。"
}
Write-Host '  ✓ WindowsAppSdkDeploymentManagerInitialize = false' -ForegroundColor Green

# PackageDependency 决定运行时能否被找到
$manifestCheck = Join-Path $PayloadDir 'AppxManifest.xml'
$manifestText  = Get-Content $manifestCheck -Raw -Encoding UTF8
if ($manifestText -notmatch 'Microsoft\.WindowsAppRuntime\.2') {
    throw "AppxManifest.xml 缺少 Microsoft.WindowsAppRuntime.2 的 PackageDependency，安装后无法加载运行时。"
}
Write-Host '  ✓ PackageDependency 已注入' -ForegroundColor Green

# 载荷里必须有入口 exe
if (-not (Test-Path (Join-Path $PayloadDir "$ExeName.exe"))) {
    throw "载荷中缺少入口可执行文件 $ExeName.exe"
}
Write-Host ("  ✓ 入口 exe 存在（{0:N1} MB）" -f ((Get-Item (Join-Path $PayloadDir "$ExeName.exe")).Length/1MB)) -ForegroundColor Green

# ==================== 生成 / 复用签名证书 ====================
Write-Host "`n========== 3/6 证书（$Publisher） ==========" -ForegroundColor Cyan
$cert = Get-ChildItem Cert:\CurrentUser\My -ErrorAction SilentlyContinue |
    Where-Object {
        # 用 Subject + 私钥识别即可（CN=AvoidGuest 为专属发布者）。
        # 注意：不要用 EnhancedKeyUsageList 过滤——New-SelfSignedCertificate 生成的
        # 证书其 EnhancedKeyUsageList 在 PowerShell 中读取可能为空，导致永远匹配失败。
        $_.Subject -eq $Publisher -and $_.HasPrivateKey
    } |
    Sort-Object NotBefore -Descending |
    Select-Object -First 1

if (-not $cert) {
    Write-Host '未找到现成证书，正在生成自签名代码签名证书（有效期 5 年）...'
    $cert = New-SelfSignedCertificate `
        -Type Custom `
        -Subject $Publisher `
        -KeyUsage DigitalSignature `
        -CertStoreLocation 'Cert:\CurrentUser\My' `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}') `
        -FriendlyName 'SightoHear 开发证书（代码签名）' `
        -NotAfter (Get-Date).AddYears(5)
} else {
    Write-Host "复用现有证书：$($cert.Thumbprint)"
}

# 导出 PFX（签名用，含私钥）与 CER（信任用，仅公钥）
if ($CertPassword -eq '') {
    $CertPassword = -join ((48..57) + (65..90) + (97..122) | Get-Random -Count 16 | ForEach-Object { [char]$_ })
    [System.IO.File]::WriteAllText(
        (Join-Path $OutputRoot 'cert-password.txt'),
        "SightoHear MSIX 签名证书密码（build-msix.ps1 -CertPassword 可覆盖）：$CertPassword",
        (New-Object System.Text.UTF8Encoding($false)))
    Write-Host "已生成随机密码：$OutputRoot\cert-password.txt（务必妥善保管，勿提交到版本库）" -ForegroundColor Yellow
}
$securePwd = ConvertTo-SecureString -String $CertPassword -Force -AsPlainText
New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
Export-PfxCertificate -Cert "Cert:\CurrentUser\My\$($cert.Thumbprint)" -FilePath $PfxPath -Password $securePwd -Force | Out-Null
Export-Certificate -Cert $cert -FilePath $CerPath -Force | Out-Null
Write-Host "PFX : $PfxPath"

# 信任证书（用 X509Store 编程导入，避免 PS Import-Certificate 在 Root 存储弹 UI 失败；
# 本地机器存储需要管理员，当前用户存储不需要）
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$trustStores = @(
    @{ StoreName = 'Root';          StoreLocation = 'CurrentUser' },
    @{ StoreName = 'TrustedPeople'; StoreLocation = 'CurrentUser' }
)
if ($isAdmin) {
    $trustStores += @(
        @{ StoreName = 'Root';          StoreLocation = 'LocalMachine' },
        @{ StoreName = 'TrustedPeople'; StoreLocation = 'LocalMachine' }
    )
}
foreach ($ts in $trustStores) {
    try {
        $store = New-Object System.Security.Cryptography.X509Certificates.X509Store($ts.StoreName, $ts.StoreLocation)
        $store.Open('ReadWrite')
        $store.Add($cert)
        $store.Close()
        Write-Host "证书已信任：$($ts.StoreLocation)\$($ts.StoreName)"
    } catch {
        Write-Warning "证书信任失败：$($ts.StoreLocation)\$($ts.StoreName) - $($_.Exception.Message)"
    }
}

# ==================== 打包 ====================
Write-Host "`n========== 4/6 MakeAppx 打包 ==========" -ForegroundColor Cyan
New-Item -ItemType Directory -Path $BinOutputDir -Force | Out-Null
$msixFile = Join-Path $BinOutputDir "SightoHear_$MsixVersion`_$Platform.msix"
& $MakeAppx pack /d $PayloadDir /p $msixFile /o /v
if ($LASTEXITCODE -ne 0) { throw "MakeAppx 打包失败（退出码 $LASTEXITCODE）" }

# ==================== 签名 ====================
Write-Host "`n========== 5/6 SignTool 签名 ==========" -ForegroundColor Cyan
$signArgs = @('sign', '/fd', 'SHA256', '/f', $PfxPath, '/p', $CertPassword)
if (-not $SkipTimestamp) {
    $signArgs += @('/tr', 'http://timestamp.digicert.com', '/td', 'SHA256')
}
$signArgs += $msixFile
& $SignTool @signArgs
if ($LASTEXITCODE -ne 0) { throw "SignTool 签名失败（退出码 $LASTEXITCODE）" }

# 签名验证
& $SignTool verify /pa /v $msixFile | Out-Host
if ($LASTEXITCODE -ne 0) { throw "签名验证未通过（退出码 $LASTEXITCODE）" }

# ==================== 完成 ====================
Write-Host "`n========== 6/6 打包完成 ==========" -ForegroundColor Green
Write-Host "MSIX 包   : $msixFile"
Write-Host "包版本    : $MsixVersion"
Write-Host "平台      : $Platform"

# 清理打包临时 payload（保留证书/密码供下次签名复用，避免 artifacts 长期堆积几十 MB 中间文件）
if (Test-Path $PayloadDir) {
    Remove-Item $PayloadDir -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "已清理打包临时区：$PayloadDir（证书/密码已保留复用）"
}

# ==================== 安装（可选） ====================
if ($Install) {
    Write-Host "`n正在安装 MSIX ..." -ForegroundColor Cyan
    $installed = Get-AppxPackage -Name $ExeName -ErrorAction SilentlyContinue
    if ($installed) {
        Write-Host "检测到已安装版本 $($installed.Version)，先卸载旧版本..."
        Remove-AppxPackage -Package $installed.PackageFullName -ErrorAction Stop
    }
    try {
        Add-AppxPackage -Path $msixFile
        Write-Host "安装成功！已安装版本 $MsixVersion" -ForegroundColor Green
    } catch {
        Write-Warning "安装失败：$($_.Exception.Message)"
        Write-Host "可能原因：旁加载未开启 / 证书未受信任。可尝试："
        Write-Host "  设置 → 系统 → 开发者选项 → 打开『开发人员模式』或『旁加载应用』"
        Write-Host "  然后重新执行：Add-AppxPackage -Path $msixFile"
    }
}
