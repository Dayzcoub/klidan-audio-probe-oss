#pragma once

#include <cstdint>
#include <memory>
#include <span>
#include <string>

// This component is deliberately OS-neutral. Its in-process identity and observations are
// control inputs only; it owns no device, COM object, native handle, or physical Render client.
namespace RuntimeSafetyGate {

enum class State { Idle, Configured, CaptureReady, Revalidating, OutputArmed, Running,
                   Stopping, Completed, Failed, Cancelled };
enum class EvidenceSource { RuntimeObserved, MockAdapter, Simulated };
enum class SampleEncoding { Pcm16, Other };
enum class Readiness { NotChecked, MockedReady, ObservedReady, Failed };
enum class RoutingStatus { NotChecked, Isolated, Failed };
enum class OutputState { Disabled, Enabled, Inhibited };
enum class PeerShutdown { NotStarted, StopRequested, Quiescent, Failed };
enum class ResourceCleanup { NotStarted, WaitingForQuiescence, ReadyToReclaim, Cleaned, Failed };
enum class SimulationStatus { Mocked };
enum class HardwareQaStatus { NotRun };
enum class PhysicalRtlStatus { NotMeasured };
enum class Worker { Capture, Render };
enum class Fault { CaptureFailure, RenderFailure, EndpointInvalidation, FormatMismatch,
                   RingOverflow, RingUnderrun, InvalidRouting, RenderRightNonZero,
                   TimestampContinuity, WatchdogTimeout, PeerShutdown, InvalidTransition };
enum class Failure { None, InvalidConfiguration, StaleSession, ReplayedReadiness,
                     SimulatedEvidenceRejected, MissingEndpoint, EndpointChanged,
                     CaptureFormatInvalid, RenderFormatInvalid, RuntimeFormatMismatch, InitializeFailed, EventSetupFailed,
                     BufferGeometryMismatch, WorkerNotReady, DeviceInvalidated,
                     CaptureContinuityFault, TimestampFault, PeriodChanged,
                     CaptureNotReady, RenderNotReady, TransportNotFresh, InvalidRouting,
                     GenericStereoRouting, OperatorApprovalMissing, PhysicalSetupUnconfirmed,
                     LevelMatchingFixtureUnapproved, StartBeforeAuthorized, RepeatedStart,
                     InvalidStartAcknowledgement,
                     CaptureFailed, RenderFailed, RingOverflow, RingUnderrun,
                     RenderRightNonZero, WatchdogTimeout, PeerShutdownFailed,
                     InvalidTransition, PeerTimeout, CleanupFailed, LateWorkerEvent };

inline constexpr const char* StateText(State value) noexcept {
    switch (value) {
    case State::Idle: return "IDLE";
    case State::Configured: return "CONFIGURED";
    case State::CaptureReady: return "CAPTURE_READY";
    case State::Revalidating: return "REVALIDATING";
    case State::OutputArmed: return "OUTPUT_ARMED";
    case State::Running: return "RUNNING";
    case State::Stopping: return "STOPPING";
    case State::Completed: return "COMPLETED";
    case State::Failed: return "FAILED";
    case State::Cancelled: return "CANCELLED";
    }
    return "FAILED";
}
inline constexpr const char* ReadinessText(Readiness value) noexcept {
    switch (value) {
    case Readiness::NotChecked: return "NOT_CHECKED";
    case Readiness::MockedReady: return "MOCKED_READY";
    case Readiness::ObservedReady: return "OBSERVED_READY";
    case Readiness::Failed: return "FAILED";
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
inline constexpr const char* OutputText(OutputState value) noexcept {
    switch (value) {
    case OutputState::Disabled: return "OUTPUT_DISABLED";
    case OutputState::Enabled: return "OUTPUT_ENABLED";
    case OutputState::Inhibited: return "OUTPUT_INHIBITED";
    }
    return "OUTPUT_INHIBITED";
}
inline constexpr const char* PeerShutdownText(PeerShutdown value) noexcept {
    switch (value) {
    case PeerShutdown::NotStarted: return "NOT_STARTED";
    case PeerShutdown::StopRequested: return "STOP_REQUESTED";
    case PeerShutdown::Quiescent: return "QUIESCENT";
    case PeerShutdown::Failed: return "FAILED";
    }
    return "FAILED";
}
inline constexpr const char* ResourceCleanupText(ResourceCleanup value) noexcept {
    switch (value) {
    case ResourceCleanup::NotStarted: return "NOT_STARTED";
    case ResourceCleanup::WaitingForQuiescence: return "WAITING_FOR_QUIESCENCE";
    case ResourceCleanup::ReadyToReclaim: return "READY_TO_RECLAIM";
    case ResourceCleanup::Cleaned: return "CLEANED";
    case ResourceCleanup::Failed: return "FAILED";
    }
    return "FAILED";
}
inline constexpr const char* SimulationStatusText(SimulationStatus) noexcept { return "MOCKED"; }
inline constexpr const char* HardwareQaStatusText(HardwareQaStatus) noexcept { return "NOT_RUN"; }
inline constexpr const char* PhysicalRtlStatusText(PhysicalRtlStatus) noexcept { return "NOT_MEASURED"; }

struct AudioFormat {
    SampleEncoding encoding = SampleEncoding::Pcm16;
    std::uint32_t sampleRate = 48'000;
    std::uint16_t channels = 2;
    std::uint16_t bitsPerSample = 16;
    friend constexpr bool operator==(const AudioFormat&, const AudioFormat&) = default;
};

struct StreamPlan {
    std::string endpointIdentity;
    AudioFormat format{};
    std::uint64_t period100ns = 0; // Must be explicitly supplied from this session's plan.
    std::uint32_t bufferFrames = 0;
};

struct SessionConfiguration {
    StreamPlan capture;
    StreamPlan render;
};

struct SessionMarker final {};

class SessionIdentity final {
public:
    SessionIdentity(const SessionIdentity&) = delete;
    SessionIdentity& operator=(const SessionIdentity&) = delete;
    SessionIdentity(SessionIdentity&&) = delete;
    SessionIdentity& operator=(SessionIdentity&&) = delete;
    std::weak_ptr<const SessionMarker> Reference() const noexcept { return marker_; }
private:
    friend class Gate;
    SessionIdentity() : marker_(std::make_shared<const SessionMarker>()) {}
    std::shared_ptr<const SessionMarker> marker_;
};

// A pointer to the gate-owned, non-copyable identity is intentionally process-local and
// non-serializable. Observations from another gate/session cannot be replayed here.
struct StreamObservation {
    std::weak_ptr<const SessionMarker> session;
    EvidenceSource source = EvidenceSource::Simulated;
    std::string endpointIdentity;
    AudioFormat format{};
    std::uint64_t period100ns = 0;
    std::uint32_t actualBufferFrames = 0;
    std::uint64_t observationSequence = 0;
    bool endpointExplicitlySelected = false;
    bool initializeSucceeded = false;
    bool eventHandleSet = false;
    bool bufferGeometryValid = false;
    bool workerReady = false;
    bool deviceValid = false;
};

struct CaptureObservation {
    StreamObservation stream;
    bool continuityClean = false;
    bool timestampsClean = false;
};

struct RenderObservation {
    StreamObservation stream;
    bool initiallySilent = false;
};

struct TransportObservation {
    std::weak_ptr<const SessionMarker> session;
    EvidenceSource source = EvidenceSource::Simulated;
    bool newlyConfiguredRing = false;
    bool ringEmpty = false;
    bool input1OnlyRouting = false;
    bool renderRightGuaranteedZero = false;
    bool noStalePcm = false;
    bool noTransportFaults = false;
    bool genericStereoMonitorPath = false;
};

// These are operator declarations, not measurements of physical switches/cabling.
struct OperatorDeclarations {
    std::weak_ptr<const SessionMarker> session;
    bool explicitPerRunApproval = false;
    bool physicalSetupVerifiedByOperator = false;
    bool levelMatchingFixtureApprovedByOperator = false;
};

struct Revalidation {
    CaptureObservation capture;
    RenderObservation render;
    TransportObservation transport;
    OperatorDeclarations operatorDeclarations;
};

struct WorkerStatus {
    bool ownerCreated = false;
    bool startRequested = false;
    bool startAcknowledged = false;
    bool startSucceeded = false;
    bool stopRequested = false;
    bool quiescent = true;
    bool failed = false;
};

struct Report {
    State safetyGateStatus = State::Idle;
    Readiness captureReadiness = Readiness::NotChecked;
    Readiness renderReadiness = Readiness::NotChecked;
    RoutingStatus routingIsolationStatus = RoutingStatus::NotChecked;
    OutputState outputInhibitStatus = OutputState::Disabled;
    PeerShutdown peerShutdownStatus = PeerShutdown::NotStarted;
    ResourceCleanup resourceCleanupStatus = ResourceCleanup::NotStarted;
    SimulationStatus simulationStatus = SimulationStatus::Mocked;
    HardwareQaStatus hardwareQaStatus = HardwareQaStatus::NotRun;
    PhysicalRtlStatus physicalRtlStatus = PhysicalRtlStatus::NotMeasured;
    Failure failure = Failure::None;
    WorkerStatus captureWorker{};
    WorkerStatus renderWorker{};
    std::uint64_t acceptedNonSilentBuffers = 0;
    std::uint64_t acceptedSilentBuffers = 0;
    std::uint64_t lateWorkerEvents = 0;
};

class OutputInhibit final {
public:
    OutputState StateValue() const noexcept { return state_; }
    bool AllowsNonSilentPcm() const noexcept { return state_ == OutputState::Enabled; }
    void Inhibit() noexcept { state_ = OutputState::Inhibited; }
private:
    friend class Gate;
    void EnableForMockRun() noexcept {
        if (state_ == OutputState::Disabled) state_ = OutputState::Enabled;
    }
    OutputState state_ = OutputState::Disabled;
};

class Gate final {
public:
    Gate() = default;
    Gate(const Gate&) = delete;
    Gate& operator=(const Gate&) = delete;

    const SessionIdentity& Identity() const noexcept { return identity_; }
    const Report& GetReport() const noexcept { return report_; }
    const OutputInhibit& OutputControl() const noexcept { return output_; }
    State CurrentState() const noexcept { return report_.safetyGateStatus; }

    bool Configure(const SessionConfiguration& configuration) noexcept {
        if (report_.safetyGateStatus != State::Idle || configured_) return FailClosed(Failure::InvalidTransition);
        if (!ValidPlan(configuration.capture) || !ValidPlan(configuration.render))
            return FailClosed(Failure::InvalidConfiguration);
        try {
            configuration_ = configuration;
        } catch (...) {
            return FailClosed(Failure::InvalidConfiguration);
        }
        if (!Transition(State::Idle, State::Configured)) return FailClosed(Failure::InvalidTransition);
        configured_ = true;
        report_.resourceCleanupStatus = ResourceCleanup::WaitingForQuiescence;
        return true;
    }

    bool PrepareCapture(const CaptureObservation& observation) noexcept {
        if (IsTerminalOrStopping()) return LateEvent(Failure::LateWorkerEvent);
        if (report_.safetyGateStatus != State::Configured && report_.safetyGateStatus != State::CaptureReady)
            return FailClosed(Failure::InvalidTransition);
        if (capturePrepared_) return FailClosed(Failure::ReplayedReadiness);
        if (!BelongsToSession(observation.stream.session)) {
            report_.captureReadiness = Readiness::Failed;
            return FailClosed(Failure::StaleSession);
        }
        report_.captureWorker.ownerCreated = true;
        report_.captureWorker.quiescent = false;
        if (!ValidateCapture(observation, configuration_.capture, 0)) {
            report_.captureReadiness = Readiness::Failed;
            return false;
        }
        capturePrepared_ = true;
        captureSequence_ = observation.stream.observationSequence;
        report_.captureReadiness = observation.stream.source == EvidenceSource::MockAdapter
            ? Readiness::MockedReady : Readiness::ObservedReady;
        return true;
    }

    bool PrepareRender(const RenderObservation& observation) noexcept {
        if (IsTerminalOrStopping()) return LateEvent(Failure::LateWorkerEvent);
        if (report_.safetyGateStatus != State::Configured && report_.safetyGateStatus != State::CaptureReady)
            return FailClosed(Failure::InvalidTransition);
        if (renderPrepared_) return FailClosed(Failure::ReplayedReadiness);
        if (!BelongsToSession(observation.stream.session)) {
            report_.renderReadiness = Readiness::Failed;
            return FailClosed(Failure::StaleSession);
        }
        report_.renderWorker.ownerCreated = true;
        report_.renderWorker.quiescent = false;
        if (!ValidateRender(observation, configuration_.render, 0)) {
            report_.renderReadiness = Readiness::Failed;
            return false;
        }
        renderPrepared_ = true;
        renderSequence_ = observation.stream.observationSequence;
        report_.renderReadiness = observation.stream.source == EvidenceSource::MockAdapter
            ? Readiness::MockedReady : Readiness::ObservedReady;
        return true;
    }

    bool MarkCaptureReady() noexcept {
        if (report_.safetyGateStatus != State::Configured || !capturePrepared_)
            return FailClosed(Failure::CaptureNotReady);
        if (!Transition(State::Configured, State::CaptureReady)) return FailClosed(Failure::InvalidTransition);
        return true;
    }

    bool BeginRevalidation() noexcept {
        if (report_.safetyGateStatus != State::CaptureReady)
            return FailClosed(Failure::InvalidTransition);
        if (!capturePrepared_) return FailClosed(Failure::CaptureNotReady);
        if (!renderPrepared_) return FailClosed(Failure::RenderNotReady);
        if (!Transition(State::CaptureReady, State::Revalidating)) return FailClosed(Failure::InvalidTransition);
        return true;
    }

    bool Revalidate(const Revalidation& evidence) noexcept {
        if (report_.safetyGateStatus != State::Revalidating)
            return FailClosed(Failure::InvalidTransition);
        if (!capturePrepared_) return FailClosed(Failure::CaptureNotReady);
        if (!renderPrepared_) return FailClosed(Failure::RenderNotReady);
        if (!ValidateCapture(evidence.capture, configuration_.capture, captureSequence_)) {
            report_.captureReadiness = Readiness::Failed;
            return false;
        }
        if (!ValidateRender(evidence.render, configuration_.render, renderSequence_)) {
            report_.renderReadiness = Readiness::Failed;
            return false;
        }
        if (!BelongsToSession(evidence.transport.session) ||
            !BelongsToSession(evidence.operatorDeclarations.session))
            return FailClosed(Failure::StaleSession);
        if (evidence.transport.source == EvidenceSource::Simulated)
            return FailClosed(Failure::SimulatedEvidenceRejected);
        if (!evidence.transport.newlyConfiguredRing || !evidence.transport.ringEmpty ||
            !evidence.transport.noStalePcm || !evidence.transport.noTransportFaults)
            return FailClosed(Failure::TransportNotFresh);
        if (!evidence.transport.input1OnlyRouting || !evidence.transport.renderRightGuaranteedZero) {
            report_.routingIsolationStatus = RoutingStatus::Failed;
            return FailClosed(Failure::InvalidRouting);
        }
        if (evidence.transport.genericStereoMonitorPath) {
            report_.routingIsolationStatus = RoutingStatus::Failed;
            return FailClosed(Failure::GenericStereoRouting);
        }
        if (!evidence.operatorDeclarations.explicitPerRunApproval)
            return FailClosed(Failure::OperatorApprovalMissing);
        if (!evidence.operatorDeclarations.physicalSetupVerifiedByOperator)
            return FailClosed(Failure::PhysicalSetupUnconfirmed);
        if (!evidence.operatorDeclarations.levelMatchingFixtureApprovedByOperator)
            return FailClosed(Failure::LevelMatchingFixtureUnapproved);
        if (!Transition(State::Revalidating, State::OutputArmed)) return FailClosed(Failure::InvalidTransition);
        report_.routingIsolationStatus = RoutingStatus::Isolated;
        // OUTPUT_ARMED remains disabled; worker acknowledgements are required after Start.
        return true;
    }

    bool Start(bool explicitSharedStartDecision) noexcept {
        if (startConsumed_) return FailClosed(Failure::RepeatedStart);
        if (report_.safetyGateStatus != State::OutputArmed)
            return FailClosed(Failure::StartBeforeAuthorized);
        if (!explicitSharedStartDecision) return FailClosed(Failure::StartBeforeAuthorized);
        if (!Transition(State::OutputArmed, State::Running)) return FailClosed(Failure::InvalidTransition);
        startConsumed_ = true;
        report_.captureWorker.startRequested = true;
        report_.renderWorker.startRequested = true;
        report_.outputInhibitStatus = output_.StateValue();
        return true;
    }

    bool AcknowledgeStarted(Worker worker, bool startSucceeded = true) noexcept {
        if (report_.safetyGateStatus != State::Running) {
            if (IsTerminalOrStopping()) return LateEvent(Failure::LateWorkerEvent);
            return FailClosed(Failure::InvalidStartAcknowledgement);
        }
        auto& status = WorkerValue(worker);
        if (!status.ownerCreated || !status.startRequested || status.startAcknowledged)
            return FailClosed(Failure::InvalidStartAcknowledgement);
        status.startAcknowledged = true;
        status.startSucceeded = startSucceeded;
        if (!startSucceeded) {
            status.failed = true;
            return FailClosed(worker == Worker::Capture ? Failure::CaptureFailed : Failure::RenderFailed);
        }
        if (report_.captureWorker.startAcknowledged && report_.captureWorker.startSucceeded &&
            report_.renderWorker.startAcknowledged && report_.renderWorker.startSucceeded) {
            output_.EnableForMockRun();
            report_.outputInhibitStatus = output_.StateValue();
        }
        return true;
    }

    bool AcceptFakeRenderBuffer(std::span<const std::int16_t> interleavedStereo) noexcept {
        if ((interleavedStereo.size() % 2) != 0 || interleavedStereo.empty())
            return FailClosed(Failure::InvalidRouting);
        bool nonSilent = false;
        for (std::size_t frame = 0; frame < interleavedStereo.size() / 2; ++frame) {
            nonSilent = nonSilent || interleavedStereo[frame * 2] != 0 || interleavedStereo[frame * 2 + 1] != 0;
            if (interleavedStereo[frame * 2 + 1] != 0)
                return FailClosed(Failure::RenderRightNonZero);
        }
        if (nonSilent && !output_.AllowsNonSilentPcm())
            return FailClosed(Failure::InvalidTransition);
        if (report_.safetyGateStatus != State::Running && nonSilent)
            return FailClosed(Failure::InvalidTransition);
        if (nonSilent) ++report_.acceptedNonSilentBuffers;
        else ++report_.acceptedSilentBuffers;
        return true;
    }

    bool StopNormally() noexcept {
        if (report_.safetyGateStatus != State::Running) return FailClosed(Failure::InvalidTransition);
        BeginStopping(State::Completed, Failure::None);
        return true;
    }

    bool Cancel() noexcept {
        if (report_.safetyGateStatus == State::Completed || report_.safetyGateStatus == State::Failed ||
            report_.safetyGateStatus == State::Cancelled) return false;
        BeginStopping(State::Cancelled, Failure::None);
        return true;
    }

    bool NotifyFault(Fault fault) noexcept {
        if (report_.safetyGateStatus == State::Completed || report_.safetyGateStatus == State::Failed ||
            report_.safetyGateStatus == State::Cancelled) return LateEvent(Failure::LateWorkerEvent);
        Failure failure = Failure::InvalidTransition;
        switch (fault) {
        case Fault::CaptureFailure: failure = Failure::CaptureFailed; report_.captureWorker.failed = true; report_.captureReadiness = Readiness::Failed; break;
        case Fault::RenderFailure: failure = Failure::RenderFailed; report_.renderWorker.failed = true; report_.renderReadiness = Readiness::Failed; break;
        case Fault::EndpointInvalidation:
            failure = Failure::DeviceInvalidated;
            report_.captureReadiness = Readiness::Failed;
            report_.renderReadiness = Readiness::Failed;
            break;
        case Fault::FormatMismatch:
            failure = Failure::RuntimeFormatMismatch;
            report_.captureReadiness = Readiness::Failed;
            report_.renderReadiness = Readiness::Failed;
            break;
        case Fault::RingOverflow: failure = Failure::RingOverflow; break;
        case Fault::RingUnderrun: failure = Failure::RingUnderrun; break;
        case Fault::InvalidRouting: failure = Failure::InvalidRouting; report_.routingIsolationStatus = RoutingStatus::Failed; break;
        case Fault::RenderRightNonZero: failure = Failure::RenderRightNonZero; report_.routingIsolationStatus = RoutingStatus::Failed; break;
        case Fault::TimestampContinuity: failure = Failure::TimestampFault; report_.captureReadiness = Readiness::Failed; break;
        case Fault::WatchdogTimeout: failure = Failure::WatchdogTimeout; break;
        case Fault::PeerShutdown: failure = Failure::PeerShutdownFailed; peerShutdownFailed_ = true; break;
        case Fault::InvalidTransition: failure = Failure::InvalidTransition; break;
        }
        FailClosed(failure);
        return true;
    }

    bool RequestWorkerStop(Worker worker) noexcept {
        if (report_.safetyGateStatus != State::Stopping) return FailClosed(Failure::InvalidTransition);
        auto& status = WorkerValue(worker);
        if (!status.ownerCreated) return true; // This worker never acquired resources.
        status.stopRequested = true;
        report_.peerShutdownStatus = PeerShutdown::StopRequested;
        return true;
    }

    bool AcknowledgeQuiescent(Worker worker) noexcept {
        if (report_.safetyGateStatus != State::Stopping) return FailClosed(Failure::InvalidTransition);
        auto& status = WorkerValue(worker);
        if (!status.ownerCreated) return true;
        if (status.quiescent) return true; // Duplicate stop acknowledgement is idempotent.
        if (!status.stopRequested) return FailClosed(Failure::InvalidTransition);
        status.quiescent = true;
        UpdateCleanupReadiness();
        return true;
    }

    bool CompleteCleanup(bool cleanupSucceeded) noexcept {
        if (report_.safetyGateStatus == State::Completed || report_.safetyGateStatus == State::Failed ||
            report_.safetyGateStatus == State::Cancelled)
            return report_.resourceCleanupStatus == ResourceCleanup::Cleaned;
        if (report_.safetyGateStatus != State::Stopping)
            return FailClosed(Failure::InvalidTransition);
        if (!AllWorkersQuiescent()) return false;
        if (!cleanupSucceeded) {
            pendingTerminal_ = State::Failed;
            report_.failure = Failure::CleanupFailed;
        }
        if (!Transition(State::Stopping, pendingTerminal_)) return FailClosed(Failure::InvalidTransition);
        report_.resourceCleanupStatus = cleanupSucceeded ? ResourceCleanup::Cleaned : ResourceCleanup::Failed;
        report_.peerShutdownStatus = peerShutdownFailed_ ? PeerShutdown::Failed : PeerShutdown::Quiescent;
        return cleanupSucceeded;
    }

    bool NoteLateWorkerEvent() noexcept { return LateEvent(Failure::LateWorkerEvent); }

private:
    static bool ValidPlan(const StreamPlan& plan) noexcept {
        return !plan.endpointIdentity.empty() && plan.format.encoding == SampleEncoding::Pcm16 &&
            plan.format.sampleRate == 48'000 && plan.format.channels == 2 &&
            plan.format.bitsPerSample == 16 && plan.period100ns != 0 && plan.bufferFrames != 0;
    }

    bool ValidateCommon(const StreamObservation& observation, const StreamPlan& plan,
                        std::uint64_t previousSequence, Failure invalidFormat) noexcept {
        if (!BelongsToSession(observation.session)) return FailClosed(Failure::StaleSession);
        if (observation.source == EvidenceSource::Simulated) return FailClosed(Failure::SimulatedEvidenceRejected);
        if (observation.observationSequence == 0 || observation.observationSequence <= previousSequence)
            return FailClosed(Failure::ReplayedReadiness);
        if (!observation.endpointExplicitlySelected || observation.endpointIdentity.empty())
            return FailClosed(Failure::MissingEndpoint);
        if (observation.endpointIdentity != plan.endpointIdentity)
            return FailClosed(Failure::EndpointChanged);
        if (!(observation.format == plan.format)) return FailClosed(invalidFormat);
        if (observation.period100ns != plan.period100ns) return FailClosed(Failure::PeriodChanged);
        if (observation.actualBufferFrames != plan.bufferFrames || !observation.bufferGeometryValid)
            return FailClosed(Failure::BufferGeometryMismatch);
        if (!observation.initializeSucceeded) return FailClosed(Failure::InitializeFailed);
        if (!observation.eventHandleSet) return FailClosed(Failure::EventSetupFailed);
        if (!observation.workerReady) return FailClosed(Failure::WorkerNotReady);
        if (!observation.deviceValid) return FailClosed(Failure::DeviceInvalidated);
        return true;
    }

    bool ValidateCapture(const CaptureObservation& observation, const StreamPlan& plan,
                         std::uint64_t previousSequence) noexcept {
        if (!ValidateCommon(observation.stream, plan, previousSequence, Failure::CaptureFormatInvalid)) return false;
        if (!observation.continuityClean) return FailClosed(Failure::CaptureContinuityFault);
        if (!observation.timestampsClean) return FailClosed(Failure::TimestampFault);
        return true;
    }

    bool ValidateRender(const RenderObservation& observation, const StreamPlan& plan,
                        std::uint64_t previousSequence) noexcept {
        if (!ValidateCommon(observation.stream, plan, previousSequence, Failure::RenderFormatInvalid)) return false;
        if (!observation.initiallySilent) return FailClosed(Failure::InvalidRouting);
        return true;
    }

    WorkerStatus& WorkerValue(Worker worker) noexcept {
        return worker == Worker::Capture ? report_.captureWorker : report_.renderWorker;
    }
    bool BelongsToSession(const std::weak_ptr<const SessionMarker>& reference) const noexcept {
        const auto marker = reference.lock();
        return marker && marker == identity_.marker_;
    }
    bool AllWorkersQuiescent() const noexcept {
        const auto quiescent = [](const WorkerStatus& worker) { return !worker.ownerCreated || worker.quiescent; };
        return quiescent(report_.captureWorker) && quiescent(report_.renderWorker);
    }
    bool IsTerminalOrStopping() const noexcept {
        const auto state = report_.safetyGateStatus;
        return state == State::Stopping || state == State::Completed || state == State::Failed || state == State::Cancelled;
    }

    bool Transition(State expected, State next) noexcept {
        if (report_.safetyGateStatus != expected) return false;
        const bool allowed =
            (expected == State::Idle && (next == State::Configured || next == State::Stopping)) ||
            (expected == State::Configured && (next == State::CaptureReady || next == State::Stopping)) ||
            (expected == State::CaptureReady && (next == State::Revalidating || next == State::Stopping)) ||
            (expected == State::Revalidating && (next == State::OutputArmed || next == State::Stopping)) ||
            (expected == State::OutputArmed && (next == State::Running || next == State::Stopping)) ||
            (expected == State::Running && next == State::Stopping) ||
            (expected == State::Stopping && (next == State::Completed || next == State::Failed || next == State::Cancelled));
        if (!allowed) return false;
        report_.safetyGateStatus = next;
        return true;
    }

    void BeginStopping(State terminal, Failure failure) noexcept {
        output_.Inhibit();
        report_.outputInhibitStatus = output_.StateValue();
        if (report_.safetyGateStatus == State::Stopping) {
            if (pendingTerminal_ == State::Completed && terminal == State::Cancelled)
                pendingTerminal_ = State::Cancelled;
            return;
        }
        pendingTerminal_ = terminal;
        report_.failure = failure;
        const auto current = report_.safetyGateStatus;
        if (!Transition(current, State::Stopping)) {
            output_.Inhibit();
            report_.outputInhibitStatus = output_.StateValue();
            report_.failure = Failure::InvalidTransition;
            pendingTerminal_ = State::Failed;
            return;
        }
        report_.resourceCleanupStatus = ResourceCleanup::WaitingForQuiescence;
        if (report_.captureWorker.ownerCreated) report_.captureWorker.stopRequested = true;
        if (report_.renderWorker.ownerCreated) report_.renderWorker.stopRequested = true;
        report_.peerShutdownStatus = AllWorkersQuiescent() ? PeerShutdown::Quiescent : PeerShutdown::StopRequested;
    }

    bool FailClosed(Failure failure) noexcept {
        if (report_.safetyGateStatus == State::Completed || report_.safetyGateStatus == State::Failed ||
            report_.safetyGateStatus == State::Cancelled) return false;
        if (report_.safetyGateStatus == State::Stopping) {
            // Preserve the initiating failure/cancellation. A fault during an otherwise normal
            // stop can still upgrade completion to failure; repeated rejected output cannot.
            if (pendingTerminal_ == State::Completed) {
                pendingTerminal_ = State::Failed;
                report_.failure = failure;
            }
            return false;
        }
        output_.Inhibit();
        report_.outputInhibitStatus = output_.StateValue();
        pendingTerminal_ = State::Failed;
        report_.failure = failure;
        const auto current = report_.safetyGateStatus;
        if (!Transition(current, State::Stopping)) {
            output_.Inhibit();
            report_.outputInhibitStatus = output_.StateValue();
            report_.failure = Failure::InvalidTransition;
            pendingTerminal_ = State::Failed;
            return false;
        }
        report_.resourceCleanupStatus = ResourceCleanup::WaitingForQuiescence;
        if (report_.captureWorker.ownerCreated) report_.captureWorker.stopRequested = true;
        if (report_.renderWorker.ownerCreated) report_.renderWorker.stopRequested = true;
        report_.peerShutdownStatus = peerShutdownFailed_ ? PeerShutdown::Failed :
            (AllWorkersQuiescent() ? PeerShutdown::Quiescent : PeerShutdown::StopRequested);
        UpdateCleanupReadiness();
        return false;
    }

    bool LateEvent(Failure failure) noexcept {
        ++report_.lateWorkerEvents;
        if (report_.safetyGateStatus == State::Completed || report_.safetyGateStatus == State::Failed ||
            report_.safetyGateStatus == State::Cancelled) return false;
        return FailClosed(failure);
    }

    void UpdateCleanupReadiness() noexcept {
        if (report_.safetyGateStatus != State::Stopping) return;
        if (AllWorkersQuiescent()) report_.resourceCleanupStatus = ResourceCleanup::ReadyToReclaim;
        report_.peerShutdownStatus = peerShutdownFailed_ ? PeerShutdown::Failed :
            (AllWorkersQuiescent() ? PeerShutdown::Quiescent : PeerShutdown::StopRequested);
    }

    SessionIdentity identity_;
    SessionConfiguration configuration_{};
    Report report_{};
    OutputInhibit output_{};
    std::uint64_t captureSequence_ = 0;
    std::uint64_t renderSequence_ = 0;
    State pendingTerminal_ = State::Failed;
    bool configured_ = false;
    bool capturePrepared_ = false;
    bool renderPrepared_ = false;
    bool startConsumed_ = false;
    bool peerShutdownFailed_ = false;
};

} // namespace RuntimeSafetyGate
