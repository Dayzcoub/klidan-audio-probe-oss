#include "../src/probe/capture_runtime_evidence.h"
#include "../src/probe/monitor_rtl_preflight.h"

#include <cstdint>
#include <cstdio>
#include <type_traits>

namespace {
using namespace CaptureRuntimeEvidence;
using namespace RuntimeSafetyGate;
using ReviewStatus = CaptureRuntimeEvidence::HardwareQaStatus;
int failures = 0;
#define CHECK(x) do { if (!(x)) { std::fprintf(stderr, "FAIL %s:%d: %s\n", __FILE__, __LINE__, #x); ++failures; } } while (false)

Facts CleanFacts(const Session& session) {
    Facts facts;
    MarkUnattempted(facts);
    facts.session = session.Reference();
    facts.endpointIdentity = session.EndpointIdentity();
    facts.endpointExplicitlySelected = true;
    facts.sampleRate = 48'000;
    facts.requestedDurationSeconds = 5;
    facts.channels = 2;
    facts.containerBits = facts.validBits = 16;
    facts.finalPeriod100ns = 30'000;
    facts.requestedBufferFrames = facts.actualBufferFrames = 144;
    facts.getDevice.Record(0); facts.activateClient.Record(0); facts.devicePeriod.Record(0);
    facts.formatSupport.Record(0); facts.firstInitialize.Record(0); facts.finalInitialize.Record(0);
    facts.actualGetBufferSize.Record(0); facts.eventCreation.Record(0); facts.setEventHandle.Record(0);
    facts.captureService.Record(0); facts.start.Record(0); facts.stop.Record(0); facts.eventClose.Record(0);
    facts.packetCount = 1'666; facts.frameCount = kExpectedFrames;
    facts.firstDevicePosition = 884'736; facts.firstDevicePositionValid = true;
    facts.firstQpc100ns = 1'000'000; facts.lastQpc100ns = 51'000'000; facts.qpcPositionValid = true;
    facts.durationReached = true;
    facts.continuityValidationAvailable = facts.continuityClean = true;
    facts.timestampValidationAvailable = facts.timestampsClean = true;
    facts.clippingValidationAvailable = facts.noClipping = true;
    facts.clientAcquired = facts.clientReleased = true;
    facts.eventAcquired = facts.eventReleased = true;
    facts.endpointAcquired = facts.endpointReleased = true;
    facts.captureServiceReleased = true;
    return facts;
}

bool TestValidEvidenceAndProjection() {
    Session session("private-capture-endpoint");
    auto facts = CleanFacts(session);
    const auto report = Evaluate(session, facts);
    CHECK(report.evidence == EvidenceStatus::Clean);
    CHECK(report.initialize == Field::Succeeded && report.start == Field::Succeeded);
    CHECK(report.stream == Field::Succeeded && report.integrity == Field::Succeeded);
    CHECK(report.actualGeometryValid && report.outputDisabled);
    CHECK(facts.firstDevicePosition == 884'736); // First device position is an absolute origin, not frame zero.
    const auto observation = ToGateObservation(session, facts, 1);
    CHECK(observation.stream.source == EvidenceSource::MockAdapter);
    CHECK(observation.stream.endpointIdentity == session.EndpointIdentity());
    CHECK(observation.stream.actualBufferFrames == 144 && observation.stream.period100ns == 30'000);
    CHECK(observation.stream.workerReady && observation.continuityClean && observation.timestampsClean);
    CHECK(!observation.stream.session.expired());
    CHECK(report.outputDisabled); // Diagnostic projection does not authorize or start duplex output.
    CHECK(HardwareQaReviewStatus(facts) == ReviewStatus::NotRun);
    CHECK(std::string_view(HardwareQaStatusText(HardwareQaReviewStatus(facts))) == "NOT_RUN");
    return failures == 0;
}

bool TestHardwareQaReviewRequiresObservedCapture() {
    Session session("capture");
    auto facts = CleanFacts(session);
    facts.source = EvidenceSource::Simulated;
    CHECK(HardwareQaReviewStatus(facts) == ReviewStatus::NotRun);
    facts.source = EvidenceSource::RuntimeObserved;
    facts.start.NotCalled();
    CHECK(HardwareQaReviewStatus(facts) == ReviewStatus::NotRun);
    facts.start.Record(static_cast<std::int32_t>(0x80004005U));
    CHECK(HardwareQaReviewStatus(facts) == ReviewStatus::NotRun);
    facts.start.Record(0);
    CHECK(HardwareQaReviewStatus(facts) == ReviewStatus::OperatorReviewRequired);
    CHECK(std::string_view(HardwareQaStatusText(HardwareQaReviewStatus(facts))) == "OPERATOR_REVIEW_REQUIRED");
    facts.source = EvidenceSource::MockAdapter;
    CHECK(HardwareQaReviewStatus(facts) == ReviewStatus::NotRun);
    return failures == 0;
}

bool TestInitializeAndFormatFailures() {
    Session session("capture");
    auto unsupported = CleanFacts(session);
    unsupported.formatSupport.Record(static_cast<std::int32_t>(0x88890008U));
    CHECK(Evaluate(session, unsupported).evidence == EvidenceStatus::Failed);
    auto wrongFormat = CleanFacts(session); wrongFormat.channels = 1;
    CHECK(Evaluate(session, wrongFormat).evidence != EvidenceStatus::Clean);
    auto initializeFailure = CleanFacts(session); initializeFailure.finalInitialize.Record(static_cast<std::int32_t>(0x80004005U));
    CHECK(Evaluate(session, initializeFailure).initialize == Field::Failed);
    CHECK(Evaluate(session, initializeFailure).evidence == EvidenceStatus::Failed);
    auto eventFailure = CleanFacts(session); eventFailure.setEventHandle.Record(static_cast<std::int32_t>(0x80004005U));
    CHECK(Evaluate(session, eventFailure).evidence == EvidenceStatus::Failed);
    auto eventCreationFailure = CleanFacts(session); eventCreationFailure.eventCreation.Record(static_cast<std::int32_t>(0x80004005U));
    eventCreationFailure.setEventHandle.NotCalled();
    CHECK(Evaluate(session, eventCreationFailure).evidence == EvidenceStatus::Failed);
    auto captureServiceFailure = CleanFacts(session); captureServiceFailure.captureService.Record(static_cast<std::int32_t>(0x80004005U));
    CHECK(Evaluate(session, captureServiceFailure).evidence == EvidenceStatus::Failed);
    auto startFailure = CleanFacts(session); startFailure.start.Record(static_cast<std::int32_t>(0x80004005U));
    CHECK(Evaluate(session, startFailure).start == Field::Failed);
    CHECK(Evaluate(session, startFailure).stream == Field::Failed);
    auto unknown = CleanFacts(session); unknown.start.NotCalled();
    CHECK(Evaluate(session, unknown).evidence != EvidenceStatus::Clean);
    CHECK(Evaluate(session, unknown).stream == Field::Unknown);
    auto unknownSupport = CleanFacts(session); unknownSupport.formatSupport.NotCalled();
    CHECK(Evaluate(session, unknownSupport).evidence != EvidenceStatus::Clean);
    auto contradictoryHresult = CleanFacts(session);
    contradictoryHresult.finalInitialize.value = static_cast<std::int32_t>(0x80004005U);
    CHECK(Evaluate(session, contradictoryHresult).evidence == EvidenceStatus::Failed);
    return failures == 0;
}

bool TestAlignmentRetryEvidence() {
    Session session("capture");
    auto retry = CleanFacts(session);
    retry.alignmentRetryAttempted = true;
    retry.firstInitialize.Record(static_cast<std::int32_t>(0x88890019U));
    retry.alignmentGetBufferSize.Record(0);
    retry.alignmentBufferFrames = retry.requestedBufferFrames = retry.actualBufferFrames = 160;
    retry.retryActivateClient.Record(0);
    retry.finalPeriod100ns = 33'333;
    CHECK(Evaluate(session, retry).evidence == EvidenceStatus::Clean);
    CHECK(Evaluate(session, retry).actualGeometryValid);

    auto retryFailed = retry;
    retryFailed.finalInitialize.Record(static_cast<std::int32_t>(0x80004005U));
    CHECK(Evaluate(session, retryFailed).initialize == Field::Failed);
    CHECK(Evaluate(session, retryFailed).evidence == EvidenceStatus::Failed);
    auto alignmentQueryFailed = retry;
    alignmentQueryFailed.alignmentGetBufferSize.Record(static_cast<std::int32_t>(0x80004005U));
    alignmentQueryFailed.finalInitialize.NotCalled();
    CHECK(Evaluate(session, alignmentQueryFailed).evidence == EvidenceStatus::Failed);
    return failures == 0;
}

bool TestInitializeProvenanceCompleteness() {
    Session session("capture");

    auto firstUnknown = CleanFacts(session);
    firstUnknown.firstInitialize.NotCalled();
    CHECK(Evaluate(session, firstUnknown).initialize == Field::Incomplete);
    CHECK(Evaluate(session, firstUnknown).evidence != EvidenceStatus::Clean);

    auto firstFailure = CleanFacts(session);
    firstFailure.firstInitialize.Record(static_cast<std::int32_t>(0x80004005U));
    CHECK(Evaluate(session, firstFailure).initialize == Field::Failed);
    CHECK(Evaluate(session, firstFailure).evidence == EvidenceStatus::Failed);

    auto retryWithFirstSuccess = CleanFacts(session);
    retryWithFirstSuccess.alignmentRetryAttempted = true;
    retryWithFirstSuccess.alignmentGetBufferSize.Record(0);
    retryWithFirstSuccess.alignmentBufferFrames = 144;
    retryWithFirstSuccess.retryActivateClient.Record(0);
    CHECK(Evaluate(session, retryWithFirstSuccess).initialize == Field::Failed);

    auto wrongAlignmentHresult = retryWithFirstSuccess;
    wrongAlignmentHresult.firstInitialize.Record(static_cast<std::int32_t>(0x80004005U));
    CHECK(Evaluate(session, wrongAlignmentHresult).evidence == EvidenceStatus::Failed);

    auto missingAlignmentQuery = retryWithFirstSuccess;
    missingAlignmentQuery.firstInitialize.Record(kBufferSizeNotAligned);
    missingAlignmentQuery.alignmentGetBufferSize.NotCalled();
    CHECK(Evaluate(session, missingAlignmentQuery).initialize == Field::Incomplete);

    auto zeroAlignedFrames = retryWithFirstSuccess;
    zeroAlignedFrames.firstInitialize.Record(kBufferSizeNotAligned);
    zeroAlignedFrames.alignmentBufferFrames = 0;
    CHECK(Evaluate(session, zeroAlignedFrames).initialize == Field::Failed);

    auto retryActivationNotAttempted = retryWithFirstSuccess;
    retryActivationNotAttempted.firstInitialize.Record(kBufferSizeNotAligned);
    retryActivationNotAttempted.retryActivateClient.NotCalled();
    CHECK(Evaluate(session, retryActivationNotAttempted).initialize == Field::Incomplete);

    auto correctRetry = CleanFacts(session);
    correctRetry.alignmentRetryAttempted = true;
    correctRetry.firstInitialize.Record(kBufferSizeNotAligned);
    correctRetry.alignmentGetBufferSize.Record(0);
    correctRetry.alignmentBufferFrames = 160;
    correctRetry.retryActivateClient.Record(0);
    correctRetry.finalInitialize.Record(0);
    correctRetry.finalPeriod100ns = 33'333;
    correctRetry.requestedBufferFrames = correctRetry.actualBufferFrames = 160;
    CHECK(Evaluate(session, correctRetry).evidence == EvidenceStatus::Clean);

    auto wrongGeometry = correctRetry;
    wrongGeometry.actualBufferFrames = 144;
    CHECK(Evaluate(session, wrongGeometry).evidence == EvidenceStatus::Failed);
    auto wrongRequestedGeometry = correctRetry;
    wrongRequestedGeometry.requestedBufferFrames = 159;
    CHECK(Evaluate(session, wrongRequestedGeometry).evidence == EvidenceStatus::Failed);
    return failures == 0;
}

bool TestCaptureProvenanceCompleteness() {
    Session session("capture");
    auto missingPosition = CleanFacts(session);
    missingPosition.firstDevicePositionValid = false;
    CHECK(Evaluate(session, missingPosition).evidence == EvidenceStatus::Incomplete);
    auto missingQpc = CleanFacts(session);
    missingQpc.qpcPositionValid = false;
    CHECK(Evaluate(session, missingQpc).evidence == EvidenceStatus::Incomplete);

    auto noEndpointOwnership = CleanFacts(session);
    noEndpointOwnership.endpointAcquired = false;
    CHECK(Evaluate(session, noEndpointOwnership).evidence == EvidenceStatus::Failed);
    auto noClientOwnership = CleanFacts(session);
    noClientOwnership.clientAcquired = false;
    CHECK(Evaluate(session, noClientOwnership).evidence == EvidenceStatus::Failed);
    auto noEventOwnership = CleanFacts(session);
    noEventOwnership.eventAcquired = false;
    CHECK(Evaluate(session, noEventOwnership).evidence == EvidenceStatus::Failed);

    auto unknownCaptureService = CleanFacts(session);
    unknownCaptureService.captureService.NotCalled();
    CHECK(Evaluate(session, unknownCaptureService).evidence == EvidenceStatus::Incomplete);

    auto endpointApiUnknown = CleanFacts(session);
    endpointApiUnknown.getDevice.NotCalled();
    CHECK(Evaluate(session, endpointApiUnknown).evidence == EvidenceStatus::Incomplete);
    auto apiSuccessWithoutOwnership = CleanFacts(session);
    apiSuccessWithoutOwnership.eventAcquired = false;
    CHECK(Evaluate(session, apiSuccessWithoutOwnership).evidence == EvidenceStatus::Failed);

    auto incompleteCleanup = CleanFacts(session);
    incompleteCleanup.eventClose.NotCalled();
    CHECK(Evaluate(session, incompleteCleanup).cleanup == Field::Incomplete);
    CHECK(Evaluate(session, incompleteCleanup).evidence == EvidenceStatus::Incomplete);
    return failures == 0;
}

bool TestPacketsIntegrityAndCoverage() {
    Session session("capture");
    auto shortRun = CleanFacts(session); shortRun.frameCount = kMinimumFrames - 1;
    CHECK(Evaluate(session, shortRun).evidence != EvidenceStatus::Clean);
    auto exactlyCovered = CleanFacts(session); exactlyCovered.frameCount = kMinimumFrames;
    CHECK(Evaluate(session, exactlyCovered).evidence == EvidenceStatus::Clean);
    auto timestampError = CleanFacts(session); timestampError.timestampErrors = 1; timestampError.timestampsClean = false;
    CHECK(Evaluate(session, timestampError).integrity == Field::Failed);
    auto gap = CleanFacts(session); gap.positionGaps = 1; gap.continuityClean = false;
    CHECK(Evaluate(session, gap).evidence == EvidenceStatus::Failed);
    auto overlap = CleanFacts(session); overlap.positionOverlaps = 1; overlap.continuityClean = false;
    CHECK(Evaluate(session, overlap).evidence == EvidenceStatus::Failed);
    auto discontinuity = CleanFacts(session); discontinuity.discontinuities = 1; discontinuity.continuityClean = false;
    CHECK(Evaluate(session, discontinuity).evidence == EvidenceStatus::Failed);
    auto unavailable = CleanFacts(session); unavailable.unavailableBufferResults = 1;
    CHECK(Evaluate(session, unavailable).evidence == EvidenceStatus::Failed);
    auto invalidated = CleanFacts(session); invalidated.deviceInvalidated = true;
    CHECK(Evaluate(session, invalidated).evidence == EvidenceStatus::Failed);
    auto cancelled = CleanFacts(session); cancelled.cancelled = true;
    CHECK(Evaluate(session, cancelled).evidence == EvidenceStatus::Failed);
    auto cancelledBeforeStart = CleanFacts(session);
    cancelledBeforeStart.start.NotCalled(); cancelledBeforeStart.stop.NotCalled();
    cancelledBeforeStart.packetCount = cancelledBeforeStart.frameCount = 0;
    cancelledBeforeStart.cancelled = true;
    CHECK(Evaluate(session, cancelledBeforeStart).start == Field::NotAttempted);
    CHECK(Evaluate(session, cancelledBeforeStart).evidence == EvidenceStatus::Failed);
    auto watchdog = CleanFacts(session); watchdog.watchdogTimedOut = true;
    CHECK(Evaluate(session, watchdog).evidence == EvidenceStatus::Failed);
    auto stopFailure = CleanFacts(session); stopFailure.stop.Record(static_cast<std::int32_t>(0x80004005U));
    CHECK(Evaluate(session, stopFailure).evidence == EvidenceStatus::Failed);
    auto partialCleanup = CleanFacts(session); partialCleanup.eventReleased = false;
    partialCleanup.eventClose.Record(static_cast<std::int32_t>(0x80004005U));
    CHECK(Evaluate(session, partialCleanup).cleanup == Field::Failed);
    CHECK(Evaluate(session, partialCleanup).evidence != EvidenceStatus::Clean);
    auto partialInitialize = CleanFacts(session);
    partialInitialize.finalInitialize.Record(static_cast<std::int32_t>(0x80004005U));
    partialInitialize.actualGetBufferSize.NotCalled(); partialInitialize.eventCreation.NotCalled();
    partialInitialize.setEventHandle.NotCalled(); partialInitialize.eventClose.NotCalled();
    partialInitialize.clientReleased = true; partialInitialize.eventAcquired = false;
    CHECK(Evaluate(session, partialInitialize).cleanup == Field::Succeeded);
    CHECK(Evaluate(session, partialInitialize).evidence == EvidenceStatus::Failed);
    return failures == 0;
}

bool TestPreflightPacketSemanticsAndSessionBinding() {
    MonitorRtlPreflight::CaptureData data;
    CHECK(data.Configure(128));
    CHECK(data.Append(nullptr, 64, true, false, false, 9'000'000, 50'000'000));
    CHECK(data.SilentPackets() == 1);
    CHECK(data.Append(nullptr, 64, true, false, false, 9'000'064, 50'133'333));
    CHECK(data.PositionGaps() == 0 && data.PositionOverlaps() == 0 && data.TimestampErrors() == 0);

    MonitorRtlPreflight::CaptureData badTimeline;
    CHECK(badTimeline.Configure(128));
    CHECK(badTimeline.Append(nullptr, 64, true, false, false, 1'000, 100'000));
    CHECK(badTimeline.Append(nullptr, 64, true, false, false, 1'064, 99'999));
    CHECK(badTimeline.TimestampErrors() == 1);
    CHECK(!badTimeline.Analyze().timestampsClean);
    MonitorRtlPreflight::CaptureData gap;
    CHECK(gap.Configure(128));
    CHECK(gap.Append(nullptr, 64, true, false, false, 100, 100));
    CHECK(gap.Append(nullptr, 64, true, false, false, 165, 200));
    CHECK(gap.PositionGaps() == 1);
    MonitorRtlPreflight::CaptureData overlap;
    CHECK(overlap.Configure(128));
    CHECK(overlap.Append(nullptr, 64, true, false, false, 100, 100));
    CHECK(overlap.Append(nullptr, 64, true, false, false, 163, 200));
    CHECK(overlap.PositionOverlaps() == 1);

    Session first("capture-a"), second("capture-b");
    auto stale = CleanFacts(first);
    CHECK(Evaluate(second, stale).evidence == EvidenceStatus::Failed);
    const auto projection = ToGateObservation(second, stale, 1);
    CHECK(!projection.stream.workerReady && !projection.stream.initializeSucceeded);
    CHECK(projection.stream.format.encoding == SampleEncoding::Other);
    auto simulated = CleanFacts(first);
    simulated.source = EvidenceSource::Simulated;
    CHECK(ToGateObservation(first, simulated, 2).stream.source == EvidenceSource::Simulated);
    auto wrongFormat = CleanFacts(first);
    wrongFormat.sampleRate = 44'100;
    CHECK(Evaluate(first, wrongFormat).evidence == EvidenceStatus::Failed);
    auto incomplete = CleanFacts(first); incomplete.continuityValidationAvailable = false;
    CHECK(Evaluate(first, incomplete).integrity != Field::Succeeded);
    CHECK(Evaluate(first, incomplete).evidence != EvidenceStatus::Clean);
    return failures == 0;
}

bool TestCaptureLifecycleIsolatedFromDuplex() {
    Lifecycle life;
    CHECK(life.State() == LifecycleState::Idle);
    CHECK(!life.Transition(LifecycleState::Capturing));
    CHECK(life.Transition(LifecycleState::Configured));
    CHECK(!life.Transition(LifecycleState::Capturing)); // Start cannot skip initialization/readiness.
    CHECK(life.Transition(LifecycleState::Initialized));
    CHECK(life.Transition(LifecycleState::CaptureReady));
    CHECK(life.Transition(LifecycleState::Capturing));
    CHECK(!life.Transition(LifecycleState::Capturing)); // Repeated Start rejected.
    CHECK(life.Transition(LifecycleState::Stopping));
    CHECK(life.Transition(LifecycleState::Completed));

    Lifecycle cancelled;
    CHECK(cancelled.Transition(LifecycleState::Configured));
    CHECK(cancelled.Transition(LifecycleState::Cancelled));
    CHECK(!cancelled.Transition(LifecycleState::Capturing));
    Lifecycle cancelledDuring;
    CHECK(cancelledDuring.Transition(LifecycleState::Configured));
    CHECK(cancelledDuring.Transition(LifecycleState::Initialized));
    CHECK(cancelledDuring.Transition(LifecycleState::CaptureReady));
    CHECK(cancelledDuring.Transition(LifecycleState::Capturing));
    CHECK(cancelledDuring.Transition(LifecycleState::Stopping));
    CHECK(cancelledDuring.Transition(LifecycleState::Cancelled));
    static_assert(std::is_same_v<decltype(ToGateObservation(
        std::declval<const Session&>(), std::declval<const Facts&>(), std::uint64_t{})), CaptureObservation>);
    return failures == 0;
}
}

int main() {
    TestValidEvidenceAndProjection();
    TestHardwareQaReviewRequiresObservedCapture();
    TestInitializeAndFormatFailures();
    TestAlignmentRetryEvidence();
    TestInitializeProvenanceCompleteness();
    TestCaptureProvenanceCompleteness();
    TestPacketsIntegrityAndCoverage();
    TestPreflightPacketSemanticsAndSessionBinding();
    TestCaptureLifecycleIsolatedFromDuplex();
    if (failures != 0) return 1;
    std::puts("Capture runtime evidence tests passed");
    return 0;
}
