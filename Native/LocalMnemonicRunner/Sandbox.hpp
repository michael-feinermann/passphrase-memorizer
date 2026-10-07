#pragma once

// This policy is applied before reading stdin or any model bytes. It is also
// compiled into the standalone, non-secret sandbox probe.
#include <cerrno>
#include <csignal>
#include <cstdlib>
#include <cstring>
#include <dirent.h>
#include <fcntl.h>
#include <limits.h>
#include <netinet/in.h>
#include <mach-o/dyld.h>
#include <sandbox.h>
#include <string>
#include <sys/ptrace.h>
#include <sys/mount.h>
#include <sys/resource.h>
#include <sys/socket.h>
#include <sys/stat.h>
#include <sys/un.h>
#include <unistd.h>

namespace local_ai {
constexpr size_t kFrameLimit = 65536;
constexpr unsigned kWallSeconds = 600;
inline volatile sig_atomic_t cancelled = 0;
inline std::string runtime_directory;
inline std::string runtime_dyld_directory;
inline void cancel_handler(int) { cancelled = 1; }
inline void wipe(void * bytes, size_t length) {
    volatile unsigned char * p = static_cast<volatile unsigned char *>(bytes);
    while (length--) *p++ = 0;
}

inline bool harden_process() {
    // Refuse regular files and sockets as transports. No prompt can be read
    // from or written to a disk file or an inherited network socket.
    struct stat in {}, out {};
    if (fstat(STDIN_FILENO, &in) || fstat(STDOUT_FILENO, &out) ||
        !S_ISFIFO(in.st_mode) || !S_ISFIFO(out.st_mode)) return false;
    const int null_fd = open("/dev/null", O_WRONLY | O_CLOEXEC);
    if (null_fd < 0 || dup2(null_fd, STDERR_FILENO) < 0) return false;
    if (null_fd > STDERR_FILENO) close(null_fd);
    const int limit = getdtablesize();
    for (int fd = 3; fd < limit; ++fd) close(fd);
    const struct rlimit zero {0, 0};
    // CPU time is aggregated across worker threads.
    const struct rlimit cpu {kWallSeconds * 8, kWallSeconds * 8};
    if (setrlimit(RLIMIT_CORE, &zero) || setrlimit(RLIMIT_FSIZE, &zero) ||
        setrlimit(RLIMIT_CPU, &cpu)) return false;
    if (ptrace(PT_DENY_ATTACH, 0, nullptr, 0) != 0) return false;
    struct sigaction action {};
    action.sa_handler = cancel_handler;
    sigemptyset(&action.sa_mask);
    if (sigaction(SIGTERM, &action, nullptr) || sigaction(SIGINT, &action, nullptr) ||
        sigaction(SIGALRM, &action, nullptr)) return false;
    signal(SIGPIPE, SIG_IGN);
    alarm(kWallSeconds);
    return true;
}

inline bool canonical_model(const char * path, std::string & canonical) {
    if (!path || path[0] != '/') return false;
    struct stat supplied {};
    if (lstat(path, &supplied) || !S_ISREG(supplied.st_mode) || (supplied.st_flags & SF_DATALESS)) return false;
    char result[PATH_MAX];
    if (!realpath(path, result)) return false;
    canonical = result;
    if (canonical.find("/Library/CloudStorage/") != std::string::npos ||
        canonical.find("/Mobile Documents/") != std::string::npos) return false;
    struct statfs filesystem {};
    if (statfs(canonical.c_str(), &filesystem) || !(filesystem.f_flags & MNT_LOCAL)) return false;
    return canonical.size() > 5 && canonical.substr(canonical.size() - 5) == ".gguf";
}

inline std::string sbpl_quote(const std::string & value) {
    std::string result = "\"";
    for (unsigned char c : value) {
        if (c < 32 || c == 127) return {};
        if (c == '\\' || c == '"') result += '\\';
        result += static_cast<char>(c);
    }
    return result + "\"";
}

inline bool enter_sandbox(const std::string & model, bool gpu = false) {
    const std::string quoted = sbpl_quote(model);
    if (quoted.empty()) return false;
    char executable[PATH_MAX];
    uint32_t executable_size = sizeof(executable);
    char canonical_executable[PATH_MAX];
    if (_NSGetExecutablePath(executable, &executable_size) || !realpath(executable, canonical_executable)) return false;
    const std::string executable_path = canonical_executable;
    runtime_directory = executable_path.substr(0, executable_path.rfind('/'));
    const std::string parent_quoted = sbpl_quote(runtime_directory);
    // CF may test the dyld spelling (/var) before the canonical (/private/var)
    // directory. Both names are derived from the actual loaded image, whose
    // realpath above proves identity; argv[0] is never a policy input.
    std::string dyld_path = executable;
    if (dyld_path.empty()) return false;
    if (dyld_path.front() != '/') {
        char directory[PATH_MAX];
        if (!getcwd(directory, sizeof(directory))) return false;
        dyld_path = std::string(directory) + "/" + dyld_path;
    }
    runtime_dyld_directory = dyld_path.substr(0, dyld_path.rfind('/'));
    const std::string dyld_parent_quoted = sbpl_quote(runtime_dyld_directory);
    if (parent_quoted.empty() || dyld_parent_quoted.empty()) return false;
    std::string system_alias_metadata;
    if (dyld_path.rfind("/var/", 0) == 0 && executable_path.rfind("/private/var/", 0) == 0)
        system_alias_metadata = "(allow file-read-metadata (literal \"/var\"))\n";
    else if (dyld_path.rfind("/tmp/", 0) == 0 && executable_path.rfind("/private/tmp/", 0) == 0)
        system_alias_metadata = "(allow file-read-metadata (literal \"/tmp\"))\n";
    // CoreFoundation's Metal telemetry requires stat of its own runtime
    // directory. Directory enumeration/data and every write remain denied.
    const std::string gpu_profile = gpu ?
        "(allow iokit-open (iokit-user-client-class \"AGXDeviceUserClient\"))\n"
        "(allow mach-lookup (global-name \"com.apple.MTLCompilerService\"))\n"
        "(allow file-read-metadata (literal " + parent_quoted + ") (literal " + dyld_parent_quoted + "))\n" + system_alias_metadata : "";
    const std::string profile =
        "(version 1)\n"
        "(deny default)\n"
        "(allow sysctl-read)\n"
        "(allow process-info* (target self))\n"
        "(allow file-read* (literal " + quoted + "))\n"
        "(allow file-read* (subpath \"/System/Library\") (subpath \"/usr/lib\"))\n" + gpu_profile;
    char * error = nullptr;
#pragma clang diagnostic push
#pragma clang diagnostic ignored "-Wdeprecated-declarations"
    const int result = sandbox_init(profile.c_str(), 0, &error);
    if (error) sandbox_free_error(error);
#pragma clang diagnostic pop
    return result == 0;
}

inline bool transfer(int fd, void * data, size_t length, bool writing) {
    unsigned char * bytes = static_cast<unsigned char *>(data);
    while (length && !cancelled) {
        const ssize_t count = writing ? write(fd, bytes, length) : read(fd, bytes, length);
        if (count < 0 && errno == EINTR) continue;
        if (count <= 0) return false;
        bytes += count;
        length -= static_cast<size_t>(count);
    }
    return length == 0 && !cancelled;
}
inline bool denied_probes() {
    const auto denied = [](int result, int error) { return result == -1 && (error == EPERM || error == EACCES); };
    // Seatbelt gates connection initiation, binding and traffic, not the
    // allocation of an unconnected socket. Never confuse ECONNREFUSED with a
    // denied operation: probes must return EPERM/EACCES from the kernel.
    for (const int family : {AF_INET, AF_INET6}) {
        sockaddr_storage storage {};
        socklen_t length;
        if (family == AF_INET) {
            auto * address = reinterpret_cast<sockaddr_in *>(&storage);
            address->sin_len = sizeof(sockaddr_in); address->sin_family = AF_INET;
            address->sin_addr.s_addr = htonl(INADDR_LOOPBACK); address->sin_port = htons(9);
            length = sizeof(sockaddr_in);
        } else {
            auto * address = reinterpret_cast<sockaddr_in6 *>(&storage);
            address->sin6_len = sizeof(sockaddr_in6); address->sin6_family = AF_INET6;
            address->sin6_addr = in6addr_loopback; address->sin6_port = htons(9);
            length = sizeof(sockaddr_in6);
        }
        const int stream = socket(family, SOCK_STREAM, 0);
        if (stream < 0) { if (!denied(stream, errno)) return false; continue; }
        const int connection = connect(stream, reinterpret_cast<sockaddr *>(&storage), length);
        const int connection_error = errno;
        const int binding = bind(stream, reinterpret_cast<sockaddr *>(&storage), length);
        const int binding_error = errno;
        close(stream);
        if (!denied(connection, connection_error) || !denied(binding, binding_error)) return false;
        const int datagram = socket(family, SOCK_DGRAM, 0);
        if (datagram < 0) { if (!denied(datagram, errno)) return false; continue; }
        constexpr char public_probe[] = "sandbox-probe";
        const int sent = static_cast<int>(sendto(datagram, public_probe, sizeof(public_probe) - 1, 0,
            reinterpret_cast<sockaddr *>(&storage), length));
        const int sent_error = errno;
        close(datagram);
        if (!denied(sent, sent_error)) return false;
    }
    const int unix_socket = socket(AF_UNIX, SOCK_STREAM, 0);
    if (unix_socket >= 0) {
        sockaddr_un address {}; address.sun_family = AF_UNIX;
        constexpr char target[] = "/private/var/run/mDNSResponder";
        memcpy(address.sun_path, target, sizeof(target));
        address.sun_len = sizeof(address);
        const int result = connect(unix_socket, reinterpret_cast<sockaddr *>(&address), sizeof(address));
        const int error = errno; close(unix_socket);
        if (!denied(result, error)) return false;
    } else if (!denied(unix_socket, errno)) return false;
    const int unrelated = open("/private/etc/passwd", O_RDONLY);
    const int read_error = errno;
    if (unrelated >= 0) { close(unrelated); return false; }
    if (read_error != EPERM && read_error != EACCES) return false;
    for (const std::string * path : {&runtime_directory, &runtime_dyld_directory}) {
        errno = 0;
        DIR * directory = opendir(path->c_str());
        const int directory_error = errno;
        if (directory) {
            errno = 0;
            const dirent * entry = readdir(directory);
            const int listing_error = errno;
            closedir(directory);
            if (entry || (listing_error != EPERM && listing_error != EACCES)) return false;
        } else if (directory_error != EPERM && directory_error != EACCES) return false;
    }
    const std::string target = "/private/tmp/LocalMnemonicRunner-write-probe-" + std::to_string(getpid());
    const int writing = open(target.c_str(), O_WRONLY | O_CREAT | O_EXCL, 0600);
    const int write_error = errno;
    if (writing >= 0) { close(writing); return false; }
    return write_error == EPERM || write_error == EACCES;
}
inline bool ready() {
    char marker[4] {'M', 'S', 'A', 'I'};
    return transfer(STDOUT_FILENO, marker, sizeof(marker), true);
}
inline bool output_frame(const char * bytes, size_t size) {
    if (!size || size > kFrameLimit) return false;
    unsigned char header[4] {static_cast<unsigned char>(size),
        static_cast<unsigned char>(size >> 8), static_cast<unsigned char>(size >> 16),
        static_cast<unsigned char>(size >> 24)};
    return transfer(STDOUT_FILENO, header, 4, true) &&
        transfer(STDOUT_FILENO, const_cast<char *>(bytes), size, true);
}
}
