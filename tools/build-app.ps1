#requires -Version 5.1
<#
.SYNOPSIS
    编译「用户真正双击的那个」可执行文件：Release 版，原地输出。

.DESCRIPTION
    桌面快捷方式 C:\Users\<用户>\Desktop\GameOptimizer.lnk 指向
        src/GameOptimizer.App/bin/Release/net8.0-windows/GameOptimizer.App.exe
    也就是说，只有 `-c Release` 原地编译，用户点快捷方式才会看到新版本。

    只编 Debug（bin/Debug）、只编到 .artifacts、或只提交代码，都不会改变快捷方式指向的文件。
    这类「改完打开还是旧版本」在 v2.5.1 当天连着踩了两次，所以固化成脚本。

    脚本做四件事：
      1. 先检查有没有实例在跑 —— 在跑会锁住输出目录（MSB3027），直接报错并提示怎么退出；
      2. Release 编译到默认输出目录（不指定 OutputPath，避免相对路径被按项目目录解析）；
      3. 回读产物的 FileVersion 与时间戳，和 Directory.Build.props 里的版本比对；
      4. -Run 时编译完直接启动（会弹 UAC，程序清单要求管理员权限）。

.PARAMETER Configuration
    默认 Release —— 快捷方式指向的就是这一份。改 Debug 只用于临时排查。

.PARAMETER Run
    编译完成后启动程序。

.EXAMPLE
    pwsh -File tools/build-app.ps1
    pwsh -File tools/build-app.ps1 -Run
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',

    [switch]$Run
)

$ErrorActionPreference = 'Stop'

# dotnet 的输出是中文：不先统一编码，Windows PowerShell 5.1 下会读成乱码
# （默认 gb2312），pwsh 7 下也可能把 dotnet 写的 GBK 当成 UTF-8。
try {
    [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
    $OutputEncoding = [System.Text.Encoding]::UTF8
}
catch {
    # 重定向到文件时没有控制台句柄，忽略即可
}

# ── 编码自检 ───────────────────────────────────────────────────────────────
# tools 下的脚本一律带 UTF-8 BOM：Windows PowerShell 5.1 无 BOM 时按 ANSI 代码页读，
# 中文输出会变乱码。
$selfBytes = [System.IO.File]::ReadAllBytes($PSCommandPath)
if (-not ($selfBytes.Length -ge 3 -and $selfBytes[0] -eq 0xEF -and $selfBytes[1] -eq 0xBB -and $selfBytes[2] -eq 0xBF)) {
    throw "$($MyInvocation.MyCommand.Name) 必须保存为 UTF-8 with BOM"
}

$repo = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $repo 'src\GameOptimizer.App\GameOptimizer.App.csproj'
$outDir = Join-Path $repo "src\GameOptimizer.App\bin\$Configuration\net8.0-windows"
$exe = Join-Path $outDir 'GameOptimizer.App.exe'
$core = Join-Path $outDir 'GameOptimizer.Core.dll'
$props = Join-Path $repo 'Directory.Build.props'

# ── 1. 有没有实例在跑 ──────────────────────────────────────────────────────
$running = @(Get-Process -Name 'GameOptimizer*' -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    $ids = ($running | ForEach-Object { $_.Id }) -join ', '
    throw "还有实例在运行（PID $ids），输出目录被锁住没法覆盖。`n" +
          "请到程序里点右上角 × →「退出程序」（**不要**选缩小到托盘，那样进程还在），然后重跑本脚本。"
}

# ── 2. 编译 ────────────────────────────────────────────────────────────────
Write-Host "编译 $Configuration ..." -ForegroundColor Cyan
dotnet build $proj -c $Configuration --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "编译失败（dotnet 退出码 $LASTEXITCODE）" }

# ── 3. 回读产物 ────────────────────────────────────────────────────────────
if (-not (Test-Path $exe)) { throw "编译成功但没找到产物：$exe" }

$built = (Get-Item $exe).VersionInfo.FileVersion
$coreVer = (Get-Item $core).VersionInfo.FileVersion
$propsVer = ([xml](Get-Content $props -Raw)).Project.PropertyGroup.Version

Write-Host ''
Write-Host "产物：$exe" -ForegroundColor Green
Write-Host "  FileVersion   : $built（$((Get-Item $exe).LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss'))）"
Write-Host "  Core 版本     : $coreVer"
Write-Host "  props 声明    : $propsVer"

if ($built -ne "$propsVer.0" -and $built -ne $propsVer) {
    Write-Warning "产物版本 $built 与 Directory.Build.props 的 $propsVer 不一致——先跑 tools/bump-version.ps1，或确认没有残留的旧 dll。"
}

# ── 4. 启动 ────────────────────────────────────────────────────────────────
if ($Run) {
    Write-Host "启动中（会弹 UAC）..." -ForegroundColor Cyan
    Start-Process -FilePath $exe
}
else {
    Write-Host ''
    Write-Host "桌面快捷方式无需改动，它指向的就是这个文件。直接双击即可。" -ForegroundColor Gray
}
