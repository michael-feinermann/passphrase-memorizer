param([switch]$SkipRestore)
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$project = Join-Path $PSScriptRoot 'PassphraseMemorizer.WindowsSigner.csproj'
if (-not $SkipRestore) {
    & dotnet restore $project --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Locked Windows signer restore failed.' }
}
& dotnet build $project -c Release --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Windows signer build failed.' }
& (Join-Path $PSScriptRoot 'build-reference.ps1')
$signer = Join-Path $PSScriptRoot 'bin\Release\net10.0\PassphraseMemorizer.WindowsSigner.dll'
& dotnet $signer selftest --reference-library (Join-Path $PSScriptRoot 'build-reference\mldsa87-reference.dll') --skein-vectors (Join-Path $repoRoot 'Tests\Reference\Skein\vectors.json')
if ($LASTEXITCODE -ne 0) { throw 'Windows hybrid signer security selftests failed.' }
& (Join-Path $PSScriptRoot 'test-reference-hashes.ps1')
