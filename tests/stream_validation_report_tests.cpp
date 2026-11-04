#include "stream_validation_report.h"
#include "stream_report_contract.h"

#include <rapidjson/document.h>

#include <cstdlib>
#include <algorithm>
#include <cctype>
#include <iostream>
#include <string>

int main() {
    StreamValidationReport::Report report;
    report.direction = "CAPTURE";
    report.period = "MINIMUM";
    report.probeVersion = "0.1.0";
    report.formatSupported = true;
    report.initialized = true;
    report.startSucceeded = true;
    report.startHresult = "0x00000000";
    report.stopSucceeded.reset();
    report.streamQuality = "INCOMPLETE";
    report.durationReached = true;
    report.actualBufferFrames = 144;
    report.events = 1667;
    report.packets = 1667;
    report.framesProcessed = 240000;
    report.expectedFrames = 240000;
    report.frameCoveragePercent = 100.0;
    report.cleanup = "STOP_SUCCEEDED;CLIENT_RELEASE_ATTEMPTED";
    const auto json = StreamValidationReport::ToJson(report, "2026-10-31T00:00:00.000Z", "20000000-0000-4000-8000-000000000001");
    for (const std::string_view required : {"\"schemaVersion\":1", "\"provenance\":\"REAL_WASAPI_STREAM_VALIDATION\"", "\"direction\":\"CAPTURE\"", "\"period\":\"MINIMUM\"", "\"actualBufferFrames\":144", "\"startSucceeded\":true", "\"framesProcessed\":240000", "\"streamQuality\":\"INCOMPLETE\"", "\"cleanup\":\"STOP_SUCCEEDED;CLIENT_RELEASE_ATTEMPTED\"", "\"physicalPlayback\":\"NOT_MEASURED\"", "\"physicalRouting\":\"UNKNOWN\""})
        if (json.find(required) == std::string::npos) return EXIT_FAILURE;
    if (json.find("\"stopSucceeded\":null") == std::string::npos || json.find("\"stopHresult\":null") == std::string::npos || json.find("\"queryHresult\":null") == std::string::npos || json.find("NaN") != std::string::npos || json.find("Infinity") != std::string::npos) return EXIT_FAILURE;
    report.frameCoveragePercent.reset();
    const auto incomplete = StreamValidationReport::ToJson(report, "2026-10-31T00:00:00.000Z", "20000000-0000-4000-8000-000000000002");
    if (incomplete.find("\"frameCoveragePercent\":null") == std::string::npos) return EXIT_FAILURE;

    const auto checkContract = [](std::string json, std::string_view direction, std::string_view period, bool expected) {
        rapidjson::Document parsed;
        parsed.Parse(json.data(), json.size());
        return !parsed.HasParseError() && StreamReportContract::Validate(parsed, direction, period, 5) == expected;
    };
    const auto makeJson = [](std::string direction, std::string period,
                             std::string reportId = "20000000-0000-4000-8000-000000000003") {
        StreamValidationReport::Report candidate;
        candidate.direction = std::move(direction);
        candidate.period = std::move(period);
        candidate.durationSeconds = 5;
        candidate.formatSupported = true;
        candidate.initialized = true;
        candidate.startSucceeded = true;
        candidate.streamQuality = "INCOMPLETE";
        candidate.framesProcessed = 0;
        candidate.expectedFrames = 240'000;
        candidate.durationReached = false;
        candidate.stopSucceeded = true;
        return StreamValidationReport::ToJson(candidate, "2026-10-31T00:00:00.000Z", reportId);
    };
    for (const auto direction : {"CAPTURE", "RENDER"}) for (const auto period : {"MINIMUM", "DEFAULT"})
        if (!checkContract(makeJson(direction, period), direction, period, true)) {
            std::cerr << "real serializer report rejected for " << direction << '/' << period << '\n';
            return EXIT_FAILURE;
        }

    if (!checkContract(makeJson("CAPTURE", "DEFAULT"), "CAPTURE", "MINIMUM", false) ||
        !checkContract(makeJson("RENDER", "MINIMUM"), "CAPTURE", "MINIMUM", false)) return EXIT_FAILURE;
    const std::string lowerUuid = "20000000-0000-4000-8000-000000000003";
    std::string upperUuid = lowerUuid;
    std::transform(upperUuid.begin(), upperUuid.end(), upperUuid.begin(), [](unsigned char c) { return static_cast<char>(std::toupper(c)); });
    if (!checkContract(makeJson("CAPTURE", "MINIMUM", lowerUuid), "CAPTURE", "MINIMUM", true) ||
        !checkContract(makeJson("CAPTURE", "MINIMUM", upperUuid), "CAPTURE", "MINIMUM", true) ||
        !checkContract(makeJson("CAPTURE", "MINIMUM", "00000000-0000-0000-0000-000000000000"), "CAPTURE", "MINIMUM", false) ||
        !checkContract(makeJson("CAPTURE", "MINIMUM", "not-a-uuid"), "CAPTURE", "MINIMUM", false)) return EXIT_FAILURE;
    auto missingReportId = makeJson("CAPTURE", "MINIMUM");
    const auto reportIdMember = missingReportId.find("\"reportId\":\"20000000-0000-4000-8000-000000000003\",");
    if (reportIdMember == std::string::npos) return EXIT_FAILURE;
    missingReportId.erase(reportIdMember, std::string("\"reportId\":\"20000000-0000-4000-8000-000000000003\",").size());
    rapidjson::Document missingIdParsed;
    missingIdParsed.Parse(missingReportId.data(), missingReportId.size());
    std::string_view failedField;
    if (missingIdParsed.HasParseError() || StreamReportContract::Validate(missingIdParsed, "CAPTURE", "MINIMUM", 5, &failedField) || failedField != "reportId") return EXIT_FAILURE;
    auto invalidReportId = makeJson("CAPTURE", "MINIMUM", "not-a-uuid");
    rapidjson::Document invalidIdParsed;
    invalidIdParsed.Parse(invalidReportId.data(), invalidReportId.size());
    if (invalidIdParsed.HasParseError() || StreamReportContract::Validate(invalidIdParsed, "CAPTURE", "MINIMUM", 5, &failedField) || failedField != "reportId") return EXIT_FAILURE;
    auto invalidFormat = makeJson("CAPTURE", "MINIMUM");
    const auto rate = invalidFormat.find("\"sampleRateHz\":48000");
    if (rate == std::string::npos) return EXIT_FAILURE;
    invalidFormat.replace(rate, std::string("\"sampleRateHz\":48000").size(), "\"sampleRateHz\":44100");
    if (!checkContract(invalidFormat, "CAPTURE", "MINIMUM", false)) return EXIT_FAILURE;
    auto invalidSchema = makeJson("CAPTURE", "MINIMUM");
    invalidSchema.replace(invalidSchema.find("\"schemaVersion\":1"), std::string("\"schemaVersion\":1").size(), "\"schemaVersion\":2");
    if (!checkContract(invalidSchema, "CAPTURE", "MINIMUM", false)) return EXIT_FAILURE;
    auto invalidProvenance = makeJson("CAPTURE", "MINIMUM");
    invalidProvenance.replace(invalidProvenance.find("REAL_WASAPI_STREAM_VALIDATION"), std::string("REAL_WASAPI_STREAM_VALIDATION").size(), "TEST_FAKE_STREAM_BACKEND");
    if (!checkContract(invalidProvenance, "CAPTURE", "MINIMUM", false)) return EXIT_FAILURE;
    auto embeddedNullEncoding = makeJson("CAPTURE", "MINIMUM");
    const auto encoding = embeddedNullEncoding.find("\"encoding\":\"PCM16\"");
    if (encoding == std::string::npos) return EXIT_FAILURE;
    embeddedNullEncoding.replace(encoding, std::string("\"encoding\":\"PCM16\"").size(), "\"encoding\":\"PCM16\\u0000suffix\"");
    if (!checkContract(embeddedNullEncoding, "CAPTURE", "MINIMUM", false)) return EXIT_FAILURE;
    rapidjson::Document incompleteParsed;
    const auto incompleteFixture = makeJson("CAPTURE", "MINIMUM");
    incompleteParsed.Parse(incompleteFixture.data(), incompleteFixture.size());
    if (incompleteParsed.HasParseError() || !StreamReportContract::Validate(incompleteParsed, "CAPTURE", "MINIMUM", 5) ||
        !incompleteParsed["framesProcessed"].IsInt64() || incompleteParsed["framesProcessed"].GetInt64() != 0 ||
        std::string_view(incompleteParsed["streamQuality"].GetString(), incompleteParsed["streamQuality"].GetStringLength()) != "INCOMPLETE") return EXIT_FAILURE;
    std::cout << "Stream report contract accepted native serializer JSON for Capture/Render x Minimum/Default and UUID case variants; malformed/missing reportId and mismatched metadata were rejected with field diagnostics; incomplete evidence remained INCOMPLETE.\n";
    return EXIT_SUCCESS;
}
