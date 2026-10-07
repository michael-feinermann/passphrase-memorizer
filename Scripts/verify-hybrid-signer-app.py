#!/usr/bin/env python3
"""Verify Apple distribution policy for the separate public Signer.app."""
import argparse
from pathlib import Path
import plistlib
import subprocess

ID = "local.passphrasememorizer.hybridsigner"
HOST = "PassphraseMemorizer.HybridSigner"
ENV = {"PATH": "/usr/bin:/bin:/usr/sbin:/sbin"}


def output(*args):
    result = subprocess.run(list(map(str, args)), capture_output=True, env=ENV, timeout=60)
    if result.returncode:
        raise ValueError(result.stderr.decode(errors="replace").strip())
    return result.stdout, result.stderr


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--app", required=True, type=Path)
    parser.add_argument("--signed-candidate", action="store_true", help="Explicitly skip only Apple notarization gates")
    args = parser.parse_args()
    app = args.app.absolute()
    if app.is_symlink() or not app.is_dir() or app.name != "Passphrase Memorizer Hybrid Signer.app":
        parser.error("Expected the physical, correctly named signer bundle")
    entries = list(app.rglob("*"))
    if any(e.is_symlink() or not (e.is_file() or e.is_dir()) for e in entries):
        parser.error("Signer contains an alias or special object")
    info = plistlib.loads((app / "Contents/Info.plist").read_bytes())
    expected = {"CFBundleIdentifier": ID, "CFBundleExecutable": HOST,
                "CFBundleShortVersionString": "1.0.0", "CFBundleVersion": "1",
                "LSMinimumSystemVersion": "14.0"}
    if any(info.get(k) != v for k, v in expected.items()):
        parser.error("Signer identity or release version differs")
    output("/usr/bin/codesign", "--verify", "--deep", "--strict", app)
    native = list((app / "Contents/MacOS").iterdir())
    if {entry.name for entry in native} != {HOST, "libmldsa87_ref.dylib"}:
        parser.error("Expected the single-file runtime and own native reference only")
    for entry in native:
        with entry.open("rb") as stream:
            magic = stream.read(4)
        if not entry.is_file() or magic != b"\xcf\xfa\xed\xfe":
            parser.error("Code directory must contain only arm64 Mach-O programs")
        attributes, _ = output("/usr/bin/xattr", entry)
        if any(a.startswith("com.apple.cs.") for a in attributes.decode().splitlines()):
            parser.error("Signer may not depend on transferable generic-code xattrs")
        stdout, _ = output("/usr/bin/lipo", "-archs", entry)
        if stdout.decode().strip() != "arm64":
            parser.error("Unexpected signer architecture")
        _, signature = output("/usr/bin/codesign", "-dv", "--verbose=4", entry)
        text = signature.decode(errors="replace")
        if "TeamIdentifier=2T6K9PGS55" not in text or "flags=0x10000(runtime)" not in text or "Timestamp=" not in text:
            parser.error("Every native program needs timestamped Developer ID Hardened Runtime")
        if entry.name == HOST and "Identifier=" + ID + "\n" not in text:
            parser.error("Wrong main executable signing identifier")
        stdout, _ = output("/usr/bin/codesign", "-d", "--entitlements", "-", "--xml", entry)
        ent = plistlib.loads(stdout) if stdout else {}
        required = {"com.apple.security.cs.allow-jit": True} if entry.name == HOST else {}
        if ent != required:
            parser.error("Unexpected native entitlements: " + entry.name)
        stdout, _ = output("/usr/bin/otool", "-L", entry)
        install_name, _ = output("/usr/bin/otool", "-D", entry)
        own_names = install_name.decode().splitlines()[1:]
        for line in stdout.decode().splitlines()[1:]:
            dependency = line.strip().split(" ", 1)[0]
            if dependency.startswith(("/usr/lib/", "/System/Library/")):
                continue
            if entry.name == "libmldsa87_ref.dylib" and dependency == "@rpath/" + entry.name and dependency in own_names:
                continue
            parser.error("External runtime dependency: " + dependency)
        load_commands, _ = output("/usr/bin/otool", "-l", entry)
        if "cmd LC_RPATH" in load_commands.decode():
            parser.error("This self-contained signer needs no runtime search paths")
    _, signature = output("/usr/bin/codesign", "-dv", "--verbose=4", app)
    if "Identifier=" + ID not in signature.decode():
        parser.error("Wrong app signing identifier")
    if not args.signed_candidate:
        output("/usr/bin/xcrun", "stapler", "validate", app)
        _, gatekeeper = output("/usr/sbin/spctl", "--assess", "--type", "execute", "--verbose=4", app)
        if "source=Notarized Developer ID" not in gatekeeper.decode():
            parser.error("Gatekeeper did not confirm Notarized Developer ID")
    print("PASS signer: complete code seal, %d arm64 native objects, minimal entitlements%s" %
          (len(native), ", Apple staple and Gatekeeper" if not args.signed_candidate else "; candidate only"))


if __name__ == "__main__":
    main()
