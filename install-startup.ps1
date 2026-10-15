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

# every question, course and topic comes from Claude Code: stop here if it is missing or not logged in
if (-not (Get-Command claude -ErrorAction SilentlyContinue)) {
    Write-Error "Claude Code is not installed (no 'claude' on the PATH). Install it, run 'claude' once and log in, then run this again."
    exit 1
}
$reply = 'Reply with just: OK' | claude -p --no-session-persistence 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Error "Claude Code did not answer - is it logged in? Run 'claude' once and log in, then run this again.`n$reply"
    exit 1
}

$link = (New-Object -ComObject WScript.Shell).CreateShortcut($shortcut)
$link.TargetPath = $exe
$link.WorkingDirectory = Split-Path $exe
$link.Save()

Start-Process $exe
Write-Output "Installed $shortcut and started Dev break (look for the green ? in the tray)."
