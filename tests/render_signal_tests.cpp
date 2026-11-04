#include "../src/probe/render_signal.h"
#include "../src/probe/probe_math.h"

#include <array>
#include <cmath>
#include <cstdio>
#include <vector>

namespace {
int failures = 0;
#define CHECK(x) do { if (!(x)) { std::fprintf(stderr, "%s:%d: %s\n", __FILE__, __LINE__, #x); ++failures; } } while (false)

bool TestChannelAndCeiling() {
    RenderSignal::Generator generator;
    CHECK(generator.Configure(48'000, 2, 1));
    std::array<std::int16_t, 4'800 * 2> samples{};
    CHECK(generator.Fill(samples.data(), 4'800));
    std::int16_t peak = 0;
    bool leftNonzero = false, rightNonzero = false;
    for (std::size_t i = 0; i < samples.size(); i += 2) {
        leftNonzero |= samples[i] != 0;
        rightNonzero |= samples[i + 1] != 0;
        peak = std::max<std::int16_t>(peak, static_cast<std::int16_t>(std::abs(samples[i + 1])));
    }
    CHECK(!leftNonzero && rightNonzero);
    CHECK(peak <= RenderSignal::kPeakCeiling);
    CHECK(20.0 * std::log10(peak / 32768.0) <= -36.0);
    std::int64_t sum = 0;
    for (const auto sample : samples) sum += sample;
    CHECK(std::abs(static_cast<double>(sum) / samples.size()) < 2.0);
    std::size_t risingCrossings = 0;
    for (std::size_t frame = 1; frame < 4'800; ++frame)
        if (samples[(frame - 1) * 2 + 1] <= 0 && samples[frame * 2 + 1] > 0) ++risingCrossings;
    CHECK(risingCrossings >= 98 && risingCrossings <= 101);
    return failures == 0;
}

bool TestContinuityEnvelopeAndSilence() {
    RenderSignal::Generator whole, split;
    CHECK(whole.Configure(48'000, 2, 0));
    CHECK(split.Configure(48'000, 2, 0));
    std::vector<std::int16_t> a(12'000 * 2), b(12'000 * 2);
    CHECK(whole.Fill(a.data(), 12'000));
    CHECK(split.Fill(b.data(), 6'000));
    CHECK(split.Fill(b.data() + 12'000, 6'000));
    CHECK(a == b);
    CHECK(split.Stats()[0].nonzeroFrames != 0);
    CHECK(split.Stats()[1].nonzeroFrames == 0);
    for (std::size_t frame = 4'800; frame < 12'000; ++frame) CHECK(a[frame * 2] == 0);
    std::array<int, 100> oneMillisecondPeak{};
    for (std::size_t ms = 0; ms < 100; ++ms)
        for (std::size_t frame = ms * 48; frame < (ms + 1) * 48; ++frame)
            oneMillisecondPeak[ms] = std::max(oneMillisecondPeak[ms], std::abs(a[frame * 2]));
    for (std::size_t ms = 1; ms < 5; ++ms) CHECK(oneMillisecondPeak[ms] >= oneMillisecondPeak[ms - 1] - 4);
    for (std::size_t ms = 95; ms < 100; ++ms) CHECK(oneMillisecondPeak[ms] <= oneMillisecondPeak[ms - 1] + 4);
    CHECK(oneMillisecondPeak[4] > oneMillisecondPeak[0]);
    CHECK(oneMillisecondPeak[99] < oneMillisecondPeak[95]);
    split.Stop();
    std::array<std::int16_t, 256> tail;
    tail.fill(123);
    CHECK(split.Fill(tail.data(), 128));
    for (const auto value : tail) CHECK(value == 0);
    split.Reset();
    CHECK(split.GeneratedFrames() == 0);
    CHECK(split.Fill(b.data(), 12'000));
    CHECK(a == b);
    return failures == 0;
}

bool TestValidationAndSilence() {
    RenderSignal::Generator generator;
    CHECK(!generator.Configure(44'100, 2, 0));
    CHECK(!generator.Configure(48'000, 33, 0));
    CHECK(!generator.Configure(48'000, 2, 2));
    CHECK(generator.Configure(48'000, 32, 31));
    std::array<std::int16_t, 64> samples;
    samples.fill(55);
    CHECK(generator.Silence(samples.data(), 2));
    for (const auto value : samples) CHECK(value == 0);
    CHECK(!generator.Fill(samples.data(), RenderSignal::kMaximumFramesPerBuffer + 1));
    CHECK(!generator.Fill(nullptr, 1));
    CHECK(!generator.Configure(96'000, 2, 0));
    CHECK(!generator.Fill(samples.data(), 2));
    return failures == 0;
}

bool TestRunOutcomes() {
    using ProbeMath::StreamStatus;
    CHECK(ProbeMath::ClassifyStream(false, false, false, false, 0, 0, false,
        false, false, false) == StreamStatus::InitializeFailed);
    CHECK(ProbeMath::ClassifyStream(true, true, false, false, 0, 0, false,
        false, true, false) == StreamStatus::StartFailed);
    const bool cancellationRequested = true;
    CHECK(cancellationRequested && ProbeMath::ClassifyStream(true, true, true, false, 1, 480, false,
        false, false, true) == StreamStatus::StreamFailed);
    const bool simulatedDeviceLoss = true;
    CHECK(simulatedDeviceLoss && ProbeMath::ClassifyStream(true, true, true, false, 1, 480, false,
        false, true, false) == StreamStatus::StreamFailed);
    CHECK(ProbeMath::ClassifyStream(true, true, true, true, 100, 48'000, true,
        true, false, true) == StreamStatus::StreamCompletedWithWarnings);
    return failures == 0;
}
}

int main() {
    TestChannelAndCeiling();
    TestContinuityEnvelopeAndSilence();
    TestValidationAndSilence();
    TestRunOutcomes();
    return failures == 0 ? 0 : 1;
}
