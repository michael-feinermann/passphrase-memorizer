#!/usr/bin/env python3
"""Create public release hashes and inventories before detached key signing."""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[1]
ENV = {"PATH": "/usr/bin:/bin:/usr/sbin:/sbin"}
spec = importlib.util.spec_from_file_location("hybrid_release_verifier", ROOT / "Scripts/verify-hybrid-signatures.py")
V = importlib.util.module_from_spec(spec)
spec.loader.exec_module(V)


def run(*args, capture=False):
    result = subprocess.run(list(map(str, args)), check=True, env=ENV,
                            stdout=subprocess.PIPE if capture else None, text=True)
    return result.stdout


def hashes(path, signer):
    data = json.loads(run(ROOT / "Scripts/run-hybrid-signer.sh", "--signer-dir",
                          signer / "Contents/MacOS", "hash", "--target", path, capture=True))
    independent = {"sha256": hashlib.sha256(), "sha3-512": hashlib.sha3_512()}
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            for digest in independent.values():
                digest.update(block)
    if any(data[key] != digest.hexdigest() for key, digest in independent.items()):
        raise ValueError("Independent SHA checks disagree with signer")
    skein = run(ROOT / "Scripts/skein-reference-checksum.sh", path, capture=True).split()[0].lower()
    if data["skein-1024-1024"] != skein:
        raise ValueError("Independent Skein reference disagrees with signer")
    return {key: data[key] for key in ("sha256", "sha3-512", "skein-1024-1024")}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--release-dir", required=True, type=Path)
    parser.add_argument("--app", required=True, type=Path)
    parser.add_argument("--signer-app", required=True, type=Path)
    parser.add_argument("--reuse-signer-zip", type=Path,
                        help="Reuse an unchanged public signer ZIP after full inventory and Apple roundtrip checks")
    V.add_release_arguments(parser)
    args = parser.parse_args()
    V.validate_identity(args.version, args.build)
    V.validate_identity(args.signer_version, args.signer_build)
    targets = V.release_targets(args.version, args.signer_version)
    signer_name = targets[3]
    release, app, signer = (path.resolve(strict=True) for path in (args.release_dir, args.app, args.signer_app))
    run("/usr/bin/python3", "-I", ROOT / "Scripts/verify-hybrid-signer-app.py", "--app", signer)
    destination = release / signer_name
    if args.reuse_signer_zip and args.reuse_signer_zip.is_symlink():
        parser.error("Reused signer ZIP must be a physical regular file")
    reuse = args.reuse_signer_zip.resolve(strict=True) if args.reuse_signer_zip else None
    if not (release / targets[0]).is_file() or (destination.exists() and reuse != destination):
        parser.error("Provide the unchanged app ZIP and a fresh signer ZIP destination")
    with tempfile.TemporaryDirectory(prefix="passphrase-public-signer-archive-") as scratch:
        payload = Path(scratch) / "payload"
        payload.mkdir()
        run("/usr/bin/ditto", signer, payload / signer.name)
        run("/usr/bin/python3", "-I", ROOT / "Scripts/create-hybrid-inventory.py", "--signer", "--app",
            payload / signer.name, "--output", payload / "signer-inventory.json",
            "--version", args.signer_version, "--build", args.signer_build)
        expected_signer_inventory = (payload / "signer-inventory.json").read_bytes()
        if reuse is None:
            run("/usr/bin/ditto", "-c", "-k", "--sequesterRsrc", payload, destination)
        else:
            data = V.read_regular(reuse, V.MAX_ZIP)[0]
            actual_inventory = V.verify_signer_zip(data, args.signer_version, args.signer_build)
            if V.canonical_json(actual_inventory) != expected_signer_inventory:
                raise ValueError("Reused signer ZIP differs from the provided notarized Signer.app")
            if reuse != destination:
                with destination.open("xb") as stream:
                    stream.write(data)
            elif V.read_regular(destination, V.MAX_ZIP)[0] != data:
                raise ValueError("Reused signer ZIP changed")
        extracted = Path(scratch) / "roundtrip"
        run("/usr/bin/ditto", "-x", "-k", destination, extracted)
        run("/usr/bin/python3", "-I", ROOT / "Scripts/verify-hybrid-signer-app.py", "--app", extracted / signer.name)
        if (extracted / "signer-inventory.json").read_bytes() != expected_signer_inventory:
            raise ValueError("Signer ZIP roundtrip inventory differs")
    inventory = release / targets[2]
    if not inventory.exists():
        run("/usr/bin/python3", "-I", ROOT / "Scripts/create-hybrid-inventory.py", "--app", app, "--output", inventory,
            "--version", args.version, "--build", args.build)
    app_inventory = V.parse_inventory(V.read_regular(inventory, V.MAX_INVENTORY)[0], args.version, args.build)
    V.verify_app(app, app_inventory, args.version, args.build)
    V.verify_zip(V.read_regular(release / targets[0], V.MAX_ZIP)[0], app_inventory, args.version, args.build)
    app_hashes, signer_hashes = hashes(release / targets[0], signer), hashes(destination, signer)
    for name, digests in ((targets[0], app_hashes), (signer_name, signer_hashes)):
        sidecar = release / (name + ".sha256")
        text = digests["sha256"] + "  " + name + "\n"
        if sidecar.exists():
            if sidecar.read_text() != text:
                raise ValueError("Existing SHA-256 sidecar disagrees")
        else:
            sidecar.write_text(text)
    fields = {"artifact": targets[0], "coverage": "complete-signed-app-archive",
              "bundle-identifier": "local.passphrasereminder.reminder", "bundle-version": args.version,
              "bundle-build": args.build, "architecture": "arm64", "minimum-macos": "14.0",
              "signature-mode": "developer-id-notarized", **app_hashes,
              "signer-artifact": signer_name, **{"signer-" + k: v for k, v in signer_hashes.items()}}
    with (release / targets[1]).open("x") as stream:
        stream.write("Passphrase Memorizer Release Integrity Manifest v1\n")
        stream.write("".join(key + "=" + value + "\n" for key, value in fields.items()))
    print(json.dumps({"app": app_hashes, "signer": signer_hashes}, sort_keys=True))
    print("Created public inventories and independent hashes. Twelve hybrid envelopes are still required.")


if __name__ == "__main__":
    main()
