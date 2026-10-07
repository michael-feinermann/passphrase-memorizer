#!/usr/bin/env python3
"""Create the canonical public inventory signed alongside a notarized app.

This never modifies the app. Signing happens only after Apple's final export and
stapling, because a change to the bundle afterwards would break its Apple seal.
"""
import argparse
import hashlib
import json
import os
import pathlib
import plistlib
import stat


def inventory(app, signer=False):
    if app.is_symlink() or not app.is_dir():
        raise ValueError("The app must be a real directory.")
    directories = []
    files = []
    metadata = None
    for entry in sorted(app.rglob("*")):
        info = entry.lstat()
        path = entry.relative_to(app).as_posix()
        if stat.S_ISDIR(info.st_mode):
            directories.append(path)
        elif stat.S_ISREG(info.st_mode):
            digest = hashlib.sha512()
            fd = os.open(entry, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK)
            with os.fdopen(fd, "rb") as stream:
                opened = os.fstat(stream.fileno())
                if not stat.S_ISREG(opened.st_mode):
                    raise ValueError("A file was replaced by a nonregular input.")
                if path == "Contents/Info.plist":
                    if opened.st_size > 65536:
                        raise ValueError("App metadata exceeds its size bound.")
                    metadata = stream.read()
                    digest.update(metadata)
                for block in iter(lambda: stream.read(1024 * 1024), b""):
                    digest.update(block)
                finished = os.fstat(stream.fileno())
            after = entry.lstat()
            fields = ("st_dev", "st_ino", "st_size", "st_mtime_ns", "st_ctime_ns", "st_mode")
            if not all(getattr(info, f) == getattr(opened, f) == getattr(finished, f) == getattr(after, f) for f in fields):
                raise ValueError("A file changed during inventory creation.")
            mode = stat.S_IMODE(info.st_mode)
            if mode & ~0o777:
                raise ValueError("Special permission bits are not supported.")
            files.append({"path": path, "size": info.st_size, "sha512": digest.hexdigest(), "mode": mode})
        else:
            raise ValueError("Symlinks and special files are not supported.")
    if metadata is None:
        raise ValueError("App metadata is missing.")
    info = plistlib.loads(metadata)
    expected = {
        "CFBundleIdentifier": "local.passphrasememorizer.hybridsigner" if signer else "local.passphrasereminder.reminder",
        "CFBundleShortVersionString": "1.0.0",
        "CFBundleVersion": "1",
        "CFBundleExecutable": "PassphraseMemorizer.HybridSigner" if signer else "MnemonicStoryApp",
    }
    for key, value in expected.items():
        if info.get(key) != value:
            raise ValueError("Unexpected app metadata: " + key)
    if app.name != ("Passphrase Memorizer Hybrid Signer.app" if signer else "Passphrase Memorizer.app"):
        raise ValueError("Unexpected app name.")
    return {
        "schema": "passphrase-memorizer-signer-inventory-v1" if signer else "passphrase-memorizer-bundle-inventory-v1",
        "product_id": expected["CFBundleIdentifier"],
        "version": expected["CFBundleShortVersionString"],
        "build": expected["CFBundleVersion"],
        "app_name": app.name,
        "files": files,
        "directories": directories,
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--app", required=True, type=pathlib.Path)
    parser.add_argument("--output", required=True, type=pathlib.Path)
    parser.add_argument("--signer", action="store_true", help="Create signer-inventory.json beside the final signer .app")
    args = parser.parse_args()
    if args.output.resolve().is_relative_to(args.app.resolve()):
        raise ValueError("The inventory must be stored outside the app bundle.")
    data = inventory(args.app, signer=args.signer)
    with args.output.open("x", encoding="utf-8") as stream:
        stream.write(json.dumps(data, ensure_ascii=True, sort_keys=True, separators=(",", ":")) + "\n")
    print("Created public inventory: %d files, %d directories." % (len(data["files"]), len(data["directories"])))


if __name__ == "__main__":
    main()
