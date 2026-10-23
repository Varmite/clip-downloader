# Installs YT Downloader into Program Files with Start menu/desktop shortcuts and an Apps & Features entry.
param([string]$Source = (Join-Path $PSScriptRoot 'dist'))
$ErrorActionPreference = 'Stop'

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    $p = Start-Process powershell.exe -Verb RunAs -Wait -PassThru -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", '-Source', "`"$Source`"")
    exit $p.ExitCode
}

try {
    $app = 'YT Downloader'
    $dest = Join-Path $env:ProgramFiles $app
    $exe = Join-Path $dest 'YTDownloader.exe'
    if (-not (Test-Path (Join-Path $Source 'YTDownloader.exe'))) { throw "Build output not found in $Source. Run build.ps1 first." }

    Get-Process YTDownloader -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 300
    New-Item -ItemType Directory -Force $dest | Out-Null
    robocopy $Source $dest /MIR /R:2 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "Copying files failed (robocopy code $LASTEXITCODE)" }

    # Precompile to native code so the app starts even faster
    $ngen = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\ngen.exe'
    & $ngen install $exe /nologo /silent | Out-Null

    $shell = New-Object -ComObject WScript.Shell
    foreach ($dir in [Environment]::GetFolderPath('CommonPrograms'), [Environment]::GetFolderPath('CommonDesktopDirectory')) {
        $lnk = $shell.CreateShortcut((Join-Path $dir "$app.lnk"))
        $lnk.TargetPath = $exe
        $lnk.WorkingDirectory = $dest
        $lnk.IconLocation = "$exe,0"
        $lnk.Description = 'Download YouTube, TikTok and Instagram videos and audio'
        $lnk.Save()
    }

    $key = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\YTDownloader'
    New-Item $key -Force | Out-Null
    $sizeKb = [int]((Get-ChildItem $dest -Recurse -File | Measure-Object Length -Sum).Sum / 1KB)
    $strings = @{
        DisplayName     = $app
        DisplayVersion  = '1.0.0'
        Publisher       = 'YT Downloader'
        DisplayIcon     = "$exe,0"
        InstallLocation = $dest
        UninstallString = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$dest\uninstall.ps1`""
    }
    foreach ($k in $strings.Keys) { Set-ItemProperty $key -Name $k -Value $strings[$k] -Type String }
    foreach ($k in 'NoModify', 'NoRepair') { Set-ItemProperty $key -Name $k -Value 1 -Type DWord }
    Set-ItemProperty $key -Name EstimatedSize -Value $sizeKb -Type DWord

    Write-Host "Installed to $dest"
    exit 0
}
catch {
    Write-Host "Install failed: $_" -ForegroundColor Red
    Read-Host 'Press Enter to close'
    exit 1
}
