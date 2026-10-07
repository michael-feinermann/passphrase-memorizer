#include "Sandbox.hpp"
#include <spawn.h>

int main(int argc, char ** argv) {
    const bool gpu = argc == 3 && strcmp(argv[1], "--metal") == 0;
    if ((!gpu && argc != 2) || !local_ai::harden_process()) return 71;
    std::string model;
    if (!local_ai::canonical_model(argv[gpu ? 2 : 1], model) || !local_ai::enter_sandbox(model, gpu)) return 71;
    if (!local_ai::denied_probes()) return 71;
    // This binary has no llama.cpp code. It independently exercises the exact
    // shared production policy with public fixture paths, never secret data.
    const int fixture = open(model.c_str(), O_RDONLY | O_NOFOLLOW);
    if (fixture < 0) return 71;
    char bytes[4];
    const bool model_read = read(fixture, bytes, sizeof(bytes)) == 4;
    close(fixture);
    if (!model_read) return 71;
    pid_t child = 0;
    char command[] = "/usr/bin/true";
    char * args[] {command, nullptr};
    char * env[] {nullptr};
    const int spawn_result = posix_spawn(&child, command, nullptr, nullptr, args, env);
    if (spawn_result != EPERM && spawn_result != EACCES) return 71;
    errno = 0;
    const pid_t fork_result = fork();
    const int fork_error = errno;
    if (fork_result >= 0) _exit(71);
    if (fork_error != EPERM && fork_error != EACCES) return 71;
    if (!local_ai::ready()) return 71;
    constexpr char result[] = "{\"sandbox\":\"passed\",\"ipv4\":\"denied\",\"ipv6\":\"denied\",\"writes\":\"denied\",\"unrelated_reads\":\"denied\",\"model_read\":\"allowed\",\"child_exec\":\"denied\",\"fork\":\"denied\"}";
    return local_ai::output_frame(result, sizeof(result) - 1) ? 0 : 71;
}
