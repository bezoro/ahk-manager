#define WIN32_LEAN_AND_MEAN
#define UNICODE
#define _UNICODE
#include <windows.h>
#include <shellapi.h>
#include <string>

int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR, int) {
    int count = 0;
    wchar_t** arguments = CommandLineToArgvW(GetCommandLineW(), &count);
    if (arguments == nullptr || count < 2) return 2;
    const HANDLE output = CreateFileW(arguments[1], GENERIC_WRITE, 0, nullptr, CREATE_NEW,
                                      FILE_ATTRIBUTE_NORMAL, nullptr);
    if (output == INVALID_HANDLE_VALUE) { LocalFree(arguments); return 3; }
    std::wstring data = GetConsoleWindow() == nullptr ? L"no-console" : L"console";
    data.push_back(L'\0');
    wchar_t buffer[32768];
    DWORD length = GetCurrentDirectoryW(32768, buffer);
    if (length == 0 || length >= 32768) return 4;
    data.append(buffer, length);
    data.push_back(L'\0');
    length = GetModuleFileNameW(nullptr, buffer, 32768);
    if (length == 0 || length >= 32768) return 5;
    data.append(buffer, length);
    data.push_back(L'\0');
    for (int index = 2; index < count; ++index) {
        data.append(arguments[index]);
        data.push_back(L'\0');
    }
    DWORD written = 0;
    const DWORD bytes = static_cast<DWORD>(data.size() * sizeof(wchar_t));
    const BOOL saved = WriteFile(output, data.data(), bytes, &written, nullptr);
    CloseHandle(output);
    LocalFree(arguments);
    return saved && written == bytes ? 0 : 6;
}
