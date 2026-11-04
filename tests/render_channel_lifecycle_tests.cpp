#include "../src/probe/probe_math.h"
#include "../src/probe/render_channel_lifecycle.h"
#include "../src/probe/render_channel_report.h"

#ifdef NDEBUG
#undef NDEBUG
#endif
#include <cassert>
#include <string>

namespace {

void PairSuccessfulEvent(bool releaseSucceeds = true) {
    RenderChannelTest::EventProtocol protocol;
    assert(protocol.BeginEvent());
    assert(protocol.RequestBuffer());
    assert(protocol.BufferResult(true));
    assert(protocol.ReleaseResult(releaseSucceeds));
    assert(protocol.EndEvent());
    assert(protocol.Acquisitions() == 1 && protocol.Releases() == 1);
    assert(protocol.CleanPairs());
}

void TestBoundedCompletionAndFinalSilence() {
    RenderChannelTest::EventProtocol protocol;
    assert(protocol.BeginEvent());
    assert(protocol.RequestBuffer());
    assert(protocol.BufferResult(true));
    assert(protocol.ReleaseResult(true));
    assert(protocol.MarkFinalSilenceSubmitted());
    assert(protocol.EndEvent());
    protocol.Finish();
    assert(protocol.FinalSilenceSubmitted());
    assert(protocol.CleanPairs());
    assert(!protocol.RequestBuffer());
    assert(protocol.Requests() == 1);

    assert(ProbeMath::ClassifyStream(true, true, true, true, 1, 48'000,
        true, false, false, true) == ProbeMath::StreamStatus::StreamCompleted);
    RenderChannelReport::Report report;
    report.finalSilenceSubmitted = protocol.FinalSilenceSubmitted();
    report.lifecycleStatus = "STREAM_COMPLETED";
    report.startHresult = "0x00000000";
    report.stopHresult = "0x00000000";
    const auto json = RenderChannelReport::ToJson(report, "2026-10-30T12:00:00Z",
        "12345678-1234-4234-8234-123456789abc", "0.3.0");
    assert(json.find("\"finalSilenceSubmitted\":true") != std::string::npos);
    assert(json.find("\"lifecycleStatus\":\"STREAM_COMPLETED\"") != std::string::npos);
    assert(json.find("\"startHresult\":\"0x00000000\"") != std::string::npos);
    assert(json.find("\"stopHresult\":\"0x00000000\"") != std::string::npos);
}

void TestCancellationAndDeviceInvalidationStopWithoutAnotherRequest() {
    for (const bool deviceLost : {false, true}) {
        RenderChannelTest::EventProtocol protocol;
        assert(protocol.BeginEvent());
        assert(protocol.RequestBuffer());
        assert(protocol.BufferResult(true));
        assert(protocol.ReleaseResult(true));
        assert(protocol.EndEvent());
        protocol.Finish();
        const auto requestsAtStop = protocol.Requests();
        assert(!protocol.RequestBuffer());
        assert(protocol.Requests() == requestsAtStop);

        RenderChannelReport::Report report;
        report.cancelled = !deviceLost;
        report.deviceLost = deviceLost;
        report.finalSilenceSubmitted = protocol.FinalSilenceSubmitted();
        report.lifecycleStatus = "STREAM_FAILED";
        const auto json = RenderChannelReport::ToJson(report, "2026-10-30T12:00:00Z",
            "12345678-1234-4234-8234-123456789abd", "0.3.0");
        assert(json.find(deviceLost ? "\"deviceLost\":true" : "\"cancelled\":true") != std::string::npos);
        assert(json.find("\"finalSilenceSubmitted\":false") != std::string::npos);
        assert(json.find("\"lifecycleStatus\":\"STREAM_FAILED\"") != std::string::npos);
    }
    assert(ProbeMath::ClassifyStream(true, true, true, false, 1, 48'000,
        true, false, false, true) == ProbeMath::StreamStatus::StreamFailed);
}

void TestOrdinaryCompletionWithoutTerminalSilenceIsWarning() {
    RenderChannelTest::EventProtocol protocol;
    PairSuccessfulEvent();
    protocol.Finish();
    assert(!protocol.FinalSilenceSubmitted());
    assert(ProbeMath::ClassifyStream(true, true, true, true, 1, 48'000,
        true, true, false, true) == ProbeMath::StreamStatus::StreamCompletedWithWarnings);
    RenderChannelReport::Report report;
    report.finalSilenceSubmitted = protocol.FinalSilenceSubmitted();
    report.lifecycleStatus = "STREAM_COMPLETED_WITH_WARNINGS";
    const auto json = RenderChannelReport::ToJson(report, "2026-10-30T12:00:00Z",
        "12345678-1234-4234-8234-123456789abe", "0.3.0");
    assert(json.find("\"finalSilenceSubmitted\":false") != std::string::npos);
    assert(json.find("\"lifecycleStatus\":\"STREAM_COMPLETED_WITH_WARNINGS\"") != std::string::npos);
}

void TestGetBufferFailureHasNoRelease() {
    RenderChannelTest::EventProtocol protocol;
    assert(protocol.BeginEvent());
    assert(protocol.RequestBuffer());
    assert(protocol.BufferResult(false));
    assert(protocol.EndEvent());
    protocol.Finish();
    assert(protocol.Requests() == 1);
    assert(protocol.Acquisitions() == 0);
    assert(protocol.Releases() == 0);
    assert(protocol.AcquisitionFailures() == 1);
    assert(protocol.CleanPairs());
    assert(ProbeMath::ClassifyStream(true, true, true, true, 1, 0,
        false, false, true, true) == ProbeMath::StreamStatus::StreamFailed);
    RenderChannelReport::Report report;
    report.lifecycleStatus = "STREAM_FAILED";
    report.streamApiFailures = 1;
    report.errorStage = "render GetBuffer";
    report.errorHresult = "0x8889000A";
    const auto json = RenderChannelReport::ToJson(report, "2026-10-30T12:00:00Z",
        "12345678-1234-4234-8234-123456789ab0", "0.3.0");
    assert(json.find("\"streamApiFailures\":1") != std::string::npos);
    assert(json.find("\"errorStage\":\"render GetBuffer\"") != std::string::npos);
    assert(json.find("\"errorHresult\":\"0x8889000A\"") != std::string::npos);
}

void TestReleaseBufferFailureStillPairsAcquisition() {
    RenderChannelTest::EventProtocol protocol;
    assert(protocol.BeginEvent());
    assert(protocol.RequestBuffer());
    assert(protocol.BufferResult(true));
    assert(protocol.ReleaseResult(false));
    assert(protocol.EndEvent());
    protocol.Finish();
    assert(protocol.Acquisitions() == 1 && protocol.Releases() == 1);
    assert(protocol.ReleaseFailures() == 1);
    assert(protocol.CleanPairs());
    assert(ProbeMath::ClassifyStream(true, true, true, true, 1, 48'000,
        true, false, true, true) == ProbeMath::StreamStatus::StreamFailed);
    RenderChannelReport::Report report;
    report.lifecycleStatus = "STREAM_FAILED";
    report.streamApiFailures = 1;
    report.startHresult = "0x00000000";
    report.stopHresult = "0x00000000";
    report.errorStage = "render ReleaseBuffer(SILENT)";
    report.errorHresult = "0x88890001";
    const auto json = RenderChannelReport::ToJson(report, "2026-10-30T12:00:00Z",
        "12345678-1234-4234-8234-123456789ab1", "0.3.0");
    assert(json.find("\"lifecycleStatus\":\"STREAM_FAILED\"") != std::string::npos);
    assert(json.find("\"startHresult\":\"0x00000000\"") != std::string::npos);
    assert(json.find("\"stopHresult\":\"0x00000000\"") != std::string::npos);
    assert(json.find("\"errorHresult\":\"0x88890001\"") != std::string::npos);
}

void TestFinalSilenceWindowBoundaries() {
    assert(RenderChannelTest::ShouldSilenceForRemainingTime(0.0, 3.0));
    assert(RenderChannelTest::ShouldSilenceForRemainingTime(3.0, 3.0));
    assert(!RenderChannelTest::ShouldSilenceForRemainingTime(3.01, 3.0));
    assert(!RenderChannelTest::ShouldSilenceForRemainingTime(-0.01, 3.0));
    assert(!RenderChannelTest::ShouldSilenceForRemainingTime(1.0, 0.0));
}

} // namespace

int main() {
    TestBoundedCompletionAndFinalSilence();
    TestCancellationAndDeviceInvalidationStopWithoutAnotherRequest();
    TestOrdinaryCompletionWithoutTerminalSilenceIsWarning();
    TestGetBufferFailureHasNoRelease();
    TestReleaseBufferFailureStillPairsAcquisition();
    TestFinalSilenceWindowBoundaries();
}
