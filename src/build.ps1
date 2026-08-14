<#
.SYNOPSIS
SightoHear 项目标准构建脚本。默认使用 Debug | x64。

.DESCRIPTION
为避免误选平台，本项目推荐统一使用此脚本进行构建。
若需 Release 版本，请显式指定 -Configuration Release。

.PARAMETER Configuration
构建配置，仅支持 Debug 或 Release，默认 Debug。

.EXAMPLE
.\build.ps1
.\build.ps1 -Configuration Release
#>
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug"
)

if ($Configuration -eq "Release") {
    Write-Warning "【编译提醒】Release 编译通常不需要，默认推荐使用 Debug 配置。"
}

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "【标准构建】Configuration: $Configuration | Platform: x64" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

dotnet build "$PSScriptRoot\SightoHear.csproj" -c $Configuration /p:Platform=x64 -v minimal
