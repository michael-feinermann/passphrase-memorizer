# Release hashes and detached hybrid signatures

English | [Deutsch](HYBRID_SIGNING.de.md)

Passphrase Memorizer supplements Apple's Developer ID signature, notarization and Gatekeeper checks with detached RSA-4096-PSS/SHA-512 and ML-DSA-87 signatures. Both algorithms must verify. The separate Hybrid Signer is a release tool and has no connection to the mnemonic app, its model, its AI worker or your passphrase.

The [public v1.0.0 release](https://github.com/michael-feinermann/passphrase-memorizer/releases/tag/v1.0.0) contains all 28 assets, including the notarized Signer and twelve hybrid envelopes. Every asset was downloaded without authentication, compared with the verified originals and checked again after installation. The [internal audit](SECURITY_AUDIT.md) records the source, signature, tamper, notarization and public-download evidence separately. Signing sources are tagged [v1.0.0-hybrid.1](https://github.com/michael-feinermann/passphrase-memorizer/tree/v1.0.0-hybrid.1); the original app tag and app ZIP remain unchanged.

## Required release files

The complete verifier requires these four targets. Omitting the Signer is an error.

| Target | What it authenticates |
| --- | --- |
| `Passphrase-Memorizer-1.0.0.zip` | The complete final notarized app archive |
| `Passphrase-Memorizer-1.0.0.integrity.txt` | Product/version and all three hashes of the app and Signer archives |
| `Passphrase-Memorizer-1.0.0.bundle-inventory.json` | Every app file, length, mode and directory |
| `Passphrase-Memorizer-HybridSigner-1.0.0.zip` | The separate Signer.app, runtime, reference library, licenses and canonical `signer-inventory.json` |

Each target has an adjacent `.khsig`, `.sha3`, `.sha3.khsig`, `.skein` and `.skein.khsig`. Thus twelve hybrid envelopes are mandatory, each containing both signatures. The two ZIPs also have conventional `.sha256` files. SHA-256, SHA3-512 and Skein-1024-1024 are public integrity hashes; a checksum alone does not authenticate its publisher. The integrity manifest and signed archive bind the expected hashes, and the verifier recomputes all three.

The Signer inventory remains beside its sealed `.app` inside the signed ZIP. Detached signatures remain beside the ZIP rather than inside it. This avoids changing an already signed artifact or requiring a signature to contain itself.

`Passphrase-Memorizer-1.0.0.hybrid-signatures.zip` conveniently bundles the signature files, public trust material, checker scripts and C-Skein references. It has its own SHA-256 sidecar. It is not a fifth hybrid-signature target, and that checksum does not authenticate its included checker or keys. Establish the independent trust described below before running downloaded verification code.

## Trust the public keys first

The public [Signing directory](../Signing/) contains `trust.json`, `rsa4096-spki.der`, `rsa4096-certificate.cer`, `mldsa87-public.bin` and `PassphraseMemorizerPolicy.props`. The new product uses independent RSA and ML-DSA keys. The Signer has no default public key or fallback pins from Keep Vault or Password Generator. `sign` and `verify` require an explicit policy containing all three fingerprints for each public key.

Obtain or confirm `trust.json` and the six public-key fingerprints through an independently trusted channel before accepting a release. Downloading an archive, replacement keys and matching pins from the same compromised account cannot establish the publisher's identity. A self-issued signing certificate is accepted because its exact public key is pinned; it is not a certificate-authority trust chain. These pins are separate from Apple's Developer ID identity.

The independent Python checker requires Python 3, OpenSSL 3.5 or newer and the trusted C-Skein reference wrapper. The wrapper compiles the checked-in reference with Xcode's C compiler. Trust the checker and its dependency sources before running them. It verifies the Signer ZIP without extracting or executing the Signer.

From an independently trusted copy of this repository, with every release asset downloaded into one local directory:

```sh
python3 -I Scripts/verify-hybrid-signatures.py \
  --release-dir '/path/to/downloaded-release' \
  --trust '/path/to/trusted/Signing/trust.json' \
  --checksum-tool '/path/to/trusted/repository/Scripts/skein-reference-checksum.sh' \
  --openssl '/path/to/trusted/openssl' \
  --app '/Applications/Passphrase Memorizer.app'
```

`--app` additionally compares the installed app with the signed inventory. Leave it out to verify the downloaded release alone. Successful complete verification reports twelve hybrid envelopes and checks both algorithms, public-key fingerprints, certificate policy, all hashes, product/version, ZIP safety and complete inventories. Apple signature, notarization and Gatekeeper checks remain separate; reproduce them with the [release verifier](../Scripts/verify-release.sh).

On the verified installation, public assets and tools reside in `/Applications/Passphrase Memorizer 1.0.0.signatures`, and the separate Signer is `/Applications/Passphrase Memorizer Hybrid Signer.app`. The mnemonic app remains `/Applications/Passphrase Memorizer.app`. None of these public verification tools needs the private key volume or the AI model.

## Compute hashes or use the Signer

After independently verifying its archive, extract the complete Signer.app with a macOS archive tool that preserves Apple's bundle metadata. Keep the complete `.app` together. The production wrapper refuses a loose executable, a broken resource seal, an unexpected team or version, additional entitlements and an external reference library.

Example for a public file, from this repository:

```sh
memorizer_signer_dir='/path/to/Passphrase Memorizer Hybrid Signer.app/Contents/MacOS'
Scripts/run-hybrid-signer.sh --signer-dir "$memorizer_signer_dir" \
  hash --target '/path/to/public-file'
```

The `hash` command prints SHA-256, SHA3-512 and Skein-1024-1024 as JSON and does not access private keys. The same hashes are printed during signing. A SHA-256 ZIP sidecar can also be checked with `shasum -a 256 -c FILE.sha256` in its containing directory; this is an integrity check, not the complete signature verification above.

For a public Signer-side verification of an ordinary file and its adjacent sidecars:

```sh
Scripts/run-hybrid-signer.sh --signer-dir "$memorizer_signer_dir" \
  verify --target '/path/to/public-file' \
  --mldsa-public-key '/path/to/trusted/Signing/mldsa87-public.bin' \
  --policy '/path/to/trusted/Signing/PassphraseMemorizerPolicy.props'
```

This command checks that file and its SHA3/Skein signatures. The Python release checker additionally enforces all four mandatory release targets and the complete app/Signer inventories.

## Protected local release keys

The requested key container is `/Volumes/NO NAME/Passphrase Memorizer Keys/ReleaseKeys.sparsebundle`. The outer volume is a VeraCrypt-backed FAT volume with disabled ownership. FAT permissions alone cannot provide the required macOS private-file semantics. An attached, unencrypted 128 MB APFS image supplies enforced ownership; it does not add another encryption layer.

The release key directory inside that image is `/Volumes/Passphrase Memorizer Release Keys/MemorizerRelease-v1`. It is mode 0700 and owned by the release user. Private files are mode 0600, single-link, without extended ACLs. The bound-descriptor implementation refuses symbolic links, switched paths/objects, disabled ownership and existing key sets. Private material remains outside Git and GitHub.

RSA is stored in an encrypted PKCS#12 file. Its password and the ML-DSA private key use separate AES-256-GCM envelopes and independent RSA/ML-DSA wrapping keys. The wrapping keys reside on the same protected volume; confidentiality at rest therefore depends on the outer VeraCrypt protection. Neither a password nor a private key is passed as an argument, environment value, log or release asset.

Key generation is an explicit `release-keygen` operation and requires a new empty mode-0700 directory outside a repository. It never overwrites an existing key set. Production private operations must use `Scripts/run-hybrid-signer.sh`. It validates the complete Apple-sealed Signer.app before any key access, accepts only its own exact `Contents/MacOS/libmldsa87_ref.dylib`, uses a clean environment, disables diagnostics and core dumps, and supplies an empty private TMPDIR. Temporary macOS PFX keychain objects and the user's keychain inventory are checked afterwards. Unexpected residue is retained for review and causes failure.

## Build and publish a release

`Scripts/build-hybrid-signer.sh` obtains the official Microsoft SDK 10.0.400 archive, verifies its pinned SHA-512 and Microsoft Developer ID, and uses a fresh private SDK/build tree. NuGet uses only the configured source and locked package contents. The Signer includes the .NET 10.0.11 runtime; running the published tool needs no .NET SDK or restore. Building and dependency acquisition are setup operations separate from offline AI inference.

```sh
Scripts/build-hybrid-signer.sh --output '/absolute/path/to/new-signer-output'
```

The release layout carries managed code and the complete CLR runtime within its native apphost without runtime self-extraction. The own native ML-DSA reference remains a separate signed Mach-O library. Readme and license files belong in Resources. This avoids depending on generic extended-attribute signatures of managed DLLs in a code directory. The separate Signer.app receives only `com.apple.security.cs.allow-jit`; the mnemonic app and AI helper retain their existing stricter rights.

Package a fresh candidate with the explicit Developer ID certificate SHA-1. Replace the example paths and certificate placeholder:

```sh
python3 -I Scripts/package-hybrid-signer.py \
  --publish-dir '/absolute/path/to/new-signer-output' \
  --output-app '/absolute/path/to/new-staging/Passphrase Memorizer Hybrid Signer.app' \
  --identity 'YOUR_40_HEX_CERTIFICATE_SHA1' \
  --archive '/absolute/path/to/new-archive/Passphrase-Memorizer-HybridSigner-1.0.0.xcarchive'
```

The output app and optional `.xcarchive` must be fresh. This creates a signed candidate, not a notarization result. After Apple's final notarized export and stapling, check the actual exported app with the default distribution policy:

```sh
python3 -I Scripts/verify-hybrid-signer-app.py \
  --app '/absolute/path/to/final-export/Passphrase Memorizer Hybrid Signer.app'
```

`--signed-candidate` deliberately skips only notarization gates and must not be used as final release proof. The default checks the complete code seal, exact two-code-file layout, arm64, expected team/identity, Hardened Runtime, minimal entitlements, system-only dependencies, absence of runtime search paths, staple and Gatekeeper.

Place the unchanged final app ZIP, and optionally its existing SHA-256 sidecar, in a new release directory. Create the public Signer archive, inventories, hashes and integrity manifest with:

```sh
python3 -I Scripts/create-hybrid-release.py \
  --release-dir '/absolute/path/to/new-release-directory' \
  --app '/absolute/path/to/final-export/Passphrase Memorizer.app' \
  --signer-app '/absolute/path/to/final-export/Passphrase Memorizer Hybrid Signer.app'
```

This script accesses no private key. It requires a fresh Signer ZIP and integrity manifest, checks the notarized Signer before and after the ZIP roundtrip, and compares the Signer's three hash outputs with Python SHA-256/SHA3 and the independent C-Skein reference. It does not create the twelve hybrid envelopes.

The publisher first Developer-ID signs, notarizes and staples both final app bundles. After final export, create canonical inventories outside the sealed bundles and form their ZIPs. The Signer ZIP contains its separate inventory. Write the integrity manifest using the final app and Signer hashes. Then sign the four immutable targets with the explicit product policy, never rebuild or resign a bundle afterwards, and run the independent checker and public-only negative suite. Finally verify the exact assets downloaded without authentication from GitHub, including an extracted Signer bundle's Apple seal.

For ordinary signing, use the wrapper's `sign` command with one `--target FILE` per target and paths for `--pfx`, `--pfx-password-encrypted`, `--pfx-wrapping-key-file`, `--mldsa-private-key-encrypted`, `--mldsa-wrapping-key-file`, `--mldsa-public-key`, `--reference-library`, `--policy` and `--launcher-pins`. The final option writes an external public pin file. Secret file contents are loaded only inside the Signer. Target bytes remain unchanged; detached sidecars are written beside them.

## Retained Keep Vault format

The source provenance is recorded in [SOURCE_PROVENANCE.json](../Signing/HybridSigner/SOURCE_PROVENANCE.json). The adapted Signer retains the `KZVHSIG1` envelope, version 1, its little-endian length/SHA-512 binding, the payload domain `KalynaZpaqVault/HybridArtifactSignature/SHA-512/v1\0` and pure ML-DSA context `KalynaZpaqVault/HybridArtifactSignature/v1`. The legacy namespace is a deliberate format choice, not reuse of another product's keys. Product identity and complete release membership are additionally enforced by the signed manifest/inventories and explicit new trust policy.

RSA-PSS uses SHA-512, MGF1/SHA-512 and a 64-byte salt. The second signature is pure ML-DSA-87, with the explicit context and message encoding, rather than HashML-DSA. Signing/key generation cross-check Bouncy Castle and the native reference pinned to `pq-crystals/dilithium@d35ba3fe5449bee3e6d43e1f296c3ca818bd36be` in both directions. The public checker uses OpenSSL independently. Algorithm references: [NIST FIPS 204](https://csrc.nist.gov/pubs/fips/204/final) and [OpenSSL pkeyutl](https://docs.openssl.org/3.5/man1/openssl-pkeyutl/).
