#pragma once

#include <algorithm>
#include <cmath>
#include <cstdint>
#include <limits>
#include <vector>

namespace ClockAnalysis {

constexpr std::int32_t kSOk = 0;
constexpr std::int32_t kSFalse = 1;
constexpr std::uint64_t kHnsPerSecond = 10'000'000;
constexpr std::size_t kWarmupSamples = 4;
constexpr std::size_t kTrimEndSamples = 1;
constexpr std::size_t kMinimumFitSamples = 8;
constexpr std::size_t kMaximumSnapshots = 128;
constexpr std::uint64_t kMinimumFitSpanHns = kHnsPerSecond;
constexpr long double kMinimumRSquared = 0.95L;

inline bool TryNormalizePosition(std::uint64_t position, std::uint64_t frequency, double& seconds) {
    if (frequency == 0) return false;
    seconds = static_cast<double>(position) / static_cast<double>(frequency);
    return true;
}

enum class Direction { Capture, Render };
enum class Status { Available, AvailableWithRejections, InsufficientData, UnreliableTimestamps, ClockApiFailed };

struct Snapshot {
    Direction direction = Direction::Capture;
    std::uint32_t sequence = 0;
    std::uint64_t position = 0;
    std::uint64_t qpc100ns = 0;
    std::int32_t hresult = kSOk;
};

struct Series {
    Direction direction = Direction::Capture;
    std::int32_t serviceHresult = std::numeric_limits<std::int32_t>::min();
    std::int32_t frequencyHresult = std::numeric_limits<std::int32_t>::min();
    std::uint64_t frequency = 0;
    bool storageReady = false;
    std::vector<Snapshot> snapshots;
};

struct SideResult {
    Status status = Status::InsufficientData;
    std::uint64_t frequency = 0;
    std::size_t snapshots = 0;
    std::size_t validSnapshots = 0;
    std::size_t analysisSnapshots = 0;
    std::size_t rejectedSFalse = 0;
    std::size_t rejectedApiErrors = 0;
    bool hasPositionApiError = false;
    std::int32_t firstPositionApiError = 0;
    std::size_t invalidTimestamps = 0;
    std::size_t nonmonotonicPositions = 0;
    std::size_t invalidSequences = 0;
    std::size_t unchangedPositions = 0;
    std::uint64_t qpcFirst100ns = 0;
    std::uint64_t qpcLast100ns = 0;
    double ratePpm = 0;
    double rSquared = 0;
    double rmsResidualMilliseconds = 0;
    double maximumResidualMilliseconds = 0;
    bool hasFitModel = false;
    std::uint64_t fitPositionOrigin = 0;
    std::uint64_t fitQpcOrigin100ns = 0;
    long double fitSlopeSecondsPerQpcSecond = 0;
    long double fitInterceptPositionSeconds = 0;
};

struct Result {
    Status status = Status::InsufficientData;
    SideResult capture;
    SideResult render;
    bool hasRegression = false;
    std::uint64_t overlapStart100ns = 0;
    std::uint64_t overlapEnd100ns = 0;
    double relativeRateDifferencePpm = 0;
    double relativePhaseChangeMilliseconds = 0;
};

namespace detail {

struct Point {
    std::uint64_t position;
    std::uint64_t qpc100ns;
};

struct Prepared {
    SideResult result;
    std::vector<Point> points;
    bool apiFailed = false;
    bool storageFailed = false;
    bool unreliable = false;
};

Prepared Prepare(const Series& series, Direction expectedDirection) {
    Prepared prepared;
    auto& result = prepared.result;
    result.frequency = series.frequency;
    result.snapshots = series.snapshots.size();
    prepared.apiFailed = series.serviceHresult != kSOk ||
        series.frequencyHresult != kSOk || series.frequency == 0;
    prepared.storageFailed = !series.storageReady;
    std::uint64_t previousPosition = 0;
    std::uint64_t previousQpc = 0;
    std::uint32_t previousSequence = 0;
    bool havePrevious = false;
    for (const auto& sample : series.snapshots) {
        if (sample.hresult == kSFalse) {
            ++result.rejectedSFalse;
            continue;
        }
        if (sample.hresult != kSOk) {
            ++result.rejectedApiErrors;
            if (!result.hasPositionApiError) {
                result.hasPositionApiError = true;
                result.firstPositionApiError = sample.hresult;
            }
            continue;
        }
        if (sample.direction != expectedDirection) prepared.unreliable = true;
        if (sample.qpc100ns == 0 || (havePrevious && sample.qpc100ns <= previousQpc)) {
            ++result.invalidTimestamps;
            prepared.unreliable = true;
            continue;
        }
        if (havePrevious && sample.position < previousPosition) {
            ++result.nonmonotonicPositions;
            prepared.unreliable = true;
            continue;
        }
        if (havePrevious && sample.sequence <= previousSequence) {
            ++result.invalidSequences;
            prepared.unreliable = true;
            continue;
        }
        if (havePrevious && sample.position == previousPosition) ++result.unchangedPositions;
        prepared.points.push_back({sample.position, sample.qpc100ns});
        previousPosition = sample.position;
        previousQpc = sample.qpc100ns;
        previousSequence = sample.sequence;
        havePrevious = true;
    }
    result.validSnapshots = prepared.points.size();
    if (!prepared.points.empty()) {
        result.qpcFirst100ns = prepared.points.front().qpc100ns;
        result.qpcLast100ns = prepared.points.back().qpc100ns;
    }
    return prepared;
}

bool FitRate(const std::vector<Point>& points, const std::vector<Point>& allPoints,
             std::uint64_t frequency, std::uint64_t qpcOrigin, SideResult& result) {
    if (points.size() < kMinimumFitSamples || frequency == 0) return false;
    const auto positionOrigin = points.front().position;
    long double meanX = 0;
    long double meanY = 0;
    for (const auto& point : points) {
        meanX += static_cast<long double>(point.qpc100ns - qpcOrigin) / kHnsPerSecond;
        meanY += (static_cast<long double>(point.position) - positionOrigin) / frequency;
    }
    meanX /= points.size();
    meanY /= points.size();
    long double covariance = 0;
    long double varianceX = 0;
    long double varianceY = 0;
    for (const auto& point : points) {
        const long double x = static_cast<long double>(point.qpc100ns - qpcOrigin) / kHnsPerSecond - meanX;
        const long double y = (static_cast<long double>(point.position) - positionOrigin) / frequency - meanY;
        covariance += x * y;
        varianceX += x * x;
        varianceY += y * y;
    }
    if (varianceX <= 0 || varianceY <= 0) return false;
    const long double slope = covariance / varianceX;
    const long double intercept = meanY - slope * meanX;
    result.hasFitModel = true;
    result.fitPositionOrigin = positionOrigin;
    result.fitQpcOrigin100ns = qpcOrigin;
    result.fitSlopeSecondsPerQpcSecond = slope;
    result.fitInterceptPositionSeconds = intercept;
    long double residualSquares = 0;
    for (const auto& point : points) {
        const long double x = static_cast<long double>(point.qpc100ns - qpcOrigin) / kHnsPerSecond;
        const long double y = (static_cast<long double>(point.position) - positionOrigin) / frequency;
        const long double residual = y - (intercept + slope * x);
        residualSquares += residual * residual;
    }
    long double maximumResidual = 0;
    // QpcAtRenderFrame interpolates through warmup/end samples too, so include them in the worst residual.
    for (const auto& point : allPoints) {
        const long double x = (static_cast<long double>(point.qpc100ns) - qpcOrigin) / kHnsPerSecond;
        const long double y = (static_cast<long double>(point.position) - positionOrigin) / frequency;
        const long double residual = y - (intercept + slope * x);
        maximumResidual = std::max(maximumResidual, std::abs(residual));
    }
    if (!std::isfinite(static_cast<double>(slope)) || slope <= 0) return false;
    result.ratePpm = static_cast<double>((slope - 1.0L) * 1'000'000.0L);
    result.rSquared = static_cast<double>(std::clamp(1.0L - residualSquares / varianceY, 0.0L, 1.0L));
    result.rmsResidualMilliseconds = static_cast<double>(std::sqrt(residualSquares / points.size()) * 1'000.0L);
    result.maximumResidualMilliseconds = static_cast<double>(maximumResidual * 1'000.0L);
    result.analysisSnapshots = points.size();
    return result.rSquared >= kMinimumRSquared;
}

}  // namespace detail

inline Result Analyze(const Series& capture, const Series& render) {
    Result analysis;
    auto captureData = detail::Prepare(capture, Direction::Capture);
    auto renderData = detail::Prepare(render, Direction::Render);
    analysis.capture = captureData.result;
    analysis.render = renderData.result;
    if (captureData.apiFailed || renderData.apiFailed) {
        analysis.status = Status::ClockApiFailed;
        if (captureData.apiFailed) analysis.capture.status = Status::ClockApiFailed;
        if (renderData.apiFailed) analysis.render.status = Status::ClockApiFailed;
        return analysis;
    }
    if (captureData.unreliable || renderData.unreliable) {
        analysis.status = Status::UnreliableTimestamps;
        if (captureData.unreliable) analysis.capture.status = Status::UnreliableTimestamps;
        if (renderData.unreliable) analysis.render.status = Status::UnreliableTimestamps;
        return analysis;
    }
    if (captureData.storageFailed || renderData.storageFailed ||
        captureData.points.size() <= kWarmupSamples + kTrimEndSamples ||
        renderData.points.size() <= kWarmupSamples + kTrimEndSamples) {
        analysis.status = Status::InsufficientData;
        return analysis;
    }
    auto& capturePoints = captureData.points;
    auto& renderPoints = renderData.points;
    const auto allCapturePoints = capturePoints;
    const auto allRenderPoints = renderPoints;
    capturePoints.erase(capturePoints.begin(), capturePoints.begin() + kWarmupSamples);
    renderPoints.erase(renderPoints.begin(), renderPoints.begin() + kWarmupSamples);
    capturePoints.pop_back();
    renderPoints.pop_back();
    const auto overlapStart = std::max(capturePoints.front().qpc100ns, renderPoints.front().qpc100ns);
    const auto overlapEnd = std::min(capturePoints.back().qpc100ns, renderPoints.back().qpc100ns);
    if (overlapEnd <= overlapStart || overlapEnd - overlapStart < kMinimumFitSpanHns) {
        analysis.status = Status::InsufficientData;
        return analysis;
    }
    const auto inOverlap = [overlapStart, overlapEnd](const detail::Point& point) {
        return point.qpc100ns >= overlapStart && point.qpc100ns <= overlapEnd;
    };
    capturePoints.erase(std::remove_if(capturePoints.begin(), capturePoints.end(),
        [&](const detail::Point& point) { return !inOverlap(point); }), capturePoints.end());
    renderPoints.erase(std::remove_if(renderPoints.begin(), renderPoints.end(),
        [&](const detail::Point& point) { return !inOverlap(point); }), renderPoints.end());
    analysis.overlapStart100ns = overlapStart;
    analysis.overlapEnd100ns = overlapEnd;
    if (capturePoints.size() < kMinimumFitSamples || renderPoints.size() < kMinimumFitSamples) {
        analysis.status = Status::InsufficientData;
        return analysis;
    }
    const bool captureFit = detail::FitRate(capturePoints, allCapturePoints, capture.frequency,
        overlapStart, analysis.capture);
    const bool renderFit = detail::FitRate(renderPoints, allRenderPoints, render.frequency,
        overlapStart, analysis.render);
    if (!captureFit || !renderFit) {
        analysis.status = Status::UnreliableTimestamps;
        if (!captureFit) analysis.capture.status = Status::UnreliableTimestamps;
        if (!renderFit) analysis.render.status = Status::UnreliableTimestamps;
        return analysis;
    }
    analysis.capture.status = captureData.result.rejectedSFalse || captureData.result.rejectedApiErrors
        ? Status::AvailableWithRejections : Status::Available;
    analysis.render.status = renderData.result.rejectedSFalse || renderData.result.rejectedApiErrors
        ? Status::AvailableWithRejections : Status::Available;
    analysis.status = analysis.capture.status == Status::AvailableWithRejections ||
        analysis.render.status == Status::AvailableWithRejections
        ? Status::AvailableWithRejections : Status::Available;
    analysis.relativeRateDifferencePpm = analysis.capture.ratePpm - analysis.render.ratePpm;
    analysis.relativePhaseChangeMilliseconds = (analysis.capture.ratePpm - analysis.render.ratePpm) /
        1'000'000.0 * static_cast<double>(overlapEnd - overlapStart) / kHnsPerSecond * 1'000.0;
    analysis.hasRegression = true;
    return analysis;
}

inline const char* StatusText(Status status) {
    switch (status) {
    case Status::Available: return "AVAILABLE";
    case Status::AvailableWithRejections: return "AVAILABLE_WITH_REJECTIONS";
    case Status::InsufficientData: return "INSUFFICIENT_DATA";
    case Status::UnreliableTimestamps: return "UNRELIABLE_TIMESTAMPS";
    case Status::ClockApiFailed: return "CLOCK_API_FAILED";
    }
    return "INSUFFICIENT_DATA";
}

}  // namespace ClockAnalysis
