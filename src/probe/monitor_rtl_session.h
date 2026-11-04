#pragma once

#include "live_transport.h"
#include "monitor_rtl_analyzer.h"
#include "monitor_rtl_preflight.h"

#include <algorithm>
#include <atomic>
#include <cstdint>
#include <limits>
#include <memory>
#include <span>
#include <vector>

namespace MonitorRtlSession {

constexpr std::uint32_t kRate = MonitorRtlAnalyzer::kRate;
constexpr std::uint32_t kNominalFrames = MonitorRtlAnalyzer::kMaxFrames;
constexpr std::uint32_t kMinimumFrames = (kNominalFrames * 99 + 99) / 100;

enum class Status { Idle, Configured, Measuring, Completed, Failed, Cancelled };
enum class RoutingStatus { NotChecked, Isolated, Failed };
enum class CaptureStatus { NotChecked, Clean, Failed };
enum class RingStatus { NotRun, Clean, Failed };
enum class AnalyzerStatus { NotRun, Validated, Rejected };
enum class SimulationStatus { SimulatedOnly };
enum class Failure { None, InvalidState, InvalidConfiguration, Allocation, MissingSimulationInput,
    StaleSimulationInput,
    CapturePreflightRejected, RevalidationRejected, InvalidFormat, InvalidSize, InvalidPointer,
    InvalidFramePosition, InvalidTimestamp, Discontinuity, Clipping, StagingRejected,
    RingUnavailable, RingOverflow, RenderUnderrun, RenderRightNotZero, RenderBounds,
    InsufficientCoverage, AnalyzerRejected, Cancelled };

inline constexpr const char* StatusText(Status value) noexcept {
    switch (value) {
    case Status::Idle: return "IDLE";
    case Status::Configured: return "CONFIGURED";
    case Status::Measuring: return "MEASURING";
    case Status::Completed: return "COMPLETED";
    case Status::Failed: return "FAILED";
    case Status::Cancelled: return "CANCELLED";
    }
    return "FAILED";
}
inline constexpr const char* RoutingText(RoutingStatus value) noexcept {
    switch (value) {
    case RoutingStatus::NotChecked: return "NOT_CHECKED";
    case RoutingStatus::Isolated: return "ISOLATED";
    case RoutingStatus::Failed: return "FAILED";
    }
    return "FAILED";
}
inline constexpr const char* CaptureText(CaptureStatus value) noexcept {
    switch (value) {
    case CaptureStatus::NotChecked: return "NOT_CHECKED";
    case CaptureStatus::Clean: return "CLEAN";
    case CaptureStatus::Failed: return "FAILED";
    }
    return "FAILED";
}
inline constexpr const char* RingText(RingStatus value) noexcept {
    switch (value) {
    case RingStatus::NotRun: return "NOT_RUN";
    case RingStatus::Clean: return "CLEAN";
    case RingStatus::Failed: return "FAILED";
    }
    return "FAILED";
}
inline constexpr const char* AnalyzerText(AnalyzerStatus value) noexcept {
    switch (value) {
    case AnalyzerStatus::NotRun: return "NOT_RUN";
    case AnalyzerStatus::Validated: return "SYNTHETIC_VALIDATED";
    case AnalyzerStatus::Rejected: return "REJECTED";
    }
    return "REJECTED";
}
inline constexpr const char* SimulationText(SimulationStatus) noexcept { return "SIMULATED_ONLY"; }

struct Configuration {
    std::uint32_t rate = kRate;
    std::uint16_t channels = 2;
    MonitorRtlAnalyzer::SampleFormat format = MonitorRtlAnalyzer::SampleFormat::Pcm16;
    std::uint32_t nominalFrames = kNominalFrames;
    // Synthetic buffer geometry only; this is not an endpoint period claim.
    std::uint32_t captureBufferFrames = 144;
    std::uint32_t renderBufferFrames = 144;
};

// These are synthetic harness inputs, never operator/hardware authorization.
struct SimulatedReadiness {
    std::uint64_t simulationSessionId = 0;
    bool explicitlyInjectedByHarness = false;
    bool simulatedCapturePreflightPassed = false;
    bool simulatedOperatorApproval = false;
    bool simulatedPhysicalFixtureConfirmed = false;
    bool simulatedEndpointsRechecked = false;
    bool simulatedFreshEmptyRing = false;
    bool simulatedCaptureReady = false;
    bool simulatedRenderSilent = false;
};

struct CapturePacket {
    std::span<const std::int16_t> interleavedPcm;
    std::uint64_t firstFrame = 0;
    std::uint64_t qpc100ns = 0;
    std::uint32_t frames = 0;
    bool validPcm16Stereo = true;
    bool silent = false;
    bool dataDiscontinuity = false;
    bool timestampError = false;
};

struct Report {
    Status sessionStatus = Status::Idle;
    RoutingStatus routingIsolation = RoutingStatus::NotChecked;
    CaptureStatus captureIntegrity = CaptureStatus::NotChecked;
    RingStatus ringTransport = RingStatus::NotRun;
    AnalyzerStatus analyzerStatus = AnalyzerStatus::NotRun;
    SimulationStatus simulationStatus = SimulationStatus::SimulatedOnly;
    MonitorRtlAnalyzer::TimingQuality timingQuality = MonitorRtlAnalyzer::TimingQuality::InsufficientData;
    MonitorRtlAnalyzer::PhysicalRtlStatus physicalRtl = MonitorRtlAnalyzer::PhysicalRtlStatus::NotMeasured;
    LiveTransport::Stats ring{};
    MonitorRtlAnalyzer::Result analysis{};
    Failure failure = Failure::None;
    std::uint64_t capturedFrames = 0;
    std::uint64_t fakeRenderFrames = 0;
    std::uint64_t fakeRenderNonSilentFrames = 0;
    bool ringStartedEmpty = false;
    bool input2ExcludedByStaging = false;
    bool renderRightAlwaysZero = false;
    bool outputSilencedOnFailure = false;
    bool transientPcmErased = false;
};

class Coordinator {
public:
    // Synchronous synthetic harness contract: serialize calls; this object owns no workers.
    Coordinator() noexcept : simulationSessionId_(NextSimulationSessionId().fetch_add(1, std::memory_order_relaxed)) {}
    Coordinator(const Coordinator&) = delete;
    Coordinator& operator=(const Coordinator&) = delete;
    ~Coordinator() { EraseAndRelease(); }

    bool Configure(const Configuration& configuration) noexcept {
        if (attempted_) return Reject(Failure::InvalidState);
        attempted_ = true;
        if (configuration.rate != kRate || configuration.channels != 2 ||
            configuration.format != MonitorRtlAnalyzer::SampleFormat::Pcm16 ||
            configuration.nominalFrames != kNominalFrames ||
            configuration.captureBufferFrames == 0 || configuration.renderBufferFrames == 0 ||
            configuration.captureBufferFrames > kNominalFrames ||
            configuration.renderBufferFrames > kNominalFrames) return Reject(Failure::InvalidConfiguration);

        try {
            configuration_ = configuration;
            const auto largest = std::max(configuration.captureBufferFrames, configuration.renderBufferFrames);
            prefillFrames_ = static_cast<std::uint64_t>(largest) * 3;
            if (prefillFrames_ > (std::numeric_limits<std::size_t>::max() - kNominalFrames) / 2)
                return Reject(Failure::InvalidConfiguration);
            capturePcm_.assign(static_cast<std::size_t>(kNominalFrames) * 2, 0);
            packets_.reserve(kNominalFrames); // Worst case is one frame per packet.
            pullScratch_.assign(static_cast<std::size_t>(configuration.renderBufferFrames) * 2, 0);
            fakeRenderPcm_.assign(static_cast<std::size_t>(kNominalFrames + 2 * prefillFrames_) * 2, 0);
            if (!staging_.Configure(configuration.captureBufferFrames)) return Reject(Failure::Allocation);
            ring_ = std::make_unique<LiveTransport::SpscPcm16StereoRing>();
            if (!ring_->Configure(configuration.captureBufferFrames, configuration.renderBufferFrames, kRate))
                return Reject(Failure::Allocation);
            const auto stats = ring_->GetStats();
            if (stats.occupancyCurrent != 0 || stats.framesTransferred != 0) return Reject(Failure::InvalidState);
            report_.ringStartedEmpty = true;
            report_.sessionStatus = Status::Configured;
            return true;
        } catch (...) {
            return Reject(Failure::Allocation);
        }
    }

    bool Prepare(const SimulatedReadiness& readiness) noexcept {
        if (report_.sessionStatus != Status::Configured || !ring_) return Reject(Failure::InvalidState);
        if (!readiness.explicitlyInjectedByHarness) return Reject(Failure::MissingSimulationInput);
        if (readiness.simulationSessionId != simulationSessionId_)
            return Reject(Failure::StaleSimulationInput);
        if (!lifecycle_.BeginCapturePreflight() || !lifecycle_.BeginValidation() ||
            !lifecycle_.Arm(readiness.simulatedCapturePreflightPassed))
            return Reject(Failure::CapturePreflightRejected);

        MonitorRtlAnalyzer::Revalidation revalidation{
            readiness.simulatedOperatorApproval,
            readiness.simulatedPhysicalFixtureConfirmed,
            readiness.simulatedEndpointsRechecked,
            readiness.simulatedFreshEmptyRing && report_.ringStartedEmpty &&
                ring_->GetStats().occupancyCurrent == 0,
            readiness.simulatedCaptureReady,
            readiness.simulatedRenderSilent};
        if (!lifecycle_.RevalidateForThisRun(revalidation)) return Reject(Failure::RevalidationRejected);
        if (!lifecycle_.BeginMeasurement()) return Reject(Failure::InvalidState);
        acceptingPackets_ = true;
        report_.sessionStatus = Status::Measuring;
        report_.captureIntegrity = CaptureStatus::Clean;
        report_.ringTransport = RingStatus::Clean;
        return true;
    }

    bool IngestCapturePacket(const CapturePacket& packet) noexcept {
        if (!acceptingPackets_ || report_.sessionStatus != Status::Measuring || !ring_)
            return Reject(Failure::InvalidState);
        if (packet.frames == 0 || packet.frames > kNominalFrames - capturedFrames_)
            return Reject(Failure::InvalidSize);
        if (packet.firstFrame != capturedFrames_) return Reject(Failure::InvalidFramePosition);
        if (packet.dataDiscontinuity) return Reject(Failure::Discontinuity);
        if (packet.timestampError || packet.qpc100ns == 0 ||
            (haveQpc_ && packet.qpc100ns <= previousQpc_)) return Reject(Failure::InvalidTimestamp);
        const auto sampleCount = static_cast<std::size_t>(packet.frames) * 2;
        if (packets_.size() == packets_.capacity()) return Reject(Failure::InvalidSize);
        const auto packetSamples = packet.silent ? std::span<const std::int16_t>{} : packet.interleavedPcm;
        const auto routeResult = staging_.Process(packetSamples, packet.frames,
                                                  packet.validPcm16Stereo, true, packet.silent);
        if (routeResult != MonitorRtlPreflight::RouteError::None) {
            const auto failure = routeResult == MonitorRtlPreflight::RouteError::InvalidFormat
                ? Failure::InvalidFormat : routeResult == MonitorRtlPreflight::RouteError::InvalidPointer
                ? Failure::InvalidPointer : routeResult == MonitorRtlPreflight::RouteError::InvalidSize
                ? Failure::InvalidSize : Failure::StagingRejected;
            return Reject(failure);
        }
        if (!packet.silent) {
            for (const auto sample : packet.interleavedPcm)
                if (sample == INT16_MIN || sample == INT16_MAX) return Reject(Failure::Clipping);
        }

        const auto storageOffset = static_cast<std::size_t>(capturedFrames_) * 2;
        if (packet.silent) {
            std::fill_n(capturePcm_.data() + storageOffset, sampleCount, std::int16_t{0});
        } else {
            std::copy(packet.interleavedPcm.begin(), packet.interleavedPcm.end(),
                      capturePcm_.begin() + static_cast<std::ptrdiff_t>(storageOffset));
        }
        const auto staged = staging_.Samples(packet.frames);
        if (staged.size() != sampleCount) return Reject(Failure::StagingRejected);
        for (std::size_t frame = 0; frame < packet.frames; ++frame) {
            const auto inputLeft = packet.silent ? 0 : packet.interleavedPcm[frame * 2];
            if (staged[frame * 2] != inputLeft || staged[frame * 2 + 1] != 0)
                return Reject(Failure::RenderRightNotZero);
        }
        report_.routingIsolation = RoutingStatus::Isolated;
        report_.input2ExcludedByStaging = true;
        packets_.push_back({packet.firstFrame, packet.frames, packet.qpc100ns,
                            packet.dataDiscontinuity, packet.timestampError});
        if (!ring_->Push(staged.data(), packet.frames, false, packet.firstFrame))
            return Reject(Failure::RingUnavailable);
        ring_->RecordCaptureFramesReceived(packet.frames);
        capturedFrames_ += packet.frames;
        previousQpc_ = packet.qpc100ns;
        haveQpc_ = true;
        report_.capturedFrames = capturedFrames_;
        report_.ring = ring_->GetStats();
        if (report_.ring.overflowEvents != 0 || report_.ring.overflowFrames != 0)
            return Reject(Failure::RingOverflow);
        return true;
    }

    bool SimulateRenderPull(std::uint32_t frames) noexcept {
        if (!acceptingPackets_ || report_.sessionStatus != Status::Measuring || !ring_)
            return Reject(Failure::InvalidState);
        return PullFake(frames, false);
    }

    bool Finish() noexcept {
        if (!acceptingPackets_ || report_.sessionStatus != Status::Measuring || !ring_)
            return Reject(Failure::InvalidState);
        acceptingPackets_ = false; // The packet producer is stopped before analysis or reclamation.
        if (capturedFrames_ < kMinimumFrames) return Reject(Failure::InsufficientCoverage);

        auto stats = ring_->GetStats();
        if (!stats.prefillCompleted && stats.occupancyCurrent < stats.prefillFrames)
            return Reject(Failure::RenderUnderrun);
        while ((stats = ring_->GetStats()).occupancyCurrent != 0) {
            const auto count = static_cast<std::uint32_t>(std::min<std::uint64_t>(
                stats.occupancyCurrent, configuration_.renderBufferFrames));
            if (!PullFake(count, true)) return false;
        }
        stats = ring_->GetStats();
        report_.ring = stats;
        if (stats.overflowEvents || stats.overflowFrames || stats.underrunEvents || stats.underrunFrames ||
            stats.renderFramesConsumed != capturedFrames_)
            return Reject(stats.overflowEvents || stats.overflowFrames ? Failure::RingOverflow : Failure::RenderUnderrun);

        const MonitorRtlAnalyzer::Input analyzerInput{
            std::span<const std::int16_t>(capturePcm_.data(), static_cast<std::size_t>(capturedFrames_) * 2),
            packets_, kRate, 2, MonitorRtlAnalyzer::SampleFormat::Pcm16, kNominalFrames};
        report_.analysis = MonitorRtlAnalyzer::Analyze(analyzerInput); // Post-run only; allocation is permitted here.
        report_.timingQuality = report_.analysis.timing;
        report_.physicalRtl = MonitorRtlAnalyzer::PhysicalRtlStatus::NotMeasured;
        report_.analyzerStatus = report_.analysis.validation == MonitorRtlAnalyzer::ValidationStatus::SyntheticValidated
            ? AnalyzerStatus::Validated : AnalyzerStatus::Rejected;
        const bool success = report_.analyzerStatus == AnalyzerStatus::Validated &&
            report_.routingIsolation == RoutingStatus::Isolated && report_.renderRightAlwaysZero &&
            report_.ring.renderFramesConsumed == capturedFrames_ && report_.ring.occupancyCurrent == 0;
        if (!success) {
            report_.failure = Failure::AnalyzerRejected;
            SilencePendingOutput();
            report_.outputSilencedOnFailure = true;
        }
        if (!lifecycle_.Stop(success)) {
            report_.sessionStatus = Status::Failed;
            report_.failure = Failure::InvalidState;
            SilencePendingOutput();
            report_.outputSilencedOnFailure = true;
            success_ = false;
        } else {
            report_.sessionStatus = success ? Status::Completed : Status::Failed;
            success_ = success;
        }
        report_.ring = stats;
        EraseAndRelease();
        return success_;
    }

    bool Cancel() noexcept {
        if (report_.sessionStatus == Status::Completed || report_.sessionStatus == Status::Failed ||
            report_.sessionStatus == Status::Cancelled) return false;
        acceptingPackets_ = false;
        lifecycle_.Cancel();
        report_.sessionStatus = Status::Cancelled;
        report_.failure = Failure::Cancelled;
        if (capturedFrames_ != 0) report_.captureIntegrity = CaptureStatus::Failed;
        if (report_.ring.framesTransferred != 0) report_.ringTransport = RingStatus::Failed;
        SilencePendingOutput();
        EraseAndRelease();
        report_.outputSilencedOnFailure = true;
        return true;
    }

    Status GetStatus() const noexcept { return report_.sessionStatus; }
    const Report& GetReport() const noexcept { return report_; }
    MonitorRtlAnalyzer::LifecycleState GetLifecycleState() const noexcept { return lifecycle_.State(); }
    std::uint64_t SimulationSessionId() const noexcept { return simulationSessionId_; }
    std::uint64_t RingOccupancy() const noexcept { return ring_ ? ring_->GetStats().occupancyCurrent : 0; }
    std::span<const std::int16_t> FakeRenderSamples() const noexcept {
        if (fakeRenderPcm_.empty()) return {};
        return {fakeRenderPcm_.data(), static_cast<std::size_t>(fakeRenderFramesWritten_) * 2};
    }

private:
    static std::atomic<std::uint64_t>& NextSimulationSessionId() noexcept {
        static std::atomic<std::uint64_t> next{1};
        return next;
    }

    bool PullFake(std::uint32_t frames, bool finalDrain) noexcept {
        if (!ring_ || frames == 0 || frames > configuration_.renderBufferFrames ||
            fakeRenderFramesWritten_ + frames > fakeRenderPcm_.size() / 2 ||
            (!finalDrain && requestedRenderFrames_ + frames > kNominalFrames + prefillFrames_))
            return Reject(Failure::RenderBounds);
        const auto pulled = ring_->Pull(pullScratch_.data(), frames);
        for (std::uint32_t frame = 0; frame < frames; ++frame) {
            if (pullScratch_[static_cast<std::size_t>(frame) * 2 + 1] != 0)
                return Reject(Failure::RenderRightNotZero);
        }
        report_.renderRightAlwaysZero = true;
        if (pulled.underrun || pulled.overwrittenFrames != 0)
            return Reject(Failure::RenderUnderrun);
        const auto outputOffset = static_cast<std::size_t>(fakeRenderFramesWritten_) * 2;
        std::copy_n(pullScratch_.data(), static_cast<std::size_t>(frames) * 2,
                    fakeRenderPcm_.begin() + static_cast<std::ptrdiff_t>(outputOffset));
        for (std::uint32_t frame = 0; frame < frames; ++frame) {
            if (pullScratch_[static_cast<std::size_t>(frame) * 2] != 0)
                ++report_.fakeRenderNonSilentFrames;
        }
        fakeRenderFramesWritten_ += frames;
        report_.fakeRenderFrames = fakeRenderFramesWritten_;
        if (!finalDrain) requestedRenderFrames_ += frames;
        report_.ring = ring_->GetStats();
        return true;
    }

    bool Reject(Failure failure) noexcept {
        if (report_.sessionStatus == Status::Completed || report_.sessionStatus == Status::Failed ||
            report_.sessionStatus == Status::Cancelled) return false;
        acceptingPackets_ = false;
        report_.sessionStatus = Status::Failed;
        report_.failure = failure;
        if (failure == Failure::StagingRejected || failure == Failure::InvalidFormat ||
            failure == Failure::InvalidSize || failure == Failure::InvalidPointer ||
            failure == Failure::RenderRightNotZero)
            report_.routingIsolation = RoutingStatus::Failed;
        if (failure == Failure::InvalidFormat || failure == Failure::InvalidSize ||
            failure == Failure::InvalidPointer || failure == Failure::InvalidFramePosition ||
            failure == Failure::InvalidTimestamp || failure == Failure::Discontinuity ||
            failure == Failure::Clipping || failure == Failure::InsufficientCoverage)
            report_.captureIntegrity = CaptureStatus::Failed;
        if (failure == Failure::RingUnavailable || failure == Failure::RingOverflow ||
            failure == Failure::RenderUnderrun || failure == Failure::RenderBounds)
            report_.ringTransport = RingStatus::Failed;
        if (failure == Failure::RenderRightNotZero) report_.renderRightAlwaysZero = false;
        lifecycle_.FailSession();
        SilencePendingOutput();
        if (ring_) report_.ring = ring_->GetStats();
        EraseAndRelease();
        report_.outputSilencedOnFailure = true;
        return false;
    }

    void SilencePendingOutput() noexcept {
        std::fill(fakeRenderPcm_.begin(), fakeRenderPcm_.end(), std::int16_t{0});
        std::fill(pullScratch_.begin(), pullScratch_.end(), std::int16_t{0});
        staging_.Silence();
    }

    static void Erase(std::vector<std::int16_t>& samples) noexcept {
        volatile std::int16_t* data = samples.data();
        for (std::size_t i = 0; i < samples.size(); ++i) data[i] = 0;
        std::vector<std::int16_t>().swap(samples);
    }

    void EraseAndRelease() noexcept {
        // Preserve diagnostics before EraseAfterStop resets the Ring's internal counters.
        if (ring_) report_.ring = ring_->GetStats();
        Erase(capturePcm_);
        Erase(pullScratch_);
        Erase(fakeRenderPcm_);
        packets_.clear();
        std::vector<MonitorRtlAnalyzer::Packet>().swap(packets_);
        staging_.Silence();
        staging_ = MonitorRtlPreflight::StereoStagingBuffer{}; // Reclaim staging storage after processing stops.
        if (ring_) ring_->EraseAfterStop();
        ring_.reset();
        report_.transientPcmErased = true;
    }

    Configuration configuration_{};
    MonitorRtlAnalyzer::Lifecycle lifecycle_{};
    MonitorRtlPreflight::StereoStagingBuffer staging_{};
    std::unique_ptr<LiveTransport::SpscPcm16StereoRing> ring_;
    std::vector<std::int16_t> capturePcm_;
    std::vector<std::int16_t> pullScratch_;
    std::vector<std::int16_t> fakeRenderPcm_;
    std::vector<MonitorRtlAnalyzer::Packet> packets_;
    Report report_{};
    std::uint64_t capturedFrames_ = 0;
    std::uint64_t fakeRenderFramesWritten_ = 0;
    std::uint64_t requestedRenderFrames_ = 0;
    std::uint64_t previousQpc_ = 0;
    std::uint64_t prefillFrames_ = 0;
    std::uint64_t simulationSessionId_ = 0;
    bool attempted_ = false;
    bool acceptingPackets_ = false;
    bool haveQpc_ = false;
    bool success_ = false;
};

} // namespace MonitorRtlSession
