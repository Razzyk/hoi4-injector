#include "protocol.h"
#include <tlhelp32.h>
#include <wchar.h>
#include <stdlib.h>

static uintptr_t module_base(DWORD pid, const WCHAR *name, int full_path) {
    HANDLE snapshot = INVALID_HANDLE_VALUE;
    for (int retry = 0; retry < 6; ++retry) {
        snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, pid);
        if (snapshot != INVALID_HANDLE_VALUE || GetLastError() != ERROR_BAD_LENGTH) break;
        Sleep(25);
    }
    if (snapshot == INVALID_HANDLE_VALUE) return 0;
    MODULEENTRY32W entry;
    memset(&entry, 0, sizeof(entry));
    entry.dwSize = sizeof(entry);
    uintptr_t found = 0;
    if (Module32FirstW(snapshot, &entry)) do {
        if (_wcsicmp(full_path ? entry.szExePath : entry.szModule, name) == 0) { found = (uintptr_t)entry.modBaseAddr; break; }
    } while (Module32NextW(snapshot, &entry));
    CloseHandle(snapshot);
    return found;
}
static int run_remote(HANDLE process, uintptr_t entry, const void *data, SIZE_T bytes, int result_required) {
    LPVOID remote = VirtualAllocEx(process, NULL, bytes, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    if (!remote) return 0;
    SIZE_T written;
    if (!WriteProcessMemory(process, remote, data, bytes, &written) || written != bytes) { VirtualFreeEx(process, remote, 0, MEM_RELEASE); return 0; }
    HANDLE thread = CreateRemoteThread(process, NULL, 0, (LPTHREAD_START_ROUTINE)entry, remote, 0, NULL);
    if (!thread) { VirtualFreeEx(process, remote, 0, MEM_RELEASE); return 0; }
    DWORD wait = WaitForSingleObject(thread, 15000), result = 1;
    int ok = wait == WAIT_OBJECT_0;
    if (ok) {
        if (result_required) ok = GetExitCodeThread(thread, &result) && (result == 0 || result == ERROR_ALREADY_EXISTS);
        VirtualFreeEx(process, remote, 0, MEM_RELEASE);
    } else {
        // The target may still read these arguments. Retain this small allocation
        // until target exit, refuse success, and never terminate a target thread.
        fputs("Remote initialization timed out; arguments retained until game exit.\n", stderr);
    }
    CloseHandle(thread);
    return ok;
}
static int read_ascii(const WCHAR *ini, const WCHAR *key, char *out, int size) {
    WCHAR text[512];
    DWORD length = GetPrivateProfileStringW(L"admission", key, L"", text, 512, ini);
    if (!length || length >= 511) return 0;
    for (DWORD i = 0; i < length; ++i) if (!(text[i] >= 33 && text[i] <= 126) || text[i] == L'|') return 0;
    return WideCharToMultiByte(CP_UTF8, 0, text, -1, out, size, NULL, NULL) > 0;
}

int wmain(int argc, WCHAR **argv) {
    if (argc != 5) { fputs("Usage: AdmissionLoader.exe PID CREATION_FILETIME ABSOLUTE_DLL ABSOLUTE_BOOTSTRAP_INI\n", stderr); return 2; }
    DWORD pid = (DWORD)wcstoul(argv[1], NULL, 10);
    unsigned long long expected_creation = wcstoull(argv[2], NULL, 10);
    WCHAR dll[32768], exe[32768];
    if (!pid || !expected_creation || !GetFullPathNameW(argv[3], 32768, dll, NULL) || wcslen(dll) >= 259) return 3;
    AdmissionConfig start;
    memset(&start, 0, sizeof(start));
    start.magic = ADMISSION_MAGIC; start.bytes = sizeof(start); start.protocol = ADMISSION_PROTOCOL; start.heartbeat_ms = 2000;
    char secret[65], expected_dll_hash[65], actual_dll_hash[65];
    if (!read_ascii(argv[4], L"Session", start.session, sizeof(start.session)) || !read_ascii(argv[4], L"Player", start.player, sizeof(start.player)) ||
        !read_ascii(argv[4], L"Secret", secret, sizeof(secret)) || !hex_decode(secret, start.secret, 32) ||
        !read_ascii(argv[4], L"DllHash", expected_dll_hash, sizeof(expected_dll_hash)) || !sha_file(dll, actual_dll_hash) || strcmp(expected_dll_hash, actual_dll_hash)) return 4;
    if (!GetPrivateProfileStringW(L"admission", L"Pipe", L"", start.pipe_path, 256, argv[4]) ||
        !GetPrivateProfileStringW(L"admission", L"Manifest", L"", start.manifest_path, 1024, argv[4]) ||
        !GetPrivateProfileStringW(L"admission", L"Log", L"", start.log_path, 1024, argv[4])) return 5;
    HANDLE process = OpenProcess(PROCESS_CREATE_THREAD | PROCESS_QUERY_INFORMATION | PROCESS_VM_OPERATION | PROCESS_VM_WRITE | PROCESS_VM_READ, FALSE, pid);
    if (!process) { fprintf(stderr, "Cannot open game: Win32 error %lu\n", GetLastError()); return 6; }
    DWORD capacity = 32768;
    FILETIME created, ended, kernel, user;
    ULARGE_INTEGER creation;
    int success = 0;
    HMODULE local_dll = NULL;
    if (!QueryFullProcessImageNameW(process, 0, exe, &capacity) || !GetProcessTimes(process, &created, &ended, &kernel, &user)) goto done;
    creation.LowPart = created.dwLowDateTime; creation.HighPart = created.dwHighDateTime;
    if (creation.QuadPart != expected_creation) { fputs("Game identity changed; refused.\n", stderr); goto done; }
    const WCHAR *basename = wcsrchr(exe, L'\\');
    if (!basename || _wcsicmp(basename + 1, L"hoi4.exe")) goto done;
    char game_hash[65];
    if (!sha_file(exe, game_hash) || strcmp(game_hash, GAME_SHA256)) { fputs("Unsupported game SHA256; refused.\n", stderr); goto done; }
    local_dll = LoadLibraryExW(dll, NULL, DONT_RESOLVE_DLL_REFERENCES);
    if (!local_dll) goto done;
    IMAGE_DOS_HEADER *dos = (IMAGE_DOS_HEADER *)local_dll;
    IMAGE_NT_HEADERS64 *nt = (IMAGE_NT_HEADERS64 *)((BYTE *)local_dll + dos->e_lfanew);
    FARPROC start_export = GetProcAddress(local_dll, "AdmissionStart");
    if (nt->FileHeader.Machine != IMAGE_FILE_MACHINE_AMD64 || nt->OptionalHeader.Magic != IMAGE_NT_OPTIONAL_HDR64_MAGIC || !start_export) goto done;
    uintptr_t start_rva = (uintptr_t)start_export - (uintptr_t)local_dll;
    if (start_rva >= nt->OptionalHeader.SizeOfImage) goto done;
    uintptr_t remote_dll = module_base(pid, dll, 1);
    if (!remote_dll) {
        FARPROC load = GetProcAddress(GetModuleHandleW(L"kernel32.dll"), "LoadLibraryW");
        HMODULE owner;
        WCHAR owner_path[32768];
        if (!load || !GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, (LPCWSTR)load, &owner) || !GetModuleFileNameW(owner, owner_path, 32768)) goto done;
        const WCHAR *owner_name = wcsrchr(owner_path, L'\\');
        uintptr_t remote_owner = owner_name ? module_base(pid, owner_name + 1, 0) : 0;
        if (!remote_owner || !run_remote(process, remote_owner + ((uintptr_t)load - (uintptr_t)owner), dll, (wcslen(dll) + 1) * sizeof(WCHAR), 0)) goto done;
        // Module enumeration confirms the complete 64-bit module identity.
        remote_dll = module_base(pid, dll, 1);
    }
    if (!remote_dll || !run_remote(process, remote_dll + start_rva, &start, sizeof(start), 1)) goto done;
    printf("{\"pid\":%lu,\"loaded\":true,\"worker_started\":true}\n", pid);
    success = 1;
done:
    SecureZeroMemory(&start, sizeof(start)); SecureZeroMemory(secret, sizeof(secret));
    if (local_dll) FreeLibrary(local_dll);
    CloseHandle(process);
    if (!success) fprintf(stderr, "Loading or initialization refused/failed: Win32 error %lu\n", GetLastError());
    return success ? 0 : 1;
}
