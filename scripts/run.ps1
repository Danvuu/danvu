[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

# Use the same self-contained deployment path used by the distributable. This
# also avoids requiring a separately installed Windows App Runtime.
& (Join-Path $PSScriptRoot "build.ps1") -Configuration Debug -SkipArchive
$repository = Split-Path -Parent $PSScriptRoot
$applicationDirectory = Join-Path $repository "artifacts/publish/win-x64"
Start-Process -FilePath (Join-Path $applicationDirectory "Music.exe") -WorkingDirectory $applicationDirectory
