#pragma once

#include <algorithm>
#include <array>
#include <atomic>
#include <cmath>
#include <cstdint>
#include <iomanip>
#include <limits>
#include <optional>
#include <sstream>
#include <string>
#include <string_view>

namespace ChannelMeter {

constexpr std::uint32_t kMaximumChannels = 32;
constexpr std::uint32_t kPcm16FullScale = 32'768;
static_assert(std::atomic<std::uint64_t>::is_always_lock_free);
static_assert(std::atomic<std::uint32_t>::is_always_lock_free);

struct ChannelValues {
    std::uint64_t peakMagnitude = 0;
    std::uint64_t peakHoldMagnitude = 0;
    std::uint64_t squareSum = 0;
    std::uint64_t clippedSamples = 0;
    std::uint64_t analyzedSamples = 0;
    std::uint64_t nonzeroSamples = 0;
};

struct Snapshot {
    std::uint32_t channelCount = 0;
    std::uint32_t sampleRateHz = 0;
    std::uint32_t windowMilliseconds = 0;
    std::uint32_t windowFrames = 0;
    std::uint64_t analyzedFrames = 0;
    std::uint64_t completedWindowIndex = 0;
    std::uint32_t framesInCurrentWindow = 0;
    std::uint32_t lastCompletedWindowFrames = 0;
    std::uint64_t sequence = 0;
    bool configured = false;
    bool hasCompletedWindow = false;
    bool invalidInput = false;
    bool overflowed = false;
    std::array<ChannelValues, kMaximumChannels> total{};
    std::array<ChannelValues, kMaximumChannels> currentWindow{};
    std::array<ChannelValues, kMaximumChannels> lastCompletedWindow{};
};

class Meter final {
public:
    bool Configure(std::uint32_t channels, std::uint32_t sampleRateHz,
                   std::uint32_t windowMilliseconds) noexcept {
        configured_ = false;
        Publish();
        if (channels == 0 || channels > kMaximumChannels || sampleRateHz == 0 || windowMilliseconds == 0)
            return false;
        const std::uint64_t frameCount =
            (static_cast<std::uint64_t>(sampleRateHz) * windowMilliseconds + 500) / 1'000;
        if (frameCount == 0 || frameCount > std::numeric_limits<std::uint32_t>::max()) return false;
        channels_ = channels;
        sampleRateHz_ = sampleRateHz;
        windowMilliseconds_ = windowMilliseconds;
        windowFrames_ = static_cast<std::uint32_t>(frameCount);
        analyzedFrames_ = completedWindowIndex_ = 0;
        framesInCurrentWindow_ = lastCompletedWindowFrames_ = 0;
        hasCompletedWindow_ = invalidInput_ = overflowed_ = false;
        total_.fill({});
        currentWindow_.fill({});
        lastCompletedWindow_.fill({});
        configured_ = true;
        Publish();
        return true;
    }

    bool Process(const std::int16_t* interleaved, std::uint32_t frames,
                 bool silentPacket) noexcept {
        if (!configured_ || invalidInput_ || overflowed_) return false;
        if (frames == 0) return true;
        if (!silentPacket && interleaved == nullptr) {
            invalidInput_ = true;
            Publish();
            return false;
        }
        if (static_cast<std::uint64_t>(frames) >
            std::numeric_limits<std::uint64_t>::max() - analyzedFrames_) {
            overflowed_ = true;
            Publish();
            return false;
        }
        if (silentPacket) {
            std::uint32_t remaining = frames;
            while (remaining != 0 && !overflowed_) {
                const auto count = std::min(remaining, windowFrames_ - framesInCurrentWindow_);
                for (std::uint32_t channel = 0; channel < channels_; ++channel) {
                    if (!Add(currentWindow_[channel].analyzedSamples, count) ||
                        !Add(total_[channel].analyzedSamples, count)) {
                        overflowed_ = true;
                        break;
                    }
                }
                if (overflowed_) break;
                analyzedFrames_ += count;
                framesInCurrentWindow_ += count;
                remaining -= count;
                if (framesInCurrentWindow_ == windowFrames_) CompleteWindow();
            }
            Publish();
            return !overflowed_;
        }
        if (static_cast<std::uint64_t>(frames) * channels_ >
            std::numeric_limits<std::size_t>::max() / sizeof(std::int16_t)) {
            invalidInput_ = true;
            Publish();
            return false;
        }
        for (std::uint32_t frame = 0; frame < frames && !overflowed_; ++frame) {
            const auto base = static_cast<std::size_t>(frame) * channels_;
            for (std::uint32_t channel = 0; channel < channels_; ++channel) {
                if (!AddSample(currentWindow_[channel], interleaved[base + channel]) ||
                    !AddSample(total_[channel], interleaved[base + channel])) {
                    overflowed_ = true;
                    break;
                }
            }
            if (overflowed_) break;
            ++analyzedFrames_;
            ++framesInCurrentWindow_;
            if (framesInCurrentWindow_ == windowFrames_) CompleteWindow();
        }
        Publish();
        return !overflowed_;
    }

    bool TrySnapshot(Snapshot& snapshot, std::uint32_t attempts = 4) const noexcept {
        attempts = std::min(attempts, 4U);
        for (std::uint32_t attempt = 0; attempt < attempts; ++attempt) {
            const auto before = published_.sequence.load(std::memory_order_acquire);
            if ((before & 1U) != 0) continue;
            snapshot.channelCount = published_.channelCount.load(std::memory_order_relaxed);
            snapshot.sampleRateHz = published_.sampleRateHz.load(std::memory_order_relaxed);
            snapshot.windowMilliseconds = published_.windowMilliseconds.load(std::memory_order_relaxed);
            snapshot.windowFrames = published_.windowFrames.load(std::memory_order_relaxed);
            snapshot.analyzedFrames = published_.analyzedFrames.load(std::memory_order_relaxed);
            snapshot.completedWindowIndex = published_.completedWindowIndex.load(std::memory_order_relaxed);
            snapshot.framesInCurrentWindow = published_.framesInCurrentWindow.load(std::memory_order_relaxed);
            snapshot.lastCompletedWindowFrames = published_.lastCompletedWindowFrames.load(std::memory_order_relaxed);
            const auto flags = published_.flags.load(std::memory_order_relaxed);
            snapshot.configured = (flags & 1U) != 0;
            snapshot.hasCompletedWindow = (flags & 2U) != 0;
            snapshot.invalidInput = (flags & 4U) != 0;
            snapshot.overflowed = (flags & 8U) != 0;
            for (std::uint32_t channel = 0; channel < kMaximumChannels; ++channel) {
                LoadValues(published_.total[channel], snapshot.total[channel]);
                LoadValues(published_.current[channel], snapshot.currentWindow[channel]);
                LoadValues(published_.completed[channel], snapshot.lastCompletedWindow[channel]);
            }
            const auto after = published_.sequence.load(std::memory_order_acquire);
            if (before == after && (after & 1U) == 0) {
                snapshot.sequence = after;
                return true;
            }
        }
        return false;
    }

private:
    struct AtomicValues {
        std::atomic<std::uint64_t> peak{0}, hold{0}, squares{0}, clips{0}, samples{0}, nonzero{0};
    };
    struct Published {
        std::atomic<std::uint64_t> sequence{0}, analyzedFrames{0}, completedWindowIndex{0};
        std::atomic<std::uint32_t> channelCount{0}, sampleRateHz{0}, windowMilliseconds{0}, windowFrames{0};
        std::atomic<std::uint32_t> framesInCurrentWindow{0}, lastCompletedWindowFrames{0}, flags{0};
        std::array<AtomicValues, kMaximumChannels> total{}, current{}, completed{};
    };

    static bool Add(std::uint64_t& value, std::uint64_t increment) noexcept {
        if (increment > std::numeric_limits<std::uint64_t>::max() - value) return false;
        value += increment;
        return true;
    }
    static bool AddSample(ChannelValues& values, std::int16_t sample) noexcept {
        const auto widened = static_cast<std::int32_t>(sample);
        const auto magnitude = static_cast<std::uint64_t>(widened < 0 ? -widened : widened);
        if (!Add(values.analyzedSamples, 1) || !Add(values.squareSum, magnitude * magnitude) ||
            ((sample == INT16_MIN || sample == INT16_MAX) && !Add(values.clippedSamples, 1)) ||
            (sample != 0 && !Add(values.nonzeroSamples, 1))) return false;
        values.peakMagnitude = std::max(values.peakMagnitude, magnitude);
        values.peakHoldMagnitude = std::max(values.peakHoldMagnitude, magnitude);
        return true;
    }
    void CompleteWindow() noexcept {
        lastCompletedWindow_ = currentWindow_;
        lastCompletedWindowFrames_ = framesInCurrentWindow_;
        hasCompletedWindow_ = true;
        if (completedWindowIndex_ == std::numeric_limits<std::uint64_t>::max()) {
            overflowed_ = true;
            return;
        }
        ++completedWindowIndex_;
        currentWindow_.fill({});
        framesInCurrentWindow_ = 0;
    }
    static void StoreValues(AtomicValues& destination, const ChannelValues& source) noexcept {
        destination.peak.store(source.peakMagnitude, std::memory_order_relaxed);
        destination.hold.store(source.peakHoldMagnitude, std::memory_order_relaxed);
        destination.squares.store(source.squareSum, std::memory_order_relaxed);
        destination.clips.store(source.clippedSamples, std::memory_order_relaxed);
        destination.samples.store(source.analyzedSamples, std::memory_order_relaxed);
        destination.nonzero.store(source.nonzeroSamples, std::memory_order_relaxed);
    }
    static void LoadValues(const AtomicValues& source, ChannelValues& destination) noexcept {
        destination.peakMagnitude = source.peak.load(std::memory_order_relaxed);
        destination.peakHoldMagnitude = source.hold.load(std::memory_order_relaxed);
        destination.squareSum = source.squares.load(std::memory_order_relaxed);
        destination.clippedSamples = source.clips.load(std::memory_order_relaxed);
        destination.analyzedSamples = source.samples.load(std::memory_order_relaxed);
        destination.nonzeroSamples = source.nonzero.load(std::memory_order_relaxed);
    }
    void Publish() noexcept {
        auto sequence = published_.sequence.load(std::memory_order_relaxed);
        published_.sequence.store(sequence + 1, std::memory_order_release);
        published_.channelCount.store(channels_, std::memory_order_relaxed);
        published_.sampleRateHz.store(sampleRateHz_, std::memory_order_relaxed);
        published_.windowMilliseconds.store(windowMilliseconds_, std::memory_order_relaxed);
        published_.windowFrames.store(windowFrames_, std::memory_order_relaxed);
        published_.analyzedFrames.store(analyzedFrames_, std::memory_order_relaxed);
        published_.completedWindowIndex.store(completedWindowIndex_, std::memory_order_relaxed);
        published_.framesInCurrentWindow.store(framesInCurrentWindow_, std::memory_order_relaxed);
        published_.lastCompletedWindowFrames.store(lastCompletedWindowFrames_, std::memory_order_relaxed);
        const std::uint32_t flags = (configured_ ? 1U : 0U) | (hasCompletedWindow_ ? 2U : 0U) |
            (invalidInput_ ? 4U : 0U) | (overflowed_ ? 8U : 0U);
        published_.flags.store(flags, std::memory_order_relaxed);
        for (std::uint32_t channel = 0; channel < channels_; ++channel) {
            StoreValues(published_.total[channel], total_[channel]);
            StoreValues(published_.current[channel], currentWindow_[channel]);
            StoreValues(published_.completed[channel], lastCompletedWindow_[channel]);
        }
        published_.sequence.store(sequence + 2, std::memory_order_release);
    }

    std::uint32_t channels_ = 0, sampleRateHz_ = 0, windowMilliseconds_ = 0, windowFrames_ = 0;
    std::uint64_t analyzedFrames_ = 0, completedWindowIndex_ = 0;
    std::uint32_t framesInCurrentWindow_ = 0, lastCompletedWindowFrames_ = 0;
    bool configured_ = false, hasCompletedWindow_ = false, invalidInput_ = false, overflowed_ = false;
    std::array<ChannelValues, kMaximumChannels> total_{}, currentWindow_{}, lastCompletedWindow_{};
    Published published_{};
};

inline double Dbfs(std::uint64_t magnitude) noexcept {
    return magnitude == 0 ? -std::numeric_limits<double>::infinity()
                          : 20.0 * std::log10(static_cast<double>(magnitude) / kPcm16FullScale);
}
inline double RmsDbfs(const ChannelValues& values) noexcept {
    if (values.analyzedSamples == 0 || values.squareSum == 0)
        return -std::numeric_limits<double>::infinity();
    const double meanSquare = static_cast<double>(values.squareSum) / values.analyzedSamples;
    return 10.0 * std::log10(meanSquare /
        (static_cast<double>(kPcm16FullScale) * kPcm16FullScale));
}
inline bool IsSilent(const ChannelValues& values) noexcept { return values.nonzeroSamples == 0; }
inline const ChannelValues* LatestCompletedWindow(const Snapshot& snapshot,
                                                  std::uint32_t channel) noexcept {
    return snapshot.hasCompletedWindow && channel < snapshot.channelCount
        ? &snapshot.lastCompletedWindow[channel] : nullptr;
}

enum class RunStatus { Completed, Cancelled, DeviceLost, Unsupported, Failed, Incomplete };
inline RunStatus ClassifyRun(bool durationReached, bool cancelled, bool deviceLost,
                             bool unsupported, bool apiFailure, bool cleanupFailure) noexcept {
    if (deviceLost) return RunStatus::DeviceLost;
    if (unsupported) return RunStatus::Unsupported;
    if (apiFailure || cleanupFailure) return RunStatus::Failed;
    if (cancelled) return RunStatus::Cancelled;
    return durationReached ? RunStatus::Completed : RunStatus::Incomplete;
}
inline const char* RunStatusText(RunStatus status) noexcept {
    switch (status) {
    case RunStatus::Completed: return "COMPLETED";
    case RunStatus::Cancelled: return "CANCELLED";
    case RunStatus::DeviceLost: return "DEVICE_LOST";
    case RunStatus::Unsupported: return "UNSUPPORTED";
    case RunStatus::Failed: return "FAILED";
    case RunStatus::Incomplete: return "INCOMPLETE";
    }
    return "INCOMPLETE";
}

enum class SpeakerLabel : std::uint8_t {
    Unknown, FrontLeft, FrontRight, FrontCenter, LowFrequency, BackLeft, BackRight,
    FrontLeftOfCenter, FrontRightOfCenter, BackCenter, SideLeft, SideRight,
    TopCenter, TopFrontLeft, TopFrontCenter, TopFrontRight, TopBackLeft,
    TopBackCenter, TopBackRight
};
inline const char* SpeakerLabelText(SpeakerLabel label) noexcept {
    switch (label) {
    case SpeakerLabel::FrontLeft: return "FRONT_LEFT";
    case SpeakerLabel::FrontRight: return "FRONT_RIGHT";
    case SpeakerLabel::FrontCenter: return "FRONT_CENTER";
    case SpeakerLabel::LowFrequency: return "LOW_FREQUENCY";
    case SpeakerLabel::BackLeft: return "BACK_LEFT";
    case SpeakerLabel::BackRight: return "BACK_RIGHT";
    case SpeakerLabel::FrontLeftOfCenter: return "FRONT_LEFT_OF_CENTER";
    case SpeakerLabel::FrontRightOfCenter: return "FRONT_RIGHT_OF_CENTER";
    case SpeakerLabel::BackCenter: return "BACK_CENTER";
    case SpeakerLabel::SideLeft: return "SIDE_LEFT";
    case SpeakerLabel::SideRight: return "SIDE_RIGHT";
    case SpeakerLabel::TopCenter: return "TOP_CENTER";
    case SpeakerLabel::TopFrontLeft: return "TOP_FRONT_LEFT";
    case SpeakerLabel::TopFrontCenter: return "TOP_FRONT_CENTER";
    case SpeakerLabel::TopFrontRight: return "TOP_FRONT_RIGHT";
    case SpeakerLabel::TopBackLeft: return "TOP_BACK_LEFT";
    case SpeakerLabel::TopBackCenter: return "TOP_BACK_CENTER";
    case SpeakerLabel::TopBackRight: return "TOP_BACK_RIGHT";
    case SpeakerLabel::Unknown: return "UNKNOWN";
    }
    return "UNKNOWN";
}

struct FormatTopology {
    bool available = false;
    std::uint32_t sampleRateHz = 0;
    std::uint16_t reportedChannels = 0;
    std::uint16_t containerBits = 0;
    std::uint16_t validBits = 0;
    enum class Encoding : std::uint8_t { Unknown, Pcm16, Pcm24In32, Float32 } encoding = Encoding::Unknown;
    bool channelMaskAvailable = false;
    std::uint32_t channelMask = 0;
    std::uint16_t labelCount = 0;
    std::array<SpeakerLabel, kMaximumChannels> channelLabels{};
    std::array<bool, kMaximumChannels> channelLabelKnown{};
};

struct Report {
    FormatTopology deviceMixFormat{};
    FormatTopology selectedTestFormat{};
    Snapshot meter{};
    RunStatus status = RunStatus::Incomplete;
    std::uint32_t requestedDurationSeconds = 0;
    std::optional<std::uint64_t> elapsedMilliseconds;
    std::optional<std::uint32_t> actualBufferFrames;
    std::optional<std::uint64_t> actualBufferPeriod100ns;
    bool minimumPeriod = false;
    std::int32_t formatSupportHresult = 0, firstInitializeHresult = 0;
    std::int32_t alignmentRetryInitializeHresult = 0, finalInitializeHresult = 0;
    std::int32_t startHresult = 0, stopHresult = 0;
    bool formatSupportHresultKnown = false, firstInitializeHresultKnown = false;
    bool alignmentRetryInitializeHresultKnown = false, finalInitializeHresultKnown = false;
    bool startHresultKnown = false, stopHresultKnown = false;
    std::int32_t errorHresult = 0;
    bool errorHresultKnown = false;
    std::string errorStage;
    std::string cleanupStatus = "INCOMPLETE";
    std::optional<bool> meterAnalysisAttempted;
    std::optional<bool> durationReached;
    std::optional<bool> watchdogTimedOut;
    std::optional<bool> cancelled;
    std::optional<std::uint64_t> captureFrames;
    std::optional<double> captureFrameCoveragePercent;
    std::optional<std::uint64_t> captureDiscontinuities;
    std::optional<std::uint64_t> captureTimestampErrors;
    std::optional<std::uint64_t> unavailableCaptureBuffers;
    std::optional<std::string> captureStreamStatus;
    std::optional<std::string> captureStreamQuality;
    std::optional<std::string> meterAnalysisStatus;
};

inline std::string JsonString(std::string_view value) {
    std::string out = "\"";
    for (const unsigned char c : value) {
        switch (c) {
        case '"': out += "\\\""; break;
        case '\\': out += "\\\\"; break;
        case '\b': out += "\\b"; break;
        case '\f': out += "\\f"; break;
        case '\n': out += "\\n"; break;
        case '\r': out += "\\r"; break;
        case '\t': out += "\\t"; break;
        default:
            if (c < 0x20) {
                constexpr char hex[] = "0123456789abcdef";
                out += "\\u00"; out += hex[c >> 4]; out += hex[c & 15];
            } else out += static_cast<char>(c);
        }
    }
    out += '"';
    return out;
}
inline std::string HresultJson(std::int32_t value, bool known) {
    if (!known) return "null";
    std::ostringstream out;
    out << '"' << "0x" << std::uppercase << std::hex << std::setw(8) << std::setfill('0')
        << static_cast<std::uint32_t>(value) << '"';
    return out.str();
}
inline std::string NumberJson(double value) {
    if (!std::isfinite(value)) return "null";
    std::ostringstream out;
    out << std::setprecision(8) << value;
    return out.str();
}
inline std::string ValuesJson(const ChannelValues& values, bool attempted) {
    if (!attempted) return "{\"peakDbfs\":null,\"peakHoldDbfs\":null,\"rmsDbfs\":null,\"clippedSamples\":null,\"analyzedSamples\":null,\"silent\":null}";
    std::ostringstream out;
    out << "{\"peakDbfs\":" << NumberJson(Dbfs(values.peakMagnitude))
        << ",\"peakHoldDbfs\":" << NumberJson(Dbfs(values.peakHoldMagnitude))
        << ",\"rmsDbfs\":" << NumberJson(RmsDbfs(values))
        << ",\"clippedSamples\":" << values.clippedSamples
        << ",\"analyzedSamples\":" << values.analyzedSamples
        << ",\"silent\":" << (values.analyzedSamples == 0 ? "null" : IsSilent(values) ? "true" : "false") << '}';
    return out.str();
}
template <typename T>
inline std::string OptionalJson(const std::optional<T>& value) {
    return value ? std::to_string(*value) : "null";
}
inline std::string OptionalJson(const std::optional<bool>& value) {
    return value ? (*value ? "true" : "false") : "null";
}
inline std::string OptionalJson(const std::optional<std::string>& value) {
    return value ? JsonString(*value) : "null";
}
inline std::string OptionalJson(const std::optional<double>& value) {
    return value ? NumberJson(*value) : "null";
}
inline std::string FormatTopologyJson(const FormatTopology& format) {
    const char* encoding = "UNKNOWN";
    switch (format.encoding) {
    case FormatTopology::Encoding::Pcm16: encoding = "PCM16"; break;
    case FormatTopology::Encoding::Pcm24In32: encoding = "PCM24_IN_32"; break;
    case FormatTopology::Encoding::Float32: encoding = "FLOAT32"; break;
    case FormatTopology::Encoding::Unknown: break;
    }
    std::ostringstream out;
    out << "{\"available\":" << (format.available ? "true" : "false")
        << ",\"sampleRateHz\":" << (format.available ? std::to_string(format.sampleRateHz) : "null")
        << ",\"reportedChannelCount\":" << (format.available ? std::to_string(format.reportedChannels) : "null")
        << ",\"containerBits\":" << (format.available ? std::to_string(format.containerBits) : "null")
        << ",\"validBits\":" << (format.available ? std::to_string(format.validBits) : "null")
        << ",\"encoding\":" << JsonString(format.available ? encoding : "UNKNOWN")
        << ",\"channelMask\":";
    if (!format.channelMaskAvailable) out << "null";
    else out << format.channelMask;
    out << ",\"windowsChannelLabels\":[";
    for (std::uint16_t channel = 0; channel < format.labelCount; ++channel) {
        if (channel != 0) out << ',';
        out << "{\"channelIndex\":" << channel << ",\"label\":"
            << JsonString(format.channelLabelKnown[channel] ? SpeakerLabelText(format.channelLabels[channel]) : "UNKNOWN")
            << ",\"labelKnown\":" << (format.channelLabelKnown[channel] ? "true" : "false")
            << ",\"physicalConnector\":null}";
    }
    out << "]}";
    return out.str();
}

inline std::string ToJson(const Report& report, std::string_view timestampUtc,
                          std::string_view reportId, std::string_view probeVersion) {
    const auto expectedFrames = static_cast<std::uint64_t>(report.meter.sampleRateHz) * report.requestedDurationSeconds;
    const double coveragePercent = expectedFrames == 0 ? 0.0
        : 100.0 * static_cast<double>(report.meter.analyzedFrames) / expectedFrames;
    std::ostringstream out;
    out << "{\"schemaVersion\":1,\"reportType\":\"wasapi-capture-channel-meter\","
        << "\"reportId\":" << JsonString(reportId) << ",\"timestampUtc\":" << JsonString(timestampUtc)
        << ",\"probeVersion\":" << JsonString(probeVersion) << ",\"platform\":\"Windows\","
        << "\"backend\":\"wasapi\",\"privacy\":\"sanitized\","
        << "\"evidence\":{\"sourceType\":\"LIVE_CAPTURE_METER\",\"acousticCalibration\":false},"
        << "\"topology\":{\"endpointDirection\":\"CAPTURE\",\"endpointIdentity\":null,\"identityScope\":\"LOCAL_CONSOLE_ONLY\"," 
        << "\"deviceMixFormat\":" << FormatTopologyJson(report.deviceMixFormat)
        << ",\"selectedTestFormat\":" << FormatTopologyJson(report.selectedTestFormat)
        << ",\"physicalConnectorMappings\":\"UNKNOWN\"},"
        << "\"observed\":{\"status\":" << JsonString(RunStatusText(report.status))
        << ",\"requestedDurationSeconds\":" << report.requestedDurationSeconds
        << ",\"elapsedMilliseconds\":" << OptionalJson(report.elapsedMilliseconds)
        << ",\"periodMode\":" << JsonString(report.minimumPeriod ? "minimum" : "default")
        << ",\"actualBufferFrames\":" << OptionalJson(report.actualBufferFrames)
        << ",\"actualBufferPeriod100ns\":" << OptionalJson(report.actualBufferPeriod100ns)
        << ",\"formatSupportHresult\":" << HresultJson(report.formatSupportHresult, report.formatSupportHresultKnown)
        << ",\"firstInitializeHresult\":" << HresultJson(report.firstInitializeHresult, report.firstInitializeHresultKnown)
        << ",\"alignmentRetryInitializeHresult\":" << HresultJson(report.alignmentRetryInitializeHresult, report.alignmentRetryInitializeHresultKnown)
        << ",\"finalInitializeHresult\":" << HresultJson(report.finalInitializeHresult, report.finalInitializeHresultKnown)
        << ",\"startHresult\":" << HresultJson(report.startHresult, report.startHresultKnown)
        << ",\"stopHresult\":" << HresultJson(report.stopHresult, report.stopHresultKnown)
        << ",\"sampleRateHz\":" << report.meter.sampleRateHz
        << ",\"channelCount\":" << report.meter.channelCount
        << ",\"expectedFrames\":" << expectedFrames
        << ",\"analyzedFrames\":" << (report.meterAnalysisAttempted.value_or(false)
            ? std::to_string(report.meter.analyzedFrames) : "null")
        << ",\"frameCoveragePercent\":" << (report.meterAnalysisAttempted.value_or(false)
            ? NumberJson(coveragePercent) : "null")
        << ",\"durationReached\":" << OptionalJson(report.durationReached)
        << ",\"captureFrames\":" << OptionalJson(report.captureFrames)
        << ",\"captureFrameCoveragePercent\":" << OptionalJson(report.captureFrameCoveragePercent)
        << ",\"captureDiscontinuities\":" << OptionalJson(report.captureDiscontinuities)
        << ",\"captureTimestampErrors\":" << OptionalJson(report.captureTimestampErrors)
        << ",\"unavailableCaptureBuffers\":" << OptionalJson(report.unavailableCaptureBuffers)
        << ",\"watchdogTimedOut\":" << OptionalJson(report.watchdogTimedOut)
        << ",\"cancelled\":" << OptionalJson(report.cancelled)
        << ",\"captureStreamStatus\":" << OptionalJson(report.captureStreamStatus)
        << ",\"captureStreamQuality\":" << OptionalJson(report.captureStreamQuality)
        << ",\"meterAnalysisAttempted\":" << OptionalJson(report.meterAnalysisAttempted)
        << ",\"meterAnalysisStatus\":" << OptionalJson(report.meterAnalysisStatus)
        << ",\"errorStage\":" << (report.errorStage.empty() ? "null" : JsonString(report.errorStage))
        << ",\"errorHresult\":" << HresultJson(report.errorHresult, report.errorHresultKnown)
        << ",\"cleanupStatus\":" << JsonString(report.cleanupStatus)
        << ",\"invalidInput\":" << OptionalJson(report.meterAnalysisAttempted.has_value()
            ? std::optional<bool>(report.meter.invalidInput) : std::optional<bool>{})
        << ",\"overflowed\":" << OptionalJson(report.meterAnalysisAttempted.has_value()
            ? std::optional<bool>(report.meter.overflowed) : std::optional<bool>{})
        << ",\"windowMilliseconds\":" << report.meter.windowMilliseconds
        << ",\"windowFrames\":" << report.meter.windowFrames
        << ",\"completedWindowIndex\":" << (report.meterAnalysisAttempted.value_or(false)
            ? std::to_string(report.meter.completedWindowIndex) : "null")
        << ",\"framesInCurrentWindow\":" << (report.meterAnalysisAttempted.value_or(false)
            ? std::to_string(report.meter.framesInCurrentWindow) : "null")
        << ",\"channels\":[";
    for (std::uint32_t channel = 0; channel < report.meter.channelCount; ++channel) {
        if (channel != 0) out << ',';
        out << "{\"channelIndex\":" << channel << ",\"total\":"
            << ValuesJson(report.meter.total[channel], report.meterAnalysisAttempted.value_or(false))
            << ",\"currentWindow\":"
            << ValuesJson(report.meter.currentWindow[channel], report.meterAnalysisAttempted.value_or(false))
            << ",\"lastCompletedWindow\":";
        if (!report.meterAnalysisAttempted.value_or(false) || !report.meter.hasCompletedWindow) out << "null";
        else out << ValuesJson(report.meter.lastCompletedWindow[channel], true);
        out << '}';
    }
    out << "]}}";
    return out.str();
}

} // namespace ChannelMeter
