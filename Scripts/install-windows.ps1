[CmdletBinding()]
param([Parameter(Mandatory)][string]$Bundle)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$source = (Resolve-Path -LiteralPath $Bundle).Path
$signerProject = Join-Path $root 'Windows\Signing\PassphraseMemorizer.WindowsSigner.csproj'
& dotnet run --project $signerProject -c Release -- verify --bundle $source
if ($LASTEXITCODE) { throw 'Refusing to install an invalid hybrid-signed bundle.' }
$programs = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs'))
$destination = [IO.Path]::GetFullPath((Join-Path $programs 'Passphrase Memorizer'))
$staging = [IO.Path]::GetFullPath((Join-Path $programs ('Passphrase Memorizer.staging-' + [Guid]::NewGuid().ToString('N'))))
$backup = [IO.Path]::GetFullPath((Join-Path $programs ('Passphrase Memorizer.backup-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N'))))
foreach ($target in @($destination,$staging,$backup)) {
    if (!( $target.StartsWith($programs.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase))) { throw 'Install target escaped the Programs directory.' }
}
if ((Test-Path -LiteralPath $staging) -or (Test-Path -LiteralPath $backup)) { throw 'The unique install staging/backup target already exists.' }
if (Test-Path -LiteralPath $destination) {
    $running = Get-Process -ErrorAction SilentlyContinue | Where-Object { try { $_.Path -eq (Join-Path $destination 'Passphrase Memorizer.exe') } catch { $false } }
    if ($running) { throw 'Close the installed application before upgrading.' }
}
New-Item -ItemType Directory -Path $staging -Force | Out-Null
Copy-Item -LiteralPath (Get-ChildItem -LiteralPath $source -Force).FullName -Destination $staging -Recurse
& dotnet run --project $signerProject -c Release -- verify --bundle $staging
if ($LASTEXITCODE) { throw 'The staged installation failed verification.' }
$movedOld = $false
try {
    if (Test-Path -LiteralPath $destination) { Move-Item -LiteralPath $destination -Destination $backup; $movedOld = $true }
    Move-Item -LiteralPath $staging -Destination $destination
    & dotnet run --project $signerProject -c Release -- verify --bundle $destination
    if ($LASTEXITCODE) { throw 'The installed bundle failed verification.' }
} catch {
    if ($movedOld) {
        if (Test-Path -LiteralPath $destination) {
            $failed = [IO.Path]::GetFullPath((Join-Path $programs ('Passphrase Memorizer.failed-' + [Guid]::NewGuid().ToString('N'))))
            if (!$failed.StartsWith($programs.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Rollback target escaped the Programs directory.' }
            Move-Item -LiteralPath $destination -Destination $failed
        }
        Move-Item -LiteralPath $backup -Destination $destination
    }
    throw
}
$shell = New-Object -ComObject WScript.Shell
$startMenu = Join-Path ([Environment]::GetFolderPath('Programs')) 'Passphrase Memorizer.lnk'
$desktop = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Passphrase Memorizer.lnk'
foreach ($shortcutPath in @($startMenu,$desktop)) {
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = Join-Path $destination 'Passphrase Memorizer.exe'
    $shortcut.WorkingDirectory = $destination
    $shortcut.Description = 'Passphrase Memorizer 1.0.1 — local BIP39 and EFF memory aids'
    $shortcut.IconLocation = $shortcut.TargetPath + ',0'
    $shortcut.Save()
}
$uninstall = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\PassphraseMemorizer'
New-Item -Path $uninstall -Force | Out-Null
$properties = @{ DisplayName='Passphrase Memorizer'; DisplayVersion='1.0.1'; Publisher='Michael Feinermann'; InstallLocation=$destination; DisplayIcon=(Join-Path $destination 'Passphrase Memorizer.exe'); UninstallString=('powershell.exe -NoProfile -ExecutionPolicy Bypass -File "' + (Join-Path $destination 'Uninstall.ps1') + '"') }
foreach ($item in $properties.GetEnumerator()) { New-ItemProperty -Path $uninstall -Name $item.Key -Value $item.Value -PropertyType String -Force | Out-Null }
New-ItemProperty -Path $uninstall -Name NoModify -Value 1 -PropertyType DWord -Force | Out-Null
New-ItemProperty -Path $uninstall -Name NoRepair -Value 1 -PropertyType DWord -Force | Out-Null
[pscustomobject]@{ status='installed-and-verified'; directory=$destination; startMenu=$startMenu; desktop=$desktop; previousVersion= $(if($movedOld){$backup}else{$null}) } | ConvertTo-Json -Compress
