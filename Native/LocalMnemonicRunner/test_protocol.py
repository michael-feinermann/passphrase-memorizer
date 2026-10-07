#!/usr/bin/env python3
"""No real credentials or downloaded model needed. Uses a public fake GGUF."""
import json
from pathlib import Path
import signal
import shutil
import struct
import subprocess
import tempfile
import time

PROJECT = Path(__file__).resolve().parents[2]
RUNNER = PROJECT / ".build/local-ai/LocalMnemonicRunner"
PROBE = PROJECT / ".build/local-ai/LocalMnemonicSandboxProbe"


def frame(data):
    return struct.pack("<I", len(data)) + data


def result(data):
    assert data[:4] == b"MSAI", "READY handshake absent"
    length = struct.unpack("<I", data[4:8])[0]
    assert 0 < length <= 65536 and len(data) == length + 8
    return json.loads(data[8:])


def invoke(binary, argument, payload=b""):
    arguments = argument if isinstance(argument, list) else [argument]
    return subprocess.run([str(binary)] + [str(item) for item in arguments], input=payload,
                          capture_output=True, env={}, timeout=90)


with tempfile.TemporaryDirectory(prefix="mnemonic-public-probe-") as directory:
    model = Path(directory) / "public-fixture.gguf"
    model.write_bytes(b"GGUF" + bytes(28))
    for binary, argument in ((RUNNER, "--self-test"), (RUNNER, "--self-test-metal"),
                             (PROBE, model), (PROBE, ["--metal", model])):
        completed = invoke(binary, argument)
        assert completed.returncode == 0 and completed.stderr == b""
        report = result(completed.stdout)
        assert report["sandbox"] == "passed"
        for field in ("ipv4", "ipv6", "writes", "unrelated_reads"):
            assert report[field] == "denied"
        if binary == PROBE:
            assert report["model_read"] == "allowed"
            assert report["child_exec"] == report["fork"] == "denied"
        if argument == "--self-test-metal":
            assert report["backend"] == "Metal" and report["gpu_computation"] == "passed"
            assert isinstance(report["metal_tensor_kernels"], bool)
        print(f"PASS {binary.name}: {json.dumps(report, sort_keys=True)}")

    malformed = {
        "zero frame": struct.pack("<I", 0),
        "oversized frame": struct.pack("<I", 65537),
        "incomplete header": b"\x10\x00",
        "incomplete body": struct.pack("<I", 12) + b"public",
        "invalid UTF-8": frame(b"\xc0\xaf"),
        "NUL input": frame(b"a\x00b"),
        "surplus data": frame(b"public fixture") + b"extra",
    }
    for label, payload in malformed.items():
        completed = invoke(RUNNER, model, payload)
        assert completed.returncode == 65, (label, completed.returncode)
        assert completed.stdout == b"MSAI" and completed.stderr == b"", label
        print(f"PASS malformed protocol: {label}")

    completed = invoke(RUNNER, model, frame(b"public test prompt"))
    assert completed.returncode == 66 and completed.stdout == b"MSAI" and completed.stderr == b""
    print("PASS fake model fails with a constant exit code and no logs")
    completed = invoke(RUNNER, ["--cpu", model], frame(b"public test prompt"))
    assert completed.returncode == 66 and completed.stdout == b"MSAI" and completed.stderr == b""
    print("PASS forced CPU reaches READY without GPU rights and rejects fake model silently")

    alias = Path(directory) / "alias.gguf"
    alias.symlink_to(model)
    completed = invoke(RUNNER, alias, frame(b"public"))
    assert completed.returncode == 66 and completed.stdout == completed.stderr == b""
    print("PASS symlink model rejected before READY")

    process = subprocess.Popen([str(RUNNER), str(model)], stdin=subprocess.PIPE,
                               stdout=subprocess.PIPE, stderr=subprocess.PIPE, env={})
    assert process.stdout.read(4) == b"MSAI"
    started = time.monotonic()
    process.send_signal(signal.SIGTERM)
    output, errors = process.communicate(timeout=10)
    assert process.returncode == 75 and output == errors == b""
    assert time.monotonic() - started < 10
    print("PASS cancellation while waiting for secret input")

    # Retain TMPDIR's original /var spelling: this is the same layout used by
    # archive verification, while realpath resolves it to /private/var.
    packaged = Path(directory) / "Public.app/Contents/Helpers/LocalMnemonicRunner"
    packaged.parent.mkdir(parents=True)
    shutil.copy2(RUNNER, packaged)
    completed = invoke(packaged, "--self-test-metal")
    assert completed.returncode == 0 and completed.stderr == b""
    report = result(completed.stdout)
    assert report["backend"] == "Metal" and report["gpu_computation"] == "passed"
    assert all(report[key] == "denied" for key in ("ipv4", "ipv6", "writes", "unrelated_reads"))
    print("PASS packaged helper via TMPDIR alias: GPU computation and all denials, including both directory listings")
    completed = subprocess.run(["/private/etc/forged-name", "--self-test-metal"],
        executable=str(RUNNER), input=b"", capture_output=True, env={}, timeout=90)
    assert completed.returncode == 0 and completed.stderr == b""
    assert result(completed.stdout)["gpu_computation"] == "passed"
    print("PASS forged argv[0] does not select sandbox filesystem permissions")

print("All local runner protocol and isolation checks passed.")
