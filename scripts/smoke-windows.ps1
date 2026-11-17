[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ApplicationDirectory,
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [Parameter(Mandatory = $true)][ValidatePattern('^[0-9a-fA-F]{40}$')][string]$SourceCommit
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Test-ScopedResource {
    param([string]$Resource, [string]$Directory)
    $prefix = $Directory.Replace('/', '\').TrimEnd('\') + '\'
    return $Resource.Replace('/', '\').IndexOf($prefix, [StringComparison]::OrdinalIgnoreCase) -ge 0
}

function Get-BundleHashes {
    param([string]$Directory)
    return @(Get-ChildItem -LiteralPath $Directory -File -Recurse | Sort-Object FullName | ForEach-Object {
        [pscustomobject]@{ Path = $_.FullName.Substring($Directory.Length).TrimStart('\', '/'); Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    })
}

function Invoke-CapturedNativeProcess {
    param([string]$Executable, [string]$Arguments, [int]$TimeoutMilliseconds = 300000)
    $start = New-Object System.Diagnostics.ProcessStartInfo
    $start.FileName = $Executable; $start.Arguments = $Arguments
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $start
    try {
        if (-not $process.Start()) { throw 'Native scanner process did not start.' }
        # Drain both pipes concurrently, avoiding a deadlock when either fills.
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $timedOut = -not $process.WaitForExit($TimeoutMilliseconds)
        if ($timedOut) { $process.Kill() }
        $process.WaitForExit()
        $nativeExitCode = $process.ExitCode
        if ($null -eq $nativeExitCode) { throw 'The owned native process returned no exit status; scan is unverified.' }
        $complete = [Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($stdout, $stderr), 30000)
        return [pscustomobject]@{
            ExitCode = $nativeExitCode; TimedOut = $timedOut; OutputComplete = $complete
            Stdout = $(if ($stdout.IsCompleted) { $stdout.GetAwaiter().GetResult() } else { '[stdout capture incomplete]' })
            Stderr = $(if ($stderr.IsCompleted) { $stderr.GetAwaiter().GetResult() } else { '[stderr capture incomplete]' })
        }
    }
    finally { $process.Dispose() }
}

function Get-ScopedDetections {
    $detections = @(Get-MpThreatDetection -ErrorAction Stop | Where-Object {
        @($_.Resources | Where-Object { Test-ScopedResource -Resource ([string]$_) -Directory $ApplicationDirectory }).Count -gt 0
    })
    if ($detections.Count -eq 0) { return @() }
    $threats = @(Get-MpThreat -ErrorAction Stop)
    return @($detections | ForEach-Object {
        $detection = $_
        $name = @($threats | Where-Object { $_.ThreatID -eq $detection.ThreatID } | Select-Object -First 1)
        [pscustomobject]@{
            ThreatID = [string]$detection.ThreatID
            ThreatName = $(if ($name.Count -gt 0) { [string]$name[0].ThreatName } else { 'Unknown; inspect native scan log' })
            ActionSuccess = $detection.ActionSuccess
            Resources = @($detection.Resources | Where-Object { Test-ScopedResource -Resource ([string]$_) -Directory $ApplicationDirectory })
        }
    })
}

function Add-Check {
    param([string]$Name, [string]$Detail)
    $report.Checks += [pscustomobject]@{ Name = $Name; Passed = $true; Detail = $Detail; Utc = [DateTime]::UtcNow.ToString('o') }
}

function Find-Control {
    param([string]$Id, [string]$Name = '')
    $condition = New-Object -TypeName System.Windows.Automation.PropertyCondition -ArgumentList @([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $Id)
    $element = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
    # Stable IDs identify the control. A display-name fallback can select page
    # text such as the Home heading when the responsive sidebar is collapsed.
    # Keep Name as a diagnostic hint only; never dispatch input to a namesake.
    return $element
}

function Wait-Control {
    param([string]$Id, [string]$Name = '', [int]$TimeoutSeconds = 20)
    $clock = [Diagnostics.Stopwatch]::StartNew()
    while ($clock.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
        if ($application.HasExited) { throw "Application exited while waiting for $Id (exit code $($application.ExitCode))." }
        $element = Find-Control -Id $Id -Name $Name
        if ($null -ne $element -and -not $element.Current.IsOffscreen -and $element.Current.IsEnabled) { return $element }
        Start-Sleep -Milliseconds 200
    }
    throw "Visible accessible control did not become ready: $Id. Inspect UIA tree and startup/event logs."
}

function Test-PointInsideBounds {
    param($Point, $Bounds)
    return $Point.X -ge $Bounds.Left -and $Point.X -lt $Bounds.Right -and $Point.Y -ge $Bounds.Top -and $Point.Y -lt $Bounds.Bottom
}

function Invoke-Control {
    param($Element)
    $current = $Element.Current
    $interaction = [ordered]@{ Id = $current.AutomationId; Name = $current.Name; SupportedPatterns = @(); PatternType = $null; Method = 'None'; Status = 'Pending'; PatternError = $null; FocusError = $null; KeyboardFocusVerified = $false; ForegroundVerified = $false; ClickPoint = $null }
    try {
        if ($current.IsOffscreen -or -not $current.IsEnabled) { throw 'Interaction requires a visible, enabled control.' }
        $pattern = $null
        try {
            $interaction.SupportedPatterns = @($Element.GetSupportedPatterns() | ForEach-Object { $_.ProgrammaticName })
            $pattern = $Element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
            if ($null -ne $pattern) {
                $interaction.PatternType = $pattern.GetType().FullName
                if ($pattern -isnot [System.Windows.Automation.InvokePattern]) { throw 'Provider did not return an InvokePattern instance.' }
            }
        } catch { $interaction.PatternError = $_.Exception.Message; $pattern = $null }
        if ($null -ne $pattern) {
            $interaction.Method = 'InvokePattern.GetCurrentPattern'
            $pattern.Invoke()
        }
        else {
            if ($current.ControlType -ne [System.Windows.Automation.ControlType]::Button) { throw 'Non-button control has no supported invocation pattern.' }
            [MusicDesktopProbe]::SetForegroundWindow($application.MainWindowHandle) | Out-Null
            $focused = $false
            try {
                $Element.SetFocus()
                $focusClock = [Diagnostics.Stopwatch]::StartNew()
                while (-not $Element.Current.HasKeyboardFocus -and $focusClock.Elapsed.TotalSeconds -lt 2) { Start-Sleep -Milliseconds 50 }
                $focused = $Element.Current.HasKeyboardFocus
            } catch { $interaction.FocusError = $_.Exception.Message }
            if ([MusicDesktopProbe]::GetForegroundWindow() -ne $application.MainWindowHandle) { throw 'Owned Music window is not foreground; no keyboard or mouse input was sent.' }
            $interaction.ForegroundVerified = $true
            if ($focused) {
                $interaction.Method = 'Keyboard.EnterWithVerifiedFocus'
                $interaction.KeyboardFocusVerified = $true
                [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
            }
            else {
                $point = $Element.GetClickablePoint()
                if (-not (Test-PointInsideBounds $point $Element.Current.BoundingRectangle) -or -not (Test-PointInsideBounds $point $window.Current.BoundingRectangle)) { throw 'Clickable point is outside the control or verified Music window; no mouse input was sent.' }
                if ([MusicDesktopProbe]::GetForegroundWindow() -ne $application.MainWindowHandle) { throw 'Music lost foreground; no mouse input was sent.' }
                $interaction.Method = 'PhysicalClick.SendInputWithinOwnedWindow'
                $interaction.ClickPoint = [pscustomobject]@{ X = $point.X; Y = $point.Y }
                [MusicDesktopProbe]::ClickAt([int]$point.X, [int]$point.Y)
            }
        }
        # Dispatch is not a pass: callers must observe navigation/playback/seek.
        $interaction.Status = 'DispatchedAwaitingSemanticAssertion'
    }
    catch { $interaction.Status = 'Failed'; throw }
    finally { $report.Interactions += [pscustomobject]$interaction }
}

function Get-Position {
    $element = Find-Control -Id 'Player.Position' -Name 'Playback position'
    if ($null -eq $element) { throw 'Playback position slider is absent.' }
    $provider = [ordered]@{ Id = $element.Current.AutomationId; SupportedPatterns = @(); PatternType = $null; Method = 'RangeValuePattern.GetCurrentPattern'; Error = $null }
    try {
        $provider.SupportedPatterns = @($element.GetSupportedPatterns() | ForEach-Object { $_.ProgrammaticName })
        $pattern = $element.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern)
        if ($null -eq $pattern -or $pattern -isnot [System.Windows.Automation.RangeValuePattern]) { throw 'Playback position did not expose a RangeValuePattern instance.' }
        $provider.PatternType = $pattern.GetType().FullName
        $value = [double]$pattern.Current.Value
        if ([double]::IsNaN($value) -or [double]::IsInfinity($value)) { throw 'Playback provider returned an invalid numeric position.' }
        return $value
    }
    catch { $provider.Error = $_.Exception.Message; throw }
    finally { $report.PositionProvider = [pscustomobject]$provider }
}

function Capture-Window {
    param([string]$Name)
    $bitmap = $null; $graphics = $null
    try {
        $rectangle = $window.Current.BoundingRectangle
        $width = [int]$rectangle.Width; $height = [int]$rectangle.Height
        if ($width -lt 200 -or $height -lt 200) { throw 'Window has no usable on-screen bounds.' }
        $bitmap = New-Object System.Drawing.Bitmap -ArgumentList @($width, $height)
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        $graphics.CopyFromScreen([int]$rectangle.Left, [int]$rectangle.Top, 0, 0, $bitmap.Size, [Drawing.CopyPixelOperation]::SourceCopy)
        $path = Join-Path $OutputDirectory ($Name + '.png')
        $bitmap.Save($path, [Drawing.Imaging.ImageFormat]::Png)
        $colors = @{}
        for ($x = 0; $x -lt $width; $x += [Math]::Max(1, [int]($width / 20))) {
            for ($y = 0; $y -lt $height; $y += [Math]::Max(1, [int]($height / 20))) { $colors[$bitmap.GetPixel($x, $y).ToArgb()] = $true }
        }
        if ($colors.Count -lt 2) { throw 'Captured desktop pixels are uniform; screenshot cannot validate rendering.' }
        $report.Screenshots += [pscustomobject]@{ Name = $Name; Status = 'Captured'; File = $Name + '.png' }
    }
    catch { $report.Screenshots += [pscustomobject]@{ Name = $Name; Status = 'Unavailable'; Error = $_.Exception.Message } }
    finally {
        if ($null -ne $graphics) { $graphics.Dispose() }
        if ($null -ne $bitmap) { $bitmap.Dispose() }
    }
}

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'Native smoke checks require Windows; no application was executed.' }
$ApplicationDirectory = [IO.Path]::GetFullPath($ApplicationDirectory).TrimEnd('\', '/')
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$started = Get-Date
$application = $null; $window = $null
$report = [ordered]@{
    SourceCommit = $SourceCommit; StartedUtc = $started.ToUniversalTime().ToString('o'); FinishedUtc = $null
    Result = 'Unverified'; FailureKind = $null; Stage = 'ValidateCandidate'; ApplicationExecuted = $false
    Candidate = @(); Defender = $null; DefenderScanExitCode = $null; DefenderScanOutcome = 'NotStarted'; Detections = @(); Desktop = $null
    ProtectionEnableAttempted = $false; ProtectionEnableResult = 'NotNeeded'
    ProcessId = $null; ProcessExitCode = $null; ForcedTermination = $false; Checks = @(); Interactions = @(); PositionProvider = $null; Screenshots = @(); Errors = @(); ApplicationEvents = @()
    Scope = 'Current source build only. Silent demo UI flow; no Spotify login, quarantined release execution, AV bypass, binary upload, or performance claim.'
}

try {
    $executable = Join-Path $ApplicationDirectory 'Music.exe'
    if (-not (Test-Path -LiteralPath $executable) -or -not (Test-Path -LiteralPath (Join-Path $ApplicationDirectory 'Music.dll'))) { throw 'Current build does not contain both Music.exe and Music.dll.' }
    $state = Join-Path $env:LOCALAPPDATA 'Music'
    if (Test-Path -LiteralPath $state) { throw 'Fresh default profile prerequisite failed: Music app data already exists. No existing files were deleted or changed.' }
    $baseline = @(Get-BundleHashes -Directory $ApplicationDirectory)
    $baseline | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $OutputDirectory 'bundle-sha256.json') -Encoding UTF8
    $report.Candidate = @($baseline | Where-Object { $_.Path -in @('Music.exe', 'Music.dll') })

    $report.Stage = 'DefenderReadiness'
    foreach ($command in @('Get-MpComputerStatus', 'Update-MpSignature', 'Get-MpThreatDetection', 'Get-MpThreat')) {
        if (-not (Get-Command $command -ErrorAction SilentlyContinue)) { throw "Required Microsoft Defender command unavailable: $command" }
    }
    $service = Get-Service WinDefend -ErrorAction Stop
    if ($service.Status -ne 'Running') {
        if ($service.StartType -eq 'Disabled') { throw 'Defender service is disabled on the runner; no startup configuration was changed.' }
        Start-Service WinDefend -ErrorAction Stop
        $service.WaitForStatus([ServiceProcess.ServiceControllerStatus]::Running, [TimeSpan]::FromSeconds(30))
    }
    Update-MpSignature -ErrorAction Stop
    $status = Get-MpComputerStatus -ErrorAction Stop
    if (-not $status.RealTimeProtectionEnabled) {
        $report.ProtectionEnableAttempted = $true
        if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
            $report.ProtectionEnableResult = 'RefusedOutsideHostedCI'
            throw 'Real-time protection is off. Automatic protective preference changes are restricted to the isolated GitHub-hosted runner.'
        }
        if (-not (Get-Command Set-MpPreference -ErrorAction SilentlyContinue)) {
            $report.ProtectionEnableResult = 'SupportedCommandUnavailable'
            throw 'Supported Defender preference command is unavailable. Real-time protection cannot be verified; application will not run.'
        }
        $report.ProtectionEnableResult = 'Requested'
        Set-MpPreference -DisableRealtimeMonitoring $false -ErrorAction Stop
        $protectionClock = [Diagnostics.Stopwatch]::StartNew()
        do {
            Start-Sleep -Milliseconds 500
            $status = Get-MpComputerStatus -ErrorAction Stop
        } while (-not $status.RealTimeProtectionEnabled -and $protectionClock.Elapsed.TotalSeconds -lt 20)
        if (-not $status.RealTimeProtectionEnabled) {
            $report.ProtectionEnableResult = 'NotEffective'
            throw 'Enabling real-time protection was refused or did not become effective. No policy, registry, tamper setting, or exclusion was changed; application will not run.'
        }
        $report.ProtectionEnableResult = 'EnabledAndVerified'
        Add-Check 'Protection strengthened' 'Enabled real-time protection with the supported Defender preference command on the isolated hosted runner, then verified it effective. No protection reduction was requested.'
    }
    $report.Defender = $status | Select-Object AMEngineVersion, AMProductVersion, AMServiceEnabled, AntivirusEnabled, RealTimeProtectionEnabled, AntivirusSignatureVersion, AntivirusSignatureLastUpdated, DefenderSignaturesOutOfDate
    $age = ([DateTime]::UtcNow - ([DateTime]$status.AntivirusSignatureLastUpdated).ToUniversalTime()).TotalHours
    if (-not $status.AMServiceEnabled -or -not $status.AntivirusEnabled -or -not $status.RealTimeProtectionEnabled -or
        $status.DefenderSignaturesOutOfDate -ne $false -or $age -gt 48 -or $age -lt -1) { throw 'Fresh, enabled Microsoft Defender protection could not be verified; application will not be launched.' }
    $platform = Join-Path $env:ProgramData 'Microsoft/Windows Defender/Platform'
    $candidates = @()
    if (Test-Path $platform) {
        $candidates += @(Get-ChildItem $platform -Directory | Sort-Object @{Expression = { try { [version](($_.Name -split '-')[0]) } catch { [version]'0.0' } }} -Descending | ForEach-Object { Join-Path $_.FullName 'MpCmdRun.exe' })
    }
    $candidates += (Join-Path $env:ProgramFiles 'Windows Defender/MpCmdRun.exe')
    $scanner = @($candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1)
    if ($scanner.Count -eq 0) { throw 'Microsoft Defender command-line scanner is unavailable.' }
    $report.Stage = 'DefenderScan'
    $report.DefenderScanOutcome = 'Started'
    $scan = Invoke-CapturedNativeProcess -Executable $scanner[0] -Arguments ('-Scan -ScanType 3 -File "' + $ApplicationDirectory + '"')
    $report.DefenderScanExitCode = $scan.ExitCode
    $scan.Stdout | Set-Content (Join-Path $OutputDirectory 'defender.stdout.log') -Encoding UTF8
    $scan.Stderr | Set-Content (Join-Path $OutputDirectory 'defender.stderr.log') -Encoding UTF8
    if ($scan.TimedOut) { $report.DefenderScanOutcome = 'TimedOut'; throw 'Candidate Defender scan timed out; no app launch attempted.' }
    if (-not $scan.OutputComplete) { $report.DefenderScanOutcome = 'OutputCaptureIncomplete'; throw 'Native scan console capture did not complete; no app launch attempted.' }
    $report.Detections = @(Get-ScopedDetections)
    if ($report.Detections.Count -gt 0) { $report.DefenderScanOutcome = 'DetectionReported'; throw 'Defender reported a scoped detection; no application was launched and no remediation was reversed.' }
    if ($scan.ExitCode -ne 0) { $report.DefenderScanOutcome = 'NativeScanFailureOrDetection'; throw "Native Defender scan returned exit $($scan.ExitCode); inspect captured logs. No application was launched." }
    $report.DefenderScanOutcome = 'ExitZeroNoScopedDetection'
    $after = @(Get-BundleHashes -Directory $ApplicationDirectory)
    if ((ConvertTo-Json -InputObject $baseline -Depth 4 -Compress) -cne (ConvertTo-Json -InputObject $after -Depth 4 -Compress)) { throw 'Bundle files changed or were removed during the scan; no application was launched or restored.' }
    Add-Check 'Candidate antivirus gate' 'Signature refresh succeeded, real-time protection enabled, native scan exit 0, no scoped detections, and bundle hashes unchanged. This does not clear the original user alert.'

    $report.Stage = 'InteractiveDesktop'
    Add-Type -AssemblyName WindowsBase, UIAutomationTypes, UIAutomationClient, System.Windows.Forms, System.Drawing
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
public static class MusicDesktopProbe {
    [DllImport("user32.dll", SetLastError=true)] static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll", SetLastError=true, CharSet=CharSet.Unicode)] static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder value, int length, out int needed);
    [DllImport("user32.dll")] static extern bool CloseDesktop(IntPtr handle);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll", SetLastError=true)] static extern bool SetCursorPos(int x, int y);
    [StructLayout(LayoutKind.Sequential)] struct MouseInput { public int X, Y; public uint Data, Flags, Time; public IntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] struct Input { public uint Type; public MouseInput Mouse; }
    [DllImport("user32.dll", SetLastError=true)] static extern uint SendInput(uint count, Input[] inputs, int size);
    public static void ClickAt(int x, int y) {
        if (!SetCursorPos(x, y)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var inputs = new Input[] {
            new Input { Type = 0, Mouse = new MouseInput { X = 0, Y = 0, Data = 0, Flags = 2, Time = 0, Extra = IntPtr.Zero } },
            new Input { Type = 0, Mouse = new MouseInput { X = 0, Y = 0, Data = 0, Flags = 4, Time = 0, Extra = IntPtr.Zero } }
        };
        if (SendInput(2, inputs, Marshal.SizeOf(typeof(Input))) != 2) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    public static string InputDesktop() {
        var handle = OpenInputDesktop(0, false, 1);
        if (handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try {
            var name = new StringBuilder(256); int needed;
            if (!GetUserObjectInformation(handle, 2, name, name.Capacity * 2, out needed)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return name.ToString();
        } finally { CloseDesktop(handle); }
    }
}
'@
    $session = [Diagnostics.Process]::GetCurrentProcess().SessionId
    $desktop = [MusicDesktopProbe]::InputDesktop()
    $report.Desktop = [pscustomobject]@{ UserInteractive = [Environment]::UserInteractive; SessionId = $session; InputDesktop = $desktop }
    if (-not [Environment]::UserInteractive -or $session -eq 0 -or $desktop -ne 'Default') { throw 'Hosted runner has no verified interactive Default desktop. Native UI checks are unrun.' }
    Add-Check 'Interactive desktop' "Session $session; accessible Default input desktop."

    $report.Stage = 'Launch'
    $start = New-Object System.Diagnostics.ProcessStartInfo
    $start.FileName = $executable; $start.WorkingDirectory = $ApplicationDirectory; $start.UseShellExecute = $false
    foreach ($name in @('GH_TOKEN', 'GITHUB_TOKEN', 'ACTIONS_RUNTIME_TOKEN', 'ACTIONS_ID_TOKEN_REQUEST_TOKEN', 'SYSTEM_ACCESSTOKEN')) { $start.EnvironmentVariables.Remove($name) }
    $application = [Diagnostics.Process]::Start($start)
    $report.ApplicationExecuted = $true; $report.ProcessId = $application.Id
    $report.Stage = 'WaitWindow'
    $clock = [Diagnostics.Stopwatch]::StartNew()
    while ($clock.Elapsed.TotalSeconds -lt 30) {
        if ($application.HasExited) { throw "Application exited before its window was ready (exit $($application.ExitCode))." }
        $application.Refresh()
        if ($application.MainWindowHandle -ne [IntPtr]::Zero) { break }
        Start-Sleep -Milliseconds 200
    }
    if ($application.MainWindowHandle -eq [IntPtr]::Zero) { throw 'No native main window was observed within 30 seconds.' }
    [MusicDesktopProbe]::ShowWindow($application.MainWindowHandle, 9) | Out-Null
    [MusicDesktopProbe]::SetForegroundWindow($application.MainWindowHandle) | Out-Null
    $window = [System.Windows.Automation.AutomationElement]::FromHandle($application.MainWindowHandle)
    if ($null -eq $window -or $window.Current.Name -ne 'Music') { throw 'UI Automation did not expose the expected Music window.' }

    $report.Stage = 'Home'
    $homeControl = Find-Control 'Nav.Home' 'Home'
    if ($null -eq $homeControl -or $homeControl.Current.IsOffscreen) {
        Invoke-Control (Wait-Control 'Nav.Toggle' 'Toggle library sidebar')
    }
    Invoke-Control (Wait-Control 'Nav.Home' 'Home')
    $explore = Wait-Control 'Home.ExploreAlbum' 'Explore album'
    Add-Check 'Home ready' 'Real native window exposes Home navigation and Explore album.'
    Capture-Window 'home'

    $report.Stage = 'Album'
    Invoke-Control $explore
    $play = Wait-Control 'Collection.Play' 'Play'
    $title = Wait-Control 'Collection.Title' 'Afterglow'
    if ($title.Current.Name -ne 'Afterglow') { throw 'Demo album route did not display Afterglow.' }
    Add-Check 'Album navigation' 'Explore album opened the Afterglow detail view and collection Play action.'
    Capture-Window 'album'
    Invoke-Control $play
    $track = Wait-Control 'Player.TrackTitle' 'Afterglow'
    $clock.Restart()
    while ($track.Current.Name -ne 'Afterglow' -and $clock.Elapsed.TotalSeconds -lt 10) { Start-Sleep -Milliseconds 200; $track = Wait-Control 'Player.TrackTitle' }
    if ($track.Current.Name -ne 'Afterglow') { throw 'Play did not update the current track title.' }
    $initial = Get-Position; $clock.Restart()
    while ((Get-Position) -lt ($initial + 600) -and $clock.Elapsed.TotalSeconds -lt 10) { Start-Sleep -Milliseconds 200 }
    $advanced = Get-Position
    if ($advanced -lt ($initial + 600)) { throw 'Silent demo playback timeline did not advance.' }
    Add-Check 'Demo playback' "Current track Afterglow; timeline advanced from $initial to $advanced milliseconds. Demo mode is silent."
    Invoke-Control (Wait-Control 'Player.PlayPause' 'Play or pause (Space)')
    Start-Sleep -Milliseconds 700
    $paused = Get-Position; Start-Sleep -Milliseconds 900
    if ([Math]::Abs((Get-Position) - $paused) -gt 350) { throw 'Pause did not stop the demo timeline.' }
    Add-Check 'Pause' 'Native playback control stopped the timeline.'

    $report.Stage = 'Lyrics'
    Invoke-Control (Wait-Control 'Player.Lyrics' 'Lyrics (Ctrl+L)')
    $lyrics = Wait-Control 'Lyrics.Scroll' 'Synchronized lyrics. Scroll to browse; choose Jump to current to resume following.'
    $clock.Restart(); $source = $null
    do {
        if ($application.HasExited) { throw 'Application exited while its lyrics document was loading.' }
        $source = Find-Control 'Lyrics.Source'
        if ($null -ne $source -and -not $source.Current.IsOffscreen -and $source.Current.IsEnabled -and $source.Current.Name -like 'Original demo lyrics*simulated playback') { break }
        Start-Sleep -Milliseconds 200
    } while ($clock.Elapsed.TotalSeconds -lt 20)
    if ($null -eq $source -or $source.Current.Name -notlike 'Original demo lyrics*simulated playback') { throw 'The lyrics view did not load its original demo document within 20 seconds.' }
    $line = Wait-Control 'Lyrics.Line.0'
    if ($line.Current.Name -notmatch '^Seek to (\d+):(\d{2}):') { throw 'First lyric does not expose its seek timestamp.' }
    $seekTarget = ([int]$Matches[1] * 60 + [int]$Matches[2]) * 1000
    Invoke-Control $line
    $clock.Restart()
    while ([Math]::Abs((Get-Position) - $seekTarget) -gt 750 -and $clock.Elapsed.TotalSeconds -lt 10) { Start-Sleep -Milliseconds 200 }
    if ([Math]::Abs((Get-Position) - $seekTarget) -gt 750) { throw 'Clicking the lyric did not seek the real demo player to its timestamp.' }
    Add-Check 'Lyrics and click-to-seek' "Original timed lyrics loaded; first line sought to $seekTarget milliseconds."
    Capture-Window 'lyrics'
    Invoke-Control (Wait-Control 'Lyrics.Close' 'Close lyrics')
    Start-Sleep -Milliseconds 300
    $closed = Find-Control 'Lyrics.Scroll'
    if ($null -ne $closed -and -not $closed.Current.IsOffscreen) { throw 'Lyrics close action left the lyrics view visible.' }
    Add-Check 'Close lyrics' 'The native close action hid the lyrics pane.'

    $report.Stage = 'Queue'
    Invoke-Control (Wait-Control 'Player.Queue')
    $queueTitle = Wait-Control 'Queue.Title'
    if ($queueTitle.Current.Name -ne 'Queue') { throw 'Queue control did not expose its real heading.' }
    Capture-Window 'queue'
    Invoke-Control (Wait-Control 'Queue.Close')
    Start-Sleep -Milliseconds 250
    $queueTitle = Find-Control 'Queue.Title'
    if ($null -ne $queueTitle -and -not $queueTitle.Current.IsOffscreen) { throw 'Queue close action left the queue visible.' }
    Add-Check 'Queue open and close' 'Real queue panel rendered and closed through its native controls.'

    foreach ($dialogProbe in @(
        @{ Open = 'Player.Devices'; Dialog = 'Devices.Dialog'; Title = 'Playback devices' },
        @{ Open = 'Header.Account'; Dialog = 'Account.Dialog'; Title = 'Spotify account settings' },
        @{ Open = 'Header.Jam'; Dialog = 'Jam.Dialog'; Title = 'Official Spotify Jam handoff' }
    )) {
        $report.Stage = $dialogProbe.Title
        Invoke-Control (Wait-Control $dialogProbe.Open)
        $dialogControl = Wait-Control $dialogProbe.Dialog
        Invoke-Control (Wait-Control ($dialogProbe.Dialog + '.Close'))
        Start-Sleep -Milliseconds 250
        $dialogControl = Find-Control $dialogProbe.Dialog
        if ($null -ne $dialogControl -and -not $dialogControl.Current.IsOffscreen) { throw ('Native dialog remained visible after Close: ' + $dialogProbe.Title) }
        Add-Check $dialogProbe.Title 'Native dialog opened and dismissed. No account authorization, external application, or Spotify session was invoked.'
    }
    $report.Result = 'Passed'
    if (@($report.Screenshots | Where-Object { $_.Status -ne 'Captured' }).Count -gt 0) { $report.Result = 'Partial'; $report.FailureKind = 'ScreenshotCaptureUnavailable' }
    $report.Stage = 'Complete'
}
catch {
    $report.Errors += $_.Exception.Message
    if ($report.Stage -in @('ValidateCandidate', 'DefenderReadiness', 'DefenderScan', 'InteractiveDesktop')) { $report.Result = 'Blocked'; $report.FailureKind = 'SecurityOrEnvironmentPrerequisite' }
    elseif ($report.Stage -eq 'WaitWindow' -and $null -ne $application -and -not $application.HasExited) { $report.Result = 'Unverified'; $report.FailureKind = 'NativeWindowOrAutomationUnavailable' }
    else { $report.Result = 'Failed'; $report.FailureKind = 'NativeStartupOrFunctionalCheck' }
}
finally {
    if ($null -ne $window) {
        try {
            $elements = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
            $tree = @()
            for ($index = 0; $index -lt [Math]::Min(200, $elements.Count); $index++) {
                try { $current = $elements[$index].Current; $tree += [pscustomobject]@{ Id = $current.AutomationId; Name = $current.Name; Type = $current.ControlType.ProgrammaticName; Offscreen = $current.IsOffscreen; Enabled = $current.IsEnabled } } catch { }
            }
            $tree | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $OutputDirectory 'uia-tree.json') -Encoding UTF8
            if ($report.Result -ne 'Passed') { Capture-Window 'last-window' }
        }
        catch { $report.Errors += "UIA diagnostic tree unavailable: $($_.Exception.Message)" }
    }
    if ($null -ne $application) {
        try {
            if (-not $application.HasExited) {
                $application.CloseMainWindow() | Out-Null
                if (-not $application.WaitForExit(5000)) {
                    $report.ForcedTermination = $true
                    $application.Kill(); $application.WaitForExit()
                    if ($report.Result -in @('Passed', 'Partial')) { $report.Result = 'Failed'; $report.FailureKind = 'NativeShutdown' }
                    $report.Errors += 'The owned app did not close gracefully within five seconds; only that process was terminated.'
                }
            }
            $report.ProcessExitCode = $application.ExitCode
            if ($report.ProcessExitCode -ne 0 -and $report.Result -in @('Passed', 'Partial')) { $report.Result = 'Failed'; $report.FailureKind = 'NativeProcessExit' }
        } catch {
            $report.Errors += "Own process cleanup failed: $($_.Exception.Message)"
            if ($report.Result -in @('Passed', 'Partial')) { $report.Result = 'Failed'; $report.FailureKind = 'NativeShutdown' }
        }
    }
    if (Get-Command Get-MpThreatDetection -ErrorAction SilentlyContinue) {
        try {
            $report.Detections = @(Get-ScopedDetections)
            if ($report.Detections.Count -gt 0) {
                $report.Result = 'Blocked'; $report.FailureKind = 'DefenderDetectedCandidate'
                $report.Errors += 'Defender reported a scoped candidate detection; no remediation was reversed and no candidate binary is uploaded.'
            }
            if ($report.ApplicationExecuted) {
                $finalProtection = Get-MpComputerStatus -ErrorAction Stop
                if (-not $finalProtection.RealTimeProtectionEnabled -or -not $finalProtection.AntivirusEnabled) { throw 'Enabled antivirus protection could not be verified after the native session.' }
            }
        } catch {
            $report.Errors += "Post-run Defender evidence unavailable: $($_.Exception.Message)"
            if ($report.Result -in @('Passed', 'Partial')) { $report.Result = 'Unverified'; $report.FailureKind = 'PostRunDefenderEvidenceUnavailable' }
        }
    }
    foreach ($name in @('startup.log', 'startup.previous.log')) {
        $path = Join-Path $env:LOCALAPPDATA ('Music/Logs/' + $name)
        if (Test-Path -LiteralPath $path) {
            try { if ((Get-Item -LiteralPath $path).Length -le 65536) { Copy-Item -LiteralPath $path -Destination (Join-Path $OutputDirectory $name) } } catch { $report.Errors += 'Could not copy the bounded startup diagnostic log.' }
        }
    }
    try {
        $events = @(Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = $started } -ErrorAction SilentlyContinue | Where-Object { $_.ProviderName -in @('Application Error', '.NET Runtime') -and $_.Message -match '(?i)Music\.(exe|dll)' })
        $report.ApplicationEvents = @($events | Select-Object -First 10 | ForEach-Object { [pscustomobject]@{ TimeCreated = $_.TimeCreated; Id = $_.Id; ProviderName = $_.ProviderName; Message = $_.Message.Substring(0, [Math]::Min(5000, $_.Message.Length)) } })
    } catch { $report.Errors += 'Application event diagnostics were unavailable.' }
    $report.FinishedUtc = [DateTime]::UtcNow.ToString('o')
    $report | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $OutputDirectory 'report.json') -Encoding UTF8
    $summary = @('## Native Windows desktop smoke', '', "Result: **$($report.Result)**", "Stage: $($report.Stage)", "Failure kind: $($report.FailureKind)", "Source: $SourceCommit", "Application executed: $($report.ApplicationExecuted)", "Native Defender exit: $($report.DefenderScanExitCode)", "Application exit: $($report.ProcessExitCode)")
    foreach ($check in $report.Checks) { $summary += "- Passed: $($check.Name) - $($check.Detail)" }
    foreach ($message in $report.Errors) { $summary += "- Diagnostic: $($message -replace '[\r\n]+', ' ')" }
    $summary += 'Only current-source demo functionality is covered. Screenshots require visual review. No Spotify account or animation frame-rate validation is claimed; no candidate binary is uploaded or published.'
    $summary | Set-Content (Join-Path $OutputDirectory 'summary.md') -Encoding UTF8
    $summary | ForEach-Object { Write-Host $_ }
    if (-not [string]::IsNullOrEmpty($env:GITHUB_STEP_SUMMARY)) { $summary | Add-Content $env:GITHUB_STEP_SUMMARY -Encoding UTF8 }
}

if ($report.Result -ne 'Passed') { exit 1 }
