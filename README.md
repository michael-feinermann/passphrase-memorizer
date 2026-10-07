# Passphrase Memorizer

English | [Deutsch](README.de.md)

Passphrase Memorizer is a separate macOS app for turning an existing BIP39 or EFF word sequence into a rhyme, ballad, poem, short story, or rap. It runs Gemma 4 E4B or a compatible local GGUF through its own isolated native worker with Metal GPU acceleration and CPU fallback. It is independent of Password Generator and other AI applications. The [model setup guide](docs/LOCAL_AI.md) explains startup fallback and acceleration limits.

The interface and generated text start in English. German is available. Only the last selected language and window size are saved as preferences. Model weights are selected locally and are not included in Git or the app archive.

The app version appears after its name in the interface. Required words appear in square brackets, in blue and bold, while a revealed memory aid remains visible for up to ten minutes. Deactivation still conceals the text immediately.

## Download and install

The macOS release targets Apple Silicon (arm64) and macOS 14 or later. Download the app ZIP from [GitHub Releases](https://github.com/michael-feinermann/passphrase-memorizer/releases/latest), extract it and move `Passphrase Memorizer.app` into `/Applications`. The local model is installed separately; follow the [model setup guide](docs/LOCAL_AI.md).

The release includes SHA-256, SHA3-512 and Skein-1024-1024 checksums, detached RSA-4096-PSS/SHA-512 and ML-DSA-87 signatures, and a separate local Hybrid Signer. The app ZIP, integrity manifest, bundle inventory and Signer ZIP must all pass both signatures. Follow the [hybrid verification and signing guide](docs/HYBRID_SIGNING.md), including its public-key trust requirements. The immutable [v1.0.0 integrity manifest](Signing/Releases/v1.0.0/Passphrase-Memorizer-1.0.0.integrity.txt) and detached signatures are also tracked in the repository. The signing tool is separate from inference and does not receive passphrases.

The matching icon is supplied in [Assets/AppIcon-1024.png](Assets/AppIcon-1024.png); its creation brief is in [ICON_PROMPT.md](Assets/ICON_PROMPT.md).

## Using the app

1. Obtain a trusted local GGUF before entering any secret. Follow the [Gemma 4 E4B and alternative-model guide](docs/LOCAL_AI.md).
2. Open Passphrase Memorizer, choose the local model, and enter your existing words.
3. Choose the BIP39 or EFF wordlist, language and literary form, then generate.
4. Reveal the memory aid when your screen is private. Clear the session when finished.

The app accepts 1 through 128 words from the selected English wordlist. It checks wordlist membership and the generated text's marked word sequence, including repeated words. It does not validate BIP39 wallet checksums. A standard BIP39 mnemonic has 12, 15, 18, 21 or 24 words and the specified checksum; other accepted lengths are generic word sequences. See the [BIP39 specification](https://github.com/bitcoin/bips/blob/master/bip-0039.mediawiki).

The original phrase remains authoritative. A memory aid does not increase its entropy or replace a secure backup. EFF wordlist membership alone does not prove random selection; see [EFF's passphrase method](https://www.eff.org/dice).

## Privacy and security

Each generation starts a fresh native process. Before receiving the phrase, the worker applies a macOS Seatbelt policy and checks that IPv4/IPv6 traffic, filesystem writes and unrelated file reads are denied. It has no HTTP server, model downloader, browser or tools. The GUI communicates using bounded pipes, not a network endpoint. Both app and worker require Hardened Runtime; production inference also requires their expected signatures.

No transcript, prompt cache, story file, telemetry, selected model path or model bookmark is deliberately stored. Clearing, closing and orderly quitting terminate inference and wipe controlled secret buffers. Displayed text is concealed on deactivation; the generated story also hides after about 10 minutes. The app provides no story-copy or export function.

Complete irrevocable erasure of every Swift, AppKit, inference-engine, GPU/driver, screen, swap or operating-system copy cannot be guaranteed. Forced termination may bypass cleanup. The custom sandbox API is deprecated and its behavior must be checked on each supported macOS release. The GUI itself is not an App Sandbox process because the helper must establish its stricter policy in a fresh process. Read the [internal security audit](docs/SECURITY_AUDIT.md) for evidence and remaining limits. This is an internal audit, not external certification.

## Building

Requirements: macOS 14 or later, a full Xcode installation with Swift 6, macOS SDK 26 or later and its Metal compiler/toolchain, CMake, and Python 3 for the release checker. Install development dependencies and obtain model files before working with confidential words. Dependency acquisition requires a separate online setup step; inference does not.

```sh
./Scripts/build-local-ai.sh --fetch
swift test
./Scripts/package-app.sh --dev
```

The native transport integration tests require `RUNTIME_TEST_SIGN_IDENTITY` set to the SHA-1 identity of an available Developer ID certificate from team `2T6K9PGS55`. They sign public test fixtures and verify the actual running child before any prompt byte is sent. Without that explicit test identity, those signed integration cases are skipped; unsigned-child rejection and the other tests still run. This setting is read only by tests and cannot bypass production signature checks.

The first command explicitly fetches the pinned llama.cpp source. Subsequent builds use `./Scripts/build-local-ai.sh` without a fetch. The package script builds Swift in release mode with warnings treated as errors, builds the native worker, removes external build paths, copies wordlists and license notices, signs the helper before the app, and creates:

- `build/Passphrase Memorizer.app`
- `build/Passphrase-Memorizer-1.0.0.zip`
- `build/Passphrase-Memorizer-1.0.0.zip.sha256`

Development packaging is explicitly ad-hoc signed by default. It can exercise the native sandbox self-test, but production GUI inference deliberately rejects an ad-hoc signature. A production build must use the configured Developer ID team `2T6K9PGS55`; a fork must deliberately review and update both signature policies before using a different signing team.

## Release signing and verification

Use a Developer ID Application certificate and an existing Keychain notarization profile. Do not put passwords, private keys, signing exports or model weights in the repository.

```sh
SIGN_IDENTITY='Developer ID Application: Your Name (2T6K9PGS55)' \
NOTARY_PROFILE='Your-Keychain-Profile' ./Scripts/package-app.sh
./Scripts/verify-release.sh
```

The Keychain profile supplies notarization credentials directly to Apple's tools. Without `NOTARY_PROFILE`, production packaging creates a signed candidate that still requires notarization before publication. The checker requires the expected team and signing identifiers, Hardened Runtime without relaxed entitlements, valid nested signatures, system-only dependencies, intact wordlists, bundled notices, no model weights, successful embedded-worker isolation probes, a valid notarization staple and Gatekeeper acceptance. It also checks the archive and verifies the extracted app.

For an explicitly identified development artifact only:

```sh
ALLOW_UNNOTARIZED_DEVELOPMENT=1 ./Scripts/verify-release.sh
```

That override skips the notarization and Gatekeeper requirements and permits matching ad-hoc signatures. It does not make the artifact a verified public release or unlock production inference.

Application source is [MIT licensed](LICENSE). Wordlists and the bundled native dependency have their own [third-party notices](THIRD_PARTY_NOTICES.md). Model licenses apply separately to the weights you install.
