$ErrorActionPreference = 'Stop'
$transferRoot = $PSScriptRoot
[xml]$properties = Get-Content -LiteralPath (Join-Path $transferRoot 'Directory.Build.props') -Raw
$version = [string]$properties.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw '版本号无效' }
$release = Join-Path $transferRoot "outputs\OBS电池安全录制-v$version"
$archivePath = Join-Path $transferRoot "outputs\OBS电池安全录制-v$version-软件及源码.zip"
if (Test-Path -LiteralPath $archivePath) { throw '压缩包已经存在，未覆盖；请先为已有包改名。' }
$entries = [ordered]@{}
foreach ($folder in @('src','tests','artwork')) {
    Get-ChildItem -LiteralPath (Join-Path $transferRoot $folder) -File -Recurse -Force |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj|\.vs|\.git)[\\/]|\.(user|suo|pdb)$' } |
        Sort-Object FullName | ForEach-Object {
            $relative = $_.FullName.Substring($transferRoot.Length + 1).Replace('\','/')
            $entries['源码/' + $relative] = $_.FullName
        }
}
foreach ($file in @('.gitignore','Directory.Build.props','ObsBatteryGuard.sln','README.md','build-release.ps1','build-transfer.ps1','迁移说明.md')) {
    $entries['源码/' + $file] = Join-Path $transferRoot $file
}
foreach ($file in @('OBS电池安全录制.exe','ObsBatteryGuard.Guard.exe','使用说明.md')) {
    $path = Join-Path $release $file
    if (!(Test-Path -LiteralPath $path)) { throw ('发布文件缺失：' + $path) }
    if ($file.EndsWith('.exe') -and [Diagnostics.FileVersionInfo]::GetVersionInfo($path).ProductVersion -ne $version) { throw '前后台发布版本不一致' }
    $entries['软件/' + $file] = $path
}
$entries['迁移说明.md'] = Join-Path $transferRoot '迁移说明.md'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::Open($archivePath, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($name in $entries.Keys) {
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $entries[$name], $name, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
} finally { $zip.Dispose() }
$zip = [IO.Compression.ZipFile]::OpenRead($archivePath)
try {
    if ($zip.Entries.Count -ne $entries.Count) { throw '条目数量不一致' }
    foreach ($entry in $zip.Entries) {
        $stream = $entry.Open()
        $sha = [Security.Cryptography.SHA256]::Create()
        try { $actual = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-','') }
        finally { $sha.Dispose(); $stream.Dispose() }
        if ($actual -ne (Get-FileHash -LiteralPath $entries[$entry.FullName] -Algorithm SHA256).Hash) { throw ('内容校验失败：' + $entry.FullName) }
    }
    Write-Output ('PASS 全部压缩条目 SHA256 校验：' + $zip.Entries.Count)
} finally { $zip.Dispose() }
Get-Item -LiteralPath $archivePath | Select-Object FullName,Length
Get-FileHash -LiteralPath $archivePath -Algorithm SHA256
