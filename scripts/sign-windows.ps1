[CmdletBinding()]
param(
    [ValidateSet("Payload", "Installer")]
    [string]$Target = "Payload",
    [string]$PublishDirectory = (Join-Path $PSScriptRoot "../artifacts/publish/win-x64"),
    [string]$InstallerPath = (Join-Path $PSScriptRoot "../artifacts/Music-Setup.exe"),
    [string]$ReportPath
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

# Publisher metadata is not an Authenticode signature. With no configured PFX,
# the current distribution remains unsigned. No certificate store is modified.
if ([string]::IsNullOrWhiteSpace($ReportPath)) {
    $ReportPath = Join-Path $PSScriptRoot ("../artifacts/native-smoke/signing-" + $Target.ToLowerInvariant() + ".json")
}
$ReportPath = [IO.Path]::GetFullPath($ReportPath)
$report = [ordered]@{
    SchemaVersion = 1
    SourceCommit = [Environment]::GetEnvironmentVariable("GITHUB_SHA", "Process")
    Target = $Target
    StartedUtc = [DateTime]::UtcNow.ToString("o")
    FinishedUtc = $null
    Result = "Unverified"
    Stage = "Configuration"
    SigningConfigured = $false
    VerifiedPublisher = $false
    PublisherMetadata = "Danvu"
    SignerSubject = $null
    TimestampHost = $null
    FailureReason = $null
    ErrorType = $null
    ErrorHResult = $null
    Files = @()
}
$certificate = $null
$securePassword = $null
$password = $null
$failed = $false
$files = @()

function Stop-Signing {
    param([string]$Reason)
    $report.FailureReason = $Reason
    throw "Signing validation failed."
}

function Assert-CertificatePolicy {
    param(
        [System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate,
        [string]$PolicyOid,
        [switch]$RequirePrivateKey
    )
    if ($RequirePrivateKey -and -not $Certificate.HasPrivateKey) {
        Stop-Signing "The configured certificate does not have a private key."
    }
    $now = [DateTime]::UtcNow
    if ($now -lt $Certificate.NotBefore.ToUniversalTime() -or $now -gt $Certificate.NotAfter.ToUniversalTime()) {
        Stop-Signing "A signing or timestamp certificate is outside its validity period."
    }
    $hasPolicy = $false
    foreach ($extension in $Certificate.Extensions) {
        if ($extension.Oid.Value -eq "2.5.29.37") {
            $eku = [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($extension, $extension.Critical)
            foreach ($usage in $eku.EnhancedKeyUsages) {
                if ($usage.Value -eq $PolicyOid) { $hasPolicy = $true }
            }
        }
    }
    if (-not $hasPolicy) {
        Stop-Signing "A certificate lacks the required explicit enhanced key usage."
    }
    $chain = [System.Security.Cryptography.X509Certificates.X509Chain]::new()
    try {
        $chain.ChainPolicy.RevocationMode = [System.Security.Cryptography.X509Certificates.X509RevocationMode]::Online
        $chain.ChainPolicy.RevocationFlag = [System.Security.Cryptography.X509Certificates.X509RevocationFlag]::EntireChain
        $chain.ChainPolicy.VerificationFlags = [System.Security.Cryptography.X509Certificates.X509VerificationFlags]::NoFlag
        $chain.ChainPolicy.UrlRetrievalTimeout = [TimeSpan]::FromSeconds(30)
        $chain.ChainPolicy.VerificationTime = [DateTime]::Now
        $chain.ChainPolicy.ApplicationPolicy.Add([System.Security.Cryptography.Oid]::new($PolicyOid)) | Out-Null
        if (-not $chain.Build($Certificate)) {
            Stop-Signing "A certificate did not pass normal Windows trust and online revocation checks."
        }
    }
    finally { $chain.Dispose() }
}

function Find-WindowsTimestampTool {
    # CryptUI, used by Set-AuthenticodeSignature, cannot apply HTTPS timestamps.
    # Use the installed SDK tool only for RFC 3161 timestamping, never its PFX
    # password arguments. Do not download or install a signing tool here.
    $candidates = @()
    $programFiles32 = [Environment]::GetEnvironmentVariable("ProgramFiles(x86)", "Process")
    if (-not [string]::IsNullOrWhiteSpace($programFiles32)) {
        $sdkBin = Join-Path $programFiles32 "Windows Kits/10/bin"
        if (Test-Path -LiteralPath $sdkBin -PathType Container) {
            $candidates += @(Get-ChildItem -LiteralPath $sdkBin -Directory |
                Where-Object { $_.Name -match '^10\.0\.\d+\.\d+$' } |
                Sort-Object { [version]$_.Name } -Descending |
                ForEach-Object { Join-Path $_.FullName "x64/signtool.exe" })
        }
    }
    $onPath = Get-Command signtool.exe -CommandType Application -ErrorAction SilentlyContinue
    if ($null -ne $onPath) { $candidates += @($onPath | ForEach-Object { $_.Source }) }
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            $toolSignature = Get-AuthenticodeSignature -LiteralPath $candidate -ErrorAction Stop
            if ($toolSignature.Status.ToString() -ne "Valid" -or $null -eq $toolSignature.SignerCertificate -or
                $toolSignature.SignerCertificate.Subject -notmatch '(^|,\s*)O=Microsoft Corporation(,|$)') {
                Stop-Signing "The installed timestamp tool does not have a valid Microsoft Authenticode signature."
            }
            return $candidate
        }
    }
    Stop-Signing "Configured signing requires an installed Microsoft Windows SDK signtool.exe for HTTPS timestamping."
}

function Add-HttpsTimestamp {
    param([string]$ToolPath, [string]$FilePath, [Uri]$Server)
    $arguments = @("timestamp", "/tr", $Server.AbsoluteUri, "/td", "SHA256", $FilePath)
    $quoted = @($arguments | ForEach-Object {
        if ($_.Contains('"')) { Stop-Signing "An invalid quote was found in a timestamp argument." }
        '"' + $_ + '"'
    })
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $ToolPath
    $start.Arguments = $quoted -join " "
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($name in @("MUSIC_CODESIGN_PFX_PATH", "MUSIC_CODESIGN_PFX_PASSWORD", "MUSIC_CODESIGN_TIMESTAMP_URL")) {
        $start.EnvironmentVariables.Remove($name)
    }
    $process = [Diagnostics.Process]::new()
    try {
        $process.StartInfo = $start
        if (-not $process.Start()) { Stop-Signing "The HTTPS timestamp process did not start." }
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(120000)) {
            $process.Kill()
            $process.WaitForExit()
            Stop-Signing "The HTTPS timestamp request timed out."
        }
        $process.WaitForExit()
        # Drain both streams without logging provider output or input values.
        $stdout.GetAwaiter().GetResult() | Out-Null
        $stderr.GetAwaiter().GetResult() | Out-Null
        if ($process.ExitCode -ne 0) {
            Stop-Signing "The HTTPS timestamp tool did not complete successfully."
        }
    }
    finally { $process.Dispose() }
}

try {
    $pfxPath = [Environment]::GetEnvironmentVariable("MUSIC_CODESIGN_PFX_PATH", "Process")
    $password = [Environment]::GetEnvironmentVariable("MUSIC_CODESIGN_PFX_PASSWORD", "Process")
    $timestampUrl = [Environment]::GetEnvironmentVariable("MUSIC_CODESIGN_TIMESTAMP_URL", "Process")
    $report.SigningConfigured = -not [string]::IsNullOrWhiteSpace($pfxPath)
    if (-not $report.SigningConfigured -and -not [string]::IsNullOrEmpty($password)) {
        Stop-Signing "A signing password was configured without a PFX file."
    }

    $report.Stage = "TargetFiles"
    $paths = if ($Target -eq "Payload") {
        @("Music.exe", "Music.dll", "Danvu.Core.dll") | ForEach-Object { Join-Path $PublishDirectory $_ }
    }
    else { @($InstallerPath) }
    if ($Target -eq "Installer" -and [IO.Path]::GetFileName($InstallerPath) -cne "Music-Setup.exe") {
        Stop-Signing "The installer signing target must be Music-Setup.exe."
    }
    foreach ($path in $paths) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            Stop-Signing "An expected Music signing target is missing."
        }
        $file = Get-Item -LiteralPath $path
        if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            Stop-Signing "Signing targets must be ordinary files, not reparse points."
        }
        $files += [pscustomobject]@{
            Name = $file.Name
            Path = $file.FullName
            Sha256Before = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            Sha256After = $null
            Signature = "NotChecked"
            TimestampVerified = $false
        }
    }
    # Paths are used only in memory. The report contains names and hashes.
    $report.Files = @($files | Select-Object Name, Sha256Before, Sha256After, Signature, TimestampVerified)

    if (-not $report.SigningConfigured) {
        foreach ($file in $files) {
            $file.Sha256After = $file.Sha256Before
            $file.Signature = "NotSignedByThisStep"
        }
        $report.Result = "Unsigned"
        $report.Stage = "Complete"
        Write-Host "No code-signing certificate is configured. Music remains unsigned; Danvu is publisher metadata, not a Windows-verified publisher."
    }
    else {
        $report.Stage = "CertificateValidation"
        if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
            Stop-Signing "Configured Authenticode signing requires Windows."
        }
        if (-not (Test-Path -LiteralPath $pfxPath -PathType Leaf)) {
            Stop-Signing "The configured PFX file is unavailable."
        }
        $timestampUri = $null
        if (-not [Uri]::TryCreate($timestampUrl, [UriKind]::Absolute, [ref]$timestampUri) -or
            $timestampUri.Scheme -ne "https" -or [string]::IsNullOrWhiteSpace($timestampUri.Host) -or
            -not [string]::IsNullOrEmpty($timestampUri.UserInfo) -or -not [string]::IsNullOrEmpty($timestampUri.Query) -or
            -not [string]::IsNullOrEmpty($timestampUri.Fragment)) {
            Stop-Signing "Signing requires an absolute HTTPS timestamp server URL without credentials, a query, or a fragment."
        }
        $report.TimestampHost = $timestampUri.DnsSafeHost
        Get-Command Set-AuthenticodeSignature -CommandType Cmdlet -ErrorAction Stop | Out-Null
        Get-Command Get-AuthenticodeSignature -CommandType Cmdlet -ErrorAction Stop | Out-Null
        $timestampTool = Find-WindowsTimestampTool
        $securePassword = [Security.SecureString]::new()
        if (-not [string]::IsNullOrEmpty($password)) {
            $securePassword.Dispose()
            $securePassword = ConvertTo-SecureString -String $password -AsPlainText -Force
        }
        $password = $null
        $flags = [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet
        $certificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($pfxPath, $securePassword, $flags)
        Assert-CertificatePolicy -Certificate $certificate -PolicyOid "1.3.6.1.5.5.7.3.3" -RequirePrivateKey
        $report.SignerSubject = $certificate.Subject

        foreach ($file in $files) {
            $report.Stage = "Sign-" + $file.Name
            # The password never becomes a process argument. The PFX private key
            # is ephemeral; no key export, trust import, or persistent key store.
            $signature = Set-AuthenticodeSignature -LiteralPath $file.Path -Certificate $certificate `
                -HashAlgorithm SHA256 -IncludeChain NotRoot -ErrorAction Stop
            if ($signature.Status.ToString() -ne "Valid") {
                Stop-Signing "Windows did not validate the newly applied Authenticode signature."
            }
            $report.Stage = "Timestamp-" + $file.Name
            Add-HttpsTimestamp -ToolPath $timestampTool -FilePath $file.Path -Server $timestampUri
            $report.Stage = "Verify-" + $file.Name
            $verified = Get-AuthenticodeSignature -LiteralPath $file.Path -ErrorAction Stop
            if ($verified.Status.ToString() -ne "Valid" -or $null -eq $verified.SignerCertificate -or
                $verified.SignerCertificate.Thumbprint -ne $certificate.Thumbprint) {
                Stop-Signing "The file signature did not match the configured trusted signing certificate."
            }
            if ($null -eq $verified.TimeStamperCertificate) {
                Stop-Signing "The file signature has no verified timestamp certificate."
            }
            Assert-CertificatePolicy -Certificate $verified.TimeStamperCertificate -PolicyOid "1.3.6.1.5.5.7.3.8"
            $file.Signature = "Valid"
            $file.TimestampVerified = $true
            $file.Sha256After = (Get-FileHash -LiteralPath $file.Path -Algorithm SHA256).Hash.ToLowerInvariant()
        }
        $report.Result = "SignedAndVerified"
        $report.VerifiedPublisher = $true
        $report.Stage = "Complete"
        Write-Host ("Authenticode signatures and HTTPS timestamps verified for the " + $Target.ToLowerInvariant() + " targets.")
    }
}
catch {
    $failed = $true
    $report.Result = "Failed"
    $report.VerifiedPublisher = $false
    if ([string]::IsNullOrEmpty($report.FailureReason)) {
        $report.FailureReason = "Signing did not complete during the recorded stage. No unsigned fallback was accepted."
    }
    # Provider exception messages can contain input values. Record their type
    # and HResult only; never serialize the PFX path, password, or private key.
    $report.ErrorType = $_.Exception.GetType().FullName
    $report.ErrorHResult = $_.Exception.HResult
}
finally {
    if ($null -ne $certificate) { $certificate.Dispose() }
    if ($null -ne $securePassword) { $securePassword.Dispose() }
    $password = $null
    $report.Files = @($files | Select-Object Name, Sha256Before, Sha256After, Signature, TimestampVerified)
    $report.FinishedUtc = [DateTime]::UtcNow.ToString("o")
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($ReportPath)) -Force | Out-Null
    [IO.File]::WriteAllText($ReportPath, ($report | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
}
if ($failed) { throw "Windows signing failed. See the signing report for its stage and safe failure metadata." }
