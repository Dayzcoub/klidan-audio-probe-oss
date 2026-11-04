#pragma once

#include "exclusive_format_matrix.h"
#include "probe_math.h"

#include <algorithm>
#include <array>
#include <cstdint>
#include <set>
#include <stdexcept>
#include <string>
#include <string_view>
#include <sstream>
#include <vector>

namespace ExclusivePeriodDiscovery {

constexpr std::size_t kMaximumCandidates = 8;
enum class Classification { Accepted, AcceptedAligned, Rejected, Incomplete, Unknown };

struct DevicePeriods {
    std::int64_t minimum100ns = 0;
    std::int64_t default100ns = 0;
};

struct Candidate {
    std::int64_t requestedPeriod100ns = 0;
    std::uint32_t requestedFrames = 0;
    Classification classification = Classification::Unknown;
    std::string reason;
    ExclusiveFormatMatrix::Attempt attempt{};
    bool alignmentRequiresConfirmation = false;
};

struct Report {
    std::string endpointId;
    ExclusiveFormatMatrix::Direction direction = ExclusiveFormatMatrix::Direction::Capture;
    ExclusiveFormatMatrix::Format format{};
    DevicePeriods devicePeriods{};
    std::string operationId;
    std::string reportId;
    std::vector<Candidate> candidates;
};

inline const char* ClassificationText(Classification value) noexcept {
    switch (value) {
    case Classification::Accepted: return "ACCEPTED";
    case Classification::AcceptedAligned: return "ACCEPTED_ALIGNED";
    case Classification::Rejected: return "REJECTED";
    case Classification::Incomplete: return "INCOMPLETE";
    case Classification::Unknown: return "UNKNOWN";
    }
    return "UNKNOWN";
}

inline std::vector<std::uint32_t> GenerateIntermediateFrames(DevicePeriods periods, std::uint32_t rate,
                                                               std::size_t maximum = kMaximumCandidates) {
    if (rate == 0 || periods.minimum100ns <= 0 || periods.default100ns <= periods.minimum100ns || maximum == 0)
        return {};
    const auto minimum = static_cast<std::uint32_t>(ProbeMath::FramesForDuration(periods.minimum100ns, rate));
    const auto maximumFrames = static_cast<std::uint32_t>(ProbeMath::FramesForDuration(periods.default100ns, rate));
    if (minimum == 0 || maximumFrames <= minimum) return {};
    std::set<std::uint32_t> frames;
    const auto add = [&](std::uint32_t value) { if (value > minimum && value < maximumFrames) frames.insert(value); };
    // Practical reference values are candidates only when the endpoint bounds contain them.
    for (const auto value : {192U, 240U, 288U, 336U, 384U, 432U}) add(value);
    const std::array derived{minimum + (maximumFrames - minimum) / 4,
                             minimum + (maximumFrames - minimum) / 2,
                             minimum + (maximumFrames - minimum) * 3 / 4};
    for (const auto value : derived) if (value > minimum && value < maximumFrames && frames.size() < maximum) frames.insert(value);
    std::vector<std::uint32_t> result;
    for (const auto value : frames) { if (result.size() == maximum) break; result.push_back(value); }
    return result;
}

inline std::vector<ExclusiveFormatMatrix::PeriodRequest> GenerateCandidates(DevicePeriods periods,
                                                                              std::uint32_t rate,
                                                                              std::size_t maximum = kMaximumCandidates) {
    std::vector<ExclusiveFormatMatrix::PeriodRequest> result;
    for (const auto frames : GenerateIntermediateFrames(periods, rate, maximum))
        result.push_back(ExclusiveFormatMatrix::PeriodRequest::Custom(ProbeMath::DurationForFrames(frames, rate), frames));
    return result;
}

inline Classification Classify(const ExclusiveFormatMatrix::Attempt& attempt) noexcept {
    if (attempt.cleanup == ExclusiveFormatMatrix::Cleanup::Failed) return Classification::Unknown;
    if (attempt.cleanup != ExclusiveFormatMatrix::Cleanup::ReleaseAttempted || attempt.clientReleaseAttempts == 0)
        return Classification::Unknown;
    if (!attempt.finalInitializeHr) return Classification::Incomplete;
    if (*attempt.finalInitializeHr == ExclusiveFormatMatrix::kOk) {
        if (!attempt.actualBufferFrames || !attempt.actualPeriod100ns || !attempt.actualBufferSizeHr || *attempt.actualBufferSizeHr != ExclusiveFormatMatrix::kOk)
            return Classification::Incomplete;
        const bool actualMatchesRequested = attempt.requestedBufferFrames && attempt.requestedPeriod100ns &&
            *attempt.actualBufferFrames == *attempt.requestedBufferFrames && *attempt.actualPeriod100ns == *attempt.requestedPeriod100ns;
        if (!attempt.firstInitializeHr) return Classification::Incomplete;
        if (attempt.firstInitializeHr && *attempt.firstInitializeHr == ExclusiveFormatMatrix::kBufferSizeNotAligned) {
            const bool actualMatchesAlignment = attempt.alignmentFrames && attempt.alignmentPeriod100ns &&
                *attempt.actualBufferFrames == *attempt.alignmentFrames && *attempt.actualPeriod100ns == *attempt.alignmentPeriod100ns;
            return actualMatchesAlignment ? Classification::AcceptedAligned : Classification::Incomplete;
        }
        return actualMatchesRequested ? Classification::Accepted : Classification::Rejected;
    }
    return Classification::Rejected;
}

template<class Api>
inline Report Probe(const std::string& endpointId, ExclusiveFormatMatrix::Direction direction,
                    const ExclusiveFormatMatrix::Format& format, DevicePeriods periods,
                    const std::string& operationId, const std::string& reportId, Api& api,
                    std::size_t maximum = kMaximumCandidates) {
    if (endpointId.empty() || operationId.empty() || reportId.empty() || format.sampleRate == 0 || format.channels == 0)
        throw std::invalid_argument("period discovery identity and format are required");
    Report report{endpointId, direction, format, periods, operationId, reportId, {}};
    for (const auto& request : GenerateCandidates(periods, format.sampleRate, maximum)) {
        auto attempt = api.InitializeOnly(format, request);
        const auto classification = Classify(attempt);
        report.candidates.push_back({*request.requestedPeriod100ns, *request.requestedFrames, classification,
            classification == Classification::Accepted ? "Exact requested period initialized and client release was attempted" :
            classification == Classification::AcceptedAligned ? "Original request was not aligned; adjusted retry initialized and client release was attempted" :
            classification == Classification::Rejected ? "Initialize rejected the exact requested period" :
            classification == Classification::Incomplete ? "Initialize evidence is incomplete" : "Cleanup or native state is unknown", attempt,
            classification == Classification::AcceptedAligned});
    }
    return report;
}

struct Eligibility {
    std::uint32_t requestedFrames = 0;
    bool captureAccepted = false;
    bool renderAccepted = false;
    bool unknown = false;
    std::string status;
};

inline std::vector<Eligibility> Compare(const Report* capture, const Report* render) {
    std::set<std::uint32_t> values;
    for (const auto* report : {capture, render}) if (report) for (const auto& item : report->candidates) values.insert(item.requestedFrames);
    std::vector<Eligibility> result;
    for (const auto frames : values) {
        const auto find = [frames](const Report* report) -> const Candidate* {
            if (!report) return nullptr;
            const auto it = std::find_if(report->candidates.begin(), report->candidates.end(), [frames](const auto& c) { return c.requestedFrames == frames; });
            return it == report->candidates.end() ? nullptr : &*it;
        };
        const auto* c = find(capture); const auto* r = find(render);
        const bool unknown = !c || !r || c->classification == Classification::Unknown || r->classification == Classification::Unknown;
        result.push_back({frames, c && c->classification == Classification::Accepted, r && r->classification == Classification::Accepted,
                          unknown, unknown ? "UNKNOWN" : c->classification == Classification::Accepted && r->classification == Classification::Accepted ? "BOTH_EXACT" :
                              c->classification == Classification::Accepted ? "CAPTURE_ONLY_EXACT" : r->classification == Classification::Accepted ? "RENDER_ONLY_EXACT" :
                              c->classification == Classification::AcceptedAligned || r->classification == Classification::AcceptedAligned ? "ALIGNED_REQUIRES_CONFIRMATION" : "NEITHER"});
    }
    return result;
}

inline std::string ToJson(const Report& report, std::string_view timestampUtc, std::string_view provenance) {
    using namespace ExclusiveFormatMatrix;
    std::ostringstream out;
    out << "{\"schemaVersion\":1,\"reportType\":\"wasapi-exclusive-period-discovery\",\"reportId\":"
        << JsonString(report.reportId) << ",\"operationId\":" << JsonString(report.operationId)
        << ",\"timestampUtc\":" << JsonString(std::string(timestampUtc)) << ",\"provenance\":" << JsonString(std::string(provenance))
        << ",\"endpointId\":" << JsonString(report.endpointId) << ",\"direction\":" << JsonString(report.direction == ExclusiveFormatMatrix::Direction::Capture ? "CAPTURE" : "RENDER")
        << ",\"format\":{\"sampleRateHz\":" << report.format.sampleRate << ",\"encoding\":" << JsonString(EncodingText(report.format.encoding))
        << ",\"channels\":" << report.format.channels << "},\"devicePeriods\":{\"minimum100ns\":" << report.devicePeriods.minimum100ns
        << ",\"default100ns\":" << report.devicePeriods.default100ns << "},\"startCalled\":false,\"audioTransferred\":false,\"candidates\":[";
    for (std::size_t i = 0; i < report.candidates.size(); ++i) {
        const auto& c = report.candidates[i]; const auto& a = c.attempt;
        out << "{\"requestedPeriod100ns\":" << c.requestedPeriod100ns << ",\"requestedFrames\":" << c.requestedFrames
            << ",\"requestedEquivalentPeriod100ns\":" << ProbeMath::DurationForFrames(c.requestedFrames, report.format.sampleRate)
            << ",\"classification\":" << JsonString(ClassificationText(c.classification))
            << ",\"reason\":" << JsonString(c.reason)
            << ",\"finalInitializeHr\":" << HresultJson(a.finalInitializeHr)
            << ",\"alignmentFrames\":" << NumberJson(a.alignmentFrames)
            << ",\"alignmentInitializeHr\":" << HresultJson(a.alignmentInitializeHr)
            << ",\"alignmentAlternativeFrames\":" << NumberJson(a.alignmentFrames)
            << ",\"alignmentRequiresExplicitConfirmation\":" << (c.alignmentRequiresConfirmation ? "true" : "false")
            << ",\"actualBufferFrames\":" << NumberJson(a.actualBufferFrames)
            << ",\"actualPeriod100ns\":" << NumberJson(a.actualPeriod100ns)
            << ",\"cleanupStatus\":" << JsonString(CleanupText(a.cleanup))
            << ",\"clientReleaseAttempts\":" << a.clientReleaseAttempts
            << ",\"requestedFormat\":{\"sampleRateHz\":" << report.format.sampleRate << ",\"encoding\":" << JsonString(EncodingText(report.format.encoding)) << ",\"channels\":" << report.format.channels << "}}"
            << (i + 1 == report.candidates.size() ? "" : ",");
    }
    out << "]}";
    return out.str();
}

} // namespace ExclusivePeriodDiscovery
