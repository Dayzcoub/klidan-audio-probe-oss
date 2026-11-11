#include "wasapi_event_diagnostic_contract.h"
#include "wasapi_event_diagnostic_io.h"
#include "wasapi_event_diagnostic_json.h"

#include <windows.h>
#include <audioclient.h>
#include <mmdeviceapi.h>
#include <ksmedia.h>

#include <algorithm>
#include <atomic>
#include <chrono>
#include <cmath>
#include <cstdio>
#include <filesystem>
#include <string>
#include <vector>

#ifndef WASAPI_DIAGNOSTIC_BUILD_COMMIT
#define WASAPI_DIAGNOSTIC_BUILD_COMMIT "UNKNOWN"
#endif

namespace {
using namespace WasapiEventDiagnostic;
using Clock = std::chrono::steady_clock;
std::atomic<HANDLE> g_cancelEvent{nullptr};

BOOL WINAPI ConsoleControl(DWORD event) {
    if (event != CTRL_C_EVENT && event != CTRL_BREAK_EVENT) return FALSE;
    if (const auto handle = g_cancelEvent.load(std::memory_order_acquire); handle != nullptr)
        SetEvent(handle);
    return TRUE;
}

std::string Hresult(HRESULT value) {
    char text[16]{};
    std::snprintf(text, sizeof(text), "0x%08lX", static_cast<unsigned long>(value));
    return text;
}

std::string Utf8(std::wstring_view value) {
    if (value.empty()) return {};
    const int length = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value.data(),
        static_cast<int>(value.size()), nullptr, 0, nullptr, nullptr);
    if (length <= 0) return {};
    std::string result(static_cast<std::size_t>(length), '\0');
    if (WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value.data(), static_cast<int>(value.size()),
        result.data(), length, nullptr, nullptr) != length) return {};
    return result;
}

std::string GuidText(GUID value) {
    wchar_t wide[40]{};
    if (StringFromGUID2(value, wide, static_cast<int>(std::size(wide))) == 0) return "UNAVAILABLE";
    return Utf8(wide);
}

std::string TimestampUtc() {
    SYSTEMTIME time{};
    GetSystemTime(&time);
    char text[40]{};
    std::snprintf(text, sizeof(text), "%04u-%02u-%02uT%02u:%02u:%02u.%03uZ",
        time.wYear, time.wMonth, time.wDay, time.wHour, time.wMinute, time.wSecond, time.wMilliseconds);
    return text;
}

double Percentile(const std::vector<double>& values, double percentile) {
    if (values.empty()) return 0.0;
    std::vector<double> sorted(values);
    std::sort(sorted.begin(), sorted.end());
    if (percentile == 50.0 && sorted.size() % 2 == 0)
        return (sorted[sorted.size() / 2 - 1] + sorted[sorted.size() / 2]) / 2.0;
    const auto rank = static_cast<std::size_t>(std::ceil(percentile * sorted.size() / 100.0));
    return sorted[std::max<std::size_t>(1, rank) - 1];
}

void PrintHelp() {
    std::puts("Independent WASAPI Exclusive Render event diagnostic (fixed 3 ms / 5 s; silence only).");
    std::puts("No operation runs without the explicit confirmation flag.");
    std::puts("Usage: WasapiRenderEventDiagnostic.exe --confirm-operator-authorization --render-id {0.0.0.00000000}.{ba4ca4cc-35d7-437a-b53b-99ffedc7e5b8} --duration 5 --rate 48000 --channels 2 --period-ms 3 --format pcm16 --mmcss off --silence-only --output-json ABSOLUTE_PATH");
    std::puts("The executable performs one Exclusive EVENTCALLBACK operation and never changes Windows device settings.");
}

int Run(const Options& options) {
    Report report;
    report.buildCommit = WASAPI_DIAGNOSTIC_BUILD_COMMIT;
    report.timestampUtc = TimestampUtc();
    HRESULT comHr = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    report.comInitializeHresult = Hresult(comHr);
    const bool comInitialized = SUCCEEDED(comHr);
    GUID operation{};
    if (comInitialized && SUCCEEDED(CoCreateGuid(&operation))) report.operationId = GuidText(operation);
    else report.operationId = "UNAVAILABLE";

    if (GetFileAttributesW(options.outputPath.c_str()) != INVALID_FILE_ATTRIBUTES) {
        report.diagnosticDisposition = "FAILED";
        report.errorStage = "OUTPUT_ALREADY_EXISTS";
    } else {
        const DWORD pathError = GetLastError();
        if (pathError != ERROR_FILE_NOT_FOUND && pathError != ERROR_PATH_NOT_FOUND) {
            report.diagnosticDisposition = "FAILED";
            report.errorStage = "OUTPUT_PATH_PREFLIGHT";
        } else {
            const auto parent = std::filesystem::path(options.outputPath).parent_path();
            const DWORD parentAttributes = GetFileAttributesW(parent.c_str());
            if (parentAttributes == INVALID_FILE_ATTRIBUTES || (parentAttributes & FILE_ATTRIBUTE_DIRECTORY) == 0) {
                report.diagnosticDisposition = "FAILED";
                report.errorStage = "OUTPUT_DIRECTORY_PREFLIGHT";
            }
        }
    }

    IMMDeviceEnumerator* enumerator = nullptr;
    IMMDevice* device = nullptr;
    IAudioClient* client = nullptr;
    IAudioRenderClient* render = nullptr;
    HANDLE eventHandle = nullptr;
    HANDLE cancelHandle = nullptr;
    bool started = false;
    bool bufferLeaseUnknown = false;
    bool stopSucceeded = false;
    std::vector<double> intervals;
    intervals.reserve(kMaximumEvents);
    double intervalSum = 0.0;
    double maximumIntervalEndOffset = 0.0;
    Clock::time_point startedAt{};
    std::uint32_t eventCount = 0, bufferGetCount = 0, bufferReleaseCount = 0, waitTimeoutCount = 0;
    std::uint64_t framesProcessed = 0;
    std::string stageFailure;

    const auto fail = [&](const char* stage, HRESULT hr, bool unknown = false) {
        if (hr == AUDCLNT_E_DEVICE_INVALIDATED) { report.deviceInvalidated = true; unknown = true; }
        if (stageFailure.empty()) { stageFailure = stage; report.errorHresult = hr == E_PENDING ? "NOT_APPLICABLE" : Hresult(hr); }
        report.errorStage = stageFailure;
        if (unknown) report.diagnosticDisposition = "UNKNOWN";
        else if (report.diagnosticDisposition == "NOT_STARTED") report.diagnosticDisposition = "FAILED";
    };

    if (report.errorStage == "NONE" && !comInitialized) fail("COM_INITIALIZE", comHr);
    if (report.errorStage == "NONE") {
        HRESULT hr = CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_INPROC_SERVER,
                                      IID_PPV_ARGS(&enumerator));
        if (FAILED(hr)) fail("MMDEVICE_ENUMERATOR", hr);
    }
    if (report.errorStage == "NONE") {
        HRESULT hr = enumerator->GetDevice(std::wstring(kRenderEndpointId).c_str(), &device);
        if (FAILED(hr) || device == nullptr) fail("ENDPOINT_LOOKUP", FAILED(hr) ? hr : E_POINTER);
    }
    if (report.errorStage == "NONE") {
        LPWSTR actualId = nullptr;
        const HRESULT idHr = device->GetId(&actualId);
        const bool identityMatches = SUCCEEDED(idHr) && actualId != nullptr &&
            CompareStringOrdinal(actualId, -1, kRenderEndpointId.data(), static_cast<int>(kRenderEndpointId.size()), TRUE) == CSTR_EQUAL;
        CoTaskMemFree(actualId);
        DWORD state = 0;
        const HRESULT stateHr = identityMatches ? device->GetState(&state) : E_ACCESSDENIED;
        if (!identityMatches || FAILED(idHr) || FAILED(stateHr) || (state & DEVICE_STATE_ACTIVE) == 0)
            fail("ENDPOINT_IDENTITY_OR_STATE", FAILED(idHr) ? idHr : FAILED(stateHr) ? stateHr : E_ACCESSDENIED);
    }
    if (report.errorStage == "NONE") {
        const HRESULT hr = device->Activate(__uuidof(IAudioClient), CLSCTX_INPROC_SERVER, nullptr,
                                            reinterpret_cast<void**>(&client));
        report.activateHresult = Hresult(hr);
        if (FAILED(hr) || client == nullptr) fail("AUDIOCLIENT_ACTIVATE", FAILED(hr) ? hr : E_POINTER);
    }
    if (report.errorStage == "NONE") {
        REFERENCE_TIME defaultPeriod = 0, minimumPeriod = 0;
        const HRESULT hr = client->GetDevicePeriod(&defaultPeriod, &minimumPeriod);
        report.getDevicePeriodHresult = Hresult(hr);
        if (SUCCEEDED(hr)) { report.defaultPeriod100ns = defaultPeriod; report.minimumPeriod100ns = minimumPeriod; }
        if (FAILED(hr)) fail("GET_DEVICE_PERIOD", hr);
        else if (minimumPeriod <= 0 || defaultPeriod < kPeriod100ns || minimumPeriod > kPeriod100ns)
            fail("REQUESTED_PERIOD_OUTSIDE_DEVICE_BOUNDS", E_PENDING);
    }

    WAVEFORMATEXTENSIBLE format{};
    if (report.errorStage == "NONE") {
        format.Format.wFormatTag = WAVE_FORMAT_EXTENSIBLE;
        format.Format.nChannels = static_cast<WORD>(kChannels);
        format.Format.nSamplesPerSec = kRate;
        format.Format.wBitsPerSample = 16;
        format.Format.nBlockAlign = 4;
        format.Format.nAvgBytesPerSec = kRate * format.Format.nBlockAlign;
        format.Format.cbSize = sizeof(WAVEFORMATEXTENSIBLE) - sizeof(WAVEFORMATEX);
        format.Samples.wValidBitsPerSample = 16;
        format.dwChannelMask = SPEAKER_FRONT_LEFT | SPEAKER_FRONT_RIGHT;
        format.SubFormat = KSDATAFORMAT_SUBTYPE_PCM;
        const HRESULT hr = client->IsFormatSupported(AUDCLNT_SHAREMODE_EXCLUSIVE, &format.Format, nullptr);
        report.formatSupportHresult = Hresult(hr);
        if (hr != S_OK) fail("FORMAT_NOT_EXACTLY_SUPPORTED", hr);
    }
    if (report.errorStage == "NONE") {
        const HRESULT hr = client->Initialize(AUDCLNT_SHAREMODE_EXCLUSIVE,
            AUDCLNT_STREAMFLAGS_EVENTCALLBACK, kPeriod100ns, kPeriod100ns, &format.Format, nullptr);
        report.initializeHresult = Hresult(hr);
        if (FAILED(hr)) fail("AUDIOCLIENT_INITIALIZE", hr);
    }
    if (report.errorStage == "NONE") {
        UINT32 frames = 0;
        const HRESULT hr = client->GetBufferSize(&frames);
        report.getBufferSizeHresult = Hresult(hr);
        if (SUCCEEDED(hr)) {
            report.actualBufferFrames = frames;
            report.actualBufferPeriod100ns = static_cast<std::int64_t>((static_cast<std::uint64_t>(frames) * 10'000'000ULL + kRate / 2) / kRate);
        }
        if (FAILED(hr)) fail("GET_BUFFER_SIZE", hr);
        else if (frames != kRequestedFrames) fail("ACTUAL_BUFFER_GEOMETRY_MISMATCH", E_PENDING);
    }
    if (report.errorStage == "NONE") {
        eventHandle = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        if (eventHandle == nullptr) fail("CREATE_AUDIO_EVENT", HRESULT_FROM_WIN32(GetLastError()));
    }
    if (report.errorStage == "NONE") {
        const HRESULT hr = client->SetEventHandle(eventHandle);
        report.setEventHandleHresult = Hresult(hr);
        if (FAILED(hr)) fail("SET_EVENT_HANDLE", hr);
    }
    if (report.errorStage == "NONE") {
        const HRESULT hr = client->GetService(IID_PPV_ARGS(&render));
        report.renderServiceHresult = Hresult(hr);
        if (FAILED(hr) || render == nullptr) fail("GET_RENDER_CLIENT", FAILED(hr) ? hr : E_POINTER);
    }
    if (report.errorStage == "NONE") {
        BYTE* data = nullptr;
        const HRESULT getHr = render->GetBuffer(kRequestedFrames, &data);
        report.prefillGetBufferHresult = Hresult(getHr);
        if (FAILED(getHr)) fail("PREFILL_GET_BUFFER", getHr);
        else {
            const HRESULT releaseHr = render->ReleaseBuffer(kRequestedFrames, AUDCLNT_BUFFERFLAGS_SILENT);
            report.prefillReleaseBufferHresult = Hresult(releaseHr);
            if (SUCCEEDED(releaseHr)) report.prefillFrames = kRequestedFrames;
            if (FAILED(releaseHr)) { bufferLeaseUnknown = true; fail("PREFILL_RELEASE_BUFFER", releaseHr, true); }
        }
    }
    if (report.errorStage == "NONE") {
        cancelHandle = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        report.cancelEventCreateHresult = cancelHandle != nullptr ? Hresult(S_OK) : Hresult(HRESULT_FROM_WIN32(GetLastError()));
        if (cancelHandle == nullptr) fail("CREATE_CANCEL_EVENT", HRESULT_FROM_WIN32(GetLastError()));
    }
    if (report.errorStage == "NONE") {
        g_cancelEvent.store(cancelHandle, std::memory_order_release);
        if (!SetConsoleCtrlHandler(ConsoleControl, TRUE)) fail("REGISTER_CANCEL_HANDLER", HRESULT_FROM_WIN32(GetLastError()));
    }
    if (report.errorStage == "NONE") {
        const HRESULT hr = client->Start();
        report.startHresult = Hresult(hr);
        if (hr != S_OK) fail("AUDIOCLIENT_START", FAILED(hr) ? hr : E_FAIL, true);
        else {
            started = true;
            report.diagnosticDisposition = "UNKNOWN";
            const auto start = Clock::now();
            startedAt = start;
            const auto deadline = start + std::chrono::seconds(kDurationSeconds);
            auto previousEvent = Clock::time_point{};
            bool stoppedByError = false;
            while (Clock::now() < deadline) {
                const auto now = Clock::now();
                const auto remaining = std::chrono::ceil<std::chrono::milliseconds>(deadline - now).count();
                const DWORD waitMs = static_cast<DWORD>(std::clamp<std::int64_t>(remaining, 1, 1000));
                HANDLE waits[] = {eventHandle, cancelHandle};
                const DWORD wait = WaitForMultipleObjects(2, waits, FALSE, waitMs);
                const DWORD waitError = wait == WAIT_FAILED ? GetLastError() : ERROR_SUCCESS;
                const auto eventAt = Clock::now();
                report.lastWaitStatus = wait == WAIT_OBJECT_0 ? "AUDIO_EVENT" : wait == WAIT_OBJECT_0 + 1 ? "CANCEL_EVENT" : wait == WAIT_TIMEOUT ? "WAIT_TIMEOUT" : "WAIT_FAILED";
                if (wait == WAIT_OBJECT_0 + 1) { report.cancelled = true; report.diagnosticDisposition = "CANCELLED"; break; }
                if (wait == WAIT_TIMEOUT) {
                    ++waitTimeoutCount;
                    if (eventAt >= deadline) { report.durationReached = true; break; }
                    report.watchdogTimedOut = true;
                    report.diagnosticDisposition = "UNKNOWN";
                    fail("AUDIO_EVENT_WATCHDOG", HRESULT_FROM_WIN32(ERROR_TIMEOUT), true);
                    stoppedByError = true;
                    break;
                }
                if (wait != WAIT_OBJECT_0) {
                    const HRESULT waitHr = HRESULT_FROM_WIN32(wait == WAIT_FAILED ? waitError : ERROR_INVALID_DATA);
                    fail("WAIT_FOR_MULTIPLE_OBJECTS", waitHr, true);
                    stoppedByError = true;
                    break;
                }
                if (eventAt >= deadline) { report.durationReached = true; break; }
                if (eventCount >= kMaximumEvents) {
                    fail("EVENT_COUNT_LIMIT", E_PENDING, true);
                    stoppedByError = true;
                    break;
                }
                ++eventCount;
                if (eventCount == 1) report.firstEventDelayMilliseconds = std::chrono::duration<double, std::milli>(eventAt - start).count();
                if (previousEvent != Clock::time_point{}) {
                    const double interval = std::chrono::duration<double, std::milli>(eventAt - previousEvent).count();
                    intervals.push_back(interval);
                    intervalSum += interval;
                    if (!report.eventIntervalMaximumMilliseconds || interval > *report.eventIntervalMaximumMilliseconds) {
                        report.eventIntervalMaximumMilliseconds = interval;
                        maximumIntervalEndOffset = std::chrono::duration<double, std::milli>(eventAt - start).count();
                    }
                }
                previousEvent = eventAt;
                BYTE* buffer = nullptr;
                const HRESULT getHr = render->GetBuffer(kRequestedFrames, &buffer);
                if (FAILED(getHr)) {
                    fail("RENDER_GET_BUFFER", getHr, true); stoppedByError = true; break;
                }
                ++bufferGetCount;
                const HRESULT releaseHr = render->ReleaseBuffer(kRequestedFrames, AUDCLNT_BUFFERFLAGS_SILENT);
                if (FAILED(releaseHr)) {
                    bufferLeaseUnknown = true;
                    fail("RENDER_RELEASE_BUFFER", releaseHr, true); stoppedByError = true; break;
                }
                ++bufferReleaseCount;
                framesProcessed += kRequestedFrames;
            }
            const auto ended = Clock::now();
            report.elapsedMilliseconds = std::chrono::duration<double, std::milli>(ended - start).count();
            report.eventCount = eventCount;
            report.bufferGetCount = bufferGetCount;
            report.bufferReleaseCount = bufferReleaseCount;
            report.framesProcessed = framesProcessed;
            report.waitTimeoutCount = waitTimeoutCount;
            if (*report.elapsedMilliseconds > 0.0)
                report.eventRateHz = static_cast<double>(eventCount) / (*report.elapsedMilliseconds / 1000.0);
            if (bufferReleaseCount > 0)
                report.meanFramesPerEvent = static_cast<double>(framesProcessed) / bufferReleaseCount;
            if (!intervals.empty()) {
                report.meanEventIntervalMilliseconds = intervalSum / intervals.size();
                report.eventIntervalMedianMilliseconds = Percentile(intervals, 50.0);
                report.eventIntervalP95Milliseconds = Percentile(intervals, 95.0);
                report.eventIntervalP99Milliseconds = Percentile(intervals, 99.0);
                report.eventIntervalMaximumEndOffsetMilliseconds = maximumIntervalEndOffset;
            }
            report.frameCoveragePercent = 100.0 * static_cast<double>(framesProcessed) /
                (static_cast<double>(kRate) * kDurationSeconds);
            if (report.durationReached && !report.cancelled && !stoppedByError && !report.watchdogTimedOut)
                report.diagnosticDisposition = "COMPLETED";
        }
    }

    if (started) {
        const HRESULT hr = client->Stop();
        report.stopHresult = Hresult(hr);
        stopSucceeded = hr == S_OK;
        if (!stopSucceeded) fail("AUDIOCLIENT_STOP", FAILED(hr) ? hr : E_FAIL, true);
    }
    if (g_cancelEvent.load(std::memory_order_acquire) != nullptr) {
        SetConsoleCtrlHandler(ConsoleControl, FALSE);
        g_cancelEvent.store(nullptr, std::memory_order_release);
    }
    if (render != nullptr) { render->Release(); render = nullptr; }
    if (client != nullptr) { client->Release(); client = nullptr; report.clientReleased = true; }
    else report.clientReleased = true;
    if (device != nullptr) { device->Release(); device = nullptr; }
    if (enumerator != nullptr) { enumerator->Release(); enumerator = nullptr; }
    if (eventHandle != nullptr) {
        const BOOL closed = CloseHandle(eventHandle);
        report.eventCloseHresult = closed ? Hresult(S_OK) : Hresult(HRESULT_FROM_WIN32(GetLastError()));
        report.eventHandleClosed = closed != FALSE;
        if (!closed) fail("CLOSE_AUDIO_EVENT", HRESULT_FROM_WIN32(GetLastError()), true);
    } else report.eventHandleClosed = true;
    if (cancelHandle != nullptr) {
        const BOOL closed = CloseHandle(cancelHandle);
        report.cancelEventCloseHresult = closed ? Hresult(S_OK) : Hresult(HRESULT_FROM_WIN32(GetLastError()));
        report.cancelEventClosed = closed != FALSE;
        if (!closed) fail("CLOSE_CANCEL_EVENT", HRESULT_FROM_WIN32(GetLastError()), true);
    } else { report.cancelEventCloseHresult = Hresult(S_OK); report.cancelEventClosed = true; }
    if (comInitialized) CoUninitialize();

    if (stageFailure.empty() && report.diagnosticDisposition == "COMPLETED") report.errorStage = "NONE";
    if (started && stopSucceeded && report.eventHandleClosed && report.cancelEventClosed && report.clientReleased && !bufferLeaseUnknown)
        report.cleanupState = "CONFIRMED";
    else if (!started && report.eventHandleClosed && report.cancelEventClosed && report.clientReleased && !bufferLeaseUnknown)
        report.cleanupState = "CONFIRMED_NOT_STARTED";
    else report.cleanupState = "UNKNOWN";
    if (report.cleanupState == "UNKNOWN") {
        report.diagnosticDisposition = "UNKNOWN";
        if (report.errorStage == "NONE") report.errorStage = "CLEANUP_UNCERTAIN";
    }

    DWORD writeError = ERROR_SUCCESS;
    const std::string json = ToJson(report);
    if (!WriteNewJson(options.outputPath, json, writeError)) {
        std::fwprintf(stderr, L"Evidence save failed (Win32 %lu); operation=%hs; output path was not overwritten: %ls\n",
            static_cast<unsigned long>(writeError), report.operationId.c_str(), options.outputPath.c_str());
        return 20;
    }
    std::printf("EVIDENCE=%ls\nDISPOSITION=%s\nCLEANUP=%s\nEVENTS=%u\nFRAMES=%llu\n",
        options.outputPath.c_str(), report.diagnosticDisposition.c_str(), report.cleanupState.c_str(), eventCount,
        static_cast<unsigned long long>(framesProcessed));
    return report.diagnosticDisposition == "COMPLETED" && report.cleanupState == "CONFIRMED" ? 0 :
        report.diagnosticDisposition == "CANCELLED" ? 6 : report.diagnosticDisposition == "UNKNOWN" ? 7 : 5;
}
} // namespace

int wmain(int argc, wchar_t** argv) {
    using namespace WasapiEventDiagnostic;
    const Options options = ParseArguments(argc, argv);
    if (options.command == Command::Help) { PrintHelp(); return 0; }
    if (options.command == Command::SelfTest) { std::puts("SELF_TEST_ONLY: no COM or WASAPI call"); return 0; }
    if (options.command != Command::Run) {
        std::fwprintf(stderr, L"%ls\n", options.error.empty() ? L"Invalid arguments." : options.error.c_str());
        return 2;
    }
    if (!std::filesystem::path(options.outputPath).is_absolute()) {
        std::fwprintf(stderr, L"--output-json must be an absolute path.\n");
        return 2;
    }
    return Run(options);
}
