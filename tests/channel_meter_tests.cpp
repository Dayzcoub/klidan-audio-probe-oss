#include "../src/probe/channel_meter.h"
#include "../src/probe/probe_math.h"

#include <array>
#include <atomic>
#include <cmath>
#include <cstdio>
#include <fstream>
#include <string>
#include <string_view>
#include <thread>

namespace {
using namespace ChannelMeter;
int failures = 0;
#define CHECK(condition) do { if (!(condition)) { std::fprintf(stderr, "FAIL %s:%d: %s\n", __FILE__, __LINE__, #condition); ++failures; } } while (false)
bool Near(double actual, double expected, double tolerance = 0.02) {
    return std::isfinite(actual) && std::abs(actual - expected) <= tolerance;
}

bool TestSilenceNullInputAndConfiguration() {
    Meter meter;
    CHECK(!meter.Configure(0, 48'000, 100));
    CHECK(!meter.Configure(33, 48'000, 100));
    CHECK(!meter.Configure(2, 0, 100));
    CHECK(!meter.Configure(2, 48'000, 0));
    CHECK(meter.Configure(2, 48'000, 100));
    CHECK(meter.Process(nullptr, 4'800, true));
    Snapshot snapshot;
    CHECK(meter.TrySnapshot(snapshot));
    CHECK(snapshot.analyzedFrames == 4'800 && snapshot.channelCount == 2);
    CHECK(snapshot.total[0].analyzedSamples == 4'800 && snapshot.total[1].analyzedSamples == 4'800);
    CHECK(IsSilent(snapshot.total[0]) && IsSilent(snapshot.total[1]));
    CHECK(snapshot.total[0].peakMagnitude == 0 && snapshot.total[0].squareSum == 0);
    CHECK(snapshot.completedWindowIndex == 1 && snapshot.framesInCurrentWindow == 0);
    CHECK(!meter.Process(nullptr, 1, false));
    CHECK(meter.TrySnapshot(snapshot) && snapshot.invalidInput && snapshot.analyzedFrames == 4'800);
    CHECK(!meter.TrySnapshot(snapshot, 0));
    return failures == 0;
}

bool TestMonoFullScaleAndClipping() {
    Meter meter;
    CHECK(meter.Configure(1, 1'000, 100));
    const std::int16_t samples[]{INT16_MAX, INT16_MIN, 0};
    CHECK(meter.Process(samples, 3, false));
    Snapshot snapshot;
    CHECK(meter.TrySnapshot(snapshot));
    const auto& channel = snapshot.total[0];
    CHECK(channel.analyzedSamples == 3 && channel.nonzeroSamples == 2);
    CHECK(channel.peakMagnitude == 32'768 && channel.peakHoldMagnitude == 32'768);
    CHECK(channel.clippedSamples == 2 && !IsSilent(channel));
    CHECK(Near(Dbfs(channel.peakMagnitude), 0.0));
    const double expectedRms = 10.0 * std::log10(
        (static_cast<double>(INT16_MAX) * INT16_MAX + 32768.0 * 32768.0) / 3.0 /
        (32768.0 * 32768.0));
    CHECK(Near(RmsDbfs(channel), expectedRms));
    return failures == 0;
}

bool TestStereoIndependenceAndPartialWindows() {
    Meter meter;
    CHECK(meter.Configure(2, 1'000, 10));
    const std::int16_t first[]{16'384, 8'192, 0, 0, 8'192, 4'096, 0, 0};
    CHECK(meter.Process(first, 4, false));
    Snapshot snapshot;
    CHECK(meter.TrySnapshot(snapshot));
    CHECK(snapshot.framesInCurrentWindow == 4 && snapshot.completedWindowIndex == 0);
    CHECK(snapshot.currentWindow[0].analyzedSamples == 4 && snapshot.currentWindow[1].analyzedSamples == 4);
    CHECK(Near(Dbfs(snapshot.currentWindow[0].peakMagnitude), -6.0206));
    CHECK(Near(Dbfs(snapshot.currentWindow[1].peakMagnitude), -12.0412));
    const std::int16_t second[]{0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0};
    CHECK(meter.Process(second, 6, false));
    CHECK(meter.TrySnapshot(snapshot));
    CHECK(snapshot.completedWindowIndex == 1 && snapshot.lastCompletedWindowFrames == 10);
    CHECK(snapshot.lastCompletedWindow[0].analyzedSamples == 10);
    CHECK(snapshot.framesInCurrentWindow == 0);
    const std::int16_t partial[]{0, 0, 0, 0, 0, 0};
    CHECK(meter.Process(partial, 3, false));
    CHECK(meter.TrySnapshot(snapshot));
    CHECK(snapshot.framesInCurrentWindow == 3 && snapshot.currentWindow[0].analyzedSamples == 3);
    CHECK(snapshot.total[0].analyzedSamples == 13 && snapshot.total[1].analyzedSamples == 13);
    CHECK(snapshot.total[0].peakHoldMagnitude == 16'384 && snapshot.total[1].peakHoldMagnitude == 8'192);
    return failures == 0;
}

bool TestLatestCompletedWindows() {
    Meter meter;
    CHECK(meter.Configure(2, 1'000, 4));
    const std::int16_t firstHalf[]{16'000, 0, 16'000, 0};
    CHECK(meter.Process(firstHalf, 2, false));
    Snapshot snapshot;
    CHECK(meter.TrySnapshot(snapshot));
    CHECK(!snapshot.hasCompletedWindow);
    CHECK(LatestCompletedWindow(snapshot, 0) == nullptr);

    const std::int16_t secondHalf[]{0, 8'000, 0, 8'000};
    CHECK(meter.Process(secondHalf, 2, false));
    CHECK(meter.TrySnapshot(snapshot));
    const auto* left = LatestCompletedWindow(snapshot, 0);
    const auto* right = LatestCompletedWindow(snapshot, 1);
    CHECK(left != nullptr && right != nullptr);
    if (left != nullptr && right != nullptr) {
        CHECK(left->peakMagnitude == 16'000 && right->peakMagnitude == 8'000);
        CHECK(left->analyzedSamples == 4 && right->analyzedSamples == 4);
        CHECK(!IsSilent(*left) && !IsSilent(*right));
    }
    CHECK(snapshot.total[0].peakHoldMagnitude == 16'000);
    CHECK(snapshot.total[1].peakHoldMagnitude == 8'000);

    CHECK(meter.Process(nullptr, 4, true));
    CHECK(meter.TrySnapshot(snapshot));
    left = LatestCompletedWindow(snapshot, 0);
    right = LatestCompletedWindow(snapshot, 1);
    CHECK(left != nullptr && right != nullptr);
    if (left != nullptr && right != nullptr) {
        CHECK(left->peakMagnitude == 0 && right->peakMagnitude == 0);
        CHECK(std::isinf(Dbfs(left->peakMagnitude)) && Dbfs(left->peakMagnitude) < 0);
        CHECK(std::isinf(RmsDbfs(*left)) && RmsDbfs(*left) < 0);
        CHECK(IsSilent(*left) && IsSilent(*right));
    }
    CHECK(snapshot.total[0].peakHoldMagnitude == 16'000);
    CHECK(snapshot.total[1].peakHoldMagnitude == 8'000);
    return failures == 0;
}

bool TestSnapshotConsistency() {
    Meter meter;
    CHECK(meter.Configure(2, 48'000, 1));
    std::atomic<bool> done{false};
    std::atomic<bool> consistent{true};
    const std::int16_t samples[]{1, -1, 2, -2, 3, -3, 4, -4};
    std::thread writer([&] {
        for (int i = 0; i < 2'000; ++i) if (!meter.Process(samples, 4, false)) consistent.store(false);
        done.store(true, std::memory_order_release);
    });
    Snapshot snapshot;
    do {
        if (meter.TrySnapshot(snapshot)) {
            if ((snapshot.sequence & 1U) != 0 ||
                snapshot.total[0].analyzedSamples != snapshot.analyzedFrames ||
                snapshot.total[1].analyzedSamples != snapshot.analyzedFrames ||
                snapshot.currentWindow[0].analyzedSamples != snapshot.framesInCurrentWindow ||
                snapshot.currentWindow[1].analyzedSamples != snapshot.framesInCurrentWindow)
                consistent.store(false);
        }
    } while (!done.load(std::memory_order_acquire));
    writer.join();
    CHECK(consistent.load());
    CHECK(meter.TrySnapshot(snapshot));
    CHECK(snapshot.analyzedFrames == 8'000 && snapshot.total[0].analyzedSamples == 8'000);
    CHECK(snapshot.lastCompletedWindowFrames == 48 && snapshot.completedWindowIndex > 0);
    return failures == 0;
}

bool TestRunClassificationAndJson() {
    CHECK(ClassifyRun(true, false, false, false, false, false) == RunStatus::Completed);
    CHECK(ClassifyRun(false, true, false, false, false, false) == RunStatus::Cancelled);
    CHECK(ClassifyRun(false, false, true, false, false, false) == RunStatus::DeviceLost);
    CHECK(ClassifyRun(false, false, false, true, false, false) == RunStatus::Unsupported);
    CHECK(ClassifyRun(false, false, false, false, true, false) == RunStatus::Failed);
    CHECK(ClassifyRun(false, false, false, false, false, true) == RunStatus::Failed);
    CHECK(ClassifyRun(false, false, false, false, false, false) == RunStatus::Incomplete);
    const auto completedWithWarnings = ProbeMath::ClassifyStream(true, true, true, true, 4, 47'520,
        true, true, false, true);
    const auto warningQuality = ProbeMath::ClassifyStreamQuality(true, true, true, 1, 0, 1,
        0, 0, 0, 0, 480);
    CHECK(completedWithWarnings == ProbeMath::StreamStatus::StreamCompletedWithWarnings);
    CHECK(warningQuality == ProbeMath::StreamQuality::AnomaliesPresent);

    Meter meter;
    CHECK(meter.Configure(2, 48'000, 100));
    const std::int16_t input[]{12'000, -24'000, 0, 0};
    CHECK(meter.Process(input, 2, false));
    Snapshot snapshot;
    CHECK(meter.TrySnapshot(snapshot));
    Report report;
    report.meter = snapshot;
    report.status = RunStatus::Completed;
    report.requestedDurationSeconds = 1;
    report.elapsedMilliseconds = 1'000;
    report.cleanupStatus = "RELEASE_ATTEMPTED";
    report.meterAnalysisAttempted = true;
    report.meterAnalysisStatus = "ANALYZED";
    report.durationReached = true;
    report.captureFrames = 47'520;
    report.captureFrameCoveragePercent = 99.0;
    report.captureDiscontinuities = 1;
    report.captureTimestampErrors = 1;
    report.unavailableCaptureBuffers = 0;
    report.watchdogTimedOut = false;
    report.cancelled = false;
    report.captureStreamStatus = "STREAM_COMPLETED_WITH_WARNINGS";
    report.captureStreamQuality = "ANOMALIES_PRESENT";
    report.deviceMixFormat.available = true;
    report.deviceMixFormat.sampleRateHz = 48'000;
    report.deviceMixFormat.reportedChannels = 2;
    report.deviceMixFormat.encoding = FormatTopology::Encoding::Pcm16;
    report.deviceMixFormat.channelMaskAvailable = true;
    report.deviceMixFormat.channelMask = 3;
    report.deviceMixFormat.labelCount = 2;
    report.deviceMixFormat.channelLabels[0] = SpeakerLabel::FrontLeft;
    report.deviceMixFormat.channelLabels[1] = SpeakerLabel::FrontRight;
    report.deviceMixFormat.channelLabelKnown[0] = report.deviceMixFormat.channelLabelKnown[1] = true;
    report.selectedTestFormat = report.deviceMixFormat;
    const auto json = ToJson(report, "2026-10-30T12:00:00Z",
        "12345678-1234-4234-8234-123456789abc", "0.3.0");
    CHECK(json.find("\"schemaVersion\":1") != std::string::npos);
    CHECK(json.find("\"topology\":") < json.find("\"observed\":") );
    CHECK(json.find("\"physicalConnectorMappings\":\"UNKNOWN\"") != std::string::npos);
    CHECK(json.find("\"labelKnown\":true") != std::string::npos);
    CHECK(json.find("\"encoding\":\"PCM16\"") != std::string::npos);
    CHECK(json.find("\"peakHoldDbfs\"") != std::string::npos);
    CHECK(json.find("\"clippedSamples\"") != std::string::npos);
    CHECK(json.find("\"analyzedFrames\":2") != std::string::npos);
    CHECK(json.find("\"lastCompletedWindow\":null") != std::string::npos);
    CHECK(json.find("\"status\":\"COMPLETED\"") != std::string::npos);
    CHECK(json.find("\"captureStreamStatus\":\"STREAM_COMPLETED_WITH_WARNINGS\"") != std::string::npos);
    CHECK(json.find("\"captureStreamQuality\":\"ANOMALIES_PRESENT\"") != std::string::npos);
    CHECK(json.find("\"captureTimestampErrors\":1") != std::string::npos);
    CHECK(json.find("\"cancelled\":false") != std::string::npos);
    CHECK(json.find("\"endpointId\"") == std::string::npos);
    CHECK(json.find("H:/private") == std::string::npos);
    CHECK(json.find("\"acousticCalibration\":false") != std::string::npos);
    CHECK(json.find("12345678-1234-4234-8234-123456789abc") != std::string::npos);
    CHECK(JsonString("quote\" \\\n") == "\"quote\\\" \\\\\\n\"");
    Report cancelledReport = report;
    cancelledReport.status = RunStatus::Cancelled;
    cancelledReport.durationReached = false;
    cancelledReport.cancelled = true;
    cancelledReport.captureStreamStatus = "STREAM_FAILED";
    cancelledReport.captureStreamQuality = "SEVERE_ANOMALIES";
    const auto cancelledJson = ToJson(cancelledReport, "2026-10-30T12:00:00Z",
        "12345678-1234-4234-8234-123456789abd", "0.3.0");
    CHECK(cancelledJson.find("\"status\":\"CANCELLED\"") != std::string::npos);
    CHECK(cancelledJson.find("\"cancelled\":true") != std::string::npos);
    Report watchdogReport = report;
    watchdogReport.status = RunStatus::Incomplete;
    watchdogReport.durationReached = false;
    watchdogReport.watchdogTimedOut = true;
    watchdogReport.captureStreamStatus = "STREAM_FAILED";
    const auto watchdogJson = ToJson(watchdogReport, "2026-10-30T12:00:00Z",
        "12345678-1234-4234-8234-123456789abf", "0.3.0");
    CHECK(watchdogJson.find("\"watchdogTimedOut\":true") != std::string::npos);
    Report unattempted;
    unattempted.meter.channelCount = 1;
    const auto unattemptedJson = ToJson(unattempted, "2026-10-30T12:00:00Z",
        "12345678-1234-4234-8234-123456789abe", "0.3.0");
    CHECK(unattemptedJson.find("\"durationReached\":null") != std::string::npos);
    CHECK(unattemptedJson.find("\"captureFrameCoveragePercent\":null") != std::string::npos);
    CHECK(unattemptedJson.find("\"analyzedFrames\":null") != std::string::npos);
    CHECK(unattemptedJson.find("\"captureStreamQuality\":null") != std::string::npos);
    CHECK(unattemptedJson.find("\"lastCompletedWindow\":null") != std::string::npos);
    CHECK(unattemptedJson.find("\"actualBufferFrames\":null") != std::string::npos);
    CHECK(unattemptedJson.find("\"elapsedMilliseconds\":null") != std::string::npos);
    CHECK(unattemptedJson.find("\"invalidInput\":null") != std::string::npos);
    return failures == 0;
}
}

int main(int argc, char** argv) {
    if (argc == 2 && std::string_view(argv[1]) == "--write-json") {
        Meter meter;
        if (!meter.Configure(1, 48'000, 100)) return 2;
        const std::int16_t sample[]{1};
        if (!meter.Process(sample, 1, false)) return 3;
        Snapshot snapshot;
        if (!meter.TrySnapshot(snapshot)) return 4;
        Report report;
        report.meter = snapshot;
        report.status = RunStatus::Completed;
        report.requestedDurationSeconds = 1;
        report.meterAnalysisAttempted = true;
        report.meterAnalysisStatus = "ANALYZED";
        report.durationReached = true;
        report.captureFrames = 47'520;
        report.captureFrameCoveragePercent = 99.0;
        report.captureDiscontinuities = 1;
        report.captureTimestampErrors = 1;
        report.unavailableCaptureBuffers = 0;
        report.watchdogTimedOut = false;
        report.cancelled = false;
        report.captureStreamStatus = "STREAM_COMPLETED_WITH_WARNINGS";
        report.captureStreamQuality = "ANOMALIES_PRESENT";
        report.deviceMixFormat.available = true;
        report.deviceMixFormat.reportedChannels = 2;
        report.deviceMixFormat.labelCount = 2;
        report.deviceMixFormat.channelMaskAvailable = false;
        report.selectedTestFormat.available = true;
        report.selectedTestFormat.sampleRateHz = 48'000;
        report.selectedTestFormat.reportedChannels = 1;
        report.selectedTestFormat.containerBits = 16;
        report.selectedTestFormat.validBits = 16;
        report.selectedTestFormat.encoding = FormatTopology::Encoding::Pcm16;
        const auto json = ToJson(report, "2026-10-30T12:00:00Z",
            "12345678-1234-4234-8234-123456789abc", "0.3.0");
        std::puts(json.c_str());
        return 0;
    }
    if (argc != 1) return 2;
    TestSilenceNullInputAndConfiguration();
    TestMonoFullScaleAndClipping();
    TestStereoIndependenceAndPartialWindows();
    TestLatestCompletedWindows();
    TestSnapshotConsistency();
    TestRunClassificationAndJson();
    if (failures != 0) return 1;
    std::puts("Channel meter synthetic tests passed");
    return 0;
}
