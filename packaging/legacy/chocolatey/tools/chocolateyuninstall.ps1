$ErrorActionPreference = 'Stop'
$appDir = Join-Path $env:ProgramData 'BookshelfNG\app'
$service = Get-Service -Name 'BookshelfNG' -ErrorAction SilentlyContinue
if (-not $service) {
  if (Test-Path $appDir) { Remove-Item -Recurse -Force $appDir }
  return
}

$nssm = Get-Command nssm.exe -ErrorAction SilentlyContinue
if (-not $nssm) { throw 'NSSM is required to remove the BookshelfNG migration service.' }
if ($service.Status -ne 'Stopped') {
  Stop-Service -Name 'BookshelfNG' -Force
  $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
}
& $nssm.Source remove BookshelfNG confirm
if ($LASTEXITCODE -ne 0) { throw 'NSSM could not remove the BookshelfNG service.' }
if (Test-Path $appDir) { Remove-Item -Recurse -Force $appDir }

Write-Host 'The BookshelfNG service and application files were removed. Persistent data and the migration backup were kept.'
