<#
.SYNOPSIS
  发布「免安装版」并启动——每轮改动后用来立刻看效果。
.DESCRIPTION
  1) dotnet publish（Release / win-x64 / Native AOT，失败自动回退 PublishAot=false）；
  2) 产物写入 artifacts\publish\win-x64（历史免安装目录），顺带刷新其中的 版本.txt；
  3) 启动 SteamEyaWinUI.exe（发布前会先温和结束该目录里正在运行的旧实例，否则 exe 被占用无法覆盖）。
.PARAMETER Version
  写进 版本.txt 的版本号；默认读 SteamEyaWinUI.csproj 的 <Version>。
.PARAMETER NoPublish
  跳过发布，直接回报现有产物的路径。
.PARAMETER Launch
  发布后顺手启动（会先温和结束旧实例）。默认不启动：只发布并把路径回报给用户。
.EXAMPLE
  pwsh -File scripts\run-portable.ps1                 # 发布 + 回报路径（不启动）
.EXAMPLE
  pwsh -File scripts\run-portable.ps1 -Launch         # 发布后直接跑起来
.EXAMPLE
  pwsh -File scripts\run-portable.ps1 -NoPublish      # 只回报现有产物路径
#>
param(
    [string]$Version,
    [switch]$NoPublish,
    # 默认不启动：用户要求「改动完成后只回报免安装 exe 的路径，不要自动打开」。
    [switch]$Launch
)

$ErrorActionPreference = "Stop"

$repoRoot    = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$projectPath = Join-Path $repoRoot "SteamEyaWinUI\SteamEyaWinUI.csproj"
$publishDir  = Join-Path $repoRoot "artifacts\publish\win-x64"
$runtime     = "win-x64"
$exeName     = "SteamEyaWinUI.exe"

if (-not $Version) {
    [xml]$csproj = Get-Content -LiteralPath $projectPath
    $Version = @($csproj.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ }) | Select-Object -First 1
    if (-not $Version) { $Version = "0.1.0" }
}

function Stop-RunningPortable {
    # 免安装目录里的旧实例占着 exe 时无法覆盖：先关窗口，超时再结束进程。
    $running = Get-Process -Name "SteamEyaWinUI" -ErrorAction SilentlyContinue | Where-Object {
        try { $_.Path -and $_.Path.StartsWith($publishDir, [StringComparison]::OrdinalIgnoreCase) } catch { $false }
    }
    foreach ($process in $running) {
        Write-Host "Stopping running portable instance (pid $($process.Id))..."
        $null = $process.CloseMainWindow()
        if (-not $process.WaitForExit(4000)) {
            $process.Kill()
            $process.WaitForExit(4000)
        }
    }
}

if (-not $NoPublish) {
    Write-Host "[1/3] Restoring..."
    dotnet restore $projectPath -r $runtime -p:Platform=x64
    if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed with exit code $LASTEXITCODE." }

    Stop-RunningPortable
    if (Test-Path -LiteralPath $publishDir) {
        Remove-Item -LiteralPath $publishDir -Recurse -Force
    }

    Write-Host "[2/3] Publishing (Native AOT)..."
    dotnet publish $projectPath `
      --configuration Release `
      --runtime $runtime `
      --no-restore `
      -p:Platform=x64 `
      --output $publishDir `
      -p:Version=$Version
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "AOT publish failed, retrying with PublishAot=false."
        dotnet publish $projectPath `
          --configuration Release `
          --runtime $runtime `
          --no-restore `
          -p:Platform=x64 `
          --output $publishDir `
          -p:PublishAot=false `
          -p:Version=$Version
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }
    }

    # 免安装目录不留调试符号（体积）；版本标记与历史格式保持一致。
    Get-ChildItem -LiteralPath $publishDir -Filter "*.pdb" -File -ErrorAction SilentlyContinue | Remove-Item -Force
    $versionText = @(
        "SteamEYA v$Version",
        "主程序：$exeName",
        "构建：win-x64 Release Native AOT"
    ) -join "`r`n"
    Set-Content -LiteralPath (Join-Path $publishDir "版本.txt") -Value $versionText -Encoding utf8
}

$exe = Join-Path $publishDir $exeName
if (-not (Test-Path -LiteralPath $exe)) { throw "免安装 exe 不存在：$exe" }

if ($Launch) {
    Write-Host "[3/3] Launching $exe"
    Start-Process -FilePath $exe -WorkingDirectory $publishDir
} else {
    Write-Host "[3/3] Skipped launch (pass -Launch to run it)."
}

Write-Host "Done: $exe"