<#
.SYNOPSIS
把源码打包成 zip 上传到当前版本独立的 GitHub Release，并推送工作区改动与版本标签。

.DESCRIPTION
1) 把仓库源码（任何层级都排除 .git/.vs/artifacts/bin/obj/node_modules）复制到临时目录，压成 artifacts\SteamEYANFJ-<版本>-source.zip；
2) git add -A → 有改动就提交 → push 到指定分支；
3) 确保 v<版本> 标签存在且指向当前提交，不移动旧标签；
4) 把源码 zip 上传到该版本自己的 Release。

.EXAMPLE
pwsh -File scripts\publish-source.ps1 -Version 1.2.9
pwsh -File scripts\publish-source.ps1 -Version 1.2.9 -DryRun    # 只打源码包看结果，不推代码、不上传
#>
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$ProjectRoot = '',
    [string]$Repository = 'NFJ1337/OpenSteamEYA',
    [string]$Tag = '',
    [string]$Branch = 'main',
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'github-auth.ps1')

if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
    $ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
}

if ([string]::IsNullOrWhiteSpace($Tag)) {
    $Tag = "v$Version"
}

$zipPath = Join-Path $ProjectRoot "artifacts\SteamEYANFJ-$Version-source.zip"
$excludedDirs = @('.git', '.vs', 'artifacts', 'node_modules', 'node_modules_temp', 'bin', 'obj')

function Copy-SourceTree([string]$source, [string]$target) {
    New-Item -ItemType Directory -Force -Path $target | Out-Null
    foreach ($item in (Get-ChildItem -LiteralPath $source -Force)) {
        if ($excludedDirs -contains $item.Name) { continue }
        if ($item.PSIsContainer) {
            Copy-SourceTree $item.FullName (Join-Path $target $item.Name)
        }
        else {
            Copy-Item -LiteralPath $item.FullName -Destination (Join-Path $target $item.Name) -Force
        }
    }
}

# 本机是否有人在监听这个端口（探测 Clash/V2Ray 之类的本地代理端口）
function Test-LocalProxyPort([int]$port) {
    $client = New-Object System.Net.Sockets.TcpClient
    try {
        $iar = $client.BeginConnect('127.0.0.1', $port, $null, $null)
        return ($iar.AsyncWaitHandle.WaitOne(300) -and $client.Connected)
    }
    catch { return $false }
    finally { $client.Close() }
}

# 推送（分支或 tag）：GitHub 的 HTTPS 在国内经常被重置，直连失败时自动改用本机代理再试。
function Invoke-GitPush([string[]]$pushArguments) {
    git push @pushArguments
    if ($LASTEXITCODE -eq 0) { return $true }

    Write-Host '直连推送失败，尝试本机代理…'

    # 候选端口：先是本程序自带 VPN 的内核端口（用户开着它时就在监听），
    # 再是系统代理里写的那个（可能是 Clash/V2Ray 之类），最后是常见默认端口。
    # 注意 git 不读 WinINET 系统代理，必须显式 -c http.proxy=… 指过去，否则挂着一堆代理也推不动。
    $ports = [System.Collections.Generic.List[int]]::new()
    $ports.Add(17897)
    try {
        $sysProxy = (Get-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings' -ErrorAction SilentlyContinue).ProxyServer
        if ($sysProxy -match '^127\.0\.0\.1:(\d+)$') { $ports.Add([int]$Matches[1]) }
    }
    catch {
        # 读系统代理失败无所谓，继续用默认端口列表
    }
    foreach ($extra in 7897, 7890, 7899, 10809, 10808, 1080, 8889, 2080) { $ports.Add($extra) }

    foreach ($port in ($ports | Select-Object -Unique)) {
        if (-not (Test-LocalProxyPort $port)) { continue }

        Write-Host "  改用 127.0.0.1:$port 重试"
        git -c "http.proxy=http://127.0.0.1:$port" -c "https.proxy=http://127.0.0.1:$port" push @pushArguments
        if ($LASTEXITCODE -eq 0) { return $true }
    }

    return $false
}

Write-Host "源码包：$zipPath"

$staging = Join-Path ([System.IO.Path]::GetTempPath()) "steameya-src-$([System.IO.Path]::GetRandomFileName())"
New-Item -ItemType Directory -Force -Path $staging | Out-Null

Copy-SourceTree $ProjectRoot $staging
$copied = (Get-ChildItem -LiteralPath $staging -Recurse -File -Force | Measure-Object).Count
Write-Host "纳入源码包的文件数：$copied"

$zipDir = Split-Path -Parent $zipPath
New-Item -ItemType Directory -Force -Path $zipDir | Out-Null
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zipPath -CompressionLevel Optimal
Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue

$zip = Get-Item -LiteralPath $zipPath
Write-Host "源码包大小：$([math]::Round($zip.Length / 1MB, 2)) MB"

if ($DryRun) {
    $pending = (git status --porcelain | Measure-Object).Count
    Write-Host '[DryRun] 将要执行：'
    Write-Host "  - git add -A（当前改动 $pending 条）"
    Write-Host "  - git commit -m ""SteamEYA $Version：源码同步（含本次改动）""（无改动则跳过）"
    Write-Host "  - git push origin $Branch"
    Write-Host "  - 确保新标签 $Tag 指向当前提交并 push origin refs/tags/$Tag（不移动旧标签）"
    Write-Host "  - gh release upload $Tag $zipPath（上传到该版本自己的 Release）"
    return
}

Connect-GitHub

Push-Location $ProjectRoot
try {
    git add -A
    $pending = (git status --porcelain | Measure-Object).Count
    if ($pending -gt 0) {
        git commit -m "SteamEYA $Version：源码同步（含本次改动）" | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "git commit 失败（退出码 $LASTEXITCODE）。" }
    }
    else {
        Write-Host '工作区没有需要提交的改动。'
    }

    # git 是非托管命令：失败不会抛异常，必须自己看退出码，否则会出现「没推上去却报成功」。
    if (-not (Invoke-GitPush @('origin', $Branch))) {
        throw "git push 失败：本地已提交但**没有推送成功**（直连与本机代理都试过了），请挂上 VPN 后重试 git push origin $Branch。"
    }

    $head = "$(git rev-parse --short HEAD)".Trim()
    Write-Host "已推送源码到 $Repository（$Branch），提交 $head"

    # 每个版本使用新标签。publish-release 正常会先创建好；单独运行本脚本时在这里补建。
    # 已存在的标签只能指向当前提交，绝不再强制移动。
    $tagOutput = @(git rev-parse -q --verify "refs/tags/$Tag^{}" 2>$null)
    if ($LASTEXITCODE -ne 0 -or $tagOutput.Count -eq 0) {
        git tag -a $Tag -m "SteamEYA v$Version" HEAD | Out-Host
        if ($LASTEXITCODE -ne 0) {
            throw "创建标签 $Tag 失败（退出码 $LASTEXITCODE）。"
        }
    }
    else {
        $tagCommit = $tagOutput[0].Trim()
        $headCommit = "$(git rev-parse HEAD)".Trim()
        if (-not [string]::Equals($tagCommit, $headCommit, [StringComparison]::OrdinalIgnoreCase)) {
            throw "标签 $Tag 已存在但未指向当前提交（$tagCommit，当前 $headCommit）。请使用新的版本号。"
        }
    }

    if (-not (Invoke-GitPush @('origin', "refs/tags/$Tag"))) {
        throw "git push origin refs/tags/$Tag 失败：版本标签没有推送成功。"
    }

    Write-Host "版本标签「$Tag」已确认指向当前提交并推送（GitHub 自动源码包对应该版本）。"
}
finally {
    Pop-Location
}

# 只向当前版本自己的 Release 上传源码包，不改旧 Release。
gh release upload $Tag $zipPath --repo $Repository
Write-Host 'release 现在的资产：'
gh release view $Tag --repo $Repository --json assets --jq '.assets[] | "  - \(.name)  (\(.size) 字节)"'
