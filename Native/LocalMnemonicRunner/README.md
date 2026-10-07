# LocalMnemonicRunner

Dedicated macOS text inference executable using pinned llama.cpp commit
`8e1642198dcd4e408f8776222d6ae31b74d01187`.

Build once with `./Scripts/build-local-ai.sh --fetch`, which fetches source code
only. Subsequent `./Scripts/build-local-ai.sh` builds require no network access.
The model remains an external, user-selected, trusted GGUF file. The executable
contains statically linked Apple Metal GPU and CPU inference, no server or other
AI service. Accelerate, BLAS, OpenMP, RPC, CUDA, dynamic backend discovery, common tools,
downloads, subprocess support, HTTPS, and multimodal projectors are disabled.
Build prerequisites are CMake, Xcode and Apple's installed Metal Toolchain with
SDK 26 or newer. General Metal libraries target macOS 14; the separate tensor
libraries target macOS 26 and are loaded only when supported. The pinned backend
has no Apple Neural Engine/Core ML backend. M5 GPU tensor acceleration is GPU
execution, rather than NPU execution.

The build produces `.build/local-ai/LocalMnemonicRunner` and an independent
`.build/local-ai/LocalMnemonicSandboxProbe`. A separate
`.build/local-ai/LocalMnemonicBenchmark` is a development fixture, never bundled.
Distribution packaging places the
production runner in `Contents/Helpers/LocalMnemonicRunner` and includes the
llama.cpp MIT license from `.build/local-ai/licenses/llama.cpp-LICENSE.txt`.
Use Hardened Runtime signing with library validation and no debug entitlement.
Do not sign this helper with App Sandbox or sandbox inheritance entitlements:
its dynamically restricted Seatbelt policy must be installed successfully.
Already sandboxed callers must not be used without verifying policy composition.

## Swift integration contract

1. Spawn a fresh runner for each generation. The normal argument is an absolute
   path to one ordinary `.gguf` file. Optional `--cpu` or `--metal` before that path
   forces a backend for public verification. Secrets never occur in arguments or the
   environment. Supply a minimal environment and anonymous pipes for stdin and
   stdout. The runner rejects regular files and sockets as stdio transports,
   closes every inherited descriptor above 2, and redirects stderr to `/dev/null`.
2. Wait for the exact four bytes `MSAI`. These are emitted only after core dumps
   and regular-file output are disabled, debugger attachment is denied, the
   deny-default sandbox is applied, and mandatory network/file denial probes pass.
   Automatic mode also verifies an actual public computation on Metal before
   sending `MSAI`, then repeats the denial probes. An ordinary Metal initialization
   or computation failure selects CPU; `--metal` instead fails closed. A fatal OS
   driver failure aborts before the handshake, without receiving any prompt.
   Do not send the prompt before this handshake. A startup failure emits no story.
3. Write a little-endian unsigned 32-bit byte count followed by UTF-8 prompt bytes.
   The count must be between 1 and 65,536 inclusive. Supply the complete language,
   mnemonic style, and ordered word instructions in the prompt. Close stdin after
   the frame, then wipe the sender's buffers. No additional frames are accepted.
   The final nonempty prompt line must contain 1 to 128 space-separated `[word]`
   markers. Words consist only of ASCII letters, at most 64 per word. Capitalization
   is preserved exactly.
4. Read a little-endian unsigned 32-bit count followed by UTF-8 story bytes,
   again at most 65,536 bytes. No partial or truncated story is emitted on failure.
   Verify exit code zero, UTF-8, bounds, full frame, absence of trailing bytes, and
   presence and order of every exact passphrase word before displaying it.
5. Kill and reap the child on cancellation, Clear, window close, or app exit.
   Discard pending outputs from cancelled requests. The runner handles SIGTERM
   and SIGINT by aborting and cleaning up; the parent must enforce the 600-second
   hard deadline with a final kill and reap if necessary.

Exit codes: `0` success; `64` invocation; `65` invalid/oversized input; `66` invalid
or unsupported model; `70` inference/result failure; `71` protection failure;
`75` cancellation or timeout. Stderr carries no diagnostics or prompt text.

The context is capped at 8,192 tokens, prompt at 4,096 tokens, generation at
4,096 tokens, CPU threads at eight, wall duration at 600 seconds, aggregate CPU
duration at 4,800 seconds, and model
file size at 32 GiB. A generated story that reaches the token cap without an
end-of-generation token is rejected. A smaller model or shorter input may be
needed on machines with insufficient RAM or CPU speed.

Gemma 4 text generation uses its `<|turn>` prompt format with thinking disabled,
following Google's E4B instructions. Other models must include a chat template
supported by the pinned `llama_chat_apply_template` implementation. Unsupported
templates fail closed; there is no Jinja interpreter or raw-prompt fallback.
Generated thought, turn, and tool markers are rejected. No tools are executed.
A generated GBNF grammar enforces every exact bracketed word in its required
position, including repeated occurrences, with no additional square brackets.
The opening segment requires an unmarked English or German letter; a bare marker
list cannot satisfy the grammar. This is a syntactic requirement, not a guarantee
that a generated text rhymes or meets every literary expectation.
Connecting segments cannot contain angle brackets and are bounded by the global
token, output, and time limits. Malformed marker sequences or grammar initialization failure stop
generation; there is no unconstrained decoding fallback.

## Protection and its practical limits

Seatbelt denies every operation by default and permits read-only access to the
exact selected model and system libraries under `/System/Library` and `/usr/lib`,
self process information, and read-only sysctl queries. GPU mode additionally
permits only `AGXDeviceUserClient`, the exactly named Apple
`com.apple.MTLCompilerService`, and file metadata for the actual executable's
direct parent directory, using both the actual dyld path spelling and its verified
canonical equivalent for aliases such as `/var` and `/private/var`. The paths come
from `_NSGetExecutablePath` and `realpath`, never `argv[0]`. Directory enumeration
and file contents there remain denied.
When that actual dyld path uses the public `/var/` or `/tmp/` system alias and its
realpath proves the corresponding `/private/var/` or `/private/tmp/` prefix, Metal
also receives metadata for exactly `/var` or `/tmp`, respectively. CoreFoundation
needs to stat the alias itself. No alias subpath, directory data, or writes are
permitted.
Forced CPU mode receives none of these GPU permissions and never initializes
Metal. No network, file writes,
child execution, fork, arbitrary home-directory reads, or external AI services
are permitted. The model is loaded from a validated read-only descriptor, without
mmap, split-model discovery, or lazy loading. A successful Metal selection requests
offloading all model layers and attention operations to that explicitly selected
device; unsupported operations and sampling can still use the CPU.
Cloud-storage paths, dataless placeholders, symlinks, and remote file systems are
rejected before reading model bytes, to avoid file-provider or remote-volume IO.

Mandatory probes require the kernel to return `EPERM` or `EACCES` for IPv4/IPv6
TCP connections and binds, UDP traffic, a local UNIX DNS socket connection,
unrelated file reads, enumeration of the helper's own directory, and file creation.
Socket allocation alone is not a network
operation gated by Seatbelt. Existing descriptors are removed before the policy
is applied, so they cannot bypass connection restrictions.

Explicit prompt, formatted prompt, token, output, grammar, and temporary piece buffers
must be successfully memory-locked before reading the prompt and are wiped using
volatile writes on normal completion and handled cancellation. GPU work is
synchronized before and after zeroing llama's memory cache; its sampler is reset
before destruction. Logging is disabled.
Each generation then destroys its complete process address space.

The OS and third-party inference library can still create internal copies not
exposed by their APIs. `mlockall` for internal allocations is attempted but can
fail, and the macOS paging, compression, hibernation, crash reporting, backup,
physical RAM, and a compromised operating system are outside this app's absolute
control. GPU driver allocations and private GPU resources also cannot be completely
locked or physically erased by this process. Backend cache clearing is attempted;
this does not establish forensic erasure of every GPU-managed copy.
SIGKILL destroys the address space but does not run buffer destructors.
SIGTERM/SIGINT/SIGALRM set a signal-safe cancellation flag. Input/output loops,
model loading, the outer token loop and llama's CPU abort callback observe it,
allowing normal cleanup. A drift-checked overlay also exposes the pin's existing
Metal setter under llama's generic abort-registration proc name. Metal checks
that callback only at limited backend checkpoints; it does not preempt normal
asynchronous GPU work or interrupt already submitted command buffers. The outer
token loop remains the cooperative cancellation gate for that work.
These checks do not guarantee that a blocked OS/GPU-driver call will
return promptly; synchronization and cleanup can also wait for driver work. The
native 600-second alarm is therefore cooperative rather than a guaranteed kill.
The parent must retain every running session, enforce its independent deadline,
send SIGKILL if needed, and reap every helper before completing app termination.
This implementation creates no prompt files, model caches, history, or chat logs;
it does not claim forensic erasure of every possible OS-managed copy.

All Metal libraries, including the two tiny public tensor capability probes, are
compiled during the build and embedded in the signed executable. A deterministic
overlay verifies the pinned loader/callsite boundaries, removes runtime source
compiler code and filesystem fallback, and stubs the exported source compiler to
return NULL. Metal loads libraries only from embedded binary data. Apple's
compiler service is still required to specialize fixed pipelines from that IR;
it receives shaders and pipeline constants, never word strings or tensor contents.
Some constants describe tensor shapes and workload, so the service can receive
coarse shape or length metadata about a session.
That OS service has its own logging/cache policy outside the worker's write ban.
The worker has no cache-directory write permission. The overlay also prevents
upstream's eager Metal registration during a forced CPU run.

## Reproducible isolation tests

Run `python3 Native/LocalMnemonicRunner/test_protocol.py` after building. It uses
only a public synthetic GGUF header and checks kernel-enforced denial, permitted
model reads, denied child execution and fork, malformed framing, invalid UTF-8,
symlink refusal, silent failures, and SIGTERM before prompt delivery. The
independent probe shares only `Sandbox.hpp` with production and links no inference
library; both CPU and GPU policy variants are exercised. The production `--self-test` uses the same mandatory startup checks,
returns `MSAI` and a framed JSON report, and accepts no secret prompt.
`--self-test-metal` additionally verifies real GPU computation and reports whether
the tensor libraries were loaded. The release verifier requires both probes on
the actual packaged helper, including the exported archive.
`python3 Scripts/test-local-ai.py --model /absolute/local/model.gguf --all` also
tests public fixture inference in all five styles and both languages, plus a
128-word sequence. It reports timing and exact-word validation, without printing
generated stories. It never uses genuine credentials.
The final native protocol/isolation suite passes 17 cases, including both policy
variants, packaged TMPDIR alias startup and a forged `argv[0]`. The Gemma 4 E4B
Metal matrix passes all 13 public cases: five styles in English and German,
128 words, mixed capitalization/repetitions, and an EFF sequence. Every result
contains the exact ordered markers and unmarked letters, with empty stderr.
The 128-word case completed successfully in 68.58 seconds during release QA;
this case timing is separate from the controlled CPU/Metal benchmark below.
After the abort-registration overlay was added, all 17 isolation/protocol cases
and a real four-word Metal rhyme passed again. Run
`python3 Scripts/test-local-ai.py --model /absolute/local/model.gguf --cancel-test`
to exercise SIGTERM after public prompt delivery. Both 128-word fixtures,
cancelled after 1.8 and 5.0 seconds, exited with code 75 within 0.20 seconds of
the signal and emitted no result or stderr bytes. These observed exits do not
establish a universal deadline for driver stalls; the parent's kill/reap path
remains required.

For a controlled public fixture comparison, run
`python3 Scripts/test-local-ai.py --model /absolute/local/model.gguf --benchmark --iterations 3`.
The separate benchmark executable uses the same policy and model settings,
greedy decoding, a fixed public 12-word sequence, and alternating CPU/Metal runs.
Each run is a fresh signed Hardened Runtime process. Only timings, token counts
and public backend metadata are printed, with no prompt or story text.

Measured on 2026-10-05 with Apple M5, 16 GiB, 10 CPU cores, macOS 27.0.1 and the
official `gemma-4-E4B_q4_0-it.gguf` (SHA-256
`676c35070db6dbe52f93e9c864ee0fba4eddea94b9c875d9cb10daff453fbaee`):

| Median of three runs | CPU | Metal GPU |
| --- | ---: | ---: |
| Total wall duration, including startup and cleanup | 12.665 s | 11.762 s |
| Generation throughput | 15.146 tokens/s | 16.080 tokens/s |

Metal reduced total duration by 7.1% for this fixture and is the default when
its protected warmup succeeds. Both runs processed 272 prompt tokens; CPU emitted
106 tokens and Metal 134, because floating-point backend differences can alter
greedy choices. Throughput and full duration are reported separately. This is a
single-machine comparison with a warm OS model-file cache, rather than a promise
for every model or computer. NPU throughput was not measured because the pinned
inference backend cannot execute on Apple's NPU.

Sources: [llama.cpp](https://github.com/ggml-org/llama.cpp/tree/8e1642198dcd4e408f8776222d6ae31b74d01187),
[Gemma 4 prompt format](https://ai.google.dev/gemma/docs/core/prompt-formatting-gemma4),
[Apple networking restriction semantics](https://developer.apple.com/documentation/bundleresources/entitlements/com.apple.security.network.server).
