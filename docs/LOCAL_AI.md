# Local models and offline execution

English | [Deutsch](LOCAL_AI.de.md)

Passphrase Memorizer is an independent macOS app. Copy your existing BIP39 or EFF word sequence into its input field and choose a rhyme, ballad, poem, short story, or rap. It does not generate passwords or alter Password Generator. English is the initial interface and output language; the last chosen language and window size are local preferences. The interface shows the app version after its name. A revealed memory aid is visible for ten minutes, with the exact passphrase words shown as blue, bold `[word]` markers. Deactivation conceals it sooner; reveal it again only when your surroundings are private.

The app uses its own native `LocalMnemonicRunner`, linked to a fixed revision of llama.cpp. Each generation starts a fresh process. There is no HTTP service, localhost endpoint, external AI app, plug-in, retrieval, browser, or tool execution. The worker applies its macOS Seatbelt restrictions before accepting sensitive input. Network operations and filesystem writes are denied. A bounded public Metal computation runs before readiness when acceleration is attempted, followed by another set of network/write/read denial probes. The GUI then attests the actual child process before transmitting the prompt.

## Native acceleration

The runtime selects the native Metal GPU backend when its public startup computation succeeds. A graceful unavailable/failed initialization selects the CPU. A fatal Apple driver error, failed security probe or later inference error ends the request; there is no general automatic retry guarantee. CPU and Metal retain the same network and file-write prohibition. Backend choice is volatile and is not a saved setting or a per-request CPU/GPU timing calibration.

The signed worker embeds precompiled Metal shader libraries and fixed tensor capability probes. A reviewed build overlay removes runtime shader-source compilation and file-based shader fallback; other GPU backends and dynamically loaded plug-ins are disabled. GPU use permits only the `AGXDeviceUserClient`, Apple's `com.apple.MTLCompilerService` for pipeline specialization, and narrowly specified directory metadata. The metadata covers the worker's actual direct parent under both its canonical path and verified dyld spelling. An actual `/var/` or `/tmp/` launch with the corresponding verified `/private/` canonical path additionally permits metadata of that public system alias itself. It does not grant directory listing, additional file contents or filesystem writes. The compiler receives fixed shader code and specialization constants rather than phrase text or tensor contents. These constants can describe tensor shapes and workload sizes; the Apple service can retain such metadata in its own shader caches/logs outside the worker's policy. GPU mode therefore also trusts Apple's compiler and GPU driver.

Metal is GPU execution. This build has no separate Core ML/Apple Neural Engine backend. Compatible M5 GPU tensor operations can use GPU Neural Accelerators; these are distinct from the separate Neural Engine. Availability depends on the hardware, operating system and model. [Apple's M5 architecture](https://www.apple.com/newsroom/2025/10/apple-unleashes-m5-the-next-big-leap-in-ai-performance-for-apple-silicon/).

Three fresh CPU/Metal benchmark pairs on Apple M5 with the final connected-text prompt and grammar measured median complete helper-process durations of 12.665 s on CPU and 11.762 s with Metal, including startup and cleanup: 7.1% less time for that public twelve-word fixture. Numerical backend differences changed output lengths; this does not establish a universal speedup or lower energy use. See the [audit](SECURITY_AUDIT.md) for the method and current verification state.

## Use Gemma 4 E4B

Obtain the model before entering any secret. The app itself does not download models. Acquisition and development dependency installation are separate setup tasks that require Internet access; generation does not.

1. Open [Google's official Gemma 4 E4B QAT GGUF repository](https://huggingface.co/google/gemma-4-E4B-it-qat-q4_0-gguf/tree/main).
2. Download `gemma-4-E4B_q4_0-it.gguf` to a fully local folder outside this Git checkout and cloud-sync folders. The `mmproj` file is for multimodal input and is unnecessary for this text-only app. Mounted network volumes, known cloud-provider locations and dataless placeholders are refused.
3. Check the publisher, license and published file digest. Retain the model revision and digest if you need reproducible model selection. The app does not authenticate a weight file's publisher merely because its extension is `.gguf`.
4. Start the signed Passphrase Memorizer app, select the local `.gguf` file, enter your word sequence, select its wordlist and literary form, then generate.

Select the model again after launching the app. Its path and access bookmark are not stored by the app. The selected file remains on your disk as the intentionally installed model; clearing a session does not delete model weights.

Gemma's model family and instruction-tuned E4B checkpoint are described by [Google](https://ai.google.dev/gemma/docs/core) and the [official model card](https://huggingface.co/google/gemma-4-E4B-it). Model capability is separate from the capabilities allowed by this app: generated text cannot open websites or execute a model's suggested commands.

## Build the native runtime

The source revision is pinned to [`8e1642198dcd4e408f8776222d6ae31b74d01187`](https://github.com/ggml-org/llama.cpp/tree/8e1642198dcd4e408f8776222d6ae31b74d01187). Build prerequisites are CMake, a full Xcode installation with macOS SDK 26 or later, and its Metal compiler/toolchain. Install these before the offline build. The runtime retains a macOS 14 deployment target; tensor libraries are loaded only on supported devices/systems. From this repository:

```sh
./Scripts/build-local-ai.sh --fetch
```

This explicitly fetches the pinned source into the ignored build workspace and builds `.build/local-ai/LocalMnemonicRunner`. After the source has been fetched, rebuild offline with:

```sh
./Scripts/build-local-ai.sh
```

The packaging script embeds that runtime at `Passphrase Memorizer.app/Contents/Helpers/LocalMnemonicRunner`. The signed helper is part of the release, whereas its model weights are supplied locally. Building an ad-hoc development helper does not unlock production GUI inference. See the [build and signing instructions](../README.md) for the expected signing team and the deliberate policy changes needed for a separately signed fork. Never substitute `llama-server`, Ollama, LM Studio, or an arbitrary helper executable. The app's protocol and isolation checks are specific to this runner.

## Convert a different checkpoint

Prefer an existing GGUF from a trusted publisher. For self-conversion, download the complete [official E4B instruction-tuned checkpoint](https://huggingface.co/google/gemma-4-E4B-it) with its tokenizer and configuration into a local directory. Install the conversion dependencies in a separate Python environment, using the pinned llama.cpp checkout. Conversion operates on model files, so do not put secret phrases in the conversion commands or filenames.

The following paths are examples, to be replaced with your local directories:

```sh
python3 -m venv /path/to/conversion-env
/path/to/conversion-env/bin/python -m pip install -r /path/to/llama.cpp/requirements/requirements-convert_hf_to_gguf.txt
HF_HUB_OFFLINE=1 TRANSFORMERS_OFFLINE=1 /path/to/conversion-env/bin/python /path/to/llama.cpp/convert_hf_to_gguf.py /path/to/gemma-4-E4B-it --outfile /path/to/models/gemma-4-E4B-it-f16.gguf --outtype f16
```

Install dependencies and obtain all checkpoint files before the offline conversion command. Depending on the checkpoint, conversion may require considerable free memory and disk space. Read the pinned converter's `--help` and [llama.cpp model documentation](https://github.com/ggml-org/llama.cpp/blob/8e1642198dcd4e408f8776222d6ae31b74d01187/docs/models.md). Optional quantization is a separate local conversion step using the same revision's `llama-quantize` tool.

## Other models

Select another local, text-capable, instruction-tuned GGUF supported by the pinned llama.cpp revision. Its tokenizer and supported chat template must be included in the GGUF metadata. Gemma 4 uses the runner's explicit text-only turn format; other models use llama.cpp's supported lightweight template formatter. Arbitrary Jinja code is not executed. A `.safetensors`, ONNX, MLX, adapter, or projector file is not a substitute for the main GGUF. New architectures or unsupported templates may require a reviewed runtime update and fresh isolation tests; changing a filename cannot add architecture support.

No account, API key, remote model ID, `-hf` download argument, Internet fallback, or custom endpoint is accepted during inference. Output grammar requires every `[word]` marker in order, including repeated words and the input's capitalization; the GUI verifies the sequence again before accepting the result. A bare marker list is refused: the grammar requires an unmarked English/German letter before the first marker, and the GUI checks for an unmarked Unicode letter. This minimum check does not prove literary quality or factual accuracy. If constraints, limits or validation fail, no unconstrained story is returned. Test a new model with public sample words first. A memory aid does not strengthen the original password and may expose it just as clearly.

## Session deletion and limits

Clear the session before leaving it unattended. Cancellation requests normal worker cleanup first and escalates to forced termination after two seconds if needed. Orderly quit closes the launch gate and keeps the GUI process alive until active and already cancelling workers are reaped. Cancelling also clears the controlled prompt before a scheduled generation task has started. Already submitted GPU commands are not guaranteed to be preempted; forced termination cannot guarantee destructor cleanup. Clearing, closing and orderly quitting cancel generation, terminate the worker and remove the app's volatile input and output. The app has no transcript file, prompt cache, database, telemetry, automatic export, or saved model selection. Only the language and window size are deliberately retained by app-managed preferences. A three-key allowlist removes file-dialog bookmarks (`NSNav`/`NSOSP`) and window autosave keys from the app's preference domain at startup, after the dialog and during orderly shutdown; global Finder and operating-system metadata remain outside that guarantee.

This is deletion of the application session and explicit wiping of controlled buffers. It is not a guarantee of irrevocable physical erasure of all Swift, AppKit, llama.cpp, GPU/driver, register, swap, crash-report, screenshot, or operating-system copies. Normal cleanup synchronizes GPU work and clears the model memory cache before destroying the context; this cannot guarantee wiping every Metal allocation, driver cache or OS-service copy. A forcibly terminated process cannot reliably execute cleanup. The macOS custom sandbox API is deprecated, so future OS compatibility must be tested; failure to establish the worker's restrictions blocks generation. The [internal security audit](SECURITY_AUDIT.md) identifies the verified protections and remaining limits.

The original sequence remains authoritative. The app checks English wordlist membership for 1 through 128 words, not wallet validity or BIP39 checksums. Letter case is preserved. Whitespace and EFF hyphens separate words and are not part of the story; use your original password's separators when entering it elsewhere. BIP39 permits only 12, 15, 18, 21 or 24 words for a standards-compliant mnemonic, with the specified checksum. Other lengths drawn from that wordlist are generic word sequences, not valid BIP39 wallet mnemonics. [BIP39 specification](https://github.com/bitcoin/bips/blob/master/bip-0039.mediawiki). EFF wordlist membership alone does not prove that a phrase was selected randomly. [EFF's passphrase method](https://www.eff.org/dice).
