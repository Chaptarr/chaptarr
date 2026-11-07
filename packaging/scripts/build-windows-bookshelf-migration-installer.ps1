param(
    [string] $AppDirectory = (Join-Path $PSScriptRoot '..\..\publish\Chaptarr'),
    [string] $InstallerOutput = (Join-Path $PSScriptRoot '..\..\publish\ChaptarrNGInstaller'),
    [switch] $KeepPayload
)

$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$projectDirectory = Join-Path $repoRoot 'distribution\windows\installer-net'
$project = Join-Path $projectDirectory 'ChaptarrNG.Installer.csproj'
$payloadDirectory = Join-Path $projectDirectory 'Payload'
$payload = Join-Path $payloadDirectory 'Chaptarr.payload.zip'
$appRoot = (Resolve-Path -LiteralPath $AppDirectory).Path
$outputRoot = [IO.Path]::GetFullPath($InstallerOutput)

if (-not (Test-Path -LiteralPath (Join-Path $appRoot 'Chaptarr.Console.exe') -PathType Leaf)) {
    throw "The published Windows application is missing Chaptarr.Console.exe: $appRoot"
}
if (Test-Path -LiteralPath $payload) {
    throw "Refusing to overwrite an existing payload file: $payload"
}
if (Test-Path -LiteralPath $outputRoot) {
    throw "Refusing to overwrite an existing installer output directory: $outputRoot"
}

$createdPayloadDirectory = $false
try {
    if (-not (Test-Path -LiteralPath $payloadDirectory)) {
        New-Item -ItemType Directory -Path $payloadDirectory | Out-Null
        $createdPayloadDirectory = $true
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory(
        $appRoot,
        $payload,
        [IO.Compression.CompressionLevel]::Optimal,
        $false)

    & dotnet publish $project -c Release -r win-x64 --self-contained true -p:EnableWindowsTargeting=true -p:PublishSingleFile=true -o $outputRoot
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE."
    }

    $installer = Join-Path $outputRoot 'ChaptarrNG.Installer.exe'
    if (-not (Test-Path -LiteralPath $installer -PathType Leaf) -or (Get-Item -LiteralPath $installer).Length -lt 1MB) {
        throw "The self-contained migration installer was not produced or is unexpectedly small: $installer"
    }

    if (Test-Path -LiteralPath (Join-Path $outputRoot 'Chaptarr.payload.zip')) {
        throw 'The application payload was emitted as a sidecar instead of being embedded in the installer.'
    }

    & $installer '/verify-payload'
    if ($LASTEXITCODE -ne 0) {
        throw "The installer did not load both required embedded resources (exit code $LASTEXITCODE)."
    }

    Write-Host "Built candidate installer: $installer"
}
finally {
    if (-not $KeepPayload -and (Test-Path -LiteralPath $payload)) {
        Remove-Item -LiteralPath $payload -Force
    }
    if ($createdPayloadDirectory -and
        (Test-Path -LiteralPath $payloadDirectory) -and
        -not (Get-ChildItem -LiteralPath $payloadDirectory -Force | Select-Object -First 1)) {
        Remove-Item -LiteralPath $payloadDirectory -Force
    }
}
