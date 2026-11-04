#include "../src/probe/live_transport.h"
#include "../src/probe/signal_levels.h"

#include <cmath>
#include <cstdio>
#include <cstdint>

namespace {

#define CHECK(condition) do { if (!(condition)) { std::fprintf(stderr, "Check failed at line %d: %s\n", __LINE__, #condition); return false; } } while (false)

bool Near(double actual, double expected, double tolerance = 0.01) {
    return std::abs(actual - expected) <= tolerance;
}

bool CheckSilenceAndEmpty() {
    SignalLevels::Accumulator emptyCapture(true);
    SignalLevels::Accumulator emptyRender(false);
    const auto emptyC = emptyCapture.Finish();
    const auto emptyR = emptyRender.Finish();
    CHECK(std::isinf(emptyC.channels[0].rmsDbfs) && emptyC.channels[0].rmsDbfs < 0);
    CHECK(SignalLevels::Classify(emptyC, emptyR) == SignalLevels::Status::InsufficientData);

    SignalLevels::Accumulator capture(true);
    SignalLevels::Accumulator render(false);
    capture.CapturePacket(nullptr, 4, true);
    const std::int16_t zeros[]{0, 0, 0, 0};
    render.RenderBuffer(zeros, 2, false, 0, 0);
    const auto c = capture.Finish();
    const auto r = render.Finish();
    CHECK(c.silentFlagCount == 1 && c.zeroDataPacketsOrBuffers == 1);
    CHECK(c.channels[0].analyzedSamples == 4 && c.channels[0].nonzeroSamples == 0);
    CHECK(r.silentFlagCount == 0 && r.zeroDataPacketsOrBuffers == 1);
    CHECK(SignalLevels::Classify(c, r) == SignalLevels::Status::DigitalSilence);
    CHECK(std::isinf(c.channels[0].peakDbfs) && c.channels[0].peakDbfs < 0);
    return true;
}

bool CheckChannelsAndKnownLevels() {
    SignalLevels::Accumulator leftOnly(true);
    const std::int16_t left[]{16384, 0, -16384, 0};
    leftOnly.CapturePacket(left, 2, false);
    auto l = leftOnly.Finish();
    CHECK(l.channels[0].peakMagnitude == 16384 && l.channels[1].peakMagnitude == 0);
    CHECK(Near(l.channels[0].peakDbfs, -6.0206));
    CHECK(Near(l.channels[0].rmsDbfs, -6.0206));
    CHECK(SignalLevels::HasSingleChannelSignal(l));
    CHECK(l.channels[0].analyzedSamples == 2 && l.activeFrames == 2);

    SignalLevels::Accumulator rightOnly(false);
    const std::int16_t right[]{0, 8192, 0, -8192};
    rightOnly.RenderBuffer(right, 2, false, 0, 0);
    const auto rr = rightOnly.Finish();
    CHECK(rr.channels[0].nonzeroSamples == 0 && rr.channels[1].nonzeroSamples == 2);
    CHECK(SignalLevels::HasSingleChannelSignal(rr));
    CHECK(Near(rr.channels[1].peakDbfs, -12.0412));

    SignalLevels::Accumulator stereo(true);
    const std::int16_t samples[]{-32768, 16384, 0, 0};
    stereo.CapturePacket(samples, 2, false);
    const auto s = stereo.Finish();
    CHECK(s.channels[0].peakDbfs == 0.0);
    CHECK(Near(s.channels[0].rmsDbfs, -3.0103));
    CHECK(Near(s.channels[1].peakDbfs, -6.0206));
    CHECK(Near(s.channels[1].rmsDbfs, -9.0309));
    CHECK(SignalLevels::Classify(l, rr) == SignalLevels::Status::SignalPresent);
    return true;
}

bool CheckRenderFlagsAndImmutability() {
    SignalLevels::Accumulator render(false);
    const std::int16_t samples[]{4096, -2048};
    const std::int16_t original[]{4096, -2048};
    render.RenderBuffer(samples, 1, true, 0, 0);
    CHECK(samples[0] == original[0] && samples[1] == original[1]);
    auto metrics = render.Finish();
    CHECK(metrics.silentFlagCount == 1 && metrics.zeroDataPacketsOrBuffers == 0);
    CHECK(metrics.channels[0].nonzeroSamples == 1 && metrics.channels[1].nonzeroSamples == 1);

    LiveTransport::SpscPcm16StereoRing ring;
    CHECK(ring.Configure(1, 1, 48'000));
    const std::int16_t input[]{32767, -32768, 16384, -16384, 8192, -8192};
    std::int16_t unchanged[6]{};
    for (int i = 0; i < 6; ++i) unchanged[i] = input[i];
    CHECK(ring.Push(input, 3, false));
    std::int16_t output[6]{};
    const auto pull = ring.Pull(output, 3);
    CHECK(pull.consumedFrames == 3);
    for (int i = 0; i < 6; ++i) CHECK(input[i] == unchanged[i]);
    SignalLevels::Accumulator attenuated(false);
    attenuated.RenderBuffer(output, 3, false, 0, 0);
    const auto attenuatedMetrics = attenuated.Finish();
    CHECK(Near(attenuatedMetrics.channels[0].peakDbfs, -24.0, 0.1));
    CHECK(Near(attenuatedMetrics.channels[1].peakDbfs, -24.0, 0.1));

    SignalLevels::Accumulator quietCapture(true);
    SignalLevels::Accumulator quietRender(false);
    const std::int16_t quiet[]{1, 0};
    quietCapture.CapturePacket(quiet, 1, false);
    quietRender.RenderBuffer(quiet, 1, false, 0, 0);
    const auto quietC = quietCapture.Finish();
    const auto quietR = quietRender.Finish();
    CHECK(SignalLevels::HasLowSignalLevel(quietC));
    CHECK(SignalLevels::Classify(quietC, quietR) == SignalLevels::Status::SignalPresent);
    return true;
}

} // namespace

int main() {
    return CheckSilenceAndEmpty() && CheckChannelsAndKnownLevels() &&
        CheckRenderFlagsAndImmutability() ? 0 : 1;
}
