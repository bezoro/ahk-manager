#define WIN32_LEAN_AND_MEAN
#ifndef _WIN32_WINNT
#define _WIN32_WINNT 0x0A00
#endif
#ifndef UNICODE
#define UNICODE
#endif
#ifndef _UNICODE
#define _UNICODE
#endif
#include <windows.h>
#include <shellapi.h>
#include <strsafe.h>
#include <cwchar>

namespace {
constexpr size_t maximumPath = 32768;
wchar_t image[maximumPath];
wchar_t target[maximumPath];
wchar_t command[maximumPath];

enum class Failure { Package, Access, FileSystem, Redirected, Cloud, Other };

Failure FileError() {
    switch (GetLastError()) {
        case ERROR_ACCESS_DENIED: return Failure::Access;
        case ERROR_FILE_NOT_FOUND:
        case ERROR_PATH_NOT_FOUND: return Failure::Package;
        default: return Failure::Other;
    }
}

bool CheckEntry(const wchar_t* candidate, bool directory, Failure& failure) {
    wchar_t extended[maximumPath];
    if (FAILED(StringCchPrintfW(extended, maximumPath, L"\\\\?\\%s", candidate))) {
        failure = Failure::Package;
        return false;
    }
    HANDLE handle = CreateFileW(extended, FILE_READ_ATTRIBUTES,
                                FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                                nullptr, OPEN_EXISTING,
                                FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_BACKUP_SEMANTICS,
                                nullptr);
    if (handle == INVALID_HANDLE_VALUE) {
        failure = FileError();
        return false;
    }
    FILE_ATTRIBUTE_TAG_INFO information{};
    const BOOL read = GetFileInformationByHandleEx(handle, FileAttributeTagInfo,
                                                  &information, sizeof(information));
    if (!read) failure = FileError();
    CloseHandle(handle);
    if (!read) return false;
    const DWORD attributes = information.FileAttributes;
    if ((attributes & FILE_ATTRIBUTE_DEVICE) != 0 ||
        ((attributes & FILE_ATTRIBUTE_DIRECTORY) != 0) != directory) {
        failure = Failure::Package;
        return false;
    }
    // Cloud Files tags describe local provider metadata, not a name surrogate.
    // This matches WindowsStoragePath; unknown tags, junctions and symlinks fail.
    if ((attributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0 &&
        (information.ReparseTag & ~0xF000UL) != 0x9000001AUL) {
        failure = Failure::Redirected;
        return false;
    }
    if (!directory && (attributes & (0x1000UL | 0x400000UL)) != 0) {
        failure = Failure::Cloud;
        return false;
    }
    return true;
}

bool CheckAncestors(Failure& failure) {
    wchar_t current[maximumPath];
    if (FAILED(StringCchCopyW(current, maximumPath, image))) return false;
    for (;;) {
        if (!CheckEntry(current, true, failure)) return false;
        if (wcslen(current) == 3) break;
        wchar_t* separator = wcsrchr(current, L'\\');
        if (separator == nullptr) return false;
        if (separator == current + 2) separator[1] = L'\0';
        else *separator = L'\0';
    }
    wchar_t root[] = { image[0], L':', L'\\', L'\0' };
    wchar_t fileSystem[64];
    if (!GetVolumeInformationW(root, nullptr, 0, nullptr, nullptr, nullptr,
                               fileSystem, ARRAYSIZE(fileSystem))) {
        failure = FileError();
        return false;
    }
    if (_wcsicmp(fileSystem, L"NTFS") != 0) {
        failure = Failure::FileSystem;
        return false;
    }
    return true;
}

const wchar_t* ArgumentTail(const wchar_t* value) {
    while (*value == L' ' || *value == L'\t') ++value;
    bool quoted = false;
    while (*value) {
        if (*value == L'"') quoted = !quoted;
        else if (!quoted && (*value == L' ' || *value == L'\t')) break;
        ++value;
    }
    while (*value == L' ' || *value == L'\t') ++value;
    return value;
}

bool Sibling(const wchar_t* name, bool directory, Failure& failure) {
    wchar_t candidate[maximumPath];
    if (FAILED(StringCchPrintfW(candidate, maximumPath, L"%s%s%s", image,
                               image[wcslen(image) - 1] == L'\\' ? L"" : L"\\", name)))
        return false;
    return CheckEntry(candidate, directory, failure);
}

bool QuietStartup() {
    int count = 0;
    wchar_t** arguments = CommandLineToArgvW(GetCommandLineW(), &count);
    if (arguments == nullptr) return false;
    bool quiet = false;
    bool explicitRequest = false;
    for (int index = 1; index < count; ++index) {
        if (wcscmp(arguments[index], L"--login") == 0 ||
            wcscmp(arguments[index], L"--background") == 0)
            quiet = true;
        if (wcscmp(arguments[index], L"--foreground") == 0 ||
            wcscmp(arguments[index], L"--request-administrator") == 0)
            explicitRequest = true;
    }
    LocalFree(arguments);
    return quiet && !explicitRequest;
}

int Fail(Failure failure = Failure::Package) {
    if (QuietStartup()) return 1;
    const wchar_t* message;
    switch (failure) {
        case Failure::FileSystem:
            message = L"AHK Manager Portable requires NTFS. ReFS, exFAT and FAT are not "
                      L"supported. Extract the complete portable ZIP into its own folder "
                      L"on a local NTFS drive and keep its files available on this device.";
            break;
        case Failure::Cloud:
            message = L"Some AHK Manager files are not available on this device. Make the "
                      L"complete portable folder available offline in your sync app and "
                      L"wait for the download to finish before opening Manager.";
            break;
        case Failure::Redirected:
            message = L"The portable folder contains a junction, symbolic link or unsupported "
                      L"redirected path. Use a regular local NTFS folder with files "
                      L"available offline.";
            break;
        case Failure::Access:
            message = L"AHK Manager cannot access its portable folder. Check your account's "
                      L"read and write permissions for the application and Data folders.";
            break;
        case Failure::Other:
            message = L"AHK Manager could not open its portable files. Close applications "
                      L"using this folder, check that the drive is available, and try again.";
            break;
        default:
            message = L"The portable package is incomplete or has an invalid layout. "
                      L"Extract the complete ZIP into its own folder and keep the root "
                      L"launcher, portable.json, portable-package.json and App folder together.";
            break;
    }
    MessageBoxW(nullptr, message, L"AHK Manager", MB_OK | MB_ICONERROR);
    return 1;
}
}

int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR, int) {
    const DWORD length = GetModuleFileNameW(nullptr, image, static_cast<DWORD>(maximumPath));
    if (length == 0 || length >= maximumPath) return Fail();
    wchar_t* separator = wcsrchr(image, L'\\');
    if (separator == nullptr) return Fail();
    // Retain the volume separator when the launcher is placed at a drive root.
    if (separator == image + 2) separator[1] = L'\0';
    else *separator = L'\0';
    if (!((image[0] >= L'A' && image[0] <= L'Z') ||
          (image[0] >= L'a' && image[0] <= L'z')) || image[1] != L':' || image[2] != L'\\')
        return Fail(Failure::Redirected);
    Failure failure = Failure::Package;
    if (!CheckAncestors(failure) || !Sibling(L"portable.json", false, failure) ||
        !Sibling(L"portable-package.json", false, failure) || !Sibling(L"App", true, failure))
        return Fail(failure);
    if (FAILED(StringCchPrintfW(target, maximumPath, L"%s%sApp\\Bezoro.AhkManager.exe",
                               image, image[wcslen(image) - 1] == L'\\' ? L"" : L"\\")))
        return Fail();
    if (!CheckEntry(target, false, failure)) return Fail(failure);
    if (FAILED(StringCchPrintfW(command, maximumPath, L"\"%s\" %s", target,
                               ArgumentTail(GetCommandLineW()))))
        return Fail();
    STARTUPINFOW startup{};
    startup.cb = sizeof(startup);
    PROCESS_INFORMATION child{};
    if (!CreateProcessW(target, command, nullptr, nullptr, FALSE, CREATE_UNICODE_ENVIRONMENT,
                        nullptr, image, &startup, &child))
        return Fail(FileError());
    CloseHandle(child.hThread);
    CloseHandle(child.hProcess);
    return 0;
}
