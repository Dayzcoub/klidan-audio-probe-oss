#include "../src/probe/monitor_rtl_session.h"

#include <algorithm>
#include <array>
#include <cmath>
#include <cstdio>
#include <cstdint>
#include <span>
#include <vector>

namespace {
using namespace MonitorRtlSession;
constexpr std::uint32_t kPacketFrames = 144;
int failures = 0;
#define CHECK(x) do { if (!(x)) { std::fprintf(stderr, "FAIL %s:%d: %s\n", __FILE__, __LINE__, #x); ++failures; } } while (false)

struct Fixture {
    std::vector<std::int16_t> pcm = std::vector<std::int16_t>(static_cast<std::size_t>(kNominalFrames) * 2, 0);
    bool filtered = false;
    bool unrelatedInput2 = false;
    bool reference = true;
    bool returned = true;

    Fixture(bool filter = false, bool unrelated = false, bool withReference = true, bool withReturn = true)
        : filtered(filter), unrelatedInput2(unrelated), reference(withReference), returned(withReturn) {
        AnalogLoopback::RenderSignal generated(1);
        CHECK(generated.Configure(kRate, 1, 0, 5));
        std::vector<std::int16_t> source(static_cast<std::size_t>(kNominalFrames) * 2, 0);
        generated.Fill(source.data(), kNominalFrames, 0);
        std::int32_t previous = 0;
        std::uint32_t random = 0xC001D00DU;
        for (std::uint32_t frame = 0; frame < kNominalFrames; ++frame) {
            const auto left = source[static_cast<std::size_t>(frame) * 2];
            const auto sourceFrame = static_cast<std::int64_t>(frame) - 192;
            std::int32_t right = returned && sourceFrame >= 0
                ? source[static_cast<std::size_t>(sourceFrame) * 2] : 0;
            if (filtered) {
                previous = (previous * 3 + right) / 4;
                right = previous;
            }
            right = right * 3 / 4;
            if (unrelatedInput2) {
                random = random * 1664525U + 1013904223U;
                right = static_cast<std::int32_t>((random >> 16) & 0x1fffU) - 4096;
            }
            pcm[static_cast<std::size_t>(frame) * 2] = reference ? left : 0;
            pcm[static_cast<std::size_t>(frame) * 2 + 1] = static_cast<std::int16_t>(right);
        }
    }

    CapturePacket Packet(std::uint64_t firstFrame, std::uint32_t frames,
                         bool format = true, bool discontinuity = false,
                         bool timestampError = false, bool silent = false) const {
        const auto offset = static_cast<std::size_t>(firstFrame) * 2;
        const auto data = silent ? std::span<const std::int16_t>{}
            : std::span<const std::int16_t>(pcm.data() + offset, static_cast<std::size_t>(frames) * 2);
        return {data, firstFrame, 5'000'000'000ULL + firstFrame * 10'000'000ULL / kRate,
            frames, format, silent, discontinuity, timestampError};
    }
};

SimulatedReadiness Ready(const Coordinator& session) {
    return {session.SimulationSessionId(), true, true, true, true, true, true, true, true};
}

bool Begin(Coordinator& session, const SimulatedReadiness& readiness) {
    return session.Configure(Configuration{}) && session.Prepare(readiness);
}
bool Begin(Coordinator& session) { return Begin(session, Ready(session)); }

bool FeedPacket(Coordinator& session, const Fixture& fixture, std::uint64_t firstFrame,
                std::uint32_t frames, bool simulatePull = true) {
    if (!session.IngestCapturePacket(fixture.Packet(firstFrame, frames))) return false;
    return !simulatePull || session.SimulateRenderPull(frames);
}

bool RunFixture(Coordinator& session, const Fixture& fixture) {
    if (!Begin(session)) return false;
    for (std::uint64_t first = 0; first < kNominalFrames;) {
        const auto count = static_cast<std::uint32_t>(std::min<std::uint64_t>(
            kPacketFrames, kNominalFrames - first));
        if (!FeedPacket(session, fixture, first, count)) return false;
        first += count;
    }
    return session.Finish();
}

bool TestSuccessfulAndFilteredSessions() {
    const Fixture fixture;
    Coordinator session;
    CHECK(Begin(session));
    for (std::uint64_t first = 0; first < kNominalFrames;) {
        const auto count = static_cast<std::uint32_t>(std::min<std::uint64_t>(kPacketFrames, kNominalFrames - first));
        CHECK(FeedPacket(session, fixture, first, count));
        first += count;
    }
    CHECK(session.GetStatus() == Status::Measuring);
    const auto activeSamples = session.FakeRenderSamples();
    CHECK(activeSamples.size() > 288 * 2);
    for (std::size_t frame = 0; frame < activeSamples.size() / 2; ++frame)
        CHECK(activeSamples[frame * 2 + 1] == 0);
    CHECK(activeSamples[0] == 0 && activeSamples[287 * 2 + 1] == 0);
    CHECK(activeSamples[288 * 2] == static_cast<std::int16_t>(std::lrint(
        fixture.pcm[0] * LiveTransport::kOutputGain)));
    CHECK(session.Finish());
    const auto& report = session.GetReport();
    CHECK(report.sessionStatus == Status::Completed);
    CHECK(report.routingIsolation == RoutingStatus::Isolated && report.input2ExcludedByStaging);
    CHECK(report.captureIntegrity == CaptureStatus::Clean);
    CHECK(report.ringTransport == RingStatus::Clean && report.ring.overflowFrames == 0 && report.ring.underrunFrames == 0);
    CHECK(report.analyzerStatus == AnalyzerStatus::Validated);
    CHECK(report.analysis.pairedMarkers >= AnalogLoopback::kMinimumMarkers);
    CHECK(std::abs(report.analysis.medianDelayFrames - 192.0) < 1.0);
    CHECK(report.fakeRenderFrames == kNominalFrames + 288);
    CHECK(report.fakeRenderNonSilentFrames != 0);
    CHECK(report.renderRightAlwaysZero && report.transientPcmErased);
    CHECK(report.ring.framesTransferred == kNominalFrames);
    CHECK(report.ring.renderFramesConsumed == kNominalFrames);
    CHECK(report.ring.occupancyCurrent == 0);
    CHECK(report.simulationStatus == SimulationStatus::SimulatedOnly);
    CHECK(report.physicalRtl == MonitorRtlAnalyzer::PhysicalRtlStatus::NotMeasured);
    CHECK(session.FakeRenderSamples().empty());
    CHECK(session.GetLifecycleState() == MonitorRtlAnalyzer::LifecycleState::Completed);

    const Fixture filtered(true);
    Coordinator filteredSession;
    CHECK(RunFixture(filteredSession, filtered));
    CHECK(filteredSession.GetReport().analyzerStatus == AnalyzerStatus::Validated);
    return failures == 0;
}

bool TestInput2IsolationAndAnalyzerRejection() {
    Fixture unrelated(false, true);
    Coordinator session;
    CHECK(Begin(session));
    for (std::uint64_t first = 0; first < kNominalFrames;) {
        const auto count = static_cast<std::uint32_t>(std::min<std::uint64_t>(kPacketFrames, kNominalFrames - first));
        CHECK(FeedPacket(session, unrelated, first, count));
        first += count;
    }
    const auto routed = session.FakeRenderSamples();
    for (std::size_t i = 1; i < routed.size(); i += 2) CHECK(routed[i] == 0);
    CHECK(!session.Finish());
    const auto& report = session.GetReport();
    CHECK(report.sessionStatus == Status::Failed);
    CHECK(report.failure == Failure::AnalyzerRejected);
    CHECK(report.analyzerStatus == AnalyzerStatus::Rejected);
    CHECK(report.captureIntegrity == CaptureStatus::Clean);
    CHECK(report.ringTransport == RingStatus::Clean);
    CHECK(report.renderRightAlwaysZero && report.transientPcmErased);
    CHECK(report.physicalRtl == MonitorRtlAnalyzer::PhysicalRtlStatus::NotMeasured);

    Fixture markerFixture;
    auto packet = markerFixture.Packet(0, 144);
    std::vector<std::int16_t> injection(288);
    for (std::size_t frame = 0; frame < 144; ++frame) {
        injection[frame * 2] = markerFixture.pcm[frame * 2];
        injection[frame * 2 + 1] = 32000;
    }
    packet.interleavedPcm = injection;
    Coordinator injectionProbe;
    CHECK(Begin(injectionProbe));
    for (std::uint64_t first = 0; first < 432; first += 144) {
        std::vector<std::int16_t> injection(288);
        for (std::size_t frame = 0; frame < 144; ++frame) {
            injection[frame * 2] = markerFixture.pcm[(first + frame) * 2];
            injection[frame * 2 + 1] = 32000;
        }
        packet = markerFixture.Packet(first, 144);
        packet.interleavedPcm = injection;
        CHECK(injectionProbe.IngestCapturePacket(packet));
        CHECK(injectionProbe.SimulateRenderPull(144));
    }
    const auto injectionOutput = injectionProbe.FakeRenderSamples();
    CHECK(std::all_of(injectionOutput.begin(), injectionOutput.end(), [](auto sample) { return sample == 0; }));
    injectionProbe.Cancel();
    CHECK(injectionProbe.GetReport().outputSilencedOnFailure);
    return failures == 0;
}

bool TestCaptureFailuresAndNoLeak() {
    const Fixture fixture;
    const auto expectPacketFailure = [&](CapturePacket packet, Failure failure) {
        Coordinator session;
        CHECK(Begin(session));
        CHECK(!session.IngestCapturePacket(packet));
        CHECK(session.GetStatus() == Status::Failed);
        CHECK(session.GetReport().failure == failure);
        CHECK(session.GetReport().outputSilencedOnFailure);
        CHECK(session.GetReport().transientPcmErased);
        CHECK(session.RingOccupancy() == 0);
        CHECK(session.FakeRenderSamples().empty());
        CHECK(!session.SimulateRenderPull(144));
    };
    auto packet = fixture.Packet(0, 144, false);
    expectPacketFailure(packet, Failure::InvalidFormat);
    packet = fixture.Packet(0, 144);
    packet.interleavedPcm = std::span<const std::int16_t>(fixture.pcm.data(), 10);
    expectPacketFailure(packet, Failure::InvalidSize);
    packet = fixture.Packet(0, 145);
    expectPacketFailure(packet, Failure::InvalidSize);
    packet = fixture.Packet(0, 144, true, true);
    expectPacketFailure(packet, Failure::Discontinuity);
    packet = fixture.Packet(0, 144, true, false, true);
    expectPacketFailure(packet, Failure::InvalidTimestamp);
    packet = fixture.Packet(0, 144);
    packet.firstFrame = 1;
    expectPacketFailure(packet, Failure::InvalidFramePosition);

    Coordinator partial;
    CHECK(Begin(partial));
    CHECK(FeedPacket(partial, fixture, 0, 144));
    auto clipped = fixture.Packet(144, 144);
    std::vector<std::int16_t> clippedSamples(clipped.interleavedPcm.begin(), clipped.interleavedPcm.end());
    clippedSamples[0] = INT16_MAX;
    clipped.interleavedPcm = clippedSamples;
    CHECK(!partial.IngestCapturePacket(clipped));
    CHECK(partial.GetReport().ring.framesTransferred == 144);
    CHECK(partial.GetReport().failure == Failure::Clipping);
    CHECK(partial.GetReport().outputSilencedOnFailure && partial.GetReport().transientPcmErased);
    CHECK(partial.FakeRenderSamples().empty());

    Coordinator noReference;
    Fixture referenceMissing(false, false, false, true);
    CHECK(!RunFixture(noReference, referenceMissing));
    CHECK(noReference.GetReport().analyzerStatus == AnalyzerStatus::Rejected);
    Coordinator noReturn;
    Fixture returnMissing(false, false, true, false);
    CHECK(!RunFixture(noReturn, returnMissing));
    CHECK(noReturn.GetReport().analyzerStatus == AnalyzerStatus::Rejected);
    return failures == 0;
}

bool TestRingFailuresAndLifecycle() {
    const Fixture fixture;
    Coordinator overflow;
    CHECK(Begin(overflow));
    for (std::uint64_t first = 0; first <= 1152; first += 144) {
        if (!overflow.IngestCapturePacket(fixture.Packet(first, 144))) break;
    }
    CHECK(overflow.GetStatus() == Status::Failed);
    CHECK(overflow.GetReport().failure == Failure::RingOverflow);
    CHECK(overflow.GetReport().ring.overflowFrames > 0);
    CHECK(overflow.GetReport().ringTransport == RingStatus::Failed);
    CHECK(overflow.GetReport().transientPcmErased);

    Coordinator underrun;
    CHECK(Begin(underrun));
    CHECK(FeedPacket(underrun, fixture, 0, 144));
    CHECK(FeedPacket(underrun, fixture, 144, 144));
    CHECK(FeedPacket(underrun, fixture, 288, 144));
    CHECK(underrun.SimulateRenderPull(144));
    CHECK(underrun.SimulateRenderPull(144));
    CHECK(!underrun.SimulateRenderPull(144));
    CHECK(underrun.GetReport().failure == Failure::RenderUnderrun);

    Coordinator noRevalidation;
    auto readiness = Ready(noRevalidation); readiness.simulatedRenderSilent = false;
    CHECK(noRevalidation.Configure(Configuration{}));
    CHECK(!noRevalidation.Prepare(readiness));
    CHECK(noRevalidation.GetReport().failure == Failure::RevalidationRejected);
    CHECK(noRevalidation.GetLifecycleState() == MonitorRtlAnalyzer::LifecycleState::Failed);

    Coordinator noApprovals;
    CHECK(noApprovals.Configure(Configuration{}));
    readiness = Ready(noApprovals); readiness.explicitlyInjectedByHarness = false;
    CHECK(!noApprovals.Prepare(readiness));
    CHECK(noApprovals.GetReport().failure == Failure::MissingSimulationInput);

    Coordinator repeated;
    CHECK(Begin(repeated));
    CHECK(!repeated.Prepare(Ready(repeated)));
    CHECK(repeated.GetStatus() == Status::Failed);
    CHECK(!repeated.IngestCapturePacket(fixture.Packet(0, 144)));

    Coordinator cancelledBefore;
    CHECK(cancelledBefore.Configure(Configuration{}));
    CHECK(cancelledBefore.Cancel());
    CHECK(cancelledBefore.GetStatus() == Status::Cancelled);
    CHECK(cancelledBefore.GetReport().transientPcmErased);

    Coordinator cancelledDuring;
    CHECK(Begin(cancelledDuring));
    CHECK(FeedPacket(cancelledDuring, fixture, 0, 144));
    CHECK(cancelledDuring.Cancel());
    CHECK(cancelledDuring.GetStatus() == Status::Cancelled);
    CHECK(cancelledDuring.GetReport().outputSilencedOnFailure);
    CHECK(!cancelledDuring.IngestCapturePacket(fixture.Packet(144, 144)));

    MonitorRtlAnalyzer::Lifecycle stale;
    CHECK(stale.BeginCapturePreflight() && stale.BeginValidation() && stale.Arm(true));
    MonitorRtlAnalyzer::Lifecycle fresh;
    MonitorRtlAnalyzer::Revalidation approvals{true, true, true, true, true, true};
    CHECK(!fresh.RevalidateForThisRun(approvals));
    CHECK(fresh.State() == MonitorRtlAnalyzer::LifecycleState::Failed);
    CHECK(!fresh.BeginMeasurement());

    Coordinator freshSecondSession;
    CHECK(Begin(freshSecondSession));
    CHECK(freshSecondSession.RingOccupancy() == 0);
    CHECK(FeedPacket(freshSecondSession, fixture, 0, 144));
    CHECK(freshSecondSession.GetReport().ring.framesTransferred == 144);
    freshSecondSession.Cancel();

    Coordinator priorSession;
    CHECK(priorSession.Configure(Configuration{}));
    const auto priorReadiness = Ready(priorSession);
    CHECK(priorSession.Prepare(priorReadiness));
    priorSession.Cancel();
    Coordinator laterSession;
    CHECK(laterSession.Configure(Configuration{}));
    CHECK(!laterSession.Prepare(priorReadiness));
    CHECK(laterSession.GetReport().failure == Failure::StaleSimulationInput);

    LiveTransport::SpscPcm16StereoRing scrubbedRing;
    CHECK(scrubbedRing.Configure(4, 4, kRate));
    const std::array<std::int16_t, 8> privateFrames{1234, 0, -2345, 0, 4567, 0, -6789, 0};
    CHECK(scrubbedRing.Push(privateFrames.data(), 4, false));
    scrubbedRing.EraseAfterStop(); // Post-stop only; no producer/consumer is active in this fixture.
    CHECK(scrubbedRing.GetStats().occupancyCurrent == 0);
    CHECK(scrubbedRing.GetStats().framesTransferred == 0);
    std::array<std::int16_t, 8> erasedOutput{};
    CHECK(scrubbedRing.Pull(erasedOutput.data(), 4).prefillSilence);
    CHECK(std::all_of(erasedOutput.begin(), erasedOutput.end(), [](auto sample) { return sample == 0; }));
    return failures == 0;
}

bool TestInvalidConfigurationAndGap() {
    Coordinator invalid;
    auto config = Configuration{}; config.nominalFrames -= 144;
    CHECK(!invalid.Configure(config));
    CHECK(invalid.GetReport().failure == Failure::InvalidConfiguration);
    CHECK(!invalid.Configure(Configuration{})); // A failed coordinator cannot be reused.

    const Fixture fixture;
    Coordinator gap;
    CHECK(Begin(gap));
    CHECK(FeedPacket(gap, fixture, 0, 144));
    CHECK(!FeedPacket(gap, fixture, 288, 144));
    CHECK(gap.GetReport().failure == Failure::InvalidFramePosition);
    CHECK(gap.GetReport().outputSilencedOnFailure);
    return failures == 0;
}

bool TestCleanupOnPartialConfigurationAndDestruction() {
    Coordinator partialConfiguration;
    auto invalidConfig = Configuration{};
    invalidConfig.rate = 44'100;
    CHECK(!partialConfiguration.Configure(invalidConfig));
    CHECK(partialConfiguration.GetReport().sessionStatus == Status::Failed);
    CHECK(partialConfiguration.GetReport().transientPcmErased);
    CHECK(partialConfiguration.RingOccupancy() == 0);

    // Exercise destructor cleanup with live transient PCM and a configured Ring.
    {
        Coordinator destroyedAfterPartialRun;
        const Fixture fixture;
        CHECK(Begin(destroyedAfterPartialRun));
        CHECK(FeedPacket(destroyedAfterPartialRun, fixture, 0, 144));
        CHECK(!destroyedAfterPartialRun.GetReport().transientPcmErased);
    }
    return failures == 0;
}
}

int main() {
    TestSuccessfulAndFilteredSessions();
    TestInput2IsolationAndAnalyzerRejection();
    TestCaptureFailuresAndNoLeak();
    TestRingFailuresAndLifecycle();
    TestInvalidConfigurationAndGap();
    TestCleanupOnPartialConfigurationAndDestruction();
    if (failures != 0) return 1;
    std::puts("Disarmed synthetic monitor RTL session tests passed.");
    return 0;
}
