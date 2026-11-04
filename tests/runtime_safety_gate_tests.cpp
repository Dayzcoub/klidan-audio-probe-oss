#include "../src/probe/runtime_safety_gate.h"
#include "../src/probe/monitor_rtl_session.h"

#include <array>
#include <cstdio>
#include <span>
#include <string_view>
#include <type_traits>

namespace {
using namespace RuntimeSafetyGate;
int failures = 0;
#define CHECK(x) do { if (!(x)) { std::fprintf(stderr, "FAIL %s:%d: %s\n", __FILE__, __LINE__, #x); ++failures; } } while (false)

static_assert(!std::is_copy_constructible_v<SessionIdentity>);
static_assert(!std::is_convertible_v<MonitorRtlSession::SimulatedReadiness, CaptureObservation>);

StreamPlan CapturePlan() { return {"mock-capture", {}, 300'000, 144}; }
StreamPlan RenderPlan() { return {"mock-render", {}, 300'000, 144}; }
SessionConfiguration Configuration() { return {CapturePlan(), RenderPlan()}; }

StreamObservation Stream(const Gate& gate, const StreamPlan& plan, std::uint64_t sequence) {
    StreamObservation result;
    result.session = gate.Identity().Reference();
    result.source = EvidenceSource::MockAdapter;
    result.endpointIdentity = plan.endpointIdentity;
    result.format = plan.format;
    result.period100ns = plan.period100ns;
    result.actualBufferFrames = plan.bufferFrames;
    result.observationSequence = sequence;
    result.endpointExplicitlySelected = true;
    result.initializeSucceeded = true;
    result.eventHandleSet = true;
    result.bufferGeometryValid = true;
    result.workerReady = true;
    result.deviceValid = true;
    return result;
}

CaptureObservation Capture(const Gate& gate, std::uint64_t sequence = 1) {
    return {Stream(gate, CapturePlan(), sequence), true, true};
}
RenderObservation Render(const Gate& gate, std::uint64_t sequence = 1) {
    return {Stream(gate, RenderPlan(), sequence), true};
}
TransportObservation Transport(const Gate& gate) {
    return {gate.Identity().Reference(), EvidenceSource::MockAdapter, true, true, true, true, true, true, false};
}
OperatorDeclarations OperatorApproval(const Gate& gate) {
    return {gate.Identity().Reference(), true, true, true};
}
Revalidation FreshRevalidation(const Gate& gate) {
    return {Capture(gate, 2), Render(gate, 2), Transport(gate), OperatorApproval(gate)};
}

bool Configure(Gate& gate) { return gate.Configure(Configuration()); }
bool PrepareBoth(Gate& gate, bool renderFirst = false) {
    if (renderFirst) {
        if (!gate.PrepareRender(Render(gate))) return false;
        if (!gate.PrepareCapture(Capture(gate))) return false;
    } else {
        if (!gate.PrepareCapture(Capture(gate))) return false;
        if (!gate.PrepareRender(Render(gate))) return false;
    }
    return gate.MarkCaptureReady() && gate.BeginRevalidation() && gate.Revalidate(FreshRevalidation(gate));
}
bool ArmAndStart(Gate& gate, bool renderFirst = false) {
    return Configure(gate) && PrepareBoth(gate, renderFirst) && gate.Start(true) &&
        (renderFirst ? gate.AcknowledgeStarted(Worker::Render) && gate.AcknowledgeStarted(Worker::Capture)
                     : gate.AcknowledgeStarted(Worker::Capture) && gate.AcknowledgeStarted(Worker::Render));
}

void AcknowledgeOwned(Gate& gate, Worker worker) {
    const auto& status = worker == Worker::Capture ? gate.GetReport().captureWorker : gate.GetReport().renderWorker;
    if (status.ownerCreated && !status.quiescent) CHECK(gate.AcknowledgeQuiescent(worker));
}

bool FinishCleanup(Gate& gate, bool success = true) {
    AcknowledgeOwned(gate, Worker::Capture);
    AcknowledgeOwned(gate, Worker::Render);
    return gate.CompleteCleanup(success);
}

struct FakeRenderAdapter {
    explicit FakeRenderAdapter(Gate& value) : gate(value) {}
    bool Submit(std::span<const std::int16_t> pcm) { return gate.AcceptFakeRenderBuffer(pcm); }
    Gate& gate;
};

struct MockWorkerAdapter {
    MockWorkerAdapter(Gate& value, Worker kind) : gate(value), worker(kind) {}
    bool Prepare() {
        return worker == Worker::Capture ? gate.PrepareCapture(Capture(gate))
                                         : gate.PrepareRender(Render(gate));
    }
    bool StartAfterSharedDecision() {
        const auto& status = worker == Worker::Capture ? gate.GetReport().captureWorker
                                                       : gate.GetReport().renderWorker;
        if (gate.CurrentState() != State::Running || !status.startRequested) return false;
        ++startCalls;
        started = true;
        return true;
    }
    bool StopAndAcknowledge() {
        if (gate.CurrentState() != State::Stopping) return false;
        ++stopCalls;
        return gate.AcknowledgeQuiescent(worker);
    }
    Gate& gate;
    Worker worker;
    unsigned startCalls = 0;
    unsigned stopCalls = 0;
    bool started = false;
};

bool AssertClosed(Gate& gate) {
    CHECK(!gate.OutputControl().AllowsNonSilentPcm());
    const std::array<std::int16_t, 2> nonSilent{1200, 0};
    FakeRenderAdapter fake(gate);
    CHECK(!fake.Submit(nonSilent));
    CHECK(!gate.OutputControl().AllowsNonSilentPcm());
    CHECK(gate.GetReport().physicalRtlStatus == PhysicalRtlStatus::NotMeasured);
    CHECK(gate.GetReport().hardwareQaStatus == HardwareQaStatus::NotRun);
    CHECK(gate.GetReport().simulationStatus == SimulationStatus::Mocked);
    return failures == 0;
}

bool TestValidOrderingsAndNormalShutdown() {
    for (const bool renderFirst : {false, true}) {
        Gate gate;
        MockWorkerAdapter captureWorker(gate, Worker::Capture);
        MockWorkerAdapter renderWorker(gate, Worker::Render);
        CHECK(gate.CurrentState() == State::Idle);
        CHECK(gate.OutputControl().StateValue() == OutputState::Disabled);
        CHECK(Configure(gate));
        if (renderFirst) {
            CHECK(renderWorker.Prepare());
            CHECK(captureWorker.Prepare());
        } else {
            CHECK(captureWorker.Prepare());
            CHECK(renderWorker.Prepare());
        }
        CHECK(gate.MarkCaptureReady());
        CHECK(gate.BeginRevalidation());
        CHECK(gate.Revalidate(FreshRevalidation(gate)));
        CHECK(gate.CurrentState() == State::OutputArmed);
        CHECK(gate.OutputControl().StateValue() == OutputState::Disabled);
        CHECK(gate.Start(true));
        CHECK(captureWorker.StartAfterSharedDecision());
        CHECK(renderWorker.StartAfterSharedDecision());
        CHECK(captureWorker.startCalls == 1 && renderWorker.startCalls == 1);
        CHECK(gate.CurrentState() == State::Running);
        CHECK(gate.OutputControl().StateValue() == OutputState::Disabled);
        CHECK(!gate.GetReport().captureWorker.startAcknowledged && !gate.GetReport().renderWorker.startAcknowledged);
        CHECK(renderFirst ? gate.AcknowledgeStarted(Worker::Render) : gate.AcknowledgeStarted(Worker::Capture));
        CHECK(gate.OutputControl().StateValue() == OutputState::Disabled);
        CHECK(renderFirst ? gate.AcknowledgeStarted(Worker::Capture) : gate.AcknowledgeStarted(Worker::Render));
        CHECK(gate.GetReport().captureWorker.startSucceeded && gate.GetReport().renderWorker.startSucceeded);
        CHECK(gate.GetReport().captureReadiness == Readiness::MockedReady);
        CHECK(gate.GetReport().renderReadiness == Readiness::MockedReady);
        CHECK(gate.GetReport().routingIsolationStatus == RoutingStatus::Isolated);
        CHECK(SimulationStatusText(gate.GetReport().simulationStatus) == std::string_view("MOCKED"));
        CHECK(HardwareQaStatusText(gate.GetReport().hardwareQaStatus) == std::string_view("NOT_RUN"));
        CHECK(PhysicalRtlStatusText(gate.GetReport().physicalRtlStatus) == std::string_view("NOT_MEASURED"));
        CHECK(gate.OutputControl().AllowsNonSilentPcm());
        const std::array<std::int16_t, 4> validRoute{1000, 0, -1000, 0};
        FakeRenderAdapter fake(gate);
        CHECK(fake.Submit(validRoute));
        const std::array<std::int16_t, 4> silence{};
        CHECK(fake.Submit(silence));
        CHECK(gate.GetReport().acceptedNonSilentBuffers == 1);
        CHECK(gate.GetReport().acceptedSilentBuffers == 1);
        CHECK(gate.StopNormally());
        CHECK(gate.CurrentState() == State::Stopping);
        CHECK(gate.OutputControl().StateValue() == OutputState::Inhibited);
        CHECK(gate.GetReport().resourceCleanupStatus == ResourceCleanup::WaitingForQuiescence);
        CHECK((renderFirst ? renderWorker.StopAndAcknowledge() : captureWorker.StopAndAcknowledge()));
        CHECK(captureWorker.stopCalls + renderWorker.stopCalls == 1);
        CHECK(gate.CurrentState() == State::Stopping);
        CHECK(gate.GetReport().resourceCleanupStatus == ResourceCleanup::WaitingForQuiescence);
        CHECK((renderFirst ? captureWorker.StopAndAcknowledge() : renderWorker.StopAndAcknowledge()));
        CHECK(captureWorker.stopCalls == 1 && renderWorker.stopCalls == 1);
        CHECK(gate.GetReport().resourceCleanupStatus == ResourceCleanup::ReadyToReclaim);
        CHECK(gate.CompleteCleanup(true));
        CHECK(gate.CurrentState() == State::Completed);
        CHECK(gate.GetReport().resourceCleanupStatus == ResourceCleanup::Cleaned);
        CHECK(!gate.OutputControl().AllowsNonSilentPcm());
        CHECK(gate.CompleteCleanup(true)); // Idempotent after completed cleanup.
        AssertClosed(gate);
    }
    return failures == 0;
}

bool TestReadinessRejection() {
    {
        Gate gate; CHECK(Configure(gate)); CHECK(gate.PrepareCapture(Capture(gate)));
        CHECK(gate.MarkCaptureReady());
        CHECK(!gate.BeginRevalidation());
        CHECK(gate.GetReport().failure == Failure::RenderNotReady);
        CHECK(!gate.GetReport().captureWorker.startRequested && !gate.GetReport().renderWorker.startRequested);
        AssertClosed(gate); CHECK(FinishCleanup(gate));
    }
    {
        Gate gate; CHECK(Configure(gate));
        auto capture = Capture(gate); capture.stream.eventHandleSet = false;
        CHECK(!gate.PrepareCapture(capture)); CHECK(gate.GetReport().failure == Failure::EventSetupFailed);
        AssertClosed(gate); CHECK(FinishCleanup(gate)); CHECK(gate.CurrentState() == State::Failed);
    }
    {
        Gate gate; CHECK(Configure(gate));
        CHECK(gate.PrepareCapture(Capture(gate)));
        auto render = Render(gate); render.stream.format.channels = 1;
        CHECK(!gate.PrepareRender(render)); CHECK(gate.GetReport().failure == Failure::RenderFormatInvalid);
        CHECK(!gate.GetReport().captureWorker.startRequested && !gate.GetReport().renderWorker.startRequested);
        AssertClosed(gate); CHECK(FinishCleanup(gate));
    }
    {
        Gate gate; CHECK(Configure(gate));
        auto capture = Capture(gate); capture.stream.format.encoding = SampleEncoding::Other;
        CHECK(!gate.PrepareCapture(capture)); CHECK(gate.GetReport().failure == Failure::CaptureFormatInvalid);
        AssertClosed(gate); CHECK(FinishCleanup(gate));
    }
    {
        Gate gate; CHECK(Configure(gate));
        auto render = Render(gate); render.stream.actualBufferFrames = 128;
        CHECK(!gate.PrepareRender(render)); CHECK(gate.GetReport().failure == Failure::BufferGeometryMismatch);
        AssertClosed(gate); CHECK(FinishCleanup(gate));
    }
    {
        Gate gate; CHECK(Configure(gate));
        auto capture = Capture(gate); capture.stream.period100ns++;
        CHECK(!gate.PrepareCapture(capture)); CHECK(gate.GetReport().failure == Failure::PeriodChanged);
        AssertClosed(gate); CHECK(FinishCleanup(gate));
    }
    {
        Gate gate; CHECK(Configure(gate));
        CHECK(gate.PrepareCapture(Capture(gate)));
        auto render = Render(gate); render.stream.endpointIdentity = "changed-endpoint";
        CHECK(!gate.PrepareRender(render)); CHECK(gate.GetReport().failure == Failure::EndpointChanged);
        AssertClosed(gate); CHECK(FinishCleanup(gate));
    }
    {
        Gate gate; CHECK(Configure(gate));
        auto capture = Capture(gate); capture.stream.source = EvidenceSource::Simulated;
        CHECK(!gate.PrepareCapture(capture));
        CHECK(gate.GetReport().failure == Failure::SimulatedEvidenceRejected);
        AssertClosed(gate); CHECK(FinishCleanup(gate));
    }
    {
        Gate gate; CHECK(Configure(gate));
        auto capture = Capture(gate); capture.continuityClean = false;
        CHECK(!gate.PrepareCapture(capture)); CHECK(gate.GetReport().failure == Failure::CaptureContinuityFault);
        AssertClosed(gate); CHECK(FinishCleanup(gate));
    }
    {
        Gate gate; CHECK(Configure(gate));
        auto capture = Capture(gate); capture.stream.initializeSucceeded = false;
        CHECK(!gate.PrepareCapture(capture)); CHECK(gate.GetReport().failure == Failure::InitializeFailed);
        AssertClosed(gate); CHECK(FinishCleanup(gate));
    }
    return failures == 0;
}

bool TestSessionReplayAndRevalidation() {
    Gate prior; CHECK(Configure(prior)); CHECK(prior.PrepareCapture(Capture(prior)));
    auto staleCapture = Capture(prior);
    Gate fresh; CHECK(Configure(fresh));
    CHECK(!fresh.PrepareCapture(staleCapture));
    CHECK(fresh.GetReport().failure == Failure::StaleSession);
    AssertClosed(fresh); CHECK(FinishCleanup(fresh));
    CHECK(!prior.PrepareCapture(Capture(prior)));
    CHECK(prior.GetReport().failure == Failure::ReplayedReadiness);
    AssertClosed(prior); CHECK(FinishCleanup(prior));

    Gate reused; CHECK(Configure(reused)); CHECK(PrepareBoth(reused));
    CHECK(reused.CurrentState() == State::OutputArmed);
    CHECK(reused.OutputControl().StateValue() == OutputState::Disabled);
    auto replay = FreshRevalidation(reused);
    // Revalidation is single-use; a second submission cannot re-arm the gate.
    CHECK(!reused.Revalidate(replay));
    CHECK(reused.GetReport().failure == Failure::InvalidTransition);
    AssertClosed(reused); CHECK(FinishCleanup(reused));

    Gate stalePreflight; CHECK(Configure(stalePreflight));
    CHECK(stalePreflight.PrepareCapture(Capture(stalePreflight, 9)));
    CHECK(stalePreflight.PrepareRender(Render(stalePreflight, 1)));
    CHECK(stalePreflight.MarkCaptureReady()); CHECK(stalePreflight.BeginRevalidation());
    auto oldObservation = FreshRevalidation(stalePreflight);
    oldObservation.capture.stream.observationSequence = 9;
    CHECK(!stalePreflight.Revalidate(oldObservation));
    CHECK(stalePreflight.GetReport().failure == Failure::ReplayedReadiness);
    AssertClosed(stalePreflight); CHECK(FinishCleanup(stalePreflight));

    CaptureObservation expiredSessionEvidence;
    {
        Gate expired; CHECK(Configure(expired));
        expiredSessionEvidence = Capture(expired);
    }
    Gate reusedStorage; CHECK(Configure(reusedStorage));
    CHECK(!reusedStorage.PrepareCapture(expiredSessionEvidence));
    CHECK(reusedStorage.GetReport().failure == Failure::StaleSession);
    AssertClosed(reusedStorage); CHECK(FinishCleanup(reusedStorage));

    Gate sourceRun; CHECK(Configure(sourceRun));
    CHECK(sourceRun.PrepareCapture(Capture(sourceRun)) && sourceRun.PrepareRender(Render(sourceRun)));
    CHECK(sourceRun.MarkCaptureReady() && sourceRun.BeginRevalidation());
    const auto sourceEvidence = FreshRevalidation(sourceRun);
    Gate transportReplay; CHECK(Configure(transportReplay));
    CHECK(transportReplay.PrepareCapture(Capture(transportReplay)) && transportReplay.PrepareRender(Render(transportReplay)));
    CHECK(transportReplay.MarkCaptureReady() && transportReplay.BeginRevalidation());
    auto replayedTransport = FreshRevalidation(transportReplay);
    replayedTransport.transport = sourceEvidence.transport;
    CHECK(!transportReplay.Revalidate(replayedTransport));
    CHECK(transportReplay.GetReport().failure == Failure::StaleSession);
    AssertClosed(transportReplay); CHECK(FinishCleanup(transportReplay));

    Gate approvalReplay; CHECK(Configure(approvalReplay));
    CHECK(approvalReplay.PrepareCapture(Capture(approvalReplay)) && approvalReplay.PrepareRender(Render(approvalReplay)));
    CHECK(approvalReplay.MarkCaptureReady() && approvalReplay.BeginRevalidation());
    auto replayedApproval = FreshRevalidation(approvalReplay);
    replayedApproval.operatorDeclarations = sourceEvidence.operatorDeclarations;
    CHECK(!approvalReplay.Revalidate(replayedApproval));
    CHECK(approvalReplay.GetReport().failure == Failure::StaleSession);
    AssertClosed(approvalReplay); CHECK(FinishCleanup(approvalReplay));
    return failures == 0;
}

bool TestRoutingTransportAndOperatorGates() {
    const auto rejectRevalidation = [](const auto& mutate, Failure expected) {
        Gate gate; CHECK(Configure(gate));
        CHECK(gate.PrepareCapture(Capture(gate)));
        CHECK(gate.PrepareRender(Render(gate)));
        CHECK(gate.MarkCaptureReady());
        CHECK(gate.BeginRevalidation());
        auto evidence = FreshRevalidation(gate); mutate(evidence);
        CHECK(!gate.Revalidate(evidence)); CHECK(gate.GetReport().failure == expected);
        AssertClosed(gate); CHECK(FinishCleanup(gate));
    };
    rejectRevalidation([](auto& e) { e.transport.ringEmpty = false; }, Failure::TransportNotFresh);
    rejectRevalidation([](auto& e) { e.transport.newlyConfiguredRing = false; }, Failure::TransportNotFresh);
    rejectRevalidation([](auto& e) { e.transport.noStalePcm = false; }, Failure::TransportNotFresh);
    rejectRevalidation([](auto& e) { e.transport.noTransportFaults = false; }, Failure::TransportNotFresh);
    rejectRevalidation([](auto& e) { e.transport.input1OnlyRouting = false; }, Failure::InvalidRouting);
    rejectRevalidation([](auto& e) { e.transport.renderRightGuaranteedZero = false; }, Failure::InvalidRouting);
    rejectRevalidation([](auto& e) { e.transport.genericStereoMonitorPath = true; }, Failure::GenericStereoRouting);
    rejectRevalidation([](auto& e) { e.transport.source = EvidenceSource::Simulated; }, Failure::SimulatedEvidenceRejected);
    rejectRevalidation([](auto& e) { e.operatorDeclarations.explicitPerRunApproval = false; }, Failure::OperatorApprovalMissing);
    rejectRevalidation([](auto& e) { e.operatorDeclarations.physicalSetupVerifiedByOperator = false; }, Failure::PhysicalSetupUnconfirmed);
    rejectRevalidation([](auto& e) { e.operatorDeclarations.levelMatchingFixtureApprovedByOperator = false; }, Failure::LevelMatchingFixtureUnapproved);
    rejectRevalidation([](auto& e) { e.capture.stream.endpointIdentity = "swapped-capture"; }, Failure::EndpointChanged);
    rejectRevalidation([](auto& e) { e.render.stream.format.sampleRate = 44'100; }, Failure::RenderFormatInvalid);
    rejectRevalidation([](auto& e) { e.capture.stream.period100ns -= 1; }, Failure::PeriodChanged);
    rejectRevalidation([](auto& e) { e.render.stream.actualBufferFrames -= 1; }, Failure::BufferGeometryMismatch);
    rejectRevalidation([](auto& e) { e.capture.timestampsClean = false; }, Failure::TimestampFault);
    rejectRevalidation([](auto& e) { e.render.initiallySilent = false; }, Failure::InvalidRouting);
    return failures == 0;
}

bool TestStartAndFakeOutputInhibit() {
    {
        Gate gate; CHECK(Configure(gate));
        CHECK(gate.PrepareCapture(Capture(gate))); CHECK(gate.PrepareRender(Render(gate)));
        CHECK(!gate.Start(true)); CHECK(gate.GetReport().failure == Failure::StartBeforeAuthorized);
        AssertClosed(gate); CHECK(FinishCleanup(gate));
    }
    {
        Gate gate; CHECK(ArmAndStart(gate));
        CHECK(!gate.Start(true));
        CHECK(gate.GetReport().failure == Failure::RepeatedStart);
        CHECK(gate.CurrentState() == State::Stopping);
        CHECK(gate.OutputControl().StateValue() == OutputState::Inhibited);
        const std::array<std::int16_t, 2> data{900, 0};
        CHECK(!gate.AcceptFakeRenderBuffer(data));
        CHECK(FinishCleanup(gate)); CHECK(gate.CurrentState() == State::Failed);
    }
    {
        Gate gate; CHECK(Configure(gate)); CHECK(PrepareBoth(gate));
        const std::array<std::int16_t, 2> data{900, 0};
        CHECK(!gate.AcceptFakeRenderBuffer(data));
        CHECK(gate.GetReport().failure == Failure::InvalidTransition);
        AssertClosed(gate); CHECK(FinishCleanup(gate));
    }
    {
        Gate gate; CHECK(Configure(gate)); CHECK(PrepareBoth(gate));
        CHECK(!gate.AcknowledgeStarted(Worker::Capture)); // No shared start request exists yet.
        CHECK(gate.GetReport().failure == Failure::InvalidStartAcknowledgement);
        AssertClosed(gate); CHECK(FinishCleanup(gate));
    }
    {
        Gate gate; CHECK(Configure(gate)); CHECK(PrepareBoth(gate)); CHECK(gate.Start(true));
        CHECK(gate.GetReport().captureWorker.startRequested && gate.GetReport().renderWorker.startRequested);
        CHECK(gate.OutputControl().StateValue() == OutputState::Disabled);
        const std::array<std::int16_t, 2> data{900, 0};
        CHECK(!gate.AcceptFakeRenderBuffer(data));
        CHECK(gate.OutputControl().StateValue() == OutputState::Inhibited);
        CHECK(FinishCleanup(gate));
    }
    for (const Worker first : {Worker::Capture, Worker::Render}) {
        Gate gate; CHECK(Configure(gate)); CHECK(PrepareBoth(gate)); CHECK(gate.Start(true));
        const Worker second = first == Worker::Capture ? Worker::Render : Worker::Capture;
        CHECK(gate.AcknowledgeStarted(first));
        CHECK(gate.OutputControl().StateValue() == OutputState::Disabled);
        const auto& pending = second == Worker::Capture ? gate.GetReport().captureWorker : gate.GetReport().renderWorker;
        CHECK(!pending.startAcknowledged);
        CHECK(gate.AcknowledgeStarted(second));
        CHECK(gate.OutputControl().StateValue() == OutputState::Enabled);
        CHECK(gate.StopNormally()); CHECK(FinishCleanup(gate));
    }
    {
        Gate gate; CHECK(Configure(gate)); CHECK(PrepareBoth(gate)); CHECK(gate.Start(true));
        CHECK(gate.AcknowledgeStarted(Worker::Capture));
        CHECK(!gate.AcknowledgeStarted(Worker::Capture));
        CHECK(gate.GetReport().failure == Failure::InvalidStartAcknowledgement);
        CHECK(gate.OutputControl().StateValue() == OutputState::Inhibited);
        AssertClosed(gate); CHECK(FinishCleanup(gate));
    }
    {
        Gate gate; CHECK(Configure(gate)); CHECK(PrepareBoth(gate)); CHECK(gate.Start(true));
        CHECK(gate.AcknowledgeStarted(Worker::Capture));
        CHECK(!gate.AcknowledgeStarted(Worker::Render, false));
        CHECK(gate.GetReport().renderWorker.failed);
        CHECK(gate.GetReport().captureWorker.stopRequested && gate.GetReport().renderWorker.stopRequested);
        AssertClosed(gate); CHECK(FinishCleanup(gate));
    }
    for (const Fault fault : {Fault::CaptureFailure, Fault::RenderFailure}) {
        Gate gate; CHECK(Configure(gate)); CHECK(PrepareBoth(gate)); CHECK(gate.Start(true));
        CHECK(gate.AcknowledgeStarted(Worker::Capture));
        CHECK(gate.NotifyFault(fault));
        CHECK(gate.OutputControl().StateValue() == OutputState::Inhibited);
        CHECK(gate.GetReport().captureWorker.stopRequested && gate.GetReport().renderWorker.stopRequested);
        AssertClosed(gate); CHECK(FinishCleanup(gate));
    }
    {
        Gate gate; CHECK(Configure(gate)); CHECK(PrepareBoth(gate)); CHECK(gate.Start(true));
        CHECK(gate.AcknowledgeStarted(Worker::Capture)); CHECK(gate.Cancel());
        CHECK(!gate.AcknowledgeStarted(Worker::Render));
        CHECK(gate.CurrentState() == State::Stopping);
        AssertClosed(gate); CHECK(FinishCleanup(gate)); CHECK(gate.CurrentState() == State::Cancelled);
    }
    {
        Gate gate; CHECK(ArmAndStart(gate));
        const std::array<std::int16_t, 2> badRight{0, 900};
        CHECK(!gate.AcceptFakeRenderBuffer(badRight));
        CHECK(gate.GetReport().failure == Failure::RenderRightNonZero);
        CHECK(gate.OutputControl().StateValue() == OutputState::Inhibited);
        const std::array<std::int16_t, 2> data{900, 0};
        CHECK(!gate.AcceptFakeRenderBuffer(data));
        CHECK(FinishCleanup(gate));
    }
    {
        Gate gate; CHECK(ArmAndStart(gate));
        CHECK(!gate.AcknowledgeQuiescent(Worker::Capture));
        CHECK(gate.GetReport().failure == Failure::InvalidTransition);
        AssertClosed(gate); CHECK(FinishCleanup(gate));
    }
    {
        Gate gate; CHECK(Configure(gate)); CHECK(PrepareBoth(gate));
        CHECK(!gate.CompleteCleanup(true));
        CHECK(gate.GetReport().failure == Failure::InvalidTransition);
        AssertClosed(gate); CHECK(FinishCleanup(gate));
    }
    return failures == 0;
}

bool TestFaultsCancellationLateEventsAndFreshSession() {
    const std::array<Fault, 10> faults{Fault::CaptureFailure, Fault::RenderFailure,
        Fault::EndpointInvalidation, Fault::FormatMismatch, Fault::RingOverflow,
        Fault::RingUnderrun, Fault::InvalidRouting, Fault::TimestampContinuity,
        Fault::WatchdogTimeout, Fault::PeerShutdown};
    for (const auto fault : faults) {
        Gate gate; CHECK(ArmAndStart(gate));
        CHECK(gate.NotifyFault(fault)); CHECK(gate.CurrentState() == State::Stopping);
        CHECK(gate.OutputControl().StateValue() == OutputState::Inhibited);
        CHECK(gate.GetReport().captureWorker.stopRequested && gate.GetReport().renderWorker.stopRequested);
        const std::array<std::int16_t, 2> data{500, 0};
        CHECK(!gate.AcceptFakeRenderBuffer(data));
        CHECK(FinishCleanup(gate)); CHECK(gate.CurrentState() == State::Failed);
        CHECK(gate.GetReport().peerShutdownStatus ==
              (fault == Fault::PeerShutdown ? PeerShutdown::Failed : PeerShutdown::Quiescent));
        AssertClosed(gate);
    }

    {
        Gate gate; CHECK(Configure(gate)); CHECK(gate.PrepareCapture(Capture(gate)));
        CHECK(gate.Cancel()); CHECK(gate.CurrentState() == State::Stopping);
        CHECK(!gate.OutputControl().AllowsNonSilentPcm());
        CHECK(FinishCleanup(gate)); CHECK(gate.CurrentState() == State::Cancelled);
        CHECK(!gate.NoteLateWorkerEvent());
        CHECK(gate.CurrentState() == State::Cancelled);
        AssertClosed(gate);
    }
    {
        Gate gate; CHECK(ArmAndStart(gate)); CHECK(gate.Cancel());
        const std::array<std::int16_t, 2> data{700, 0};
        CHECK(!gate.AcceptFakeRenderBuffer(data));
        CHECK(FinishCleanup(gate)); CHECK(gate.CurrentState() == State::Cancelled);
        CHECK(!gate.Start(true)); // A cancelled run cannot be restarted.
        CHECK(gate.CurrentState() == State::Cancelled);
    }
    {
        Gate gate; CHECK(ArmAndStart(gate)); CHECK(gate.StopNormally());
        CHECK(gate.AcknowledgeQuiescent(Worker::Capture));
        CHECK(gate.AcknowledgeQuiescent(Worker::Capture)); // Duplicate is idempotent.
        CHECK(!gate.CompleteCleanup(true));
        CHECK(gate.CurrentState() == State::Stopping);
        CHECK(gate.GetReport().failure == Failure::None);
        CHECK(gate.GetReport().resourceCleanupStatus == ResourceCleanup::WaitingForQuiescence);
        CHECK(!gate.OutputControl().AllowsNonSilentPcm());
        CHECK(gate.AcknowledgeQuiescent(Worker::Render));
        CHECK(gate.CompleteCleanup(true)); CHECK(gate.CurrentState() == State::Completed);
    }
    {
        Gate gate; CHECK(ArmAndStart(gate)); CHECK(gate.StopNormally());
        CHECK(!gate.CompleteCleanup(true)); // No stop acknowledgements: still only waiting.
        CHECK(gate.GetReport().failure == Failure::None);
        CHECK(gate.AcknowledgeQuiescent(Worker::Capture));
        CHECK(!gate.CompleteCleanup(true)); // Exactly one owner is quiescent.
        CHECK(gate.GetReport().resourceCleanupStatus == ResourceCleanup::WaitingForQuiescence);
        CHECK(gate.AcknowledgeQuiescent(Worker::Render));
        CHECK(gate.CompleteCleanup(true)); CHECK(gate.CurrentState() == State::Completed);
    }
    {
        Gate gate; CHECK(ArmAndStart(gate)); CHECK(gate.NotifyFault(Fault::WatchdogTimeout));
        CHECK(gate.GetReport().failure == Failure::WatchdogTimeout);
        CHECK(!gate.CompleteCleanup(true)); // Explicit timeout is recorded; peer still owns resources.
        CHECK(gate.CurrentState() == State::Stopping);
        CHECK(gate.GetReport().resourceCleanupStatus == ResourceCleanup::WaitingForQuiescence);
        CHECK(gate.AcknowledgeQuiescent(Worker::Capture));
        CHECK(gate.AcknowledgeQuiescent(Worker::Render));
        CHECK(gate.CompleteCleanup(true)); CHECK(gate.CurrentState() == State::Failed);
        CHECK(gate.GetReport().failure == Failure::WatchdogTimeout);
        AssertClosed(gate);
    }
    {
        Gate gate; CHECK(ArmAndStart(gate)); CHECK(gate.NotifyFault(Fault::CaptureFailure));
        CHECK(!FinishCleanup(gate, false));
        CHECK(gate.CurrentState() == State::Failed);
        CHECK(gate.GetReport().resourceCleanupStatus == ResourceCleanup::Failed);
        CHECK(!gate.OutputControl().AllowsNonSilentPcm());
    }
    {
        Gate neverStarted; CHECK(Configure(neverStarted)); CHECK(neverStarted.Cancel());
        CHECK(neverStarted.CompleteCleanup(true)); CHECK(neverStarted.CurrentState() == State::Cancelled);
        Gate fresh; CHECK(fresh.CurrentState() == State::Idle);
        CHECK(fresh.OutputControl().StateValue() == OutputState::Disabled);
        CHECK(ArmAndStart(fresh)); CHECK(fresh.StopNormally()); CHECK(FinishCleanup(fresh));
        CHECK(fresh.CurrentState() == State::Completed);
        Gate afterFailed; CHECK(Configure(afterFailed)); CHECK(afterFailed.NotifyFault(Fault::CaptureFailure));
        CHECK(afterFailed.CompleteCleanup(true)); CHECK(afterFailed.CurrentState() == State::Failed);
        CHECK(!afterFailed.Start(true));
        Gate afterFailureFresh; CHECK(afterFailureFresh.CurrentState() == State::Idle);
        CHECK(Configure(afterFailureFresh)); CHECK(PrepareBoth(afterFailureFresh)); CHECK(afterFailureFresh.Start(true));
        CHECK(!afterFailureFresh.GetReport().captureWorker.startAcknowledged &&
              !afterFailureFresh.GetReport().renderWorker.startAcknowledged);
        CHECK(afterFailureFresh.OutputControl().StateValue() == OutputState::Disabled);
        CHECK(afterFailureFresh.AcknowledgeStarted(Worker::Render));
        CHECK(afterFailureFresh.OutputControl().StateValue() == OutputState::Disabled);
        CHECK(afterFailureFresh.AcknowledgeStarted(Worker::Capture));
        CHECK(afterFailureFresh.OutputControl().StateValue() == OutputState::Enabled);
        CHECK(afterFailureFresh.StopNormally());
        CHECK(FinishCleanup(afterFailureFresh));
    }
    {
        Gate invalid; CHECK(Configure(invalid));
        CHECK(!invalid.BeginRevalidation());
        CHECK(invalid.GetReport().failure == Failure::InvalidTransition);
        AssertClosed(invalid); CHECK(FinishCleanup(invalid));
    }
    return failures == 0;
}
}

int main() {
    TestValidOrderingsAndNormalShutdown();
    TestReadinessRejection();
    TestSessionReplayAndRevalidation();
    TestRoutingTransportAndOperatorGates();
    TestStartAndFakeOutputInhibit();
    TestFaultsCancellationLateEventsAndFreshSession();
    if (failures != 0) return 1;
    std::puts("Mocked runtime safety gate tests passed; no hardware APIs used.");
    return 0;
}
