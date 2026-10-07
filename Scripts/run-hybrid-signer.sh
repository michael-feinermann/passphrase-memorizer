#!/bin/zsh -f
set -euo pipefail
umask 077
ulimit -c 0
PATH=/usr/bin:/bin:/usr/sbin:/sbin
export PATH
unset ZDOTDIR ENV BASH_ENV CDPATH PYTHONHOME PYTHONPATH NODE_OPTIONS \
  DYLD_INSERT_LIBRARIES DYLD_LIBRARY_PATH DYLD_FRAMEWORK_PATH \
  DYLD_FALLBACK_LIBRARY_PATH DYLD_FALLBACK_FRAMEWORK_PATH

[[ $# -ge 3 && $1 == --signer-dir && $2 == /* ]] || {
  print -u2 'Usage: run-hybrid-signer.sh --signer-dir ABSOLUTE_DIRECTORY COMMAND [OPTIONS]'; exit 64;
}
signer_directory=$2
shift 2
signer_directory=${signer_directory:A}
[[ ${signer_directory} == *.app/Contents/MacOS ]] || {
  print -u2 'Production signing requires the complete signed Signer.app bundle'; exit 2;
}
signer_app=${signer_directory:h:h}
signer=${signer_directory}/PassphraseMemorizer.HybridSigner
[[ -f ${signer} && ! -L ${signer} && -x ${signer} ]] || {
  print -u2 'The explicitly selected physical signer executable is missing'; exit 2;
}
command=${1:l}
if [[ ${command} == sign || ${command} == release-keygen ]]; then
  expected_reference=${signer_directory}/libmldsa87_ref.dylib
  reference_count=0
  for (( index = 2; index <= $#; index += 2 )); do
    (( index + 1 <= $# )) || { print -u2 'Incomplete signing option'; exit 2; }
    if [[ ${argv[index]} == --reference-library ]]; then
      (( reference_count += 1 ))
      [[ ${argv[index+1]} == ${expected_reference} \
        && -f ${expected_reference} && ! -L ${expected_reference} ]] || {
        print -u2 'Signing requires the sealed native reference inside this Signer.app'; exit 2;
      }
    fi
  done
  (( reference_count == 1 )) || {
    print -u2 'Signing requires exactly one sealed --reference-library'; exit 2;
  }
fi
# Managed assemblies are embedded in the signed Mach-O host. Still verify the
# complete bundle, including its native libraries and all resource bytes.
/usr/bin/codesign --verify --deep --strict ${signer_app}
/usr/bin/codesign --verify --strict ${signer}
signature=$(/usr/bin/codesign -dv --verbose=4 ${signer_app} 2>&1)
[[ ${signature} == *'TeamIdentifier=2T6K9PGS55'* \
  && ${signature} == *'Identifier=local.passphrasememorizer.hybridsigner'* \
  && ${signature} == *'flags=0x10000(runtime)'* ]] || {
  print -u2 'The selected signer lacks the expected Developer ID Hardened Runtime identity'; exit 2;
}
/usr/bin/env -i PATH=${PATH} /usr/bin/python3 -I - ${signer_app} ${signer} <<'PY'
import pathlib,plistlib,subprocess,sys
app,host=map(pathlib.Path,sys.argv[1:])
try:
    info_path=app/'Contents/Info.plist'
    if info_path.is_symlink() or not info_path.is_file() or info_path.stat().st_size>65536:
        raise ValueError('Invalid signer metadata')
    info=plistlib.loads(info_path.read_bytes())
    expected={'CFBundleIdentifier':'local.passphrasememorizer.hybridsigner',
              'CFBundleExecutable':'PassphraseMemorizer.HybridSigner',
              'CFBundleShortVersionString':'1.0.0','CFBundleVersion':'1'}
    if any(info.get(key)!=value for key,value in expected.items()):
        raise ValueError('Wrong signer identity/version')
    for code in (app,host):
        result=subprocess.run(['/usr/bin/codesign','-d','--entitlements',':-',str(code)],
                              capture_output=True,timeout=30)
        if result.returncode:
            raise ValueError('Cannot inspect signer entitlements')
        entitlements=plistlib.loads(result.stdout)
        if (set(entitlements)!= {'com.apple.security.cs.allow-jit'} or
                entitlements['com.apple.security.cs.allow-jit'] is not True):
            raise ValueError('Signer must have only allow-jit entitlement')
except (OSError,ValueError,plistlib.InvalidFileException,subprocess.SubprocessError) as error:
    print('Signer bundle preflight rejected: '+str(error),file=sys.stderr)
    sys.exit(2)
PY
scratch=$(/usr/bin/mktemp -d /private/tmp/passphrase-memorizer-signing.XXXXXXXX)
keychains_before=$(/usr/bin/security list-keychains -d user)
cleanup() {
  local prior_status=$?
  local cleanup_failed=0
  local keychains_after=$(/usr/bin/security list-keychains -d user)
  if [[ ${keychains_after} != ${keychains_before} ]]; then
    print -u2 'Signing changed the user keychain inventory'; cleanup_failed=1;
  fi
  if [[ -n $(/usr/bin/find ${scratch} -mindepth 1 -print -quit) ]]; then
    print -u2 "Signing left a temporary object; preserved for review: ${scratch}"; cleanup_failed=1;
  else
    /bin/rmdir ${scratch} || cleanup_failed=1
  fi
  (( cleanup_failed == 0 )) || exit 2
  exit ${prior_status}
}
trap cleanup EXIT
/usr/bin/env -i PATH=${PATH} TMPDIR=${scratch}/ KEEPVAULT_KEYCHAIN_TEMP_ROOT=${scratch} \
  DOTNET_EnableDiagnostics=0 COMPlus_EnableDiagnostics=0 \
  DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 \
  ${signer} "$@"
