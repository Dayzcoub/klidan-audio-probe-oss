#pragma once

#include "analog_loopback.h"

#include <algorithm>
#include <array>
#include <cmath>
#include <cstdint>
#include <limits>
#include <span>
#include <vector>

namespace MonitorRtlAnalyzer {

constexpr std::uint32_t kRate = 48'000;
constexpr std::uint32_t kMaxFrames = kRate * 5;
constexpr std::uint32_t kMinimumCoveragePercent = 99;
constexpr std::size_t kMaxMarkers = 20;
constexpr double kMaximumGoodVariationFrames = 6.0;      // Existing B4-B1 GOOD budget.
constexpr double kMaximumWarningVariationFrames = 24.0; // Existing B4-B1 WARNING budget.

enum class SampleFormat { Pcm16, Other };
enum class SignalStatus { NotFound, ReferenceOnly, ReturnOnly, Found };
enum class TimingQuality { InsufficientData, Good, Warning, Unreliable };
enum class ValidationStatus { Rejected, SyntheticValidated };
enum class PhysicalRtlStatus { NotMeasured };
enum class FailureReason {
    None, InvalidFormat, InvalidBounds, InsufficientCoverage, InvalidPackets,
    Discontinuity, TimestampError, FrameGap, Clipping, ReferenceMissing,
    ReturnMissing, SequenceRejected, MissingMarkers, WrongPolarity,
    AmbiguousCorrelation, NegativeOffset, NonPositiveDifferentialDelay, DelayVariation
};

inline constexpr const char* ValidationText(ValidationStatus value) noexcept {
    return value == ValidationStatus::SyntheticValidated ? "SYNTHETIC_VALIDATED" : "REJECTED";
}
inline constexpr const char* SignalText(SignalStatus value) noexcept {
    switch (value) {
    case SignalStatus::NotFound: return "NOT_FOUND";
    case SignalStatus::ReferenceOnly: return "REFERENCE_ONLY";
    case SignalStatus::ReturnOnly: return "RETURN_ONLY";
    case SignalStatus::Found: return "SIGNAL_FOUND";
    }
    return "NOT_FOUND";
}
inline constexpr const char* TimingText(TimingQuality value) noexcept {
    switch (value) {
    case TimingQuality::InsufficientData: return "INSUFFICIENT_DATA";
    case TimingQuality::Good: return "GOOD";
    case TimingQuality::Warning: return "WARNING";
    case TimingQuality::Unreliable: return "UNRELIABLE";
    }
    return "UNRELIABLE";
}
inline constexpr const char* PhysicalRtlText(PhysicalRtlStatus) noexcept { return "NOT_MEASURED"; }

struct Packet {
    std::uint64_t firstFrame = 0;
    std::uint32_t frames = 0;
    std::uint64_t qpc100ns = 0;
    bool dataDiscontinuity = false;
    bool timestampError = false;
};

struct Input {
    std::span<const std::int16_t> interleavedPcm;
    std::span<const Packet> packets;
    std::uint32_t rate = kRate;
    std::uint16_t channels = 2;
    SampleFormat format = SampleFormat::Pcm16;
    std::uint64_t expectedFrames = 0;
};

struct MarkerDelay {
    std::size_t markerIndex = 0;
    double referenceFrame = 0;
    double returnFrame = 0;
    double signedOffsetFrames = 0;
    double signedOffsetMilliseconds = 0;
    double referenceCorrelation = 0;
    double returnCorrelation = 0;
};

struct Result {
    ValidationStatus validation = ValidationStatus::Rejected;
    SignalStatus signal = SignalStatus::NotFound;
    TimingQuality timing = TimingQuality::InsufficientData;
    PhysicalRtlStatus physicalRtl = PhysicalRtlStatus::NotMeasured;
    FailureReason failure = FailureReason::None;
    std::size_t expectedMarkers = 0;
    std::size_t referenceMarkersMatched = 0;
    std::size_t returnMarkersMatched = 0;
    std::size_t pairedMarkers = 0;
    std::size_t referenceMarkersMissing = 0;
    std::size_t returnMarkersMissing = 0;
    std::size_t ambiguousSequences = 0;
    std::size_t polarityContradictions = 0;
    double referenceConfidence = 0;
    double returnConfidence = 0;
    double correlationConfidence = 0;
    double medianDelayFrames = std::numeric_limits<double>::quiet_NaN();
    double minimumDelayFrames = std::numeric_limits<double>::quiet_NaN();
    double maximumDelayFrames = std::numeric_limits<double>::quiet_NaN();
    double delaySpreadFrames = std::numeric_limits<double>::quiet_NaN();
    double medianDelayMilliseconds = std::numeric_limits<double>::quiet_NaN();
    double minimumDelayMilliseconds = std::numeric_limits<double>::quiet_NaN();
    double maximumDelayMilliseconds = std::numeric_limits<double>::quiet_NaN();
    double delayTrendFramesAcrossWindow = std::numeric_limits<double>::quiet_NaN();
    double maximumPacketTimestampResidualFrames = 0;
    bool differentialInputChannelDelayCalibrated = false;
    std::array<MarkerDelay, kMaxMarkers> markerDelays{};
    std::size_t markerDelayCount = 0;
};

inline std::uint64_t MinimumCapturedFrames(std::uint64_t expectedFrames) noexcept {
    return (expectedFrames / 100) * kMinimumCoveragePercent +
        ((expectedFrames % 100) * kMinimumCoveragePercent + 99) / 100;
}

inline TimingQuality ClassifyTimingVariation(double spreadFrames, double trendFrames) noexcept {
    if (!std::isfinite(spreadFrames) || !std::isfinite(trendFrames)) return TimingQuality::Unreliable;
    const double variation = std::max(spreadFrames, std::abs(trendFrames));
    return variation <= kMaximumGoodVariationFrames ? TimingQuality::Good
        : variation <= kMaximumWarningVariationFrames ? TimingQuality::Warning
        : TimingQuality::Unreliable;
}

inline bool ValidatePackets(const Input& input, std::uint64_t frames, Result& result) noexcept {
    if (input.packets.empty()) { result.failure = FailureReason::InvalidPackets; return false; }
    std::uint64_t nextFrame = 0;
    std::uint64_t previousFrame = 0;
    std::uint64_t previousQpc = 0;
    bool havePrevious = false;
    constexpr long double qpcUnitsPerFrame = 10'000'000.0L / kRate;
    for (const auto& packet : input.packets) {
        if (packet.frames == 0 || packet.firstFrame != nextFrame ||
            packet.frames > std::numeric_limits<std::uint64_t>::max() - nextFrame) {
            result.failure = FailureReason::FrameGap;
            return false;
        }
        if (packet.dataDiscontinuity) { result.failure = FailureReason::Discontinuity; return false; }
        if (packet.timestampError || packet.qpc100ns == 0 ||
            (previousQpc != 0 && packet.qpc100ns <= previousQpc)) {
            result.failure = FailureReason::TimestampError;
            return false;
        }
        if (havePrevious) {
            const long double frameDelta = static_cast<long double>(packet.firstFrame - previousFrame);
            const long double expectedQpcDelta = frameDelta * qpcUnitsPerFrame;
            const auto actualQpcDelta = packet.qpc100ns - previousQpc;
            const double residualFrames = static_cast<double>(
                std::abs(static_cast<long double>(actualQpcDelta) - expectedQpcDelta) / qpcUnitsPerFrame);
            result.maximumPacketTimestampResidualFrames = std::max(
                result.maximumPacketTimestampResidualFrames, residualFrames);
            if (residualFrames > kMaximumWarningVariationFrames) {
                result.failure = FailureReason::TimestampError;
                return false;
            }
        }
        previousFrame = packet.firstFrame;
        previousQpc = packet.qpc100ns;
        havePrevious = true;
        nextFrame += packet.frames;
    }
    if (nextFrame != frames) { result.failure = FailureReason::FrameGap; return false; }
    return true;
}

inline double Median(std::array<double, kMaxMarkers>& values, std::size_t count) noexcept {
    std::sort(values.begin(), values.begin() + count);
    if ((count & 1U) != 0) return values[count / 2];
    return (values[count / 2 - 1] + values[count / 2]) * 0.5;
}

inline Result Analyze(const Input& input) noexcept {
    Result result;
    if (input.rate != kRate || input.channels != 2 || input.format != SampleFormat::Pcm16) {
        result.failure = FailureReason::InvalidFormat;
        result.timing = TimingQuality::Unreliable;
        return result;
    }
    if (input.interleavedPcm.empty() || (input.interleavedPcm.size() & 1U) != 0 ||
        input.expectedFrames != kMaxFrames) {
        result.failure = FailureReason::InvalidBounds;
        return result;
    }
    const auto frames = static_cast<std::uint64_t>(input.interleavedPcm.size() / 2);
    if (frames > kMaxFrames || frames < MinimumCapturedFrames(kMaxFrames)) {
        result.failure = FailureReason::InsufficientCoverage;
        return result;
    }
    if (!ValidatePackets(input, frames, result)) {
        result.timing = TimingQuality::Unreliable;
        return result;
    }
    for (const auto sample : input.interleavedPcm) {
        if (sample == INT16_MIN || sample == INT16_MAX) {
            result.failure = FailureReason::Clipping;
            result.timing = TimingQuality::Unreliable;
            return result;
        }
    }

    try {
        AnalogLoopback::RenderSignal markerSchedule(1);
        if (!markerSchedule.Configure(kRate, 1, 0, 5)) {
            result.failure = FailureReason::InvalidBounds;
            return result;
        }
        const auto marker = std::span<const std::int16_t>(markerSchedule.Marker());
        const auto starts = markerSchedule.MarkerStarts();
        while (result.expectedMarkers < starts.size() &&
               starts[result.expectedMarkers] + marker.size() <= frames) ++result.expectedMarkers;
        if (result.expectedMarkers < AnalogLoopback::kMinimumMarkers) {
            result.failure = FailureReason::InsufficientCoverage;
            return result;
        }

        std::vector<std::int16_t> left(static_cast<std::size_t>(frames));
        std::vector<std::int16_t> right(static_cast<std::size_t>(frames));
        for (std::size_t frame = 0; frame < frames; ++frame) {
            left[frame] = input.interleavedPcm[frame * 2];
            right[frame] = input.interleavedPcm[frame * 2 + 1];
        }
        const auto leftSequence = AnalogLoopback::DetectCoherentSequence(
            AnalogLoopback::DiagnoseMarkers(left, marker), markerSchedule);
        const auto rightSequence = AnalogLoopback::DetectCoherentSequence(
            AnalogLoopback::DiagnoseMarkers(right, marker), markerSchedule);
        result.referenceConfidence = leftSequence.meanMatchedCorrelation;
        result.returnConfidence = rightSequence.meanMatchedCorrelation;
        result.ambiguousSequences =
            static_cast<std::size_t>(leftSequence.status == AnalogLoopback::SequenceStatus::Ambiguous) +
            static_cast<std::size_t>(rightSequence.status == AnalogLoopback::SequenceStatus::Ambiguous);
        result.polarityContradictions = leftSequence.contradictoryMarkers + rightSequence.contradictoryMarkers;
        result.signal = leftSequence.matchedMarkers != 0 && rightSequence.matchedMarkers != 0
            ? SignalStatus::Found
            : leftSequence.matchedMarkers != 0 ? SignalStatus::ReferenceOnly
            : rightSequence.matchedMarkers != 0 ? SignalStatus::ReturnOnly : SignalStatus::NotFound;
        result.referenceMarkersMatched = static_cast<std::size_t>(std::count_if(
            leftSequence.matches.begin(), leftSequence.matches.end(), [&](const auto& m) {
                return m.markerIndex < result.expectedMarkers;
            }));
        result.returnMarkersMatched = static_cast<std::size_t>(std::count_if(
            rightSequence.matches.begin(), rightSequence.matches.end(), [&](const auto& m) {
                return m.markerIndex < result.expectedMarkers;
            }));
        result.referenceMarkersMissing = result.expectedMarkers - result.referenceMarkersMatched;
        result.returnMarkersMissing = result.expectedMarkers - result.returnMarkersMatched;
        if (leftSequence.status == AnalogLoopback::SequenceStatus::Ambiguous ||
            rightSequence.status == AnalogLoopback::SequenceStatus::Ambiguous) {
            result.failure = FailureReason::AmbiguousCorrelation;
            result.timing = TimingQuality::Unreliable;
            return result;
        }
        if (leftSequence.status != AnalogLoopback::SequenceStatus::Matched ||
            rightSequence.status != AnalogLoopback::SequenceStatus::Matched) {
            result.failure = result.signal == SignalStatus::ReferenceOnly ? FailureReason::ReturnMissing
                : result.signal == SignalStatus::ReturnOnly ? FailureReason::ReferenceMissing
                : FailureReason::SequenceRejected;
            result.timing = TimingQuality::Unreliable;
            return result;
        }
        if (result.polarityContradictions != 0) {
            result.failure = FailureReason::WrongPolarity;
            result.timing = TimingQuality::Unreliable;
            return result;
        }
        if (result.referenceMarkersMissing != 0 || result.returnMarkersMissing != 0) {
            result.failure = FailureReason::MissingMarkers;
            result.timing = TimingQuality::Unreliable;
            return result;
        }
        std::array<const AnalogLoopback::CorrelationPeak*, kMaxMarkers> refPeaks{};
        std::array<const AnalogLoopback::CorrelationPeak*, kMaxMarkers> returnPeaks{};
        for (const auto& match : leftSequence.matches)
            if (match.markerIndex < result.expectedMarkers) refPeaks[match.markerIndex] = &match.peak;
        for (const auto& match : rightSequence.matches)
            if (match.markerIndex < result.expectedMarkers) returnPeaks[match.markerIndex] = &match.peak;

        std::array<double, kMaxMarkers> delays{};
        double delaySum = 0;
        for (std::size_t index = 0; index < result.expectedMarkers; ++index) {
            if (refPeaks[index] == nullptr || returnPeaks[index] == nullptr) {
                result.failure = FailureReason::MissingMarkers;
                result.timing = TimingQuality::Unreliable;
                return result;
            }
            const double offset = returnPeaks[index]->offsetFrames - refPeaks[index]->offsetFrames;
            if (offset <= 0) {
                result.failure = offset < 0 ? FailureReason::NegativeOffset
                    : FailureReason::NonPositiveDifferentialDelay;
                result.timing = TimingQuality::Unreliable;
                return result;
            }
            auto& delay = result.markerDelays[result.markerDelayCount++];
            delay = {index, refPeaks[index]->offsetFrames, returnPeaks[index]->offsetFrames,
                offset, offset * 1000.0 / kRate,
                refPeaks[index]->confidence, returnPeaks[index]->confidence};
            delays[index] = offset;
            delaySum += offset;
        }
        result.pairedMarkers = result.markerDelayCount;
        result.correlationConfidence = std::min(result.referenceConfidence, result.returnConfidence);
        result.minimumDelayFrames = *std::min_element(delays.begin(), delays.begin() + result.markerDelayCount);
        result.maximumDelayFrames = *std::max_element(delays.begin(), delays.begin() + result.markerDelayCount);
        result.delaySpreadFrames = result.maximumDelayFrames - result.minimumDelayFrames;
        result.medianDelayFrames = Median(delays, result.markerDelayCount);
        result.minimumDelayMilliseconds = result.minimumDelayFrames * 1000.0 / kRate;
        result.maximumDelayMilliseconds = result.maximumDelayFrames * 1000.0 / kRate;
        result.medianDelayMilliseconds = result.medianDelayFrames * 1000.0 / kRate;

        double meanIndex = (result.markerDelayCount - 1) * 0.5;
        const double meanDelay = delaySum / result.markerDelayCount;
        double covariance = 0;
        double variance = 0;
        for (std::size_t i = 0; i < result.markerDelayCount; ++i) {
            covariance += (static_cast<double>(i) - meanIndex) *
                (result.markerDelays[i].signedOffsetFrames - meanDelay);
            variance += (static_cast<double>(i) - meanIndex) * (static_cast<double>(i) - meanIndex);
        }
        const double slope = variance > 0 ? covariance / variance : 0;
        result.delayTrendFramesAcrossWindow = slope * (result.markerDelayCount - 1);
        result.timing = ClassifyTimingVariation(result.delaySpreadFrames,
                                                result.delayTrendFramesAcrossWindow);
        if (result.timing != TimingQuality::Good) {
            result.failure = FailureReason::DelayVariation;
            return result;
        }
        if (result.maximumPacketTimestampResidualFrames > kMaximumGoodVariationFrames) {
            result.timing = TimingQuality::Warning;
            result.failure = FailureReason::TimestampError;
            return result;
        }
        result.validation = ValidationStatus::SyntheticValidated;
        result.failure = FailureReason::None;
        return result;
    } catch (...) {
        result.validation = ValidationStatus::Rejected;
        result.timing = TimingQuality::Unreliable;
        result.failure = FailureReason::InvalidBounds;
        return result;
    }
}

// This state is deliberately process-local and non-serializable: every run must create a fresh lifecycle.
enum class LifecycleState { Idle, CapturePreflight, Validating, Armed, Revalidating, Measuring, Stopping, Completed, Failed, Cancelled };

struct Revalidation {
    bool operatorApproved = false;
    bool physicalFixtureConfirmed = false;
    bool endpointsRechecked = false;
    bool ringFreshAndEmpty = false;
    bool captureReady = false;
    bool renderSilent = false;
};

class Lifecycle {
public:
    LifecycleState State() const noexcept { return state_; }
    bool BeginCapturePreflight() noexcept { return Move(LifecycleState::Idle, LifecycleState::CapturePreflight); }
    bool BeginValidation() noexcept { return Move(LifecycleState::CapturePreflight, LifecycleState::Validating); }
    bool Arm(bool preflightAccepted) noexcept {
        if (state_ != LifecycleState::Validating || !preflightAccepted) return Fail();
        return Move(LifecycleState::Validating, LifecycleState::Armed);
    }
    bool RevalidateForThisRun(const Revalidation& checks) noexcept {
        if (state_ != LifecycleState::Armed) return Fail();
        if (!checks.operatorApproved || !checks.physicalFixtureConfirmed || !checks.endpointsRechecked ||
            !checks.ringFreshAndEmpty || !checks.captureReady || !checks.renderSilent) return Fail();
        return Move(LifecycleState::Armed, LifecycleState::Revalidating);
    }
    bool BeginMeasurement() noexcept { return Move(LifecycleState::Revalidating, LifecycleState::Measuring); }
    bool FailSession() noexcept { return Fail(); }
    bool Stop(bool succeeded) noexcept {
        if (!Move(LifecycleState::Measuring, LifecycleState::Stopping)) return false;
        return Move(LifecycleState::Stopping, succeeded ? LifecycleState::Completed : LifecycleState::Failed);
    }
    bool Cancel() noexcept {
        if (state_ == LifecycleState::Completed || state_ == LifecycleState::Failed ||
            state_ == LifecycleState::Cancelled) return false;
        state_ = LifecycleState::Cancelled;
        return true;
    }
private:
    bool Move(LifecycleState expected, LifecycleState next) noexcept {
        if (state_ != expected) return false;
        state_ = next;
        return true;
    }
    bool Fail() noexcept {
        if (state_ == LifecycleState::Completed || state_ == LifecycleState::Cancelled) return false;
        state_ = LifecycleState::Failed;
        return false;
    }
    LifecycleState state_ = LifecycleState::Idle;
};

} // namespace MonitorRtlAnalyzer
