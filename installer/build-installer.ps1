<#
.SYNOPSIS
    构建「游戏优化助手」Windows 安装程序（Inno Setup）。

.DESCRIPTION
    一条命令走完：读版本号 → dotnet build / test → 单文件自包含发布 → ISCC 编译 → 校验产物 → 刷新 SHA256SUMS.txt。

    版本号的唯一来源是仓库根的 Directory.Build.props，脚本与 .iss 都不硬编码版本，
    所以升级版本只需要改那一个文件。

    刻意不做的事：不自动更新、不检查在线版本、不打 Git tag、不上传任何东西、不碰用户数据。

.PARAMETER SkipTests
    跳过 dotnet build / dotnet test（只想重新封装安装包时用）。

.PARAMETER SkipPublish
    跳过 dotnet publish，直接复用 publish\win-x64\app 里已有的发布产物。

.PARAMETER IsccPath
    手动指定 ISCC.exe。默认按「PATH → 卸载注册表 → 常见安装目录」顺序自动查找。

.EXAMPLE
    pwsh -File installer\build-installer.ps1

.EXAMPLE
    pwsh -File installer\build-installer.ps1 -SkipTests -SkipPublish
#>
[CmdletBinding()]
param(
    [switch]$SkipTests,
    [switch]$SkipPublish,
    [string]$IsccPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot    = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$publishDir  = Join-Path $repoRoot 'publish\win-x64'
$stageDir    = Join-Path $publishDir 'app'
$issPath     = Join-Path $PSScriptRoot 'GameOptimizer.iss'
$appProject  = Join-Path $repoRoot 'src\GameOptimizer.App\GameOptimizer.App.csproj'
$slnPath     = Join-Path $repoRoot 'GameOptimizer.sln'
$exeName     = 'GameOptimizer.exe'
$minSetupSize = 20MB

function Write-Step([string]$Text) {
    Write-Host ''
    Write-Host "==> $Text" -ForegroundColor Cyan
}

function Assert-LastExitCode([string]$What) {
    if ($LASTEXITCODE -ne 0) {
        throw "$What 失败（退出码 $LASTEXITCODE）。"
    }
}

function Find-Iscc {
    $candidates = New-Object System.Collections.Generic.List[string]

    $cmd = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
    if ($cmd -and $cmd.Source) { $candidates.Add($cmd.Source) }

    $uninstallKeys = @(
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1',
        'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1'
    )
    foreach ($key in $uninstallKeys) {
        if (-not (Test-Path $key)) { continue }
        $location = (Get-ItemProperty -Path $key -ErrorAction SilentlyContinue).InstallLocation
        if ($location) { $candidates.Add((Join-Path $location 'ISCC.exe')) }
    }

    $programDirs = @(
        [Environment]::GetEnvironmentVariable('ProgramFiles'),
        [Environment]::GetEnvironmentVariable('ProgramFiles(x86)'),
        [Environment]::GetEnvironmentVariable('LOCALAPPDATA')
    )
    foreach ($dir in $programDirs) {
        if (-not $dir) { continue }
        $candidates.Add((Join-Path $dir 'Programs\Inno Setup 6\ISCC.exe'))
        $candidates.Add((Join-Path $dir 'Inno Setup 6\ISCC.exe'))
    }

    foreach ($candidate in $candidates) {
        if (Test-Path $candidate) { return $candidate }
    }
    return $null
}

# ---------------------------------------------------------------- 1. 版本号
Write-Step '读取版本号（Directory.Build.props）'

$propsPath = Join-Path $repoRoot 'Directory.Build.props'
if (-not (Test-Path $propsPath)) { throw "找不到 $propsPath" }

[xml]$props = Get-Content -Path $propsPath -Encoding UTF8
$versionNode = $props.SelectSingleNode('/Project/PropertyGroup/Version')
if ($null -eq $versionNode -or [string]::IsNullOrWhiteSpace($versionNode.InnerText)) {
    throw 'Directory.Build.props 里没有 <Version>，无法确定安装包版本号。'
}
$version = $versionNode.InnerText.Trim()
Write-Host "    版本：$version"

# ------------------------------------------------------- 2. 构建 + 测试
if (-not $SkipTests) {
    Write-Step 'dotnet build -c Release'
    & dotnet build $slnPath -c Release --nologo
    Assert-LastExitCode 'dotnet build'

    Write-Step 'dotnet test -c Release'
    & dotnet test $slnPath -c Release --nologo --no-build
    Assert-LastExitCode 'dotnet test'
}

# --------------------------------------------------- 3. 单文件自包含发布
if (-not $SkipPublish) {
    Write-Step 'dotnet publish（win-x64 自包含单文件）'

    if (Test-Path $stageDir) { Remove-Item -Path $stageDir -Recurse -Force }
    New-Item -ItemType Directory -Path $stageDir -Force | Out-Null

    # 参数沿用 v2.4.0 的发布方式：漏掉 IncludeNativeLibrariesForSelfExtract
    # 会散出 wpfgfx_cor3.dll 等原生库，就不是单文件了。
    & dotnet publish $appProject -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -p:DebugType=none -p:DebugSymbols=false `
        -o $stageDir --nologo
    Assert-LastExitCode 'dotnet publish'
}

# ------------------------------------------- 4. 整理暂存目录（只留单文件）
Write-Step '检查发布产物'

$publishedExe = Join-Path $stageDir 'GameOptimizer.App.exe'
$stagedExe    = Join-Path $stageDir $exeName

if (Test-Path $publishedExe) {
    if (Test-Path $stagedExe) { Remove-Item -Path $stagedExe -Force }
    Move-Item -Path $publishedExe -Destination $stagedExe
}
elseif (-not (Test-Path $stagedExe)) {
    throw "没有找到发布产物：$publishedExe（先去掉 -SkipPublish 跑一次）"
}

$extraFiles = @(Get-ChildItem -Path $stageDir -Recurse -File |
    Where-Object { $_.FullName -ne (Resolve-Path $stagedExe).Path })
if ($extraFiles.Count -gt 0) {
    Write-Host '    提示：暂存目录里还有下列文件，会一并打进安装包：' -ForegroundColor Yellow
    $extraFiles | ForEach-Object { Write-Host "      $($_.Name)" }
}

$exeVersion = (Get-Item $stagedExe).VersionInfo.FileVersion
if (-not $exeVersion -or -not $exeVersion.StartsWith($version)) {
    throw "发布产物版本（$exeVersion）与 Directory.Build.props（$version）不一致。"
}
Write-Host ("    主程序：{0}  （{1:N1} MB，文件版本 {2}）" -f $exeName, ((Get-Item $stagedExe).Length / 1MB), $exeVersion)

# 旧版本号首次构建时补一份免安装单文件；已存在则不动（避免改动已发布的产物）
$portable = Join-Path $publishDir "GameOptimizer-v$version-win-x64.exe"
if (-not (Test-Path $portable)) {
    Copy-Item -Path $stagedExe -Destination $portable
    Write-Host "    已补出免安装单文件：$(Split-Path -Leaf $portable)"
}

# ------------------------------------------------------------ 5. 编译安装包
Write-Step '编译安装程序（Inno Setup）'

if (-not $IsccPath) { $IsccPath = Find-Iscc }
if (-not $IsccPath) {
    throw @'
没有找到 Inno Setup 编译器（ISCC.exe）。
请先安装：winget install --id JRSoftware.InnoSetup --accept-package-agreements --accept-source-agreements
或用 -IsccPath 指定：pwsh -File installer\build-installer.ps1 -IsccPath "C:\路径\ISCC.exe"
'@
}
Write-Host "    编译器：$IsccPath"

if (-not (Test-Path $publishDir)) { New-Item -ItemType Directory -Path $publishDir -Force | Out-Null }

& $IsccPath "/DMyAppVersion=$version" "/DStageDir=$stageDir" "/DOutputDir=$publishDir" $issPath '/Qp'
Assert-LastExitCode 'ISCC 编译'

# ------------------------------------------------------------- 6. 校验产物
Write-Step '校验安装程序'

$setupPath = Join-Path $publishDir "GameOptimizer-Setup-v$version.exe"
if (-not (Test-Path $setupPath)) { throw "ISCC 报告成功，但安装程序不存在：$setupPath" }

$setup = Get-Item $setupPath
if ($setup.Length -lt $minSetupSize) {
    throw "安装程序大小异常（$([math]::Round($setup.Length / 1MB, 1)) MB），未达到 $($minSetupSize / 1MB) MB。"
}

$setupVersion = $setup.VersionInfo.FileVersion
if (-not $setupVersion -or -not $setupVersion.StartsWith($version)) {
    throw "安装程序版本（$setupVersion）与 $version 不一致。"
}

# ------------------------------------------------- 7. 刷新 SHA256SUMS.txt
Write-Step '刷新 SHA256SUMS.txt'

$sumFile = Join-Path $publishDir 'SHA256SUMS.txt'
$lines = @()
foreach ($file in (Get-ChildItem -Path $publishDir -Filter '*.exe' -File | Sort-Object Name)) {
    $hash = (Get-FileHash -Path $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $lines += ('{0} *{1}' -f $hash, $file.Name)
}
Set-Content -Path $sumFile -Value $lines -Encoding ASCII
$lines | ForEach-Object { Write-Host "    $_" }

# ---------------------------------------------------------------- 汇总
$setupHash = (Get-FileHash -Path $setupPath -Algorithm SHA256).Hash.ToLowerInvariant()

Write-Host ''
Write-Host '完成。' -ForegroundColor Green
Write-Host "  安装程序：$setupPath"
Write-Host ("  大小：{0:N1} MB" -f ($setup.Length / 1MB))
Write-Host "  版本：$setupVersion（源：Directory.Build.props <Version>$version</Version>）"
Write-Host "  SHA256：$setupHash"
Write-Host "  校验文件：$sumFile"
Write-Host ''
Write-Host '安装包未做代码签名，首次运行时 Windows SmartScreen 可能提示「已保护你的电脑」→「更多信息」→「仍要运行」。' -ForegroundColor Yellow
