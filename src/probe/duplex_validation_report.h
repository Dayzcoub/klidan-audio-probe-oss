#pragma once

#include <rapidjson/document.h>
#include <rapidjson/stringbuffer.h>
#include <rapidjson/writer.h>

#include <cstdint>
#include <cmath>
#include <charconv>
#include <optional>
#include <string>
#include <string_view>
#include "channel_routing.h"

namespace DuplexValidationReport {

struct Side {
    std::string endpointId;
    std::string direction;
    std::string activationHresult;
    std::string devicePeriodHresult;
    std::string formatSupportHresult;
    std::string initializeHresult;
    std::string startHresult;
    std::string stopHresult;
    std::string bufferHresult;
    std::string eventHresult;
    std::string errorStage = "UNKNOWN";
    std::string errorHresult;
    std::string quality = "UNKNOWN";
    std::string cleanup = "UNKNOWN";
    std::optional<ChannelRouting::EndpointFormat> routeFormat;
    std::uint64_t bufferFrames = 0;
    std::uint64_t framesProcessed = 0;
    std::uint64_t selectedCaptureSamples = 0;
    std::uint64_t invalidSelectedCaptureSamples = 0;
    std::uint64_t expectedFrames = 0;
    std::uint64_t events = 0;
    std::uint64_t packets = 0;
    std::uint64_t discontinuities = 0;
    std::uint64_t timestampErrors = 0;
    std::uint64_t serviceFaults = 0;
    std::uint64_t packetDrainLimitHits = 0;
    std::uint64_t intermediateWaitTimeouts = 0;
    std::uint64_t longEventIntervals = 0;
    std::optional<std::uint64_t> devicePositionAnomalies;
    std::optional<std::uint64_t> unavailableCaptureBuffers;
    std::optional<std::uint64_t> silentBuffersServiced;
    std::uint64_t eventIntervalsObserved = 0;
    std::optional<double> frameCoveragePercent;
    std::optional<double> eventIntervalMedianMilliseconds;
    std::optional<double> eventIntervalP95Milliseconds;
    std::optional<double> eventIntervalP99Milliseconds;
    double elapsedMilliseconds = 0;
    std::optional<double> startOffsetMilliseconds;
    bool ready = false;
    bool startSucceeded = false;
    bool stopSucceeded = false;
    bool peerStopped = false;
    bool watchdogTimedOut = false;
    bool deviceInvalidated = false;
    bool durationReached = false;
    bool cancelled = false;
};

struct Report {
    int schemaVersion = 2;
    std::string reportType = "wasapi-duplex-validation";
    std::string reportId;
    std::string operationId;
    std::string timestampUtc;
    std::string probeVersion;
    std::string provenance = "REAL_WASAPI_DUPLEX";
    std::string captureEndpointId;
    std::string renderEndpointId;
    std::string period;
    std::string mmcss;
    unsigned durationSeconds = 0;
    bool bothReady = false;
    bool startAuthorized = false;
    bool cancelled = false;
    bool peerStopRequested = false;
    std::string status = "INCOMPLETE";
    std::string routeFingerprint;
    std::optional<double> startDifferenceMilliseconds;
    std::optional<double> simultaneousRuntimeMilliseconds;
    Side capture;
    Side render;
    std::optional<ChannelRouting::Configuration> route;
};

inline std::string ToJson(const Report& report) {
    rapidjson::StringBuffer out;
    rapidjson::Writer<rapidjson::StringBuffer> w(out);
    w.StartObject();
    w.Key("schemaVersion"); w.Int(report.schemaVersion);
    w.Key("reportType"); w.String(report.reportType.c_str());
    w.Key("reportId"); w.String(report.reportId.c_str());
    w.Key("operationId"); w.String(report.operationId.c_str());
    w.Key("timestampUtc"); w.String(report.timestampUtc.c_str());
    w.Key("probeVersion"); w.String(report.probeVersion.c_str());
    w.Key("provenance"); w.String(report.provenance.c_str());
    w.Key("requested"); w.StartObject();
      w.Key("captureEndpointId"); w.String(report.captureEndpointId.c_str());
      w.Key("renderEndpointId"); w.String(report.renderEndpointId.c_str());
      w.Key("sampleRateHz"); w.Int(report.route ? report.route->capture.format.sampleRate : 48000);
      w.Key("encoding"); w.String(report.route ? ExclusiveFormatMatrix::EncodingText(report.route->capture.format.encoding) : "PCM16");
      w.Key("channels"); w.Int(report.route ? report.route->capture.format.channels : 2);
      w.Key("period"); w.String(report.period.c_str());
      w.Key("durationSeconds"); w.Uint(report.durationSeconds);
      w.Key("mmcss"); w.String(report.mmcss.c_str());
    w.EndObject();
    if (report.route) {
      const auto& route = *report.route;
      w.Key("route"); w.StartObject();
      w.Key("contractVersion"); w.Uint(ChannelRouting::kContractVersion);
      w.Key("fingerprint"); w.String(report.routeFingerprint.c_str());
      w.Key("mode"); w.String(ChannelRouting::ModeText(route.mode).data());
      w.Key("logicalChannelCount"); w.Uint(ChannelRouting::RenderCount(route.mode));
      w.Key("capture"); w.StartObject();
        w.Key("endpointId"); w.String(route.captureEndpointId.c_str());
        w.Key("sampleRateHz"); w.Uint(route.capture.format.sampleRate);
        w.Key("encoding"); w.String(ExclusiveFormatMatrix::EncodingText(route.capture.format.encoding));
        w.Key("channels"); w.Uint(route.capture.format.channels);
        w.Key("channelMask"); w.Uint(route.capture.channelMask);
        w.Key("selectedIndicesZeroBased"); w.StartArray(); for (std::uint16_t i=0;i<ChannelRouting::CaptureCount(route.mode);++i) w.Uint(route.captureChannels[i]); w.EndArray();
      w.EndObject();
      w.Key("render"); w.StartObject();
        w.Key("endpointId"); w.String(route.renderEndpointId.c_str());
        w.Key("sampleRateHz"); w.Uint(route.render.format.sampleRate);
        w.Key("encoding"); w.String(ExclusiveFormatMatrix::EncodingText(route.render.format.encoding));
        w.Key("channels"); w.Uint(route.render.format.channels);
        w.Key("channelMask"); w.Uint(route.render.channelMask);
        w.Key("selectedIndicesZeroBased"); w.StartArray(); for (std::uint16_t i=0;i<ChannelRouting::RenderCount(route.mode);++i) w.Uint(route.renderChannels[i]); w.EndArray();
      w.EndObject();
      w.Key("physicalMapping"); w.String("UNKNOWN");
      w.Key("renderPolicy"); w.String("SILENCE_ONLY");
      w.EndObject();
    }
    const auto side = [&w](const char* key, const Side& s) {
        w.Key(key); w.StartObject();
        w.Key("endpointId"); w.String(s.endpointId.c_str());
        w.Key("direction"); w.String(s.direction.c_str());
        if(s.routeFormat){w.Key("format");w.StartObject();w.Key("sampleRateHz");w.Uint(s.routeFormat->format.sampleRate);w.Key("channels");w.Uint(s.routeFormat->format.channels);w.Key("encoding");w.String(ExclusiveFormatMatrix::EncodingText(s.routeFormat->format.encoding));w.Key("channelMask");w.Uint(s.routeFormat->channelMask);w.EndObject();}
        w.Key("activationHresult"); if (s.activationHresult.empty()) w.Null(); else w.String(s.activationHresult.c_str());
        w.Key("devicePeriodHresult"); if (s.devicePeriodHresult.empty()) w.Null(); else w.String(s.devicePeriodHresult.c_str());
        w.Key("isFormatSupportedHresult"); if (s.formatSupportHresult.empty()) w.Null(); else w.String(s.formatSupportHresult.c_str());
        w.Key("initializeHresult"); if (s.initializeHresult.empty()) w.Null(); else w.String(s.initializeHresult.c_str());
        w.Key("startHresult"); if (s.startHresult.empty()) w.Null(); else w.String(s.startHresult.c_str());
        w.Key("stopHresult"); if (s.stopHresult.empty()) w.Null(); else w.String(s.stopHresult.c_str());
        w.Key("getBufferSizeHresult"); if (s.bufferHresult.empty()) w.Null(); else w.String(s.bufferHresult.c_str());
        w.Key("setEventHandleHresult"); if (s.eventHresult.empty()) w.Null(); else w.String(s.eventHresult.c_str());
        w.Key("actualBufferFrames"); if (s.bufferFrames == 0) w.Null(); else w.Uint64(s.bufferFrames);
        w.Key("framesProcessed"); w.Uint64(s.framesProcessed);
        w.Key("selectedCaptureSamples"); w.Uint64(s.selectedCaptureSamples);
        w.Key("invalidSelectedCaptureSamples"); w.Uint64(s.invalidSelectedCaptureSamples);
        w.Key("expectedFrames"); w.Uint64(s.expectedFrames);
        w.Key("events"); w.Uint64(s.events);
        w.Key("packets"); w.Uint64(s.packets);
        w.Key("discontinuities"); w.Uint64(s.discontinuities);
        w.Key("timestampErrors"); w.Uint64(s.timestampErrors);
        w.Key("serviceFaults"); w.Uint64(s.serviceFaults);
        w.Key("packetDrainLimitHits"); w.Uint64(s.packetDrainLimitHits);
        w.Key("intermediateWaitTimeouts"); w.Uint64(s.intermediateWaitTimeouts);
        w.Key("longEventIntervals"); w.Uint64(s.longEventIntervals);
        w.Key("devicePositionAnomalies"); if (s.devicePositionAnomalies) w.Uint64(*s.devicePositionAnomalies); else w.Null();
        w.Key("unavailableCaptureBuffers"); if (s.unavailableCaptureBuffers) w.Uint64(*s.unavailableCaptureBuffers); else w.Null();
        w.Key("silentBuffersServiced"); if (s.silentBuffersServiced) w.Uint64(*s.silentBuffersServiced); else w.Null();
        w.Key("eventIntervalsObserved"); w.Uint64(s.eventIntervalsObserved);
        w.Key("frameCoveragePercent"); if (s.frameCoveragePercent) w.Double(*s.frameCoveragePercent); else w.Null();
        w.Key("eventIntervalMedianMilliseconds"); if (s.eventIntervalMedianMilliseconds) w.Double(*s.eventIntervalMedianMilliseconds); else w.Null();
        w.Key("eventIntervalP95Milliseconds"); if (s.eventIntervalP95Milliseconds) w.Double(*s.eventIntervalP95Milliseconds); else w.Null();
        w.Key("eventIntervalP99Milliseconds"); if (s.eventIntervalP99Milliseconds) w.Double(*s.eventIntervalP99Milliseconds); else w.Null();
        w.Key("elapsedMilliseconds"); w.Double(s.elapsedMilliseconds);
        w.Key("startOffsetMilliseconds"); if (s.startOffsetMilliseconds) w.Double(*s.startOffsetMilliseconds); else w.Null();
        w.Key("ready"); w.Bool(s.ready);
        w.Key("startSucceeded"); w.Bool(s.startSucceeded);
        w.Key("stopSucceeded"); w.Bool(s.stopSucceeded);
        w.Key("peerStopped"); w.Bool(s.peerStopped);
        w.Key("watchdogTimedOut"); w.Bool(s.watchdogTimedOut);
        w.Key("deviceInvalidated"); w.Bool(s.deviceInvalidated);
        w.Key("durationReached"); w.Bool(s.durationReached);
        w.Key("cancelled"); w.Bool(s.cancelled);
        w.Key("quality"); w.String(s.quality.c_str());
        w.Key("cleanup"); w.String(s.cleanup.c_str());
        w.Key("errorStage"); w.String(s.errorStage.c_str());
        w.Key("errorHresult"); if (s.errorHresult.empty()) w.Null(); else w.String(s.errorHresult.c_str());
        w.EndObject();
    };
    side("capture", report.capture);
    side("render", report.render);
    w.Key("duplex"); w.StartObject();
      w.Key("bothReadyBeforeStart"); w.Bool(report.bothReady);
      w.Key("coordinatedStartAuthorized"); w.Bool(report.startAuthorized);
      w.Key("cancelled"); w.Bool(report.cancelled);
      w.Key("peerStopRequested"); w.Bool(report.peerStopRequested);
      w.Key("status"); w.String(report.status.c_str());
      w.Key("startDifferenceMilliseconds"); if (report.startDifferenceMilliseconds) w.Double(*report.startDifferenceMilliseconds); else w.Null();
      w.Key("simultaneousRuntimeMilliseconds"); if (report.simultaneousRuntimeMilliseconds) w.Double(*report.simultaneousRuntimeMilliseconds); else w.Null();
    w.EndObject();
    w.Key("physicalRtl"); w.String("NOT_MEASURED");
    w.Key("audioHostEligibility"); w.String("NOT_CERTIFIED");
    w.EndObject();
    return {out.GetString(), out.GetSize()};
}

inline bool IsValidPeriod(std::string_view period) {
    if (period == "MINIMUM" || period == "DEFAULT") return true;
    if (!period.starts_with("CUSTOM:")) return false;
    const auto separator = period.find(':', 7);
    if (separator == std::string_view::npos || period.find(':', separator + 1) != std::string_view::npos || separator == 7 || separator + 1 >= period.size()) return false;
    std::uint64_t period100ns = 0;
    const auto periodResult = std::from_chars(period.data() + 7, period.data() + separator, period100ns);
    std::uint32_t frames = 0;
    const auto frameResult = std::from_chars(period.data() + separator + 1, period.data() + period.size(), frames);
    if (periodResult.ec != std::errc{} || periodResult.ptr != period.data() + separator || frameResult.ec != std::errc{} || frameResult.ptr != period.data() + period.size() || period100ns == 0 || frames == 0 || frames > 48'000) return false;
    const auto expectedPeriod100ns = (static_cast<std::uint64_t>(frames) * 10'000'000U + 24'000U) / 48'000U;
    return period100ns == expectedPeriod100ns;
}

inline bool Validate(const rapidjson::Value& root, std::string_view captureId,
                     std::string_view renderId, std::string_view period,
                     unsigned duration, std::string_view mmcss, std::string_view operationId = {},
                     std::string_view provenance = "REAL_WASAPI_DUPLEX") {
    const auto equals = [](const rapidjson::Value& object, const char* name, std::string_view expected) {
        const auto it = object.FindMember(name);
        return it != object.MemberEnd() && it->value.IsString() &&
            std::string_view(it->value.GetString(), it->value.GetStringLength()) == expected;
    };
        const bool sustained = provenance == "REAL_WASAPI_SUSTAINED_DUPLEX" || provenance == "TEST_FAKE_WASAPI_SUSTAINED_DUPLEX";
    if (!root.IsObject() || !root.HasMember("schemaVersion") || !root["schemaVersion"].IsInt() || root["schemaVersion"].GetInt() != (sustained ? 1 : 2) ||
        !equals(root, "reportType", sustained ? "wasapi-sustained-duplex-validation" : "wasapi-duplex-validation") || !equals(root, "provenance", provenance) ||
        !root.HasMember("reportId") || !root["reportId"].IsString() || root["reportId"].GetStringLength() != 36 ||
        !root.HasMember("operationId") || !root["operationId"].IsString() ||
        !equals(root, "operationId", operationId) ||
        !root.HasMember("timestampUtc") || !root["timestampUtc"].IsString() || root["timestampUtc"].GetStringLength() == 0 ||
        !root.HasMember("probeVersion") || !root["probeVersion"].IsString() || root["probeVersion"].GetStringLength() == 0 ||
        !root.HasMember("requested") || !root["requested"].IsObject() || !root.HasMember("capture") || !root["capture"].IsObject() ||
        !root.HasMember("render") || !root["render"].IsObject() || !root.HasMember("duplex") || !root["duplex"].IsObject()) return false;
    const auto& requested = root["requested"];
    if (!equals(requested, "captureEndpointId", captureId) || !equals(requested, "renderEndpointId", renderId) ||
        !equals(requested, "period", period) || !equals(requested, "mmcss", mmcss) ||
        !requested.HasMember("sampleRateHz") || !requested["sampleRateHz"].IsInt() || requested["sampleRateHz"].GetInt() != 48000 ||
        !requested.HasMember("channels") || !requested["channels"].IsInt() || requested["channels"].GetInt() != 2 ||
        !requested.HasMember("encoding") || !equals(requested, "encoding", "PCM16") ||
        !requested.HasMember("durationSeconds") || !requested["durationSeconds"].IsUint() || requested["durationSeconds"].GetUint() != duration) return false;
    if (!IsValidPeriod(period) || (mmcss != "OFF" && mmcss != "PRO_AUDIO") || duration < 1 || (sustained ? (duration != 60U && duration != 300U && duration != 600U) : duration > 10U)) return false;
    const auto sideValid = [&](const rapidjson::Value& side, std::string_view id, std::string_view direction) {
        if (!equals(side, "endpointId", id) || !equals(side, "direction", direction) ||
            !side.HasMember("isFormatSupportedHresult") || !(side["isFormatSupportedHresult"].IsNull() || side["isFormatSupportedHresult"].IsString()) ||
            !side.HasMember("framesProcessed") || !side["framesProcessed"].IsUint64() ||
            !side.HasMember("expectedFrames") || !side["expectedFrames"].IsUint64() ||
            !side.HasMember("discontinuities") || !side["discontinuities"].IsUint64() ||
            !side.HasMember("timestampErrors") || !side["timestampErrors"].IsUint64() ||
            !side.HasMember("events") || !side["events"].IsUint64() ||
            !side.HasMember("packets") || !side["packets"].IsUint64() ||
            !side.HasMember("serviceFaults") || !side["serviceFaults"].IsUint64() ||
            !side.HasMember("packetDrainLimitHits") || !side["packetDrainLimitHits"].IsUint64() ||
            !side.HasMember("intermediateWaitTimeouts") || !side["intermediateWaitTimeouts"].IsUint64() ||
            !side.HasMember("longEventIntervals") || !side["longEventIntervals"].IsUint64() ||
            !side.HasMember("devicePositionAnomalies") || !(side["devicePositionAnomalies"].IsNull() || side["devicePositionAnomalies"].IsUint64()) ||
            !side.HasMember("unavailableCaptureBuffers") || !(side["unavailableCaptureBuffers"].IsNull() || side["unavailableCaptureBuffers"].IsUint64()) ||
            !side.HasMember("silentBuffersServiced") || !(side["silentBuffersServiced"].IsNull() || side["silentBuffersServiced"].IsUint64()) ||
            !side.HasMember("eventIntervalsObserved") || !side["eventIntervalsObserved"].IsUint64() ||
            !side.HasMember("elapsedMilliseconds") || !side["elapsedMilliseconds"].IsNumber() ||
            !std::isfinite(side["elapsedMilliseconds"].GetDouble()) || side["elapsedMilliseconds"].GetDouble() < 0 ||
            !side.HasMember("quality") || !side["quality"].IsString() ||
            !side.HasMember("cleanup") || !side["cleanup"].IsString() ||
            !side.HasMember("errorStage") || !side["errorStage"].IsString() ||
            !side.HasMember("errorHresult") || !(side["errorHresult"].IsNull() || side["errorHresult"].IsString())) return false;
        for (const auto name : {"activationHresult", "devicePeriodHresult", "initializeHresult", "startHresult", "stopHresult", "getBufferSizeHresult", "setEventHandleHresult"})
            if (!side.HasMember(name) || !(side[name].IsNull() || side[name].IsString())) return false;
        if (!side.HasMember("actualBufferFrames") || !(side["actualBufferFrames"].IsNull() || side["actualBufferFrames"].IsUint64())) return false;
        for (const auto name : {"ready", "startSucceeded", "stopSucceeded", "peerStopped"})
            if (!side.HasMember(name) || !side[name].IsBool()) return false;
        for (const auto name : {"watchdogTimedOut", "deviceInvalidated", "durationReached", "cancelled"})
            if (!side.HasMember(name) || !side[name].IsBool()) return false;
        for (const auto name : {"frameCoveragePercent", "eventIntervalMedianMilliseconds", "eventIntervalP95Milliseconds", "eventIntervalP99Milliseconds"})
            if (!side.HasMember(name) || !(side[name].IsNull() || (side[name].IsNumber() && std::isfinite(side[name].GetDouble()) && side[name].GetDouble() >= 0))) return false;
        if (!side.HasMember("startOffsetMilliseconds") || !(side["startOffsetMilliseconds"].IsNull() ||
            (side["startOffsetMilliseconds"].IsNumber() && std::isfinite(side["startOffsetMilliseconds"].GetDouble())))) return false;
        const auto quality = std::string_view(side["quality"].GetString(), side["quality"].GetStringLength());
        const auto cleanup = std::string_view(side["cleanup"].GetString(), side["cleanup"].GetStringLength());
        if (quality != "CLEAN" && quality != "ANOMALIES_PRESENT" && quality != "SEVERE_ANOMALIES" && quality != "UNKNOWN") return false;
        if (cleanup != "NOT_ATTEMPTED" && cleanup != "RELEASE_ATTEMPTED" && cleanup != "UNKNOWN" && cleanup != "FAILED") return false;
        if (provenance == "REAL_WASAPI_DUPLEX" && side["startSucceeded"].GetBool() && (!side["startHresult"].IsString() ||
            std::string_view(side["startHresult"].GetString(), side["startHresult"].GetStringLength()) != "0x00000000")) return false;
        if (provenance == "REAL_WASAPI_DUPLEX" && side["stopSucceeded"].GetBool() && (!side["stopHresult"].IsString() ||
            std::string_view(side["stopHresult"].GetString(), side["stopHresult"].GetStringLength()) != "0x00000000")) return false;
        return true;
    };
    if (!sideValid(root["capture"], captureId, "CAPTURE") || !sideValid(root["render"], renderId, "RENDER")) return false;
    const auto& duplex = root["duplex"];
    for (const auto name : {"bothReadyBeforeStart", "coordinatedStartAuthorized", "cancelled", "peerStopRequested"})
        if (!duplex.HasMember(name) || !duplex[name].IsBool()) return false;
    if (!duplex.HasMember("status") || !duplex["status"].IsString()) return false;
    const auto status = std::string_view(duplex["status"].GetString(), duplex["status"].GetStringLength());
    if (status != "DUPLEX_CLEAN" && status != "DUPLEX_WARNINGS" && status != "FAILED" && status != "INCOMPLETE" && status != "CANCELLED") return false;
    for (const auto name : {"startDifferenceMilliseconds", "simultaneousRuntimeMilliseconds"})
        if (!duplex.HasMember(name) || !(duplex[name].IsNull() ||
            (duplex[name].IsNumber() && std::isfinite(duplex[name].GetDouble()) && duplex[name].GetDouble() >= 0))) return false;
    if (equals(duplex, "status", "DUPLEX_CLEAN") &&
        (!root["capture"]["startSucceeded"].GetBool() || !root["render"]["startSucceeded"].GetBool() ||
         !root["capture"]["durationReached"].GetBool() || !root["render"]["durationReached"].GetBool() ||
         root["capture"]["cancelled"].GetBool() || root["render"]["cancelled"].GetBool() ||
         root["capture"]["watchdogTimedOut"].GetBool() || root["render"]["watchdogTimedOut"].GetBool() ||
         root["capture"]["deviceInvalidated"].GetBool() || root["render"]["deviceInvalidated"].GetBool() ||
         root["duplex"]["peerStopRequested"].GetBool() ||
         root["capture"]["framesProcessed"].GetUint64() < root["capture"]["expectedFrames"].GetUint64() * 9 / 10 ||
         root["render"]["framesProcessed"].GetUint64() < root["render"]["expectedFrames"].GetUint64() * 9 / 10 ||
         root["capture"]["expectedFrames"].GetUint64() == 0 || root["render"]["expectedFrames"].GetUint64() == 0 ||
         !root["capture"]["stopSucceeded"].GetBool() || !root["render"]["stopSucceeded"].GetBool() ||
         !root["duplex"]["bothReadyBeforeStart"].GetBool() || !root["duplex"]["coordinatedStartAuthorized"].GetBool() ||
         !equals(root["capture"], "quality", "CLEAN") || !equals(root["render"], "quality", "CLEAN") ||
         root["capture"]["discontinuities"].GetUint64() != 0 || root["render"]["discontinuities"].GetUint64() != 0 ||
         root["capture"]["timestampErrors"].GetUint64() != 0 || root["render"]["timestampErrors"].GetUint64() != 0 ||
         root["capture"]["serviceFaults"].GetUint64() != 0 || root["render"]["serviceFaults"].GetUint64() != 0 ||
         root["capture"]["packetDrainLimitHits"].GetUint64() != 0 || root["render"]["packetDrainLimitHits"].GetUint64() != 0 ||
         root["capture"]["longEventIntervals"].GetUint64() != 0 || root["render"]["longEventIntervals"].GetUint64() != 0 ||
         (root["capture"]["devicePositionAnomalies"].IsUint64() && root["capture"]["devicePositionAnomalies"].GetUint64() != 0) ||
         (root["capture"]["unavailableCaptureBuffers"].IsUint64() && root["capture"]["unavailableCaptureBuffers"].GetUint64() != 0) ||
         !root["render"]["silentBuffersServiced"].IsUint64() || root["render"]["silentBuffersServiced"].GetUint64() == 0 ||
         !root["capture"]["ready"].GetBool() || !root["render"]["ready"].GetBool() ||
         !root["capture"]["frameCoveragePercent"].IsNumber() || root["capture"]["frameCoveragePercent"].GetDouble() < 90.0 ||
         !root["render"]["frameCoveragePercent"].IsNumber() || root["render"]["frameCoveragePercent"].GetDouble() < 90.0 ||
         (provenance == "REAL_WASAPI_DUPLEX" && (!root["capture"]["initializeHresult"].IsString() || !root["render"]["initializeHresult"].IsString() ||
          std::string_view(root["capture"]["initializeHresult"].GetString(), root["capture"]["initializeHresult"].GetStringLength()) != "0x00000000" ||
          std::string_view(root["render"]["initializeHresult"].GetString(), root["render"]["initializeHresult"].GetStringLength()) != "0x00000000")) ||
         !root["capture"]["actualBufferFrames"].IsUint64() || !root["render"]["actualBufferFrames"].IsUint64() ||
         !equals(root["capture"], "cleanup", "RELEASE_ATTEMPTED") || !equals(root["render"], "cleanup", "RELEASE_ATTEMPTED") ||
         !duplex["simultaneousRuntimeMilliseconds"].IsNumber() || duplex["simultaneousRuntimeMilliseconds"].GetDouble() < duration * 900.0)) return false;
    return true;
}

} // namespace DuplexValidationReport
