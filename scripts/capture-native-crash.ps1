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

function Invoke-PrivateCdb {
    param([string]$Cdb, [string]$Dump, [string]$Symbols, [string]$Commands, [int]$TimeoutMilliseconds = 60000)
    $start = New-Object System.Diagnostics.ProcessStartInfo
    $start.FileName = $Cdb; $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    $start.Arguments = '-z "' + $Dump + '" -y "srv*' + $Symbols + '*https://msdl.microsoft.com/download/symbols" -c "' + $Commands + '"'
    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $start
    try {
        if (-not $process.Start()) { throw 'Private stowed-exception debugger did not start.' }
        $stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutMilliseconds)) { $process.Kill(); $process.WaitForExit(); throw 'Private stowed-exception inspection timed out.' }
        $process.WaitForExit()
        if ($null -eq $process.ExitCode -or $process.ExitCode -ne 0) { throw 'Private stowed-exception debugger did not complete successfully.' }
        if (-not [Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($stdout, $stderr), 10000)) { throw 'Private debugger output was incomplete.' }
        return $stdout.GetAwaiter().GetResult() + "`r`n" + $stderr.GetAwaiter().GetResult()
    }
    finally { $process.Dispose() }
}

function Get-MarkedCdbText {
    param([string]$Text, [string]$Marker)
    $pattern = '(?ms)^\s*' + [regex]::Escape($Marker + '_BEGIN') + '\s*\r?\n(.*?)^\s*' + [regex]::Escape($Marker + '_END') + '\s*$'
    $match = [regex]::Match($Text, $pattern)
    if (-not $match.Success) { throw 'Expected private debugger output marker was absent.' }
    return $match.Groups[1].Value
}

function Get-StowedArrayInformation {
    param([string]$Text)
    $codes = [regex]::Matches($Text, '(?im)^\s*ExceptionCode:\s*([0-9a-f]{8})\b')
    $first = [regex]::Matches($Text, '(?im)^\s*Parameter\[0\]:\s*(?:0x)?([0-9a-f`]+)\s*$')
    $second = [regex]::Matches($Text, '(?im)^\s*Parameter\[1\]:\s*(?:0x)?([0-9a-f`]+)\s*$')
    if ($codes.Count -eq 0 -or $codes[$codes.Count - 1].Groups[1].Value -ine 'c000027b' -or $first.Count -eq 0 -or $second.Count -eq 0) { return $null }
    $pointer = $first[$first.Count - 1].Groups[1].Value.Replace('`', '')
    $count = [Convert]::ToUInt64($second[$second.Count - 1].Groups[1].Value.Replace('`', ''), 16)
    if ($pointer -notmatch '^[0-9a-fA-F]{1,16}$' -or [Convert]::ToUInt64($pointer, 16) -eq 0 -or $count -lt 1 -or $count -gt 16) { throw 'Stowed-exception array/count outside the diagnostic bound.' }
    return [pscustomobject]@{ Pointer = $pointer; Count = [int]$count }
}

function Convert-StowedTypeFields {
    param([string]$Text)
    $code = [regex]::Match($Text, '(?im)^\s*\+0x[0-9a-f]+\s+ResultCode\s*:\s*(?:0x([0-9a-f]+)|0n(-?\d+))')
    $form = [regex]::Match($Text, '(?im)^\s*\+0x[0-9a-f]+\s+ExceptionForm\s*:\s*(?:0y([01]+)|0n(\d+)|0x([0-9a-f]+)|(\d+))')
    if (-not $code.Success -or -not $form.Success) { throw 'Symbols did not expose the stowed structure ResultCode/ExceptionForm fields.' }
    $number = if ($code.Groups[1].Success) { [Convert]::ToInt64($code.Groups[1].Value, 16) } else { [long]$code.Groups[2].Value }
    $hresult = '0x' + (($number -band 4294967295L).ToString('X8'))
    $kind = if ($form.Groups[1].Success) { [Convert]::ToInt32($form.Groups[1].Value, 2) } elseif ($form.Groups[2].Success) { [int]$form.Groups[2].Value } elseif ($form.Groups[3].Success) { [Convert]::ToInt32($form.Groups[3].Value, 16) } else { [int]$form.Groups[4].Value }
    $pointer = $null
    if ($kind -eq 2) {
        $errorText = [regex]::Match($Text, '(?im)^\s*\+0x[0-9a-f]+\s+ErrorText\s*:\s*(?:0x)?([0-9a-f]{8}`?[0-9a-f]{8})\b')
        if ($errorText.Success) { $pointer = $errorText.Groups[1].Value.Replace('`', '') }
    }
    return [pscustomobject]@{ HResult = $hresult; ExceptionForm = $kind; ErrorTextPointer = $pointer }
}

function Convert-BoundedDebuggerUtf16 {
    param([string]$Text, [string]$Pointer)
    $base = [Convert]::ToUInt64($Pointer, 16)
    $words = @{}
    foreach ($line in ($Text -split "`r?`n")) {
        $row = [regex]::Match($line, '^\s*([0-9a-fA-F]{8}`?[0-9a-fA-F]{8})\s+(.+)$')
        if (-not $row.Success) { continue }
        $address = [Convert]::ToUInt64($row.Groups[1].Value.Replace('`', ''), 16)
        foreach ($token in ($row.Groups[2].Value -split '[\s-]+')) {
            if ($token -notmatch '^[0-9a-fA-F]{4}$') { break }
            if ($address -ge $base -and $address -lt ($base + 512) -and ($address - $base) % 2 -eq 0) { $words[[int]($address - $base)] = [Convert]::ToUInt16($token, 16) }
            $address += 2
        }
    }
    $builder = New-Object System.Text.StringBuilder
    $terminated = $false
    for ($offset = 0; $offset -lt 512; $offset += 2) {
        if (-not $words.ContainsKey($offset)) { break }
        if ($words[$offset] -eq 0) { $terminated = $true; break }
        [void]$builder.Append([char]$words[$offset])
    }
    $message = $builder.ToString() -replace '(?i)github_pat_[a-z0-9_]+|gh[pousr]_[a-z0-9_]{20,}', '[redacted credential pattern]'
    if ($message -match '(?i)authorization|bearer\s|access.token|refresh.token|password|secret|environment') { $message = '[sensitive diagnostic text omitted]' }
    return [pscustomobject]@{ Text = $message; Terminated = $terminated; CharactersRead = $builder.Length }
}

function Get-StowedExceptionDetails {
    param([string]$InitialText, [string]$Cdb, [string]$Dump, [string]$Symbols)
    $array = Get-StowedArrayInformation $InitialText
    if ($null -eq $array) { return @() }
    $arrayText = Invoke-PrivateCdb $Cdb $Dump $Symbols ('.echo MUSIC_ARRAY_BEGIN; dps 0x' + $array.Pointer + ' L0x' + $array.Count.ToString('X') + '; .echo MUSIC_ARRAY_END; q')
    $block = Get-MarkedCdbText $arrayText 'MUSIC_ARRAY'
    $base = [Convert]::ToUInt64($array.Pointer, 16)
    $records = @()
    foreach ($match in [regex]::Matches($block, '(?im)^\s*([0-9a-f]{8}`?[0-9a-f]{8})\s+([0-9a-f]{8}`?[0-9a-f]{8})\b')) {
        $slot = [Convert]::ToUInt64($match.Groups[1].Value.Replace('`', ''), 16)
        $pointer = $match.Groups[2].Value.Replace('`', '')
        if ($slot -ge $base -and $slot -lt ($base + $array.Count * 8) -and ($slot - $base) % 8 -eq 0 -and [Convert]::ToUInt64($pointer, 16) -ne 0) { $records += [pscustomobject]@{ Index = [int](($slot - $base) / 8); Pointer = $pointer } }
    }
    if ($records.Count -eq 0) { throw 'No readable stowed-exception pointers were obtained.' }
    $commands = '.reload /f combase.dll; '
    foreach ($record in $records) { $commands += '.echo MUSIC_TYPE_' + $record.Index + '_BEGIN; dt 0x' + $record.Pointer + ' combase!STOWED_EXCEPTION_INFORMATION_V2 -r; .echo MUSIC_TYPE_' + $record.Index + '_END; ' }
    $types = Invoke-PrivateCdb $Cdb $Dump $Symbols ($commands + 'q')
    $public = @(); $textCommands = ''
    foreach ($record in $records) {
        $fields = Convert-StowedTypeFields (Get-MarkedCdbText $types ('MUSIC_TYPE_' + $record.Index))
        $record | Add-Member -NotePropertyName Fields -NotePropertyValue $fields
        if ($fields.ExceptionForm -eq 2 -and $null -ne $fields.ErrorTextPointer -and [Convert]::ToUInt64($fields.ErrorTextPointer, 16) -ne 0) {
            $textCommands += '.echo MUSIC_TEXT_' + $record.Index + '_BEGIN; dw 0x' + $fields.ErrorTextPointer + ' L0x100; .echo MUSIC_TEXT_' + $record.Index + '_END; '
        }
    }
    $strings = if ($textCommands.Length -gt 0) { Invoke-PrivateCdb $Cdb $Dump $Symbols ($textCommands + 'q') } else { '' }
    foreach ($record in $records) {
        $fields = $record.Fields
        $item = [ordered]@{ Index = $record.Index; Type = 'combase!STOWED_EXCEPTION_INFORMATION_V2'; HResult = $fields.HResult; ExceptionForm = $fields.ExceptionForm; ErrorText = $null; TextStatus = 'BinaryFormNoErrorTextRead' }
        if ($fields.ExceptionForm -eq 2) {
            $item.TextStatus = 'NullOrUnreadableErrorText'
            if ($null -ne $fields.ErrorTextPointer -and [Convert]::ToUInt64($fields.ErrorTextPointer, 16) -ne 0) {
                $decoded = Convert-BoundedDebuggerUtf16 (Get-MarkedCdbText $strings ('MUSIC_TEXT_' + $record.Index)) $fields.ErrorTextPointer
                $item.ErrorText = $decoded.Text
                $item.TextStatus = if ($decoded.Terminated) { 'NullTerminatedWithin256Characters' } else { 'TruncatedOrUnreadableWithin256Characters' }
            }
        }
        $public += [pscustomobject]$item
    }
    return $public
}

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT -or $env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw 'WER crash diagnostics are restricted to the disposable GitHub-hosted Windows runner; no client registry changes are allowed.'
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$publicPath = Join-Path $OutputDirectory 'crash-capture.json'
$statePath = Join-Path $env:RUNNER_TEMP 'music-wer-state.json'
$registryPath = 'HKLM:\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\Music.exe'
$info = [ordered]@{ SourceCommit = $SourceCommit; Status = 'Unverified'; CdbPath = $null; CdbVersion = $null; DumpCreated = $false; DumpBytes = $null; CdbExitCode = $null; StowedInspectionStatus = 'NotAttempted'; StowedExceptions = @(); RegistryRestored = $false; Error = $null; Note = 'Runner-local dump only. No dump, raw memory, or full debugger log is exported. Missing tools or capture do not validate the app.' }
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
            try {
                $info.StowedExceptions = @(Get-StowedExceptionDetails -InitialText $text -Cdb $state.CdbPath -Dump $dump.FullName -Symbols $symbols)
                $info.StowedInspectionStatus = if ($info.StowedExceptions.Count -gt 0) { 'SymbolFieldsRead' } else { 'NoStowedArrayFound' }
                $specific = @()
                foreach ($item in $info.StowedExceptions) {
                    $specific += "STOWED RECORD $($item.Index): $($item.Type)"
                    $specific += "HRESULT: $($item.HResult); ExceptionForm: $($item.ExceptionForm)"
                    $specific += "ErrorText: $($item.ErrorText)"
                    $specific += "Text status: $($item.TextStatus)"
                }
                if ($specific.Count -gt 0) { $bounded = ($specific -join "`r`n") + "`r`n`r`n" + $bounded }
            }
            catch { $info.StowedInspectionStatus = 'Unavailable'; $info.Error = 'Stowed detail inspection unavailable: ' + $_.Exception.Message }
            $bounded = $bounded.Substring(0, [Math]::Min(16000, $bounded.Length))
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
