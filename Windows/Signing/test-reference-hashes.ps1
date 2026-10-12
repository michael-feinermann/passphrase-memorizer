param(
    [string]$Target = '',
    [string]$FingerprintJson = '',
    [string]$ReferenceDirectory = (Join-Path $PSScriptRoot 'build-reference')
)
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$skein = Join-Path $ReferenceDirectory 'skein-reference-checksum.exe'
$sha3 = Join-Path $ReferenceDirectory 'sha3-reference-checksum.exe'
if (-not (Test-Path -LiteralPath $skein) -or -not (Test-Path -LiteralPath $sha3)) { throw 'Run build-reference.ps1 first.' }
function Check-Fingerprints([string]$Path, $Expected) {
    $sha256Actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
    $sha3Actual = & $sha3 $Path
    if ($LASTEXITCODE -ne 0) { throw "SHA3 reference failed for $Path" }
    $skeinActual = & $skein $Path
    if ($LASTEXITCODE -ne 0) { throw "Skein reference failed for $Path" }
    if ($sha256Actual -ne $Expected.Sha256 -or $sha3Actual -ne $Expected.Sha3_512 -or $skeinActual -ne $Expected.Skein1024) { throw "Independent release fingerprint mismatch: $Path" }
    Write-Output "PASS independent SHA256/SHA3-512/Skein1024: $Path"
}
if ($Target) {
    if (-not $FingerprintJson) { $FingerprintJson = $Target + '.hashes.json' }
    $expected = [IO.File]::ReadAllText([IO.Path]::GetFullPath($FingerprintJson)) | ConvertFrom-Json
    Check-Fingerprints ([IO.Path]::GetFullPath($Target)) $expected
}
else {
    $trustDirectory = Join-Path $repoRoot 'Windows\Crypto\Trust'
    $trust = [IO.File]::ReadAllText((Join-Path $trustDirectory 'trust.json')) | ConvertFrom-Json
    Check-Fingerprints (Join-Path $trustDirectory 'rsa4096-spki.der') $trust.rsa
    Check-Fingerprints (Join-Path $trustDirectory 'mldsa87-public.bin') $trust.mldsa
    $fixtureDirectory = Join-Path ([IO.Path]::GetTempPath()) ('PassphraseMemorizer-HashFixture-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $fixtureDirectory | Out-Null
    try {
        $fixturePath = Join-Path $fixtureDirectory 'public-vector.dat'
        $vectors = [IO.File]::ReadAllText((Join-Path $repoRoot 'Tests\Reference\Skein\vectors.json')) | ConvertFrom-Json
        foreach ($vector in $vectors) {
            [IO.File]::WriteAllBytes($fixturePath, [Convert]::FromHexString($vector.message))
            $actual = & $skein $fixturePath
            if ($LASTEXITCODE -ne 0 -or $actual -ne $vector.output) { throw "Independent Skein reference golden vector failed: $($vector.label)" }
        }
        Write-Output "PASS independent Skein C reference: $($vectors.Count) published golden vectors"
    }
    finally {
        $fixtureAbsolute = [IO.Path]::GetFullPath($fixtureDirectory)
        $tempAbsolute = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath([IO.Path]::GetTempPath())) + '\'
        if (-not $fixtureAbsolute.StartsWith($tempAbsolute, [StringComparison]::OrdinalIgnoreCase) -or -not [IO.Path]::GetFileName($fixtureAbsolute).StartsWith('PassphraseMemorizer-HashFixture-', [StringComparison]::Ordinal)) { throw 'Unsafe public hash fixture cleanup.' }
        Remove-Item -LiteralPath $fixtureAbsolute -Recurse -Force
    }
}
