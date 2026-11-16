[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$SourceDirectory = $PSScriptRoot
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw "Music is a native Windows desktop application."
}

$source = (Resolve-Path -LiteralPath $SourceDirectory).Path
if (-not (Test-Path -LiteralPath (Join-Path $source "Music.exe"))) {
    throw "Run this script from an extracted Music distribution, or provide its folder with -SourceDirectory."
}

$destination = Join-Path $env:LOCALAPPDATA "Programs/Music"
if ($source.TrimEnd('\', '/') -eq $destination.TrimEnd('\', '/')) {
    throw "Music is already in its installation folder. Run Music.exe directly."
}

if ((Test-Path -LiteralPath $destination) -and
    -not (Test-Path -LiteralPath (Join-Path $destination ".music-install")) -and
    (Get-ChildItem -LiteralPath $destination -Force | Select-Object -First 1)) {
    throw "The installation directory already contains files from another source. No files were replaced."
}

$running = Get-Process -Name "Music" -ErrorAction SilentlyContinue
if ($running) {
    throw "Close Music before installing or updating it."
}

if ($PSCmdlet.ShouldProcess($destination, "Install Music for the current Windows user")) {
    $startMenu = Join-Path ([Environment]::GetFolderPath("Programs")) "Music.lnk"
    $shell = New-Object -ComObject WScript.Shell
    try {
        $shortcut = $shell.CreateShortcut($startMenu)
        if ((Test-Path -LiteralPath $startMenu) -and
            $shortcut.TargetPath -ne (Join-Path $destination "Music.exe")) {
            throw "A Music Start menu shortcut already belongs to another application. No files were replaced."
        }

        New-Item -ItemType Directory -Path $destination -Force | Out-Null
        Copy-Item -Path (Join-Path $source "*") -Destination $destination -Recurse -Force
        Set-Content -LiteralPath (Join-Path $destination ".music-install") -Value "Music per-user installation" -Encoding UTF8

        $shortcut.TargetPath = Join-Path $destination "Music.exe"
        $shortcut.WorkingDirectory = $destination
        $shortcut.Description = "Music desktop player"
        $shortcut.Save()
    }
    finally {
        [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) | Out-Null
    }

    Write-Host "Installed to $destination"
    Write-Host "Open Music from the Windows Start menu."
    Write-Host "To uninstall, run uninstall.ps1 in the installation folder."
}
