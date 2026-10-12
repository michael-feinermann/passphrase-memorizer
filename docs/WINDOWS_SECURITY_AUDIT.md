# Windows v1.0.1 internal security audit

Audit date: 12 October 2026. Source baseline: upstream `v1.0.1`, with macOS source and release-signing trust preserved. Scope: the WPF Windows x64 port, its native CPU worker, local Gemma 4 31B integration, Windows signing/key provisioning and per-user installer. This is an internal engineering audit, not independent certification.

Live host: Windows 11 Education 26H2, build **26300.9550**, Intel Core i9-13900K and 31.7 GiB reported physical RAM. Isolation results below apply to this observed host. Other Windows builds must pass the same mandatory checks before accepting a prompt.

## Current validation state

The final hybrid-signed Windows package passed local release validation and was installed per-user. No blocking findings remain in the reviewed scope. The source workflow checks core, signing and managed build on GitHub; it does not run the user's external model or production keys.

Verified:

- Core validation, original word-list hashes, count limits, separators, original case/repetitions, exact ordered markers, supplementary Unicode letter parity, output caps, disposal and four-field preference allowlist: **45 checks passed**.
- WPF public fixture: **12 checks passed**, including language, format/styles, literal bold/blue markers, concealed render clearing and session clearing. The full-height image under ignored `.build/windows-ui-public.png` contains only deliberately public fixtures.
- Managed Release application build: **zero warnings and errors**, locked package restore; WPF self-contained single-file distribution uses .NET 10.0.12. Managed verifier, Bouncy Castle and trust are inside the executable; native WPF dependencies are inventoried separately.
- Hybrid signing suite: **72 checks passed**, including bidirectional Bouncy Castle / pinned pq-crystals ML-DSA interoperability, RSA and ML-DSA tamper rejection, DPAPI role/ciphertext isolation, read leases, payload inventory and 49 published Skein vectors.
- Independent original Skein C reference: **49 vectors passed**. SHA-256, independent pq-crystals SHA3-512 and independent Skein-1024-1024 agree on both public keys; real provisioned keys signed and verified a public test archive and rejected one-byte tampering.
- NuGet vulnerability queries for application and signer, including transitive packages, reported no listed vulnerable packages. Bouncy Castle is exactly `[2.6.2]`, with content-hash lock files. This is an advisory database result, not proof that dependencies are defect-free.
- Complete local Gemma GGUF hash matched `0374ce7b0124db9ba96fc649e835c531223ee224a497ce88a374baaea10932ec`; length **17,650,999,456 bytes**. Installed metadata reports architecture `gemma4`, 30.7B parameters, Q4_0.
- An isolated public installer fixture deliberately failed verification after the installation swap: the previous installation was restored and the failed candidate moved aside. Exact archive-entry coverage, including nested `.signatures`, passed separately. These fixtures did not alter the real installation or its registry/shortcuts.
- Final signed package: UI **12 checks passed**; all live sandbox gates passed; cancellation passed in **2.1 seconds**. Actual local Gemma 4 31B inference with the public phrase `abandon ability able` and a 60-word fixture budget passed in **56.7 seconds**, including independent exact-order story validation. No generated story or secret fixture was written to disk. An earlier package without the closed empty thought channel reached the ten-minute fixture deadline; it was superseded and was not installed.
- Actual isolation failures: external/loopback IPv4/IPv6 **WSAEACCES 10013**; unrelated file/model/profile write and private registry read/Storage write **ERROR_ACCESS_DENIED 5**; child creation **ERROR_NOT_ENOUGH_QUOTA 1816** under the one-process job. The actual production LPAC checker accepted LPAC mask 2 and rejected ordinary AppContainer mask 3.
- Final archive: **17 payload files**, dual signatures on all payloads and inventory, independently verified SHA-256/SHA3-512/Skein-1024-1024 archive fingerprints, and successful signature/inventory/hash verification after extraction. All required app, word-list, llama.cpp, Bouncy Castle and Microsoft runtime license/notice files are included.
- Installed extracted archive at `C:\Users\Michael\AppData\Local\Programs\Passphrase Memorizer`; source, staging and installed bundle verified. The installed copy repeated UI, sandbox and cancellation fixtures successfully. Windows uninstall registration is version 1.0.1; Start Menu and Desktop shortcuts target the installed executable.
- After full generation and cancellation: **zero native workers, zero runner profiles, zero private-registry canaries and zero explicit temporary package SID ACEs** on the model and tested native executable.

Final local artifact: `build/Passphrase-Memorizer-1.0.1-Windows-x64.zip`, **66,859,657 bytes**.

```text
SHA-256: 1f951fde6ed05caa1494153ee3cf0e4b768729d2b142d1dd968e60ae08ef0199
```

## Security design

Before accepting normal UI input, the app verifies both signatures on the complete inventory and every payload and retains read handles without write/delete sharing. Before sending any prompt byte, the supervisor repeats verification, requires the inventoried native helper, starts it suspended, checks the actual process-image path and assigns a kill-on-close job. Signature policy uses embedded public-key SHA-256 pins. Archive signatures bind filename, length and SHA-256/SHA3-512/Skein-1024-1024 fingerprints. Modified, missing or additional payloads, malformed inventories, path traversal, device names and reparse paths fail closed.

The inference worker is its own statically linked llama.cpp program pinned to commit `8e1642198dcd4e408f8776222d6ae31b74d01187`. HTTP/server/tools/cURL, dynamic backend loading, OpenMP dependency DLLs and GPU backends are disabled. Gemma weights are read directly, not through the running Ollama service. A bounded binary pipe carries one prompt and one output; arguments contain only public paths/handles, and no prompt is stored in a temporary file. Before readiness, the worker must attest a less-privileged AppContainer (LPAC), an exact one-capability allowlist (`registryRead`) and zero network capabilities. Windows requires a registered profile and public system/profile environment paths to initialize this process. The fresh temporary profile and its registry storage receive explicit write/delete/ACL-change denials before launch and are removed after termination. The registry-read capability permits access to registry objects whose ACLs grant that capability; it is not a general prohibition on all registry reads. An independently protected private registry canary must remain unreadable.

Mandatory live probes must return actual access-denied results for external and loopback IPv4/IPv6, unrelated file reads, arbitrary/model/profile file writes, profile-registry writes, private-registry reads and child-process creation. A suspended process is assigned to a one-process, kill-on-close job before execution. Per-user cross-session serialization prevents overlapping temporary file ACL leases. Restricted permissions and model-open leases remain in force throughout inference. If any check fails, no prompt is sent. Cancellation first signals an inherited synchronize-only event for cooperative native cleanup; a hung worker is terminated after a two-second grace period.

Only English/German and five fixed forms are selected. The final input line contains exactly the validated marked ASCII words. A generated grammar forces the same literal order and repeated occurrences; the UI verifies it independently. Output is literal WPF text, never executable Markdown, HTML, links, shell commands or model tools. Model output cannot initiate external actions. Word-list membership does not establish BIP39 checksum validity or random EFF selection.

Preferences have exactly four fields: language, width, height and model path. No history, prompt cache, telemetry, output export or story clipboard feature exists. Input copy/cut/undo/drag export and UI Automation text export are disabled. Generated text starts concealed, reveals only after confirmation, hides after ten minutes and on deactivation/lock/suspend. Owned mutable buffers are wiped and input/render references cleared during session cleanup.

## Key handling and trust

Private RSA-4096 and ML-DSA-87 keys exist only as separate DPAPI CurrentUser ciphertexts at `A:\Passphrase Memorizer Keys`. Generation requires the exact external directory to be empty and never overwrites existing keys. The FAT32 drive has no NTFS ACLs, so protection at rest is provided by DPAPI with independent role entropy. Plaintext DPAPI output and owned private arrays are zeroed; no unencrypted wrapping-key file or private export is committed. Public keys and the self-signed public certificate are in `Windows/Crypto/Trust`.

Public SHA-256 trust anchors:

```text
RSA-4096 SPKI: 349A8BEF61057A00FC50E759722F3630633B1CC405C1DE1657405FCBD0BB5362
ML-DSA-87:    3DC0A4D00CEA516BBA9C64539E21912AC7EEA10420053995A9FBDD16CCD29A6A
```

DPAPI recovery depends on the original Windows account/profile and its protected DPAPI state; copying the key drive alone is not a guaranteed portable recovery plan. Code running as that same user can also call DPAPI. There is no publicly trusted Authenticode signing certificate and no root certificate is installed. First-download trust must be established independently; an executable cannot authenticate a malicious replacement of itself and its embedded trust anchor.

## Review findings resolved

1. Windows Git checkout initially translated pinned word-list newlines. `.gitattributes` now preserves exact resource bytes across platforms; original SHA-256 values are unchanged.
2. Secondary Unicode-plane letters were initially counted as UTF-16 code units. The verifier now uses Unicode scalars, with a regression fixture matching Swift behavior.
3. Installer backup paths initially used only a timestamp. GUID-qualified staging/backups and explicit collision rejection now prevent nested backup directories and broken rollback.
4. Public test wrapper initially combined incompatible fixture flags. It now runs the app's exclusive smoke/sandbox/cancel modes separately.
5. A redundant Windows child-process policy prevented DLL initialization on this host. The suspended worker now enters the restrictive job before execution, and a mandatory child-creation denial probe verifies the enforced restriction. Plain AppContainer loopback refusal was not accepted as a denial result; LPAC probes require `WSAEACCES` on all four network paths.
6. An undocumented LPAC token-information query returned `ERROR_INVALID_PARAMETER` on this host. The supported `DuplicateToken`/`AccessCheck` attestation instead requires the restricted-packages grant alone: the actual production checker accepted LPAC (mask 2) and rejected ordinary AppContainer (mask 3) in positive/negative public fixtures.
7. The manual Gemma 4 chat prefix initially omitted the empty closed thought channel used by the pinned model template when thinking is disabled. The final prefix follows `google-gemma-4-31B-it.jinja` and the output grammar accepts only the requested final text.

## Limits

CPU execution replaces the macOS Metal backend; GPU acceleration is absent. Managed strings, WPF, native allocators, registers, Windows paging/hibernation, crash capture and display copies cannot all be irreversibly erased by this app. Graceful cleanup and forced child termination have different guarantees; forced termination can skip destructors. Windows capture exclusion is best effort and not a universal screen-recording guarantee. The GUI itself is a normal desktop process; the stricter token applies to inference.

Windows isolation depends on the current OS, effective resource ACLs and kernel enforcement. Representative denial probes are repeated before every prompt and are not a proof of all possible Windows API behavior. Administrators, malware running as the same user, a compromised OS, malicious first installation and unreviewed future OS changes remain outside the asserted trust boundary. The model is external input to the isolated GGUF parser; a matching installed blob hash establishes local consistency, not a publisher authenticity proof.

The security-center inventory lists Bitdefender and an inactive Defender engine on this machine. No separate full malware-scan result has been claimed. Code review, dependency-advisory queries, independent cryptographic checks and release-integrity verification constitute the documented security examination.
