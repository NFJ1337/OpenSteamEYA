<#
.SYNOPSIS
为当前版本创建独立的 GitHub Release 和版本标签，并上传安装包与 latest.json。
.DESCRIPTION
每个版本固定使用 v<Version> 标签（例如 v1.9.6），不再复用“正式exe”，
也不会覆盖旧 Release 的安装包或正文。latest.json 仍随新 Release 上传，
因此 /releases/latest/download/latest.json 会自动指向最新版本。

.EXAMPLE
pwsh -File scripts\publish-release.ps1 -Version 1.9.6 -Commit -NotesFile .\artifacts\release-notes-1.9.6.txt
#>
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$ProjectRoot = '',
    [string]$Repository = 'NFJ1337/OpenSteamEYA',
    [string]$Tag = '',
    [string]$Branch = 'main',
    [string]$InstallerPath = '',
    [switch]$DryRun,
    [switch]$Commit,
    [string[]]$Notes = @(),
    [string]$NotesFile = ''
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
    $ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
}

if ([string]::IsNullOrWhiteSpace($Tag)) {
    $Tag = "v$Version"
}

if ([string]::IsNullOrWhiteSpace($InstallerPath)) {
    $InstallerPath = Join-Path $ProjectRoot "artifacts\SteamEYANFJ-$Version-win-x64-setup.exe"
}

if (-not (Test-Path -LiteralPath $InstallerPath)) {
    throw "找不到安装包：$InstallerPath"
}

. (Join-Path $PSScriptRoot 'github-auth.ps1')

function Get-LocalTagCommit([string]$tagName) {
    $output = @(git rev-parse -q --verify "refs/tags/$tagName" 2>$null)
    if ($LASTEXITCODE -ne 0 -or $output.Count -eq 0) {
        return $null
    }

    return $output[0].Trim()
}

function Test-LocalProxyPort([int]$port) {
    $client = New-Object System.Net.Sockets.TcpClient
    try {
        $iar = $client.BeginConnect('127.0.0.1', $port, $null, $null)
        return ($iar.AsyncWaitHandle.WaitOne(300) -and $client.Connected)
    }
    catch {
        return $false
    }
    finally {
        $client.Close()
    }
}

function Invoke-GitPush([string[]]$pushArguments) {
    git push @pushArguments
    if ($LASTEXITCODE -eq 0) {
        return $true
    }

    Write-Host '直连推送失败，尝试本机代理…'
    $ports = [System.Collections.Generic.List[int]]::new()
    $ports.Add(17897)
    try {
        $systemProxy = (Get-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings' -ErrorAction SilentlyContinue).ProxyServer
        if ($systemProxy -match '^127\.0\.0\.1:(\d+)$') {
            $ports.Add([int]$Matches[1])
        }
    }
    catch {
    }

    foreach ($extra in 7897, 7890, 7899, 10809, 10808, 1080, 8889, 2080) {
        $ports.Add($extra)
    }

    foreach ($port in ($ports | Select-Object -Unique)) {
        if (-not (Test-LocalProxyPort $port)) {
            continue
        }

        Write-Host "  改用 127.0.0.1:$port 重试"
        git -c "http.proxy=http://127.0.0.1:$port" -c "https.proxy=http://127.0.0.1:$port" push @pushArguments
        if ($LASTEXITCODE -eq 0) {
            return $true
        }
    }

    return $false
}

function Test-GitHubReleaseExists([string]$tagName) {
    $errPath = Join-Path ([System.IO.Path]::GetTempPath()) "steameya-release-check-$([guid]::NewGuid().ToString('N')).txt"
    try {
        & gh release view $tagName --repo $Repository --json tagName 2> $errPath | Out-Null
        if ($LASTEXITCODE -eq 0) {
            return $true
        }

        $errorText = if (Test-Path -LiteralPath $errPath) {
            Get-Content -LiteralPath $errPath -Raw
        }
        else {
            ''
        }

        if ($errorText -match 'release not found|HTTP 404|Not Found') {
            return $false
        }

        throw "检查 Release「$tagName」失败：$errorText"
    }
    finally {
        if (Test-Path -LiteralPath $errPath) {
            Remove-Item -LiteralPath $errPath -Force
        }
    }
}

function New-ReleaseBodyBlock([string]$version, [string[]]$noteLines) {
    $lines = @("SteamEYA v$version")
    if ($noteLines.Count -gt 0) {
        $lines += ''
        $lines += @($noteLines | ForEach-Object {
            if ($_ -match '^[-*]') { $_ } else { "- $_" }
        })
    }

    return (($lines -join "`n").TrimEnd())
}

$notesLines = @()
if (-not [string]::IsNullOrWhiteSpace($NotesFile)) {
    if (-not (Test-Path -LiteralPath $NotesFile)) {
        throw "找不到说明文件：$NotesFile"
    }

    $notesLines = @(Get-Content -LiteralPath $NotesFile | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
}
elseif ($Notes.Count -gt 0) {
    $notesLines = @($Notes | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
}

$bodyText = New-ReleaseBodyBlock -version $Version -noteLines $notesLines
$bodyPath = Join-Path $ProjectRoot "artifacts\release-notes-v$Version.md"
$metadataPath = Join-Path $ProjectRoot 'artifacts\latest.json'

if ($DryRun) {
    Write-Host '[DryRun] 将创建全新的 Release：'
    Write-Host "  - 标签：$Tag"
    Write-Host "  - 标题：SteamEYA v$Version"
    Write-Host "  - 安装包：$InstallerPath"
    Write-Host "  - 元数据：$metadataPath"
    Write-Host "  - 正文只包含 v$Version，不读取也不修改旧 Release"
    if ($Commit) {
        Write-Host '  - 本地提交当前改动（git add -A + commit），然后推送 main 与版本标签'
    }
    return
}

if (Get-LocalTagCommit $Tag) {
    throw "标签 $Tag 已存在。为避免覆盖旧发布，请提升版本号或换一个新标签。"
}

Connect-GitHub

if (Test-GitHubReleaseExists $Tag) {
    throw "GitHub Release「$Tag」已存在。新版本必须使用新的版本号和标签。"
}

if ($Commit) {
    $pending = @(git status --porcelain).Count
    if ($pending -gt 0) {
        git add -A | Out-Host
        git commit -m "SteamEYA $Version：版本号与本次改动" | Out-Host
        if ($LASTEXITCODE -ne 0) {
            throw "git commit 失败（退出码 $LASTEXITCODE）。"
        }
        Write-Host "已本地提交 $pending 处改动。"
    }
    else {
        Write-Host '工作区没有需要提交的改动。'
    }
}

$commitHash = "$(git rev-parse HEAD)".Trim()
git tag -a $Tag -m "SteamEYA v$Version" HEAD | Out-Host
if ($LASTEXITCODE -ne 0) {
    throw "创建标签 $Tag 失败（退出码 $LASTEXITCODE）。"
}

if (-not (Invoke-GitPush @('origin', $Branch))) {
    throw "git push 失败：无法推送 $Branch（直连与本机代理都失败）。"
}

if (-not (Invoke-GitPush @('origin', "refs/tags/$Tag"))) {
    throw "git push 失败：无法推送标签 $Tag（直连与本机代理都失败）。"
}

$file = Get-Item -LiteralPath $InstallerPath
$hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $InstallerPath).Hash.ToLowerInvariant()
$metadata = [ordered]@{
    version        = $Version
    tag            = $Tag
    channel        = 'stable'
    platform       = 'win-x64'
    commit         = $commitHash
    artifactName   = $file.Name
    artifactSize   = $file.Length
    artifactSha256 = $hash
    artifactType   = 'exe-installer'
    changelog      = @($notesLines)
}

New-Item -ItemType Directory -Force -Path (Split-Path -Parent $bodyPath) | Out-Null
Set-Content -LiteralPath $bodyPath -Value $bodyText -Encoding utf8NoBOM
Set-Content -LiteralPath $metadataPath -Value ($metadata | ConvertTo-Json -Depth 4) -Encoding utf8NoBOM

$releaseTitle = "SteamEYA v$Version"
$createArgs = @(
    'release', 'create', $Tag,
    $InstallerPath, $metadataPath,
    '--repo', $Repository,
    '--title', $releaseTitle,
    '--notes-file', $bodyPath,
    '--verify-tag',
    '--latest'
)

& gh @createArgs
if ($LASTEXITCODE -ne 0) {
    throw "创建 GitHub Release 失败（退出码 $LASTEXITCODE）。标签已推送，可在修复网络后重新上传资产。"
}

$releaseUrl = "https://github.com/$Repository/releases/tag/$([Uri]::EscapeDataString($Tag))"
Write-Host "已创建新 Release：$releaseUrl"
Write-Host 'Release 资产：'
gh release view $Tag --repo $Repository --json assets --jq '.assets[] | "  - \(.name)  (\(.size) 字节)"'
