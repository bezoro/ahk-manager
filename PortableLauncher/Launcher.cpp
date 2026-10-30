#define WIN32_LEAN_AND_MEAN
#define UNICODE
#define _UNICODE
#include <windows.h>
#include <strsafe.h>
#include <cwchar>

namespace {
constexpr size_t maximumPath = 32768;
wchar_t image[maximumPath];
wchar_t target[maximumPath];
wchar_t command[maximumPath];

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

bool RegularSibling(const wchar_t* name) {
    wchar_t candidate[maximumPath];
    if (FAILED(StringCchPrintfW(candidate, maximumPath, L"%s%s%s", image,
                               image[wcslen(image) - 1] == L'\\' ? L"" : L"\\", name)))
        return false;
    const DWORD attributes = GetFileAttributesW(candidate);
    return attributes != INVALID_FILE_ATTRIBUTES &&
           (attributes & (FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_REPARSE_POINT |
                          FILE_ATTRIBUTE_DEVICE)) == 0;
}

int Fail() {
    MessageBoxW(nullptr,
                L"AHK Manager could not start. Keep the portable launcher, portable.json, "
                L"and App folder together, then try again.",
                L"AHK Manager", MB_OK | MB_ICONERROR);
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
    if (!RegularSibling(L"portable.json") || !RegularSibling(L"portable-package.json"))
        return Fail();
    if (FAILED(StringCchPrintfW(target, maximumPath, L"%s%sApp\\Bezoro.AhkManager.exe",
                               image, image[wcslen(image) - 1] == L'\\' ? L"" : L"\\")))
        return Fail();
    if (FAILED(StringCchPrintfW(command, maximumPath, L"\"%s\" %s", target,
                               ArgumentTail(GetCommandLineW()))))
        return Fail();
    STARTUPINFOW startup{};
    startup.cb = sizeof(startup);
    PROCESS_INFORMATION child{};
    if (!CreateProcessW(target, command, nullptr, nullptr, FALSE, CREATE_UNICODE_ENVIRONMENT,
                        nullptr, image, &startup, &child))
        return Fail();
    CloseHandle(child.hThread);
    CloseHandle(child.hProcess);
    return 0;
}
