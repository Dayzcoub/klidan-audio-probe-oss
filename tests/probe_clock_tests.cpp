#include "../src/probe/clock_analysis.h"

ClockAnalysis::Series MakeSeries(ClockAnalysis::Direction direction, std::uint64_t frequency,
                                 std::uint64_t qpcStart, std::uint64_t positionStart,
                                 double ratePpm, unsigned durationSeconds = 5) {
    ClockAnalysis::Series series;
    series.direction = direction;
    series.serviceHresult = ClockAnalysis::kSOk;
    series.frequencyHresult = ClockAnalysis::kSOk;
    series.frequency = frequency;
    series.storageReady = true;
    series.snapshots.reserve(durationSeconds * 4 + 1);
    for (unsigned index = 0; index <= durationSeconds * 4; ++index) {
        const std::uint64_t elapsed100ns = static_cast<std::uint64_t>(index) * 2'500'000;
        const long double elapsedSeconds = static_cast<long double>(elapsed100ns) / 10'000'000;
        const long double ticks = elapsedSeconds * frequency * (1.0L + ratePpm / 1'000'000.0L);
        series.snapshots.push_back({direction, index + 1, positionStart + static_cast<std::uint64_t>(ticks),
                                    qpcStart + elapsed100ns, ClockAnalysis::kSOk});
    }
    return series;
}

bool Near(double value, double expected, double tolerance) {
    return value >= expected - tolerance && value <= expected + tolerance;
}

int main() {
    double normalized = 0;
    if (!ClockAnalysis::TryNormalizePosition(10'000, 2'000, normalized) || normalized != 5.0 ||
        ClockAnalysis::TryNormalizePosition(10, 0, normalized)) return 1;

    constexpr std::uint64_t frequency = 48'000'000;
    constexpr std::uint64_t captureQpcStart = 100'000'000'000;
    constexpr std::uint64_t renderQpcStart = captureQpcStart + 500'000;
    auto capture = MakeSeries(ClockAnalysis::Direction::Capture, frequency,
                              captureQpcStart, 1'000'000'000, 50.0);
    auto render = MakeSeries(ClockAnalysis::Direction::Render, frequency,
                             renderQpcStart, 9'000'000'000, -50.0);
    auto result = ClockAnalysis::Analyze(capture, render);
    if (result.status != ClockAnalysis::Status::Available || !result.hasRegression ||
        !Near(result.capture.ratePpm, 50.0, 1.0) || !Near(result.render.ratePpm, -50.0, 1.0) ||
        !Near(result.relativeRateDifferencePpm, 100.0, 2.0) ||
        !Near(result.relativePhaseChangeMilliseconds, 0.37, 0.02)) return 2;

    capture = MakeSeries(ClockAnalysis::Direction::Capture, frequency,
                         captureQpcStart, 3'000'000, 0.0);
    render = MakeSeries(ClockAnalysis::Direction::Render, frequency,
                        renderQpcStart, 30'000'000'000, 0.0);
    result = ClockAnalysis::Analyze(capture, render);
    if (result.status != ClockAnalysis::Status::Available ||
        !Near(result.relativeRateDifferencePpm, 0.0, 1.0) ||
        !Near(result.relativePhaseChangeMilliseconds, 0.0, 0.01) ||
        result.overlapStart100ns <= captureQpcStart ||
        result.capture.maximumResidualMilliseconds < result.capture.rmsResidualMilliseconds) return 3;

    capture.snapshots[10].qpc100ns += 10'000;
    result = ClockAnalysis::Analyze(capture, render);
    if (result.status != ClockAnalysis::Status::Available ||
        result.capture.maximumResidualMilliseconds < 0.5 ||
        result.capture.maximumResidualMilliseconds < result.capture.rmsResidualMilliseconds) return 14;

    capture = MakeSeries(ClockAnalysis::Direction::Capture, frequency,
                         captureQpcStart, 3'000'000, 0.0);
    capture.snapshots[1].qpc100ns += 10'000;
    result = ClockAnalysis::Analyze(capture, render);
    if (result.status != ClockAnalysis::Status::Available ||
        result.capture.maximumResidualMilliseconds < 0.5) return 15;

    capture.snapshots[6].hresult = ClockAnalysis::kSFalse;
    result = ClockAnalysis::Analyze(capture, render);
    if (result.status != ClockAnalysis::Status::AvailableWithRejections ||
        result.capture.rejectedSFalse != 1 || result.capture.validSnapshots != 20) return 4;

    capture = MakeSeries(ClockAnalysis::Direction::Capture, frequency,
                         captureQpcStart, 1'000, 0.0);
    capture.snapshots[6].hresult = static_cast<std::int32_t>(0x80004005U);
    result = ClockAnalysis::Analyze(capture, render);
    if (result.status != ClockAnalysis::Status::AvailableWithRejections ||
        result.capture.rejectedApiErrors != 1 || !result.capture.hasPositionApiError ||
        result.capture.firstPositionApiError != static_cast<std::int32_t>(0x80004005U)) return 5;

    capture = MakeSeries(ClockAnalysis::Direction::Capture, frequency,
                         captureQpcStart, 1'000, 0.0);
    capture.snapshots[7].qpc100ns = capture.snapshots[6].qpc100ns;
    result = ClockAnalysis::Analyze(capture, render);
    if (result.status != ClockAnalysis::Status::UnreliableTimestamps || result.hasRegression ||
        result.capture.invalidTimestamps != 1) return 6;

    capture = MakeSeries(ClockAnalysis::Direction::Capture, frequency,
                         captureQpcStart, 1'000, 0.0);
    capture.snapshots[6].qpc100ns = 0;
    result = ClockAnalysis::Analyze(capture, render);
    if (result.status != ClockAnalysis::Status::UnreliableTimestamps || result.hasRegression ||
        result.capture.invalidTimestamps != 1) return 7;

    capture = MakeSeries(ClockAnalysis::Direction::Capture, frequency,
                         captureQpcStart, 1'000, 0.0);
    capture.snapshots[6].position = capture.snapshots[5].position - 1;
    result = ClockAnalysis::Analyze(capture, render);
    if (result.status != ClockAnalysis::Status::UnreliableTimestamps || result.hasRegression ||
        result.capture.nonmonotonicPositions != 1) return 8;

    capture = MakeSeries(ClockAnalysis::Direction::Capture, frequency,
                         captureQpcStart, 1'000, 0.0, 2);
    render = MakeSeries(ClockAnalysis::Direction::Render, frequency,
                        renderQpcStart, 1'000, 0.0, 2);
    result = ClockAnalysis::Analyze(capture, render);
    if (result.status != ClockAnalysis::Status::InsufficientData || result.hasRegression) return 9;

    capture = MakeSeries(ClockAnalysis::Direction::Capture, frequency,
                         captureQpcStart, 1'000, 0.0);
    render = MakeSeries(ClockAnalysis::Direction::Render, frequency,
                        renderQpcStart, 1'000, 0.0);
    render.frequencyHresult = static_cast<std::int32_t>(0x80004005U);
    result = ClockAnalysis::Analyze(capture, render);
    if (result.status != ClockAnalysis::Status::ClockApiFailed || result.hasRegression) return 10;

    render.frequencyHresult = ClockAnalysis::kSOk;
    render.frequency = 0;
    result = ClockAnalysis::Analyze(capture, render);
    if (result.status != ClockAnalysis::Status::ClockApiFailed || result.hasRegression) return 11;

    capture = MakeSeries(ClockAnalysis::Direction::Capture, frequency,
                         captureQpcStart, 1'000, 0.0);
    render = MakeSeries(ClockAnalysis::Direction::Render, frequency,
                        renderQpcStart, 1'000, 0.0);
    for (auto& sample : capture.snapshots) sample.position = 1'000;
    result = ClockAnalysis::Analyze(capture, render);
    if (result.status != ClockAnalysis::Status::UnreliableTimestamps || result.hasRegression ||
        result.capture.unchangedPositions == 0) return 12;

    capture = MakeSeries(ClockAnalysis::Direction::Capture, frequency,
                         captureQpcStart, 1'000, 0.0);
    render = MakeSeries(ClockAnalysis::Direction::Render, frequency,
                        renderQpcStart, 1'000, 0.0);
    for (std::size_t i = 1; i < capture.snapshots.size(); ++i)
        capture.snapshots[i].position = capture.snapshots[i - 1].position +
            (i < 10 ? 30'000'000 : 1'000'000);
    result = ClockAnalysis::Analyze(capture, render);
    if (result.status != ClockAnalysis::Status::UnreliableTimestamps || result.hasRegression ||
        result.capture.rSquared >= 0.95) return 13;
    return 0;
}
