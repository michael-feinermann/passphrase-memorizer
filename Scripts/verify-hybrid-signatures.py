#!/usr/bin/env python3
"""Verify detached Passphrase Memorizer release signatures using public data only.

The KZVHSIG1 envelope and legacy domains are interoperable with Keep Vault's
HybridSigner. Both RSA-PSS/SHA-512 and pure ML-DSA-87 must verify. The trusted
public-key fingerprints must themselves come from an independently trusted
channel. This release check does not change the app's Apple-only runtime gate.
"""

import argparse
import hashlib
import hmac
import io
import json
import os
import pathlib
import plistlib
import re
import shutil
import stat
import struct
import subprocess
import sys
import tempfile
import zipfile


PRODUCT_ID = "local.passphrasereminder.reminder"
VERSION = "1.0.0"
BUILD = "1"
APP_NAME = "Passphrase Memorizer.app"
STEM = "Passphrase-Memorizer-1.0.0"
SIGNER_TARGET = "Passphrase-Memorizer-HybridSigner-1.0.0.zip"
SIGNER_APP_NAME = "Passphrase Memorizer Hybrid Signer.app"
SIGNER_PRODUCT_ID = "local.passphrasememorizer.hybridsigner"
SIGNER_INVENTORY = "signer-inventory.json"
SIGNER_HOST = "PassphraseMemorizer.HybridSigner"
DOTNET_BUNDLE_MARKER = bytes.fromhex("8b1202b96a612038727b930214d7a03213f5b9e6efae3318ee3b2dce24b36aae")
DOTNET_VERSION = "10.0.11"
TARGETS = (STEM + ".zip", STEM + ".integrity.txt", STEM + ".bundle-inventory.json", SIGNER_TARGET)
TARGET_LIMITS = (512 * 1024 * 1024, 65536, 8 * 1024 * 1024, 512 * 1024 * 1024)
MAGIC = b"KZVHSIG1"
PAYLOAD_DOMAIN = b"KalynaZpaqVault/HybridArtifactSignature/SHA-512/v1\0"
ML_CONTEXT = "KalynaZpaqVault/HybridArtifactSignature/v1"
HEADER = struct.Struct("<8siq64siii")
MAX_SIDECAR = 65536
MAX_CERTIFICATE = 32768
MAX_TRUST = 65536
MAX_INVENTORY = 8 * 1024 * 1024
MAX_ZIP = 512 * 1024 * 1024
MAX_BUNDLE_BYTES = 512 * 1024 * 1024
MAX_ENTRIES = 100000
HASH_LENGTHS = {"sha256": 64, "sha3-512": 128, "skein-1024-1024": 256}


class VerificationError(Exception):
    pass


def require(condition, message):
    if not condition:
        raise VerificationError(message)


def exact_keys(value, keys, name):
    require(type(value) is dict and set(value) == set(keys), "Invalid " + name + " schema")


def safe_path(value, basename=False):
    require(type(value) is str and 0 < len(value) <= 1024, "Invalid relative path")
    require(all(32 <= ord(c) <= 126 for c in value), "Paths must be printable ASCII")
    require("\\" not in value and ":" not in value, "Unsafe path separator")
    parts = value.split("/")
    require(all(part not in {"", ".", ".."} for part in parts), "Unsafe relative path")
    require(not basename or len(parts) == 1, "Public key must be a basename")
    return value


def read_regular(path, limit):
    """Bind reads to an open regular file, without following a final symlink."""
    path = pathlib.Path(path)
    try:
        fd = os.open(path, os.O_RDONLY | getattr(os, "O_NOFOLLOW", 0) | getattr(os, "O_NONBLOCK", 0))
        with os.fdopen(fd, "rb") as source:
            before = os.fstat(source.fileno())
            require(stat.S_ISREG(before.st_mode), "Nonregular input: " + path.name)
            require(0 <= before.st_size <= limit, "Input exceeds bound: " + path.name)
            data = source.read(limit + 1)
            after = os.fstat(source.fileno())
        current = path.lstat()
    except OSError as error:
        raise VerificationError("Cannot read public input: " + path.name) from error
    fields = ("st_dev", "st_ino", "st_size", "st_mtime_ns", "st_ctime_ns", "st_mode")
    require(all(getattr(before, f) == getattr(after, f) == getattr(current, f) for f in fields),
            "Input changed while reading: " + path.name)
    require(len(data) == before.st_size and len(data) <= limit, "Incomplete input: " + path.name)
    return data, stat.S_IMODE(before.st_mode)


def reject_duplicate_keys(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result, "Duplicate JSON key")
        result[key] = value
    return result


def parse_json(data):
    try:
        return json.loads(data.decode("utf-8"), object_pairs_hook=reject_duplicate_keys,
                          parse_constant=lambda value: (_ for _ in ()).throw(VerificationError("Invalid JSON number")))
    except (ValueError, UnicodeError, RecursionError) as error:
        raise VerificationError("Invalid JSON") from error


def canonical_json(value):
    return json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=True).encode("utf-8") + b"\n"


def validate_hashes(value):
    exact_keys(value, HASH_LENGTHS, "fingerprints")
    for algorithm, length in HASH_LENGTHS.items():
        require(type(value[algorithm]) is str and re.fullmatch("[0-9a-f]{%d}" % length, value[algorithm]),
                "Invalid " + algorithm + " fingerprint")


def parse_trust(data):
    trust = parse_json(data)
    exact_keys(trust, {"schema", "product_id", "rsa_spki", "mldsa_public_key",
                       "rsa_spki_hashes", "mldsa_public_key_hashes"}, "trust")
    require(trust["schema"] == "passphrase-memorizer-hybrid-trust-v1" and trust["product_id"] == PRODUCT_ID,
            "Wrong trust product or schema")
    safe_path(trust["rsa_spki"], basename=True)
    safe_path(trust["mldsa_public_key"], basename=True)
    require(trust["rsa_spki"] != trust["mldsa_public_key"], "Public key filenames must differ")
    validate_hashes(trust["rsa_spki_hashes"])
    validate_hashes(trust["mldsa_public_key_hashes"])
    return trust


def parse_inventory(data):
    return _parse_inventory(data, {"schema": "passphrase-memorizer-bundle-inventory-v1", "product_id": PRODUCT_ID,
                                   "version": VERSION, "build": BUILD, "app_name": APP_NAME})


def parse_signer_inventory(data):
    return _parse_inventory(data, {"schema": "passphrase-memorizer-signer-inventory-v1", "product_id": SIGNER_PRODUCT_ID,
                                   "version": VERSION, "build": BUILD, "app_name": SIGNER_APP_NAME})


def _parse_inventory(data, expected):
    inventory = parse_json(data)
    exact_keys(inventory, {"schema", "product_id", "version", "build", "app_name", "files", "directories"}, "inventory")
    require(all(inventory[key] == value for key, value in expected.items()), "Wrong inventory identity")
    require(type(inventory["files"]) is list and 0 < len(inventory["files"]) <= MAX_ENTRIES, "Invalid inventory files")
    require(type(inventory["directories"]) is list and len(inventory["directories"]) <= MAX_ENTRIES, "Invalid inventory directories")
    paths = []
    total = 0
    for item in inventory["files"]:
        exact_keys(item, {"path", "size", "mode", "sha512"}, "inventory file")
        paths.append(safe_path(item["path"]))
        require(type(item["size"]) is int and 0 <= item["size"] <= MAX_BUNDLE_BYTES, "Invalid file size")
        require(type(item["mode"]) is int and 0 <= item["mode"] <= 0o777, "Invalid file mode")
        require(type(item["sha512"]) is str and re.fullmatch(r"[0-9a-f]{128}", item["sha512"]), "Invalid file SHA-512")
        total += item["size"]
    require(total <= MAX_BUNDLE_BYTES, "Bundle exceeds size bound")
    require(paths == sorted(set(paths)), "Files must be sorted and unique")
    directories = [safe_path(path) for path in inventory["directories"]]
    require(directories == sorted(set(directories)), "Directories must be sorted and unique")
    require(not set(paths).intersection(directories), "Path is both file and directory")
    for path in paths + directories:
        parts = path.split("/")
        for index in range(1, len(parts)):
            require("/".join(parts[:index]) in directories, "Missing inventory parent directory")
    require("Contents/Info.plist" in paths, "Missing app metadata")
    require(data == canonical_json(inventory), "Inventory JSON is not canonical")
    return inventory


def parse_envelope(data, artifact):
    require(HEADER.size == 96 and 96 <= len(data) <= MAX_SIDECAR, "Invalid signature size")
    magic, version, length, digest, cert_length, rsa_length, ml_length = HEADER.unpack_from(data)
    require(magic == MAGIC and version == 1, "Unknown signature envelope")
    require(length >= 0 and length == len(artifact), "Signature file length mismatch")
    actual_digest = hashlib.sha512(artifact).digest()
    require(hmac.compare_digest(digest, actual_digest), "Signature SHA-512 binding mismatch")
    require(1 <= cert_length <= MAX_CERTIFICATE and rsa_length == 512 and ml_length == 4627,
            "Invalid signature field lengths")
    require(len(data) == 96 + cert_length + rsa_length + ml_length, "Truncated or trailing signature bytes")
    offset = 96
    certificate = data[offset:offset + cert_length]
    offset += cert_length
    rsa = data[offset:offset + rsa_length]
    ml = data[offset + rsa_length:]
    payload = PAYLOAD_DOMAIN + struct.pack("<q", length) + actual_digest
    return certificate, rsa, ml, payload


def der(tag, content):
    length = len(content)
    if length < 128:
        prefix = bytes([length])
    else:
        encoded = length.to_bytes((length.bit_length() + 7) // 8, "big")
        prefix = bytes([0x80 | len(encoded)]) + encoded
    return bytes([tag]) + prefix + content


def mldsa_spki(raw):
    require(len(raw) == 2592, "ML-DSA-87 public key must contain exactly 2592 bytes")
    algorithm = bytes.fromhex("300b0609608648016503040313")
    return der(0x30, algorithm + der(0x03, b"\0" + raw))


def validate_der_sequence(data):
    require(len(data) >= 2 and data[0] == 0x30, "Invalid certificate DER")
    first = data[1]
    if first < 128:
        length, header_size = first, 2
    else:
        count = first & 127
        require(1 <= count <= 4 and len(data) >= 2 + count and data[2] != 0, "Invalid DER length")
        length, header_size = int.from_bytes(data[2:2 + count], "big"), 2 + count
        require(length >= 128 and (length.bit_length() + 7) // 8 == count, "Noncanonical DER length")
    require(header_size + length == len(data), "Trailing or truncated certificate DER")


class PublicVerifier:
    def __init__(self, openssl, checksum_tool, temporary):
        self.openssl = str(pathlib.Path(openssl).resolve())
        self.checksum_tool = str(pathlib.Path(checksum_tool).resolve())
        require(os.access(self.openssl, os.X_OK) and os.access(self.checksum_tool, os.X_OK), "Verification executable missing")
        self.temporary = pathlib.Path(temporary)
        self.environment = {k: v for k, v in os.environ.items()
                            if not k.startswith(("OPENSSL_", "DYLD_", "LD_"))}
        self.environment.update({"OPENSSL_CONF": os.devnull, "LC_ALL": "C"})
        version = self.run([self.openssl, "version"]).decode("ascii", "strict")
        match = re.match(r"OpenSSL (\d+)\.(\d+)\.(\d+)(?:\s|[-+])", version)
        require(match is not None and tuple(map(int, match.groups())) >= (3, 5, 0), "OpenSSL 3.5 or newer is required")
        self.certificates = {}
        self.serial = 0

    def run(self, command, input_data=None):
        try:
            result = subprocess.run(command, input=input_data, stdout=subprocess.PIPE,
                                    stderr=subprocess.PIPE, env=self.environment, timeout=120, check=False)
        except (OSError, subprocess.TimeoutExpired) as error:
            raise VerificationError("Public verification command failed") from error
        require(result.returncode == 0, "Public verification command rejected input: " + pathlib.Path(command[0]).name)
        require(len(result.stdout) <= 1024 * 1024 and len(result.stderr) <= 1024 * 1024, "Excessive verification output")
        return result.stdout

    def snapshot(self, data, suffix):
        self.serial += 1
        path = self.temporary / (str(self.serial) + suffix)
        path.write_bytes(data)
        return str(path)

    def hashes(self, data):
        path = self.snapshot(data, ".public-data")
        skein = self.run([self.checksum_tool, path]).decode("ascii", "strict")
        require(re.fullmatch(r"[0-9a-f]{256}", skein), "Invalid checksum tool output")
        return {"sha256": hashlib.sha256(data).hexdigest(), "sha3-512": hashlib.sha3_512(data).hexdigest(),
                "skein-1024-1024": skein}

    def check_certificate(self, certificate, pinned_rsa):
        cache_key = (hashlib.sha512(certificate).digest(), hashlib.sha512(pinned_rsa).digest())
        if cache_key in self.certificates:
            return self.certificates[cache_key]
        validate_der_sequence(certificate)
        cert_path = self.snapshot(certificate, ".certificate.der")
        pem = self.run([self.openssl, "x509", "-inform", "DER", "-in", cert_path, "-outform", "PEM"])
        pem_path = self.snapshot(pem, ".certificate.pem")
        pub_pem = self.run([self.openssl, "x509", "-in", pem_path, "-pubkey", "-noout"])
        spki = self.run([self.openssl, "pkey", "-pubin", "-outform", "DER"], pub_pem)
        require(hmac.compare_digest(spki, pinned_rsa), "Certificate RSA key does not match trusted SPKI")
        key_path = self.snapshot(spki, ".rsa-spki.der")
        text = self.run([self.openssl, "pkey", "-pubin", "-inform", "DER", "-in", key_path, "-text_pub", "-noout"])
        require(re.search(rb"^Public-Key: \(4096 bit\)$", text, re.MULTILINE), "Signing key must be RSA-4096")
        cert_text = self.run([self.openssl, "x509", "-in", pem_path, "-text", "-noout"])
        algorithms = re.findall(rb"Signature Algorithm: ([^\r\n]+)", cert_text)
        require(algorithms == [b"sha512WithRSAEncryption", b"sha512WithRSAEncryption"], "Certificate must use SHA-512 with RSA")
        usage = self.run([self.openssl, "x509", "-in", pem_path, "-noout", "-ext", "keyUsage"])
        eku = self.run([self.openssl, "x509", "-in", pem_path, "-noout", "-ext", "extendedKeyUsage"])
        require(b"Digital Signature" in usage and b"Code Signing" in eku, "Certificate signing usage is missing")
        subject = self.run([self.openssl, "x509", "-in", pem_path, "-noout", "-subject", "-nameopt", "RFC2253"])
        issuer = self.run([self.openssl, "x509", "-in", pem_path, "-noout", "-issuer", "-nameopt", "RFC2253"])
        require(subject.removeprefix(b"subject=") == issuer.removeprefix(b"issuer="), "Signing certificate must be self-issued")
        # A pinned key establishes trust. The self-signature binds the certificate
        # policy; verify also enforces NotBefore and NotAfter at current UTC time.
        self.run([self.openssl, "verify", "-trusted", pem_path, "-check_ss_sig", "-purpose", "any", pem_path])
        self.certificates[cache_key] = key_path
        return key_path

    def verify(self, sidecar, artifact, pinned_rsa, ml_spki_path):
        certificate, rsa, ml, payload = parse_envelope(sidecar, artifact)
        rsa_path = self.check_certificate(certificate, pinned_rsa)
        payload_path = self.snapshot(payload, ".payload")
        rsa_sig_path = self.snapshot(rsa, ".rsa-signature")
        ml_sig_path = self.snapshot(ml, ".ml-signature")
        self.run([self.openssl, "pkeyutl", "-verify", "-rawin", "-digest", "sha512", "-pubin", "-keyform", "DER",
                  "-inkey", rsa_path, "-in", payload_path, "-sigfile", rsa_sig_path,
                  "-pkeyopt", "rsa_padding_mode:pss", "-pkeyopt", "rsa_pss_saltlen:64", "-pkeyopt", "rsa_mgf1_md:sha512"])
        self.run([self.openssl, "pkeyutl", "-verify", "-rawin", "-pubin", "-keyform", "DER",
                  "-inkey", ml_spki_path, "-in", payload_path, "-sigfile", ml_sig_path,
                  "-pkeyopt", "context-string:" + ML_CONTEXT, "-pkeyopt", "message-encoding:1"])


def check_metadata(data, product_id=PRODUCT_ID, executable="MnemonicStoryApp"):
    try:
        metadata = plistlib.loads(data)
    except (ValueError, plistlib.InvalidFileException) as error:
        raise VerificationError("Invalid bundle metadata") from error
    require(type(metadata) is dict, "Invalid bundle metadata type")
    expected = {"CFBundleIdentifier": product_id, "CFBundleShortVersionString": VERSION,
                "CFBundleVersion": BUILD, "CFBundleExecutable": executable}
    require(all(metadata.get(key) == value for key, value in expected.items()), "Wrong app identity, version or build")


def file_record(path, data, mode):
    require(0 <= mode <= 0o777, "Special file permission bits are forbidden")
    return {"path": path, "size": len(data), "mode": mode, "sha512": hashlib.sha512(data).hexdigest()}


def compare_bundle(files, directories, metadata, inventory):
    require(sorted(files, key=lambda item: item["path"]) == inventory["files"], "Bundle file set, bytes or modes differ from signed inventory")
    require(sorted(directories) == inventory["directories"], "Bundle directories differ from signed inventory")
    require(metadata is not None, "Missing app Info.plist")
    check_metadata(metadata)


def verify_zip(data, inventory):
    files, directories, names = [], [], set()
    metadata = None
    total = 0
    try:
        with zipfile.ZipFile(io.BytesIO(data)) as archive:
            entries = archive.infolist()
            require(0 < len(entries) <= MAX_ENTRIES, "Invalid ZIP entry count")
            for entry in entries:
                name = entry.filename
                require(name == entry.orig_filename, "NUL-truncated ZIP name")
                directory = entry.is_dir()
                normalized = safe_path(name[:-1] if directory else name)
                require(normalized not in names, "Duplicate or aliased ZIP path")
                names.add(normalized)
                raw_mode = entry.external_attr >> 16
                kind = stat.S_IFMT(raw_mode)
                require(kind in {0, stat.S_IFREG, stat.S_IFDIR}, "ZIP symlink or nonregular payload")
                require(not entry.flag_bits & 1, "Encrypted ZIP entry")
                require(kind != stat.S_IFDIR or directory, "ZIP directory type mismatch")
                require(kind != stat.S_IFREG or not directory, "ZIP file type mismatch")
                parts = normalized.split("/")
                if parts[0] == "__MACOSX":
                    require(len(parts) == 1 or parts[1] in {APP_NAME, "._" + APP_NAME}, "Unexpected ZIP metadata")
                    # ditto's AppleDouble records are authenticated by the ZIP
                    # signature but are not ordinary files inside the app tree.
                    require(directory or parts[-1].startswith("._"), "Unexpected AppleDouble metadata file")
                    continue
                require(parts[0] == APP_NAME, "Unexpected ZIP root")
                if len(parts) == 1:
                    require(directory, "App root must be a directory")
                    continue
                relative = "/".join(parts[1:])
                if directory:
                    require(entry.file_size == 0, "Nonempty ZIP directory")
                    directories.append(relative)
                else:
                    require(0 <= entry.file_size <= MAX_BUNDLE_BYTES, "ZIP file exceeds size bound")
                    total += entry.file_size
                    require(total <= MAX_BUNDLE_BYTES, "ZIP bundle exceeds size bound")
                    contents = archive.read(entry)
                    require(len(contents) == entry.file_size, "ZIP length mismatch")
                    files.append(file_record(relative, contents, stat.S_IMODE(raw_mode)))
                    if relative == "Contents/Info.plist":
                        metadata = contents
    except (OSError, ValueError, zipfile.BadZipFile, RuntimeError, NotImplementedError) as error:
        raise VerificationError("Invalid release ZIP") from error
    compare_bundle(files, directories, metadata, inventory)


def verify_app(app, inventory):
    app = pathlib.Path(app)
    require(app.name == APP_NAME and stat.S_ISDIR(app.lstat().st_mode), "Wrong app directory")
    files, directories = [], []
    metadata = None
    total = 0
    try:
        def walk_error(error):
            raise VerificationError("Cannot enumerate app bundle") from error
        for directory, dirs, filenames in os.walk(app, followlinks=False, onerror=walk_error):
            for name in dirs:
                path = pathlib.Path(directory) / name
                require(stat.S_ISDIR(path.lstat().st_mode), "App contains a symlink or non-directory")
                directories.append(safe_path(path.relative_to(app).as_posix()))
            for name in filenames:
                path = pathlib.Path(directory) / name
                relative = safe_path(path.relative_to(app).as_posix())
                contents, mode = read_regular(path, MAX_BUNDLE_BYTES)
                total += len(contents)
                require(total <= MAX_BUNDLE_BYTES and len(files) + len(directories) <= MAX_ENTRIES, "App exceeds size bound")
                files.append(file_record(relative, contents, mode))
                if relative == "Contents/Info.plist":
                    metadata = contents
    except OSError as error:
        raise VerificationError("Cannot enumerate app bundle") from error
    compare_bundle(files, directories, metadata, inventory)


def verify_single_file_bundle(content):
    """Read the public .NET 10 v6 manifest; never load or extract embedded code.

    Format sources (MIT, .NET Foundation): dotnet/runtime v10.0.11,
    src/installer/managed/Microsoft.NET.HostModel/Bundle/{Bundler,Manifest,
    FileEntry,FileType}.cs. Only assemblies and the two configuration types are
    allowed; NativeBinary/Unknown/Symbols would need disk extraction.
    """
    marker = content.find(DOTNET_BUNDLE_MARKER)
    require(marker >= 8 and content.find(DOTNET_BUNDLE_MARKER, marker + 1) == -1,
            "Missing or ambiguous .NET bundle marker")
    header = struct.unpack_from("<q", content, marker - 8)[0]
    require(marker + len(DOTNET_BUNDLE_MARKER) <= header <= len(content) - 12,
            "Invalid .NET bundle header offset")
    cursor = header

    def read(format):
        nonlocal cursor
        length = struct.calcsize(format)
        require(cursor + length <= len(content), "Truncated .NET bundle manifest")
        values = struct.unpack_from(format, content, cursor)
        cursor += length
        return values

    def string():
        nonlocal cursor
        length = 0
        for index in range(5):
            byte, = read("<B")
            require(index < 4 or byte <= 7, "Invalid .NET string length")
            length |= (byte & 127) << (7 * index)
            if byte < 128:
                require(index == 0 or byte > 0, "Noncanonical .NET string length")
                break
        else:
            raise VerificationError("Invalid .NET string length")
        require(0 < length <= 1024 and cursor + length <= len(content), "Invalid .NET string bounds")
        raw = content[cursor:cursor + length]
        cursor += length
        try:
            return raw.decode("utf-8")
        except UnicodeError as error:
            raise VerificationError("Invalid .NET string encoding") from error

    major, minor, count = read("<IIi")
    require((major, minor) == (6, 0) and 0 < count <= 1024, "Unexpected .NET bundle version or count")
    require(re.fullmatch("[A-Za-z0-9_-]{12}", string()), "Invalid .NET bundle ID")
    deps_offset, deps_size, config_offset, config_size, flags = read("<qqqqQ")
    require(flags == 0, "The signer bundle may not enable all-content extraction")
    entries = {}
    ranges = []
    for _ in range(count):
        offset, size, compressed, kind = read("<qqqB")
        name = safe_path(string(), basename=True)
        require(name.casefold() not in {key.casefold() for key in entries}, "Duplicate .NET bundle path")
        require(kind in {1, 3, 4} and compressed == 0,
                "The signer bundle may contain only uncompressed assemblies and configuration")
        require(marker + len(DOTNET_BUNDLE_MARKER) <= offset < header and 0 < size <= header - offset,
                "Invalid embedded .NET file bounds")
        ranges.append((offset, offset + size))
        entries[name] = (kind, offset, size)
        if kind == 1:
            require(name.endswith(".dll") and size >= 68 and content[offset:offset + 2] == b"MZ",
                    "Invalid embedded managed assembly")
            pe = struct.unpack_from("<I", content, offset + 60)[0]
            require(64 <= pe <= size - 4 and content[offset + pe:offset + pe + 4] == b"PE\0\0",
                    "Invalid embedded PE header")
    ranges.sort()
    require(all(left[1] <= right[0] for left, right in zip(ranges, ranges[1:])), "Overlapping .NET bundle files")
    assemblies = {SIGNER_HOST + ".dll", "BouncyCastle.Cryptography.dll", "System.Security.Cryptography.Pkcs.dll",
                  "System.Private.CoreLib.dll", "System.Runtime.dll"}
    require(assemblies <= entries.keys() and all(entries[name][0] == 1 for name in assemblies),
            "Embedded signer, cryptography dependencies or managed runtime are missing")
    deps_name, config_name = SIGNER_HOST + ".deps.json", SIGNER_HOST + ".runtimeconfig.json"
    require(entries.get(deps_name) == (3, deps_offset, deps_size) and
            entries.get(config_name) == (4, config_offset, config_size) and
            sum(item[0] == 3 for item in entries.values()) == 1 and
            sum(item[0] == 4 for item in entries.values()) == 1,
            "Embedded .NET configuration does not match its manifest header")
    require(deps_size <= MAX_INVENTORY and config_size <= MAX_TRUST, "Embedded configuration exceeds bounds")
    deps = parse_json(content[deps_offset:deps_offset + deps_size])
    config = parse_json(content[config_offset:config_offset + config_size])
    require(type(deps) is dict and type(deps.get("runtimeTarget")) is dict and
            deps["runtimeTarget"].get("name") == ".NETCoreApp,Version=v10.0/osx-arm64",
            "Wrong embedded .NET runtime target")
    options = config.get("runtimeOptions") if type(config) is dict else None
    require(type(options) is dict and options.get("tfm") == "net10.0" and
            options.get("includedFrameworks") == [{"name": "Microsoft.NETCore.App", "version": DOTNET_VERSION}] and
            "framework" not in options and "frameworks" not in options,
            "The embedded .NET runtime must be self-contained")
    return entries


def verify_signer_zip(data):
    """Check the authenticated signer archive and its canonical inner inventory.

    The inventory stays beside the sealed .app. Its own bytes are authenticated
    by the outer ZIP's hybrid signatures; every app file, mode and directory is
    authenticated by the inventory. This function never extracts or executes it.
    """
    files, directories, contents, names = [], [], {}, set()
    metadata_targets = set()
    total = 0
    inventory_data = None
    metadata = None
    try:
        with zipfile.ZipFile(io.BytesIO(data)) as archive:
            entries = archive.infolist()
            require(0 < len(entries) <= MAX_ENTRIES, "Invalid signer ZIP entry count")
            for entry in entries:
                require(entry.filename == entry.orig_filename, "NUL-truncated signer ZIP name")
                directory = entry.is_dir()
                normalized = safe_path(entry.filename[:-1] if directory else entry.filename)
                require(normalized not in names, "Duplicate signer ZIP path")
                names.add(normalized)
                raw_mode = entry.external_attr >> 16
                kind = stat.S_IFMT(raw_mode)
                mode = stat.S_IMODE(raw_mode)
                require(kind in {0, stat.S_IFREG, stat.S_IFDIR} and mode <= 0o777,
                        "Signer ZIP contains a symlink, special file or permission bits")
                require(not entry.flag_bits & 1, "Encrypted signer ZIP entry")
                require(kind != stat.S_IFDIR or directory, "Signer ZIP directory type mismatch")
                require(kind != stat.S_IFREG or not directory, "Signer ZIP file type mismatch")
                parts = normalized.split("/")
                if parts[0] == "__MACOSX":
                    # ditto may preserve resource metadata in AppleDouble records.
                    # The outer ZIP signature authenticates these bytes;
                    # they are not ordinary files in the app's logical inventory.
                    if directory:
                        require(entry.file_size == 0 and (len(parts) == 1 or parts[1] == SIGNER_APP_NAME),
                                "Unexpected signer ZIP metadata directory")
                        if len(parts) > 1:
                            metadata_targets.add("/".join(parts[1:]))
                    else:
                        require(len(parts) >= 2 and parts[-1].startswith("._"), "Unexpected signer AppleDouble file")
                        if len(parts) == 2:
                            target = parts[1][2:]
                            require(target in {SIGNER_APP_NAME, SIGNER_INVENTORY}, "Unexpected signer ZIP metadata target")
                        else:
                            require(parts[1] == SIGNER_APP_NAME, "Signer ZIP metadata escapes app root")
                            target = "/".join(parts[1:-1] + [parts[-1][2:]])
                        metadata_targets.add(safe_path(target))
                        require(0 <= entry.file_size <= MAX_BUNDLE_BYTES, "Signer metadata exceeds size bound")
                        total += entry.file_size
                        require(total <= MAX_BUNDLE_BYTES, "Signer ZIP exceeds decompressed size bound")
                        require(len(archive.read(entry)) == entry.file_size, "Signer metadata ZIP length mismatch")
                    continue
                require(parts[0] in {SIGNER_APP_NAME, SIGNER_INVENTORY}, "Unexpected signer ZIP root")
                if normalized == SIGNER_INVENTORY:
                    require(not directory and entry.file_size <= MAX_INVENTORY, "Invalid signer inventory entry")
                    inventory_data = archive.read(entry)
                    require(len(inventory_data) == entry.file_size, "Signer inventory ZIP length mismatch")
                    continue
                require(parts[0] == SIGNER_APP_NAME, "Inventory cannot contain descendants")
                if len(parts) == 1:
                    require(directory, "Signer ZIP app root must be a directory")
                    continue
                relative = "/".join(parts[1:])
                if directory:
                    require(entry.file_size == 0, "Nonempty signer ZIP directory")
                    directories.append(relative)
                    continue
                require(0 <= entry.file_size <= MAX_BUNDLE_BYTES, "Signer ZIP file exceeds size bound")
                total += entry.file_size
                require(total <= MAX_BUNDLE_BYTES, "Signer ZIP exceeds decompressed size bound")
                content = archive.read(entry)
                require(len(content) == entry.file_size, "Signer ZIP length mismatch")
                contents[relative] = (content, mode)
                files.append(file_record(relative, content, mode))
                if relative == "Contents/Info.plist":
                    metadata = content
    except (OSError, ValueError, zipfile.BadZipFile, RuntimeError, NotImplementedError) as error:
        raise VerificationError("Invalid signer ZIP") from error
    require(inventory_data is not None, "Signer canonical inventory is missing")
    require(metadata_targets <= names, "Signer AppleDouble metadata target is missing")
    inventory = parse_signer_inventory(inventory_data)
    require(sorted(files, key=lambda item: item["path"]) == inventory["files"], "Signer file set, bytes or modes differ from signed inventory")
    require(sorted(directories) == inventory["directories"], "Signer directories differ from signed inventory")
    require(metadata is not None, "Signer app metadata is missing")
    check_metadata(metadata, SIGNER_PRODUCT_ID, SIGNER_HOST)
    root = "Contents/MacOS/"
    required = {root + SIGNER_HOST, root + "libmldsa87_ref.dylib"}
    require({path for path in contents if path.startswith(root)} == required and
            not any(path.startswith(root) for path in directories),
            "Offline signer code directory must contain only the single-file host and own reference")
    for name in (SIGNER_HOST, "libmldsa87_ref.dylib"):
        content, mode = contents[root + name]
        require(len(content) >= 32 and content[:8] == bytes.fromhex("cffaedfe0c000001"),
                "Offline signer must contain native arm64 Mach-O files")
        if name == SIGNER_HOST:
            require(mode & 0o111, "Offline signer apphost lacks executable permissions")
    verify_single_file_bundle(contents[root + SIGNER_HOST][0])
    require({"Contents/Resources/README.md", "Contents/Resources/SOURCE_PROVENANCE.json"} <= contents.keys() and
            any(path.startswith("Contents/Resources/Licenses/") for path in contents),
            "Offline signer documentation, source provenance or licenses are missing")
    return inventory


def verify_manifest(data, hashes, signer_hashes):
    try:
        lines = data.decode("utf-8").splitlines()
    except UnicodeError as error:
        raise VerificationError("Invalid integrity manifest encoding") from error
    require(lines and lines[0] == "Passphrase Memorizer Release Integrity Manifest v1", "Invalid integrity manifest")
    fields = {}
    for line in lines[1:]:
        key, separator, value = line.partition("=")
        require(separator and key and key not in fields, "Duplicate or invalid integrity field")
        fields[key] = value
    expected = {"artifact": TARGETS[0], "coverage": "complete-signed-app-archive", "bundle-identifier": PRODUCT_ID,
                "bundle-version": VERSION, "bundle-build": BUILD, "architecture": "arm64", "minimum-macos": "14.0",
                "signature-mode": "developer-id-notarized"}
    require(all(fields.get(key) == value for key, value in expected.items()), "Wrong integrity manifest identity or release policy")
    require(all(fields.get(key) == value for key, value in hashes.items()), "Integrity manifest ZIP hashes mismatch")
    require(fields.get("signer-artifact") == SIGNER_TARGET, "Integrity manifest signer artifact mismatch")
    require(all(fields.get("signer-" + key) == value for key, value in signer_hashes.items()),
            "Integrity manifest signer ZIP hashes mismatch")


def verify_release(release_dir, trust_path, checksum_tool, openssl, app=None):
    release_dir, trust_path = pathlib.Path(release_dir), pathlib.Path(trust_path)
    trust = parse_trust(read_regular(trust_path, MAX_TRUST)[0])
    rsa = read_regular(trust_path.parent / trust["rsa_spki"], MAX_CERTIFICATE)[0]
    ml = read_regular(trust_path.parent / trust["mldsa_public_key"], 2592)[0]
    validate_der_sequence(rsa)
    ml_der = mldsa_spki(ml)
    with tempfile.TemporaryDirectory(prefix="passphrase-memorizer-public-verification-") as temporary:
        verifier = PublicVerifier(openssl, checksum_tool, temporary)
        require(verifier.hashes(rsa) == trust["rsa_spki_hashes"], "RSA public key fingerprint mismatch")
        require(verifier.hashes(ml) == trust["mldsa_public_key_hashes"], "ML-DSA public key fingerprint mismatch")
        ml_path = verifier.snapshot(ml_der, ".ml-spki.der")
        artifacts, hashes = {}, {}
        for name, limit in zip(TARGETS, TARGET_LIMITS):
            data = read_regular(release_dir / name, limit)[0]
            artifacts[name] = data
            hashes[name] = verifier.hashes(data)
            verifier.verify(read_regular(release_dir / (name + ".khsig"), MAX_SIDECAR)[0], data, rsa, ml_path)
            for suffix, algorithm in ((".sha3", "sha3-512"), (".skein", "skein-1024-1024")):
                digest_name = name + suffix
                digest_data = read_regular(release_dir / digest_name, 1024)[0]
                expected = (hashes[name][algorithm].upper() + "\n").encode("ascii")
                require(hmac.compare_digest(digest_data, expected), "Digest sidecar contents mismatch: " + digest_name)
                verifier.verify(read_regular(release_dir / (digest_name + ".khsig"), MAX_SIDECAR)[0], digest_data, rsa, ml_path)
        inventory = parse_inventory(artifacts[TARGETS[2]])
        verify_manifest(artifacts[TARGETS[1]], hashes[TARGETS[0]], hashes[SIGNER_TARGET])
        verify_zip(artifacts[TARGETS[0]], inventory)
        verify_signer_zip(artifacts[SIGNER_TARGET])
        for name in (TARGETS[0], SIGNER_TARGET):
            expected = (hashes[name]["sha256"] + "  " + name + "\n").encode("ascii")
            require(hmac.compare_digest(read_regular(release_dir / (name + ".sha256"), 1024)[0], expected),
                    "SHA-256 archive sidecar mismatch: " + name)
        if app is not None:
            verify_app(app, inventory)
    return {"signatures": len(TARGETS) * 3, "files": len(inventory["files"]), "directories": len(inventory["directories"])}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--release-dir", required=True, type=pathlib.Path)
    parser.add_argument("--trust", required=True, type=pathlib.Path,
                        help="Public trust JSON obtained through an independently trusted channel")
    parser.add_argument("--checksum-tool", required=True, type=pathlib.Path,
                        help="Trusted Skein C-reference wrapper: Scripts/skein-reference-checksum.sh")
    parser.add_argument("--openssl", default=shutil.which("openssl"))
    parser.add_argument("--app", type=pathlib.Path, help="Also verify the installed app's exact files, modes and directories")
    args = parser.parse_args()
    try:
        require(args.openssl is not None, "OpenSSL 3.5 or newer is required")
        result = verify_release(args.release_dir, args.trust, args.checksum_tool, args.openssl, args.app)
    except (VerificationError, UnicodeError, OSError) as error:
        print("Hybrid release verification FAILED: " + str(error), file=sys.stderr)
        return 1
    print("Hybrid release verification passed: RSA-4096-PSS/SHA-512 AND ML-DSA-87 for all 12 signed artifacts.")
    print("Signed bundle inventory: %d files, %d directories; product %s, version %s, build %s." %
          (result["files"], result["directories"], PRODUCT_ID, VERSION, BUILD))
    if args.app:
        print("Installed app matches the signed inventory, including file permissions and empty directories.")
    print("Detached release signatures are external; the app's runtime integrity checks continue to use Apple code signing.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
