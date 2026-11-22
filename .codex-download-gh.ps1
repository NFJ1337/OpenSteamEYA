$release = Invoke-RestMethod `
    -Uri "https://api.github.com/repos/cli/cli/releases/latest" `
    -Headers @{ "User-Agent" = "SteamEYA-release-helper" } `
    -TimeoutSec 60
$zipAsset = $release.assets |
    Where-Object { $_.name -match '^gh_.+_windows_amd64\.zip$' } |
    Select-Object -First 1
$checksumsAsset = $release.assets |
    Where-Object { $_.name -match '^gh_.+_checksums\.txt$' } |
    Select-Object -First 1
if (-not $zipAsset -or -not $checksumsAsset) {
    throw "GitHub CLI release assets were not found."
}

$targetDir = Join-Path $env:TEMP "steameya-gh-cli"
$zipPath = Join-Path $env:TEMP $zipAsset.name
$checksumsPath = Join-Path $env:TEMP $checksumsAsset.name
if (Test-Path -LiteralPath $targetDir) {
    $resolved = (Resolve-Path -LiteralPath $targetDir).Path
    $tempRoot = [IO.Path]::GetFullPath($env:TEMP)
    if (-not $resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Unexpected GitHub CLI temp directory."
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}

Invoke-WebRequest -UseBasicParsing -Uri $zipAsset.browser_download_url -OutFile $zipPath -TimeoutSec 180
Invoke-WebRequest -UseBasicParsing -Uri $checksumsAsset.browser_download_url -OutFile $checksumsPath -TimeoutSec 60

$expectedHash = $null
foreach ($line in Get-Content -LiteralPath $checksumsPath) {
    if ($line -match "^([0-9a-fA-F]{64})\s+\*?$([regex]::Escape($zipAsset.name))$") {
        $expectedHash = $matches[1].ToLowerInvariant()
        break
    }
}
if (-not $expectedHash) {
    throw "Checksum for $($zipAsset.name) was not found."
}

$actualHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $zipPath).Hash.ToLowerInvariant()
if ($actualHash -ne $expectedHash) {
    throw "GitHub CLI checksum mismatch."
}

Expand-Archive -LiteralPath $zipPath -DestinationPath $targetDir -Force
$gh = Get-ChildItem -LiteralPath $targetDir -Recurse -Filter "gh.exe" -File |
    Select-Object -First 1
if (-not $gh) {
    throw "gh.exe was not found after extraction."
}

$version = (& $gh.FullName --version | Select-Object -First 1)
[ordered]@{
    Version = $version
    GhPath = $gh.FullName
    Sha256 = $actualHash
} | ConvertTo-Json -Compress
