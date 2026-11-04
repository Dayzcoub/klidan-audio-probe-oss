#include "../src/probe/render_channel_report.h"

#include <fstream>
#include <iostream>

int main(int argc, char** argv) {
    RenderChannelReport::Report report;
    report.renderChannel = 2;
    report.durationSeconds = 5;
    report.minimumPeriod = true;
    report.endpointMixRateHz = 44'100;
    report.endpointMixChannels = 2;
    report.endpointMixBitsPerSample = 32;
    report.actualSampleRateHz = 48'000;
    report.actualChannels = 2;
    report.actualEncoding = "PCM16";
    report.formatSupportHresult = "0x00000000";
    report.initializeHresult = "0x00000000";
    report.startHresult = "0x00000000";
    report.stopHresult = "0x00000000";
    report.actualBufferFrames = 144;
    report.actualPeriodMicroseconds = 3'000;
    report.generatedFrames = 239'904;
    report.leftNonzeroFrames = 0;
    report.rightNonzeroFrames = 96'000;
    report.leftPeakDbfs = "-INF";
    report.rightPeakDbfs = "-36.1234";
    report.eventCount = 1'666;
    report.eventIntervalAverageMs = 3.0;
    report.eventIntervalMaximumMs = 3.2;
    report.frameCoveragePercent = 100.0;
    report.anomalies = 0;
    report.lifecycleStatus = "STREAM_COMPLETED";
    report.streamQuality = "CLEAN";
    report.cleanupStatus = "RELEASE_ATTEMPTED";
    report.finalSilenceSubmitted = true;
    const auto json = RenderChannelReport::ToJson(report, "2026-10-30T12:00:00Z",
        "12345678-1234-4234-8234-123456789abc", "0.3.0");
    if (json.find("\"schemaVersion\":1") == std::string::npos ||
        json.find("\"renderChannelIndex\":2") == std::string::npos ||
        json.find("\"endpointId\":null") == std::string::npos ||
        json.find("\"physicalOutputMapping\":\"UNKNOWN\"") == std::string::npos ||
        json.find("\"certified\":false") == std::string::npos ||
        json.find("12345678-1234-4234-8234-123456789abc") == std::string::npos ||
        json.find("private-path") != std::string::npos) return 1;
    const auto untouched = RenderChannelReport::ToJson(RenderChannelReport::Report{},
        "2026-10-30T12:00:00Z", "12345678-1234-4234-8234-123456789abd", "0.3.0");
    if (untouched.find("\"startHresult\":null") == std::string::npos ||
        untouched.find("\"generatedFrames\":null") == std::string::npos ||
        untouched.find("\"cleanupStatus\":null") == std::string::npos) return 1;
    report.lifecycleStatus = "INITIALIZE_FAILED";
    report.initializeHresult = "0x88890008";
    report.errorStage = "Initialize";
    report.errorHresult = "0x88890008";
    const auto failedJson = RenderChannelReport::ToJson(report, "2026-10-30T12:00:00Z",
        "12345678-1234-4234-8234-123456789abe", "0.3.0");
    if (failedJson.find("\"lifecycleStatus\":\"INITIALIZE_FAILED\"") == std::string::npos ||
        failedJson.find("\"errorHresult\":\"0x88890008\"") == std::string::npos) return 1;
    report.lifecycleStatus = "STREAM_FAILED";
    report.cancelled = true;
    report.deviceLost = false;
    const auto cancelledJson = RenderChannelReport::ToJson(report, "2026-10-30T12:00:00Z",
        "12345678-1234-4234-8234-123456789abf", "0.3.0");
    if (cancelledJson.find("\"cancelled\":true") == std::string::npos ||
        cancelledJson.find("\"deviceLost\":false") == std::string::npos) return 1;
    if (argc == 3 && std::string_view(argv[1]) == "--json") {
        std::ofstream file(argv[2], std::ios::binary | std::ios::trunc);
        file << json;
        return file ? 0 : 2;
    }
    if (argc != 1) return 2;
    std::cout << json << '\n';
    return 0;
}
