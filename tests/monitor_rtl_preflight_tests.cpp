#include "../src/probe/monitor_rtl_preflight.h"

#include <array>
#include <cstdio>
#include <thread>
#include <vector>

namespace {
int failures = 0;
#define CHECK(x) do { if (!(x)) { std::fprintf(stderr, "FAIL %s:%d: %s\n", __FILE__, __LINE__, #x); ++failures; } } while (false)

bool TestRoutingIsolation() {
    MonitorRtlPreflight::StereoStagingBuffer staging;
    CHECK(staging.Configure(4));
    const std::array<std::int16_t, 8> capture{100, 30000, -200, -32000, 0, 14000, 44, 32700};
    CHECK(staging.Process(capture, 4, true, true) == MonitorRtlPreflight::RouteError::None);
    const auto render = staging.Samples(4);
    CHECK(render[0] == 100 && render[2] == -200 && render[4] == 0 && render[6] == 44);
    CHECK(render[1] == 0 && render[3] == 0 && render[5] == 0 && render[7] == 0);
    CHECK(staging.Process({}, 4, true, true, true) == MonitorRtlPreflight::RouteError::None);
    CHECK(std::all_of(staging.Samples(4).begin(), staging.Samples(4).end(), [](auto s) { return s == 0; }));
    CHECK(staging.Process(capture, 4, true, false) == MonitorRtlPreflight::RouteError::InvalidState);
    const auto rejectedState = staging.Samples(4);
    CHECK(std::all_of(rejectedState.begin(), rejectedState.end(), [](auto s) { return s == 0; }));
    CHECK(staging.Process(capture, 4, false, true) == MonitorRtlPreflight::RouteError::InvalidFormat);
    CHECK(std::all_of(staging.Samples(4).begin(), staging.Samples(4).end(), [](auto s) { return s == 0; }));
    CHECK(staging.Process(capture, 5, true, true) == MonitorRtlPreflight::RouteError::InvalidSize);
    CHECK(!staging.Configure(0));
    return failures == 0;
}

MonitorRtlPreflight::Result Analyze(bool marker, bool swap, bool input2Active,
                                     bool clipping = false, bool discontinuity = false,
                                     bool timestampError = false, bool missingFrames = false,
                                     bool noiseOnly = false, std::int16_t input2Level = 20000) {
    using namespace MonitorRtlPreflight;
    AnalogLoopback::RenderSignal signal(1);
    signal.Configure(kExpectedRate, 1, 0, 5);
    std::vector<std::int16_t> pcm(static_cast<std::size_t>(kExpectedFrames) * 2, 0);
    if (marker) signal.Fill(pcm.data(), kExpectedFrames, 0);
    if (noiseOnly) {
        std::uint32_t state = 0x12345678U;
        for (std::size_t i = 0; i < pcm.size(); i += 2) {
            state = state * 1664525U + 1013904223U;
            pcm[i] = static_cast<std::int16_t>(static_cast<int>((state >> 16) & 0x1fff) - 4096);
        }
    }
    if (input2Active) for (std::size_t i = 1; i < pcm.size(); i += 2) pcm[i] = input2Level;
    if (clipping && !pcm.empty()) pcm[0] = INT16_MAX;
    if (swap) for (std::size_t i = 0; i < pcm.size(); i += 2) std::swap(pcm[i], pcm[i + 1]);
    MonitorRtlPreflight::CaptureData data;
    CHECK(data.Configure());
    std::uint64_t position = 1000;
    for (std::uint32_t start = 0; start < kExpectedFrames; start += 256) {
        const auto count = std::min<std::uint32_t>(256, kExpectedFrames - start);
        if (missingFrames && start == 256) { position += count; continue; }
        CHECK(data.Append(pcm.data() + static_cast<std::size_t>(start) * 2, count, false,
            discontinuity && start == 0, timestampError && start == 0, position,
            1'000'000 + static_cast<std::uint64_t>(start) * 10'000'000 / kExpectedRate));
        position += count;
    }
    return data.Analyze();
}

bool TestPreflightDetectionAndIntegrity() {
    const auto good = Analyze(true, false, false);
    CHECK(good.markerFound && good.matchedMarkers >= AnalogLoopback::kMinimumMarkers);
    CHECK(good.input2Quiet && good.continuityClean && good.timestampsClean && good.noClipping);
    const auto missing = Analyze(false, false, false);
    CHECK(!missing.markerFound);
    const auto noise = Analyze(false, false, false, false, false, false, false, true);
    CHECK(!noise.markerFound);
    const auto activeReturn = Analyze(true, false, true);
    CHECK(activeReturn.markerFound && !activeReturn.input2Quiet);
    const auto thresholdReturn = Analyze(true, false, true, false, false, false, false, false, 329);
    CHECK(!thresholdReturn.input2Quiet);
    const auto swapped = Analyze(true, true, false);
    CHECK(!swapped.markerFound);
    const auto clipped = Analyze(true, false, false, true);
    CHECK(!clipped.noClipping);
    const auto gap = Analyze(true, false, false, false, true);
    CHECK(!gap.continuityClean);
    const auto badTimestamp = Analyze(true, false, false, false, false, true);
    CHECK(!badTimestamp.timestampsClean);
    const auto missingFrames = Analyze(true, false, false, false, false, false, true);
    CHECK(!missingFrames.continuityClean);
    MonitorRtlPreflight::CaptureData data;
    CHECK(!data.Configure(0));
    return failures == 0;
}

bool TestStateMachineCancellationAndReuse() {
    using namespace MonitorRtlPreflight;
    StateMachine state;
    CHECK(state.Get() == State::Idle);
    CHECK(state.Transition(State::PreflightCapture));
    std::thread cancel([&] { state.Transition(State::Cancelled); });
    cancel.join();
    CHECK(state.Get() == State::Cancelled);
    CHECK(!state.Transition(State::Validating));
    StateMachine prepared;
    CHECK(prepared.Transition(State::PreflightCapture));
    CHECK(prepared.Transition(State::Validating));
    CHECK(prepared.Transition(State::Armed));
    CHECK(!prepared.Transition(State::Measuring)); // B4-B2B.1 intentionally has no measuring transition.
    MonitorRtlPreflight::CaptureData data;
    CHECK(data.Configure());
    std::array<std::int16_t, 8> first{1, 2, 3, 4, 5, 6, 7, 8};
    CHECK(data.Append(first.data(), 4, false, false, false, 0, 1));
    data.Erase();
    CHECK(data.Configure());
    const auto fresh = data.Analyze();
    CHECK(fresh.frames == 0 && !fresh.markerFound);
    return failures == 0;
}

bool TestStrictCaptureCoverageGate() {
    using namespace MonitorRtlPreflight;
    constexpr std::uint64_t requested = 48'000ULL * 5;
    constexpr auto minimum = MinimumCapturedFrames(48'000, 5);
    CHECK(minimum == 237'600);
    CHECK(HasSufficientCaptureFrames(requested, 48'000, 5));
    CHECK(HasSufficientCaptureFrames(minimum, 48'000, 5));
    CHECK(!HasSufficientCaptureFrames(minimum - 1, 48'000, 5));
    CHECK(!HasSufficientCaptureFrames(0, 0, 5));
    CHECK(!HasSufficientCaptureFrames(requested, 48'000, 0));
    return failures == 0;
}
}

int main() {
    TestRoutingIsolation();
    TestPreflightDetectionAndIntegrity();
    TestStateMachineCancellationAndReuse();
    TestStrictCaptureCoverageGate();
    if (failures != 0) return 1;
    std::puts("Safe routing and capture-only preflight tests passed.");
    return 0;
}
