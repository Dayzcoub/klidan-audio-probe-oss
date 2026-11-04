#pragma once

#include <cmath>
#include <cstdint>
#include <limits>

namespace SignalLevels {

constexpr double kPcm16FullScale = 32768.0;
constexpr double kLowLevelThresholdDbfs = -60.0;

struct Channel {
    std::uint64_t peakMagnitude = 0;
    std::uint64_t squareSum = 0;
    std::uint64_t nonzeroSamples = 0;
    std::uint64_t analyzedSamples = 0;
    double peakDbfs = -std::numeric_limits<double>::infinity();
    double rmsDbfs = -std::numeric_limits<double>::infinity();
};

struct Metrics {
    Channel channels[2]{};
    std::uint64_t packetsOrBuffers = 0;
    std::uint64_t silentFlagCount = 0;
    std::uint64_t zeroDataPacketsOrBuffers = 0;
    std::uint64_t activeFrames = 0;
    std::uint64_t prefillSilenceFrames = 0;
    std::uint64_t underrunSilenceFrames = 0;
    bool capture = false;
    bool overflowed = false;
    bool invalidData = false;
};

enum class Status { SignalPresent, DigitalSilence, InsufficientData };

class Accumulator {
public:
    explicit Accumulator(bool capture) noexcept { metrics_.capture = capture; }

    void CapturePacket(const std::int16_t* interleaved, std::uint32_t frames,
                       bool silentFlag) noexcept {
        if (!metrics_.capture) return;
        AddCounter(metrics_.packetsOrBuffers, 1);
        if (silentFlag) AddCounter(metrics_.silentFlagCount, 1);
        if (silentFlag) {
            AddKnownSilence(frames);
            AddCounter(metrics_.zeroDataPacketsOrBuffers, 1);
            return;
        }
        if (frames != 0 && interleaved == nullptr) {
            metrics_.invalidData = true;
            return;
        }
        bool packetHasSignal = false;
        for (std::uint32_t frame = 0; frame < frames; ++frame) {
            const auto sampleIndex = static_cast<std::uint64_t>(frame) * 2;
            const auto left = interleaved[sampleIndex];
            const auto right = interleaved[sampleIndex + 1];
            AddSample(metrics_.channels[0], left);
            AddSample(metrics_.channels[1], right);
            if (left != 0 || right != 0) {
                packetHasSignal = true;
                AddCounter(metrics_.activeFrames, 1);
            }
        }
        if (!packetHasSignal) AddCounter(metrics_.zeroDataPacketsOrBuffers, 1);
    }

    void RenderBuffer(const std::int16_t* interleaved, std::uint32_t frames,
                      bool silentFlag, std::uint32_t prefillSilenceFrames,
                      std::uint32_t underrunSilenceFrames) noexcept {
        if (metrics_.capture) return;
        AddCounter(metrics_.packetsOrBuffers, 1);
        if (silentFlag) AddCounter(metrics_.silentFlagCount, 1);
        AddCounter(metrics_.prefillSilenceFrames, prefillSilenceFrames);
        AddCounter(metrics_.underrunSilenceFrames, underrunSilenceFrames);
        if (frames != 0 && interleaved == nullptr) {
            metrics_.invalidData = true;
            return;
        }
        bool bufferHasSignal = false;
        for (std::uint32_t frame = 0; frame < frames; ++frame) {
            const auto sampleIndex = static_cast<std::uint64_t>(frame) * 2;
            const auto left = interleaved[sampleIndex];
            const auto right = interleaved[sampleIndex + 1];
            AddSample(metrics_.channels[0], left);
            AddSample(metrics_.channels[1], right);
            if (left != 0 || right != 0) {
                bufferHasSignal = true;
                AddCounter(metrics_.activeFrames, 1);
            }
        }
        if (!bufferHasSignal) AddCounter(metrics_.zeroDataPacketsOrBuffers, 1);
    }

    Metrics Finish() const noexcept {
        auto result = metrics_;
        if (result.overflowed || result.invalidData) return result;
        for (auto& channel : result.channels) {
            if (channel.peakMagnitude != 0)
                channel.peakDbfs = 20.0 * std::log10(channel.peakMagnitude / kPcm16FullScale);
            if (channel.analyzedSamples != 0 && channel.squareSum != 0) {
                const double meanSquare = static_cast<double>(channel.squareSum) /
                    static_cast<double>(channel.analyzedSamples);
                channel.rmsDbfs = 10.0 * std::log10(meanSquare /
                    (kPcm16FullScale * kPcm16FullScale));
            }
        }
        return result;
    }

private:
    void AddCounter(std::uint64_t& counter, std::uint64_t increment) noexcept {
        if (increment > std::numeric_limits<std::uint64_t>::max() - counter) {
            metrics_.overflowed = true;
            return;
        }
        counter += increment;
    }

    void AddKnownSilence(std::uint32_t frames) noexcept {
        AddCounter(metrics_.channels[0].analyzedSamples, frames);
        AddCounter(metrics_.channels[1].analyzedSamples, frames);
    }

    void AddSample(Channel& channel, std::int16_t sample) noexcept {
        const auto widened = static_cast<std::int32_t>(sample);
        const auto magnitude = static_cast<std::uint64_t>(widened < 0 ? -widened : widened);
        const auto square = magnitude * magnitude;
        if (square > std::numeric_limits<std::uint64_t>::max() - channel.squareSum) {
            metrics_.overflowed = true;
            return;
        }
        AddCounter(channel.analyzedSamples, 1);
        if (sample != 0) AddCounter(channel.nonzeroSamples, 1);
        if (magnitude > channel.peakMagnitude) channel.peakMagnitude = magnitude;
        channel.squareSum += square;
    }

    Metrics metrics_{};
};

inline Status Classify(const Metrics& capture, const Metrics& render) noexcept {
    const bool captureAnalyzed = capture.channels[0].analyzedSamples != 0 &&
        capture.channels[1].analyzedSamples != 0;
    const bool renderAnalyzed = render.channels[0].analyzedSamples != 0 &&
        render.channels[1].analyzedSamples != 0;
    if (!capture.capture || render.capture || !captureAnalyzed || !renderAnalyzed ||
        capture.overflowed || render.overflowed || capture.invalidData || render.invalidData)
        return Status::InsufficientData;
    const bool signalPresent = capture.channels[0].nonzeroSamples != 0 ||
        capture.channels[1].nonzeroSamples != 0 || render.channels[0].nonzeroSamples != 0 ||
        render.channels[1].nonzeroSamples != 0;
    return signalPresent ? Status::SignalPresent : Status::DigitalSilence;
}

inline const char* StatusText(Status status) noexcept {
    switch (status) {
    case Status::SignalPresent: return "SIGNAL_PRESENT";
    case Status::DigitalSilence: return "DIGITAL_SILENCE";
    case Status::InsufficientData: return "INSUFFICIENT_DATA";
    }
    return "INSUFFICIENT_DATA";
}

inline bool HasSingleChannelSignal(const Metrics& metrics) noexcept {
    const bool left = metrics.channels[0].nonzeroSamples != 0;
    const bool right = metrics.channels[1].nonzeroSamples != 0;
    return left != right;
}

inline bool HasLowSignalLevel(const Metrics& metrics) noexcept {
    if (metrics.overflowed || metrics.invalidData) return false;
    const auto peak = metrics.channels[0].peakMagnitude > metrics.channels[1].peakMagnitude
        ? metrics.channels[0].peakMagnitude : metrics.channels[1].peakMagnitude;
    if (peak == 0) return false;
    const double peakDbfs = 20.0 * std::log10(peak / kPcm16FullScale);
    return peakDbfs < kLowLevelThresholdDbfs;
}

} // namespace SignalLevels
