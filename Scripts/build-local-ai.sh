#!/bin/bash
set -euo pipefail

# Fetches code only when --fetch is explicit. No model is downloaded or tracked.
PROJECT_DIR="$(cd "$(dirname "$0")/.." && pwd)"
LLAMA_COMMIT="8e1642198dcd4e408f8776222d6ae31b74d01187"
AI_BUILD_DIR="$PROJECT_DIR/.build/local-ai"
LLAMA_SOURCE_DIR="${LLAMA_SOURCE_DIR:-$AI_BUILD_DIR/llama.cpp}"
FETCH=0
if [[ "${1:-}" == "--fetch" && "$#" == 1 ]]; then FETCH=1
elif [[ "$#" != 0 ]]; then
  echo "Usage: $0 [--fetch]" >&2
  exit 64
fi
if [[ "$(uname -s)" != "Darwin" ]]; then
  echo "macOS is required for the enforced Seatbelt sandbox." >&2
  exit 1
fi
command -v cmake >/dev/null || { echo "Install CMake before building." >&2; exit 1; }
mkdir -p "$AI_BUILD_DIR"
if [[ ! -d "$LLAMA_SOURCE_DIR/.git" ]]; then
  if [[ "$FETCH" != 1 ]]; then
    echo "Pinned llama.cpp is missing. Run $0 --fetch once while online." >&2
    exit 1
  fi
  git clone --filter=blob:none --no-checkout https://github.com/ggml-org/llama.cpp.git "$LLAMA_SOURCE_DIR"
fi
if [[ "$FETCH" == 1 ]]; then
  git -C "$LLAMA_SOURCE_DIR" fetch --depth=1 origin "$LLAMA_COMMIT"
  git -C "$LLAMA_SOURCE_DIR" checkout --detach "$LLAMA_COMMIT"
fi
if [[ "$(git -C "$LLAMA_SOURCE_DIR" rev-parse HEAD)" != "$LLAMA_COMMIT" ]]; then
  echo "llama.cpp must be exactly $LLAMA_COMMIT; run --fetch to select it." >&2
  exit 1
fi
if [[ -n "$(git -C "$LLAMA_SOURCE_DIR" status --porcelain --untracked-files=all)" ]]; then
  echo "The pinned llama.cpp checkout contains changes; refusing a modified dependency." >&2
  exit 1
fi
cmake -S "$PROJECT_DIR/Native/LocalMnemonicRunner" -B "$AI_BUILD_DIR/cmake" \
  -DLLAMA_SOURCE_DIR="$LLAMA_SOURCE_DIR" \
  -DCMAKE_BUILD_TYPE=Release -DCMAKE_OSX_DEPLOYMENT_TARGET=14.0 \
  -DBUILD_SHARED_LIBS=OFF -DGGML_BACKEND_DL=OFF -DGGML_NATIVE=OFF \
  -DGGML_METAL=ON -DGGML_METAL_EMBED_LIBRARY=OFF -DGGML_METAL_MACOSX_VERSION_MIN= \
  -DGGML_BLAS=OFF -DGGML_ACCELERATE=OFF -DGGML_OPENMP=OFF \
  -DGGML_RPC=OFF -DGGML_CUDA=OFF -DGGML_HIP=OFF -DGGML_VULKAN=OFF \
  -DGGML_SYCL=OFF -DGGML_OPENCL=OFF -DGGML_CPU_KLEIDIAI=OFF \
  -DGGML_CCACHE=OFF -DLLAMA_OPENSSL=OFF -DLLAMA_SUBPROCESS=OFF \
  -DLLAMA_BUILD_COMMON=OFF -DLLAMA_BUILD_TESTS=OFF -DLLAMA_BUILD_TOOLS=OFF \
  -DLLAMA_BUILD_EXAMPLES=OFF -DLLAMA_BUILD_SERVER=OFF -DLLAMA_BUILD_APP=OFF \
  -DLLAMA_BUILD_MTMD=OFF -DLLAMA_BUILD_UI=OFF
MACOSX_DEPLOYMENT_TARGET=14.0 cmake --build "$AI_BUILD_DIR/cmake" --config Release --target LocalMnemonicRunner LocalMnemonicBenchmark LocalMnemonicSandboxProbe --parallel 8
RUNNER_STAGE="$AI_BUILD_DIR/.LocalMnemonicRunner.$$.tmp"
PROBE_STAGE="$AI_BUILD_DIR/.LocalMnemonicSandboxProbe.$$.tmp"
BENCHMARK_STAGE="$AI_BUILD_DIR/.LocalMnemonicBenchmark.$$.tmp"
trap 'rm -f "$RUNNER_STAGE" "$PROBE_STAGE" "$BENCHMARK_STAGE"' EXIT
cp "$AI_BUILD_DIR/cmake/LocalMnemonicRunner" "$RUNNER_STAGE"
cp "$AI_BUILD_DIR/cmake/LocalMnemonicSandboxProbe" "$PROBE_STAGE"
cp "$AI_BUILD_DIR/cmake/LocalMnemonicBenchmark" "$BENCHMARK_STAGE"
mkdir -p "$AI_BUILD_DIR/licenses"
cp "$LLAMA_SOURCE_DIR/LICENSE" "$AI_BUILD_DIR/licenses/llama.cpp-LICENSE.txt"
# ggml is incorporated into llama.cpp under the repository MIT license.
# Development binary has the same library-validation/anti-debug policy as the
# release helper. Release packaging re-signs with the distribution identity.
codesign --force --sign - --identifier de.feinermann.MnemonicStory.LocalMnemonicRunner --options runtime "$RUNNER_STAGE"
codesign --force --sign - --identifier de.feinermann.MnemonicStory.LocalMnemonicSandboxProbe --options runtime "$PROBE_STAGE"
codesign --force --sign - --identifier de.feinermann.MnemonicStory.LocalMnemonicBenchmark --options runtime "$BENCHMARK_STAGE"
# Atomic replacement preserves the signed executable vnode of an active child.
mv -f "$RUNNER_STAGE" "$AI_BUILD_DIR/LocalMnemonicRunner"
mv -f "$PROBE_STAGE" "$AI_BUILD_DIR/LocalMnemonicSandboxProbe"
mv -f "$BENCHMARK_STAGE" "$AI_BUILD_DIR/LocalMnemonicBenchmark"
echo "Built Metal + CPU runner with precompiled embedded shaders from llama.cpp $LLAMA_COMMIT."
