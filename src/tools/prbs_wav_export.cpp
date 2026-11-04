#include "../probe/analog_loopback.h"

#include <Windows.h>

#include <array>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <vector>

namespace {
constexpr std::uint32_t kRate = 48'000;
constexpr std::uint32_t kFrames = 240'000;
constexpr std::uint32_t kBytes = kFrames * sizeof(std::int16_t);

void Put16(std::array<std::uint8_t, 44>& header, std::size_t offset, std::uint16_t value) {
    header[offset] = static_cast<std::uint8_t>(value);
    header[offset + 1] = static_cast<std::uint8_t>(value >> 8);
}
void Put32(std::array<std::uint8_t, 44>& header, std::size_t offset, std::uint32_t value) {
    for (unsigned i = 0; i < 4; ++i) header[offset + i] = static_cast<std::uint8_t>(value >> (i * 8));
}
}

int wmain(int argc, wchar_t** argv) {
    if (argc != 2 || argv[1][0] == L'\0') {
        std::fwprintf(stderr, L"Usage: KlidanPrbsWavExport.exe <new-output.wav>\n");
        return 2;
    }

    AnalogLoopback::RenderSignal reference(1);
    if (!reference.Configure(kRate, 1, 0, 5)) return 3;
    std::vector<std::int16_t> stereo(static_cast<std::size_t>(kFrames) * 2);
    reference.Fill(stereo.data(), kFrames, 0);
    std::vector<std::int16_t> mono(kFrames);
    for (std::size_t i = 0; i < mono.size(); ++i) mono[i] = stereo[i * 2];
    std::vector<std::uint8_t> pcm(kBytes);
    for (std::size_t i = 0; i < mono.size(); ++i) {
        const auto sample = static_cast<std::uint16_t>(mono[i]);
        pcm[i * 2] = static_cast<std::uint8_t>(sample);
        pcm[i * 2 + 1] = static_cast<std::uint8_t>(sample >> 8);
    }

    HANDLE file = CreateFileW(argv[1], GENERIC_WRITE, 0, nullptr, CREATE_NEW,
                              FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) {
        std::fwprintf(stderr, L"Cannot create output (it must not already exist; Win32 error %lu).\n",
                      GetLastError());
        return 4;
    }

    std::array<std::uint8_t, 44> header{};
    std::memcpy(header.data(), "RIFF", 4); Put32(header, 4, 36 + kBytes);
    std::memcpy(header.data() + 8, "WAVEfmt ", 8); Put32(header, 16, 16);
    Put16(header, 20, 1); Put16(header, 22, 1); Put32(header, 24, kRate);
    Put32(header, 28, kRate * 2); Put16(header, 32, 2); Put16(header, 34, 16);
    std::memcpy(header.data() + 36, "data", 4); Put32(header, 40, kBytes);

    auto write = [file](const void* data, std::uint32_t size) {
        const auto* cursor = static_cast<const std::uint8_t*>(data);
        while (size != 0) {
            DWORD written = 0;
            const DWORD chunk = size > 1U << 20 ? 1U << 20 : size;
            if (!WriteFile(file, cursor, chunk, &written, nullptr) || written == 0) return false;
            cursor += written;
            size -= written;
        }
        return true;
    };
    bool ok = write(header.data(), static_cast<std::uint32_t>(header.size()));
    if (ok) ok = write(pcm.data(), static_cast<std::uint32_t>(pcm.size()));
    if (ok && !FlushFileBuffers(file)) ok = false;
    CloseHandle(file);
    if (!ok) {
        DeleteFileW(argv[1]);
        std::fwprintf(stderr, L"WAV write failed.\n");
        return 5;
    }
    std::wprintf(L"Wrote %u frames, mono PCM16 at %u Hz: %ls\n", kFrames, kRate, argv[1]);
    return 0;
}
