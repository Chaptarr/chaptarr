param(
    [Parameter(Mandatory = $true)]
    [int] $ParentProcessId,

    [Parameter(Mandatory = $true)]
    [long] $ParentProcessStartTicks,

    [Parameter(Mandatory = $true)]
    [string] $DataDirectory,

    [Parameter(Mandatory = $true)]
    [ValidateSet('0', '1')]
    [string] $ChaptarrServiceWasRunning
)

$ErrorActionPreference = 'Stop'
$DataDirectory = [IO.Path]::GetFullPath($DataDirectory)

try {
    while ($true) {
        $parent = Get-Process -Id $ParentProcessId -ErrorAction SilentlyContinue
        if (-not $parent) { break }
        try {
            if ($parent.StartTime.ToUniversalTime().Ticks -ne $ParentProcessStartTicks) { break }
        } finally {
            $parent.Dispose()
        }
        Start-Sleep -Seconds 1
    }

    $chaptarrKeyPath = 'HKLM:\SYSTEM\CurrentControlSet\Services\Chaptarr'
    $readarrKeyPath = 'HKLM:\SYSTEM\CurrentControlSet\Services\Readarr'
    $chaptarrKey = Get-ItemProperty -Path $chaptarrKeyPath -ErrorAction SilentlyContinue
    $readarrKey = Get-ItemProperty -Path $readarrKeyPath -ErrorAction SilentlyContinue
    if (-not $chaptarrKey -or -not $readarrKey -or [int] $readarrKey.Start -ne 4) { return }

    $chaptarrImagePath = [Environment]::ExpandEnvironmentVariables([string] $chaptarrKey.ImagePath)
    $readarrImagePath = [Environment]::ExpandEnvironmentVariables([string] $readarrKey.ImagePath)
    if ($chaptarrImagePath.IndexOf($DataDirectory, [StringComparison]::OrdinalIgnoreCase) -lt 0 -or
        $readarrImagePath.IndexOf($DataDirectory, [StringComparison]::OrdinalIgnoreCase) -lt 0) { return }

    $chaptarrService = Get-Service -Name 'Chaptarr' -ErrorAction SilentlyContinue
    if (-not $chaptarrService) { return }
    try {
        $expectedStatus = if ($ChaptarrServiceWasRunning -eq '1') { 'Running' } else { 'Stopped' }
        if ($chaptarrService.Status -ne $expectedStatus) { return }
    } finally {
        $chaptarrService.Dispose()
        $chaptarrService = $null
    }

    $readarrService = Get-Service -Name 'Readarr' -ErrorAction SilentlyContinue
    if (-not $readarrService) { return }
    try {
        if ($readarrService.Status -ne 'Stopped') { return }
    } finally {
        $readarrService.Dispose()
        $readarrService = $null
    }

    & sc.exe delete Readarr | Out-Null
    if ($LASTEXITCODE -ne 0 -and $LASTEXITCODE -ne 1072) { return }
} catch {
    # The legacy service stays disabled if post-exit cleanup cannot complete.
} finally {
    try { Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue }
    catch { }
}
