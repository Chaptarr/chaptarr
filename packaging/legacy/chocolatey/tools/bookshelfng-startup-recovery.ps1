param(
  [Parameter(Mandatory = $true)] [string] $ManifestPath
)

$ErrorActionPreference = 'Stop'
$taskName = 'ChaptarrNG-BookshelfNG-Migration-Recovery'

function Write-Manifest {
  param([object] $Manifest)
  $temporaryPath = "$ManifestPath.tmp-$([Guid]::NewGuid().ToString('N'))"
  $Manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $temporaryPath -Encoding UTF8
  Move-Item -LiteralPath $temporaryPath -Destination $ManifestPath -Force
}

function Get-BookshelfFileSha256 {
  param([string] $LiteralPath)

  $algorithm = [Security.Cryptography.SHA256]::Create()
  try {
    $stream = [IO.File]::OpenRead($LiteralPath)
    try {
      return [BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-', '').ToLowerInvariant()
    } finally {
      $stream.Dispose()
    }
  } finally {
    $algorithm.Dispose()
  }
}

function Get-BookshelfJournalFingerprint {
  param([string] $Path)

  try {
    $rootItem = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
  } catch [System.Management.Automation.ItemNotFoundException] {
    return 'missing'
  } catch [System.IO.FileNotFoundException] {
    return 'missing'
  } catch [System.IO.DirectoryNotFoundException] {
    return 'missing'
  }

  if (($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw "Migration fingerprint '$Path' cannot follow a symbolic link or reparse point."
  }

  if (-not $rootItem.PSIsContainer) {
    $before = Get-Item -LiteralPath $rootItem.FullName -Force -ErrorAction Stop
    if (($before.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
      throw "Migration fingerprint '$Path' changed to a symbolic link or reparse point."
    }
    $hash = Get-BookshelfFileSha256 -LiteralPath $rootItem.FullName
    $after = Get-Item -LiteralPath $rootItem.FullName -Force -ErrorAction Stop
    if (($after.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
        $after.Length -ne $before.Length -or
        $after.LastWriteTimeUtc -ne $before.LastWriteTimeUtc) {
      throw "Migration fingerprint '$Path' changed while it was being recorded."
    }
    return 'sha256:' + $hash
  }

  $root = $rootItem.FullName
  $allItems = @(Get-ChildItem -LiteralPath $root -Force -Recurse -ErrorAction Stop)
  $relativePaths = [Collections.Generic.List[string]]::new()
  foreach ($item in $allItems) {
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
      throw "Migration fingerprint '$Path' contains a symbolic link or reparse point."
    }
    if (-not $item.PSIsContainer) {
      $relativePaths.Add([IO.Path]::GetRelativePath($root, $item.FullName).Replace('\', '/'))
    }
  }
  $relativePathArray = $relativePaths.ToArray()
  [Array]::Sort($relativePathArray, [StringComparer]::Ordinal)
  $entries = [Collections.Generic.List[string]]::new()
  foreach ($relativePath in $relativePathArray) {
    $filePath = Join-Path $root $relativePath.Replace('/', [IO.Path]::DirectorySeparatorChar)
    $before = Get-Item -LiteralPath $filePath -Force -ErrorAction Stop
    if (($before.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
      throw "Migration fingerprint '$Path' contains a symbolic link or reparse point."
    }
    $hash = Get-BookshelfFileSha256 -LiteralPath $filePath
    $after = Get-Item -LiteralPath $filePath -Force -ErrorAction Stop
    if (($after.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
        $after.Length -ne $before.Length -or
        $after.LastWriteTimeUtc -ne $before.LastWriteTimeUtc) {
      throw "Migration fingerprint '$filePath' changed while it was being recorded."
    }
    $entries.Add([string]::Concat($relativePath, [char]0, $hash))
  }

  $algorithm = [Security.Cryptography.SHA256]::Create()
  try {
    $bytes = [Text.Encoding]::UTF8.GetBytes(($entries -join [char]10))
    return 'sha256:' + [BitConverter]::ToString($algorithm.ComputeHash($bytes)).Replace('-', '').ToLowerInvariant()
  } finally {
    $algorithm.Dispose()
  }
}

function Write-SharedMigrationJournal {
  param(
    [string] $DataDirectory,
    [string] $State,
    [string] $OutcomeName,
    [string] $OutcomeValue,
    [string] $FingerprintName,
    [string] $FingerprintPath
  )

  try {
    $journalDirectory = Join-Path $DataDirectory '.chaptarrng-bookshelfng-journal'
    $statesDirectory = Join-Path $journalDirectory 'states'
    $outcomesDirectory = Join-Path $journalDirectory 'outcomes'
    $fingerprintsDirectory = Join-Path $journalDirectory 'fingerprints'
    foreach ($directoryPath in @($journalDirectory, $statesDirectory, $outcomesDirectory, $fingerprintsDirectory)) {
      if (-not (Test-Path -LiteralPath $directoryPath -PathType Container)) {
        New-Item -ItemType Directory -Force -Path $directoryPath | Out-Null
      }
      $directoryItem = Get-Item -LiteralPath $directoryPath -Force -ErrorAction Stop
      if (-not $directoryItem.PSIsContainer -or
          ($directoryItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Shared BookshelfNG journal path '$directoryPath' must be a regular directory."
      }
    }
    $utf8 = [Text.UTF8Encoding]::new($false)

    if (-not [string]::IsNullOrWhiteSpace($State)) {
      if ($State -notin @('detected', 'preflighted', 'snapshotted', 'converted', 'promoted', 'healthy', 'rolled-back', 'intervention-required')) {
        throw "Invalid shared BookshelfNG journal state '$State'."
      }

      $timestamp = [DateTime]::UtcNow.ToString('o')
      $stateMarkerPath = Join-Path $statesDirectory $State
      $temporaryPath = "$stateMarkerPath.tmp-$([Guid]::NewGuid().ToString('N'))"
      [IO.File]::WriteAllText($temporaryPath, "$timestamp`n", $utf8)
      Move-Item -LiteralPath $temporaryPath -Destination $stateMarkerPath -Force

      $currentStatePath = Join-Path $journalDirectory 'state'
      $temporaryPath = "$currentStatePath.tmp-$([Guid]::NewGuid().ToString('N'))"
      [IO.File]::WriteAllText($temporaryPath, "$State`n", $utf8)
      Move-Item -LiteralPath $temporaryPath -Destination $currentStatePath -Force
    }

    if (-not [string]::IsNullOrWhiteSpace($OutcomeName)) {
      if ($OutcomeName -notmatch '^[A-Za-z0-9_.-]+$' -or $OutcomeValue -notmatch '^[A-Za-z0-9:._-]+$') {
        throw 'Invalid shared BookshelfNG journal outcome token.'
      }

      $targetPath = Join-Path $outcomesDirectory $OutcomeName
      $temporaryPath = "$targetPath.tmp-$([Guid]::NewGuid().ToString('N'))"
      [IO.File]::WriteAllText($temporaryPath, "$OutcomeValue`n", $utf8)
      Move-Item -LiteralPath $temporaryPath -Destination $targetPath -Force
    }

    if (-not [string]::IsNullOrWhiteSpace($FingerprintName)) {
      if ($FingerprintName -notmatch '^[A-Za-z0-9_.-]+$' -or [string]::IsNullOrWhiteSpace($FingerprintPath)) {
        throw 'Invalid shared BookshelfNG journal fingerprint.'
      }
      $fingerprint = Get-BookshelfJournalFingerprint $FingerprintPath
      $targetPath = Join-Path $fingerprintsDirectory $FingerprintName
      $temporaryPath = "$targetPath.tmp-$([Guid]::NewGuid().ToString('N'))"
      [IO.File]::WriteAllText($temporaryPath, [string]::Concat($fingerprint, [char]10), $utf8)
      Move-Item -LiteralPath $temporaryPath -Destination $targetPath -Force
    }
  } catch {
    Write-EventLog -LogName Application -Source 'ChaptarrNG BookshelfNG Migration' -EventId 4205 -EntryType Warning -Message "The channel recovery result could not be written to the shared migration journal: $($_.Exception.Message)" -ErrorAction SilentlyContinue
  }
}

function Invoke-BookshelfUtility {
  param(
    [string] $Executable,
    [string[]] $Arguments,
    [string[]] $EnvironmentEntries
  )

  $savedValues = @{}
  try {
    foreach ($entry in $EnvironmentEntries) {
      $separator = $entry.IndexOf('=')
      if ($separator -le 0) { continue }
      $name = $entry.Substring(0, $separator)
      if (-not $savedValues.ContainsKey($name)) {
        $savedValues[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
      }
      [Environment]::SetEnvironmentVariable($name, $entry.Substring($separator + 1), 'Process')
    }

    & $Executable @Arguments
    return $LASTEXITCODE
  } finally {
    foreach ($name in $savedValues.Keys) {
      [Environment]::SetEnvironmentVariable($name, $savedValues[$name], 'Process')
    }
  }
}

function Set-NssmValue {
  param([string] $Nssm, [string] $Name, [AllowNull()] [string] $Value)
  if ([string]::IsNullOrEmpty($Value)) {
    & $Nssm reset BookshelfNG $Name
  } else {
    & $Nssm set BookshelfNG $Name $Value
  }
  if ($LASTEXITCODE -ne 0) { throw "NSSM could not restore BookshelfNG $Name." }
}

function Restore-Environment {
  param([string] $Nssm, [string] $Setting, [string[]] $Entries)
  if ($Entries.Count -gt 0) {
    & $Nssm set BookshelfNG $Setting @Entries | Out-Null
  } else {
    & $Nssm reset BookshelfNG $Setting
  }
  if ($LASTEXITCODE -ne 0) { throw "NSSM could not restore BookshelfNG $Setting." }
}

function Wait-ServiceStopped {
  param([int] $TimeoutSeconds = 60)
  $watch = [Diagnostics.Stopwatch]::StartNew()
  while ($watch.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
    $service = Get-Service -Name 'BookshelfNG' -ErrorAction SilentlyContinue
    if (-not $service -or $service.Status -eq 'Stopped') { return $true }
    Start-Sleep -Seconds 1
  }
  return $false
}

function Test-HasPopulatedSqliteDatabaseFamily {
  param([Parameter(Mandatory = $true)] [string] $DatabasePath)
  if ((Test-Path -LiteralPath $DatabasePath -PathType Leaf) -and
      (Get-Item -LiteralPath $DatabasePath -Force).Length -gt 0) {
    return $true
  }

  foreach ($suffix in @('-journal', '-wal')) {
    $sidecarPath = "$DatabasePath$suffix"
    if ((Test-Path -LiteralPath $sidecarPath -PathType Leaf) -and
        (Get-Item -LiteralPath $sidecarPath -Force).Length -gt 0) {
      return $true
    }
  }

  return $false
}

function Restore-PreviousBookshelf {
  param([object] $Manifest)

  $service = Get-Service -Name 'BookshelfNG' -ErrorAction SilentlyContinue
  if ($service -and $service.Status -ne 'Stopped') {
    Stop-Service -Name 'BookshelfNG' -Force -ErrorAction Stop
    if (-not (Wait-ServiceStopped)) { throw 'The BookshelfNG service did not stop for automatic rollback.' }
  }

  $console = $Manifest.ConsolePath
  $dataDir = $Manifest.DataDirectory
  $environmentEntries = @($Manifest.MigrationEnvironment)
  $bookshelfSource = $true
  if ($Manifest.PSObject.Properties.Name -contains 'BookshelfSource') {
    $bookshelfSource = [bool] $Manifest.BookshelfSource
  }

  if ($bookshelfSource) {
    $previewArguments = @(
      '-preview-bookshelf-migration',
      "-data=$dataDir"
    )
    if (($Manifest.PSObject.Properties.Name -contains 'MergeSqliteSources') -and $Manifest.MergeSqliteSources) {
      $previewArguments += '-merge-bookshelf-databases'
    }
    $sourceStillReadable = Invoke-BookshelfUtility $console $previewArguments $environmentEntries

    if ($sourceStillReadable -ne 0) {
      if ($Manifest.PostgresSource) {
        if (-not $Manifest.PostgresBackupDirectory -or
            -not (Test-Path -LiteralPath $Manifest.PostgresBackupDirectory) -or
            -not (Get-ChildItem -LiteralPath $Manifest.PostgresBackupDirectory -Filter 'postgres-main.dump' -File -ErrorAction SilentlyContinue | Select-Object -First 1)) {
          throw 'The PostgreSQL source is not readable and its complete pre-cutover backup is unavailable; the current database was left untouched.'
        }

        $restorePreview = Invoke-BookshelfUtility $console @(
          '-preview-bookshelf-postgres-restore',
          '-confirm-bookshelf-service-stopped',
          "-bookshelf-postgres-backup=$($Manifest.PostgresBackupDirectory)",
          "-data=$dataDir"
        ) $environmentEntries
        if ($restorePreview -ne 0) { throw "The retained PostgreSQL rollback dumps could not be validated (exit $restorePreview)." }

        $targetPostgresBackupDirectory = Join-Path $Manifest.PostgresBackupDirectory ("target-chaptarr-log-cache-{0}" -f [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ'))
        if (Test-Path -LiteralPath $targetPostgresBackupDirectory) {
          throw "A ChaptarrNG PostgreSQL log/cache backup path already exists at $targetPostgresBackupDirectory; inspect it before retrying."
        }
        New-Item -ItemType Directory -Path $targetPostgresBackupDirectory | Out-Null
        $Manifest.TargetPostgresBackupDirectory = $targetPostgresBackupDirectory
        Write-Manifest $Manifest
        $targetBackupExit = Invoke-BookshelfUtility $console @(
          '-backup-bookshelf-postgres',
          '-confirm-bookshelf-service-stopped',
          '-bookshelf-postgres-log-cache-only',
          "-bookshelf-postgres-backup=$targetPostgresBackupDirectory",
          "-data=$dataDir"
        ) $environmentEntries
        if ($targetBackupExit -ne 0) { throw "The current ChaptarrNG PostgreSQL log/cache data could not be preserved (exit $targetBackupExit)." }
        $targetPostgresBackupFiles = @(Get-ChildItem -LiteralPath $targetPostgresBackupDirectory -File -ErrorAction Stop)
        if ($targetPostgresBackupFiles.Count -eq 0) {
          Remove-Item -LiteralPath $targetPostgresBackupDirectory -Force
          $Manifest.TargetPostgresBackupDirectory = $null
          Write-Manifest $Manifest
        } else {
          Write-SharedMigrationJournal -DataDirectory $dataDir -FingerprintName 'rollback-target-postgres-log-cache' -FingerprintPath $targetPostgresBackupDirectory
          Write-SharedMigrationJournal -DataDirectory $dataDir -OutcomeName 'rollback-target-postgres-log-cache' -OutcomeValue 'retained'
        }

        $restoreExit = Invoke-BookshelfUtility $console @(
          '-restore-bookshelf-postgres',
          '-confirm-bookshelf-service-stopped',
          "-bookshelf-postgres-backup=$($Manifest.PostgresBackupDirectory)",
          "-data=$dataDir"
        ) $environmentEntries
        if ($restoreExit -ne 0) { throw "The retained PostgreSQL source backup could not be restored (exit $restoreExit)." }
      } elseif ($Manifest.SqliteSourceExisted) {
        $isMerge = ($Manifest.PSObject.Properties.Name -contains 'MergeSqliteSources') -and $Manifest.MergeSqliteSources
        $nativeDatabasePath = Join-Path $dataDir 'chaptarr.db'
        $nativeTargetExists = Test-HasPopulatedSqliteDatabaseFamily $nativeDatabasePath
        $oldBackups = @($Manifest.SqliteBackupNamesBefore)
        $backupPatterns = @('readarr.db.bookshelfng-backup-*.db', 'audioarr.db.bookshelfng-backup-*.db')
        if ($isMerge) {
          $backupPatterns += 'chaptarr.db.bookshelfng-backup-*.db'
        }
        $newBackup = Get-ChildItem -LiteralPath $dataDir -File -ErrorAction SilentlyContinue |
          Where-Object { $name = $_.Name; @($backupPatterns | Where-Object { $name -like $_ }).Count -gt 0 } |
          Where-Object { $_.Name -notin $oldBackups } |
          Sort-Object LastWriteTimeUtc -Descending |
          Select-Object -First 1

        if ($isMerge -and -not $nativeTargetExists) {
          # Before promotion, the merge works only on private snapshots. The
          # original two databases are still the previous service's live data.
        } else {
          if (-not $newBackup) {
            throw 'No new BookshelfNG SQLite source snapshot is available; the current database was left untouched.'
          }

          $restoreExit = Invoke-BookshelfUtility $console @(
            '-restore-bookshelf',
            '-confirm-bookshelf-service-stopped',
            "-bookshelf-backup=$($newBackup.FullName)",
            "-data=$dataDir"
          ) $environmentEntries
          if ($restoreExit -ne 0) { throw "The retained SQLite source backup could not be restored (exit $restoreExit)." }
        }
      }
    }
  }

  $appDirectory = $Manifest.ApplicationDirectory
  $previousDirectory = $Manifest.PreviousApplicationDirectory
  $failedDirectory = "$appDirectory.chaptarrng-failed-$([DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ'))"
  if (Test-Path -LiteralPath $appDirectory) {
    Move-Item -LiteralPath $appDirectory -Destination $failedDirectory -ErrorAction Stop
  }
  if ($Manifest.HadApplication -and (Test-Path -LiteralPath $previousDirectory)) {
    Move-Item -LiteralPath $previousDirectory -Destination $appDirectory -ErrorAction Stop
  } elseif ($Manifest.HadApplication) {
    throw "The previous BookshelfNG application is unavailable at $previousDirectory."
  }

  $nssm = $Manifest.NssmPath
  Set-NssmValue $nssm 'Application' $Manifest.OldApplication
  Set-NssmValue $nssm 'AppDirectory' $Manifest.OldAppDirectory
  Set-NssmValue $nssm 'AppParameters' $Manifest.OldAppParameters
  Restore-Environment $nssm 'AppEnvironment' @($Manifest.OldAppEnvironment)
  Restore-Environment $nssm 'AppEnvironmentExtra' @($Manifest.OldAppEnvironmentExtra)
  Set-NssmValue $nssm 'Start' $Manifest.OldStart

  if ($bookshelfSource) {
    Write-EventLog -LogName Application -Source 'ChaptarrNG BookshelfNG Migration' -EventId 4202 -EntryType Warning -Message 'The deferred BookshelfNG migration failed its startup health check. The original application and source database were restored.' -ErrorAction SilentlyContinue
    $Manifest.State = 'rolled-back'
    Write-SharedMigrationJournal -DataDirectory $dataDir -State 'rolled-back' -OutcomeName 'channel-state' -OutcomeValue 'rolled-back'
  } else {
    Write-EventLog -LogName Application -Source 'ChaptarrNG BookshelfNG Migration' -EventId 4206 -EntryType Error -Message 'A deferred ChaptarrNG update failed its startup health check. The previous application and service settings were restored, but the ChaptarrNG database was preserved and the service remains stopped for compatibility review.' -ErrorAction SilentlyContinue
    $Manifest.State = 'intervention-required'
    Write-SharedMigrationJournal -DataDirectory $dataDir -State 'intervention-required' -OutcomeName 'channel-state' -OutcomeValue 'intervention-required'
  }
  if ($bookshelfSource) {
    $Manifest.LastError = ''
  }
  Write-Manifest $Manifest
  if ($bookshelfSource -and $Manifest.WasRunning) {
    Start-Service -Name 'BookshelfNG' -ErrorAction Stop
  }
  return $Manifest.State
}

try {
  if (-not (Test-Path -LiteralPath $ManifestPath)) {
    & schtasks.exe /Delete /TN $taskName /F 2>$null | Out-Null
    exit 0
  }
  $manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
  if ($manifest.State -eq 'healthy') {
    if ($manifest.PreviousApplicationDirectory -and (Test-Path -LiteralPath $manifest.PreviousApplicationDirectory)) {
      Remove-Item -LiteralPath $manifest.PreviousApplicationDirectory -Recurse -Force -ErrorAction Stop
    }
    & schtasks.exe /Delete /TN $taskName /F 2>$null | Out-Null
    Remove-Item -LiteralPath $ManifestPath -Force -ErrorAction SilentlyContinue
    exit 0
  }
  if ($manifest.State -eq 'rolled-back' -or $manifest.State -eq 'intervention-required') {
    & schtasks.exe /Delete /TN $taskName /F 2>$null | Out-Null
    exit 0
  }
  if ($manifest.State -ne 'pending') {
    Write-EventLog -LogName Application -Source 'ChaptarrNG BookshelfNG Migration' -EventId 4203 -EntryType Error -Message "The deferred BookshelfNG recovery record has an unknown state '$($manifest.State)'; no automatic action was taken." -ErrorAction SilentlyContinue
    exit 2
  }
  Write-SharedMigrationJournal -DataDirectory $manifest.DataDirectory -OutcomeName 'channel' -OutcomeValue 'chocolatey'

  $expectedApplication = [IO.Path]::GetFullPath($manifest.NewApplication)
  $currentApplication = (& $manifest.NssmPath get BookshelfNG Application | Out-String).Trim().Trim('"')
  if ($LASTEXITCODE -ne 0) { throw 'NSSM could not verify the running BookshelfNG application.' }
  if (-not [string]::Equals(
        [IO.Path]::GetFullPath($currentApplication),
        $expectedApplication,
        [StringComparison]::OrdinalIgnoreCase)) {
    Write-SharedMigrationJournal -DataDirectory $manifest.DataDirectory -State 'rolled-back' -OutcomeName 'channel-state' -OutcomeValue 'rolled-back'
    $manifest.State = 'rolled-back'
    $manifest.LastError = 'The service still points to the original application; no recovery action was needed.'
    Write-Manifest $manifest
    & schtasks.exe /Delete /TN $taskName /F 2>$null | Out-Null
    exit 0
  }

  $healthTimeoutSeconds = 14700
  if ($null -ne $manifest.HealthTimeoutSeconds) {
    $healthTimeoutSeconds = 0
    if (-not [int]::TryParse([string] $manifest.HealthTimeoutSeconds, [Globalization.NumberStyles]::None, [Globalization.CultureInfo]::InvariantCulture, [ref] $healthTimeoutSeconds) -or
        $healthTimeoutSeconds -lt 1 -or $healthTimeoutSeconds -gt 14700) {
      throw 'The retained Windows migration record has an invalid health timeout.'
    }
  }
  $deadline = [DateTime]::UtcNow.AddSeconds($healthTimeoutSeconds)
  while ([DateTime]::UtcNow -lt $deadline) {
    $service = Get-Service -Name 'BookshelfNG' -ErrorAction SilentlyContinue
    if (-not $service -or $service.Status -eq 'Stopped') {
      throw 'The ChaptarrNG service exited before the migrated database passed its health check.'
    }

    $uri = $manifest.HealthUrl
    try {
      $response = Invoke-WebRequest -Uri $uri -UseBasicParsing -TimeoutSec 2 -MaximumRedirection 0
      if ($response.StatusCode -eq 200) {
        $healthExit = Invoke-BookshelfUtility $manifest.ConsolePath @(
          '-check-bookshelf-migration',
          "-data=$($manifest.DataDirectory)"
        ) @($manifest.MigrationEnvironment)
        if ($healthExit -eq 0) {
          Write-SharedMigrationJournal -DataDirectory $manifest.DataDirectory -State 'healthy' -OutcomeName 'channel-state' -OutcomeValue 'healthy'
          $manifest.State = 'healthy'
          $manifest.LastError = ''
          Write-Manifest $manifest
          try {
            if (Test-Path -LiteralPath $manifest.PreviousApplicationDirectory) {
              Remove-Item -LiteralPath $manifest.PreviousApplicationDirectory -Recurse -Force -ErrorAction Stop
            }
            Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
            Remove-Item -LiteralPath $ManifestPath -Force -ErrorAction SilentlyContinue
          } catch {
            Write-EventLog -LogName Application -Source 'ChaptarrNG BookshelfNG Migration' -EventId 4204 -EntryType Warning -Message "BookshelfNG migration passed its health check; previous application cleanup remains at $($manifest.PreviousApplicationDirectory)." -ErrorAction SilentlyContinue
          }
          exit 0
        }
      }
    } catch {
      # Startup can take longer than the service-control transition; keep waiting.
    }
    Start-Sleep -Seconds 5
  }

  throw "ChaptarrNG did not validate the migrated database and provider profile within $healthTimeoutSeconds seconds at $($manifest.HealthUrl)."
} catch {
  $manifest = if ($manifest) { $manifest } else { $null }
  if (-not $manifest) {
    try { $manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json } catch { exit 1 }
  }

  $manifest.State = 'intervention-required'
  $manifest.LastError = $_.Exception.Message
  Write-SharedMigrationJournal -DataDirectory $manifest.DataDirectory -State 'intervention-required' -OutcomeName 'channel-state' -OutcomeValue 'intervention-required'
  Write-Manifest $manifest
  try {
    $rollbackState = Restore-PreviousBookshelf $manifest
  } catch {
    $manifest.State = 'intervention-required'
    $manifest.LastError = $_.Exception.Message
    Write-Manifest $manifest
    Write-EventLog -LogName Application -Source 'ChaptarrNG BookshelfNG Migration' -EventId 4203 -EntryType Error -Message "Automatic BookshelfNG rollback needs operator recovery: $($_.Exception.Message)" -ErrorAction SilentlyContinue
    exit 1
  }

  & schtasks.exe /Delete /TN $taskName /F 2>$null | Out-Null
  $manifest.State = $rollbackState
  if ($rollbackState -eq 'rolled-back') {
    $manifest.LastError = ''
  }
  Write-Manifest $manifest
  if ($rollbackState -ne 'rolled-back') { exit 2 }
}
