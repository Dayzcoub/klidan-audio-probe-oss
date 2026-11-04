#include "../src/probe/exclusive_period_discovery.h"

#include <cstdio>

#include "matrix_timestamp.h"

using namespace ExclusiveFormatMatrix;
using namespace ExclusivePeriodDiscovery;

struct FakeApi final : Api {
    Attempt InitializeOnly(const Format&, Period) override { return {}; }
    Attempt InitializeOnly(const Format&, const PeriodRequest& request) override {
        Attempt result;
        result.requestedPeriod100ns = request.requestedPeriod100ns;
        result.requestedBufferFrames = request.requestedFrames;
        result.initializeAttempted = true;
        result.firstInitializeHr = kOk;
        result.finalInitializeHr = request.requestedFrames == 256 ? kUnsupportedFormat : kOk;
        result.actualBufferSizeHr = kOk;
        result.actualBufferFrames = request.requestedFrames;
        result.actualPeriod100ns = request.requestedPeriod100ns;
        result.cleanup = Cleanup::ReleaseAttempted;
        result.clientReleaseAttempts = 1;
        if (request.requestedFrames == 240) {
            result.firstInitializeHr = kBufferSizeNotAligned;
            result.alignmentGetBufferSizeHr = kOk;
            result.alignmentFrames = 256;
            result.alignmentPeriod100ns = ProbeMath::DurationForFrames(256, 48'000);
            result.alignmentActivateHr = kOk;
            result.alignmentInitializeHr = kOk;
            result.actualBufferFrames = 256;
            result.actualPeriod100ns = result.alignmentPeriod100ns;
        }
        if (request.requestedFrames == 192) {
            result.actualBufferFrames = 193;
            result.actualPeriod100ns = ProbeMath::DurationForFrames(193, 48'000);
        }
        return result;
    }
    QueryResult QuerySupport(const Format&) override { return {kOk, kOk, Cleanup::ReleaseAttempted, 1}; }
};

int main() {
    const DevicePeriods periods{30'000, 100'000};
    const auto frames = GenerateIntermediateFrames(periods, 48'000);
    const auto candidates = GenerateCandidates(periods, 48'000);
    if (frames.size() != 8 || std::find(frames.begin(), frames.end(), 192U) == frames.end() || std::find(frames.begin(), frames.end(), 432U) == frames.end()) return 1;
    if (frames.front() != 192U || frames.back() != 432U || candidates[0].requestedFrames != 192U) return 2;
    if (GenerateCandidates({100'000, 30'000}, 48'000).size() != 0) return 3;
    FakeApi api;
    const auto report = Probe("capture-id", Direction::Capture, {48'000, 2, Encoding::Pcm16}, periods, "operation", "report", api);
    const auto aligned = std::find_if(report.candidates.begin(), report.candidates.end(), [](const auto& candidate) { return candidate.requestedFrames == 240; });
    const auto exact = std::find_if(report.candidates.begin(), report.candidates.end(), [](const auto& candidate) { return candidate.requestedFrames == 288; });
    if (report.candidates.size() != 8 || report.candidates[0].classification != Classification::Rejected || exact == report.candidates.end() || exact->classification != Classification::Accepted || aligned == report.candidates.end() ||
        aligned->classification != Classification::AcceptedAligned || aligned->attempt.requestedBufferFrames != 240 || aligned->attempt.alignmentFrames != 256) return 4;
    const auto rejected = api.InitializeOnly({48'000, 2, Encoding::Pcm16}, PeriodRequest::Custom(ProbeMath::DurationForFrames(256, 48'000), 256));
    if (Classify(rejected) != Classification::Rejected) return 5;
    auto missingInitial = api.InitializeOnly({48'000, 2, Encoding::Pcm16}, PeriodRequest::Custom(ProbeMath::DurationForFrames(288, 48'000), 288));
    missingInitial.firstInitializeHr.reset();
    if (Classify(missingInitial) != Classification::Incomplete) return 8;
    ExclusivePeriodDiscovery::Report render = report; render.direction = Direction::Render; render.endpointId = "render-id";
    render.candidates[1].classification = Classification::Unknown;
    const auto matrix = Compare(&report, &render);
    if (matrix.size() != 8 || matrix[0].status != "NEITHER" || matrix[1].status != "UNKNOWN" || matrix[2].status != "ALIGNED_REQUIRES_CONFIRMATION" || matrix[3].status != "BOTH_EXACT") return 6;
    const auto json = ToJson(report, "2026-11-01T00:00:00Z", "TEST_FAKE_PERIOD_BACKEND");
    if (json.find("wasapi-exclusive-period-discovery") == std::string::npos || json.find("\"startCalled\":false") == std::string::npos || json.find("ACCEPTED") == std::string::npos) return 7;
    SYSTEMTIME controlled{}; controlled.wYear = 2026; controlled.wMonth = 11; controlled.wDay = 1; controlled.wHour = 0; controlled.wMinute = 0; controlled.wSecond = 0; controlled.wMilliseconds = 123;
    if (MatrixTimestampUtc(controlled) != "2026-11-01T00:00:00.123Z" || MatrixTimestampUtc() == "2026-11-01T00:00:00Z") return 9;
    if (json.find("CLEANUP_CONFIRMED") != std::string::npos) return 10;
    return 0;
}
