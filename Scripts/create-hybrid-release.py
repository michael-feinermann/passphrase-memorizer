#!/usr/bin/env python3
"""Create public release hashes and inventories before detached key signing."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[1]
ENV = {"PATH": "/usr/bin:/bin:/usr/sbin:/sbin"}
STEM = "Passphrase-Memorizer-1.0.0"
SIGNER = "Passphrase-Memorizer-HybridSigner-1.0.0.zip"


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
    args = parser.parse_args()
    release, app, signer = (path.resolve(strict=True) for path in (args.release_dir, args.app, args.signer_app))
    run("/usr/bin/python3", "-I", ROOT / "Scripts/verify-hybrid-signer-app.py", "--app", signer)
    if not (release / (STEM + ".zip")).is_file() or (release / SIGNER).exists():
        parser.error("Provide the unchanged app ZIP and a fresh signer ZIP destination")
    with tempfile.TemporaryDirectory(prefix="passphrase-public-signer-archive-") as scratch:
        payload = Path(scratch) / "payload"
        payload.mkdir()
        run("/usr/bin/ditto", signer, payload / signer.name)
        run("/usr/bin/python3", "-I", ROOT / "Scripts/create-hybrid-inventory.py", "--signer", "--app",
            payload / signer.name, "--output", payload / "signer-inventory.json")
        run("/usr/bin/ditto", "-c", "-k", "--sequesterRsrc", payload, release / SIGNER)
        extracted = Path(scratch) / "roundtrip"
        run("/usr/bin/ditto", "-x", "-k", release / SIGNER, extracted)
        run("/usr/bin/python3", "-I", ROOT / "Scripts/verify-hybrid-signer-app.py", "--app", extracted / signer.name)
    inventory = release / (STEM + ".bundle-inventory.json")
    if not inventory.exists():
        run("/usr/bin/python3", "-I", ROOT / "Scripts/create-hybrid-inventory.py", "--app", app, "--output", inventory)
    app_hashes, signer_hashes = hashes(release / (STEM + ".zip"), signer), hashes(release / SIGNER, signer)
    for name, digests in ((STEM + ".zip", app_hashes), (SIGNER, signer_hashes)):
        sidecar = release / (name + ".sha256")
        text = digests["sha256"] + "  " + name + "\n"
        if sidecar.exists():
            if sidecar.read_text() != text:
                raise ValueError("Existing SHA-256 sidecar disagrees")
        else:
            sidecar.write_text(text)
    fields = {"artifact": STEM + ".zip", "coverage": "complete-signed-app-archive",
              "bundle-identifier": "local.passphrasereminder.reminder", "bundle-version": "1.0.0",
              "bundle-build": "1", "architecture": "arm64", "minimum-macos": "14.0",
              "signature-mode": "developer-id-notarized", **app_hashes,
              "signer-artifact": SIGNER, **{"signer-" + k: v for k, v in signer_hashes.items()}}
    with (release / (STEM + ".integrity.txt")).open("x") as stream:
        stream.write("Passphrase Memorizer Release Integrity Manifest v1\n")
        stream.write("".join(key + "=" + value + "\n" for key, value in fields.items()))
    print(json.dumps({"app": app_hashes, "signer": signer_hashes}, sort_keys=True))
    print("Created public inventories and independent hashes. Twelve hybrid envelopes are still required.")


if __name__ == "__main__":
    main()
