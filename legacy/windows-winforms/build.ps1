# Builds YT Downloader into .\dist: compiles the app and, if missing, downloads yt-dlp, ffmpeg and deno.
# Uses the C# compiler that ships with Windows, so no SDK is needed.
param([switch]$RefreshDeps)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$root = $PSScriptRoot
$dist = Join-Path $root 'dist'
$deps = Join-Path $root 'deps'
New-Item -ItemType Directory -Force $dist, $deps | Out-Null
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

function Get-Verified([string]$url, [string]$file, [string]$expectedSha) {
    Invoke-WebRequest $url -OutFile $file -UseBasicParsing
    if ($expectedSha -and (Get-FileHash $file -Algorithm SHA256).Hash -ne $expectedSha.ToUpper()) { throw "Checksum mismatch for $url" }
}

# --- Dependencies -------------------------------------------------------------
if ($RefreshDeps -or -not (Test-Path "$dist\yt-dlp\yt-dlp.exe")) {
    Write-Host 'Downloading yt-dlp...'
    $tag = (Invoke-RestMethod 'https://api.github.com/repos/yt-dlp/yt-dlp/releases/latest').tag_name
    $base = "https://github.com/yt-dlp/yt-dlp/releases/download/$tag"
    $sums = (Invoke-WebRequest "$base/SHA2-256SUMS" -UseBasicParsing).Content
    $sha = ($sums -split "`n" | Where-Object { $_ -match ' yt-dlp_win\.zip$' }) -replace '\s.*$', ''
    Get-Verified "$base/yt-dlp_win.zip" "$deps\yt-dlp_win.zip" $sha
    if (Test-Path "$dist\yt-dlp") { Remove-Item "$dist\yt-dlp" -Recurse -Force }
    Expand-Archive "$deps\yt-dlp_win.zip" "$dist\yt-dlp"
    Set-Content "$dist\yt-dlp\version.txt" $tag -NoNewline -Encoding Ascii
}
if ($RefreshDeps -or -not (Test-Path "$dist\bin\ffmpeg.exe")) {
    Write-Host 'Downloading ffmpeg...'
    Get-Verified 'https://github.com/yt-dlp/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl-shared.zip' "$deps\ffmpeg.zip" $null
    $x = "$deps\ffmpeg"
    if (Test-Path $x) { Remove-Item $x -Recurse -Force }
    Expand-Archive "$deps\ffmpeg.zip" $x
    $src = Get-ChildItem $x -Directory | Select-Object -First 1
    New-Item -ItemType Directory -Force "$dist\bin" | Out-Null
    Copy-Item "$($src.FullName)\bin\ffmpeg.exe", "$($src.FullName)\bin\ffprobe.exe", "$($src.FullName)\bin\*.dll" "$dist\bin" -Force
    Copy-Item "$($src.FullName)\LICENSE.txt" "$dist\bin\FFMPEG-LICENSE.txt" -Force
}
if ($RefreshDeps -or -not (Test-Path "$dist\bin\deno.exe")) {
    Write-Host 'Downloading deno...'
    $rel = Invoke-RestMethod 'https://api.github.com/repos/denoland/deno/releases/latest'
    $base = "https://github.com/denoland/deno/releases/download/$($rel.tag_name)"
    $sha = ((Invoke-WebRequest "$base/deno-x86_64-pc-windows-msvc.zip.sha256sum" -UseBasicParsing).Content | Select-String '[0-9a-fA-F]{64}').Matches[0].Value
    Get-Verified "$base/deno-x86_64-pc-windows-msvc.zip" "$deps\deno.zip" $sha
    Expand-Archive "$deps\deno.zip" "$dist\bin" -Force
}

# --- App ----------------------------------------------------------------------
$icon = Join-Path $root 'src\app.ico'
if (-not (Test-Path $icon)) { & (Join-Path $root 'tools\make-icon.ps1') -Out $icon }

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
Write-Host 'Compiling...'
& $csc /nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 `
    "/out:$dist\YTDownloader.exe" "/win32icon:$icon" "/win32manifest:$root\src\app.manifest" `
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll `
    /r:System.Web.Extensions.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll `
    "$root\src\YTDownloader.cs"
if ($LASTEXITCODE -ne 0) { throw 'Compile failed' }
Copy-Item "$root\uninstall.ps1" $dist -Force
Write-Host "Built $dist\YTDownloader.exe"
