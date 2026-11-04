#pragma once

#include "runtime_safety_gate.h"

#include <cmath>
#include <cstdint>
#include <memory>
#include <string>
#include <string_view>
#include <utility>

// OS-neutral facts collected by the existing bounded Capture-only preflight.
namespace CaptureRuntimeEvidence {

enum class Field { Unknown, NotAttempted, Succeeded, Failed, Incomplete };
enum class LifecycleState { Idle, Configured, Initialized, CaptureReady, Capturing,
                            Stopping, Completed, Failed, Cancelled };
enum class EvidenceStatus { Unknown, Incomplete, Failed, Clean };
enum class HardwareQaStatus { NotRun, OperatorReviewRequired };

inline constexpr std::string_view FieldText(Field value) noexcept {
    switch (value) {
    case Field::Unknown: return "UNKNOWN";
    case Field::NotAttempted: return "NOT_ATTEMPTED";
    case Field::Succeeded: return "SUCCEEDED";
    case Field::Failed: return "FAILED";
    case Field::Incomplete: return "INCOMPLETE";
    }
    return "UNKNOWN";
}

class Lifecycle final {
public:
    LifecycleState State() const noexcept { return state_; }
    bool Transition(LifecycleState next) noexcept {
        const bool allowed =
            (state_ == LifecycleState::Idle && next == LifecycleState::Configured) ||
            (state_ == LifecycleState::Configured && (next == LifecycleState::Initialized || next == LifecycleState::Failed || next == LifecycleState::Cancelled)) ||
            (state_ == LifecycleState::Initialized && (next == LifecycleState::CaptureReady || next == LifecycleState::Stopping || next == LifecycleState::Failed || next == LifecycleState::Cancelled)) ||
            (state_ == LifecycleState::CaptureReady && (next == LifecycleState::Capturing || next == LifecycleState::Stopping || next == LifecycleState::Failed || next == LifecycleState::Cancelled)) ||
            (state_ == LifecycleState::Capturing && (next == LifecycleState::Stopping || next == LifecycleState::Failed || next == LifecycleState::Cancelled)) ||
            (state_ == LifecycleState::Stopping && (next == LifecycleState::Completed || next == LifecycleState::Failed || next == LifecycleState::Cancelled));
        if (!allowed) return false;
        state_ = next;
        return true;
    }
private:
    LifecycleState state_ = LifecycleState::Idle;
};

struct HresultFact {
    Field state = Field::Unknown;
    std::int32_t value = 0;
    void Record(std::int32_t result) noexcept {
        value = result;
        state = result == 0 ? Field::Succeeded : Field::Failed;
    }
    void NotCalled() noexcept { state = Field::NotAttempted; value = 0; }
    bool Succeeded() const noexcept { return state == Field::Succeeded && value == 0; }
    bool WellFormed() const noexcept {
        switch (state) {
        case Field::Unknown:
        case Field::NotAttempted: return value == 0;
        case Field::Succeeded: return value == 0;
        case Field::Failed: return value != 0;
        case Field::Incomplete: return false;
        }
        return false;
    }
};

inline constexpr std::int32_t kBufferSizeNotAligned = static_cast<std::int32_t>(0x88890019U);

class Session final {
public:
    explicit Session(std::string endpointIdentity)
        : marker_(std::make_shared<const RuntimeSafetyGate::SessionMarker>()),
          endpointIdentity_(std::move(endpointIdentity)) {}
    Session(const Session&) = delete;
    Session& operator=(const Session&) = delete;
    std::weak_ptr<const RuntimeSafetyGate::SessionMarker> Reference() const noexcept { return marker_; }
    const std::string& EndpointIdentity() const noexcept { return endpointIdentity_; }
private:
    std::shared_ptr<const RuntimeSafetyGate::SessionMarker> marker_;
    std::string endpointIdentity_;
};

struct Facts {
    // Test collectors default to MockAdapter; production integration must opt in explicitly.
    RuntimeSafetyGate::EvidenceSource source = RuntimeSafetyGate::EvidenceSource::MockAdapter;
    std::weak_ptr<const RuntimeSafetyGate::SessionMarker> session;
    std::string endpointIdentity;
    bool endpointExplicitlySelected = false;
    std::uint32_t sampleRate = 0;
    std::uint32_t requestedDurationSeconds = 0;
    std::uint16_t channels = 0;
    std::uint16_t containerBits = 0;
    std::uint16_t validBits = 0;
    std::uint64_t finalPeriod100ns = 0;
    std::uint32_t requestedBufferFrames = 0;
    std::uint32_t actualBufferFrames = 0;
    bool alignmentRetryAttempted = false;
    std::uint32_t alignmentBufferFrames = 0;
    HresultFact getDevice{};
    HresultFact activateClient{};
    HresultFact devicePeriod{};
    HresultFact formatSupport{};
    HresultFact firstInitialize{};
    HresultFact alignmentGetBufferSize{};
    HresultFact retryActivateClient{};
    HresultFact finalInitialize{};
    HresultFact actualGetBufferSize{};
    HresultFact eventCreation{};
    HresultFact setEventHandle{};
    HresultFact eventClose{};
    HresultFact captureService{};
    HresultFact start{};
    HresultFact stop{};
    std::uint64_t packetCount = 0;
    std::uint64_t frameCount = 0;
    std::uint64_t firstDevicePosition = 0;
    std::uint64_t firstQpc100ns = 0;
    std::uint64_t lastQpc100ns = 0;
    bool firstDevicePositionValid = false;
    bool qpcPositionValid = false;
    std::uint64_t emptyBufferResults = 0;
    std::uint64_t unavailableBufferResults = 0;
    std::uint64_t silentPackets = 0;
    std::uint64_t clippedSamples = 0;
    std::uint64_t discontinuities = 0;
    std::uint64_t timestampErrors = 0;
    std::uint64_t positionGaps = 0;
    std::uint64_t positionOverlaps = 0;
    std::uint64_t invalidPositions = 0;
    bool continuityValidationAvailable = false;
    bool continuityClean = false;
    bool timestampValidationAvailable = false;
    bool timestampsClean = false;
    bool clippingValidationAvailable = false;
    bool noClipping = false;
    bool durationReached = false;
    bool cancelled = false;
    bool watchdogTimedOut = false;
    bool deviceInvalidated = false;
    bool criticalApiFailure = false;
    std::int32_t firstFailureHresult = 0;
    std::string_view firstFailureStage = "none";
    bool captureServiceReleased = false;
    bool clientAcquired = false;
    bool clientReleased = false;
    bool eventAcquired = false;
    bool eventReleased = false;
    bool endpointAcquired = false;
    bool endpointReleased = false;
};

inline HardwareQaStatus HardwareQaReviewStatus(const Facts& facts) noexcept {
    return facts.source == RuntimeSafetyGate::EvidenceSource::RuntimeObserved && facts.start.Succeeded()
        ? HardwareQaStatus::OperatorReviewRequired : HardwareQaStatus::NotRun;
}

inline const char* HardwareQaStatusText(HardwareQaStatus status) noexcept {
    switch (status) {
    case HardwareQaStatus::NotRun: return "NOT_RUN";
    case HardwareQaStatus::OperatorReviewRequired: return "OPERATOR_REVIEW_REQUIRED";
    }
    return "NOT_RUN";
}

inline Field ValidateInitializeProvenance(const Facts& facts) noexcept {
    const auto consistentSuccess = [](const HresultFact& fact) {
        return fact.state == Field::Succeeded && fact.value == 0;
    };
    const auto explicitlyFailed = [](const HresultFact& fact) {
        return fact.state == Field::Failed && fact.value != 0;
    };
    const auto malformed = [](const HresultFact& fact) {
        return (fact.state == Field::Succeeded && fact.value != 0) ||
            (fact.state == Field::Failed && fact.value == 0);
    };

    if (malformed(facts.firstInitialize) || malformed(facts.finalInitialize) ||
        malformed(facts.alignmentGetBufferSize) || malformed(facts.retryActivateClient))
        return Field::Failed;
    if (!facts.alignmentRetryAttempted) {
        if (explicitlyFailed(facts.firstInitialize) || explicitlyFailed(facts.finalInitialize))
            return Field::Failed;
        if (!consistentSuccess(facts.firstInitialize) || !consistentSuccess(facts.finalInitialize))
            return Field::Incomplete;
        if (facts.firstInitialize.value != facts.finalInitialize.value || facts.alignmentBufferFrames != 0 ||
            facts.alignmentGetBufferSize.state == Field::Succeeded ||
            facts.alignmentGetBufferSize.state == Field::Failed ||
            facts.retryActivateClient.state == Field::Succeeded || facts.retryActivateClient.state == Field::Failed)
            return Field::Failed;
        if (facts.alignmentGetBufferSize.state != Field::NotAttempted ||
            facts.retryActivateClient.state != Field::NotAttempted)
            return Field::Incomplete;
        return Field::Succeeded;
    }
    if (facts.firstInitialize.state == Field::Succeeded ||
        (facts.firstInitialize.state == Field::Failed &&
         facts.firstInitialize.value != kBufferSizeNotAligned) ||
        facts.finalInitialize.state == Field::Failed || facts.alignmentGetBufferSize.state == Field::Failed ||
        facts.retryActivateClient.state == Field::Failed)
        return Field::Failed;
    if (facts.firstInitialize.state == Field::Unknown || facts.firstInitialize.state == Field::NotAttempted ||
        facts.finalInitialize.state == Field::Unknown || facts.finalInitialize.state == Field::NotAttempted ||
        facts.alignmentGetBufferSize.state == Field::Unknown ||
        facts.alignmentGetBufferSize.state == Field::NotAttempted ||
        facts.retryActivateClient.state == Field::Unknown ||
        facts.retryActivateClient.state == Field::NotAttempted)
        return Field::Incomplete;
    if (facts.firstInitialize.state != Field::Failed ||
        facts.firstInitialize.value != kBufferSizeNotAligned ||
        !consistentSuccess(facts.alignmentGetBufferSize) || facts.alignmentBufferFrames == 0 ||
        facts.requestedBufferFrames != facts.alignmentBufferFrames ||
        !consistentSuccess(facts.retryActivateClient) || !consistentSuccess(facts.finalInitialize))
        return Field::Failed;
    return Field::Succeeded;
}

inline Field ValidateAcquisitionProvenance(const Facts& facts) noexcept {
    const auto check = [](const HresultFact& result, bool acquired) {
        if (result.state == Field::Failed) return Field::Failed;
        if (result.state != Field::Succeeded) return Field::Incomplete;
        return acquired ? Field::Succeeded : Field::Failed;
    };
    const Field endpoint = check(facts.getDevice, facts.endpointAcquired);
    const Field client = check(facts.activateClient, facts.clientAcquired);
    const Field event = check(facts.eventCreation, facts.eventAcquired);
    if (endpoint == Field::Failed || client == Field::Failed || event == Field::Failed ||
        facts.devicePeriod.state == Field::Failed || facts.formatSupport.state == Field::Failed ||
        facts.actualGetBufferSize.state == Field::Failed || facts.setEventHandle.state == Field::Failed ||
        facts.captureService.state == Field::Failed ||
        (facts.setEventHandle.Succeeded() && !facts.eventAcquired))
        return Field::Failed;
    if (endpoint != Field::Succeeded || client != Field::Succeeded || event != Field::Succeeded ||
        !facts.devicePeriod.Succeeded() || !facts.formatSupport.Succeeded() ||
        !facts.actualGetBufferSize.Succeeded() || !facts.setEventHandle.Succeeded() ||
        !facts.captureService.Succeeded())
        return Field::Incomplete;
    if (facts.formatSupport.value != 0 || facts.setEventHandle.value != 0)
        return Field::Failed;
    return Field::Succeeded;
}

inline void MarkUnattempted(Facts& facts) noexcept {
    facts.getDevice.NotCalled(); facts.activateClient.NotCalled(); facts.devicePeriod.NotCalled();
    facts.formatSupport.NotCalled(); facts.firstInitialize.NotCalled();
    facts.alignmentGetBufferSize.NotCalled(); facts.retryActivateClient.NotCalled();
    facts.finalInitialize.NotCalled(); facts.actualGetBufferSize.NotCalled();
    facts.eventCreation.NotCalled(); facts.setEventHandle.NotCalled(); facts.eventClose.NotCalled();
    facts.captureService.NotCalled(); facts.start.NotCalled(); facts.stop.NotCalled();
}

struct Report {
    EvidenceStatus evidence = EvidenceStatus::Unknown;
    Field initialize = Field::Unknown;
    Field start = Field::Unknown;
    Field stream = Field::Unknown;
    Field integrity = Field::Unknown;
    Field cleanup = Field::Unknown;
    bool exactFormat = false;
    bool actualGeometryValid = false;
    bool outputDisabled = true;
    std::uint64_t minimumFrames = 0;
};

inline constexpr std::uint64_t kExpectedFrames = 240'000;
inline constexpr std::uint64_t kMinimumFrames = 237'600;

inline Report Evaluate(const Session& session, const Facts& facts) noexcept {
    Report report;
    const auto boundSession = facts.session.lock();
    const auto currentSession = session.Reference().lock();
    if (!boundSession || boundSession != currentSession || facts.endpointIdentity != session.EndpointIdentity()) {
        report.evidence = EvidenceStatus::Failed;
        report.integrity = Field::Failed;
        return report;
    }
    const bool hresultsWellFormed = facts.getDevice.WellFormed() && facts.activateClient.WellFormed() &&
        facts.devicePeriod.WellFormed() && facts.formatSupport.WellFormed() &&
        facts.firstInitialize.WellFormed() && facts.alignmentGetBufferSize.WellFormed() &&
        facts.retryActivateClient.WellFormed() && facts.finalInitialize.WellFormed() &&
        facts.actualGetBufferSize.WellFormed() && facts.eventCreation.WellFormed() &&
        facts.setEventHandle.WellFormed() && facts.eventClose.WellFormed() &&
        facts.captureService.WellFormed() && facts.start.WellFormed() && facts.stop.WellFormed();
    if (!hresultsWellFormed) {
        report.evidence = EvidenceStatus::Failed;
        return report;
    }
    report.minimumFrames = kMinimumFrames;
    report.exactFormat = facts.endpointExplicitlySelected &&
        facts.sampleRate == 48'000 && facts.channels == 2 && facts.containerBits == 16 && facts.validBits == 16;
    const bool periodProductValid = facts.sampleRate != 0 && facts.finalPeriod100ns <=
        (UINT64_MAX - 5'000'000) / facts.sampleRate;
    const auto framesForPeriod = periodProductValid
        ? (facts.finalPeriod100ns * facts.sampleRate + 5'000'000) / 10'000'000 : 0;
    report.actualGeometryValid = facts.actualGetBufferSize.Succeeded() && facts.actualBufferFrames != 0 &&
        facts.requestedBufferFrames != 0 && framesForPeriod != 0 &&
        facts.actualBufferFrames == framesForPeriod && facts.requestedBufferFrames == framesForPeriod;
    report.initialize = ValidateInitializeProvenance(facts);
    report.start = facts.start.state;

    const Field acquisition = ValidateAcquisitionProvenance(facts);
    const bool initialized = report.exactFormat && acquisition == Field::Succeeded &&
        report.initialize == Field::Succeeded && report.actualGeometryValid;
    const bool started = initialized && facts.start.Succeeded();
    const bool complete = facts.requestedDurationSeconds == 5 && started && facts.stop.Succeeded() &&
        facts.durationReached && !facts.cancelled &&
        !facts.watchdogTimedOut && !facts.deviceInvalidated && !facts.criticalApiFailure &&
        facts.packetCount != 0 && facts.frameCount >= kMinimumFrames &&
        facts.firstDevicePositionValid && facts.qpcPositionValid && facts.firstQpc100ns != 0 &&
        facts.lastQpc100ns >= facts.firstQpc100ns;
    const bool continuityClean = complete && facts.continuityValidationAvailable && facts.continuityClean &&
        facts.clippingValidationAvailable && facts.noClipping && facts.discontinuities == 0 &&
        facts.positionGaps == 0 && facts.positionOverlaps == 0 && facts.invalidPositions == 0 &&
        facts.unavailableBufferResults == 0;
    const bool timestampsClean = complete && facts.timestampValidationAvailable && facts.timestampsClean &&
        facts.timestampErrors == 0;
    report.stream = facts.cancelled || facts.watchdogTimedOut || facts.criticalApiFailure ||
        facts.deviceInvalidated || facts.start.state == Field::Failed || facts.stop.state == Field::Failed
        ? Field::Failed : complete ? Field::Succeeded : started ? Field::Incomplete : Field::Unknown;
    const bool knownIntegrityFailure = facts.discontinuities != 0 || facts.timestampErrors != 0 ||
        facts.positionGaps != 0 || facts.positionOverlaps != 0 || facts.invalidPositions != 0 ||
        facts.unavailableBufferResults != 0 || (facts.continuityValidationAvailable && !facts.continuityClean) ||
        (facts.timestampValidationAvailable && !facts.timestampsClean) ||
        (facts.clippingValidationAvailable && !facts.noClipping);
    report.integrity = knownIntegrityFailure ? Field::Failed
        : continuityClean && timestampsClean ? Field::Succeeded
        : facts.packetCount == 0 ? Field::Unknown : Field::Incomplete;
    const bool releaseFailed = (facts.clientAcquired && !facts.clientReleased) ||
        (facts.eventAcquired && (!facts.eventReleased || facts.eventClose.state == Field::Failed)) ||
        (facts.endpointAcquired && !facts.endpointReleased) ||
        (facts.captureService.Succeeded() && !facts.captureServiceReleased);
    const bool releaseComplete = (!facts.clientAcquired || facts.clientReleased) &&
        (!facts.eventAcquired || (facts.eventReleased && facts.eventClose.Succeeded())) &&
        (!facts.endpointAcquired || facts.endpointReleased) &&
        (!facts.captureService.Succeeded() || facts.captureServiceReleased);
    report.cleanup = releaseFailed ? Field::Failed : releaseComplete ? Field::Succeeded : Field::Incomplete;
    const bool originAndQpcValid = facts.firstDevicePositionValid && facts.qpcPositionValid &&
        facts.firstQpc100ns != 0 && facts.lastQpc100ns >= facts.firstQpc100ns;
    const bool originContradictory = facts.qpcPositionValid &&
        (facts.firstQpc100ns == 0 || facts.lastQpc100ns < facts.firstQpc100ns);
    const bool geometryContradictory = facts.actualGetBufferSize.Succeeded() &&
        facts.sampleRate != 0 && facts.finalPeriod100ns != 0 && facts.requestedBufferFrames != 0 &&
        (facts.actualBufferFrames == 0 || !report.actualGeometryValid);
    const bool knownWrongFormat = facts.sampleRate != 0 && facts.channels != 0 &&
        facts.containerBits != 0 && facts.validBits != 0 && !report.exactFormat;
    if (complete && continuityClean && timestampsClean && originAndQpcValid &&
        acquisition == Field::Succeeded && report.initialize == Field::Succeeded && releaseComplete)
        report.evidence = EvidenceStatus::Clean;
    else if (knownWrongFormat || originContradictory || geometryContradictory || acquisition == Field::Failed ||
             report.initialize == Field::Failed || facts.criticalApiFailure || facts.cancelled || facts.watchdogTimedOut || facts.deviceInvalidated ||
             facts.getDevice.state == Field::Failed || facts.activateClient.state == Field::Failed ||
             facts.devicePeriod.state == Field::Failed || facts.formatSupport.state == Field::Failed ||
             facts.alignmentGetBufferSize.state == Field::Failed || facts.retryActivateClient.state == Field::Failed ||
             facts.finalInitialize.state == Field::Failed || facts.actualGetBufferSize.state == Field::Failed ||
             facts.eventCreation.state == Field::Failed || facts.setEventHandle.state == Field::Failed ||
             facts.captureService.state == Field::Failed || facts.start.state == Field::Failed || facts.stop.state == Field::Failed ||
             report.cleanup == Field::Failed ||
             (facts.continuityValidationAvailable && !facts.continuityClean) ||
             (facts.timestampValidationAvailable && !facts.timestampsClean) ||
             (facts.clippingValidationAvailable && !facts.noClipping) ||
             facts.discontinuities != 0 || facts.timestampErrors != 0 || facts.positionGaps != 0 ||
             facts.positionOverlaps != 0 || facts.invalidPositions != 0 || facts.unavailableBufferResults != 0)
        report.evidence = EvidenceStatus::Failed;
    else
        report.evidence = EvidenceStatus::Incomplete;
    return report;
}

inline const char* EvidenceText(EvidenceStatus value) noexcept {
    switch (value) {
    case EvidenceStatus::Unknown: return "UNKNOWN";
    case EvidenceStatus::Incomplete: return "INCOMPLETE";
    case EvidenceStatus::Failed: return "FAILED";
    case EvidenceStatus::Clean: return "CLEAN";
    }
    return "UNKNOWN";
}

inline const char* LifecycleText(LifecycleState value) noexcept {
    switch (value) {
    case LifecycleState::Idle: return "IDLE";
    case LifecycleState::Configured: return "CONFIGURED";
    case LifecycleState::Initialized: return "INITIALIZED";
    case LifecycleState::CaptureReady: return "CAPTURE_READY";
    case LifecycleState::Capturing: return "CAPTURING";
    case LifecycleState::Stopping: return "STOPPING";
    case LifecycleState::Completed: return "COMPLETED";
    case LifecycleState::Failed: return "FAILED";
    case LifecycleState::Cancelled: return "CANCELLED";
    }
    return "FAILED";
}

inline RuntimeSafetyGate::CaptureObservation ToGateObservation(
        const Session& session, const Facts& facts, std::uint64_t observationSequence) {
    RuntimeSafetyGate::CaptureObservation observation{};
    observation.stream.session = facts.session;
    observation.stream.source = facts.source;
    observation.stream.format = {RuntimeSafetyGate::SampleEncoding::Other, 0, 0, 0};
    const auto boundSession = facts.session.lock();
    const auto currentSession = session.Reference().lock();
    if (!boundSession || boundSession != currentSession || facts.endpointIdentity != session.EndpointIdentity())
        return observation;
    observation.stream.endpointIdentity = facts.endpointIdentity;
    observation.stream.format = {RuntimeSafetyGate::SampleEncoding::Pcm16, facts.sampleRate,
                                 facts.channels, facts.containerBits};
    observation.stream.period100ns = facts.finalPeriod100ns;
    observation.stream.actualBufferFrames = facts.actualBufferFrames;
    observation.stream.observationSequence = observationSequence;
    observation.stream.endpointExplicitlySelected = facts.endpointExplicitlySelected;
    observation.stream.initializeSucceeded = facts.finalInitialize.Succeeded();
    observation.stream.eventHandleSet = facts.eventCreation.Succeeded() && facts.setEventHandle.Succeeded();
    const auto report = Evaluate(session, facts);
    observation.stream.bufferGeometryValid = report.actualGeometryValid;
    observation.stream.workerReady = report.start == Field::Succeeded && facts.packetCount != 0;
    observation.stream.deviceValid = !facts.deviceInvalidated;
    observation.continuityClean = report.integrity == Field::Succeeded && facts.continuityValidationAvailable &&
        facts.continuityClean && facts.clippingValidationAvailable && facts.noClipping && facts.discontinuities == 0 &&
        facts.positionGaps == 0 && facts.positionOverlaps == 0 && facts.invalidPositions == 0;
    observation.timestampsClean = report.integrity == Field::Succeeded && facts.timestampValidationAvailable &&
        facts.timestampsClean && facts.timestampErrors == 0;
    return observation;
}

} // namespace CaptureRuntimeEvidence
