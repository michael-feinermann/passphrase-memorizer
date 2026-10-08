#!/usr/bin/env python3
"""Public-fixture tests for verify-hybrid-signatures.py; no private keys used.

Provide a genuine signed release and its independently obtained public trust
files. Mutated copies stay in a temporary directory and are never published.
"""

import argparse
import copy
import hashlib
import importlib.util
import io
import json
import pathlib
import shutil
import stat
import struct
import sys
import tempfile
import zipfile


SCRIPT = pathlib.Path(__file__).with_name("verify-hybrid-signatures.py")
sys.dont_write_bytecode = True
SPEC = importlib.util.spec_from_file_location("hybrid_verifier", SCRIPT)
V = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(V)


class Checks:
    def __init__(self):
        self.positive = 0
        self.negative = 0

    def accepts(self, name, operation):
        try:
            operation()
        except Exception as error:
            raise RuntimeError("Positive check failed: " + name) from error
        self.positive += 1

    def rejects(self, name, operation):
        try:
            operation()
        except V.VerificationError:
            self.negative += 1
            return
        raise RuntimeError("Tampering was accepted: " + name)


def flip(data, offset):
    result = bytearray(data)
    result[offset] ^= 1
    return bytes(result)


def altered_zip(data, mutate):
    entries = []
    with zipfile.ZipFile(io.BytesIO(data)) as archive:
        for entry in archive.infolist():
            entries.append((copy.copy(entry), archive.read(entry)))
    mutate(entries)
    stream = io.BytesIO()
    with zipfile.ZipFile(stream, "w") as archive:
        for entry, contents in entries:
            archive.writestr(entry, contents)
    return stream.getvalue()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--release-dir", required=True, type=pathlib.Path)
    parser.add_argument("--trust", required=True, type=pathlib.Path)
    parser.add_argument("--checksum-tool", required=True, type=pathlib.Path)
    parser.add_argument("--openssl", default=shutil.which("openssl"))
    parser.add_argument("--app", type=pathlib.Path, default=pathlib.Path("/Applications") / V.APP_NAME)
    V.add_release_arguments(parser)
    args = parser.parse_args()
    identity = dict(version=args.version, build=args.build,
                    signer_version=args.signer_version, signer_build=args.signer_build)
    targets = V.release_targets(args.version, args.signer_version)
    signer_target = targets[3]
    def verify_release(*positional):
        return V.verify_release(*positional, **identity)
    def verify_manifest(*positional):
        return V.verify_manifest(*positional, version=args.version, build=args.build, signer_version=args.signer_version)
    def verify_signer_zip(data):
        return V.verify_signer_zip(data, args.signer_version, args.signer_build)
    def parse_inventory(data):
        return V.parse_inventory(data, args.version, args.build)
    def verify_zip(data, inventory):
        return V.verify_zip(data, inventory, args.version, args.build)
    def verify_app(app, inventory):
        return V.verify_app(app, inventory, args.version, args.build)
    checks = Checks()
    release = args.release_dir.resolve()
    trust_path = args.trust.resolve()
    trust_bytes = V.read_regular(trust_path, V.MAX_TRUST)[0]
    trust = V.parse_trust(trust_bytes)
    rsa = V.read_regular(trust_path.parent / trust["rsa_spki"], V.MAX_CERTIFICATE)[0]
    ml = V.read_regular(trust_path.parent / trust["mldsa_public_key"], 2592)[0]
    artifacts = {name: V.read_regular(release / name, bound)[0]
                 for name, bound in zip(targets, V.TARGET_LIMITS)}
    inventory = parse_inventory(artifacts[targets[2]])
    primary = artifacts[targets[0]]
    sidecar = V.read_regular(release / (targets[0] + ".khsig"), V.MAX_SIDECAR)[0]

    with tempfile.TemporaryDirectory(prefix="passphrase-memorizer-public-tests-") as temporary:
        temporary = pathlib.Path(temporary)
        checks.accepts("real twelve-signature release and installed app", lambda: verify_release(
            release, trust_path, args.checksum_tool, args.openssl, args.app))
        public = V.PublicVerifier(args.openssl, args.checksum_tool, temporary / "crypto")
        public.temporary.mkdir()
        ml_spki = public.snapshot(V.mldsa_spki(ml), ".ml-spki.der")
        checks.accepts("independent RSA and pure ML-DSA interoperation", lambda: public.verify(sidecar, primary, rsa, ml_spki))
        header = list(V.HEADER.unpack_from(sidecar))
        cert_length = header[4]
        rsa_offset = V.HEADER.size + cert_length
        ml_offset = rsa_offset + 512
        for name, offset in (("RSA alone", rsa_offset), ("ML-DSA alone", ml_offset)):
            checks.rejects(name + " corrupted", lambda offset=offset: public.verify(flip(sidecar, offset), primary, rsa, ml_spki))
        checks.rejects("both signatures corrupted", lambda: public.verify(flip(flip(sidecar, rsa_offset), ml_offset), primary, rsa, ml_spki))
        checks.rejects("certificate corrupted", lambda: public.verify(flip(sidecar, 96), primary, rsa, ml_spki))
        checks.rejects("wrong trusted RSA key", lambda: public.verify(sidecar, primary, flip(rsa, -1), ml_spki))
        other_ml = public.snapshot(V.mldsa_spki(flip(ml, 0)), ".other-ml-spki.der")
        checks.rejects("wrong trusted ML key", lambda: public.verify(sidecar, primary, rsa, other_ml))

        for target in targets:
            for suffix in ("", ".sha3", ".skein"):
                name = target + suffix
                data = V.read_regular(release / name, V.MAX_ZIP)[0]
                envelope = V.read_regular(release / (name + ".khsig"), V.MAX_SIDECAR)[0]
                cert_size = V.HEADER.unpack_from(envelope)[4]
                rsa_start = 96 + cert_size
                ml_start = rsa_start + 512
                for label, candidate in (
                        ("RSA", flip(envelope, rsa_start)),
                        ("ML", flip(envelope, ml_start)),
                        ("RSA and ML", flip(flip(envelope, rsa_start), ml_start))):
                    checks.rejects(name + " altered " + label,
                                   lambda candidate=candidate, data=data: public.verify(candidate, data, rsa, ml_spki))

        for name, field, value in (("magic", 0, b"INVALID1"), ("version", 1, 2), ("negative length", 2, -1),
                                   ("wrong length", 2, len(primary) + 1), ("wrong digest", 3, b"\0" * 64),
                                   ("zero certificate", 4, 0), ("oversized certificate", 4, 32769),
                                   ("RSA length", 5, 511), ("ML length", 6, 4626)):
            replacement = header.copy()
            replacement[field] = value
            candidate = V.HEADER.pack(*replacement) + sidecar[V.HEADER.size:]
            checks.rejects("envelope " + name, lambda candidate=candidate: V.parse_envelope(candidate, primary))
        for name, candidate in (("header truncation", sidecar[:95]), ("payload truncation", sidecar[:-1]),
                                ("trailing bytes", sidecar + b"\0"), ("oversized envelope", sidecar + b"\0" * V.MAX_SIDECAR)):
            checks.rejects(name, lambda candidate=candidate: V.parse_envelope(candidate, primary))
        checks.rejects("changed signed artifact", lambda: V.parse_envelope(sidecar, flip(primary, 0)))

        for label, candidate in (("trust duplicate key", b'{"schema":"a","schema":"b"}'),
                                 ("inventory duplicate key", b'{"files":[],"files":[]}')):
            checks.rejects(label, lambda candidate=candidate: V.parse_json(candidate))
        for name, field, value in (("trust schema", "schema", "other"), ("trust product", "product_id", "other"),
                                  ("trust RSA traversal", "rsa_spki", "../public.der"),
                                  ("trust ML absolute", "mldsa_public_key", "/public.bin")):
            candidate = copy.deepcopy(trust)
            candidate[field] = value
            checks.rejects(name, lambda candidate=candidate: V.parse_trust(V.canonical_json(candidate)))
        candidate = copy.deepcopy(trust)
        candidate["unexpected"] = True
        checks.rejects("unknown trust key", lambda: V.parse_trust(V.canonical_json(candidate)))
        for pins in ("rsa_spki_hashes", "mldsa_public_key_hashes"):
            for algorithm in V.HASH_LENGTHS:
                candidate = copy.deepcopy(trust)
                candidate[pins][algorithm] = "0" * V.HASH_LENGTHS[algorithm]
                checks.rejects(pins + " altered " + algorithm, lambda candidate=candidate, pins=pins, key=rsa if pins.startswith("rsa") else ml:
                              V.require(public.hashes(key) == V.parse_trust(V.canonical_json(candidate))[pins], "Fingerprint mismatch"))

        for name, field, value in (("inventory schema", "schema", "other"), ("inventory product", "product_id", "other"),
                                  ("inventory version", "version", "0.9.9"), ("inventory build", "build", "0"),
                                  ("inventory app", "app_name", "Old.app")):
            candidate = copy.deepcopy(inventory)
            candidate[field] = value
            checks.rejects(name, lambda candidate=candidate: parse_inventory(V.canonical_json(candidate)))
        candidate = copy.deepcopy(inventory)
        candidate["unexpected"] = True
        checks.rejects("unknown inventory key", lambda: parse_inventory(V.canonical_json(candidate)))
        checks.rejects("noncanonical inventory whitespace", lambda: parse_inventory(json.dumps(inventory).encode()))
        for label, mutate in (
                ("duplicate file", lambda x: x["files"].append(copy.deepcopy(x["files"][0]))),
                ("unsorted files", lambda x: x["files"].reverse()),
                ("duplicate directory", lambda x: x["directories"].append(x["directories"][0])),
                ("unsorted directories", lambda x: x["directories"].reverse()),
                ("missing parent", lambda x: x["directories"].remove("Contents")),
                ("boolean size", lambda x: x["files"][0].update(size=True)),
                ("negative size", lambda x: x["files"][0].update(size=-1)),
                ("special permission", lambda x: x["files"][0].update(mode=0o4755)),
                ("boolean mode", lambda x: x["files"][0].update(mode=True)),
                ("bad SHA512", lambda x: x["files"][0].update(sha512="0")),
                ("unknown file key", lambda x: x["files"][0].update(extra=1))):
            candidate = copy.deepcopy(inventory)
            mutate(candidate)
            checks.rejects("inventory " + label, lambda candidate=candidate: parse_inventory(V.canonical_json(candidate)))
        for unsafe in ("../file", "/file", "a//b", "a/./b", "a\\b", "a\0b", "ä", "a:b"):
            checks.rejects("unsafe path " + repr(unsafe), lambda unsafe=unsafe: V.safe_path(unsafe))

        checks.accepts("actual signed ZIP including ditto metadata", lambda: verify_zip(primary, inventory))
        # Tests of structure compare use the authenticated inventory directly;
        # an attacker also has to pass the outer ZIP signature in normal use.
        first_file = next(item["path"] for item in inventory["files"] if item["size"] > 0 and item["path"] != "Contents/Info.plist")
        first_dir = inventory["directories"][-1]
        def edit_entry(entries, relative, mutate):
            for index, (entry, data) in enumerate(entries):
                if entry.filename == V.APP_NAME + "/" + relative:
                    entries[index] = mutate(entry, data)
                    return
            raise RuntimeError("Fixture entry missing")
        mutations = [
            ("changed ZIP file", lambda e: edit_entry(e, first_file, lambda item, data: (item, flip(data, 0)))),
            ("missing ZIP file", lambda e: e.__setitem__(slice(None), [(i, d) for i, d in e if i.filename != V.APP_NAME + "/" + first_file])),
            ("missing ZIP directory", lambda e: e.__setitem__(slice(None), [(i, d) for i, d in e if i.filename != V.APP_NAME + "/" + first_dir + "/"])),
        ]
        for label, mutate in mutations:
            candidate = altered_zip(primary, mutate)
            checks.rejects(label, lambda candidate=candidate: verify_zip(candidate, inventory))
        for label, path, mode in (("extra ZIP file", V.APP_NAME + "/extra", stat.S_IFREG | 0o644),
                                 ("extra ZIP directory", V.APP_NAME + "/empty/", stat.S_IFDIR | 0o755),
                                 ("ZIP symlink", V.APP_NAME + "/link", stat.S_IFLNK | 0o777),
                                 ("ZIP FIFO", V.APP_NAME + "/fifo", stat.S_IFIFO | 0o644),
                                 ("ZIP traversal", V.APP_NAME + "/../escape", stat.S_IFREG | 0o644),
                                 ("ZIP unexpected root", "other/file", stat.S_IFREG | 0o644),
                                 ("ZIP metadata escape", "__MACOSX/other/._file", stat.S_IFREG | 0o644)):
            item = zipfile.ZipInfo(path)
            item.create_system = 3
            item.external_attr = mode << 16
            candidate = altered_zip(primary, lambda e, item=item: e.append((item, b"")))
            checks.rejects(label, lambda candidate=candidate: verify_zip(candidate, inventory))

        copied_app = temporary / V.APP_NAME
        shutil.copytree(args.app, copied_app, symlinks=True)
        checks.accepts("copied actual app", lambda: verify_app(copied_app, inventory))
        changed = copied_app / first_file
        original = changed.read_bytes()
        original_mode = stat.S_IMODE(changed.stat().st_mode)
        changed.write_bytes(flip(original, 0))
        checks.rejects("changed installed file", lambda: verify_app(copied_app, inventory))
        changed.write_bytes(original)
        changed.chmod(original_mode ^ 0o111)
        checks.rejects("changed executable permissions", lambda: verify_app(copied_app, inventory))
        changed.chmod(0o4755)
        checks.rejects("setuid permission", lambda: verify_app(copied_app, inventory))
        changed.chmod(original_mode)
        changed.unlink()
        checks.rejects("missing installed file", lambda: verify_app(copied_app, inventory))
        changed.write_bytes(original)
        changed.chmod(original_mode)
        extra = copied_app / "extra"
        extra.write_bytes(b"public fixture")
        checks.rejects("extra installed file", lambda: verify_app(copied_app, inventory))
        extra.unlink()
        extra.mkdir()
        checks.rejects("extra empty installed directory", lambda: verify_app(copied_app, inventory))
        extra.rmdir()
        missing_directory = copied_app / first_dir
        parked_directory = temporary / "parked-public-directory"
        shutil.move(missing_directory, parked_directory)
        checks.rejects("missing installed directory", lambda: verify_app(copied_app, inventory))
        shutil.move(parked_directory, missing_directory)
        extra.symlink_to(changed)
        checks.rejects("installed symlink", lambda: verify_app(copied_app, inventory))
        extra.unlink()
        checks.rejects("public input size bound", lambda: V.read_regular(changed, 0))
        extra.symlink_to(changed)
        checks.rejects("public input follows symlink", lambda: V.read_regular(extra, V.MAX_BUNDLE_BYTES))
        extra.unlink()

        hashes = public.hashes(primary)
        signer_hashes = public.hashes(artifacts[signer_target])
        checks.accepts("linked manifest hashes and product", lambda: verify_manifest(artifacts[targets[1]], hashes, signer_hashes))
        for field, value in (("bundle-build", "0"), ("bundle-version", "0.9.9"), ("bundle-identifier", "other"),
                             ("signature-mode", "local-ad-hoc"), ("sha256", "0" * 64), ("sha3-512", "0" * 128),
                             ("skein-1024-1024", "0" * 256), ("signer-artifact", "other.zip"),
                             ("signer-sha256", "0" * 64), ("signer-sha3-512", "0" * 128),
                             ("signer-skein-1024-1024", "0" * 256)):
            lines = artifacts[targets[1]].decode().splitlines()
            candidate = ("\n".join(field + "=" + value if line.startswith(field + "=") else line for line in lines) + "\n").encode()
            checks.rejects("manifest " + field, lambda candidate=candidate: verify_manifest(candidate, hashes, signer_hashes))

        checks.accepts("actual authenticated offline signer archive", lambda: verify_signer_zip(artifacts[signer_target]))
        for label, mutate in (
                ("signer apphost missing", lambda e: e.__setitem__(slice(None), [(i, d) for i, d in e if i.filename != V.SIGNER_APP_NAME + "/Contents/MacOS/" + V.SIGNER_HOST])),
                ("signer source provenance missing", lambda e: e.__setitem__(slice(None), [(i, d) for i, d in e if i.filename != V.SIGNER_APP_NAME + "/Contents/Resources/SOURCE_PROVENANCE.json"])),
                ("signer ML reference missing", lambda e: e.__setitem__(slice(None), [(i, d) for i, d in e if i.filename != V.SIGNER_APP_NAME + "/Contents/MacOS/libmldsa87_ref.dylib"]))):
            candidate = altered_zip(artifacts[signer_target], mutate)
            checks.rejects(label, lambda candidate=candidate: verify_signer_zip(candidate))
        for label, path, mode in (
                ("signer symlink", V.SIGNER_APP_NAME + "/link", stat.S_IFLNK | 0o777),
                ("signer traversal", V.SIGNER_APP_NAME + "/../escape", stat.S_IFREG | 0o644),
                ("signer setuid", V.SIGNER_APP_NAME + "/bad", stat.S_IFREG | 0o4755)):
            item = zipfile.ZipInfo(path)
            item.create_system = 3
            item.external_attr = mode << 16
            candidate = altered_zip(artifacts[signer_target], lambda e, item=item: e.append((item, b"public fixture")))
            checks.rejects(label, lambda candidate=candidate: verify_signer_zip(candidate))

        copied_release = temporary / "release"
        copied_trust = temporary / "trust"
        shutil.copytree(release, copied_release)
        copied_trust.mkdir()
        for name in (trust_path.name, trust["rsa_spki"], trust["mldsa_public_key"]):
            shutil.copy2(trust_path.parent / name, copied_trust / name)
        for target in targets:
            for suffix in ("", ".sha3", ".skein"):
                signature = copied_release / (target + suffix + ".khsig")
                saved = signature.read_bytes()
                signature.unlink()
                checks.rejects("missing required signature " + target + suffix, lambda: verify_release(
                    copied_release, copied_trust / trust_path.name, args.checksum_tool, args.openssl))
                signature.write_bytes(saved)
            for suffix in (".sha3", ".skein"):
                digest_path = copied_release / (target + suffix)
                saved = digest_path.read_bytes()
                digest_path.write_bytes(saved.lower())
                checks.rejects("noncanonical digest " + target + suffix, lambda: verify_release(
                    copied_release, copied_trust / trust_path.name, args.checksum_tool, args.openssl))
                digest_path.write_bytes(saved)
        for key in (trust["rsa_spki"], trust["mldsa_public_key"]):
            key_path = copied_trust / key
            saved = key_path.read_bytes()
            key_path.write_bytes(flip(saved, -1))
            checks.rejects("changed public trust key " + key, lambda: verify_release(
                copied_release, copied_trust / trust_path.name, args.checksum_tool, args.openssl))
            key_path.write_bytes(saved)
        for name in (targets[0], signer_target):
            path = copied_release / (name + ".sha256")
            saved = path.read_bytes()
            path.write_bytes(b"0" * 64 + b"  " + name.encode() + b"\n")
            checks.rejects("changed SHA-256 sidecar " + name, lambda: verify_release(
                copied_release, copied_trust / trust_path.name, args.checksum_tool, args.openssl))
            path.write_bytes(saved)
        checks.accepts("complete restored public fixture", lambda: verify_release(
            copied_release, copied_trust / trust_path.name, args.checksum_tool, args.openssl))

    print("Hybrid verification tests passed: %d checks (%d positive, %d negative)." %
          (checks.positive + checks.negative, checks.positive, checks.negative))
    print("All fixtures are public; no private key generation, access or publication occurred.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
