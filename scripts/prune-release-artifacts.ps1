param(
    [string]$ProjectRoot = '',
    [ValidateRange(1, 100)][int]$Keep = 3,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
    $ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
}

$artifactRoot = [System.IO.Path]::GetFullPath((Join-Path $ProjectRoot 'artifacts'))
if (-not (Test-Path -LiteralPath $artifactRoot)) {
    return
}

$categories = @(
    @{ Name = '发布说明 TXT'; Pattern = '^release-notes-(?<version>\d+(?:\.\d+){1,3})\.txt$' },
    @{ Name = '发布说明 MD'; Pattern = '^release-notes-v(?<version>\d+(?:\.\d+){1,3})\.md$' },
    @{ Name = '源码包'; Pattern = '^SteamEYANFJ-(?<version>\d+(?:\.\d+){1,3})-source\.zip$' },
    @{ Name = '安装包'; Pattern = '^SteamEYANFJ-(?<version>\d+(?:\.\d+){1,3})-win-x64-setup\.exe$' }
)

foreach ($category in $categories) {
    $files = @(
        Get-ChildItem -LiteralPath $artifactRoot -File |
            ForEach-Object {
                $match = [regex]::Match(
                    $_.Name,
                    $category.Pattern,
                    [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
                if (-not $match.Success) {
                    return
                }

                [pscustomobject]@{
                    File = $_
                    Version = [version]$match.Groups['version'].Value
                }
            } |
            Sort-Object Version -Descending
    )

    foreach ($entry in ($files | Select-Object -Skip $Keep)) {
        $path = [System.IO.Path]::GetFullPath($entry.File.FullName)
        if (-not $path.StartsWith($artifactRoot, [StringComparison]::OrdinalIgnoreCase)) {
            throw "拒绝删除 artifacts 目录以外的文件：$path"
        }

        if ($DryRun) {
            Write-Host "[DryRun] 将清理${($category.Name)}：$($entry.File.Name)"
        }
        else {
            Remove-Item -LiteralPath $path -Force
            Write-Host "已清理旧${($category.Name)}：$($entry.File.Name)"
        }
    }
}
