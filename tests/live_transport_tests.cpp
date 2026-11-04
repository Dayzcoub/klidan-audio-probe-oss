#include "../src/probe/live_transport.h"

#include <algorithm>
#include <array>
#include <atomic>
#include <cmath>
#include <cstdint>
#include <thread>

namespace {

std::int16_t Attenuate(std::int16_t sample) {
    return static_cast<std::int16_t>(std::lrint(static_cast<double>(sample) * LiveTransport::kOutputGain));
}

bool TestPrefillAndStereoFifo() {
    LiveTransport::SpscPcm16StereoRing ring;
    if (!ring.Configure(1, 1, 48'000)) return false;
    std::array<std::int16_t, 8> output{};
    auto read = ring.Pull(output.data(), 2);
    if (!read.prefillSilence || read.underrun || read.silenceFrames != 2) return false;
    for (const auto sample : output) if (sample != 0) return false;

    const std::array<std::int16_t, 6> first{1000, -2000, 3000, -4000, 5000, -6000};
    ring.RecordCaptureFramesReceived(1);
    if (!ring.Push(first.data(), 1, false)) return false;
    read = ring.Pull(output.data(), 2);
    if (!read.prefillSilence) return false;
    const std::array<std::int16_t, 4> second{7000, -8000, 9000, -10000};
    if (!ring.Push(second.data(), 2, false)) return false;
    const std::array<std::int16_t, 2> third{11000, -12000};
    if (!ring.Push(third.data(), 1, false)) return false;
    read = ring.Pull(output.data(), 4);
    if (!read.prefillCompleted || read.consumedFrames != 4 || read.silenceFrames != 0) return false;
    const std::array<std::int16_t, 8> expected{
        Attenuate(1000), Attenuate(-2000), Attenuate(7000), Attenuate(-8000),
        Attenuate(9000), Attenuate(-10000), Attenuate(11000), Attenuate(-12000)};
    if (output != expected) return false;

    const std::array<std::int16_t, 10> variable{13000, -14000, 15000, -16000, 17000,
                                                -18000, 19000, -20000, 21000, -22000};
    if (!ring.Push(variable.data(), 1, false) || !ring.Push(variable.data() + 2, 4, false)) return false;
    read = ring.Pull(output.data(), 5);
    if (read.consumedFrames != 5 || output[0] != Attenuate(13000) ||
        output[1] != Attenuate(-14000) || output[8] != Attenuate(21000) ||
        output[9] != Attenuate(-22000)) return false;
    return true;
}

bool TestSilentFlagAndUnderrun() {
    LiveTransport::SpscPcm16StereoRing ring;
    if (!ring.Configure(1, 1, 48'000)) return false;
    const std::array<std::int16_t, 6> zeros{};
    if (!ring.Push(nullptr, 3, true)) return false;
    std::array<std::int16_t, 10> output{};
    auto read = ring.Pull(output.data(), 3);
    if (!read.prefillCompleted || read.consumedFrames != 3) return false;
    for (std::size_t i = 0; i < 6; ++i) if (output[i] != zeros[i]) return false;
    read = ring.Pull(output.data(), 2);
    const auto stats = ring.GetStats();
    return read.underrun && read.silenceFrames == 2 && stats.silenceFrames == 2 &&
           stats.underrunEvents == 1 && stats.underrunFrames == 2;
}

bool TestOverflowDropsOldestAndReinitialize() {
    LiveTransport::SpscPcm16StereoRing ring;
    if (!ring.Configure(1, 1, 48'000)) return false;
    std::array<std::int16_t, 20> samples{};
    for (int i = 0; i < 10; ++i) {
        samples[i * 2] = static_cast<std::int16_t>((i + 1) * 1000);
        samples[i * 2 + 1] = static_cast<std::int16_t>(-(i + 1) * 1000);
    }
    if (!ring.Push(samples.data(), 10, false)) return false;
    std::array<std::int16_t, 16> output{};
    auto read = ring.Pull(output.data(), 8);
    auto stats = ring.GetStats();
    if (!read.prefillCompleted || read.consumedFrames != 8 || stats.overflowEvents != 1 ||
        stats.overflowFrames != 2 || stats.capacityFrames != 8 || stats.prefillFrames != 3 ||
        stats.captureBufferFrames != 1 || stats.renderBufferFrames != 1 ||
        stats.occupancyMinimum != 0 || stats.occupancyMaximum != 8 ||
        stats.occupancyCurrent != 0 || stats.occupancyAverage <= 0) return false;
    if (output[0] != Attenuate(3000) || output[1] != Attenuate(-3000) ||
        output[14] != Attenuate(10000) || output[15] != Attenuate(-10000)) return false;
    if (!ring.Configure(4, 2, 48'000)) return false;
    ring.RecordPrefillDuration(9'500);
    stats = ring.GetStats();
    return stats.framesTransferred == 0 && stats.overflowFrames == 0 &&
           stats.prefillFrames == 12 && stats.capacityFrames == 32 &&
           stats.prefillDurationMicroseconds == 9'500;
}

bool TestPcm16LimitsAndCapacityBound() {
    LiveTransport::SpscPcm16StereoRing ring;
    if (ring.Configure(UINT32_MAX, UINT32_MAX, 48'000) || !ring.Configure(1, 1, 48'000)) return false;
    const std::array<std::int16_t, 6> samples{INT16_MAX, INT16_MIN, 12345, -23456, -1, 1};
    if (!ring.Push(samples.data(), 3, false)) return false;
    std::array<std::int16_t, 6> output{};
    const auto read = ring.Pull(output.data(), 3);
    return read.consumedFrames == 3 && output[0] == Attenuate(INT16_MAX) &&
        output[1] == Attenuate(INT16_MIN) && output[2] == Attenuate(12345) &&
        output[3] == Attenuate(-23456) && output[4] == Attenuate(-1) &&
        output[5] == Attenuate(1);
}

bool TestBoundedFrameProvenance() {
    LiveTransport::SpscPcm16StereoRing ring;
    if (!ring.Configure(1, 1, 48'000)) return false;
    const std::array<std::int16_t, 4> source{1000, -1000, 2000, -2000};
    std::array<std::int16_t, 8> output{};
    std::array<std::uint64_t, 4> tags{};
    if (!ring.Push(source.data(), 2, false, 100)) return false;
    auto pull = ring.Pull(output.data(), 3, false, tags.data());
    if (!pull.prefillSilence || std::any_of(tags.begin(), tags.begin() + 3,
        [](auto tag) { return tag != LiveTransport::kNoSourceFrame; })) return false;
    if (!ring.Push(source.data() + 2, 2, false, 102)) return false;
    pull = ring.Pull(output.data(), 4, false, tags.data());
    if (pull.consumedFrames != 4 || tags != std::array<std::uint64_t, 4>{100, 101, 102, 103}) return false;
    pull = ring.Pull(output.data(), 1, false, tags.data());
    return pull.underrun && tags[0] == LiveTransport::kNoSourceFrame;
}

bool TestForceSilencePreservesRealLoss() {
    LiveTransport::SpscPcm16StereoRing ring;
    const std::array<std::int16_t, 8> source{1000, -1000, 2000, -2000,
                                             3000, -3000, 4000, -4000};
    std::array<std::int16_t, 8> output{};
    if (!ring.Configure(1, 1, 48'000) || !ring.Push(source.data(), 4, false)) return false;
    if (!ring.Pull(output.data(), 3).prefillCompleted) return false;
    auto pull = ring.Pull(output.data(), 2);
    if (!pull.underrun || pull.consumedFrames != 1) return false;
    ring.ForceSilenceAfterPull(pull, 2, true);
    const auto stats = ring.GetStats();
    return stats.underrunEvents == 1 && stats.underrunFrames == 1 &&
        stats.renderFramesConsumed == 3 && stats.silenceFrames == 2 &&
        pull.consumedFrames == 0 && pull.silenceFrames == 2;
}

bool TestAtomicProducerConsumerStress() {
    LiveTransport::SpscPcm16StereoRing ring;
    constexpr std::uint64_t totalFrames = 30'000;
    if (!ring.Configure(64, 64, 48'000)) return false;
    std::atomic<bool> producerDone{false};
    std::atomic<bool> valid{true};
    std::thread producer([&] {
        std::array<std::int16_t, 62> packet{};
        std::uint64_t produced = 0;
        while (produced < totalFrames) {
            while (ring.Occupancy() > 512 - 32) std::this_thread::yield();
            const auto count = static_cast<std::uint32_t>(std::min<std::uint64_t>(
                1 + (produced % 31), totalFrames - produced));
            for (std::uint32_t i = 0; i < count; ++i) {
                const auto code = static_cast<std::int16_t>(10000 + ((produced + i) % 512) * 32);
                packet[i * 2] = code;
                packet[i * 2 + 1] = static_cast<std::int16_t>(-code);
            }
            if (!ring.Push(packet.data(), count, false)) valid.store(false);
            produced += count;
        }
        producerDone.store(true, std::memory_order_release);
    });

    std::uint64_t consumed = 0;
    std::array<std::int16_t, 46> output{};
    while (!producerDone.load(std::memory_order_acquire) || ring.Occupancy() != 0) {
        if (!producerDone.load(std::memory_order_acquire) && ring.Occupancy() < 32) {
            std::this_thread::yield();
            continue;
        }
        const auto available = ring.Occupancy();
        if (available == 0) continue;
        const auto count = static_cast<std::uint32_t>(std::min<std::uint64_t>(
            23, producerDone.load(std::memory_order_acquire) ? available : 23));
        const auto read = ring.Pull(output.data(), count);
        if (read.prefillSilence) continue;
        for (std::uint32_t i = 0; i < count; ++i) {
            const auto code = static_cast<std::int16_t>(10000 + (consumed % 512) * 32);
            if (output[i * 2] != Attenuate(code) || output[i * 2 + 1] != Attenuate(-code))
                valid.store(false, std::memory_order_relaxed);
            ++consumed;
        }
    }
    producer.join();
    const auto stats = ring.GetStats();
    return valid.load() && consumed == totalFrames && stats.framesTransferred == totalFrames &&
        stats.renderFramesConsumed == totalFrames && stats.overflowFrames == 0 &&
        stats.underrunEvents == 0;
}

bool TestMonitorClassification() {
    LiveTransport::Stats stats;
    stats.prefillCompleted = true;
    stats.framesTransferred = 10;
    stats.renderFramesConsumed = 10;
    if (LiveTransport::ClassifyMonitor(true, true, false, false, stats) !=
        LiveTransport::MonitorStatus::Completed) return false;
    stats.underrunEvents = 1;
    if (LiveTransport::ClassifyMonitor(true, true, false, false, stats) !=
        LiveTransport::MonitorStatus::CompletedWithWarnings) return false;
    auto cleanStats = stats;
    cleanStats.underrunEvents = 0;
    cleanStats.prefillCompleted = true;
    cleanStats.framesTransferred = 10;
    cleanStats.renderFramesConsumed = 10;
    if (LiveTransport::ClassifyMonitor(true, true, false, true, cleanStats) !=
        LiveTransport::MonitorStatus::CompletedWithWarnings) return false;
    if (LiveTransport::ClassifyMonitor(false, true, false, false, stats) !=
        LiveTransport::MonitorStatus::Failed) return false;
    if (LiveTransport::ClassifyMonitor(true, false, false, false, stats) !=
        LiveTransport::MonitorStatus::Failed) return false;
    if (LiveTransport::ClassifyMonitor(true, true, true, false, stats) !=
        LiveTransport::MonitorStatus::Failed) return false;
    stats.prefillCompleted = false;
    return LiveTransport::ClassifyMonitor(true, true, false, false, stats) ==
        LiveTransport::MonitorStatus::Failed;
}

} // namespace

int main() {
    if (!TestPrefillAndStereoFifo()) return 1;
    if (!TestSilentFlagAndUnderrun()) return 2;
    if (!TestOverflowDropsOldestAndReinitialize()) return 3;
    if (!TestPcm16LimitsAndCapacityBound()) return 4;
    if (!TestBoundedFrameProvenance()) return 5;
    if (!TestForceSilencePreservesRealLoss()) return 6;
    if (!TestAtomicProducerConsumerStress()) return 7;
    if (!TestMonitorClassification()) return 8;
    return 0;
}
