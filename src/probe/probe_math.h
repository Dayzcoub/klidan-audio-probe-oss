#pragma once

#include <algorithm>
#include <chrono>
#include <cstdint>
#include <vector>

namespace ProbeMath {

constexpr std::int64_t kHnsPerSecond = 10'000'000;

constexpr std::uint64_t FramesForDuration(std::int64_t duration, std::uint32_t rate) {
    return (static_cast<std::uint64_t>(duration) * rate + kHnsPerSecond / 2) / kHnsPerSecond;
}

constexpr std::int64_t DurationForFrames(std::uint32_t frames, std::uint32_t rate) {
    return static_cast<std::int64_t>((static_cast<std::uint64_t>(frames) * kHnsPerSecond + rate / 2) / rate);
}

constexpr std::int64_t SelectDevicePeriod(std::int64_t defaultPeriod, std::int64_t minimumPeriod,
                                          bool useMinimum) {
    return useMinimum ? minimumPeriod : defaultPeriod;
}

constexpr std::uint32_t kMaximumEventWaitMilliseconds = 1'000;
constexpr auto kEventWatchdog = std::chrono::milliseconds(kMaximumEventWaitMilliseconds);

constexpr bool IsValidStreamDuration(std::uint32_t seconds) {
    return seconds >= 1 && seconds <= 30;
}

constexpr bool IsValidSustainedDuplexDuration(std::uint32_t seconds) {
    return seconds == 60 || seconds == 300 || seconds == 600;
}

constexpr std::uint32_t BoundedEventWaitMilliseconds(std::uint64_t remainingMilliseconds) {
    return static_cast<std::uint32_t>(remainingMilliseconds < kMaximumEventWaitMilliseconds
        ? remainingMilliseconds : kMaximumEventWaitMilliseconds);
}

constexpr double ExpectedIntervalMilliseconds(std::uint32_t bufferFrames, std::uint32_t rate) {
    return rate == 0 ? 0.0 : static_cast<double>(bufferFrames) * 1'000.0 / rate;
}

constexpr std::uint64_t ExpectedFramesForMicroseconds(std::uint64_t elapsedMicroseconds,
                                                       std::uint32_t rate) {
    return (elapsedMicroseconds * rate + 500'000) / 1'000'000;
}

constexpr std::uint64_t FrameTolerance(std::uint64_t expectedFrames, std::uint32_t bufferFrames) {
    const auto edgeTolerance = static_cast<std::uint64_t>(bufferFrames) * 2;
    return std::max(edgeTolerance, expectedFrames / 10);
}

constexpr bool HasSufficientFrames(std::uint64_t actualFrames, std::uint64_t expectedFrames,
                                   std::uint32_t bufferFrames) {
    return actualFrames >= expectedFrames || expectedFrames - actualFrames <= FrameTolerance(expectedFrames, bufferFrames);
}

constexpr bool IsLongEventInterval(double intervalMilliseconds, double expectedMilliseconds) {
    return expectedMilliseconds > 0 && intervalMilliseconds * 2 > expectedMilliseconds * 3;
}

enum class PositionTransition { Initial, Contiguous, Jump, Overlap, TimestampInvalid };

struct PositionDelta {
    PositionTransition transition;
    std::uint64_t frames;
};

constexpr PositionDelta CompareDevicePosition(bool hasPrevious, std::uint64_t previousPacketEnd,
                                               std::uint64_t currentPacketStart, bool timestampValid) {
    if (!timestampValid) return {PositionTransition::TimestampInvalid, 0};
    if (!hasPrevious) return {PositionTransition::Initial, 0};
    if (currentPacketStart > previousPacketEnd)
        return {PositionTransition::Jump, currentPacketStart - previousPacketEnd};
    if (currentPacketStart < previousPacketEnd)
        return {PositionTransition::Overlap, previousPacketEnd - currentPacketStart};
    return {PositionTransition::Contiguous, 0};
}

constexpr bool CanStartStream(bool setupReady, bool cancelled, bool mmcssRequested, bool mmcssRegistered) {
    return setupReady && !cancelled && (!mmcssRequested || mmcssRegistered);
}

enum class WaitDecision { ContinueWaiting, ProcessEvent, DeadlineReached, WatchdogTimeout, Cancelled, ApiFailure };

constexpr WaitDecision DecideWaitOutcome(bool eventSignaled, bool cancelled, bool apiFailure,
                                         std::chrono::nanoseconds remainingToDeadline,
                                         std::chrono::nanoseconds sinceLastEvent,
                                         std::chrono::nanoseconds watchdog) {
    if (apiFailure) return WaitDecision::ApiFailure;
    if (cancelled) return WaitDecision::Cancelled;
    if (sinceLastEvent >= watchdog) return WaitDecision::WatchdogTimeout;
    if (remainingToDeadline <= std::chrono::nanoseconds::zero()) return WaitDecision::DeadlineReached;
    return eventSignaled ? WaitDecision::ProcessEvent : WaitDecision::ContinueWaiting;
}

inline double Percentile(const std::vector<double>& sortedValues, unsigned percentile) {
    if (sortedValues.empty() || percentile == 0 || percentile > 100) return 0.0;
    const std::size_t rank = (sortedValues.size() * percentile + 99) / 100;
    return sortedValues[rank - 1];
}

enum class StreamStatus { InitializeFailed, StartFailed, StreamFailed, StreamCompletedWithWarnings, StreamCompleted };
enum class StreamQuality { Clean, AnomaliesPresent, SevereAnomalies };

enum class DuplexPreparation { Waiting, Abort, StartTogether };

constexpr DuplexPreparation DecideDuplexPreparation(bool captureReported, bool captureReady,
                                                      bool renderReported, bool renderReady) {
    if ((captureReported && !captureReady) || (renderReported && !renderReady))
        return DuplexPreparation::Abort;
    if (!captureReported || !renderReported) return DuplexPreparation::Waiting;
    return DuplexPreparation::StartTogether;
}

constexpr bool ShouldStopDuplexPeer(bool criticalError, bool startFailed, bool cancelled) {
    return criticalError || startFailed || cancelled;
}

constexpr StreamStatus ClassifyDuplex(StreamStatus capture, StreamStatus render) {
    if (capture == StreamStatus::InitializeFailed || render == StreamStatus::InitializeFailed)
        return StreamStatus::InitializeFailed;
    if (capture == StreamStatus::StartFailed || render == StreamStatus::StartFailed)
        return StreamStatus::StartFailed;
    if (capture == StreamStatus::StreamFailed || render == StreamStatus::StreamFailed)
        return StreamStatus::StreamFailed;
    if (capture == StreamStatus::StreamCompletedWithWarnings ||
        render == StreamStatus::StreamCompletedWithWarnings)
        return StreamStatus::StreamCompletedWithWarnings;
    return StreamStatus::StreamCompleted;
}

constexpr StreamQuality ClassifyDuplexQuality(StreamQuality capture, StreamQuality render) {
    if (capture == StreamQuality::SevereAnomalies || render == StreamQuality::SevereAnomalies)
        return StreamQuality::SevereAnomalies;
    if (capture == StreamQuality::AnomaliesPresent || render == StreamQuality::AnomaliesPresent)
        return StreamQuality::AnomaliesPresent;
    return StreamQuality::Clean;
}

constexpr std::int64_t DuplexOverlapMicroseconds(std::int64_t captureStart, std::int64_t captureEnd,
                                                 std::int64_t renderStart, std::int64_t renderEnd) {
    const auto start = std::max(captureStart, renderStart);
    const auto end = std::min(captureEnd, renderEnd);
    return std::max<std::int64_t>(0, end - start);
}

constexpr StreamStatus ClassifyStream(bool initialized, bool startAttempted, bool started,
                                      bool durationReached, std::uint64_t processedEvents,
                                      std::uint64_t processedFrames, bool sufficientFrames,
                                      bool warning, bool criticalError, bool stopSucceeded) {
    if (!initialized) return StreamStatus::InitializeFailed;
    if (!startAttempted) return StreamStatus::StreamFailed;
    if (!started) return StreamStatus::StartFailed;
    if (!durationReached || processedEvents == 0 || processedFrames == 0 || !sufficientFrames ||
        criticalError || !stopSucceeded) return StreamStatus::StreamFailed;
    return warning ? StreamStatus::StreamCompletedWithWarnings : StreamStatus::StreamCompleted;
}

constexpr StreamQuality ClassifyStreamQuality(bool streamReachedEnd, bool sufficientFrames, bool anyWarning,
                                               std::uint64_t discontinuities,
                                               std::uint64_t positionAnomalies,
                                               std::uint64_t timestampErrors,
                                               std::uint64_t bufferUnavailable,
                                               std::uint64_t longIntervals,
                                               std::uint64_t packetDrainLimitHits,
                                               std::uint64_t maximumPositionDeviation,
                                               std::uint32_t bufferFrames) {
    if (!streamReachedEnd || !sufficientFrames || discontinuities >= 3 || positionAnomalies >= 3 ||
        timestampErrors >= 3 || bufferUnavailable >= 3 || longIntervals >= 3 || packetDrainLimitHits >= 3 ||
        maximumPositionDeviation >= bufferFrames) return StreamQuality::SevereAnomalies;
    return anyWarning || discontinuities != 0 || positionAnomalies != 0 || timestampErrors != 0 ||
           bufferUnavailable != 0 || longIntervals != 0 || packetDrainLimitHits != 0
        ? StreamQuality::AnomaliesPresent : StreamQuality::Clean;
}

}  // namespace ProbeMath
