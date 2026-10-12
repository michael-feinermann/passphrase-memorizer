$ErrorActionPreference = 'Stop'
$installation = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\')
$expected = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs\Passphrase Memorizer')).TrimEnd('\')
if (!$installation.Equals($expected,[StringComparison]::OrdinalIgnoreCase)
    -or !(Test-Path -LiteralPath (Join-Path $installation 'Passphrase Memorizer.exe'))
    -or !(Test-Path -LiteralPath (Join-Path $installation 'windows-release.json'))) { throw 'This uninstaller is only valid in the registered user installation.' }
$running = Get-Process -ErrorAction SilentlyContinue | Where-Object { try { $_.Path -eq (Join-Path $installation 'Passphrase Memorizer.exe') } catch { $false } }
if ($running) { throw 'Close Passphrase Memorizer before uninstalling.' }
$registration = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\PassphraseMemorizer'
if ((Get-ItemProperty -Path $registration -Name InstallLocation).InstallLocation -ne $installation) { throw 'Installation registration mismatch.' }
$shell = New-Object -ComObject WScript.Shell
foreach ($shortcutPath in @((Join-Path ([Environment]::GetFolderPath('Programs')) 'Passphrase Memorizer.lnk'),(Join-Path ([Environment]::GetFolderPath('Desktop')) 'Passphrase Memorizer.lnk'))) {
    if (Test-Path -LiteralPath $shortcutPath) {
        $shortcut = $shell.CreateShortcut($shortcutPath)
        if ($shortcut.TargetPath -eq (Join-Path $installation 'Passphrase Memorizer.exe')) { Remove-Item -LiteralPath $shortcutPath }
    }
}
Remove-Item -LiteralPath $registration
# Exact absolute target was validated above against the registered application path.
Remove-Item -LiteralPath $installation -Recurse -Force
Write-Output 'Passphrase Memorizer was uninstalled. Model weights, signing keys and preferences are retained.'
