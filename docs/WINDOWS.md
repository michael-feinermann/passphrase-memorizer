# Passphrase Memorizer 1.0.1 for Windows

The WPF port preserves the v1.0.1 features and visual layout: English/German, BIP39/EFF word lists, 1–128 words, five literary styles, exact bold blue `[word]` markers, ordered repeated words, temporary ten-minute reveal, concealment on deactivation, clear/cancel/quit cleanup and preferences limited to language, dimensions and model path.

Windows uses its own static native CPU worker in an AppContainer without network capabilities. This build has no GPU backend. It reads the installed Gemma 4 31B GGUF directly; it does not contact Ollama or another HTTP service. Model weights are never included in Git or the app bundle. The Windows package includes .NET 10 and installs per-user with Start Menu/Desktop shortcuts and a Windows uninstall entry.

See the [German Windows instructions](WINDOWS.de.md) for the installed model, build/package/install commands and signature verification. The [Windows security audit](WINDOWS_SECURITY_AUDIT.md) records validation evidence and limitations.

Private RSA-4096 and ML-DSA-87 keys are stored only at `A:\Passphrase Memorizer Keys`, separately encrypted with Windows DPAPI CurrentUser. The FAT32 key drive cannot enforce NTFS ACLs. Decryption depends on the original Windows account/profile and DPAPI state; backing up this drive alone is not a portable recovery procedure. The signed release uses independently required RSA-PSS/SHA-512 and ML-DSA-87 plus SHA-256, SHA3-512 and Skein-1024-1024 fingerprints. Public key trust must be established independently before installation. There is no publicly trusted Authenticode signature or root-certificate installation.

Neither mnemonic quality nor word-list membership increases phrase entropy. Wallet checksum validation is not performed. Owned mutable buffers are wiped, but irrevocable erasure of every managed/UI/runtime/OS copy is not guaranteed. This is an internal audit, not an external security certification.
