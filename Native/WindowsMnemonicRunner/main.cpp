#include "Sandbox.hpp"
#include "llama.h"
#include "llama-model.h"
#include "llama-context.h"
#include "llama-ext.h"

#include "ggml-alloc.h"
#include <algorithm>
#include <chrono>
#include <cstdio>
#include <new>
#include <limits>
#include <stdexcept>



namespace {
enum Status { usage = 64, input_error = 65, model_error = 66,
              inference_error = 70, security_error = 71, aborted = 75 };
constexpr size_t kPromptTokens = 4096;
constexpr size_t kGeneratedTokens = 4096;
constexpr size_t kContextTokens = 8192;

// Locked before receiving secrets. Locking third-party allocator memory is only
// best effort; no guarantee against OS swapping/hibernation is made.
class Buffer {
public:
    explicit Buffer(size_t count) : size(count), data(static_cast<char *>(calloc(count, 1))) {
        if (!data) throw std::bad_alloc();
        locked = VirtualLock(data, size) != FALSE;
    }
    ~Buffer() { local_ai::wipe(data, size); if (locked) VirtualUnlock(data, size); free(data); }
    Buffer(const Buffer &) = delete;
    Buffer & operator=(const Buffer &) = delete;
    size_t size;
    char * data;
    bool locked = false;
};

bool valid_utf8(const unsigned char * text, size_t size) {
    size_t i = 0;
    while (i < size) {
        const unsigned char c = text[i++];
        if (c < 0x80) {
            if (c == 0 || (c < 32 && c != '\n' && c != '\r' && c != '\t')) return false;
            continue;
        }
        unsigned remaining; uint32_t value; uint32_t minimum;
        if (c >= 0xc2 && c <= 0xdf) { remaining = 1; value = c & 0x1f; minimum = 0x80; }
        else if (c >= 0xe0 && c <= 0xef) { remaining = 2; value = c & 0xf; minimum = 0x800; }
        else if (c >= 0xf0 && c <= 0xf4) { remaining = 3; value = c & 7; minimum = 0x10000; }
        else return false;
        if (i + remaining > size) return false;
        while (remaining--) {
            const unsigned char next = text[i++];
            if ((next & 0xc0) != 0x80) return false;
            value = (value << 6) | (next & 0x3f);
        }
        if (value < minimum || value > 0x10ffff || (value >= 0xd800 && value <= 0xdfff)) return false;
    }
    return true;
}
void silent_log(enum ggml_log_level, const char *, void *) {}
bool abort_compute(void *) { return local_ai::is_cancelled(); }
bool progress(float, void *) { return !local_ai::is_cancelled(); }

struct BackendSelection {
    ggml_backend_dev_t device = nullptr;
    size_t free_before = 0;
    size_t total = 0;
    ~BackendSelection() { llama_backend_free(); }
    bool prepare() {
        llama_log_set(silent_log, nullptr);
        ggml_log_set(silent_log, nullptr);
        llama_backend_init();
        // Dedicated CUDA memory only. No integrated GPU, unified-memory
        // environment switch, dynamic backend, or CPU fallback is accepted.
        if (getenv("GGML_CUDA_ENABLE_UNIFIED_MEMORY")) return false;
        for (size_t i = 0; i < ggml_backend_dev_count(); ++i) {
            auto * candidate = ggml_backend_dev_get(i);
            if (ggml_backend_dev_type(candidate) != GGML_BACKEND_DEVICE_TYPE_GPU ||
                strcmp(ggml_backend_reg_name(ggml_backend_dev_backend_reg(candidate)), "CUDA") != 0) continue;
            size_t available = 0, capacity = 0;
            ggml_backend_dev_memory(candidate, &available, &capacity);
            if (available > free_before) { device = candidate; free_before = available; total = capacity; }
        }
        return device && free_before && total && llama_supports_gpu_offload();
    }
};

bool public_gpu_compute_probe(ggml_backend_dev_t device) {
    // Fixed public tensors prove actual CUDA arithmetic before accepting input.
    ggml_backend_t backend = ggml_backend_dev_init(device, nullptr);
    if (!backend) return false;
    struct BackendCloser { ggml_backend_t value; ~BackendCloser() { ggml_backend_free(value); } } backend_closer {backend};
    const ggml_init_params params {ggml_tensor_overhead() * 4 + ggml_graph_overhead_custom(16, false), nullptr, true};
    ggml_context * context = ggml_init(params);
    if (!context) return false;
    struct ContextCloser { ggml_context * value; ~ContextCloser() { ggml_free(value); } } context_closer {context};
    auto * left = ggml_new_tensor_2d(context, GGML_TYPE_F32, 2, 2);
    auto * right = ggml_new_tensor_2d(context, GGML_TYPE_F32, 2, 2);
    auto * result = ggml_mul_mat(context, left, right);
    auto * graph = ggml_new_graph_custom(context, 16, false);
    ggml_build_forward_expand(graph, result);
    auto * buffer = ggml_backend_alloc_ctx_tensors_from_buft(context, ggml_backend_dev_buffer_type(device));
    if (!buffer) return false;
    struct BufferCloser { ggml_backend_buffer_t value; ~BufferCloser() { ggml_backend_buffer_free(value); } } buffer_closer {buffer};
    constexpr float public_left[4] {1, 2, 3, 4};
    constexpr float public_right[4] {5, 6, 7, 8};
    constexpr float expected[4] {17, 39, 23, 53};
    float actual[4] {};
    ggml_backend_tensor_set(left, public_left, 0, sizeof(public_left));
    ggml_backend_tensor_set(right, public_right, 0, sizeof(public_right));
    if (ggml_backend_graph_compute(backend, graph) != GGML_STATUS_SUCCESS) return false;
    ggml_backend_tensor_get(result, actual, 0, sizeof(actual));
    return memcmp(actual, expected, sizeof(actual)) == 0;
}

struct GpuPlacementFailure {};
struct PlacementProof {
    ggml_backend_dev_t device = nullptr;
    llama_context * context = nullptr;
    uint64_t operations = 0;
    size_t model_bytes = 0, kv_bytes = 0, compute_bytes = 0, host_compute_bytes = 0;
};
bool gpu_operation(ggml_tensor * tensor, bool ask, void * user) {
    auto & proof = *static_cast<PlacementProof *>(user);
    if (!ask) return !local_ai::is_cancelled();
    // These nodes only describe tensor shape/strides; they execute no model
    // arithmetic. Every actual inference operation must stay on CUDA.
    switch (tensor->op) {
        case GGML_OP_NONE: case GGML_OP_VIEW: case GGML_OP_RESHAPE:
        case GGML_OP_PERMUTE: case GGML_OP_TRANSPOSE: return false;
        default: break;
    }
    auto * backend = ggml_backend_sched_get_tensor_backend(proof.context->get_sched(), tensor);
    if (!backend || ggml_backend_get_device(backend) != proof.device) {
        // Fail before the scheduler dispatches a CPU model operation.
        throw GpuPlacementFailure {};
    }
    ++proof.operations;
    return false;
}
bool verify_gpu_placement(llama_model * model, llama_context * context, PlacementProof & proof) {
    // The scheduler can grow buffers for a later decode. Always query current
    // allocations; preserve only the independently counted operation total.
    proof.model_bytes = proof.kv_bytes = proof.compute_bytes = proof.host_compute_bytes = 0;
    if (llama_model_n_devices(model) != 1 || llama_model_get_device(model, 0) != proof.device ||
        model->dev_output() != proof.device) return false;
    for (int layer = 0; layer < llama_model_n_layer(model); ++layer)
        if (model->dev_layer(layer) != proof.device) return false;
    for (const auto & entry : llama_internal_get_tensor_map(model)) {
        const auto * tensor = entry.second;
        const auto * resident = tensor->view_src ? tensor->view_src : tensor;
        if (!resident->buffer || ggml_backend_buffer_is_host(resident->buffer) ||
            ggml_backend_buft_get_device(ggml_backend_buffer_get_type(resident->buffer)) != proof.device) return false;
    }
    for (const auto & entry : llama_get_memory_breakdown(context)) {
        const bool gpu = !ggml_backend_buft_is_host(entry.first) &&
            ggml_backend_buft_get_device(entry.first) == proof.device;
        const auto & bytes = entry.second;
        if (gpu) { proof.model_bytes += bytes.model; proof.kv_bytes += bytes.context; proof.compute_bytes += bytes.compute; }
        else {
            if (bytes.model || bytes.context) return false;
            proof.host_compute_bytes += bytes.compute;
        }
    }
    // A small host scheduler/input buffer is metadata, not resident weights or
    // KV. Bound it independently, and guard every model operation above.
    return proof.model_bytes && proof.kv_bytes && proof.compute_bytes && proof.host_compute_bytes <= (16u << 20);
}
bool gpu_report(const BackendSelection & selected, const PlacementProof & proof) {
    char device[256] {};
    const char * description = ggml_backend_dev_description(selected.device);
    size_t count = 0;
    for (; description[count] && count < sizeof(device) - 1; ++count) {
        const unsigned char c = static_cast<unsigned char>(description[count]);
        if (c < 32 || c > 126 || c == '"' || c == '\\') return false;
        device[count] = static_cast<char>(c);
    }
    if (description[count]) return false;
    char report[1024] {};
    const int length = snprintf(report, sizeof(report),
        "{\"backend\":\"CUDA\",\"device\":\"%s\",\"gpuModelBytes\":%llu,\"cpuModelBytes\":0,"
        "\"gpuKvBytes\":%llu,\"cpuKvBytes\":0,\"gpuComputeBytes\":%llu,\"cpuComputeBufferBytes\":%llu,"
        "\"contextTokens\":%llu,\"freeVramBeforeBytes\":%llu,\"totalVramBytes\":%llu,"
        "\"gpuOperationCount\":%llu,\"cpuModelOperationCount\":0}", device,
        static_cast<unsigned long long>(proof.model_bytes), static_cast<unsigned long long>(proof.kv_bytes),
        static_cast<unsigned long long>(proof.compute_bytes), static_cast<unsigned long long>(proof.host_compute_bytes),
        static_cast<unsigned long long>(kContextTokens), static_cast<unsigned long long>(selected.free_before),
        static_cast<unsigned long long>(selected.total), static_cast<unsigned long long>(proof.operations));
    return length > 0 && static_cast<size_t>(length) < sizeof(report) && local_ai::output_frame(report, static_cast<size_t>(length));
}
bool marker_grammar(const char * prompt, size_t length, Buffer & grammar) {
    // Only the final nonempty line carries passphrase data. The fixed public
    // instructions above can contain examples such as [word]. No grammar source
    // supplied by a caller is accepted, and words cannot inject GBNF syntax.
    size_t end = length;
    while (end && (prompt[end-1] == '\n' || prompt[end-1] == '\r' || prompt[end-1] == ' ' || prompt[end-1] == '\t')) --end;
    size_t cursor = end;
    while (cursor && prompt[cursor-1] != '\n') --cursor;
    size_t used = 0, words = 0;
    const auto append = [&](const char * bytes, size_t count) {
        if (used + count >= grammar.size) return false;
        memcpy(grammar.data + used, bytes, count); used += count; return true;
    };
    constexpr char prefix[] = "root ::= lead";
    if (!append(prefix, sizeof(prefix)-1)) return false;
    while (cursor < end) {
        while (cursor < end && (prompt[cursor] == ' ' || prompt[cursor] == '\t' || prompt[cursor] == '\r')) ++cursor;
        if (cursor == end) break;
        if (prompt[cursor++] != '[') return false;
        const size_t start = cursor;
        while (cursor < end && ((prompt[cursor] >= 'a' && prompt[cursor] <= 'z') ||
               (prompt[cursor] >= 'A' && prompt[cursor] <= 'Z'))) ++cursor;
        const size_t size = cursor - start;
        if (!size || size > 64 || cursor == end || prompt[cursor++] != ']' || ++words > 128) return false;
        if (!append(" \"[", 3) || !append(prompt + start, size) || !append("]\" gap", 6)) return false;
        if (cursor < end && prompt[cursor] != ' ' && prompt[cursor] != '\t' && prompt[cursor] != '\r') return false;
    }
    // Linear repetition avoids the large ambiguous stack fan-out of optional
    // bounded character sequences. Global token/output/time caps bound the text.
    // Disjoint prefix/letter classes make the lead linear: require an actual
    // English/German unmarked letter without branching repetition fan-out.
    constexpr char suffix[] = "\nlead ::= [^\\[\\]<>A-Za-zÄÖÜäöüß]* [A-Za-zÄÖÜäöüß] gap\ngap ::= [^\\[\\]<>]*\n";
    return words > 0 && append(suffix, sizeof(suffix)-1);
}

struct Engine {
    llama_model * model = nullptr;
    llama_context * context = nullptr;
    llama_sampler * sampler = nullptr;
    ~Engine() {
        if (context) {
            llama_synchronize(context);
            if (auto memory = llama_get_memory(context)) llama_memory_clear(memory, true);
            llama_synchronize(context);
        }
        if (sampler) { llama_sampler_reset(sampler); llama_sampler_free(sampler); }
        if (context) llama_free(context);
        if (model) llama_model_free(model);
    }
};

int run(const std::wstring & path, const BackendSelection & selected) {
    const auto start = std::chrono::steady_clock::now();
    HANDLE model_handle = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr,
        OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL | FILE_FLAG_OPEN_REPARSE_POINT, nullptr);
    if (model_handle == INVALID_HANDLE_VALUE) return model_error;
    BY_HANDLE_FILE_INFORMATION original {};
    LARGE_INTEGER size {};
    if (!GetFileInformationByHandle(model_handle, &original) ||
        (original.dwFileAttributes & (FILE_ATTRIBUTE_REPARSE_POINT | FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_OFFLINE)) ||
        !GetFileSizeEx(model_handle, &size) || size.QuadPart < 24 || size.QuadPart > (int64_t{32} << 30)) {
        CloseHandle(model_handle); return model_error;
    }
    const int fd = _open_osfhandle(reinterpret_cast<intptr_t>(model_handle), _O_RDONLY | _O_BINARY);
    if (fd < 0) { CloseHandle(model_handle); return model_error; }
    FILE * file = _fdopen(fd, "rb");
    if (!file) { _close(fd); return model_error; }
    struct FileCloser { FILE * file; ~FileCloser() { fclose(file); } } file_closer {file};
    char magic[4];
    if (fread(magic, 1, 4, file) != 4 || memcmp(magic, "GGUF", 4) != 0 || fseek(file, 0, SEEK_SET)) return model_error;
    // LPAC, the job limit and all live transport/filesystem denial probes are active.
    Buffer prompt(local_ai::kFrameLimit + 1), formatted(local_ai::kFrameLimit * 2 + 1);
    Buffer token_storage((kPromptTokens + kGeneratedTokens + 1) * sizeof(llama_token));
    Buffer output(local_ai::kFrameLimit + 1), piece(local_ai::kFrameLimit + 1), grammar(16384);
    if (!prompt.locked || !formatted.locked || !token_storage.locked || !output.locked || !piece.locked || !grammar.locked) return security_error;

    // Conservative preflight before any model allocation or secret transfer.
    // Actual model, KV and compute placement is inspected after allocation.
    constexpr size_t allocation_reserve = size_t{2} << 30;
    if (static_cast<uint64_t>(size.QuadPart) > selected.free_before ||
        selected.free_before - static_cast<size_t>(size.QuadPart) < allocation_reserve) return model_error;
    Engine engine;
    PlacementProof proof;
    proof.device = selected.device;
    auto model_params = llama_model_default_params();
    ggml_backend_dev_t devices[2] {selected.device, nullptr};
    const llama_model_tensor_buft_override overrides[] {
        {".*", ggml_backend_dev_buffer_type(selected.device)}, {nullptr, nullptr}
    };
    model_params.devices = devices;
    model_params.tensor_buft_overrides = overrides;
    model_params.n_gpu_layers = std::numeric_limits<int32_t>::max();
    model_params.split_mode = LLAMA_SPLIT_MODE_NONE;
    model_params.no_host = true;
    model_params.use_extra_bufts = false;
    model_params.load_mode = LLAMA_LOAD_MODE_NONE;
    model_params.lazy_mode = LLAMA_LAZY_MODE_OFF;
    model_params.load_mtp = false;
    model_params.progress_callback = progress;
    // Stream temporary upload staging into dedicated buffers. No mmap-backed
    // model, resident host weights, split-model siblings, or CPU fallback.
    engine.model = llama_model_load_from_file_ptr(file, model_params);
    if (!engine.model) return local_ai::is_cancelled() ? aborted : model_error;
    const auto loaded = std::chrono::steady_clock::now();
    BY_HANDLE_FILE_INFORMATION current {};
    if (!GetFileInformationByHandle(model_handle, &current) || current.nFileSizeHigh != original.nFileSizeHigh ||
        current.nFileSizeLow != original.nFileSizeLow ||
        CompareFileTime(&current.ftLastWriteTime, &original.ftLastWriteTime) != 0) return model_error;
    auto context_params = llama_context_default_params();
    context_params.n_ctx = kContextTokens;
    context_params.n_batch = 512;
    context_params.n_ubatch = 128;
    context_params.n_outputs_max = 1;
    context_params.n_outputs_max_per_seq = 1;
    context_params.n_seq_max = 1;
    context_params.n_threads = 1;
    context_params.n_threads_batch = 1;
    context_params.offload_kqv = true;
    context_params.op_offload = true;
    context_params.flash_attn_type = LLAMA_FLASH_ATTN_TYPE_ENABLED;
    context_params.type_k = GGML_TYPE_Q4_0;
    context_params.type_v = GGML_TYPE_Q4_0;
    context_params.no_perf = true;
    context_params.abort_callback = abort_compute;
    context_params.cb_eval = gpu_operation;
    context_params.cb_eval_user_data = &proof;
    engine.context = llama_init_from_model(engine.model, context_params);
    if (!engine.context) return local_ai::is_cancelled() ? aborted : inference_error;
    proof.context = engine.context;
    if (!verify_gpu_placement(engine.model, engine.context, proof)) return security_error;
    constexpr size_t safety_headroom = size_t{512} << 20;
    const size_t required_gpu = proof.model_bytes + proof.kv_bytes + proof.compute_bytes;
    if (required_gpu > selected.free_before || selected.free_before - required_gpu < safety_headroom) return model_error;
    // No passphrase reaches CUDA until its allocation and isolation checks pass.
    if (!local_ai::ready()) return security_error;
    unsigned char header[4];
    if (!local_ai::transfer(GetStdHandle(STD_INPUT_HANDLE), header, 4, false)) return local_ai::is_cancelled() ? aborted : input_error;
    const uint32_t length = uint32_t(header[0]) | (uint32_t(header[1]) << 8) |
        (uint32_t(header[2]) << 16) | (uint32_t(header[3]) << 24);
    if (!length || length > local_ai::kFrameLimit ||
        !local_ai::transfer(GetStdHandle(STD_INPUT_HANDLE), prompt.data, length, false)) return local_ai::is_cancelled() ? aborted : input_error;
    if (!valid_utf8(reinterpret_cast<unsigned char *>(prompt.data), length)) return input_error;
    // Exactly one frame. Parent must close its write end after sending it.
    unsigned char extra;
    DWORD extra_count = 0;
    const BOOL extra_result = ReadFile(GetStdHandle(STD_INPUT_HANDLE), &extra, 1, &extra_count, nullptr);
    if (extra_count != 0 || (!extra_result && GetLastError() != ERROR_BROKEN_PIPE)) return input_error;

    const llama_vocab * vocab = llama_model_get_vocab(engine.model);
    if (!vocab) return model_error;

    int32_t formatted_count;
    char architecture[64] {};
    llama_model_meta_val_str(engine.model, "general.architecture", architecture, sizeof(architecture));
    if (strcmp(architecture, "gemma4") == 0) {
        // Gemma 4 uses new tokens; the pinned lightweight llama template API
        // recognises older Gemma templates only. No Jinja runtime is needed.
        constexpr char prefix[] = "<|turn>system\nWrite only the final mnemonic requested by the user. "
            "You have no tools. Preserve every supplied [word] exactly, INCLUDING its literal square brackets, "
            "and in the supplied order. For example, [apple] must appear as [apple], never as apple. "
            "Do not translate, inflect, omit, or rearrange a marked word. "
            "Repeated words must repeat in their listed positions.<turn|>\n<|turn>user\n";
        // Canonical Gemma 4 enable_thinking=false generation prefix: close an
        // empty thought channel before sampling the final mnemonic text.
        constexpr char suffix[] = "<turn|>\n<|turn>model\n<|channel>thought\n<channel|>";
        formatted_count = static_cast<int32_t>(sizeof(prefix) - 1 + length + sizeof(suffix) - 1);
        memcpy(formatted.data, prefix, sizeof(prefix) - 1);
        memcpy(formatted.data + sizeof(prefix) - 1, prompt.data, length);
        memcpy(formatted.data + sizeof(prefix) - 1 + length, suffix, sizeof(suffix) - 1);
    } else {
        const char * model_template = llama_model_chat_template(engine.model, nullptr);
        if (!model_template) return model_error;
        const llama_chat_message message {"user", prompt.data};
        formatted_count = llama_chat_apply_template(model_template, &message, 1, true,
            formatted.data, static_cast<int32_t>(formatted.size - 1));
        if (formatted_count <= 0 || static_cast<size_t>(formatted_count) >= formatted.size) return model_error;
    }
    auto * tokens = reinterpret_cast<llama_token *>(token_storage.data);
    const int32_t token_count = llama_tokenize(vocab, formatted.data, formatted_count, tokens,
        static_cast<int32_t>(kPromptTokens), true, true);
    if (token_count <= 0 || static_cast<size_t>(token_count) > kPromptTokens) return input_error;
    if (!marker_grammar(prompt.data, length, grammar)) return input_error;
    local_ai::wipe(prompt.data, prompt.size);
    local_ai::wipe(formatted.data, formatted.size);

    uint32_t seed = 0;
    if (BCryptGenRandom(nullptr, reinterpret_cast<PUCHAR>(&seed), sizeof(seed), BCRYPT_USE_SYSTEM_PREFERRED_RNG) != 0) return security_error;
    engine.sampler = llama_sampler_chain_init(llama_sampler_chain_default_params());
    if (!engine.sampler) return inference_error;
    llama_sampler * constraints = llama_sampler_init_grammar(vocab, grammar.data, "root");
    if (!constraints) return inference_error;
    llama_sampler_chain_add(engine.sampler, constraints);
    local_ai::wipe(grammar.data, grammar.size);
#ifdef LOCAL_AI_BENCHMARK
    (void) seed;
    llama_sampler_chain_add(engine.sampler, llama_sampler_init_greedy());
#else
    llama_sampler_chain_add(engine.sampler, llama_sampler_init_top_k(64));
    llama_sampler_chain_add(engine.sampler, llama_sampler_init_top_p(0.95f, 1));
    llama_sampler_chain_add(engine.sampler, llama_sampler_init_temp(1.0f));
    llama_sampler_chain_add(engine.sampler, llama_sampler_init_dist(seed));
#endif
    const auto prompt_start = std::chrono::steady_clock::now();
    for (int32_t offset = 0; offset < token_count; offset += 512) {
        const int32_t count = std::min<int32_t>(512, token_count - offset);
        if (llama_decode(engine.context, llama_batch_get_one(tokens + offset, count)))
            return local_ai::is_cancelled() ? aborted : inference_error;
    }
    size_t output_length = 0;
    size_t generated_tokens = 0;
    const auto generation_start = std::chrono::steady_clock::now();
    bool ended = false;
    for (size_t count = 0; count < kGeneratedTokens && !local_ai::is_cancelled(); ++count) {
        const llama_token token = llama_sampler_sample(engine.sampler, engine.context, -1);
        if (llama_vocab_is_eog(vocab, token)) { ended = true; break; }
        const int32_t piece_count = llama_token_to_piece(vocab, token, piece.data,
            static_cast<int32_t>(piece.size - 1), 0, true);
        if (piece_count < 0 || output_length + static_cast<size_t>(piece_count) > local_ai::kFrameLimit) return inference_error;
        memcpy(output.data + output_length, piece.data, static_cast<size_t>(piece_count));
        output_length += static_cast<size_t>(piece_count);
        ++generated_tokens;
        local_ai::wipe(piece.data, piece.size);
        tokens[token_count + count] = token;
        if (llama_decode(engine.context, llama_batch_get_one(tokens + token_count + count, 1)))
            return local_ai::is_cancelled() ? aborted : inference_error;
    }
    if (local_ai::is_cancelled()) return aborted;
    // Never return silently truncated stories.
    if (!ended || !output_length || !valid_utf8(reinterpret_cast<unsigned char *>(output.data), output_length)) return inference_error;
    // Never expose thought/tool channels or interpret generated instructions.
    constexpr const char * forbidden[] = {"<|channel>", "<channel|>", "<|turn>", "<turn|>",
        "<|tool", "<tool", "<|think|>", "<start_of_turn>", "<end_of_turn>"};
    for (const char * marker : forbidden) if (strstr(output.data, marker)) return inference_error;
    (void) start; (void) loaded; (void) prompt_start; (void) generation_start; (void) generated_tokens;
    if (!proof.operations || !verify_gpu_placement(engine.model, engine.context, proof) ||
        !gpu_report(selected, proof)) return security_error;
    return local_ai::output_frame(output.data, output_length) ? 0 : inference_error;
}
}

int wmain(int argc, wchar_t ** argv) {
    // Arguments carry only public filesystem paths. One secret frame is accepted
    // from the inherited pipe after the parent verifies both signatures and the
    // child verifies its actual token and live IPv4/IPv6/filesystem denials.
    if (argc != 9) return usage;
    if (!local_ai::harden_process() || !local_ai::initialize_cancellation(argv[8])) return security_error;
    const bool self_test = wcscmp(argv[1], L"--self-test") == 0;
    if (!self_test && wcscmp(argv[1], L"--gpu") != 0) return usage;
    if (!local_ai::denied_probes(argv[3], argv[4], argv[2], argv[5], argv[6], argv[7])) return security_error;
    BackendSelection selected;
    if (!selected.prepare() || !public_gpu_compute_probe(selected.device) ||
        !local_ai::denied_probes(argv[3], argv[4], argv[2], argv[5], argv[6], argv[7])) return security_error;
    if (self_test) {
        if (!local_ai::ready()) return security_error;
        constexpr char result[] = "{\"sandbox\":\"passed\",\"backend\":\"CUDA\",\"ipv4\":\"denied\",\"ipv6\":\"denied\",\"loopback_ipv4\":\"denied\",\"loopback_ipv6\":\"denied\",\"writes\":\"denied\",\"unrelated_reads\":\"denied\",\"model_writes\":\"denied\",\"profile_writes\":\"denied\",\"storage_writes\":\"denied\",\"private_registry_reads\":\"denied\",\"child_processes\":\"denied\",\"capabilities\":1,\"registryReadOnly\":true,\"zeroNetworkCapabilities\":true}";
        return local_ai::output_frame(result, sizeof(result) - 1) ? 0 : security_error;
    }
    try { return run(argv[2], selected); }
    catch (...) { return inference_error; }
}
