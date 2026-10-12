#pragma once
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#include <bcrypt.h>
#include <sddl.h>
#include <io.h>
#include <fcntl.h>
#include <cstdio>
#include <cstdint>
#include <cstdlib>
#include <cstring>
#include <string>

namespace local_ai {
constexpr size_t kFrameLimit = 65536;
inline HANDLE cancellation_event = nullptr;
inline bool is_cancelled() { return cancellation_event && WaitForSingleObject(cancellation_event, 0) != WAIT_TIMEOUT; }
inline bool initialize_cancellation(const wchar_t * value) {
    if (!value || !*value) return false;
    for (const wchar_t * cursor = value; *cursor; ++cursor) if (*cursor < L'0' || *cursor > L'9') return false;
    wchar_t * end = nullptr;
    const unsigned long long number = wcstoull(value, &end, 10);
    if (!number || !end || *end || number > static_cast<unsigned long long>(UINTPTR_MAX)) return false;
    cancellation_event = reinterpret_cast<HANDLE>(static_cast<uintptr_t>(number));
    DWORD flags = 0;
    if (!GetHandleInformation(cancellation_event, &flags) || !(flags & HANDLE_FLAG_INHERIT) ||
        WaitForSingleObject(cancellation_event, 0) != WAIT_TIMEOUT) return false;
    // The inherited duplicate is synchronization-only. A compromised child
    // cannot clear, replace or signal the parent's cancellation state.
    if (SetEvent(cancellation_event)) return false;
    return GetLastError() == ERROR_ACCESS_DENIED;
}
inline void wipe(void * data, size_t size) { SecureZeroMemory(data, size); }
inline bool transfer(HANDLE pipe, void * bytes, size_t size, bool writing) {
    auto * cursor = static_cast<unsigned char *>(bytes);
    while (size) {
        if (is_cancelled()) return false;
        DWORD count = 0;
        const DWORD request = static_cast<DWORD>(size);
        const BOOL result = writing ? WriteFile(pipe, cursor, request, &count, nullptr)
                                    : ReadFile(pipe, cursor, request, &count, nullptr);
        if (!result || !count) return false;
        cursor += count; size -= count;
    }
    return true;
}
inline bool output_frame(const void * bytes, size_t size) {
    const uint32_t length = static_cast<uint32_t>(size);
    unsigned char header[4] {static_cast<unsigned char>(length), static_cast<unsigned char>(length >> 8),
                            static_cast<unsigned char>(length >> 16), static_cast<unsigned char>(length >> 24)};
    return size && size <= kFrameLimit && transfer(GetStdHandle(STD_OUTPUT_HANDLE), header, 4, true) &&
        transfer(GetStdHandle(STD_OUTPUT_HANDLE), const_cast<void *>(bytes), size, true);
}
inline bool ready() {
    char marker[] = "MSAI";
    return transfer(GetStdHandle(STD_OUTPUT_HANDLE), marker, 4, true);
}
inline bool lpac_access_semantics(HANDLE token) {
    // Class 46 (TokenIsLessPrivilegedAppContainer) has no complete documented
    // GetTokenInformation contract and returns ERROR_INVALID_PARAMETER on the
    // tested Windows build. Test LPAC's documented access semantics instead:
    // ALL APPLICATION PACKAGES must grant nothing, while ALL RESTRICTED
    // APPLICATION PACKAGES grants bit 2. Everyone grants the user-side bits.
    PSECURITY_DESCRIPTOR descriptor = nullptr;
    if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(
        L"O:SYG:SYD:(A;;0x3;;;WD)(A;;0x1;;;AC)(A;;0x2;;;S-1-15-2-2)", SDDL_REVISION_1, &descriptor, nullptr)) return false;
    HANDLE impersonation = nullptr;
    bool valid = false;
    if (DuplicateToken(token, SecurityImpersonation, &impersonation)) {
        GENERIC_MAPPING mapping {1, 2, 0, 3};
        PRIVILEGE_SET privileges {};
        DWORD privilege_size = sizeof(privileges), granted = 0;
        BOOL allowed = FALSE;
        valid = AccessCheck(descriptor, impersonation, MAXIMUM_ALLOWED, &mapping, &privileges, &privilege_size, &granted, &allowed)
            && allowed && granted == 2;
        CloseHandle(impersonation);
    }
    LocalFree(descriptor);
    return valid;
}
inline bool harden_process() {
    SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX | SEM_NOOPENFILEERRORBOX);
    // Never emit a prompt, model metadata, third-party diagnostic or crash UI.
    _close(_fileno(stderr));
    HANDLE token = nullptr;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY | TOKEN_DUPLICATE, &token)) return false;
    DWORD container = 0, used = 0;
    const bool app_container = GetTokenInformation(token, TokenIsAppContainer, &container, sizeof(container), &used) && container;
    DWORD needed = 0;
    GetTokenInformation(token, TokenCapabilities, nullptr, 0, &needed);
    auto * capabilities = static_cast<TOKEN_GROUPS *>(calloc(needed, 1));
    PSID registry_read = nullptr;
    const bool known_sid = ConvertStringSidToSidW(L"S-1-15-3-1024-1065365936-1281604716-3511738428-1654721687-432734479-3232135806-4053264122-3456934681", &registry_read) != FALSE;
    const bool only_registry_read = known_sid && capabilities && GetTokenInformation(token, TokenCapabilities, capabilities, needed, &used)
        && capabilities->GroupCount == 1 && EqualSid(capabilities->Groups[0].Sid, registry_read)
        && (capabilities->Groups[0].Attributes & SE_GROUP_ENABLED) && !(capabilities->Groups[0].Attributes & SE_GROUP_USE_FOR_DENY_ONLY);
    const bool lpac = lpac_access_semantics(token);
    if (registry_read) LocalFree(registry_read);
    free(capabilities); CloseHandle(token);
    if (!app_container || !lpac || !only_registry_read) return false;
    // Known secret buffers are locked before READY. Internal llama allocations
    // are wiped best effort; this does not promise protection against hibernation.
    return SetProcessWorkingSetSize(GetCurrentProcess(), 16 * 1024 * 1024, 256 * 1024 * 1024) != FALSE;
}
inline bool file_denied(const wchar_t * path, DWORD access, DWORD disposition) {
    HANDLE file = CreateFileW(path, access, FILE_SHARE_READ, nullptr, disposition, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file != INVALID_HANDLE_VALUE) { CloseHandle(file); return false; }
    return GetLastError() == ERROR_ACCESS_DENIED;
}
inline bool network_denied(int family, bool loopback) {
    SOCKET socket_handle = socket(family, SOCK_STREAM, IPPROTO_TCP);
    if (socket_handle == INVALID_SOCKET) return WSAGetLastError() == WSAEACCES;
    u_long blocking = 1; ioctlsocket(socket_handle, FIONBIO, &blocking);
    int result;
    if (family == AF_INET) {
        sockaddr_in address {}; address.sin_family = AF_INET; address.sin_port = htons(443);
        InetPtonW(AF_INET, loopback ? L"127.0.0.1" : L"1.1.1.1", &address.sin_addr);
        result = connect(socket_handle, reinterpret_cast<sockaddr *>(&address), sizeof(address));
    } else {
        sockaddr_in6 address {}; address.sin6_family = AF_INET6; address.sin6_port = htons(443);
        InetPtonW(AF_INET6, loopback ? L"::1" : L"2606:4700:4700::1111", &address.sin6_addr);
        result = connect(socket_handle, reinterpret_cast<sockaddr *>(&address), sizeof(address));
    }
    const int error = WSAGetLastError();
    bool denied = result == SOCKET_ERROR && error == WSAEACCES;
    if (result == SOCKET_ERROR && error == WSAEWOULDBLOCK) {
        fd_set writable, exceptional; FD_ZERO(&writable); FD_ZERO(&exceptional);
        FD_SET(socket_handle, &writable); FD_SET(socket_handle, &exceptional);
        timeval timeout {3, 0};
        if (select(0, nullptr, &writable, &exceptional, &timeout) > 0) {
            int completed_error = 0, length = sizeof(completed_error);
            denied = getsockopt(socket_handle, SOL_SOCKET, SO_ERROR, reinterpret_cast<char *>(&completed_error), &length) == 0 && completed_error == WSAEACCES;
        }
    }
    closesocket(socket_handle);
    return denied;
}
inline bool registry_denied(const wchar_t * path, REGSAM access) {
    HKEY key = nullptr;
    const LSTATUS result = RegOpenKeyExW(HKEY_USERS, path, 0, access, &key);
    if (key) RegCloseKey(key);
    return result == ERROR_ACCESS_DENIED;
}
inline bool child_process_denied() {
    wchar_t executable[32768] {};
    const DWORD count = GetModuleFileNameW(nullptr, executable, 32768);
    if (!count || count == 32768) return false;
    STARTUPINFOW startup {}; startup.cb = sizeof(startup);
    PROCESS_INFORMATION process {};
    wchar_t command[] = L"\"public-child-probe\"";
    if (CreateProcessW(executable, command, nullptr, nullptr, FALSE, CREATE_SUSPENDED | CREATE_NO_WINDOW, nullptr, nullptr, &startup, &process)) {
        TerminateProcess(process.hProcess, 75); WaitForSingleObject(process.hProcess, 2000);
        CloseHandle(process.hThread); CloseHandle(process.hProcess); return false;
    }
    const DWORD error = GetLastError();
    return error == ERROR_ACCESS_DENIED || error == ERROR_NOT_ENOUGH_QUOTA;
}
inline bool denied_probes(const wchar_t * read_probe, const wchar_t * write_probe, const wchar_t * model,
                         const wchar_t * profile_write, const wchar_t * storage, const wchar_t * private_registry) {
    WSADATA data {};
    if (WSAStartup(MAKEWORD(2, 2), &data)) return false;
    const bool network = network_denied(AF_INET, false) && network_denied(AF_INET6, false) &&
        network_denied(AF_INET, true) && network_denied(AF_INET6, true);
    WSACleanup();
    return network && file_denied(read_probe, GENERIC_READ, OPEN_EXISTING) &&
        file_denied(write_probe, GENERIC_WRITE, CREATE_NEW) &&
        file_denied(model, GENERIC_WRITE, OPEN_EXISTING) &&
        file_denied(profile_write, GENERIC_WRITE, CREATE_NEW) &&
        registry_denied(storage, KEY_WRITE) && registry_denied(private_registry, KEY_READ) && child_process_denied();
}
}
