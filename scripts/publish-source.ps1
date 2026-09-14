<#
.SYNOPSIS
把源码打包成 zip 上传到 GitHub Release，并把工作区改动提交、推送到远端仓库（用户说的「源码打包发送到 github」）。

.DESCRIPTION
1) 把仓库源码（任何层级都排除 .git/.vs/artifacts/bin/obj/node_modules）复制到临时目录，压成 artifacts\SteamEYA-<版本>-source.zip；
2) git add -A → 有改动就提交 → push 到指定分支；
3) 把源码 zip 上传到 release（同名覆盖）。

.EXAMPLE
pwsh -File scripts\publish-source.ps1 -Version 1.2.9
pwsh -File scripts\publish-source.ps1 -Version 1.2.9 -DryRun    # 只打源码包看结果，不推代码、不上传
#>
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$ProjectRoot = '',
    [string]$Repository = 'NFJ1337/OpenSteamEYA',
    [string]$Tag = '正式exe',
    [string]$Branch = 'main',
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'github-auth.ps1')

if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
    $ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
}

$zipPath = Join-Path $ProjectRoot "artifacts\SteamEYA-$Version-source.zip"
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
    foreach ($port in 7897, 7890, 7899, 10809, 10808, 1080, 8889, 2080) {
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
    Write-Host "  - git tag -f $Tag HEAD + git push --force origin refs/tags/$Tag（让发布页的 Source code zip/tar.gz 也变最新）"
    Write-Host '  - 删除 release 上旧的 SteamEYA-*-source.zip（若有）'
    Write-Host "  - gh release upload $Tag $zipPath --clobber（仓库 $Repository）"
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

    # GitHub release 页上那两个自动生成的「Source code (zip / tar.gz)」是按 release 所在 tag 的提交
    # 即时打包的。tag 不移动到最新提交，它们就一直是你发版那天的旧代码 —— 所以这里把 tag 跟到 HEAD。
    git tag -f $Tag HEAD | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "git tag -f $Tag 失败（退出码 $LASTEXITCODE）。" }

    if (-not (Invoke-GitPush @('--force', 'origin', "refs/tags/$Tag"))) {
        throw "git push --force origin refs/tags/$Tag 失败：release tag 没能移到最新提交，发布页的 Source code (zip/tar.gz) 会仍是旧代码。"
    }

    Write-Host "已把 release tag「$Tag」移动并推送到最新提交（GitHub 源码包 zip / tar.gz 会随之更新）"
}
finally {
    Pop-Location
}

# 先删掉 release 上旧的源码包，只保留这一次的（和安装包一样做「替换」而不是堆积）
$staleZips = @(gh release view $Tag --repo $Repository --json assets --jq '.assets[].name' |
    Where-Object { $_ -like 'SteamEYA-*-source.zip' -and $_ -ne $zip.Name })
foreach ($name in $staleZips) {
    Write-Host "删除旧源码包：$name"
    gh release delete-asset $Tag $name --repo $Repository --yes
}

gh release upload $Tag $zipPath --repo $Repository --clobber
Write-Host 'release 现在的资产：'
gh release view $Tag --repo $Repository --json assets --jq '.assets[] | "  - \(.name)  (\(.size) 字节)"'
