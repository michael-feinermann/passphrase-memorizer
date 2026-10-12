param([string]$OutputDirectory = (Join-Path $PSScriptRoot '..\.build\windows-runtime\dist'))
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$buildRoot = Join-Path $projectRoot '.build\windows-runtime'
$source = Join-Path $buildRoot 'source'
$revision = '8e1642198dcd4e408f8776222d6ae31b74d01187'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$vs) { throw 'Visual Studio C++ Build Tools are required.' }
$cmake = Join-Path $vs 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
if (!(Test-Path -LiteralPath $cmake)) { throw 'Visual Studio CMake component is required.' }
if (!(Test-Path -LiteralPath (Join-Path $source '.git'))) {
    New-Item -ItemType Directory -Path $source -Force | Out-Null
    & git init $source
    if ($LASTEXITCODE) { throw 'Cannot initialize pinned source.' }
    & git -C $source remote add origin https://github.com/ggml-org/llama.cpp.git
    if ($LASTEXITCODE) { throw 'Cannot configure pinned source.' }
    & git -C $source fetch --depth 1 origin $revision
    if ($LASTEXITCODE) { throw 'Cannot fetch pinned source.' }
    & git -C $source checkout --detach FETCH_HEAD
    if ($LASTEXITCODE) { throw 'Cannot select pinned source.' }
}
if ((& git -C $source rev-parse HEAD) -ne $revision) { throw 'llama.cpp revision mismatch.' }
if (& git -C $source status --porcelain) { throw 'Pinned llama.cpp source contains local changes.' }
$nativeBuild = Join-Path $buildRoot 'build'
& $cmake -S (Join-Path $projectRoot 'Native\WindowsMnemonicRunner') -B $nativeBuild -G 'Visual Studio 17 2022' -A x64 "-DLLAMA_SOURCE_DIR=$source" -DGGML_NATIVE=OFF -DGGML_AVX2=ON -DGGML_AVX512=OFF -DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded
if ($LASTEXITCODE) { throw 'Native runtime configuration failed.' }
& $cmake --build $nativeBuild --config Release --target WindowsMnemonicRunner --parallel 8
if ($LASTEXITCODE) { throw 'Native runtime build failed.' }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $nativeBuild 'Release\WindowsMnemonicRunner.exe') -Destination $OutputDirectory -Force
Write-Output ([IO.Path]::GetFullPath((Join-Path $OutputDirectory 'WindowsMnemonicRunner.exe')))
