#pragma once

#include <algorithm>
#include <array>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <numbers>

namespace RenderSignal {

inline constexpr std::uint32_t kSampleRate = 48'000;
inline constexpr std::uint32_t kMaximumChannels = 32;
inline constexpr std::size_t kMaximumFramesPerBuffer = 48'000;
inline constexpr std::uint32_t kPeakCeiling = 518; // approximately -36.02 dBFS
inline constexpr double kFrequencyHz = 997.0;

struct ChannelStats {
    std::uint64_t nonzeroFrames = 0;
    std::int16_t peakMagnitude = 0;
};

class Generator {
public:
    bool Configure(std::uint32_t rate, std::uint32_t channels, std::uint32_t selectedChannel) noexcept {
        if (rate != kSampleRate || channels == 0 || channels > kMaximumChannels || selectedChannel >= channels) {
            rate_ = channels_ = selectedChannel_ = 0;
            Stop();
            return false;
        }
        rate_ = rate;
        channels_ = channels;
        selectedChannel_ = selectedChannel;
        Reset();
        return true;
    }

    bool Fill(std::int16_t* interleaved, std::size_t frames) noexcept {
        if (interleaved == nullptr || channels_ == 0 || frames > kMaximumFramesPerBuffer) return false;
        std::fill_n(interleaved, frames * channels_, std::int16_t{0});
        if (!running_) return true;
        for (std::size_t frame = 0; frame < frames; ++frame) {
            const auto withinBurst = frameIndex_ % kBurstPeriodFrames;
            double envelope = 0.0;
            if (withinBurst < kBurstOnFrames) {
                const auto attack = std::min<std::uint64_t>(withinBurst + 1, kAttackFrames);
                const auto releaseRemaining = kBurstOnFrames - withinBurst;
                const auto release = std::min<std::uint64_t>(releaseRemaining, kReleaseFrames);
                envelope = static_cast<double>(std::min(attack, release)) /
                    static_cast<double>(kAttackFrames);
                const auto sample = static_cast<std::int16_t>(std::lround(
                    kPeakCeiling * envelope * std::sin(phase_)));
                interleaved[frame * channels_ + selectedChannel_] = sample;
                if (sample != 0) ++stats_[selectedChannel_].nonzeroFrames;
                stats_[selectedChannel_].peakMagnitude = std::max<std::int16_t>(
                    stats_[selectedChannel_].peakMagnitude, static_cast<std::int16_t>(std::abs(sample)));
            }
            phase_ += phaseStep_;
            if (phase_ >= 2.0 * std::numbers::pi) phase_ -= 2.0 * std::numbers::pi;
            ++frameIndex_;
            ++generatedFrames_;
        }
        return true;
    }

    bool Silence(std::int16_t* interleaved, std::size_t frames) const noexcept {
        if (interleaved == nullptr || channels_ == 0 || frames > kMaximumFramesPerBuffer) return false;
        std::fill_n(interleaved, frames * channels_, std::int16_t{0});
        return true;
    }

    void Reset() noexcept {
        phase_ = 0.0;
        frameIndex_ = 0;
        generatedFrames_ = 0;
        stats_.fill({});
        running_ = true;
        phaseStep_ = rate_ == 0 ? 0.0 : 2.0 * std::numbers::pi * kFrequencyHz / rate_;
    }

    void Stop() noexcept { running_ = false; }
    std::uint32_t Channels() const noexcept { return channels_; }
    std::uint32_t SelectedChannel() const noexcept { return selectedChannel_; }
    std::uint64_t GeneratedFrames() const noexcept { return generatedFrames_; }
    const std::array<ChannelStats, kMaximumChannels>& Stats() const noexcept { return stats_; }

private:
    static constexpr std::uint64_t kBurstPeriodFrames = kSampleRate / 4;
    static constexpr std::uint64_t kBurstOnFrames = kSampleRate / 10;
    static constexpr std::uint64_t kAttackFrames = kSampleRate / 200;
    static constexpr std::uint64_t kReleaseFrames = kSampleRate / 200;
    std::uint32_t rate_ = 0;
    std::uint32_t channels_ = 0;
    std::uint32_t selectedChannel_ = 0;
    double phase_ = 0.0;
    double phaseStep_ = 0.0;
    std::uint64_t frameIndex_ = 0;
    std::uint64_t generatedFrames_ = 0;
    std::array<ChannelStats, kMaximumChannels> stats_{};
    bool running_ = false;
};

} // namespace RenderSignal
