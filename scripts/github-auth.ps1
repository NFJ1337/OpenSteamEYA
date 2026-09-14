<#
.SYNOPSIS
GitHub 凭据助手：取 token 给 gh 用（被 publish-release.ps1 / publish-source.ps1 点源加载）。

.DESCRIPTION
取值顺序：环境变量 GH_TOKEN → GITHUB_TOKEN → 本机 Git 凭据管理器里 github.com 的那份（与 git push 同一份）。
取到后写进 $env:GH_TOKEN 供后续 gh 命令使用；绝不会打印 token 内容。
#>

function Get-GitHubToken {
    if (-not [string]::IsNullOrWhiteSpace($env:GH_TOKEN)) { return $env:GH_TOKEN }
    if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_TOKEN)) { return $env:GITHUB_TOKEN }

    $helper = (git config --get credential.helper)
    if ([string]::IsNullOrWhiteSpace($helper)) { $helper = (git config --global --get credential.helper) }
    if ([string]::IsNullOrWhiteSpace($helper)) { return $null }

    $helperPath = ($helper -replace '^!', '').Trim('"')
    if (-not (Test-Path -LiteralPath $helperPath)) { return $null }

    $answer = "protocol=https`nhost=github.com`n`n" | & $helperPath get 2>$null
    $passwordLine = $answer | Where-Object { $_ -like 'password=*' } | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($passwordLine)) { return $null }

    return ($passwordLine -replace '^password=', '')
}

function Connect-GitHub {
    $token = Get-GitHubToken
    if ([string]::IsNullOrWhiteSpace($token)) {
        throw '取不到 GitHub 凭据：请先 gh auth login，或设置 GH_TOKEN 环境变量。'
    }

    $env:GH_TOKEN = $token
}
