"""Pure validation shared by the release checker and adversarial tests."""
import pathlib
import stat
import unicodedata
import zipfile

MAX_ARCHIVE_ENTRIES = 10_000
MAX_EXPANDED_BYTES = 128 * 1024 * 1024
ALLOWED_ENTITLEMENTS = {"com.apple.application-identifier", "com.apple.developer.team-identifier"}


def validate_entitlements(value, identifier, team="2T6K9PGS55"):
    if not isinstance(value, dict) or not set(value).issubset(ALLOWED_ENTITLEMENTS):
        raise ValueError("Unexpected entitlement, including App Sandbox or a Hardened Runtime exception")
    if "com.apple.developer.team-identifier" in value and value["com.apple.developer.team-identifier"] != team:
        raise ValueError("Entitlement signing team mismatch")
    if "com.apple.application-identifier" in value and value["com.apple.application-identifier"] != team + "." + identifier:
        raise ValueError("Entitlement application identifier mismatch")


def preflight_archive(archive):
    """Reject unsafe ZIP metadata before decompression or filesystem extraction."""
    entries = archive.infolist()
    if not entries or len(entries) > MAX_ARCHIVE_ENTRIES:
        raise ValueError("Archive entry-count limit exceeded or archive is empty")
    expanded = 0
    seen = set()
    for entry in entries:
        raw = entry.filename
        if entry.orig_filename != raw:
            raise ValueError("ZIP filename was truncated or interpreted inconsistently")
        path = pathlib.PurePosixPath(raw)
        canonical = str(path) + ("/" if entry.is_dir() else "")
        if (path.is_absolute() or ".." in path.parts or not path.parts
                or path.parts[0] not in {"Passphrase Memorizer.app", "__MACOSX"}
                or raw != canonical or "\\" in raw
                or any(ord(character) < 32 or ord(character) == 127 for character in raw)):
            raise ValueError("Unexpected or unsafe archive path")
        key = unicodedata.normalize("NFC", str(path)).casefold()
        if key in seen:
            raise ValueError("Duplicate or ambiguous archive path")
        seen.add(key)
        mode = stat.S_IFMT(entry.external_attr >> 16)
        if mode not in {0, stat.S_IFREG, stat.S_IFDIR}:
            raise ValueError("Special file or symlink in release archive")
        if mode == stat.S_IFDIR and not entry.is_dir():
            raise ValueError("Conflicting archive directory metadata")
        if mode == stat.S_IFREG and entry.is_dir():
            raise ValueError("Conflicting archive file metadata")
        if entry.flag_bits & 1 or entry.compress_type not in {zipfile.ZIP_STORED, zipfile.ZIP_DEFLATED}:
            raise ValueError("Encrypted or unsupported archive entry")
        expanded += entry.file_size
        if expanded > MAX_EXPANDED_BYTES:
            raise ValueError("Expanded archive-size limit exceeded")
