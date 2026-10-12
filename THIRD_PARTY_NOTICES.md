# Third-party notices

## BIP39 English wordlist

The unmodified list is included in `Sources/MnemonicStoryCore/Resources/english.txt` and the application bundle. [Original wordlist](https://github.com/bitcoin/bips/blob/master/bip-0039/english.txt).

BIP39's authors are Marek Palatinus, Pavol Rusnak, Aaron Voisine and Sean Bowe. The [specification](https://github.com/bitcoin/bips/blob/master/bip-0039.mediawiki) is MIT licensed. The corresponding python-mnemonic notice, copyright 2013-2016 Pavol Rusnak, is included at `Licenses/python-mnemonic-MIT.txt` and in the app's license directory.

## EFF Large Wordlist

Copyright Electronic Frontier Foundation. The original list by Joseph Bonneau is included unchanged in `Sources/MnemonicStoryCore/Resources/eff_large_wordlist.txt` and the application bundle. The app parses dice labels and words without changing the distributed file.

- [Original wordlist](https://www.eff.org/files/2016/07/18/eff_large_wordlist.txt)
- [EFF passphrase method](https://www.eff.org/dice)
- [EFF copyright policy](https://www.eff.org/copyright)
- [Creative Commons Attribution 4.0 International license](https://creativecommons.org/licenses/by/4.0/legalcode)

EFF's copyright policy permits redistribution of its original website material under CC BY 4.0 unless otherwise stated. No affiliation with or endorsement by EFF is implied.

## llama.cpp and ggml

The native worker statically links the native implementation from [llama.cpp revision 8e1642198dcd4e408f8776222d6ae31b74d01187](https://github.com/ggml-org/llama.cpp/tree/8e1642198dcd4e408f8776222d6ae31b74d01187), including its incorporated ggml source. Copyright 2023-2026 The ggml authors, MIT License.

The pinned revision's [root license](https://github.com/ggml-org/llama.cpp/blob/8e1642198dcd4e408f8776222d6ae31b74d01187/LICENSE) covers the incorporated ggml source and is copied into `Contents/Resources/Licenses/llama.cpp-LICENSE.txt` when packaging. No model weights, dynamic backend plugins, HTTP server or conversion tools are included in the app.

## Windows distribution

The Windows worker links the same pinned llama.cpp revision statically; its license is packaged at `Licenses/llama.cpp-LICENSE.txt`. Bouncy Castle C# 2.6.2 provides ML-DSA-87, SHA3-512 and Skein-1024-1024 under its MIT license; its notice is included in the Windows package. The self-contained WPF package includes Microsoft's .NET 10 runtime and its distributed license and third-party notices. No native HTTP server, model weights or dynamic model-backend plugins are packaged. Public test signing interoperability uses the already vendored pinned pq-crystals reference under `Signing/HybridSigner/Reference/LICENSE`.

## Model weights

Models are installed separately by the user. This repository and its application archive contain no model weights. Gemma models have their own [Google Gemma terms](https://ai.google.dev/gemma/terms). Other models have their publishers' licenses. The application's MIT license does not grant rights to model weights.
