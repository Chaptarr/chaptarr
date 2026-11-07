param(
    [Parameter(Mandatory = $true)]
    [string] $BackupPath,

    [Parameter(Mandatory = $true)]
    [string] $DataDirectory,

    [string] $ChaptarrConsolePath,

    [string] $BookshelfServiceAccount = 'NT AUTHORITY\LocalService',

    [switch] $RestorePostgresDatabase,

    [int] $BookshelfServiceStartType = 0,

    [int] $BookshelfServiceDelayedAutoStart = -1,

    [int] $BookshelfServiceWasRunning = -1
)

$ErrorActionPreference = 'Stop'
$BackupPath = [IO.Path]::GetFullPath($BackupPath)
$DataDirectory = [IO.Path]::GetFullPath($DataDirectory)
$backupBin = Join-Path $BackupPath 'bin'
$backupReadarrConsole = Join-Path $backupBin 'Readarr.Console.exe'
$postgresDump = Join-Path $BackupPath 'postgres-main.dump'
$migrationRecordPath = 'HKLM:\SOFTWARE\ChaptarrNG\BookshelfMigration'
$migrationRecord = Get-ItemProperty -LiteralPath $migrationRecordPath -ErrorAction SilentlyContinue
$readarrServiceKeyPath = 'HKLM:\SYSTEM\CurrentControlSet\Services\Readarr'
$existingReadarrService = Get-ItemProperty -LiteralPath $readarrServiceKeyPath -ErrorAction SilentlyContinue
$savedStartType = if ($BookshelfServiceStartType -in @(2, 3, 4)) {
    $BookshelfServiceStartType
} elseif ($null -ne $migrationRecord -and $null -ne $migrationRecord.BookshelfServiceStartType) {
    [int] $migrationRecord.BookshelfServiceStartType
} else {
    2
}
$savedDelayedAutoStart = if ($BookshelfServiceDelayedAutoStart -in @(0, 1)) {
    $BookshelfServiceDelayedAutoStart -ne 0
} elseif ($null -ne $migrationRecord -and $null -ne $migrationRecord.BookshelfServiceDelayedAutoStart) {
    [int] $migrationRecord.BookshelfServiceDelayedAutoStart -ne 0
} elseif ($null -ne $existingReadarrService -and $null -ne $existingReadarrService.DelayedAutoStart) {
    [int] $existingReadarrService.DelayedAutoStart -ne 0
} else {
    $false
}

if ([string]::IsNullOrWhiteSpace($BookshelfServiceAccount)) {
    $BookshelfServiceAccount = 'NT AUTHORITY\LocalService'
}
if ($BookshelfServiceAccount.StartsWith('NT SERVICE\', [StringComparison]::OrdinalIgnoreCase)) {
    throw "The Readarr service uses service-specific virtual account '$BookshelfServiceAccount', which cannot be reused by the Chaptarr service."
}

function Normalize-ServiceAccount([string] $Account) {
    $value = $Account.Trim()
    if ($value -in @('LocalSystem', 'NT AUTHORITY\LocalSystem', 'NT AUTHORITY\SYSTEM')) { return 'LocalSystem' }
    if ($value -in @('LocalService', 'NT AUTHORITY\LocalService')) { return 'NT AUTHORITY\LocalService' }
    if ($value -in @('NetworkService', 'NT AUTHORITY\NetworkService')) { return 'NT AUTHORITY\NetworkService' }
    return $value
}

function Test-ServiceAccountNeedsPassword([string] $Account) {
    $normalized = Normalize-ServiceAccount $Account
    if ($normalized -in @('LocalSystem', 'NT AUTHORITY\LocalService', 'NT AUTHORITY\NetworkService')) { return $false }
    return -not $normalized.EndsWith('$')
}

function Read-ServiceAccountPassword {
    $inputStream = [Console]::OpenStandardInput()
    $lengthBytes = New-Object byte[] 4
    $offset = 0
    while ($offset -lt 4) {
        $read = $inputStream.Read($lengthBytes, $offset, 4 - $offset)
        if ($read -le 0) { throw 'The secure service-account password input was not provided.' }
        $offset += $read
    }

    $characterCount = [BitConverter]::ToInt32($lengthBytes, 0)
    if ($characterCount -lt 1 -or $characterCount -gt 4096) {
        throw 'The secure service-account password input has an invalid length.'
    }

    $secretBytes = New-Object byte[] ($characterCount * 2)
    $offset = 0
    while ($offset -lt $secretBytes.Length) {
        $read = $inputStream.Read($secretBytes, $offset, $secretBytes.Length - $offset)
        if ($read -le 0) { throw 'The secure service-account password input was incomplete.' }
        $offset += $read
    }

    $securePassword = New-Object System.Security.SecureString
    try {
        for ($index = 0; $index -lt $characterCount; $index++) {
            $securePassword.AppendChar([BitConverter]::ToChar($secretBytes, $index * 2))
        }
        $securePassword.MakeReadOnly()
        return $securePassword
    } finally {
        [Array]::Clear($secretBytes, 0, $secretBytes.Length)
        [Array]::Clear($lengthBytes, 0, $lengthBytes.Length)
    }
}

$existingReadarrAccount = if ($existingReadarrService) { [string] $existingReadarrService.ObjectName } else { $null }
$serviceAccountPassword = $null
trap {
    $rollbackError = $_
    if ($null -ne $serviceAccountPassword) {
        try { $serviceAccountPassword.Dispose() }
        catch { }
        $serviceAccountPassword = $null
    }
    $errorType = $rollbackError.Exception.GetType().Name
    $errorLine = $rollbackError.InvocationInfo.ScriptLineNumber
    $errorMessage = $rollbackError.Exception.Message
    [Console]::Error.WriteLine(('[rollback] {0} at rollback.ps1:{1}: {2}' -f $errorType, $errorLine, $errorMessage))
    exit 1
}
if (Test-ServiceAccountNeedsPassword $BookshelfServiceAccount) {
    if (-not $existingReadarrService -or
        (Normalize-ServiceAccount $existingReadarrAccount) -ne (Normalize-ServiceAccount $BookshelfServiceAccount)) {
        $serviceAccountPassword = Read-ServiceAccountPassword
    }
}

if (-not ('ChaptarrServiceAccountNative' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Security;

public static class ChaptarrServiceAccountNative
{
    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceChangeConfig = 0x0002;
    private const uint ServiceNoChange = 0xFFFFFFFF;

    [DllImport("advapi32.dll", EntryPoint = "OpenSCManagerW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenServiceControlManager(string machineName, string databaseName, uint access);

    [DllImport("advapi32.dll", EntryPoint = "OpenServiceW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenService(IntPtr manager, string serviceName, uint access);

    [DllImport("advapi32.dll", EntryPoint = "ChangeServiceConfigW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ChangeServiceConfig(IntPtr service, uint serviceType, uint startType, uint errorControl,
        string binaryPath, string loadOrderGroup, IntPtr tagId, string dependencies, string serviceStartName,
        IntPtr password, string displayName);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr handle);

    private static InvalidOperationException NativeFailure(string operation)
    {
        return new InvalidOperationException(operation + " failed with Win32 error " + Marshal.GetLastWin32Error() + ".");
    }

    public static void SetAccount(string serviceName, string account, SecureString password)
    {
        IntPtr manager = OpenServiceControlManager(null, null, ScManagerConnect);
        if (manager == IntPtr.Zero) throw NativeFailure("OpenSCManagerW");
        IntPtr service = IntPtr.Zero;
        IntPtr passwordPointer = IntPtr.Zero;
        SecureString emptyPassword = null;
        try
        {
            service = OpenService(manager, serviceName, ServiceChangeConfig);
            if (service == IntPtr.Zero) throw NativeFailure("OpenServiceW");
            if (password != null) passwordPointer = Marshal.SecureStringToGlobalAllocUnicode(password);
            else if (account.Equals("LocalSystem", StringComparison.OrdinalIgnoreCase) ||
                     account.Equals("NT AUTHORITY\\LocalSystem", StringComparison.OrdinalIgnoreCase) ||
                     account.Equals("NT AUTHORITY\\SYSTEM", StringComparison.OrdinalIgnoreCase) ||
                     account.Equals("LocalService", StringComparison.OrdinalIgnoreCase) ||
                     account.Equals("NT AUTHORITY\\LocalService", StringComparison.OrdinalIgnoreCase) ||
                     account.Equals("NetworkService", StringComparison.OrdinalIgnoreCase) ||
                     account.Equals("NT AUTHORITY\\NetworkService", StringComparison.OrdinalIgnoreCase))
            {
                emptyPassword = new SecureString();
                passwordPointer = Marshal.SecureStringToGlobalAllocUnicode(emptyPassword);
            }
            if (!ChangeServiceConfig(service, ServiceNoChange, ServiceNoChange, ServiceNoChange,
                    null, null, IntPtr.Zero, null, account, passwordPointer, null))
                throw NativeFailure("ChangeServiceConfigW");
        }
        finally
        {
            if (passwordPointer != IntPtr.Zero) Marshal.ZeroFreeGlobalAllocUnicode(passwordPointer);
            if (emptyPassword != null) emptyPassword.Dispose();
            if (service != IntPtr.Zero) CloseServiceHandle(service);
            CloseServiceHandle(manager);
        }
    }
}
'@
}
$bookshelfWasRunning = if ($BookshelfServiceWasRunning -in @(0, 1)) {
    $BookshelfServiceWasRunning -ne 0
} elseif ($null -ne $migrationRecord -and $null -ne $migrationRecord.BookshelfServiceWasRunning) {
    [int] $migrationRecord.BookshelfServiceWasRunning -ne 0
} else {
    $true
}
$startTypeName = switch ($savedStartType) {
    2 { 'auto' }
    3 { 'demand' }
    4 { 'disabled' }
    default { throw "The saved BookshelfNG service start type '$savedStartType' is unsupported." }
}

$sqliteBackupNames = @(
    @('readarr.db', 'audioarr.db') | Where-Object {
        Test-Path -LiteralPath (Join-Path $BackupPath $_) -PathType Leaf
    }
)
$hasPostgresBackup = Test-Path -LiteralPath $postgresDump -PathType Leaf
$hasSqliteBackup = $sqliteBackupNames.Count -gt 0
$hasMergedSqliteBackup = -not $hasPostgresBackup -and
    $sqliteBackupNames -contains 'readarr.db' -and $sqliteBackupNames -contains 'audioarr.db'

if (-not (Test-Path -LiteralPath $backupBin -PathType Container) -or
    -not (Test-Path -LiteralPath $backupReadarrConsole -PathType Leaf) -or
    (-not $hasSqliteBackup -and -not $hasPostgresBackup)) {
    throw "The BookshelfNG rollback backup is incomplete: $BackupPath"
}
if ($RestorePostgresDatabase -and -not (Test-Path -LiteralPath $postgresDump -PathType Leaf)) {
    throw "The independent PostgreSQL main-database backup is missing: $postgresDump"
}

function Stop-And-DeleteService([string] $Name) {
    $service = Get-Service -Name $Name -ErrorAction SilentlyContinue
    if (-not $service) { return }

    $serviceProcessId = [int] (Get-CimInstance -ClassName Win32_Service -Filter "Name = '$Name'" -ErrorAction Stop).ProcessId

    try {
        Write-Host "Stopping $Name service..."
        if ($service.Status -ne 'Stopped') {
            $service.Stop()
            $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
        }

        if ($serviceProcessId -gt 0) {
            $serviceProcess = Get-Process -Id $serviceProcessId -ErrorAction SilentlyContinue
            if ($serviceProcess) {
                try {
                    if (-not $serviceProcess.WaitForExit(15000)) {
                        throw "$Name service process $serviceProcessId did not exit after the service reported STOPPED."
                    }
                } finally {
                    $serviceProcess.Dispose()
                }
            }
        }
    } finally {
        # Release this exact ServiceController before asking SCM to delete it.
        $service.Dispose()
        $service = $null
    }

    Write-Host "Removing $Name service registration..."
    & sc.exe delete $Name | Out-Null
    if ($LASTEXITCODE -ne 0 -and $LASTEXITCODE -ne 1072) {
        throw "sc.exe delete $Name failed with exit code $LASTEXITCODE."
    }
    if ($LASTEXITCODE -eq 1072) {
        Write-Host "$Name is already marked for deletion; waiting for Windows to release it..."
    }

    $serviceKeyPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$Name"
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    # Query the registry while deletion is pending. Get-Service opens an SCM
    # handle, which can keep the marked-for-deletion registration alive.
    while ((Test-Path -LiteralPath $serviceKeyPath) -and [DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 500
    }
    if (Test-Path -LiteralPath $serviceKeyPath) {
        throw "$Name is still registered after a delete request."
    }
}

function Stop-ServiceOnly([string] $Name) {
    $service = Get-Service -Name $Name -ErrorAction SilentlyContinue
    if (-not $service) { return }

    $serviceProcessId = 0
    $serviceInfo = Get-CimInstance -ClassName Win32_Service -Filter "Name = '$Name'" -ErrorAction SilentlyContinue
    if ($serviceInfo) {
        $serviceProcessId = [int] $serviceInfo.ProcessId
        $serviceInfo = $null
    }

    try {
        if ($service.Status -ne 'Stopped') {
            Write-Host "Stopping $Name service while preserving its registration..."
            if ($service.Status -ne 'StopPending') { $service.Stop() }
            $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
        }
    } finally {
        $service.Dispose()
        $service = $null
    }

    if ($serviceProcessId -gt 0) {
        $serviceProcess = Get-Process -Id $serviceProcessId -ErrorAction SilentlyContinue
        if ($serviceProcess) {
            try {
                if (-not $serviceProcess.WaitForExit(15000)) {
                    throw "$Name service process $serviceProcessId did not exit after reporting STOPPED."
                }
            } finally {
                $serviceProcess.Dispose()
                $serviceProcess = $null
            }
        }
    }
}

function Assert-ServiceTargetsDataDirectory([string] $Name) {
    $keyPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$Name"
    $serviceKey = Get-ItemProperty -Path $keyPath -ErrorAction SilentlyContinue
    if (-not $serviceKey) { return }

    $imagePath = [Environment]::ExpandEnvironmentVariables([string] $serviceKey.ImagePath)
    if ($imagePath.IndexOf($DataDirectory, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "Refusing to remove $Name because its service command does not target $DataDirectory."
    }
}

Write-Host "Restoring BookshelfNG from $BackupPath"
Assert-ServiceTargetsDataDirectory 'Chaptarr'
Assert-ServiceTargetsDataDirectory 'Readarr'
Stop-And-DeleteService 'Chaptarr'
Stop-ServiceOnly 'Readarr'

if ($RestorePostgresDatabase) {
    if ([string]::IsNullOrWhiteSpace($ChaptarrConsolePath)) {
        $ChaptarrConsolePath = Join-Path $DataDirectory 'bin\Chaptarr.Console.exe'
    }
    if (-not (Test-Path -LiteralPath $ChaptarrConsolePath -PathType Leaf)) {
        throw "The ChaptarrNG utility needed to restore the PostgreSQL backup was not found: $ChaptarrConsolePath"
    }

    Write-Host 'Restoring the independent PostgreSQL database backups before restoring BookshelfNG binaries...'
    & $ChaptarrConsolePath '-preview-bookshelf-postgres-restore' "-bookshelf-postgres-backup=$BackupPath" "-data=$DataDirectory" '-confirm-bookshelf-service-stopped'
    if ($LASTEXITCODE -ne 0) {
        throw "ChaptarrNG PostgreSQL rollback preflight exited with code $LASTEXITCODE. The database was not changed."
    }

    $targetPostgresBackupDirectory = Join-Path $BackupPath ("target-chaptarr-log-cache-{0}" -f [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ'))
    if (Test-Path -LiteralPath $targetPostgresBackupDirectory) {
        throw "A ChaptarrNG PostgreSQL log/cache backup path already exists at $targetPostgresBackupDirectory; inspect it before retrying."
    }
    New-Item -ItemType Directory -Path $targetPostgresBackupDirectory | Out-Null
    & $ChaptarrConsolePath '-backup-bookshelf-postgres' '-bookshelf-postgres-log-cache-only' "-bookshelf-postgres-backup=$targetPostgresBackupDirectory" "-data=$DataDirectory" '-confirm-bookshelf-service-stopped'
    if ($LASTEXITCODE -ne 0) {
        throw "ChaptarrNG PostgreSQL log/cache backup exited with code $LASTEXITCODE. The source database was not restored."
    }
    if (-not (Get-ChildItem -LiteralPath $targetPostgresBackupDirectory -File -ErrorAction SilentlyContinue | Select-Object -First 1)) {
        Remove-Item -LiteralPath $targetPostgresBackupDirectory -Force
    }

    & $ChaptarrConsolePath '-restore-bookshelf-postgres' "-bookshelf-postgres-backup=$BackupPath" "-data=$BackupPath" '-confirm-bookshelf-service-stopped'
    if ($LASTEXITCODE -ne 0) {
        throw "ChaptarrNG PostgreSQL backup restore exited with code $LASTEXITCODE. The dump is preserved at $postgresDump."
    }
}

$currentBin = Join-Path $DataDirectory 'bin'
if ($hasMergedSqliteBackup) {
    $mergeReceipt = Join-Path $DataDirectory 'bookshelfng-merge.receipt'
    $nativeDatabase = Join-Path $DataDirectory 'chaptarr.db'
    if ((Test-Path -LiteralPath $mergeReceipt -PathType Leaf) -and
        (Test-Path -LiteralPath $nativeDatabase -PathType Leaf) -and
        (Get-Item -LiteralPath $nativeDatabase -Force).Length -gt 0) {
        if ([string]::IsNullOrWhiteSpace($ChaptarrConsolePath) -or
            -not (Test-Path -LiteralPath $ChaptarrConsolePath -PathType Leaf)) {
            throw 'A merged ChaptarrNG database is present, but its migration utility is unavailable to retire the target safely.'
        }

        Write-Host 'Retiring the merged ChaptarrNG target while keeping both original BookshelfNG databases available...'
        & $ChaptarrConsolePath '-check-bookshelf-migration' "-data=$DataDirectory" '-exitimmediately'
        if ($LASTEXITCODE -ne 0) {
            throw 'The merged ChaptarrNG target or its source fingerprint receipt failed validation; the BookshelfNG databases were not restored over it.'
        }

        & $ChaptarrConsolePath '-restore-bookshelf' '-bookshelf-backup=auto' '-confirm-bookshelf-service-stopped' "-data=$DataDirectory" '-exitimmediately'
        if ($LASTEXITCODE -ne 0) {
            throw "The merged ChaptarrNG target could not be retired safely (exit $LASTEXITCODE)."
        }
    }
}

if (Test-Path -LiteralPath $currentBin) {
    Remove-Item -LiteralPath $currentBin -Recurse -Force
}
Copy-Item -LiteralPath $backupBin -Destination $currentBin -Recurse -Force
$readarrConsole = Join-Path $currentBin 'Readarr.Console.exe'
if (-not (Test-Path -LiteralPath $readarrConsole -PathType Leaf)) {
    throw "The restored BookshelfNG executable is missing from $currentBin."
}

$restoreFileNames = if ($hasSqliteBackup) {
    $names = @('config.xml')
    foreach ($databaseName in $sqliteBackupNames) {
        $names += @($databaseName, "$databaseName-journal", "$databaseName-wal")
    }
    $names
} else {
    @('config.xml')
}
foreach ($name in $restoreFileNames) {
    $current = Join-Path $DataDirectory $name
    $source = Join-Path $BackupPath $name
    if (Test-Path -LiteralPath $current) {
        Remove-Item -LiteralPath $current -Force
    }
    if (Test-Path -LiteralPath $source -PathType Leaf) {
        Copy-Item -LiteralPath $source -Destination $current -Force
    }
}

$currentBackups = Join-Path $DataDirectory 'Backups'
$savedBackups = Join-Path $BackupPath 'Backups'
if (Test-Path -LiteralPath $currentBackups) {
    Remove-Item -LiteralPath $currentBackups -Recurse -Force
}
if (Test-Path -LiteralPath $savedBackups -PathType Container) {
    Copy-Item -LiteralPath $savedBackups -Destination $currentBackups -Recurse -Force
}

if (-not (Test-Path -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Services\Readarr')) {
    Write-Host 'Re-registering the restored Readarr service...'
    # The deferred successful-handoff cleanup may already have removed the old
    # registration. Recreate it with the same service identity and saved policy.
    $imagePathArgument = '"\"' + $readarrConsole + '\" -data=\"' + $DataDirectory + '\" /nobrowser"'
    $createArguments = "create Readarr DisplayName= `"Readarr`" binpath= $imagePathArgument start= $startTypeName depend= EventLog/Tcpip/http obj= `"NT AUTHORITY\LocalService`""
    $createProcess = Start-Process -FilePath 'sc.exe' -ArgumentList $createArguments -Wait -PassThru -NoNewWindow
    if ($createProcess.ExitCode -ne 0) {
        throw "sc.exe could not create the restored Readarr service (exit code $($createProcess.ExitCode))."
    }

    $descriptionProcess = Start-Process -FilePath 'sc.exe' -ArgumentList 'description Readarr "Readarr Application Server"' -Wait -PassThru -NoNewWindow
    if ($descriptionProcess.ExitCode -ne 0) {
        throw "sc.exe could not set the restored Readarr service description (exit code $($descriptionProcess.ExitCode))."
    }

    $daclLines = & sc.exe sdshow Readarr
    if ($LASTEXITCODE -ne 0) {
        throw "sc.exe could not read the restored Readarr service permissions (exit code $LASTEXITCODE)."
    }
    $serviceDacl = ($daclLines | Where-Object { $_ -match '^D:' } | Select-Object -First 1).Trim()
    if ([string]::IsNullOrWhiteSpace($serviceDacl)) {
        throw 'The restored Readarr service returned an invalid security descriptor.'
    }

    $authenticatedUsersDacl = '(A;;CCLCSWRPWPLOCRRC;;;AU)'
    if (-not $serviceDacl.Contains($authenticatedUsersDacl)) {
        $indexOfS = $serviceDacl.IndexOf('S:', [StringComparison]::OrdinalIgnoreCase)
        $serviceDacl = if ($indexOfS -eq -1) {
            "$serviceDacl$authenticatedUsersDacl"
        } else {
            $serviceDacl.Insert($indexOfS, $authenticatedUsersDacl)
        }

        $permissionsProcess = Start-Process -FilePath 'sc.exe' -ArgumentList "sdset Readarr $serviceDacl" -Wait -PassThru -NoNewWindow
        if ($permissionsProcess.ExitCode -ne 0) {
            throw "sc.exe could not set the restored Readarr service permissions (exit code $($permissionsProcess.ExitCode))."
        }
    }
}

$savedServiceEnvironmentPath = Join-Path $BackupPath 'bookshelf-service-environment.json'
if (Test-Path -LiteralPath $savedServiceEnvironmentPath -PathType Leaf) {
    $savedServiceEnvironment = Get-Content -LiteralPath $savedServiceEnvironmentPath -Raw | ConvertFrom-Json
    $serviceEnvironmentValues = @(
        $savedServiceEnvironment.PSObject.Properties |
            ForEach-Object { '{0}={1}' -f $_.Name, [string] $_.Value }
    )
    if ($serviceEnvironmentValues.Count -gt 0) {
        New-ItemProperty -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Services\Readarr' `
            -Name Environment -PropertyType MultiString -Value ([string[]] $serviceEnvironmentValues) -Force | Out-Null
    } else {
        Remove-ItemProperty -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Services\Readarr' `
            -Name Environment -ErrorAction SilentlyContinue
    }
    Write-Host 'Restored the original BookshelfNG service environment from the restricted migration backup.'
}
New-ItemProperty -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Services\Readarr' `
    -Name DelayedAutoStart -PropertyType DWord -Value ([int] $savedDelayedAutoStart) -Force | Out-Null

$readarrServiceKey = 'HKLM:\SYSTEM\CurrentControlSet\Services\Readarr'
$serviceKey = Get-ItemProperty -Path $readarrServiceKey -ErrorAction Stop
if ([Environment]::ExpandEnvironmentVariables([string] $serviceKey.ImagePath).IndexOf($DataDirectory, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
    throw 'The restored Readarr service command does not target the restored data directory.'
}

$serviceAccount = [string] $serviceKey.ObjectName
if ((Normalize-ServiceAccount $serviceAccount) -ne (Normalize-ServiceAccount $BookshelfServiceAccount)) {
    Write-Host "Restoring the original Readarr service identity '$BookshelfServiceAccount'..."
    [ChaptarrServiceAccountNative]::SetAccount('Readarr', $BookshelfServiceAccount, $serviceAccountPassword)
    $serviceKey = Get-ItemProperty -Path $readarrServiceKey -ErrorAction Stop
    $serviceAccount = [string] $serviceKey.ObjectName
}
if ((Normalize-ServiceAccount $serviceAccount) -ne (Normalize-ServiceAccount $BookshelfServiceAccount)) {
    throw "The restored Readarr service uses '$serviceAccount' instead of the saved BookshelfNG identity '$BookshelfServiceAccount'."
}
if ($null -ne $serviceAccountPassword) {
    $serviceAccountPassword.Dispose()
    $serviceAccountPassword = $null
}

$restoreStart = Start-Process -FilePath 'sc.exe' -ArgumentList "config Readarr start= $startTypeName" -Wait -PassThru -NoNewWindow
if ($restoreStart.ExitCode -ne 0) {
    throw "sc.exe could not restore the Readarr startup policy (exit code $($restoreStart.ExitCode))."
}

if ($bookshelfWasRunning) {
    if ($savedStartType -eq 4) {
        $temporaryStart = Start-Process -FilePath 'sc.exe' -ArgumentList 'config Readarr start= demand' -Wait -PassThru -NoNewWindow
        if ($temporaryStart.ExitCode -ne 0) {
            throw "Could not temporarily enable the previously running Readarr service (exit code $($temporaryStart.ExitCode))."
        }
    }

    Write-Host 'Starting the restored BookshelfNG service...'
    Start-Service -Name 'Readarr' -ErrorAction Stop
    $service = Get-Service -Name 'Readarr' -ErrorAction Stop
    try {
        $service.WaitForStatus('Running', [TimeSpan]::FromSeconds(60))
        if ($service.Status -ne 'Running') {
            throw 'The restored Readarr service did not reach RUNNING.'
        }
    } finally {
        $service.Dispose()
        $service = $null
    }

    if ($savedStartType -eq 4) {
        $disableProcess = Start-Process -FilePath 'sc.exe' -ArgumentList 'config Readarr start= disabled' -Wait -PassThru -NoNewWindow
        if ($disableProcess.ExitCode -ne 0) {
            throw "The restored running Readarr service could not recover its disabled startup policy (exit code $($disableProcess.ExitCode))."
        }
    }
} else {
    Write-Host "BookshelfNG was stopped before migration; leaving Readarr stopped with its original '$startTypeName' startup policy."
}

try {
    Set-ItemProperty -Path 'HKLM:\SOFTWARE\ChaptarrNG\BookshelfMigration' -Name LastRollbackTime -Value (Get-Date -Format 'o') -ErrorAction Stop
} catch {
    Write-Warning "Could not record the rollback timestamp: $($_.Exception.Message)"
}

Write-Host 'BookshelfNG rollback completed; the Readarr registration and original startup state were restored.'
