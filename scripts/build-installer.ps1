[CmdletBinding()]
param(
    [ValidateSet('Build', 'Verify')][string]$Action = 'Build',
    [ValidatePattern('^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$')][string]$Version = '0.1.1-preview.1',
    [string]$PublishDirectory = (Join-Path $PSScriptRoot '../artifacts/publish/win-x64'),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../artifacts')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Invoke-OwnedProcess {
    param([string]$Executable, [string]$Arguments, [int]$TimeoutMilliseconds = 300000)
    $start = New-Object System.Diagnostics.ProcessStartInfo
    $start.FileName = $Executable; $start.Arguments = $Arguments; $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $start
    try {
        if (-not $process.Start()) { throw 'Owned build/verification process did not start.' }
        $stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutMilliseconds)) { $process.Kill(); $process.WaitForExit(); throw 'Owned build/verification process timed out.' }
        $process.WaitForExit()
        $nativeExit = $process.ExitCode
        if ($null -eq $nativeExit) { throw 'Owned process returned no exit status.' }
        if (-not [Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($stdout, $stderr), 10000)) { throw 'Owned process output was incomplete.' }
        return [pscustomobject]@{ ExitCode = $nativeExit; Stdout = $stdout.GetAwaiter().GetResult(); Stderr = $stderr.GetAwaiter().GetResult() }
    }
    finally { $process.Dispose() }
}

function Get-InstallerPayloadManifest {
    param([string]$Directory)
    return @(Get-ChildItem -LiteralPath $Directory -File -Recurse | Where-Object { $_.Name -notin @('install.ps1', 'uninstall.ps1') } | Sort-Object FullName | ForEach-Object {
        [pscustomobject]@{ Path = $_.FullName.Substring($Directory.Length).TrimStart('\', '/'); Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    })
}

function Test-PackageResource {
    param([string]$Resource, [string]$Directory)
    return $Resource.Replace('/', '\').IndexOf($Directory.Replace('/', '\').TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -ge 0
}

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'The native installer must be built/verified on Windows.' }
$PublishDirectory = [IO.Path]::GetFullPath($PublishDirectory).TrimEnd('\', '/')
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$repository = Split-Path -Parent $PSScriptRoot
$diagnostics = Join-Path $OutputDirectory 'native-smoke'
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $diagnostics -Force | Out-Null
$installer = Join-Path $OutputDirectory 'Music-Setup.exe'
$archive = Join-Path $OutputDirectory 'Music-win-x64.zip'
$report = [ordered]@{
    Action = $Action; SourceCommit = $env:GITHUB_SHA; Version = $Version; PublisherMetadata = 'Danvu'
    Result = 'Unverified'; Stage = 'ValidatePayload'; CompilerVersion = $null; InstallerSha256 = $null; ArchiveSha256 = $null
    AuthenticodeStatus = $null; VerifiedWindowsPublisher = $false; Defender = $null; Scans = @(); Detections = @()
    InstalledFilesVerified = 0; SilentInstallExit = $null; SilentUninstallExit = $null; UninstallerVerified = $false
    Errors = @(); FinishedUtc = $null
    Scope = 'No app autostart, administrator requirement, AV exclusion, trust-store change, or binary publication. Publisher metadata alone is not a verified Windows signature.'
}
$verificationDirectory = $null; $uninstaller = $null

try {
    foreach ($required in @('Music.exe', 'Music.dll', 'Assets/Music.ico')) {
        if (-not (Test-Path -LiteralPath (Join-Path $PublishDirectory $required))) { throw "Required tested payload file missing: $required" }
    }
    $project = [xml](Get-Content -LiteralPath (Join-Path $repository 'src/Danvu.Windows/Danvu.Windows.csproj') -Raw)
    $declared = $project.SelectSingleNode('/Project/PropertyGroup/Version')
    if ($null -eq $declared -or $declared.InnerText -ne $Version) { throw 'Installer version differs from the native project version.' }
    $payload = @(Get-InstallerPayloadManifest $PublishDirectory)
    if ($Action -eq 'Build') {
        $report.Stage = 'LocateCompiler'
        $candidates = @((Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) 'Inno Setup 6/ISCC.exe'))
        $command = Get-Command ISCC.exe -CommandType Application -ErrorAction SilentlyContinue
        if ($null -ne $command) { $candidates += $command.Source }
        $compiler = @($candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1)
        if ($compiler.Count -eq 0) { throw 'An existing Inno Setup 6.3+ compiler is required. No executable was downloaded or installed.' }
        $report.CompilerVersion = (Get-Item -LiteralPath $compiler[0]).VersionInfo.FileVersion
        $match = [regex]::Match($report.CompilerVersion, '\d+\.\d+(?:\.\d+)?')
        if (-not $match.Success -or [version]$match.Value -lt [version]'6.3') { throw 'Existing Inno Setup compiler is older than 6.3.' }
        $report.Stage = 'CompileInstaller'
        $fileVersion = ($Version -split '-')[0] + '.0'
        $arguments = '/Qp /DSourceDir="' + $PublishDirectory + '" /DOutputDir="' + $OutputDirectory + '" /DAppVersion=' + $Version + ' /DAppFileVersion=' + $fileVersion + ' "' + (Join-Path $repository 'packaging/windows/Music.iss') + '"'
        $result = Invoke-OwnedProcess $compiler[0] $arguments
        $result.Stdout | Set-Content (Join-Path $diagnostics 'installer-build.stdout.log') -Encoding UTF8
        $result.Stderr | Set-Content (Join-Path $diagnostics 'installer-build.stderr.log') -Encoding UTF8
        if ($result.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $installer)) { throw "Inno Setup did not produce the installer (exit $($result.ExitCode))." }
        Compress-Archive -Path (Join-Path $PublishDirectory '*') -DestinationPath $archive -Force
        $report.Result = 'BuiltNotYetVerified'
    }
    else {
        if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') { throw 'Silent installer verification is restricted to the isolated GitHub-hosted runner. No client installation was attempted.' }
        $report.Stage = 'RequireNativePass'
        $native = Get-Content -LiteralPath (Join-Path $diagnostics 'report.json') -Raw | ConvertFrom-Json
        if ($native.Result -ne 'Passed' -or $native.SourceCommit -ne $env:GITHUB_SHA) { throw 'The same-source protected native UI checks must pass before installer verification.' }
        $testedFiles = @(Get-Content -LiteralPath (Join-Path $diagnostics 'bundle-sha256.json') -Raw | ConvertFrom-Json)
        if ($testedFiles.Count -lt 3) { throw 'The protected native bundle hash inventory is missing or incomplete.' }
        foreach ($candidate in $testedFiles) {
            if ((Get-FileHash -LiteralPath (Join-Path $PublishDirectory $candidate.Path) -Algorithm SHA256).Hash -ine $candidate.Sha256) { throw 'Native payload changed after the protected UI run.' }
        }
        $report.Stage = 'VerifyFinalPackage'
        $report.InstallerSha256 = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash
        $report.ArchiveSha256 = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
        $signature = Get-AuthenticodeSignature -LiteralPath $installer
        $report.AuthenticodeStatus = [string]$signature.Status
        $report.VerifiedWindowsPublisher = $signature.Status -eq 'Valid'
        if ($signature.Status -notin @('Valid', 'NotSigned')) { throw 'Final installer has an invalid or unverifiable Authenticode signature.' }
        if ((Get-Item -LiteralPath $installer).VersionInfo.CompanyName -ne 'Danvu') { throw 'Installer publisher/company metadata is not Danvu.' }
        Update-MpSignature -ErrorAction Stop
        $status = Get-MpComputerStatus -ErrorAction Stop
        $report.Defender = $status | Select-Object AMEngineVersion, AMProductVersion, AMServiceEnabled, AntivirusEnabled, RealTimeProtectionEnabled, AntivirusSignatureVersion, AntivirusSignatureLastUpdated, DefenderSignaturesOutOfDate
        $age = ([DateTime]::UtcNow - ([DateTime]$status.AntivirusSignatureLastUpdated).ToUniversalTime()).TotalHours
        if (-not $status.AMServiceEnabled -or -not $status.AntivirusEnabled -or -not $status.RealTimeProtectionEnabled -or $status.DefenderSignaturesOutOfDate -ne $false -or $age -gt 48 -or $age -lt -1) { throw 'Current active Defender protection could not be verified for the final installer.' }
        $platform = Join-Path $env:ProgramData 'Microsoft/Windows Defender/Platform'
        $scanners = @()
        if (Test-Path -LiteralPath $platform) {
            $scanners += @(Get-ChildItem $platform -Directory | Sort-Object @{Expression = { try { [version](($_.Name -split '-')[0]) } catch { [version]'0.0' } }} -Descending | ForEach-Object { Join-Path $_.FullName 'MpCmdRun.exe' })
        }
        $scanners += (Join-Path $env:ProgramFiles 'Windows Defender/MpCmdRun.exe')
        $scanner = @($scanners | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1)
        if ($scanner.Count -eq 0) { throw 'Supported Defender scanner is unavailable.' }
        $report.Stage = 'ScanFinalPackages'
        foreach ($package in @($installer, $archive)) {
            $scan = Invoke-OwnedProcess $scanner[0] ('-Scan -ScanType 3 -File "' + $package + '"')
            $name = [IO.Path]::GetFileName($package)
            $scan.Stdout | Set-Content (Join-Path $diagnostics ($name + '.defender.stdout.log')) -Encoding UTF8
            $scan.Stderr | Set-Content (Join-Path $diagnostics ($name + '.defender.stderr.log')) -Encoding UTF8
            $report.Scans += [pscustomobject]@{ File = $name; ExitCode = $scan.ExitCode }
            if ($scan.ExitCode -ne 0) { throw "Normal Defender scan failed or reported a detection for $name; package was not executed." }
        }
        $report.Detections = @(Get-MpThreatDetection -ErrorAction Stop | Where-Object {
            @($_.Resources | Where-Object { Test-PackageResource -Resource ([string]$_) -Directory $OutputDirectory }).Count -gt 0
        } | Select-Object ThreatID, ActionSuccess, Resources)
        if ($report.Detections.Count -gt 0) { throw 'Defender has scoped package detections; no installer was executed and no remediation was reversed.' }
        if ((Get-FileHash $installer -Algorithm SHA256).Hash -ine $report.InstallerSha256 -or (Get-FileHash $archive -Algorithm SHA256).Hash -ine $report.ArchiveSha256) { throw 'Package bytes changed during normal Defender scanning.' }
        $report.Stage = 'SilentInstall'
        $uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{4D1B0428-0FE0-44F5-8A31-9E39D4F1431A}_is1'
        if (Test-Path -LiteralPath $uninstallKey) { throw 'An existing installation registry entry was preserved; isolated install verification cannot proceed.' }
        $verificationDirectory = Join-Path $env:RUNNER_TEMP ('Music-installer-check-' + [guid]::NewGuid().ToString('N'))
        $arguments = '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /DIR="' + $verificationDirectory + '" /LOG="' + (Join-Path $diagnostics 'installer-install.log') + '"'
        $install = Invoke-OwnedProcess $installer $arguments
        $report.SilentInstallExit = $install.ExitCode
        if ($install.ExitCode -ne 0) { throw 'Silent per-user installer verification failed.' }
        foreach ($file in $payload) {
            if ((Get-FileHash -LiteralPath (Join-Path $verificationDirectory $file.Path) -Algorithm SHA256).Hash -ine $file.Sha256) { throw 'Installed payload differs from the protected tested native bundle.' }
            $report.InstalledFilesVerified++
        }
        foreach ($required in @('Music.exe', 'Music.dll', 'Microsoft.UI.Xaml.dll', 'Assets/Artwork/afterglow.png')) {
            if (-not (Test-Path -LiteralPath (Join-Path $verificationDirectory $required))) { throw 'Expected installed application/runtime/artwork is missing.' }
        }
        $uninstallers = @(Get-ChildItem -LiteralPath $verificationDirectory -Filter 'unins*.exe' -File)
        if ($uninstallers.Count -ne 1) { throw 'Native per-user uninstaller was not created.' }
        $uninstaller = $uninstallers[0].FullName
        $report.UninstallerVerified = $true
        $report.Stage = 'SilentUninstall'
        $remove = Invoke-OwnedProcess $uninstaller ('/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG="' + (Join-Path $diagnostics 'installer-uninstall.log') + '"')
        $report.SilentUninstallExit = $remove.ExitCode
        if ($remove.ExitCode -ne 0) { throw 'Native per-user uninstaller returned a failure.' }
        $clock = [Diagnostics.Stopwatch]::StartNew()
        while (((Test-Path (Join-Path $verificationDirectory 'Music.exe')) -or (Test-Path $uninstallKey)) -and $clock.Elapsed.TotalSeconds -lt 10) { Start-Sleep -Milliseconds 200 }
        if ((Test-Path (Join-Path $verificationDirectory 'Music.exe')) -or (Test-Path $uninstallKey)) { throw 'Native uninstaller did not remove the owned installation.' }
        $report.Result = 'Passed'
    }
}
catch { $report.Result = 'Failed'; $report.Errors += $_.Exception.Message }
finally {
    $report.FinishedUtc = [DateTime]::UtcNow.ToString('o')
    $report | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $diagnostics ('installer-' + $Action.ToLowerInvariant() + '.json')) -Encoding UTF8
    Write-Host "Installer $Action result: $($report.Result). Publisher metadata: Danvu. Authenticode: $($report.AuthenticodeStatus)."
}
if ($report.Result -notin @('Passed', 'BuiltNotYetVerified')) { exit 1 }
