#pragma once

#include <cstdint>
#include <limits>
#include <set>
#include <stdexcept>
#include <string>
#include <string_view>
#include <vector>

#include "channel_routing.h"

namespace ScanPlanV2V4 {
struct Candidate {
    unsigned index{};
    std::int64_t duration100ns{};
    std::uint32_t captureFrames{}, renderFrames{};
    std::int64_t capturePeriod100ns{}, renderPeriod100ns{};
    std::uint32_t captureAlignmentFrames{1}, renderAlignmentFrames{1};
    std::uint32_t captureMin{}, captureMax{}, renderMin{}, renderMax{};
    std::string rejectionReason;
};
struct Budget { unsigned operations{}, activeSeconds{}, sessionSeconds{}; };

inline Budget ComputeBudget(unsigned n, unsigned r) {
    if (n < 1 || n > 8 || r > 5) throw std::invalid_argument("V4 candidate/repeatability count out of range");
    const auto operations = 3U * n + r;
    const auto active = 5U * (n + r);
    const auto session = 111U * n + 51U * r + 28U;
    if (operations > 29 || active > 65 || session > 1171) throw std::invalid_argument("V4 budget exceeds hard ceiling");
    return {operations, active, session};
}

inline std::int64_t Duration(std::uint32_t frames, std::uint32_t rate) {
    if (frames == 0 || rate == 0) throw std::invalid_argument("zero frame/rate geometry");
    return static_cast<std::int64_t>((static_cast<std::uint64_t>(frames) * 10'000'000ULL + rate / 2U) / rate);
}
inline std::uint32_t Frames(std::int64_t duration100ns, std::uint32_t rate) {
    if (duration100ns <= 0 || rate == 0 || static_cast<std::uint64_t>(duration100ns) > (std::numeric_limits<std::uint64_t>::max() - 5'000'000ULL) / rate)
        throw std::invalid_argument("period-to-frame conversion overflow");
    const auto frames = (static_cast<std::uint64_t>(duration100ns) * rate + 5'000'000ULL) / 10'000'000ULL;
    if (frames == 0 || frames > std::numeric_limits<std::uint32_t>::max()) throw std::invalid_argument("period frame count out of range");
    return static_cast<std::uint32_t>(frames);
}

inline std::string Canonical(std::string_view captureId, std::string_view renderId,
                             const ChannelRouting::Configuration& route,
                             const std::vector<Candidate>& candidates, unsigned repeatability,
                             unsigned maximumOperations, unsigned maximumActiveSeconds,
                             unsigned initializeTimeoutSeconds, unsigned sessionLifetimeSeconds,
                             unsigned cleanupDeadlineSeconds, std::uint64_t generation,
                             std::string_view routeFingerprint) {
    if (captureId.empty() || renderId.empty() || captureId == renderId || captureId != route.captureEndpointId || renderId != route.renderEndpointId || candidates.empty() || candidates.size() > 8 || repeatability > 5)
        throw std::invalid_argument("invalid V4 identity or scope");
    const auto budget = ComputeBudget(static_cast<unsigned>(candidates.size()), repeatability);
    if (maximumOperations != budget.operations || maximumActiveSeconds != budget.activeSeconds || sessionLifetimeSeconds != budget.sessionSeconds || initializeTimeoutSeconds != 30 || cleanupDeadlineSeconds != 8)
        throw std::invalid_argument("V4 budget mismatch");
    if (routeFingerprint != ChannelRouting::Fingerprint(route)) throw std::invalid_argument("V4 route fingerprint mismatch");
    std::string result = "SCAN_PLAN_V2|4|" + std::to_string(generation) + "|" + std::string(captureId) + "|" + std::string(renderId) + "|" + std::string(routeFingerprint) + "|" + std::to_string(repeatability) + "|" + std::to_string(budget.operations) + "|" + std::to_string(budget.activeSeconds) + "|" + std::to_string(initializeTimeoutSeconds) + "|" + std::to_string(sessionLifetimeSeconds) + "|" + std::to_string(cleanupDeadlineSeconds) + "|\n";
    std::int64_t previous = 0;
    std::set<std::pair<std::int64_t, std::int64_t>> seenGeometry;
    for (std::size_t i = 0; i < candidates.size(); ++i) {
        const auto& c = candidates[i];
        if (c.index != i || c.duration100ns <= previous || c.captureFrames == 0 || c.renderFrames == 0 || c.captureAlignmentFrames == 0 || c.renderAlignmentFrames == 0 ||
            c.captureMin == 0 || c.renderMin == 0 || c.captureMax < c.captureMin || c.renderMax < c.renderMin ||
            (c.rejectionReason.empty() && (c.captureFrames < c.captureMin || c.captureFrames > c.captureMax || c.renderFrames < c.renderMin || c.renderFrames > c.renderMax || c.captureFrames % c.captureAlignmentFrames != 0 || c.renderFrames % c.renderAlignmentFrames != 0)) ||
            c.captureFrames != Frames(c.duration100ns, route.capture.format.sampleRate) ||
            c.renderFrames != Frames(c.duration100ns, route.render.format.sampleRate) ||
            c.capturePeriod100ns != Duration(c.captureFrames, route.capture.format.sampleRate) || c.renderPeriod100ns != Duration(c.renderFrames, route.render.format.sampleRate) ||
            !seenGeometry.emplace(c.capturePeriod100ns, c.renderPeriod100ns).second)
            throw std::invalid_argument("invalid, unordered, or ambiguous V4 candidate geometry");
        previous = c.duration100ns;
        result += "C|" + std::to_string(c.index) + "|" + std::to_string(c.duration100ns) + "|" + std::to_string(c.captureFrames) + "|" + std::to_string(c.renderFrames) + "|" + std::to_string(c.capturePeriod100ns) + "|" + std::to_string(c.renderPeriod100ns) + "|" + std::to_string(c.captureAlignmentFrames) + "|" + std::to_string(c.renderAlignmentFrames) + "|" + std::to_string(c.captureMin) + "|" + std::to_string(c.captureMax) + "|" + std::to_string(c.renderMin) + "|" + std::to_string(c.renderMax) + "|" + c.rejectionReason + "\n";
    }
    result += "M|OFF\n";
    return result;
}
} // namespace ScanPlanV2V4
