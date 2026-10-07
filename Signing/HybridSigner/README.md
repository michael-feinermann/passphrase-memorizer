# Passphrase Memorizer Hybrid Signer

This separate macOS arm64 release tool signs and verifies ordinary files with
RSA-4096-PSS/SHA-512 and pure ML-DSA-87. Both signatures are required. It is not
part of the mnemonic app or its restricted AI process. Its published ZIP is
itself a mandatory detached hybrid signature target.

The source adapts Keep Vault's reviewed signer. `SOURCE_PROVENANCE.json` records
the source revision and file hashes. The signature format, payload domain,
ML-DSA context, protected memory, role-separated AES-256-GCM envelopes and
descriptor-bound filesystem checks are retained. The certificate subject and
standalone project paths/name change, and a public `hash` command is added.
The included native ML-DSA reference
is pinned to `pq-crystals/dilithium@d35ba3fe5449bee3e6d43e1f296c3ca818bd36be`.
Generation and signing cross-check Bouncy Castle and that implementation in
both directions. Reference sources use their offered public-domain/CC0 option.

Build with `Scripts/build-hybrid-signer.sh`. The build obtains the official
Microsoft SDK 10.0.400 archive through `Scripts/provision-verified-dotnet.sh`,
checks the pinned SHA-512 and Microsoft Developer ID, uses a fresh private SDK
tree and locked NuGet restore, and publishes the complete .NET 10.0.11 runtime
to `build/hybrid-signer-singlefile-publish` (or a fresh explicit `--output`).
Managed assemblies and runtime JSON are embedded in the single Mach-O apphost
and loaded from memory. Both `IncludeNativeLibrariesForSelfExtract` and
`IncludeAllContentForSelfExtract` are explicitly false; no runtime files are
extracted to a temporary directory or the user's home directory. In the pinned
macOS arm64 build, the native CLR is part of the Mach-O host; the bundled manifest
contains only managed assemblies and the two runtime JSON files. The separately
built ML-DSA reference remains a physical native library. Trimming and single-file
compression are off.
The lockfile additionally pins Microsoft's ILLink.Tasks 10.0.11 compatibility
build analyzer. Existing Bouncy Castle and cryptographic runtime dependencies
are unchanged; the analyzer does not become a distributed runtime dependency.
No SDK installation is required to execute the published signer. The separate
apphost has the single `allow-jit` entitlement needed by .NET. It receives no
library-validation or unsigned-memory exception. These rights do not apply to
the mnemonic app or AI helper.

The published archive contains `Passphrase Memorizer Hybrid Signer.app` and a
canonical `signer-inventory.json`. The .app is independently Developer-ID signed
and notarized before its inventory and ZIP are created. Its executable and the
separate ML-DSA reference library reside in `Contents/MacOS`. README, provenance and
licenses are regular sealed resources in `Contents/Resources`; the inventory
binds every app file, mode and directory. The outer ZIP and its SHA3/Skein sidecars are hybrid-signed afterwards,
without embedding those detached signatures into the signed ZIP.

Only Mach-O files occupy the executable-code directory. This prevents reliance
on generic code signatures stored in extended attributes for loose managed
DLLs, JSON or notices. Xcode's notarization upload ZIP can discard those attributes
even when the source app passes a local signature check. The published package
must pass a fresh ZIP roundtrip and full strict/deep Apple verification.

Use `Scripts/run-hybrid-signer.sh --signer-dir /absolute/tool/directory COMMAND`
for all operations involving private keys. It supplies a clean environment,
disables diagnostics, confines .NET's temporary PFX keychain to an empty private
TMPDIR, and checks temporary files and the user's keychain inventory afterwards.
The wrapper runs the already-built executable and performs no restore or build.
Before execution, it checks the complete Signer.app's strict/deep Apple seal,
including every embedded managed assembly and separate native library, the expected team, bundle identity and version,
Hardened Runtime, and the single `allow-jit` entitlement. An unbundled executable
is rejected. This check is mandatory before any key access or temporary keychain
creation. The host seals the embedded managed code; the complete bundle check
also authenticates its native dependencies and resource content.
For provisioning or signing, `--reference-library` must name exactly the sealed
`Contents/MacOS/libmldsa87_ref.dylib` in that same app. A self-reported source
revision from an arbitrary external library cannot authenticate its code and is
therefore insufficient. Public `hash` and `verify` need no reference library.

Provisioning is explicit and requires an empty external private directory on a
local filesystem with enforced ownership. A FAT/noowners volume is rejected;
an APFS image within the protected outer volume supplies the required ownership
semantics. It does not add another encryption layer. Example:

```sh
Scripts/run-hybrid-signer.sh --signer-dir /absolute/tool/directory \
  release-keygen --directory '/Volumes/Passphrase Memorizer Signing Keys/PrivateKeys' \
  --reference-library /absolute/tool/directory/libmldsa87_ref.dylib
```

Provisioning never overwrites an existing key set. It writes an encrypted
`hybrid-rsa4096.pfx`, independent `hybrid-rsa4096.pfx.password.v12.enc` and
`mldsa87-private.key.v12.enc` envelopes, separate `pfx-v12-wrapping-key.b64` and
`mldsa-v12-wrapping-key.b64` files, and public `hybrid-rsa4096.cer`,
`mldsa87-public.key` and `Directory.Build.props`. Secret files are single-link,
mode 0600, owned by the calling user, in a mode-0700 directory without extended
ACLs. Symlinks and disabled ownership are rejected through the open descriptors.
The wrapping keys residing on the same volume means confidentiality depends on
that volume's protection. No password or private key is supplied as an argument,
environment value, log, source file or release asset.

For signing, invoke `sign` with `--target FILE` for each unchanged artifact,
`--pfx`, `--pfx-password-encrypted`, `--pfx-wrapping-key-file`,
`--mldsa-private-key-encrypted`, `--mldsa-wrapping-key-file`,
`--mldsa-public-key`, `--reference-library`, `--policy` and `--launcher-pins`.
The last output is an external public pin file, not an app modification. Signing
creates adjacent `.khsig` sidecars for each target and its `.sha3` and `.skein`
hash sidecars. It leaves the target's bytes unchanged.

Public `verify` needs only `--target FILE`, `--mldsa-public-key` and `--policy`.
The policy pins both public keys with SHA-256, SHA3-512 and Skein-1024-1024.
Those pins must come from a separately trusted source. The independent Python
release verifier also checks OpenSSL verification, complete app inventory, ZIP
safety and the signer's ZIP. Hybrid signatures augment Apple's app signing and
notarization, and do not replace them.

Public `hash --target FILE` prints SHA-256, SHA3-512 and Skein-1024-1024 as JSON
without accessing any private key. The same three hashes are printed when
signing. A policy is mandatory for `sign` and `verify`; the executable contains
no default public key or fallback identity from another application.

Deployment references: [Microsoft single-file deployment](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview),
[Apple bundle layout](https://developer.apple.com/documentation/bundleresources/placing-content-in-a-bundle),
and [Apple code-signature storage](https://developer.apple.com/documentation/technotes/tn3126-inside-code-signing-hashes).
