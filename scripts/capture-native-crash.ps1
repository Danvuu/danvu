[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidateSet('Prepare', 'Analyze')][string]$Action,
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [Parameter(Mandatory = $true)][ValidatePattern('^[0-9a-fA-F]{40}$')][string]$SourceCommit
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Get-InstalledCdb {
    $candidates = @()
    $kits = [Environment]::GetFolderPath('ProgramFilesX86')
    foreach ($version in @('10', '8.1')) { $candidates += (Join-Path $kits ("Windows Kits/$version/Debuggers/x64/cdb.exe")) }
    $command = Get-Command cdb.exe -CommandType Application -ErrorAction SilentlyContinue
    if ($null -ne $command) { $candidates += $command.Source }
    return @($candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1)
}

function Get-BoundedCrashAnalysis {
    param([string]$Text)
    # Do not export raw memory, environment blocks, dumps, or whole debugger logs.
    # Retain exception/HRESULT/resource diagnostics and bounded stack context only.
    $lines = @(); $context = 0
    foreach ($line in ($Text -split "`r?`n")) {
        if ($line -match '(?i)STOWED|Exception|HRESULT|ResultCode|ErrorText|FAILURE_|MODULE_NAME|IMAGE_NAME|PROCESS_NAME|STACK_TEXT|SYMBOL_NAME|FAULTING_|0x[0-9a-f]{8}|Microsoft\.UI\.Xaml|resources?\.pri|^\s*[0-9a-f]{8}`[0-9a-f]{8}\s') { $context = 2 }
        if ($context -gt 0) {
            $safe = $line -replace '(?i)github_pat_[a-z0-9_]+|gh[pousr]_[a-z0-9_]{20,}', '[redacted credential pattern]'
            if ($safe -match '(?i)authorization|bearer\s|access.token|refresh.token|password|secret|environment') { $safe = '[sensitive diagnostic line omitted]' }
            $lines += $safe.Substring(0, [Math]::Min(500, $safe.Length)); $context--
        }
        if ($lines.Count -ge 180) { break }
    }
    $result = $lines -join "`r`n"
    return $result.Substring(0, [Math]::Min(16000, $result.Length))
}

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT -or $env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw 'WER crash diagnostics are restricted to the disposable GitHub-hosted Windows runner; no client registry changes are allowed.'
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$publicPath = Join-Path $OutputDirectory 'crash-capture.json'
$statePath = Join-Path $env:RUNNER_TEMP 'music-wer-state.json'
$registryPath = 'HKLM:\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\Music.exe'
$info = [ordered]@{ SourceCommit = $SourceCommit; Status = 'Unverified'; CdbPath = $null; CdbVersion = $null; DumpCreated = $false; DumpBytes = $null; CdbExitCode = $null; RegistryRestored = $false; Error = $null; Note = 'Runner-local dump only. No dump, raw memory, or full debugger log is exported. Missing tools or capture do not validate the app.' }
$state = $null

try {
    if ($Action -eq 'Prepare') {
        $cdb = @(Get-InstalledCdb)
        if ($cdb.Count -eq 0) { $info.Status = 'CdbUnavailable'; return }
        $info.CdbPath = $cdb[0]
        $info.CdbVersion = (Get-Item -LiteralPath $cdb[0]).VersionInfo.FileVersion
        $signature = Get-AuthenticodeSignature -LiteralPath $cdb[0]
        if ($signature.Status -ne 'Valid' -or $null -eq $signature.SignerCertificate -or $signature.SignerCertificate.Subject -notmatch 'Microsoft') {
            $info.Status = 'DebuggerSignatureUnverified'; return
        }
        if (Test-Path -LiteralPath $registryPath) { $info.Status = 'ExistingWerConfigurationPreserved'; return }
        if (Test-Path -LiteralPath $statePath) { $info.Status = 'ExistingDiagnosticStatePreserved'; return }
        $private = Join-Path $env:RUNNER_TEMP ('MusicNativeCrash-' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $private | Out-Null
        $state = [pscustomobject]@{ SourceCommit = $SourceCommit; CdbPath = $cdb[0]; CdbVersion = $info.CdbVersion; DumpDirectory = $private; RegistryOwned = $true }
        # Write ownership before mutation, so Analyze can restore partial setup.
        $state | ConvertTo-Json | Set-Content -LiteralPath $statePath -Encoding UTF8
        New-Item -Path $registryPath -Force | Out-Null
        New-ItemProperty -LiteralPath $registryPath -Name DumpFolder -PropertyType ExpandString -Value $private | Out-Null
        New-ItemProperty -LiteralPath $registryPath -Name DumpType -PropertyType DWord -Value 2 | Out-Null
        New-ItemProperty -LiteralPath $registryPath -Name DumpCount -PropertyType DWord -Value 1 | Out-Null
        $info.Status = 'PreparedPerProcessWer'
    }
    else {
        if (-not (Test-Path -LiteralPath $statePath)) {
            $previous = if (Test-Path -LiteralPath $publicPath) { (Get-Content -LiteralPath $publicPath -Raw | ConvertFrom-Json).Status } else { 'NoPrepareEvidence' }
            $info.Status = 'CaptureNotPrepared: ' + $previous; return
        }
        $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
        if ($state.SourceCommit -ne $SourceCommit) { throw 'Crash capture source identity differs; existing state was preserved.' }
        $info.CdbPath = $state.CdbPath; $info.CdbVersion = $state.CdbVersion
        $smokePath = Join-Path $OutputDirectory 'report.json'
        if (-not (Test-Path -LiteralPath $smokePath)) { $info.Status = 'SmokeReportUnavailable'; return }
        $smoke = Get-Content -LiteralPath $smokePath -Raw | ConvertFrom-Json
        if ($smoke.SourceCommit -ne $SourceCommit) { throw 'Smoke report source identity differs from crash capture.' }
        if (-not $smoke.ApplicationExecuted) { $info.Status = 'NoApplicationExecuted'; return }
        if ($smoke.Result -eq 'Passed') { $info.Status = 'NoCrashAnalysisNeeded'; return }
        $dump = $null; $clock = [Diagnostics.Stopwatch]::StartNew()
        do {
            $found = @(Get-ChildItem -LiteralPath $state.DumpDirectory -Filter 'Music.exe*.dmp' -File | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1)
            if ($found.Count -gt 0) {
                try {
                    $probe = [IO.File]::Open($found[0].FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
                    $probe.Dispose()
                    if ($found[0].Length -gt 4096) { $dump = $found[0] }
                } catch { }
            }
            if ($null -eq $dump) { Start-Sleep -Milliseconds 500 }
        } while ($null -eq $dump -and $clock.Elapsed.TotalSeconds -lt 40)
        if ($null -eq $dump) { $info.Status = 'WerDumpUnavailable'; return }
        $info.DumpCreated = $true; $info.DumpBytes = $dump.Length
        $symbols = Join-Path $state.DumpDirectory 'symbols'
        $start = New-Object System.Diagnostics.ProcessStartInfo
        $start.FileName = $state.CdbPath; $start.UseShellExecute = $false; $start.CreateNoWindow = $true
        $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
        $start.Arguments = '-z "' + $dump.FullName + '" -y "srv*' + $symbols + '*https://msdl.microsoft.com/download/symbols" -c "!analyze -v; .exr -1; k 20; q"'
        $debugger = New-Object System.Diagnostics.Process
        $debugger.StartInfo = $start
        try {
            if (-not $debugger.Start()) { throw 'Existing SDK debugger did not start.' }
            $stdout = $debugger.StandardOutput.ReadToEndAsync(); $stderr = $debugger.StandardError.ReadToEndAsync()
            if (-not $debugger.WaitForExit(120000)) { $debugger.Kill(); $debugger.WaitForExit(); $info.Status = 'DebuggerTimedOut'; return }
            $debugger.WaitForExit()
            $info.CdbExitCode = $debugger.ExitCode
            if (-not [Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($stdout, $stderr), 10000)) { $info.Status = 'DebuggerOutputIncomplete'; return }
            $text = $stdout.GetAwaiter().GetResult() + "`r`n" + $stderr.GetAwaiter().GetResult()
            $bounded = Get-BoundedCrashAnalysis -Text $text
            if ($bounded.Length -eq 0) { $bounded = 'The debugger produced no matching bounded exception/stack diagnostics. No underlying cause is established.' }
            $bounded | Set-Content -LiteralPath (Join-Path $OutputDirectory 'crash-analysis.txt') -Encoding UTF8
            $info.Status = 'LocalAnalysisCompletedNotRootCauseProof'
        }
        finally { $debugger.Dispose() }
    }
}
catch { $info.Status = 'Unavailable'; $info.Error = $_.Exception.Message }
finally {
    if ($Action -eq 'Analyze' -and $null -ne $state -and $state.SourceCommit -eq $SourceCommit -and $state.RegistryOwned) {
        try {
            if (Test-Path -LiteralPath $registryPath) {
                $current = Get-ItemProperty -LiteralPath $registryPath
                $extra = @($current.PSObject.Properties.Name | Where-Object { $_ -notmatch '^PS' -and $_ -notin @('DumpFolder', 'DumpType', 'DumpCount') })
                if ($extra.Count -eq 0 -and $current.DumpFolder -eq $state.DumpDirectory -and $current.DumpType -eq 2 -and $current.DumpCount -eq 1) {
                    Remove-Item -LiteralPath $registryPath -Recurse
                    $info.RegistryRestored = $true
                }
            } else { $info.RegistryRestored = $true }
        } catch { $info.Error = 'Restoring the owned per-process WER key failed; runner disposal still removes this isolated machine.' }
    }
    $info | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $publicPath -Encoding UTF8
    Write-Host "Crash diagnostic status: $($info.Status). Dump files stay private in RUNNER_TEMP."
}
