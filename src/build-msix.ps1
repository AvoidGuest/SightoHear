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
    [string]$CertPassword = ""
)

$ErrorActionPreference = 'Stop'

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
    dotnet build $ProjectFile -c $Configuration /p:Platform=$Platform -v minimal
    if ($LASTEXITCODE -ne 0) { throw "构建失败（退出码 $LASTEXITCODE）" }
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

# 复制全部输出（排除 obj 中间产物与 *.pdb 调试符号）
Get-ChildItem $ArtifactsBuildDir -Force | Where-Object { $_.Name -ne 'obj' } |
    Copy-Item -Destination $PayloadDir -Recurse -Force
Get-ChildItem $PayloadDir -Recurse -Filter '*.pdb' -ErrorAction SilentlyContinue | Remove-Item -Force

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
