#pragma once

#include <windows.h>
#include <objbase.h>

#include <algorithm>
#include <iterator>
#include <string>
#include <string_view>

namespace WasapiEventDiagnostic {

inline bool WriteNewJson(const std::wstring& destination, std::string_view json, DWORD& error) {
    GUID id{};
    if (FAILED(CoCreateGuid(&id))) { error = ERROR_GEN_FAILURE; return false; }
    wchar_t suffix[64]{};
    if (StringFromGUID2(id, suffix, static_cast<int>(std::size(suffix))) == 0) { error = ERROR_GEN_FAILURE; return false; }
    std::wstring temporary = destination + L"." + suffix + L".tmp";
    HANDLE file = CreateFileW(temporary.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW,
                              FILE_ATTRIBUTE_NORMAL | FILE_FLAG_WRITE_THROUGH, nullptr);
    if (file == INVALID_HANDLE_VALUE) { error = GetLastError(); return false; }
    bool ok = true;
    std::size_t offset = 0;
    while (offset < json.size()) {
        const DWORD chunk = static_cast<DWORD>(std::min<std::size_t>(json.size() - offset, MAXDWORD));
        DWORD written = 0;
        if (!WriteFile(file, json.data() + offset, chunk, &written, nullptr) || written != chunk) {
            error = GetLastError(); ok = false; break;
        }
        offset += written;
    }
    if (ok && !FlushFileBuffers(file)) { error = GetLastError(); ok = false; }
    const bool closed = CloseHandle(file) != FALSE;
    if (!closed && ok) { error = GetLastError(); ok = false; }
    if (ok && !MoveFileExW(temporary.c_str(), destination.c_str(), MOVEFILE_WRITE_THROUGH)) {
        error = GetLastError(); ok = false;
    }
    if (!ok) DeleteFileW(temporary.c_str());
    else error = ERROR_SUCCESS;
    return ok;
}

} // namespace WasapiEventDiagnostic
