<#
.SYNOPSIS
把打包好的安装包与 latest.json 上传到 GitHub Release，并替换掉 release 上的旧安装包（用户说的「重新保存文件」）。

.DESCRIPTION
凭据来源（按顺序）：环境变量 GH_TOKEN / GITHUB_TOKEN → 本机 Git 凭据管理器（与 git push 用的是同一份）。
流程：算 sha256/大小 → 生成 artifacts\latest.json → 删掉 release 上旧的 SteamEYA*-win-x64-setup.exe（含改名前遗留的） →
      上传新安装包与 latest.json（同名 --clobber 覆盖）→ 把本次改动的精简说明写进 release 正文
      （程序「关于页 → 更新日志」读的就是 release 正文，latest.json 的 changelog 字段也同步）。

.EXAMPLE
pwsh -File scripts\publish-release.ps1 -Version 1.2.6
pwsh -File scripts\publish-release.ps1 -Version 1.2.6 -Notes '新增：XX','修复：YY'
pwsh -File scripts\publish-release.ps1 -Version 1.2.6 -DryRun    # 只看要做什么，不改远端
#>
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$ProjectRoot = '',
    [string]$Repository = 'NFJ1337/OpenSteamEYA',
    [string]$Tag = '正式exe',
    [string]$InstallerPath = '',
    [switch]$DryRun,
    # 发布前先把当前工作区改动本地提交（用户要求：裸回复 2 时版本号也要提交）。只提交，不推送。
    [switch]$Commit,
    # 本次改动的精简说明（一条一项，写成 release 正文与 latest.json 的 changelog）。留空则不动正文。
    [string[]]$Notes = @(),
    # 说明也可以放在文件里（每行一条），与 -Notes 二选一。
    [string]$NotesFile = ''
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
    $ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
}

if ([string]::IsNullOrWhiteSpace($InstallerPath)) {
    $InstallerPath = Join-Path $ProjectRoot "artifacts\SteamEYANFJ-$Version-win-x64-setup.exe"
}

if (-not (Test-Path -LiteralPath $InstallerPath)) {
    throw "找不到安装包：$InstallerPath"
}

. (Join-Path $PSScriptRoot 'github-auth.ps1')

Connect-GitHub

if ($Commit -and -not $DryRun) {
    $pending = @(git status --porcelain).Count
    if ($pending -gt 0) {
        git add -A | Out-Host
        git commit -m "SteamEYA $Version：版本号与本次改动（本地提交，未推送）" | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "git commit 失败（退出码 $LASTEXITCODE）。" }
        Write-Host "已本地提交 $pending 处改动（未推送；推送归触发词 3）。"
    }
    else {
        Write-Host '工作区没有需要提交的改动。'
    }
}
# 本次更新的精简说明：-Notes 直接给，或 -NotesFile 从文件读（每行一条）；都没给就保持 release 正文不变。
$notesLines = @()
if (-not [string]::IsNullOrWhiteSpace($NotesFile)) {
    if (-not (Test-Path -LiteralPath $NotesFile)) { throw "找不到说明文件：$NotesFile" }
    $notesLines = @(Get-Content -LiteralPath $NotesFile | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
}
elseif ($Notes.Count -gt 0) {
    $notesLines = @($Notes | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
}

$file = Get-Item -LiteralPath $InstallerPath
$hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $InstallerPath).Hash.ToLowerInvariant()
$commitHash = "$(git rev-parse HEAD 2>$null)".Trim()

$metadata = [ordered]@{
    version        = $Version
    # 客户端用 tag 拼 /releases/<tag>/download/<文件> 与页面地址；
    # 本仓库 release tag 是中文（正式exe）而不是 vX.Y.Z，所以这里固定写 latest：
    # 客户端走 /releases/latest/... ，GitHub 自动指向最新 release，与真实 tag 名无关。
    tag            = "latest"
    channel        = 'stable'
    platform       = 'win-x64'
    commit         = $commitHash
    artifactName   = $file.Name
    artifactSize   = $file.Length
    artifactSha256 = $hash
    artifactType   = 'exe-installer'
    changelog      = @($notesLines)
}

$metadataPath = Join-Path $ProjectRoot 'artifacts\latest.json'

Write-Host "仓库：$Repository    release tag：$Tag"
Write-Host "安装包：$($file.Name)（$([math]::Round($file.Length / 1MB, 2)) MB）"
Write-Host "latest.json：$metadataPath"

$assetNames = @(gh release view $Tag --repo $Repository --json assets --jq '.assets[].name')
$stale = @($assetNames | Where-Object { $_ -like 'SteamEYA*-win-x64-setup.exe' -and $_ -ne $file.Name })

if ($assetNames.Count -gt 0) {
    Write-Host "release 现有资产：$($assetNames -join ', ')"
}

# ---- release 正文的历史累积：新版本的说明「新增」在正文最上面，老版本原样保留 ----

# 读 release 现有正文（读不到就当没有，不影响发布）。
function Get-ReleaseBody([string]$Tag, [string]$Repository) {
    try {
        $body = gh release view $Tag --repo $Repository --json body --jq '.body'
        if ($null -eq $body) { return '' }
        return ($body -join "`n")
    }
    catch { return '' }
}

# 把正文字符串切成「每个版本一块」：以 SteamEYA v… 开头的行为块首。
function Split-ReleaseBody([string]$Body) {
    $blocks = [System.Collections.Generic.List[string]]::new()
    if ([string]::IsNullOrWhiteSpace($Body)) { return $blocks }

    $current = [System.Collections.Generic.List[string]]::new()
    foreach ($line in ($Body -split "`n")) {
        $trimmed = $line.TrimEnd()
        if ($current.Count -gt 0 -and $trimmed -match '^SteamEYA\s+v') {
            $blocks.Add((($current -join "`n").Trim()))
            $current = [System.Collections.Generic.List[string]]::new()
        }

        $current.Add($trimmed)
    }

    if ($current.Count -gt 0) { $blocks.Add((($current -join "`n").Trim())) }
    return $blocks
}

# 新块放最上面 + 历史块接在后面；同版本的旧块替换掉（重复发布不会叠加）；超过上限丢最老的。
function Merge-ReleaseBody([string]$NewBlock, [string]$ExistingBody, [string]$Version, [int]$MaxBlocks = 60) {
    $kept = @(Split-ReleaseBody $ExistingBody | Where-Object {
        $_ -notmatch ('^SteamEYA\s+v' + [regex]::Escape($Version) + '(\s|$)')
    })

    $all = @($NewBlock) + $kept
    if ($all.Count -gt $MaxBlocks) { $all = $all[0..($MaxBlocks - 1)] }
    return ((($all -join "`n`n").TrimEnd()) + "`n")
}

# 本次说明的正文块（首行固定是版本号）。
function New-ReleaseBodyBlock([string]$Version, [string[]]$Notes) {
    $lines = @("SteamEYA v$Version", '') + @($Notes | ForEach-Object { if ($_ -match '^[-*]') { $_ } else { "- $_" } })
    return (($lines -join "`n").TrimEnd())
}

if ($DryRun) {
    Write-Host '[DryRun] 将要执行：'
    foreach ($name in $stale) { Write-Host "  - 删除旧安装包资产：$name" }
    Write-Host "  - 上传（覆盖）：$($file.Name)"
    if ($Commit) { Write-Host '  - 本地提交当前改动（git add -A + commit，不推送）' }
    if ($notesLines.Count -gt 0) {
        $preview = Merge-ReleaseBody -NewBlock (New-ReleaseBodyBlock -Version $Version -Notes $notesLines) `
            -ExistingBody (Get-ReleaseBody -Tag $Tag -Repository $Repository) -Version $Version
        Write-Host "  - 更新 release 正文（新增本版本说明，保留历史；首行 SteamEYA v$Version）："
        Write-Host '      ---- 新正文预览（前 40 行）----'
        ($preview -split "`n" | Select-Object -First 40) | ForEach-Object { Write-Host "      $_" }
    }
    Write-Host '  - 上传（覆盖）：latest.json，内容预览：'
    Write-Host ($metadata | ConvertTo-Json -Depth 4)
    return
}

$json = $metadata | ConvertTo-Json -Depth 4
Set-Content -LiteralPath $metadataPath -Value $json -Encoding utf8NoBOM

foreach ($name in $stale) {
    gh release delete-asset $Tag $name --repo $Repository --yes
}

gh release upload $Tag $InstallerPath $metadataPath --repo $Repository --clobber

# 更新日志：写进 release 正文（程序关于页的「更新日志」卡片直接读它）。
# 首行固定带上版本号（用户要求：发布到 release 的说明要能一眼看出是哪个版本）。
# 用纯文本而不是 Markdown 标题：客户端是逐行原样显示的，写 "## xxx" 会把井号也显示出来。
# 累积而不是替换（用户要求）：新版本块插在正文最上面，老版本块原样接在后面，历史一直留着。
if ($notesLines.Count -gt 0) {
    $bodyPath = Join-Path $ProjectRoot 'artifacts\release-notes.md'
    $existingBody = Get-ReleaseBody -Tag $Tag -Repository $Repository
    $historyCount = @(Split-ReleaseBody $existingBody).Count
    $bodyText = Merge-ReleaseBody -NewBlock (New-ReleaseBodyBlock -Version $Version -Notes $notesLines) `
        -ExistingBody $existingBody -Version $Version
    Set-Content -LiteralPath $bodyPath -Value $bodyText -Encoding utf8NoBOM
    gh release edit $Tag --repo $Repository --notes-file $bodyPath
    Write-Host "已更新 release 正文（SteamEYA v$Version 新增 $($notesLines.Count) 条，保留历史 $historyCount 块）。"
}

Write-Host '完成。release 现在的资产：'
gh release view $Tag --repo $Repository --json assets --jq '.assets[] | "  - \(.name)  (\(.size) 字节)"'
