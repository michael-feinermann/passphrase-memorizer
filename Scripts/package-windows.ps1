[CmdletBinding()]
param(
    [string]$KeyDirectory = 'A:\Passphrase Memorizer Keys',
    [switch]$SkipNativeBuild,
    [switch]$SkipArchive,
    [string]$ArchiveName = 'Passphrase-Memorizer-1.0.1-Windows-CUDA-x64.zip'
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$candidate = Join-Path $root ('.build\windows\candidate-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $candidate -Force | Out-Null
if (!$SkipNativeBuild) { & (Join-Path $PSScriptRoot 'build-windows-runtime.ps1') }
if (!(Test-Path -LiteralPath (Join-Path $root '.build\windows-runtime\dist\WindowsMnemonicRunner.exe'))) { throw 'Build the isolated native worker first.' }
& dotnet run --project (Join-Path $root 'Windows\Tests\PassphraseMemorizer.Tests.csproj') -c Release
if ($LASTEXITCODE) { throw 'Core/security parity checks failed.' }
$signerProject = Join-Path $root 'Windows\Signing\PassphraseMemorizer.WindowsSigner.csproj'
& dotnet run --project $signerProject -c Release -- selftest
if ($LASTEXITCODE) { throw 'Hybrid signer self-test failed.' }
& dotnet restore (Join-Path $root 'Windows\App\PassphraseMemorizer.App.csproj') --locked-mode
if ($LASTEXITCODE) { throw 'Locked application restore failed.' }
& dotnet publish (Join-Path $root 'Windows\App\PassphraseMemorizer.App.csproj') --no-restore -c Release -p:DebugType=None -p:DebugSymbols=false -o $candidate
if ($LASTEXITCODE) { throw 'Windows application publish failed.' }
$native = Join-Path $candidate 'native'
New-Item -ItemType Directory -Path $native -Force | Out-Null
foreach ($fileName in @('WindowsMnemonicRunner.exe','cublas64_13.dll','cublasLt64_13.dll')) {
    $nativePayload = Join-Path $root ('.build\windows-runtime\dist\' + $fileName)
    if (!(Test-Path -LiteralPath $nativePayload)) { throw ('Missing required CUDA payload: ' + $fileName) }
    Copy-Item -LiteralPath $nativePayload -Destination $native
}
$licenses = Join-Path $candidate 'Licenses'
New-Item -ItemType Directory -Path $licenses -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'LICENSE'),(Join-Path $root 'THIRD_PARTY_NOTICES.md') -Destination $licenses
Copy-Item -LiteralPath (Join-Path $root 'Licenses\python-mnemonic-MIT.txt') -Destination $licenses
Copy-Item -LiteralPath (Join-Path $root '.build\windows-runtime\source\LICENSE') -Destination (Join-Path $licenses 'llama.cpp-LICENSE.txt')
Copy-Item -LiteralPath 'C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v13.2\EULA.txt' -Destination (Join-Path $licenses 'NVIDIA-CUDA-EULA.txt')
$packageRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
Copy-Item -LiteralPath (Join-Path $packageRoot 'bouncycastle.cryptography\2.6.2\LICENSE.md') -Destination (Join-Path $licenses 'BouncyCastle-LICENSE.md')
Copy-Item -LiteralPath (Join-Path $packageRoot 'microsoft.netcore.app.runtime.win-x64\10.0.12\LICENSE.TXT') -Destination (Join-Path $licenses 'Microsoft.NET-LICENSE.TXT')
Copy-Item -LiteralPath (Join-Path $packageRoot 'microsoft.netcore.app.runtime.win-x64\10.0.12\THIRD-PARTY-NOTICES.TXT') -Destination (Join-Path $licenses 'Microsoft.NET-THIRD-PARTY-NOTICES.TXT')
Copy-Item -LiteralPath (Join-Path $packageRoot 'microsoft.windowsdesktop.app.runtime.win-x64\10.0.12\LICENSE') -Destination (Join-Path $licenses 'Microsoft.WindowsDesktop-LICENSE.TXT')
Copy-Item -LiteralPath (Join-Path $root 'docs\WINDOWS.de.md') -Destination (Join-Path $candidate 'README-Windows.de.md')
Copy-Item -LiteralPath (Join-Path $root 'Scripts\uninstall-windows.ps1') -Destination (Join-Path $candidate 'Uninstall.ps1')
& dotnet run --project $signerProject -c Release -- sign --bundle $candidate --key-directory $KeyDirectory
if ($LASTEXITCODE) { throw 'Dual release signing failed.' }
& dotnet run --project $signerProject -c Release -- verify --bundle $candidate
if ($LASTEXITCODE) { throw 'Dual signed bundle verification failed.' }
if ($SkipArchive) { [pscustomobject]@{ Bundle = $candidate; status = 'signed-candidate' } | ConvertTo-Json -Compress; return }
if ($ArchiveName -notmatch '^Passphrase-Memorizer-1\.0\.1-Windows-(CUDA-)?x64\.zip$') { throw 'Invalid Windows archive name.' }
$archive = Join-Path $root ('build\' + $ArchiveName)
New-Item -ItemType Directory -Path (Split-Path $archive -Parent) -Force | Out-Null
if (Test-Path -LiteralPath $archive) { throw 'Archive already exists; keep or rename the previous release before packaging.' }
Compress-Archive -LiteralPath (Get-ChildItem -LiteralPath $candidate -Force).FullName -DestinationPath $archive -CompressionLevel Optimal
$hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText($archive + '.sha256', $hash + '  ' + (Split-Path $archive -Leaf) + "`n", [Text.UTF8Encoding]::new($false))
& dotnet run --project $signerProject -c Release -- sign-file --target $archive --key-directory $KeyDirectory
if ($LASTEXITCODE) { throw 'Dual archive signing failed.' }
& dotnet run --project $signerProject -c Release -- verify-file --target $archive
if ($LASTEXITCODE) { throw 'Dual archive verification failed.' }
[pscustomobject]@{ Bundle = $candidate; Archive = $archive; SHA256 = $hash } | ConvertTo-Json -Compress
