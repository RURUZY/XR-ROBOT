$ErrorActionPreference = "Stop"

$projectRoot = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$sourceRoot = Join-Path $projectRoot "LinuxSDK_Source\Linux_CameraSDK-2.1.1_MediaSDK-3.1.1"
$sdkTarget = Join-Path $PSScriptRoot "sdk"

New-Item -ItemType Directory -Path $sdkTarget -Force | Out-Null

Copy-Item -LiteralPath (Join-Path $sourceRoot "CameraSDK-2.1.1-Linux.tar.gz") `
    -Destination (Join-Path $sdkTarget "CameraSDK-2.1.1-Linux.tar.gz") -Force
Copy-Item -LiteralPath (Join-Path $sourceRoot "libMediaSDK-dev-3.1.1.0-amd64.tar_1758540334111.xz") `
    -Destination (Join-Path $sdkTarget "libMediaSDK-dev-3.1.1.0-amd64.tar.xz") -Force

Write-Host "SDK build context prepared at $sdkTarget"
