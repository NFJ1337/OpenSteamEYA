param(
    [string]$Version = "1.4.6",
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    # 先按「末位 +1，满 10 进位」自增版本号（同步写回 csproj / 本脚本 / iss），再打包。
    [switch]$Bump
)

$ErrorActionPreference = "Stop"

if ($Bump) {
    $bumpScript = Join-Path $PSScriptRoot "bump-version.ps1"
    $bumpOutput = & $bumpScript
    $bumped = ($bumpOutput | Where-Object { $_ -like "NewVersion=*" }) -replace "^NewVersion=", ""
    if ([string]::IsNullOrWhiteSpace($bumped)) {
        throw "版本自增失败：bump-version.ps1 没有返回 NewVersion。"
    }

    $Version = $bumped
    Write-Host "版本自增后：$Version"
}

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$projectPath = Join-Path $repoRoot "SteamEyaWinUI\SteamEyaWinUI.csproj"
$publishDir = Join-Path $repoRoot "artifacts\build\$Runtime"
$outputDir = Join-Path $repoRoot "artifacts"
$issPath = Join-Path $repoRoot "build\installer\SteamEYA.iss"

function Ensure-Success {
    param([string]$Step)

    if ($LASTEXITCODE -ne 0) {
        throw "$Step failed with exit code $LASTEXITCODE."
    }
}

Write-Host "[1/3] Restoring..."
dotnet restore $projectPath -r $Runtime -p:Platform=x64
Ensure-Success "dotnet restore"

Write-Host "[2/3] Publishing..."
# dotnet publish --output 只合并不清空：先清掉旧产物，避免上次的托管回退 DLL 集残留并被打进安装包。
if (Test-Path -LiteralPath $publishDir) {
    Remove-Item -LiteralPath $publishDir -Recurse -Force
}
dotnet publish $projectPath `
  --configuration $Configuration `
  --runtime $Runtime `
  --no-restore `
  -p:Platform=x64 `
  --output $publishDir `
  -p:Version=$Version `
  -p:FileVersion="$Version.0" `
  -p:AssemblyVersion="$Version.0" `
    -p:InformationalVersion=v$Version+local
if ($LASTEXITCODE -ne 0) {
    Write-Warning "AOT publish failed, retrying with PublishAot=false for local packaging."
    dotnet publish $projectPath `
      --configuration $Configuration `
      --runtime $Runtime `
      --no-restore `
      -p:Platform=x64 `
      --output $publishDir `
      -p:PublishAot=false `
      -p:Version=$Version `
      -p:FileVersion="$Version.0" `
      -p:AssemblyVersion="$Version.0" `
      -p:InformationalVersion=v$Version+local
    Ensure-Success "dotnet publish (PublishAot=false)"
}

Get-ChildItem -LiteralPath $publishDir -Filter "*.pdb" -File | Remove-Item -Force
foreach ($pattern in @(
    "Microsoft.Web.WebView2.Core*.dll",
    "WebView2Loader.dll"
)) {
    Get-ChildItem -LiteralPath $publishDir -Filter $pattern -File | Remove-Item -Force
}

# Inno Setup 6 是 32 位应用，默认装到 Program Files (x86)；IS7 x64 版才在 Program Files。两个 hive 都探。
$iscc = (Get-Command iscc -ErrorAction SilentlyContinue).Source
if (-not $iscc) {
    foreach ($candidate in @(
        (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
        (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 7\ISCC.exe"),
        (Join-Path $env:ProgramFiles "Inno Setup 7\ISCC.exe"),
        (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 7\ISCC.exe"),
        (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
        (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe")
    )) {
        if ($candidate -and (Test-Path -LiteralPath $candidate)) {
            $iscc = $candidate
            break
        }
    }
}

if (-not $iscc) {
    throw "Inno Setup compiler (ISCC.exe) was not found. Install Inno Setup 6 first."
}

# 归档本次构建的 exe + pdb 到 artifacts\symbols\<版本>\（用户要求：以后崩溃/卡死现场能直接还原符号）。
# best-effort：归档失败不影响出包，但要显式告警。
try {
    & (Join-Path $PSScriptRoot 'archive-symbols.ps1') -Version $Version -ProjectRoot $repoRoot
    Ensure-Success 'archive-symbols.ps1'
}
catch {
    Write-Warning "符号归档失败（不影响安装包）：$($_.Exception.Message)"
}

Write-Host "[3/3] Building installer..."
& $iscc "/DAppVersion=$Version" "/DPublishDir=$publishDir" "/DOutputDir=$outputDir" $issPath
Ensure-Success "ISCC"

Write-Host "Done. Output: $outputDir"
