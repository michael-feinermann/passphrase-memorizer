#!/usr/bin/env python3
"""Package the reviewed single-file .NET signer without reading release keys."""
import argparse
import datetime
from pathlib import Path
import plistlib
import shutil
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[1]
NAME = "Passphrase Memorizer Hybrid Signer.app"
HOST = "PassphraseMemorizer.HybridSigner"
IDENTIFIER = "local.passphrasememorizer.hybridsigner"
MACHO = b"\xcf\xfa\xed\xfe"
ENV = {"PATH": "/usr/bin:/bin:/usr/sbin:/sbin"}


def run(*args):
    subprocess.run(list(map(str, args)), check=True, env=ENV)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--publish-dir", required=True, type=Path)
    parser.add_argument("--output-app", required=True, type=Path)
    parser.add_argument("--identity", required=True, help="Explicit Developer ID certificate SHA-1")
    parser.add_argument("--archive", type=Path, help="Optional new Xcode Organizer archive")
    args = parser.parse_args()
    if args.publish_dir.is_symlink():
        parser.error("Publish directory must not be an alias")
    source, app = args.publish_dir.resolve(strict=True), args.output_app.absolute()
    if not source.is_dir() or app.name != NAME or app.exists() or app.is_symlink():
        parser.error("Choose a physical publish directory and a fresh correctly named .app")
    if len(args.identity) != 40 or any(c not in "0123456789abcdefABCDEF" for c in args.identity):
        parser.error("Use an explicit certificate SHA-1, not an ambiguous display name")
    if args.archive and (args.archive.exists() or args.archive.suffix != ".xcarchive"):
        parser.error("The optional archive must be a fresh .xcarchive")
    entries = list(source.rglob("*"))
    if any(e.is_symlink() or not (e.is_file() or e.is_dir()) for e in entries):
        parser.error("Publish output contains an alias or special object")
    native, resources = [], []
    for entry in entries:
        if not entry.is_file():
            continue
        with entry.open("rb") as stream:
            magic = stream.read(4)
        if magic == MACHO and entry.parent == source:
            native.append(entry)
        elif entry.name in {"README.md", "SOURCE_PROVENANCE.json"} and entry.parent == source:
            resources.append(entry)
        elif entry.relative_to(source).parts[0] == "Licenses":
            resources.append(entry)
        else:
            parser.error("Unexpected publish object; managed DLLs must be embedded: " + entry.name)
    if HOST not in {e.name for e in native} or "libmldsa87_ref.dylib" not in {e.name for e in native}:
        parser.error("Single-file host or native reference is missing")
    for entry in native:
        arch = subprocess.check_output(["/usr/bin/lipo", "-archs", str(entry)], text=True, env=ENV).strip()
        if arch != "arm64":
            parser.error("Unexpected signer architecture")
    macos, target_resources = app / "Contents/MacOS", app / "Contents/Resources"
    macos.mkdir(parents=True)
    target_resources.mkdir()
    for entry in native:
        target = macos / entry.name
        shutil.copyfile(entry, target)
        target.chmod(0o755)
    for entry in resources:
        target = target_resources / entry.relative_to(source)
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(entry, target)
        target.chmod(0o644)
    with tempfile.TemporaryDirectory(prefix="passphrase-signer-icon-") as scratch:
        iconset = Path(scratch) / "AppIcon.iconset"
        iconset.mkdir()
        for size in (16, 32, 128, 256, 512):
            for scale in (1, 2):
                suffix = "@2x" if scale == 2 else ""
                subprocess.run(["/usr/bin/sips", "-z", str(size * scale), str(size * scale),
                                str(ROOT / "Assets/AppIcon-1024.png"), "--out",
                                str(iconset / ("icon_%dx%d%s.png" % (size, size, suffix)))],
                               check=True, stdout=subprocess.DEVNULL, env=ENV)
        run("/usr/bin/iconutil", "-c", "icns", iconset, "-o", target_resources / "AppIcon.icns")
    info = {"CFBundleIdentifier": IDENTIFIER, "CFBundleExecutable": HOST,
            "CFBundleName": "Passphrase Memorizer Hybrid Signer",
            "CFBundleDisplayName": "Passphrase Memorizer Hybrid Signer",
            "CFBundleShortVersionString": "1.0.0", "CFBundleVersion": "1",
            "CFBundlePackageType": "APPL", "CFBundleIconFile": "AppIcon",
            "LSMinimumSystemVersion": "14.0", "LSUIElement": True}
    (app / "Contents/Info.plist").write_bytes(plistlib.dumps(info))
    for directory in [app] + [e for e in app.rglob("*") if e.is_dir()]:
        directory.chmod(0o755)
    entitlements = ROOT / "Signing/HybridSigner/Signer.entitlements"
    for entry in sorted(macos.iterdir()):
        if entry.name == HOST:
            continue
        run("/usr/bin/codesign", "--force", "--sign", args.identity, "--timestamp", "--options", "runtime", entry)
    run("/usr/bin/codesign", "--force", "--sign", args.identity, "--timestamp", "--options", "runtime",
        "--entitlements", entitlements, "--identifier", IDENTIFIER, macos / HOST)
    run("/usr/bin/codesign", "--force", "--sign", args.identity, "--timestamp", "--options", "runtime",
        "--entitlements", entitlements, app)
    run("/usr/bin/codesign", "--verify", "--deep", "--strict", app)
    if args.archive:
        archived_app = args.archive / "Products/Applications" / NAME
        archived_app.parent.mkdir(parents=True)
        run("/usr/bin/ditto", app, archived_app)
        archive_info = {"ArchiveVersion": 2, "CreationDate": datetime.datetime.now(datetime.timezone.utc).replace(tzinfo=None),
                        "Name": "Passphrase Memorizer Hybrid Signer", "SchemeName": HOST,
                        "ApplicationProperties": {"ApplicationPath": "Applications/" + NAME,
                                                  "Architectures": ["arm64"], "CFBundleIdentifier": IDENTIFIER,
                                                  "CFBundleShortVersionString": "1.0.0", "CFBundleVersion": "1",
                                                  "SigningIdentity": "Developer ID Application: Michael Alexander Feinermann (2T6K9PGS55)",
                                                  "Team": "2T6K9PGS55"}}
        (args.archive / "Info.plist").write_bytes(plistlib.dumps(archive_info))
    print("Created signed candidate. Apple notarization and final release checks are still required.")


if __name__ == "__main__":
    main()
