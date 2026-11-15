#pragma once

#include "scan_plan_v2_v4.h"

#include <set>
#include <string>
#include <string_view>

namespace ScanPlanV2V4 {

enum class Step { CaptureInitialize, RenderInitialize, Duplex };
enum class Disposition { Clean, Failed, Unsupported, Unknown };

class TransportSequence {
public:
    explicit TransportSequence(std::vector<Candidate> candidates, unsigned maxOperations,
                               unsigned maxActiveSeconds)
        : candidates_(std::move(candidates)), maxOperations_(maxOperations),
          maxActiveSeconds_(maxActiveSeconds) {
        if (candidates_.empty() || candidates_.size() > 8 || maxOperations_ == 0 || maxActiveSeconds_ == 0)
            throw std::invalid_argument("invalid V4 transport sequence scope");
        AdvancePastRejected();
    }

    unsigned Begin(unsigned candidateIndex, Step step, std::string operationId, unsigned durationSeconds = 0) {
        if (stopped_ || inFlight_ || cursor_ >= candidates_.size() ||
            candidateIndex != candidates_[cursor_].index || operationId.empty() ||
            !operationIds_.insert(std::move(operationId)).second || usedOperations_ >= maxOperations_)
            throw std::invalid_argument("V4 operation is stale, concurrent, replayed, or outside budget");
        const auto expected = phase_ == Phase::Capture ? Step::CaptureInitialize :
            phase_ == Phase::Render ? Step::RenderInitialize : phase_ == Phase::Duplex ? Step::Duplex : Step::CaptureInitialize;
        if (step != expected || phase_ == Phase::Done || phase_ == Phase::Stopped)
            throw std::invalid_argument("V4 operation violates Capture Initialize, Render Initialize, Duplex order");
        if (step == Step::Duplex && (durationSeconds == 0 || usedActiveSeconds_ + durationSeconds > maxActiveSeconds_))
            throw std::invalid_argument("V4 active Duplex budget exceeded");
        inFlight_ = true;
        inFlightStep_ = step;
        inFlightDuration_ = durationSeconds;
        ++usedOperations_;
        return candidates_[cursor_].index;
    }

    void Complete(Disposition disposition, bool cleanupConfirmed, bool exactGeometry = true) {
        if (!inFlight_) throw std::invalid_argument("V4 completion has no matching operation");
        inFlight_ = false;
        if (disposition == Disposition::Unknown || !cleanupConfirmed) {
            stopped_ = true;
            phase_ = Phase::Stopped;
            return;
        }
        if (!exactGeometry && inFlightStep_ != Step::Duplex) disposition = Disposition::Failed;
        if (inFlightStep_ == Step::Duplex) usedActiveSeconds_ += inFlightDuration_;
        if (disposition == Disposition::Failed || disposition == Disposition::Unsupported) {
            AdvanceCandidate();
            return;
        }
        if (inFlightStep_ == Step::CaptureInitialize) phase_ = Phase::Render;
        else if (inFlightStep_ == Step::RenderInitialize) phase_ = Phase::Duplex;
        else { stopped_ = true; phase_ = Phase::RepeatabilityPending; }
    }

    bool Stopped() const noexcept { return stopped_; }
    bool RepeatabilityPending() const noexcept { return phase_ == Phase::RepeatabilityPending; }
    bool Complete() const noexcept { return phase_ == Phase::Done; }
    unsigned UsedOperations() const noexcept { return usedOperations_; }
    unsigned UsedActiveSeconds() const noexcept { return usedActiveSeconds_; }
    unsigned CandidateIndex() const noexcept { return cursor_ < candidates_.size() ? candidates_[cursor_].index : static_cast<unsigned>(candidates_.size()); }
    Step ExpectedStep() const noexcept {
        return phase_ == Phase::Render ? Step::RenderInitialize : phase_ == Phase::Duplex ? Step::Duplex : Step::CaptureInitialize;
    }

private:
    enum class Phase { Capture, Render, Duplex, Done, Stopped, RepeatabilityPending };
    void AdvancePastRejected() {
        while (cursor_ < candidates_.size() && !candidates_[cursor_].rejectionReason.empty()) ++cursor_;
        phase_ = cursor_ < candidates_.size() ? Phase::Capture : Phase::Done;
    }
    void AdvanceCandidate() {
        ++cursor_;
        AdvancePastRejected();
    }

    std::vector<Candidate> candidates_;
    std::set<std::string> operationIds_;
    unsigned maxOperations_{}, maxActiveSeconds_{}, usedOperations_{}, usedActiveSeconds_{}, cursor_{};
    Phase phase_{Phase::Capture};
    Step inFlightStep_{Step::CaptureInitialize};
    unsigned inFlightDuration_{};
    bool inFlight_{}, stopped_{};
};

} // namespace ScanPlanV2V4
