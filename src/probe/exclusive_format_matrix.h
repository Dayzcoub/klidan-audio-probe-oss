#pragma once

#include <array>
#include <algorithm>
#include <cstdint>
#include <iomanip>
#include <optional>
#include <sstream>
#include <stdexcept>
#include <string>
#include <string_view>
#include <vector>

namespace ExclusiveFormatMatrix {

enum class Direction { Capture, Render };
enum class Level { PassiveQuery, InitializeOnly };
enum class Encoding { Pcm16, Pcm24In32, Float32 };
enum class Period { Default, Minimum };
enum class Status { SupportedReported, Initialized, Ready, Unsupported, Failed, NotTested, Incomplete };
enum class Cleanup { NotAcquired, ReleaseAttempted, Failed, Incomplete };

constexpr std::int32_t kOk = 0;
constexpr std::int32_t kFalse = 1;
constexpr std::int32_t kBufferSizeNotAligned = static_cast<std::int32_t>(0x88890019U);
constexpr std::int32_t kUnsupportedFormat = static_cast<std::int32_t>(0x88890008U);

struct Format {
    std::uint32_t sampleRate = 0;
    std::uint16_t channels = 0;
    Encoding encoding = Encoding::Pcm16;
};

// Legacy DEFAULT/MINIMUM remain an enum contract. Custom periods carry their
// exact WASAPI duration and frame request separately and are never aliases.
struct PeriodRequest {
    Period mode = Period::Default;
    std::optional<std::int64_t> requestedPeriod100ns;
    std::optional<std::uint32_t> requestedFrames;
    static constexpr PeriodRequest Legacy(Period value) noexcept { return {value, std::nullopt, std::nullopt}; }
    static constexpr PeriodRequest Custom(std::int64_t duration100ns, std::uint32_t frames) noexcept {
        return {Period::Default, duration100ns, frames};
    }
    constexpr bool IsCustom() const noexcept { return requestedPeriod100ns.has_value(); }
};

struct QueryResult {
    std::optional<std::int32_t> activationHr;
    std::optional<std::int32_t> supportHr;
    Cleanup cleanup = Cleanup::Incomplete;
    std::uint32_t clientReleaseAttempts = 0;
    std::optional<std::int32_t> devicePeriodHr;
    std::optional<std::int64_t> defaultDevicePeriod100ns;
    std::optional<std::int64_t> minimumDevicePeriod100ns;
};

struct Attempt {
    std::optional<std::int32_t> activationHr;
    std::optional<std::int32_t> periodQueryHr;
    std::optional<bool> periodAvailable;
    std::optional<std::int64_t> requestedPeriod100ns;
    std::optional<std::uint32_t> requestedBufferFrames;
    bool initializeAttempted = false;
    std::optional<std::int32_t> firstInitializeHr;
    std::optional<std::int32_t> alignmentGetBufferSizeHr;
    std::optional<std::uint32_t> alignmentFrames;
    std::optional<std::int32_t> alignmentActivateHr;
    std::optional<std::int64_t> alignmentPeriod100ns;
    std::optional<std::int32_t> alignmentInitializeHr;
    std::optional<std::int32_t> finalInitializeHr;
    std::optional<std::int32_t> actualBufferSizeHr;
    std::optional<std::uint32_t> actualBufferFrames;
    std::optional<std::int64_t> actualPeriod100ns;
    std::optional<std::int32_t> eventCreateHr;
    std::optional<std::int32_t> setEventHandleHr;
    std::optional<std::int32_t> eventHandleCloseHr;
    Cleanup cleanup = Cleanup::Incomplete;
    std::uint32_t clientReleaseAttempts = 0;
};

struct Candidate {
    Direction direction = Direction::Capture;
    Format format{};
    Period period = Period::Default;
    Status status = Status::NotTested;
    QueryResult query{};
    Attempt attempt{};
};

struct Report {
    Direction direction = Direction::Capture;
    Level level = Level::PassiveQuery;
    std::vector<Candidate> candidates;
};

class Api {
public:
    virtual ~Api() = default;
    virtual QueryResult QuerySupport(const Format& format) = 0;
    virtual Attempt InitializeOnly(const Format& format, Period period) = 0;
    virtual Attempt InitializeOnly(const Format& format, const PeriodRequest& request) {
        if (request.IsCustom()) throw std::invalid_argument("custom periods require a custom-capable WASAPI API");
        return InitializeOnly(format, request.mode);
    }
};

inline const char* DirectionText(Direction v) noexcept { return v == Direction::Capture ? "capture" : "render"; }
inline const char* LevelText(Level v) noexcept { return v == Level::PassiveQuery ? "PASSIVE_QUERY" : "INITIALIZE_ONLY"; }
inline const char* PeriodText(Period v) noexcept { return v == Period::Default ? "default" : "minimum"; }
inline const char* EncodingText(Encoding v) noexcept {
    switch (v) {
    case Encoding::Pcm16: return "PCM16";
    case Encoding::Pcm24In32: return "PCM24_IN_32";
    case Encoding::Float32: return "FLOAT32";
    }
    return "UNKNOWN";
}
inline const char* StatusText(Status v) noexcept {
    switch (v) {
    case Status::SupportedReported: return "SUPPORTED_REPORTED";
    case Status::Initialized: return "INITIALIZED";
    case Status::Ready: return "READY";
    case Status::Unsupported: return "UNSUPPORTED";
    case Status::Failed: return "FAILED";
    case Status::NotTested: return "NOT_TESTED";
    case Status::Incomplete: return "INCOMPLETE";
    }
    return "INCOMPLETE";
}
inline const char* CleanupText(Cleanup v) noexcept {
    switch (v) {
    case Cleanup::NotAcquired: return "NOT_ACQUIRED";
    case Cleanup::ReleaseAttempted: return "RELEASE_ATTEMPTED";
    case Cleanup::Failed: return "FAILED";
    case Cleanup::Incomplete: return "INCOMPLETE";
    }
    return "INCOMPLETE";
}
inline const char* CellText(Status v) noexcept {
    switch (v) {
    case Status::SupportedReported: return "SUPPORTED";
    case Status::Initialized: return "INIT";
    case Status::Ready: return "READY";
    case Status::Unsupported: return "NO";
    case Status::Failed: return "FAILED";
    case Status::NotTested: return "N/T";
    case Status::Incomplete: return "INCOMPLETE";
    }
    return "INCOMPLETE";
}

inline const char* SupportStatusText(const QueryResult& query) noexcept {
    if (query.cleanup == Cleanup::Failed) return "FAILED";
    if (query.cleanup == Cleanup::Incomplete) return "INCOMPLETE";
    if (query.supportHr && query.cleanup != Cleanup::ReleaseAttempted) return "INCOMPLETE";
    if (!query.supportHr) return query.activationHr && *query.activationHr != kOk ? "FAILED" : "INCOMPLETE";
    if (*query.supportHr == kOk) return "SUPPORTED_REPORTED";
    if (*query.supportHr == kFalse || *query.supportHr == kUnsupportedFormat) return "UNSUPPORTED";
    return "FAILED";
}
inline const char* InitializeStatusText(const Candidate& candidate, Level level) noexcept {
    const auto& a = candidate.attempt;
    if (level == Level::PassiveQuery || (a.periodAvailable && !*a.periodAvailable)) return "NOT_TESTED";
    if (a.finalInitializeHr) {
        if (*a.finalInitializeHr == kOk) return "INITIALIZED";
        if (*a.finalInitializeHr == kUnsupportedFormat) return "UNSUPPORTED";
        return "FAILED";
    }
    if ((a.alignmentGetBufferSizeHr && *a.alignmentGetBufferSizeHr != kOk) ||
        (a.alignmentActivateHr && *a.alignmentActivateHr != kOk)) return "FAILED";
    if (a.alignmentInitializeHr) {
        if (*a.alignmentInitializeHr == kUnsupportedFormat) return "UNSUPPORTED";
        return "FAILED";
    }
    if (a.firstInitializeHr) {
        if (*a.firstInitializeHr != kBufferSizeNotAligned) {
            return *a.firstInitializeHr == kUnsupportedFormat ? "UNSUPPORTED" : "FAILED";
        }
        return "INCOMPLETE";
    }
    if (a.alignmentGetBufferSizeHr || a.alignmentActivateHr || a.alignmentPeriod100ns) return "INCOMPLETE";
    return a.initializeAttempted ? "INCOMPLETE" : "NOT_TESTED";
}

inline Status Classify(const Candidate& candidate, Level level) noexcept {
    if (level == Level::PassiveQuery) {
        if (candidate.query.cleanup == Cleanup::Failed) return Status::Failed;
        if (candidate.query.cleanup == Cleanup::Incomplete) return Status::Incomplete;
        if (candidate.query.supportHr && candidate.query.cleanup != Cleanup::ReleaseAttempted)
            return Status::Incomplete;
        if (!candidate.query.supportHr)
            return candidate.query.activationHr && *candidate.query.activationHr != kOk
                ? Status::Failed : Status::Incomplete;
        if (*candidate.query.supportHr == kOk) return Status::SupportedReported;
        if (*candidate.query.supportHr == kFalse || *candidate.query.supportHr == kUnsupportedFormat)
            return Status::Unsupported;
        return Status::Failed;
    }
    if (candidate.attempt.periodAvailable && !*candidate.attempt.periodAvailable) return Status::Unsupported;
    const auto& a = candidate.attempt;
    if (a.activationHr && *a.activationHr != kOk) return Status::Failed;
    const char* initializeStatus = InitializeStatusText(candidate, level);
    if (std::string_view(initializeStatus) == "NOT_TESTED")
        return a.activationHr && *a.activationHr != kOk ? Status::Failed
            : a.periodQueryHr && *a.periodQueryHr != kOk ? Status::Failed
            : a.activationHr || a.periodQueryHr ? Status::Incomplete : Status::NotTested;
    if (std::string_view(initializeStatus) == "INCOMPLETE") return Status::Incomplete;
    if (std::string_view(initializeStatus) == "UNSUPPORTED") return Status::Unsupported;
    if (std::string_view(initializeStatus) == "FAILED") return Status::Failed;
    if (a.eventHandleCloseHr && *a.eventHandleCloseHr != kOk) return Status::Failed;
    if (a.cleanup == Cleanup::Failed) return Status::Failed;
    if ((a.actualBufferSizeHr && *a.actualBufferSizeHr != kOk) ||
        (a.eventCreateHr && *a.eventCreateHr != kOk) ||
        (a.setEventHandleHr && *a.setEventHandleHr != kOk) ||
        (a.actualBufferFrames && *a.actualBufferFrames == 0) ||
        (a.actualPeriod100ns && *a.actualPeriod100ns <= 0)) return Status::Failed;
    if (a.cleanup != Cleanup::ReleaseAttempted || a.clientReleaseAttempts == 0 ||
        !a.eventHandleCloseHr || !a.eventCreateHr ||
        !a.setEventHandleHr || *a.eventCreateHr != kOk || *a.setEventHandleHr != kOk ||
        !a.actualBufferFrames || !a.actualPeriod100ns || *a.actualBufferFrames == 0 ||
        *a.actualPeriod100ns <= 0 || !a.actualBufferSizeHr || *a.actualBufferSizeHr != kOk)
        return Status::Incomplete;
    return Status::Ready;
}

inline Report RunCandidates(Direction direction, Level level, Api& api,
                            const std::vector<Format>& formats) {
    if (formats.size() > 192) throw std::invalid_argument("exclusive format candidate count exceeds 192");
    constexpr std::array encodings{Encoding::Pcm16, Encoding::Pcm24In32, Encoding::Float32};
    constexpr std::array periods{Period::Default, Period::Minimum};
    Report report{direction, level, {}};
    report.candidates.reserve(formats.size() * periods.size());
    for (const auto& format : formats) {
        if (format.sampleRate == 0 || format.channels == 0 || format.channels > 64 ||
            std::find(encodings.begin(), encodings.end(), format.encoding) == encodings.end())
            throw std::invalid_argument("exclusive format candidate is not representable");
        const QueryResult query = api.QuerySupport(format);
        for (const auto period : periods) {
            Candidate candidate{direction, format, period, Status::NotTested, query, {}};
            if (level == Level::InitializeOnly) candidate.attempt = api.InitializeOnly(format, period);
            candidate.status = Classify(candidate, level);
            report.candidates.push_back(candidate);
        }
    }
    return report;
}

inline Candidate RunSingle(Direction direction, const Format& format, Period period, Api& api,
                           bool initializeOnly) {
    if (format.sampleRate == 0 || format.channels == 0 || format.channels > 64)
        throw std::invalid_argument("exclusive format candidate is not representable");
    Candidate candidate{direction, format, period, Status::NotTested, api.QuerySupport(format), {}};
    if (!initializeOnly || std::string_view(SupportStatusText(candidate.query)) != "SUPPORTED_REPORTED") {
        candidate.status = Classify(candidate, Level::PassiveQuery);
        return candidate;
    }
    candidate.attempt = api.InitializeOnly(format, period);
    candidate.status = Classify(candidate, Level::InitializeOnly);
    return candidate;
}

inline Report Run(Direction direction, Level level, Api& api) {
    constexpr std::array rates{44'100U, 48'000U};
    constexpr std::array channels{std::uint16_t{1}, std::uint16_t{2}};
    constexpr std::array encodings{Encoding::Pcm16, Encoding::Pcm24In32, Encoding::Float32};
    std::vector<Format> formats;
    formats.reserve(12);
    for (const auto rate : rates) for (const auto channelCount : channels) for (const auto encoding : encodings)
        formats.push_back({rate, channelCount, encoding});
    return RunCandidates(direction, level, api, formats);
}

inline std::string HresultJson(const std::optional<std::int32_t>& hr) {
    if (!hr) return "null";
    std::ostringstream out;
    out << '"' << "0x" << std::uppercase << std::hex << std::setw(8) << std::setfill('0')
        << static_cast<std::uint32_t>(*hr) << '"';
    return out.str();
}
inline std::string NumberJson(const std::optional<std::uint32_t>& n) {
    return n ? std::to_string(*n) : "null";
}
inline std::string NumberJson(const std::optional<std::int64_t>& n) {
    return n ? std::to_string(*n) : "null";
}
inline std::string JsonString(const std::string& value) {
    std::string out = "\"";
    for (const unsigned char c : value) {
        switch (c) {
        case '"': out += "\\\""; break;
        case '\\': out += "\\\\"; break;
        case '\b': out += "\\b"; break;
        case '\f': out += "\\f"; break;
        case '\n': out += "\\n"; break;
        case '\r': out += "\\r"; break;
        case '\t': out += "\\t"; break;
        default:
            if (c < 0x20) {
                constexpr char hex[] = "0123456789abcdef";
                out += "\\u00"; out += hex[c >> 4]; out += hex[c & 15];
            } else out += static_cast<char>(c);
        }
    }
    out += '"';
    return out;
}

inline std::string ToJson(const Report& report, const std::string& timestampUtc,
                          const std::string& probeVersion, const std::string& reportId) {
    std::ostringstream out;
    out << "{\n  \"schemaVersion\": 1,\n  \"reportType\": \"wasapi-exclusive-format-matrix\",\n"
        << "  \"reportId\": " << JsonString(reportId) << ",\n"
        << "  \"timestampUtc\": " << JsonString(timestampUtc) << ",\n"
        << "  \"probeVersion\": " << JsonString(probeVersion) << ",\n"
        << "  \"platform\": \"Windows\",\n  \"backend\": \"wasapi\",\n"
        << "  \"device\": {\"manufacturer\": null, \"model\": null, \"driverVersion\": null},\n"
        << "  \"direction\": " << JsonString(DirectionText(report.direction)) << ",\n"
        << "  \"evidence\": {\"sourceType\": " << JsonString(LevelText(report.level))
        << ", \"klidanVerified\": false, \"certified\": false},\n"
        << "  \"privacy\": \"sanitized\",\n  \"candidates\": [\n";
    for (std::size_t i = 0; i < report.candidates.size(); ++i) {
        const auto& c = report.candidates[i];
        const auto bits = c.format.encoding == Encoding::Pcm16 ? 16U : 32U;
        const auto validBits = c.format.encoding == Encoding::Pcm24In32 ? 24U : bits;
        out << "    {\"direction\": " << JsonString(DirectionText(c.direction))
            << ", \"requested\": {\"sampleRateHz\": " << c.format.sampleRate
            << ", \"channels\": " << c.format.channels << ", \"format\": " << JsonString(EncodingText(c.format.encoding))
            << ", \"containerBits\": " << bits << ", \"validBits\": " << validBits
            << ", \"channelMaskEvidence\": " << JsonString(c.format.channels <= 2 ? "STANDARD_MONO_STEREO_MASK" : "UNKNOWN_NOT_ASSIGNED")
            << ", \"periodMode\": " << JsonString(PeriodText(c.period)) << "}"
            << ", \"status\": " << JsonString(StatusText(c.status))
            << ", \"isFormatSupportStatus\": " << JsonString(SupportStatusText(c.query))
            << ", \"queryClientActivationHr\": " << HresultJson(c.query.activationHr)
            << ", \"isFormatSupportedHr\": " << HresultJson(c.query.supportHr)
            << ", \"devicePeriodHr\": " << HresultJson(c.query.devicePeriodHr)
            << ", \"defaultDevicePeriod100ns\": " << NumberJson(c.query.defaultDevicePeriod100ns)
            << ", \"minimumDevicePeriod100ns\": " << NumberJson(c.query.minimumDevicePeriod100ns)
            << ", \"queryCleanupStatus\": " << JsonString(CleanupText(c.query.cleanup))
            << ", \"queryClientReleaseAttempts\": " << c.query.clientReleaseAttempts
            << ", \"queryCleanupHr\": null"
            << ", \"attemptActivationHr\": " << HresultJson(c.attempt.activationHr)
            << ", \"periodQueryHr\": " << HresultJson(c.attempt.periodQueryHr)
            << ", \"periodAvailable\": ";
        if (c.attempt.periodAvailable) out << (*c.attempt.periodAvailable ? "true" : "false");
        else out << "null";
        out << ", \"initializeStatus\": " << JsonString(InitializeStatusText(c, report.level))
            << ", \"initializeAttempted\": " << (c.attempt.initializeAttempted ? "true" : "false")
            << ", \"requestedPeriod100ns\": " << NumberJson(c.attempt.requestedPeriod100ns)
            << ", \"firstInitializeHr\": " << HresultJson(c.attempt.firstInitializeHr)
            << ", \"alignmentRetry\": ";
        if (!c.attempt.alignmentGetBufferSizeHr && !c.attempt.alignmentActivateHr && !c.attempt.alignmentInitializeHr) out << "null";
        else out << "{\"getBufferSizeHr\": " << HresultJson(c.attempt.alignmentGetBufferSizeHr)
            << ", \"alignedFrames\": " << NumberJson(c.attempt.alignmentFrames)
            << ", \"activateHr\": " << HresultJson(c.attempt.alignmentActivateHr)
            << ", \"retryPeriod100ns\": " << NumberJson(c.attempt.alignmentPeriod100ns)
            << ", \"initializeHr\": " << HresultJson(c.attempt.alignmentInitializeHr) << "}";
        out << ", \"finalInitializeHr\": " << HresultJson(c.attempt.finalInitializeHr)
            << ", \"actualBufferSizeHr\": " << HresultJson(c.attempt.actualBufferSizeHr)
            << ", \"actualBufferFrames\": " << NumberJson(c.attempt.actualBufferFrames)
            << ", \"actualPeriod100ns\": " << NumberJson(c.attempt.actualPeriod100ns)
            << ", \"eventCreateHr\": " << HresultJson(c.attempt.eventCreateHr)
            << ", \"setEventHandleHr\": " << HresultJson(c.attempt.setEventHandleHr)
            << ", \"eventHandleCloseHr\": " << HresultJson(c.attempt.eventHandleCloseHr)
            << ", \"eventHandleReady\": ";
        if (!c.attempt.eventCreateHr || !c.attempt.setEventHandleHr) out << "null";
        else out << (*c.attempt.eventCreateHr == kOk && *c.attempt.setEventHandleHr == kOk ? "true" : "false");
        out << ", \"cleanupStatus\": " << JsonString(CleanupText(c.attempt.cleanup))
            << ", \"clientReleaseAttempts\": " << c.attempt.clientReleaseAttempts
            << ", \"cleanupHr\": null"
            << ", \"startHr\": null, \"startCalled\": false, \"audioTransferred\": false}"
            << (i + 1 == report.candidates.size() ? "\n" : ",\n");
    }
    out << "  ]\n}\n";
    return out.str();
}

} // namespace ExclusiveFormatMatrix
