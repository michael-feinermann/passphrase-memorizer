#!/bin/bash
set -euo pipefail

# Executes the actual RuntimeGuard source in a signed command-line process.
# No app UI, model, phrase or inference worker is opened by this regression test.
PROJECT_DIR="$(cd "$(dirname "$0")/.." && pwd)"
SIGN_IDENTITY="${SIGN_IDENTITY:-}"
[[ -n "$SIGN_IDENTITY" && "$SIGN_IDENTITY" != "-" ]] || {
    echo "Set SIGN_IDENTITY to the expected team's Developer ID Application identity." >&2
    exit 64
}
TEST_DIR="$PROJECT_DIR/.build/runtime-guard-tests"
mkdir -p "$TEST_DIR"
cat > "$TEST_DIR/main.swift" <<'SWIFT'
import Darwin
import Foundation
let accepted = RuntimeGuard.current()
print(accepted ? "runtime-guard=accepted" : "runtime-guard=rejected")
exit(accepted ? 0 : 1)
SWIFT
swiftc -warnings-as-errors -O "$PROJECT_DIR/Sources/MnemonicStoryApp/RuntimeGuard.swift" \
    "$TEST_DIR/main.swift" -o "$TEST_DIR/RuntimeGuardProbe"
codesign --force --options runtime --timestamp=none --identifier local.passphrasereminder.reminder \
    --sign "$SIGN_IDENTITY" "$TEST_DIR/RuntimeGuardProbe"
"$TEST_DIR/RuntimeGuardProbe"
codesign --force --options runtime --timestamp=none --identifier local.passphrasereminder.unexpected \
    --sign "$SIGN_IDENTITY" "$TEST_DIR/RuntimeGuardProbe"
if "$TEST_DIR/RuntimeGuardProbe"; then
    echo "Wrong signing identifier was unexpectedly accepted." >&2; exit 1
fi
codesign --force --options runtime --timestamp=none --identifier local.passphrasereminder.reminder \
    --sign - "$TEST_DIR/RuntimeGuardProbe"
if "$TEST_DIR/RuntimeGuardProbe"; then
    echo "Ad-hoc code was unexpectedly accepted." >&2; exit 1
fi
echo "RuntimeGuard integration passed: Developer ID accepted; wrong identifier and ad-hoc code rejected."
