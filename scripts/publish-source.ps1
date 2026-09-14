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
    }
    else {
        Write-Host '工作区没有需要提交的改动。'
    }

    git push origin $Branch | Out-Host
    $head = "$(git rev-parse --short HEAD)".Trim()
    Write-Host "已推送源码到 $Repository（$Branch），提交 $head"
}
finally {
    Pop-Location
}

gh release upload $Tag $zipPath --repo $Repository --clobber
Write-Host 'release 现在的资产：'
gh release view $Tag --repo $Repository --json assets --jq '.assets[] | "  - \(.name)  (\(.size) 字节)"'
