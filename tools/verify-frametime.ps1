# 验证 PresentMon 的 stdout 流式读取是否可用
#
# 用法：以管理员身份运行
#   powershell -ExecutionPolicy Bypass -File tools\verify-frametime.ps1
#
# 目的：主程序从 PresentMon 的 stdout 实时读帧数据（因为 --output_file 的 CSV
#       在采集期间被独占，外部读不到）。本脚本用同样方式验证这条链路能否进数据。

param(
    [string]$ProcessName = '',
    [int]$Seconds = 5
)

$ErrorActionPreference = 'Stop'

if ($PSVersionTable.PSVersion.Major -lt 6 -and $PSVersionTable.PSVersion.Major -ne 7) {
    # 兼容 5.1
}

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    Write-Host '[提示] 当前不是管理员。若采集失败，请用管理员身份重试。' -ForegroundColor Yellow
}

$pm = 'E:\dsh-插件\PresentMon\PresentMon-2.6.0-x64.exe'
if (-not (Test-Path $pm)) {
    Write-Host "[失败] 找不到 PresentMon：$pm" -ForegroundColor Red
    exit 1
}

# 没指定就自动找一个正在运行的游戏
if (-not $ProcessName) {
    $candidates = Get-Process |
        Where-Object { $_.ProcessName -match 'Shipping|VALORANT|Naraka' } |
        Select-Object -First 1

    if ($candidates) {
        $ProcessName = $candidates.ProcessName
        Write-Host "自动检测到游戏进程：$ProcessName" -ForegroundColor Green
    } else {
        Write-Host '[失败] 没有检测到运行中的游戏。请先启动游戏，或用 -ProcessName 指定。' -ForegroundColor Red
        exit 1
    }
}

Write-Host "采集目标：$ProcessName（$Seconds 秒）"
Write-Host ''

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $pm
$psi.Arguments = "--process_name $ProcessName.exe --output_stdout --timed $Seconds --no_console_stats --session_name VerifyFrameTime"
$psi.UseShellExecute = $false
$psi.CreateNoWindow = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true

$proc = [System.Diagnostics.Process]::Start($psi)

$header = $null
$frameTimeIndex = -1
$frames = New-Object System.Collections.ArrayList
$stderr = ''

# 逐行读，模拟主程序的处理方式
$reader = $proc.StandardOutput
while ($true) {
    $line = $reader.ReadLine()
    if ($null -eq $line) { break }

    if ($null -eq $header) {
        $header = $line
        $cols = $header.Split(',')
        for ($i = 0; $i -lt $cols.Count; $i++) {
            if ($cols[$i] -match '^(msBetweenPresents|FrameTime)') { $frameTimeIndex = $i; break }
        }
        Write-Host "表头列数：$($cols.Count)，帧时间列索引：$frameTimeIndex"
        continue
    }

    if ($frameTimeIndex -lt 0) { continue }

    $cols = $line.Split(',')
    if ($cols.Count -gt $frameTimeIndex) {
        $v = 0.0
        if ([double]::TryParse($cols[$frameTimeIndex], [ref]$v) -and $v -gt 0) {
            $frames.Add($v) | Out-Null
        }
    }
}

$proc.WaitForExit(15000) | Out-Null
$stderr = $proc.StandardError.ReadToEnd()

Write-Host ''
if ($frames.Count -gt 0) {
    $avg = ($frames | Measure-Object -Average).Average
    Write-Host "[成功] 收到 $($frames.Count) 帧，平均帧时间 $([math]::Round($avg,2)) ms ≈ $([math]::Round(1000/$avg,0)) FPS" -ForegroundColor Green
    Write-Host '结论：stdout 流式读取可用，主程序的采集链路没问题。'
    exit 0
}

Write-Host "[失败] 没有收到任何帧数据。" -ForegroundColor Red
if ($stderr) {
    Write-Host '--- PresentMon 输出 ---'
    Write-Host $stderr.Trim()
}
exit 1
