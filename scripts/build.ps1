[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$SkipArchive
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw "The native WinUI application must be built on Windows. Core tests can run on other operating systems."
}

$repository = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repository "src/Danvu.Windows/Danvu.Windows.csproj"
$publishDirectory = Join-Path $repository "artifacts/publish/win-x64"
$archivePath = Join-Path $repository "artifacts/Music-win-x64.zip"

function Invoke-DotNet {
    param([string[]]$Arguments)
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments[0]) failed with exit code $LASTEXITCODE."
    }
}

Push-Location $repository
try {
    Invoke-DotNet @("--version")
    $properties = @(
        "-p:Platform=x64",
        "-p:WindowsPackageType=None",
        "-p:WindowsAppSDKSelfContained=true",
        "-p:SelfContained=true"
    )

    Invoke-DotNet (@("restore", $project, "--runtime", "win-x64") + $properties)

    # This directory belongs to this script. Clearing it prevents stale binaries
    # from being included when a project dependency changes.
    if (Test-Path -LiteralPath $publishDirectory) {
        Remove-Item -LiteralPath $publishDirectory -Recurse -Force
    }

    Invoke-DotNet (@(
        "publish", $project,
        "--no-restore",
        "--configuration", $Configuration,
        "--runtime", "win-x64",
        "--self-contained", "true",
        "--output", $publishDirectory,
        "-p:PublishSingleFile=false",
        "-p:PublishTrimmed=false"
    ) + $properties)

    $executable = Join-Path $publishDirectory "Music.exe"
    if (-not (Test-Path -LiteralPath $executable)) {
        throw "Publish completed without producing $executable."
    }

    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "install.ps1") -Destination $publishDirectory -Force
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "uninstall.ps1") -Destination $publishDirectory -Force
    Copy-Item -LiteralPath (Join-Path $repository "docs/DISTRIBUTION.txt") -Destination (Join-Path $publishDirectory "README.txt") -Force

    if (-not $SkipArchive) {
        Compress-Archive -Path (Join-Path $publishDirectory "*") -DestinationPath $archivePath -Force
        Write-Host "Windows distribution: $archivePath"
    }
    Write-Host "Native executable: $executable"
}
finally {
    Pop-Location
}
