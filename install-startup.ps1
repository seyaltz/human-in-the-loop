# Starts Dev break at Windows login (a shortcut in the Startup folder) and starts it now.
# Remove it again: .\install-startup.ps1 -Uninstall
param([switch]$Uninstall)

$shortcut = Join-Path ([Environment]::GetFolderPath('Startup')) 'Dev break.lnk'

if ($Uninstall) {
    Remove-Item $shortcut -ErrorAction SilentlyContinue
    Write-Output "Removed $shortcut (an already running copy keeps running until you Exit it from the tray)."
    return
}

$exe = Join-Path $PSScriptRoot 'publish\DevBreak.exe'

$link = (New-Object -ComObject WScript.Shell).CreateShortcut($shortcut)
$link.TargetPath = $exe
$link.WorkingDirectory = Split-Path $exe
$link.Save()

Start-Process $exe
Write-Output "Installed $shortcut and started Dev break (look for the green ? in the tray)."
