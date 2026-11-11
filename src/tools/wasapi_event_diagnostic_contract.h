#pragma once

#include <cstdint>
#include <chrono>
#include <optional>
#include <string>
#include <string_view>

namespace WasapiEventDiagnostic {

inline constexpr std::wstring_view kRenderEndpointId = L"{0.0.0.00000000}.{ba4ca4cc-35d7-437a-b53b-99ffedc7e5b8}";
inline constexpr std::string_view kProvenance = "INDEPENDENT_WASAPI_RENDER_EVENT_DIAGNOSTIC";
inline constexpr std::uint32_t kSchemaVersion = 2;
inline constexpr std::string_view kReportType = "independent-wasapi-render-event-diagnostic-v2";
inline constexpr std::uint32_t kRate = 48'000;
inline constexpr std::uint32_t kChannels = 2;
inline constexpr std::uint32_t kDurationSeconds = 5;
inline constexpr std::uint32_t kRequestedFrames = 144;
inline constexpr std::int64_t kPeriod100ns = 30'000;
inline constexpr std::uint32_t kMaximumEvents = 4096;

enum class Command { Run, Help, SelfTest, BuildInfo, Invalid };
enum class WaitTimeoutDisposition { ContinueWaiting, DeadlineReached, WatchdogExpired };

inline constexpr std::chrono::milliseconds kEventWatchdog{1'000};

inline WaitTimeoutDisposition ClassifyWaitTimeout(std::chrono::steady_clock::time_point now,
    std::chrono::steady_clock::time_point deadline, std::chrono::steady_clock::time_point lastEventAt) {
    if (now >= deadline) return WaitTimeoutDisposition::DeadlineReached;
    if (now - lastEventAt >= kEventWatchdog) return WaitTimeoutDisposition::WatchdogExpired;
    return WaitTimeoutDisposition::ContinueWaiting;
}

inline constexpr bool MmcssRegistrationAllowsStart(bool requested, bool registered) {
    return !requested || registered;
}

inline constexpr bool MmcssCleanupConfirmed(bool requested, bool registered, bool reverted) {
    return !requested || !registered || reverted;
}

struct Options {
    Command command = Command::Invalid;
    std::wstring outputPath;
    std::wstring error;
    bool useProAudioMmcss = false;
};

inline Options ParseArguments(int argc, wchar_t** argv) {
    Options result;
    if (argc == 2 && std::wstring_view(argv[1]) == L"--help") { result.command = Command::Help; return result; }
    if (argc == 2 && std::wstring_view(argv[1]) == L"--self-test") { result.command = Command::SelfTest; return result; }
    if (argc == 2 && std::wstring_view(argv[1]) == L"--build-info") { result.command = Command::BuildInfo; return result; }
    bool confirmed = false, endpoint = false, duration = false, rate = false, channels = false;
    bool period = false, format = false, mmcss = false, silence = false, output = false;
    for (int i = 1; i < argc; ++i) {
        const std::wstring_view key(argv[i]);
        const auto value = [&]() -> std::wstring_view { return i + 1 < argc ? argv[++i] : L""; };
        if (key == L"--confirm-operator-authorization" && !confirmed) confirmed = true;
        else if (key == L"--render-id" && !endpoint && value() == kRenderEndpointId) endpoint = true;
        else if (key == L"--duration" && !duration && value() == L"5") duration = true;
        else if (key == L"--rate" && !rate && value() == L"48000") rate = true;
        else if (key == L"--channels" && !channels && value() == L"2") channels = true;
        else if (key == L"--period-ms" && !period && value() == L"3") period = true;
        else if (key == L"--format" && !format && value() == L"pcm16") format = true;
        else if (key == L"--mmcss" && !mmcss) {
            const auto mode = value();
            if (mode == L"off" || mode == L"pro-audio") {
                mmcss = true;
                result.useProAudioMmcss = mode == L"pro-audio";
            } else {
                result.error = L"--mmcss must be off or pro-audio.";
                return result;
            }
        }
        else if (key == L"--silence-only" && !silence) silence = true;
        else if (key == L"--output-json" && !output && !value().empty()) { output = true; result.outputPath = std::wstring(argv[i]); }
        else { result.error = L"Arguments must specify exactly one authorized Bloody Render 3 ms / 5 s PCM16 silence-only test."; return result; }
    }
    if (!(confirmed && endpoint && duration && rate && channels && period && format && mmcss && silence && output)) {
        result.error = L"Missing required fixed test setting or explicit operator authorization.";
        return result;
    }
    result.command = Command::Run;
    return result;
}

struct Report {
    std::string buildCommit;
    std::string mmcss = "OFF";
    std::string mmcssRegistrationHresult = "NOT_REQUESTED";
    std::string mmcssRevertHresult = "NOT_REQUESTED";
    std::string operationId;
    std::string timestampUtc;
    std::string diagnosticDisposition = "NOT_STARTED";
    std::string errorStage = "NONE";
    std::string errorHresult = "NOT_APPLICABLE";
    std::string cleanupState = "NOT_STARTED";
    std::string activateHresult = "NOT_CALLED";
    std::string getDevicePeriodHresult = "NOT_CALLED";
    std::string formatSupportHresult = "NOT_CALLED";
    std::string initializeHresult = "NOT_CALLED";
    std::string getBufferSizeHresult = "NOT_CALLED";
    std::string setEventHandleHresult = "NOT_CALLED";
    std::string renderServiceHresult = "NOT_CALLED";
    std::string prefillGetBufferHresult = "NOT_CALLED";
    std::string prefillReleaseBufferHresult = "NOT_CALLED";
    std::string startHresult = "NOT_CALLED";
    std::string stopHresult = "NOT_CALLED";
    std::string eventCloseHresult = "NOT_CALLED";
    std::string cancelEventCreateHresult = "NOT_CALLED";
    std::string cancelEventCloseHresult = "NOT_CALLED";
    std::string lastWaitStatus = "NOT_CALLED";
    std::string comInitializeHresult = "NOT_CALLED";
    std::optional<std::int64_t> defaultPeriod100ns;
    std::optional<std::int64_t> minimumPeriod100ns;
    std::int64_t requestedBufferDuration100ns = kPeriod100ns;
    std::int64_t requestedPeriodicity100ns = kPeriod100ns;
    std::optional<std::uint32_t> actualBufferFrames;
    std::optional<std::int64_t> actualBufferPeriod100ns;
    std::uint32_t prefillFrames = 0;
    std::optional<std::uint32_t> eventCount;
    std::optional<std::uint32_t> bufferGetCount;
    std::optional<std::uint32_t> bufferReleaseCount;
    std::optional<std::uint64_t> framesProcessed;
    std::optional<double> frameCoveragePercent;
    std::optional<double> elapsedMilliseconds;
    std::optional<double> eventRateHz;
    std::optional<double> meanFramesPerEvent;
    std::optional<double> meanEventIntervalMilliseconds;
    std::optional<double> firstEventDelayMilliseconds;
    std::optional<double> eventIntervalMedianMilliseconds;
    std::optional<double> eventIntervalP95Milliseconds;
    std::optional<double> eventIntervalP99Milliseconds;
    std::optional<double> eventIntervalMaximumMilliseconds;
    std::optional<double> eventIntervalMaximumEndOffsetMilliseconds;
    std::optional<std::uint32_t> waitTimeoutCount;
    bool durationReached = false;
    bool cancelled = false;
    bool watchdogTimedOut = false;
    bool deviceInvalidated = false;
    bool clientReleased = false;
    bool eventHandleClosed = false;
    bool cancelEventClosed = false;
    bool mmcssRegistered = false;
    bool mmcssReverted = false;
};

} // namespace WasapiEventDiagnostic
