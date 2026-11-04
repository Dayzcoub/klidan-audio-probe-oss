#pragma once

#include <array>
#include <cstdint>
#include <iomanip>
#include <optional>
#include <sstream>
#include <string>
#include <string_view>

namespace RenderChannelReport {

inline std::string Escape(std::string_view value) {
    std::string out = "\"";
    for (const unsigned char c : value) {
        if (c == '"' || c == '\\') { out += '\\'; out += static_cast<char>(c); }
        else if (c == '\n') out += "\\n";
        else if (c == '\r') out += "\\r";
        else if (c == '\t') out += "\\t";
        else if (c >= 0x20) out += static_cast<char>(c);
    }
    out += '"';
    return out;
}

inline std::string OptionalNumber(const std::optional<std::uint64_t>& value) {
    return value ? std::to_string(*value) : "null";
}
inline std::string OptionalText(const std::optional<std::string>& value) {
    return value ? Escape(*value) : "null";
}
inline std::string OptionalDecimal(const std::optional<double>& value) {
    if (!value) return "null";
    std::ostringstream out;
    out << std::setprecision(8) << *value;
    return out.str();
}

struct Report {
    std::uint32_t renderChannel = 0;
    std::uint32_t durationSeconds = 0;
    bool minimumPeriod = false;
    std::optional<std::uint64_t> endpointMixRateHz;
    std::optional<std::uint64_t> endpointMixChannels;
    std::optional<std::uint64_t> endpointMixBitsPerSample;
    std::optional<std::uint64_t> actualSampleRateHz;
    std::optional<std::uint64_t> actualChannels;
    std::optional<std::string> actualEncoding;
    std::optional<std::string> formatSupportHresult;
    std::optional<std::string> initializeHresult;
    std::optional<std::string> firstInitializeHresult;
    std::optional<std::string> alignmentRetryInitializeHresult;
    std::optional<std::string> startHresult;
    std::optional<std::string> stopHresult;
    std::optional<std::uint64_t> actualBufferFrames;
    std::optional<std::uint64_t> actualPeriodMicroseconds;
    std::optional<std::uint64_t> generatedFrames;
    std::optional<std::uint64_t> leftNonzeroFrames;
    std::optional<std::uint64_t> rightNonzeroFrames;
    std::optional<std::string> leftPeakDbfs;
    std::optional<std::string> rightPeakDbfs;
    std::optional<std::uint64_t> eventCount;
    std::optional<double> eventIntervalAverageMs;
    std::optional<double> eventIntervalMaximumMs;
    std::optional<double> frameCoveragePercent;
    std::optional<std::uint64_t> anomalies;
    std::optional<std::uint64_t> longEventIntervals;
    std::optional<std::uint64_t> intermediateWaitTimeouts;
    std::optional<std::uint64_t> streamApiFailures;
    std::optional<bool> watchdogTimedOut;
    std::optional<bool> cancelled;
    std::optional<bool> deviceLost;
    std::optional<bool> finalSilenceSubmitted;
    std::optional<std::string> lifecycleStatus;
    std::optional<std::string> streamQuality;
    std::optional<std::string> cleanupStatus;
    std::optional<std::string> errorStage;
    std::optional<std::string> errorHresult;
};

inline std::string ToJson(const Report& r, std::string_view timestampUtc,
                          std::string_view reportId, std::string_view probeVersion) {
    std::ostringstream out;
    out << "{\"schemaVersion\":1,\"reportType\":\"wasapi-render-channel-test\",\"reportId\":" << Escape(reportId)
        << ",\"timestampUtc\":" << Escape(timestampUtc) << ",\"probeVersion\":" << Escape(probeVersion)
        << ",\"backend\":\"wasapi\",\"evidence\":{\"sourceType\":\"ACTIVE_STREAM\",\"certified\":false,\"acousticMeasurement\":false},\"privacy\":\"sanitized\""
        << ",\"requested\":{\"direction\":\"RENDER\",\"sampleRateHz\":48000,\"channels\":2,\"format\":\"PCM16\",\"renderChannelIndex\":" << r.renderChannel
        << ",\"durationSeconds\":" << r.durationSeconds << ",\"periodMode\":" << Escape(r.minimumPeriod ? "minimum" : "default") << "}"
        << ",\"endpoint\":{\"identity\":\"EXPLICITLY_SELECTED\",\"endpointId\":null,\"deviceMixFormat\":{\"sampleRateHz\":"
        << OptionalNumber(r.endpointMixRateHz) << ",\"channels\":" << OptionalNumber(r.endpointMixChannels)
        << ",\"bitsPerSample\":" << OptionalNumber(r.endpointMixBitsPerSample) << "}}"
        << ",\"observed\":{\"actualFormat\":{\"sampleRateHz\":" << OptionalNumber(r.actualSampleRateHz)
        << ",\"channels\":" << OptionalNumber(r.actualChannels)
        << ",\"encoding\":" << OptionalText(r.actualEncoding) << "}"
        << ",\"formatSupportHresult\":" << OptionalText(r.formatSupportHresult)
        << ",\"initializeHresult\":" << OptionalText(r.initializeHresult)
        << ",\"firstInitializeHresult\":" << OptionalText(r.firstInitializeHresult)
        << ",\"alignmentRetryInitializeHresult\":" << OptionalText(r.alignmentRetryInitializeHresult)
        << ",\"startHresult\":" << OptionalText(r.startHresult)
        << ",\"stopHresult\":" << OptionalText(r.stopHresult)
        << ",\"actualBufferFrames\":" << OptionalNumber(r.actualBufferFrames)
        << ",\"actualPeriodMicroseconds\":" << OptionalNumber(r.actualPeriodMicroseconds)
        << ",\"generatedFrames\":" << OptionalNumber(r.generatedFrames)
        << ",\"nonzeroFramesByChannel\":[" << OptionalNumber(r.leftNonzeroFrames) << ',' << OptionalNumber(r.rightNonzeroFrames) << ']'
        << ",\"peakDbfsByChannel\":[" << OptionalText(r.leftPeakDbfs) << ',' << OptionalText(r.rightPeakDbfs) << ']'
        << ",\"eventCount\":" << OptionalNumber(r.eventCount)
        << ",\"eventIntervalAverageMs\":" << OptionalDecimal(r.eventIntervalAverageMs)
        << ",\"eventIntervalMaximumMs\":" << OptionalDecimal(r.eventIntervalMaximumMs)
        << ",\"frameCoveragePercent\":" << OptionalDecimal(r.frameCoveragePercent)
        << ",\"bufferServiceAnomalies\":" << OptionalNumber(r.anomalies)
        << ",\"longEventIntervals\":" << OptionalNumber(r.longEventIntervals)
        << ",\"intermediateWaitTimeouts\":" << OptionalNumber(r.intermediateWaitTimeouts)
        << ",\"streamApiFailures\":" << OptionalNumber(r.streamApiFailures)
        << ",\"watchdogTimedOut\":" << (r.watchdogTimedOut ? (*r.watchdogTimedOut ? "true" : "false") : "null")
        << ",\"cancelled\":" << (r.cancelled ? (*r.cancelled ? "true" : "false") : "null")
        << ",\"deviceLost\":" << (r.deviceLost ? (*r.deviceLost ? "true" : "false") : "null")
        << ",\"finalSilenceSubmitted\":" << (r.finalSilenceSubmitted ? (*r.finalSilenceSubmitted ? "true" : "false") : "null")
        << ",\"lifecycleStatus\":" << OptionalText(r.lifecycleStatus)
        << ",\"streamQuality\":" << OptionalText(r.streamQuality)
        << ",\"cleanupStatus\":" << OptionalText(r.cleanupStatus)
        << ",\"errorStage\":" << OptionalText(r.errorStage)
        << ",\"errorHresult\":" << OptionalText(r.errorHresult) << '}'
        << ",\"physicalOutputMapping\":\"UNKNOWN\",\"physicalPlayback\":\"NOT_MEASURED\"}";
    return out.str();
}

} // namespace RenderChannelReport
