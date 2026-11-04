#pragma once

#include "analog_loopback.h"

#include <algorithm>
#include <atomic>
#include <cstdint>
#include <limits>
#include <span>
#include <vector>

namespace MonitorRtlPreflight {

enum class State : std::uint8_t { Idle, PreflightCapture, Validating, Armed, Measuring, Stopping, Completed, Failed, Cancelled };
enum class RouteError { None, InvalidState, InvalidFormat, InvalidSize, InvalidPointer };

inline const char* StateText(State state) noexcept {
    switch (state) {
    case State::Idle: return "IDLE";
    case State::PreflightCapture: return "PREFLIGHT_CAPTURE";
    case State::Validating: return "VALIDATING";
    case State::Armed: return "ARMED";
    case State::Measuring: return "MEASURING";
    case State::Stopping: return "STOPPING";
    case State::Completed: return "COMPLETED";
    case State::Failed: return "FAILED";
    case State::Cancelled: return "CANCELLED";
    }
    return "FAILED";
}

class StateMachine {
public:
    bool Transition(State next) noexcept {
        auto current = state_.load(std::memory_order_acquire);
        if (next == State::Cancelled || next == State::Failed) {
            while (current != State::Completed && current != State::Failed && current != State::Cancelled) {
                if (state_.compare_exchange_weak(current, next, std::memory_order_acq_rel)) return true;
            }
            return false;
        }
        const bool allowed = (current == State::Idle && next == State::PreflightCapture) ||
            (current == State::PreflightCapture && (next == State::Validating || next == State::Stopping)) ||
            (current == State::Validating && (next == State::Armed || next == State::Stopping)) ||
            (current == State::Armed && next == State::Stopping) ||
            (current == State::Measuring && next == State::Stopping) ||
            (current == State::Stopping && next == State::Completed);
        return allowed && state_.compare_exchange_strong(current, next, std::memory_order_acq_rel);
    }
    State Get() const noexcept { return state_.load(std::memory_order_acquire); }
private:
    std::atomic<State> state_{State::Idle};
};

struct StereoRouter {
    RouteError Process(std::span<const std::int16_t> capture, std::span<std::int16_t> render,
                       std::uint32_t frames, bool validPcm16Stereo, bool armed,
                       bool silent = false) const noexcept {
        std::fill(render.begin(), render.end(), std::int16_t{0});
        if (!armed) return RouteError::InvalidState;
        if (!validPcm16Stereo) return RouteError::InvalidFormat;
        const auto samples = static_cast<std::size_t>(frames) * 2;
        if (render.size() < samples || (!silent && capture.size() != samples)) return RouteError::InvalidSize;
        if (frames != 0 && !silent && capture.data() == nullptr) return RouteError::InvalidPointer;
        if (!silent) for (std::size_t frame = 0; frame < frames; ++frame)
            render[frame * 2] = capture[frame * 2];
        return RouteError::None;
    }
};

class StereoStagingBuffer {
public:
    bool Configure(std::uint32_t capacityFrames) noexcept {
        Silence();
        samples_.clear();
        capacityFrames_ = 0;
        if (capacityFrames == 0 || capacityFrames > std::numeric_limits<std::size_t>::max() / 2) return false;
        try { samples_.assign(static_cast<std::size_t>(capacityFrames) * 2, 0); }
        catch (...) { samples_.clear(); return false; }
        capacityFrames_ = capacityFrames;
        return true;
    }
    RouteError Process(std::span<const std::int16_t> capture, std::uint32_t frames,
                       bool validPcm16Stereo, bool armed, bool silent = false) noexcept {
        if (frames > capacityFrames_) {
            std::fill(samples_.begin(), samples_.end(), std::int16_t{0});
            return RouteError::InvalidSize;
        }
        return router_.Process(capture, samples_, frames, validPcm16Stereo, armed, silent);
    }
    std::span<const std::int16_t> Samples(std::uint32_t frames) const noexcept {
        if (frames > capacityFrames_) return {};
        return {samples_.data(), static_cast<std::size_t>(frames) * 2};
    }
    std::uint32_t CapacityFrames() const noexcept { return capacityFrames_; }
    void Silence() noexcept { std::fill(samples_.begin(), samples_.end(), std::int16_t{0}); }
private:
    StereoRouter router_;
    std::vector<std::int16_t> samples_;
    std::uint32_t capacityFrames_ = 0;
};

constexpr double kMinimumMarkerCorrelation = 0.35;
constexpr double kBaselineSignificantPeakDbfs = -40.0;
constexpr std::uint32_t kExpectedRate = 48'000;
constexpr std::uint32_t kExpectedFrames = kExpectedRate * 5;
constexpr std::uint32_t kMinimumCaptureCoveragePercent = 99;

inline constexpr std::uint64_t MinimumCapturedFrames(std::uint32_t rate,
                                                       std::uint32_t durationSeconds) noexcept {
    const auto expected = static_cast<std::uint64_t>(rate) * durationSeconds;
    return (expected / 100) * kMinimumCaptureCoveragePercent +
        ((expected % 100) * kMinimumCaptureCoveragePercent + 99) / 100;
}

inline constexpr bool HasSufficientCaptureFrames(std::uint64_t capturedFrames, std::uint32_t rate,
                                                 std::uint32_t durationSeconds) noexcept {
    return rate != 0 && durationSeconds != 0 && capturedFrames >=
        MinimumCapturedFrames(rate, durationSeconds);
}

struct Result {
    bool configured = false;
    bool markerFound = false;
    bool baselineAvailable = false;
    bool input2Quiet = false;
    bool continuityClean = true;
    bool timestampsClean = true;
    bool noClipping = true;
    std::size_t matchedMarkers = 0;
    double markerConfidence = 0;
    double input2PeakDbfs = -std::numeric_limits<double>::infinity();
    std::uint64_t frames = 0;
};

class CaptureData {
public:
    ~CaptureData() { Erase(); }
    bool Configure(std::uint32_t capacityFrames = kExpectedFrames + kExpectedRate) noexcept {
        try {
            left_.assign(capacityFrames, 0);
            right_.assign(capacityFrames, 0);
        } catch (...) { Erase(); return false; }
        capacity_ = capacityFrames;
        ResetCounters();
        configured_ = capacityFrames != 0;
        return configured_;
    }
    bool Append(const std::int16_t* stereo, std::uint32_t frames, bool silent,
                bool discontinuity, bool timestampError, std::uint64_t devicePosition,
                std::uint64_t qpc100ns) noexcept {
        if (!configured_ || (frames != 0 && !silent && stereo == nullptr) || frames > capacity_ - stored_) {
            continuityClean_ = false;
            return false;
        }
        if (discontinuity) { continuityClean_ = false; ++discontinuities_; }
        if (timestampError || qpc100ns == 0 || (haveQpc_ && qpc100ns <= previousQpc_)) {
            timestampsClean_ = false;
            ++timestampErrors_;
        }
        if (qpc100ns != 0) { previousQpc_ = qpc100ns; haveQpc_ = true; }
        if (havePosition_ && devicePosition != expectedPosition_) {
            continuityClean_ = false;
            if (devicePosition > expectedPosition_) ++positionGaps_;
            else ++positionOverlaps_;
        }
        if (devicePosition > std::numeric_limits<std::uint64_t>::max() - frames) {
            continuityClean_ = false;
            ++invalidPositions_;
        }
        else { expectedPosition_ = devicePosition + frames; havePosition_ = true; }
        if (silent) ++silentPackets_;
        for (std::uint32_t i = 0; i < frames; ++i) {
            const auto left = silent ? std::int16_t{0} : stereo[static_cast<std::size_t>(i) * 2];
            const auto right = silent ? std::int16_t{0} : stereo[static_cast<std::size_t>(i) * 2 + 1];
            left_[stored_ + i] = left;
            right_[stored_ + i] = right;
            if (left == INT16_MIN || left == INT16_MAX || right == INT16_MIN || right == INT16_MAX) {
                noClipping_ = false;
                ++clippedSamples_;
            }
        }
        stored_ += frames;
        return true;
    }
    Result Analyze() const noexcept {
        Result result;
        result.configured = configured_;
        result.baselineAvailable = stored_ != 0;
        result.continuityClean = continuityClean_;
        result.timestampsClean = timestampsClean_;
        result.noClipping = noClipping_;
        result.frames = stored_;
        if (!configured_ || stored_ < AnalogLoopback::kMarkerFrames * AnalogLoopback::kMinimumMarkers) return result;
        try {
        AnalogLoopback::RenderSignal reference(1);
        if (!reference.Configure(kExpectedRate, 1, 0, 5)) return result;
        const auto marker = std::span<const std::int16_t>(reference.Marker());
        const auto correlation = AnalogLoopback::DiagnoseMarkers({left_.data(), stored_}, marker);
        const auto& candidates = correlation.independentCandidates;
        for (std::size_t first = 0; first < candidates.size(); ++first) {
            if (candidates[first].confidence < kMinimumMarkerCorrelation) continue;
            std::size_t matched = 0;
            double confidence = 0;
            const double origin = candidates[first].offsetFrames;
            for (std::size_t expected = 0; expected < 20; ++expected) {
                const double target = origin + expected * AnalogLoopback::kMarkerSpacingFrames;
                const auto found = std::find_if(candidates.begin(), candidates.end(), [&](const auto& candidate) {
                    return std::abs(candidate.offsetFrames - target) <= AnalogLoopback::kSequenceOffsetToleranceFrames &&
                        candidate.confidence >= kMinimumMarkerCorrelation &&
                        candidate.polarity == AnalogLoopback::RenderSignal::MarkerPolarity(expected);
                });
                if (found != candidates.end()) { ++matched; confidence += found->confidence; }
            }
            if (matched > result.matchedMarkers) {
                result.matchedMarkers = matched;
                result.markerConfidence = confidence / matched;
            }
        }
        result.markerFound = result.matchedMarkers >= AnalogLoopback::kMinimumMarkers;
        const auto max = *std::max_element(right_.begin(), right_.begin() + stored_, [](auto a, auto b) {
            const auto aa = a == INT16_MIN ? 32768 : std::abs(static_cast<int>(a));
            const auto bb = b == INT16_MIN ? 32768 : std::abs(static_cast<int>(b));
            return aa < bb;
        });
        const int peak = max == INT16_MIN ? 32768 : std::abs(static_cast<int>(max));
        result.input2PeakDbfs = peak == 0 ? -std::numeric_limits<double>::infinity()
            : 20.0 * std::log10(static_cast<double>(peak) / 32768.0);
        result.input2Quiet = result.input2PeakDbfs <= kBaselineSignificantPeakDbfs;
        } catch (...) {
            result.markerFound = false;
            result.markerConfidence = 0;
        }
        return result;
    }
    std::uint64_t TimestampErrors() const noexcept { return timestampErrors_; }
    std::uint64_t PositionGaps() const noexcept { return positionGaps_; }
    std::uint64_t PositionOverlaps() const noexcept { return positionOverlaps_; }
    std::uint64_t InvalidPositions() const noexcept { return invalidPositions_; }
    std::uint64_t Discontinuities() const noexcept { return discontinuities_; }
    std::uint64_t SilentPackets() const noexcept { return silentPackets_; }
    std::uint64_t ClippedSamples() const noexcept { return clippedSamples_; }
    void Erase() noexcept {
        volatile std::int16_t* l = left_.data();
        volatile std::int16_t* r = right_.data();
        for (std::size_t i = 0; i < left_.size(); ++i) l[i] = 0;
        for (std::size_t i = 0; i < right_.size(); ++i) r[i] = 0;
        left_.clear(); right_.clear(); stored_ = capacity_ = 0; configured_ = false;
        timestampErrors_ = positionGaps_ = positionOverlaps_ = invalidPositions_ = discontinuities_ =
            silentPackets_ = clippedSamples_ = 0;
    }
private:
    void ResetCounters() noexcept {
        stored_ = 0; havePosition_ = haveQpc_ = false; previousQpc_ = 0;
        continuityClean_ = timestampsClean_ = noClipping_ = true;
        timestampErrors_ = positionGaps_ = positionOverlaps_ = invalidPositions_ = discontinuities_ =
            silentPackets_ = clippedSamples_ = 0;
    }
    std::vector<std::int16_t> left_, right_;
    std::size_t capacity_ = 0, stored_ = 0;
    std::uint64_t expectedPosition_ = 0;
    bool configured_ = false, havePosition_ = false, haveQpc_ = false;
    std::uint64_t previousQpc_ = 0;
    std::uint64_t timestampErrors_ = 0, positionGaps_ = 0, positionOverlaps_ = 0;
    std::uint64_t invalidPositions_ = 0, discontinuities_ = 0, silentPackets_ = 0, clippedSamples_ = 0;
    bool continuityClean_ = true, timestampsClean_ = true, noClipping_ = true;
};

} // namespace MonitorRtlPreflight
