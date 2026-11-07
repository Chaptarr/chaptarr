param(
  [string] $InstallScriptPath = (Join-Path $PSScriptRoot '../legacy/chocolatey/tools/chocolateyinstall.ps1.in')
)

$ErrorActionPreference = 'Stop'

$installScript = Get-Content -LiteralPath $InstallScriptPath -Raw
$functionMatch = [regex]::Match(
  $installScript,
  '(?ms)^function Confirm-BookshelfMigrationChoice \{\r?\n.*?^\}')
if (-not $functionMatch.Success) {
  throw 'Could not find Confirm-BookshelfMigrationChoice in the Chocolatey install script.'
}
. ([scriptblock]::Create($functionMatch.Value))

$parseTokens = $null
$parseErrors = $null
$installAst = [System.Management.Automation.Language.Parser]::ParseInput($installScript, [ref] $parseTokens, [ref] $parseErrors)
if ($parseErrors.Count -gt 0) {
  throw 'The Chocolatey install script contains PowerShell parse errors.'
}
$utilityFunction = $installAst.Find({
    param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
      $node.Name -eq 'Invoke-BookshelfUtility'
  }, $true)
if (-not $utilityFunction) {
  throw 'Could not find Invoke-BookshelfUtility in the Chocolatey install template.'
}
. ([scriptblock]::Create($utilityFunction.Extent.Text))
$fileHashFunction = $installAst.Find({
    param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
      $node.Name -eq 'Get-BookshelfFileSha256'
  }, $true)
if (-not $fileHashFunction) {
  throw 'Could not find Get-BookshelfFileSha256 in the Chocolatey install template.'
}
. ([scriptblock]::Create($fileHashFunction.Extent.Text))
$journalFingerprintFunction = $installAst.Find({
    param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
      $node.Name -eq 'Get-BookshelfJournalFingerprint'
  }, $true)
if (-not $journalFingerprintFunction) {
  throw 'Could not find Get-BookshelfJournalFingerprint in the Chocolatey install template.'
}
. ([scriptblock]::Create($journalFingerprintFunction.Extent.Text))
$journalWriterFunction = $installAst.Find({
    param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
      $node.Name -eq 'Write-SharedMigrationJournal'
  }, $true)
if (-not $journalWriterFunction) {
  throw 'Could not find Write-SharedMigrationJournal in the Chocolatey install template.'
}
. ([scriptblock]::Create($journalWriterFunction.Extent.Text))

function Assert-Condition {
  param([bool] $Condition, [string] $Message)
  if (-not $Condition) { throw $Message }
}

$installStatements = @($installAst.EndBlock.Statements)
$parserStartIndex = -1
$parserEndIndex = -1
for ($index = 0; $index -lt $installStatements.Count; $index++) {
  $statement = $installStatements[$index]
  if ($parserStartIndex -lt 0 -and
      $statement -is [System.Management.Automation.Language.AssignmentStatementAst] -and
      $statement.Left -is [System.Management.Automation.Language.VariableExpressionAst] -and
      $statement.Left.VariablePath.UserPath -eq 'packageParameters') {
    $parserStartIndex = $index
    continue
  }
  if ($parserStartIndex -ge 0 -and
      $statement -is [System.Management.Automation.Language.IfStatementAst] -and
      $statement.Extent.Text -match '(?s)\$packageParameters\s+-split') {
    $parserEndIndex = $index
    break
  }
}
if ($parserStartIndex -lt 0 -or $parserEndIndex -lt $parserStartIndex) {
  throw 'Could not isolate the packaged Chocolatey parameter parser.'
}
$parserStart = $installStatements[$parserStartIndex].Extent.StartOffset
$parserEnd = $installStatements[$parserEndIndex].Extent.EndOffset
$script:packageParameterParserText = $installScript.Substring($parserStart, $parserEnd - $parserStart) +
  [Environment]::NewLine +
  '[pscustomobject]@{ ProviderMode = $requestedProviderMode; MigrationMode = $requestedMigrationMode }'
$choiceCalls = @($installAst.FindAll({
      param($node)
      $node -is [System.Management.Automation.Language.CommandAst] -and
        $node.GetCommandName() -eq 'Confirm-BookshelfMigrationChoice'
    }, $true))
if ($choiceCalls.Count -ne 1 -or
    $choiceCalls[0].Extent.Text -notmatch '(?is)-HasMigrationData\s+\$hasMigrationDataForChoice.*-Mode\s+\$requestedMigrationMode') {
  throw 'The packaged Chocolatey install path does not wire parsed package parameters into the migration choice.'
}

function Invoke-PackageParameterParser {
  param([AllowNull()][string] $Parameters)
  $previousParameters = [Environment]::GetEnvironmentVariable('ChocolateyPackageParameters', 'Process')
  try {
    [Environment]::SetEnvironmentVariable('ChocolateyPackageParameters', $Parameters, 'Process')
    return & ([scriptblock]::Create($script:packageParameterParserText))
  } finally {
    [Environment]::SetEnvironmentVariable('ChocolateyPackageParameters', $previousParameters, 'Process')
  }
}

$defaultModes = Invoke-PackageParameterParser ''
Assert-Condition ($defaultModes.MigrationMode -eq 'automatic') 'The packaged parser did not preserve automatic migration as the default.'
$askModes = Invoke-PackageParameterParser '/MigrationMode=ask'
Assert-Condition ($askModes.MigrationMode -eq 'ask') 'The packaged parser did not accept MigrationMode=ask.'
$manualModes = Invoke-PackageParameterParser '/ProviderMode=Goodreads /MigrationMode=manual'
Assert-Condition ($manualModes.ProviderMode -eq 'Goodreads' -and $manualModes.MigrationMode -eq 'manual') `
  'The packaged parser did not preserve the selected provider and manual migration mode.'
$duplicateModeRejected = $false
try {
  Invoke-PackageParameterParser '/MigrationMode=ask /migrationmode=manual' | Out-Null
} catch {
  $duplicateModeRejected = $_.Exception.Message -like '*specified more than once*'
}
Assert-Condition $duplicateModeRejected 'The packaged parser did not reject duplicate migration mode parameters.'

$journalTestDirectory = Join-Path ([IO.Path]::GetTempPath()) ("bookshelfng-journal-fingerprint-{0}" -f [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $journalTestDirectory | Out-Null
$journalTestSucceeded = $false
try {
  $script:dataDir = Join-Path $journalTestDirectory 'data'
  $postgresBackupDirectory = Join-Path $journalTestDirectory 'postgres-backup'
  New-Item -ItemType Directory -Path $postgresBackupDirectory | Out-Null
  $mainDump = Join-Path $postgresBackupDirectory 'postgres-main.dump'
  [IO.File]::WriteAllText($mainDump, 'bookshelf-postgres-main-fixture')

  $singleFileFingerprint = Get-BookshelfJournalFingerprint $mainDump
  $expectedFileFingerprint = 'sha256:' + (Get-FileHash -LiteralPath $mainDump -Algorithm SHA256).Hash.ToLowerInvariant()
  Assert-Condition ($singleFileFingerprint -eq $expectedFileFingerprint) 'Journal file fingerprint did not match SHA-256.'

  $backupFingerprint = Get-BookshelfJournalFingerprint $postgresBackupDirectory
  Assert-Condition ($backupFingerprint -match '^sha256:[a-f0-9]{64}$') 'PostgreSQL backup directory did not produce a SHA-256 fingerprint.'
  Assert-Condition ((Get-BookshelfJournalFingerprint $postgresBackupDirectory) -eq $backupFingerprint) `
    'An unchanged PostgreSQL backup directory produced a different fingerprint.'

  [IO.File]::WriteAllText((Join-Path $postgresBackupDirectory 'postgres-log.dump'), 'bookshelf-postgres-log-fixture')
  $updatedBackupFingerprint = Get-BookshelfJournalFingerprint $postgresBackupDirectory
  Assert-Condition ($updatedBackupFingerprint -ne $backupFingerprint) 'Adding a PostgreSQL dump did not change its directory fingerprint.'

  Write-SharedMigrationJournal -FingerprintName 'source-postgres-backup' -FingerprintPath $postgresBackupDirectory
  $recordedFingerprint = (Get-Content -LiteralPath (Join-Path $script:dataDir '.chaptarrng-bookshelfng-journal/fingerprints/source-postgres-backup') -Raw).Trim()
  Assert-Condition ($recordedFingerprint -eq $updatedBackupFingerprint) 'The shared journal did not record the PostgreSQL backup directory fingerprint.'

  Write-SharedMigrationJournal -FingerprintName 'missing-postgres-backup' -FingerprintPath (Join-Path $journalTestDirectory 'missing-backup')
  $missingFingerprint = (Get-Content -LiteralPath (Join-Path $script:dataDir '.chaptarrng-bookshelfng-journal/fingerprints/missing-postgres-backup') -Raw).Trim()
  Assert-Condition ($missingFingerprint -eq 'missing') 'The shared journal did not mark a missing backup as missing.'
  $journalTestSucceeded = $true
} finally {
  if ($journalTestSucceeded) {
    Remove-Item -LiteralPath $journalTestDirectory -Recurse -Force -ErrorAction SilentlyContinue
  } else {
    Write-Warning "Journal fingerprint test failed; preserving evidence at $journalTestDirectory"
  }
}

$script:PromptCalls = 0
$script:PromptMessage = ''
$script:PromptResponse = ''
$testPrompt = {
  param([string] $Message)
  $script:PromptCalls++
  $script:PromptMessage = $Message
  return $script:PromptResponse
}

function Invoke-ChoiceCase {
  param(
    [string] $Response,
    [bool] $ExpectedToProceed
  )

  $script:PromptCalls = 0
  $script:PromptMessage = ''
  $script:PromptResponse = $Response
  $stagingReached = $false
  $failure = $null
  $parsedAskModes = Invoke-PackageParameterParser '/MigrationMode=ask'
  try {
    Confirm-BookshelfMigrationChoice `
      -HasMigrationData $true `
      -Mode $parsedAskModes.MigrationMode `
      -InputIsRedirected $false `
      -Prompt $testPrompt
    $stagingReached = $true
  } catch {
    $failure = $_.Exception.Message
  }

  Assert-Condition ($script:PromptCalls -eq 1) 'Ask mode did not show exactly one prompt.'
  Assert-Condition ($script:PromptMessage -match '\[y/N\]$') 'The prompt does not advertise its default-No choice.'
  if ($ExpectedToProceed) {
    Assert-Condition $stagingReached "Explicit yes '$Response' did not continue to package staging."
    Assert-Condition ($null -eq $failure) "Explicit yes '$Response' was rejected: $failure"
  } else {
    Assert-Condition (-not $stagingReached) "Response '$Response' continued into package staging."
    Assert-Condition ($failure -like 'BookshelfNG replacement cancelled before staging files*') `
      "Response '$Response' did not cancel before package staging."
  }
}

Invoke-ChoiceCase -Response 'n' -ExpectedToProceed $false
Invoke-ChoiceCase -Response 'N' -ExpectedToProceed $false
Invoke-ChoiceCase -Response '' -ExpectedToProceed $false
Invoke-ChoiceCase -Response 'y' -ExpectedToProceed $true
Invoke-ChoiceCase -Response 'YES' -ExpectedToProceed $true

$script:PromptCalls = 0
try {
  Confirm-BookshelfMigrationChoice `
    -HasMigrationData $true `
    -Mode $askModes.MigrationMode `
    -InputIsRedirected $true `
    -Prompt $testPrompt
  throw 'Ask mode unexpectedly accepted redirected input.'
} catch {
  if ($_.Exception.Message -notlike 'MigrationMode=ask requires an interactive terminal*') { throw }
}
Assert-Condition ($script:PromptCalls -eq 0) 'Ask mode prompted despite redirected input.'

$script:PromptCalls = 0
Confirm-BookshelfMigrationChoice `
  -HasMigrationData $false `
  -Mode $askModes.MigrationMode `
  -InputIsRedirected $true `
  -Prompt $testPrompt
Assert-Condition ($script:PromptCalls -eq 0) 'A fresh install prompted without BookshelfNG migration data.'

$script:PromptCalls = 0
Confirm-BookshelfMigrationChoice `
  -HasMigrationData $true `
  -Mode $defaultModes.MigrationMode `
  -InputIsRedirected $false `
  -Prompt $testPrompt
Assert-Condition ($script:PromptCalls -eq 0) 'The default automatic migration path unexpectedly prompted.'

$script:PromptCalls = 0
try {
  Confirm-BookshelfMigrationChoice `
    -HasMigrationData $true `
    -Mode $manualModes.MigrationMode `
    -InputIsRedirected $false `
    -Prompt $testPrompt
  throw 'Manual mode unexpectedly accepted the package transition.'
} catch {
  if ($_.Exception.Message -notlike 'MigrationMode=manual cancelled this package update*') { throw }
}
Assert-Condition ($script:PromptCalls -eq 0) 'Manual mode prompted for an automatic package transition.'

$temporaryDirectory = Join-Path ([IO.Path]::GetTempPath()) ("bookshelfng-chocolatey-utility-{0}" -f [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporaryDirectory | Out-Null
try {
  if ([Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT) {
    $fakeUtility = Join-Path $temporaryDirectory 'fake-utility.cmd'
    @(
      '@echo off',
      'if /I not "%2"=="-exitimmediately" exit /b 99',
      'echo Utility output must not replace the exit code',
      'exit /b 7'
    ) | Set-Content -LiteralPath $fakeUtility -Encoding ascii
  } else {
    $fakeUtility = Join-Path $temporaryDirectory 'fake-utility.sh'
    @(
      '#!/bin/sh',
      '[ "$2" = "-exitimmediately" ] || exit 99',
      'echo Utility output must not replace the exit code',
      'exit 7'
    ) | Set-Content -LiteralPath $fakeUtility -Encoding ascii
    & chmod +x $fakeUtility
    if ($LASTEXITCODE -ne 0) { throw 'Could not make the fake utility executable.' }
  }

  $utilityResult = @(Invoke-BookshelfUtility -Executable $fakeUtility -Arguments @('fixture-argument') -EnvironmentEntries @())
  Assert-Condition ($utilityResult.Count -eq 1) 'Utility stdout leaked into the returned exit-code value.'
  Assert-Condition ([int] $utilityResult[0] -eq 7) 'Utility did not preserve its process exit code.'
  $global:LASTEXITCODE = 0
} finally {
  Remove-Item -LiteralPath $temporaryDirectory -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host 'Chocolatey migration tests passed: packaged ask parameter parsing reaches the default-No prompt, explicit yes proceeds, manual defer and automatic default remain non-interactive, and journal/utility failure contracts hold.'
