#include "../src/probe/probe_math.h"

int main() {
    if (ProbeMath::FramesForDuration(100'000, 48'000) != 480) return 1;
    if (ProbeMath::FramesForDuration(100'000, 44'100) != 441) return 2;
    if (ProbeMath::FramesForDuration(ProbeMath::DurationForFrames(128, 48'000), 48'000) != 128) return 3;
    if (ProbeMath::FramesForDuration(ProbeMath::DurationForFrames(128, 44'100), 44'100) != 128) return 4;
    if (ProbeMath::SelectDevicePeriod(100'000, 30'000, false) != 100'000) return 5;
    if (ProbeMath::SelectDevicePeriod(100'000, 30'000, true) != 30'000) return 6;
    if (ProbeMath::IsValidStreamDuration(0) || !ProbeMath::IsValidStreamDuration(1) ||
        !ProbeMath::IsValidStreamDuration(30) || ProbeMath::IsValidStreamDuration(31)) return 7;
    if (ProbeMath::BoundedEventWaitMilliseconds(0) != 0 ||
        ProbeMath::BoundedEventWaitMilliseconds(12) != 12 ||
        ProbeMath::BoundedEventWaitMilliseconds(2'000) != 1'000) return 8;
    if (ProbeMath::ExpectedIntervalMilliseconds(144, 48'000) != 3.0 ||
        ProbeMath::ExpectedIntervalMilliseconds(480, 48'000) != 10.0) return 9;
    if (ProbeMath::ExpectedFramesForMicroseconds(5'000'000, 48'000) != 240'000) return 10;
    if (ProbeMath::FrameTolerance(240'000, 480) != 24'000 ||
        ProbeMath::FrameTolerance(5'000, 480) != 960 ||
        !ProbeMath::HasSufficientFrames(216'000, 240'000, 480) ||
        ProbeMath::HasSufficientFrames(215'999, 240'000, 480) ||
        ProbeMath::HasSufficientFrames(120'096, 240'000, 144) ||
        ProbeMath::HasSufficientFrames(72'000, 240'720, 144)) return 11;
    if (ProbeMath::IsLongEventInterval(15.0, 10.0) ||
        !ProbeMath::IsLongEventInterval(15.01, 10.0)) return 12;
    const std::vector<double> sortedIntervals{1.0, 2.0, 3.0, 10.0};
    if (ProbeMath::Percentile(sortedIntervals, 50) != 2.0 ||
        ProbeMath::Percentile(sortedIntervals, 95) != 10.0 ||
        ProbeMath::Percentile(sortedIntervals, 99) != 10.0 ||
        ProbeMath::Percentile(sortedIntervals, 0) != 0.0) return 13;
    if (ProbeMath::CompareDevicePosition(false, 0, 100, true).transition !=
            ProbeMath::PositionTransition::Initial ||
        ProbeMath::CompareDevicePosition(true, 100, 100, true).transition !=
            ProbeMath::PositionTransition::Contiguous ||
        ProbeMath::CompareDevicePosition(true, 100, 105, true).transition !=
            ProbeMath::PositionTransition::Jump ||
        ProbeMath::CompareDevicePosition(true, 100, 105, true).frames != 5 ||
        ProbeMath::CompareDevicePosition(true, 100, 95, true).transition !=
            ProbeMath::PositionTransition::Overlap ||
        ProbeMath::CompareDevicePosition(true, 100, 95, true).frames != 5 ||
        ProbeMath::CompareDevicePosition(true, 100, 100, false).transition !=
            ProbeMath::PositionTransition::TimestampInvalid) return 14;
    if (ProbeMath::ClassifyStream(false, false, false, false, 0, 0, false, false, false, false) !=
        ProbeMath::StreamStatus::InitializeFailed) return 15;
    if (ProbeMath::ClassifyStream(true, true, false, false, 0, 0, false, false, false, false) !=
        ProbeMath::StreamStatus::StartFailed) return 16;
    if (ProbeMath::ClassifyStream(true, false, false, false, 0, 0, false, false, false, false) !=
        ProbeMath::StreamStatus::StreamFailed) return 17;
    if (ProbeMath::ClassifyStream(true, true, true, true, 1, 240'000, true, false, false, true) !=
        ProbeMath::StreamStatus::StreamCompleted) return 18;
    if (ProbeMath::ClassifyStream(true, true, true, true, 1, 240'000, true, true, false, true) !=
        ProbeMath::StreamStatus::StreamCompletedWithWarnings) return 19;
    if (ProbeMath::ClassifyStream(true, true, true, true, 1, 240'000, false, false, false, true) !=
        ProbeMath::StreamStatus::StreamFailed) return 20;
    if (ProbeMath::ClassifyStream(true, true, true, true, 1, 240'000, true, false, true, true) !=
        ProbeMath::StreamStatus::StreamFailed) return 21;
    if (ProbeMath::ClassifyStream(true, true, true, true, 1, 240'000, true, false, false, false) !=
        ProbeMath::StreamStatus::StreamFailed) return 22;
    if (ProbeMath::ClassifyStream(true, true, true, false, 1, 240'000, true, false, false, true) !=
        ProbeMath::StreamStatus::StreamFailed) return 23;
    if (ProbeMath::ClassifyStream(true, true, true, true, 0, 240'000, true, false, false, true) !=
        ProbeMath::StreamStatus::StreamFailed) return 24;
    if (ProbeMath::ClassifyStreamQuality(true, true, false, 0, 0, 0, 0, 0, 0, 0, 144) !=
        ProbeMath::StreamQuality::Clean) return 25;
    if (ProbeMath::ClassifyStreamQuality(true, true, true, 1, 0, 0, 0, 0, 0, 0, 144) !=
        ProbeMath::StreamQuality::AnomaliesPresent) return 26;
    if (ProbeMath::ClassifyStreamQuality(true, true, true, 3, 0, 0, 0, 0, 0, 0, 144) !=
        ProbeMath::StreamQuality::SevereAnomalies) return 27;
    if (ProbeMath::ClassifyStreamQuality(true, true, true, 0, 1, 0, 0, 0, 0, 144, 144) !=
        ProbeMath::StreamQuality::SevereAnomalies) return 28;
    if (ProbeMath::ClassifyStreamQuality(true, true, true, 0, 1, 0, 0, 0, 0, 1, 144) !=
        ProbeMath::StreamQuality::AnomaliesPresent) return 29;
    if (ProbeMath::ClassifyStreamQuality(true, false, true, 0, 0, 0, 0, 0, 0, 0, 144) !=
        ProbeMath::StreamQuality::SevereAnomalies) return 30;
    if (ProbeMath::ClassifyStreamQuality(false, true, true, 0, 0, 0, 0, 0, 0, 0, 144) !=
        ProbeMath::StreamQuality::SevereAnomalies) return 31;
    if (ProbeMath::ClassifyStreamQuality(true, true, true, 0, 0, 0, 0, 3, 0, 0, 144) !=
        ProbeMath::StreamQuality::SevereAnomalies) return 32;
    if (ProbeMath::ClassifyStreamQuality(true, true, true, 0, 0, 3, 0, 0, 0, 0, 144) !=
        ProbeMath::StreamQuality::SevereAnomalies) return 33;
    if (ProbeMath::ClassifyStreamQuality(true, true, true, 0, 0, 0, 3, 0, 0, 0, 144) !=
        ProbeMath::StreamQuality::SevereAnomalies) return 34;
    if (!ProbeMath::CanStartStream(true, false, false, false) ||
        !ProbeMath::CanStartStream(true, false, true, true) ||
        ProbeMath::CanStartStream(true, false, true, false) ||
        ProbeMath::CanStartStream(true, true, false, false) ||
        ProbeMath::CanStartStream(false, false, false, false)) return 35;
    using namespace std::chrono_literals;
    if (ProbeMath::DecideWaitOutcome(false, false, false, 200us, 100ms,
                                     ProbeMath::kEventWatchdog) !=
        ProbeMath::WaitDecision::ContinueWaiting) return 36;
    if (ProbeMath::DecideWaitOutcome(false, false, false, 60us, 100ms,
                                     ProbeMath::kEventWatchdog) !=
        ProbeMath::WaitDecision::ContinueWaiting) return 37;
    if (ProbeMath::DecideWaitOutcome(false, false, false, 0ns, 100ms,
                                     ProbeMath::kEventWatchdog) !=
        ProbeMath::WaitDecision::DeadlineReached) return 38;
    if (ProbeMath::DecideWaitOutcome(false, false, false, 2s, 1s,
                                     ProbeMath::kEventWatchdog) !=
        ProbeMath::WaitDecision::WatchdogTimeout) return 39;
    if (ProbeMath::DecideWaitOutcome(false, true, false, 0ns, 0ns,
                                     ProbeMath::kEventWatchdog) !=
        ProbeMath::WaitDecision::Cancelled) return 40;
    if (ProbeMath::DecideWaitOutcome(false, true, true, 0ns, 2s,
                                     ProbeMath::kEventWatchdog) !=
        ProbeMath::WaitDecision::ApiFailure) return 41;
    // MMCSS changes setup only; both accepted modes use this same deadline decision.
    for (int mmcssMode = 0; mmcssMode != 2; ++mmcssMode) {
        const bool mmcssRequested = mmcssMode != 0;
        if (!ProbeMath::CanStartStream(true, false, mmcssRequested, mmcssRequested) ||
            ProbeMath::DecideWaitOutcome(false, false, false, 200us, 100ms,
                                         ProbeMath::kEventWatchdog) !=
                ProbeMath::WaitDecision::ContinueWaiting) return 42;
    }
    if (ProbeMath::DecideDuplexPreparation(false, false, false, false) !=
            ProbeMath::DuplexPreparation::Waiting ||
        ProbeMath::DecideDuplexPreparation(true, true, false, false) !=
            ProbeMath::DuplexPreparation::Waiting ||
        ProbeMath::DecideDuplexPreparation(true, false, false, false) !=
            ProbeMath::DuplexPreparation::Abort ||
        ProbeMath::DecideDuplexPreparation(false, false, true, false) !=
            ProbeMath::DuplexPreparation::Abort ||
        ProbeMath::DecideDuplexPreparation(true, true, true, true) !=
            ProbeMath::DuplexPreparation::StartTogether) return 43;
    if (!ProbeMath::ShouldStopDuplexPeer(true, false, false) ||
        !ProbeMath::ShouldStopDuplexPeer(false, true, false) ||
        !ProbeMath::ShouldStopDuplexPeer(false, false, true) ||
        ProbeMath::ShouldStopDuplexPeer(false, false, false)) return 44;
    if (ProbeMath::ClassifyDuplex(ProbeMath::StreamStatus::StreamCompleted,
                                  ProbeMath::StreamStatus::StreamCompleted) !=
            ProbeMath::StreamStatus::StreamCompleted ||
        ProbeMath::ClassifyDuplex(ProbeMath::StreamStatus::StreamCompleted,
                                  ProbeMath::StreamStatus::StreamCompletedWithWarnings) !=
            ProbeMath::StreamStatus::StreamCompletedWithWarnings ||
        ProbeMath::ClassifyDuplex(ProbeMath::StreamStatus::StreamCompleted,
                                  ProbeMath::StreamStatus::StreamFailed) !=
            ProbeMath::StreamStatus::StreamFailed ||
        ProbeMath::ClassifyDuplex(ProbeMath::StreamStatus::StartFailed,
                                  ProbeMath::StreamStatus::StreamFailed) !=
            ProbeMath::StreamStatus::StartFailed ||
        ProbeMath::ClassifyDuplex(ProbeMath::StreamStatus::StreamFailed,
                                  ProbeMath::StreamStatus::StartFailed) !=
            ProbeMath::StreamStatus::StartFailed ||
        ProbeMath::ClassifyDuplex(ProbeMath::StreamStatus::InitializeFailed,
                                  ProbeMath::StreamStatus::StartFailed) !=
            ProbeMath::StreamStatus::InitializeFailed) return 45;
    if (ProbeMath::ClassifyDuplexQuality(ProbeMath::StreamQuality::Clean,
                                         ProbeMath::StreamQuality::AnomaliesPresent) !=
            ProbeMath::StreamQuality::AnomaliesPresent ||
        ProbeMath::ClassifyDuplexQuality(ProbeMath::StreamQuality::Clean,
                                         ProbeMath::StreamQuality::SevereAnomalies) !=
            ProbeMath::StreamQuality::SevereAnomalies) return 46;
    if (ProbeMath::DuplexOverlapMicroseconds(0, 5'000'000, 20'000, 5'020'000) != 4'980'000 ||
        ProbeMath::DuplexOverlapMicroseconds(0, 1'000, 2'000, 3'000) != 0) return 47;
    return 0;
}
