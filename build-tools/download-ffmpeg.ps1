# Download FFmpeg binaries for Windows source/native packaging.

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$ToolsDir = Join-Path $PSScriptRoot "..\Tools"
New-Item -ItemType Directory -Force -Path $ToolsDir | Out-Null

Write-Host "Downloading FFmpeg binaries for Windows..." -ForegroundColor Green

function Invoke-DownloadWithRetry {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Uri,
        [Parameter(Mandatory = $true)]
        [string]$OutFile
    )

    for ($attempt = 1; $attempt -le 3; $attempt++) {
        try {
            Invoke-WebRequest -UseBasicParsing -Uri $Uri -OutFile $OutFile

            if (!(Test-Path $OutFile) -or (Get-Item $OutFile).Length -eq 0) {
                throw "Downloaded file is empty"
            }

            return
        }
        catch {
            Remove-Item $OutFile -Force -ErrorAction SilentlyContinue

            if ($attempt -eq 3) {
                throw
            }

            Write-Warning "Download attempt $attempt failed for $Uri; retrying in 5 seconds."
            Start-Sleep -Seconds 5
        }
    }
}

function Assert-ExecutableVersion {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [string]$ExpectedName
    )

    $output = & $Path -version 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0 -or $output -notmatch [regex]::Escape($ExpectedName)) {
        throw "$ExpectedName failed its -version check at $Path"
    }
}

function Download-FFmpeg {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Platform,
        [Parameter(Mandatory = $true)]
        [string]$Url,
        [Parameter(Mandatory = $true)]
        [ValidatePattern('^[0-9a-fA-F]{64}$')]
        [string]$ExpectedSha256
    )

    Write-Host "Downloading FFmpeg for $Platform..." -ForegroundColor Yellow

    $ffmpegDir = Join-Path $ToolsDir "ffmpeg\$Platform"
    $ffprobeDir = Join-Path $ToolsDir "ffprobe\$Platform"

    New-Item -ItemType Directory -Force -Path $ffmpegDir | Out-Null
    New-Item -ItemType Directory -Force -Path $ffprobeDir | Out-Null

    $zipPath = Join-Path $ffmpegDir "ffmpeg.zip"
    $zip = $null

    try {
        $expectedHash = $ExpectedSha256.ToUpperInvariant()
        $verified = $false

        for ($attempt = 1; $attempt -le 3; $attempt++) {
            Invoke-DownloadWithRetry -Uri $Url -OutFile $zipPath
            $actualHash = (Get-FileHash -Algorithm SHA256 -Path $zipPath).Hash.ToUpperInvariant()

            if ($actualHash -eq $expectedHash) {
                $verified = $true
                break
            }

            Remove-Item $zipPath -Force -ErrorAction SilentlyContinue
            if ($attempt -lt 3) {
                Write-Warning "SHA256 validation attempt $attempt failed for $Url; retrying in 5 seconds."
                Start-Sleep -Seconds 5
            }
        }

        if (!$verified) {
            throw "SHA256 mismatch for $Url (expected $expectedHash, got $actualHash)"
        }

        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $zip = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
        $ffmpegEntry = $zip.Entries | Where-Object { $_.Name -eq "ffmpeg.exe" } | Select-Object -First 1
        $ffprobeEntry = $zip.Entries | Where-Object { $_.Name -eq "ffprobe.exe" } | Select-Object -First 1

        if ($null -eq $ffmpegEntry -or $null -eq $ffprobeEntry) {
            throw "Archive does not contain both ffmpeg.exe and ffprobe.exe"
        }

        $ffmpegPath = Join-Path $ffmpegDir "ffmpeg.exe"
        $ffprobePath = Join-Path $ffprobeDir "ffprobe.exe"

        [System.IO.Compression.ZipFileExtensions]::ExtractToFile($ffmpegEntry, $ffmpegPath, $true)
        [System.IO.Compression.ZipFileExtensions]::ExtractToFile($ffprobeEntry, $ffprobePath, $true)

        Assert-ExecutableVersion -Path $ffmpegPath -ExpectedName "ffmpeg"
        Assert-ExecutableVersion -Path $ffprobePath -ExpectedName "ffprobe"

        Write-Host "Downloaded and verified $Platform successfully" -ForegroundColor Green
    }
    finally {
        if ($null -ne $zip) {
            $zip.Dispose()
        }

        Remove-Item $zipPath -Force -ErrorAction SilentlyContinue
    }
}

Download-FFmpeg -Platform "win-x64" `
    -Url "https://www.gyan.dev/ffmpeg/builds/packages/ffmpeg-8.1.2-essentials_build.zip" `
    -ExpectedSha256 "db580001caa24ac104c8cb856cd113a87b0a443f7bdf47d8c12b1d740584a2ec"

Write-Host "FFmpeg download complete!" -ForegroundColor Green
Write-Host "Remember to add Tools/** to your .gitignore if you don't want to commit binaries" -ForegroundColor Yellow
