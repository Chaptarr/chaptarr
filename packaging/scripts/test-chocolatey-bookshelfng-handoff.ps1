[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)]
  [ValidatePattern('^\d+\.\d+\.\d+$')]
  [string] $Version,

  [Parameter(Mandatory = $true)]
  [string] $ArchivePath,

  [string] $PackagePath,

  [ValidatePattern('^\d+\.\d+\.\d+\.\d+$')]
  [string] $BookshelfVersion = '0.4.21.76'
)

$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$ArchivePath = (Resolve-Path -LiteralPath $ArchivePath).Path
$runnerTemp = if ([string]::IsNullOrWhiteSpace($env:RUNNER_TEMP)) { [IO.Path]::GetTempPath() } else { $env:RUNNER_TEMP }
$runSuffix = if ([string]::IsNullOrWhiteSpace($env:GITHUB_RUN_ID)) { [guid]::NewGuid().ToString('N') } else { $env:GITHUB_RUN_ID }
$workingDirectory = Join-Path $runnerTemp "chocolatey-bookshelfng-handoff-$runSuffix"
$bookshelfRoot = Join-Path $env:ProgramData 'BookshelfNG'
$legacyDataDirectory = Join-Path $bookshelfRoot 'data with spaces'
$choco = Get-Command choco.exe -ErrorAction Stop
$smokeProject = Join-Path $repoRoot 'distribution\windows\installer-net-smoke\ChaptarrNG.Installer.Smoke.csproj'
$createdPackage = [string]::IsNullOrWhiteSpace($PackagePath)
$handoffPassed = $false

if (Test-Path -LiteralPath $workingDirectory) {
  throw "The task-scoped Chocolatey directory already exists: $workingDirectory"
}
if (Get-Service -Name 'BookshelfNG' -ErrorAction SilentlyContinue) {
  throw 'A BookshelfNG service is already registered on this Windows runner; refusing to modify it.'
}
if (Test-Path -LiteralPath $bookshelfRoot) {
  throw "BookshelfNG app data already exists at $bookshelfRoot; refusing to overwrite runner state."
}

function Invoke-Chocolatey {
  param([Parameter(Mandatory = $true)][string[]] $Arguments)
  $output = @(& $script:choco.Source @Arguments 2>&1)
  $exitCode = $LASTEXITCODE
  [pscustomobject]@{
    ExitCode = $exitCode
    Output = @($output | ForEach-Object { $_.ToString() })
  }
}

function Get-InstalledPackageVersion {
  param([Parameter(Mandatory = $true)][string] $PackageId)
  $packageRoot = Join-Path $env:ChocolateyInstall "lib\$PackageId"
  $nuspec = Get-ChildItem -LiteralPath $packageRoot -Filter '*.nuspec' -File -Recurse -ErrorAction SilentlyContinue |
    Select-Object -First 1
  if (-not $nuspec) { return $null }
  [xml] $parsed = Get-Content -LiteralPath $nuspec.FullName -Raw
  $metadata = $parsed.SelectSingleNode("//*[local-name()='metadata']")
  if (-not $metadata) { throw "Installed Chocolatey package $PackageId has no metadata element." }
  return $metadata.SelectSingleNode("*[local-name()='version']").InnerText
}

function Read-PackageMetadata {
  param([Parameter(Mandatory = $true)][string] $Path)
  Add-Type -AssemblyName System.IO.Compression.FileSystem
  $zip = [System.IO.Compression.ZipFile]::OpenRead($Path)
  try {
    $nuspecEntries = @($zip.Entries | Where-Object { $_.FullName -match '(?i)\.nuspec$' })
    if ($nuspecEntries.Count -ne 1) { throw 'The candidate Chocolatey package must contain exactly one nuspec.' }
    $reader = [IO.StreamReader]::new($nuspecEntries[0].Open())
    try { [xml] $nuspec = $reader.ReadToEnd() } finally { $reader.Dispose() }
    $metadata = $nuspec.SelectSingleNode("//*[local-name()='metadata']")
    $installEntry = $zip.Entries | Where-Object { $_.FullName.Replace('\', '/') -match '(?i)^tools/chocolateyinstall\.ps1$' } | Select-Object -First 1
    if (-not $installEntry) { throw 'The candidate Chocolatey package is missing tools/chocolateyinstall.ps1.' }
    $recoveryEntry = $zip.Entries | Where-Object { $_.FullName.Replace('\', '/') -match '(?i)^tools/bookshelfng-startup-recovery\.ps1$' } | Select-Object -First 1
    if (-not $recoveryEntry -or $recoveryEntry.Length -le 0) {
      throw 'The candidate Chocolatey package is missing the BookshelfNG startup recovery helper.'
    }
    $reader = [IO.StreamReader]::new($installEntry.Open())
    try { $installScript = $reader.ReadToEnd() } finally { $reader.Dispose() }
    return [pscustomobject]@{
      Id = $metadata.SelectSingleNode("*[local-name()='id']").InnerText
      Version = $metadata.SelectSingleNode("*[local-name()='version']").InnerText
      InstallScript = $installScript
    }
  } finally {
    $zip.Dispose()
  }
}

function Get-NssmSetting {
  param([Parameter(Mandatory = $true)][string] $Name)
  $value = (& $script:nssm.Source get BookshelfNG $Name 2>&1 | Out-String).Trim()
  if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($value)) {
    throw "NSSM could not read the BookshelfNG $Name setting."
  }
  return $value
}

function Format-RedactedServiceSetting {
  param([AllowNull()][string] $Value)
  if ($null -eq $Value) { return '<null>' }
  return [regex]::Replace($Value, '(?i)(?<name>api(?:[-_]?key)|password|token|secret)(?<separator>\s*[:=]\s*|\s+)(?<value>"[^"]*"|''[^'']*''|\S+)', '${name}${separator}<redacted>')
}

function Wait-BookshelfService {
  param([int] $TimeoutSeconds = 90)
  $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
  do {
    $service = Get-Service -Name 'BookshelfNG' -ErrorAction SilentlyContinue
    if ($service -and $service.Status -eq 'Running') { return $service }
    Start-Sleep -Seconds 2
  } while ([DateTime]::UtcNow -lt $deadline)
  throw "The BookshelfNG service did not reach Running within $TimeoutSeconds seconds."
}

function Wait-BookshelfHealth {
  $port = 8787
  $configPath = Join-Path $legacyDataDirectory 'config.xml'
  if (Test-Path -LiteralPath $configPath) {
    [xml] $config = Get-Content -LiteralPath $configPath -Raw
    $portNode = $config.SelectSingleNode("//*[local-name()='Port']")
    if ($portNode -and $portNode.InnerText -match '^\d+$') { $port = [int] $portNode.InnerText }
  }
  $url = "http://127.0.0.1:$port/ping"
  $deadline = [DateTime]::UtcNow.AddSeconds(120)
  do {
    $service = Get-Service -Name 'BookshelfNG' -ErrorAction SilentlyContinue
    if (-not $service -or $service.Status -ne 'Running') {
      throw 'The BookshelfNG service stopped while waiting for its health endpoint.'
    }
    try {
      $response = Invoke-WebRequest -Uri $url -TimeoutSec 3
      if ($response.StatusCode -eq 200) { return $url }
    } catch { }
    Start-Sleep -Seconds 2
  } while ([DateTime]::UtcNow -lt $deadline)
  throw "BookshelfNG did not answer HTTP 200 from $url within 120 seconds."
}

function Invoke-MigrationFixture {
  param([Parameter(Mandatory = $true)][string[]] $Arguments)
  $output = @(& $script:smokeExecutable @Arguments 2>&1)
  if ($LASTEXITCODE -ne 0) {
    throw "The Windows migration fixture command failed ($($Arguments -join ' ')): $($output -join "`n")"
  }
  return @($output | ForEach-Object { $_.ToString() })
}

function Get-MigrationCanary {
  $output = Invoke-MigrationFixture @('--bookshelf-data-canary', $legacyDataDirectory)
  $hash = $output | Where-Object { $_ -match '^[a-f0-9]{64}$' } | Select-Object -Last 1
  if (-not $hash) { throw 'The migration fixture did not emit its logical data canary.' }
  return $hash
}

try {
  New-Item -ItemType Directory -Path $workingDirectory | Out-Null

  if ($createdPackage) {
    $PackagePath = Join-Path $workingDirectory "bookshelfng.$Version.nupkg"
    $packageRoot = Join-Path $workingDirectory 'package'
    $toolsDirectory = Join-Path $packageRoot 'tools'
    New-Item -ItemType Directory -Force -Path $toolsDirectory | Out-Null
    Copy-Item -LiteralPath (Join-Path $repoRoot 'packaging\legacy\chocolatey\bookshelfng.nuspec.in') -Destination (Join-Path $packageRoot 'bookshelfng.nuspec')
    Copy-Item -LiteralPath (Join-Path $repoRoot 'packaging\legacy\chocolatey\tools\chocolateyinstall.ps1.in') -Destination (Join-Path $toolsDirectory 'chocolateyinstall.ps1')
    Copy-Item -LiteralPath (Join-Path $repoRoot 'packaging\legacy\chocolatey\tools\chocolateyuninstall.ps1') -Destination (Join-Path $toolsDirectory 'chocolateyuninstall.ps1')

    Copy-Item -LiteralPath (Join-Path $repoRoot 'packaging\legacy\chocolatey\tools\bookshelfng-startup-recovery.ps1') -Destination (Join-Path $toolsDirectory 'bookshelfng-startup-recovery.ps1')
    $archiveHash = (Get-FileHash -LiteralPath $ArchivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $nuspecPath = Join-Path $packageRoot 'bookshelfng.nuspec'
    $nuspecText = (Get-Content -LiteralPath $nuspecPath -Raw).Replace('__VERSION__', $Version)
    [IO.File]::WriteAllText($nuspecPath, $nuspecText, [Text.UTF8Encoding]::new($false))
    $installPath = Join-Path $toolsDirectory 'chocolateyinstall.ps1'
    $installText = (Get-Content -LiteralPath $installPath -Raw).Replace('__VERSION__', $Version).Replace('__ARCHIVE_SHA256__', $archiveHash)
    if ($installText.Contains('__VERSION__') -or $installText.Contains('__ARCHIVE_SHA256__')) {
      throw 'The rendered BookshelfNG Chocolatey candidate has unresolved archive or version placeholders.'
    }
    $tokens = $null; $parseErrors = $null
    [System.Management.Automation.Language.Parser]::ParseInput($installText, [ref] $tokens, [ref] $parseErrors) | Out-Null
    if ($parseErrors.Count -gt 0) { throw ($parseErrors | Out-String) }
    [IO.File]::WriteAllText($installPath, $installText, [Text.UTF8Encoding]::new($false))
    & $script:choco.Source pack $nuspecPath --outputdirectory $packageRoot
    if ($LASTEXITCODE -ne 0) { throw 'Chocolatey could not pack the BookshelfNG candidate package.' }
    $PackagePath = (Get-ChildItem -LiteralPath $packageRoot -Filter "bookshelfng.$Version.nupkg" -File | Select-Object -First 1).FullName
    if (-not $PackagePath) { throw 'The BookshelfNG Chocolatey candidate package was not created.' }
  } else {
    $PackagePath = (Resolve-Path -LiteralPath $PackagePath).Path
  }

  if (-not (Test-Path -LiteralPath $ArchivePath -PathType Leaf) -or -not (Test-Path -LiteralPath $PackagePath -PathType Leaf)) {
    throw 'The candidate archive and Chocolatey package must both exist as files.'
  }
  $packageMetadata = Read-PackageMetadata $PackagePath
  if ($packageMetadata.Id -ne 'bookshelfng' -or $packageMetadata.Version -ne $Version) {
    throw "The candidate package identity/version is $($packageMetadata.Id)/$($packageMetadata.Version); expected bookshelfng/$Version."
  }
  if ($packageMetadata.InstallScript -notmatch 'CHAPTARRNG_TEST_ARCHIVE_PATH') {
    throw 'The candidate package does not contain the checksum-pinned private Windows archive handoff.'
  }
  if ($packageMetadata.InstallScript -notmatch 'CHAPTARRNG_TEST_FAIL_HANDOFF') {
    throw 'The candidate package does not contain the guarded private Windows rollback injection.'
  }
  $archiveHash = (Get-FileHash -LiteralPath $ArchivePath -Algorithm SHA256).Hash.ToLowerInvariant()
  $checksumPattern = '\$testArchiveHash -ne ''(?<hash>[a-f0-9]{64})'''
  $checksumMatch = [regex]::Match($packageMetadata.InstallScript, $checksumPattern)
  if (-not $checksumMatch.Success -or $checksumMatch.Groups['hash'].Value -ne $archiveHash) {
    throw 'The candidate package does not pin the exact Windows archive supplied to this handoff test.'
  }

  $packageInstallScriptPath = Join-Path $workingDirectory 'candidate-chocolateyinstall.ps1'
  [IO.File]::WriteAllText($packageInstallScriptPath, $packageMetadata.InstallScript, [Text.UTF8Encoding]::new($false))
  & (Join-Path $PSScriptRoot 'test-chocolatey-migration-choice.ps1') -InstallScriptPath $packageInstallScriptPath

  # The smoke project references the installer, whose embedded resource must
  # exist at compile time. Use the already verified release archive as its
  # exact payload and remove only the file staged by this test.
  $payloadDirectory = Join-Path $repoRoot 'distribution\windows\installer-net\Payload'
  $payloadPath = Join-Path $payloadDirectory 'Chaptarr.payload.zip'
  if (Test-Path -LiteralPath $payloadPath) {
    throw "Refusing to replace an existing Windows installer payload: $payloadPath"
  }
  $createdPayloadDirectory = -not (Test-Path -LiteralPath $payloadDirectory)
  if ($createdPayloadDirectory) { New-Item -ItemType Directory -Path $payloadDirectory | Out-Null }
  try {
    Copy-Item -LiteralPath $ArchivePath -Destination $payloadPath
    & dotnet build $smokeProject --configuration Release --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw 'The Windows migration fixture could not be built for this handoff.' }
  } finally {
    if (Test-Path -LiteralPath $payloadPath) { Remove-Item -LiteralPath $payloadPath -Force }
    if ($createdPayloadDirectory -and (Test-Path -LiteralPath $payloadDirectory)) {
      Remove-Item -LiteralPath $payloadDirectory
    }
  }
  $smokeExecutable = Get-ChildItem -LiteralPath (Join-Path $repoRoot 'distribution\windows\installer-net-smoke\bin\Release') -Filter 'ChaptarrNG.Installer.Smoke.exe' -File -Recurse |
    Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1 -ExpandProperty FullName
  if (-not $smokeExecutable) { throw 'The self-contained Windows migration fixture executable was not produced.' }

  $publicSource = 'https://community.chocolatey.org/api/v2/'
  $candidateSource = "$(Split-Path -Parent $PackagePath);$publicSource"
  $installResult = Invoke-Chocolatey @('install', 'bookshelfng', '--version', $BookshelfVersion, '--source', $publicSource, '--yes', '--no-progress')
  if ($installResult.ExitCode -ne 0) { throw "Could not install the exact public BookshelfNG $BookshelfVersion predecessor: $($installResult.Output -join "`n")" }
  if ((Get-InstalledPackageVersion 'bookshelfng') -ne $BookshelfVersion) {
    throw "Chocolatey did not install the pinned BookshelfNG package version $BookshelfVersion."
  }

  $script:nssm = Get-Command nssm.exe -ErrorAction Stop
  $legacyService = Wait-BookshelfService
  $legacyService.WaitForStatus('Running', [TimeSpan]::FromSeconds(90))
  $legacyDatabase = Join-Path (Join-Path $env:ProgramData 'BookshelfNG\data') 'readarr.db'
  $databaseDeadline = [DateTime]::UtcNow.AddSeconds(90)
  while ((-not (Test-Path -LiteralPath $legacyDatabase)) -and [DateTime]::UtcNow -lt $databaseDeadline) {
    Start-Sleep -Seconds 2
  }
  if (-not (Test-Path -LiteralPath $legacyDatabase)) { throw 'The pinned public BookshelfNG package did not create readarr.db.' }

  $legacyService.Stop()
  $legacyService.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
  $defaultDataDirectory = Join-Path $bookshelfRoot 'data'
  Move-Item -LiteralPath $defaultDataDirectory -Destination $legacyDataDirectory
  $legacyDatabase = Join-Path $legacyDataDirectory 'readarr.db'
  $serviceParameters = "/nobrowser -data=`"$legacyDataDirectory`""
  & $nssm.Source set BookshelfNG AppParameters $serviceParameters
  if ($LASTEXITCODE -ne 0) { throw 'NSSM could not move the BookshelfNG service onto its custom data path.' }

  Invoke-MigrationFixture @('--seed-bookshelfng', $legacyDataDirectory) | Out-Null
  Start-Service -Name 'BookshelfNG'
  Wait-BookshelfService | Out-Null
  $healthUrl = Wait-BookshelfHealth
  $canaryBeforeManual = Get-MigrationCanary
  $legacyServiceRecord = Get-CimInstance -ClassName Win32_Service -Filter "Name='BookshelfNG'" -ErrorAction Stop
  if (-not $legacyServiceRecord -or $legacyServiceRecord.State -ne 'Running' -or $legacyServiceRecord.ProcessId -le 0) {
    throw 'The pinned BookshelfNG predecessor did not have a running process before the manual-defer check.'
  }
  $serviceProcessIdBeforeManual = [uint32] $legacyServiceRecord.ProcessId
  $serviceStartNameBeforeManual = $legacyServiceRecord.StartName
  $serviceStartModeBeforeManual = $legacyServiceRecord.StartMode
  $applicationBeforeManual = Get-NssmSetting 'Application'
  $parametersBeforeManual = Get-NssmSetting 'AppParameters'
  $directoryBeforeManual = Get-NssmSetting 'AppDirectory'
  if (-not (Test-Path -LiteralPath $applicationBeforeManual -PathType Leaf)) {
    throw "The BookshelfNG executable was not present before manual defer: $applicationBeforeManual"
  }
  $applicationHashBeforeManual = (Get-FileHash -LiteralPath $applicationBeforeManual -Algorithm SHA256).Hash
  $stageDirectory = Join-Path $bookshelfRoot 'app.new'
  $previousDirectory = Join-Path $bookshelfRoot 'app.previous'
  $handoffManifest = Join-Path $bookshelfRoot 'handoff\bookshelfng-migration-handoff.json'

  $manualResult = Invoke-Chocolatey @('upgrade', 'bookshelfng', '--version', $Version, '--source', $candidateSource, '--yes', '--no-progress', '--force', '--params', "'/ProviderMode=Goodreads /MigrationMode=manual'")
  if ($manualResult.ExitCode -eq 0) { throw 'The MigrationMode=manual update unexpectedly succeeded instead of deferring.' }
  $manualDiagnostic = 'MigrationMode=manual cancelled this package update before stopping BookshelfNG or changing its files.'
  if (($manualResult.Output -join "`n") -notmatch [regex]::Escape($manualDiagnostic)) {
    throw "The manual update failed without the package's exact defer diagnostic: $($manualResult.Output -join "`n")"
  }
  $serviceAfterManual = Get-CimInstance -ClassName Win32_Service -Filter "Name='BookshelfNG'" -ErrorAction Stop
  if (-not $serviceAfterManual -or $serviceAfterManual.State -ne 'Running' -or [uint32] $serviceAfterManual.ProcessId -ne $serviceProcessIdBeforeManual) {
    throw 'Manual defer stopped or restarted the installed BookshelfNG service.'
  }
  if ($serviceAfterManual.StartName -ne $serviceStartNameBeforeManual -or $serviceAfterManual.StartMode -ne $serviceStartModeBeforeManual) {
    throw 'Manual defer changed the BookshelfNG service account or start mode.'
  }
  if ((Get-NssmSetting 'Application') -ne $applicationBeforeManual -or
      (Get-NssmSetting 'AppParameters') -ne $parametersBeforeManual -or
      (Get-NssmSetting 'AppDirectory') -ne $directoryBeforeManual) {
    throw 'Manual defer changed the BookshelfNG executable path or service parameters.'
  }
  if ((Get-FileHash -LiteralPath $applicationBeforeManual -Algorithm SHA256).Hash -ne $applicationHashBeforeManual) {
    throw 'Manual defer changed the installed BookshelfNG executable.'
  }
  Invoke-MigrationFixture @('--verify-bookshelfng-source', $legacyDataDirectory) | Out-Null
  if ((Get-MigrationCanary) -ne $canaryBeforeManual -or -not (Test-Path -LiteralPath $legacyDatabase -PathType Leaf)) {
    throw 'Manual defer changed the seeded BookshelfNG records or removed readarr.db.'
  }
  $sourceBackupsAfterManual = @(Get-ChildItem -LiteralPath $legacyDataDirectory -Filter '*.bookshelfng-backup-*.db' -File -Force -ErrorAction SilentlyContinue)
  if ($sourceBackupsAfterManual.Count -gt 0) {
    throw 'Manual defer created a BookshelfNG source backup before migration was accepted.'
  }
  if ((Test-Path -LiteralPath $stageDirectory) -or (Test-Path -LiteralPath $previousDirectory) -or (Test-Path -LiteralPath $handoffManifest)) {
    throw 'Manual defer created staged application, rollback, or migration-handoff files.'
  }
  if ((Get-InstalledPackageVersion 'bookshelfng') -ne $BookshelfVersion) {
    throw 'Chocolatey recorded the candidate package as installed even though manual defer should have cancelled it.'
  }
  $healthUrlAfterManual = Wait-BookshelfHealth
  if ($healthUrlAfterManual -ne $healthUrl) {
    throw 'Manual defer changed the BookshelfNG health endpoint.'
  }

  $env:CHAPTARRNG_TEST_ARCHIVE_PATH = $ArchivePath
  $env:CHAPTARRNG_TEST_FAIL_HANDOFF = 'after-migration-health'
  $env:CHAPTARR_BOOKSHELF_HANDOFF_TIMEOUT_SECONDS = '600'
  $previousMachineArchivePath = [Environment]::GetEnvironmentVariable('CHAPTARRNG_TEST_ARCHIVE_PATH', 'Machine')
  $previousMachineHandoffFailure = [Environment]::GetEnvironmentVariable('CHAPTARRNG_TEST_FAIL_HANDOFF', 'Machine')
  $previousMachineHandoffTimeout = [Environment]::GetEnvironmentVariable('CHAPTARR_BOOKSHELF_HANDOFF_TIMEOUT_SECONDS', 'Machine')
  try {
    # Chocolatey can run package scripts in a nested PowerShell process. Make
    # the disposable acceptance inputs visible there, then restore machine state.
    [Environment]::SetEnvironmentVariable('CHAPTARRNG_TEST_ARCHIVE_PATH', $ArchivePath, 'Machine')
    [Environment]::SetEnvironmentVariable('CHAPTARRNG_TEST_FAIL_HANDOFF', 'after-migration-health', 'Machine')
    [Environment]::SetEnvironmentVariable('CHAPTARR_BOOKSHELF_HANDOFF_TIMEOUT_SECONDS', '600', 'Machine')
    $rollbackResult = Invoke-Chocolatey @('upgrade', 'bookshelfng', '--version', $Version, '--source', $candidateSource, '--yes', '--no-progress', '--force', '--params', "'/ProviderMode=Goodreads'")
  } finally {
    [Environment]::SetEnvironmentVariable('CHAPTARRNG_TEST_ARCHIVE_PATH', $previousMachineArchivePath, 'Machine')
    [Environment]::SetEnvironmentVariable('CHAPTARRNG_TEST_FAIL_HANDOFF', $previousMachineHandoffFailure, 'Machine')
    [Environment]::SetEnvironmentVariable('CHAPTARR_BOOKSHELF_HANDOFF_TIMEOUT_SECONDS', $previousMachineHandoffTimeout, 'Machine')
    Remove-Item Env:\CHAPTARRNG_TEST_ARCHIVE_PATH -ErrorAction SilentlyContinue
    Remove-Item Env:\CHAPTARRNG_TEST_FAIL_HANDOFF -ErrorAction SilentlyContinue
    Remove-Item Env:\CHAPTARR_BOOKSHELF_HANDOFF_TIMEOUT_SECONDS -ErrorAction SilentlyContinue
  }
  if ($rollbackResult.ExitCode -eq 0) { throw 'The injected post-migration failure unexpectedly completed the Chocolatey update.' }
  $rollbackDiagnostic = 'Injected private Windows acceptance failure after BookshelfNG migration health passed.'
  if (($rollbackResult.Output -join "`n") -notmatch [regex]::Escape($rollbackDiagnostic)) {
    throw "The automatic rollback run failed before the intended post-migration injection: $($rollbackResult.Output -join "`n")"
  }
  if ((Get-InstalledPackageVersion 'bookshelfng') -ne $BookshelfVersion) {
    throw 'Chocolatey did not retain the public BookshelfNG package version after the injected rollback.'
  }
  $serviceRecoveryWaitFailure = $null
  try { Wait-BookshelfService | Out-Null }
  catch { $serviceRecoveryWaitFailure = $_.Exception.Message }
  $serviceAfterRollback = Get-CimInstance -ClassName Win32_Service -Filter "Name='BookshelfNG'" -ErrorAction SilentlyContinue
  if ($serviceRecoveryWaitFailure -or -not $serviceAfterRollback -or $serviceAfterRollback.State -ne 'Running' -or
      $serviceAfterRollback.StartName -ne $serviceStartNameBeforeManual -or
      $serviceAfterRollback.StartMode -ne $serviceStartModeBeforeManual) {
    $actualState = if ($serviceAfterRollback) { $serviceAfterRollback.State } else { '<missing>' }
    $actualStartName = if ($serviceAfterRollback) { $serviceAfterRollback.StartName } else { '<missing>' }
    $actualStartMode = if ($serviceAfterRollback) { $serviceAfterRollback.StartMode } else { '<missing>' }
    $rollbackOutputTail = @($rollbackResult.Output | Select-Object -Last 40) -join "`n"
    throw "The injected migration failure did not restore and restart the original BookshelfNG service. Actual state=$actualState, start name=$actualStartName, start mode=$actualStartMode; expected start name=$serviceStartNameBeforeManual and start mode=$serviceStartModeBeforeManual. Wait error=$serviceRecoveryWaitFailure. Chocolatey output tail:`n$rollbackOutputTail"
  }
  $applicationAfterRollback = Get-NssmSetting 'Application'
  $parametersAfterRollback = Get-NssmSetting 'AppParameters'
  $directoryAfterRollback = Get-NssmSetting 'AppDirectory'
  $serviceSettingsChanged = @(
    ($applicationAfterRollback -ne $applicationBeforeManual),
    ($parametersAfterRollback -ne $parametersBeforeManual),
    ($directoryAfterRollback -ne $directoryBeforeManual)
  ) -contains $true
  if ($serviceSettingsChanged) {
    $expectedApplication = Format-RedactedServiceSetting $applicationBeforeManual
    $actualApplication = Format-RedactedServiceSetting $applicationAfterRollback
    $expectedParameters = Format-RedactedServiceSetting $parametersBeforeManual
    $actualParameters = Format-RedactedServiceSetting $parametersAfterRollback
    $expectedDirectory = Format-RedactedServiceSetting $directoryBeforeManual
    $actualDirectory = Format-RedactedServiceSetting $directoryAfterRollback
    throw "The injected migration failure did not restore the original BookshelfNG service paths and parameters. Expected Application=[$expectedApplication], AppParameters=[$expectedParameters], AppDirectory=[$expectedDirectory]; actual Application=[$actualApplication], AppParameters=[$actualParameters], AppDirectory=[$actualDirectory]."
  }
  if ((Get-FileHash -LiteralPath $applicationBeforeManual -Algorithm SHA256).Hash -ne $applicationHashBeforeManual) {
    throw 'The injected migration failure changed the original BookshelfNG executable.'
  }
  Wait-BookshelfService | Out-Null
  $healthUrl = Wait-BookshelfHealth
  Invoke-MigrationFixture @('--verify-bookshelfng-source', $legacyDataDirectory) | Out-Null
  if ((Get-MigrationCanary) -ne $canaryBeforeManual) {
    throw 'The injected migration failure did not restore the schema-158 BookshelfNG canary.'
  }
  $failedTargetDatabase = Get-ChildItem -LiteralPath $legacyDataDirectory -Filter 'readarr.db.chaptarr-failed-*' -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match '^readarr\.db\.chaptarr-failed-\d{8}T\d{9}Z$' } |
    Select-Object -First 1
  if (-not $failedTargetDatabase) {
    throw 'The injected migration rollback did not retain the failed ChaptarrNG target database for diagnosis.'
  }
  Invoke-MigrationFixture @('--verify-chaptarr-database', $failedTargetDatabase.FullName) | Out-Null
  if ((Test-Path -LiteralPath $stageDirectory) -or (Test-Path -LiteralPath $previousDirectory) -or (Test-Path -LiteralPath $handoffManifest)) {
    throw 'The injected migration rollback left staging, rollback-app, or deferred-handoff state behind.'
  }

  $env:CHAPTARRNG_TEST_ARCHIVE_PATH = $ArchivePath
  $env:CHAPTARR_BOOKSHELF_HANDOFF_TIMEOUT_SECONDS = '600'
  $previousMachineArchivePath = [Environment]::GetEnvironmentVariable('CHAPTARRNG_TEST_ARCHIVE_PATH', 'Machine')
  $previousMachineHandoffFailure = [Environment]::GetEnvironmentVariable('CHAPTARRNG_TEST_FAIL_HANDOFF', 'Machine')
  $previousMachineHandoffTimeout = [Environment]::GetEnvironmentVariable('CHAPTARR_BOOKSHELF_HANDOFF_TIMEOUT_SECONDS', 'Machine')
  try {
    # Chocolatey may elevate its package script, and service recovery runs as
    # LocalSystem; expose the local archive and timeout while disabling failure injection.
    [Environment]::SetEnvironmentVariable('CHAPTARRNG_TEST_ARCHIVE_PATH', $ArchivePath, 'Machine')
    [Environment]::SetEnvironmentVariable('CHAPTARRNG_TEST_FAIL_HANDOFF', $null, 'Machine')
    [Environment]::SetEnvironmentVariable('CHAPTARR_BOOKSHELF_HANDOFF_TIMEOUT_SECONDS', '600', 'Machine')
    $automaticResult = Invoke-Chocolatey @('upgrade', 'bookshelfng', '--version', $Version, '--source', $candidateSource, '--yes', '--no-progress', '--force', '--params', "'/ProviderMode=Goodreads'")
  } finally {
    [Environment]::SetEnvironmentVariable('CHAPTARRNG_TEST_ARCHIVE_PATH', $previousMachineArchivePath, 'Machine')
    [Environment]::SetEnvironmentVariable('CHAPTARRNG_TEST_FAIL_HANDOFF', $previousMachineHandoffFailure, 'Machine')
    [Environment]::SetEnvironmentVariable('CHAPTARR_BOOKSHELF_HANDOFF_TIMEOUT_SECONDS', $previousMachineHandoffTimeout, 'Machine')
    Remove-Item Env:\CHAPTARRNG_TEST_ARCHIVE_PATH -ErrorAction SilentlyContinue
    Remove-Item Env:\CHAPTARRNG_TEST_FAIL_HANDOFF -ErrorAction SilentlyContinue
    Remove-Item Env:\CHAPTARR_BOOKSHELF_HANDOFF_TIMEOUT_SECONDS -ErrorAction SilentlyContinue
  }
  if ($automaticResult.ExitCode -ne 0) { throw "The automatic Chocolatey bridge update failed: $($automaticResult.Output -join "`n")" }
  if ((Get-InstalledPackageVersion 'bookshelfng') -ne $Version) {
    throw "Chocolatey did not record the exact candidate version $Version after the automatic update."
  }
  Wait-BookshelfService | Out-Null
  $healthUrl = Wait-BookshelfHealth

  $expectedApplication = Join-Path $bookshelfRoot 'app\bin\Chaptarr.Console.exe'
  $applicationAfterUpgrade = Get-NssmSetting 'Application'
  if ([IO.Path]::GetFullPath($applicationAfterUpgrade) -ne [IO.Path]::GetFullPath($expectedApplication) -or
      -not (Test-Path -LiteralPath $expectedApplication -PathType Leaf)) {
    throw "The BookshelfNG service did not hand off to the exact ChaptarrNG candidate executable: $applicationAfterUpgrade"
  }
  if ([IO.Path]::GetFullPath((Get-NssmSetting 'AppDirectory')) -ne [IO.Path]::GetFullPath((Join-Path $bookshelfRoot 'app\bin'))) {
    throw 'The BookshelfNG service working directory did not move to the ChaptarrNG candidate.'
  }
  if ((Get-NssmSetting 'AppParameters') -notmatch [regex]::Escape($legacyDataDirectory)) {
    throw 'The BookshelfNG service lost its selected data path during the ChaptarrNG handoff.'
  }
  $serviceAfterUpgrade = Get-CimInstance -ClassName Win32_Service -Filter "Name='BookshelfNG'" -ErrorAction Stop
  if (-not $serviceAfterUpgrade -or $serviceAfterUpgrade.State -ne 'Running' -or
      $serviceAfterUpgrade.StartName -ne $serviceStartNameBeforeManual -or
      $serviceAfterUpgrade.StartMode -ne $serviceStartModeBeforeManual) {
    throw 'The ChaptarrNG candidate did not preserve the running BookshelfNG service identity and start mode.'
  }
  $serviceProcessId = [uint32] $serviceAfterUpgrade.ProcessId
  if ($serviceProcessId -le 0) { throw 'The upgraded BookshelfNG service has no running service process.' }
  $candidateProcessPath = [IO.Path]::GetFullPath($expectedApplication)
  $runningCandidateProcess = Get-CimInstance -ClassName Win32_Process -Filter "ParentProcessId=$serviceProcessId" -ErrorAction Stop |
    Where-Object {
      -not [string]::IsNullOrWhiteSpace($_.ExecutablePath) -and
      [string]::Equals([IO.Path]::GetFullPath($_.ExecutablePath), $candidateProcessPath, [StringComparison]::OrdinalIgnoreCase)
    } |
    Select-Object -First 1
  if (-not $runningCandidateProcess) {
    throw 'The running BookshelfNG service process has no ChaptarrNG candidate executable child.'
  }
  Invoke-MigrationFixture @('--verify-bookshelfng-migration', $legacyDataDirectory) | Out-Null
  if ((Get-MigrationCanary) -ne $canaryBeforeManual) {
    throw 'The ChaptarrNG candidate changed the migrated author, book, edition, file, path, or setting canary after startup.'
  }
  $retainedBackup = Get-ChildItem -LiteralPath $legacyDataDirectory -Filter '*.bookshelfng-backup-*.db' -File -Force -ErrorAction SilentlyContinue |
    Select-Object -First 1
  if (-not $retainedBackup) { throw 'The automatic Chocolatey handoff did not retain a BookshelfNG source database backup.' }

  $uninstallResult = Invoke-Chocolatey @('uninstall', 'bookshelfng', '--yes', '--no-progress')
  if ($uninstallResult.ExitCode -ne 0) { throw "Chocolatey could not remove the upgraded package: $($uninstallResult.Output -join "`n")" }
  if (Get-Service -Name 'BookshelfNG' -ErrorAction SilentlyContinue) { throw 'Chocolatey uninstall left the BookshelfNG service registered.' }
  if (Test-Path -LiteralPath (Join-Path $bookshelfRoot 'app')) { throw 'Chocolatey uninstall left the candidate application payload behind.' }
  if (-not (Test-Path -LiteralPath $retainedBackup.FullName)) {
    throw 'Chocolatey uninstall removed the migrated database or retained BookshelfNG source backup.'
  }
  Invoke-MigrationFixture @('--verify-bookshelfng-migration', $legacyDataDirectory) | Out-Null

  $handoffPassed = $true
  Write-Host "PASS: BookshelfNG $BookshelfVersion MigrationMode=manual preserved the live service and data; an injected post-migration failure restored schema-158 source data, service configuration, executable, and health while retaining the failed target; automatic candidate $Version migration preserved fixture records, integrity, source backup, service identity, running candidate process, data path, and /ping at $healthUrl."
} finally {
  if (-not $handoffPassed) {
    $service = Get-Service -Name 'BookshelfNG' -ErrorAction SilentlyContinue
    if ($service -and $service.Status -ne 'Stopped') {
      try { Stop-Service -Name 'BookshelfNG' -Force -ErrorAction Stop } catch { }
    }
    Write-Warning "Preserving failed Chocolatey handoff state for review at $bookshelfRoot and $workingDirectory."
  } else {
    if (Test-Path -LiteralPath $bookshelfRoot) { Remove-Item -LiteralPath $bookshelfRoot -Recurse -Force }
    if (Test-Path -LiteralPath $workingDirectory) { Remove-Item -LiteralPath $workingDirectory -Recurse -Force }
  }
}
