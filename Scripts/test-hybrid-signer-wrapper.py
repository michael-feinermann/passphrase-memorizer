#!/usr/bin/env python3
"""Test the production wrapper with signed PUBLIC code and synthetic data only."""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import struct
import subprocess
import tempfile

PROJECT = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--app", required=True, type=Path)
args = parser.parse_args()
app = args.app.resolve(strict=True)
host = app / "Contents/MacOS/PassphraseMemorizer.HybridSigner"
wrapper = PROJECT / "Scripts/run-hybrid-signer.sh"
subprocess.run(["/usr/bin/codesign", "--verify", "--deep", "--strict", str(app)], check=True)


def invoke(directory, options):
    return subprocess.run([str(wrapper), "--signer-dir", str(directory)] + options,
                          capture_output=True, timeout=60,
                          env={"PATH": "/usr/bin:/bin:/usr/sbin:/sbin"})


def managed_byte(host_bytes):
    """Locate public PE assembly bytes via the .NET v6 bundle manifest entry.

    Diagnostic test only; the production gate always uses Apple's verifier.
    Layout reference: dotnet/runtime v10.0.11 HostModel/Bundle/FileEntry.cs.
    """
    name = b"PassphraseMemorizer.HybridSigner.dll"
    location = host_bytes.rfind(name)
    assert location > 26 and host_bytes[location - 1] == len(name)
    entry = location - 26  # Int64 offset/size/compressed-size, byte type, byte length.
    offset, size, compressed = struct.unpack_from("<qqq", host_bytes, entry)
    assert compressed == 0 and host_bytes[entry + 24] == 1
    assert size > 64 and 0 < offset < offset + size <= entry
    assert host_bytes[offset:offset + 2] == b"MZ"
    pe = struct.unpack_from("<I", host_bytes, offset + 0x3C)[0]
    assert 0 < pe < size and host_bytes[offset + pe:offset + pe + 4] == b"PE\0\0"
    assert host_bytes[:4] == b"\xcf\xfa\xed\xfe"
    load = 32
    signature_offset = None
    for _ in range(struct.unpack_from("<I", host_bytes, 16)[0]):
        command, command_size = struct.unpack_from("<II", host_bytes, load)
        assert command_size >= 8
        if command == 0x1D:
            signature_offset = struct.unpack_from("<I", host_bytes, load + 8)[0]
        load += command_size
    assert signature_offset is not None and offset + size <= signature_offset
    return offset + size // 2


with tempfile.TemporaryDirectory(prefix="passphrase-public-wrapper-") as directory:
    public = Path(directory)
    fixture = public / "public-abc.bin"
    fixture.write_bytes(b"abc")
    accepted = invoke(host.parent, ["hash", "--target", str(fixture)])
    assert accepted.returncode == 0, accepted.stderr.decode(errors="replace")
    hashes = json.loads(accepted.stdout)
    assert hashes["sha256"] == hashlib.sha256(b"abc").hexdigest()
    assert hashes["sha3-512"] == hashlib.sha3_512(b"abc").hexdigest()
    print("PASS production signed bundle accepts public hashing")

    # Xcode's notarization ZIP discards resource forks and generic-signature
    # extended attributes. The published layout must not rely on either.
    archive = public / "public-roundtrip.zip"
    subprocess.run(["/usr/bin/ditto", "-c", "-k", "--norsrc", "--keepParent", str(app), str(archive)], check=True)
    roundtrip = public / "roundtrip"
    subprocess.run(["/usr/bin/ditto", "-x", "-k", str(archive), str(roundtrip)], check=True)
    subprocess.run(["/usr/bin/codesign", "--verify", "--deep", "--strict", str(roundtrip / app.name)], check=True)
    assert not list(host.parent.glob("*.dll"))
    assert not list(host.parent.glob("*.json"))
    print("PASS ZIP roundtrip without extended-attribute signatures preserves the full bundle seal")

    copied = public / app.name
    subprocess.run(["/usr/bin/ditto", str(app), str(copied)], check=True)
    subprocess.run(["/usr/bin/codesign", "--verify", "--deep", "--strict", str(copied)], check=True)
    copied_host = copied / "Contents/MacOS" / host.name
    original_host = hashlib.sha256(copied_host.read_bytes()).digest()
    resource = copied / "Contents/Resources/README.md"
    data = bytearray(resource.read_bytes())
    assert data
    data[-1] ^= 1
    resource.write_bytes(data)
    assert hashlib.sha256(copied_host.read_bytes()).digest() == original_host
    # Ignore the resource seal only for this assertion about the unchanged
    # native executable; production always verifies every resource above.
    subprocess.run(["/usr/bin/codesign", "--verify", "--strict", "--ignore-resources", str(copied_host)], check=True)
    rejected = invoke(copied_host.parent, ["release-keygen", "--directory", str(public / "not-a-key-dir"),
                                         "--reference-library", str(copied_host.parent / "libmldsa87_ref.dylib")])
    assert rejected.returncode != 0 and rejected.stdout == b""
    assert not (public / "not-a-key-dir").exists()
    print("PASS changed sealed resource with unchanged valid apphost rejected before key generation")

    embedded_copy = public / "embedded" / app.name
    subprocess.run(["/usr/bin/ditto", str(app), str(embedded_copy)], check=True)
    embedded_host = embedded_copy / "Contents/MacOS" / host.name
    data = bytearray(embedded_host.read_bytes())
    data[managed_byte(data)] ^= 1
    embedded_host.write_bytes(data)
    rejected = invoke(embedded_host.parent, ["release-keygen", "--directory", str(public / "not-a-key-dir"),
                                           "--reference-library", str(embedded_host.parent / "libmldsa87_ref.dylib")])
    assert rejected.returncode != 0 and rejected.stdout == b""
    assert not (public / "not-a-key-dir").exists()
    print("PASS changed embedded managed assembly byte rejected before key generation")

    rejected = invoke(host.parent, ["release-keygen", "--directory", str(public / "not-a-key-dir"),
                                   "--reference-library", str(fixture)])
    assert rejected.returncode != 0 and rejected.stdout == b""
    assert b"sealed native reference" in rejected.stderr
    assert not (public / "not-a-key-dir").exists()
    print("PASS external public reference file rejected before any managed execution")

    alias = public / "sealed-reference-alias.dylib"
    alias.symlink_to(host.parent / "libmldsa87_ref.dylib")
    rejected = invoke(host.parent, ["release-keygen", "--directory", str(public / "not-a-key-dir"),
                                   "--reference-library", str(alias)])
    assert rejected.returncode != 0 and rejected.stdout == b""
    assert b"sealed native reference" in rejected.stderr
    print("PASS reference alias rejected before any managed execution")

    unbundled = public / "unbundled"
    unbundled.mkdir()
    shutil.copy2(host, unbundled / host.name)
    rejected = invoke(unbundled, ["hash", "--target", str(fixture)])
    assert rejected.returncode != 0 and rejected.stdout == b""
    assert b"complete signed Signer.app" in rejected.stderr
    print("PASS unbundled native apphost rejected")

print("All seven production-wrapper public integration cases passed; no private keys read or generated.")
