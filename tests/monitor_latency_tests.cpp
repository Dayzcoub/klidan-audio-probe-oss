#include "../src/probe/monitor_latency.h"

#include <algorithm>
#include <array>
#include <cmath>
#include <cstdio>
#include <cstdint>
#include <vector>

namespace {

constexpr std::uint32_t kRate = 48'000;
constexpr std::uint32_t kBuffer = 144;
constexpr std::uint64_t kBaseQpc = 8'000'000'000'000ULL;
constexpr std::uint64_t kRenderFrameOrigin = 73'000;

std::uint64_t QpcForFrame(std::uint64_t frame) {
    return kBaseQpc + static_cast<std::uint64_t>(std::llround(
        static_cast<long double>(frame) * 10'000'000.0L / kRate));
}

bool BuildRun(unsigned delayFrames, bool captureStep,
              std::size_t anchorLimit, bool omitMostMarkers,
              MonitorLatency::RunData& data, ClockAnalysis::Series& captureClock,
              AnalogLoopback::RenderSignal& signal,
              std::uint64_t packetOrigin = 0, std::uint64_t captureClockOrigin = 0) {
    if (!data.Configure(2'000, MonitorLatency::kMaximumMarkers) ||
        !signal.Configure(kRate, 1, 3 * kBuffer, 5)) return false;
    const auto starts = signal.MarkerStarts();
    data.expectedMarkers = starts.size();
    const auto stepAtFrame = starts.size() > 9 ? starts[9] + 96 : 5ULL * kRate / 2;
    const std::uint64_t totalFrames = 5ULL * kRate;
    for (std::uint64_t frame = 0, index = 0; frame < totalFrames && index < anchorLimit;
         frame += kBuffer, ++index) {
        const auto step = captureStep && frame >= stepAtFrame ? 10'000ULL : 0ULL;
        if (!data.RecordCapturePacket({frame + packetOrigin, QpcForFrame(frame) + step,
                frame, kBuffer, false, false, QpcForFrame(frame) + step + 100})) return false;
    }
    for (std::size_t marker = 0; marker < starts.size(); ++marker) {
        const auto sourceFrame = starts[marker];
        const auto packetIndex = static_cast<std::size_t>(sourceFrame / kBuffer);
        if (!data.RecordCaptureMarker({static_cast<std::uint32_t>(marker), sourceFrame,
                sourceFrame + packetOrigin,
                QpcForFrame(sourceFrame) + (captureStep && sourceFrame >= stepAtFrame ? 10'000 : 0),
                QpcForFrame(sourceFrame) + 150, QpcForFrame(sourceFrame) + 180,
                QpcForFrame(sourceFrame) + 180, QpcForFrame(sourceFrame) + 200,
                3 * kBuffer, packetIndex, true})) return false;
    }
    for (std::size_t marker = 0; marker < starts.size(); ++marker) {
        if (omitMostMarkers && marker % 4 != 0) continue;
        const auto firstEndQpc = QpcForFrame(starts[marker] + delayFrames) + 200;
        const auto lastFrame = starts[marker] + delayFrames +
            AnalogLoopback::kMarkerFrames - 1;
        const auto fullEndQpc = QpcForFrame(lastFrame) + 200;
        data.RecordRenderMarker({static_cast<std::uint32_t>(marker), starts[marker],
            starts[marker] + delayFrames + kRenderFrameOrigin,
            starts[marker] + delayFrames + kRenderFrameOrigin + AnalogLoopback::kMarkerFrames - 1,
            2 * kBuffer,
            firstEndQpc - 100, firstEndQpc,
            fullEndQpc - 100, fullEndQpc, true});
    }

    captureClock.direction = ClockAnalysis::Direction::Capture;
    captureClock.serviceHresult = ClockAnalysis::kSOk;
    captureClock.frequencyHresult = ClockAnalysis::kSOk;
    captureClock.frequency = kRate;
    for (std::uint64_t frame = 0, index = 0; frame < totalFrames && index < anchorLimit;
         frame += kBuffer, ++index) {
        const auto step = captureStep && frame >= stepAtFrame ? 10'000ULL : 0ULL;
        captureClock.snapshots.push_back({ClockAnalysis::Direction::Capture,
            static_cast<std::uint32_t>(index + 1), frame + captureClockOrigin, QpcForFrame(frame) + step,
            ClockAnalysis::kSOk});
        if (!data.RecordCaptureClockQpcBracket(true)) return false;
    }
    return true;
}

LiveTransport::Stats CleanRingStats() {
    LiveTransport::Stats stats;
    stats.prefillCompleted = true;
    stats.prefillFrames = 3 * kBuffer;
    stats.rate = kRate;
    return stats;
}

bool TestSoftwareSubmissionIntervalsAndIndependentStarts() {
    for (const auto delay : {96U, 192U, 480U}) {
        MonitorLatency::RunData data;
        ClockAnalysis::Series captureClock;
        AnalogLoopback::RenderSignal signal(1);
        if (!BuildRun(delay, false, 2'000, false, data, captureClock, signal)) return false;
        std::reverse(data.renderMarkers.begin(), data.renderMarkers.end());
        const auto result = MonitorLatency::Analyze(data, captureClock, kRate, true, false,
                                                    CleanRingStats());
        if (result.status == MonitorLatency::Status::Measured || result.absoluteTimelineResolved ||
            !result.softwareTransportMeasured ||
            result.sequenceStatus != MonitorLatency::SequenceStatus::Matched ||
            result.softwareTransportSamples != data.expectedMarkers ||
            result.firstFrameHandoffSamples != data.expectedMarkers ||
            std::abs(result.firstFrameHandoffMedianMilliseconds - delay * 1'000.0 / kRate) > 0.0001 ||
            std::abs(result.softwareTransportMedianMilliseconds -
                (delay + AnalogLoopback::kMarkerFrames - 1) * 1'000.0 / kRate) > 0.0001 ||
            result.firstFrameHandoffSpreadMilliseconds != 0.0 ||
            result.fullMarkerHandoffSpreadMilliseconds != 0.0) return false;
        if (result.markerDiagnosticCount != data.expectedMarkers ||
            std::any_of(result.markerDiagnostics.begin(),
                result.markerDiagnostics.begin() + result.markerDiagnosticCount,
                [](const auto& marker) {
                    return marker.packetServiceQpc100ns == 0 ||
                        marker.markerGenerationEndQpc100ns < marker.packetServiceQpc100ns ||
                        marker.ringPushEndQpc100ns < marker.markerGenerationEndQpc100ns ||
                        marker.renderSubmissionEndQpc100ns < marker.ringPushEndQpc100ns;
                })) return false;
        const auto& first = result.markerDiagnostics[0];
        if (first.markerQpc100ns <= first.packetQpc100ns ||
            std::abs((first.pushMinusMarkerBeginMilliseconds +
                (static_cast<double>(first.markerQpc100ns - first.packetQpc100ns) / 10'000.0)) -
                first.pushMinusPacketBeginMilliseconds) > 0.0002) return false;
    }
    return true;
}

bool TestIndependentCapturePositionOrigins() {
    MonitorLatency::RunData data;
    ClockAnalysis::Series captureClock;
    AnalogLoopback::RenderSignal signal(1);
    constexpr std::uint64_t packetOrigin = 50'000;
    constexpr std::uint64_t clockOrigin = packetOrigin + 143;
    if (!BuildRun(192, false, 2'000, false, data, captureClock, signal,
                  packetOrigin, clockOrigin)) return false;
    const auto result = MonitorLatency::Analyze(data, captureClock, kRate, true, false,
                                                CleanRingStats());
    if (result.status == MonitorLatency::Status::Measured || !result.softwareTransportMeasured ||
        result.sequenceStatus != MonitorLatency::SequenceStatus::Matched ||
        !std::isfinite(result.captureClockOffsetMedianFrames) ||
        std::abs(result.captureClockOffsetMedianFrames - 143.0) > 0.1 ||
        result.captureClockOffsetVariationFrames > 0.1 ||
        std::abs(result.captureClockRateDifferencePpm) > 1.0) return false;

    auto shifted = data;
    for (auto& packet : shifted.capturePackets) packet.qpc100ns += 10'000;
    const auto shiftedResult = MonitorLatency::Analyze(shifted, captureClock, kRate, true, false,
                                                        CleanRingStats());
    return shiftedResult.status != MonitorLatency::Status::Measured &&
        shiftedResult.softwareTransportMeasured &&
        std::abs(shiftedResult.captureClockOffsetMedianFrames -
                 result.captureClockOffsetMedianFrames) > 40.0;
}

bool TestCaptureClockOffsetStepIsNotMistakenForPacketOrigin() {
    MonitorLatency::RunData data;
    ClockAnalysis::Series captureClock;
    AnalogLoopback::RenderSignal signal(1);
    if (!BuildRun(192, false, 2'000, false, data, captureClock, signal)) return false;
    const auto stepFrame = signal.MarkerStarts()[10];
    const auto stepIndex = static_cast<std::size_t>(stepFrame / kBuffer);
    for (std::size_t i = stepIndex; i < captureClock.snapshots.size(); ++i)
        captureClock.snapshots[i].position += 144;
    const auto result = MonitorLatency::Analyze(data, captureClock, kRate, true, false,
                                                CleanRingStats());
    return result.status != MonitorLatency::Status::Measured && result.softwareTransportMeasured &&
        result.captureClockOffsetVariationFrames > 100.0 && result.captureClockQpcBoundaries != 0;
}

bool TestQpcStepsAreSegmentedAndCrossingsRejected() {
    for (const auto [captureStep, clockStep] : {std::pair{true, true},
                                                 std::pair{true, false},
                                                 std::pair{false, true}}) {
        MonitorLatency::RunData data;
        ClockAnalysis::Series captureClock;
        AnalogLoopback::RenderSignal signal(1);
        if (!BuildRun(192, captureStep, 2'000, false, data, captureClock, signal)) return false;
        const auto stepFrame = signal.MarkerStarts()[9] + 96;
        if (captureStep != clockStep) {
            for (auto& snapshot : captureClock.snapshots) {
                if (snapshot.position >= stepFrame) {
                    if (captureStep) snapshot.qpc100ns -= 10'000;
                    else snapshot.qpc100ns += 10'000;
                }
            }
        }
        const auto result = MonitorLatency::Analyze(data, captureClock, kRate, true, false,
                                                    CleanRingStats());
        const bool bothSidesStep = captureStep && clockStep;
        if (result.status != MonitorLatency::Status::Unreliable || !result.softwareTransportMeasured ||
            (captureStep && result.rejectedMarkers == 0) || result.captureSegmentCount < 2 && captureStep ||
            result.captureClockSegmentCount < 2 && clockStep ||
            (!bothSidesStep && result.timingQuality != MonitorLatency::TimingQuality::Unreliable)) {
            std::fprintf(stderr, "step case packet/clock=%d/%d status=%s rejected=%zu segments=%zu clock=%zu reason=%s\n",
                captureStep, clockStep, MonitorLatency::StatusText(result.status),
                result.rejectedMarkers, result.captureSegmentCount,
                result.captureClockSegmentCount, result.rejectionReason);
            return false;
        }
    }
    return true;
}

std::size_t CountLocalOutliers(const MonitorLatency::Analysis& result) {
    std::size_t total = 0;
    for (std::size_t i = 0; i < result.markerDiagnosticCount; ++i)
        total += result.markerDiagnostics[i].captureFit.rejectedOutliers;
    return total;
}

bool TestIsolatedQpcOutliersAreLocalAndVisible() {
    for (const std::size_t outlierCount : {1U, 3U}) {
        MonitorLatency::RunData data;
        ClockAnalysis::Series captureClock;
        AnalogLoopback::RenderSignal signal(1);
        if (!BuildRun(192, false, 2'000, false, data, captureClock, signal)) return false;
        const auto starts = signal.MarkerStarts();
        for (std::size_t i = 0; i < outlierCount; ++i) {
            const auto source = starts[4 + i * 5];
            const auto packet = static_cast<std::size_t>(source / kBuffer);
            if ((i & 1U) == 0) data.capturePackets[packet].qpc100ns += 1'700;
            else data.capturePackets[packet].qpc100ns -= 1'700;
        }
        const auto result = MonitorLatency::Analyze(data, captureClock, kRate, true, false, CleanRingStats());
        if (CountLocalOutliers(result) == 0 || !result.softwareTransportMeasured ||
            result.status == MonitorLatency::Status::Measured) return false;
        bool reportedOutlierWindow = false;
        for (std::size_t i = 0; i < result.markerDiagnosticCount; ++i) {
            const auto& marker = result.markerDiagnostics[i];
            reportedOutlierWindow = reportedOutlierWindow || marker.captureFit.rejectedOutliers != 0 ||
                marker.captureClockFit.rejectedOutliers != 0;
        }
        if (!reportedOutlierWindow) return false;
    }
    return true;
}

bool TestCaptureClockOffsetAndRateDiagnostics() {
    MonitorLatency::RunData shifted;
    ClockAnalysis::Series shiftedClock;
    AnalogLoopback::RenderSignal shiftedSignal(1);
    if (!BuildRun(192, false, 2'000, false, shifted, shiftedClock, shiftedSignal)) return false;
    for (auto& packet : shifted.capturePackets) packet.qpc100ns += 10'000;
    auto result = MonitorLatency::Analyze(shifted, shiftedClock, kRate, true, false, CleanRingStats());
    if (result.status == MonitorLatency::Status::Measured || !result.softwareTransportMeasured ||
        std::abs(result.captureClockOffsetMedianFrames) < 40.0) {
        std::fprintf(stderr, "persistent offset status=%s reason=%s detail=%s agreement=%.3f\n",
            MonitorLatency::StatusText(result.status),
            MonitorLatency::MarkerReasonText(result.markerDiagnostics[0].reason),
            result.markerDiagnostics[0].detail, result.markerDiagnostics[0].captureClockAgreementFrames);
        return false;
    }

    MonitorLatency::RunData commonShift;
    ClockAnalysis::Series commonShiftClock;
    AnalogLoopback::RenderSignal commonShiftSignal(1);
    if (!BuildRun(192, false, 2'000, false, commonShift, commonShiftClock,
                  commonShiftSignal)) return false;
    for (auto& packet : commonShift.capturePackets) packet.qpc100ns += 10'000;
    for (auto& snapshot : commonShiftClock.snapshots) snapshot.qpc100ns += 10'000;
    std::fill(commonShift.captureClockQpcBracketValid.begin(),
        commonShift.captureClockQpcBracketValid.end(), 0);
    commonShift.captureClockQpcBracketErrors = commonShift.captureClockQpcBracketValid.size();
    result = MonitorLatency::Analyze(commonShift, commonShiftClock, kRate, true, false, CleanRingStats());
    if (result.status == MonitorLatency::Status::Measured) {
        std::fprintf(stderr, "common shift status=%s\n", MonitorLatency::StatusText(result.status)); return false;
    }

    MonitorLatency::RunData data;
    ClockAnalysis::Series captureClock;
    AnalogLoopback::RenderSignal signal(1);
    if (!BuildRun(192, false, 2'000, false, data, captureClock, signal)) return false;
    for (auto& packet : data.capturePackets) {
        const auto elapsedTicks = QpcForFrame(packet.sourceFrame) - kBaseQpc;
        packet.qpc100ns += static_cast<std::uint64_t>(std::llround(elapsedTicks * 300.0 / 1'000'000.0));
    }
    result = MonitorLatency::Analyze(data, captureClock, kRate, true, false, CleanRingStats());
    const auto rejectedForClockDrift = std::count_if(result.markerDiagnostics.begin(),
        result.markerDiagnostics.begin() + result.markerDiagnosticCount,
        [](const auto& marker) {
            return marker.reason == MonitorLatency::MarkerReason::CaptureLocalTimingRejected &&
                marker.captureClockAgreementFrames > MonitorLatency::kMaximumGoodResidualFrames;
        });
    if (result.status == MonitorLatency::Status::Measured || rejectedForClockDrift == 0) {
        std::fprintf(stderr, "drift rejected=%zu status=%s\n", rejectedForClockDrift, MonitorLatency::StatusText(result.status)); return false;
    }

    MonitorLatency::RunData inconsistent;
    ClockAnalysis::Series inconsistentClock;
    AnalogLoopback::RenderSignal inconsistentSignal(1);
    if (!BuildRun(192, false, 2'000, false, inconsistent, inconsistentClock,
                  inconsistentSignal)) return false;
    result = MonitorLatency::Analyze(inconsistent, inconsistentClock, kRate, true, false,
                                     CleanRingStats());
    if (result.status == MonitorLatency::Status::Measured || !result.softwareTransportMeasured) {
        std::fprintf(stderr, "padding test sw=%d samples=%zu status=%s\n", result.softwareTransportMeasured, result.softwareTransportSamples, MonitorLatency::StatusText(result.status)); return false;
    }

    MonitorLatency::RunData jittered;
    ClockAnalysis::Series jitteredClock;
    AnalogLoopback::RenderSignal jitteredSignal(1);
    if (!BuildRun(192, false, 2'000, false, jittered, jitteredClock,
                  jitteredSignal)) return false;
    for (std::size_t i = 0; i < jittered.capturePackets.size(); ++i) {
        if ((i & 1U) != 0) jittered.capturePackets[i].qpc100ns += 2'000;
        else jittered.capturePackets[i].qpc100ns -= 2'000;
    }
    result = MonitorLatency::Analyze(jittered, jitteredClock, kRate, true, false, CleanRingStats());
    if (result.status == MonitorLatency::Status::Measured || result.rejectedMarkers == 0) {
        std::fprintf(stderr, "jitter rejected=%zu status=%s\n", result.rejectedMarkers, MonitorLatency::StatusText(result.status)); return false;
    }
    return true;
}

bool TestBoundaryMarkerDiagnosticsAndValidPostStepMarkers() {
    MonitorLatency::RunData data;
    ClockAnalysis::Series captureClock;
    AnalogLoopback::RenderSignal signal(1);
    if (!BuildRun(192, true, 2'000, false, data, captureClock, signal)) return false;
    const auto result = MonitorLatency::Analyze(data, captureClock, kRate, true, false, CleanRingStats());
    bool sawBoundaryReject = false;
    bool sawAcceptedAfterStep = false;
    for (std::size_t i = 0; i < result.markerDiagnosticCount; ++i) {
        const auto& marker = result.markerDiagnostics[i];
        sawBoundaryReject |= marker.reason == MonitorLatency::MarkerReason::CrossesQpcBoundary;
        sawAcceptedAfterStep |= marker.reason == MonitorLatency::MarkerReason::Accepted && marker.markerIndex > 9;
    }
    const bool sawLocalRejectAtStep = result.markerDiagnostics[9].reason ==
        MonitorLatency::MarkerReason::InsufficientLocalAnchors;
    if ((!sawBoundaryReject && !sawLocalRejectAtStep) || !sawAcceptedAfterStep) {
        std::fprintf(stderr, "boundary diagnostics=%zu rejected=%zu boundaries=%zu/%zu acceptedAfter=%d\n",
            result.markerDiagnosticCount, result.rejectedMarkers,
            result.capturePacketQpcBoundaries, result.captureClockQpcBoundaries,
            sawAcceptedAfterStep);
        for (std::size_t i = 0; i < result.markerDiagnosticCount; ++i)
            std::fprintf(stderr, " marker %zu: %s (%s)\n", i + 1,
                MonitorLatency::MarkerReasonText(result.markerDiagnostics[i].reason),
                result.markerDiagnostics[i].detail);
    }
    return (sawBoundaryReject || sawLocalRejectAtStep) && sawAcceptedAfterStep;
}

bool TestInsufficientTimingAndRingLossNeverMeasure() {
    MonitorLatency::RunData data;
    ClockAnalysis::Series captureClock;
    AnalogLoopback::RenderSignal signal(1);
    if (!BuildRun(192, false, 7, false, data, captureClock, signal)) return false;
    auto result = MonitorLatency::Analyze(data, captureClock, kRate, true, false, CleanRingStats());
    if (result.status == MonitorLatency::Status::Measured) return false;

    MonitorLatency::RunData fullData;
    ClockAnalysis::Series fullClock;
    AnalogLoopback::RenderSignal fullSignal(1);
    if (!BuildRun(192, false, 2'000, false, fullData, fullClock, fullSignal)) return false;
    auto ring = CleanRingStats();
    ring.underrunEvents = 1;
    result = MonitorLatency::Analyze(fullData, fullClock, kRate, true, false, ring);
    if (result.status != MonitorLatency::Status::Unreliable ||
        result.rejectionReason != std::string_view("ring underrun or overflow was observed")) return false;
    ring.underrunEvents = 0;
    ring.overflowEvents = 1;
    result = MonitorLatency::Analyze(fullData, fullClock, kRate, true, false, ring);
    if (result.status != MonitorLatency::Status::Unreliable) return false;
    ring.overflowEvents = 0;
    fullData.captureTimestampErrors = 1;
    fullData.captureMarkers[0].timestampValid = false;
    result = MonitorLatency::Analyze(fullData, fullClock, kRate, true, false, ring);
    return result.status == MonitorLatency::Status::Unreliable &&
        result.markerDiagnostics[0].reason == MonitorLatency::MarkerReason::InvalidTimestamp &&
        std::any_of(result.markerDiagnostics.begin() + 1,
            result.markerDiagnostics.begin() + result.markerDiagnosticCount,
            [](const auto& marker) { return marker.reason == MonitorLatency::MarkerReason::Accepted; });
}

bool TestNoSignalIsNotMeasured() {
    MonitorLatency::RunData data;
    ClockAnalysis::Series captureClock;
    if (!data.Configure(16, MonitorLatency::kMaximumMarkers)) return false;
    const auto result = MonitorLatency::Analyze(data, captureClock, kRate, true, false,
                                                CleanRingStats());
    return result.status == MonitorLatency::Status::SignalNotFound &&
        result.sequenceStatus == MonitorLatency::SequenceStatus::NotFound;
}

bool TestQpcTickConversion() {
    return MonitorLatency::QpcTicksTo100ns(10'000'000, 10'000'000) == 10'000'000 &&
        MonitorLatency::QpcTicksTo100ns(5'000'000, 10'000'000) == 5'000'000 &&
        MonitorLatency::QpcTicksTo100ns(1, 0) == 0;
}

bool TestMissingMarkersAndAbortedRun() {
    MonitorLatency::RunData data;
    ClockAnalysis::Series captureClock;
    AnalogLoopback::RenderSignal signal(1);
    if (!BuildRun(192, false, 2'000, true, data, captureClock, signal)) return false;
    auto result = MonitorLatency::Analyze(data, captureClock, kRate, true, false, CleanRingStats());
    if (result.sequenceStatus != MonitorLatency::SequenceStatus::Partial ||
        result.missingMarkers == 0 || result.status == MonitorLatency::Status::Measured ||
        result.softwareTransportMeasured) return false;
    result = MonitorLatency::Analyze(data, captureClock, kRate, false, true, CleanRingStats());
    return result.status != MonitorLatency::Status::Measured;
}

bool TrackOneMarker(unsigned offsetInFirstBuffer, bool corruptContinuation,
                    bool loseContinuation, bool omitFinalRelease,
                    MonitorLatency::RunData& data, std::uint64_t& firstBegin,
                    std::uint64_t& firstEnd, std::uint64_t& finalEnd) {
    AnalogLoopback::RenderSignal signal(1);
    if (!signal.Configure(kRate, 1, 3 * kBuffer, 5) ||
        !data.Configure(16, MonitorLatency::kMaximumMarkers)) return false;
    MonitorLatency::RenderTracker tracker;
    tracker.Reset();
    const auto markerStart = signal.MarkerStarts()[0];
    const auto sourceStart = markerStart - offsetInFirstBuffer;
    constexpr std::uint64_t renderStart = 90'000;
    constexpr std::uint64_t qpcStart = 9'000'000;
    const auto requiredFrames = static_cast<std::uint64_t>(offsetInFirstBuffer) +
        AnalogLoopback::kMarkerFrames;
    std::array<std::int16_t, kBuffer * 2> pcm{};
    std::array<std::uint64_t, kBuffer> sourceFrames{};
    std::uint64_t consumed = 0;
    std::uint64_t cycle = 0;
    bool lost = false;
    firstBegin = firstEnd = finalEnd = 0;
    while (consumed < requiredFrames) {
        signal.Fill(pcm.data(), kBuffer, sourceStart + consumed);
        for (auto& sample : pcm) sample = static_cast<std::int16_t>(std::lrint(
            static_cast<double>(sample) * LiveTransport::kOutputGain));
        for (std::size_t i = 0; i < kBuffer; ++i) sourceFrames[i] = sourceStart + consumed + i;
        if (corruptContinuation && consumed <= offsetInFirstBuffer + 200 &&
            consumed + kBuffer > offsetInFirstBuffer + 200) {
            const auto frame = static_cast<std::size_t>(offsetInFirstBuffer + 200 - consumed);
            pcm[frame * 2] ^= 1;
        }
        const auto lostSource = markerStart + kBuffer;
        if (loseContinuation && !lost && lostSource >= sourceStart + consumed &&
            lostSource < sourceStart + consumed + kBuffer) {
            const auto frame = static_cast<std::size_t>(lostSource - (sourceStart + consumed));
            sourceFrames[frame] = LiveTransport::kNoSourceFrame;
            pcm[frame * 2] = pcm[frame * 2 + 1] = 0;
            lost = true;
        }
        tracker.InspectBuffer(pcm, sourceFrames, renderStart + cycle * kBuffer, signal);
        const bool finalBuffer = consumed + kBuffer >= requiredFrames;
        const auto begin = qpcStart + cycle * 1'000;
        const auto end = begin + 100;
        if (cycle == 0) { firstBegin = begin; firstEnd = end; }
        if (finalBuffer) finalEnd = end;
        if (!(omitFinalRelease && finalBuffer) &&
            !tracker.CommitBuffer(data, 0, begin, end)) return false;
        consumed += kBuffer;
        ++cycle;
    }
    return true;
}

bool TestFirstAndFullMarkerHandoffFrames() {
    for (const unsigned offset : {0U, 72U, 143U}) {
        MonitorLatency::RunData data;
        std::uint64_t firstBegin = 0, firstEnd = 0, finalEnd = 0;
        if (!TrackOneMarker(offset, false, false, false, data,
                firstBegin, firstEnd, finalEnd) || data.renderMarkers.size() != 1) {
            std::fprintf(stderr, "tracker completion offset=%u records=%zu first=%llu/%llu final=%llu\n",
                offset, data.renderMarkers.size(), static_cast<unsigned long long>(firstBegin),
                static_cast<unsigned long long>(firstEnd), static_cast<unsigned long long>(finalEnd));
            return false;
        }
        const auto& marker = data.renderMarkers.front();
        AnalogLoopback::RenderSignal signal(1);
        if (!signal.Configure(kRate, 1, 3 * kBuffer, 5)) return false;
        const auto expectedSource = signal.MarkerStarts()[0];
        const auto expectedRender = 90'000 + offset;
        const auto expectedCycles = (offset + AnalogLoopback::kMarkerFrames + kBuffer - 1) / kBuffer;
        if (marker.sourceFrame != expectedSource || marker.renderFrame != expectedRender ||
            marker.lastRenderFrame != expectedRender + AnalogLoopback::kMarkerFrames - 1 ||
            marker.firstSubmissionBeginQpc100ns != firstBegin ||
            marker.firstSubmissionEndQpc100ns != firstEnd ||
            marker.submissionEndQpc100ns != finalEnd || expectedCycles < 4) {
            std::fprintf(stderr, "handoff offsets=%u src=%llu/%llu render=%llu/%llu last=%llu expected=%llu firstqpc=%llu/%llu expected=%llu/%llu final=%llu/%llu cycles=%llu\n",
                offset, static_cast<unsigned long long>(marker.sourceFrame),
                static_cast<unsigned long long>(expectedSource),
                static_cast<unsigned long long>(marker.renderFrame),
                static_cast<unsigned long long>(expectedRender),
                static_cast<unsigned long long>(marker.lastRenderFrame),
                static_cast<unsigned long long>(expectedRender + AnalogLoopback::kMarkerFrames - 1),
                static_cast<unsigned long long>(marker.firstSubmissionBeginQpc100ns),
                static_cast<unsigned long long>(marker.firstSubmissionEndQpc100ns),
                static_cast<unsigned long long>(firstBegin), static_cast<unsigned long long>(firstEnd),
                static_cast<unsigned long long>(marker.submissionEndQpc100ns),
                static_cast<unsigned long long>(finalEnd), static_cast<unsigned long long>(expectedCycles));
            return false;
        }
    }
    return true;
}

bool TestIncompleteOrDamagedMarkerIsNotDelivered() {
    for (const auto [corrupt, lose, omitRelease] : {
            std::array{true, false, false}, std::array{false, true, false},
            std::array{false, false, true}}) {
        MonitorLatency::RunData data;
        std::uint64_t firstBegin = 0, firstEnd = 0, finalEnd = 0;
        if (!TrackOneMarker(143, corrupt, lose, omitRelease, data,
                firstBegin, firstEnd, finalEnd) || !data.renderMarkers.empty()) return false;
    }
    return true;
}

bool TestHandoffMetricsRequireTheirOwnQpcBrackets() {
    MonitorLatency::RunData data;
    ClockAnalysis::Series captureClock;
    AnalogLoopback::RenderSignal signal(1);
    if (!BuildRun(192, false, 2'000, false, data, captureClock, signal)) return false;
    data.renderMarkers[0].firstSubmissionBeginQpc100ns = 0;
    data.renderMarkers[0].firstSubmissionEndQpc100ns = 0;
    auto result = MonitorLatency::Analyze(data, captureClock, kRate, true, false, CleanRingStats());
    if (result.firstFrameHandoffSamples + 1 != data.expectedMarkers ||
        result.softwareTransportSamples != data.expectedMarkers || !result.softwareTransportMeasured)
        return false;

    data.renderMarkers[0].submissionBeginQpc100ns = 0;
    data.renderMarkers[0].submissionEndQpc100ns = 0;
    result = MonitorLatency::Analyze(data, captureClock, kRate, true, false, CleanRingStats());
    if (result.firstFrameHandoffSamples + 1 != data.expectedMarkers ||
        result.softwareTransportSamples + 1 != data.expectedMarkers ||
        result.softwareTransportMeasured) return false;

    MonitorLatency::RunData mismatched;
    ClockAnalysis::Series mismatchedClock;
    AnalogLoopback::RenderSignal mismatchedSignal(1);
    if (!BuildRun(192, false, 2'000, false, mismatched, mismatchedClock, mismatchedSignal)) return false;
    ++mismatched.renderMarkers[0].sourceFrame;
    result = MonitorLatency::Analyze(mismatched, mismatchedClock, kRate, true, false, CleanRingStats());
    return result.firstFrameHandoffSamples + 1 == mismatched.expectedMarkers &&
        result.softwareTransportSamples + 1 == mismatched.expectedMarkers &&
        !result.softwareTransportMeasured &&
        result.markerDiagnostics[0].detail == std::string_view(
            "Render provenance source-frame does not match the Capture marker start");
}

bool TestRingProvenanceAndRenderSubmission() {
    LiveTransport::SpscPcm16StereoRing ring;
    AnalogLoopback::RenderSignal signal(1);
    MonitorLatency::RunData data;
    if (!ring.Configure(kBuffer, kBuffer, kRate) ||
        !signal.Configure(kRate, 1, 3 * kBuffer, 5) ||
        !data.Configure(2'000, MonitorLatency::kMaximumMarkers)) return false;
    std::array<std::int16_t, kBuffer * 2> capturePcm{};
    std::array<std::int16_t, kBuffer * 2> renderPcm{};
    std::array<std::uint64_t, kBuffer> sourceFrames{};
    MonitorLatency::RenderTracker tracker;
    tracker.Reset();
    std::uint64_t captureFrame = 0;
    std::uint64_t renderFrame = kBuffer;
    for (int packet = 0; packet < 3; ++packet) {
        signal.Fill(capturePcm.data(), kBuffer, captureFrame);
        if (!ring.Push(capturePcm.data(), kBuffer, false, captureFrame)) return false;
        signal.CommitFrames(captureFrame, kBuffer);
        captureFrame += kBuffer;
    }
    auto pull = ring.Pull(renderPcm.data(), kBuffer, false, sourceFrames.data());
    if (!pull.prefillCompleted) return false;
    tracker.InspectBuffer(renderPcm, sourceFrames, renderFrame, signal);
    if (!tracker.CommitBuffer(data, ring.Occupancy(), QpcForFrame(renderFrame), QpcForFrame(renderFrame) + 10)) return false;
    renderFrame += kBuffer;
    while (captureFrame < 5ULL * kRate) {
        signal.Fill(capturePcm.data(), kBuffer, captureFrame);
        if (!ring.Push(capturePcm.data(), kBuffer, false, captureFrame)) return false;
        signal.CommitFrames(captureFrame, kBuffer);
        captureFrame += kBuffer;
        pull = ring.Pull(renderPcm.data(), kBuffer, false, sourceFrames.data());
        if (pull.underrun || pull.overwrittenFrames != 0) return false;
        tracker.InspectBuffer(renderPcm, sourceFrames, renderFrame, signal);
        if (!tracker.CommitBuffer(data, ring.Occupancy(), QpcForFrame(renderFrame), QpcForFrame(renderFrame) + 10)) return false;
        renderFrame += kBuffer;
    }
    while (ring.Occupancy() >= kBuffer) {
        pull = ring.Pull(renderPcm.data(), kBuffer, false, sourceFrames.data());
        tracker.InspectBuffer(renderPcm, sourceFrames, renderFrame, signal);
        if (!tracker.CommitBuffer(data, ring.Occupancy(), QpcForFrame(renderFrame), QpcForFrame(renderFrame) + 10)) return false;
        renderFrame += kBuffer;
    }
    return signal.MarkersCompleted() == signal.MarkerStarts().size() &&
        data.renderMarkers.size() == signal.MarkerStarts().size() &&
        std::all_of(data.renderMarkers.begin(), data.renderMarkers.end(),
            [](const auto& marker) { return marker.samplesVerified; }) &&
        ring.GetStats().overflowFrames == 0 && ring.GetStats().underrunFrames == 0;
}

} // namespace

int main() {
    if (!TestSoftwareSubmissionIntervalsAndIndependentStarts()) return 1;
    if (!TestIndependentCapturePositionOrigins()) return 2;
    if (!TestCaptureClockOffsetStepIsNotMistakenForPacketOrigin()) return 3;
    if (!TestQpcStepsAreSegmentedAndCrossingsRejected()) return 4;
    if (!TestInsufficientTimingAndRingLossNeverMeasure()) return 3;
    if (!TestMissingMarkersAndAbortedRun()) return 4;
    if (!TestRingProvenanceAndRenderSubmission()) return 5;
    if (!TestNoSignalIsNotMeasured()) return 6;
    if (!TestQpcTickConversion()) return 7;
    if (!TestIsolatedQpcOutliersAreLocalAndVisible()) return 8;
    if (!TestCaptureClockOffsetAndRateDiagnostics()) return 9;
    if (!TestBoundaryMarkerDiagnosticsAndValidPostStepMarkers()) return 10;
    if (!TestFirstAndFullMarkerHandoffFrames()) return 11;
    if (!TestIncompleteOrDamagedMarkerIsNotDelivered()) return 12;
    if (!TestHandoffMetricsRequireTheirOwnQpcBrackets()) return 13;
    return 0;
}
