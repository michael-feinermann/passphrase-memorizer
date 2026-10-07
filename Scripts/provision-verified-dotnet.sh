#!/bin/zsh -f
set -euo pipefail
umask 077
PATH='/usr/bin:/bin:/usr/sbin:/sbin'
export PATH
unset ZDOTDIR ENV BASH_ENV CDPATH PERL5OPT PERL5LIB PYTHONHOME PYTHONPATH \
  RUBYOPT RUBYLIB NODE_OPTIONS OPENSSL_CONF OPENSSL_MODULES SSL_CERT_FILE \
  SSL_CERT_DIR CURL_HOME XDG_CONFIG_HOME DYLD_INSERT_LIBRARIES \
  DYLD_LIBRARY_PATH DYLD_FRAMEWORK_PATH DYLD_FALLBACK_LIBRARY_PATH \
  DYLD_FALLBACK_FRAMEWORK_PATH

# Release builds never execute an ambient SDK installation. This script obtains
# the exact Microsoft archive whose SHA-512 is pinned in Microsoft's .NET 10
# release metadata, verifies it before extraction, and creates a fresh private
# SDK tree for one build invocation.
sdk_version='10.0.400'
sdk_url='https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.400/dotnet-sdk-10.0.400-osx-arm64.tar.gz'
sdk_sha512='e440e9a58d4ff7741c8342ac3e086fa9ee2dadc25e01c0449a88317a74cfbd63625b8092c3b2a131ae14b16ab3401e9cc470e578e4c65a72a0b5786bd2308cde'
private_temp_parent=/private/tmp
default_archive=${private_temp_parent}/passphrase-memorizer-dotnet-sdk-${sdk_version}-osx-arm64-${EUID}.tar.gz

curl_path=/usr/bin/curl
tar_path=/usr/bin/bsdtar
shasum_path=/usr/bin/shasum
stat_path=/usr/bin/stat
find_path=/usr/bin/find
grep_path=/usr/bin/grep
awk_path=/usr/bin/awk
codesign_path=/usr/bin/codesign
mktemp_path=/usr/bin/mktemp
chmod_path=/bin/chmod
mkdir_path=/bin/mkdir
ln_path=/bin/ln
unlink_path=/bin/unlink
env_path=/usr/bin/env
cp_path=/bin/cp

require_root_system_tool() {
  local tool=$1
  if [[ ${tool} != /* || ! -f ${tool} || -L ${tool} || ! -x ${tool} \
      || $(${stat_path} -f '%u' ${tool} 2>/dev/null || print invalid) != 0 ]]; then
    print -u2 "SDK GATE: required system tool is not a physical root-owned executable: ${tool}"
    exit 2
  fi
  local mode=$(( 8#$(${stat_path} -f '%Lp' ${tool}) ))
  if (( (mode & 8#022) != 0 )); then
    print -u2 "SDK GATE: required system tool is group/other writable: ${tool}"
    exit 2
  fi
}
for fixed_tool in ${curl_path} ${tar_path} ${shasum_path} ${stat_path} \
    ${find_path} ${grep_path} ${awk_path} ${codesign_path} ${mktemp_path} ${chmod_path} \
    ${mkdir_path} ${ln_path} ${unlink_path} ${env_path} ${cp_path}; do
  require_root_system_tool ${fixed_tool}
done

target=''
archive=${PASSPHRASE_MEMORIZER_DOTNET_SDK_ARCHIVE:-${default_archive}}
tool_path_self_test=0

usage() {
  print -u2 'Usage: provision-verified-dotnet.sh --target PRIVATE_EMPTY_DIRECTORY [--archive FILE]'
  print -u2 '       provision-verified-dotnet.sh --tool-path-self-test'
  exit 64
}

while (( $# != 0 )); do
  case $1 in
    --target) (( $# >= 2 )) || usage; target=$2; shift 2 ;;
    --archive) (( $# >= 2 )) || usage; archive=$2; shift 2 ;;
    --tool-path-self-test) tool_path_self_test=1; shift ;;
    *) usage ;;
  esac
done

if (( tool_path_self_test )); then
  print 'verified_dotnet_tool_paths=verified'
  exit 0
fi
[[ -n ${target} && ${target} == /* ]] || usage

private_temp_uid=$(${stat_path} -f '%u' ${private_temp_parent} 2>/dev/null || print invalid)
private_temp_mode=$(( 8#$(${stat_path} -f '%p' ${private_temp_parent} 2>/dev/null || print 0) & 8#7777 ))
if [[ ! -d ${private_temp_parent} || -L ${private_temp_parent} \
    || ${private_temp_uid} != 0 ]] || (( private_temp_mode != 8#1777 )); then
  print -u2 'SDK GATE: /private/tmp must be a physical root-owned mode-1777 directory.'
  exit 2
fi

script_dir=${0:A:h}
repo_root=${script_dir:h}
target_parent=${target:h}
target_name=${target:t}
target_parent=${target_parent:A}
target=${target_parent}/${target_name}
if [[ ${target} == ${repo_root}/* || -e ${target} || -L ${target} \
    || ! -d ${target_parent} || -L ${target_parent} \
    || $(${stat_path} -f '%u' ${target_parent} 2>/dev/null || print invalid) != ${EUID} ]]; then
  print -u2 'SDK GATE: target must be a new directory below a physical caller-owned parent outside the repository.'
  exit 2
fi
parent_mode=$(( 8#$(${stat_path} -f '%Lp' ${target_parent}) ))
if (( (parent_mode & 8#077) != 0 )); then
  print -u2 'SDK GATE: target parent must be private to the current user.'
  exit 2
fi
target_parent_identity=$(${stat_path} -f '%d:%i:%u:%Lp' ${target_parent})

download_tmp=''
download_tmp_identity=''
archive_snapshot=''
archive_snapshot_identity=''
cleanup_download() {
  set +e
  if [[ -n ${download_tmp:-} && -f ${download_tmp} && ! -L ${download_tmp} \
      && $(${stat_path} -f '%d:%i' ${download_tmp} 2>/dev/null || print invalid) == ${download_tmp_identity:-invalid} \
      && ${download_tmp} == ${private_temp_parent}/.passphrase-memorizer-dotnet-sdk-download.* ]]; then
    ${unlink_path} ${download_tmp}
  fi
  if [[ -n ${archive_snapshot:-} && -f ${archive_snapshot} && ! -L ${archive_snapshot} \
      && $(${stat_path} -f '%d:%i:%u:%Lp:%l:%z:%m:%c' ${archive_snapshot} 2>/dev/null || print invalid) == ${archive_snapshot_identity:-invalid} \
      && ${archive_snapshot:h} == ${target_parent:-missing} \
      && ${archive_snapshot:t} == .passphrase-memorizer-dotnet-sdk-snapshot.* ]]; then
    ${unlink_path} ${archive_snapshot}
  fi
}
trap cleanup_download EXIT
trap 'cleanup_download; exit 130' INT
trap 'cleanup_download; exit 143' TERM

verify_archive() {
  local candidate=$1
  [[ -f ${candidate} && ! -L ${candidate} \
      && $(${stat_path} -f '%u' ${candidate} 2>/dev/null || print invalid) == ${EUID} ]] || return 1
  local candidate_mode=$(( 8#$(${stat_path} -f '%Lp' ${candidate}) ))
  (( (candidate_mode & 8#077) == 0 )) || return 1
  local identity_before
  local identity_after
  identity_before=$(${stat_path} -f '%d:%i:%u:%Lp:%l:%z:%m:%c' ${candidate} 2>/dev/null) || return 1
  local actual_hash
  actual_hash=$(${env_path} -i PATH=${PATH} \
    ${shasum_path} -a 512 ${candidate} | ${awk_path} '{print tolower($1)}')
  identity_after=$(${stat_path} -f '%d:%i:%u:%Lp:%l:%z:%m:%c' ${candidate} 2>/dev/null) || return 1
  [[ ${identity_before} == ${identity_after} && ${actual_hash} == ${sdk_sha512} ]]
}

archive=${archive:a}
if ! verify_archive ${archive}; then
  download_tmp=$(${mktemp_path} "${private_temp_parent}/.passphrase-memorizer-dotnet-sdk-download.XXXXXXXX")
  ${chmod_path} 0600 ${download_tmp}
  download_tmp_identity=$(${stat_path} -f '%d:%i' ${download_tmp})
  ${env_path} -i PATH=${PATH} \
    ${curl_path} --disable --fail --location --silent --show-error \
    --proto '=https' --proto-redir '=https' --tlsv1.2 \
    --output ${download_tmp} ${sdk_url}
  verify_archive ${download_tmp} || {
    print -u2 'SDK GATE: downloaded Microsoft .NET SDK archive failed its pinned SHA-512.'
    exit 2
  }

  if [[ ${archive} == ${default_archive} && ! -e ${archive} && ! -L ${archive} ]]; then
    if ${ln_path} ${download_tmp} ${archive} 2>/dev/null; then
      ${chmod_path} 0600 ${archive}
    fi
  fi
  if verify_archive ${archive}; then
    archive=${archive:A}
  else
    archive=${download_tmp}
  fi
else
  archive=${archive:A}
fi

if [[ $(${stat_path} -f '%d:%i:%u:%Lp' ${target_parent} 2>/dev/null || print invalid) \
      != ${target_parent_identity} ]]; then
  print -u2 'SDK GATE: private target parent identity changed before SDK snapshotting.'
  exit 2
fi
archive_snapshot=$(${mktemp_path} "${target_parent}/.passphrase-memorizer-dotnet-sdk-snapshot.XXXXXXXX")
${chmod_path} 0600 ${archive_snapshot}
${cp_path} ${archive} ${archive_snapshot}
archive_snapshot_identity=$(${stat_path} -f '%d:%i:%u:%Lp:%l:%z:%m:%c' ${archive_snapshot})
verify_archive ${archive_snapshot} || {
  print -u2 'SDK GATE: private SDK archive snapshot failed its pinned identity and SHA-512 check.'
  exit 2
}

${mkdir_path} -m 0700 ${target}
target_identity=$(${stat_path} -f '%d:%i' ${target})
if [[ $(${stat_path} -f '%d:%i:%u:%Lp' ${target_parent} 2>/dev/null || print invalid) \
      != ${target_parent_identity} ]]; then
  print -u2 'SDK GATE: private target parent identity changed while creating the SDK root.'
  exit 2
fi
${env_path} -i PATH=${PATH} COPYFILE_DISABLE=1 \
  ${tar_path} -xzf ${archive_snapshot} -C ${target}
if [[ $(${stat_path} -f '%d:%i:%u:%Lp:%l:%z:%m:%c' ${archive_snapshot} 2>/dev/null || print invalid) \
      != ${archive_snapshot_identity} ]] || ! verify_archive ${archive_snapshot}; then
  print -u2 'SDK GATE: private SDK archive snapshot changed during extraction.'
  exit 2
fi
if [[ $(${stat_path} -f '%d:%i:%u:%Lp' ${target} 2>/dev/null || print invalid) \
      != ${target_identity}:${EUID}:700 ]]; then
  print -u2 'SDK GATE: private SDK root identity changed during extraction.'
  exit 2
fi
if [[ -n $(${find_path} ${target} -mindepth 1 ! -type d ! -type f -print -quit) ]]; then
  print -u2 'SDK GATE: verified SDK archive extracted a symbolic link or special object.'
  exit 2
fi

while IFS= read -r -d '' extracted_path; do
  if [[ $(${stat_path} -f '%u' ${extracted_path} 2>/dev/null || print invalid) != ${EUID} ]]; then
    print -u2 'SDK GATE: extracted SDK object has an unexpected owner.'
    exit 2
  fi
  extracted_mode=$(( 8#$(${stat_path} -f '%Lp' ${extracted_path}) ))
  if (( (extracted_mode & 8#022) != 0 )); then
    print -u2 'SDK GATE: extracted SDK object is group/other writable.'
    exit 2
  fi
  if [[ -f ${extracted_path} \
      && $(${stat_path} -f '%l' ${extracted_path} 2>/dev/null || print invalid) != 1 ]]; then
    print -u2 'SDK GATE: extracted SDK contains a hard-linked regular file.'
    exit 2
  fi
done < <(${find_path} ${target} -mindepth 1 -print0)

dotnet=${target}/dotnet
if [[ ! -f ${dotnet} || -L ${dotnet} || ! -x ${dotnet} ]]; then
  print -u2 'SDK GATE: verified SDK did not contain its physical executable host.'
  exit 2
fi
${env_path} -i PATH=${PATH} ${codesign_path} --verify --strict ${dotnet}
signature=$(${env_path} -i PATH=${PATH} ${codesign_path} -dv --verbose=4 ${dotnet} 2>&1)
if [[ ${signature} != *'Authority=Developer ID Application: Microsoft Corporation (UBF8T346G9)'* \
    || ${signature} != *'TeamIdentifier=UBF8T346G9'* ]]; then
  print -u2 'SDK GATE: verified SDK host lacks the pinned Microsoft Developer ID identity.'
  exit 2
fi

print -r -- ${dotnet}
