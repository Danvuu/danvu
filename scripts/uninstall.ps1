[CmdletBinding(SupportsShouldProcess, ConfirmImpact = "Medium")]
param()

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw "This script uninstalls the Windows desktop application."
}

$destination = Join-Path $env:LOCALAPPDATA "Programs/Music"
if (-not (Test-Path -LiteralPath (Join-Path $destination ".music-install"))) {
    throw "No installation created by Music's install.ps1 was found. No files were removed."
}

if (Get-Process -Name "Music" -ErrorAction SilentlyContinue) {
    throw "Close Music before uninstalling it."
}

if ($PSCmdlet.ShouldProcess($destination, "Remove Music application files and Start menu shortcut")) {
    $startMenu = Join-Path ([Environment]::GetFolderPath("Programs")) "Music.lnk"
    if (Test-Path -LiteralPath $startMenu) {
        $shell = New-Object -ComObject WScript.Shell
        try {
            $shortcut = $shell.CreateShortcut($startMenu)
            if ($shortcut.TargetPath -eq (Join-Path $destination "Music.exe")) {
                Remove-Item -LiteralPath $startMenu -Force
            }
        }
        finally {
            [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) | Out-Null
        }
    }
    Remove-Item -LiteralPath $destination -Recurse -Force
    Write-Host "Music was removed. Personal app settings and account data were retained."
}
