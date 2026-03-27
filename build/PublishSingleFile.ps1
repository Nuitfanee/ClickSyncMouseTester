$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $PSScriptRoot
$buildOutputRoot = Join-Path $projectRoot ".artifacts\build\"
$publishOutputDir = Join-Path $projectRoot ".artifacts\publish\win-x64-self-contained-single-file\"
$packageCacheDir = Join-Path $env:USERPROFILE ".nuget\packages"

$env:DOTNET_CLI_HOME = Join-Path $projectRoot ".dotnet"
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
$env:NUGET_PACKAGES = $packageCacheDir

dotnet restore (Join-Path $projectRoot "ClickSyncMouseTester.vbproj") `
  -r win-x64 `
  --ignore-failed-sources `
  -p:NuGetAudit=false `
  -p:BaseOutputPath="$buildOutputRoot"

if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

dotnet publish (Join-Path $projectRoot "ClickSyncMouseTester.vbproj") `
  -p:PublishProfile='Properties\PublishProfiles\SingleFile-win-x64.pubxml' `
  -p:PublishDir="$publishOutputDir" `
  -p:BaseOutputPath="$buildOutputRoot" `
  --no-restore

if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

Get-Item (Join-Path $publishOutputDir "ClickSyncMouseTester.exe") |
    Select-Object FullName, Length, LastWriteTime
