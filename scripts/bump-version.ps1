<#
.SYNOPSIS
版本号自增：末位 +1，满 10 进位（1.2.0 → 1.2.1；1.2.9 → 1.3.0；1.9.9 → 2.0.0）。

.DESCRIPTION
读 SteamEyaWinUI.csproj 里的 <Version>，算出下一个版本并同步写入三处版本定义：
  · SteamEyaWinUI\SteamEyaWinUI.csproj   （Version / FileVersion / AssemblyVersion / InformationalVersion）
  · scripts\build-installer.ps1          （默认 $Version）
  · build\installer\SteamEYA.iss         （默认 AppVersion）
末尾输出 NewVersion=<x.y.z>，供打包脚本或自动化读取。

.PARAMETER ProjectRoot
仓库根目录，默认取本脚本的上一级。

.PARAMETER ToVersion
直接指定目标版本（不做自增），例如 -ToVersion 2.0.0。

.PARAMETER DryRun
只计算并打印，不写任何文件。
#>
param(
    [string]$ProjectRoot = '',
    [string]$ToVersion = '',
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
    $ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
}

$projectPath = Join-Path $ProjectRoot 'SteamEyaWinUI\SteamEyaWinUI.csproj'
$installerScriptPath = Join-Path $ProjectRoot 'scripts\build-installer.ps1'
$issPath = Join-Path $ProjectRoot 'build\installer\SteamEYA.iss'

foreach ($path in @($projectPath, $installerScriptPath, $issPath)) {
    if (-not (Test-Path -LiteralPath $path)) { throw "找不到文件：$path" }
}

function Get-NextVersion([string]$version) {
    $parts = @($version.Split('.') | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($parts.Count -lt 2 -or $parts.Count -gt 3) { throw "版本号格式应为 X.Y 或 X.Y.Z，当前拿到：'$version'" }
    while ($parts.Count -lt 3) { $parts += '0' }

    $numbers = @($parts | ForEach-Object { [int]$_ })
    $numbers[2]++
    if ($numbers[2] -gt 9) { $numbers[2] = 0; $numbers[1]++ }
    if ($numbers[1] -gt 9) { $numbers[1] = 0; $numbers[0]++ }
    return "$($numbers[0]).$($numbers[1]).$($numbers[2])"
}

function Read-Text([string]$path) {
    $bytes = [System.IO.File]::ReadAllBytes($path)
    $offset = if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) { 3 } else { 0 }
    return [System.Text.Encoding]::UTF8.GetString($bytes, $offset, $bytes.Length - $offset)
}

# 保持原文件的 BOM 与 CRLF 风格，并且先写临时文件再替换，避免写坏。
function Write-TextPreservingStyle([string]$path, [string]$text) {
    $bytes = [System.IO.File]::ReadAllBytes($path)
    $hasBom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
    $normalized = $text.Replace("`r`n", "`n").Replace("`n", "`r`n")
    $temp = "$path.$([System.IO.Path]::GetRandomFileName()).tmp"
    [System.IO.File]::WriteAllText($temp, $normalized, (New-Object System.Text.UTF8Encoding($hasBom)))
    Move-Item -LiteralPath $temp -Destination $path -Force
}

function Set-VersionLine([string]$path, [string]$pattern, [string]$replacement, [string]$label) {
    $text = Read-Text $path
    $regex = [regex]$pattern
    if ($regex.Matches($text).Count -ne 1) { throw "$label 里没找到唯一匹配：$pattern" }
    $updated = $regex.Replace($text, $replacement, 1)
    if ($updated -ne $text) { Write-TextPreservingStyle $path $updated }
}

$currentMatch = [regex]::Match((Read-Text $projectPath), '<Version>([^<]+)</Version>')
if (-not $currentMatch.Success) { throw '读不到 csproj 里的 <Version>' }
$current = $currentMatch.Groups[1].Value.Trim()

if ([string]::IsNullOrWhiteSpace($ToVersion)) {
    $target = Get-NextVersion $current
} else {
    $target = $ToVersion.Trim().TrimStart('v', 'V')
}

Write-Host "版本：$current → $target$(if ($DryRun) { '（DryRun，不写文件）' })"

if (-not $DryRun) {
    Set-VersionLine $projectPath '<Version>[^<]+</Version>' "<Version>$target</Version>" 'csproj'
    Set-VersionLine $projectPath '<FileVersion>[^<]+</FileVersion>' "<FileVersion>$target.0</FileVersion>" 'csproj'
    Set-VersionLine $projectPath '<AssemblyVersion>[^<]+</AssemblyVersion>' "<AssemblyVersion>$target.0</AssemblyVersion>" 'csproj'
    Set-VersionLine $projectPath '<InformationalVersion>[^<]+</InformationalVersion>' "<InformationalVersion>v$target-dev</InformationalVersion>" 'csproj'
    Set-VersionLine $installerScriptPath '\[string\]\$Version = "[^"]+"' "[string]`$Version = `"$target`"" 'build-installer.ps1'
    Set-VersionLine $issPath '#define AppVersion "[^"]+"' "#define AppVersion `"$target`"" 'SteamEYA.iss'
}

Write-Output "NewVersion=$target"
