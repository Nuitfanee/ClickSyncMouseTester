param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^v?\d+\.\d+\.\d+([-.][0-9A-Za-z.-]+)?$')]
    [string]$Version,

    [string]$Configuration = 'Release',

    [switch]$SkipTests,

    [switch]$NoClean,

    [switch]$KeepPublishOutput
)

$ErrorActionPreference = 'Stop'

Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot 'src\ClickSyncMouseTester\ClickSyncMouseTester.csproj'
$testProjectPath = Join-Path $repoRoot 'tests\ClickSyncMouseTester.Tests\ClickSyncMouseTester.Tests.csproj'
$runtimeIdentifier = 'win-x64'
$targetFramework = 'net10.0-windows'
$productName = 'ClickSyncMouseTester'

$normalizedVersion = $Version.Trim()
if (-not $normalizedVersion.StartsWith('v', [System.StringComparison]::OrdinalIgnoreCase)) {
    $normalizedVersion = "v$normalizedVersion"
}

$packageVersion = $normalizedVersion.Substring(1)
$assemblyVersion = [regex]::Match($packageVersion, '^\d+\.\d+\.\d+').Value
$releaseRoot = Join-Path $repoRoot "artifacts\releases\$normalizedVersion"
$publishRoot = Join-Path $repoRoot "artifacts\publish\$normalizedVersion"

$standalonePublishDir = Join-Path $publishRoot 'standalone'
$frameworkPublishDir = Join-Path $publishRoot 'requires-dotnet10'

$standaloneName = "$productName-$normalizedVersion-$runtimeIdentifier-standalone.exe"
$frameworkName = "$productName-$normalizedVersion-$runtimeIdentifier-requires-dotnet10.exe"

$standaloneOutput = Join-Path $releaseRoot $standaloneName
$frameworkOutput = Join-Path $releaseRoot $frameworkName
$checksumOutput = Join-Path $releaseRoot 'SHA256SUMS.txt'

function Invoke-LoggedCommand {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Label,

        [Parameter(Mandatory = $true)]
        [string]$FilePath,

        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    Write-Host ""
    Write-Host "==> $Label" -ForegroundColor Cyan
    Write-Host "$FilePath $($Arguments -join ' ')" -ForegroundColor DarkGray

    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Label failed with exit code $LASTEXITCODE."
    }
}

function Copy-PublishedExe {
    param(
        [Parameter(Mandatory = $true)]
        [string]$PublishDir,

        [Parameter(Mandatory = $true)]
        [string]$DestinationPath
    )

    $sourceExe = Join-Path $PublishDir "$productName.exe"
    if (-not (Test-Path -LiteralPath $sourceExe)) {
        throw "Publish output was not found: $sourceExe"
    }

    Copy-Item -LiteralPath $sourceExe -Destination $DestinationPath -Force
}

function Write-Checksums {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Files,

        [Parameter(Mandatory = $true)]
        [string]$DestinationPath
    )

    $lines = foreach ($file in $Files) {
        $hash = Get-FileHash -Algorithm SHA256 -LiteralPath $file
        "$($hash.Hash.ToLowerInvariant())  $(Split-Path -Leaf $file)"
    }

    $utf8NoBom = [System.Text.UTF8Encoding]::new($false)
    [System.IO.File]::WriteAllLines($DestinationPath, $lines, $utf8NoBom)
}

Write-Host "Publishing $productName $normalizedVersion" -ForegroundColor Green
Write-Host "Release output: $releaseRoot"

if (-not (Test-Path -LiteralPath $projectPath)) {
    throw "Project file was not found: $projectPath"
}

if (-not $SkipTests -and -not (Test-Path -LiteralPath $testProjectPath)) {
    throw "Test project file was not found: $testProjectPath"
}

if (-not $NoClean) {
    Remove-Item -LiteralPath $releaseRoot -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $publishRoot -Recurse -Force -ErrorAction SilentlyContinue
}

New-Item -ItemType Directory -Force -Path $releaseRoot | Out-Null
New-Item -ItemType Directory -Force -Path $standalonePublishDir | Out-Null
New-Item -ItemType Directory -Force -Path $frameworkPublishDir | Out-Null

if (-not $SkipTests) {
    Invoke-LoggedCommand `
        -Label 'Run regression tests' `
        -FilePath 'dotnet' `
        -Arguments @(
            'run',
            '--project', $testProjectPath,
            '-c', $Configuration
        )
}

$commonPublishArgs = @(
    'publish', $projectPath,
    '-c', $Configuration,
    '-f', $targetFramework,
    '-r', $runtimeIdentifier,
    "/p:Version=$packageVersion",
    "/p:AssemblyVersion=$assemblyVersion",
    "/p:FileVersion=$assemblyVersion",
    "/p:InformationalVersion=$normalizedVersion",
    '/p:PublishSingleFile=true',
    '/p:IncludeNativeLibrariesForSelfExtract=true',
    '/p:DebugType=None',
    '/p:DebugSymbols=false'
)

Invoke-LoggedCommand `
    -Label 'Publish standalone build with .NET runtime' `
    -FilePath 'dotnet' `
    -Arguments ($commonPublishArgs + @(
        '--self-contained', 'true',
        '/p:PublishTrimmed=false',
        '/p:EnableCompressionInSingleFile=true',
        '-o', $standalonePublishDir
    ))

Copy-PublishedExe -PublishDir $standalonePublishDir -DestinationPath $standaloneOutput

Invoke-LoggedCommand `
    -Label 'Publish framework-dependent build requiring .NET 10 Desktop Runtime' `
    -FilePath 'dotnet' `
    -Arguments ($commonPublishArgs + @(
        '--self-contained', 'false',
        '-o', $frameworkPublishDir
    ))

Copy-PublishedExe -PublishDir $frameworkPublishDir -DestinationPath $frameworkOutput

Write-Checksums -Files @($standaloneOutput, $frameworkOutput) -DestinationPath $checksumOutput

if (-not $KeepPublishOutput) {
    Remove-Item -LiteralPath $publishRoot -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ""
Write-Host "Release files:" -ForegroundColor Green
Get-ChildItem -LiteralPath $releaseRoot -File | Sort-Object Name | ForEach-Object {
    $sizeMb = [Math]::Round($_.Length / 1MB, 2)
    Write-Host ("  {0} ({1} MB)" -f $_.Name, $sizeMb)
}

Write-Host ""
Write-Host "Suggested release notes:" -ForegroundColor Green
Write-Host "Two Windows x64 builds are included in this release."
Write-Host ""
Write-Host $standaloneName
Write-Host "- Includes .NET runtime"
Write-Host "- Recommended for most users"
Write-Host "- Larger file size"
Write-Host ""
Write-Host $frameworkName
Write-Host "- Requires .NET 10 Desktop Runtime"
Write-Host "- Smaller file size"
Write-Host "- Better for developer or preconfigured systems"
