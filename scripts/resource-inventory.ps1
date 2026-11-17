[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ApplicationDirectory,
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [Parameter(Mandatory = $true)][string]$SourceCommit
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd('\', '/')
$ApplicationDirectory = [IO.Path]::GetFullPath($ApplicationDirectory).TrimEnd('\', '/')
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$report = [ordered]@{ SourceCommit = $SourceCommit; Scope = 'Build metadata and relative native resource filenames only; no binaries or credentials.'; PublishResources = @(); BuildResources = @(); GeneratedApplication = @(); CompilerInputs = @(); EvaluatedProperties = $null; PriDumpStatus = 'NotRun'; AppResourceMapEntries = @(); Errors = @() }
function Relative-Name([string]$Path, [string]$Root) {
    return [IO.Path]::GetRelativePath($Root, $Path).Replace('\', '/')
}
try {
    if (Test-Path -LiteralPath $ApplicationDirectory) {
        $report.PublishResources = @(Get-ChildItem -LiteralPath $ApplicationDirectory -File -Recurse | Where-Object { $_.Extension -in '.pri', '.xbf', '.xaml' } | Sort-Object FullName | Select-Object -First 512 | ForEach-Object { [ordered]@{ Path = Relative-Name $_.FullName $ApplicationDirectory; Bytes = $_.Length } })
    }
    $project = Join-Path $repository 'src/Danvu.Windows/Danvu.Windows.csproj'
    $obj = Join-Path $repository 'src/Danvu.Windows/obj'
    $bin = Join-Path $repository 'src/Danvu.Windows/bin'
    foreach ($directory in @($obj, $bin)) {
        if (-not (Test-Path -LiteralPath $directory)) { continue }
        $report.BuildResources += @(Get-ChildItem -LiteralPath $directory -File -Recurse | Where-Object { $_.Name -in 'App.xbf', 'App.xaml', 'Music.pri', 'resources.pri' } | Sort-Object FullName | Select-Object -First 32 | ForEach-Object { [ordered]@{ Path = Relative-Name $_.FullName $repository; Bytes = $_.Length } })
    }
    if (Test-Path -LiteralPath $obj) {
        foreach ($file in @(Get-ChildItem -LiteralPath $obj -Filter 'App.g*.cs' -File -Recurse | Sort-Object FullName | Select-Object -First 8)) {
            if ($file.Length -gt 1048576) { continue }
            $code = [IO.File]::ReadAllText($file.FullName)
            $report.GeneratedApplication += [ordered]@{ Path = Relative-Name $file.FullName $repository; ResourceUris = @([regex]::Matches($code, 'ms-appx:[^"\s]+') | ForEach-Object { $_.Value } | Select-Object -Unique -First 8); CallsLoadComponent = $code.Contains('LoadComponent'); Bytes = $file.Length }
        }
        foreach ($file in @(Get-ChildItem -LiteralPath $obj -Filter 'input.json' -File -Recurse | Sort-Object FullName | Select-Object -First 8)) {
            if ($file.Length -gt 16777216) { continue }
            $inputData = [IO.File]::ReadAllText($file.FullName) | ConvertFrom-Json
            $entry = [ordered]@{ Path = Relative-Name $file.FullName $repository }
            foreach ($name in @('RootNamespace', 'PriIndexName', 'XamlResourceMapName', 'ProjectName', 'OutputType', 'IsPass1')) {
                if ($null -ne $inputData.PSObject.Properties[$name]) { $entry[$name] = ([string]$inputData.$name).Substring(0, [Math]::Min(256, ([string]$inputData.$name).Length)) }
            }
            $report.CompilerInputs += $entry
        }
    }
    $properties = & dotnet msbuild $project '-nologo' '-p:Configuration=Release' '-p:Platform=x64' '-p:RuntimeIdentifier=win-x64' '-getProperty:PriIndexName,PriInitialPath,XamlResourceMapName,EnablePriGenTooling,ProjectPriFileName,PrependPriInitialPath,EnableMsixTooling' 2>$null
    if ($LASTEXITCODE -eq 0) { $report.EvaluatedProperties = (($properties -join "`n") | ConvertFrom-Json).Properties }
    else { $report.Errors += 'MSBuild resource-property evaluation failed.' }
    $pri = Join-Path $ApplicationDirectory 'Music.pri'
    $sdkBin = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10/bin'
    $makePri = @(Get-ChildItem -LiteralPath $sdkBin -Directory | Sort-Object Name -Descending | ForEach-Object { Join-Path $_.FullName 'x64/makepri.exe' } | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1)
    if ((Test-Path -LiteralPath $pri) -and $makePri.Count -eq 1) {
        $temporary = Join-Path $env:RUNNER_TEMP ('music-resource-map-' + [guid]::NewGuid().ToString('N') + '.xml')
        try {
            & $makePri[0] dump /if $pri /of $temporary /dt detailed /o 2>&1 | Out-Null
            if ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $temporary)) {
                $report.PriDumpStatus = 'Completed'
                if ((Get-Item -LiteralPath $temporary).Length -le 16777216) {
                    [xml]$map = [IO.File]::ReadAllText($temporary)
                    $report.AppResourceMapEntries = @($map.SelectNodes('//*[@name="App.xbf" or @name="App.xaml"]') | Select-Object -First 8 | ForEach-Object { $text = $_.OuterXml.Replace($repository, '<repository>'); $text.Substring(0, [Math]::Min(4096, $text.Length)) })
                } else { $report.Errors += 'PRI dump exceeded bounded parser limit.' }
            } else { $report.PriDumpStatus = 'ToolFailed' }
        } finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force } }
    } else { $report.PriDumpStatus = 'ApplicationPriOrSdkToolUnavailable' }
} catch {
    $report.Errors += ('Resource inventory failed: ' + $_.Exception.GetType().FullName + '; HRESULT=0x' + $_.Exception.HResult.ToString('X8'))
}
$json = $report | ConvertTo-Json -Depth 10
if ([Text.Encoding]::UTF8.GetByteCount($json) -gt 262144) { throw 'Resource inventory exceeded its bounded export size.' }
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'resource-inventory.json'), $json, [Text.UTF8Encoding]::new($false))
