#!/bin/bash
set -euo pipefail

PROJECT_DIR="$(cd "$(dirname "$0")/.." && pwd)"
if [[ "$#" == 0 ]]; then
    APP_PATH="$PROJECT_DIR/build/Passphrase Memorizer.app"
    ZIP_PATH="$PROJECT_DIR/build/Passphrase-Memorizer-1.0.0.zip"
elif [[ "$#" == 1 || "$#" == 2 ]]; then
    APP_PATH="$1"; ZIP_PATH="${2:-}"
else echo "Usage: $0 [APP_PATH [ZIP_PATH]]" >&2; exit 64
fi
ALLOW_DEVELOPMENT="${ALLOW_UNNOTARIZED_DEVELOPMENT:-0}"
[[ "$ALLOW_DEVELOPMENT" == 0 || "$ALLOW_DEVELOPMENT" == 1 ]] || { echo "Invalid development override." >&2; exit 1; }
[[ "$(uname -s)" == "Darwin" && -d "$APP_PATH" ]] || { echo "A macOS app bundle is required." >&2; exit 1; }
APP_PATH="$(cd "$APP_PATH" && pwd)"
APP_BINARY="$APP_PATH/Contents/MacOS/MnemonicStoryApp"
HELPER="$APP_PATH/Contents/Helpers/LocalMnemonicRunner"
[[ -x "$APP_BINARY" && -x "$HELPER" ]] || { echo "App or embedded helper is missing." >&2; exit 1; }
codesign --verify --deep --strict --verbose=2 "$APP_PATH"
plutil -lint "$APP_PATH/Contents/Info.plist"

python3 - "$APP_PATH" "$ALLOW_DEVELOPMENT" "$PROJECT_DIR/Scripts" <<'PY'
import hashlib, json, pathlib, plistlib, re, struct, subprocess, sys

app = pathlib.Path(sys.argv[1])
development = sys.argv[2] == "1"
sys.path.insert(0, sys.argv[3])
from release_checks import validate_entitlements
def require(condition, message):
    if not condition:
        raise SystemExit(message)

with (app / "Contents/Info.plist").open("rb") as stream:
    info = plistlib.load(stream)
expected = {"CFBundleIdentifier": "local.passphrasereminder.reminder", "CFBundleExecutable": "MnemonicStoryApp",
            "CFBundleDisplayName": "Passphrase Memorizer", "CFBundleName": "Passphrase Memorizer",
            "CFBundleIconFile": "AppIcon",
            "CFBundleShortVersionString": "1.0.0", "CFBundleVersion": "1", "LSMinimumSystemVersion": "14.0",
            "CFBundleLocalizations": ["en", "de"], "NSSupportsAutomaticTermination": False,
            "NSSupportsSuddenTermination": False}
for key, value in expected.items():
    require(info.get(key) == value, "Unexpected bundle metadata: " + key)

targets = [(app, "local.passphrasereminder.reminder"),
           (app / "Contents/Helpers/LocalMnemonicRunner", "local.passphrasereminder.reminder.runner")]
modes = []
for target, identifier in targets:
    subprocess.run(["codesign", "--verify", "--strict", "--all-architectures", str(target)], check=True)
    result = subprocess.run(["codesign", "-dv", "--verbose=4", str(target)], capture_output=True, text=True, check=True)
    description = result.stderr
    require("Identifier=" + identifier + "\n" in description, "Unexpected signing identifier")
    require(re.search(r"flags=.*\bruntime\b", description), "Hardened Runtime is missing")
    adhoc = "Signature=adhoc" in description
    if adhoc:
        require(development, "Ad-hoc code is not a public release")
    else:
        require("Authority=Developer ID Application:" in description and "TeamIdentifier=2T6K9PGS55\n" in description,
                "Expected Developer ID Application team 2T6K9PGS55")
    modes.append(adhoc)
    ent = subprocess.run(["codesign", "-d", "--entitlements", "-", "--xml", str(target)], capture_output=True, check=True).stdout
    entitlements = plistlib.loads(ent) if ent.strip() else {}
    validate_entitlements(entitlements, identifier)
require(modes[0] == modes[1], "App and helper signature modes differ")

for binary in [app / "Contents/MacOS/MnemonicStoryApp", targets[1][0]]:
    dependencies = subprocess.check_output(["otool", "-L", str(binary)], text=True).splitlines()[1:]
    require(all(line.strip().split()[0].startswith(("/usr/lib/", "/System/Library/")) for line in dependencies),
            "External runtime dependency")
    loads = subprocess.check_output(["otool", "-l", str(binary)], text=True)
    rpaths = re.findall(r"cmd LC_RPATH\s+cmdsize \d+\s+path (.*?) \(offset", loads)
    require(all(path == "/usr/lib/swift" for path in rpaths), "External build-machine RPATH")

hashes = {"english.txt": "2f5eed53a4727b4bf8880d8f3f199efc90e58503646d9ff8eff3a2ed3b24dbda",
          "eff_large_wordlist.txt": "addd35536511597a02fa0a9ff1e5284677b8883b83e986e43f15a3db996b903e"}
for name, expected_hash in hashes.items():
    require(hashlib.sha256((app / "Contents/Resources" / name).read_bytes()).hexdigest() == expected_hash,
            "Wordlist integrity failed: " + name)
for name in ["AppIcon.icns", "LICENSE", "THIRD_PARTY_NOTICES.md", "Licenses/python-mnemonic-MIT.txt", "Licenses/llama.cpp-LICENSE.txt"]:
    require((app / "Contents/Resources" / name).is_file(), "Required resource missing: " + name)
for path in app.rglob("*"):
    require(not path.is_symlink(), "Unexpected bundle symlink")
    require(path.suffix.lower() not in {".gguf", ".safetensors", ".pt", ".pth", ".onnx", ".mlx", ".bin"}, "Model weights are bundled")

# Public, non-secret self-test. Both transports must be pipes, never disk files.
result = subprocess.run([str(targets[1][0]), "--self-test"], input=b"", capture_output=True, timeout=45,
                        env={"PATH": "/usr/bin:/bin", "LC_ALL": "C"})
require(result.returncode == 0 and result.stderr == b"", "Embedded helper isolation self-test failed")
output = result.stdout
require(len(output) >= 8 and output[:4] == b"MSAI", "Bad isolation handshake")
length = struct.unpack("<I", output[4:8])[0]
require(0 < length <= 65_536 and len(output) == length + 8, "Bad self-test framing")
expected_probe = {"sandbox": "passed", "ipv4": "denied", "ipv6": "denied", "writes": "denied", "unrelated_reads": "denied"}
require(json.loads(output[8:].decode("utf-8")) == expected_probe, "Unexpected isolation probe result")
print("Embedded helper isolation: IPv4/IPv6 traffic, writes and unrelated reads denied.")
# A release must prove computation on Metal after installing the production
# deny-default policy, rather than merely enumerating a GPU or falling back.
result = subprocess.run([str(targets[1][0]), "--self-test-metal"], input=b"", capture_output=True, timeout=90,
                        env={"PATH": "/usr/bin:/bin", "LC_ALL": "C"})
require(result.returncode == 0 and result.stderr == b"", "Embedded helper Metal computation self-test failed")
output = result.stdout
require(len(output) >= 8 and output[:4] == b"MSAI", "Bad Metal isolation handshake")
length = struct.unpack("<I", output[4:8])[0]
require(0 < length <= 65_536 and len(output) == length + 8, "Bad Metal self-test framing")
metal_probe = json.loads(output[8:].decode("utf-8"))
require(isinstance(metal_probe.get("metal_tensor_kernels"), bool), "Invalid Metal tensor capability result")
expected_metal_probe = {**expected_probe, "backend": "Metal", "gpu_computation": "passed",
                        "metal_tensor_kernels": metal_probe["metal_tensor_kernels"]}
require(metal_probe == expected_metal_probe, "Unexpected Metal isolation probe result")
print("Embedded helper Metal: actual GPU computation passed; all denials remain enforced.")
PY

if [[ "$ALLOW_DEVELOPMENT" == 0 ]]; then
    xcrun stapler validate "$APP_PATH"
    spctl --assess --type execute --verbose=2 "$APP_PATH"
else
    echo "Explicit development verification: notarization and Gatekeeper are not required."
fi

if [[ -n "$ZIP_PATH" ]]; then
    [[ -f "$ZIP_PATH" && -f "$ZIP_PATH.sha256" ]] || { echo "Release archive or SHA-256 sidecar is missing." >&2; exit 1; }
    ZIP_PATH="$(cd "$(dirname "$ZIP_PATH")" && pwd)/$(basename "$ZIP_PATH")"
    python3 - "$ZIP_PATH" "$PROJECT_DIR/Scripts" <<'PY'
import hashlib, pathlib, sys, zipfile
sys.path.insert(0, sys.argv[2])
from release_checks import preflight_archive
path = pathlib.Path(sys.argv[1])
line = pathlib.Path(str(path) + ".sha256").read_text().strip().split()
if len(line) != 2 or line[1] != path.name:
    raise SystemExit("Malformed SHA-256 sidecar")
digest = hashlib.sha256()
with path.open("rb") as stream:
    for chunk in iter(lambda: stream.read(1024 * 1024), b""):
        digest.update(chunk)
digest = digest.hexdigest()
if line[0] != digest:
    raise SystemExit("Archive SHA-256 mismatch")
with zipfile.ZipFile(path) as archive:
    preflight_archive(archive)
    if archive.testzip() is not None:
        raise SystemExit("Corrupt release archive")
print("Archive SHA-256 and ZIP integrity verified.")
PY
    VERIFY_DIR="$(mktemp -d "${TMPDIR:-/tmp}/PassphraseReminder-verify.XXXXXX")"
    trap 'rm -rf "$VERIFY_DIR"' EXIT
    ditto -x -k "$ZIP_PATH" "$VERIFY_DIR"
    # Verify the actual exported app, including its ticket and executable probe.
    ALLOW_UNNOTARIZED_DEVELOPMENT="$ALLOW_DEVELOPMENT" "$0" "$VERIFY_DIR/Passphrase Memorizer.app"
    python3 - "$APP_PATH" "$VERIFY_DIR/Passphrase Memorizer.app" <<'PY'
import hashlib, pathlib, sys
def digest(path):
    value = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            value.update(chunk)
    return value.hexdigest()
def inventory(root):
    return {str(path.relative_to(root)): ("file", digest(path)) if path.is_file() else ("directory", None)
            for path in root.rglob("*")}
if inventory(pathlib.Path(sys.argv[1])) != inventory(pathlib.Path(sys.argv[2])):
    raise SystemExit("Exported app differs from the verified bundle")
print("Exported app matches the verified bundle.")
PY
fi
echo "Verification passed: $APP_PATH"
