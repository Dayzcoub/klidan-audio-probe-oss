#include <windows.h>
#include <array>
#include <cstdint>
#include <string_view>

int wmain(int argc, wchar_t** argv) {
#ifdef PROBE_WORKER_STALL
    Sleep(INFINITE);
    return 0;
#else
    if (argc != 3 || std::wstring_view(argv[1]) != L"--pipe") return 2;
    const auto deadline = GetTickCount64() + 5000;
    HANDLE pipe = INVALID_HANDLE_VALUE;
    while (GetTickCount64() < deadline) {
        pipe = CreateFileW(argv[2], GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, 0, nullptr);
        if (pipe != INVALID_HANDLE_VALUE) break;
        WaitNamedPipeW(argv[2], 100);
    }
    if (pipe == INVALID_HANDLE_VALUE) return 3;
    constexpr char response[] = "{\"bootstrapVersion\":1,\"kind\":\"response\",\"correlationId\":\"00000000-0000-4000-8000-000000000000\",\"product\":\"KLIDAN_AUDIO_PROBE_WORKER\",\"protocol\":{\"major\":99,\"minor\":0},\"schemas\":{\"command\":1,\"event\":1,\"report\":1},\"workerVersion\":\"test\",\"capabilities\":[]}";
    constexpr std::uint32_t n = sizeof(response) - 1;
    const std::array<std::uint8_t, 4> header{static_cast<std::uint8_t>(n), static_cast<std::uint8_t>(n >> 8), static_cast<std::uint8_t>(n >> 16), static_cast<std::uint8_t>(n >> 24)};
    DWORD written = 0;
    const bool ok = WriteFile(pipe, header.data(), static_cast<DWORD>(header.size()), &written, nullptr) && written == header.size() && WriteFile(pipe, response, n, &written, nullptr) && written == n;
    FlushFileBuffers(pipe);
    CloseHandle(pipe);
    return ok ? 0 : 4;
#endif
}
