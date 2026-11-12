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
        side.requestedPeriod100ns = 50'000; side.deviceDefaultPeriod100ns = 100'000; side.deviceMinimumPeriod100ns = 30'000; side.actualBufferPeriod100ns = 50'000;
        side.firstEventDelayMilliseconds = 1.25; side.framesPerEventMinimum = 240; side.framesPerEventMaximum = 240; side.framesPerEventMean = 240.0;
        side.eventWaitCount = 501; side.eventSignalCount = 500; side.eventTimeoutCount = 1; side.lastWaitResult = 0;
        side.lastWaitReason = "AUDIO_EVENT";
        side.cadenceAssessment = "OBSERVED_CADENCE_DIFFERS_FROM_BUFFER_PERIOD";
        side.captureReleaseBufferHresult = "0x00000000"; side.renderReleaseBufferHresult = "0x00000000";
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
    report.capture.eventIntervalMaximumMilliseconds = 22.5;
    report.capture.eventIntervalMaximumEndOffsetMilliseconds = 231.4;
    report.capture.longEventIntervalThresholdMilliseconds = 15.0;
    report.capture.longEventIntervals = 0;

    const auto json = DuplexValidationReport::ToJson(report);
    rapidjson::Document parsed;
    parsed.Parse(json.data(), json.size());
    if (parsed.HasParseError() || !DuplexValidationReport::Validate(parsed, report.captureEndpointId,
            report.renderEndpointId, "MINIMUM", 5, "OFF", report.operationId)) return 1;
    if (parsed["schemaVersion"].GetInt() != 2 || parsed["capture"]["intermediateWaitTimeouts"].GetUint64() != 1 ||
        parsed["render"]["intermediateWaitTimeouts"].GetUint64() != 1 ||
        parsed["capture"]["requestedPeriod100ns"].GetUint64() != 50'000 || parsed["capture"]["deviceMinimumPeriod100ns"].GetUint64() != 30'000 ||
        parsed["capture"]["actualBufferPeriod100ns"].GetUint64() != 50'000 || parsed["capture"]["firstEventDelayMilliseconds"].GetDouble() != 1.25 ||
        parsed["capture"]["eventIntervalMaximumMilliseconds"].GetDouble() != 22.5 ||
        parsed["capture"]["eventIntervalMaximumEndOffsetMilliseconds"].GetDouble() != 231.4 ||
        parsed["capture"]["longEventIntervalThresholdMilliseconds"].GetDouble() != 15.0 ||
        parsed["capture"]["framesPerEventMean"].GetDouble() != 240.0 || parsed["render"]["eventSignalCount"].GetUint64() != 500 ||
        parsed["render"]["eventTimeoutCount"].GetUint64() != 1 || std::string(parsed["render"]["lastWaitReason"].GetString()) != "AUDIO_EVENT" ||
        std::string(parsed["render"]["cadenceAssessment"].GetString()) != "OBSERVED_CADENCE_DIFFERS_FROM_BUFFER_PERIOD" ||
        std::string(parsed["render"]["renderReleaseBufferHresult"].GetString()) != "0x00000000") return 9;

    auto diagnosticReport = report;
    diagnosticReport.capture.discontinuities = 1;
    diagnosticReport.capture.startupDiscontinuities = 0;
    diagnosticReport.capture.steadyDiscontinuities = 1;
    diagnosticReport.capture.discontinuitiesWithoutPositionAnomaly = 1;
    diagnosticReport.capture.discontinuityPackets.push_back({500, 499, 2'345.67, 0x1u, 240'000, 123'456,
        0, "CONTIGUOUS", "STEADY"});
    const auto diagnosticJson = DuplexValidationReport::ToJson(diagnosticReport);
    rapidjson::Document diagnostic;
    diagnostic.Parse(diagnosticJson.data(), diagnosticJson.size());
    const auto& capture = diagnostic["capture"];
    if (diagnostic.HasParseError() || capture["discontinuities"].GetUint64() != 1 ||
        capture["startupDiscontinuities"].GetUint64() != 0 || capture["steadyDiscontinuities"].GetUint64() != 1 ||
        capture["discontinuitiesWithoutPositionAnomaly"].GetUint64() != 1 ||
        capture["discontinuityPackets"].Size() != 1 ||
        capture["discontinuityPackets"][0]["packetOrdinal"].GetUint64() != 500 ||
        capture["discontinuityPackets"][0]["eventOrdinal"].GetUint64() != 499 ||
        capture["discontinuityPackets"][0]["millisecondsSinceStart"].GetDouble() != 2'345.67 ||
        capture["discontinuityPackets"][0]["flags"].GetUint() != 1 ||
        capture["discontinuityPackets"][0]["devicePositionDeltaFrames"].GetUint64() != 0 ||
        std::string(capture["discontinuityPackets"][0]["phase"].GetString()) != "STEADY") return 20;

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

    report.capture.quality = "ANOMALIES_PRESENT";
    report.capture.longEventIntervals = 1;
    report.status = "DUPLEX_WARNINGS";
    const auto warningJson = DuplexValidationReport::ToJson(report);
    rapidjson::Document warningReport;
    warningReport.Parse(warningJson.data(), warningJson.size());
    if (warningReport.HasParseError() || !DuplexValidationReport::Validate(warningReport, report.captureEndpointId,
            report.renderEndpointId, "MINIMUM", 5, "OFF", report.operationId) ||
        warningReport["capture"]["longEventIntervals"].GetUint64() != 1 ||
        warningReport["capture"]["eventIntervalMaximumMilliseconds"].GetDouble() != 22.5) return 16;
    report.capture.quality = "CLEAN";
    report.status = "DUPLEX_CLEAN";
    report.capture.longEventIntervals = 0;
    report.capture.eventIntervalMaximumMilliseconds.reset();
    report.capture.eventIntervalMaximumEndOffsetMilliseconds.reset();
    report.capture.longEventIntervalThresholdMilliseconds.reset();

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
    if (failedReport.HasParseError() || failedReport["capture"]["requestedPeriod100ns"].GetUint64() != 50'000 ||
            failedReport["capture"]["eventWaitCount"].GetUint64() != 501 || !DuplexValidationReport::Validate(failedReport, report.captureEndpointId,
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
