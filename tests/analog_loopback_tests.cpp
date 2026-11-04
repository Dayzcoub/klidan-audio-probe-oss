#include "../src/probe/analog_loopback.h"

#include <algorithm>
#include <array>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <limits>
#include <vector>

namespace {

#define CHECK(condition) do { if (!(condition)) { std::fprintf(stderr, "Check failed at line %d: %s\n", __LINE__, #condition); return false; } } while (false)

bool RangeFits(std::size_t capacity, std::size_t start, std::size_t count) {
    return start <= capacity && count <= capacity - start;
}

bool Near(double actual, double expected, double tolerance = 0.02) {
    return std::abs(actual - expected) <= tolerance;
}

bool TestMarkerOutputAndCorrelation() {
    AnalogLoopback::RenderSignal render(2);
    CHECK(render.Configure(48'000, 2, 144, 5));
    CHECK(render.MarkerStarts().size() >= 5);
    const auto maxMarker = *std::max_element(render.Marker().begin(), render.Marker().end(),
        [](auto left, auto right) { return std::abs(static_cast<int>(left)) < std::abs(static_cast<int>(right)); });
    CHECK(std::abs(static_cast<int>(maxMarker)) <= AnalogLoopback::kSignalPeakCeiling);
    CHECK(render.Marker().front() == 0 && render.Marker().back() == 0);
    std::array<std::int16_t, 240> stereo{};
    render.Fill(stereo.data(), 120, render.FirstMarkerFrame());
    bool rightActive = false;
    for (std::size_t frame = 0; frame < 120; ++frame) {
        CHECK(stereo[frame * 2] == 0);
        rightActive = rightActive || stereo[frame * 2 + 1] != 0;
    }
    CHECK(rightActive);
    render.CommitFrames(render.FirstMarkerFrame(), 120);
    render.Fill(stereo.data(), 120, render.FirstMarkerFrame() + 120);
    render.CommitFrames(render.FirstMarkerFrame() + 120, 120);
    CHECK(render.NonzeroFrames() > 0 && render.Peak() <= AnalogLoopback::kSignalPeakCeiling);
    CHECK(render.MarkersStarted() == 1 && render.MarkersCompleted() == 0);
    std::array<std::int16_t, 782> markerTail{};
    render.Fill(markerTail.data(), 391, render.FirstMarkerFrame() + 240);
    render.CommitFrames(render.FirstMarkerFrame() + 240, 391);
    CHECK(render.MarkersCompleted() == 1 && render.SubmissionContinuous());

    std::vector<std::int16_t> capture(4'000, 0);
    constexpr std::size_t offset = 192;
    CHECK(RangeFits(capture.size(), offset, render.Marker().size()));
    for (std::size_t i = 0; i < render.Marker().size(); ++i)
        capture[offset + i] = static_cast<std::int16_t>(-render.Marker()[i] / 2);
    const auto peaks = AnalogLoopback::FindMarkers(capture, render.Marker());
    CHECK(!peaks.empty());
    CHECK(std::abs(peaks.front().offsetFrames - offset) < 0.2);
    CHECK(peaks.front().confidence > 0.99);
    CHECK(peaks.front().polarity == -1);
    for (const std::size_t offset : {96U, 480U}) {
        std::fill(capture.begin(), capture.end(), 0);
        CHECK(RangeFits(capture.size(), offset, render.Marker().size()));
        std::copy(render.Marker().begin(), render.Marker().end(), capture.begin() + offset);
        const auto knownDelay = AnalogLoopback::FindMarkers(capture, render.Marker());
        CHECK(!knownDelay.empty() && std::abs(knownDelay.front().offsetFrames - offset) < 0.2);
    }
    CHECK(Near(AnalogLoopback::SamplesToMilliseconds(96, 48'000), 2.0));
    CHECK(Near(AnalogLoopback::SamplesToMilliseconds(192, 48'000), 4.0));
    CHECK(Near(AnalogLoopback::SamplesToMilliseconds(480, 48'000), 10.0));
    return true;
}

std::int16_t NoiseSample(std::uint32_t& state, int amplitude) {
    state ^= state << 13;
    state ^= state >> 17;
    state ^= state << 5;
    return static_cast<std::int16_t>(static_cast<int>(state % (2 * amplitude + 1)) - amplitude);
}

void BuildClockSeries(ClockAnalysis::Series& series, ClockAnalysis::Direction direction,
                      std::uint64_t baseQpc, std::uint32_t frames);

enum class SyntheticTiming { Clean, CaptureStartupJump, CaptureStep, CaptureDrift,
                             CaptureNegativeDrift, RenderStartupClockJump, RenderClockStep };

bool TestFractionalAmplitudeNoiseAndNoSignal() {
    AnalogLoopback::RenderSignal render(1);
    CHECK(render.Configure(48'000, 1, 144, 5));
    constexpr double fractionalOffset = 311.35;
    std::vector<std::int16_t> capture(3'000, 0);
    for (std::size_t n = 0; n < capture.size(); ++n) {
        const double source = static_cast<double>(n) - fractionalOffset;
        const auto left = static_cast<long long>(std::floor(source));
        const double fraction = source - left;
        auto sampleAt = [&](long long index) -> double {
            return index >= 0 && static_cast<std::size_t>(index) < render.Marker().size()
                ? render.Marker()[static_cast<std::size_t>(index)] : 0.0;
        };
        capture[n] = static_cast<std::int16_t>(std::lrint((sampleAt(left) * (1 - fraction) +
            sampleAt(left + 1) * fraction) * 0.35));
    }
    std::uint32_t random = 0x12345678U;
    for (auto& sample : capture) sample = static_cast<std::int16_t>(sample + NoiseSample(random, 20));
    const auto peaks = AnalogLoopback::FindMarkers(capture, render.Marker());
    CHECK(!peaks.empty());
    CHECK(std::abs(peaks.front().offsetFrames - fractionalOffset) < 0.25);
    CHECK(peaks.front().confidence > AnalogLoopback::kMinimumCorrelation);

    std::vector<std::int16_t> noSignal(3'000, 0);
    const auto silence = AnalogLoopback::DiagnoseMarkers(noSignal, render.Marker());
    CHECK(silence.available && silence.maximum == 0.0 && silence.maximumOffsetFrames == 0.0);
    CHECK(silence.detectedPeaks.empty());
    for (auto& sample : noSignal) sample = NoiseSample(random, 500);
    const auto noise = AnalogLoopback::DiagnoseMarkers(noSignal, render.Marker());
    CHECK(noise.available && noise.maximum > 0.0);
    CHECK(noise.maximum < AnalogLoopback::kMinimumCorrelation);
    CHECK(noise.detectedPeaks.empty());
    CHECK(noise.candidatesAboveThreshold == 0 && noise.candidatesBelowThreshold != 0);
    CHECK(noise.bestIndependentCandidates.size() <= 5);
    CHECK(!AnalogLoopback::DiagnoseMarkers(
        std::span<const std::int16_t>(noSignal.data(), render.Marker().size() - 1), render.Marker()).available);
    return true;
}

bool TestFilteredAnalogSequenceDetection() {
    constexpr std::uint32_t frames = 190'000;
    constexpr std::uint64_t baseQpc = 10'000'000'000ULL;
    constexpr double delay = 377.35;
    constexpr std::array<double, 5> lowPass{0.10, 0.20, 0.40, 0.20, 0.10};
    AnalogLoopback::RenderSignal render(1);
    CHECK(render.Configure(48'000, 1, 144, 5));
    std::vector<std::int16_t> source(frames, 0);
    std::array<std::int16_t, 288> stereo{};
    for (std::uint32_t start = 0; start < frames; start += 144) {
        const auto count = std::min<std::uint32_t>(144, frames - start);
        render.Fill(stereo.data(), count, start);
        for (std::uint32_t i = 0; i < count; ++i) source[start + i] = stereo[i * 2];
    }
    std::vector<std::int16_t> response(frames, 0);
    for (std::size_t n = 0; n < response.size(); ++n) {
        double value = 0;
        for (std::size_t tap = 0; tap < lowPass.size(); ++tap) {
            if (n >= tap) value += source[n - tap] * lowPass[tap];
        }
        response[n] = static_cast<std::int16_t>(std::lrint(value * -0.2));
    }
    std::vector<std::int16_t> filtered(frames, 0);
    for (std::size_t n = 0; n < response.size(); ++n) {
        const double target = n + delay;
        const auto whole = static_cast<std::size_t>(std::floor(target));
        const double fraction = target - whole;
        if (whole < filtered.size())
            filtered[whole] = static_cast<std::int16_t>(filtered[whole] + std::lrint(response[n] * (1.0 - fraction)));
        if (whole + 1 < filtered.size())
            filtered[whole + 1] = static_cast<std::int16_t>(filtered[whole + 1] + std::lrint(response[n] * fraction));
    }
    std::uint32_t random = 0xA51CE55U;
    for (auto& sample : filtered) sample = static_cast<std::int16_t>(sample + NoiseSample(random, 8));

    AnalogLoopback::CaptureData capture(1);
    CHECK(capture.Configure(1, 48'000, frames, 2'048));
    std::array<std::int16_t, 288> packet{};
    for (std::uint32_t start = 0; start < frames; start += 144) {
        const auto count = std::min<std::uint32_t>(144, frames - start);
        for (std::uint32_t i = 0; i < count; ++i) packet[i * 2] = filtered[start + i];
        const auto qpc = baseQpc + static_cast<std::uint64_t>(start) * 10'000'000 / 48'000;
        CHECK(capture.Append(packet.data(), count, false, false, false, start, qpc));
    }
    ClockAnalysis::Series captureClock;
    ClockAnalysis::Series renderClock;
    BuildClockSeries(captureClock, ClockAnalysis::Direction::Capture, baseQpc, frames);
    BuildClockSeries(renderClock, ClockAnalysis::Direction::Render, baseQpc, frames);
    const auto clock = ClockAnalysis::Analyze(captureClock, renderClock);
    const auto analysis = AnalogLoopback::Analyze(capture, render, captureClock, renderClock, clock, true);
    CHECK(analysis.correlation.available);
    CHECK(analysis.correlation.maximum < AnalogLoopback::kMinimumCorrelation);
    CHECK(std::abs(analysis.correlation.maximum - 0.59) <= 0.10);
    CHECK(analysis.correlation.candidatesAboveThreshold == 0);
    CHECK(analysis.correlation.candidatesBelowThreshold != 0);
    CHECK(!analysis.correlation.bestIndependentCandidates.empty());
    CHECK(analysis.sequence.status == AnalogLoopback::SequenceStatus::Matched);
    CHECK(analysis.sequence.matchedMarkers >= AnalogLoopback::kMinimumMarkers);
    CHECK(analysis.sequence.globalPolarity == -1);
    CHECK(analysis.status == AnalogLoopback::Status::Measured);
    CHECK(std::abs(analysis.medianSamples - (delay + 2.0)) < 2.0);
    const auto interrupted = AnalogLoopback::Analyze(capture, render,
        captureClock, renderClock, clock, false);
    CHECK(interrupted.status == AnalogLoopback::Status::Failed);
    CHECK(std::strcmp(interrupted.rejectionReason,
        "one or both WASAPI streams did not complete cleanly") == 0);
    return true;
}

void BuildClockSeries(ClockAnalysis::Series& series, ClockAnalysis::Direction direction,
                      std::uint64_t baseQpc, std::uint32_t frames) {
    series.direction = direction;
    series.serviceHresult = ClockAnalysis::kSOk;
    series.frequencyHresult = ClockAnalysis::kSOk;
    series.frequency = 48'000;
    series.storageReady = true;
    const std::uint32_t step = 4'800;
    for (std::uint32_t position = 0, sequence = 1; position <= frames; position += step, ++sequence) {
        ClockAnalysis::Snapshot sample;
        sample.direction = direction;
        sample.sequence = sequence;
        sample.position = position;
        sample.qpc100ns = baseQpc + static_cast<std::uint64_t>(position) * 10'000'000 / 48'000;
        series.snapshots.push_back(sample);
    }
}

bool BuildSyntheticMeasurement(std::uint32_t delayFrames, bool skipPacket,
                               ClockAnalysis::Status forcedClockStatus,
                               AnalogLoopback::Analysis& result, bool clipping = false,
                               bool timestampError = false, unsigned missingEvery = 0,
                               bool alternatePolarity = false,
                               SyntheticTiming timing = SyntheticTiming::Clean) {
    constexpr std::uint32_t frames = 190'000;
    constexpr std::uint64_t baseQpc = 10'000'000'000ULL;
    AnalogLoopback::RenderSignal render(1);
    if (!render.Configure(48'000, 1, 144, 5)) return false;
    AnalogLoopback::CaptureData capture(1);
    if (!capture.Configure(1, 48'000, frames, 2'048)) return false;
    std::array<std::int16_t, 288> interleaved{};
    for (std::uint32_t start = 0; start < frames; start += 144) {
        const auto packetFrames = std::min<std::uint32_t>(144, frames - start);
        std::fill(interleaved.begin(), interleaved.end(), 0);
        const std::int64_t sourceStart = static_cast<std::int64_t>(start) - delayFrames;
        if (sourceStart + packetFrames > 0) {
            const auto skip = sourceStart < 0 ? static_cast<std::uint32_t>(-sourceStart) : 0U;
            if (skip > packetFrames || !RangeFits(interleaved.size(), skip * 2,
                                                  static_cast<std::size_t>(packetFrames - skip) * 2)) return false;
            render.Fill(interleaved.data() + skip * 2, packetFrames - skip,
                static_cast<std::uint64_t>(std::max<std::int64_t>(0, sourceStart + skip)));
            for (std::uint32_t i = skip; i < packetFrames; ++i) {
                const auto sourceFrame = static_cast<std::uint64_t>(sourceStart + i);
                if (sourceFrame < render.FirstMarkerFrame()) continue;
                const auto relative = sourceFrame - render.FirstMarkerFrame();
                const auto markerIndex = static_cast<unsigned>(relative / AnalogLoopback::kMarkerSpacingFrames);
                const auto markerOffset = relative % AnalogLoopback::kMarkerSpacingFrames;
                if (markerIndex >= render.MarkerStarts().size() || markerOffset >= AnalogLoopback::kMarkerFrames) continue;
                auto& sample = interleaved[i * 2];
                if (missingEvery != 0 && markerIndex % missingEvery == 0) sample = 0;
                else if (alternatePolarity && markerIndex % 2 == 1) sample = static_cast<std::int16_t>(-sample);
            }
        }
        if (clipping && start == 0) interleaved[0] = std::numeric_limits<std::int16_t>::max();
        if (skipPacket && start == 20'016) continue;
        auto qpc = baseQpc + static_cast<std::uint64_t>(start) * 10'000'000 / 48'000;
        if (timing == SyntheticTiming::CaptureStartupJump && start == 0) qpc += 10'000;
        if (timing == SyntheticTiming::CaptureStep && start >= 48'000) qpc += 10'000;
        if (timing == SyntheticTiming::CaptureDrift)
            qpc += static_cast<std::uint64_t>(start) * 10'000'000 / 48'000 / 10'000;
        if (timing == SyntheticTiming::CaptureNegativeDrift)
            qpc -= static_cast<std::uint64_t>(start) * 10'000'000 / 48'000 / 10'000;
        capture.Append(interleaved.data(), packetFrames, false, false,
            timestampError && start == 20'016, start, qpc);
    }
    ClockAnalysis::Series captureClock;
    ClockAnalysis::Series renderClock;
    BuildClockSeries(captureClock, ClockAnalysis::Direction::Capture, baseQpc, frames);
    BuildClockSeries(renderClock, ClockAnalysis::Direction::Render, baseQpc, frames);
    if (timing == SyntheticTiming::RenderStartupClockJump) {
        for (auto& snapshot : renderClock.snapshots)
            if (snapshot.sequence == 2) snapshot.qpc100ns += 10'000;
    } else if (timing == SyntheticTiming::RenderClockStep) {
        for (auto& snapshot : renderClock.snapshots)
            if (snapshot.sequence >= 20) snapshot.qpc100ns += 10'000;
    }
    auto clockResult = ClockAnalysis::Analyze(captureClock, renderClock);
    if (forcedClockStatus != ClockAnalysis::Status::Available) clockResult.status = forcedClockStatus;
    result = AnalogLoopback::Analyze(capture, render, captureClock, renderClock, clockResult, true);
    return true;
}

bool TestTimingAndUnreliableCases() {
    AnalogLoopback::Analysis result;
    CHECK(BuildSyntheticMeasurement(192, false, ClockAnalysis::Status::Available, result));
    CHECK(result.status == AnalogLoopback::Status::Measured);
    CHECK(result.timingQuality == AnalogLoopback::Analysis::TimingQuality::Good);
    CHECK(result.delays.size() >= AnalogLoopback::kMinimumMarkers);
    CHECK(result.sequence.status == AnalogLoopback::SequenceStatus::Matched);
    CHECK(result.sequence.matchedMarkers >= AnalogLoopback::kMinimumMarkers);
    CHECK(std::abs(result.medianSamples - 192.0) < 1.0);
    CHECK(result.positionGaps == 0 && result.discontinuities == 0 && result.timestampErrors == 0);

    CHECK(BuildSyntheticMeasurement(192, false, ClockAnalysis::Status::Available, result,
        false, false, 0, false, SyntheticTiming::CaptureStartupJump));
    CHECK(result.sequence.status == AnalogLoopback::SequenceStatus::Matched);
    CHECK(result.capturePacketMaximumResidualFrames > 24.0);
    CHECK(result.timingQuality == AnalogLoopback::Analysis::TimingQuality::Unreliable);
    CHECK(result.status == AnalogLoopback::Status::Unreliable);

    CHECK(BuildSyntheticMeasurement(192, false, ClockAnalysis::Status::Available, result,
        false, false, 0, false, SyntheticTiming::RenderStartupClockJump));
    CHECK(result.sequence.status == AnalogLoopback::SequenceStatus::Matched);
    CHECK(result.delays.size() == result.sequence.matchedMarkers);
    CHECK(result.timingQuality == AnalogLoopback::Analysis::TimingQuality::Unreliable);
    CHECK(result.status == AnalogLoopback::Status::Unreliable);

    CHECK(BuildSyntheticMeasurement(192, false, ClockAnalysis::Status::Available, result,
        false, false, 0, false, SyntheticTiming::CaptureStep));
    CHECK(result.sequence.status == AnalogLoopback::SequenceStatus::Matched);
    CHECK(result.delays.size() >= AnalogLoopback::kMinimumMarkers);
    CHECK(result.capturePacketMaximumLocalIntervalDeviationFrames > 24.0);
    CHECK(result.timingQuality == AnalogLoopback::Analysis::TimingQuality::Unreliable);
    CHECK(result.status == AnalogLoopback::Status::Unreliable);

    CHECK(BuildSyntheticMeasurement(192, false, ClockAnalysis::Status::Available, result,
        false, false, 0, false, SyntheticTiming::CaptureDrift));
    CHECK(result.sequence.status == AnalogLoopback::SequenceStatus::Matched);
    CHECK(result.timingQuality == AnalogLoopback::Analysis::TimingQuality::Warning);
    CHECK(result.status == AnalogLoopback::Status::Unreliable);
    CHECK(result.delays.size() == result.sequence.matchedMarkers);
    CHECK(std::abs(result.delayTrendSamples) > AnalogLoopback::kGoodTimingErrorFrames);

    CHECK(BuildSyntheticMeasurement(192, false, ClockAnalysis::Status::Available, result,
        false, false, 0, false, SyntheticTiming::CaptureNegativeDrift));
    CHECK(result.sequence.status == AnalogLoopback::SequenceStatus::Matched);
    CHECK(result.timingQuality == AnalogLoopback::Analysis::TimingQuality::Warning);
    CHECK(result.status == AnalogLoopback::Status::Unreliable);
    CHECK(result.delays.size() == result.sequence.matchedMarkers);
    CHECK(result.delayTrendSamples < -AnalogLoopback::kGoodTimingErrorFrames);

    CHECK(BuildSyntheticMeasurement(192, false, ClockAnalysis::Status::Available, result,
        false, true));
    CHECK(result.sequence.status == AnalogLoopback::SequenceStatus::Matched);
    CHECK(result.timestampErrors != 0);
    CHECK(result.timingQuality == AnalogLoopback::Analysis::TimingQuality::Unreliable);
    CHECK(result.status == AnalogLoopback::Status::Unreliable);

    CHECK(BuildSyntheticMeasurement(192, false, ClockAnalysis::Status::Available, result,
        false, false, 0, false, SyntheticTiming::RenderClockStep));
    CHECK(result.sequence.status == AnalogLoopback::SequenceStatus::Matched);
    CHECK(result.delays.size() >= AnalogLoopback::kMinimumMarkers);
    CHECK(result.timingQuality == AnalogLoopback::Analysis::TimingQuality::Unreliable);
    CHECK(result.status == AnalogLoopback::Status::Unreliable);

    CHECK(BuildSyntheticMeasurement(192, false, ClockAnalysis::Status::Available, result,
        false, false, 4));
    CHECK(result.status == AnalogLoopback::Status::Measured);
    CHECK(result.sequence.matchedMarkers >= AnalogLoopback::kMinimumMarkers);
    CHECK(result.sequence.missingMarkers > 0 && result.sequence.contradictoryMarkers == 0);

    CHECK(BuildSyntheticMeasurement(17'722, false, ClockAnalysis::Status::Available, result));
    CHECK(result.status == AnalogLoopback::Status::Measured);
    CHECK(std::abs(result.medianSamples - 17'722.0) < 2.0);

    CHECK(BuildSyntheticMeasurement(192, false, ClockAnalysis::Status::Available, result,
        false, false, 0, true));
    CHECK(result.sequence.status != AnalogLoopback::SequenceStatus::Matched);
    CHECK(result.sequence.contradictoryMarkers > 0 || result.sequence.matchedMarkers < AnalogLoopback::kMinimumMarkers);
    CHECK(result.status != AnalogLoopback::Status::Measured);

    CHECK(BuildSyntheticMeasurement(192, true, ClockAnalysis::Status::Available, result));
    CHECK(result.status == AnalogLoopback::Status::Unreliable && result.positionGaps > 0);
    CHECK(result.framePositionGaps > 0);
    CHECK(BuildSyntheticMeasurement(192, false, ClockAnalysis::Status::InsufficientData, result));
    CHECK(result.status == AnalogLoopback::Status::Unreliable);
    CHECK(BuildSyntheticMeasurement(192, false, ClockAnalysis::Status::Available, result, true));
    CHECK(result.status == AnalogLoopback::Status::Unreliable && result.captureClippedSamples != 0);
    CHECK(BuildSyntheticMeasurement(192, false, ClockAnalysis::Status::Available, result, false, true));
    CHECK(result.status == AnalogLoopback::Status::Unreliable && result.timestampErrors != 0);
    CHECK(result.framePositionGaps == 0);

    AnalogLoopback::CaptureData silence(1);
    CHECK(silence.Configure(1, 48'000, 1'000, 16));
    silence.Append(nullptr, 1'000, true, false, false, 0, 1);
    AnalogLoopback::RenderSignal render(1);
    CHECK(render.Configure(48'000, 1, 144, 5));
    ClockAnalysis::Series captureClock;
    ClockAnalysis::Series renderClock;
    BuildClockSeries(captureClock, ClockAnalysis::Direction::Capture, 10'000'000'000ULL, 48'000);
    BuildClockSeries(renderClock, ClockAnalysis::Direction::Render, 10'000'000'000ULL, 48'000);
    const auto clock = ClockAnalysis::Analyze(captureClock, renderClock);
    const auto noMarker = AnalogLoopback::Analyze(silence, render, captureClock, renderClock, clock, true);
    CHECK(noMarker.status == AnalogLoopback::Status::SignalNotFound);
    return true;
}

bool TestSequenceRejectsNoiseAndUnrelatedPeriodicity() {
    constexpr std::uint32_t frames = 190'000;
    AnalogLoopback::RenderSignal render(1);
    CHECK(render.Configure(48'000, 1, 144, 5));
    std::vector<std::int16_t> noise(frames);
    std::uint32_t random = 0x7788AACC;
    for (auto& sample : noise) sample = NoiseSample(random, 1'500);
    const auto noiseDiagnostics = AnalogLoopback::DiagnoseMarkers(noise, render.Marker());
    const auto noiseSequence = AnalogLoopback::DetectCoherentSequence(noiseDiagnostics, render);
    CHECK(noiseSequence.status == AnalogLoopback::SequenceStatus::NotFound);

    std::vector<std::int16_t> periodic(frames, 0);
    for (std::size_t markerIndex = 0; markerIndex < render.MarkerStarts().size(); ++markerIndex) {
        const auto start = static_cast<std::size_t>(render.MarkerStarts()[markerIndex]);
        for (std::size_t i = 0; i < AnalogLoopback::kMarkerFrames && start + i < periodic.size(); ++i) {
            const double envelope = std::sin(3.141592653589793 * (i + 0.5) /
                AnalogLoopback::kMarkerFrames);
            const double carrier = std::sin(6.283185307179586 * i / 29.0);
            periodic[start + i] = static_cast<std::int16_t>(std::lrint(4'000 * envelope * carrier));
        }
    }
    const auto unrelatedDiagnostics = AnalogLoopback::DiagnoseMarkers(periodic, render.Marker());
    const auto unrelatedSequence = AnalogLoopback::DetectCoherentSequence(unrelatedDiagnostics, render);
    CHECK(unrelatedSequence.status == AnalogLoopback::SequenceStatus::NotFound);
    CHECK(unrelatedSequence.rejectionReason != nullptr);
    return true;
}

bool TestRepeatedSequenceIsAmbiguousAndCannotMeasure() {
    constexpr std::uint32_t frames = 550'000;
    constexpr std::uint64_t baseQpc = 25'000'000'000ULL;
    constexpr std::array<std::uint32_t, 2> offsets{1'000, 300'000};
    AnalogLoopback::RenderSignal render(1);
    CHECK(render.Configure(48'000, 1, 144, 5));
    std::vector<std::int16_t> mono(frames, 0);
    for (const auto base : offsets) {
        for (std::size_t markerIndex = 0; markerIndex < render.MarkerStarts().size(); ++markerIndex) {
            const auto start = static_cast<std::size_t>(base + render.MarkerStarts()[markerIndex]);
            for (std::size_t i = 0; i < render.Marker().size() && start + i < mono.size(); ++i)
                mono[start + i] = static_cast<std::int16_t>(
                    AnalogLoopback::RenderSignal::MarkerPolarity(markerIndex) * render.Marker()[i]);
        }
    }
    const auto correlations = AnalogLoopback::DiagnoseMarkers(mono, render.Marker());
    const auto sequence = AnalogLoopback::DetectCoherentSequence(correlations, render);
    CHECK(sequence.status == AnalogLoopback::SequenceStatus::Ambiguous);
    CHECK(sequence.matchedMarkers == render.MarkerStarts().size());
    CHECK(sequence.ambiguityMargin < AnalogLoopback::kSequenceMinimumAmbiguityMargin);

    AnalogLoopback::CaptureData capture(1);
    CHECK(capture.Configure(1, 48'000, frames, 4'000));
    std::array<std::int16_t, 288> packet{};
    for (std::uint32_t start = 0; start < frames; start += 144) {
        const auto count = std::min<std::uint32_t>(144, frames - start);
        CHECK(RangeFits(mono.size(), start, count));
        std::fill(packet.begin(), packet.end(), 0);
        CHECK(count <= packet.size() / 2);
        for (std::uint32_t i = 0; i < count; ++i) packet[i * 2] = mono[start + i];
        const auto qpc = baseQpc + static_cast<std::uint64_t>(start) * 10'000'000 / 48'000;
        CHECK(capture.Append(packet.data(), count, false, false, false, start, qpc));
    }
    ClockAnalysis::Series captureClock;
    ClockAnalysis::Series renderClock;
    BuildClockSeries(captureClock, ClockAnalysis::Direction::Capture, baseQpc, frames);
    BuildClockSeries(renderClock, ClockAnalysis::Direction::Render, baseQpc, frames);
    const auto clock = ClockAnalysis::Analyze(captureClock, renderClock);
    const auto analysis = AnalogLoopback::Analyze(capture, render,
        captureClock, renderClock, clock, true);
    CHECK(analysis.status == AnalogLoopback::Status::Unreliable);
    CHECK(analysis.sequence.status == AnalogLoopback::SequenceStatus::Ambiguous);
    CHECK(analysis.timingQuality == AnalogLoopback::Analysis::TimingQuality::Unreliable);
    return true;
}

bool TestCaptureTimelineInterceptAndLargeQpc() {
    constexpr std::uint32_t packetFrames = 1'000;
    constexpr std::uint64_t firstPosition = 4'000'000'000ULL;
    constexpr std::uint64_t qpcAtFirstPosition = 9'000'000'000'000'000ULL + 2'500'000ULL;
    std::array<std::int16_t, packetFrames * 2> pcm{};
    AnalogLoopback::CaptureData capture(1);
    CHECK(capture.Configure(1, 48'000, packetFrames * 16, 16));
    for (std::uint64_t i = 0; i < 16; ++i) {
        const auto position = firstPosition + i * packetFrames;
        const auto qpc = qpcAtFirstPosition + i * packetFrames * 10'000'000ULL / 48'000;
        CHECK(capture.Append(pcm.data(), packetFrames, false, false, false,
            position, qpc));
    }
    const auto timeline = AnalogLoopback::FitCaptureTimeline(capture.Packets(), 48'000);
    CHECK(timeline.reliable);
    CHECK(std::abs(timeline.intercept100ns) > 1.0L);
    double estimatedQpc = 0;
    CHECK(AnalogLoopback::CaptureQpcAtFrame(capture, timeline, 7'500, estimatedQpc));
    const long double expectedQpc = static_cast<long double>(qpcAtFirstPosition) +
        7'500.0L * 10'000'000.0L / 48'000.0L;
    CHECK(std::abs(static_cast<long double>(estimatedQpc) - expectedQpc) <= 2.0L);

    auto discontinuous = capture.Packets();
    discontinuous[8].devicePosition += 1;
    CHECK(!AnalogLoopback::FitCaptureTimeline(discontinuous, 48'000).reliable);
    auto storedGap = capture.Packets();
    storedGap[8].firstStoredFrame += 1;
    CHECK(!AnalogLoopback::FitCaptureTimeline(storedGap, 48'000).reliable);
    auto badTimestamp = capture.Packets();
    badTimestamp[8].qpc100ns = badTimestamp[7].qpc100ns;
    CHECK(!AnalogLoopback::FitCaptureTimeline(badTimestamp, 48'000).reliable);
    return true;
}

bool TestTimestampAnomalyDiagnostics() {
    constexpr std::uint32_t packetFrames = 144;
    constexpr std::size_t packetCount = 1'600;
    constexpr std::size_t stepPacket = 600;
    constexpr std::uint64_t baseQpc = 40'000'000'000ULL;
    const auto makePackets = [&](bool persistent, bool transient) {
        std::vector<AnalogLoopback::CapturePacket> packets;
        packets.reserve(packetCount);
        for (std::size_t i = 0; i < packetCount; ++i) {
            const auto position = static_cast<std::uint64_t>(i) * packetFrames;
            auto qpc = baseQpc + position * 10'000'000 / 48'000;
            if (persistent && i >= stepPacket) qpc += 10'000;
            if (transient && i == stepPacket) qpc += 10'000;
            packets.push_back({position, position, qpc, packetFrames, false, false, false});
        }
        return packets;
    };

    auto transientPackets = makePackets(false, true);
    auto transientTimeline = AnalogLoopback::FitCaptureTimeline(transientPackets, 48'000);
    ClockAnalysis::Series emptyCaptureClock;
    ClockAnalysis::Series emptyRenderClock;
    auto transient = AnalogLoopback::AnalyzeTimestampAnomalies(transientPackets, 48'000,
        transientTimeline.intercept100ns, transientTimeline.qpcUnitsPerFrame,
        emptyCaptureClock, {}, emptyRenderClock, {});
    CHECK(transient.capturePacketCount == AnalogLoopback::kTimestampAnomalyReportCount);
    bool sawPositiveStep = false;
    bool sawNegativeRecovery = false;
    bool sawRecoveredState = false;
    for (std::size_t i = 0; i < transient.capturePacketCount; ++i) {
        const auto& anomaly = transient.capturePackets[i];
        if (anomaly.packetIndex == stepPacket) {
            sawPositiveStep = anomaly.signedResidualMilliseconds > 0.5;
            sawRecoveredState = anomaly.offsetState == AnalogLoopback::CaptureOffsetState::Recovered;
        }
        if (anomaly.packetIndex == stepPacket + 1)
            sawNegativeRecovery = anomaly.signedResidualMilliseconds < -0.5;
    }
    CHECK(sawPositiveStep && sawNegativeRecovery && sawRecoveredState);

    ClockAnalysis::Series transientClock;
    ClockAnalysis::Series cleanPeerClock;
    BuildClockSeries(transientClock, ClockAnalysis::Direction::Capture, baseQpc, 240'000);
    BuildClockSeries(cleanPeerClock, ClockAnalysis::Direction::Render, baseQpc, 240'000);
    for (auto& sample : transientClock.snapshots)
        if (sample.sequence == 20) sample.qpc100ns += 10'000;
    const auto transientClockFit = ClockAnalysis::Analyze(transientClock, cleanPeerClock);
    CHECK(transientClockFit.hasRegression);
    const auto clockTransient = AnalogLoopback::AnalyzeTimestampAnomalies(
        std::span<const AnalogLoopback::CapturePacket>{}, 48'000, 0, 0,
        transientClock, transientClockFit.capture, cleanPeerClock, transientClockFit.render);
    bool sawClockJumpAndRecovery = false;
    for (std::size_t i = 0; i < clockTransient.captureClockResidualCount; ++i) {
        const auto& anomaly = clockTransient.captureClockResiduals[i];
        if (anomaly.sequence == 20)
            sawClockJumpAndRecovery = anomaly.signedAdjacentQpcStepMilliseconds > 0.5 &&
                anomaly.recoveryDeltaMilliseconds < -0.5 &&
                std::abs(anomaly.signedAdjacentQpcStepMilliseconds +
                         anomaly.recoveryDeltaMilliseconds) < 0.125;
    }
    CHECK(sawClockJumpAndRecovery);

    auto persistentPackets = makePackets(true, false);
    auto persistentTimeline = AnalogLoopback::FitCaptureTimeline(persistentPackets, 48'000);
    const auto persistent = AnalogLoopback::AnalyzeTimestampAnomalies(persistentPackets, 48'000,
        persistentTimeline.intercept100ns, persistentTimeline.qpcUnitsPerFrame,
        emptyCaptureClock, {}, emptyRenderClock, {});
    bool sawPersistentOffset = false;
    for (std::size_t i = 0; i < persistent.capturePacketCount; ++i) {
        const auto& anomaly = persistent.capturePackets[i];
        if (anomaly.packetIndex == stepPacket)
            sawPersistentOffset = anomaly.signedResidualMilliseconds > 0.5 &&
                anomaly.offsetState == AnalogLoopback::CaptureOffsetState::Persistent;
    }
    CHECK(sawPersistentOffset);

    ClockAnalysis::Series captureClock;
    ClockAnalysis::Series renderClock;
    BuildClockSeries(captureClock, ClockAnalysis::Direction::Capture, baseQpc, 240'000);
    BuildClockSeries(renderClock, ClockAnalysis::Direction::Render, baseQpc, 240'000);
    for (auto& sample : captureClock.snapshots)
        if (sample.sequence >= 19) sample.qpc100ns += 10'000;
    for (auto& sample : renderClock.snapshots)
        if (sample.sequence >= 19) sample.qpc100ns += 10'000;
    const auto clockFit = ClockAnalysis::Analyze(captureClock, renderClock);
    CHECK(clockFit.hasRegression);
    const auto common = AnalogLoopback::AnalyzeTimestampAnomalies(persistentPackets, 48'000,
        persistentTimeline.intercept100ns, persistentTimeline.qpcUnitsPerFrame,
        captureClock, clockFit.capture, renderClock, clockFit.render);
    CHECK(common.captureClockResidualCount == AnalogLoopback::kTimestampAnomalyReportCount);
    CHECK(common.renderClockResidualCount == AnalogLoopback::kTimestampAnomalyReportCount);
    CHECK(common.captureClockStrongestStep.valid && common.renderClockStrongestStep.valid);
    CHECK(common.captureClockMatch.matched && common.renderClockMatch.matched);
    CHECK(common.commonMode == AnalogLoopback::CommonModeState::PresentInAllThree);
    CHECK(common.captureClockMatch.captureStepMilliseconds > 0 &&
        common.captureClockMatch.clockStepMilliseconds > 0);
    return true;
}

bool TestCaptureMetricsAndAmbiguity() {
    AnalogLoopback::CaptureData selectedRight(2);
    CHECK(selectedRight.Configure(2, 48'000, 2, 2));
    const std::int16_t stereo[]{100, -200, 300, -400};
    CHECK(selectedRight.Append(stereo, 2, false, false, false, 0, 1));
    CHECK(selectedRight.Samples()[0] == -200 && selectedRight.Samples()[1] == -400);

    AnalogLoopback::CaptureData capture(1);
    CHECK(capture.Configure(1, 48'000, 8, 4));
    const std::int16_t clipped[]{-32768, 0, 32767, 0};
    capture.Append(clipped, 2, false, false, false, 0, 1);
    CHECK(capture.ClippedSamples() == 2);
    CHECK(capture.PeakDbfs() == 0.0);
    CHECK(std::isfinite(capture.RmsDbfs()));
    capture.EraseSamples();
    CHECK(capture.StoredFrames() == 0 && capture.Samples().empty());

    AnalogLoopback::RenderSignal render(1);
    CHECK(render.Configure(48'000, 1, 144, 5));
    std::vector<std::int16_t> ambiguous(2'000, 0);
    CHECK(RangeFits(ambiguous.size(), 100, render.Marker().size()));
    CHECK(RangeFits(ambiguous.size(), 900, render.Marker().size()));
    std::copy(render.Marker().begin(), render.Marker().end(), ambiguous.begin() + 100);
    std::copy(render.Marker().begin(), render.Marker().end(), ambiguous.begin() + 900);
    const auto peaks = AnalogLoopback::FindMarkers(ambiguous, render.Marker());
    CHECK(peaks.size() >= 2);
    std::vector<std::size_t> markerIndices;
    CHECK(!AnalogLoopback::AlignMarkerIndices(peaks, render.MarkerStarts().size(), markerIndices));

    std::array<AnalogLoopback::CapturePacket, 8> badRate{};
    for (std::size_t i = 0; i < badRate.size(); ++i) {
        badRate[i].devicePosition = i * 4'800;
        badRate[i].qpc100ns = 1'000'000'000ULL + static_cast<std::uint64_t>(i) * 4'800 * 10'000'000 / 48'048;
    }
    CHECK(!AnalogLoopback::FitCaptureTimeline(badRate, 48'000).reliable);
    return true;
}

} // namespace

int main() {
    return TestMarkerOutputAndCorrelation() && TestFractionalAmplitudeNoiseAndNoSignal() &&
        TestFilteredAnalogSequenceDetection() && TestTimingAndUnreliableCases() &&
        TestSequenceRejectsNoiseAndUnrelatedPeriodicity() &&
        TestRepeatedSequenceIsAmbiguousAndCannotMeasure() &&
        TestCaptureTimelineInterceptAndLargeQpc() && TestTimestampAnomalyDiagnostics() &&
        TestCaptureMetricsAndAmbiguity() ? 0 : 1;
}
