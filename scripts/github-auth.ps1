<#
.SYNOPSIS
GitHub 凭据助手：取 token 给 gh 用（被 publish-release.ps1 / publish-source.ps1 点源加载）。

.DESCRIPTION
取值顺序：环境变量 GH_TOKEN → GITHUB_TOKEN → 本机 Git 凭据管理器里 github.com 的那份
（与 git push 同一份）→ 本机 GitHub CLI 的登录态（gh auth token）。
取到后写进 $env:GH_TOKEN 供后续 gh 命令使用；绝不会打印 token 内容。
#>

function Get-GitHubToken {
    if (-not [string]::IsNullOrWhiteSpace($env:GH_TOKEN)) { return $env:GH_TOKEN }
    if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_TOKEN)) { return $env:GITHUB_TOKEN }

    # 1) Git 凭据管理器：直接让 git 用它自己配置的助手（"manager"、"store"、甚至 "!gh auth git-credential"
    #    这类「命令式」助手都能用）。旧版本要求 helper 是「存在的文件路径」，于是本机配的 "manager"
    #    永远取不到（GCM 是 PATH 上的 exe，不是路径），这里改成不再做路径判断。
    #    必须非交互：credential.interactive=false 关掉 GCM 弹窗，GIT_TERMINAL_PROMPT=0 关掉终端提问，
    #    否则无人值守时（比如自动化跑发布）会挂在那里等输入。
    $previousPrompt = $env:GIT_TERMINAL_PROMPT
    try {
        $env:GIT_TERMINAL_PROMPT = '0'
        $answer = "protocol=https`nhost=github.com`n`n" |
            & git -c credential.interactive=false credential fill 2>$null
    }
    catch {
        $answer = @()
    }
    finally {
        $env:GIT_TERMINAL_PROMPT = $previousPrompt
    }

    $passwordLine = @($answer) | Where-Object { $_ -like 'password=*' } | Select-Object -First 1
    if (-not [string]::IsNullOrWhiteSpace($passwordLine)) {
        return ($passwordLine -replace '^password=', '')
    }

    # 2) GitHub CLI：本机 gh 已登录（gh auth login）时直接用它的 token，与 gh 用的是同一份。
    $gh = (Get-Command gh -ErrorAction SilentlyContinue | Select-Object -First 1).Source
    if ([string]::IsNullOrWhiteSpace($gh)) {
        foreach ($candidate in @(
            (Join-Path $env:ProgramFiles "GitHub CLI\gh.exe"),
            (Join-Path ${env:ProgramFiles(x86)} "GitHub CLI\gh.exe"),
            (Join-Path $env:LOCALAPPDATA "Programs\GitHub CLI\gh.exe")
        )) {
            if ($candidate -and (Test-Path -LiteralPath $candidate)) { $gh = $candidate; break }
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($gh)) {
        try {
            $token = @(& $gh auth token 2>$null | Select-Object -First 1)[0]
        }
        catch {
            $token = $null
        }

        if (-not [string]::IsNullOrWhiteSpace($token)) { return $token.Trim() }
    }

    return $null
}

function Connect-GitHub {
    $token = Get-GitHubToken
    if ([string]::IsNullOrWhiteSpace($token)) {
        throw '取不到 GitHub 凭据：请先 gh auth login，或设置 GH_TOKEN 环境变量。'
    }

    $env:GH_TOKEN = $token
}