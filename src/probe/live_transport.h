#pragma once

#include <algorithm>
#include <atomic>
#include <bit>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <limits>
#include <memory>
#include <new>
#include <utility>

namespace LiveTransport {

constexpr double kOutputGain = 0.06309573444801933; // -24 dB
constexpr std::uint64_t kMaximumStorageBytes = 64ULL * 1024 * 1024;
constexpr std::uint64_t kNoSourceFrame = std::numeric_limits<std::uint64_t>::max();

struct PullResult {
    std::uint32_t consumedFrames = 0;
    std::uint32_t silenceFrames = 0;
    std::uint32_t overwrittenFrames = 0;
    std::uint32_t prefillSilenceFrames = 0;
    std::uint32_t underrunFrames = 0;
    bool prefillSilence = false;
    bool prefillCompleted = false;
    bool underrun = false;
};

struct Stats {
    std::uint64_t captureFramesReceived = 0;
    std::uint64_t framesTransferred = 0;
    std::uint64_t renderFramesConsumed = 0;
    std::uint64_t silenceFrames = 0;
    std::uint64_t prefillSilenceFrames = 0;
    std::uint64_t underrunEvents = 0;
    std::uint64_t underrunFrames = 0;
    std::uint64_t overflowEvents = 0;
    std::uint64_t overflowFrames = 0;
    std::uint64_t occupancyMinimum = 0;
    double occupancyAverage = 0;
    std::uint64_t occupancyMaximum = 0;
    std::uint64_t occupancyCurrent = 0;
    std::uint64_t prefillFrames = 0;
    std::uint64_t capacityFrames = 0;
    std::uint32_t captureBufferFrames = 0;
    std::uint32_t renderBufferFrames = 0;
    std::uint32_t rate = 0;
    bool prefillCompleted = false;
    std::uint64_t prefillDurationMicroseconds = 0;
};

class SpscPcm16StereoRing {
    struct Slot {
        std::atomic<std::uint64_t> value{0};
        std::atomic<std::uint64_t> provenance{0};
    };
    static_assert(std::atomic<std::uint64_t>::is_always_lock_free,
                  "The live PCM ring requires lock-free 64-bit atomics on Windows x64.");

public:
    bool Configure(std::uint32_t captureBufferFrames, std::uint32_t renderBufferFrames,
                   std::uint32_t rate) noexcept {
        const auto largestBuffer = std::max(captureBufferFrames, renderBufferFrames);
        if (largestBuffer == 0 || rate == 0) return false;
        const auto prefillFrames = static_cast<std::uint64_t>(largestBuffer) * 3;
        const auto capacityFrames = static_cast<std::uint64_t>(largestBuffer) * 8;
        if (capacityFrames > std::numeric_limits<std::size_t>::max() / sizeof(Slot) ||
            capacityFrames * sizeof(Slot) > kMaximumStorageBytes) return false;
        std::unique_ptr<Slot[]> slots(new (std::nothrow) Slot[static_cast<std::size_t>(capacityFrames)]);
        if (!slots) return false;
        for (std::size_t i = 0; i < capacityFrames; ++i) {
            slots[i].value.store(0, std::memory_order_relaxed);
        }
        slots_ = std::move(slots);
        capacityFrames_ = capacityFrames;
        prefillFrames_ = prefillFrames;
        rate_ = rate;
        captureBufferFrames_ = captureBufferFrames;
        renderBufferFrames_ = renderBufferFrames;
        readPosition_.store(0, std::memory_order_relaxed);
        writePosition_.store(0, std::memory_order_relaxed);
        captureFramesReceived_.store(0, std::memory_order_relaxed);
        framesTransferred_.store(0, std::memory_order_relaxed);
        renderFramesConsumed_.store(0, std::memory_order_relaxed);
        silenceFrames_.store(0, std::memory_order_relaxed);
        prefillSilenceFrames_.store(0, std::memory_order_relaxed);
        underrunEvents_.store(0, std::memory_order_relaxed);
        underrunFrames_.store(0, std::memory_order_relaxed);
        overflowEvents_.store(0, std::memory_order_relaxed);
        overflowFrames_.store(0, std::memory_order_relaxed);
        occupancyMinimum_.store(0, std::memory_order_relaxed);
        occupancyMaximum_.store(0, std::memory_order_relaxed);
        occupancySum_.store(0, std::memory_order_relaxed);
        occupancySamples_.store(0, std::memory_order_relaxed);
        prefillCompleted_.store(false, std::memory_order_relaxed);
        prefillDurationMicroseconds_.store(0, std::memory_order_relaxed);
        RecordOccupancy();
        return true;
    }

    bool Configured() const noexcept { return slots_ != nullptr; }

    // Caller must stop both SPSC participants before erasing slots and discarding indices.
    void EraseAfterStop() noexcept {
        if (!Configured()) return;
        for (std::uint64_t i = 0; i < capacityFrames_; ++i) {
            slots_[i].value.store(0, std::memory_order_relaxed);
            slots_[i].provenance.store(0, std::memory_order_relaxed);
        }
        readPosition_.store(0, std::memory_order_relaxed);
        writePosition_.store(0, std::memory_order_relaxed);
        captureFramesReceived_.store(0, std::memory_order_relaxed);
        framesTransferred_.store(0, std::memory_order_relaxed);
        renderFramesConsumed_.store(0, std::memory_order_relaxed);
        silenceFrames_.store(0, std::memory_order_relaxed);
        prefillSilenceFrames_.store(0, std::memory_order_relaxed);
        underrunEvents_.store(0, std::memory_order_relaxed);
        underrunFrames_.store(0, std::memory_order_relaxed);
        overflowEvents_.store(0, std::memory_order_relaxed);
        overflowFrames_.store(0, std::memory_order_relaxed);
        occupancyMinimum_.store(0, std::memory_order_relaxed);
        occupancyMaximum_.store(0, std::memory_order_relaxed);
        occupancySum_.store(0, std::memory_order_relaxed);
        occupancySamples_.store(0, std::memory_order_relaxed);
        prefillCompleted_.store(false, std::memory_order_relaxed);
        prefillDurationMicroseconds_.store(0, std::memory_order_relaxed);
        RecordOccupancy();
    }

    void RecordCaptureFramesReceived(std::uint32_t frames) noexcept {
        captureFramesReceived_.fetch_add(frames, std::memory_order_relaxed);
    }

    bool Push(const std::int16_t* interleavedStereo, std::uint32_t frames,
              bool silent, std::uint64_t firstSourceFrame = kNoSourceFrame) noexcept {
        if (!Configured() || (!silent && interleavedStereo == nullptr)) return false;
        std::uint64_t dropped = 0;
        auto writePosition = writePosition_.load(std::memory_order_relaxed);
        for (std::uint32_t frame = 0; frame < frames; ++frame) {
            for (;;) {
                auto readPosition = readPosition_.load(std::memory_order_acquire);
                if (writePosition - readPosition < capacityFrames_) break;
                const auto newReadPosition = writePosition - capacityFrames_ + 1;
                if (newReadPosition <= readPosition) continue;
                if (readPosition_.compare_exchange_strong(readPosition, newReadPosition,
                        std::memory_order_acq_rel, std::memory_order_acquire)) {
                    dropped += newReadPosition - readPosition;
                    break;
                }
            }
            std::uint32_t packed = 0;
            if (!silent) {
                const auto sampleIndex = static_cast<std::size_t>(frame) * 2;
                const auto left = ApplyGain(interleavedStereo[sampleIndex]);
                const auto right = ApplyGain(interleavedStereo[sampleIndex + 1]);
                packed = static_cast<std::uint16_t>(left) |
                    (static_cast<std::uint32_t>(static_cast<std::uint16_t>(right)) << 16);
            }
            auto& slot = slots_[writePosition % capacityFrames_];
            // Runs are capped at 30 s, so the packed 32-bit slot generation cannot wrap.
            const auto sequence = static_cast<std::uint32_t>(writePosition + 1);
            const auto sourceFrame = firstSourceFrame == kNoSourceFrame ||
                firstSourceFrame + frame >= std::numeric_limits<std::uint32_t>::max()
                ? std::numeric_limits<std::uint32_t>::max()
                : static_cast<std::uint32_t>(firstSourceFrame + frame);
            slot.provenance.store((static_cast<std::uint64_t>(sequence) << 32) | sourceFrame,
                                  std::memory_order_relaxed);
            slot.value.store((static_cast<std::uint64_t>(sequence) << 32) | packed,
                             std::memory_order_release);
            ++writePosition;
            writePosition_.store(writePosition, std::memory_order_release);
        }
        framesTransferred_.fetch_add(frames, std::memory_order_relaxed);
        if (dropped != 0) {
            overflowEvents_.fetch_add(1, std::memory_order_relaxed);
            overflowFrames_.fetch_add(dropped, std::memory_order_relaxed);
        }
        RecordOccupancy();
        return true;
    }

    PullResult Pull(std::int16_t* interleavedStereo, std::uint32_t frames,
                    bool forceSilence = false,
                    std::uint64_t* sourceFrames = nullptr) noexcept {
        PullResult result;
        if (interleavedStereo == nullptr || frames == 0) return result;
        if (forceSilence || !Configured()) {
            std::fill_n(interleavedStereo, static_cast<std::size_t>(frames) * 2, std::int16_t{0});
            if (sourceFrames != nullptr) std::fill_n(sourceFrames, frames, kNoSourceFrame);
            result.silenceFrames = frames;
            silenceFrames_.fetch_add(frames, std::memory_order_relaxed);
            RecordOccupancy();
            return result;
        }
        if (!prefillCompleted_.load(std::memory_order_acquire)) {
            if (Occupancy() < prefillFrames_) {
                std::fill_n(interleavedStereo, static_cast<std::size_t>(frames) * 2, std::int16_t{0});
                if (sourceFrames != nullptr) std::fill_n(sourceFrames, frames, kNoSourceFrame);
                result.silenceFrames = frames;
                result.prefillSilenceFrames = frames;
                result.prefillSilence = true;
                silenceFrames_.fetch_add(frames, std::memory_order_relaxed);
                prefillSilenceFrames_.fetch_add(frames, std::memory_order_relaxed);
                RecordOccupancy();
                return result;
            }
            prefillCompleted_.store(true, std::memory_order_release);
            result.prefillCompleted = true;
        }

        for (std::uint32_t frame = 0; frame < frames; ++frame) {
            bool read = false;
            for (unsigned retry = 0; retry < 4; ++retry) {
                auto readPosition = readPosition_.load(std::memory_order_acquire);
                const auto writePosition = writePosition_.load(std::memory_order_acquire);
                if (readPosition >= writePosition) break;
                if (!readPosition_.compare_exchange_strong(readPosition, readPosition + 1,
                        std::memory_order_acq_rel, std::memory_order_acquire)) continue;
                const auto& slot = slots_[readPosition % capacityFrames_];
                const auto value = slot.value.load(std::memory_order_acquire);
                const auto sequence = static_cast<std::uint32_t>(value >> 32);
                const auto expectedSequence = static_cast<std::uint32_t>(readPosition + 1);
                if (sequence != expectedSequence) {
                    if (sequence > expectedSequence) ++result.overwrittenFrames;
                    break;
                }
                const auto packed = static_cast<std::uint32_t>(value);
                if (sourceFrames != nullptr) {
                    const auto provenance = slot.provenance.load(std::memory_order_relaxed);
                    sourceFrames[frame] = static_cast<std::uint32_t>(provenance >> 32) == expectedSequence &&
                        static_cast<std::uint32_t>(provenance) != std::numeric_limits<std::uint32_t>::max()
                        ? static_cast<std::uint32_t>(provenance) : kNoSourceFrame;
                }
                const auto sampleIndex = static_cast<std::size_t>(frame) * 2;
                interleavedStereo[sampleIndex] = std::bit_cast<std::int16_t>(
                    static_cast<std::uint16_t>(packed));
                interleavedStereo[sampleIndex + 1] = std::bit_cast<std::int16_t>(
                    static_cast<std::uint16_t>(packed >> 16));
                read = true;
                break;
            }
            if (read) {
                ++result.consumedFrames;
            } else {
                const auto sampleIndex = static_cast<std::size_t>(frame) * 2;
                interleavedStereo[sampleIndex] = 0;
                interleavedStereo[sampleIndex + 1] = 0;
                if (sourceFrames != nullptr) sourceFrames[frame] = kNoSourceFrame;
                ++result.silenceFrames;
            }
        }
        renderFramesConsumed_.fetch_add(result.consumedFrames, std::memory_order_relaxed);
        silenceFrames_.fetch_add(result.silenceFrames, std::memory_order_relaxed);
        const auto underrunFrames = result.silenceFrames - result.overwrittenFrames;
        result.underrunFrames = static_cast<std::uint32_t>(underrunFrames);
        if (underrunFrames != 0) {
            result.underrun = true;
            underrunEvents_.fetch_add(1, std::memory_order_relaxed);
            underrunFrames_.fetch_add(underrunFrames, std::memory_order_relaxed);
        }
        if (result.overwrittenFrames != 0) {
            overflowEvents_.fetch_add(1, std::memory_order_relaxed);
            overflowFrames_.fetch_add(result.overwrittenFrames, std::memory_order_relaxed);
        }
        RecordOccupancy();
        return result;
    }

    void ForceSilenceAfterPull(PullResult& result, std::uint32_t frames,
                               bool preserveLossCounters = false) noexcept {
        if (result.consumedFrames != 0)
            renderFramesConsumed_.fetch_sub(result.consumedFrames, std::memory_order_relaxed);
        const auto addedSilence = frames - result.silenceFrames;
        silenceFrames_.fetch_add(addedSilence, std::memory_order_relaxed);
        if (result.underrun && !preserveLossCounters) {
            underrunEvents_.fetch_sub(1, std::memory_order_relaxed);
            underrunFrames_.fetch_sub(result.underrunFrames,
                                      std::memory_order_relaxed);
        }
        result.consumedFrames = 0;
        result.silenceFrames = frames;
        result.prefillSilence = false;
        result.underrunFrames = 0;
        result.underrun = false;
    }

    void RecordPrefillDuration(std::uint64_t microseconds) noexcept {
        std::uint64_t expected = 0;
        prefillDurationMicroseconds_.compare_exchange_strong(expected, microseconds,
            std::memory_order_relaxed);
    }

    std::uint64_t Occupancy() const noexcept {
        const auto writePosition = writePosition_.load(std::memory_order_acquire);
        const auto readPosition = readPosition_.load(std::memory_order_acquire);
        return writePosition >= readPosition ? std::min(writePosition - readPosition, capacityFrames_) : 0;
    }

    bool PrefillCompleted() const noexcept { return prefillCompleted_.load(std::memory_order_acquire); }
    bool HasTransportLoss() const noexcept {
        return underrunEvents_.load(std::memory_order_relaxed) != 0 ||
            overflowEvents_.load(std::memory_order_relaxed) != 0;
    }

    Stats GetStats() const noexcept {
        Stats stats;
        stats.captureFramesReceived = captureFramesReceived_.load(std::memory_order_relaxed);
        stats.framesTransferred = framesTransferred_.load(std::memory_order_relaxed);
        stats.renderFramesConsumed = renderFramesConsumed_.load(std::memory_order_relaxed);
        stats.silenceFrames = silenceFrames_.load(std::memory_order_relaxed);
        stats.prefillSilenceFrames = prefillSilenceFrames_.load(std::memory_order_relaxed);
        stats.underrunEvents = underrunEvents_.load(std::memory_order_relaxed);
        stats.underrunFrames = underrunFrames_.load(std::memory_order_relaxed);
        stats.overflowEvents = overflowEvents_.load(std::memory_order_relaxed);
        stats.overflowFrames = overflowFrames_.load(std::memory_order_relaxed);
        stats.occupancyMinimum = occupancyMinimum_.load(std::memory_order_relaxed);
        const auto samples = occupancySamples_.load(std::memory_order_relaxed);
        stats.occupancyAverage = samples == 0 ? 0.0
            : static_cast<double>(occupancySum_.load(std::memory_order_relaxed)) / samples;
        stats.occupancyMaximum = occupancyMaximum_.load(std::memory_order_relaxed);
        stats.occupancyCurrent = Occupancy();
        stats.prefillFrames = prefillFrames_;
        stats.capacityFrames = capacityFrames_;
        stats.captureBufferFrames = captureBufferFrames_;
        stats.renderBufferFrames = renderBufferFrames_;
        stats.rate = rate_;
        stats.prefillCompleted = prefillCompleted_.load(std::memory_order_relaxed);
        stats.prefillDurationMicroseconds = prefillDurationMicroseconds_.load(std::memory_order_relaxed);
        return stats;
    }

private:
    static std::int16_t ApplyGain(std::int16_t sample) noexcept {
        const auto scaled = static_cast<long>(std::lrint(static_cast<double>(sample) * kOutputGain));
        return static_cast<std::int16_t>(std::clamp<long>(scaled, -32768, 32767));
    }

    void RecordOccupancy() noexcept {
        const auto occupancy = Occupancy();
        auto maximum = occupancyMaximum_.load(std::memory_order_relaxed);
        while (maximum < occupancy && !occupancyMaximum_.compare_exchange_strong(
                   maximum, occupancy, std::memory_order_relaxed)) {}
        auto minimum = occupancyMinimum_.load(std::memory_order_relaxed);
        while (minimum > occupancy && !occupancyMinimum_.compare_exchange_strong(
                   minimum, occupancy, std::memory_order_relaxed)) {}
        occupancySum_.fetch_add(occupancy, std::memory_order_relaxed);
        occupancySamples_.fetch_add(1, std::memory_order_relaxed);
    }

    std::unique_ptr<Slot[]> slots_;
    std::uint64_t capacityFrames_ = 0;
    std::uint64_t prefillFrames_ = 0;
    std::uint32_t captureBufferFrames_ = 0;
    std::uint32_t renderBufferFrames_ = 0;
    std::uint32_t rate_ = 0;
    std::atomic<std::uint64_t> readPosition_{0};
    std::atomic<std::uint64_t> writePosition_{0};
    std::atomic<std::uint64_t> captureFramesReceived_{0};
    std::atomic<std::uint64_t> framesTransferred_{0};
    std::atomic<std::uint64_t> renderFramesConsumed_{0};
    std::atomic<std::uint64_t> silenceFrames_{0};
    std::atomic<std::uint64_t> prefillSilenceFrames_{0};
    std::atomic<std::uint64_t> underrunEvents_{0};
    std::atomic<std::uint64_t> underrunFrames_{0};
    std::atomic<std::uint64_t> overflowEvents_{0};
    std::atomic<std::uint64_t> overflowFrames_{0};
    std::atomic<std::uint64_t> occupancyMinimum_{0};
    std::atomic<std::uint64_t> occupancyMaximum_{0};
    std::atomic<std::uint64_t> occupancySum_{0};
    std::atomic<std::uint64_t> occupancySamples_{0};
    std::atomic<bool> prefillCompleted_{false};
    std::atomic<std::uint64_t> prefillDurationMicroseconds_{0};
};

enum class MonitorStatus { Failed, CompletedWithWarnings, Completed };

inline MonitorStatus ClassifyMonitor(bool captureCompleted, bool renderCompleted, bool cancelled,
                                     bool streamWarning, const Stats& stats) noexcept {
    if (!captureCompleted || !renderCompleted || cancelled || !stats.prefillCompleted ||
        stats.framesTransferred == 0 || stats.renderFramesConsumed == 0)
        return MonitorStatus::Failed;
    return streamWarning || stats.underrunEvents != 0 || stats.overflowEvents != 0
        ? MonitorStatus::CompletedWithWarnings : MonitorStatus::Completed;
}

inline const char* MonitorStatusText(MonitorStatus status) noexcept {
    switch (status) {
    case MonitorStatus::Failed: return "MONITOR_FAILED";
    case MonitorStatus::CompletedWithWarnings: return "MONITOR_COMPLETED_WITH_WARNINGS";
    case MonitorStatus::Completed: return "MONITOR_COMPLETED";
    }
    return "MONITOR_FAILED";
}

} // namespace LiveTransport
