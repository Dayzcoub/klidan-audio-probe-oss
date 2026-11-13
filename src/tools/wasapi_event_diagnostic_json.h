#pragma once

#include "wasapi_event_diagnostic_contract.h"

#include <rapidjson/stringbuffer.h>
#include <rapidjson/writer.h>

namespace WasapiEventDiagnostic {

inline std::string ToJson(const Report& r) {
    rapidjson::StringBuffer buffer;
    rapidjson::Writer<rapidjson::StringBuffer> w(buffer);
    w.StartObject();
    w.Key("schemaVersion"); w.Uint(kSchemaVersion);
    w.Key("reportType"); w.String(kReportType.data(), static_cast<rapidjson::SizeType>(kReportType.size()));
    w.Key("provenance"); w.String(kProvenance.data(), static_cast<rapidjson::SizeType>(kProvenance.size()));
    w.Key("buildCommit"); w.String(r.buildCommit.c_str());
    w.Key("operationId"); w.String(r.operationId.c_str());
    w.Key("timestampUtc"); w.String(r.timestampUtc.c_str());
    w.Key("endpointId"); w.String("{0.0.0.00000000}.{ba4ca4cc-35d7-437a-b53b-99ffedc7e5b8}");
    w.Key("direction"); w.String("RENDER");
    w.Key("shareMode"); w.String("EXCLUSIVE");
    w.Key("eventMode"); w.String("EVENTCALLBACK");
    w.Key("audioEventType"); w.String("AUTO_RESET_NONSIGNALED");
    w.Key("eventTimestampSource"); w.String("STEADY_CLOCK_IMMEDIATELY_AFTER_WAIT_RETURN");
    w.Key("rateHz"); w.Uint(kRate);
    w.Key("channels"); w.Uint(kChannels);
    w.Key("encoding"); w.String("PCM16");
    w.Key("mmcss"); w.String(r.mmcss.c_str());
    w.Key("mmcssRegistrationHresult"); w.String(r.mmcssRegistrationHresult.c_str());
    w.Key("mmcssRevertHresult"); w.String(r.mmcssRevertHresult.c_str());
    w.Key("mmcssRegistered"); w.Bool(r.mmcssRegistered);
    w.Key("mmcssReverted"); w.Bool(r.mmcssReverted);
    w.Key("renderPolicy"); w.String("SILENCE_ONLY");
    w.Key("durationSeconds"); w.Uint(kDurationSeconds);
    w.Key("requestedPeriod100ns"); w.Int64(kPeriod100ns);
    w.Key("requestedBufferDuration100ns"); w.Int64(r.requestedBufferDuration100ns);
    w.Key("requestedPeriodicity100ns"); w.Int64(r.requestedPeriodicity100ns);
    w.Key("requestedFrames"); w.Uint(kRequestedFrames);
    const auto optionalNumber = [&w](const char* name, const auto& value) {
        w.Key(name);
        if (value) w.Double(static_cast<double>(*value)); else w.Null();
    };
    const auto optionalUnsigned = [&w](const char* name, const auto& value) {
        w.Key(name);
        if (value) w.Uint64(static_cast<std::uint64_t>(*value)); else w.Null();
    };
    const auto optionalSigned = [&w](const char* name, const auto& value) {
        w.Key(name);
        if (value) w.Int64(*value); else w.Null();
    };
    optionalSigned("defaultPeriod100ns", r.defaultPeriod100ns);
    optionalSigned("minimumPeriod100ns", r.minimumPeriod100ns);
    optionalUnsigned("actualBufferFrames", r.actualBufferFrames);
    optionalSigned("actualBufferPeriod100ns", r.actualBufferPeriod100ns);
    w.Key("prefillFrames"); w.Uint(r.prefillFrames);
    optionalUnsigned("eventCount", r.eventCount);
    optionalUnsigned("bufferGetCount", r.bufferGetCount);
    optionalUnsigned("bufferReleaseCount", r.bufferReleaseCount);
    optionalUnsigned("framesProcessed", r.framesProcessed);
    w.Key("expectedFrames"); w.Uint64(static_cast<std::uint64_t>(kRate) * kDurationSeconds);
    optionalNumber("frameCoveragePercent", r.frameCoveragePercent);
    optionalNumber("elapsedMilliseconds", r.elapsedMilliseconds);
    optionalNumber("eventRateHz", r.eventRateHz);
    optionalNumber("meanFramesPerEvent", r.meanFramesPerEvent);
    optionalNumber("meanEventIntervalMilliseconds", r.meanEventIntervalMilliseconds);
    optionalNumber("firstEventDelayMilliseconds", r.firstEventDelayMilliseconds);
    optionalNumber("eventIntervalMedianMilliseconds", r.eventIntervalMedianMilliseconds);
    optionalNumber("eventIntervalP95Milliseconds", r.eventIntervalP95Milliseconds);
    optionalNumber("eventIntervalP99Milliseconds", r.eventIntervalP99Milliseconds);
    optionalNumber("eventIntervalMaximumMilliseconds", r.eventIntervalMaximumMilliseconds);
    optionalNumber("eventIntervalMaximumEndOffsetMilliseconds", r.eventIntervalMaximumEndOffsetMilliseconds);
    optionalUnsigned("waitTimeoutCount", r.waitTimeoutCount);
    w.Key("apiHresults"); w.StartObject();
    w.Key("comInitialize"); w.String(r.comInitializeHresult.c_str());
    w.Key("activate"); w.String(r.activateHresult.c_str());
    w.Key("getDevicePeriod"); w.String(r.getDevicePeriodHresult.c_str());
    w.Key("formatSupport"); w.String(r.formatSupportHresult.c_str());
    w.Key("initialize"); w.String(r.initializeHresult.c_str());
    w.Key("getBufferSize"); w.String(r.getBufferSizeHresult.c_str());
    w.Key("setEventHandle"); w.String(r.setEventHandleHresult.c_str());
    w.Key("getRenderClient"); w.String(r.renderServiceHresult.c_str());
    w.Key("prefillGetBuffer"); w.String(r.prefillGetBufferHresult.c_str());
    w.Key("prefillReleaseBuffer"); w.String(r.prefillReleaseBufferHresult.c_str());
    w.Key("start"); w.String(r.startHresult.c_str());
    w.Key("stop"); w.String(r.stopHresult.c_str());
    w.Key("eventClose"); w.String(r.eventCloseHresult.c_str());
    w.Key("cancelEventCreate"); w.String(r.cancelEventCreateHresult.c_str());
    w.Key("cancelEventClose"); w.String(r.cancelEventCloseHresult.c_str());
    w.EndObject();
    w.Key("diagnosticDisposition"); w.String(r.diagnosticDisposition.c_str());
    w.Key("audioQualityDisposition"); w.String("NOT_CLASSIFIED");
    w.Key("intervalPercentileMethod"); w.String("MEDIAN_MIDPOINT;P95_P99_NEAREST_RANK");
    w.Key("errorStage"); w.String(r.errorStage.c_str());
    w.Key("errorHresult"); w.String(r.errorHresult.c_str());
    w.Key("cleanupState"); w.String(r.cleanupState.c_str());
    w.Key("durationReached"); w.Bool(r.durationReached);
    w.Key("cancelled"); w.Bool(r.cancelled);
    w.Key("watchdogTimedOut"); w.Bool(r.watchdogTimedOut);
    w.Key("deviceInvalidated"); w.Bool(r.deviceInvalidated);
    w.Key("clientReleased"); w.Bool(r.clientReleased);
    w.Key("eventHandleClosed"); w.Bool(r.eventHandleClosed);
    w.Key("cancelEventClosed"); w.Bool(r.cancelEventClosed);
    w.Key("lastWaitStatus"); w.String(r.lastWaitStatus.c_str());
    w.EndObject();
    return {buffer.GetString(), buffer.GetSize()};
}

} // namespace WasapiEventDiagnostic
