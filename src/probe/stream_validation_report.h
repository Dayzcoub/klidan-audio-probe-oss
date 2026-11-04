#pragma once

#include <cstdint>
#include <optional>
#include <sstream>
#include <string>
#include <string_view>
#include <type_traits>

namespace StreamValidationReport {

struct Report {
    std::string direction, period, streamQuality = "UNKNOWN", cleanup = "UNKNOWN";
    std::string activationHresult, devicePeriodHresult, queryHresult, initializeHresult, firstInitializeHresult, startHresult, stopHresult;
    std::string actualBufferFramesHresult, eventCreateHresult, setEventHandleHresult, errorHresult;
    std::string errorStage = "UNKNOWN", probeVersion = "unknown";
    unsigned durationSeconds = 0;
    std::optional<std::uint32_t> actualBufferFrames;
    std::optional<std::int64_t> requestedPeriod100ns, actualBufferPeriod100ns;
    std::optional<std::uint64_t> events, packets, framesProcessed, expectedFrames;
    std::optional<double> elapsedMilliseconds, frameCoveragePercent;
    std::optional<std::uint64_t> discontinuities, timestampErrors, unavailableCaptureBuffers;
    std::optional<std::uint64_t> bufferServiceFaults, eventWaitTimeouts;
    std::optional<bool> formatSupported, initialized, eventReady, startSucceeded, durationReached;
    std::optional<bool> cancelled, watchdogTimedOut, deviceInvalidated, stopSucceeded;
};

inline std::string Escape(std::string_view value) {
    std::string out = "\"";
    for (const unsigned char c : value) {
        if (c == '"' || c == '\\') { out.push_back('\\'); out.push_back(static_cast<char>(c)); }
        else if (c < 0x20) { constexpr char hex[] = "0123456789abcdef"; out += "\\u00"; out.push_back(hex[c >> 4]); out.push_back(hex[c & 15]); }
        else out.push_back(static_cast<char>(c));
    }
    return out + '"';
}
template<class T> inline std::string Optional(const std::optional<T>& value) {
    if (!value) return "null";
    if constexpr (std::is_same_v<T, bool>) return *value ? "true" : "false";
    else return std::to_string(*value);
}
inline std::string OptionalString(std::string_view value) { return value.empty() ? "null" : Escape(value); }

inline std::string ToJson(const Report& r, std::string_view timestampUtc, std::string_view reportId) {
    std::ostringstream out;
    out << "{\"schemaVersion\":1,\"reportType\":\"wasapi-stream-validation\",\"reportId\":" << Escape(reportId)
        << ",\"timestampUtc\":" << Escape(timestampUtc) << ",\"provenance\":\"REAL_WASAPI_STREAM_VALIDATION\",\"probeVersion\":" << Escape(r.probeVersion)
        << ",\"direction\":" << Escape(r.direction) << ",\"format\":{\"sampleRateHz\":48000,\"encoding\":\"PCM16\",\"channels\":2},\"period\":" << Escape(r.period)
        << ",\"durationSeconds\":" << r.durationSeconds << ",\"activationHresult\":" << OptionalString(r.activationHresult)
        << ",\"devicePeriodHresult\":" << OptionalString(r.devicePeriodHresult) << ",\"queryHresult\":" << OptionalString(r.queryHresult) << ",\"formatSupported\":" << Optional(r.formatSupported)
        << ",\"initializeSucceeded\":" << Optional(r.initialized) << ",\"firstInitializeHresult\":" << OptionalString(r.firstInitializeHresult)
        << ",\"finalInitializeHresult\":" << OptionalString(r.initializeHresult) << ",\"requestedPeriod100ns\":" << Optional(r.requestedPeriod100ns)
        << ",\"actualBufferFrames\":" << Optional(r.actualBufferFrames) << ",\"actualBufferPeriod100ns\":" << Optional(r.actualBufferPeriod100ns)
        << ",\"actualBufferFramesHresult\":" << OptionalString(r.actualBufferFramesHresult) << ",\"setEventHandleHresult\":" << OptionalString(r.setEventHandleHresult)
        << ",\"eventCreateHresult\":" << OptionalString(r.eventCreateHresult) << ",\"eventReady\":" << Optional(r.eventReady) << ",\"startSucceeded\":" << Optional(r.startSucceeded)
        << ",\"startHresult\":" << OptionalString(r.startHresult) << ",\"events\":" << Optional(r.events) << ",\"packets\":" << Optional(r.packets)
        << ",\"framesProcessed\":" << Optional(r.framesProcessed) << ",\"expectedFrames\":" << Optional(r.expectedFrames)
        << ",\"frameCoveragePercent\":" << Optional(r.frameCoveragePercent) << ",\"elapsedMilliseconds\":" << Optional(r.elapsedMilliseconds)
        << ",\"durationReached\":" << Optional(r.durationReached) << ",\"discontinuities\":" << Optional(r.discontinuities)
        << ",\"timestampErrors\":" << Optional(r.timestampErrors) << ",\"unavailableCaptureBuffers\":" << Optional(r.unavailableCaptureBuffers)
        << ",\"bufferServiceFaults\":" << Optional(r.bufferServiceFaults) << ",\"eventWaitTimeouts\":" << Optional(r.eventWaitTimeouts)
        << ",\"watchdogTimedOut\":" << Optional(r.watchdogTimedOut) << ",\"cancelled\":" << Optional(r.cancelled)
        << ",\"deviceInvalidated\":" << Optional(r.deviceInvalidated) << ",\"streamQuality\":" << Escape(r.streamQuality)
        << ",\"stopSucceeded\":" << Optional(r.stopSucceeded) << ",\"stopHresult\":" << OptionalString(r.stopHresult)
        << ",\"cleanup\":" << Escape(r.cleanup) << ",\"errorStage\":" << Escape(r.errorStage)
        << ",\"errorHresult\":" << OptionalString(r.errorHresult) << ",\"physicalPlayback\":\"NOT_MEASURED\",\"physicalRouting\":\"UNKNOWN\"}";
    return out.str();
}

} // namespace StreamValidationReport
