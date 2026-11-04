#pragma once

#include <cstdint>

namespace RenderChannelTest {

class EventProtocol {
public:
    bool BeginEvent() noexcept {
        if (finished_ || eventOpen_ || acquired_) return false;
        eventOpen_ = true;
        requestMade_ = false;
        releaseMade_ = false;
        return true;
    }

    bool RequestBuffer() noexcept {
        if (finished_ || !eventOpen_ || requestMade_ || acquired_) return false;
        requestMade_ = true;
        ++requests_;
        return true;
    }

    bool BufferResult(bool acquired) noexcept {
        if (!eventOpen_ || !requestMade_ || acquired_) return false;
        if (acquired) {
            acquired_ = true;
            ++acquisitions_;
        } else {
            ++acquisitionFailures_;
        }
        return true;
    }

    bool ReleaseResult(bool succeeded) noexcept {
        if (!eventOpen_ || !acquired_ || releaseMade_) return false;
        acquired_ = false;
        releaseMade_ = true;
        releaseSucceeded_ = succeeded;
        ++releases_;
        if (!succeeded) ++releaseFailures_;
        return true;
    }

    bool MarkFinalSilenceSubmitted() noexcept {
        if (!eventOpen_ || !releaseMade_ || !releaseSucceeded_) return false;
        finalSilenceSubmitted_ = true;
        return true;
    }

    bool EndEvent() noexcept {
        if (!eventOpen_ || acquired_) return false;
        eventOpen_ = false;
        return true;
    }

    void Finish() noexcept {
        finished_ = true;
        eventOpen_ = false;
    }

    bool CleanPairs() const noexcept {
        return !acquired_ && acquisitions_ == releases_ &&
            requests_ == acquisitions_ + acquisitionFailures_;
    }
    bool FinalSilenceSubmitted() const noexcept { return finalSilenceSubmitted_; }
    std::uint64_t Requests() const noexcept { return requests_; }
    std::uint64_t Acquisitions() const noexcept { return acquisitions_; }
    std::uint64_t Releases() const noexcept { return releases_; }
    std::uint64_t AcquisitionFailures() const noexcept { return acquisitionFailures_; }
    std::uint64_t ReleaseFailures() const noexcept { return releaseFailures_; }

private:
    bool eventOpen_ = false;
    bool requestMade_ = false;
    bool acquired_ = false;
    bool releaseMade_ = false;
    bool releaseSucceeded_ = false;
    bool finished_ = false;
    bool finalSilenceSubmitted_ = false;
    std::uint64_t requests_ = 0;
    std::uint64_t acquisitions_ = 0;
    std::uint64_t releases_ = 0;
    std::uint64_t acquisitionFailures_ = 0;
    std::uint64_t releaseFailures_ = 0;
};

inline bool ShouldSilenceForRemainingTime(double remainingMilliseconds,
                                          double expectedBufferMilliseconds) noexcept {
    return remainingMilliseconds >= 0.0 && expectedBufferMilliseconds > 0.0 &&
        remainingMilliseconds <= expectedBufferMilliseconds;
}

} // namespace RenderChannelTest
