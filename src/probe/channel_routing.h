#pragma once

#include "exclusive_format_matrix.h"

#include <array>
#include <bit>
#include <cmath>
#include <cstddef>
#include <cstring>
#include <limits>
#include <cstdint>
#include <string>
#include <string_view>
#include <optional>
#include <string_view>

namespace ChannelRouting {

enum class Mode { MonoToStereo, StereoToStereo, MonoToMono };

struct EndpointFormat {
    ExclusiveFormatMatrix::Format format;
    // Zero means that Windows channel order is known only as logical slots.
    std::uint32_t channelMask{};
};

struct Configuration {
    std::string captureEndpointId;
    std::string renderEndpointId;
    EndpointFormat capture;
    EndpointFormat render;
    Mode mode{Mode::MonoToStereo};
    std::array<std::uint16_t, 2> captureChannels{};
    std::array<std::uint16_t, 2> renderChannels{};
};

inline constexpr std::uint32_t kContractVersion = 2;

inline constexpr std::string_view ModeText(Mode mode) noexcept {
    switch (mode) {
    case Mode::MonoToStereo: return "MONO_TO_STEREO";
    case Mode::StereoToStereo: return "STEREO_TO_STEREO";
    case Mode::MonoToMono: return "MONO_TO_MONO";
    }
    return "UNKNOWN";
}

inline constexpr std::uint16_t CaptureCount(Mode mode) noexcept {
    return mode == Mode::StereoToStereo ? 2 : 1;
}

inline constexpr std::uint16_t RenderCount(Mode mode) noexcept {
    return mode == Mode::MonoToStereo || mode == Mode::StereoToStereo ? 2 : 1;
}

inline constexpr bool ValidMask(std::uint16_t channels, std::uint32_t mask) noexcept {
    if (mask == 0) return true;
    return std::popcount(mask) == channels;
}

inline bool Validate(const Configuration& route) noexcept {
    if (route.mode != Mode::MonoToStereo && route.mode != Mode::StereoToStereo && route.mode != Mode::MonoToMono) return false;
    const auto captureCount = CaptureCount(route.mode);
    const auto renderCount = RenderCount(route.mode);
    const auto& captureFormat = route.capture.format;
    const auto& renderFormat = route.render.format;
    const auto validEncoding=[](ExclusiveFormatMatrix::Encoding encoding){return encoding==ExclusiveFormatMatrix::Encoding::Pcm16||encoding==ExclusiveFormatMatrix::Encoding::Pcm24In32||encoding==ExclusiveFormatMatrix::Encoding::Float32;};
    if (route.captureEndpointId.empty() || route.renderEndpointId.empty() ||
        route.captureEndpointId == route.renderEndpointId || captureFormat.sampleRate == 0 ||
        renderFormat.sampleRate == 0 || captureFormat.channels == 0 || renderFormat.channels == 0 ||
        !validEncoding(captureFormat.encoding) || !validEncoding(renderFormat.encoding) ||
        captureFormat.channels > 32 || renderFormat.channels > 32 ||
        captureCount > captureFormat.channels || renderCount > renderFormat.channels ||
        !ValidMask(captureFormat.channels, route.capture.channelMask) ||
        !ValidMask(renderFormat.channels, route.render.channelMask)) return false;
    for (std::uint16_t i = 0; i < captureCount; ++i) {
        if (route.captureChannels[i] >= captureFormat.channels) return false;
        for (std::uint16_t j = 0; j < i; ++j)
            if (route.captureChannels[i] == route.captureChannels[j]) return false;
    }
    for (std::uint16_t i = 0; i < renderCount; ++i) {
        if (route.renderChannels[i] >= renderFormat.channels) return false;
        for (std::uint16_t j = 0; j < i; ++j)
            if (route.renderChannels[i] == route.renderChannels[j]) return false;
    }
    const bool ratesSupported = (captureFormat.sampleRate == 44'100 || captureFormat.sampleRate == 48'000) &&
        (renderFormat.sampleRate == 44'100 || renderFormat.sampleRate == 48'000);
    return ratesSupported;
}

inline std::string Fingerprint(const Configuration& route) {
    std::string canonical = std::to_string(kContractVersion) + "|" + std::string(route.captureEndpointId) + "|" +
        std::string(route.renderEndpointId) + "|" + std::string(ModeText(route.mode)) + "|";
    const auto append = [&canonical](const EndpointFormat& endpoint) {
        canonical += std::to_string(endpoint.format.sampleRate) + "," +
            std::to_string(endpoint.format.channels) + "," +
            ExclusiveFormatMatrix::EncodingText(endpoint.format.encoding) + "," +
            std::to_string(endpoint.channelMask) + "|";
    };
    append(route.capture); append(route.render);
    for (std::uint16_t i=0;i<CaptureCount(route.mode);++i) canonical += "C" + std::to_string(route.captureChannels[i]) + "|";
    for (std::uint16_t i=0;i<RenderCount(route.mode);++i) canonical += "R" + std::to_string(route.renderChannels[i]) + "|";
    std::uint64_t hash = 14695981039346656037ULL;
    for (const auto c : canonical) { hash ^= static_cast<unsigned char>(c); hash *= 1099511628211ULL; }
    constexpr char hex[] = "0123456789abcdef";
    std::string result(16, '0');
    for (int i=15;i>=0;--i) { result[static_cast<std::size_t>(i)] = hex[hash & 0xfU]; hash >>= 4; }
    return result;
}

inline double ReadSample(const void* packet, std::uint32_t frame, std::uint16_t channel,
                         const EndpointFormat& format) noexcept {
    if (!packet || channel >= format.format.channels) return std::numeric_limits<double>::quiet_NaN();
    const auto sampleIndex=static_cast<std::size_t>(frame)*format.format.channels+channel;
    switch(format.format.encoding){
    case ExclusiveFormatMatrix::Encoding::Pcm16:{std::int16_t value{};std::memcpy(&value,static_cast<const std::uint8_t*>(packet)+sampleIndex*2,2);return static_cast<double>(value)/32768.0;}
    case ExclusiveFormatMatrix::Encoding::Pcm24In32:{std::int32_t value{};std::memcpy(&value,static_cast<const std::uint8_t*>(packet)+sampleIndex*4,4);return static_cast<double>(value)/2147483648.0;}
    case ExclusiveFormatMatrix::Encoding::Float32:{float value{};std::memcpy(&value,static_cast<const std::uint8_t*>(packet)+sampleIndex*4,4);return std::isfinite(value)?value:std::numeric_limits<double>::quiet_NaN();}
    }
    return std::numeric_limits<double>::quiet_NaN();
}

} // namespace ChannelRouting
