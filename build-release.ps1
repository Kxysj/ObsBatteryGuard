$ErrorActionPreference = 'Stop'

$projectRoot = $PSScriptRoot
[xml]$versionProperties = Get-Content -LiteralPath (Join-Path $projectRoot 'Directory.Build.props') -Raw
$releaseVersion = [string]$versionProperties.Project.PropertyGroup.Version
if ($releaseVersion -notmatch '^\d+\.\d+\.\d+$') { throw '发布版本号无效' }
$outputDirectory = Join-Path $projectRoot "outputs\OBS电池安全录制-v$releaseVersion"
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null

dotnet publish (Join-Path $projectRoot 'src\ObsBatteryGuard.App\ObsBatteryGuard.App.csproj') `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None `
    -o $outputDirectory
if ($LASTEXITCODE -ne 0) { throw '主界面发布失败，停止打包' }

dotnet publish (Join-Path $projectRoot 'src\ObsBatteryGuard.Guard\ObsBatteryGuard.Guard.csproj') `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None `
    -o $outputDirectory
if ($LASTEXITCODE -ne 0) { throw '后台守护发布失败，停止打包' }

$extraRuntimeConfig = Join-Path $outputDirectory 'ObsBatteryGuard.Guard.runtimeconfig.json'
if (Test-Path -LiteralPath $extraRuntimeConfig) { Remove-Item -LiteralPath $extraRuntimeConfig -Force }
Copy-Item (Join-Path $projectRoot 'README.md') (Join-Path $outputDirectory '使用说明.md') -Force
Write-Host "发布完成：$outputDirectory"
