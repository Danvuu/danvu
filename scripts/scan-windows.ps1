[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ArchivePath,
    [Parameter(Mandatory = $true)]
    [string]$ChecksumPath,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot "../artifacts/antivirus")
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

# Diagnose the exact withdrawn build. Do not rebuild it, add exclusions, change
# Defender preferences, unblock the executable, or execute any bundled program.
$releaseTag = "v0.1.0-preview.1"
$expectedArchiveHash = "5abc1a283f7c70fd6690ab6bc4c4df35fa2ea3bff66c7c4e93d01132d305bd64"
$expectedExecutableHash = "4376b77e3be4c9de932b4baafe9bef373560c6c3e1904da0c9c1b6602e50dbf5"

function Test-ResourceUnderDirectory {
    param([string]$Resource, [string]$Directory)
    if ([string]::IsNullOrWhiteSpace($Resource) -or [string]::IsNullOrWhiteSpace($Directory)) { return $false }
    $normalizedRoot = $Directory.Replace('/', '\').TrimEnd('\')
    $normalizedResource = $Resource.Replace('/', '\')
    # Defender resources can contain file:_, containerfile:_, or webfile:_ prefixes.
    # Require a directory separator so a sibling such as Music-sample2 cannot match.
    return $normalizedResource.IndexOf($normalizedRoot + '\', [StringComparison]::OrdinalIgnoreCase) -ge 0
}

function Test-ArchiveEntryDestination {
    param([string]$EntryName, [string]$Destination)
    if ([string]::IsNullOrWhiteSpace($EntryName) -or $EntryName.Contains(':')) { return $false }
    $root = [IO.Path]::GetFullPath($Destination).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $entry = $EntryName.Replace('\', [IO.Path]::DirectorySeparatorChar).Replace('/', [IO.Path]::DirectorySeparatorChar)
    if ([IO.Path]::IsPathRooted($entry)) { return $false }
    try { $path = [IO.Path]::GetFullPath([IO.Path]::Combine($root, $entry)) }
    catch { return $false }
    return $path.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
}

function Get-DefenderStatusSnapshot {
    $status = Get-MpComputerStatus -ErrorAction Stop
    $snapshot = [ordered]@{}
    foreach ($name in @("AMEngineVersion", "AMProductVersion", "AMServiceEnabled", "AntivirusEnabled",
        "RealTimeProtectionEnabled", "AntivirusSignatureVersion", "AntivirusSignatureLastUpdated", "DefenderSignaturesOutOfDate")) {
        $property = $status.PSObject.Properties[$name]
        $snapshot[$name] = if ($null -eq $property) { $null } else { $property.Value }
    }
    return [pscustomobject]$snapshot
}

function Invoke-DefenderCommand {
    param([string]$Executable, [string[]]$Arguments, [string]$LogName, [int]$TimeoutSeconds = 480)
    $stdout = Join-Path $OutputDirectory ($LogName + ".stdout.txt")
    $stderr = Join-Path $OutputDirectory ($LogName + ".stderr.txt")
    # Start-Process joins ArgumentList; quote each argument explicitly. Windows
    # paths cannot contain a literal double quote. No shell interprets this line.
    $quoted = @($Arguments | ForEach-Object {
        if ($_.Contains('"')) { throw "Invalid quote in scanner argument." }
        '"' + $_ + '"'
    })
    $process = Start-Process -FilePath $Executable -ArgumentList ($quoted -join ' ') -NoNewWindow -PassThru `
        -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        $process.Kill()
        throw "Microsoft Defender command timed out ($LogName). The scan is unverified."
    }
    $process.WaitForExit()
    $output = @()
    foreach ($path in @($stdout, $stderr)) {
        if (Test-Path -LiteralPath $path) { $output += [IO.File]::ReadAllText($path) }
    }
    return [pscustomobject]@{ ExitCode = $process.ExitCode; Text = ($output -join "`n"); Log = $LogName }
}

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw "This script requires Windows and Microsoft Defender. No sample was executed."
}

$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$sampleRoot = Join-Path ([IO.Path]::GetTempPath()) ("Music-Defender-" + [guid]::NewGuid().ToString("N"))
$bundle = Join-Path $sampleRoot "bundle"
$scopeDirectories = @($sampleRoot, [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($ArchivePath)))
$report = [ordered]@{
    ReleaseTag = $releaseTag
    PreviewWithdrawn = $true
    StartedUtc = [DateTime]::UtcNow.ToString("o")
    FinishedUtc = $null
    Result = "Unverified"
    Stage = "Initialize"
    ExpectedArchiveSha256 = $expectedArchiveHash
    ArchiveSha256 = $null
    ExpectedMusicExeSha256 = $expectedExecutableHash
    MusicExeSha256 = $null
    MusicExeHashSource = $null
    HashesVerified = $false
    SignatureUpdate = "NotAttempted"
    SignatureFreshness = "NotVerified"
    SignatureAgeHours = $null
    InitialDefenderStatus = $null
    FinalDefenderStatus = $null
    ScannerPath = $null
    DisableRemediationAdvertised = $false
    Scans = @()
    DetectionQuery = "NotAttempted"
    Detections = @()
    Errors = @()
    Warnings = @()
    Note = "Diagnostic only. No application was executed. No exclusion, protection preference change, or remediation requested. A single-engine scan cannot establish that a file is safe."
}

try {
    # Hash the original ZIP and its Music.exe entry before Defender readiness
    # checks, so an unavailable runner still produces useful identity evidence.
    $report.Stage = "VerifyArchive"
    $publishedChecksum = ([IO.File]::ReadAllText([IO.Path]::GetFullPath($ChecksumPath))).Trim()
    if ($publishedChecksum -notmatch '^([0-9a-fA-F]{64})\s+\*?Music-win-x64\.zip$') { throw "Published checksum file has an unexpected format." }
    if ($Matches[1] -ine $expectedArchiveHash) { throw "Published checksum does not match the pinned original release." }
    $report.ArchiveSha256 = (Get-FileHash -LiteralPath $ArchivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($report.ArchiveSha256 -ne $expectedArchiveHash) { throw "Archive SHA-256 differs from the original withdrawn preview. No scan or extraction was performed." }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($ArchivePath))
    try {
        $entry = $zip.GetEntry("Music.exe")
        if ($null -eq $entry) { throw "The original archive has no root Music.exe entry." }
        $stream = $entry.Open()
        $hasher = [Security.Cryptography.SHA256]::Create()
        try { $report.MusicExeSha256 = [BitConverter]::ToString($hasher.ComputeHash($stream)).Replace('-', '').ToLowerInvariant() }
        finally { $stream.Dispose(); $hasher.Dispose() }
    }
    finally { $zip.Dispose() }
    if ($report.MusicExeSha256 -ne $expectedExecutableHash) { throw "Music.exe archive entry differs from the original withdrawn executable." }
    $report.HashesVerified = $true
    $report.MusicExeHashSource = "ZIP entry (not executed)"

    $report.Stage = "DefenderReadiness"
    foreach ($command in @("Get-MpComputerStatus", "Get-MpThreatDetection", "Get-MpThreat", "Update-MpSignature")) {
        if (-not (Get-Command $command -ErrorAction SilentlyContinue)) { throw "Required Defender command unavailable: $command" }
    }
    try { $report.InitialDefenderStatus = Get-DefenderStatusSnapshot }
    catch { $report.Warnings += "Initial status before service startup unavailable: $($_.Exception.Message)" }
    $service = Get-Service -Name WinDefend -ErrorAction Stop
    if ($service.Status -ne "Running") {
        if ($service.StartType -eq "Disabled") { throw "WinDefend is disabled on this runner. Its startup configuration was not changed." }
        Start-Service -Name WinDefend -ErrorAction Stop
        $service.WaitForStatus([ServiceProcess.ServiceControllerStatus]::Running, [TimeSpan]::FromSeconds(30))
    }

    $platform = Join-Path $env:ProgramData "Microsoft/Windows Defender/Platform"
    $candidates = @()
    if (Test-Path -LiteralPath $platform) {
        $candidates += @(Get-ChildItem -LiteralPath $platform -Directory | Sort-Object @{ Expression = {
            try { [version](($_.Name -split '-')[0]) } catch { [version]'0.0' }
        } } -Descending | ForEach-Object {
            Join-Path $_.FullName "MpCmdRun.exe"
        })
    }
    $candidates += (Join-Path $env:ProgramFiles "Windows Defender/MpCmdRun.exe")
    $scanner = @($candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1)
    if ($scanner.Count -eq 0) { throw "Microsoft Defender MpCmdRun.exe is unavailable on this runner." }
    $report.ScannerPath = $scanner[0]
    $help = Invoke-DefenderCommand -Executable $scanner[0] -Arguments @("-?") -LogName "scanner-help" -TimeoutSeconds 30
    $report.DisableRemediationAdvertised = $help.Text -match '(?i)-DisableRemediation\b'
    if (-not $report.DisableRemediationAdvertised) { throw "Installed Microsoft's scanner help does not advertise -DisableRemediation. No scan was started." }

    $report.Stage = "SignatureUpdate"
    try {
        Update-MpSignature -ErrorAction Stop
        $report.SignatureUpdate = "Succeeded"
    }
    catch {
        $report.SignatureUpdate = "Failed"
        $report.Errors += "Signature update failed; any scan uses installed signatures and remains unverified: $($_.Exception.Message)"
    }
    $report.FinalDefenderStatus = Get-DefenderStatusSnapshot

    $report.Stage = "PrepareSample"
    New-Item -ItemType Directory -Path $sampleRoot -Force | Out-Null
    $archive = Join-Path $sampleRoot "Music-win-x64.zip"
    Copy-Item -LiteralPath $ArchivePath -Destination $archive
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ine $expectedArchiveHash) { throw "Copied sample no longer matches the original ZIP." }

    $report.Stage = "ScanArchive"
    $scan = Invoke-DefenderCommand -Executable $scanner[0] -Arguments @("-Scan", "-ScanType", "3", "-File", $archive, "-DisableRemediation") -LogName "archive-scan"
    $report.Scans += [pscustomobject]@{ Target = "Original release ZIP"; ExitCode = $scan.ExitCode; Log = $scan.Log; ConsoleOutput = $scan.Text }

    $report.Stage = "ExtractBundle"
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($archive)
    try {
        foreach ($entry in $zip.Entries) {
            if (-not (Test-ArchiveEntryDestination -EntryName $entry.FullName -Destination $bundle)) { throw "Unsafe ZIP entry rejected before extraction." }
        }
    }
    finally { $zip.Dispose() }
    Expand-Archive -LiteralPath $archive -DestinationPath $bundle
    $executable = Join-Path $bundle "Music.exe"
    $report.Stage = "VerifyExecutable"
    $report.MusicExeSha256 = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash.ToLowerInvariant()
    $report.MusicExeHashSource = "Extracted file (not executed)"
    if ($report.MusicExeSha256 -ne $expectedExecutableHash) { throw "Music.exe does not match the original withdrawn executable. No bundle scan was performed." }
    $report.HashesVerified = $true

    $report.Stage = "ScanBundle"
    $scan = Invoke-DefenderCommand -Executable $scanner[0] -Arguments @("-Scan", "-ScanType", "3", "-File", $bundle, "-DisableRemediation") -LogName "bundle-scan"
    $report.Scans += [pscustomobject]@{ Target = "Extracted original bundle"; ExitCode = $scan.ExitCode; Log = $scan.Log; ConsoleOutput = $scan.Text }
    $report.Stage = "Complete"
}
catch {
    $report.Errors += $_.Exception.Message
}
finally {
    if (Get-Command Get-MpThreatDetection -ErrorAction SilentlyContinue) {
        try {
            $allDetections = @(Get-MpThreatDetection -ErrorAction Stop)
            $threats = @(Get-MpThreat -ErrorAction Stop)
            foreach ($detection in $allDetections) {
                $resources = @($detection.Resources | Where-Object {
                    $resource = [string]$_
                    @($scopeDirectories | Where-Object { Test-ResourceUnderDirectory -Resource $resource -Directory $_ }).Count -gt 0
                })
                if ($resources.Count -eq 0) { continue }
                $threat = @($threats | Where-Object { $_.ThreatID -eq $detection.ThreatID } | Select-Object -First 1)
                $name = if ($threat.Count -gt 0) { [string]$threat[0].ThreatName } else { "Unknown (see ThreatID and scanner logs)" }
                $report.Detections += [pscustomobject]@{
                    ThreatName = $name
                    ThreatID = [string]$detection.ThreatID
                    InitialDetectionTime = $detection.InitialDetectionTime
                    LastThreatStatusChangeTime = $detection.LastThreatStatusChangeTime
                    ActionSuccess = $detection.ActionSuccess
                    Resources = $resources
                }
            }
            $report.DetectionQuery = "Succeeded"
        }
        catch { $report.DetectionQuery = "Failed"; $report.Errors += "Scoped detection history query failed: $($_.Exception.Message)" }
    }
    try { $report.FinalDefenderStatus = Get-DefenderStatusSnapshot }
    catch { $report.Errors += "Final Defender status unavailable: $($_.Exception.Message)" }

    if ($null -ne $report.FinalDefenderStatus) {
        try {
            if ($null -eq $report.FinalDefenderStatus.AntivirusSignatureLastUpdated) { throw "Signature timestamp is unavailable." }
            $updated = ([DateTime]$report.FinalDefenderStatus.AntivirusSignatureLastUpdated).ToUniversalTime()
            $age = [DateTime]::UtcNow - $updated
            $report.SignatureAgeHours = [Math]::Round($age.TotalHours, 2)
            if ($report.FinalDefenderStatus.DefenderSignaturesOutOfDate -eq $true -or $age.TotalHours -gt 48 -or $age.TotalHours -lt -1) {
                $report.SignatureFreshness = "StaleOrInvalid"
                $report.Errors += "Signatures are marked out of date, older than 48 hours, or have an invalid future timestamp. No fresh-signature result can be claimed."
            }
            elseif ($report.FinalDefenderStatus.DefenderSignaturesOutOfDate -eq $false) { $report.SignatureFreshness = "Fresh" }
            else { throw "Defender signature freshness status is unavailable." }
        }
        catch { $report.SignatureFreshness = "Unknown"; $report.Errors += "Signature freshness unverified: $($_.Exception.Message)" }
    }
    if ($report.Detections.Count -gt 0) { $report.Result = "DetectionReported" }
    elseif (@($report.Scans | Where-Object { $_.ExitCode -ne 0 }).Count -gt 0) { $report.Result = "ScanFailureOrDetection" }
    elseif ($report.HashesVerified -and $report.Scans.Count -eq 2 -and $report.Errors.Count -eq 0 -and
        $report.SignatureUpdate -eq "Succeeded" -and $report.SignatureFreshness -eq "Fresh" -and $report.DetectionQuery -eq "Succeeded" -and
        $null -ne $report.FinalDefenderStatus -and $report.FinalDefenderStatus.AMServiceEnabled -and
        $report.FinalDefenderStatus.AntivirusEnabled) { $report.Result = "NoDetectionReported" }

    $report.FinishedUtc = [DateTime]::UtcNow.ToString("o")
    $reportPath = Join-Path $OutputDirectory "defender-report.json"
    $report | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $reportPath -Encoding UTF8

    $summary = @(
        "## Withdrawn Music preview: Microsoft Defender diagnostic",
        "",
        "Preview v0.1.0-preview.1 is withdrawn (draft). No rebuilt binary is published.",
        "",
        "- Result: **$($report.Result)**",
        "- Last stage: $($report.Stage)",
        "- Signature update: $($report.SignatureUpdate)",
        "- Signature freshness: $($report.SignatureFreshness) (age hours: $($report.SignatureAgeHours))",
        "- ZIP SHA-256: $($report.ArchiveSha256)",
        "- Music.exe SHA-256: $($report.MusicExeSha256)",
        "- Music.exe hash source: $($report.MusicExeHashSource)",
        "- Scoped detection query: $($report.DetectionQuery)",
        "- Scoped detections: $($report.Detections.Count)"
    )
    if ($null -ne $report.FinalDefenderStatus) {
        $summary += "- Engine: $($report.FinalDefenderStatus.AMEngineVersion)"
        $summary += "- Signatures: $($report.FinalDefenderStatus.AntivirusSignatureVersion)"
        $summary += "- Signatures last updated: $($report.FinalDefenderStatus.AntivirusSignatureLastUpdated)"
        $summary += "- Antivirus enabled: $($report.FinalDefenderStatus.AntivirusEnabled)"
    }
    foreach ($detection in $report.Detections) { $summary += "- Detection: $($detection.ThreatName) (ThreatID $($detection.ThreatID))" }
    foreach ($scan in $report.Scans) {
        $summary += ""
        $summary += "### $($scan.Target) — native exit code $($scan.ExitCode)"
        $summary += '```text'
        $summary += ($scan.ConsoleOutput -split "`r?`n" | Select-Object -First 60)
        $summary += '```'
    }
    foreach ($message in $report.Warnings) { $summary += "- Diagnostic warning: $($message -replace '[\r\n]+', ' ')" }
    foreach ($message in $report.Errors) { $summary += "- Diagnostic error: $($message -replace '[\r\n]+', ' ')" }
    $summary += ""
    $summary += "No application was executed. No exclusion, Defender preference change, or remediation was requested. -DisableRemediation may omit detection history, so native scan codes and console output are retained. A no-detection result does not establish that the file is safe."
    $summary | Set-Content -LiteralPath (Join-Path $OutputDirectory "summary.md") -Encoding UTF8
    $summary | ForEach-Object { Write-Host $_ }
    if (-not [string]::IsNullOrEmpty($env:GITHUB_STEP_SUMMARY)) { $summary | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Encoding UTF8 }
}

if ($report.Result -ne "NoDetectionReported") { exit 1 }
