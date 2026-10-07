#!/bin/zsh -f
set -euo pipefail
umask 077
PATH=/usr/bin:/bin:/usr/sbin:/sbin
export PATH
unset ZDOTDIR ENV BASH_ENV CDPATH PYTHONHOME PYTHONPATH NODE_OPTIONS \
  DYLD_INSERT_LIBRARIES DYLD_LIBRARY_PATH DYLD_FRAMEWORK_PATH \
  DYLD_FALLBACK_LIBRARY_PATH DYLD_FALLBACK_FRAMEWORK_PATH

project_root=${0:A:h:h}
source_root=${project_root}/Signing/HybridSigner
output=${project_root}/build/hybrid-signer-singlefile-publish
if [[ $# == 2 && $1 == --output && $2 == /* ]]; then output=$2
elif [[ $# != 0 ]]; then
  print -u2 'Usage: build-hybrid-signer.sh [--output ABSOLUTE_NEW_DIRECTORY]'; exit 64;
fi
[[ ! -e ${output} && ! -L ${output} ]] || {
  print -u2 'Signer output already exists; choose a fresh --output directory'; exit 2;
}
work=$(/usr/bin/mktemp -d /private/tmp/passphrase-memorizer-signer-build.XXXXXXXX)
print -r -- "Public signer build workspace: ${work}"
mkdir -m 0700 ${work}/source ${work}/scratch ${work}/cli-home ${work}/packages ${work}/http-cache
# The build does not inspect any key files. Keep intermediate outputs separate
# from the repository and never read previously compiled assemblies.
/usr/bin/ditto ${source_root} ${work}/source
project=${work}/source/PassphraseMemorizer.HybridSigner.csproj
lock=${work}/source/packages.lock.json
expected_lock=744771db27559112f08caea53a584b9b0cf5618f85a679bb703efd8858f44d33
actual_lock=$(/usr/bin/shasum -a 256 ${lock} | /usr/bin/awk '{print $1}')
[[ ${actual_lock} == ${expected_lock} ]] || { print -u2 'Signer lockfile pin mismatch'; exit 2; }
${project_root}/Scripts/provision-verified-dotnet.sh --target ${work}/dotnet-sdk

run_dotnet() {
  /usr/bin/env -i PATH=${PATH} TMPDIR=${work}/scratch/ \
    DOTNET_CLI_HOME=${work}/cli-home NUGET_PACKAGES=${work}/packages \
    NUGET_HTTP_CACHE_PATH=${work}/http-cache NUGET_PLUGINS_CACHE_PATH=${work}/scratch/plugins \
    DOTNET_EnableDiagnostics=0 COMPlus_EnableDiagnostics=0 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 \
    DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER=1 \
    ${work}/dotnet-sdk/dotnet "$@"
}
cd ${work}/source
[[ $(run_dotnet --version) == 10.0.400 ]] || { print -u2 'Unexpected SDK version'; exit 2; }
run_dotnet restore ${project} --locked-mode --force --no-http-cache \
  --configfile ${work}/source/NuGet.Config --artifacts-path ${work}/artifacts \
  --disable-build-servers -p:UseSharedCompilation=false
run_dotnet publish ${project} -c Release --self-contained true --no-restore \
  --artifacts-path ${work}/artifacts --output ${work}/publish \
  --disable-build-servers -p:UseSharedCompilation=false -warnaserror
[[ $(/usr/bin/shasum -a 256 ${lock} | /usr/bin/awk '{print $1}') == ${expected_lock} ]] || {
  print -u2 'Signer lockfile changed'; exit 2;
}

reference=${work}/source/Reference
cd ${reference}
/usr/bin/shasum -a 256 -c SOURCE_SHA256SUMS
[[ $(cat PINNED_COMMIT.txt) == d35ba3fe5449bee3e6d43e1f296c3ca818bd36be ]] || {
  print -u2 'ML-DSA reference pin mismatch'; exit 2;
}
native_sources=(sign.c packing.c polyvec.c poly.c ntt.c reduce.c rounding.c symmetric-shake.c fips202.c)
native_paths=()
for file in ${native_sources[@]}; do native_paths+=(${reference}/ref/${file}); done
/usr/bin/xcrun -sdk macosx clang -arch arm64 -mmacosx-version-min=14.0 \
  -O3 -fPIC -dynamiclib -DDILITHIUM_MODE=5 -I${reference}/ref \
  -install_name @rpath/libmldsa87_ref.dylib -Wl,-dead_strip -Wl,-fatal_warnings \
  ${reference}/mldsa87_ref_export.c ${native_paths[@]} -framework Security \
  -o ${work}/publish/libmldsa87_ref.dylib

mkdir -m 0700 ${work}/publish/Licenses
cp ${work}/source/Reference/LICENSE ${work}/publish/Licenses/ML-DSA-reference.txt
cp ${work}/source/README.md ${work}/publish/README.md
cp ${work}/source/SOURCE_PROVENANCE.json ${work}/publish/SOURCE_PROVENANCE.json
for license in LICENSE.txt ThirdPartyNotices.txt; do
  cp ${work}/dotnet-sdk/${license} ${work}/publish/Licenses/dotnet-${license}
done
bc_license=${work}/packages/bouncycastle.cryptography/2.6.2/LICENSE.md
[[ -f ${bc_license} ]] || { print -u2 'Bouncy Castle package license missing'; exit 2; }
cp ${bc_license} ${work}/publish/Licenses/BouncyCastle-LICENSE.md
# Managed assemblies and runtime JSON must be embedded in the Mach-O host.
# Native libraries remain ordinary signed files, never a runtime extraction.
# Resources are moved into Contents/Resources by the separate app packager.
if [[ -n $(/usr/bin/find ${work}/publish -maxdepth 1 -type f \( -name '*.dll' -o -name '*.deps.json' -o -name '*.runtimeconfig.json' \) -print -quit) ]]; then
  print -u2 'Single-file publish unexpectedly left loose managed/runtime files'; exit 2
fi
if [[ -n $(/usr/bin/find ${work}/publish -mindepth 1 ! -type d ! -type f -print -quit) ]]; then
  print -u2 'Signer publish contains a symlink or special object'; exit 2
fi
[[ ! -e ${output} && ! -L ${output} ]] || {
  print -u2 'Signer output already exists; select a fresh build instead of overwriting'; exit 2;
}
mkdir -p ${project_root}/build
/usr/bin/ditto ${work}/publish ${output}
print -r -- "Self-contained public signer ready: ${output}"
print -r -- 'Root packaging must Apple-sign/notarize the separate Signer.app before creating its ZIP and detached hybrid signatures.'
