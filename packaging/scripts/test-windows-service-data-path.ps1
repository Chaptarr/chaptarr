param(
  [string] $AppDirectory = (Join-Path $PSScriptRoot '..\..\publish\Chaptarr')
)

$ErrorActionPreference = 'Stop'

function Protect-DiagnosticLine([string] $line) {
  $line = [regex]::Replace(
    $line,
    '(?i)((?:--|/)[\w-]*(?:api[-_]?key|access[-_]?token|refresh[-_]?token|token|password|passwd|secret|authorization)\s+)(?:"[^"]*"|''[^'']*''|[^\s,;]+)',
    '$1<redacted>'
  )
  $line = [regex]::Replace(
    $line,
    '(?i)(\b[\w-]*(?:api[-_]?key|token|password|secret|authorization)\b["'']?\s*[:=]\s*)(?:"[^"]*"|''[^'']*''|(?:Bearer|Basic)\s+\S+|[^,\s;}\]]+)',
    '$1<redacted>'
  )
  $line = [regex]::Replace(
    $line,
    '(?i)(<(?:api[-_]?key|token|password|secret|authorization)\b[^>]*>)[^<]*(</[^>]+>)',
    '$1<redacted>$2'
  )
  return [regex]::Replace($line, '(?i)(\bBearer\s+)\S+', '$1<redacted>')
}

function Test-DiagnosticRedaction {
  $samples = @(
    'apiKey=smoke-api-secret',
    '{"accessToken":"smoke-token-secret","accessTokenSecret":"smoke-oauth-secret"}',
    'Chaptarr.Console.exe --api-key smoke-cli-api-secret --oauth-token "smoke-cli-oauth-secret"',
    'Authorization: Bearer smoke-bearer-secret',
    '<apiKey>smoke-xml-secret</apiKey>'
  )

  foreach ($sample in $samples) {
    $redacted = Protect-DiagnosticLine $sample
    if ($redacted -match 'smoke-(?:api|token|oauth|bearer|xml|cli-api|cli-oauth)-secret') {
      throw 'The Windows service diagnostic redactor left a synthetic secret in its output.'
    }
  }
}

Test-DiagnosticRedaction

function Write-ServiceHealthDiagnostics([string] $DataDirectory, [int] $Port, [DateTime] $StartedAt) {
  Write-Host "Collecting safe startup diagnostics for Chaptarr service on port $Port."

  try {
    $serviceRecord = Get-CimInstance Win32_Service -Filter "Name='Chaptarr'" -ErrorAction Stop
    if ($serviceRecord) {
      Write-Host "Service state=$($serviceRecord.State); processId=$($serviceRecord.ProcessId); startName=$($serviceRecord.StartName)"
      Write-Host "Service command: $(Protect-DiagnosticLine $serviceRecord.PathName)"
      if ($serviceRecord.ProcessId -gt 0) {
        $process = Get-CimInstance Win32_Process -Filter "ProcessId=$($serviceRecord.ProcessId)" -ErrorAction SilentlyContinue
        if ($process) {
          Write-Host "Service process: $(Protect-DiagnosticLine $process.CommandLine)"
        }
      }
    }
  } catch {
    Write-Warning "Could not inspect the Chaptarr service process: $($_.Exception.GetType().Name)"
  }

  try {
    $listeners = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue
    if ($listeners) {
      foreach ($listener in $listeners) {
        $owner = Get-Process -Id $listener.OwningProcess -ErrorAction SilentlyContinue
        Write-Host "Listener: address=$($listener.LocalAddress); processId=$($listener.OwningProcess); process=$($owner.ProcessName)"
      }
    } else {
      Write-Host "No process is listening on port $Port."
    }
  } catch {
    Write-Warning "Could not inspect the listener on port ${Port}: $($_.Exception.GetType().Name)"
  }

  $logDirectory = Join-Path $DataDirectory 'logs'
  if (Test-Path -LiteralPath $logDirectory -PathType Container) {
    $logFiles = Get-ChildItem -LiteralPath $logDirectory -File -Recurse -ErrorAction SilentlyContinue |
      Where-Object { $_.Extension -in '.txt', '.log' } |
      Sort-Object LastWriteTime -Descending |
      Select-Object -First 3

    foreach ($logFile in $logFiles) {
      Write-Host "Recent application log lines from $($logFile.Name):"
      Get-Content -LiteralPath $logFile.FullName -Tail 80 -ErrorAction SilentlyContinue |
        ForEach-Object { Write-Host (Protect-DiagnosticLine $_) }
    }
    if (-not $logFiles) {
      Write-Host "No text application logs were created in $logDirectory."
    }
  } else {
    Write-Host "The application log directory was not created in the selected data directory."
  }

  try {
    $events = Get-WinEvent -FilterHashtable @{
      LogName = 'Application'
      StartTime = $StartedAt
      ProviderName = @('.NET Runtime', 'Application Error', 'Windows Error Reporting')
    } -ErrorAction SilentlyContinue | Select-Object -First 8

    foreach ($event in $events) {
      Write-Host "Application event: time=$($event.TimeCreated.ToUniversalTime().ToString('o')); source=$($event.ProviderName); id=$($event.Id); message=$(Protect-DiagnosticLine $event.Message)"
    }
  } catch {
    Write-Warning "Could not inspect recent application events: $($_.Exception.GetType().Name)"
  }
}

$appRoot = (Resolve-Path -LiteralPath $AppDirectory).Path
$sourceConsole = Join-Path $appRoot 'Chaptarr.Console.exe'
if (-not (Test-Path -LiteralPath $sourceConsole -PathType Leaf)) {
  throw "The published Windows application is missing Chaptarr.Console.exe at $sourceConsole."
}

if (Get-Service -Name 'Chaptarr' -ErrorAction SilentlyContinue) {
  throw 'A Chaptarr service already exists on this runner; refusing to replace it.'
}

$testRoot = Join-Path $env:RUNNER_TEMP ("chaptarr-service-data-path-{0}" -f [guid]::NewGuid().ToString('N'))
$installRoot = Join-Path $testRoot 'Chaptarr Install With Spaces'
$dataDirectory = Join-Path $testRoot 'Readarr Data With Spaces'
$console = Join-Path $installRoot 'Chaptarr.Console.exe'
$serviceStartedAt = [DateTime]::UtcNow

try {
  New-Item -ItemType Directory -Force -Path $installRoot, $dataDirectory | Out-Null
  Copy-Item -Path (Join-Path $appRoot '*') -Destination $installRoot -Recurse -Force

  & icacls.exe $installRoot /grant 'NT AUTHORITY\LOCAL SERVICE:(OI)(CI)RX' /T /C | Out-Null
  if ($LASTEXITCODE -ne 0) { throw 'Could not grant the Local Service account read access to the test application.' }
  & icacls.exe $dataDirectory /grant 'NT AUTHORITY\LOCAL SERVICE:(OI)(CI)M' /T /C | Out-Null
  if ($LASTEXITCODE -ne 0) { throw 'Could not grant the Local Service account access to the test data directory.' }

  & $console '/i' "-data=$dataDirectory" '/nobrowser'
  if ($LASTEXITCODE -ne 0) { throw "Chaptarr service installation returned exit code $LASTEXITCODE." }

  $service = Get-Service -Name 'Chaptarr' -ErrorAction Stop
  $deadline = [DateTime]::UtcNow.AddSeconds(90)
  while ($service.Status -ne 'Running' -and [DateTime]::UtcNow -lt $deadline) {
    Start-Sleep -Seconds 2
    $service = Get-Service -Name 'Chaptarr' -ErrorAction Stop
  }
  if ($service.Status -ne 'Running') { throw 'Chaptarr did not reach RUNNING after service installation.' }

  $serviceRecord = Get-CimInstance Win32_Service -Filter "Name='Chaptarr'"
  if (-not $serviceRecord -or $serviceRecord.PathName -notmatch [regex]::Escape($dataDirectory)) {
    throw 'The registered Windows service command does not contain the selected data directory.'
  }

  $configPath = Join-Path $dataDirectory 'config.xml'
  if (-not (Test-Path -LiteralPath $configPath)) {
    throw 'Chaptarr did not create config.xml in the selected data directory.'
  }
  [xml] $config = Get-Content -LiteralPath $configPath
  $port = 8789
  if ($config.Config.Port -and [int]::TryParse($config.Config.Port, [ref] $port) -eq $false) {
    throw "Chaptarr wrote an invalid HTTP port to config.xml: $($config.Config.Port)"
  }

  $ready = $false
  $deadline = [DateTime]::UtcNow.AddSeconds(90)
  while (-not $ready -and [DateTime]::UtcNow -lt $deadline) {
    try {
      $response = Invoke-WebRequest -Uri "http://127.0.0.1:$port/ping" -TimeoutSec 3
      $ready = $response.StatusCode -eq 200
    } catch {
      Start-Sleep -Seconds 2
    }
  }
  if (-not $ready) {
    Write-ServiceHealthDiagnostics -DataDirectory $dataDirectory -Port $port -StartedAt $serviceStartedAt
    throw 'The Chaptarr service did not pass its local health check. Safe service, listener, application-log, and Windows event diagnostics are printed above.'
  }

  Write-Host 'Windows service install preserved the selected data directory and passed the local health check.'
} finally {
  $service = Get-Service -Name 'Chaptarr' -ErrorAction SilentlyContinue
  if ($service) {
    if ($service.Status -ne 'Stopped') {
      Stop-Service -Name 'Chaptarr' -Force
      $service = Get-Service -Name 'Chaptarr' -ErrorAction Stop
      $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    }

    & sc.exe delete Chaptarr | Out-Null
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    while ((Get-Service -Name 'Chaptarr' -ErrorAction SilentlyContinue) -and [DateTime]::UtcNow -lt $deadline) {
      Start-Sleep -Milliseconds 500
    }
  }

  if (-not (Get-Service -Name 'Chaptarr' -ErrorAction SilentlyContinue) -and (Test-Path -LiteralPath $testRoot)) {
    Remove-Item -LiteralPath $testRoot -Recurse -Force
  } elseif (Test-Path -LiteralPath $testRoot) {
    Write-Warning "Chaptarr's test service is still registered; keeping its files at $testRoot."
  }
}
