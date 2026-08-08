# Download FFmpeg binaries for Windows
# This script should be run during CI/CD build process

$ErrorActionPreference = "Stop"

$ToolsDir = Join-Path $PSScriptRoot "..\Tools"
New-Item -ItemType Directory -Force -Path $ToolsDir | Out-Null

Write-Host "Downloading FFmpeg binaries for Windows..." -ForegroundColor Green

function Download-FFmpeg {
    param(
        [string]$Platform,
        [string]$Url
    )
    
    Write-Host "Downloading FFmpeg for $Platform..." -ForegroundColor Yellow
    
    $ffmpegDir = Join-Path $ToolsDir "ffmpeg\$Platform"
    $ffprobeDir = Join-Path $ToolsDir "ffprobe\$Platform"
    
    New-Item -ItemType Directory -Force -Path $ffmpegDir | Out-Null
    New-Item -ItemType Directory -Force -Path $ffprobeDir | Out-Null
    
    $zipPath = Join-Path $ffmpegDir "ffmpeg.zip"
    
    # Download (HTTPS provides transport integrity; gyan.dev does not publish .sha256 sidecar files)
    Invoke-WebRequest -Uri $Url -OutFile $zipPath

    # Extract
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
    
    $ffmpegEntry = $zip.Entries | Where-Object { $_.Name -eq "ffmpeg.exe" } | Select-Object -First 1
    $ffprobeEntry = $zip.Entries | Where-Object { $_.Name -eq "ffprobe.exe" } | Select-Object -First 1
    
    [System.IO.Compression.ZipFileExtensions]::ExtractToFile($ffmpegEntry, (Join-Path $ffmpegDir "ffmpeg.exe"), $true)
    [System.IO.Compression.ZipFileExtensions]::ExtractToFile($ffprobeEntry, (Join-Path $ffmpegDir "ffprobe.exe"), $true)
    
    # Copy ffprobe to its directory
    Copy-Item (Join-Path $ffmpegDir "ffprobe.exe") -Destination $ffprobeDir -Force
    
    $zip.Dispose()
    Remove-Item $zipPath
    
    Write-Host "Downloaded $Platform successfully" -ForegroundColor Green
}

# Windows x64
Download-FFmpeg -Platform "win-x64" `
    -Url "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip"

Write-Host "FFmpeg download complete!" -ForegroundColor Green
Write-Host "Remember to add Tools/** to your .gitignore if you don't want to commit binaries" -ForegroundColor Yellow
