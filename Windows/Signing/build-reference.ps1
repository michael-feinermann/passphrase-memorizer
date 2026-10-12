param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'build-reference'))
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$referenceRoot = Join-Path $repoRoot 'Signing\HybridSigner\Reference'
foreach ($line in Get-Content -LiteralPath (Join-Path $referenceRoot 'SOURCE_SHA256SUMS')) {
    if ($line -notmatch '^([a-f0-9]{64})  (.+)$') { throw 'Invalid pinned reference source hash inventory.' }
    $expected = $Matches[1]
    $source = [IO.Path]::GetFullPath((Join-Path $referenceRoot $Matches[2]))
    if (-not $source.StartsWith($referenceRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Reference source escaped its directory.' }
    if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected) { throw "Reference source hash mismatch: $source" }
}
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$visualStudio = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $visualStudio) { throw 'Visual Studio C++ build tools required for independent ML-DSA verification.' }
$vcvars = Join-Path $visualStudio 'VC\Auxiliary\Build\vcvars64.bat'
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$sourceNames = @('mldsa87_ref_export.c', 'ref\fips202.c', 'ref\ntt.c', 'ref\packing.c', 'ref\poly.c', 'ref\polyvec.c', 'ref\reduce.c', 'ref\rounding.c', 'ref\sign.c', 'ref\symmetric-shake.c')
$sources = ($sourceNames | ForEach-Object { '"' + (Join-Path $referenceRoot $_) + '"' }) -join ' '
$dll = Join-Path $outputRoot 'mldsa87-reference.dll'
$compile = 'call "' + $vcvars + '" >nul && cl.exe /nologo /LD /O2 /W3 /D_CRT_SECURE_NO_WARNINGS /DDILITHIUM_MODE=5 ' + $sources + ' /link bcrypt.lib /OUT:"' + $dll + '"'
Push-Location -LiteralPath $outputRoot
try { & $env:ComSpec /d /s /c $compile; if ($LASTEXITCODE -ne 0) { throw 'Independent ML-DSA reference build failed.' } }
finally { Pop-Location }
$skeinRoot = Join-Path $repoRoot 'Tests\Reference\Skein'
$skeinSourceHashes = @{
    'skein.c' = '73cff8b3470ba1bf8fef7adc99fc1b78a58682a39debdbdba337e74e2c0899b9'
    'skein_block.c' = '8a310dce6a17b98fcf061faa9e75a82f70b535c174a1d2ba8c8b0b27804bbce8'
    'skein.h' = '2988f8efa71095441b31654a51034df50a74f09912c6f9fcb4bd14dece37d30d'
    'skein_port.h' = 'e99efc1a1e09a2307379576fb2096e5ae06ada34f92a60845917dd99f09ad2b8'
    'brg_types.h' = '240329b4ca4d829ac4d1490e96e83118e161e719e448c7e8dbf15735ab8a8e87'
}
foreach ($name in $skeinSourceHashes.Keys) {
    if ((Get-FileHash -LiteralPath (Join-Path $skeinRoot $name) -Algorithm SHA256).Hash.ToLowerInvariant() -ne $skeinSourceHashes[$name]) { throw "Skein reference source hash mismatch: $name" }
}
# The published JSON fixture is a text file with automatic CRLF checkout on Windows.
# Normalize only its line endings; the cryptographic C sources above remain byte-pinned.
$vectorText = [IO.File]::ReadAllText((Join-Path $skeinRoot 'vectors.json')).Replace("`r`n", "`n")
$vectorHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($vectorText)))
if ($vectorHash -ne 'DE4EB02118816273D87ACD04C42739E55133A8446324E75E21288A39A285D2DD') { throw 'Published Skein vector fixture hash mismatch.' }
$skeinSources = (@('file_checksum.c', 'skein.c', 'skein_block.c') | ForEach-Object { '"' + (Join-Path $skeinRoot $_) + '"' }) -join ' '
$skeinExe = Join-Path $outputRoot 'skein-reference-checksum.exe'
$skeinCompile = 'call "' + $vcvars + '" >nul && cl.exe /nologo /O2 /W3 /D_CRT_SECURE_NO_WARNINGS /DSKEIN_ERR_CHECK=1 ' + $skeinSources + ' /link /OUT:"' + $skeinExe + '"'
$sha3Exe = Join-Path $outputRoot 'sha3-reference-checksum.exe'
$sha3Compile = 'call "' + $vcvars + '" >nul && cl.exe /nologo /O2 /W3 /D_CRT_SECURE_NO_WARNINGS /I"' + (Join-Path $referenceRoot 'ref') + '" "' + (Join-Path $PSScriptRoot 'sha3_reference_checksum.c') + '" "' + (Join-Path $referenceRoot 'ref\fips202.c') + '" /link /OUT:"' + $sha3Exe + '"'
Push-Location -LiteralPath $outputRoot
try {
    & $env:ComSpec /d /s /c $skeinCompile
    if ($LASTEXITCODE -ne 0) { throw 'Independent Skein reference build failed.' }
    & $env:ComSpec /d /s /c $sha3Compile
    if ($LASTEXITCODE -ne 0) { throw 'Independent SHA3 reference build failed.' }
}
finally { Pop-Location }
Write-Output $dll
Write-Output $skeinExe
Write-Output $sha3Exe
