#include "duplex_validation_report.h"

#include <rapidjson/document.h>

#include <string>
#include <vector>

int main() {
    DuplexValidationReport::Report report;
    report.reportId = "20000000-0000-4000-8000-000000000001";
    report.operationId = "30000000-0000-4000-8000-000000000001";
    report.timestampUtc = "2026-10-31T00:00:00.000Z";
    report.probeVersion = "0.1.0";
    report.captureEndpointId = "capture-local-id";
    report.renderEndpointId = "render-local-id";
    report.period = "MINIMUM";
    report.mmcss = "OFF";
    report.durationSeconds = 5;
    report.bothReady = true;
    report.startAuthorized = true;
    report.startDifferenceMilliseconds = 0.026;
    report.simultaneousRuntimeMilliseconds = 5001.246;
    report.status = "DUPLEX_CLEAN";
    const auto setSide = [](DuplexValidationReport::Side& side, const char* id, const char* direction) {
        side.endpointId = id;
        side.direction = direction;
        side.activationHresult = "0x00000000";
        side.devicePeriodHresult = "0x00000000";
        side.formatSupportHresult = "0x00000000";
        side.initializeHresult = "0x00000000";
        side.startHresult = "0x00000000";
        side.stopHresult = "0x00000000";
        side.bufferHresult = "0x00000000";
        side.eventHresult = "0x00000000";
        side.bufferFrames = 144;
        side.framesProcessed = 239'856;
        side.expectedFrames = 240'000;
        side.events = 1666;
        side.packets = 1666;
        side.elapsedMilliseconds = 5001.246;
        side.ready = true;
        side.startSucceeded = true;
        side.stopSucceeded = true;
        side.durationReached = true;
        side.quality = "CLEAN";
        side.cleanup = "RELEASE_ATTEMPTED";
        side.startOffsetMilliseconds = 0.026;
        side.peerStopped = false;
    };
    setSide(report.capture, report.captureEndpointId.c_str(), "CAPTURE");
    setSide(report.render, report.renderEndpointId.c_str(), "RENDER");
    report.capture.devicePositionAnomalies = 0;
    report.capture.unavailableCaptureBuffers = 0;
    report.render.silentBuffersServiced = 1666;
    report.capture.frameCoveragePercent = 99.94;
    report.render.frameCoveragePercent = 100.0;
    report.capture.eventIntervalsObserved = report.capture.events;
    report.render.eventIntervalsObserved = report.render.events;
    report.capture.intermediateWaitTimeouts = 1;
    report.render.intermediateWaitTimeouts = 1;

    const auto json = DuplexValidationReport::ToJson(report);
    rapidjson::Document parsed;
    parsed.Parse(json.data(), json.size());
    if (parsed.HasParseError() || !DuplexValidationReport::Validate(parsed, report.captureEndpointId,
            report.renderEndpointId, "MINIMUM", 5, "OFF", report.operationId)) return 1;
    if (parsed["schemaVersion"].GetInt() != 2 || parsed["capture"]["intermediateWaitTimeouts"].GetUint64() != 1 ||
        parsed["render"]["intermediateWaitTimeouts"].GetUint64() != 1) return 9;

    const auto cleanMustReject = [&](auto mutate) {
        auto candidate = DuplexValidationReport::ToJson(report);
        rapidjson::Document doc;
        doc.Parse(candidate.data(), candidate.size());
        mutate(doc);
        return !DuplexValidationReport::Validate(doc, report.captureEndpointId, report.renderEndpointId,
            "MINIMUM", 5, "OFF", report.operationId);
    };
    if (!cleanMustReject([](rapidjson::Document& d) { d["capture"]["serviceFaults"].SetUint64(1); }) ||
        !cleanMustReject([](rapidjson::Document& d) { d["render"]["watchdogTimedOut"].SetBool(true); }) ||
        !cleanMustReject([](rapidjson::Document& d) { d["capture"]["longEventIntervals"].SetUint64(1); }) ||
        !cleanMustReject([](rapidjson::Document& d) { d["render"]["packetDrainLimitHits"].SetUint64(1); }) ||
        !cleanMustReject([](rapidjson::Document& d) { d["capture"]["framesProcessed"].SetUint64(100'000); }) ||
        !cleanMustReject([](rapidjson::Document& d) { d["capture"]["cleanup"].SetString("UNKNOWN", d.GetAllocator()); })) return 10;

    for (const auto period : {"MINIMUM", "DEFAULT"}) {
        report.period = period;
        const auto periodJson = DuplexValidationReport::ToJson(report);
        rapidjson::Document periodReport;
        periodReport.Parse(periodJson.data(), periodJson.size());
        if (periodReport.HasParseError() ||
            !DuplexValidationReport::Validate(periodReport, report.captureEndpointId, report.renderEndpointId,
                period, 5, "OFF", report.operationId) ||
            !periodReport["capture"]["direction"].IsString() ||
            std::string(periodReport["capture"]["direction"].GetString()) != "CAPTURE" ||
            std::string(periodReport["render"]["direction"].GetString()) != "RENDER") return 8;
    }
    report.period = "MINIMUM";

    report.period = "CUSTOM:50000:240";
    const auto customJson = DuplexValidationReport::ToJson(report);
    rapidjson::Document customReport;
    customReport.Parse(customJson.data(), customJson.size());
    if (customReport.HasParseError() || !DuplexValidationReport::Validate(customReport, report.captureEndpointId,
            report.renderEndpointId, "CUSTOM:50000:240", 5, "OFF", report.operationId)) return 11;
    const auto customMustReject = [&](std::string_view requestedPeriod, auto mutate) {
        auto candidate = DuplexValidationReport::ToJson(report);
        rapidjson::Document doc;
        doc.Parse(candidate.data(), candidate.size());
        mutate(doc);
        return !DuplexValidationReport::Validate(doc, report.captureEndpointId, report.renderEndpointId,
            requestedPeriod, 5, "OFF", report.operationId);
    };
    if (!customMustReject("CUSTOM:50001:240", [](rapidjson::Document&) {}) ||
        !customMustReject("CUSTOM:50000:241", [](rapidjson::Document&) {}) ||
        !customMustReject("CUSTOM:50000", [](rapidjson::Document&) {}) ||
        !customMustReject("CUSTOM:50000:240:extra", [](rapidjson::Document&) {}) ||
        !customMustReject("CUSTOM:0:0", [](rapidjson::Document&) {}) ||
        !customMustReject("CUSTOM:50000:240", [](rapidjson::Document& d) { d["operationId"].SetString("30000000-0000-4000-8000-000000000099", d.GetAllocator()); }) ||
        !customMustReject("CUSTOM:50000:240", [](rapidjson::Document& d) { d["requested"]["captureEndpointId"].SetString("other-capture", d.GetAllocator()); })) return 12;
    report.period = "MINIMUM";

    report.status = "FAILED";
    report.capture.quality = "UNKNOWN";
    report.capture.cleanup = "UNKNOWN";
    report.capture.framesProcessed = 0;
    const auto failedJson = DuplexValidationReport::ToJson(report);
    rapidjson::Document failedReport;
    failedReport.Parse(failedJson.data(), failedJson.size());
    if (failedReport.HasParseError() || !DuplexValidationReport::Validate(failedReport, report.captureEndpointId,
            report.renderEndpointId, "MINIMUM", 5, "OFF", report.operationId)) return 13;
    report.status = "DUPLEX_CLEAN";
    report.capture.quality = "CLEAN";
    report.capture.cleanup = "RELEASE_ATTEMPTED";
    report.capture.framesProcessed = 239'856;
    report.startAuthorized = false;
    const auto unauthorizedClean = DuplexValidationReport::ToJson(report);
    rapidjson::Document unauthorizedReport;
    unauthorizedReport.Parse(unauthorizedClean.data(), unauthorizedClean.size());
    if (unauthorizedReport.HasParseError() || DuplexValidationReport::Validate(unauthorizedReport, report.captureEndpointId,
            report.renderEndpointId, "MINIMUM", 5, "OFF", report.operationId)) return 14;
    report.startAuthorized = true;

    if (DuplexValidationReport::Validate(parsed, "wrong-capture", report.renderEndpointId, "MINIMUM", 5, "OFF", report.operationId) ||
        DuplexValidationReport::Validate(parsed, report.captureEndpointId, report.renderEndpointId, "DEFAULT", 5, "OFF", report.operationId)) return 2;
    parsed["provenance"].SetString("FAKE_SYNTHETIC", parsed.GetAllocator());
    if (DuplexValidationReport::Validate(parsed, report.captureEndpointId, report.renderEndpointId, "MINIMUM", 5, "OFF", report.operationId)) return 3;

    parsed.Parse(json.data(), json.size());
    parsed["capture"]["framesProcessed"].SetUint64(0);
    if (DuplexValidationReport::Validate(parsed, report.captureEndpointId, report.renderEndpointId, "MINIMUM", 5, "OFF", report.operationId)) return 4;
    parsed.Parse(json.data(), json.size());
    parsed["render"]["stopSucceeded"].SetBool(false);
    if (DuplexValidationReport::Validate(parsed, report.captureEndpointId, report.renderEndpointId, "MINIMUM", 5, "OFF", report.operationId)) return 5;
    parsed.Parse(json.data(), json.size());
    constexpr char embeddedNullStatus[] = "DUPLEX_CLEAN\0EVIL";
    parsed["duplex"]["status"].SetString(embeddedNullStatus, sizeof(embeddedNullStatus) - 1, parsed.GetAllocator());
    if (DuplexValidationReport::Validate(parsed, report.captureEndpointId, report.renderEndpointId, "MINIMUM", 5, "OFF", report.operationId)) return 6;

    report.capture.cleanup = "UNKNOWN";
    report.status = "INCOMPLETE";
    report.capture.framesProcessed = 0;
    const auto incomplete = DuplexValidationReport::ToJson(report);
    parsed.Parse(incomplete.data(), incomplete.size());
    if (!DuplexValidationReport::Validate(parsed, report.captureEndpointId, report.renderEndpointId, "MINIMUM", 5, "OFF", report.operationId)) return 7;

    report.schemaVersion = 1;
    report.reportType = "wasapi-sustained-duplex-validation";
    report.provenance = "REAL_WASAPI_SUSTAINED_DUPLEX";
    report.durationSeconds = 60;
    report.capture.elapsedMilliseconds = report.render.elapsedMilliseconds = 60'000.0;
    const auto sustainedJson = DuplexValidationReport::ToJson(report);
    parsed.Parse(sustainedJson.data(), sustainedJson.size());
    if (parsed.HasParseError() || !DuplexValidationReport::Validate(parsed, report.captureEndpointId, report.renderEndpointId, "MINIMUM", 60, "OFF", report.operationId, "REAL_WASAPI_SUSTAINED_DUPLEX") || parsed["schemaVersion"].GetInt() != 1) return 15;
    return 0;
}
