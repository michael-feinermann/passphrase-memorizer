#!/bin/sh
set -eu
umask 077

if [ "$#" -ne 1 ]; then
    echo "Usage: skein-reference-checksum.sh <file>" >&2
    exit 64
fi
if [ ! -f "$1" ] || [ ! -r "$1" ]; then
    echo "skein-reference-checksum: input must be a readable regular file" >&2
    exit 66
fi

SKEIN_SCRIPT_DIR="$(CDPATH= cd -- "$(dirname -- "$0")" && pwd -P)"
SKEIN_REFERENCE_DIR="$SKEIN_SCRIPT_DIR/../Tests/Reference/Skein"
SKEIN_COMPILER="$(/usr/bin/xcrun --sdk macosx --find clang)"
SKEIN_SDK="$(/usr/bin/xcrun --sdk macosx --show-sdk-path)"
SKEIN_TEMP_DIR="$(/usr/bin/mktemp -d "${TMPDIR:-/tmp}/passphrase-memorizer-skein.XXXXXXXX")"

cleanup() {
    /bin/rm -rf -- "$SKEIN_TEMP_DIR"
}
trap cleanup EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM

"$SKEIN_COMPILER" -std=c99 -O2 -isysroot "$SKEIN_SDK" -DSKEIN_ERR_CHECK=1 \
    "$SKEIN_REFERENCE_DIR/file_checksum.c" \
    "$SKEIN_REFERENCE_DIR/skein.c" \
    "$SKEIN_REFERENCE_DIR/skein_block.c" \
    -o "$SKEIN_TEMP_DIR/skein-reference-checksum" >&2

"$SKEIN_TEMP_DIR/skein-reference-checksum" "$1"
