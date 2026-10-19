param(
    [string]$GroupName = 'Performance Log Users'
)

# 授予 PresentMon 采集帧时间所需的 ETW 权限
#
# 背景：PresentMon 启动 ETW 跟踪会话需要「管理员」或「Performance Log Users」组成员身份。
#       Performance Log Users 权限远小于管理员，仅能启动性能跟踪会话，推荐用这个。
#
# 用法：右键 PowerShell -> 以管理员身份运行，然后执行
#       powershell -ExecutionPolicy Bypass -File "<本文件路径>"
#
# 撤销：net localgroup "Performance Log Users" <你的用户名> /delete
#
# 注意：本文件必须保存为 UTF-8 with BOM。Windows PowerShell 5.1 会把无 BOM 的
#       UTF-8 文件按 GBK 解码，导致中文乱码并引发 ParserError。

$ErrorActionPreference = 'Stop'

# ---- 编码自检：防止被错误解码后继续执行出一堆费解的错误 ----
$probe = '中文编码自检'
if ($probe.Length -ne 6) {
    Write-Host '[失败] 脚本被错误解码（乱码）。请用 PowerShell 7 执行，或把文件另存为 UTF-8 with BOM。' -ForegroundColor Red
    exit 1
}

$identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
$userName = $identity.Name -replace '^.*\\', ''

$isAdmin = ([Security.Principal.WindowsPrincipal]$identity).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host '[失败] 需要管理员权限。请右键 PowerShell -> 以管理员身份运行后重试。' -ForegroundColor Red
    exit 1
}

Write-Host "当前用户: $userName"
Write-Host "目标组:   $GroupName"
Write-Host "PowerShell 版本: $($PSVersionTable.PSVersion)"
Write-Host ''

# 组是否存在
$group = Get-LocalGroup -Name $GroupName -ErrorAction SilentlyContinue
if (-not $group) {
    Write-Host "[失败] 本机不存在「$GroupName」组。" -ForegroundColor Red
    exit 1
}

# 已在组中则跳过
$existing = Get-LocalGroupMember -Group $GroupName -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match "\\$([regex]::Escape($userName))$" }

if ($existing) {
    Write-Host '[跳过] 用户已在组中，无需重复添加。' -ForegroundColor Yellow
} else {
    try {
        Add-LocalGroupMember -Group $GroupName -Member $userName -ErrorAction Stop
        Write-Host '[成功] 已加入组。' -ForegroundColor Green
    }
    catch {
        Write-Host "[失败] 添加失败：$($_.Exception.Message)" -ForegroundColor Red
        exit 1
    }
}

# 回读确认
$verify = Get-LocalGroupMember -Group $GroupName -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match "\\$([regex]::Escape($userName))$" }

if (-not $verify) {
    Write-Host '[失败] 回读确认时未找到该成员，操作可能未生效。' -ForegroundColor Red
    exit 1
}

Write-Host ''
Write-Host '[验证] 回读确认成功，成员已存在于组中。' -ForegroundColor Green
Write-Host ''
Write-Host '重要：组成员身份需要重新登录才生效（注销后重新登录，或重启）。' -ForegroundColor Yellow
Write-Host ''
Write-Host '重新登录后用下面的命令确认：'
Write-Host '  whoami /groups | findstr "Performance Log Users"'
Write-Host ''
Write-Host '撤销方式（管理员 PowerShell）：'
Write-Host "  net localgroup `"$GroupName`" $userName /delete"
