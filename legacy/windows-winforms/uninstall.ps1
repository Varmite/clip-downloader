# Removes YT Downloader: program files, shortcuts, Apps & Features entry, settings and auto-updated yt-dlp.
$ErrorActionPreference = 'Continue'

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Start-Process powershell.exe -Verb RunAs -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"")
    exit
}

$app = 'YT Downloader'
$dest = Join-Path $env:ProgramFiles $app
Get-Process YTDownloader -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 300

$ngen = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\ngen.exe'
& $ngen uninstall (Join-Path $dest 'YTDownloader.exe') /nologo /silent | Out-Null

foreach ($dir in [Environment]::GetFolderPath('CommonPrograms'), [Environment]::GetFolderPath('CommonDesktopDirectory')) {
    Remove-Item (Join-Path $dir "$app.lnk") -Force -ErrorAction SilentlyContinue
}
Remove-Item 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\YTDownloader' -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $env:LOCALAPPDATA 'YTDownloader') -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $env:APPDATA 'YTDownloader') -Recurse -Force -ErrorAction SilentlyContinue

Set-Location $env:TEMP
Remove-Item $dest -Recurse -Force -ErrorAction SilentlyContinue
