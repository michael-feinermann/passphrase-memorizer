# Windows release signing

The Windows signer keeps private keys exclusively in `A:\Passphrase Memorizer Keys`. Its `generate` command requires that exact existing directory, rejects links/junctions, and refuses a nonempty directory. RSA-4096 PKCS#8 and the expanded ML-DSA-87 private key are persisted as independent Windows DPAPI **CurrentUser** envelopes with distinct role entropy. The wrapping secret is protected by Windows in the current user's profile; no plaintext wrapping key is saved on the key drive. The FAT32 key volume has no NTFS access control, so copying its encrypted blobs must still be treated as sensitive. Recovery requires the original Windows user profile and machine; these envelopes are not portable signing-key backups.

Public SPKI, ML-DSA-87 key, self-signed public certificate and all three public fingerprints live under `Windows/Crypto/Trust`. They are compiled into the application. No certificate is installed in Windows' trust stores, and the self-signed certificate does not provide publicly trusted Authenticode.

The public `GeneratedTrust.cs` SHA-256 pins must match the embedded public keys. Runtime verification never obtains its authority from an adjacent policy file. All releases require **both** RSA-4096/PSS/SHA-512 and ML-DSA-87 signatures. The inherited, reviewed ML-DSA context is `KalynaZpaqVault/HybridArtifactSignature/v1`; Windows file and inventory messages additionally have distinct `PassphraseMemorizer/WindowsRelease*/v1` domains. The inventory includes the Windows product, schema and version `1.0.1`; file messages bind their complete relative-path and fingerprint record. Bouncy Castle 2.6.2 is exactly version-pinned and dependency hashes are recorded in package lock files.

```powershell
dotnet restore Windows/Signing/PassphraseMemorizer.WindowsSigner.csproj --locked-mode
dotnet build Windows/Signing/PassphraseMemorizer.WindowsSigner.csproj -c Release --no-restore
$signer = 'Windows/Signing/bin/Release/net10.0/PassphraseMemorizer.WindowsSigner.dll'
dotnet $signer generate --key-directory 'A:\Passphrase Memorizer Keys' --public-directory 'Windows/Crypto/Trust'
# Rebuild application and signer after one-time generation; do not generate again.
dotnet $signer sign --bundle '<fresh staging bundle>' --key-directory 'A:\Passphrase Memorizer Keys'
dotnet $signer verify --bundle '<staging or installed bundle>'
dotnet $signer sign-file --target '<release.zip>' --key-directory 'A:\Passphrase Memorizer Keys'
dotnet $signer verify-file --target '<release.zip>'
Windows/Signing/build-reference.ps1
dotnet $signer selftest --reference-library 'Windows/Signing/build-reference/mldsa87-reference.dll' --skein-vectors 'Tests/Reference/Skein/vectors.json'
Windows/Signing/test-reference-hashes.ps1
Windows/Signing/test-reference-hashes.ps1 -Target '<release.zip>'
```

`Windows/Signing/test-signing.ps1` runs the complete locked restore, Release build, pinned reference builds, 72 signer self-tests and independent public-key/hash-vector checks. The Windows GitHub Actions job invokes this wrapper so CI also runs the independent implementations.

`sign` creates an ordered, complete `windows-release.json` inventory, detached signatures for that inventory, and per-file detached signatures under `.signatures`. Every record binds the relative path, byte length, SHA-256, SHA3-512 and Skein-1024-1024. `sign-file` writes the three fingerprints and both detached signatures beside an archive. Existing metadata is never overwritten; use a fresh staging bundle for a new build. Public archive names are part of the signed message and must remain unchanged.

`BundleVerifier.OpenAndVerifyRunningExecutable(AppContext.BaseDirectory)` returns a lease retaining read handles without write/delete sharing for the actual application executable, every inventoried payload, and signing metadata. The runtime checks `lease.RequireVerifiedPayload(nativePath)` before launching the helper and transmitting secrets. Unexpected payload files, path traversal, reserved devices, reparse points, wrong final opened paths, signature tampering and payload changes fail closed. Managed private arrays owned by the signer and native DPAPI plaintext output are wiped on every completion path. RSA providers and Bouncy Castle manage internal key copies; their complete memory erasure cannot be guaranteed by the caller.

The standalone pinned pq-crystals reference DLL and SHA3/Skein C checksum executables are independent **test tools**, excluded from the installed app and Git. The build wrapper verifies vendored source hashes before compilation. Self-tests exercise bidirectional ML-DSA interoperability, malformed signatures, DPAPI role isolation, ciphertext tampering, published SHA256/SHA3/Skein known-answer vectors, payload inventory tampering and lease replacement/deletion rejection. The C reference checksum tools can independently check public keys and final archive fingerprints. Runtime integrity checks protect trusted releases against changes to payload files; a malicious administrator or an attacker replacing the application and its embedded trust remains outside that trust boundary.

## Source provenance and verified checks

The Windows library links the existing reviewed `Signing/HybridSigner/Mldsa87.cs` without modifying the macOS signer. Its key and signature lengths are 2592, 4896 and 4627 bytes, and key generation/signatures use the operating-system CSPRNG through Bouncy Castle. `Mldsa87Reference.cs` likewise links the existing independent adapter and checks the loaded DLL's declared revision. The vendored reference is `pq-crystals/dilithium@d35ba3fe5449bee3e6d43e1f296c3ca818bd36be` with `DILITHIUM_MODE=5`, and `Signing/HybridSigner/Reference/SOURCE_SHA256SUMS` pins every byte of the reference inputs. The Windows adapter uses `BCryptGenRandom`.

`Tests/Reference/Skein/NOTICE.md` records the upstream Skein authors, public-domain/permissive notices, source hashes and 49 published Skein-1024-1024 vectors. The Windows reference build checks all five crypto source hashes, and verifies the JSON vector fixture's upstream hash after checkout line-ending normalization. The new SHA3 file adapter calls `sha3_512` in the independent, pinned pq-crystals FIPS202 implementation; it hashes public files only, with a 2 GiB adapter limit. SHA-256 cross-checks use PowerShell's independent platform hashing provider.

On 12 October 2026, the Release signer and Crypto library built with zero C# warnings and errors. The complete explicit selftest command above passed **72 checks**: the hybrid/interoperability/DPAPI/inventory/lease checks, three empty-message known answers, and 49 Skein vectors. `test-reference-hashes.ps1` independently matched all three exported fingerprints for each of the two generated public keys and passed all 49 vectors with the original Skein C implementation. The macOS upstream C reference emits narrowing-conversion compiler warnings under MSVC; those source bytes are preserved and the independently checked outputs agree. Repeating the `generate` command against the populated key directory failed before overwriting any file, as required.
