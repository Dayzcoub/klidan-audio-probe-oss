#pragma once

#include "clock_analysis.h"

#include <algorithm>
#include <array>
#include <cmath>
#include <cstdint>
#include <limits>
#include <span>
#include <vector>

namespace AnalogLoopback {

constexpr std::uint32_t kMarkerFrames = 511;
constexpr std::uint32_t kMarkerSpacingFrames = 12'000;
constexpr std::uint32_t kMinimumMarkers = 5;
constexpr std::uint64_t kQpcUnitsPerSecond = 10'000'000;
constexpr double kMinimumCorrelation = 0.80;
constexpr double kMinimumAmbiguityMargin = 0.10;
constexpr double kMinimumTimelineRSquared = 0.95;
constexpr double kMaximumCaptureRateErrorPpm = 500.0;
constexpr std::int16_t kSignalPeakCeiling = 519;
constexpr double kSequenceMinimumCandidateCorrelation = 0.35;
constexpr double kSequenceMinimumMeanCorrelation = 0.45;
constexpr double kSequenceMinimumScore = 0.12;
constexpr double kSequenceMinimumAmbiguityMargin = 0.02;
constexpr double kSequenceOffsetToleranceFrames = 32.0;
constexpr double kSequenceHypothesisMergeFrames = 64.0;
constexpr double kSequenceMaximumSpacingDeviationFrames = 24.0;
constexpr double kSequenceMaximumRmsOffsetErrorFrames = 8.0;
constexpr std::size_t kMaximumSequenceCandidates = 128;
constexpr double kGoodTimingErrorFrames = 6.0;
constexpr double kWarningTimingErrorFrames = 24.0;
constexpr double kGoodRenderClockBracketMilliseconds = 500.0;
constexpr double kWarningRenderClockBracketMilliseconds = 1'000.0;

enum class Status { Measured, SignalNotFound, Unreliable, Failed };

inline const char* StatusText(Status status) noexcept {
    switch (status) {
    case Status::Measured: return "MEASURED";
    case Status::SignalNotFound: return "SIGNAL_NOT_FOUND";
    case Status::Unreliable: return "UNRELIABLE";
    case Status::Failed: return "FAILED";
    }
    return "FAILED";
}

inline constexpr const char* kNoCorrelationWindow = "capture shorter than one complete marker window";

class RenderSignal {
public:
    explicit RenderSignal(unsigned channel = 1) noexcept : channel_(channel == 2 ? 1U : 0U) {}

    bool Configure(std::uint32_t rate, unsigned channel, std::uint32_t prefillFrames,
                   unsigned durationSeconds) {
        if (rate != 48'000 || (channel != 1 && channel != 2) || durationSeconds != 5) return false;
        rate_ = rate;
        channel_ = channel - 1;
        firstMarkerFrame_ = static_cast<std::uint64_t>(prefillFrames) + rate / 10;
        BuildMarker();
        markerStarts_.clear();
        const auto endFrame = static_cast<std::uint64_t>(durationSeconds) * rate;
        try { markerStarts_.reserve(static_cast<std::size_t>(endFrame / kMarkerSpacingFrames + 1)); }
        catch (...) { return false; }
        for (auto frame = firstMarkerFrame_; frame + marker_.size() <= endFrame;
             frame += kMarkerSpacingFrames) markerStarts_.push_back(frame);
        return markerStarts_.size() >= kMinimumMarkers;
    }

    void Fill(std::int16_t* stereo, std::uint32_t frames, std::uint64_t firstFrame,
              bool forceSilence = false) noexcept {
        if (stereo == nullptr) return;
        std::fill_n(stereo, static_cast<std::size_t>(frames) * 2, std::int16_t{0});
        if (forceSilence || rate_ == 0) return;
        for (std::uint32_t i = 0; i < frames; ++i) {
            const auto streamFrame = firstFrame + i;
            if (streamFrame < firstMarkerFrame_) continue;
            const auto relative = streamFrame - firstMarkerFrame_;
            const auto markerIndex = static_cast<std::size_t>(relative / kMarkerSpacingFrames);
            const auto markerOffset = static_cast<std::size_t>(relative % kMarkerSpacingFrames);
            if (markerIndex >= markerStarts_.size() || markerOffset >= marker_.size()) continue;
            const auto polarity = MarkerPolarity(markerIndex);
            const auto sample = static_cast<std::int16_t>(polarity * marker_[markerOffset]);
            stereo[static_cast<std::size_t>(i) * 2 + channel_] = sample;
            if (sample != 0) ++nonzeroFrames_;
            const auto value = static_cast<std::int32_t>(sample);
            peak_ = std::max(peak_, static_cast<std::uint32_t>(value < 0 ? -value : value));
        }
    }

    void CommitFrames(std::uint64_t firstFrame, std::uint32_t frames,
                      bool markerSignalEnabled = true) noexcept {
        if (!haveCommittedFrames_) {
            committedThroughFrame_ = firstFrame;
            haveCommittedFrames_ = true;
        }
        if (firstFrame != committedThroughFrame_) {
            submissionContinuous_ = false;
            markerRunActive_ = false;
            return;
        }
        committedThroughFrame_ += frames;
        if (!markerSignalEnabled) {
            markerRunActive_ = false;
            return;
        }
        if (!markerRunActive_ || markerRunEndFrame_ != firstFrame) {
            markerRunStartFrame_ = firstFrame;
            markerRunActive_ = true;
        }
        markerRunEndFrame_ = committedThroughFrame_;
        for (const auto markerStart : markerStarts_) {
            if (markerStart >= firstFrame && markerStart < committedThroughFrame_)
                ++markersStarted_;
            if (markerStart >= markerRunStartFrame_ && markerStart + marker_.size() > firstFrame &&
                markerStart + marker_.size() <= markerRunEndFrame_ &&
                markerStart + marker_.size() > markerRunEndFrame_ - frames)
                ++markersCompleted_;
        }
    }

    const std::array<std::int16_t, kMarkerFrames>& Marker() const noexcept { return marker_; }
    const std::vector<std::uint64_t>& MarkerStarts() const noexcept { return markerStarts_; }
    std::uint64_t FirstMarkerFrame() const noexcept { return firstMarkerFrame_; }
    unsigned Channel() const noexcept { return channel_ + 1; }
    std::uint32_t Rate() const noexcept { return rate_; }
    std::uint64_t NonzeroFrames() const noexcept { return nonzeroFrames_; }
    std::uint32_t Peak() const noexcept { return peak_; }
    std::uint64_t MarkersStarted() const noexcept { return markersStarted_; }
    std::uint64_t MarkersCompleted() const noexcept { return markersCompleted_; }
    bool SubmissionContinuous() const noexcept { return submissionContinuous_; }

    static int MarkerPolarity(std::size_t markerIndex) noexcept {
        std::uint32_t state = 0x6D2B79F5U;
        for (std::size_t i = 0; i <= markerIndex; ++i) {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
        }
        return (state & 1U) != 0 ? 1 : -1;
    }

private:
    void BuildMarker() noexcept {
        std::array<int, kMarkerFrames> signs{};
        std::uint32_t state = 0x1FFU;
        int sum = 0;
        for (auto& sign : signs) {
            sign = (state & 1U) != 0 ? 1 : -1;
            sum += sign;
            const auto feedback = ((state >> 0) ^ (state >> 4)) & 1U;
            state = (state >> 1) | (feedback << 8);
        }
        const double mean = static_cast<double>(sum) / signs.size();
        double maximum = 0;
        std::array<double, kMarkerFrames> centered{};
        for (std::size_t i = 0; i < signs.size(); ++i) {
            double window = 1.0;
            constexpr std::size_t edge = 24;
            if (i < edge) window = 0.5 - 0.5 * std::cos(3.141592653589793 * i / edge);
            else if (i >= signs.size() - edge)
                window = 0.5 - 0.5 * std::cos(3.141592653589793 * (signs.size() - 1 - i) / edge);
            centered[i] = (signs[i] - mean) * window;
            maximum = std::max(maximum, std::abs(centered[i]));
        }
        for (std::size_t i = 0; i < marker_.size(); ++i) {
            marker_[i] = static_cast<std::int16_t>(std::lrint(centered[i] * kSignalPeakCeiling / maximum));
        }
    }

    std::uint32_t rate_ = 0;
    unsigned channel_ = 0;
    std::uint64_t firstMarkerFrame_ = 0;
    std::uint64_t committedThroughFrame_ = 0;
    std::uint64_t nonzeroFrames_ = 0;
    std::uint64_t markersStarted_ = 0;
    std::uint64_t markersCompleted_ = 0;
    std::uint64_t markerRunStartFrame_ = 0;
    std::uint64_t markerRunEndFrame_ = 0;
    std::uint32_t peak_ = 0;
    bool haveCommittedFrames_ = false;
    bool submissionContinuous_ = true;
    bool markerRunActive_ = false;
    std::array<std::int16_t, kMarkerFrames> marker_{};
    std::vector<std::uint64_t> markerStarts_;
};

struct CapturePacket {
    std::uint64_t firstStoredFrame = 0;
    std::uint64_t devicePosition = 0;
    std::uint64_t qpc100ns = 0;
    std::uint32_t frames = 0;
    bool silent = false;
    bool discontinuity = false;
    bool timestampError = false;
};

class CaptureData {
public:
    explicit CaptureData(unsigned channel = 1) noexcept : channel_(channel == 2 ? 1U : 0U) {}
    ~CaptureData() { EraseSamples(); }

    bool Configure(unsigned channel, std::uint32_t rate, std::uint32_t maximumFrames,
                   std::size_t maximumPackets) noexcept {
        if ((channel != 1 && channel != 2) || rate != 48'000 || maximumFrames == 0 || maximumPackets == 0)
            return false;
        try {
            samples_.assign(maximumFrames, 0);
            packets_.reserve(maximumPackets);
        } catch (...) {
            samples_.clear();
            packets_.clear();
            return false;
        }
        channel_ = channel - 1;
        rate_ = rate;
        ready_ = true;
        return true;
    }

    bool Append(const std::int16_t* interleaved, std::uint32_t frames, bool silent,
                bool discontinuity, bool timestampError, std::uint64_t devicePosition,
                std::uint64_t qpc100ns) noexcept {
        if (!ready_) { invalidData_ = true; return false; }
        if (packets_.size() == packets_.capacity()) { truncated_ = true; return false; }
        const auto available = samples_.size() - storedFrames_;
        const auto copied = std::min<std::size_t>(frames, available);
        if (frames != 0 && !silent && interleaved == nullptr) {
            invalidData_ = true;
            return false;
        }
        packets_.push_back({storedFrames_, devicePosition, qpc100ns, frames,
                            silent, discontinuity, timestampError});
        for (std::size_t i = 0; i < copied; ++i) {
            const std::int16_t sample = silent ? std::int16_t{0} : interleaved[i * 2 + channel_];
            samples_[storedFrames_ + i] = sample;
            if (sample == std::numeric_limits<std::int16_t>::min() ||
                sample == std::numeric_limits<std::int16_t>::max()) ++clippedSamples_;
            const auto value = static_cast<std::int32_t>(sample);
            peak_ = std::max(peak_, static_cast<std::uint32_t>(value < 0 ? -value : value));
            squareSum_ += static_cast<std::uint64_t>(value * value);
        }
        storedFrames_ += copied;
        if (copied != frames) truncated_ = true;
        return copied == frames;
    }

    std::span<const std::int16_t> Samples() const noexcept { return {samples_.data(), storedFrames_}; }
    const std::vector<CapturePacket>& Packets() const noexcept { return packets_; }
    std::uint64_t ClippedSamples() const noexcept { return clippedSamples_; }
    std::uint64_t StoredFrames() const noexcept { return storedFrames_; }
    std::uint64_t Peak() const noexcept { return peak_; }
    double RmsDbfs() const noexcept {
        if (storedFrames_ == 0 || squareSum_ == 0) return -std::numeric_limits<double>::infinity();
        const double meanSquare = static_cast<double>(squareSum_) / storedFrames_;
        return 10.0 * std::log10(meanSquare / (32768.0 * 32768.0));
    }
    double PeakDbfs() const noexcept {
        return peak_ == 0 ? -std::numeric_limits<double>::infinity()
            : 20.0 * std::log10(static_cast<double>(peak_) / 32768.0);
    }
    bool Truncated() const noexcept { return truncated_; }
    bool InvalidData() const noexcept { return invalidData_; }
    unsigned Channel() const noexcept { return channel_ + 1; }
    std::uint32_t Rate() const noexcept { return rate_; }
    void EraseSamples() noexcept {
        volatile std::int16_t* sample = samples_.data();
        for (std::size_t i = 0; i < samples_.size(); ++i) sample[i] = 0;
        samples_.clear();
        packets_.clear();
        storedFrames_ = 0;
    }

private:
    std::vector<std::int16_t> samples_;
    std::vector<CapturePacket> packets_;
    std::uint64_t storedFrames_ = 0;
    std::uint64_t clippedSamples_ = 0;
    std::uint64_t squareSum_ = 0;
    std::uint32_t peak_ = 0;
    unsigned channel_ = 0;
    std::uint32_t rate_ = 0;
    bool ready_ = false;
    bool truncated_ = false;
    bool invalidData_ = false;
};

struct CorrelationPeak {
    double offsetFrames = 0;
    double confidence = 0;
    double ambiguityMargin = 0;
    int polarity = 1;
};

struct CorrelationDiagnostics {
    bool available = false;
    double maximum = 0;
    double maximumOffsetFrames = 0;
    int maximumPolarity = 1;
    std::uint64_t candidatesAboveThreshold = 0;
    std::uint64_t candidatesBelowThreshold = 0;
    std::vector<CorrelationPeak> independentCandidates;
    std::vector<CorrelationPeak> bestIndependentCandidates;
    std::vector<CorrelationPeak> detectedPeaks;
};

struct CaptureTimeline {
    bool reliable = false;
    std::uint64_t positionOrigin = 0;
    std::uint64_t qpcOrigin100ns = 0;
    long double intercept100ns = 0;
    long double qpcUnitsPerFrame = 0;
    double rateErrorPpm = 0;
    double rSquared = 0;
    double rmsResidualFrames = std::numeric_limits<double>::quiet_NaN();
    double maximumResidualFrames = std::numeric_limits<double>::quiet_NaN();
    double maximumLocalIntervalDeviationFrames = std::numeric_limits<double>::quiet_NaN();
};

inline CaptureTimeline FitCaptureTimeline(std::span<const CapturePacket> packets,
                                          std::uint32_t rate) {
    CaptureTimeline timeline;
    if (packets.size() < 8 || rate == 0) return timeline;
    timeline.positionOrigin = packets.front().devicePosition;
    timeline.qpcOrigin100ns = packets.front().qpc100ns;
    long double meanX = 0;
    long double meanY = 0;
    std::uint64_t expectedPosition = timeline.positionOrigin;
    std::uint64_t expectedStoredFrame = packets.front().firstStoredFrame;
    std::uint64_t previousQpc = 0;
    for (std::size_t i = 0; i < packets.size(); ++i) {
        const auto& packet = packets[i];
        if (packet.qpc100ns == 0 || packet.qpc100ns < timeline.qpcOrigin100ns ||
            packet.devicePosition < timeline.positionOrigin || (i != 0 && packet.qpc100ns <= previousQpc)) return timeline;
        if (packet.frames == 0) return timeline;
        if (i != 0 && (packet.devicePosition != expectedPosition ||
                       packet.firstStoredFrame != expectedStoredFrame)) return timeline;
        if (packet.frames > std::numeric_limits<std::uint64_t>::max() - packet.devicePosition ||
            packet.frames > std::numeric_limits<std::uint64_t>::max() - packet.firstStoredFrame) return timeline;
        expectedPosition = packet.devicePosition + packet.frames;
        expectedStoredFrame = packet.firstStoredFrame + packet.frames;
        previousQpc = packet.qpc100ns;
        meanX += static_cast<long double>(packet.devicePosition - timeline.positionOrigin);
        meanY += static_cast<long double>(packet.qpc100ns - timeline.qpcOrigin100ns);
    }
    meanX /= packets.size();
    meanY /= packets.size();
    long double covariance = 0;
    long double varianceX = 0;
    long double varianceY = 0;
    for (const auto& packet : packets) {
        const long double x = static_cast<long double>(packet.devicePosition - timeline.positionOrigin) - meanX;
        const long double y = static_cast<long double>(packet.qpc100ns - timeline.qpcOrigin100ns) - meanY;
        covariance += x * y;
        varianceX += x * x;
        varianceY += y * y;
    }
    if (varianceX <= 0 || varianceY <= 0) return timeline;
    timeline.qpcUnitsPerFrame = covariance / varianceX;
    if (timeline.qpcUnitsPerFrame <= 0) return timeline;
    const long double centeredIntercept = meanY - timeline.qpcUnitsPerFrame * meanX;
    timeline.intercept100ns = static_cast<long double>(timeline.qpcOrigin100ns) + centeredIntercept -
        timeline.qpcUnitsPerFrame * timeline.positionOrigin;
    long double residualSquares = 0;
    long double maximumResidual = 0;
    for (const auto& packet : packets) {
        const long double x = static_cast<long double>(packet.devicePosition - timeline.positionOrigin);
        const long double y = static_cast<long double>(packet.qpc100ns - timeline.qpcOrigin100ns);
        const long double residual = y - (centeredIntercept + timeline.qpcUnitsPerFrame * x);
        residualSquares += residual * residual;
        maximumResidual = std::max(maximumResidual, std::abs(residual));
    }
    timeline.rSquared = static_cast<double>(std::clamp(1.0L - residualSquares / varianceY, 0.0L, 1.0L));
    timeline.rmsResidualFrames = static_cast<double>(std::sqrt(residualSquares / packets.size()) /
        timeline.qpcUnitsPerFrame);
    timeline.maximumResidualFrames = static_cast<double>(maximumResidual / timeline.qpcUnitsPerFrame);
    long double maximumLocalIntervalDeviation = 0;
    for (std::size_t i = 1; i < packets.size(); ++i) {
        const auto& previous = packets[i - 1];
        const auto& current = packets[i];
        const auto qpcDelta = current.qpc100ns - previous.qpc100ns;
        const auto frameDelta = current.devicePosition - previous.devicePosition;
        const long double expectedQpcDelta = timeline.qpcUnitsPerFrame * frameDelta;
        maximumLocalIntervalDeviation = std::max(maximumLocalIntervalDeviation,
            std::abs(static_cast<long double>(qpcDelta) - expectedQpcDelta) /
                timeline.qpcUnitsPerFrame);
    }
    timeline.maximumLocalIntervalDeviationFrames = static_cast<double>(maximumLocalIntervalDeviation);
    const long double measuredRate = kQpcUnitsPerSecond / timeline.qpcUnitsPerFrame;
    timeline.rateErrorPpm = static_cast<double>((measuredRate / rate - 1.0L) * 1'000'000.0L);
    timeline.reliable = timeline.rSquared >= kMinimumTimelineRSquared &&
        std::abs(timeline.rateErrorPpm) <= kMaximumCaptureRateErrorPpm;
    return timeline;
}

inline CorrelationPeak MakeCorrelationPeak(std::size_t peak, std::span<const double> scores,
                                           std::span<const int> polarities,
                                           std::size_t markerLength) {
    const auto count = scores.size();
    constexpr std::size_t guard = 16;
    double competitor = 0;
    const auto begin = peak > markerLength ? peak - markerLength : 0;
    const auto end = std::min(count, peak + markerLength + 1);
    for (std::size_t i = begin; i < end; ++i) {
        const auto distance = i > peak ? i - peak : peak - i;
        if (distance > guard) competitor = std::max(competitor, scores[i]);
    }
    const double before = peak == 0 ? scores[peak] : scores[peak - 1];
    const double center = scores[peak];
    const double after = peak + 1 == count ? scores[peak] : scores[peak + 1];
    const double denominator = before - 2.0 * center + after;
    const double fraction = std::abs(denominator) < 1e-12 ? 0.0
        : std::clamp(0.5 * (before - after) / denominator, -0.5, 0.5);
    return {static_cast<double>(peak) + fraction, center, center - competitor, polarities[peak]};
}

inline CorrelationDiagnostics DiagnoseMarkers(std::span<const std::int16_t> capture,
                                               std::span<const std::int16_t> marker) {
    CorrelationDiagnostics result;
    if (marker.empty() || capture.size() < marker.size()) return result;
    double markerEnergy = 0;
    for (const auto sample : marker) markerEnergy += static_cast<double>(sample) * sample;
    if (markerEnergy == 0) return result;
    const auto count = capture.size() - marker.size() + 1;
    std::vector<double> scores(count);
    std::vector<int> polarities(count, 1);
    for (std::size_t offset = 0; offset < count; ++offset) {
        long double dot = 0;
        long double captureEnergy = 0;
        for (std::size_t i = 0; i < marker.size(); ++i) {
            const auto sample = static_cast<double>(capture[offset + i]);
            dot += sample * marker[i];
            captureEnergy += sample * sample;
        }
        const double normalized = captureEnergy == 0 ? 0
            : static_cast<double>(dot / std::sqrt(captureEnergy * markerEnergy));
        scores[offset] = std::abs(normalized);
        polarities[offset] = normalized < 0 ? -1 : 1;
    }
    result.available = true;
    const auto maximum = static_cast<std::size_t>(std::max_element(scores.begin(), scores.end()) - scores.begin());
    const auto maximumPeak = MakeCorrelationPeak(maximum, scores, polarities, marker.size());
    result.maximum = scores[maximum];
    result.maximumOffsetFrames = maximumPeak.offsetFrames;
    result.maximumPolarity = polarities[maximum];
    std::vector<std::size_t> candidates;
    for (std::size_t i = 0; i < count; ++i) {
        const double before = i == 0 ? -1.0 : scores[i - 1];
        const double after = i + 1 == count ? -1.0 : scores[i + 1];
        if (scores[i] >= before && scores[i] >= after && (scores[i] > before || scores[i] > after))
            candidates.push_back(i);
    }
    std::sort(candidates.begin(), candidates.end(), [&](auto left, auto right) {
        return scores[left] > scores[right];
    });
    std::vector<std::size_t> selected;
    const auto exclusion = std::max<std::size_t>(marker.size() / 2, 1);
    for (const auto candidate : candidates) {
        if (std::any_of(selected.begin(), selected.end(), [&](auto prior) {
                return candidate > prior ? candidate - prior < exclusion : prior - candidate < exclusion;
            })) continue;
        selected.push_back(candidate);
    }
    for (const auto index : selected) {
        if (scores[index] >= kMinimumCorrelation) ++result.candidatesAboveThreshold;
        else ++result.candidatesBelowThreshold;
        if (result.bestIndependentCandidates.size() < 5) {
            result.bestIndependentCandidates.push_back(MakeCorrelationPeak(index, scores, polarities, marker.size()));
        }
        if (scores[index] >= kMinimumCorrelation && result.detectedPeaks.size() < 64)
            result.detectedPeaks.push_back(MakeCorrelationPeak(index, scores, polarities, marker.size()));
        result.independentCandidates.push_back(MakeCorrelationPeak(index, scores, polarities, marker.size()));
    }
    std::sort(result.independentCandidates.begin(), result.independentCandidates.end(),
        [](const auto& left, const auto& right) { return left.offsetFrames < right.offsetFrames; });
    std::sort(result.detectedPeaks.begin(), result.detectedPeaks.end(),
        [](const auto& left, const auto& right) { return left.offsetFrames < right.offsetFrames; });
    return result;
}

enum class SequenceStatus { Matched, Ambiguous, NotFound };

inline const char* SequenceStatusText(SequenceStatus status) noexcept {
    switch (status) {
    case SequenceStatus::Matched: return "MATCHED";
    case SequenceStatus::Ambiguous: return "AMBIGUOUS";
    case SequenceStatus::NotFound: return "NOT_FOUND";
    }
    return "NOT_FOUND";
}

struct SequenceMatch {
    std::size_t markerIndex = 0;
    CorrelationPeak peak;
};

struct SequenceDiagnostics {
    SequenceStatus status = SequenceStatus::NotFound;
    const char* rejectionReason = "no coherent marker sequence found";
    std::size_t expectedMarkers = 0;
    std::size_t matchedMarkers = 0;
    std::size_t missingMarkers = 0;
    std::size_t contradictoryMarkers = 0;
    double meanMatchedCorrelation = 0;
    double coherenceScore = 0;
    double ambiguityMargin = 0;
    double estimatedSpacingFrames = 0;
    double offsetConsistencyRmsFrames = 0;
    double offsetConsistencyMaximumFrames = 0;
    int globalPolarity = 1;
    std::vector<SequenceMatch> matches;
};

inline SequenceDiagnostics DetectCoherentSequence(const CorrelationDiagnostics& correlation,
                                                   const RenderSignal& render) {
    SequenceDiagnostics result;
    const auto& starts = render.MarkerStarts();
    result.expectedMarkers = starts.size();
    result.estimatedSpacingFrames = kMarkerSpacingFrames;
    if (starts.size() < kMinimumMarkers || starts.size() > 64 ||
        correlation.independentCandidates.empty()) {
        result.rejectionReason = "too few expected markers or independent Capture candidates";
        result.missingMarkers = starts.size();
        return result;
    }

    struct Evaluation {
        bool valid = false;
        double baseOffset = 0;
        int polarity = 1;
        double score = 0;
        double meanCorrelation = 0;
        double spacing = kMarkerSpacingFrames;
        double rms = 0;
        double maximumResidual = 0;
        std::size_t matched = 0;
        std::size_t contradictory = 0;
        std::array<SequenceMatch, 64> matches{};
    } best, second;

    const auto sameHypothesis = [](const Evaluation& left, const Evaluation& right) {
        return left.valid && right.valid && std::abs(left.baseOffset - right.baseOffset) <=
            kSequenceHypothesisMergeFrames;
    };
    const auto consider = [&](const Evaluation& candidate, Evaluation& currentBest,
                              Evaluation& currentSecond) {
        if (!candidate.valid) return;
        if (sameHypothesis(candidate, currentBest)) {
            if (candidate.score > currentBest.score) currentBest = candidate;
            return;
        }
        if (sameHypothesis(candidate, currentSecond)) {
            if (candidate.score > currentSecond.score) currentSecond = candidate;
            return;
        }
        if (!currentBest.valid || candidate.score > currentBest.score) {
            currentSecond = currentBest;
            currentBest = candidate;
        } else if (!currentSecond.valid || candidate.score > currentSecond.score) {
            currentSecond = candidate;
        }
    };

    std::vector<CorrelationPeak> candidates;
    candidates.reserve(std::min(correlation.independentCandidates.size(), kMaximumSequenceCandidates));
    for (const auto& candidate : correlation.independentCandidates) {
        if (candidate.confidence >= kSequenceMinimumCandidateCorrelation)
            candidates.push_back(candidate);
    }
    std::sort(candidates.begin(), candidates.end(), [](const auto& left, const auto& right) {
        return left.confidence > right.confidence;
    });
    if (candidates.size() > kMaximumSequenceCandidates) candidates.resize(kMaximumSequenceCandidates);
    std::sort(candidates.begin(), candidates.end(), [](const auto& left, const auto& right) {
        return left.offsetFrames < right.offsetFrames;
    });
    if (candidates.empty()) {
        result.rejectionReason = "no independent candidate reached the sequence correlation floor";
        result.missingMarkers = starts.size();
        return result;
    }

    const auto evaluate = [&](double baseOffset, double spacing, int globalPolarity) {
        Evaluation evaluation;
        evaluation.valid = true;
        evaluation.baseOffset = baseOffset;
        evaluation.polarity = globalPolarity;
        std::array<double, 64> x{};
        std::array<double, 64> y{};
        double correlationSum = 0;
        for (std::size_t markerIndex = 0; markerIndex < starts.size(); ++markerIndex) {
            const double target = baseOffset + markerIndex * spacing;
            const auto iterator = std::lower_bound(candidates.begin(), candidates.end(), target,
                [](const CorrelationPeak& peak, double frame) { return peak.offsetFrames < frame; });
            const CorrelationPeak* nearest = nullptr;
            double nearestDistance = kSequenceOffsetToleranceFrames + 1.0;
            auto inspect = [&](auto candidate) {
                while (candidate != candidates.end()) {
                    const double distance = std::abs(candidate->offsetFrames - target);
                    if (distance > kSequenceOffsetToleranceFrames) break;
                    if (candidate->confidence >= kSequenceMinimumCandidateCorrelation &&
                        distance < nearestDistance) {
                        nearest = &*candidate;
                        nearestDistance = distance;
                    }
                    ++candidate;
                }
            };
            inspect(iterator);
            auto before = iterator;
            while (before != candidates.begin()) {
                --before;
                if (target - before->offsetFrames > kSequenceOffsetToleranceFrames) break;
                if (before->confidence >= kSequenceMinimumCandidateCorrelation &&
                    target - before->offsetFrames < nearestDistance) {
                    nearest = &*before;
                    nearestDistance = target - before->offsetFrames;
                }
            }
            if (nearest == nullptr) continue;
            const int expectedPolarity = globalPolarity * RenderSignal::MarkerPolarity(markerIndex);
            if (nearest->polarity != expectedPolarity) {
                ++evaluation.contradictory;
                continue;
            }
            auto& match = evaluation.matches[evaluation.matched];
            match.markerIndex = markerIndex;
            match.peak = *nearest;
            x[evaluation.matched] = static_cast<double>(markerIndex);
            y[evaluation.matched] = nearest->offsetFrames;
            correlationSum += nearest->confidence;
            ++evaluation.matched;
        }
        if (evaluation.matched == 0) return evaluation;
        evaluation.meanCorrelation = correlationSum / evaluation.matched;
        if (evaluation.matched >= 2) {
            double meanX = 0;
            double meanY = 0;
            for (std::size_t i = 0; i < evaluation.matched; ++i) {
                meanX += x[i];
                meanY += y[i];
            }
            meanX /= evaluation.matched;
            meanY /= evaluation.matched;
            double covariance = 0;
            double varianceX = 0;
            for (std::size_t i = 0; i < evaluation.matched; ++i) {
                covariance += (x[i] - meanX) * (y[i] - meanY);
                varianceX += (x[i] - meanX) * (x[i] - meanX);
            }
            if (varianceX > 0) evaluation.spacing = covariance / varianceX;
            evaluation.baseOffset = meanY - evaluation.spacing * meanX;
            double residualSquares = 0;
            for (std::size_t i = 0; i < evaluation.matched; ++i) {
                const auto residual = y[i] - (evaluation.baseOffset + evaluation.spacing * x[i]);
                residualSquares += residual * residual;
                evaluation.maximumResidual = std::max(evaluation.maximumResidual, std::abs(residual));
            }
            evaluation.rms = std::sqrt(residualSquares / evaluation.matched);
        }
        const double polarityAgreement = static_cast<double>(evaluation.matched) /
            (evaluation.matched + evaluation.contradictory);
        evaluation.score = evaluation.meanCorrelation *
            (static_cast<double>(evaluation.matched) / starts.size()) * polarityAgreement *
            std::exp(-evaluation.rms / kSequenceOffsetToleranceFrames);
        return evaluation;
    };

    for (std::size_t anchor = 0; anchor < candidates.size(); ++anchor) {
        const auto& first = candidates[anchor];
        for (std::size_t firstIndex = 0; firstIndex < starts.size(); ++firstIndex) {
            const int polarity = first.polarity * RenderSignal::MarkerPolarity(firstIndex);
            consider(evaluate(first.offsetFrames - firstIndex * kMarkerSpacingFrames,
                              kMarkerSpacingFrames, polarity), best, second);
        }
        for (std::size_t next = anchor + 1; next < candidates.size(); ++next) {
            const auto& other = candidates[next];
            const double difference = other.offsetFrames - first.offsetFrames;
            const auto gap = static_cast<std::size_t>(std::llround(difference / kMarkerSpacingFrames));
            if (gap == 0 || gap >= starts.size()) continue;
            const double estimatedSpacing = difference / gap;
            if (std::abs(estimatedSpacing - kMarkerSpacingFrames) >
                kSequenceMaximumSpacingDeviationFrames) continue;
            for (std::size_t firstIndex = 0; firstIndex + gap < starts.size(); ++firstIndex) {
                const int polarity = first.polarity * RenderSignal::MarkerPolarity(firstIndex);
                consider(evaluate(first.offsetFrames - firstIndex * estimatedSpacing,
                                  estimatedSpacing, polarity), best, second);
            }
        }
    }

    if (!best.valid) {
        result.rejectionReason = "no candidate reached the sequence correlation floor";
        result.missingMarkers = starts.size();
        return result;
    }
    result.matchedMarkers = best.matched;
    result.contradictoryMarkers = best.contradictory;
    result.missingMarkers = starts.size() - best.matched - best.contradictory;
    result.meanMatchedCorrelation = best.meanCorrelation;
    result.coherenceScore = best.score;
    result.ambiguityMargin = best.score - (second.valid ? second.score : 0.0);
    result.estimatedSpacingFrames = best.spacing;
    result.offsetConsistencyRmsFrames = best.rms;
    result.offsetConsistencyMaximumFrames = best.maximumResidual;
    result.globalPolarity = best.polarity;
    result.matches.reserve(best.matched);
    for (std::size_t i = 0; i < best.matched; ++i) result.matches.push_back(best.matches[i]);

    const bool enoughMarkers = best.matched >= kMinimumMarkers;
    const bool coherentOffsets = std::abs(best.spacing - kMarkerSpacingFrames) <=
        kSequenceMaximumSpacingDeviationFrames && best.rms <= kSequenceMaximumRmsOffsetErrorFrames;
    const bool strongEvidence = best.meanCorrelation >= kSequenceMinimumMeanCorrelation &&
        best.score >= kSequenceMinimumScore;
    if (!enoughMarkers) {
        result.rejectionReason = "fewer than five correctly polarized markers matched";
    } else if (best.contradictory != 0) {
        result.rejectionReason = "one or more expected markers have contradictory polarity";
    } else if (!coherentOffsets) {
        result.rejectionReason = "marker offsets or estimated spacing are inconsistent";
    } else if (!strongEvidence) {
        result.rejectionReason = "combined sequence evidence is below the calibrated acceptance floor";
    } else if (result.ambiguityMargin < kSequenceMinimumAmbiguityMargin) {
        result.status = SequenceStatus::Ambiguous;
        result.rejectionReason = "another distinct sequence alignment has insufficient score separation";
    } else {
        result.status = SequenceStatus::Matched;
        result.rejectionReason = "none";
    }
    return result;
}

inline std::vector<CorrelationPeak> FindMarkers(std::span<const std::int16_t> capture,
                                                std::span<const std::int16_t> marker) {
    return DiagnoseMarkers(capture, marker).detectedPeaks;
}

inline bool AlignMarkerIndices(std::span<const CorrelationPeak> peaks,
                               std::size_t expectedMarkerCount, std::vector<std::size_t>& indices) {
    indices.clear();
    if (peaks.size() < kMinimumMarkers || peaks.size() > expectedMarkerCount) return false;
    std::size_t matchStart = 0;
    unsigned matches = 0;
    for (std::size_t start = 0; start + peaks.size() <= expectedMarkerCount; ++start) {
        for (const int polarity : {-1, 1}) {
            bool matchesCode = true;
            for (std::size_t i = 0; i < peaks.size(); ++i) {
                if (peaks[i].polarity != polarity * RenderSignal::MarkerPolarity(start + i)) {
                    matchesCode = false;
                    break;
                }
            }
            if (matchesCode) { matchStart = start; ++matches; }
        }
    }
    if (matches != 1) return false;
    for (std::size_t i = 0; i < peaks.size(); ++i) indices.push_back(matchStart + i);
    return true;
}

inline bool QpcAtRenderFrame(const ClockAnalysis::Series& renderClock, double frame,
                             std::uint32_t rate, double& qpc100ns,
                             double* bracket100ns = nullptr) noexcept {
    if (rate == 0 || renderClock.frequency == 0 || !std::isfinite(frame) || frame < 0) return false;
    const long double target = static_cast<long double>(frame) * renderClock.frequency / rate;
    const ClockAnalysis::Snapshot* previous = nullptr;
    for (const auto& sample : renderClock.snapshots) {
        if (sample.hresult != ClockAnalysis::kSOk) continue;
        if (previous != nullptr && sample.position > previous->position &&
            sample.qpc100ns > previous->qpc100ns && target >= previous->position && target <= sample.position) {
            const long double fraction = (target - previous->position) /
                (static_cast<long double>(sample.position) - previous->position);
            qpc100ns = static_cast<double>(previous->qpc100ns + fraction *
                (static_cast<long double>(sample.qpc100ns) - previous->qpc100ns));
            if (bracket100ns != nullptr) *bracket100ns = static_cast<double>(sample.qpc100ns - previous->qpc100ns);
            return std::isfinite(qpc100ns);
        }
        previous = &sample;
    }
    return false;
}

inline bool CaptureQpcAtFrame(const CaptureData& capture, const CaptureTimeline& timeline,
                              double storedFrame, double& qpc100ns,
                              std::uint64_t* devicePosition = nullptr) noexcept {
    if (!timeline.reliable) return false;
    if (!std::isfinite(storedFrame) || storedFrame < 0) return false;
    for (const auto& packet : capture.Packets()) {
        if (storedFrame < static_cast<double>(packet.firstStoredFrame) ||
            storedFrame >= static_cast<double>(packet.firstStoredFrame + packet.frames)) continue;
        if (packet.silent || packet.discontinuity || packet.timestampError || packet.qpc100ns == 0)
            return false;
        const auto packetOffset = storedFrame - static_cast<double>(packet.firstStoredFrame);
        const long double targetPosition = static_cast<long double>(packet.devicePosition) + packetOffset;
        qpc100ns = static_cast<double>(timeline.intercept100ns +
            timeline.qpcUnitsPerFrame * targetPosition);
        if (devicePosition != nullptr) *devicePosition = packet.devicePosition +
            static_cast<std::uint64_t>(std::floor(packetOffset));
        return true;
    }
    return false;
}

inline double SamplesToMilliseconds(double samples, std::uint32_t rate) noexcept {
    return rate == 0 ? std::numeric_limits<double>::quiet_NaN()
                     : samples * 1'000.0 / rate;
}

struct Delay {
    double samples = 0;
    double milliseconds = 0;
    double captureFrame = 0;
    double renderFrame = 0;
    double confidence = 0;
    double ambiguityMargin = 0;
    std::uint64_t captureDevicePosition = 0;
    double renderBracketMilliseconds = 0;
};

struct Analysis {
    Status status = Status::Unreliable;
    enum class TimingQuality { Good, Warning, Unreliable };
    TimingQuality timingQuality = TimingQuality::Unreliable;
    const char* timingQualityReason = "timing alignment has not been assessed";
    const char* rejectionReason = "analysis did not complete";
    CorrelationDiagnostics correlation;
    SequenceDiagnostics sequence;
    std::vector<CorrelationPeak> peaks;
    std::vector<Delay> delays;
    double medianSamples = 0;
    double minimumSamples = 0;
    double maximumSamples = 0;
    double capturePeakDbfs = -std::numeric_limits<double>::infinity();
    double captureRmsDbfs = -std::numeric_limits<double>::infinity();
    std::uint64_t captureClippedSamples = 0;
    std::uint64_t discontinuities = 0;
    std::uint64_t timestampErrors = 0;
    std::uint64_t positionGaps = 0;
    std::uint64_t framePositionGaps = 0;
    double maximumRenderBracketMilliseconds = 0;
    double delayVariationSamples = std::numeric_limits<double>::quiet_NaN();
    double maximumLocalDelayJumpSamples = std::numeric_limits<double>::quiet_NaN();
    double delayTrendSamples = std::numeric_limits<double>::quiet_NaN();
    double capturePacketRateErrorPpm = 0;
    double capturePacketRSquared = 0;
    double capturePacketResidualFrames = 0;
    double capturePacketMaximumResidualFrames = std::numeric_limits<double>::quiet_NaN();
    double capturePacketMaximumLocalIntervalDeviationFrames = std::numeric_limits<double>::quiet_NaN();
    long double capturePacketIntercept100ns = 0;
    long double capturePacketSlope100nsPerFrame = 0;
};

inline const char* TimingQualityText(Analysis::TimingQuality quality) noexcept {
    switch (quality) {
    case Analysis::TimingQuality::Good: return "GOOD";
    case Analysis::TimingQuality::Warning: return "WARNING";
    case Analysis::TimingQuality::Unreliable: return "UNRELIABLE";
    }
    return "UNRELIABLE";
}

inline Analysis::TimingQuality ClassifyTimingQuality(
    bool structurallyValid, std::uint32_t rate,
    double captureFitRmsFrames, double captureFitMaximumFrames,
    double maximumCaptureIntervalDeviationFrames,
    double captureClockRmsMilliseconds, double captureClockMaximumMilliseconds,
    double renderClockRmsMilliseconds, double renderClockMaximumMilliseconds,
    double renderBracketMilliseconds, double delayVariationSamples,
    double maximumLocalDelayJumpSamples, double delayTrendSamples) noexcept {
    if (!structurallyValid || rate == 0) return Analysis::TimingQuality::Unreliable;
    const std::array<double, 10> frameErrors{
        captureFitRmsFrames, captureFitMaximumFrames, maximumCaptureIntervalDeviationFrames,
        captureClockRmsMilliseconds * rate / 1'000.0,
        captureClockMaximumMilliseconds * rate / 1'000.0,
        renderClockRmsMilliseconds * rate / 1'000.0,
        renderClockMaximumMilliseconds * rate / 1'000.0,
        delayVariationSamples, maximumLocalDelayJumpSamples, std::abs(delayTrendSamples)};
    if (std::any_of(frameErrors.begin(), frameErrors.end(),
        [](double value) { return !std::isfinite(value) || value < 0; }) ||
        !std::isfinite(renderBracketMilliseconds) || renderBracketMilliseconds < 0)
        return Analysis::TimingQuality::Unreliable;
    const auto worstFrames = *std::max_element(frameErrors.begin(), frameErrors.end());
    if (worstFrames > kWarningTimingErrorFrames ||
        renderBracketMilliseconds > kWarningRenderClockBracketMilliseconds)
        return Analysis::TimingQuality::Unreliable;
    if (worstFrames > kGoodTimingErrorFrames ||
        renderBracketMilliseconds > kGoodRenderClockBracketMilliseconds)
        return Analysis::TimingQuality::Warning;
    return Analysis::TimingQuality::Good;
}

constexpr std::size_t kTimestampAnomalyReportCount = 5;
constexpr double kTimestampAnomalyDiagnosticFloorMilliseconds = 0.125;
constexpr double kMaximumCommonModeWindowMilliseconds = 250.0;

enum class CaptureOffsetState { Unavailable, Recovered, Persistent, Unsettled };

inline const char* CaptureOffsetStateText(CaptureOffsetState state) noexcept {
    switch (state) {
    case CaptureOffsetState::Unavailable: return "unavailable";
    case CaptureOffsetState::Recovered: return "recovered";
    case CaptureOffsetState::Persistent: return "persistent offset";
    case CaptureOffsetState::Unsettled: return "not settled";
    }
    return "unavailable";
}

enum class CommonModeState { InsufficientData, NotDetected, Partial, PresentInAllThree };

inline const char* CommonModeStateText(CommonModeState state) noexcept {
    switch (state) {
    case CommonModeState::InsufficientData: return "INSUFFICIENT_DATA";
    case CommonModeState::NotDetected: return "NOT_DETECTED";
    case CommonModeState::Partial: return "PARTIAL_MATCH";
    case CommonModeState::PresentInAllThree: return "CONSISTENT_ACROSS_ALL_THREE";
    }
    return "INSUFFICIENT_DATA";
}

struct CapturePacketAnomaly {
    bool valid = false;
    std::size_t packetIndex = 0;
    std::uint64_t devicePosition = 0;
    double streamTimeMilliseconds = 0;
    std::uint64_t qpc100ns = 0;
    double actualDeltaMilliseconds = 0;
    double expectedDeltaMilliseconds = 0;
    double signedResidualMilliseconds = 0;
    double signedResidualFrames = 0;
    double magnitude = 0;
    double beforeOffsetFrames = 0;
    double afterOffsetFrames = 0;
    CaptureOffsetState offsetState = CaptureOffsetState::Unavailable;
};

struct ClockSnapshotAnomaly {
    bool valid = false;
    std::size_t snapshotIndex = 0;
    std::uint32_t sequence = 0;
    std::uint64_t position = 0;
    std::uint64_t qpc100ns = 0;
    double streamTimeMilliseconds = 0;
    double signedFitResidualMilliseconds = 0;
    double magnitude = 0;
    double beforeEquivalentQpcOffsetMilliseconds = 0;
    double equivalentQpcOffsetMilliseconds = 0;
    double signedAdjacentQpcStepMilliseconds = 0;
    double recoveryDeltaMilliseconds = 0;
    bool hasPrevious = false;
    bool hasNext = false;
};

struct ClockStepAnomaly {
    bool valid = false;
    std::size_t snapshotIndex = 0;
    std::uint64_t qpc100ns = 0;
    double streamTimeMilliseconds = 0;
    double signedStepMilliseconds = 0;
    double beforeOffsetMilliseconds = 0;
    double afterOffsetMilliseconds = 0;
};

struct CommonModeMatch {
    bool matched = false;
    std::size_t capturePacketIndex = 0;
    std::uint64_t clockEventQpc100ns = 0;
    double timeDifferenceMilliseconds = 0;
    double captureStepMilliseconds = 0;
    double clockStepMilliseconds = 0;
    double magnitudeDifferenceMilliseconds = 0;
    double comparisonWindowMilliseconds = 0;
};

struct TimestampAnomalyDiagnostics {
    std::array<CapturePacketAnomaly, kTimestampAnomalyReportCount> capturePackets{};
    std::size_t capturePacketCount = 0;
    std::array<ClockSnapshotAnomaly, kTimestampAnomalyReportCount> captureClockResiduals{};
    std::size_t captureClockResidualCount = 0;
    std::array<ClockSnapshotAnomaly, kTimestampAnomalyReportCount> renderClockResiduals{};
    std::size_t renderClockResidualCount = 0;
    ClockStepAnomaly captureClockStrongestStep;
    ClockStepAnomaly renderClockStrongestStep;
    CommonModeMatch captureClockMatch;
    CommonModeMatch renderClockMatch;
    CommonModeState commonMode = CommonModeState::InsufficientData;
    double commonModeWindowMilliseconds = 0;
};

template <typename T, std::size_t N>
inline void InsertLargest(std::array<T, N>& entries, std::size_t& count,
                          const T& entry) noexcept {
    std::size_t index = 0;
    while (index < count && entries[index].magnitude >= entry.magnitude)
        ++index;
    if (index >= N) return;
    const auto newCount = std::min(count + 1, N);
    for (std::size_t i = newCount; i > index + 1; --i) entries[i - 1] = entries[i - 2];
    entries[index] = entry;
    count = newCount;
}

inline double CaptureQpcResidualFrames(const CapturePacket& packet, long double intercept100ns,
                                       long double qpcUnitsPerFrame) noexcept {
    if (packet.qpc100ns == 0 || qpcUnitsPerFrame <= 0 ||
        !std::isfinite(static_cast<double>(qpcUnitsPerFrame)))
        return std::numeric_limits<double>::quiet_NaN();
    const long double fittedQpc = intercept100ns + qpcUnitsPerFrame * packet.devicePosition;
    return static_cast<double>((static_cast<long double>(packet.qpc100ns) - fittedQpc) /
                               qpcUnitsPerFrame);
}

inline bool ClockQpcOffsetMilliseconds(const ClockAnalysis::Snapshot& snapshot,
                                      const ClockAnalysis::SideResult& fit,
                                      double& fitResidualMilliseconds,
                                      double& qpcOffsetMilliseconds,
                                      double& streamTimeMilliseconds) noexcept {
    if (!fit.hasFitModel || fit.frequency == 0 || fit.fitSlopeSecondsPerQpcSecond <= 0 ||
        snapshot.hresult != ClockAnalysis::kSOk || snapshot.qpc100ns == 0 ||
        !std::isfinite(static_cast<double>(fit.fitSlopeSecondsPerQpcSecond))) return false;
    const long double qpcSeconds =
        (static_cast<long double>(snapshot.qpc100ns) - fit.fitQpcOrigin100ns) / ClockAnalysis::kHnsPerSecond;
    const long double positionSeconds =
        (static_cast<long double>(snapshot.position) - fit.fitPositionOrigin) / fit.frequency;
    const long double residual = positionSeconds -
        (fit.fitInterceptPositionSeconds + fit.fitSlopeSecondsPerQpcSecond * qpcSeconds);
    fitResidualMilliseconds = static_cast<double>(residual * 1'000.0L);
    qpcOffsetMilliseconds = static_cast<double>(-residual / fit.fitSlopeSecondsPerQpcSecond * 1'000.0L);
    streamTimeMilliseconds = static_cast<double>(positionSeconds * 1'000.0L);
    return std::isfinite(fitResidualMilliseconds) && std::isfinite(qpcOffsetMilliseconds) &&
        std::isfinite(streamTimeMilliseconds);
}

inline double MeanCaptureResidualFrames(std::span<const CapturePacket> packets,
                                        std::size_t begin, std::size_t end,
                                        long double intercept100ns,
                                        long double qpcUnitsPerFrame) noexcept {
    long double sum = 0;
    std::size_t count = 0;
    end = std::min(end, packets.size());
    for (std::size_t i = begin; i < end; ++i) {
        const auto residual = CaptureQpcResidualFrames(packets[i], intercept100ns, qpcUnitsPerFrame);
        if (!std::isfinite(residual)) continue;
        sum += residual;
        ++count;
    }
    return count == 0 ? std::numeric_limits<double>::quiet_NaN()
        : static_cast<double>(sum / count);
}

inline void CompleteCaptureOffsetDiagnostics(CapturePacketAnomaly& anomaly,
                                             std::span<const CapturePacket> packets,
                                             long double intercept100ns,
                                             long double qpcUnitsPerFrame) noexcept {
    const auto index = anomaly.packetIndex;
    const auto beforeBegin = index > 3 ? index - 3 : 0;
    const auto beforeEnd = index;
    const auto afterEnd = std::min(index + 3, packets.size());
    anomaly.beforeOffsetFrames = MeanCaptureResidualFrames(packets, beforeBegin, beforeEnd,
        intercept100ns, qpcUnitsPerFrame);
    anomaly.afterOffsetFrames = MeanCaptureResidualFrames(packets, index, afterEnd,
        intercept100ns, qpcUnitsPerFrame);
    if (!std::isfinite(anomaly.beforeOffsetFrames) || !std::isfinite(anomaly.afterOffsetFrames)) return;
    const auto frameTolerance = kGoodTimingErrorFrames;
    if (index + 1 < packets.size()) {
        const auto nextResidual = CaptureQpcResidualFrames(packets[index + 1], intercept100ns,
                                                           qpcUnitsPerFrame);
        if (std::isfinite(nextResidual) &&
            std::abs(nextResidual - anomaly.beforeOffsetFrames) <= frameTolerance) {
            anomaly.offsetState = CaptureOffsetState::Recovered;
            return;
        }
    }
    double spread = 0;
    for (std::size_t i = index; i < afterEnd; ++i) {
        const auto residual = CaptureQpcResidualFrames(packets[i], intercept100ns, qpcUnitsPerFrame);
        if (std::isfinite(residual)) spread = std::max(spread, std::abs(residual - anomaly.afterOffsetFrames));
    }
    anomaly.offsetState = std::abs(anomaly.afterOffsetFrames - anomaly.beforeOffsetFrames) > frameTolerance &&
        spread <= frameTolerance ? CaptureOffsetState::Persistent : CaptureOffsetState::Unsettled;
}

inline TimestampAnomalyDiagnostics AnalyzeTimestampAnomalies(
    std::span<const CapturePacket> packets, std::uint32_t rate,
    long double captureIntercept100ns, long double captureQpcUnitsPerFrame,
    const ClockAnalysis::Series& captureClock, const ClockAnalysis::SideResult& captureClockFit,
    const ClockAnalysis::Series& renderClock, const ClockAnalysis::SideResult& renderClockFit) noexcept {
    TimestampAnomalyDiagnostics result;
    if (rate == 0) return result;
    const bool captureFitAvailable = captureQpcUnitsPerFrame > 0 &&
        std::isfinite(static_cast<double>(captureQpcUnitsPerFrame));
    if (packets.size() >= 2) {
        const auto firstPosition = packets.front().devicePosition;
        const auto qpcUnitsPerFrame = captureFitAvailable
            ? captureQpcUnitsPerFrame : static_cast<long double>(ClockAnalysis::kHnsPerSecond) / rate;
        for (std::size_t i = 1; i < packets.size(); ++i) {
            const auto& previous = packets[i - 1];
            const auto& current = packets[i];
            if (previous.qpc100ns == 0 || current.qpc100ns == 0) continue;
            const long double actualDelta = static_cast<long double>(current.qpc100ns) - previous.qpc100ns;
            const long double frameDelta = static_cast<long double>(current.devicePosition) - previous.devicePosition;
            const long double expectedDelta = qpcUnitsPerFrame * frameDelta;
            CapturePacketAnomaly anomaly;
            anomaly.valid = true;
            anomaly.packetIndex = i;
            anomaly.devicePosition = current.devicePosition;
            anomaly.streamTimeMilliseconds = static_cast<double>(
                (static_cast<long double>(current.devicePosition) - firstPosition) * 1'000.0L / rate);
            anomaly.qpc100ns = current.qpc100ns;
            anomaly.actualDeltaMilliseconds = static_cast<double>(actualDelta / 10'000.0L);
            anomaly.expectedDeltaMilliseconds = static_cast<double>(expectedDelta / 10'000.0L);
            anomaly.signedResidualMilliseconds = static_cast<double>((actualDelta - expectedDelta) / 10'000.0L);
            anomaly.signedResidualFrames = static_cast<double>((actualDelta - expectedDelta) / qpcUnitsPerFrame);
            anomaly.magnitude = std::abs(anomaly.signedResidualMilliseconds);
            InsertLargest(result.capturePackets, result.capturePacketCount, anomaly);
        }
        if (captureFitAvailable) {
            for (std::size_t i = 0; i < result.capturePacketCount; ++i)
                CompleteCaptureOffsetDiagnostics(result.capturePackets[i], packets,
                    captureIntercept100ns, qpcUnitsPerFrame);
        }
    }

    const auto analyzeClock = [](const ClockAnalysis::Series& series,
                                 const ClockAnalysis::SideResult& fit,
                                 auto& top, std::size_t& topCount,
                                 ClockStepAnomaly& strongestStep) {
        double firstPositionTime = std::numeric_limits<double>::quiet_NaN();
        double previousOffset = 0;
        bool havePrevious = false;
        for (std::size_t i = 0; i < series.snapshots.size(); ++i) {
            const auto& snapshot = series.snapshots[i];
            double fitResidual = 0;
            double qpcOffset = 0;
            double streamTime = 0;
            if (!ClockQpcOffsetMilliseconds(snapshot, fit, fitResidual, qpcOffset, streamTime)) continue;
            if (!std::isfinite(firstPositionTime)) firstPositionTime = streamTime;
            ClockSnapshotAnomaly anomaly;
            anomaly.valid = true;
            anomaly.snapshotIndex = i;
            anomaly.sequence = snapshot.sequence;
            anomaly.position = snapshot.position;
            anomaly.qpc100ns = snapshot.qpc100ns;
            anomaly.streamTimeMilliseconds = streamTime - firstPositionTime;
            anomaly.signedFitResidualMilliseconds = fitResidual;
            anomaly.magnitude = std::abs(fitResidual);
            anomaly.beforeEquivalentQpcOffsetMilliseconds = previousOffset;
            anomaly.equivalentQpcOffsetMilliseconds = qpcOffset;
            if (havePrevious) {
                anomaly.hasPrevious = true;
                anomaly.signedAdjacentQpcStepMilliseconds = qpcOffset - previousOffset;
                if (!strongestStep.valid || std::abs(anomaly.signedAdjacentQpcStepMilliseconds) >
                    std::abs(strongestStep.signedStepMilliseconds)) {
                    strongestStep = {true, i, snapshot.qpc100ns, anomaly.streamTimeMilliseconds,
                        anomaly.signedAdjacentQpcStepMilliseconds, previousOffset, qpcOffset};
                }
            }
            InsertLargest(top, topCount, anomaly);
            previousOffset = qpcOffset;
            havePrevious = true;
        }
        for (std::size_t entry = 0; entry < topCount; ++entry) {
            auto& anomaly = top[entry];
            for (std::size_t i = anomaly.snapshotIndex + 1; i < series.snapshots.size(); ++i) {
                double residual = 0;
                double offset = 0;
                double streamTime = 0;
                if (!ClockQpcOffsetMilliseconds(series.snapshots[i], fit, residual, offset, streamTime)) continue;
                anomaly.hasNext = true;
                anomaly.recoveryDeltaMilliseconds = offset - anomaly.equivalentQpcOffsetMilliseconds;
                break;
            }
        }
    };
    analyzeClock(captureClock, captureClockFit, result.captureClockResiduals,
        result.captureClockResidualCount, result.captureClockStrongestStep);
    analyzeClock(renderClock, renderClockFit, result.renderClockResiduals,
        result.renderClockResidualCount, result.renderClockStrongestStep);

    double maximumClockIntervalMs = 0;
    const auto findMaximumClockInterval = [&](const ClockAnalysis::Series& series) {
        const ClockAnalysis::Snapshot* previous = nullptr;
        for (const auto& sample : series.snapshots) {
            if (sample.hresult != ClockAnalysis::kSOk || sample.qpc100ns == 0) continue;
            if (previous != nullptr && sample.qpc100ns > previous->qpc100ns)
                maximumClockIntervalMs = std::max(maximumClockIntervalMs,
                    static_cast<double>(sample.qpc100ns - previous->qpc100ns) / 10'000.0);
            previous = &sample;
        }
    };
    findMaximumClockInterval(captureClock);
    findMaximumClockInterval(renderClock);
    double maximumPacketIntervalMs = 0;
    for (std::size_t i = 1; i < packets.size(); ++i)
        if (packets[i].qpc100ns > packets[i - 1].qpc100ns)
            maximumPacketIntervalMs = std::max(maximumPacketIntervalMs,
                static_cast<double>(packets[i].qpc100ns - packets[i - 1].qpc100ns) / 10'000.0);
    result.commonModeWindowMilliseconds = std::min(kMaximumCommonModeWindowMilliseconds,
        std::max({maximumClockIntervalMs, maximumPacketIntervalMs, 1.0}));

    const auto findMatch = [&](const auto& clockResiduals, std::size_t clockResidualCount,
                               const ClockStepAnomaly& strongestStep) {
        CommonModeMatch best;
        const auto consider = [&](const CapturePacketAnomaly& packet, std::uint64_t clockQpc,
                                  double clockStepMilliseconds) {
            if (std::abs(clockStepMilliseconds) < kTimestampAnomalyDiagnosticFloorMilliseconds)
                return;
            const double timeDifference = std::abs(static_cast<double>(
                static_cast<long double>(packet.qpc100ns) - clockQpc) / 10'000.0);
            const double magnitudeDifference = std::abs(std::abs(packet.signedResidualMilliseconds) -
                std::abs(clockStepMilliseconds));
            const double magnitudeTolerance = std::max(kTimestampAnomalyDiagnosticFloorMilliseconds,
                0.25 * std::max(std::abs(packet.signedResidualMilliseconds),
                                std::abs(clockStepMilliseconds)));
            const bool samePolarity = std::signbit(packet.signedResidualMilliseconds) ==
                std::signbit(clockStepMilliseconds);
            if (timeDifference <= result.commonModeWindowMilliseconds && samePolarity &&
                magnitudeDifference <= magnitudeTolerance &&
                (!best.matched || timeDifference < best.timeDifferenceMilliseconds ||
                 (timeDifference == best.timeDifferenceMilliseconds &&
                  magnitudeDifference < best.magnitudeDifferenceMilliseconds))) {
                best = {true, packet.packetIndex, clockQpc, timeDifference,
                    packet.signedResidualMilliseconds, clockStepMilliseconds,
                    magnitudeDifference, result.commonModeWindowMilliseconds};
            }
        };
        for (std::size_t i = 0; i < result.capturePacketCount; ++i) {
            const auto& packet = result.capturePackets[i];
            if (std::abs(packet.signedResidualMilliseconds) < kTimestampAnomalyDiagnosticFloorMilliseconds)
                continue;
            for (std::size_t j = 0; j < clockResidualCount; ++j)
                if (clockResiduals[j].hasPrevious)
                    consider(packet, clockResiduals[j].qpc100ns,
                        clockResiduals[j].signedAdjacentQpcStepMilliseconds);
            if (strongestStep.valid)
                consider(packet, strongestStep.qpc100ns, strongestStep.signedStepMilliseconds);
        }
        return best;
    };
    result.captureClockMatch = findMatch(result.captureClockResiduals,
        result.captureClockResidualCount, result.captureClockStrongestStep);
    result.renderClockMatch = findMatch(result.renderClockResiduals,
        result.renderClockResidualCount, result.renderClockStrongestStep);
    if (result.capturePacketCount == 0 ||
        (result.captureClockResidualCount == 0 && result.renderClockResidualCount == 0)) {
        result.commonMode = CommonModeState::InsufficientData;
    } else if (result.captureClockMatch.matched && result.renderClockMatch.matched) {
        const double crossClockTimeDifference = std::abs(static_cast<double>(
            static_cast<long double>(result.captureClockMatch.clockEventQpc100ns) -
            result.renderClockMatch.clockEventQpc100ns) / 10'000.0);
        const double crossClockMagnitudeDifference = std::abs(
            std::abs(result.captureClockMatch.clockStepMilliseconds) -
            std::abs(result.renderClockMatch.clockStepMilliseconds));
        const double crossClockMagnitudeTolerance = std::max(
            kTimestampAnomalyDiagnosticFloorMilliseconds,
            0.25 * std::max(std::abs(result.captureClockMatch.clockStepMilliseconds),
                            std::abs(result.renderClockMatch.clockStepMilliseconds)));
        const bool samePacket = result.captureClockMatch.capturePacketIndex ==
            result.renderClockMatch.capturePacketIndex;
        const bool samePolarity = std::signbit(result.captureClockMatch.clockStepMilliseconds) ==
            std::signbit(result.renderClockMatch.clockStepMilliseconds);
        if (samePacket && crossClockTimeDifference <= result.commonModeWindowMilliseconds &&
            samePolarity && crossClockMagnitudeDifference <= crossClockMagnitudeTolerance)
            result.commonMode = CommonModeState::PresentInAllThree;
        else
            result.commonMode = CommonModeState::Partial;
    } else if (result.captureClockMatch.matched || result.renderClockMatch.matched) {
        result.commonMode = CommonModeState::Partial;
    } else {
        result.commonMode = CommonModeState::NotDetected;
    }
    return result;
}

inline Analysis Analyze(const CaptureData& capture, const RenderSignal& render,
                        const ClockAnalysis::Series& captureClock,
                        const ClockAnalysis::Series& renderClock,
                        const ClockAnalysis::Result& clockQuality,
                        bool streamsCompleted) {
    Analysis result;
    result.capturePeakDbfs = capture.PeakDbfs();
    result.captureRmsDbfs = capture.RmsDbfs();
    result.captureClippedSamples = capture.ClippedSamples();
    const auto& packets = capture.Packets();
    const auto captureTimeline = FitCaptureTimeline(packets, capture.Rate());
    result.capturePacketIntercept100ns = captureTimeline.intercept100ns;
    result.capturePacketSlope100nsPerFrame = captureTimeline.qpcUnitsPerFrame;
    result.capturePacketRateErrorPpm = captureTimeline.rateErrorPpm;
    result.capturePacketRSquared = captureTimeline.rSquared;
    result.capturePacketResidualFrames = captureTimeline.rmsResidualFrames;
    result.capturePacketMaximumResidualFrames = captureTimeline.maximumResidualFrames;
    result.capturePacketMaximumLocalIntervalDeviationFrames =
        captureTimeline.maximumLocalIntervalDeviationFrames;
    std::uint64_t expectedPosition = 0;
    std::uint64_t expectedFramePosition = 0;
    std::uint64_t previousQpc = 0;
    bool haveExpected = false;
    bool haveExpectedFramePosition = false;
    bool haveQpc = false;
    for (const auto& packet : packets) {
        if (packet.discontinuity) ++result.discontinuities;
        if (packet.timestampError) ++result.timestampErrors;
        if (packet.qpc100ns == 0 || (haveQpc && packet.qpc100ns <= previousQpc)) {
            ++result.timestampErrors;
            haveQpc = false;
        } else {
            previousQpc = packet.qpc100ns;
            haveQpc = true;
        }
        if (packet.devicePosition > std::numeric_limits<std::uint64_t>::max() - packet.frames) {
            ++result.framePositionGaps;
            haveExpectedFramePosition = false;
        } else {
            if (haveExpectedFramePosition && packet.devicePosition != expectedFramePosition)
                ++result.framePositionGaps;
            expectedFramePosition = packet.devicePosition + packet.frames;
            haveExpectedFramePosition = true;
        }
        if (packet.qpc100ns == 0 || packet.devicePosition > std::numeric_limits<std::uint64_t>::max() - packet.frames) {
            ++result.positionGaps;
            haveExpected = false;
        } else {
            if (haveExpected && packet.devicePosition != expectedPosition) ++result.positionGaps;
            expectedPosition = packet.devicePosition + packet.frames;
            haveExpected = true;
        }
    }
    result.correlation = DiagnoseMarkers(capture.Samples(), render.Marker());
    result.peaks = result.correlation.detectedPeaks;
    result.sequence = DetectCoherentSequence(result.correlation, render);
    if (result.sequence.status != SequenceStatus::Matched) {
        result.timingQualityReason = "no accepted marker sequence is available for timing assessment";
        if (!streamsCompleted || capture.InvalidData() || capture.Truncated()) {
            result.rejectionReason = "one or both WASAPI streams or Capture storage did not complete cleanly";
            result.status = Status::Failed;
        } else if (result.sequence.status == SequenceStatus::Ambiguous) {
            result.rejectionReason = result.sequence.rejectionReason;
            result.status = Status::Unreliable;
        } else {
            result.rejectionReason = result.correlation.available
                ? result.sequence.rejectionReason : kNoCorrelationWindow;
            result.status = Status::SignalNotFound;
        }
        return result;
    }
    if (!streamsCompleted || capture.InvalidData() || capture.Truncated()) {
        result.timingQualityReason = "stream or Capture storage did not complete cleanly";
        result.rejectionReason = "one or both WASAPI streams did not complete cleanly";
        result.status = Status::Failed;
        return result;
    }
    const bool timingStructureValid = result.discontinuities == 0 && result.timestampErrors == 0 &&
        result.positionGaps == 0 && result.captureClippedSamples == 0 &&
        clockQuality.status == ClockAnalysis::Status::Available && captureTimeline.reliable &&
        captureClock.snapshots.size() >= ClockAnalysis::kMinimumFitSamples &&
        renderClock.snapshots.size() >= ClockAnalysis::kMinimumFitSamples;
    result.delays.reserve(result.sequence.matches.size());
    bool mappingComplete = true;
    for (std::size_t i = 0; i < result.sequence.matches.size(); ++i) {
        const auto& sequenceMatch = result.sequence.matches[i];
        const auto& peak = sequenceMatch.peak;
        const auto markerIndex = sequenceMatch.markerIndex;
        const double renderFrame = static_cast<double>(render.MarkerStarts()[markerIndex]);
        double renderQpc = 0;
        double bracket = 0;
        double captureQpc = 0;
        std::uint64_t capturePosition = 0;
        if (!QpcAtRenderFrame(renderClock, renderFrame, render.Rate(), renderQpc, &bracket) ||
            !CaptureQpcAtFrame(capture, captureTimeline, peak.offsetFrames,
                               captureQpc, &capturePosition)) {
            mappingComplete = false;
            result.timingQualityReason = "one or more marker positions could not be mapped to the common QPC timeline";
            break;
        }
        const double delayMilliseconds = (captureQpc - renderQpc) * 1'000.0 / kQpcUnitsPerSecond;
        if (!std::isfinite(delayMilliseconds) || delayMilliseconds < 0) {
            mappingComplete = false;
            result.timingQualityReason = "one or more mapped delays are non-finite or negative";
            break;
        }
        const double delaySamples = delayMilliseconds * render.Rate() / 1'000.0;
        result.maximumRenderBracketMilliseconds = std::max(result.maximumRenderBracketMilliseconds,
            bracket * 1'000.0 / kQpcUnitsPerSecond);
        result.delays.push_back({delaySamples, delayMilliseconds, peak.offsetFrames, renderFrame,
            peak.confidence, peak.ambiguityMargin, capturePosition,
            bracket * 1'000.0 / kQpcUnitsPerSecond});
    }
    if (result.delays.size() < kMinimumMarkers) {
        result.timingQualityReason = "fewer than five complete marker-to-QPC mappings are available";
        result.rejectionReason = "fewer than the required five markers were mapped";
        result.status = Status::Unreliable;
        return result;
    }
    if (!mappingComplete || result.delays.size() != result.sequence.matches.size()) {
        result.rejectionReason = result.timingQualityReason;
        result.status = Status::Unreliable;
        return result;
    }
    std::vector<double> sorted;
    sorted.reserve(result.delays.size());
    for (const auto& delay : result.delays) sorted.push_back(delay.samples);
    std::sort(sorted.begin(), sorted.end());
    result.minimumSamples = sorted.front();
    result.maximumSamples = sorted.back();
    result.delayVariationSamples = result.maximumSamples - result.minimumSamples;
    for (std::size_t i = 1; i < result.delays.size(); ++i) {
        result.maximumLocalDelayJumpSamples = std::max(
            std::isfinite(result.maximumLocalDelayJumpSamples) ? result.maximumLocalDelayJumpSamples : 0.0,
            std::abs(result.delays[i].samples - result.delays[i - 1].samples));
    }
    if (result.delays.size() >= 2) {
        long double meanFrame = 0;
        long double meanDelay = 0;
        for (const auto& delay : result.delays) {
            meanFrame += delay.renderFrame;
            meanDelay += delay.samples;
        }
        meanFrame /= result.delays.size();
        meanDelay /= result.delays.size();
        long double covariance = 0;
        long double variance = 0;
        for (const auto& delay : result.delays) {
            const long double x = delay.renderFrame - meanFrame;
            covariance += x * (delay.samples - meanDelay);
            variance += x * x;
        }
        if (variance > 0)
            result.delayTrendSamples = static_cast<double>(covariance / variance *
                (result.delays.back().renderFrame - result.delays.front().renderFrame));
    }
    if (!timingStructureValid)
        result.timingQualityReason = "Capture continuity/timestamps, clipping, stream completion or clock-fit validation failed";
    const auto middle = sorted.size() / 2;
    result.medianSamples = sorted.size() % 2 == 0
        ? (sorted[middle - 1] + sorted[middle]) / 2.0 : sorted[middle];
    result.timingQuality = ClassifyTimingQuality(timingStructureValid, render.Rate(),
        result.capturePacketResidualFrames, result.capturePacketMaximumResidualFrames,
        result.capturePacketMaximumLocalIntervalDeviationFrames,
        clockQuality.capture.rmsResidualMilliseconds, clockQuality.capture.maximumResidualMilliseconds,
        clockQuality.render.rmsResidualMilliseconds, clockQuality.render.maximumResidualMilliseconds,
        result.maximumRenderBracketMilliseconds, result.delayVariationSamples,
        result.maximumLocalDelayJumpSamples, result.delayTrendSamples);
    if (result.timingQuality == Analysis::TimingQuality::Unreliable) {
        if (timingStructureValid)
            result.timingQualityReason = "one or more timing residuals, local jumps or delay trends exceed the 0.5 ms reliability budget";
        result.rejectionReason = result.timingQualityReason;
        result.status = Status::Unreliable;
        return result;
    }
    if (result.timingQuality == Analysis::TimingQuality::Warning) {
        result.timingQualityReason = "alignment exceeds the 0.125 ms precision budget but remains within the 0.5 ms diagnostic budget";
        result.rejectionReason = result.timingQualityReason;
        result.status = Status::Unreliable;
        return result;
    }
    result.timingQualityReason = "all fit residuals and delay variation are within the 0.125 ms precision budget";
    result.status = Status::Measured;
    result.rejectionReason = "none";
    return result;
}

} // namespace AnalogLoopback
