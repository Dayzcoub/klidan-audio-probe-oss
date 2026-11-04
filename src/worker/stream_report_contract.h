#pragma once

#include <rapidjson/document.h>

#include <cctype>
#include <string_view>

namespace StreamReportContract {

inline bool StringEquals(const rapidjson::Value& object, const char* name, std::string_view expected) {
    const auto member = object.FindMember(name);
    return member != object.MemberEnd() && member->value.IsString() &&
        std::string_view(member->value.GetString(), member->value.GetStringLength()) == expected;
}

inline bool IsUuid(std::string_view value) {
    if (value.size() != 36) return false;
    bool nonzero = false;
    for (std::size_t i = 0; i < value.size(); ++i) {
        if (i == 8 || i == 13 || i == 18 || i == 23) {
            if (value[i] != '-') return false;
            continue;
        }
        if (!std::isxdigit(static_cast<unsigned char>(value[i]))) return false;
        nonzero |= value[i] != '0' && value[i] != '-';
    }
    return nonzero;
}

inline bool Validate(const rapidjson::Value& report, std::string_view direction,
                     std::string_view period, unsigned duration, std::string_view* failedField = nullptr) {
    const auto fail = [failedField](std::string_view field) {
        if (failedField) *failedField = field;
        return false;
    };
    if (!report.IsObject()) return fail("report");
    const auto reportId = report.FindMember("reportId");
    if (reportId == report.MemberEnd() || !reportId->value.IsString() ||
        !IsUuid(std::string_view(reportId->value.GetString(), reportId->value.GetStringLength()))) return fail("reportId");
    if (!StringEquals(report, "reportType", "wasapi-stream-validation")) return fail("reportType");
    if (!StringEquals(report, "provenance", "REAL_WASAPI_STREAM_VALIDATION")) return fail("provenance");
    if (!report.HasMember("schemaVersion") || !report["schemaVersion"].IsInt() || report["schemaVersion"].GetInt() != 1) return fail("schemaVersion");
    if (!StringEquals(report, "direction", direction)) return fail("direction");
    if (!StringEquals(report, "period", period)) return fail("period");
    if (!report.HasMember("durationSeconds") || !report["durationSeconds"].IsUint() || report["durationSeconds"].GetUint() != duration) return fail("durationSeconds");
    if (!report.HasMember("format") || !report["format"].IsObject()) return fail("format");

    const auto& format = report["format"];
    if (!format.HasMember("sampleRateHz") || !format["sampleRateHz"].IsInt() || format["sampleRateHz"].GetInt() != 48'000) return fail("format.sampleRateHz");
    if (!StringEquals(format, "encoding", "PCM16")) return fail("format.encoding");
    if (!format.HasMember("channels") || !format["channels"].IsInt() || format["channels"].GetInt() != 2) return fail("format.channels");
    if (failedField) *failedField = {};
    return true;
}

} // namespace StreamReportContract
