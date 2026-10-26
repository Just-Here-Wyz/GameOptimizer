#requires -Version 5.1
<#
.SYNOPSIS
    按版本规则自动推进版本号，并同步所有「硬编码版本号」的位置。

.DESCRIPTION
    规则见 docs/03-版本规则.md：

        feature  外观 / 功能性更新  → 次版本 +1，修订归零    2.4.1 → 2.5.0
        fix      仅修 bug           → 修订 +1                2.4.1 → 2.4.2
        major    大版本（用户明确要求）→ 主版本 +1，其余归零  2.4.1 → 3.0.0

    这里只做「机械同步」，一次改完下面这些地方（漏一处就会有人以为是另一个版本）：

        Directory.Build.props            Version / AssemblyVersion / FileVersion
        tests/.../VersionConsistencyTests.cs   固定断言（方法名 + 三个数字）
        docs/02-功能清单.md              标题
        docs/04-已知限制.md              标题
        docs/03-版本规则.md              「当前版本」一行
        docs/README.md                   首行「当前版本」
        README.md                        徽标行、版本状态行

    **不猜的内容**（需要人工补）：
        docs/06-变更记录.md  本次改动的小节标题（写清楚用户能看到什么）
        docs/README.md       版本历史表补一行
        README.md            下载链接与附件表 —— 只有真的打包上传后才换，
                             在那之前链接必须继续指向最近发布的那一版

.PARAMETER Kind
    feature（默认）| fix | major。

.PARAMETER Set
    直接指定目标版本（如 2.5.0），跳过自动计算。用于补录或回退。

.PARAMETER DryRun
    只打印将要写什么，不落盘。

.EXAMPLE
    pwsh -File tools/bump-version.ps1 -Kind feature
    pwsh -File tools/bump-version.ps1 -Kind fix -DryRun
    pwsh -File tools/bump-version.ps1 -Set 2.5.0
#>
[CmdletBinding()]
param(
    [ValidateSet('feature', 'fix', 'major')]
    [string]$Kind = 'feature',

    [string]$Set,

    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

# ── 编码自检 ───────────────────────────────────────────────────────────────
# 本脚本必须带 UTF-8 BOM：Windows PowerShell 5.1 无 BOM 时按 ANSI 代码页读，
# 中文注释会变乱码并连带把正则改坏（tools 下的脚本都踩过这个坑）。
$selfBytes = [System.IO.File]::ReadAllBytes($PSCommandPath)
$hasBom = $selfBytes.Length -ge 3 -and $selfBytes[0] -eq 0xEF -and $selfBytes[1] -eq 0xBB -and $selfBytes[2] -eq 0xBF
if (-not $hasBom) {
    Write-Warning '本脚本缺少 UTF-8 BOM，Windows PowerShell 5.1 下中文可能乱码。'
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$propsPath = Join-Path $repoRoot 'Directory.Build.props'

function Read-Text([string]$path) {
    return [System.IO.File]::ReadAllText($path)
}

function Write-Text([string]$path, [string]$text) {
    # 用 .NET API 直接写 UTF-8（不带 BOM）：.cs / .md 里不希望凭空多出 BOM
    if ($DryRun) {
        Write-Host "  [dry-run] 将写入 $path" -ForegroundColor DarkYellow
        return
    }
    [System.IO.File]::WriteAllText($path, $text, (New-Object System.Text.UTF8Encoding($false)))
}

function Get-NewLine([string]$text) {
    if ($text.Contains("`r`n")) { return "`r`n" }
    return "`n"
}

# ── 1. 读当前版本 ─────────────────────────────────────────────────────────
$propsText = Read-Text $propsPath
$versionMatch = [regex]::Match($propsText, '<Version>([\d]+\.[\d]+\.[\d]+)</Version>')
if (-not $versionMatch.Success) {
    throw "Directory.Build.props 里找不到形如 <Version>2.4.1</Version> 的定义"
}

$oldVersion = $versionMatch.Groups[1].Value
$parts = $oldVersion.Split('.')
$major = [int]$parts[0]
$minor = [int]$parts[1]
$patch = [int]$parts[2]

# ── 2. 算新版本 ───────────────────────────────────────────────────────────
if ($Set) {
    if ($Set -notmatch '^[\d]+\.[\d]+\.[\d]+$') {
        throw "-Set 需要形如 2.5.0 的三段式版本号，收到：$Set"
    }
    $newVersion = $Set
    $bumpNote = "按 -Set 指定"
}
else {
    switch ($Kind) {
        'feature' { $minor = $minor + 1; $patch = 0; $bumpNote = '功能性更新：次版本 +1，修订归零' }
        'fix' { $patch = $patch + 1; $bumpNote = '修 bug：修订 +1' }
        'major' { $major = $major + 1; $minor = 0; $patch = 0; $bumpNote = '大版本：主版本 +1，其余归零' }
    }
    $newVersion = "$major.$minor.$patch"
}

if ($newVersion -eq $oldVersion) {
    Write-Host "版本号未变化（$oldVersion），什么都没做。" -ForegroundColor Yellow
    return
}

# 最近发布的那一版：以 Git tag 为准（tag 存在 = 真的发布过）
$latestTag = ''
try {
    $latestTag = (git -C $repoRoot tag --list 'v*' --sort=-v:refname | Select-Object -First 1)
}
catch {
    $latestTag = ''
}
if ([string]::IsNullOrWhiteSpace($latestTag)) { $latestTag = 'v' + $oldVersion }

Write-Host ''
Write-Host "版本号：$oldVersion → $newVersion" -ForegroundColor Cyan
Write-Host "  规则：$bumpNote"
Write-Host "  最近发布（Git tag）：$latestTag"
Write-Host ''

$oldEsc = [regex]::Escape($oldVersion)
$changed = @()

# ── 3. Directory.Build.props ──────────────────────────────────────────────
$text = $propsText
$text = [regex]::Replace($text, "<Version>$oldEsc</Version>", "<Version>$newVersion</Version>")
$text = [regex]::Replace($text, "<AssemblyVersion>$oldEsc\.0</AssemblyVersion>", "<AssemblyVersion>$newVersion.0</AssemblyVersion>")
$text = [regex]::Replace($text, "<FileVersion>$oldEsc\.0</FileVersion>", "<FileVersion>$newVersion.0</FileVersion>")
if ($text -ne $propsText) {
    Write-Text $propsPath $text
    $changed += 'Directory.Build.props'
}

# ── 4. VersionConsistencyTests 的固定断言 ─────────────────────────────────
$testPath = Join-Path $repoRoot 'tests\GameOptimizer.Tests\VersionConsistencyTests.cs'
$testText = Read-Text $testPath
$nl = Get-NewLine $testText

$newMethod = "    public void 当前版本应为${major}_${minor}_${patch}()" + $nl +
"    {" + $nl +
"        // 版本号按 docs/03-版本规则.md 的规则自动推进（tools/bump-version.ps1 同步此处）。" + $nl +
"        // 保留这条硬断言：改了 props 却漏掉某个文件时，这里第一个变红。" + $nl +
"        AssemblyVersion.Major.Should().Be($major);" + $nl +
"        AssemblyVersion.Minor.Should().Be($minor);" + $nl +
"        AssemblyVersion.Build.Should().Be($patch);" + $nl +
"    }"

$methodPattern = '(?s)    public void 当前版本应为[\d_]+\(\)\s*\{.*?\r?\n    \}'
$methodMatch = [regex]::Match($testText, $methodPattern)
if (-not $methodMatch.Success) {
    throw "VersionConsistencyTests 里没找到「当前版本应为X_Y_Z」这条用例，脚本无法同步，请手工处理"
}

$testNew = $testText.Substring(0, $methodMatch.Index) + $newMethod +
           $testText.Substring($methodMatch.Index + $methodMatch.Length)
if ($testNew -ne $testText) {
    Write-Text $testPath $testNew
    $changed += 'VersionConsistencyTests.cs'
}

# ── 5~8. 文档与 README 里的版本字样 ───────────────────────────────────────
$targets = @(
    @{ Path = 'docs\02-功能清单.md'; Pattern = '# 功能清单（v[\d.]+）'; Replace = "# 功能清单（v$newVersion）" },
    @{ Path = 'docs\04-已知限制.md'; Pattern = '# 已知限制（v[\d.]+）'; Replace = "# 已知限制（v$newVersion）" },
    @{ Path = 'docs\03-版本规则.md'; Pattern = '\*\*v[\d.]+\*\* ——'; Replace = "**v$newVersion** ——" },
    @{ Path = 'docs\README.md'; Pattern = '> 当前版本：\*\*v[\d.]+\*\*'; Replace = "> 当前版本：**v$newVersion**" },
    @{ Path = 'README.md'; Pattern = '> \*\*v[\d.]+\*\* ｜'; Replace = "> **v$newVersion** ｜" },
    @{ Path = 'README.md'; Pattern = '源码 \*\*v[\d.]+\*\*'; Replace = "源码 **v$newVersion**" },
    @{ Path = 'README.md'; Pattern = '最近发布 v[\d.]+'; Replace = "最近发布 $latestTag" }
)

foreach ($target in $targets) {
    $full = Join-Path $repoRoot $target.Path
    if (-not (Test-Path $full)) {
        Write-Warning "跳过（文件不存在）：$($target.Path)"
        continue
    }

    $original = Read-Text $full

    # 只换第一处：同一个文件里出现多次时（README 的徽标与环境要求），
    # 逐条 pattern 各自只认自己那一行，不会互相踩
    $rx = New-Object -TypeName System.Text.RegularExpressions.Regex -ArgumentList $target.Pattern

    # 先判「认不认得出」再判「要不要写」：
    # 两件事分开，否则「匹配上了但值没变」（例如最近发布的那版刚好就是当前版本）会被误报成漏改
    if (-not $rx.IsMatch($original)) {
        Write-Warning "没匹配到：$($target.Path)  ←  $($target.Pattern)"
        continue
    }

    $updated = $rx.Replace($original, $target.Replace, 1)
    if ($updated -ne $original) {
        Write-Text $full $updated
        if ($changed -notcontains $target.Path) { $changed += $target.Path }
    }
}

# ── 9. 收尾 ───────────────────────────────────────────────────────────────
Write-Host ''
if ($DryRun) {
    Write-Host '[dry-run] 未写入任何文件。' -ForegroundColor DarkYellow
}
else {
    Write-Host "已同步 $($changed.Count) 个文件：" -ForegroundColor Green
    foreach ($file in $changed) { Write-Host "  · $file" }
}

Write-Host ''
Write-Host '还需要人工补两处（脚本不猜内容）：' -ForegroundColor Yellow
Write-Host "  1. docs/06-变更记录.md  本次改动的小节标题写成「## v$newVersion — …」"
Write-Host '  2. docs/README.md       版本历史表补一行'
Write-Host ''
Write-Host "下一步：dotnet test（VersionConsistencyTests 会核对版本）→ git commit → 打包 → tag v$newVersion"
