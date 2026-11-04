#include "../src/probe/monitor_rtl_analyzer.h"
#include "../src/probe/live_transport.h"
#include "../src/probe/monitor_rtl_preflight.h"

#include <algorithm>
#include <array>
#include <cmath>
#include <cstdio>
#include <cstdint>
#include <string_view>
#include <vector>

namespace {
using namespace MonitorRtlAnalyzer;
constexpr std::uint32_t kFrames = kRate * 5;
int failures = 0;
#define CHECK(x) do { if (!(x)) { std::fprintf(stderr, "FAIL %s:%d: %s\n", __FILE__, __LINE__, #x); ++failures; } } while (false)

struct Fixture {
    std::vector<std::int16_t> pcm = std::vector<std::int16_t>(static_cast<std::size_t>(kFrames) * 2);
    std::vector<Packet> packets;
    Input input{};

    Fixture(int delayFrames = 192, bool filtered = false, bool invert = false, int noise = 0) {
        AnalogLoopback::RenderSignal source(1);
        CHECK(source.Configure(kRate, 1, 0, 5));
        std::vector<std::int16_t> reference(static_cast<std::size_t>(kFrames) * 2);
        source.Fill(reference.data(), kFrames, 0);
        std::uint32_t random = 0xA341316CU;
        int previous = 0;
        for (std::uint32_t frame = 0; frame < kFrames; ++frame) {
            const int ref = reference[static_cast<std::size_t>(frame) * 2];
            const auto sourceFrame = static_cast<std::int64_t>(frame) - delayFrames;
            int returned = sourceFrame >= 0 ? reference[static_cast<std::size_t>(sourceFrame) * 2] : 0;
            if (filtered) {
                previous = (previous * 3 + returned) / 4;
                returned = previous;
            }
            returned = returned * 3 / 4;
            if (invert) returned = -returned;
            if (noise != 0) {
                random = random * 1664525U + 1013904223U;
                returned += static_cast<int>((random >> 24) % static_cast<unsigned>(noise * 2 + 1)) - noise;
            }
            pcm[static_cast<std::size_t>(frame) * 2] = static_cast<std::int16_t>(ref);
            pcm[static_cast<std::size_t>(frame) * 2 + 1] = static_cast<std::int16_t>(returned);
        }
        for (std::uint32_t start = 0; start < kFrames; start += 144) {
            const auto count = std::min<std::uint32_t>(144, kFrames - start);
            packets.push_back({start, count, 1'000'000ULL +
                static_cast<std::uint64_t>(static_cast<long double>(start) * 10'000'000.0L / kRate), false, false});
        }
        input = {pcm, packets, kRate, 2, SampleFormat::Pcm16, kFrames};
    }
    Result Run() const { return Analyze(input); }
};

bool TestKnownDelaysAndSignalTolerance() {
    for (const int delay : {96, 192, 480}) {
        Fixture fixture(delay);
        const auto result = fixture.Run();
        CHECK(result.validation == ValidationStatus::SyntheticValidated);
        CHECK(result.signal == SignalStatus::Found);
        CHECK(result.pairedMarkers >= AnalogLoopback::kMinimumMarkers);
        CHECK(std::abs(result.medianDelayFrames - delay) < 1.0);
        CHECK(std::abs(result.medianDelayMilliseconds - delay * 1000.0 / kRate) < 0.03);
        CHECK(result.physicalRtl == PhysicalRtlStatus::NotMeasured);
    }
    Fixture filtered(192, true, true, 3);
    const auto result = filtered.Run();
    CHECK(result.validation == ValidationStatus::SyntheticValidated);
    CHECK(result.pairedMarkers >= AnalogLoopback::kMinimumMarkers);
    CHECK(!result.differentialInputChannelDelayCalibrated);
    CHECK(PhysicalRtlText(result.physicalRtl) == std::string_view("NOT_MEASURED"));
    CHECK(ClassifyTimingVariation(6.0, 0.0) == TimingQuality::Good);
    CHECK(ClassifyTimingVariation(std::nextafter(6.0, 7.0), 0.0) == TimingQuality::Warning);
    CHECK(ClassifyTimingVariation(24.0, 0.0) == TimingQuality::Warning);
    CHECK(ClassifyTimingVariation(std::nextafter(24.0, 25.0), 0.0) == TimingQuality::Unreliable);

    Fixture identical(0);
    for (std::size_t frame = 0; frame < kFrames; ++frame)
        identical.pcm[frame * 2 + 1] = identical.pcm[frame * 2];
    identical.input.interleavedPcm = identical.pcm;
    const auto zeroDelay = identical.Run();
    CHECK(zeroDelay.validation == ValidationStatus::Rejected);
    CHECK(zeroDelay.failure == FailureReason::NonPositiveDifferentialDelay);
    CHECK(zeroDelay.physicalRtl == PhysicalRtlStatus::NotMeasured);
    return failures == 0;
}

bool TestFailClosedInputAndTimeline() {
    Fixture good;
    auto result = good.Run();
    CHECK(result.validation == ValidationStatus::SyntheticValidated);

    auto referenceAbsentSamples = good.pcm;
    for (std::size_t i = 0; i < referenceAbsentSamples.size(); i += 2) referenceAbsentSamples[i] = 0;
    auto noReference = good.input;
    noReference.interleavedPcm = referenceAbsentSamples;
    result = Analyze(noReference);
    CHECK(result.validation == ValidationStatus::Rejected);
    CHECK(result.failure == FailureReason::ReferenceMissing || result.failure == FailureReason::SequenceRejected);

    auto returnAbsentSamples = good.pcm;
    for (std::size_t i = 1; i < returnAbsentSamples.size(); i += 2) returnAbsentSamples[i] = 0;
    auto noReturn = good.input;
    noReturn.interleavedPcm = returnAbsentSamples;
    result = Analyze(noReturn);
    CHECK(result.validation == ValidationStatus::Rejected);

    std::vector<std::int16_t> swapped(good.pcm.size());
    for (std::size_t i = 0; i < swapped.size(); i += 2) {
        swapped[i] = good.pcm[i + 1]; swapped[i + 1] = good.pcm[i];
    }
    auto wrongChannel = good.input;
    wrongChannel.interleavedPcm = swapped;
    result = Analyze(wrongChannel);
    CHECK(result.validation == ValidationStatus::Rejected);

    auto discontinuous = good.packets;
    discontinuous[100].dataDiscontinuity = true;
    auto bad = good.input; bad.packets = discontinuous;
    CHECK(Analyze(bad).failure == FailureReason::Discontinuity);
    discontinuous = good.packets; discontinuous[100].timestampError = true;
    bad.packets = discontinuous;
    CHECK(Analyze(bad).failure == FailureReason::TimestampError);
    discontinuous = good.packets; discontinuous[100].firstFrame++;
    bad.packets = discontinuous;
    CHECK(Analyze(bad).failure == FailureReason::FrameGap);
    discontinuous = good.packets; discontinuous[100].qpc100ns += 1'000'000;
    bad.packets = discontinuous;
    CHECK(Analyze(bad).failure == FailureReason::TimestampError);

    auto clipped = good.pcm; clipped[0] = INT16_MIN;
    bad = good.input; bad.interleavedPcm = clipped;
    CHECK(Analyze(bad).failure == FailureReason::Clipping);
    bad = good.input; bad.expectedFrames = kFrames + 1;
    CHECK(Analyze(bad).failure == FailureReason::InvalidBounds);
    bad = good.input; // Nominal duration must still require nominal coverage.
    bad.interleavedPcm = std::span<const std::int16_t>(good.pcm.data(), 12'000 * 2);
    bad.packets = std::span<const Packet>(good.packets.data(), 84);
    CHECK(Analyze(bad).failure == FailureReason::InsufficientCoverage);
    bad = good.input; bad.expectedFrames = 12'000; // An understated duration cannot relax the five-second contract.
    bad.interleavedPcm = std::span<const std::int16_t>(good.pcm.data(), 12'000 * 2);
    bad.packets = std::span<const Packet>(good.packets.data(), 84);
    CHECK(Analyze(bad).failure == FailureReason::InvalidBounds);
    bad = good.input; bad.rate = 44100;
    CHECK(Analyze(bad).failure == FailureReason::InvalidFormat);
    auto boundary = good.input;
    const auto ninetyNinePercentFrames = static_cast<std::size_t>(MinimumCapturedFrames(kFrames));
    boundary.interleavedPcm = std::span<const std::int16_t>(good.pcm.data(), ninetyNinePercentFrames * 2);
    boundary.packets = std::span<const Packet>(good.packets.data(), ninetyNinePercentFrames / 144);
    CHECK(Analyze(boundary).validation == ValidationStatus::SyntheticValidated);
    boundary.interleavedPcm = std::span<const std::int16_t>(good.pcm.data(),
        (ninetyNinePercentFrames - 1) * 2);
    CHECK(Analyze(boundary).failure == FailureReason::InsufficientCoverage);
    return failures == 0;
}

bool TestMissingMarkersAndVariation() {
    Fixture fixture(192);
    auto missing = fixture.pcm;
    AnalogLoopback::RenderSignal signal(1);
    CHECK(signal.Configure(kRate, 1, 0, 5));
    const auto starts = signal.MarkerStarts();
    const auto marker = signal.Marker();
    for (std::size_t i = 0; i < marker.size(); ++i)
        missing[(starts[7] + 192 + i) * 2 + 1] = 0;
    auto input = fixture.input; input.interleavedPcm = missing;
    CHECK(Analyze(input).validation == ValidationStatus::Rejected);

    auto contradictory = fixture.pcm;
    const auto startsForPolarity = signal.MarkerStarts();
    for (std::size_t offset = 0; offset < marker.size(); ++offset)
        contradictory[(startsForPolarity[8] + 192 + offset) * 2 + 1] =
            static_cast<std::int16_t>(-contradictory[(startsForPolarity[8] + 192 + offset) * 2 + 1]);
    input = fixture.input; input.interleavedPcm = contradictory;
    CHECK(Analyze(input).validation == ValidationStatus::Rejected);

    auto repeated = fixture.pcm;
    for (std::size_t markerIndex = 0; markerIndex < starts.size(); ++markerIndex) {
        const auto duplicateStart = starts[markerIndex] + 6000 + 192;
        for (std::size_t offset = 0; offset < marker.size(); ++offset) {
            const auto originalFrame = starts[markerIndex] + 192 + offset;
            if (duplicateStart + offset < kFrames)
                repeated[(duplicateStart + offset) * 2 + 1] = fixture.pcm[originalFrame * 2 + 1];
        }
    }
    input = fixture.input; input.interleavedPcm = repeated;
    const auto ambiguous = Analyze(input);
    CHECK(ambiguous.validation == ValidationStatus::Rejected);
    CHECK(ambiguous.ambiguousSequences != 0);

    const auto variableFixture = [&](double requestedSpread) {
        auto varying = fixture.pcm;
        for (std::size_t sample = 1; sample < varying.size(); sample += 2) varying[sample] = 0;
        for (std::size_t markerIndex = 0; markerIndex < starts.size(); ++markerIndex) {
            const double fractionalDelay = requestedSpread * markerIndex / (starts.size() - 1);
            for (std::size_t j = 0; j < marker.size() + 2; ++j) {
                const auto destination = starts[markerIndex] + 96 + j;
                const double sourceOffset = static_cast<double>(j) - fractionalDelay;
                int value = 0;
                if (sourceOffset >= 0 && sourceOffset < marker.size()) {
                    const auto lower = static_cast<std::size_t>(std::floor(sourceOffset));
                    const auto upper = std::min(lower + 1, marker.size() - 1);
                    const double fraction = sourceOffset - lower;
                    const double sample = fixture.pcm[(starts[markerIndex] + lower) * 2] * (1.0 - fraction) +
                        fixture.pcm[(starts[markerIndex] + upper) * 2] * fraction;
                    value = static_cast<int>(std::lrint(sample * 0.75));
                }
                if (destination < kFrames) varying[destination * 2 + 1] = static_cast<std::int16_t>(value);
            }
        }
        auto varied = fixture.input;
        varied.interleavedPcm = varying;
        return Analyze(varied);
    };
    const auto warningVariation = variableFixture(12.0);
    CHECK(warningVariation.validation == ValidationStatus::Rejected);
    CHECK(warningVariation.timing == TimingQuality::Warning);
    CHECK(warningVariation.failure == FailureReason::DelayVariation);
    const auto unreliableVariation = variableFixture(25.0);
    CHECK(unreliableVariation.validation == ValidationStatus::Rejected);
    CHECK(unreliableVariation.timing == TimingQuality::Unreliable);
    CHECK(unreliableVariation.failure == FailureReason::DelayVariation);

    std::vector<std::int16_t> noise(fixture.pcm.size());
    std::uint32_t random = 7;
    for (auto& sample : noise) {
        random = random * 1103515245U + 12345U;
        sample = static_cast<std::int16_t>((random >> 20) & 0x3ff);
    }
    input = fixture.input; input.interleavedPcm = noise;
    CHECK(Analyze(input).validation == ValidationStatus::Rejected);
    return failures == 0;
}

bool TestStagingAndExistingRingIsolation() {
    MonitorRtlPreflight::StereoStagingBuffer staging;
    CHECK(staging.Configure(144));
    LiveTransport::SpscPcm16StereoRing ring;
    CHECK(ring.Configure(144, 144, kRate));
    std::array<std::int16_t, 288> capture{};
    std::array<std::int16_t, 288> render{};
    std::array<std::uint64_t, 144> provenance{};
    for (unsigned batch = 0; batch < 2; ++batch) {
        for (std::size_t frame = 0; frame < 144; ++frame) {
            capture[frame * 2] = static_cast<std::int16_t>(1000 + batch * 144 + frame);
            capture[frame * 2 + 1] = static_cast<std::int16_t>(30000 - frame); // Analyzer-only input 2.
        }
        CHECK(staging.Process(capture, 144, true, true) == MonitorRtlPreflight::RouteError::None);
        const auto staged = staging.Samples(144);
        for (std::size_t i = 1; i < staged.size(); i += 2) CHECK(staged[i] == 0);
        CHECK(ring.Push(staged.data(), 144, false, batch * 144));
    }
    CHECK(ring.Pull(render.data(), 144, false, provenance.data()).prefillSilence);
    CHECK(std::all_of(render.begin(), render.end(), [](auto value) { return value == 0; }));
    for (std::size_t frame = 0; frame < 144; ++frame) {
        capture[frame * 2] = static_cast<std::int16_t>(1000 + 2 * 144 + frame);
        capture[frame * 2 + 1] = 32700;
    }
    CHECK(staging.Process(capture, 144, true, true) == MonitorRtlPreflight::RouteError::None);
    CHECK(ring.Push(staging.Samples(144).data(), 144, false, 2 * 144));
    CHECK(ring.Pull(render.data(), 144, false, provenance.data()).consumedFrames == 144);
    for (std::size_t frame = 0; frame < 144; ++frame) {
        const auto inputL = static_cast<std::int16_t>(1000 + frame);
        const auto expected = static_cast<std::int16_t>(std::lrint(inputL * LiveTransport::kOutputGain));
        CHECK(render[frame * 2] == expected);
        CHECK(render[frame * 2 + 1] == 0);
    }
    CHECK(staging.Process({}, 144, true, true, true) == MonitorRtlPreflight::RouteError::None);
    CHECK(std::all_of(staging.Samples(144).begin(), staging.Samples(144).end(), [](auto v) { return v == 0; }));
    CHECK(staging.Process(capture, 144, false, true) == MonitorRtlPreflight::RouteError::InvalidFormat);
    CHECK(std::all_of(staging.Samples(144).begin(), staging.Samples(144).end(), [](auto v) { return v == 0; }));
    CHECK(staging.Process(capture, 144, true, false) == MonitorRtlPreflight::RouteError::InvalidState);
    CHECK(std::all_of(staging.Samples(144).begin(), staging.Samples(144).end(), [](auto v) { return v == 0; }));
    CHECK(staging.Process(capture, 145, true, true) == MonitorRtlPreflight::RouteError::InvalidSize);
    CHECK(std::all_of(staging.Samples(144).begin(), staging.Samples(144).end(), [](auto v) { return v == 0; }));
    CHECK(ring.Configure(144, 144, kRate)); // Fresh configuration discards queued prior PCM.
    CHECK(ring.Pull(render.data(), 144, false).prefillSilence);
    CHECK(std::all_of(render.begin(), render.end(), [](auto v) { return v == 0; }));

    LiveTransport::SpscPcm16StereoRing stress;
    CHECK(stress.Configure(144, 144, kRate));
    std::vector<std::int16_t> oversized((1152 + 1) * 2, 17);
    CHECK(stress.Push(oversized.data(), 1153, false));
    CHECK(stress.GetStats().overflowEvents > 0);
    std::vector<std::int16_t> drained(1200 * 2);
    const auto drainedResult = stress.Pull(drained.data(), 1200, false);
    CHECK(drainedResult.underrun || drainedResult.silenceFrames != 0);
    CHECK(stress.GetStats().underrunEvents > 0 || stress.GetStats().underrunFrames > 0);
    return failures == 0;
}

bool TestLifecycleIsProcessLocalAndFailsClosed() {
    Lifecycle lifecycle;
    CHECK(lifecycle.BeginCapturePreflight());
    CHECK(lifecycle.BeginValidation());
    CHECK(lifecycle.Arm(true));
    Revalidation checks{true, true, true, true, true, true};
    CHECK(lifecycle.RevalidateForThisRun(checks));
    CHECK(lifecycle.BeginMeasurement());
    CHECK(lifecycle.Stop(true));
    CHECK(lifecycle.State() == LifecycleState::Completed);

    Lifecycle staleArmed;
    CHECK(staleArmed.State() == LifecycleState::Idle); // Prior process state cannot be imported.
    CHECK(!staleArmed.RevalidateForThisRun(checks));
    CHECK(staleArmed.State() == LifecycleState::Failed);
    Lifecycle denied;
    CHECK(denied.BeginCapturePreflight() && denied.BeginValidation() && denied.Arm(true));
    checks.renderSilent = false;
    CHECK(!denied.RevalidateForThisRun(checks));
    CHECK(denied.State() == LifecycleState::Failed);
    Lifecycle cancelled;
    CHECK(cancelled.BeginCapturePreflight());
    CHECK(cancelled.Cancel());
    CHECK(cancelled.State() == LifecycleState::Cancelled);
    CHECK(!cancelled.BeginValidation());
    return failures == 0;
}
}

int main() {
    TestKnownDelaysAndSignalTolerance();
    TestFailClosedInputAndTimeline();
    TestMissingMarkersAndVariation();
    TestStagingAndExistingRingIsolation();
    TestLifecycleIsProcessLocalAndFailsClosed();
    if (failures) return 1;
    std::puts("Offline dual-channel RTL analyzer and routing tests passed.");
    return 0;
}
