$ErrorActionPreference = 'Stop'
$appDir = Join-Path $env:ProgramFiles 'ChaptarrNG\app'
$service = Get-Service -Name 'ChaptarrNG' -ErrorAction SilentlyContinue
if (-not $service) {
  if (Test-Path $appDir) { Remove-Item -Recurse -Force $appDir }
  return
}

$nssm = Get-Command nssm.exe -ErrorAction SilentlyContinue
if (-not $nssm) { throw 'NSSM is required to remove the ChaptarrNG service.' }
if ($service.Status -ne 'Stopped') {
  Stop-Service -Name 'ChaptarrNG' -Force
  $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
}
& $nssm.Source remove ChaptarrNG confirm
if ($LASTEXITCODE -ne 0) { throw 'NSSM could not remove the ChaptarrNG service.' }
if (Test-Path $appDir) { Remove-Item -Recurse -Force $appDir }

Write-Host 'ChaptarrNG service removed. Persistent data was kept.'
