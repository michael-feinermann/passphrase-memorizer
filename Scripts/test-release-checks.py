#!/usr/bin/env python3
"""Adversarial tests for ZIP extraction and release entitlement boundaries."""
import io
import stat
import struct
import unittest
import warnings
import zipfile

from release_checks import MAX_ARCHIVE_ENTRIES, MAX_EXPANDED_BYTES, preflight_archive, validate_entitlements


def fixture(names, mode=None):
    stream = io.BytesIO()
    with warnings.catch_warnings():
        warnings.simplefilter("ignore", UserWarning)
        with zipfile.ZipFile(stream, "w", compression=zipfile.ZIP_DEFLATED) as archive:
            for name in names:
                entry = zipfile.ZipInfo(name)
                if mode is not None:
                    entry.create_system = 3
                    entry.external_attr = (mode | 0o600) << 16
                archive.writestr(entry, b"public fixture")
    return stream.getvalue()


def check(data):
    with zipfile.ZipFile(io.BytesIO(data)) as archive:
        preflight_archive(archive)


class ArchiveTests(unittest.TestCase):
    def test_valid_archive(self):
        check(fixture(["Passphrase Memorizer.app/Contents/Info.plist", "__MACOSX/._Passphrase Memorizer.app"]))

    def test_traversal_absolute_and_noncanonical_paths(self):
        for name in ["../outside", "/tmp/outside", "Passphrase Memorizer.app/../../outside",
                     "Passphrase Memorizer.app//Contents/file", "Passphrase Memorizer.app/./Contents/file",
                     "Passphrase Memorizer.app\\outside", "Passphrase Memorizer.app/Contents/new\nline", "Other.app/file"]:
            with self.subTest(name=name), self.assertRaises(ValueError):
                check(fixture([name]))

    def test_unix_special_files(self):
        for mode in [stat.S_IFLNK, stat.S_IFIFO, stat.S_IFCHR, stat.S_IFBLK, stat.S_IFSOCK]:
            with self.subTest(mode=mode), self.assertRaises(ValueError):
                check(fixture(["Passphrase Memorizer.app/Contents/special"], mode))

    def test_duplicates_including_case_and_unicode(self):
        for names in [["Passphrase Memorizer.app/file", "Passphrase Memorizer.app/file"],
                      ["Passphrase Memorizer.app/File", "Passphrase Memorizer.app/file"],
                      ["Passphrase Memorizer.app/caf\u00e9", "Passphrase Memorizer.app/cafe\u0301"]]:
            with self.subTest(names=names), self.assertRaises(ValueError):
                check(fixture(names))

    def test_entry_count_limit(self):
        with self.assertRaises(ValueError):
            check(fixture(["Passphrase Memorizer.app/" + str(index) for index in range(MAX_ARCHIVE_ENTRIES + 1)]))

    def test_declared_expanded_size_limit_before_decompression(self):
        data = bytearray(fixture(["Passphrase Memorizer.app/large"]))
        central = data.index(b"PK\x01\x02")
        struct.pack_into("<I", data, central + 24, MAX_EXPANDED_BYTES + 1)
        with self.assertRaises(ValueError):
            check(data)

    def test_total_expanded_size_limit(self):
        data = bytearray(fixture(["Passphrase Memorizer.app/one", "Passphrase Memorizer.app/two"]))
        offset = 0
        for _ in range(2):
            central = data.index(b"PK\x01\x02", offset)
            struct.pack_into("<I", data, central + 24, MAX_EXPANDED_BYTES // 2 + 1)
            offset = central + 46
        with self.assertRaises(ValueError):
            check(data)

    def test_empty_archive(self):
        with self.assertRaises(ValueError):
            check(fixture([]))

    def test_nul_filename_truncation(self):
        data = fixture(["Passphrase Memorizer.app/Contents/safeXsuffix"])
        data = data.replace(b"safeXsuffix", b"safe\0suffix")
        with self.assertRaises(ValueError):
            check(data)


class EntitlementTests(unittest.TestCase):
    def test_empty_or_expected_metadata(self):
        validate_entitlements({}, "local.passphrasereminder.reminder")
        validate_entitlements({"com.apple.developer.team-identifier": "2T6K9PGS55",
                               "com.apple.application-identifier": "2T6K9PGS55.local.passphrasereminder.reminder"},
                              "local.passphrasereminder.reminder")

    def test_relaxed_entitlements_even_when_false(self):
        for key in ["com.apple.security.get-task-allow", "com.apple.security.cs.allow-jit",
                    "com.apple.security.cs.allow-unsigned-executable-memory", "com.apple.security.cs.disable-library-validation",
                    "com.apple.security.cs.disable-executable-page-protection", "com.apple.security.app-sandbox",
                    "com.apple.security.network.client", "com.apple.security.files.user-selected.read-write"]:
            for value in [True, False]:
                with self.subTest(key=key, value=value), self.assertRaises(ValueError):
                    validate_entitlements({key: value}, "local.passphrasereminder.reminder")

    def test_malformed_and_wrong_signing_metadata(self):
        for value in [[], None, {"com.apple.developer.team-identifier": "OTHER"},
                      {"com.apple.application-identifier": "2T6K9PGS55.other"},
                      {"com.apple.application-identifier": False}]:
            with self.subTest(value=value), self.assertRaises(ValueError):
                validate_entitlements(value, "local.passphrasereminder.reminder")


if __name__ == "__main__":
    unittest.main()
