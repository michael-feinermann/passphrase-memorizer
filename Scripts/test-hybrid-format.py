#!/usr/bin/env python3
"""Structural adversarial tests without keys; genuine crypto tests are separate."""
import copy
import hashlib
import importlib.util
import io
import pathlib
import plistlib
import stat
import struct
import sys
import tempfile
import unittest
import warnings
import zipfile

sys.dont_write_bytecode = True
SPEC = importlib.util.spec_from_file_location("hybrid_verifier", pathlib.Path(__file__).with_name("verify-hybrid-signatures.py"))
V = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(V)


def single_file_host(change=None, flags=0):
    # A format fixture, never executable code or a substitute for release proof.
    pe = b"MZ" + bytes(58) + struct.pack("<I", 64) + b"PE\0\0"
    entries = [[name, 1, pe, 0] for name in (V.SIGNER_HOST + ".dll", "BouncyCastle.Cryptography.dll",
               "System.Security.Cryptography.Pkcs.dll", "System.Private.CoreLib.dll", "System.Runtime.dll")]
    entries.extend([[V.SIGNER_HOST + ".deps.json", 3, V.canonical_json({"runtimeTarget": {
        "name": ".NETCoreApp,Version=v10.0/osx-arm64"}}), 0],
        [V.SIGNER_HOST + ".runtimeconfig.json", 4, V.canonical_json({"runtimeOptions": {"tfm": "net10.0",
        "includedFrameworks": [{"name": "Microsoft.NETCore.App", "version": V.DOTNET_VERSION}]}}), 0]])
    if change:
        change(entries)
    def string(value):
        data = value.encode()
        size, prefix = len(data), bytearray()
        while size >= 128:
            prefix.append((size & 127) | 128)
            size >>= 7
        return bytes(prefix + bytes([size])) + data
    native = bytes.fromhex("cffaedfe0c000001") + bytes(24)
    content = bytearray(native + bytes(8) + V.DOTNET_BUNDLE_MARKER)
    records, config = [], {}
    for name, kind, body, compressed in entries:
        offset = len(content)
        content += body
        records.append(struct.pack("<qqqB", offset, len(body), compressed, kind) + string(name))
        if kind in {3, 4}:
            config[kind] = (offset, len(body))
    header = len(content)
    content += struct.pack("<IIi", 6, 0, len(entries)) + string("PUBLICFIX001")
    content += struct.pack("<qqqqQ", *config.get(3, (0, 0)), *config.get(4, (0, 0)), flags)
    content += b"".join(records)
    struct.pack_into("<q", content, 32, header)
    return bytes(content)


def signer_archive(change=None, update_inventory=False):
    native = bytes.fromhex("cffaedfe0c000001") + bytes(24)
    files = {
        "Contents/Info.plist": (plistlib.dumps({"CFBundleIdentifier": V.SIGNER_PRODUCT_ID,
            "CFBundleShortVersionString": V.SIGNER_VERSION, "CFBundleVersion": V.SIGNER_BUILD,
            "CFBundleExecutable": V.SIGNER_HOST}), 0o644),
        "Contents/MacOS/" + V.SIGNER_HOST: (single_file_host(), 0o755),
        "Contents/MacOS/libmldsa87_ref.dylib": (native, 0o644),
        "Contents/Resources/README.md": (b"Public fixture only", 0o644),
        "Contents/Resources/SOURCE_PROVENANCE.json": (b"{}", 0o644),
        "Contents/Resources/Licenses/NOTICE.md": (b"Public fixture license", 0o644),
    }
    entries = []
    dirs = set()
    for name in files:
        parts = name.split("/")
        dirs.update("/".join(parts[:index]) for index in range(1, len(parts)))
    for name in sorted(dirs):
        item = zipfile.ZipInfo(V.SIGNER_APP_NAME + "/" + name + "/")
        item.create_system = 3
        item.external_attr = (stat.S_IFDIR | 0o755) << 16
        entries.append((item, b""))
    for name, (content, mode) in sorted(files.items()):
        item = zipfile.ZipInfo(V.SIGNER_APP_NAME + "/" + name)
        item.create_system = 3
        item.external_attr = (stat.S_IFREG | mode) << 16
        entries.append((item, content))
    def inventory():
        records = []
        directories = []
        for item, content in entries:
            relative = item.filename.removeprefix(V.SIGNER_APP_NAME + "/")
            if item.is_dir():
                directories.append(relative[:-1])
            else:
                records.append(V.file_record(relative, content, stat.S_IMODE(item.external_attr >> 16)))
        return V.canonical_json({"schema": "passphrase-memorizer-signer-inventory-v1",
            "product_id": V.SIGNER_PRODUCT_ID, "version": V.SIGNER_VERSION, "build": V.SIGNER_BUILD,
            "app_name": V.SIGNER_APP_NAME, "files": sorted(records, key=lambda record: record["path"]),
            "directories": sorted(directories)})
    inventory_data = inventory()
    if change:
        change(entries)
    if update_inventory:
        inventory_data = inventory()
    item = zipfile.ZipInfo(V.SIGNER_INVENTORY)
    item.create_system = 3
    item.external_attr = (stat.S_IFREG | 0o644) << 16
    entries.append((item, inventory_data))
    output = io.BytesIO()
    with zipfile.ZipFile(output, "w") as archive:
        with warnings.catch_warnings():
            warnings.simplefilter("ignore", UserWarning) # Intentional duplicate-path negative fixture.
            for item, content in entries:
                archive.writestr(item, content)
    return output.getvalue()


def mutate_archive(data, mutation):
    with zipfile.ZipFile(io.BytesIO(data)) as archive:
        entries = [(copy.copy(item), archive.read(item)) for item in archive.infolist()]
    mutation(entries)
    output = io.BytesIO()
    with zipfile.ZipFile(output, "w") as archive:
        for item, content in entries:
            archive.writestr(item, content)
    return output.getvalue()


class HybridFormatTests(unittest.TestCase):
    def testAppAndSignerVersionPoliciesAreIndependentAndLegacyExplicit(self):
        self.assertEqual(V.release_targets()[0], "Passphrase-Memorizer-1.0.1.zip")
        self.assertEqual(V.release_targets()[3], "Passphrase-Memorizer-HybridSigner-1.0.0.zip")
        signer_inventory = V.verify_signer_zip(signer_archive())
        self.assertEqual(signer_inventory["version"], "1.0.0")
        self.assertEqual(signer_inventory["build"], "1")
        with self.assertRaises(V.VerificationError):
            V.verify_signer_zip(signer_archive(), version="1.0.1", build="2")
        legacy = plistlib.dumps({"CFBundleIdentifier": V.PRODUCT_ID,
            "CFBundleExecutable": "MnemonicStoryApp", "CFBundleShortVersionString": "1.0.0", "CFBundleVersion": "1"})
        V.check_metadata(legacy, version="1.0.0", build="1")
        with self.assertRaises(V.VerificationError):
            V.check_metadata(legacy)
        new = plistlib.dumps({"CFBundleIdentifier": V.PRODUCT_ID,
            "CFBundleExecutable": "MnemonicStoryApp", "CFBundleShortVersionString": "1.0.1", "CFBundleVersion": "2"})
        V.check_metadata(new)
        for version, build in (("../1.0.0", "1"), ("1.0.0/extra", "1"), ("1.0", "1"),
                               ("01.0.0", "1"), ("1.0.0", "0"), ("1.0.0", "../2")):
            with self.subTest(version=version, build=build), self.assertRaises(V.VerificationError):
                V.validate_identity(version, build)

    def testEnvelopeLengthAndDigestCannotBeSwappedOrExtended(self):
        artifact = b"PUBLIC ENVELOPE FIXTURE"
        cert, rsa, ml = b"public certificate fixture", bytes(512), bytes(4627)
        envelope = V.HEADER.pack(V.MAGIC, 1, len(artifact), hashlib.sha512(artifact).digest(), len(cert), len(rsa), len(ml)) + cert + rsa + ml
        # Parsing is structural only, never acceptance of these dummy signatures.
        parsed = V.parse_envelope(envelope, artifact)
        self.assertEqual(parsed[:3], (cert, rsa, ml))
        for candidate, data in ((envelope[:-1], artifact), (envelope + b"X", artifact),
                                (envelope, artifact + b"X"), (envelope, b"Q" + artifact[1:])):
            with self.subTest(candidate_size=len(candidate), artifact=data):
                with self.assertRaises(V.VerificationError):
                    V.parse_envelope(candidate, data)

    def testEnvelopeAlgorithmFieldLengthsAndMagicRejectMalformedProfiles(self):
        artifact = b"PUBLIC"
        header = [V.MAGIC, 1, len(artifact), hashlib.sha512(artifact).digest(), 1, 512, 4627]
        for index, value in ((0, b"INVALID1"), (1, 2), (2, -1), (4, 0), (4, V.MAX_CERTIFICATE + 1), (5, 511), (6, 4626)):
            candidate = header.copy()
            candidate[index] = value
            with self.subTest(index=index, value=value), self.assertRaises(V.VerificationError):
                V.parse_envelope(V.HEADER.pack(*candidate) + bytes(1 + 512 + 4627), artifact)

    def testSafeSignerStructureAndRequiredOfflineComponents(self):
        V.verify_signer_zip(signer_archive())
        for required in ("Contents/MacOS/" + V.SIGNER_HOST, "Contents/MacOS/libmldsa87_ref.dylib",
                         "Contents/Resources/README.md", "Contents/Resources/SOURCE_PROVENANCE.json", "Contents/Resources/Licenses/NOTICE.md"):
            candidate = signer_archive(lambda e: e.__setitem__(slice(None), [(i, d) for i, d in e if i.filename != V.SIGNER_APP_NAME + "/" + required]), update_inventory=True)
            with self.subTest(required=required), self.assertRaises(V.VerificationError):
                V.verify_signer_zip(candidate)

    def testSignerCodeDirectoryRejectsLooseManagedFilesAndExtraNativePrograms(self):
        for name in (V.SIGNER_HOST + ".dll", V.SIGNER_HOST + ".deps.json", "libcoreclr.dylib", "other"):
            item = zipfile.ZipInfo(V.SIGNER_APP_NAME + "/Contents/MacOS/" + name)
            item.create_system = 3
            item.external_attr = (stat.S_IFREG | 0o755) << 16
            with self.subTest(name=name), self.assertRaises(V.VerificationError):
                V.verify_signer_zip(signer_archive(lambda e: e.append((item, b"public")), update_inventory=True))

    def testSingleFileRejectsMissingAmbiguousOutOfRangeAndFutureHeaders(self):
        valid = single_file_host()
        self.assertEqual(len(V.verify_single_file_bundle(valid)), 7)
        header = struct.unpack_from("<q", valid, 32)[0]
        candidates = [valid.replace(V.DOTNET_BUNDLE_MARKER, bytes(32)), valid + V.DOTNET_BUNDLE_MARKER,
                      valid[:header + 12], valid[:header + 12] + b"\xff" * 5]
        for offset, format, value in ((32, "<q", 0), (32, "<q", len(valid)), (header, "<I", 7),
                                      (header + 4, "<I", 1), (header + 8, "<i", 1025), (header + 8, "<i", -1)):
            candidate = bytearray(valid)
            struct.pack_into(format, candidate, offset, value)
            candidates.append(bytes(candidate))
        for index, candidate in enumerate(candidates):
            with self.subTest(index=index), self.assertRaises(V.VerificationError):
                V.verify_single_file_bundle(candidate)

    def testSingleFileRejectsExtractionCompressionBadPathsAndDuplicateNames(self):
        candidates = [single_file_host(flags=1)]
        for index, value in ((1, 0), (1, 2), (1, 5), (3, 1), (0, "../outside.dll"),
                             (0, "System.Runtime.dll"), (0, "system.runtime.dll")):
            def mutate(entries, index=index, value=value):
                entries[0][index] = value
            candidates.append(single_file_host(mutate))
        for index, candidate in enumerate(candidates):
            with self.subTest(index=index), self.assertRaises(V.VerificationError):
                V.verify_single_file_bundle(candidate)

    def testSingleFileRejectsMissingAssembliesAndInvalidPEFiles(self):
        for change in ("missing-assembly", "missing-config", "wrong-PE"):
            def mutate(entries):
                if change == "missing-assembly":
                    entries.pop(0)
                elif change == "missing-config":
                    entries.pop()
                else:
                    entries[0][2] = b"not a PE assembly" + bytes(68)
            with self.subTest(change=change), self.assertRaises(V.VerificationError):
                V.verify_single_file_bundle(single_file_host(mutate))

    def testSingleFileRejectsOverlapsBadBoundsAndConfigurationHeaderMismatches(self):
        valid = single_file_host()
        header = struct.unpack_from("<q", valid, 32)[0]
        # Header: 12 fixed bytes, 1+12 ID bytes, 4*8 locations and 8 flags.
        first = header + 65
        second = first + 25 + 1 + len(V.SIGNER_HOST + ".dll")
        for offset, value in ((first, -1), (first + 8, len(valid)), (second, 72),
                              (header + 25, 0), (header + 41, 0)):
            candidate = bytearray(valid)
            struct.pack_into("<q", candidate, offset, value)
            with self.subTest(offset=offset, value=value), self.assertRaises(V.VerificationError):
                V.verify_single_file_bundle(bytes(candidate))

    def testSingleFileRejectsExternalRuntimeAndWrongArchitectureConfiguration(self):
        for change in ("external-framework", "wrong-tfm", "wrong-architecture"):
            def mutate(entries):
                if change == "wrong-architecture":
                    entries[-2][2] = V.canonical_json({"runtimeTarget": {"name": ".NETCoreApp,Version=v10.0/osx-x64"}})
                else:
                    options = {"tfm": "net9.0" if change == "wrong-tfm" else "net10.0",
                               "includedFrameworks": [{"name": "Microsoft.NETCore.App", "version": V.DOTNET_VERSION}]}
                    if change == "external-framework":
                        options["framework"] = options["includedFrameworks"][0]
                    entries[-1][2] = V.canonical_json({"runtimeOptions": options})
            with self.subTest(change=change), self.assertRaises(V.VerificationError):
                V.verify_single_file_bundle(single_file_host(mutate))

    def testSignerArchiveRejectsTraversalSymlinksFIFOsAndSetuid(self):
        for path, mode in ((V.SIGNER_APP_NAME + "/../outside", stat.S_IFREG | 0o644),
                           (V.SIGNER_APP_NAME + "/link", stat.S_IFLNK | 0o777),
                           (V.SIGNER_APP_NAME + "/fifo", stat.S_IFIFO | 0o644),
                           (V.SIGNER_APP_NAME + "/setuid", stat.S_IFREG | 0o4755),
                           ("Other/file", stat.S_IFREG | 0o644)):
            item = zipfile.ZipInfo(path)
            item.create_system = 3
            item.external_attr = mode << 16
            candidate = signer_archive(lambda e: e.append((item, b"public")))
            with self.subTest(path=path), self.assertRaises(V.VerificationError):
                V.verify_signer_zip(candidate)

    def testSignerArchiveRejectsNonarm64HostAndNonexecutableHost(self):
        for change in ("wrong-architecture", "missing-executable-bit"):
            def mutate(entries):
                for index, (item, content) in enumerate(entries):
                    if item.filename == V.SIGNER_APP_NAME + "/Contents/MacOS/" + V.SIGNER_HOST:
                        if change == "wrong-architecture":
                            entries[index] = (item, b"not native arm64" + bytes(32))
                        else:
                            item.external_attr = (stat.S_IFREG | 0o644) << 16
            with self.subTest(change=change), self.assertRaises(V.VerificationError):
                V.verify_signer_zip(signer_archive(mutate, update_inventory=True))

    def testSignerArchiveRejectsDuplicateAndAliasedPaths(self):
        candidate = signer_archive(lambda e: e.append(copy.copy(e[0])))
        with self.assertRaises(V.VerificationError):
            V.verify_signer_zip(candidate)
        for path in ("a//b", "a/./b", "a\\b", "a\0b", "/absolute", "../escape"):
            with self.subTest(path=path), self.assertRaises(V.VerificationError):
                V.safe_path(path)

    def testSignerInventoryIsCanonicalAndBindsEveryFileModeAndDirectory(self):
        original = signer_archive()
        def edit_inventory(entries, mutation):
            for index, (item, content) in enumerate(entries):
                if item.filename == V.SIGNER_INVENTORY:
                    entries[index] = (item, mutation(content))
                    return
            raise AssertionError("Public inventory fixture missing")
        def alter_json(content, field, value):
            inventory = V.parse_json(content)
            inventory[field] = value
            return V.canonical_json(inventory)
        def add_self(content):
            inventory = V.parse_json(content)
            inventory["files"].append(V.file_record(V.SIGNER_INVENTORY, b"", 0o644))
            inventory["files"].sort(key=lambda item: item["path"])
            return V.canonical_json(inventory)
        candidates = [
            mutate_archive(original, lambda e: e.__setitem__(slice(None), [(i, d) for i, d in e if i.filename != V.SIGNER_INVENTORY])),
            mutate_archive(original, lambda e: edit_inventory(e, lambda d: b" " + d)),
            mutate_archive(original, lambda e: edit_inventory(e, lambda d: alter_json(d, "version", "wrong"))),
            mutate_archive(original, lambda e: edit_inventory(e, add_self)),
            mutate_archive(original, lambda e: e.__setitem__(slice(None), [(i, d) for i, d in e if i.filename != V.SIGNER_APP_NAME + "/Contents/Resources/Licenses/"])),
        ]
        def tamper_host(entries, permissions=False):
            for index, (item, content) in enumerate(entries):
                if item.filename == V.SIGNER_APP_NAME + "/Contents/MacOS/" + V.SIGNER_HOST:
                    if permissions:
                        item.external_attr = (stat.S_IFREG | 0o744) << 16
                    else:
                        entries[index] = (item, content[:-1] + b"X")
        candidates.append(mutate_archive(original, tamper_host))
        candidates.append(mutate_archive(original, lambda e: tamper_host(e, permissions=True)))
        for index, candidate in enumerate(candidates):
            with self.subTest(mutation=index), self.assertRaises(V.VerificationError):
                V.verify_signer_zip(candidate)

    def testSignerAppleDoubleMetadataIsAllowedOnlyForExistingLogicalTargets(self):
        def append_metadata(entries, path):
            item = zipfile.ZipInfo(path)
            item.create_system = 3
            item.external_attr = (stat.S_IFREG | 0o644) << 16
            entries.append((item, b"public AppleDouble structural fixture"))
        original = signer_archive()
        valid = mutate_archive(original, lambda e: append_metadata(e,
            "__MACOSX/" + V.SIGNER_APP_NAME + "/Contents/MacOS/._" + V.SIGNER_HOST))
        V.verify_signer_zip(valid)
        for path in ("__MACOSX/other/._file", "__MACOSX/" + V.SIGNER_APP_NAME + "/Contents/._missing",
                     "__MACOSX/" + V.SIGNER_APP_NAME + "/Contents/../._escape",
                     "__MACOSX/" + V.SIGNER_APP_NAME + "/Contents/not-AppleDouble"):
            candidate = mutate_archive(original, lambda e: append_metadata(e, path))
            with self.subTest(path=path), self.assertRaises(V.VerificationError):
                V.verify_signer_zip(candidate)

    def testPublicReadRejectsFinalSymlinksAndSizeBounds(self):
        with tempfile.TemporaryDirectory(prefix="passphrase-memorizer-format-test-") as directory:
            file = pathlib.Path(directory) / "public"
            file.write_bytes(b"PUBLIC")
            link = pathlib.Path(directory) / "link"
            link.symlink_to(file)
            self.assertEqual(V.read_regular(file, 6)[0], b"PUBLIC")
            for candidate, limit in ((link, 6), (file, 5), (pathlib.Path(directory), 65536)):
                with self.subTest(candidate=candidate, limit=limit), self.assertRaises(V.VerificationError):
                    V.read_regular(candidate, limit)

    def testProductIdentityAndAllSignerHashesAreBoundInManifest(self):
        hashes = {key: "a" * size for key, size in V.HASH_LENGTHS.items()}
        signer_hashes = {key: "b" * size for key, size in V.HASH_LENGTHS.items()}
        fields = {"artifact": V.TARGETS[0], "coverage": "complete-signed-app-archive", "bundle-identifier": V.PRODUCT_ID,
                  "bundle-version": V.VERSION, "bundle-build": V.BUILD, "architecture": "arm64", "minimum-macos": "14.0",
                  "signature-mode": "developer-id-notarized", "signer-artifact": V.SIGNER_TARGET,
                  **hashes, **{"signer-" + key: value for key, value in signer_hashes.items()}}
        def encode(value):
            return ("Passphrase Memorizer Release Integrity Manifest v1\n" + "".join(key + "=" + item + "\n" for key, item in value.items())).encode()
        V.verify_manifest(encode(fields), hashes, signer_hashes)
        for key in ("bundle-identifier", "bundle-version", "bundle-build", "signer-artifact", "signer-sha256", "signer-sha3-512", "signer-skein-1024-1024"):
            candidate = fields.copy()
            candidate[key] = "wrong"
            with self.subTest(field=key), self.assertRaises(V.VerificationError):
                V.verify_manifest(encode(candidate), hashes, signer_hashes)


if __name__ == "__main__":
    unittest.main(verbosity=2)
