param(
    [Parameter(Mandatory = $true)]
    [string] $ManifestPath
)

$ErrorActionPreference = 'Stop'
$runnerTemp = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($env:RUNNER_TEMP))
$ManifestPath = [IO.Path]::GetFullPath($ManifestPath)
$expectedManifest = Join-Path $runnerTemp 'chaptarr-bookshelf-service-roundtrip-cleanup.json'
if (-not [string]::Equals($ManifestPath, $expectedManifest, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing unexpected cleanup manifest '$ManifestPath'. Expected '$expectedManifest'."
}

if (-not (Test-Path -LiteralPath $ManifestPath -PathType Leaf)) {
    Write-Host 'No Windows service round-trip cleanup manifest was created.'
    exit 0
}

$manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
$scratchRoot = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath([string] $manifest.ScratchRoot))
if (-not [string]::Equals([IO.Path]::GetDirectoryName($scratchRoot), $runnerTemp, [StringComparison]::OrdinalIgnoreCase) -or
    -not [IO.Path]::GetFileName($scratchRoot).StartsWith('chaptarr-bookshelf-service-roundtrip-', [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing cleanup outside the round-trip scratch directory: '$scratchRoot'."
}

$installerTempDirectory = [IO.Path]::GetFullPath([string] $manifest.InstallerTempDirectory)
$expectedInstallerTempDirectory = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) 'ChaptarrNG.Installer'))
if (-not [string]::Equals($installerTempDirectory, $expectedInstallerTempDirectory, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing unexpected installer temporary directory '$installerTempDirectory'."
}

function Stop-And-DeleteRoundTripService([string] $Name) {
    $serviceKeyPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$Name"
    if (-not (Test-Path -LiteralPath $serviceKeyPath)) { return }

    $serviceKey = Get-ItemProperty -LiteralPath $serviceKeyPath -ErrorAction Stop
    $imagePath = [Environment]::ExpandEnvironmentVariables([string] $serviceKey.ImagePath)
    if ([string]::IsNullOrWhiteSpace($imagePath) -or
        $imagePath.IndexOf($scratchRoot, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "Refusing to remove $Name; its service command does not point inside '$scratchRoot'."
    }

    $serviceProcessId = 0
    $serviceInfo = Get-CimInstance -ClassName Win32_Service -Filter "Name = '$Name'" -ErrorAction SilentlyContinue
    if ($serviceInfo) {
        $serviceProcessId = [int] $serviceInfo.ProcessId
        $serviceInfo = $null
    }

    $service = Get-Service -Name $Name -ErrorAction SilentlyContinue
    if ($service) {
        try {
            $service.Refresh()
            if ($service.Status -ne 'Stopped') {
                if ($service.Status -ne 'StopPending') {
                    Write-Host "Stopping $Name..."
                    $service.Stop()
                }
                $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
            }
        } finally {
            $service.Dispose()
            $service = $null
        }
    }

    if ($serviceProcessId -gt 0) {
        $serviceProcess = Get-Process -Id $serviceProcessId -ErrorAction SilentlyContinue
        if ($serviceProcess) {
            try {
                if (-not $serviceProcess.WaitForExit(15000)) {
                    throw "$Name process $serviceProcessId did not exit after reporting STOPPED."
                }
            } finally {
                $serviceProcess.Dispose()
                $serviceProcess = $null
            }
        }
    }

    Write-Host "Deleting $Name after the smoke process released its service handles..."
    & sc.exe delete $Name | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "sc.exe delete $Name failed with exit code $LASTEXITCODE."
    }

    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while ((Test-Path -LiteralPath $serviceKeyPath) -and [DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 250
    }
    if (Test-Path -LiteralPath $serviceKeyPath) {
        throw "$Name remains registered after its service handles were released and a delete request succeeded."
    }
}

Stop-And-DeleteRoundTripService 'Chaptarr'
Stop-And-DeleteRoundTripService 'Readarr'

$registryPath = 'HKLM:\SOFTWARE\ChaptarrNG\BookshelfMigration'
if (Test-Path -LiteralPath $registryPath) {
    $record = Get-ItemProperty -LiteralPath $registryPath -ErrorAction Stop
    $recordDataDirectory = [IO.Path]::GetFullPath([string] $record.DataDirectory)
    $scratchPrefix = $scratchRoot + [IO.Path]::DirectorySeparatorChar
    if ($recordDataDirectory.StartsWith($scratchPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $registryPath -Recurse -Force
    }
}

if ($manifest.RemoveInstallerTempDirectory -and
    (Test-Path -LiteralPath $installerTempDirectory -PathType Container) -and
    -not (Get-ChildItem -LiteralPath $installerTempDirectory -Force | Select-Object -First 1)) {
    Remove-Item -LiteralPath $installerTempDirectory -Force
}

if (Test-Path -LiteralPath $scratchRoot -PathType Container) {
    $removed = $false
    for ($attempt = 0; $attempt -lt 12 -and -not $removed; $attempt++) {
        try {
            Remove-Item -LiteralPath $scratchRoot -Recurse -Force
            $removed = $true
        } catch {
            if ($attempt -eq 11) { throw }
            Start-Sleep -Milliseconds 500
        }
    }
}

Remove-Item -LiteralPath $ManifestPath -Force
Write-Host "Windows service round-trip cleanup passed for '$scratchRoot'."
