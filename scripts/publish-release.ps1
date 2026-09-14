<#
.SYNOPSIS
把打包好的安装包与 latest.json 上传到 GitHub Release，并替换掉 release 上的旧安装包（用户说的「重新保存文件」）。

.DESCRIPTION
凭据来源（按顺序）：环境变量 GH_TOKEN / GITHUB_TOKEN → 本机 Git 凭据管理器（与 git push 用的是同一份）。
流程：算 sha256/大小 → 生成 artifacts\latest.json → 删掉 release 上旧的 SteamEYA-*-win-x64-setup.exe →
      上传新安装包与 latest.json（同名 --clobber 覆盖）。

.EXAMPLE
pwsh -File scripts\publish-release.ps1 -Version 1.2.6
pwsh -File scripts\publish-release.ps1 -Version 1.2.6 -DryRun    # 只看要做什么，不改远端
#>
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$ProjectRoot = '',
    [string]$Repository = 'NFJ1337/OpenSteamEYA',
    [string]$Tag = '正式exe',
    [string]$InstallerPath = '',
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
    $ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
}

if ([string]::IsNullOrWhiteSpace($InstallerPath)) {
    $InstallerPath = Join-Path $ProjectRoot "artifacts\SteamEYA-$Version-win-x64-setup.exe"
}

if (-not (Test-Path -LiteralPath $InstallerPath)) {
    throw "找不到安装包：$InstallerPath"
}

. (Join-Path $PSScriptRoot 'github-auth.ps1')

Connect-GitHub

$file = Get-Item -LiteralPath $InstallerPath
$hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $InstallerPath).Hash.ToLowerInvariant()
$commit = "$(git rev-parse HEAD 2>$null)".Trim()

$metadata = [ordered]@{
    version        = $Version
    # 客户端用 tag 拼 /releases/<tag>/download/<文件> 与页面地址；
    # 本仓库 release tag 是中文（正式exe）而不是 vX.Y.Z，所以这里固定写 latest：
    # 客户端走 /releases/latest/... ，GitHub 自动指向最新 release，与真实 tag 名无关。
    tag            = "latest"
    channel        = 'stable'
    platform       = 'win-x64'
    commit         = $commit
    artifactName   = $file.Name
    artifactSize   = $file.Length
    artifactSha256 = $hash
    artifactType   = 'exe-installer'
    changelog      = @()
}

$metadataPath = Join-Path $ProjectRoot 'artifacts\latest.json'

Write-Host "仓库：$Repository    release tag：$Tag"
Write-Host "安装包：$($file.Name)（$([math]::Round($file.Length / 1MB, 2)) MB）"
Write-Host "latest.json：$metadataPath"

$assetNames = @(gh release view $Tag --repo $Repository --json assets --jq '.assets[].name')
$stale = @($assetNames | Where-Object { $_ -like 'SteamEYA-*-win-x64-setup.exe' -and $_ -ne $file.Name })

if ($assetNames.Count -gt 0) {
    Write-Host "release 现有资产：$($assetNames -join ', ')"
}

if ($DryRun) {
    Write-Host '[DryRun] 将要执行：'
    foreach ($name in $stale) { Write-Host "  - 删除旧安装包资产：$name" }
    Write-Host "  - 上传（覆盖）：$($file.Name)"
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

Write-Host '完成。release 现在的资产：'
gh release view $Tag --repo $Repository --json assets --jq '.assets[] | "  - \(.name)  (\(.size) 字节)"'
