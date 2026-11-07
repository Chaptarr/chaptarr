$ErrorActionPreference = 'Stop'

$publisher = Join-Path $PSScriptRoot 'publish-chocolatey-packages.ps1'
$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("chaptarr-chocolatey-gate-{0}" -f [guid]::NewGuid().ToString('N'))
$originalRunnerTemp = $env:RUNNER_TEMP
$originalBridgeState = $env:LEGACY_BRIDGE_ENABLED
$originalApiKey = $env:CHOCOLATEY_API_KEY
$global:ChaptarrChocolateyPublishedPackages = [System.Collections.Generic.List[string]]::new()

function choco {
  if ($args.Count -lt 2 -or $args[0] -ne 'push') {
    throw 'The test publisher received an unexpected Chocolatey command.'
  }

  $global:ChaptarrChocolateyPublishedPackages.Add([System.IO.Path]::GetFileName([string]$args[1]))
  $global:LASTEXITCODE = 0
}

try {
  $packageDirectory = Join-Path $tempRoot 'chaptarrng-chocolatey-output'
  New-Item -ItemType Directory -Force -Path $packageDirectory | Out-Null
  Set-Content -LiteralPath (Join-Path $packageDirectory 'chaptarrng.0.9.969.nupkg') -Value 'standard' -NoNewline
  Set-Content -LiteralPath (Join-Path $packageDirectory 'bookshelfng.0.9.969.nupkg') -Value 'legacy' -NoNewline
  $env:RUNNER_TEMP = $tempRoot
  $env:CHOCOLATEY_API_KEY = 'test-only'

  $env:LEGACY_BRIDGE_ENABLED = 'false'
  & $publisher
  if ($global:ChaptarrChocolateyPublishedPackages.Count -ne 1 -or $global:ChaptarrChocolateyPublishedPackages[0] -ne 'chaptarrng.0.9.969.nupkg') {
    throw "Disabled bridge published unexpected package identities: $($global:ChaptarrChocolateyPublishedPackages -join ', ')."
  }

  $global:ChaptarrChocolateyPublishedPackages.Clear()
  $env:LEGACY_BRIDGE_ENABLED = 'true'
  & $publisher
  $published = @($global:ChaptarrChocolateyPublishedPackages | Sort-Object)
  $expected = @('bookshelfng.0.9.969.nupkg', 'chaptarrng.0.9.969.nupkg') | Sort-Object
  if ($published.Count -ne $expected.Count -or (Compare-Object $published $expected)) {
    throw "Enabled bridge did not publish both expected package identities: $($published -join ', ')."
  }

  Write-Host 'Chocolatey legacy package publish gate tests passed.'
} finally {
  if (Test-Path -LiteralPath $tempRoot) {
    Remove-Item -LiteralPath $tempRoot -Recurse -Force
  }

  if ($null -eq $originalRunnerTemp) { Remove-Item Env:RUNNER_TEMP -ErrorAction SilentlyContinue } else { $env:RUNNER_TEMP = $originalRunnerTemp }
  if ($null -eq $originalBridgeState) { Remove-Item Env:LEGACY_BRIDGE_ENABLED -ErrorAction SilentlyContinue } else { $env:LEGACY_BRIDGE_ENABLED = $originalBridgeState }
  if ($null -eq $originalApiKey) { Remove-Item Env:CHOCOLATEY_API_KEY -ErrorAction SilentlyContinue } else { $env:CHOCOLATEY_API_KEY = $originalApiKey }
  Remove-Variable -Scope Global -Name ChaptarrChocolateyPublishedPackages -ErrorAction SilentlyContinue
}
