# 授予 PresentMon 采集帧时间所需的 ETW 权限
#
# 背景：PresentMon 启动 ETW 跟踪会话需要「管理员」或「Performance Log Users」组成员身份。
#       Performance Log Users 的权限远小于管理员，仅能启动性能追踪会话，推荐用这个。
#
# 用法：右键 PowerShell -> 以管理员身份运行，然后执行
#       powershell -ExecutionPolicy Bypass -File <本文件路径>
#
# 撤销：见文件末尾的 Remove-FromGroup 调用（注释掉的那个）

$ErrorActionPreference = 'Stop'

$groupName = 'Performance Log Users'
$userName = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name -replace '^.*\\', ''

# 必须是管理员
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host '[失败] 需要管理员权限。请右键 PowerShell -> 以管理员身份运行后重试。' -ForegroundColor Red
    exit 1
}

Write-Host "当前用户: $userName"
Write-Host "目标组:   $groupName"

# 已存在则跳过
$members = @(net localgroup $groupName 2>&1 | Select-Object -Skip 6)
if ($members -match "^\s*$userName\s*$") {
    Write-Host '[跳过] 用户已在组中，无需重复添加。' -ForegroundColor Yellow
    exit 0
}

$result = net localgroup $groupName $userName /add 2>&1 | Out-String
if ($LASTEXITCODE -ne 0) {
    Write-Host "[失败] 添加失败：$($result.Trim())" -ForegroundColor Red
    exit 1
}

Write-Host '[成功] 已加入组。' -ForegroundColor Green
Write-Host ''
Write-Host '重要：组成员身份需要重新登录才生效（注销后重新登录，或重启）。' -ForegroundColor Yellow
Write-Host ''
Write-Host '验证方式：'
Write-Host '  whoami /groups | findstr "Performance Log Users"'
Write-Host ''
Write-Host '撤销方式（管理员 PowerShell）：'
Write-Host "  net localgroup `"$groupName`" $userName /delete"
