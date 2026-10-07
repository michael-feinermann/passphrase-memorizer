# Skein 1.3 reference implementation for release verification

This directory vendors the unmodified, byte-for-byte C reference implementation from the Skein authors. It is used only by `Scripts/skein-reference-checksum.sh` to independently verify release files. It is not linked into, copied into, or executed by the macOS application, and it does not depend on the signing tool's C# implementation.

## Provenance

- Project and original download link: https://www.schneier.com/academic/skein/
- Specification, version 1.3 (1 October 2010): https://www.schneier.com/wp-content/uploads/2015/01/skein.pdf
- Original archive: https://www.schneier.com/wp-content/uploads/2015/01/skein.zip
- Archive SHA-256: `121b73a4d5300b4977d3757064a29ba5e11c0fb01786171b2bc729ef099b89ad`
- Upstream directory: `NIST/CD/Reference_Implementation/`
- Retrieved and checked: 29 September 2026.

The five upstream files retain their original contents and line endings:

| File | SHA-256 |
|---|---|
| `skein.c` | `73cff8b3470ba1bf8fef7adc99fc1b78a58682a39debdbdba337e74e2c0899b9` |
| `skein_block.c` | `8a310dce6a17b98fcf061faa9e75a82f70b535c174a1d2ba8c8b0b27804bbce8` |
| `skein.h` | `2988f8efa71095441b31654a51034df50a74f09912c6f9fcb4bd14dece37d30d` |
| `skein_port.h` | `e99efc1a1e09a2307379576fb2096e5ae06ada34f92a60845917dd99f09ad2b8` |
| `brg_types.h` | `240329b4ca4d829ac4d1490e96e83118e161e719e448c7e8dbf15735ab8a8e87` |

`file_checksum.c` is the local file-I/O adapter. It calls the original `Skein1024_Init` with a 1024-bit output length, streams the input through `Skein1024_Update`, and finishes with `Skein1024_Final`. This is the fixed Skein-1024-1024 digest, not a variable-length Skein XOF. No upstream cryptographic source was changed.

The shell script compiles with the original error checks enabled (`SKEIN_ERR_CHECK=1`) into a private temporary directory and removes that directory on exit. The verifier works offline after checkout, requires Apple's C compiler through `xcrun`, and writes exactly 256 lowercase hexadecimal characters to standard output, without a trailing newline. Errors and compiler diagnostics go to standard error and return a nonzero exit status. It does not install or cache a binary.

## Licenses and attribution

The headers of `skein.c`, `skein_block.c`, `skein.h` and `skein_port.h` credit Doug Whiting (2008) and release the algorithm and source code to the public domain. `skein_port.h` additionally thanks Brian Gladman for his portable headers.

`brg_types.h` is distributed here under its permissive license option, reproduced below. This is separate from the Skein public-domain dedication; the optional GPL alternative is not selected. The full original notice also remains in the unmodified header.

```text
Copyright (c) 1998-2006, Brian Gladman, Worcester, UK. All rights reserved.

 LICENSE TERMS

 The free distribution and use of this software in both source and binary
 form is allowed (with or without changes) provided that:

   1. distributions of this source code include the above copyright
      notice, this list of conditions and the following disclaimer;

   2. distributions in binary form include the above copyright
      notice, this list of conditions and the following disclaimer
      in the documentation and/or other associated materials;

   3. the copyright holder's name is not used to endorse products
      built using this software without specific written permission.

 ALTERNATIVELY, provided that this notice is retained in full, this product
 may be distributed under the terms of the GNU General Public License (GPL),
 in which case the provisions of the GPL apply INSTEAD OF those given above.

 DISCLAIMER

 This software is provided 'as is' with no explicit or implied warranties
 in respect of its properties, including, but not limited to, correctness
 and/or fitness for purpose.
```

The local adapter `file_checksum.c` and wrapper `Scripts/skein-reference-checksum.sh` are original project code; this notice does not grant or change their license.

## Verification scope

The five reference files and the file-I/O adapter were copied without changing their bytes from the locally verified Password Generator source. The local wrapper has only the temporary-directory prefix changed for this app. SHA-256 values of all five reference sources were checked against the table above.

This checksum tool is kept outside the application and verifies only public release files and public-key fingerprints. Its verification tests compare known Skein-1024-1024 outputs and file boundaries, and reject invalid arguments and nonregular inputs. Agreement between implementations does not prove authenticity; the detached RSA-PSS and ML-DSA signatures establish provenance relative to an independently trusted public key.

The public 49-vector fixture is stored as `vectors.json`, copied byte-for-byte from Password Generator's public Skein-1024-1024 tests. Its SHA-256 is `de4eb02118816273d87acd04c42739e55133a8446324e75e21288a39a285d2dd`. `Scripts/test-skein-reference-checksum.py` passed all 49 vectors, four comparisons with an independent 4096-byte file-I/O adapter, and four invalid-invocation cases on 7 October 2026. All wrapper invocations checked the exact output and private temporary-directory cleanup.
