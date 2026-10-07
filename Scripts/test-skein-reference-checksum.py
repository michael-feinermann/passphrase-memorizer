#!/usr/bin/env python3
"""Check public Skein golden vectors, file-I/O boundaries and wrapper cleanup."""
import json
import os
import pathlib
import subprocess
import tempfile

ROOT = pathlib.Path(__file__).resolve().parent.parent
TOOL = ROOT / "Scripts/skein-reference-checksum.sh"
REFERENCE = ROOT / "Tests/Reference/Skein"


def main():
    vectors = json.loads((REFERENCE / "vectors.json").read_text())
    assert len(vectors) == 49
    with tempfile.TemporaryDirectory(prefix="passphrase-memorizer-skein-test-") as directory:
        directory = pathlib.Path(directory)
        wrapper_temp = directory / "compiler-temp"
        wrapper_temp.mkdir()
        environment = dict(os.environ, TMPDIR=str(wrapper_temp))
        public_file = directory / "public fixture with spaces"
        def run(arguments):
            value = subprocess.run([str(TOOL), *map(str, arguments)], capture_output=True, env=environment, timeout=30)
            assert list(wrapper_temp.iterdir()) == [], "Wrapper left temporary compiler files behind"
            return value
        for vector in vectors:
            public_file.write_bytes(bytes.fromhex(vector["message"]))
            result = run([public_file])
            assert result.returncode == 0 and result.stdout == vector["output"].encode("ascii"), vector["label"]
            assert len(result.stdout) == 256 and not result.stdout.endswith(b"\n")
        # Different independent file-I/O adapter: 4096-byte reads instead of 65536.
        source = directory / "public-independent-file-adapter.c"
        source.write_text('''#include <stdio.h>
#include "skein.h"
int main(int argc, char **argv) {
  if (argc != 2) return 64;
  FILE *f = fopen(argv[1], "rb"); if (!f) return 66;
  Skein1024_Ctxt_t c; unsigned char buffer[4096], out[128]; size_t n;
  if (Skein1024_Init(&c, 1024) != SKEIN_SUCCESS) return 70;
  while ((n=fread(buffer, 1, sizeof(buffer), f))) {
    if (Skein1024_Update(&c, buffer, n) != SKEIN_SUCCESS) return 70;
  }
  if (ferror(f) || fclose(f)) return 74;
  if (Skein1024_Final(&c, out) != SKEIN_SUCCESS) return 70;
  for (size_t i=0;i<sizeof(out);i++) printf("%02x", out[i]);
  return fflush(stdout) ? 74 : 0;
}''')
        compiler = subprocess.check_output(["/usr/bin/xcrun", "--sdk", "macosx", "--find", "clang"], text=True).strip()
        sdk = subprocess.check_output(["/usr/bin/xcrun", "--sdk", "macosx", "--show-sdk-path"], text=True).strip()
        binary = directory / "public-independent-reference"
        subprocess.run([compiler, "-std=c99", "-O2", "-Wall", "-Wextra", "-Werror", "-DSKEIN_ERR_CHECK=1",
                        "-isysroot", sdk, "-I", str(REFERENCE), str(source), str(REFERENCE / "skein.c"),
                        str(REFERENCE / "skein_block.c"), "-o", str(binary)], check=True, capture_output=True, timeout=30)
        for size in (65535, 65536, 65537, 131073):
            public_file.write_bytes(bytes((index * 37 + 11) & 255 for index in range(size)))
            expected = subprocess.check_output([str(binary), str(public_file)], timeout=30)
            result = run([public_file])
            assert result.returncode == 0 and result.stdout == expected, "Boundary size " + str(size)
        for arguments in ([], [public_file, public_file], [directory / "does-not-exist"], [directory]):
            result = run(arguments)
            assert result.returncode != 0 and result.stdout == b"", "Invalid invocation accepted"
    print("Skein C-reference wrapper passed 49 vectors, 4 independent file-boundary comparisons and 4 rejection cases.")
    print("Exact 256-character output and temporary-directory cleanup checked for every invocation.")


if __name__ == "__main__":
    main()
