#include "Sandbox.hpp"
#include "llama.h"
#include "ggml-metal.h"
#include "ggml-alloc.h"
#include <algorithm>
#include <chrono>
#include <cstdio>
#include <new>
#include <sys/mman.h>
extern "C" bool local_ai_tensor_kernels_loaded;

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
        locked = mlock(data, size) == 0;
    }
    ~Buffer() { local_ai::wipe(data, size); if (locked) munlock(data, size); free(data); }
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
bool abort_compute(void *) { return local_ai::cancelled != 0; }
bool progress(float, void *) { return local_ai::cancelled == 0; }

enum class RequestedBackend { automatic, cpu, metal };
struct BackendSelection {
    ggml_backend_t warmup = nullptr;
    ggml_backend_dev_t device = nullptr;
    ~BackendSelection() { if (warmup) ggml_backend_free(warmup); llama_backend_free(); }
    bool prepare(RequestedBackend requested) {
        llama_log_set(silent_log, nullptr);
        llama_backend_init();
        if (requested == RequestedBackend::cpu) return true;
        warmup = ggml_backend_metal_init();
        if (!warmup || !ggml_backend_is_metal(warmup)) return requested == RequestedBackend::automatic;
        ggml_init_params params {ggml_graph_overhead_custom(16, false) + 8 * ggml_tensor_overhead(), nullptr, true};
        ggml_context * context = ggml_init(params);
        if (!context) return requested == RequestedBackend::automatic;
        ggml_tensor * first = ggml_new_tensor_1d(context, GGML_TYPE_F32, 8);
        ggml_tensor * second = ggml_new_tensor_1d(context, GGML_TYPE_F32, 8);
        ggml_tensor * sum = ggml_add(context, first, second);
        ggml_cgraph * graph = ggml_new_graph_custom(context, 16, false);
        ggml_build_forward_expand(graph, sum);
        ggml_backend_buffer_t buffer = ggml_backend_alloc_ctx_tensors(context, warmup);
        bool passed = false;
        if (buffer) {
            const float public_values[8] {1, 2, 3, 4, 5, 6, 7, 8};
            float result[8] {};
            ggml_backend_tensor_set(first, public_values, 0, sizeof(public_values));
            ggml_backend_tensor_set(second, public_values, 0, sizeof(public_values));
            if (ggml_backend_graph_compute(warmup, graph) == GGML_STATUS_SUCCESS) {
                ggml_backend_synchronize(warmup);
                ggml_backend_tensor_get(sum, result, 0, sizeof(result));
                passed = true;
                for (size_t i = 0; i < 8; ++i) if (result[i] != 2 * public_values[i]) passed = false;
            }
            ggml_backend_synchronize(warmup);
            ggml_backend_buffer_clear(buffer, 0);
            ggml_backend_buffer_free(buffer);
        }
        ggml_free(context);
        if (passed) {
            ggml_backend_register(ggml_backend_metal_reg());
            device = ggml_backend_get_device(warmup);
        }
        return passed || requested == RequestedBackend::automatic;
    }
};

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

int run(const std::string & path, const BackendSelection & selected) {
    const auto start = std::chrono::steady_clock::now();
    const int fd = open(path.c_str(), O_RDONLY | O_NOFOLLOW | O_CLOEXEC);
    if (fd < 0) return model_error;
    struct stat original {};
    if (fstat(fd, &original) || !S_ISREG(original.st_mode) || original.st_size < 24 ||
        (original.st_flags & SF_DATALESS) ||
        static_cast<uint64_t>(original.st_size) > (uint64_t{32} << 30)) { close(fd); return model_error; }
    struct statfs filesystem {};
    if (fstatfs(fd, &filesystem) || !(filesystem.f_flags & MNT_LOCAL)) { close(fd); return model_error; }
    FILE * file = fdopen(fd, "rb");
    if (!file) { close(fd); return model_error; }
    struct FileCloser { FILE * file; ~FileCloser() { fclose(file); } } file_closer {file};
    char magic[4];
    if (fread(magic, 1, 4, file) != 4 || memcmp(magic, "GGUF", 4) != 0 || fseek(file, 0, SEEK_SET)) return model_error;

    // All transports, core limits, ptrace protection and Seatbelt are active.
    Buffer prompt(local_ai::kFrameLimit + 1), formatted(local_ai::kFrameLimit * 2 + 1);
    Buffer token_storage((kPromptTokens + kGeneratedTokens + 1) * sizeof(llama_token));
    Buffer output(local_ai::kFrameLimit + 1), piece(local_ai::kFrameLimit + 1), grammar(16384);
    if (!prompt.locked || !formatted.locked || !token_storage.locked || !output.locked || !piece.locked || !grammar.locked) return security_error;
    unsigned char header[4];
    if (!local_ai::transfer(STDIN_FILENO, header, 4, false)) return local_ai::cancelled ? aborted : input_error;
    const uint32_t length = uint32_t(header[0]) | (uint32_t(header[1]) << 8) |
        (uint32_t(header[2]) << 16) | (uint32_t(header[3]) << 24);
    if (!length || length > local_ai::kFrameLimit ||
        !local_ai::transfer(STDIN_FILENO, prompt.data, length, false)) return local_ai::cancelled ? aborted : input_error;
    if (!valid_utf8(reinterpret_cast<unsigned char *>(prompt.data), length)) return input_error;
    // Exactly one frame. Parent must close its write end after sending it.
    unsigned char extra;
    const ssize_t extra_count = read(STDIN_FILENO, &extra, 1);
    if (extra_count != 0) return local_ai::cancelled ? aborted : input_error;

    Engine engine;
    auto model_params = llama_model_default_params();
    ggml_backend_dev_t devices[2] {selected.device, nullptr};
    model_params.devices = devices;
    model_params.n_gpu_layers = selected.device ? -1 : 0;
    model_params.load_mode = LLAMA_LOAD_MODE_NONE;
    model_params.lazy_mode = LLAMA_LAZY_MODE_OFF;
    model_params.load_mtp = false;
    model_params.progress_callback = progress;
    // Load only this already validated descriptor, never split-model siblings.
    engine.model = llama_model_load_from_file_ptr(file, model_params);
    if (!engine.model) return local_ai::cancelled ? aborted : model_error;
    const auto loaded = std::chrono::steady_clock::now();
    struct stat current {};
    if (fstat(fd, &current) || current.st_size != original.st_size ||
        current.st_mtimespec.tv_sec != original.st_mtimespec.tv_sec ||
        current.st_mtimespec.tv_nsec != original.st_mtimespec.tv_nsec) return model_error;
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
        constexpr char suffix[] = "<turn|>\n<|turn>model\n";
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

    // mlockall may be refused by macOS or process memory limits. Known secret
    // buffers remain locked; llama internal buffers require an honest caveat.
    const bool heap_locked = mlockall(MCL_CURRENT | MCL_FUTURE) == 0;
    (void) heap_locked;
    auto context_params = llama_context_default_params();
    context_params.n_ctx = kContextTokens;
    context_params.n_batch = 512;
    context_params.n_ubatch = 128;
    context_params.n_seq_max = 1;
    context_params.n_threads = static_cast<int32_t>(std::clamp<long>(sysconf(_SC_NPROCESSORS_ONLN), 1, 8));
    context_params.n_threads_batch = context_params.n_threads;
    context_params.offload_kqv = selected.device != nullptr;
    context_params.op_offload = selected.device != nullptr;
    context_params.no_perf = true;
    context_params.abort_callback = abort_compute;
    engine.context = llama_init_from_model(engine.model, context_params);
    if (!engine.context) return local_ai::cancelled ? aborted : inference_error;
    const uint32_t seed = arc4random();
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
            return local_ai::cancelled ? aborted : inference_error;
    }
    size_t output_length = 0;
    size_t generated_tokens = 0;
    const auto generation_start = std::chrono::steady_clock::now();
    bool ended = false;
    for (size_t count = 0; count < kGeneratedTokens && !local_ai::cancelled; ++count) {
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
            return local_ai::cancelled ? aborted : inference_error;
    }
    if (local_ai::cancelled) return aborted;
    // Never return silently truncated stories.
    if (!ended || !output_length || !valid_utf8(reinterpret_cast<unsigned char *>(output.data), output_length)) return inference_error;
    // Never expose thought/tool channels or interpret generated instructions.
    constexpr const char * forbidden[] = {"<|channel>", "<channel|>", "<|turn>", "<turn|>",
        "<|tool", "<tool", "<|think|>", "<start_of_turn>", "<end_of_turn>"};
    for (const char * marker : forbidden) if (strstr(output.data, marker)) return inference_error;
#ifdef LOCAL_AI_BENCHMARK
    const auto finish = std::chrono::steady_clock::now();
    const auto seconds = [](auto from, auto to) { return std::chrono::duration<double>(to - from).count(); };
    char report[512];
    const int count = snprintf(report, sizeof(report), "{\"backend\":\"%s\",\"metal_tensor_kernels\":%s,\"prompt_tokens\":%d,\"generated_tokens\":%zu,\"load_seconds\":%.6f,\"prompt_seconds\":%.6f,\"generation_seconds\":%.6f,\"run_seconds\":%.6f,\"generation_tokens_per_second\":%.6f}",
        selected.device ? "Metal" : "CPU", local_ai_tensor_kernels_loaded ? "true" : "false", token_count, generated_tokens, seconds(start, loaded),
        seconds(prompt_start, generation_start), seconds(generation_start, finish), seconds(start, finish),
        generated_tokens / seconds(generation_start, finish));
    return count > 0 && static_cast<size_t>(count) < sizeof(report) && local_ai::output_frame(report, static_cast<size_t>(count)) ? 0 : inference_error;
#else
    (void) start; (void) loaded; (void) prompt_start; (void) generation_start; (void) generated_tokens;
    return local_ai::output_frame(output.data, output_length) ? 0 : (local_ai::cancelled ? aborted : inference_error);
#endif
}
}

int main(int argc, char ** argv) {
    // No logging, environment-driven behaviour, server, child process, model
    // download, file output or persistent conversation exists.
    RequestedBackend requested = RequestedBackend::automatic;
    const char * argument = nullptr;
    if (argc == 2) argument = argv[1];
    else if (argc == 3 && (strcmp(argv[1], "--cpu") == 0 || strcmp(argv[1], "--metal") == 0)) {
        requested = strcmp(argv[1], "--cpu") == 0 ? RequestedBackend::cpu : RequestedBackend::metal;
        argument = argv[2];
    } else return usage;
    if (!local_ai::harden_process()) return security_error;
    if (strcmp(argument, "--self-test") == 0) {
        if (!local_ai::enter_sandbox("/dev/null") || !local_ai::denied_probes() || !local_ai::ready()) return security_error;
        constexpr char result[] = "{\"sandbox\":\"passed\",\"ipv4\":\"denied\",\"ipv6\":\"denied\",\"writes\":\"denied\",\"unrelated_reads\":\"denied\"}";
        return local_ai::output_frame(result, sizeof(result) - 1) ? 0 : security_error;
    }
    if (strcmp(argument, "--self-test-metal") == 0) {
        if (!local_ai::enter_sandbox("/dev/null", true) || !local_ai::denied_probes()) return security_error;
        BackendSelection selected;
        if (!selected.prepare(RequestedBackend::metal) || !selected.device || !local_ai::denied_probes() || !local_ai::ready()) return security_error;
        char result[256];
        const int size = snprintf(result, sizeof(result), "{\"sandbox\":\"passed\",\"backend\":\"Metal\",\"gpu_computation\":\"passed\",\"metal_tensor_kernels\":%s,\"ipv4\":\"denied\",\"ipv6\":\"denied\",\"writes\":\"denied\",\"unrelated_reads\":\"denied\"}", local_ai_tensor_kernels_loaded ? "true" : "false");
        return size > 0 && static_cast<size_t>(size) < sizeof(result) && local_ai::output_frame(result, static_cast<size_t>(size)) ? 0 : security_error;
    }
    std::string model;
    if (!local_ai::canonical_model(argument, model)) return model_error;
    if (!local_ai::enter_sandbox(model, requested != RequestedBackend::cpu) || !local_ai::denied_probes()) return security_error;
    BackendSelection selected;
    if (!selected.prepare(requested) || !local_ai::denied_probes() || !local_ai::ready()) return security_error;
    try { return run(model, selected); }
    catch (...) { return local_ai::cancelled ? aborted : inference_error; }
}
