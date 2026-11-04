#include "../src/probe/monitor_rtl_preflight.h"

#include <Windows.h>
#include <bcrypt.h>

#include <algorithm>
#include <array>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <fstream>
#include <iterator>
#include <limits>
#include <string>
#include <vector>

namespace {
#define CHECK(x) do { if (!(x)) { std::fprintf(stderr, "FAIL %s:%d: %s\n", __FILE__, __LINE__, #x); return false; } } while (false)
constexpr std::size_t kFrames = 240'000;
constexpr std::size_t kHeader = 44;
constexpr std::size_t kFirst = 4'800;
constexpr std::size_t kSpacing = 12'000;

bool RangeFits(std::size_t capacity, std::size_t start, std::size_t count) {
    return start <= capacity && count <= capacity - start;
}

std::uint16_t U16(const std::vector<std::uint8_t>& b, std::size_t p) {
    return static_cast<std::uint16_t>(b[p] | (static_cast<unsigned>(b[p + 1]) << 8));
}
std::uint32_t U32(const std::vector<std::uint8_t>& b, std::size_t p) {
    return static_cast<std::uint32_t>(b[p] | (static_cast<unsigned>(b[p + 1]) << 8) |
        (static_cast<unsigned>(b[p + 2]) << 16) | (static_cast<unsigned>(b[p + 3]) << 24));
}
std::vector<std::uint8_t> Read(const char* path) {
    std::ifstream in(path, std::ios::binary);
    return {std::istreambuf_iterator<char>(in), std::istreambuf_iterator<char>()};
}
std::string Sha256(const std::vector<std::uint8_t>& bytes) {
    BCRYPT_ALG_HANDLE algorithm = nullptr;
    BCRYPT_HASH_HANDLE hash = nullptr;
    DWORD objectSize = 0, resultSize = 0;
    if (BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, nullptr, 0) < 0) return {};
    if (BCryptGetProperty(algorithm, BCRYPT_OBJECT_LENGTH, reinterpret_cast<PUCHAR>(&objectSize),
                          sizeof(objectSize), &resultSize, 0) < 0) {
        BCryptCloseAlgorithmProvider(algorithm, 0);
        return {};
    }
    std::vector<UCHAR> object(objectSize);
    std::array<UCHAR, 32> digest{};
    bool ok = BCryptCreateHash(algorithm, &hash, object.data(), objectSize, nullptr, 0, 0) >= 0 &&
        BCryptHashData(hash, const_cast<PUCHAR>(bytes.data()), static_cast<ULONG>(bytes.size()), 0) >= 0 &&
        BCryptFinishHash(hash, digest.data(), static_cast<ULONG>(digest.size()), 0) >= 0;
    if (hash) BCryptDestroyHash(hash);
    BCryptCloseAlgorithmProvider(algorithm, 0);
    if (!ok) return {};
    constexpr char hex[] = "0123456789abcdef";
    std::string text;
    for (auto v : digest) { text += hex[v >> 4]; text += hex[v & 15]; }
    return text;
}

bool VerifyWave(const std::vector<std::uint8_t>& bytes) {
    CHECK(bytes.size() == kHeader + kFrames * 2);
    CHECK(std::memcmp(bytes.data(), "RIFF", 4) == 0 && U32(bytes, 4) == bytes.size() - 8);
    CHECK(std::memcmp(bytes.data() + 8, "WAVEfmt ", 8) == 0 && U32(bytes, 16) == 16);
    CHECK(U16(bytes, 20) == 1 && U16(bytes, 22) == 1 && U32(bytes, 24) == 48'000);
    CHECK(U32(bytes, 28) == 96'000 && U16(bytes, 32) == 2 && U16(bytes, 34) == 16);
    CHECK(std::memcmp(bytes.data() + 36, "data", 4) == 0 && U32(bytes, 40) == kFrames * 2);

    AnalogLoopback::RenderSignal reference(1);
    CHECK(reference.Configure(48'000, 1, 0, 5));
    std::vector<std::int16_t> expectedStereo(kFrames * 2);
    reference.Fill(expectedStereo.data(), static_cast<std::uint32_t>(kFrames), 0);
    std::vector<std::int16_t> samples(kFrames);
    int peak = 0;
    for (std::size_t i = 0; i < kFrames; ++i) {
        samples[i] = static_cast<std::int16_t>(U16(bytes, kHeader + i * 2));
        CHECK(samples[i] == expectedStereo[i * 2]);
        peak = std::max(peak, std::abs(static_cast<int>(samples[i])));
        CHECK(samples[i] != INT16_MIN && samples[i] != INT16_MAX);
    }
    CHECK(peak == AnalogLoopback::kSignalPeakCeiling);
    for (std::size_t i = 0; i < kFrames; ++i) {
        const bool inMarker = i >= kFirst && ((i - kFirst) % kSpacing) < AnalogLoopback::kMarkerFrames &&
            (i - kFirst) / kSpacing < 20;
        if (!inMarker) CHECK(samples[i] == 0);
    }
    CHECK(reference.MarkerStarts().size() == 20);
    for (std::size_t marker = 0; marker < 20; ++marker) {
        CHECK(reference.MarkerStarts()[marker] == kFirst + marker * kSpacing);
        constexpr std::size_t probe = 32;
        CHECK(samples[kFirst + marker * kSpacing + probe] ==
              AnalogLoopback::RenderSignal::MarkerPolarity(marker) * reference.Marker()[probe]);
    }
    return true;
}

std::vector<std::int16_t> ReadSamples(const std::vector<std::uint8_t>& bytes) {
    std::vector<std::int16_t> result(kFrames);
    for (std::size_t i = 0; i < kFrames; ++i)
        result[i] = static_cast<std::int16_t>(U16(bytes, kHeader + i * 2));
    return result;
}

bool MatchesReference(const std::vector<std::int16_t>& samples) {
    if (samples.size() != kFrames) return false;
    AnalogLoopback::RenderSignal reference(1);
    if (!reference.Configure(48'000, 1, 0, 5)) return false;
    std::vector<std::int16_t> stereo(kFrames * 2);
    reference.Fill(stereo.data(), static_cast<std::uint32_t>(kFrames), 0);
    for (std::size_t i = 0; i < kFrames; ++i) if (samples[i] != stereo[i * 2]) return false;
    return true;
}

MonitorRtlPreflight::Result Analyze(const std::vector<std::int16_t>& mono) {
    MonitorRtlPreflight::CaptureData capture;
    if (!capture.Configure(static_cast<std::uint32_t>(mono.size()))) return {};
    std::vector<std::int16_t> stereo(mono.size() * 2, 0);
    for (std::size_t i = 0; i < mono.size(); ++i) stereo[i * 2] = mono[i];
    for (std::uint32_t start = 0; start < mono.size(); start += 256) {
        const auto count = static_cast<std::uint32_t>(std::min<std::size_t>(256, mono.size() - start));
        if (!RangeFits(mono.size(), start, count) || !RangeFits(stereo.size(), start * 2, count * 2)) return {};
        const auto qpc = 1'000'000ULL + static_cast<std::uint64_t>(start) * 10'000'000 / 48'000;
        if (!capture.Append(stereo.data() + static_cast<std::size_t>(start) * 2, count, false,
                            false, false, start, qpc)) return {};
    }
    return capture.Analyze();
}

bool VerifyCaptureCases(const std::vector<std::int16_t>& source) {
    CHECK(source.size() == kFrames);
    const auto good = Analyze(source);
    CHECK(good.markerFound && good.matchedMarkers == 20 && good.input2Quiet);
    AnalogLoopback::RenderSignal reference(1);
    CHECK(reference.Configure(48'000, 1, 0, 5));
    const auto markerFrames = reference.Marker().size();
    CHECK(RangeFits(source.size(), 0, markerFrames));

    // Playback started after Capture: the complete source begins at a later capture frame.
    for (const std::size_t delay : {1U, 48'000U, 160'000U}) {
        std::vector<std::int16_t> partial(kFrames, 0);
        CHECK(delay <= partial.size());
        const auto copyFrames = std::min(source.size(), partial.size() - delay);
        CHECK(RangeFits(source.size(), 0, copyFrames) && RangeFits(partial.size(), delay, copyFrames));
        std::copy_n(source.begin(), copyFrames, partial.begin() + delay);
        const auto result = Analyze(partial);
        CHECK(result.markerFound && result.matchedMarkers >= AnalogLoopback::kMinimumMarkers);
    }
    std::vector<std::int16_t> captureStartedMidFile(kFrames, 0);
    constexpr std::size_t sourceOffset = 48'000;
    CHECK(sourceOffset <= source.size());
    const auto midFileFrames = source.size() - sourceOffset;
    CHECK(RangeFits(source.size(), sourceOffset, midFileFrames));
    CHECK(RangeFits(captureStartedMidFile.size(), 0, midFileFrames));
    std::copy_n(source.begin() + sourceOffset, midFileFrames, captureStartedMidFile.begin());
    const auto midFile = Analyze(captureStartedMidFile);
    std::printf("One-second source offset: markerFound=%s, matchedMarkers=%zu\n",
                midFile.markerFound ? "true" : "false", midFile.matchedMarkers);
    CHECK(midFile.markerFound); // Existing five-marker threshold can accept a partial source.

    auto altered = source;
    for (std::size_t m = 0; m < 20; ++m) {
        const auto start = kFirst + m * kSpacing;
        CHECK(RangeFits(altered.size(), start, markerFrames));
        std::reverse(altered.begin() + start, altered.begin() + start + AnalogLoopback::kMarkerFrames);
    }
    CHECK(!MatchesReference(altered));
    CHECK(!Analyze(altered).markerFound);

    auto spacing = source;
    std::fill(spacing.begin(), spacing.end(), 0);
    constexpr std::size_t wrongSpacing = 13'000;
    CHECK(RangeFits(spacing.size(), kFirst, markerFrames));
    const auto maxMarkerStart = spacing.size() - markerFrames;
    const auto lastFitIndex = (maxMarkerStart - kFirst) / wrongSpacing;
    const auto lastFitStart = kFirst + lastFitIndex * wrongSpacing;
    const auto firstNonFitStart = lastFitStart + wrongSpacing;
    CHECK(lastFitStart == 238'800 && firstNonFitStart == 251'800);
    CHECK(RangeFits(spacing.size(), lastFitStart, markerFrames));
    CHECK(!RangeFits(spacing.size(), firstNonFitStart, markerFrames));
    std::size_t spacingMarkersWritten = 0;
    for (std::size_t m = 0; m < 20; ++m) {
        CHECK(m <= (std::numeric_limits<std::size_t>::max() - kFirst) / wrongSpacing);
        const auto start = kFirst + m * wrongSpacing;
        if (!RangeFits(spacing.size(), start, markerFrames)) {
            CHECK(start == firstNonFitStart);
            break;
        }
        CHECK(RangeFits(reference.Marker().size(), 0, markerFrames));
        std::copy(reference.Marker().begin(), reference.Marker().end(), spacing.begin() + start);
        ++spacingMarkersWritten;
    }
    CHECK(spacingMarkersWritten == lastFitIndex + 1);
    CHECK(!MatchesReference(spacing));
    CHECK(!Analyze(spacing).markerFound);

    auto polarity = source;
    for (std::size_t m = 0; m < 20; ++m) {
        const auto start = kFirst + m * kSpacing;
        CHECK(RangeFits(polarity.size(), start, markerFrames));
        for (std::size_t i = 0; i < AnalogLoopback::kMarkerFrames; ++i)
            polarity[start + i] = static_cast<std::int16_t>(-polarity[start + i]);
    }
    CHECK(!MatchesReference(polarity));
    const auto inverted = Analyze(polarity);
    std::printf("Globally inverted source: markerFound=%s, matchedMarkers=%zu\n",
                inverted.markerFound ? "true" : "false", inverted.matchedMarkers);

    auto missing = source;
    for (std::size_t m = 0; m < 20; ++m) if (m >= 4) {
        const auto start = kFirst + m * kSpacing;
        CHECK(RangeFits(missing.size(), start, markerFrames));
        std::fill_n(missing.begin() + start, markerFrames, 0);
    }
    CHECK(!MatchesReference(missing));
    CHECK(!Analyze(missing).markerFound);
    return true;
}

bool Run(const char* firstPath, const char* secondPath) {
    const auto first = Read(firstPath);
    const auto second = Read(secondPath);
    CHECK(VerifyWave(first));
    CHECK(VerifyWave(second));
    CHECK(first == second);
    const auto hash = Sha256(first);
    CHECK(hash.size() == 64);
    const auto samples = ReadSamples(first);
    CHECK(VerifyCaptureCases(samples));
    std::printf("Verified canonical PCM16 WAV: %zu frames; SHA-256 %s\n", samples.size(), hash.c_str());
    return true;
}
}

int main(int argc, char** argv) {
    if (argc != 3) { std::fprintf(stderr, "Usage: probe_prbs_wav_tests first.wav second.wav\n"); return 2; }
    return Run(argv[1], argv[2]) ? 0 : 1;
}
