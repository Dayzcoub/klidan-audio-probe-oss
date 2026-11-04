#pragma once

#include "analog_loopback.h"
#include "live_transport.h"

#include <algorithm>
#include <array>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <limits>
#include <span>
#include <string_view>
#include <vector>

namespace MonitorLatency {

constexpr std::size_t kMaximumMarkers = 32;
constexpr std::size_t kMinimumGoodAnchors = 8;
constexpr std::size_t kMinimumMeasuredMarkers = 5;
constexpr std::size_t kLocalFitAnchorCount = 16;
constexpr std::size_t kMinimumLocalAnchors = 12;
constexpr double kMaximumLocalOutlierFraction = 0.20;
constexpr double kTimestampStepThresholdMilliseconds = 0.5;
constexpr double kMaximumGoodResidualFrames = AnalogLoopback::kGoodTimingErrorFrames;

struct CapturePacket {
    std::uint64_t devicePosition = 0;
    std::uint64_t qpc100ns = 0;
    std::uint64_t sourceFrame = 0;
    std::uint32_t frames = 0;
    bool discontinuity = false;
    bool timestampError = false;
    std::uint64_t serviceQpc100ns = 0;
};

struct CaptureMarker {
    std::uint32_t markerIndex = 0;
    std::uint64_t sourceFrame = 0;
    std::uint64_t devicePosition = 0;
    std::uint64_t qpc100ns = 0;
    std::uint64_t generatedBeginQpc100ns = 0;
    std::uint64_t generatedEndQpc100ns = 0;
    std::uint64_t ringInsertBeginQpc100ns = 0;
    std::uint64_t ringInsertEndQpc100ns = 0;
    std::uint64_t ringOccupancyFrames = 0;
    std::size_t packetIndex = 0;
    bool timestampValid = false;
};

struct RenderMarker {
    std::uint32_t markerIndex = 0;
    std::uint64_t sourceFrame = 0;
    std::uint64_t renderFrame = 0;
    std::uint64_t lastRenderFrame = 0;
    std::uint64_t ringOccupancyFrames = 0;
    std::uint64_t firstSubmissionBeginQpc100ns = 0;
    std::uint64_t firstSubmissionEndQpc100ns = 0;
    std::uint64_t submissionBeginQpc100ns = 0;
    std::uint64_t submissionEndQpc100ns = 0;
    bool samplesVerified = false;
};

struct RunData {
    std::vector<CapturePacket> capturePackets;
    std::vector<CaptureMarker> captureMarkers;
    std::vector<RenderMarker> renderMarkers;
    std::vector<std::uint8_t> captureClockQpcBracketValid;
    bool captureStorageFull = false;
    bool renderStorageFull = false;
    std::size_t captureTimestampErrors = 0;
    std::size_t captureDiscontinuities = 0;
    std::size_t captureClockQpcBracketErrors = 0;
    std::uint64_t qpcFrequency = 0;
    std::size_t expectedMarkers = 0;

    bool Configure(std::size_t maximumPackets, std::size_t maximumMarkers) noexcept {
        if (maximumPackets == 0 || maximumMarkers == 0 || maximumMarkers > kMaximumMarkers) return false;
        try {
            capturePackets.reserve(maximumPackets);
            captureMarkers.reserve(maximumMarkers);
            renderMarkers.reserve(maximumMarkers);
            captureClockQpcBracketValid.reserve(maximumPackets);
        } catch (...) {
            return false;
        }
        return true;
    }

    bool RecordCapturePacket(const CapturePacket& packet) noexcept {
        if (capturePackets.size() == capturePackets.capacity()) {
            captureStorageFull = true;
            return false;
        }
        if (packet.timestampError) ++captureTimestampErrors;
        if (packet.discontinuity) ++captureDiscontinuities;
        capturePackets.push_back(packet);
        return true;
    }

    bool RecordCaptureMarker(const CaptureMarker& marker) noexcept {
        if (captureMarkers.size() == captureMarkers.capacity()) {
            captureStorageFull = true;
            return false;
        }
        captureMarkers.push_back(marker);
        return true;
    }

    bool RecordCaptureClockQpcBracket(bool valid) noexcept {
        if (captureClockQpcBracketValid.size() == captureClockQpcBracketValid.capacity()) {
            captureStorageFull = true;
            return false;
        }
        captureClockQpcBracketValid.push_back(valid ? 1 : 0);
        if (!valid) ++captureClockQpcBracketErrors;
        return true;
    }

    bool RecordRenderMarker(const RenderMarker& marker) noexcept {
        if (renderMarkers.size() == renderMarkers.capacity()) {
            renderStorageFull = true;
            return false;
        }
        renderMarkers.push_back(marker);
        return true;
    }

};

class RenderTracker {
public:
    void Reset() noexcept {
        markerIndex_ = 0;
        progress_ = 0;
        markerRenderStart_ = 0;
        currentBufferStartCount_ = 0;
        pendingCount_ = 0;
        firstFrames_ = {};
    }

    void InspectBuffer(std::span<const std::int16_t> pcm, std::span<const std::uint64_t> sourceFrames,
                       std::uint64_t firstRenderFrame, const AnalogLoopback::RenderSignal& signal) noexcept {
        pendingCount_ = 0;
        currentBufferStartCount_ = 0;
        const auto starts = signal.MarkerStarts();
        const auto marker = signal.Marker();
        const auto channel = signal.Channel() - 1;
        const auto frameCount = std::min(sourceFrames.size(), pcm.size() / 2);
        for (std::size_t frame = 0; frame < frameCount; ++frame) {
            const auto source = sourceFrames[frame];
            if (source == LiveTransport::kNoSourceFrame) {
                if (progress_ != 0 && markerIndex_ < firstFrames_.size())
                    firstFrames_[markerIndex_].valid = false;
                progress_ = 0;
                continue;
            }
            while (markerIndex_ < starts.size() &&
                   source >= starts[markerIndex_] + marker.size()) {
                ++markerIndex_;
                progress_ = 0;
            }
            if (markerIndex_ >= starts.size() || source < starts[markerIndex_]) continue;
            const auto offset = static_cast<std::size_t>(source - starts[markerIndex_]);
            if (offset >= marker.size()) continue;
            const auto expected = ApplyGain(static_cast<std::int32_t>(marker[offset]) *
                AnalogLoopback::RenderSignal::MarkerPolarity(markerIndex_));
            const auto left = pcm[frame * 2];
            const auto right = pcm[frame * 2 + 1];
            const bool sampleMatches = channel == 0 ? left == expected && right == 0
                                                     : right == expected && left == 0;
            if (offset != progress_ || !sampleMatches) {
                if (progress_ != 0 && markerIndex_ < firstFrames_.size())
                    firstFrames_[markerIndex_].valid = false;
                progress_ = 0;
                if (offset != 0 || !sampleMatches) continue;
            }
            if (offset == 0) {
                markerRenderStart_ = firstRenderFrame + frame;
                firstFrames_[markerIndex_] = {source, markerRenderStart_, 0, 0, true};
                if (currentBufferStartCount_ < currentBufferStarts_.size())
                    currentBufferStarts_[currentBufferStartCount_++] = static_cast<std::uint32_t>(markerIndex_);
            }
            ++progress_;
            if (progress_ == marker.size()) {
                if (pendingCount_ < pending_.size()) {
                    auto& completed = pending_[pendingCount_++];
                    completed.markerIndex = static_cast<std::uint32_t>(markerIndex_);
                    completed.sourceFrame = starts[markerIndex_];
                    completed.renderFrame = markerRenderStart_;
                    completed.lastRenderFrame = firstRenderFrame + frame;
                    completed.samplesVerified = true;
                }
                ++markerIndex_;
                progress_ = 0;
            }
        }
    }

    bool CommitBuffer(RunData& data, std::uint64_t occupancy,
                      std::uint64_t submissionBeginQpc100ns,
                      std::uint64_t submissionEndQpc100ns) noexcept {
        bool success = true;
        const bool validSubmission = submissionBeginQpc100ns != 0 &&
            submissionEndQpc100ns >= submissionBeginQpc100ns;
        for (std::size_t i = 0; i < currentBufferStartCount_; ++i) {
            auto& first = firstFrames_[currentBufferStarts_[i]];
            if (validSubmission) {
                first.submissionBeginQpc100ns = submissionBeginQpc100ns;
                first.submissionEndQpc100ns = submissionEndQpc100ns;
            }
        }
        for (std::size_t i = 0; i < pendingCount_; ++i) {
            pending_[i].ringOccupancyFrames = occupancy;
            const auto& first = firstFrames_[pending_[i].markerIndex];
            if (first.valid) {
                pending_[i].sourceFrame = first.sourceFrame;
                pending_[i].renderFrame = first.renderFrame;
                pending_[i].firstSubmissionBeginQpc100ns = first.submissionBeginQpc100ns;
                pending_[i].firstSubmissionEndQpc100ns = first.submissionEndQpc100ns;
            }
            if (validSubmission) {
                pending_[i].submissionBeginQpc100ns = submissionBeginQpc100ns;
                pending_[i].submissionEndQpc100ns = submissionEndQpc100ns;
            }
            success = data.RecordRenderMarker(pending_[i]) && success;
        }
        currentBufferStartCount_ = 0;
        pendingCount_ = 0;
        return success;
    }

private:
    struct FirstFrameState {
        std::uint64_t sourceFrame = 0;
        std::uint64_t renderFrame = 0;
        std::uint64_t submissionBeginQpc100ns = 0;
        std::uint64_t submissionEndQpc100ns = 0;
        bool valid = false;
    };

    static std::int16_t ApplyGain(std::int32_t sample) noexcept {
        const auto scaled = static_cast<long>(std::lrint(static_cast<double>(sample) * LiveTransport::kOutputGain));
        return static_cast<std::int16_t>(std::clamp<long>(scaled, -32768, 32767));
    }

    std::array<RenderMarker, kMaximumMarkers> pending_{};
    std::array<FirstFrameState, kMaximumMarkers> firstFrames_{};
    std::array<std::uint32_t, kMaximumMarkers> currentBufferStarts_{};
    std::size_t currentBufferStartCount_ = 0;
    std::size_t pendingCount_ = 0;
    std::size_t markerIndex_ = 0;
    std::size_t progress_ = 0;
    std::uint64_t markerRenderStart_ = 0;
};

enum class Status { Measured, Unreliable, SignalNotFound, Failed };
enum class SequenceStatus { Matched, Partial, NotFound };
enum class TimingQuality { Good, Warning, Unreliable };

inline const char* StatusText(Status status) noexcept {
    switch (status) {
    case Status::Measured: return "MEASURED";
    case Status::Unreliable: return "UNRELIABLE";
    case Status::SignalNotFound: return "SIGNAL_NOT_FOUND";
    case Status::Failed: return "FAILED";
    }
    return "FAILED";
}

inline const char* SequenceStatusText(SequenceStatus status) noexcept {
    switch (status) {
    case SequenceStatus::Matched: return "MATCHED";
    case SequenceStatus::Partial: return "PARTIAL";
    case SequenceStatus::NotFound: return "NOT_FOUND";
    }
    return "NOT_FOUND";
}

inline const char* TimingQualityText(TimingQuality quality) noexcept {
    switch (quality) {
    case TimingQuality::Good: return "GOOD";
    case TimingQuality::Warning: return "WARNING";
    case TimingQuality::Unreliable: return "UNRELIABLE";
    }
    return "UNRELIABLE";
}

struct Segment {
    std::size_t firstAnchor = 0;
    std::size_t lastAnchor = 0;
    std::size_t anchorCount = 0;
    std::uint64_t firstFrame = 0;
    std::uint64_t lastFrame = 0;
    std::uint64_t firstQpc100ns = 0;
    std::uint64_t lastQpc100ns = 0;
    long double qpcIntercept100ns = 0;
    long double qpcUnitsPerFrame = 0;
    double rmsResidualFrames = std::numeric_limits<double>::infinity();
    double maximumResidualFrames = std::numeric_limits<double>::infinity();
    bool fitted = false;
    const char* rejectionReason = "insufficient anchors";
};

enum class MarkerReason {
    Accepted, CaptureLocalTimingRejected, RenderLocalTimingRejected, CrossesQpcBoundary,
    InsufficientLocalAnchors, InvalidTimestamp, Other
};

inline const char* MarkerReasonText(MarkerReason reason) noexcept {
    switch (reason) {
    case MarkerReason::Accepted: return "ACCEPTED";
    case MarkerReason::CaptureLocalTimingRejected: return "CAPTURE_LOCAL_TIMING_REJECTED";
    case MarkerReason::RenderLocalTimingRejected: return "RENDER_LOCAL_TIMING_REJECTED";
    case MarkerReason::CrossesQpcBoundary: return "CROSSES_QPC_BOUNDARY";
    case MarkerReason::InsufficientLocalAnchors: return "INSUFFICIENT_LOCAL_ANCHORS";
    case MarkerReason::InvalidTimestamp: return "INVALID_TIMESTAMP";
    case MarkerReason::Other: return "OTHER";
    }
    return "OTHER";
}

struct LocalFit {
    std::size_t candidateAnchors = 0;
    std::size_t anchorCount = 0;
    std::size_t rejectedOutliers = 0;
    std::size_t firstAnchorIndex = 0;
    std::size_t lastAnchorIndex = 0;
    std::array<std::size_t, kLocalFitAnchorCount> outlierIndices{};
    double rmsResidualFrames = std::numeric_limits<double>::infinity();
    double maximumResidualFrames = std::numeric_limits<double>::infinity();
    long double intercept100ns = 0;
    long double qpcUnitsPerFrame = 0;
    bool fitted = false;
    const char* rejectionReason = "insufficient local anchors";
};

struct MarkerDiagnostic {
    std::uint32_t markerIndex = 0;
    MarkerReason reason = MarkerReason::Other;
    const char* detail = "marker provenance unavailable";
    LocalFit captureFit{};
    LocalFit captureClockFit{};
    double captureClockAgreementFrames = std::numeric_limits<double>::infinity();
    double captureClockOffsetFrames = std::numeric_limits<double>::quiet_NaN();
    double captureClockRateDifferencePpm = std::numeric_limits<double>::quiet_NaN();
    double softwareFirstFrameMilliseconds = std::numeric_limits<double>::quiet_NaN();
    double softwareTransportMilliseconds = std::numeric_limits<double>::quiet_NaN();
    std::uint64_t sourceFirstFrame = 0;
    std::uint64_t renderFirstFrame = 0;
    std::uint64_t renderLastFrame = 0;
    std::uint64_t firstSubmissionBeginQpc100ns = 0;
    std::uint64_t firstSubmissionEndQpc100ns = 0;
    std::uint64_t fullSubmissionBeginQpc100ns = 0;
    std::uint64_t fullSubmissionEndQpc100ns = 0;
    std::uint64_t packetQpc100ns = 0;
    std::uint64_t packetServiceQpc100ns = 0;
    std::uint64_t markerQpc100ns = 0;
    std::uint64_t markerGenerationBeginQpc100ns = 0;
    std::uint64_t markerGenerationEndQpc100ns = 0;
    std::uint64_t ringPushBeginQpc100ns = 0;
    std::uint64_t ringPushEndQpc100ns = 0;
    std::uint64_t renderSubmissionBeginQpc100ns = 0;
    std::uint64_t renderSubmissionEndQpc100ns = 0;
    std::uint64_t markerOffsetFrames = 0;
    double pushMinusPacketBeginMilliseconds = std::numeric_limits<double>::quiet_NaN();
    double pushMinusPacketEndMilliseconds = std::numeric_limits<double>::quiet_NaN();
    double pushMinusMarkerBeginMilliseconds = std::numeric_limits<double>::quiet_NaN();
    double pushMinusMarkerEndMilliseconds = std::numeric_limits<double>::quiet_NaN();
};

inline std::uint64_t QpcTicksTo100ns(std::uint64_t ticks, std::uint64_t frequency) noexcept {
    if (frequency == 0) return 0;
    const long double value = static_cast<long double>(ticks) * 10'000'000.0L / frequency;
    return value <= 0 || value > std::numeric_limits<std::uint64_t>::max() ? 0
        : static_cast<std::uint64_t>(std::llround(value));
}

struct Analysis {
    Status status = Status::SignalNotFound;
    SequenceStatus sequenceStatus = SequenceStatus::NotFound;
    TimingQuality timingQuality = TimingQuality::Unreliable;
    std::array<Segment, 256> captureSegments{};
    std::size_t captureSegmentCount = 0;
    std::array<Segment, 256> captureClockSegments{};
    std::size_t captureClockSegmentCount = 0;
    std::size_t capturePacketQpcBoundaries = 0;
    std::size_t captureClockQpcBoundaries = 0;
    std::array<MarkerDiagnostic, kMaximumMarkers> markerDiagnostics{};
    std::size_t markerDiagnosticCount = 0;
    std::size_t pairedMarkers = 0;
    std::size_t missingMarkers = 0;
    std::size_t rejectedMarkers = 0;
    std::size_t softwareTransportSamples = 0;
    bool softwareTransportMeasured = false;
    double softwareTransportMedianMilliseconds = std::numeric_limits<double>::quiet_NaN();
    double softwareTransportMinimumMilliseconds = std::numeric_limits<double>::quiet_NaN();
    double softwareTransportMaximumMilliseconds = std::numeric_limits<double>::quiet_NaN();
    std::size_t firstFrameHandoffSamples = 0;
    double firstFrameHandoffMedianMilliseconds = std::numeric_limits<double>::quiet_NaN();
    double firstFrameHandoffMinimumMilliseconds = std::numeric_limits<double>::quiet_NaN();
    double firstFrameHandoffMaximumMilliseconds = std::numeric_limits<double>::quiet_NaN();
    double firstFrameHandoffSpreadMilliseconds = std::numeric_limits<double>::quiet_NaN();
    double fullMarkerHandoffSpreadMilliseconds = std::numeric_limits<double>::quiet_NaN();
    double captureClockOffsetMedianFrames = std::numeric_limits<double>::quiet_NaN();
    double captureClockOffsetVariationFrames = std::numeric_limits<double>::quiet_NaN();
    double captureClockRateDifferencePpm = std::numeric_limits<double>::quiet_NaN();
    bool absoluteTimelineResolved = false;
    const char* rejectionReason = "no complete marker was rendered";
};

struct Point { std::uint64_t frame = 0; std::uint64_t qpc100ns = 0; };

inline bool FitSegment(Segment& segment, std::span<const Point> points, std::uint32_t rate) noexcept {
    if (segment.anchorCount < kMinimumGoodAnchors || rate == 0) {
        segment.rejectionReason = "fewer than 8 continuous anchors";
        return false;
    }
    const auto first = points[segment.firstAnchor];
    long double meanX = 0;
    long double meanY = 0;
    for (std::size_t i = segment.firstAnchor; i <= segment.lastAnchor; ++i) {
        meanX += static_cast<long double>(points[i].frame - first.frame);
        meanY += static_cast<long double>(points[i].qpc100ns - first.qpc100ns);
    }
    meanX /= segment.anchorCount;
    meanY /= segment.anchorCount;
    long double covariance = 0;
    long double variance = 0;
    for (std::size_t i = segment.firstAnchor; i <= segment.lastAnchor; ++i) {
        const long double x = static_cast<long double>(points[i].frame - first.frame) - meanX;
        const long double y = static_cast<long double>(points[i].qpc100ns - first.qpc100ns) - meanY;
        covariance += x * y;
        variance += x * x;
    }
    if (variance <= 0) {
        segment.rejectionReason = "frame positions do not span the segment";
        return false;
    }
    segment.qpcUnitsPerFrame = covariance / variance;
    segment.qpcIntercept100ns = static_cast<long double>(first.qpc100ns) + meanY -
        segment.qpcUnitsPerFrame * (static_cast<long double>(first.frame) + meanX);
    long double squareSum = 0;
    segment.maximumResidualFrames = 0;
    for (std::size_t i = segment.firstAnchor; i <= segment.lastAnchor; ++i) {
        const long double predicted = segment.qpcIntercept100ns +
            segment.qpcUnitsPerFrame * points[i].frame;
        const double residual = static_cast<double>((static_cast<long double>(points[i].qpc100ns) - predicted) /
            (10'000'000.0L / rate));
        squareSum += residual * residual;
        segment.maximumResidualFrames = std::max(segment.maximumResidualFrames, std::abs(residual));
    }
    segment.rmsResidualFrames = std::sqrt(static_cast<double>(squareSum / segment.anchorCount));
    segment.fitted = true;
    segment.rejectionReason = "residual exceeds 6 frames";
    if (segment.rmsResidualFrames > kMaximumGoodResidualFrames ||
        segment.maximumResidualFrames > kMaximumGoodResidualFrames) return false;
    segment.rejectionReason = "";
    return true;
}

inline void FinishSegment(std::array<Segment, 256>& output, std::size_t& count,
                          std::span<const Point> points, std::size_t first, std::size_t last,
                          std::uint32_t rate) noexcept {
    if (count == output.size() || last < first) return;
    auto& segment = output[count++];
    segment.firstAnchor = first;
    segment.lastAnchor = last;
    segment.anchorCount = last - first + 1;
    segment.firstFrame = points[first].frame;
    segment.lastFrame = points[last].frame;
    segment.firstQpc100ns = points[first].qpc100ns;
    segment.lastQpc100ns = points[last].qpc100ns;
    FitSegment(segment, points, rate);
}

inline std::size_t BuildSegments(std::span<const Point> points, std::uint32_t rate,
                                 std::span<const std::uint8_t> valid,
                                 std::array<Segment, 256>& output,
                                 std::array<std::uint64_t, 256>& boundaryQpc,
                                 std::size_t& boundaryCount) noexcept {
    std::size_t count = 0;
    boundaryCount = 0;
    std::size_t first = points.size();
    for (std::size_t i = 0; i < points.size(); ++i) {
        if (!valid[i] || points[i].qpc100ns == 0) {
            if (first != points.size()) FinishSegment(output, count, points, first, i - 1, rate);
            first = points.size();
            continue;
        }
        if (first == points.size()) { first = i; continue; }
        const auto& previous = points[i - 1];
        const bool monotonic = points[i].frame > previous.frame && points[i].qpc100ns > previous.qpc100ns;
        const long double expected = monotonic
            ? static_cast<long double>(points[i].frame - previous.frame) * 10'000'000.0L / rate : 0;
        const double residualMs = monotonic
            ? static_cast<double>((static_cast<long double>(points[i].qpc100ns - previous.qpc100ns) - expected) /
                10'000.0L) : std::numeric_limits<double>::infinity();
        if (!monotonic || std::abs(residualMs) > kTimestampStepThresholdMilliseconds) {
            FinishSegment(output, count, points, first, i - 1, rate);
            if (boundaryCount < boundaryQpc.size()) boundaryQpc[boundaryCount++] = points[i].qpc100ns;
            first = i;
        }
    }
    if (first != points.size()) FinishSegment(output, count, points, first, points.size() - 1, rate);
    return count;
}

inline std::size_t FindSegment(const std::array<Segment, 256>& segments, std::size_t count,
                               std::uint64_t frame) noexcept {
    for (std::size_t i = 0; i < count; ++i)
        if (frame >= segments[i].firstFrame && frame <= segments[i].lastFrame) return i;
    return count;
}

inline bool InSegmentBoundaryGap(const std::array<Segment, 256>& segments, std::size_t count,
                                std::uint64_t frame) noexcept {
    for (std::size_t i = 1; i < count; ++i)
        if (frame > segments[i - 1].lastFrame && frame < segments[i].firstFrame) return true;
    return false;
}

inline bool FitIndexedPoints(std::span<const Point> points,
                             const std::array<std::size_t, kLocalFitAnchorCount>& indices,
                             std::size_t count, std::size_t excluded,
                             long double& intercept, long double& slope) noexcept {
    if (count < kMinimumGoodAnchors) return false;
    const auto firstIndex = indices[0] == excluded ? indices[1] : indices[0];
    const auto origin = points[firstIndex];
    long double meanX = 0, meanY = 0;
    std::size_t used = 0;
    for (std::size_t j = 0; j < count; ++j) {
        if (indices[j] == excluded) continue;
        meanX += static_cast<long double>(points[indices[j]].frame) - origin.frame;
        meanY += static_cast<long double>(points[indices[j]].qpc100ns) - origin.qpc100ns;
        ++used;
    }
    if (used < kMinimumGoodAnchors) return false;
    meanX /= used;
    meanY /= used;
    long double covariance = 0, variance = 0;
    for (std::size_t j = 0; j < count; ++j) {
        if (indices[j] == excluded) continue;
        const long double x = static_cast<long double>(points[indices[j]].frame) - origin.frame - meanX;
        const long double y = static_cast<long double>(points[indices[j]].qpc100ns) - origin.qpc100ns - meanY;
        covariance += x * y;
        variance += x * x;
    }
    if (variance <= 0) return false;
    slope = covariance / variance;
    intercept = static_cast<long double>(origin.qpc100ns) + meanY -
        slope * (static_cast<long double>(origin.frame) + meanX);
    return true;
}

inline LocalFit FitLocalMapping(std::span<const Point> points,
                                std::span<const std::uint8_t> valid,
                                const Segment& region, std::uint64_t targetFrame,
                                std::uint32_t rate) noexcept {
    LocalFit fit;
    if (rate == 0 || targetFrame < region.firstFrame || targetFrame > region.lastFrame) {
        fit.rejectionReason = "marker is outside the continuous timestamp region";
        return fit;
    }
    std::size_t pivot = region.firstAnchor;
    while (pivot <= region.lastAnchor && points[pivot].frame < targetFrame) ++pivot;
    const auto regionCount = region.lastAnchor - region.firstAnchor + 1;
    const auto wanted = std::min(kLocalFitAnchorCount, regionCount);
    fit.candidateAnchors = wanted;
    std::size_t begin = pivot > region.firstAnchor + wanted / 2
        ? pivot - wanted / 2 : region.firstAnchor;
    if (begin + wanted > region.lastAnchor + 1) begin = region.lastAnchor + 1 - wanted;
    std::array<std::size_t, kLocalFitAnchorCount> indices{};
    std::size_t count = 0;
    fit.firstAnchorIndex = begin;
    fit.lastAnchorIndex = begin + wanted - 1;
    for (std::size_t i = begin; i < begin + wanted; ++i) {
        if (!valid[i]) continue;
        indices[count++] = i;
    }
    std::size_t beforeTarget = 0, afterTarget = 0;
    for (std::size_t j = 0; j < count; ++j) {
        beforeTarget += points[indices[j]].frame < targetFrame;
        afterTarget += points[indices[j]].frame > targetFrame;
    }
    if (count < kMinimumLocalAnchors || beforeTarget < 4 || afterTarget < 4) {
        fit.rejectionReason = "fewer than 12 valid local anchors with at least 4 on each side";
        return fit;
    }

    const long double frameTicks = 10'000'000.0L / rate;
    for (std::size_t j = 0; j < count; ++j) {
        long double intercept = 0, slope = 0;
        if (!FitIndexedPoints(points, indices, count, indices[j], intercept, slope)) continue;
        const auto& point = points[indices[j]];
        const double residual = static_cast<double>((static_cast<long double>(point.qpc100ns) -
            (intercept + slope * point.frame)) / frameTicks);
        if (std::abs(residual) > kMaximumGoodResidualFrames) {
            fit.outlierIndices[fit.rejectedOutliers++] = indices[j];
        }
    }
    if (fit.rejectedOutliers > static_cast<std::size_t>(count * kMaximumLocalOutlierFraction) ||
        count - fit.rejectedOutliers < kMinimumLocalAnchors ||
        !FitIndexedPoints(points, indices, count, std::numeric_limits<std::size_t>::max(),
                          fit.intercept100ns, fit.qpcUnitsPerFrame)) {
        fit.rejectionReason = "too many local timestamp outliers";
        return fit;
    }
    // Refit after removing isolated outliers, using centered sums for large absolute QPC values.
    if (fit.rejectedOutliers != 0) {
        std::array<std::size_t, kLocalFitAnchorCount> clean{};
        std::size_t cleanCount = 0;
        for (std::size_t j = 0; j < count; ++j) {
            if (std::find(fit.outlierIndices.begin(),
                          fit.outlierIndices.begin() + fit.rejectedOutliers, indices[j]) ==
                fit.outlierIndices.begin() + fit.rejectedOutliers) clean[cleanCount++] = indices[j];
        }
        fit.anchorCount = cleanCount;
        // FitIndexedPoints' full-fit path uses all entries in count, so copy the compact set first.
        indices = clean;
        count = cleanCount;
    }
    if (!FitIndexedPoints(points, indices, count, std::numeric_limits<std::size_t>::max(),
                          fit.intercept100ns, fit.qpcUnitsPerFrame)) {
        fit.rejectionReason = "local regression could not be fitted";
        return fit;
    }
    long double squareSum = 0;
    fit.maximumResidualFrames = 0;
    for (std::size_t j = 0; j < count; ++j) {
        const auto& point = points[indices[j]];
        const double residual = static_cast<double>((static_cast<long double>(point.qpc100ns) -
            (fit.intercept100ns + fit.qpcUnitsPerFrame * point.frame)) / frameTicks);
        squareSum += residual * residual;
        fit.maximumResidualFrames = std::max(fit.maximumResidualFrames, std::abs(residual));
    }
    fit.anchorCount = count;
    fit.rmsResidualFrames = std::sqrt(static_cast<double>(squareSum / count));
    fit.fitted = fit.rmsResidualFrames <= kMaximumGoodResidualFrames &&
        fit.maximumResidualFrames <= kMaximumGoodResidualFrames;
    fit.rejectionReason = fit.fitted ? "" : "local RMS or maximum residual exceeds 6 frames";
    return fit;
}

inline bool MapFrame(const LocalFit& fit, std::uint64_t frame, double& qpc100ns) noexcept {
    if (!fit.fitted) return false;
    qpc100ns = static_cast<double>(fit.intercept100ns + fit.qpcUnitsPerFrame * frame);
    return std::isfinite(qpc100ns);
}

inline bool MapQpcToFrame(const LocalFit& fit, double qpc100ns, double& frame) noexcept {
    if (!fit.fitted || !(fit.qpcUnitsPerFrame > 0) || !std::isfinite(qpc100ns)) return false;
    frame = static_cast<double>((static_cast<long double>(qpc100ns) - fit.intercept100ns) /
        fit.qpcUnitsPerFrame);
    return std::isfinite(frame);
}

inline std::size_t FindSegmentAtQpc(const std::array<Segment, 256>& segments, std::size_t count,
                                    std::uint64_t qpc100ns) noexcept {
    for (std::size_t i = 0; i < count; ++i)
        if (qpc100ns >= segments[i].firstQpc100ns && qpc100ns <= segments[i].lastQpc100ns) return i;
    return count;
}

inline std::uint64_t NearestFrameAtQpc(std::span<const Point> points, const Segment& segment,
                                      std::uint64_t qpc100ns) noexcept {
    auto it = std::lower_bound(points.begin() + segment.firstAnchor,
        points.begin() + segment.lastAnchor + 1, qpc100ns,
        [](const Point& point, std::uint64_t qpc) { return point.qpc100ns < qpc; });
    if (it == points.begin() + segment.lastAnchor + 1) return points[segment.lastAnchor].frame;
    if (it == points.begin() + segment.firstAnchor) return it->frame;
    const auto& after = *it;
    const auto& before = *(it - 1);
    return qpc100ns - before.qpc100ns <= after.qpc100ns - qpc100ns
        ? before.frame : after.frame;
}

inline double Median(std::array<double, kMaximumMarkers> values, std::size_t count) noexcept {
    std::sort(values.begin(), values.begin() + count);
    if ((count & 1U) != 0) return values[count / 2];
    return (values[count / 2 - 1] + values[count / 2]) / 2.0;
}

inline void SummarizeHandoff(const std::array<double, kMaximumMarkers>& values, std::size_t count,
                             double& median, double& minimum, double& maximum,
                             double& spread) noexcept {
    if (count == 0) return;
    median = Median(values, count);
    const auto [minimumIt, maximumIt] = std::minmax_element(values.begin(), values.begin() + count);
    minimum = *minimumIt;
    maximum = *maximumIt;
    spread = maximum - minimum;
}

inline Analysis Analyze(const RunData& data, const ClockAnalysis::Series& captureClock,
                        std::uint32_t rate, bool transportCompleted, bool cancelled,
                        const LiveTransport::Stats& ring) {
    Analysis result;
    constexpr std::size_t maxAnchors = 100'000;
    std::vector<Point> capturePoints;
    std::vector<std::uint8_t> captureValid;
    try {
        const auto captureCount = std::min(data.capturePackets.size(), maxAnchors);
        capturePoints.resize(captureCount);
        captureValid.resize(captureCount);
    } catch (...) {
        result.status = Status::Unreliable;
        result.rejectionReason = "post-run timestamp analysis storage allocation failed";
        return result;
    }
    const auto captureCount = capturePoints.size();
    for (std::size_t i = 0; i < captureCount; ++i) {
        const auto& packet = data.capturePackets[i];
        capturePoints[i] = {packet.devicePosition, packet.qpc100ns};
        captureValid[i] = packet.qpc100ns != 0 && !packet.timestampError && !packet.discontinuity;
        if (i != 0 && (packet.devicePosition != data.capturePackets[i - 1].devicePosition +
                       data.capturePackets[i - 1].frames)) captureValid[i] = false;
    }
    std::array<std::uint64_t, 256> captureBoundaries{};
    std::size_t captureBoundaryCount = 0;
    result.captureSegmentCount = BuildSegments({capturePoints.data(), captureCount}, rate,
        {captureValid.data(), captureCount}, result.captureSegments, captureBoundaries, captureBoundaryCount);
    result.capturePacketQpcBoundaries = captureBoundaryCount;

    std::vector<Point> captureClockPoints;
    std::vector<std::uint8_t> captureClockValid;
    try {
        captureClockPoints.reserve(captureClock.snapshots.size());
        captureClockValid.reserve(captureClock.snapshots.size());
        for (std::size_t i = 0; i < captureClock.snapshots.size(); ++i) {
            const auto& snapshot = captureClock.snapshots[i];
            if (snapshot.hresult != ClockAnalysis::kSOk || snapshot.qpc100ns == 0 ||
                captureClock.frequency == 0 || i >= data.captureClockQpcBracketValid.size() ||
                !data.captureClockQpcBracketValid[i]) {
                captureClockPoints.push_back({});
                captureClockValid.push_back(0);
                continue;
            }
            const long double framePosition = static_cast<long double>(snapshot.position) * rate /
                captureClock.frequency;
            if (framePosition < 0 || framePosition > std::numeric_limits<std::uint64_t>::max()) {
                captureClockPoints.push_back({});
                captureClockValid.push_back(0);
                continue;
            }
            captureClockPoints.push_back({static_cast<std::uint64_t>(std::llround(framePosition)), snapshot.qpc100ns});
            captureClockValid.push_back(1);
        }
    } catch (...) {
        result.status = Status::Unreliable;
        result.rejectionReason = "Capture IAudioClock analysis storage allocation failed";
        return result;
    }
    std::array<std::uint64_t, 256> captureClockBoundaries{};
    std::size_t captureClockBoundaryCount = 0;
    result.captureClockSegmentCount = BuildSegments(captureClockPoints, rate, captureClockValid,
        result.captureClockSegments, captureClockBoundaries, captureClockBoundaryCount);
    result.captureClockQpcBoundaries = captureClockBoundaryCount;
    std::array<double, kMaximumMarkers> softwareTransport{};
    std::array<double, kMaximumMarkers> firstFrameHandoff{};
    std::array<double, kMaximumMarkers> captureClockOffsets{};
    std::array<double, kMaximumMarkers> captureClockRateErrors{};
    std::size_t captureClockRelationCount = 0;
    std::size_t expected = data.expectedMarkers;
    if (expected == 0) {
        for (const auto& marker : data.captureMarkers) expected = std::max(expected, marker.markerIndex + 1ULL);
        for (const auto& marker : data.renderMarkers) expected = std::max(expected, marker.markerIndex + 1ULL);
    }
    expected = std::min(expected, kMaximumMarkers);
    for (std::size_t markerIndex = 0; markerIndex < expected; ++markerIndex) {
        auto& diagnostic = result.markerDiagnostics[result.markerDiagnosticCount++];
        diagnostic.markerIndex = static_cast<std::uint32_t>(markerIndex);
        const auto sourceIt = std::find_if(data.captureMarkers.begin(), data.captureMarkers.end(),
            [&](const CaptureMarker& marker) { return marker.markerIndex == markerIndex; });
        const auto outputIt = std::find_if(data.renderMarkers.begin(), data.renderMarkers.end(),
            [&](const RenderMarker& marker) { return marker.markerIndex == markerIndex; });
        if (sourceIt != data.captureMarkers.end() && outputIt != data.renderMarkers.end()) ++result.pairedMarkers;
        const CaptureMarker* source = sourceIt == data.captureMarkers.end() ? nullptr : &*sourceIt;
        const RenderMarker* output = outputIt == data.renderMarkers.end() ? nullptr : &*outputIt;
        const bool sourceFrameMatches = source != nullptr && output != nullptr &&
            output->sourceFrame == source->sourceFrame;
        if (source != nullptr && output != nullptr && output->samplesVerified &&
            sourceFrameMatches &&
            source->ringInsertEndQpc100ns != 0 && output->firstSubmissionBeginQpc100ns != 0 &&
            output->firstSubmissionEndQpc100ns >= output->firstSubmissionBeginQpc100ns &&
            output->firstSubmissionBeginQpc100ns >=
                source->ringInsertEndQpc100ns &&
            result.firstFrameHandoffSamples < firstFrameHandoff.size()) {
            diagnostic.softwareFirstFrameMilliseconds = static_cast<double>(
                output->firstSubmissionEndQpc100ns - source->ringInsertEndQpc100ns) / 10'000.0;
            firstFrameHandoff[result.firstFrameHandoffSamples++] =
                diagnostic.softwareFirstFrameMilliseconds;
        }
        if (source != nullptr && output != nullptr && output->samplesVerified &&
            sourceFrameMatches &&
            source->ringInsertEndQpc100ns != 0 && output->submissionBeginQpc100ns >=
                source->ringInsertEndQpc100ns && output->submissionEndQpc100ns >=
                output->submissionBeginQpc100ns && result.softwareTransportSamples < softwareTransport.size()) {
            diagnostic.softwareTransportMilliseconds = static_cast<double>(
                output->submissionEndQpc100ns - source->ringInsertEndQpc100ns) / 10'000.0;
            softwareTransport[result.softwareTransportSamples++] = diagnostic.softwareTransportMilliseconds;
        }
        if (output != nullptr) {
            diagnostic.renderFirstFrame = output->renderFrame;
            diagnostic.renderLastFrame = output->lastRenderFrame;
            diagnostic.firstSubmissionBeginQpc100ns = output->firstSubmissionBeginQpc100ns;
            diagnostic.firstSubmissionEndQpc100ns = output->firstSubmissionEndQpc100ns;
            diagnostic.fullSubmissionBeginQpc100ns = output->submissionBeginQpc100ns;
            diagnostic.fullSubmissionEndQpc100ns = output->submissionEndQpc100ns;
            diagnostic.renderSubmissionBeginQpc100ns = output->submissionBeginQpc100ns;
            diagnostic.renderSubmissionEndQpc100ns = output->submissionEndQpc100ns;
        }
        if (source != nullptr) diagnostic.sourceFirstFrame = source->sourceFrame;
        if (source != nullptr && source->packetIndex < data.capturePackets.size()) {
            const auto& packet = data.capturePackets[source->packetIndex];
            diagnostic.packetQpc100ns = packet.qpc100ns;
            diagnostic.packetServiceQpc100ns = packet.serviceQpc100ns;
            diagnostic.ringPushBeginQpc100ns = source->ringInsertBeginQpc100ns;
            diagnostic.ringPushEndQpc100ns = source->ringInsertEndQpc100ns;
            diagnostic.markerGenerationBeginQpc100ns = source->generatedBeginQpc100ns;
            diagnostic.markerGenerationEndQpc100ns = source->generatedEndQpc100ns;
            diagnostic.markerOffsetFrames = source->devicePosition >= packet.devicePosition
                ? source->devicePosition - packet.devicePosition : 0;
            if (source->ringInsertBeginQpc100ns != 0 && packet.qpc100ns != 0) {
                diagnostic.pushMinusPacketBeginMilliseconds =
                    (static_cast<double>(source->ringInsertBeginQpc100ns) - packet.qpc100ns) / 10'000.0;
                diagnostic.pushMinusPacketEndMilliseconds =
                    (static_cast<double>(source->ringInsertEndQpc100ns) - packet.qpc100ns) / 10'000.0;
            }
            if (source->timestampValid) {
                const auto segment = FindSegment(result.captureSegments, result.captureSegmentCount,
                    source->devicePosition);
                if (segment != result.captureSegmentCount) {
                    diagnostic.captureFit = FitLocalMapping(capturePoints,
                        {captureValid.data(), captureValid.size()}, result.captureSegments[segment],
                        source->devicePosition, rate);
                    double markerQpc = 0;
                    if (MapFrame(diagnostic.captureFit, source->devicePosition, markerQpc)) {
                        diagnostic.markerQpc100ns = static_cast<std::uint64_t>(std::llround(markerQpc));
                        if (source->ringInsertBeginQpc100ns != 0) {
                            diagnostic.pushMinusMarkerBeginMilliseconds =
                                (static_cast<double>(source->ringInsertBeginQpc100ns) - markerQpc) / 10'000.0;
                            diagnostic.pushMinusMarkerEndMilliseconds =
                                (static_cast<double>(source->ringInsertEndQpc100ns) - markerQpc) / 10'000.0;
                        }
                    }
                    const auto markerQpc100ns = static_cast<std::uint64_t>(std::llround(markerQpc));
                    const auto clockSegment = FindSegmentAtQpc(result.captureClockSegments,
                        result.captureClockSegmentCount, markerQpc100ns);
                    if (clockSegment != result.captureClockSegmentCount) {
                        const auto clockTargetFrame = NearestFrameAtQpc(captureClockPoints,
                            result.captureClockSegments[clockSegment], markerQpc100ns);
                        diagnostic.captureClockFit = FitLocalMapping(captureClockPoints,
                            captureClockValid, result.captureClockSegments[clockSegment],
                            clockTargetFrame, rate);
                        double packetFrameAtCommonQpc = 0, clockFrameAtCommonQpc = 0;
                        if (diagnostic.captureClockFit.fitted &&
                            MapQpcToFrame(diagnostic.captureFit, markerQpc, packetFrameAtCommonQpc) &&
                            MapQpcToFrame(diagnostic.captureClockFit, markerQpc, clockFrameAtCommonQpc)) {
                            diagnostic.captureClockOffsetFrames = clockFrameAtCommonQpc -
                                packetFrameAtCommonQpc;
                            const auto& packetRegion = result.captureSegments[segment];
                            const auto& clockRegion = result.captureClockSegments[clockSegment];
                            if (packetRegion.qpcUnitsPerFrame > 0 && clockRegion.qpcUnitsPerFrame > 0)
                                diagnostic.captureClockRateDifferencePpm = static_cast<double>(
                                    (packetRegion.qpcUnitsPerFrame / clockRegion.qpcUnitsPerFrame - 1.0L) *
                                    1'000'000.0L);
                            const auto overlapBegin = std::max(packetRegion.firstQpc100ns,
                                clockRegion.firstQpc100ns);
                            const auto overlapEnd = std::min(packetRegion.lastQpc100ns,
                                clockRegion.lastQpc100ns);
                            const long double comparisonSpan = overlapEnd > overlapBegin &&
                                packetRegion.qpcUnitsPerFrame > 0
                                ? (overlapEnd - overlapBegin) / packetRegion.qpcUnitsPerFrame : 0;
                            diagnostic.captureClockAgreementFrames = std::abs(
                                diagnostic.captureClockRateDifferencePpm) *
                                static_cast<double>(comparisonSpan) / 1'000'000.0;
                            if (captureClockRelationCount < captureClockOffsets.size()) {
                                captureClockOffsets[captureClockRelationCount] =
                                    diagnostic.captureClockOffsetFrames;
                                captureClockRateErrors[captureClockRelationCount++] =
                                    diagnostic.captureClockRateDifferencePpm;
                            }
                        }
                    }
                }
            }
        }
        if (source == nullptr) {
            diagnostic.reason = MarkerReason::Other;
            diagnostic.detail = "Capture marker was not observed in a packet";
        } else if (output == nullptr || !output->samplesVerified) {
            diagnostic.reason = MarkerReason::Other;
            diagnostic.detail = "marker was not fully verified in a released Render buffer";
        } else if (!sourceFrameMatches) {
            diagnostic.reason = MarkerReason::Other;
            diagnostic.detail = "Render provenance source-frame does not match the Capture marker start";
        } else if (!source->timestampValid || source->packetIndex >= data.capturePackets.size()) {
            diagnostic.reason = MarkerReason::InvalidTimestamp;
            diagnostic.detail = "Capture packet continuity or timestamp was invalid";
        } else if (!diagnostic.captureFit.fitted) {
            const auto reason = std::string_view(diagnostic.captureFit.rejectionReason);
            diagnostic.reason = reason.starts_with("fewer") || reason.starts_with("insufficient")
                ? MarkerReason::InsufficientLocalAnchors : MarkerReason::CaptureLocalTimingRejected;
            diagnostic.detail = diagnostic.captureFit.rejectionReason;
        } else if (!diagnostic.captureClockFit.fitted) {
            const bool clockBoundary = diagnostic.markerQpc100ns != 0 &&
                FindSegmentAtQpc(result.captureClockSegments, result.captureClockSegmentCount,
                    diagnostic.markerQpc100ns) == result.captureClockSegmentCount;
            const auto reason = std::string_view(diagnostic.captureClockFit.rejectionReason);
            diagnostic.reason = clockBoundary ? MarkerReason::CrossesQpcBoundary
                : reason.starts_with("fewer") || reason.starts_with("insufficient")
                    ? MarkerReason::InsufficientLocalAnchors : MarkerReason::CaptureLocalTimingRejected;
            diagnostic.detail = clockBoundary
                ? "Capture marker timestamp falls outside an IAudioClock continuity region"
                : diagnostic.captureClockFit.rejectionReason;
        } else if (diagnostic.captureClockAgreementFrames > kMaximumGoodResidualFrames) {
            diagnostic.reason = MarkerReason::CaptureLocalTimingRejected;
            diagnostic.detail = "Capture packet and IAudioClock local rates differ by more than 6 frames across the compared window";
        } else if (output->submissionBeginQpc100ns != 0 &&
                   output->submissionEndQpc100ns >= output->submissionBeginQpc100ns &&
                   source->ringInsertEndQpc100ns != 0) {
            diagnostic.reason = MarkerReason::Accepted;
            diagnostic.detail = "provenance verified; software Ring Push to Render ReleaseBuffer interval measured; absolute device playback time unresolved";
        } else {
            diagnostic.reason = MarkerReason::Other;
            diagnostic.detail = "valid software Push/ReleaseBuffer QPC bracket is unavailable";
        }
        if (diagnostic.reason != MarkerReason::Accepted) ++result.rejectedMarkers;
    }
    result.missingMarkers = expected > result.pairedMarkers ? expected - result.pairedMarkers : 0;
    const bool noRingLoss = ring.underrunEvents == 0 && ring.overflowEvents == 0 &&
        ring.underrunFrames == 0 && ring.overflowFrames == 0;
    const bool storageComplete = !data.captureStorageFull && !data.renderStorageFull;
    result.softwareTransportMeasured = transportCompleted && !cancelled && noRingLoss &&
        storageComplete && expected != 0 && result.pairedMarkers == expected &&
        result.softwareTransportSamples == expected;
    if (result.softwareTransportSamples != 0) {
        SummarizeHandoff(softwareTransport, result.softwareTransportSamples,
            result.softwareTransportMedianMilliseconds,
            result.softwareTransportMinimumMilliseconds,
            result.softwareTransportMaximumMilliseconds,
            result.fullMarkerHandoffSpreadMilliseconds);
    }
    if (result.firstFrameHandoffSamples != 0) {
        SummarizeHandoff(firstFrameHandoff, result.firstFrameHandoffSamples,
            result.firstFrameHandoffMedianMilliseconds,
            result.firstFrameHandoffMinimumMilliseconds,
            result.firstFrameHandoffMaximumMilliseconds,
            result.firstFrameHandoffSpreadMilliseconds);
    }
    if (captureClockRelationCount != 0) {
        const auto offsetMedian = Median(captureClockOffsets, captureClockRelationCount);
        const auto [offsetMinimum, offsetMaximum] = std::minmax_element(captureClockOffsets.begin(),
            captureClockOffsets.begin() + captureClockRelationCount);
        result.captureClockOffsetMedianFrames = offsetMedian;
        result.captureClockOffsetVariationFrames = *offsetMaximum - *offsetMinimum;
        result.captureClockRateDifferencePpm = Median(captureClockRateErrors, captureClockRelationCount);
    }
    result.sequenceStatus = result.pairedMarkers == 0 ? SequenceStatus::NotFound
        : result.missingMarkers == 0 ? SequenceStatus::Matched : SequenceStatus::Partial;
    if (result.pairedMarkers == 0) {
        result.status = transportCompleted ? Status::SignalNotFound
            : cancelled ? Status::Unreliable : Status::Failed;
        result.rejectionReason = transportCompleted ? "no complete provenance marker reached Render"
            : cancelled ? "operator cancelled before a complete marker reached Render"
                        : "transport failed before marker analysis";
        return result;
    }
    result.status = Status::Unreliable;
    result.timingQuality = TimingQuality::Unreliable;
    result.rejectionReason = !transportCompleted ? "stream did not reach its requested deadline"
        : cancelled ? "operator cancelled the test"
        : !noRingLoss ? "ring underrun or overflow was observed"
        : !storageComplete ? "bounded provenance storage was truncated"
        : !result.softwareTransportMeasured ? "software provenance or Ring-to-ReleaseBuffer timing was incomplete"
        : "absolute device-frame origin and exact Render playback time are unresolved; only software Ring-to-ReleaseBuffer timing is measured";
    return result;
}

} // namespace MonitorLatency
