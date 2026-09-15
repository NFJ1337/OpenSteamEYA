<#
.SYNOPSIS
把本次 AOT 构建的 exe + pdb 归档到 artifacts\symbols\<版本>\，供日后还原崩溃/卡死现场（用户明确要求）。

.DESCRIPTION
为什么需要 exe：minidump 里的模块基址要与磁盘上的模块文件按 PE 时间戳/大小匹配后，dbghelp 才会读同目录的 pdb。
所以 exe 与 pdb 必须成对归档，且目录名就是版本号 —— 以后拿到 hang-*.dmp / *.dmp 时，
直接用 DumpScan 指向对应版本目录即可还原符号。

.EXAMPLE
pwsh -File scripts\archive-symbols.ps1 -Version 1.3.8
pwsh -File scripts\archive-symbols.ps1 -Version 1.3.8 -NativeDir <自定义 native 目录>
#>
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$ProjectRoot = '',
    [string]$NativeDir = '',
    [long]$WarnTotalBytes = 2GB
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
    $ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
}

if ([string]::IsNullOrWhiteSpace($NativeDir)) {
    $NativeDir = Join-Path $ProjectRoot 'SteamEyaWinUI\bin\x64\Release\net10.0-windows10.0.19041.0\win-x64\native'
}

$exe = Join-Path $NativeDir 'SteamEyaWinUI.exe'
$pdb = Join-Path $NativeDir 'SteamEyaWinUI.pdb'
if (-not (Test-Path -LiteralPath $exe) -or -not (Test-Path -LiteralPath $pdb)) {
    throw "找不到本次构建的符号：$NativeDir（需要 SteamEyaWinUI.exe 与 SteamEyaWinUI.pdb 同时在）"
}

$symbolRoot = Join-Path $ProjectRoot 'artifacts\symbols'
$target = Join-Path $symbolRoot $Version
New-Item -ItemType Directory -Force -Path $target | Out-Null

Copy-Item -LiteralPath $exe -Destination (Join-Path $target 'SteamEyaWinUI.exe') -Force
Copy-Item -LiteralPath $pdb -Destination (Join-Path $target 'SteamEyaWinUI.pdb') -Force

# PE 时间戳：把 dump 里的模块时间戳与这里的值一对，就能确认符号是否对得上。
$exeInfo = Get-Item -LiteralPath (Join-Path $target 'SteamEyaWinUI.exe')
$pdbInfo = Get-Item -LiteralPath (Join-Path $target 'SteamEyaWinUI.pdb')
$fs = [System.IO.File]::OpenRead($exeInfo.FullName)
try {
    $br = New-Object System.IO.BinaryReader($fs)
    $fs.Position = 0x3C
    $peOffset = $br.ReadInt32()
    $fs.Position = $peOffset + 8
    $timeStamp = $br.ReadUInt32()
}
finally { $fs.Close() }

$manifest = [ordered]@{
    version        = $Version
    archivedAt     = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
    sourceDir      = $NativeDir
    exeSize        = $exeInfo.Length
    peTimeStamp    = ('0x{0:X}' -f $timeStamp)
    exeSha256      = (Get-FileHash -Algorithm SHA256 -LiteralPath $exeInfo.FullName).Hash.ToLowerInvariant()
    pdbSize        = $pdbInfo.Length
    pdbSha256      = (Get-FileHash -Algorithm SHA256 -LiteralPath $pdbInfo.FullName).Hash.ToLowerInvariant()
    usage          = 'DumpScan <dump 路径> ' + $target + ' --frames 60'
}
$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $target 'manifest.json') -Encoding utf8NoBOM

$total = (Get-ChildItem -LiteralPath $symbolRoot -Recurse -File | Measure-Object -Property Length -Sum).Sum
Write-Host ("已归档符号：$target（exe {0:N1} MB + pdb {1:N1} MB）" -f ($exeInfo.Length / 1MB), ($pdbInfo.Length / 1MB))
Write-Host ("artifacts\symbols 累计：{0:N0} MB" -f ($total / 1MB))
if ($total -gt $WarnTotalBytes) {
    Write-Warning ("符号累计已超过 {0:N1} GB，可自行删除 artifacts\symbols 下不再需要的旧版本目录。" -f ($WarnTotalBytes / 1GB))
}
