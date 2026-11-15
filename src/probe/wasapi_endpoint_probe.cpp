// Ported from an upstream read-only endpoint probe; private repository provenance is retained offline.
// Source blob 3c1fd2946bfe9c0bd62d27132002455bca075557, 2026-10-29.
// Active stream checks require the explicit --test-stream switch and never store audio.
#define INITGUID
#include <windows.h>
#include <bcrypt.h>
#include <audioclient.h>
#include <avrt.h>
#include <devpkey.h>
#include <propkeydef.h>
#include <functiondiscoverykeys_devpkey.h>
#include <mmdeviceapi.h>
#include <mmreg.h>
#include <propsys.h>
#include <setupapi.h>
#include <ks.h>
#include <ksmedia.h>
#include "clock_analysis.h"
#include "analog_loopback.h"
#include "live_transport.h"
#include "monitor_latency.h"
#include "monitor_rtl_preflight.h"
#include "capture_runtime_evidence.h"
#include "probe_math.h"
#include "signal_levels.h"
#include "exclusive_format_matrix.h"
#include "channel_routing.h"
#include "matrix_timestamp.h"
#include "wasapi_format_matrix_api.h"
#include "channel_meter.h"
#include "render_signal.h"
#include "render_channel_report.h"
#include "stream_validation_report.h"
#include "duplex_validation_report.h"
#include "render_channel_lifecycle.h"

#include <algorithm>
#include <array>
#include <atomic>
#include <cstdint>
#include <chrono>
#include <cmath>
#include <cstdio>
#include <cwchar>
#include <exception>
#include <limits>
#include <mutex>
#include <new>
#include <string>
#include <thread>
#include <condition_variable>
#include <vector>

#ifndef PROBE_VERSION
#define PROBE_VERSION "unknown"
#endif
#include <string_view>
#include <vector>

namespace {

constexpr std::size_t kMaximumLoopbackClockSnapshots = 8'192;

volatile LONG g_streamCancelRequested = 0;
std::atomic<HANDLE> g_duplexStopEvent{nullptr};
class DuplexCoordinator;
std::atomic<DuplexCoordinator*> g_activeDuplexCoordinator{nullptr};
BOOL WINAPI HandleStreamConsoleControl(DWORD controlType);

template <typename T>
void Release(T*& value) {
    if (value != nullptr) value->Release();
    value = nullptr;
}

std::string Utf8(const wchar_t* value) {
    if (value == nullptr || *value == L'\0') return {};
    const int size = WideCharToMultiByte(CP_UTF8, 0, value, -1, nullptr, 0, nullptr, nullptr);
    if (size <= 1) return {};
    std::string result(static_cast<std::size_t>(size), '\0');
    WideCharToMultiByte(CP_UTF8, 0, value, -1, result.data(), size, nullptr, nullptr);
    result.resize(static_cast<std::size_t>(size - 1));
    return result;
}

std::string Hresult(HRESULT value) {
    char text[32]{};
    std::snprintf(text, sizeof(text), "0x%08lX", static_cast<unsigned long>(value));
    return text;
}

std::string GuidText(REFGUID guid) {
    wchar_t text[40]{};
    if (StringFromGUID2(guid, text, static_cast<int>(std::size(text))) == 0) return "(invalid GUID)";
    return Utf8(text);
}

std::string StateText(DWORD state) {
    std::string value;
    const auto add = [&](DWORD bit, const char* name) {
        if ((state & bit) == 0) return;
        if (!value.empty()) value += '|';
        value += name;
    };
    add(DEVICE_STATE_ACTIVE, "ACTIVE");
    add(DEVICE_STATE_DISABLED, "DISABLED");
    add(DEVICE_STATE_NOTPRESENT, "NOTPRESENT");
    add(DEVICE_STATE_UNPLUGGED, "UNPLUGGED");
    return value.empty() ? "UNKNOWN" : value;
}

std::string PropertyString(IPropertyStore* store, REFPROPERTYKEY key) {
    PROPVARIANT value;
    PropVariantInit(&value);
    std::string result = "(unavailable)";
    if (SUCCEEDED(store->GetValue(key, &value))) {
        if (value.vt == VT_LPWSTR && value.pwszVal != nullptr) result = Utf8(value.pwszVal);
        else if (value.vt == VT_BSTR && value.bstrVal != nullptr) result = Utf8(value.bstrVal);
    }
    PropVariantClear(&value);
    return result;
}

std::string DevicePropertyString(HDEVINFO set, SP_DEVINFO_DATA& info, const DEVPROPKEY& key) {
    std::array<wchar_t, 1024> value{};
    DEVPROPTYPE type = 0;
    if (!SetupDiGetDevicePropertyW(set, &info, &key, &type,
            reinterpret_cast<PBYTE>(value.data()), static_cast<DWORD>(sizeof(value)), nullptr, 0) ||
        type != DEVPROP_TYPE_STRING) return "(unavailable)";
    return Utf8(value.data());
}

std::wstring WideFromUtf8(const std::string& value) {
    if (value.empty()) return {};
    const int count = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, value.c_str(), -1, nullptr, 0);
    if (count <= 1) return {};
    std::wstring result(static_cast<std::size_t>(count), L'\0');
    MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, value.c_str(), -1, result.data(), count);
    result.resize(static_cast<std::size_t>(count - 1));
    return result;
}

void PrintDriverInfo(IPropertyStore* properties) {
    const auto instanceId = PropertyString(properties, PKEY_Device_InstanceId);
    std::printf("  PnP instance: %s\n", instanceId.c_str());
    if (instanceId == "(unavailable)" || instanceId.empty()) return;
    const auto wideInstanceId = WideFromUtf8(instanceId);
    if (wideInstanceId.empty()) return;
    HDEVINFO set = SetupDiCreateDeviceInfoList(nullptr, nullptr);
    if (set == INVALID_HANDLE_VALUE) return;
    SP_DEVINFO_DATA info{};
    info.cbSize = sizeof(info);
    if (SetupDiOpenDeviceInfoW(set, wideInstanceId.c_str(), nullptr, 0, &info)) {
        std::printf("  Driver provider: %s\n", DevicePropertyString(set, info, DEVPKEY_Device_DriverProvider).c_str());
        std::printf("  Driver version: %s\n", DevicePropertyString(set, info, DEVPKEY_Device_DriverVersion).c_str());
        std::printf("  Device manufacturer: %s\n", DevicePropertyString(set, info, DEVPKEY_Device_Manufacturer).c_str());
    } else {
        std::printf("  Driver metadata: SetupAPI could not open endpoint PnP instance (%s)\n",
                    Hresult(HRESULT_FROM_WIN32(GetLastError())).c_str());
    }
    SetupDiDestroyDeviceInfoList(set);
}

void PrintFormat(const char* label, const WAVEFORMATEX* format, std::size_t availableBytes) {
    if (format == nullptr) {
        std::printf("  %s: unavailable\n", label);
        return;
    }
    std::printf("  %s: tag=0x%04X channels=%u rate=%lu avgBytesPerSec=%lu blockAlign=%u "
                "containerBits=%u cbSize=%u descriptorBytes=%zu\n",
        label, format->wFormatTag, format->nChannels, static_cast<unsigned long>(format->nSamplesPerSec),
        static_cast<unsigned long>(format->nAvgBytesPerSec), format->nBlockAlign,
        format->wBitsPerSample, format->cbSize, availableBytes);
    if (format->wFormatTag == WAVE_FORMAT_EXTENSIBLE &&
        format->cbSize >= sizeof(WAVEFORMATEXTENSIBLE) - sizeof(WAVEFORMATEX) &&
        availableBytes >= sizeof(WAVEFORMATEXTENSIBLE)) {
        const auto* extended = reinterpret_cast<const WAVEFORMATEXTENSIBLE*>(format);
        std::printf("    validBits=%u channelMask=0x%08lX subtype=%s\n",
            extended->Samples.wValidBitsPerSample,
            static_cast<unsigned long>(extended->dwChannelMask),
            GuidText(extended->SubFormat).c_str());
    }
    const auto* descriptorBytes = reinterpret_cast<const unsigned char*>(format);
    std::printf("    descriptorHex=");
    const auto dumpBytes = std::min<std::size_t>(availableBytes, 256);
    for (std::size_t index = 0; index < dumpBytes; ++index) {
        std::printf("%02X", static_cast<unsigned>(descriptorBytes[index]));
    }
    std::puts(availableBytes > dumpBytes ? "..." : "");
}

WAVEFORMATEXTENSIBLE MakeFormat(unsigned rate, unsigned channels, unsigned bits,
                                bool floatingPoint, bool extensible, unsigned validBits = 0) {
    WAVEFORMATEXTENSIBLE result{};
    auto& wave = result.Format;
    wave.wFormatTag = extensible ? WAVE_FORMAT_EXTENSIBLE
        : floatingPoint ? WAVE_FORMAT_IEEE_FLOAT : WAVE_FORMAT_PCM;
    wave.nChannels = static_cast<WORD>(channels);
    wave.nSamplesPerSec = rate;
    wave.wBitsPerSample = static_cast<WORD>(bits);
    wave.nBlockAlign = static_cast<WORD>(channels * (bits / 8));
    wave.nAvgBytesPerSec = rate * wave.nBlockAlign;
    wave.cbSize = extensible ? static_cast<WORD>(sizeof(WAVEFORMATEXTENSIBLE) - sizeof(WAVEFORMATEX)) : 0;
    if (extensible) {
        result.Samples.wValidBitsPerSample = static_cast<WORD>(validBits == 0 ? bits : validBits);
        result.dwChannelMask = channels == 1 ? SPEAKER_FRONT_CENTER
            : SPEAKER_FRONT_LEFT | SPEAKER_FRONT_RIGHT;
        result.SubFormat = floatingPoint ? KSDATAFORMAT_SUBTYPE_IEEE_FLOAT : KSDATAFORMAT_SUBTYPE_PCM;
    }
    return result;
}

WAVEFORMATEXTENSIBLE MakeInitFormat(unsigned rate, unsigned channels, const std::wstring& format) {
    if (format == L"pcm16") return MakeFormat(rate, channels, 16, false, true);
    if (format == L"pcm24-in-32") return MakeFormat(rate, channels, 32, false, true, 24);
    return MakeFormat(rate, channels, 32, true, true);
}

ChannelMeter::FormatTopology MeterTopology(const WAVEFORMATEX* format, std::size_t bytes) {
    using ChannelMeter::SpeakerLabel;
    ChannelMeter::FormatTopology topology;
    if (format == nullptr || bytes < sizeof(WAVEFORMATEX)) return topology;
    topology.available = true;
    topology.sampleRateHz = format->nSamplesPerSec;
    topology.reportedChannels = format->nChannels;
    topology.containerBits = format->wBitsPerSample;
    topology.validBits = format->wBitsPerSample;
    topology.labelCount = static_cast<std::uint16_t>(std::min<std::uint32_t>(
        format->nChannels, ChannelMeter::kMaximumChannels));
    bool pcm = format->wFormatTag == WAVE_FORMAT_PCM;
    bool floatingPoint = format->wFormatTag == WAVE_FORMAT_IEEE_FLOAT;
    const WAVEFORMATEXTENSIBLE* extended = nullptr;
    if (format->wFormatTag != WAVE_FORMAT_EXTENSIBLE ||
        format->cbSize < sizeof(WAVEFORMATEXTENSIBLE) - sizeof(WAVEFORMATEX) ||
        bytes < sizeof(WAVEFORMATEXTENSIBLE)) {
        if (pcm && format->wBitsPerSample == 16)
            topology.encoding = ChannelMeter::FormatTopology::Encoding::Pcm16;
        else if (floatingPoint && format->wBitsPerSample == 32)
            topology.encoding = ChannelMeter::FormatTopology::Encoding::Float32;
        return topology;
    }
    extended = reinterpret_cast<const WAVEFORMATEXTENSIBLE*>(format);
    topology.validBits = extended->Samples.wValidBitsPerSample;
    pcm = IsEqualGUID(extended->SubFormat, KSDATAFORMAT_SUBTYPE_PCM);
    floatingPoint = IsEqualGUID(extended->SubFormat, KSDATAFORMAT_SUBTYPE_IEEE_FLOAT);
    if (pcm && format->wBitsPerSample == 16) topology.encoding = ChannelMeter::FormatTopology::Encoding::Pcm16;
    else if (pcm && format->wBitsPerSample == 32 && topology.validBits == 24)
        topology.encoding = ChannelMeter::FormatTopology::Encoding::Pcm24In32;
    else if (floatingPoint && format->wBitsPerSample == 32)
        topology.encoding = ChannelMeter::FormatTopology::Encoding::Float32;
    if (extended->dwChannelMask == 0) return topology;
    topology.channelMaskAvailable = true;
    topology.channelMask = extended->dwChannelMask;
    constexpr std::array<std::pair<DWORD, SpeakerLabel>, 18> labels{{
        {SPEAKER_FRONT_LEFT, SpeakerLabel::FrontLeft}, {SPEAKER_FRONT_RIGHT, SpeakerLabel::FrontRight},
        {SPEAKER_FRONT_CENTER, SpeakerLabel::FrontCenter}, {SPEAKER_LOW_FREQUENCY, SpeakerLabel::LowFrequency},
        {SPEAKER_BACK_LEFT, SpeakerLabel::BackLeft}, {SPEAKER_BACK_RIGHT, SpeakerLabel::BackRight},
        {SPEAKER_FRONT_LEFT_OF_CENTER, SpeakerLabel::FrontLeftOfCenter},
        {SPEAKER_FRONT_RIGHT_OF_CENTER, SpeakerLabel::FrontRightOfCenter},
        {SPEAKER_BACK_CENTER, SpeakerLabel::BackCenter}, {SPEAKER_SIDE_LEFT, SpeakerLabel::SideLeft},
        {SPEAKER_SIDE_RIGHT, SpeakerLabel::SideRight}, {SPEAKER_TOP_CENTER, SpeakerLabel::TopCenter},
        {SPEAKER_TOP_FRONT_LEFT, SpeakerLabel::TopFrontLeft}, {SPEAKER_TOP_FRONT_CENTER, SpeakerLabel::TopFrontCenter},
        {SPEAKER_TOP_FRONT_RIGHT, SpeakerLabel::TopFrontRight}, {SPEAKER_TOP_BACK_LEFT, SpeakerLabel::TopBackLeft},
        {SPEAKER_TOP_BACK_CENTER, SpeakerLabel::TopBackCenter}, {SPEAKER_TOP_BACK_RIGHT, SpeakerLabel::TopBackRight}}};
    DWORD knownMask = 0;
    for (const auto& [bit, label] : labels) { (void)label; knownMask |= bit; }
    DWORD mask = extended->dwChannelMask;
    std::uint16_t count = 0;
    bool allKnown = (mask & ~knownMask) == 0;
    while (mask != 0 && count < ChannelMeter::kMaximumChannels) {
        const DWORD bit = mask & (~mask + 1);
        mask &= ~bit;
        const auto found = std::find_if(labels.begin(), labels.end(), [bit](const auto& entry) { return entry.first == bit; });
        if (found == labels.end()) allKnown = false;
        else topology.channelLabels[count] = found->second;
        ++count;
    }
    if (count != format->nChannels || count != topology.labelCount) allKnown = false;
    topology.labelCount = count == 0 ? topology.labelCount : count;
    topology.channelLabelKnown.fill(false);
    if (allKnown) for (std::uint16_t index = 0; index < count; ++index) topology.channelLabelKnown[index] = true;
    return topology;
}

void PrintChannelTopology(const char* label, const ChannelMeter::FormatTopology& topology) {
    std::printf("  %s: %s; channels=%u; channel mask=", label,
        topology.available ? "available" : "unknown", topology.reportedChannels);
    if (topology.channelMaskAvailable) std::printf("0x%08X\n", topology.channelMask);
    else std::puts("unknown");
    for (std::uint16_t index = 0; index < topology.labelCount; ++index)
        std::printf("    Windows channel %u: %s (physical connector: unknown)\n", index,
            topology.channelLabelKnown[index] ? ChannelMeter::SpeakerLabelText(topology.channelLabels[index]) : "unknown");
}

struct StreamExecution {
    bool startAttempted = false;
    bool started = false;
    bool durationReached = false;
    bool criticalError = false;
    bool stopped = false;
    bool watchdogTimedOut = false;
    bool cancelled = false;
    bool peerStopped = false;
    UINT64 intermediateWaitTimeouts = 0;
    HRESULT startResult = E_PENDING;
    HRESULT stopResult = E_PENDING;
    HRESULT captureServiceResult = E_PENDING;
    bool captureServiceAttempted = false;
    HRESULT errorResult = S_OK;
    const char* errorStage = "none";
    UINT64 events = 0;
    UINT64 packets = 0;
    UINT64 frames = 0;
    UINT64 selectedCaptureSamples = 0;
    UINT64 invalidSelectedCaptureSamples = 0;
    UINT64 prefillFrames = 0;
    UINT64 discontinuities = 0;
    UINT64 startupDiscontinuities = 0;
    UINT64 steadyDiscontinuities = 0;
    UINT64 initialCapturePackets = 0;
    UINT64 capturePacketsObserved = 0;
    UINT64 positionTransitionsChecked = 0;
    UINT64 positionTransitionsSkippedTimestamp = 0;
    UINT64 positionRebases = 0;
    UINT64 invalidDevicePositions = 0;
    UINT64 positionJumps = 0;
    UINT64 positionOverlaps = 0;
    UINT64 jumpFramesTotal = 0;
    UINT64 jumpFramesMaximum = 0;
    UINT64 overlapFramesTotal = 0;
    UINT64 overlapFramesMaximum = 0;
    UINT64 positionAnomaliesWithDiscontinuity = 0;
    UINT64 positionAnomaliesWithoutDiscontinuity = 0;
    UINT64 discontinuitiesWithoutPositionAnomaly = 0;
    UINT64 discontinuitiesWithoutPositionComparison = 0;
    UINT64 timestampErrors = 0;
    UINT64 qpcValidPackets = 0;
    UINT64 firstDevicePosition = 0;
    UINT64 firstQpcPosition = 0;
    UINT64 lastQpcPosition = 0;
    UINT64 previousDeviceEnd = 0;
    UINT64 unavailableCaptureBuffers = 0;
    UINT64 silentBuffersServiced = 0;
    UINT64 emptyCaptureBuffers = 0;
    UINT64 packetDrainLimitHits = 0;
    UINT64 longEventIntervals = 0;
    UINT64 eventIntervalsObserved = 0;
    UINT64 omittedEventIntervals = 0;
    UINT64 criticalErrors = 0;
    bool warning = false;
    bool deviceInvalidated = false;
    bool sufficientFrames = false;
    bool finalSilenceSubmitted = false;
    bool hasPreviousDevicePosition = false;
    bool initialPositionValid = false;
    bool mmcssRequested = false;
    bool mmcssRegistered = false;
    HANDLE mmcssHandle = nullptr;
    HRESULT mmcssRegistrationResult = E_PENDING;
    HRESULT mmcssRevertResult = E_PENDING;
    double elapsedMilliseconds = 0;
    double timeSinceLastEventMilliseconds = 0;
    double firstEventSeconds = 0;
    std::chrono::steady_clock::time_point startedAt{};
    std::chrono::steady_clock::time_point endedAt{};
    double eventIntervalSumMilliseconds = 0;
    double maximumEventIntervalMilliseconds = 0;
    double firstDiscontinuitySeconds = 0;
    double lastDiscontinuitySeconds = 0;
    double discontinuitySpacingSumSeconds = 0;
    double minimumDiscontinuitySpacingSeconds = 0;
    double maximumDiscontinuitySpacingSeconds = 0;
    std::array<double, 5> firstDiscontinuitySamples{};
    std::array<double, 5> lastDiscontinuitySamples{};
    std::size_t firstDiscontinuitySampleCount = 0;
    std::size_t lastDiscontinuitySampleCount = 0;
    std::vector<double> eventIntervalsMilliseconds;
};

struct DuplexSideSummary {
    bool startSucceeded = false;
    bool hasEnd = false;
    std::chrono::steady_clock::time_point startedAt{};
    std::chrono::steady_clock::time_point endedAt{};
    UINT64 frames = 0;
    UINT64 selectedCaptureSamples = 0;
    UINT64 invalidSelectedCaptureSamples = 0;
    UINT32 bufferFrames = 0;
    REFERENCE_TIME requestedPeriod = 0;
    ProbeMath::StreamStatus status = ProbeMath::StreamStatus::InitializeFailed;
    ProbeMath::StreamQuality quality = ProbeMath::StreamQuality::SevereAnomalies;
    SignalLevels::Metrics signalLevels{};
    bool classified = false;
    HRESULT formatSupportResult = E_PENDING;
    HRESULT activationResult = E_PENDING;
    HRESULT devicePeriodResult = E_PENDING;
    HRESULT firstInitializeResult = E_PENDING;
    HRESULT finalInitializeResult = E_PENDING;
    HRESULT bufferResult = E_PENDING;
    HRESULT eventResult = E_PENDING;
    HRESULT startResult = E_PENDING;
    HRESULT stopResult = E_PENDING;
    UINT64 events = 0;
    UINT64 packets = 0;
    UINT64 discontinuities = 0;
    UINT64 timestampErrors = 0;
    UINT64 serviceFaults = 0;
    UINT64 packetDrainLimitHits = 0;
    UINT64 intermediateWaitTimeouts = 0;
    UINT64 longEventIntervals = 0;
    UINT64 devicePositionAnomalies = 0;
    UINT64 unavailableCaptureBuffers = 0;
    UINT64 silentBuffersServiced = 0;
    bool peerStopped = false;
    bool watchdogTimedOut = false;
    bool deviceInvalidated = false;
    std::uint64_t eventIntervalsObserved = 0;
    std::optional<double> eventIntervalMedianMilliseconds;
    std::optional<double> eventIntervalP95Milliseconds;
    std::optional<double> eventIntervalP99Milliseconds;
    double elapsedMilliseconds = 0;
    bool initialized = false;
    bool eventReady = false;
    bool stopSucceeded = false;
    bool durationReached = false;
    bool cancelled = false;
    bool cleanupAttempted = false;
    bool cleanupUnknown = false;
    const char* errorStage = "UNKNOWN";
    HRESULT errorResult = E_PENDING;
};

class DuplexCoordinator {
public:
    explicit DuplexCoordinator(HANDLE stopEvent, LiveTransport::SpscPcm16StereoRing* liveRing = nullptr,
                               AnalogLoopback::RenderSignal* monitorLatencySignal = nullptr,
                               unsigned durationSeconds = 5)
        : stopEvent_(stopEvent), origin_(std::chrono::steady_clock::now()), liveRing_(liveRing),
          monitorLatencySignal_(monitorLatencySignal), durationSeconds_(durationSeconds) {}

    void MarkPrepared(int side, bool ready, UINT32 bufferFrames = 0) {
        {
            std::lock_guard lock(mutex_);
            if (preparedReported_[side]) return;
            preparedReported_[side] = true;
            preparedReady_[side] = ready;
            preparedBufferFrames_[side] = bufferFrames;
        }
        if (!ready) RequestStop(false);
        condition_.notify_all();
    }

    bool PreparationReported(int side) {
        std::lock_guard lock(mutex_);
        return preparedReported_[side];
    }

    bool AwaitBothPrepared() {
        std::unique_lock lock(mutex_);
        const bool completed = condition_.wait_for(lock, std::chrono::seconds(60), [&] {
            return stopRequested_.load() ||
                ProbeMath::DecideDuplexPreparation(preparedReported_[0], preparedReady_[0],
                                                    preparedReported_[1], preparedReady_[1]) !=
                    ProbeMath::DuplexPreparation::Waiting;
        });
        const auto readiness = ProbeMath::DecideDuplexPreparation(
            preparedReported_[0], preparedReady_[0], preparedReported_[1], preparedReady_[1]);
        if (!completed || stopRequested_.load() || readiness != ProbeMath::DuplexPreparation::StartTogether) {
            lock.unlock();
            RequestStop(false);
            return false;
        }
        if (liveRing_ != nullptr && !liveRing_->Configure(preparedBufferFrames_[0],
                preparedBufferFrames_[1], 48'000)) {
            lock.unlock();
            std::puts("Live transport ring configuration failed; both streams will remain stopped.");
            RequestStop(false);
            return false;
        }
        if (monitorLatencySignal_ != nullptr) {
            const auto stats = liveRing_->GetStats();
            if (stats.prefillFrames > std::numeric_limits<std::uint32_t>::max() ||
                !monitorLatencySignal_->Configure(48'000, 1,
                    static_cast<std::uint32_t>(stats.prefillFrames), durationSeconds_)) {
                lock.unlock();
                std::puts("Monitor latency marker preparation failed; both streams will remain stopped.");
                RequestStop(false);
                return false;
            }
        }
        startAuthorized_ = true;
        lock.unlock();
        condition_.notify_all();
        return true;
    }

    bool AwaitStartAuthorization() {
        std::unique_lock lock(mutex_);
        const bool completed = condition_.wait_for(lock, std::chrono::seconds(10), [&] {
            return startAuthorized_ || stopRequested_.load();
        });
        const bool authorized = completed && startAuthorized_ && !stopRequested_.load();
        lock.unlock();
        if (!completed) RequestStop(false);
        return authorized;
    }

    void RequestStop(bool operatorCancelled) {
        if (operatorCancelled) operatorCancelled_.store(true);
        stopRequested_.store(true);
        if (stopEvent_ != nullptr) SetEvent(stopEvent_);
        condition_.notify_all();
    }

    bool StopRequested() const { return stopRequested_.load(); }
    bool OperatorCancelled() const { return operatorCancelled_.load(); }

    void RecordStart(int side, bool succeeded, std::chrono::steady_clock::time_point at) {
        {
            std::lock_guard lock(mutex_);
            if (succeeded) {
                sides_[side].startSucceeded = true;
                sides_[side].startedAt = at;
            }
        }
        if (!succeeded) RequestStop(false);
    }

    void RecordFormatSupport(int side, HRESULT result) {
        std::lock_guard lock(mutex_);
        sides_[side].formatSupportResult = result;
    }

    void RecordClientActivation(int side, HRESULT result) {
        std::lock_guard lock(mutex_);
        sides_[side].activationResult = result;
    }

    void RecordDevicePeriod(int side, HRESULT result) {
        std::lock_guard lock(mutex_);
        sides_[side].devicePeriodResult = result;
    }

    void RecordInitialize(int side, HRESULT first, HRESULT final, bool succeeded) {
        std::lock_guard lock(mutex_);
        sides_[side].firstInitializeResult = first;
        sides_[side].finalInitializeResult = final;
        sides_[side].initialized = succeeded;
    }

    void RecordBufferAndEvent(int side, HRESULT buffer, UINT32 frames, HRESULT event, bool ready) {
        std::lock_guard lock(mutex_);
        sides_[side].bufferResult = buffer;
        sides_[side].bufferFrames = frames;
        sides_[side].eventResult = event;
        sides_[side].eventReady = ready;
    }

    void RecordExecution(int side, const StreamExecution& execution, double elapsedMilliseconds) {
        std::lock_guard lock(mutex_);
        auto& summary = sides_[side];
        summary.startResult = execution.startAttempted ? execution.startResult : E_PENDING;
        summary.stopResult = execution.started || execution.startAttempted ? execution.stopResult : E_PENDING;
        summary.stopSucceeded = execution.stopped;
        summary.events = execution.events;
        summary.packets = execution.packets;
        summary.frames = execution.frames;
        summary.selectedCaptureSamples = execution.selectedCaptureSamples;
        summary.invalidSelectedCaptureSamples = execution.invalidSelectedCaptureSamples;
        summary.discontinuities = execution.discontinuities;
        summary.timestampErrors = execution.timestampErrors;
        summary.elapsedMilliseconds = elapsedMilliseconds;
        summary.durationReached = execution.durationReached;
        summary.cancelled = execution.cancelled;
        summary.cleanupAttempted = true;
        summary.cleanupUnknown = !execution.stopped && execution.started;
        summary.serviceFaults = execution.criticalErrors;
        summary.packetDrainLimitHits = execution.packetDrainLimitHits;
        summary.intermediateWaitTimeouts = execution.intermediateWaitTimeouts;
        summary.longEventIntervals = execution.longEventIntervals;
        summary.devicePositionAnomalies = execution.positionJumps + execution.positionOverlaps + execution.invalidDevicePositions;
        summary.unavailableCaptureBuffers = execution.unavailableCaptureBuffers;
        summary.silentBuffersServiced = execution.silentBuffersServiced;
        summary.peerStopped = execution.peerStopped;
        summary.watchdogTimedOut = execution.watchdogTimedOut;
        summary.deviceInvalidated = execution.deviceInvalidated;
        summary.eventIntervalsObserved = execution.eventIntervalsObserved;
        if (!execution.eventIntervalsMilliseconds.empty()) {
            summary.eventIntervalMedianMilliseconds = ProbeMath::Percentile(execution.eventIntervalsMilliseconds, 50);
            summary.eventIntervalP95Milliseconds = ProbeMath::Percentile(execution.eventIntervalsMilliseconds, 95);
            summary.eventIntervalP99Milliseconds = ProbeMath::Percentile(execution.eventIntervalsMilliseconds, 99);
        }
        summary.errorStage = execution.errorStage;
        if (execution.criticalError) summary.errorResult = execution.errorResult;
    }

    void RecordError(int side, const char* stage, HRESULT result) {
        std::lock_guard lock(mutex_);
        sides_[side].errorStage = stage;
        sides_[side].errorResult = result;
    }

    void RecordCleanup(int side, bool attempted, bool unknown) {
        std::lock_guard lock(mutex_);
        sides_[side].cleanupAttempted = attempted;
        sides_[side].cleanupUnknown = unknown;
    }

    void RecordStartAttempt(int side, HRESULT result) {
        std::lock_guard lock(mutex_);
        sides_[side].startResult = result;
    }

    void RecordEnd(int side, std::chrono::steady_clock::time_point at, UINT64 frames) {
        std::lock_guard lock(mutex_);
        sides_[side].hasEnd = true;
        sides_[side].endedAt = at;
        sides_[side].frames = frames;
    }

    void RecordBufferInfo(int side, REFERENCE_TIME period, UINT32 frames) {
        std::lock_guard lock(mutex_);
        sides_[side].requestedPeriod = period;
        sides_[side].bufferFrames = frames;
    }

    void RecordClassification(int side, ProbeMath::StreamStatus status, ProbeMath::StreamQuality quality,
                              const SignalLevels::Metrics& signalLevels = {}) {
        std::lock_guard lock(mutex_);
        sides_[side].status = status;
        sides_[side].quality = quality;
        sides_[side].signalLevels = signalLevels;
        sides_[side].classified = true;
    }

    DuplexSideSummary Side(int side) {
        std::lock_guard lock(mutex_);
        return sides_[side];
    }

    double OffsetMilliseconds(std::chrono::steady_clock::time_point at) const {
        return std::chrono::duration<double, std::milli>(at - origin_).count();
    }

private:
    HANDLE stopEvent_;
    const std::chrono::steady_clock::time_point origin_;
    std::atomic<bool> stopRequested_{false};
    std::atomic<bool> operatorCancelled_{false};
    std::mutex mutex_;
    std::condition_variable condition_;
    bool preparedReported_[2]{};
    bool preparedReady_[2]{};
    bool startAuthorized_ = false;
    UINT32 preparedBufferFrames_[2]{};
    LiveTransport::SpscPcm16StereoRing* liveRing_;
    AnalogLoopback::RenderSignal* monitorLatencySignal_;
    unsigned durationSeconds_;
    DuplexSideSummary sides_[2]{};
};

BOOL WINAPI HandleStreamConsoleControl(DWORD controlType) {
    if (controlType != CTRL_C_EVENT && controlType != CTRL_BREAK_EVENT) return FALSE;
    InterlockedExchange(&g_streamCancelRequested, 1);
    if (auto* coordinator = g_activeDuplexCoordinator.load()) coordinator->RequestStop(true);
    else if (const HANDLE stopEvent = g_duplexStopEvent.load()) SetEvent(stopEvent);
    return TRUE;
}

const char* StreamStatusText(ProbeMath::StreamStatus status) {
    switch (status) {
    case ProbeMath::StreamStatus::InitializeFailed: return "INITIALIZE_FAILED";
    case ProbeMath::StreamStatus::StartFailed: return "START_FAILED";
    case ProbeMath::StreamStatus::StreamFailed: return "STREAM_FAILED";
    case ProbeMath::StreamStatus::StreamCompletedWithWarnings: return "STREAM_COMPLETED_WITH_WARNINGS";
    case ProbeMath::StreamStatus::StreamCompleted: return "STREAM_COMPLETED";
    }
    return "STREAM_FAILED";
}

const char* StreamQualityText(ProbeMath::StreamQuality quality) {
    switch (quality) {
    case ProbeMath::StreamQuality::Clean: return "CLEAN";
    case ProbeMath::StreamQuality::AnomaliesPresent: return "ANOMALIES_PRESENT";
    case ProbeMath::StreamQuality::SevereAnomalies: return "SEVERE_ANOMALIES";
    }
    return "SEVERE_ANOMALIES";
}

void PrintStreamNotStarted() {
    std::puts("Start HRESULT: (not called)");
    std::puts("Stop HRESULT: (not called)");
}

void PrintCaptureEvidence(const CaptureRuntimeEvidence::Session& session,
                          const CaptureRuntimeEvidence::Facts& facts,
                          const CaptureRuntimeEvidence::Lifecycle& lifecycle) {
    using CaptureRuntimeEvidence::FieldText;
    const auto report = CaptureRuntimeEvidence::Evaluate(session, facts);
    const char* firstFailureStage = facts.firstFailureStage.data();
    std::int32_t firstFailureHresult = facts.firstFailureHresult;
    const auto chooseFailure = [&](const CaptureRuntimeEvidence::HresultFact& fact, const char* stage) {
        if (firstFailureStage == std::string_view("none") && fact.state == CaptureRuntimeEvidence::Field::Failed) {
            firstFailureStage = stage;
            firstFailureHresult = fact.value;
        }
    };
    chooseFailure(facts.getDevice, "GetDevice");
    chooseFailure(facts.activateClient, "IAudioClient activation");
    chooseFailure(facts.devicePeriod, "GetDevicePeriod");
    chooseFailure(facts.formatSupport, "IsFormatSupported");
    if (!facts.alignmentRetryAttempted) chooseFailure(facts.firstInitialize, "Initialize");
    chooseFailure(facts.alignmentGetBufferSize, "alignment GetBufferSize");
    chooseFailure(facts.retryActivateClient, "alignment retry Activate");
    chooseFailure(facts.finalInitialize, "Initialize final attempt");
    chooseFailure(facts.actualGetBufferSize, "actual GetBufferSize");
    chooseFailure(facts.eventCreation, "CreateEvent");
    chooseFailure(facts.setEventHandle, "SetEventHandle");
    chooseFailure(facts.captureService, "GetService(IAudioCaptureClient)");
    chooseFailure(facts.start, "IAudioClient::Start");
    chooseFailure(facts.stop, "IAudioClient::Stop");
    chooseFailure(facts.eventClose, "CloseHandle(event)");
    const auto printResult = [](const char* label, const CaptureRuntimeEvidence::HresultFact& fact) {
        if (fact.state == CaptureRuntimeEvidence::Field::Succeeded ||
            fact.state == CaptureRuntimeEvidence::Field::Failed)
            std::printf("%s: %.*s (HRESULT %s)\n", label, static_cast<int>(FieldText(fact.state).size()),
                FieldText(fact.state).data(), Hresult(static_cast<HRESULT>(fact.value)).c_str());
        else
            std::printf("%s: %.*s\n", label, static_cast<int>(FieldText(fact.state).size()),
                FieldText(fact.state).data());
    };
    std::puts("\nCAPTURE RUNTIME EVIDENCE (read-only; endpoint identifier redacted)");
    std::printf("CAPTURE_ADAPTER_STATUS: %s\nSelected endpoint: %s\nSelected format: %u Hz / %u channels / PCM container %u bits, %u valid bits (%s)\n"
                "Final requested period: %llu (100 ns); requested frames: %u; actual frames: %u; geometry: %s\n",
        facts.getDevice.Succeeded() && facts.activateClient.Succeeded() ? "ACQUIRED"
            : facts.getDevice.state == CaptureRuntimeEvidence::Field::Failed ||
              facts.activateClient.state == CaptureRuntimeEvidence::Field::Failed ? "FAILED" : "NOT_STARTED",
        facts.endpointExplicitlySelected ? "EXPLICITLY_SELECTED" : "UNKNOWN",
        facts.sampleRate, static_cast<unsigned>(facts.channels), static_cast<unsigned>(facts.containerBits),
        static_cast<unsigned>(facts.validBits),
        report.exactFormat ? "EXACT PCM16 STEREO 48 KHZ" : "INVALID_OR_UNKNOWN",
        static_cast<unsigned long long>(facts.finalPeriod100ns), facts.requestedBufferFrames,
        facts.actualBufferFrames, report.actualGeometryValid ? "VALID" : "INVALID_OR_UNKNOWN");
    printResult("GetDevice HRESULT", facts.getDevice);
    printResult("IAudioClient activation HRESULT", facts.activateClient);
    printResult("GetDevicePeriod HRESULT", facts.devicePeriod);
    printResult("IsFormatSupported(EXCLUSIVE) HRESULT", facts.formatSupport);
    printResult("Initialize first attempt HRESULT", facts.firstInitialize);
    if (facts.alignmentRetryAttempted) {
        printResult("Alignment GetBufferSize HRESULT", facts.alignmentGetBufferSize);
        std::printf("Alignment frames: %u\n", facts.alignmentBufferFrames);
        printResult("Alignment retry Activate HRESULT", facts.retryActivateClient);
    }
    printResult("Initialize final attempt HRESULT", facts.finalInitialize);
    printResult("Actual GetBufferSize HRESULT", facts.actualGetBufferSize);
    printResult("Event creation HRESULT", facts.eventCreation);
    printResult("SetEventHandle HRESULT", facts.setEventHandle);
    printResult("Event close HRESULT", facts.eventClose);
    printResult("IAudioCaptureClient GetService HRESULT", facts.captureService);
    printResult("Capture Start HRESULT", facts.start);
    printResult("Capture Stop HRESULT", facts.stop);
    std::printf("Packets / session-relative frames accepted: %llu / %llu; frame coverage: %.2f%% (minimum %llu); empty/unavailable GetBuffer: %llu/%llu; silent packets/clipped samples: %llu/%llu\n"
                "First absolute device position: %s; first/last QPC: %s/%s (100 ns)\n"
                "Discontinuities / timestamp errors / position gaps / overlaps / invalid positions: %llu/%llu/%llu/%llu/%llu\n"
                "Validated packet continuity / QPC timeline / clipping: %s / %s / %s\n"
                "Duration reached: %s; cancelled: %s; watchdog: %s; device invalidated: %s\n",
        static_cast<unsigned long long>(facts.packetCount), static_cast<unsigned long long>(facts.frameCount),
        CaptureRuntimeEvidence::kExpectedFrames
            ? 100.0 * static_cast<double>(facts.frameCount) / CaptureRuntimeEvidence::kExpectedFrames : 0.0,
        static_cast<unsigned long long>(report.minimumFrames),
        static_cast<unsigned long long>(facts.emptyBufferResults),
        static_cast<unsigned long long>(facts.unavailableBufferResults),
        static_cast<unsigned long long>(facts.silentPackets),
        static_cast<unsigned long long>(facts.clippedSamples),
        facts.firstDevicePositionValid ? std::to_string(facts.firstDevicePosition).c_str() : "UNKNOWN",
        facts.qpcPositionValid ? std::to_string(facts.firstQpc100ns).c_str() : "UNKNOWN",
        facts.qpcPositionValid ? std::to_string(facts.lastQpc100ns).c_str() : "UNKNOWN",
        static_cast<unsigned long long>(facts.discontinuities), static_cast<unsigned long long>(facts.timestampErrors),
        static_cast<unsigned long long>(facts.positionGaps), static_cast<unsigned long long>(facts.positionOverlaps),
        static_cast<unsigned long long>(facts.invalidPositions),
        facts.continuityValidationAvailable ? facts.continuityClean ? "CLEAN" : "ERROR" : "UNKNOWN",
        facts.timestampValidationAvailable ? facts.timestampsClean ? "CLEAN" : "ERROR" : "UNKNOWN",
        facts.clippingValidationAvailable ? facts.noClipping ? "NONE" : "DETECTED" : "UNKNOWN",
        facts.durationReached ? "yes" : "no",
        facts.cancelled ? "yes" : "no", facts.watchdogTimedOut ? "yes" : "no",
        facts.deviceInvalidated ? "yes" : "no");
    std::printf("First API failure stage/HRESULT: %.*s / %s\n"
                "CAPTURE_INITIALIZE_STATUS: %s\nCAPTURE_START_STATUS: %s\nCAPTURE_STREAM_STATUS: %s\n"
                "CAPTURE_INTEGRITY_STATUS: %s\nCAPTURE_EVIDENCE_STATUS: %s\nCAPTURE_CLEANUP_STATUS: %s\n"
                "CAPTURE_LIFECYCLE_STATE: %s\nOUTPUT_INHIBIT_STATUS: OUTPUT_DISABLED\nRENDER_STATUS: NOT_STARTED\n"
                "HARDWARE_QA_STATUS: %s\nPHYSICAL_RTL_STATUS: NOT_MEASURED\n",
        static_cast<int>(std::char_traits<char>::length(firstFailureStage)), firstFailureStage,
        firstFailureStage != std::string_view("none")
            ? Hresult(static_cast<HRESULT>(firstFailureHresult)).c_str() : "UNKNOWN",
        FieldText(report.initialize).data(), FieldText(report.start).data(), FieldText(report.stream).data(),
        FieldText(report.integrity).data(), CaptureRuntimeEvidence::EvidenceText(report.evidence),
        FieldText(report.cleanup).data(), CaptureRuntimeEvidence::LifecycleText(lifecycle.State()),
        CaptureRuntimeEvidence::HardwareQaStatusText(CaptureRuntimeEvidence::HardwareQaReviewStatus(facts)));
}

StreamExecution RunStream(IAudioClient* client, HANDLE eventHandle, EDataFlow flow,
                          UINT32 bufferFrames, unsigned rate, unsigned durationSeconds,
                          bool useProAudioMmcss, DuplexCoordinator* duplex = nullptr,
                          int duplexSide = -1, ClockAnalysis::Series* clockSeries = nullptr,
                          LiveTransport::SpscPcm16StereoRing* liveRing = nullptr,
                          SignalLevels::Accumulator* signalLevels = nullptr,
                          AnalogLoopback::CaptureData* loopbackCapture = nullptr,
                          AnalogLoopback::RenderSignal* loopbackRender = nullptr,
                          bool denseClockSampling = false,
                          MonitorLatency::RunData* monitorLatencyData = nullptr,
                          AnalogLoopback::RenderSignal* monitorLatencySignal = nullptr,
                          MonitorRtlPreflight::CaptureData* rtlPreflight = nullptr,
                          CaptureRuntimeEvidence::Lifecycle* captureLifecycle = nullptr,
                          ChannelMeter::Meter* channelMeter = nullptr,
                          RenderSignal::Generator* renderChannelSignal = nullptr,
                          HANDLE externalCancelEvent = nullptr,
                          const ChannelRouting::Configuration* route = nullptr) {
    using Clock = std::chrono::steady_clock;
    StreamExecution result;
    RenderChannelTest::EventProtocol renderChannelProtocol;
    IAudioCaptureClient* capture = nullptr;
    IAudioRenderClient* render = nullptr;
    IAudioClock* audioClock = nullptr;
    UINT64 renderStreamFrame = 0;
    UINT64 captureSourceFrame = 0;
    std::vector<std::int16_t> latencyCaptureBuffer;
    std::vector<std::uint64_t> sourceFrameTags;
    MonitorLatency::RenderTracker renderMarkerTracker;
    const double expectedInterval = ProbeMath::ExpectedIntervalMilliseconds(bufferFrames, rate);
    const auto expectedIntervals = expectedInterval > 0
        ? static_cast<std::size_t>(durationSeconds * 1'000.0 / expectedInterval) + 64
        : 64;
    try {
        result.eventIntervalsMilliseconds.reserve(std::min<std::size_t>(expectedIntervals, 100'000));
    } catch (const std::bad_alloc&) {
        result.warning = true;
    }
    result.mmcssRequested = useProAudioMmcss;
    if (duplex == nullptr) InterlockedExchange(&g_streamCancelRequested, 0);
    const BOOL handlerRegistered = duplex == nullptr
        ? SetConsoleCtrlHandler(HandleStreamConsoleControl, TRUE) : TRUE;
    const DWORD handlerError = handlerRegistered ? ERROR_SUCCESS : GetLastError();
    const HRESULT handlerResult = handlerRegistered ? S_OK
        : (handlerError == ERROR_SUCCESS ? E_FAIL : HRESULT_FROM_WIN32(handlerError));
    std::printf("Console cancellation handler HRESULT: %s\n",
                duplex == nullptr ? Hresult(handlerResult).c_str() : "(owned by duplex coordinator)");
    if (handlerRegistered && duplex == nullptr) std::puts("Press Ctrl+C to stop the stream early.");
    HRESULT hr = handlerRegistered
        ? client->GetService(flow == eCapture ? __uuidof(IAudioCaptureClient) : __uuidof(IAudioRenderClient),
                             flow == eCapture ? reinterpret_cast<void**>(&capture)
                                              : reinterpret_cast<void**>(&render))
        : handlerResult;
    bool captureLifecycleTransitionFailed = false;
    if (flow == eCapture && handlerRegistered) {
        result.captureServiceAttempted = true;
        result.captureServiceResult = hr;
        if (hr == S_OK && captureLifecycle != nullptr &&
            !captureLifecycle->Transition(CaptureRuntimeEvidence::LifecycleState::CaptureReady))
            captureLifecycleTransitionFailed = true;
    }
    if (handlerRegistered) {
        std::printf("GetService(%s) HRESULT: %s\n", flow == eCapture ? "IAudioCaptureClient" : "IAudioRenderClient",
                    Hresult(hr).c_str());
    } else {
        std::puts("GetService HRESULT: (not called because the cancellation handler could not be installed)");
    }
    if (clockSeries != nullptr) {
        clockSeries->direction = flow == eCapture ? ClockAnalysis::Direction::Capture
                                                  : ClockAnalysis::Direction::Render;
        try {
            clockSeries->snapshots.reserve(denseClockSampling ? kMaximumLoopbackClockSnapshots
                                                               : ClockAnalysis::kMaximumSnapshots);
            clockSeries->storageReady = true;
        } catch (const std::bad_alloc&) {
            clockSeries->storageReady = false;
        }
        clockSeries->serviceHresult = client->GetService(__uuidof(IAudioClock),
            reinterpret_cast<void**>(&audioClock));
        if (clockSeries->serviceHresult == ClockAnalysis::kSOk && audioClock == nullptr)
            clockSeries->serviceHresult = E_POINTER;
        if (clockSeries->serviceHresult == ClockAnalysis::kSOk) {
            clockSeries->frequencyHresult = audioClock->GetFrequency(&clockSeries->frequency);
        }
        const auto frequencyResult = clockSeries->serviceHresult == ClockAnalysis::kSOk
            ? Hresult(static_cast<HRESULT>(clockSeries->frequencyHresult)) : "NOT_ATTEMPTED";
        std::printf("IAudioClock GetService HRESULT: %s\nIAudioClock GetFrequency HRESULT: %s; frequency: %llu\n"
                    "Clock snapshot storage: %s\n",
            Hresult(static_cast<HRESULT>(clockSeries->serviceHresult)).c_str(),
            frequencyResult.c_str(),
            static_cast<unsigned long long>(clockSeries->frequency),
            clockSeries->storageReady ? "reserved before Start" : "unavailable");
    }
    const auto fail = [&](const char* stage, HRESULT error) {
        ++result.criticalErrors;
        if (!result.criticalError) {
            result.errorStage = stage;
            result.errorResult = error;
        }
        result.criticalError = true;
        if (error == AUDCLNT_E_DEVICE_INVALIDATED) result.deviceInvalidated = true;
        if (duplex != nullptr && ProbeMath::ShouldStopDuplexPeer(true, false, false))
            duplex->RequestStop(false);
    };
    if (captureLifecycleTransitionFailed) fail("Capture lifecycle readiness", E_UNEXPECTED);
    if (monitorLatencyData != nullptr) {
        try {
            if (flow == eCapture) latencyCaptureBuffer.resize(static_cast<std::size_t>(bufferFrames) * 2);
            else sourceFrameTags.resize(bufferFrames);
        } catch (const std::bad_alloc&) {
            fail("monitor latency staging/provenance storage", E_OUTOFMEMORY);
        }
        renderMarkerTracker.Reset();
    }
    if (denseClockSampling && clockSeries != nullptr &&
        (!clockSeries->storageReady || clockSeries->serviceHresult != ClockAnalysis::kSOk ||
         clockSeries->frequencyHresult != ClockAnalysis::kSOk || clockSeries->frequency == 0)) {
        const HRESULT clockError = !clockSeries->storageReady ? E_OUTOFMEMORY
            : clockSeries->serviceHresult != ClockAnalysis::kSOk
                ? static_cast<HRESULT>(clockSeries->serviceHresult)
                : clockSeries->frequencyHresult != ClockAnalysis::kSOk
                    ? static_cast<HRESULT>(clockSeries->frequencyHresult) : E_FAIL;
        fail("loopback IAudioClock preparation", clockError);
    }
    const auto recordDiscontinuityTime = [&](double seconds) {
        if (result.discontinuities == 1) {
            result.firstDiscontinuitySeconds = seconds;
            result.firstDiscontinuitySamples[0] = seconds;
            result.firstDiscontinuitySampleCount = 1;
        } else {
            const double spacing = seconds - result.lastDiscontinuitySeconds;
            result.discontinuitySpacingSumSeconds += spacing;
            result.minimumDiscontinuitySpacingSeconds = result.discontinuities == 2
                ? spacing : std::min(result.minimumDiscontinuitySpacingSeconds, spacing);
            result.maximumDiscontinuitySpacingSeconds = std::max(result.maximumDiscontinuitySpacingSeconds, spacing);
            if (result.firstDiscontinuitySampleCount < result.firstDiscontinuitySamples.size()) {
                result.firstDiscontinuitySamples[result.firstDiscontinuitySampleCount++] = seconds;
            }
        }
        result.lastDiscontinuitySeconds = seconds;
        result.lastDiscontinuitySamples[(result.discontinuities - 1) % result.lastDiscontinuitySamples.size()] = seconds;
    };

    if (!result.criticalError && hr == S_OK && flow == eRender) {
        BYTE* data = nullptr;
        hr = render->GetBuffer(bufferFrames, &data);
        std::printf("Render prefill GetBuffer HRESULT: %s\n", Hresult(hr).c_str());
        if (hr == S_OK) {
            if (renderChannelSignal != nullptr && data != nullptr)
                std::fill_n(reinterpret_cast<std::int16_t*>(data),
                    static_cast<std::size_t>(bufferFrames) * renderChannelSignal->Channels(), std::int16_t{0});
            else if (renderChannelSignal != nullptr) {
                hr = E_POINTER;
                render->ReleaseBuffer(bufferFrames, AUDCLNT_BUFFERFLAGS_SILENT);
            }
            if (hr == S_OK) hr = render->ReleaseBuffer(bufferFrames,
                renderChannelSignal == nullptr ? AUDCLNT_BUFFERFLAGS_SILENT : 0);
            std::printf("Render prefill ReleaseBuffer(silence) HRESULT: %s\n", Hresult(hr).c_str());
            if (hr == S_OK) {
                result.prefillFrames += bufferFrames;
                renderStreamFrame = result.prefillFrames;
            } else fail("render prefill ReleaseBuffer", hr);
        } else fail("render prefill GetBuffer", hr);
    } else if (hr != S_OK) {
        fail(handlerRegistered ? "GetService" : "SetConsoleCtrlHandler", hr);
    }

    if (externalCancelEvent != nullptr && WaitForSingleObject(externalCancelEvent, 0) == WAIT_OBJECT_0)
        result.cancelled = true;
    if (InterlockedCompareExchange(&g_streamCancelRequested, 0, 0) != 0) result.cancelled = true;
    if (!result.criticalError && !result.cancelled && useProAudioMmcss) {
        DWORD taskIndex = 0;
        result.mmcssHandle = AvSetMmThreadCharacteristicsW(L"Pro Audio", &taskIndex);
        const DWORD mmcssError = result.mmcssHandle == nullptr ? GetLastError() : ERROR_SUCCESS;
        result.mmcssRegistrationResult = result.mmcssHandle != nullptr ? S_OK
            : (mmcssError == ERROR_SUCCESS ? E_FAIL : HRESULT_FROM_WIN32(mmcssError));
        result.mmcssRegistered = result.mmcssHandle != nullptr;
        if (result.mmcssRegistered) {
            result.mmcssRevertResult = E_PENDING;
        } else {
            fail("AvSetMmThreadCharacteristicsW", result.mmcssRegistrationResult);
        }
    } else if (!useProAudioMmcss) {
        std::puts("MMCSS: off (registration not requested)");
    } else {
        std::puts("MMCSS Pro Audio registration: not attempted before Start");
    }
    if (externalCancelEvent != nullptr && WaitForSingleObject(externalCancelEvent, 0) == WAIT_OBJECT_0)
        result.cancelled = true;
    if (!result.criticalError && InterlockedCompareExchange(&g_streamCancelRequested, 0, 0) != 0) {
        result.cancelled = true;
    }
    bool startReady = ProbeMath::CanStartStream(!result.criticalError, result.cancelled,
                                                 useProAudioMmcss, result.mmcssRegistered);
    if (duplex != nullptr) {
        duplex->MarkPrepared(duplexSide, startReady, bufferFrames);
        if (startReady && !duplex->AwaitStartAuthorization()) {
            result.cancelled = duplex->OperatorCancelled();
            result.peerStopped = !result.cancelled;
            startReady = false;
        }
        if (duplex->StopRequested() && !result.cancelled) {
            result.peerStopped = true;
            startReady = false;
        }
    }
    Clock::time_point startCompletedAt{};
    if (startReady && ProbeMath::CanStartStream(!result.criticalError, result.cancelled,
                                                useProAudioMmcss, result.mmcssRegistered)) {
        result.startAttempted = true;
        result.startResult = client->Start();
        result.started = result.startResult == S_OK;
        if (flow == eCapture && captureLifecycle != nullptr && result.started &&
            !captureLifecycle->Transition(CaptureRuntimeEvidence::LifecycleState::Capturing))
            fail("Capture lifecycle start", E_UNEXPECTED);
        startCompletedAt = Clock::now();
        std::printf("Start HRESULT: %s\n", Hresult(result.startResult).c_str());
        if (duplex != nullptr) duplex->RecordStartAttempt(duplexSide, result.startResult);
        if (duplex != nullptr) duplex->RecordStart(duplexSide, result.started, startCompletedAt);
        if (!result.started) fail("Start", result.startResult);
    } else if (!result.startAttempted) {
        PrintStreamNotStarted();
    }

    if (result.started) {
        const auto startedAt = startCompletedAt;
        result.startedAt = startedAt;
        const auto deadline = startedAt + std::chrono::seconds(durationSeconds);
        auto lastEventAt = startedAt;
        auto nextClockSampleAt = startedAt + std::chrono::milliseconds(250);
        auto previousEventAt = Clock::time_point{};
        while (!result.criticalError) {
            if (duplex != nullptr && duplex->StopRequested()) {
                result.cancelled = duplex->OperatorCancelled();
                result.peerStopped = !result.cancelled;
                break;
            }
            const auto now = Clock::now();
            const auto decision = ProbeMath::DecideWaitOutcome(false,
                InterlockedCompareExchange(&g_streamCancelRequested, 0, 0) != 0, false,
                std::chrono::duration_cast<std::chrono::nanoseconds>(deadline - now),
                std::chrono::duration_cast<std::chrono::nanoseconds>(now - lastEventAt),
                ProbeMath::kEventWatchdog);
            if (decision == ProbeMath::WaitDecision::Cancelled) {
                result.cancelled = true;
                break;
            }
            if (decision == ProbeMath::WaitDecision::WatchdogTimeout) {
                result.watchdogTimedOut = true;
                fail("event watchdog timeout", HRESULT_FROM_WIN32(ERROR_TIMEOUT));
                break;
            }
            if (decision == ProbeMath::WaitDecision::DeadlineReached) {
                result.durationReached = true;
                break;
            }
            const auto remaining = std::chrono::ceil<std::chrono::milliseconds>(deadline - now).count();
            const DWORD waitMilliseconds = ProbeMath::BoundedEventWaitMilliseconds(static_cast<UINT64>(remaining));
            HANDLE waitHandles[3]{};
            DWORD waitCount = 0;
            DWORD cancelIndex = MAXDWORD;
            DWORD duplexStopIndex = MAXDWORD;
            if (externalCancelEvent != nullptr) { cancelIndex = waitCount; waitHandles[waitCount++] = externalCancelEvent; }
            if (duplex != nullptr) { duplexStopIndex = waitCount; waitHandles[waitCount++] = g_duplexStopEvent.load(); }
            const DWORD audioEventIndex = waitCount;
            waitHandles[waitCount++] = eventHandle;
            const DWORD waitResult = WaitForMultipleObjects(waitCount, waitHandles, FALSE, waitMilliseconds);
            const DWORD waitError = waitResult == WAIT_FAILED ? GetLastError() : ERROR_SUCCESS;
            const auto afterWait = Clock::now();
            const bool externalCancelSignaled = cancelIndex != MAXDWORD && waitResult == WAIT_OBJECT_0 + cancelIndex;
            const bool stopSignaled = duplexStopIndex != MAXDWORD && waitResult == WAIT_OBJECT_0 + duplexStopIndex;
            const bool eventSignaled = waitResult == WAIT_OBJECT_0 + audioEventIndex;
            if (externalCancelSignaled) { result.cancelled = true; break; }
            if (stopSignaled) {
                result.cancelled = duplex->OperatorCancelled();
                result.peerStopped = !result.cancelled;
                break;
            }
            const bool apiFailure = waitResult != WAIT_TIMEOUT && !eventSignaled;
            const DWORD apiError = waitResult == WAIT_FAILED ? waitError : ERROR_INVALID_DATA;
            const auto waitDecision = ProbeMath::DecideWaitOutcome(eventSignaled,
                InterlockedCompareExchange(&g_streamCancelRequested, 0, 0) != 0, apiFailure,
                std::chrono::duration_cast<std::chrono::nanoseconds>(deadline - afterWait),
                std::chrono::duration_cast<std::chrono::nanoseconds>(afterWait - lastEventAt),
                ProbeMath::kEventWatchdog);
            if (waitDecision == ProbeMath::WaitDecision::ApiFailure) {
                fail("WaitForSingleObject", apiError == ERROR_SUCCESS ? E_FAIL : HRESULT_FROM_WIN32(apiError));
                break;
            }
            if (waitDecision == ProbeMath::WaitDecision::Cancelled) {
                result.cancelled = true;
                break;
            }
            if (waitDecision == ProbeMath::WaitDecision::WatchdogTimeout) {
                result.watchdogTimedOut = true;
                fail("event watchdog timeout", HRESULT_FROM_WIN32(ERROR_TIMEOUT));
                break;
            }
            if (waitDecision == ProbeMath::WaitDecision::DeadlineReached) {
                result.durationReached = true;
                break;
            }
            if (waitDecision == ProbeMath::WaitDecision::ContinueWaiting) {
                ++result.intermediateWaitTimeouts;
                continue;
            }

            const auto eventAt = afterWait;
            lastEventAt = eventAt;
            if (result.events == 0) result.firstEventSeconds = std::chrono::duration<double>(eventAt - startedAt).count();
            ++result.events;
            if (renderChannelSignal != nullptr && !renderChannelProtocol.BeginEvent()) {
                fail("render event protocol", E_UNEXPECTED);
                break;
            }
            if (previousEventAt != Clock::time_point{}) {
                const double interval = std::chrono::duration<double, std::milli>(eventAt - previousEventAt).count();
                ++result.eventIntervalsObserved;
                result.eventIntervalSumMilliseconds += interval;
                result.maximumEventIntervalMilliseconds = std::max(result.maximumEventIntervalMilliseconds, interval);
                if (result.eventIntervalsMilliseconds.size() < result.eventIntervalsMilliseconds.capacity()) {
                    result.eventIntervalsMilliseconds.push_back(interval);
                } else {
                    ++result.omittedEventIntervals;
                }
                if (ProbeMath::IsLongEventInterval(interval, expectedInterval)) {
                    ++result.longEventIntervals;
                    result.warning = true;
                }
            }
            previousEventAt = eventAt;
            const bool finalSilenceForThisBuffer = renderChannelSignal != nullptr &&
                !result.cancelled && !result.criticalError &&
                RenderChannelTest::ShouldSilenceForRemainingTime(
                    std::chrono::duration<double, std::milli>(deadline - eventAt).count(), expectedInterval);
            if (finalSilenceForThisBuffer) renderChannelSignal->Stop();
            if (flow == eRender) {
                BYTE* data = nullptr;
                if (renderChannelSignal != nullptr &&
                    InterlockedCompareExchange(&g_streamCancelRequested, 0, 0) != 0) {
                    result.cancelled = true;
                    renderChannelSignal->Stop();
                    renderChannelProtocol.EndEvent();
                    break;
                }
                if (renderChannelSignal != nullptr && !renderChannelProtocol.RequestBuffer()) {
                    fail("render event buffer protocol", E_UNEXPECTED);
                    break;
                }
                hr = render->GetBuffer(bufferFrames, &data);
                if (renderChannelSignal != nullptr && !renderChannelProtocol.BufferResult(hr == S_OK)) {
                    if (hr == S_OK) {
                        const HRESULT releaseHr = render->ReleaseBuffer(bufferFrames, AUDCLNT_BUFFERFLAGS_SILENT);
                        renderChannelProtocol.ReleaseResult(releaseHr == S_OK);
                        renderChannelProtocol.EndEvent();
                        if (releaseHr != S_OK) fail("render protocol recovery ReleaseBuffer", releaseHr);
                    }
                    fail("render event buffer acquisition protocol", E_UNEXPECTED);
                    break;
                }
                if (hr != S_OK) {
                    fail("render GetBuffer", hr);
                    break;
                }
                DWORD releaseFlags = AUDCLNT_BUFFERFLAGS_SILENT;
                bool markerSignalEnabled = true;
                if (liveRing != nullptr) {
                    if (data == nullptr) {
                        hr = E_POINTER;
                        fail("live render buffer pointer", hr);
                        break;
                    }
                    auto* samples = reinterpret_cast<std::int16_t*>(data);
                    auto pull = liveRing->Pull(samples, bufferFrames,
                        duplex != nullptr && duplex->StopRequested(),
                        monitorLatencyData != nullptr ? sourceFrameTags.data() : nullptr);
                    if (pull.prefillCompleted) {
                        const auto elapsed = std::chrono::duration_cast<std::chrono::microseconds>(
                            Clock::now() - startedAt).count();
                        liveRing->RecordPrefillDuration(static_cast<std::uint64_t>(std::max<std::int64_t>(0, elapsed)));
                    }
                    if (duplex != nullptr && duplex->StopRequested()) {
                        std::fill_n(samples, static_cast<std::size_t>(bufferFrames) * 2, std::int16_t{0});
                        liveRing->ForceSilenceAfterPull(pull, bufferFrames);
                    }
                    if (monitorLatencyData != nullptr &&
                        (pull.underrun || pull.overwrittenFrames != 0 || liveRing->HasTransportLoss())) {
                        std::fill_n(samples, static_cast<std::size_t>(bufferFrames) * 2, std::int16_t{0});
                        liveRing->ForceSilenceAfterPull(pull, bufferFrames, true);
                        if (duplex != nullptr) duplex->RequestStop(false);
                    }
                    releaseFlags = pull.consumedFrames == 0 ? AUDCLNT_BUFFERFLAGS_SILENT : 0;
                    if (monitorLatencyData != nullptr && monitorLatencySignal != nullptr &&
                        !duplex->StopRequested()) {
                        renderMarkerTracker.InspectBuffer(
                            {samples, static_cast<std::size_t>(bufferFrames) * 2}, sourceFrameTags,
                            renderStreamFrame, *monitorLatencySignal);
                    }
                    if (signalLevels != nullptr) {
                        signalLevels->RenderBuffer(samples, bufferFrames,
                            releaseFlags == AUDCLNT_BUFFERFLAGS_SILENT,
                            pull.prefillSilenceFrames, pull.underrunFrames);
                    }
                } else if (loopbackRender != nullptr) {
                    if (data == nullptr) {
                        hr = E_POINTER;
                        fail("analog loopback render buffer pointer", hr);
                        break;
                    }
                    markerSignalEnabled = duplex == nullptr || !duplex->StopRequested();
                    loopbackRender->Fill(reinterpret_cast<std::int16_t*>(data), bufferFrames,
                        renderStreamFrame, !markerSignalEnabled);
                    releaseFlags = 0;
                } else if (renderChannelSignal != nullptr) {
                    if (data == nullptr || renderChannelSignal->Channels() != 2) {
                        const HRESULT releaseHr = render->ReleaseBuffer(bufferFrames, AUDCLNT_BUFFERFLAGS_SILENT);
                        renderChannelProtocol.ReleaseResult(releaseHr == S_OK);
                        renderChannelProtocol.EndEvent();
                        if (releaseHr != S_OK) fail("render channel invalid-buffer ReleaseBuffer", releaseHr);
                        hr = E_POINTER;
                        fail("render channel signal buffer", hr);
                        break;
                    }
                    if (!renderChannelSignal->Fill(reinterpret_cast<std::int16_t*>(data), bufferFrames)) {
                        renderChannelSignal->Silence(reinterpret_cast<std::int16_t*>(data), bufferFrames);
                        const HRESULT releaseHr = render->ReleaseBuffer(bufferFrames, AUDCLNT_BUFFERFLAGS_SILENT);
                        renderChannelProtocol.ReleaseResult(releaseHr == S_OK);
                        renderChannelProtocol.EndEvent();
                        if (releaseHr != S_OK) fail("render channel signal ReleaseBuffer", releaseHr);
                        hr = E_INVALIDARG;
                        fail("render channel signal generation", hr);
                        break;
                    }
                    releaseFlags = 0;
                }
                LARGE_INTEGER renderSubmitBegin{}, renderSubmitEnd{};
                const bool hasRenderSubmitBegin = monitorLatencyData != nullptr &&
                    QueryPerformanceCounter(&renderSubmitBegin) != FALSE;
                hr = render->ReleaseBuffer(bufferFrames, releaseFlags);
                if (renderChannelSignal != nullptr) {
                    if (!renderChannelProtocol.ReleaseResult(hr == S_OK)) {
                        renderChannelProtocol.EndEvent();
                        fail(hr == S_OK ? "render event buffer release protocol" : "render ReleaseBuffer",
                            hr == S_OK ? E_UNEXPECTED : hr);
                        break;
                    }
                    if (hr == S_OK && finalSilenceForThisBuffer)
                        renderChannelProtocol.MarkFinalSilenceSubmitted();
                    renderChannelProtocol.EndEvent();
                }
                const bool hasRenderSubmitEnd = monitorLatencyData != nullptr &&
                    QueryPerformanceCounter(&renderSubmitEnd) != FALSE;
                if (hr != S_OK) {
                    fail(loopbackRender != nullptr ? "analog loopback render ReleaseBuffer"
                        : liveRing == nullptr ? "render ReleaseBuffer(SILENT)" : "live render ReleaseBuffer", hr);
                    break;
                }
                ++result.packets;
                result.frames += bufferFrames;
                if (releaseFlags == AUDCLNT_BUFFERFLAGS_SILENT) ++result.silentBuffersServiced;
                if (monitorLatencyData != nullptr && flow == eRender) {
                    const auto to100ns = [&](LONGLONG ticks) {
                        return MonitorLatency::QpcTicksTo100ns(static_cast<std::uint64_t>(ticks),
                            monitorLatencyData->qpcFrequency);
                    };
                    const auto submitBegin100ns = hasRenderSubmitBegin ? to100ns(renderSubmitBegin.QuadPart) : 0;
                    const auto submitEnd100ns = hasRenderSubmitEnd ? to100ns(renderSubmitEnd.QuadPart) : 0;
                    if (!renderMarkerTracker.CommitBuffer(*monitorLatencyData, liveRing->Occupancy(),
                            submitBegin100ns, submitEnd100ns)) {
                        fail("monitor latency render marker storage", E_OUTOFMEMORY);
                        break;
                    }
                    renderStreamFrame += bufferFrames;
                }
                if (loopbackRender != nullptr) {
                    loopbackRender->CommitFrames(renderStreamFrame, bufferFrames, markerSignalEnabled);
                    renderStreamFrame += bufferFrames;
                }
            } else {
                unsigned packetsThisEvent = 0;
                for (; packetsThisEvent < 64; ++packetsThisEvent) {
                    BYTE* data = nullptr;
                    UINT32 frames = 0;
                    DWORD flags = 0;
                    UINT64 devicePosition = 0;
                    UINT64 qpcPosition = 0;
                    LARGE_INTEGER capturePacketServiceQpc{};
                    hr = capture->GetBuffer(&data, &frames, &flags, &devicePosition, &qpcPosition);
                    const bool hasCapturePacketServiceQpc = monitorLatencyData != nullptr &&
                        QueryPerformanceCounter(&capturePacketServiceQpc) != FALSE;
                    if (hr == AUDCLNT_S_BUFFER_EMPTY) { ++result.emptyCaptureBuffers; break; }
                    if (hr == AUDCLNT_E_BUFFER_ERROR) {
                        ++result.unavailableCaptureBuffers;
                        result.warning = true;
                        break;
                    }
                    if (hr != S_OK || frames == 0) {
                        fail("capture GetBuffer", hr == S_OK ? E_UNEXPECTED : hr);
                        break;
                    }
                    ++result.capturePacketsObserved;
                    if (result.capturePacketsObserved == 1) ++result.initialCapturePackets;
                    const bool timestampValid = (flags & AUDCLNT_BUFFERFLAGS_TIMESTAMP_ERROR) == 0;
                    const bool dataDiscontinuity = (flags & AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY) != 0;
                    bool positionAnomaly = false;
                    bool positionValueValid = timestampValid;
                    bool positionTransitionChecked = false;
                    if (timestampValid) {
                        ++result.qpcValidPackets;
                        if (result.qpcValidPackets == 1) {
                            result.firstQpcPosition = qpcPosition;
                            if (result.capturePacketsObserved == 1) {
                                result.firstDevicePosition = devicePosition;
                                result.initialPositionValid = true;
                            }
                        }
                        result.lastQpcPosition = qpcPosition;
                        if (result.hasPreviousDevicePosition) {
                            positionTransitionChecked = true;
                            ++result.positionTransitionsChecked;
                            const auto delta = ProbeMath::CompareDevicePosition(
                                true, result.previousDeviceEnd, devicePosition, true);
                            if (delta.transition == ProbeMath::PositionTransition::Jump) {
                                ++result.positionJumps;
                                result.jumpFramesTotal += delta.frames;
                                result.jumpFramesMaximum = std::max(result.jumpFramesMaximum, delta.frames);
                                positionAnomaly = true;
                            } else if (delta.transition == ProbeMath::PositionTransition::Overlap) {
                                ++result.positionOverlaps;
                                result.overlapFramesTotal += delta.frames;
                                result.overlapFramesMaximum = std::max(result.overlapFramesMaximum, delta.frames);
                                positionAnomaly = true;
                            }
                        } else if (result.capturePacketsObserved > 1) {
                            ++result.positionRebases;
                        }
                        if (devicePosition > std::numeric_limits<UINT64>::max() - frames) {
                            ++result.invalidDevicePositions;
                            result.hasPreviousDevicePosition = false;
                            positionValueValid = false;
                            result.warning = true;
                        } else {
                            result.previousDeviceEnd = devicePosition + frames;
                            result.hasPreviousDevicePosition = true;
                        }
                    } else {
                        ++result.timestampErrors;
                        result.warning = true;
                        if (result.hasPreviousDevicePosition) ++result.positionTransitionsSkippedTimestamp;
                        result.hasPreviousDevicePosition = false;
                    }
                    if (dataDiscontinuity) {
                        ++result.discontinuities;
                        result.warning = true;
                        const double secondsSinceStart =
                            std::chrono::duration<double>(Clock::now() - startedAt).count();
                        if (secondsSinceStart <= 1.0) ++result.startupDiscontinuities;
                        else ++result.steadyDiscontinuities;
                        recordDiscontinuityTime(secondsSinceStart);
                        if (!positionValueValid || !positionTransitionChecked) {
                            ++result.discontinuitiesWithoutPositionComparison;
                        } else if (positionAnomaly) {
                            ++result.positionAnomaliesWithDiscontinuity;
                        } else {
                            ++result.discontinuitiesWithoutPositionAnomaly;
                        }
                    }
                    if (positionAnomaly && !dataDiscontinuity) {
                        ++result.positionAnomaliesWithoutDiscontinuity;
                    }
                    if (route != nullptr && flow == eCapture) {
                        const auto selectedCount=ChannelRouting::CaptureCount(route->mode);
                        for(UINT32 frame=0;frame<frames;++frame)for(std::uint16_t channel=0;channel<selectedCount;++channel){
                            const double sample=(flags&AUDCLNT_BUFFERFLAGS_SILENT)!=0?0.0:ChannelRouting::ReadSample(data,frame,route->captureChannels[channel],route->capture);
                            ++result.selectedCaptureSamples;
                            if(!std::isfinite(sample))++result.invalidSelectedCaptureSamples;
                        }
                    }
                    if (signalLevels != nullptr) {
                        signalLevels->CapturePacket(reinterpret_cast<const std::int16_t*>(data), frames,
                            (flags & AUDCLNT_BUFFERFLAGS_SILENT) != 0);
                    }
                    bool preflightReady = true;
                    if (rtlPreflight != nullptr) {
                        preflightReady = rtlPreflight->Append(reinterpret_cast<const std::int16_t*>(data), frames,
                            (flags & AUDCLNT_BUFFERFLAGS_SILENT) != 0, dataDiscontinuity,
                            (flags & AUDCLNT_BUFFERFLAGS_TIMESTAMP_ERROR) != 0, devicePosition, qpcPosition);
                    }
                    bool transportReady = true;
                    if (loopbackCapture != nullptr) {
                        transportReady = loopbackCapture->Append(reinterpret_cast<const std::int16_t*>(data), frames,
                            (flags & AUDCLNT_BUFFERFLAGS_SILENT) != 0, dataDiscontinuity,
                            (flags & AUDCLNT_BUFFERFLAGS_TIMESTAMP_ERROR) != 0,
                            devicePosition, qpcPosition);
                    }
                    if (liveRing != nullptr) {
                        liveRing->RecordCaptureFramesReceived(frames);
                        const bool silent = (flags & AUDCLNT_BUFFERFLAGS_SILENT) != 0;
                        if (duplex == nullptr || !duplex->StopRequested()) {
                            if (monitorLatencyData != nullptr && monitorLatencySignal != nullptr) {
                                LARGE_INTEGER markerGenerationBegin{}, markerGenerationEnd{};
                                const bool hasMarkerGenerationBegin =
                                    QueryPerformanceCounter(&markerGenerationBegin) != FALSE;
                                monitorLatencySignal->Fill(latencyCaptureBuffer.data(), frames,
                                    captureSourceFrame, duplex->StopRequested());
                                const bool hasMarkerGenerationEnd =
                                    QueryPerformanceCounter(&markerGenerationEnd) != FALSE;
                                LARGE_INTEGER insertBegin{}, insertEnd{};
                                const bool hasInsertBegin = QueryPerformanceCounter(&insertBegin) != FALSE;
                                transportReady = liveRing->Push(latencyCaptureBuffer.data(), frames, false,
                                    captureSourceFrame);
                                const bool hasInsertEnd = QueryPerformanceCounter(&insertEnd) != FALSE;
                                const auto insertBegin100ns = hasInsertBegin
                                    ? MonitorLatency::QpcTicksTo100ns(
                                        static_cast<std::uint64_t>(insertBegin.QuadPart),
                                        monitorLatencyData->qpcFrequency) : 0;
                                const auto insertEnd100ns = hasInsertEnd
                                    ? MonitorLatency::QpcTicksTo100ns(
                                        static_cast<std::uint64_t>(insertEnd.QuadPart),
                                        monitorLatencyData->qpcFrequency) : 0;
                                const auto packetService100ns = hasCapturePacketServiceQpc
                                    ? MonitorLatency::QpcTicksTo100ns(
                                        static_cast<std::uint64_t>(capturePacketServiceQpc.QuadPart),
                                        monitorLatencyData->qpcFrequency) : 0;
                                const auto markerGenerationBegin100ns = hasMarkerGenerationBegin
                                    ? MonitorLatency::QpcTicksTo100ns(
                                        static_cast<std::uint64_t>(markerGenerationBegin.QuadPart),
                                        monitorLatencyData->qpcFrequency) : 0;
                                const auto markerGenerationEnd100ns = hasMarkerGenerationEnd
                                    ? MonitorLatency::QpcTicksTo100ns(
                                        static_cast<std::uint64_t>(markerGenerationEnd.QuadPart),
                                        monitorLatencyData->qpcFrequency) : 0;
                                monitorLatencySignal->CommitFrames(captureSourceFrame, frames,
                                    !duplex->StopRequested());
                                const auto packetIndex = monitorLatencyData->capturePackets.size();
                                transportReady = monitorLatencyData->RecordCapturePacket({devicePosition,
                                    qpcPosition, captureSourceFrame, frames, dataDiscontinuity,
                                    !timestampValid, packetService100ns}) && transportReady;
                                const auto markerStarts = monitorLatencySignal->MarkerStarts();
                                for (std::size_t markerIndex = 0; markerIndex < markerStarts.size(); ++markerIndex) {
                                    if (markerStarts[markerIndex] < captureSourceFrame ||
                                        markerStarts[markerIndex] >= captureSourceFrame + frames) continue;
                                    const auto offset = markerStarts[markerIndex] - captureSourceFrame;
                                    const bool positionValid = devicePosition <=
                                        std::numeric_limits<UINT64>::max() - offset;
                                    transportReady = monitorLatencyData->RecordCaptureMarker({
                                        static_cast<std::uint32_t>(markerIndex), markerStarts[markerIndex],
                                        positionValid ? devicePosition + offset : 0, qpcPosition,
                                        markerGenerationBegin100ns, markerGenerationEnd100ns,
                                        insertBegin100ns, insertEnd100ns, liveRing->Occupancy(), packetIndex,
                                        timestampValid && !dataDiscontinuity && !positionAnomaly && positionValid}) &&
                                        transportReady;
                                }
                                captureSourceFrame += frames;
                            } else {
                                transportReady = (silent || data != nullptr) &&
                                    liveRing->Push(reinterpret_cast<const std::int16_t*>(data), frames, silent);
                            }
                        }
                    }
                    const bool meterReady = channelMeter == nullptr ||
                        channelMeter->Process(reinterpret_cast<const std::int16_t*>(data), frames,
                            (flags & AUDCLNT_BUFFERFLAGS_SILENT) != 0);
                    hr = capture->ReleaseBuffer(frames);
                    if (hr != S_OK) {
                        fail("capture ReleaseBuffer", hr);
                        break;
                    }
                    if (!transportReady || !preflightReady) {
                        if (loopbackCapture != nullptr) {
                            const HRESULT storageError = (flags & AUDCLNT_BUFFERFLAGS_SILENT) == 0 && data == nullptr
                                ? E_POINTER : E_OUTOFMEMORY;
                            fail("analog loopback capture storage", storageError);
                        } else if (!preflightReady) {
                            fail("Capture-only preflight packet validation/bounds", E_INVALIDARG);
                        } else {
                            fail("capture PCM16 ring transfer", E_UNEXPECTED);
                        }
                        break;
                    }
                    if (!meterReady) {
                        fail("capture PCM16 channel meter", data == nullptr ? E_POINTER : E_INVALIDARG);
                        break;
                    }
                    if(result.invalidSelectedCaptureSamples!=0){fail("selected Capture channel sample decoding",E_INVALIDARG);break;}
                    if (monitorLatencyData != nullptr &&
                        (!timestampValid || dataDiscontinuity || positionAnomaly || !positionValueValid)) {
                        if (duplex != nullptr) duplex->RequestStop(false);
                        result.warning = true;
                    }
                    if (monitorLatencyData != nullptr && liveRing->HasTransportLoss() && duplex != nullptr)
                        duplex->RequestStop(false);
                    ++result.packets;
                    result.frames += frames;
                    if (monitorLatencyData != nullptr && duplex != nullptr && duplex->StopRequested()) break;
                }
                if (packetsThisEvent == 64) {
                    ++result.packetDrainLimitHits;
                    result.warning = true;
                }
            }
            const auto afterService = Clock::now();
            if (!result.criticalError && clockSeries != nullptr && audioClock != nullptr &&
                clockSeries->storageReady && clockSeries->frequencyHresult == ClockAnalysis::kSOk &&
                clockSeries->frequency != 0 && (denseClockSampling || afterService >= nextClockSampleAt) &&
                clockSeries->snapshots.size() < (denseClockSampling ? kMaximumLoopbackClockSnapshots
                                                                    : ClockAnalysis::kMaximumSnapshots) &&
                clockSeries->snapshots.size() < clockSeries->snapshots.capacity()) {
                if (!denseClockSampling) {
                    constexpr auto samplePeriod = std::chrono::milliseconds(250);
                    const auto missedPeriods = (afterService - nextClockSampleAt) / samplePeriod + 1;
                    nextClockSampleAt += samplePeriod * missedPeriods;
                }
                ClockAnalysis::Snapshot sample{};
                sample.direction = flow == eCapture ? ClockAnalysis::Direction::Capture
                                                    : ClockAnalysis::Direction::Render;
                sample.sequence = static_cast<std::uint32_t>(clockSeries->snapshots.size() + 1);
                LARGE_INTEGER captureClockCallBegin{}, captureClockCallEnd{};
                bool captureClockCallBeginValid = false;
                bool captureClockCallEndValid = false;
                if (monitorLatencyData != nullptr && flow == eCapture)
                    captureClockCallBeginValid = QueryPerformanceCounter(&captureClockCallBegin) != FALSE;
                sample.hresult = audioClock->GetPosition(&sample.position, &sample.qpc100ns);
                if (monitorLatencyData != nullptr && flow == eCapture)
                    captureClockCallEndValid = QueryPerformanceCounter(&captureClockCallEnd) != FALSE;
                clockSeries->snapshots.push_back(sample);
                if (monitorLatencyData != nullptr && flow == eCapture) {
                    const auto beginQpc = captureClockCallBeginValid
                        ? MonitorLatency::QpcTicksTo100ns(
                            static_cast<std::uint64_t>(captureClockCallBegin.QuadPart),
                            monitorLatencyData->qpcFrequency) : 0;
                    const auto endQpc = captureClockCallEndValid
                        ? MonitorLatency::QpcTicksTo100ns(
                            static_cast<std::uint64_t>(captureClockCallEnd.QuadPart),
                            monitorLatencyData->qpcFrequency) : 0;
                    const bool validQpcBracket = sample.hresult == S_OK && beginQpc != 0 &&
                        endQpc >= beginQpc && sample.qpc100ns >= beginQpc && sample.qpc100ns <= endQpc;
                    if (!monitorLatencyData->RecordCaptureClockQpcBracket(validQpcBracket)) {
                        fail("monitor latency Capture clock bracket storage", E_OUTOFMEMORY);
                        break;
                    }
                }
            }
        }
        const auto streamEndedAt = Clock::now();
        if (!result.criticalError && !result.cancelled && !result.peerStopped && streamEndedAt >= deadline)
            result.durationReached = true;
        result.elapsedMilliseconds = std::chrono::duration<double, std::milli>(streamEndedAt - startedAt).count();
        result.timeSinceLastEventMilliseconds = std::chrono::duration<double, std::milli>(streamEndedAt - lastEventAt).count();
        if (renderChannelSignal != nullptr) renderChannelSignal->Stop();
        if (flow == eCapture && captureLifecycle != nullptr)
            captureLifecycle->Transition(CaptureRuntimeEvidence::LifecycleState::Stopping);
        result.stopResult = client->Stop();
        result.stopped = result.stopResult == S_OK;
        result.endedAt = Clock::now();
        std::printf("Stop HRESULT: %s\n", Hresult(result.stopResult).c_str());
        if (!result.stopped) {
            if (!result.criticalError) fail("Stop", result.stopResult);
            else ++result.criticalErrors;
        }
        if (duplex != nullptr) duplex->RecordEnd(duplexSide, result.endedAt, result.frames);
    }

    if (renderChannelSignal != nullptr) {
        renderChannelSignal->Stop();
        renderChannelProtocol.Finish();
        result.finalSilenceSubmitted = renderChannelProtocol.FinalSilenceSubmitted();
        if (!renderChannelProtocol.CleanPairs()) fail("render event buffer pairing", E_UNEXPECTED);
        if (result.durationReached && !result.cancelled && !result.criticalError &&
            !result.finalSilenceSubmitted) result.warning = true;
    }

    if (result.mmcssHandle != nullptr) {
        const BOOL reverted = AvRevertMmThreadCharacteristics(result.mmcssHandle);
        const DWORD revertError = reverted ? ERROR_SUCCESS : GetLastError();
        result.mmcssRevertResult = reverted ? S_OK
            : (revertError == ERROR_SUCCESS ? E_FAIL : HRESULT_FROM_WIN32(revertError));
        if (!reverted) fail("AvRevertMmThreadCharacteristics", result.mmcssRevertResult);
        result.mmcssHandle = nullptr;
    }

    if (duplex == nullptr && handlerRegistered && !SetConsoleCtrlHandler(HandleStreamConsoleControl, FALSE)) {
        const DWORD error = GetLastError();
        fail("SetConsoleCtrlHandler(remove)", error == ERROR_SUCCESS ? E_FAIL : HRESULT_FROM_WIN32(error));
    }

    Release(capture);
    Release(render);
    Release(audioClock);
    std::sort(result.eventIntervalsMilliseconds.begin(), result.eventIntervalsMilliseconds.end());
    const double averageInterval = result.eventIntervalsObserved == 0 ? 0.0
        : result.eventIntervalSumMilliseconds / result.eventIntervalsObserved;
    const auto elapsedMicroseconds = static_cast<std::uint64_t>(result.elapsedMilliseconds * 1'000.0);
    const UINT64 expectedFrames = ProbeMath::ExpectedFramesForMicroseconds(elapsedMicroseconds, rate);
    const UINT64 frameTolerance = ProbeMath::FrameTolerance(expectedFrames, bufferFrames);
    result.sufficientFrames = ProbeMath::HasSufficientFrames(result.frames, expectedFrames, bufferFrames);
    if (!result.sufficientFrames) result.warning = true;
    std::printf("Events signaled: %llu\nPackets processed after Start: %llu\nFrames processed after Start: %llu\n"
                "Render prefill frames: %llu\nExpected frames for runtime: %llu\nFrame tolerance: %llu\n"
                "Frame coverage: %.2f%%\nSufficient frame coverage: %s\n"
                "Expected event interval: %.3f ms\nEvent interval average: %.3f ms\n"
                "Event interval maximum: %.3f ms\n"
                "Event intervals observed: %llu; percentile sample: %llu; omitted: %llu\n"
                "Intervals over 150%% of expected: %llu\n"
                "Capture discontinuities: %llu (startup <=1 s: %llu; steady >1 s: %llu)\nCapture timestamp errors: %llu\n"
                "Capture buffer-unavailable events: %llu\nWarnings observed: %s\nCritical errors: %llu\nEvent watchdog timeout: %s\n"
                "Intermediate wait expirations: %llu\nTime since last event: %.3f ms\nFirst event after Start: %.3f ms\n"
                "Cancelled: %s; peer stop requested: %s\n"
                "Capture packet-drain limit hits: %llu\nRequested duration: %u s\nActual runtime: %.3f ms\nDuration reached: %s\n",
        static_cast<unsigned long long>(result.events), static_cast<unsigned long long>(result.packets),
        static_cast<unsigned long long>(result.frames), static_cast<unsigned long long>(result.prefillFrames),
        static_cast<unsigned long long>(expectedFrames), static_cast<unsigned long long>(frameTolerance),
        expectedFrames == 0 ? 0.0 : static_cast<double>(result.frames) * 100.0 / expectedFrames,
        result.sufficientFrames ? "yes" : "no", expectedInterval, averageInterval,
        result.maximumEventIntervalMilliseconds,
        static_cast<unsigned long long>(result.eventIntervalsObserved),
        static_cast<unsigned long long>(result.eventIntervalsMilliseconds.size()),
        static_cast<unsigned long long>(result.omittedEventIntervals),
        static_cast<unsigned long long>(result.longEventIntervals),
        static_cast<unsigned long long>(result.discontinuities),
        static_cast<unsigned long long>(result.startupDiscontinuities),
        static_cast<unsigned long long>(result.steadyDiscontinuities),
        static_cast<unsigned long long>(result.timestampErrors),
        static_cast<unsigned long long>(result.unavailableCaptureBuffers),
        result.warning ? "yes" : "no",
        static_cast<unsigned long long>(result.criticalErrors),
        result.watchdogTimedOut ? "yes" : "no",
        static_cast<unsigned long long>(result.intermediateWaitTimeouts),
        result.timeSinceLastEventMilliseconds, result.firstEventSeconds * 1'000.0,
        result.cancelled ? "yes" : "no", result.peerStopped ? "yes" : "no",
        static_cast<unsigned long long>(result.packetDrainLimitHits),
        durationSeconds, result.elapsedMilliseconds, result.durationReached ? "yes" : "no");
    if (result.eventIntervalsMilliseconds.empty()) {
        std::puts("Event interval median/P95/P99: unavailable (no timing samples)");
    } else {
        std::printf("Event interval median/P95/P99: %.3f / %.3f / %.3f ms\n",
            ProbeMath::Percentile(result.eventIntervalsMilliseconds, 50),
            ProbeMath::Percentile(result.eventIntervalsMilliseconds, 95),
            ProbeMath::Percentile(result.eventIntervalsMilliseconds, 99));
    }
    if (!result.mmcssRequested) {
        std::puts("MMCSS: off (not registered)");
    } else {
        std::printf("MMCSS Pro Audio registration HRESULT: %s; registered: %s\n",
            result.mmcssRegistrationResult == E_PENDING ? "(not attempted)" : Hresult(result.mmcssRegistrationResult).c_str(),
            result.mmcssRegistered ? "yes" : "no");
        if (result.mmcssRevertResult != E_PENDING) {
            std::printf("AvRevertMmThreadCharacteristics HRESULT: %s\n", Hresult(result.mmcssRevertResult).c_str());
        } else {
            std::puts("AvRevertMmThreadCharacteristics: (not called)");
        }
    }
    if (flow == eCapture) {
        const UINT64 subsequentPackets = result.capturePacketsObserved - result.initialCapturePackets;
        std::printf("Capture packets: initial %llu, subsequent %llu\n"
                    "Device position: transitions checked %llu, skipped on timestamp error %llu, rebases %llu\n"
                    "Device position jumps: %llu; positive deviation total/max: %llu/%llu frames\n"
                    "Device position overlaps: %llu; negative deviation total/max: %llu/%llu frames\n"
                    "Position anomaly correlation: with discontinuity %llu, without %llu; discontinuity without anomaly %llu, comparison unavailable %llu\n"
                    "Invalid device positions: %llu\n",
            static_cast<unsigned long long>(result.initialCapturePackets),
            static_cast<unsigned long long>(subsequentPackets),
            static_cast<unsigned long long>(result.positionTransitionsChecked),
            static_cast<unsigned long long>(result.positionTransitionsSkippedTimestamp),
            static_cast<unsigned long long>(result.positionRebases),
            static_cast<unsigned long long>(result.positionJumps),
            static_cast<unsigned long long>(result.jumpFramesTotal),
            static_cast<unsigned long long>(result.jumpFramesMaximum),
            static_cast<unsigned long long>(result.positionOverlaps),
            static_cast<unsigned long long>(result.overlapFramesTotal),
            static_cast<unsigned long long>(result.overlapFramesMaximum),
            static_cast<unsigned long long>(result.positionAnomaliesWithDiscontinuity),
            static_cast<unsigned long long>(result.positionAnomaliesWithoutDiscontinuity),
            static_cast<unsigned long long>(result.discontinuitiesWithoutPositionAnomaly),
            static_cast<unsigned long long>(result.discontinuitiesWithoutPositionComparison),
            static_cast<unsigned long long>(result.invalidDevicePositions));
        if (result.initialPositionValid) {
            std::printf("Initial packet device position: %llu frames\n",
                        static_cast<unsigned long long>(result.firstDevicePosition));
        } else if (result.initialCapturePackets != 0) {
            std::puts("Initial packet device position: unavailable (timestamp error)");
        }
        if (result.qpcValidPackets != 0) {
            std::printf("QPC position (valid packets): %llu; first/last %llu/%llu (100 ns)\n",
                static_cast<unsigned long long>(result.qpcValidPackets),
                static_cast<unsigned long long>(result.firstQpcPosition),
                static_cast<unsigned long long>(result.lastQpcPosition));
        } else {
            std::puts("QPC position: unavailable");
        }
        if (result.discontinuities != 0) {
            std::printf("Discontinuity time summary: first %.6f s, last %.6f s; intervals avg/min/max %.6f/%.6f/%.6f s\n",
                result.firstDiscontinuitySeconds, result.lastDiscontinuitySeconds,
                result.discontinuities > 1 ? result.discontinuitySpacingSumSeconds / (result.discontinuities - 1) : 0.0,
                result.minimumDiscontinuitySpacingSeconds, result.maximumDiscontinuitySpacingSeconds);
            std::printf("First discontinuity times (s):");
            for (std::size_t index = 0; index < result.firstDiscontinuitySampleCount; ++index)
                std::printf(" %.6f", result.firstDiscontinuitySamples[index]);
            std::puts("");
            const std::size_t firstLastOrdinal = std::max<std::size_t>(result.firstDiscontinuitySampleCount,
                result.discontinuities > result.lastDiscontinuitySampleCount
                    ? result.discontinuities - result.lastDiscontinuitySampleCount : 0);
            if (firstLastOrdinal < result.discontinuities) {
                std::printf("Last discontinuity times (s):");
                for (std::size_t ordinal = firstLastOrdinal; ordinal < result.discontinuities; ++ordinal)
                    std::printf(" %.6f", result.lastDiscontinuitySamples[ordinal % result.lastDiscontinuitySamples.size()]);
                std::puts("");
                const UINT64 omitted = firstLastOrdinal > result.firstDiscontinuitySampleCount
                    ? firstLastOrdinal - result.firstDiscontinuitySampleCount : 0;
                if (omitted != 0) std::printf("Middle discontinuity timestamps omitted: %llu\n", static_cast<unsigned long long>(omitted));
            }
        }
    }
    if (result.criticalError) {
        std::printf("Stream error stage: %s; HRESULT: %s\n", result.errorStage, Hresult(result.errorResult).c_str());
    }
    if (result.unavailableCaptureBuffers != 0) {
        std::printf("Capture buffer-unavailable HRESULT: %s\n", Hresult(AUDCLNT_E_BUFFER_ERROR).c_str());
    }
    if (!result.startAttempted) std::puts("Start HRESULT: (not called)");
    if (!result.started) std::puts("Stop HRESULT: (not called because Start did not succeed)");
    return result;
}

int TestInitialize(IMMDevice* device, EDataFlow flow, const std::wstring& endpointId,
                   unsigned rate, unsigned channels, const std::wstring& formatName, bool useMinimumPeriod,
                   unsigned streamDuration, bool useProAudioMmcss,
                   DuplexCoordinator* duplex = nullptr, int duplexSide = -1,
                   ClockAnalysis::Series* clockSeries = nullptr,
                   LiveTransport::SpscPcm16StereoRing* liveRing = nullptr,
                   bool measureSignalLevels = false,
                   AnalogLoopback::CaptureData* loopbackCapture = nullptr,
                   AnalogLoopback::RenderSignal* loopbackRender = nullptr,
                   MonitorLatency::RunData* monitorLatencyData = nullptr,
                   AnalogLoopback::RenderSignal* monitorLatencySignal = nullptr,
                   MonitorRtlPreflight::CaptureData* rtlPreflight = nullptr,
                   CaptureRuntimeEvidence::Facts* captureEvidence = nullptr,
                   CaptureRuntimeEvidence::Lifecycle* captureLifecycle = nullptr,
                   ChannelMeter::Meter* channelMeter = nullptr,
                          ChannelMeter::Report* channelMeterReport = nullptr,
                          RenderSignal::Generator* renderChannelSignal = nullptr,
                          RenderChannelReport::Report* renderChannelReport = nullptr,
                          StreamValidationReport::Report* streamReport = nullptr,
                          HANDLE externalCancelEvent = nullptr,
                          std::optional<ExclusiveFormatMatrix::PeriodRequest> customPeriod = std::nullopt,
                          const WAVEFORMATEXTENSIBLE* explicitFormat = nullptr,
                          const ChannelRouting::Configuration* route = nullptr) {
    if (streamReport != nullptr) {
        streamReport->direction = flow == eCapture ? "CAPTURE" : "RENDER";
        streamReport->period = customPeriod ? "CUSTOM:" + std::to_string(*customPeriod->requestedPeriod100ns) + ":" + std::to_string(*customPeriod->requestedFrames) : useMinimumPeriod ? "MINIMUM" : "DEFAULT";
        streamReport->durationSeconds = streamDuration;
        streamReport->probeVersion = PROBE_VERSION;
    }
    const unsigned containerBits = formatName == L"pcm16" ? 16U : 32U;
    const unsigned validBits = formatName == L"pcm24-in-32" ? 24U : containerBits;
    const auto format = explicitFormat != nullptr ? *explicitFormat : MakeInitFormat(rate, channels, formatName);
    if (renderChannelReport != nullptr) {
        renderChannelReport->renderChannel = renderChannelSignal->SelectedChannel() + 1;
        renderChannelReport->durationSeconds = streamDuration;
        renderChannelReport->minimumPeriod = useMinimumPeriod;
    }
    if (renderChannelReport != nullptr) {
        IMMEndpoint* endpoint = nullptr;
        EDataFlow actualFlow = eAll;
        HRESULT directionHr = device->QueryInterface(__uuidof(IMMEndpoint),
            reinterpret_cast<void**>(&endpoint));
        if (SUCCEEDED(directionHr) && endpoint != nullptr)
            directionHr = endpoint->GetDataFlow(&actualFlow);
        Release(endpoint);
        if (FAILED(directionHr) || actualFlow != eRender) {
            const HRESULT error = FAILED(directionHr) ? directionHr : E_INVALIDARG;
            renderChannelReport->lifecycleStatus = "ENDPOINT_DIRECTION_MISMATCH";
            renderChannelReport->cleanupStatus = "RELEASE_ATTEMPTED";
            renderChannelReport->errorStage = "Validate Render endpoint direction";
            renderChannelReport->errorHresult = Hresult(error);
            std::printf("Render endpoint direction validation failed: %s\n", Hresult(error).c_str());
            return 1;
        }
    }
    if (channelMeter != nullptr) {
        if (!channelMeter->Configure(channels, rate, 100)) {
            channelMeterReport->status = ChannelMeter::RunStatus::Failed;
            channelMeterReport->errorStage = "Configure meter";
            channelMeterReport->errorHresult = E_INVALIDARG;
            channelMeterReport->errorHresultKnown = true;
            return 5;
        }
        channelMeterReport->requestedDurationSeconds = streamDuration;
        channelMeterReport->minimumPeriod = useMinimumPeriod;
        channelMeter->TrySnapshot(channelMeterReport->meter);
        channelMeterReport->selectedTestFormat.available = true;
        channelMeterReport->selectedTestFormat.sampleRateHz = rate;
        channelMeterReport->selectedTestFormat.reportedChannels = static_cast<std::uint16_t>(channels);
        channelMeterReport->selectedTestFormat.containerBits = format.Format.wBitsPerSample;
        channelMeterReport->selectedTestFormat.validBits = format.Samples.wValidBitsPerSample;
        channelMeterReport->selectedTestFormat.encoding = ChannelMeter::FormatTopology::Encoding::Pcm16;
        channelMeterReport->selectedTestFormat.channelMaskAvailable = true;
        channelMeterReport->selectedTestFormat.channelMask = format.dwChannelMask;
        channelMeterReport->selectedTestFormat.labelCount = static_cast<std::uint16_t>(channels);
        if (channels == 1) {
            channelMeterReport->selectedTestFormat.channelLabels[0] = ChannelMeter::SpeakerLabel::FrontCenter;
            channelMeterReport->selectedTestFormat.channelLabelKnown[0] = true;
        } else {
            channelMeterReport->selectedTestFormat.channelLabels[0] = ChannelMeter::SpeakerLabel::FrontLeft;
            channelMeterReport->selectedTestFormat.channelLabels[1] = ChannelMeter::SpeakerLabel::FrontRight;
            channelMeterReport->selectedTestFormat.channelLabelKnown[0] = true;
            channelMeterReport->selectedTestFormat.channelLabelKnown[1] = true;
        }
    }
    if (captureEvidence != nullptr) {
        captureEvidence->sampleRate = rate;
        captureEvidence->channels = static_cast<std::uint16_t>(channels);
        captureEvidence->containerBits = static_cast<std::uint16_t>(containerBits);
        captureEvidence->validBits = static_cast<std::uint16_t>(validBits);
        if (captureLifecycle != nullptr && captureLifecycle->State() == CaptureRuntimeEvidence::LifecycleState::Idle)
            captureLifecycle->Transition(CaptureRuntimeEvidence::LifecycleState::Configured);
    }
    if (rtlPreflight != nullptr && (flow != eCapture || liveRing != nullptr || loopbackCapture != nullptr ||
        loopbackRender != nullptr || monitorLatencyData != nullptr || monitorLatencySignal != nullptr)) {
        if (captureEvidence != nullptr) {
            captureEvidence->criticalApiFailure = true;
            captureEvidence->firstFailureStage = "Capture-only route invariant";
            captureEvidence->firstFailureHresult = E_INVALIDARG;
        }
        if (captureLifecycle != nullptr) captureLifecycle->Transition(CaptureRuntimeEvidence::LifecycleState::Failed);
        std::puts("PREFLIGHT_STATUS: FAILED (Capture-only route invariant rejected before IAudioClient activation)");
        return 5;
    }
    std::string name = "(unavailable)";
    IPropertyStore* properties = nullptr;
    if (SUCCEEDED(device->OpenPropertyStore(STGM_READ, &properties)) && properties != nullptr) {
        name = PropertyString(properties, PKEY_Device_FriendlyName);
    }
    Release(properties);

    std::printf("Mode: ACTIVE %sExclusive test\nEndpoint name: %s\nEndpoint ID: %s\nDirection: %s\nRequested sample rate: %u Hz\n"
                "Channels: %u\nPhysical format: %s, %u container bits, %u valid bits\n",
        streamDuration == 0 ? "silent Initialize-only " : "stream ", name.c_str(),
        rtlPreflight != nullptr ? "(redacted)" : Utf8(endpointId.c_str()).c_str(), flow == eCapture ? "Capture" : "Render",
        rate, channels, formatName == L"pcm16" ? "PCM" : formatName == L"pcm24-in-32" ? "PCM24-in-32" : "Float32",
        containerBits, validBits);
    if (streamDuration != 0) {
        std::printf("MMCSS option: %s\n", useProAudioMmcss ? "pro-audio" : "off");
        if (monitorLatencyData != nullptr) {
            std::puts(flow == eCapture
                ? "Capture policy: WASAPI samples are ignored; deterministic markers are injected into a bounded staging buffer and tagged with Capture device positions."
                : "Render policy: only provenance-verified test PCM is submitted after ring prefill; the other channel is zero.");
        } else if (loopbackCapture != nullptr || loopbackRender != nullptr) {
            std::puts(flow == eCapture
                ? "Capture policy: selected PCM16 channel is retained in a bounded memory buffer for loopback analysis, then erased."
                : "Render policy: fixed low-level loopback markers are sent on the selected channel; the other channel is zero.");
        } else if (rtlPreflight != nullptr) {
            std::puts("Capture-only RTL preflight: bounded PCM is analyzed in memory; Ring is not configured or written and no Render endpoint is opened.");
        } else if (liveRing != nullptr) {
            std::puts(flow == eCapture
                ? "Capture policy: PCM16 stereo packets are copied to the bounded live ring; no samples are saved."
                : "Render policy: attenuated Capture PCM16 stereo is sent; startup/underflow silence is used.");
        } else if (renderChannelSignal != nullptr) {
            std::puts("Render policy: bounded low-level 997 Hz gated PCM16 tone on the selected Windows channel; other channels are explicitly zero.");
        } else {
            std::puts(flow == eCapture
                ? "Capture policy: packets are consumed for counts only; sample bytes are not saved."
                : "Render policy: only WASAPI silence is submitted.");
        }
    }

    IAudioClient* client = nullptr;
    HRESULT hr = device->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr, reinterpret_cast<void**>(&client));
    if (duplex != nullptr) duplex->RecordClientActivation(duplexSide, hr);
    if (captureEvidence != nullptr) {
        captureEvidence->activateClient.Record(hr);
        captureEvidence->clientAcquired = SUCCEEDED(hr) && client != nullptr;
    }
    if (streamReport != nullptr) {
        streamReport->activationHresult = Hresult(hr);
        if (FAILED(hr)) { streamReport->errorStage = "ACTIVATE"; streamReport->errorHresult = Hresult(hr); }
    }
    const auto releaseClient = [&]() {
        const bool owned = client != nullptr;
        Release(client);
        if (streamReport != nullptr && owned && client == nullptr)
            streamReport->cleanup = "CLIENT_RELEASE_ATTEMPTED";
        if (renderChannelReport != nullptr && renderChannelReport->cleanupStatus != "FAILED")
            renderChannelReport->cleanupStatus = owned ? "CLIENT_RELEASE_ATTEMPTED" : "CLIENT_NOT_ACQUIRED";
        if (channelMeterReport != nullptr && channelMeterReport->cleanupStatus != "FAILED")
            channelMeterReport->cleanupStatus = owned ? "RELEASE_ATTEMPTED" : "NOT_ACQUIRED";
        if (captureEvidence != nullptr && owned) captureEvidence->clientReleased = client == nullptr;
    };
    std::printf("IAudioClient activation HRESULT: %s\n", Hresult(hr).c_str());
    if (streamReport != nullptr && FAILED(hr)) { streamReport->errorStage = "ACTIVATE"; streamReport->errorHresult = Hresult(hr); }
    if (FAILED(hr) || client == nullptr) {
        if (renderChannelReport != nullptr) {
            renderChannelReport->lifecycleStatus = "INITIALIZE_FAILED";
            renderChannelReport->errorStage = "IAudioClient activation";
            renderChannelReport->errorHresult = Hresult(hr);
        }
        if (channelMeterReport != nullptr) {
            channelMeterReport->status = ChannelMeter::RunStatus::Failed;
            channelMeterReport->errorStage = "Activate IAudioClient";
            channelMeterReport->errorHresult = static_cast<std::int32_t>(hr);
            channelMeterReport->errorHresultKnown = true;
        }
        std::printf("STATUS: INITIALIZE_FAILED\nResult: IAudioClient activation failed.\n");
        if (streamDuration != 0) PrintStreamNotStarted();
        releaseClient();
        return 4;
    }

    if (renderChannelReport != nullptr) {
        WAVEFORMATEX* mix = nullptr;
        const HRESULT mixResult = client->GetMixFormat(&mix);
        if (SUCCEEDED(mixResult) && mix != nullptr) {
            renderChannelReport->endpointMixRateHz = mix->nSamplesPerSec;
            renderChannelReport->endpointMixChannels = mix->nChannels;
            renderChannelReport->endpointMixBitsPerSample = mix->wBitsPerSample;
        }
        CoTaskMemFree(mix);
    }

    if (channelMeterReport != nullptr) {
        WAVEFORMATEX* mix = nullptr;
        const HRESULT mixResult = client->GetMixFormat(&mix);
        if (SUCCEEDED(mixResult) && mix != nullptr) {
            channelMeterReport->deviceMixFormat = MeterTopology(mix, sizeof(WAVEFORMATEX) + mix->cbSize);
            PrintChannelTopology("Device mix format", channelMeterReport->deviceMixFormat);
            CoTaskMemFree(mix);
        } else {
            channelMeterReport->errorStage = "GetMixFormat";
            channelMeterReport->errorHresult = static_cast<std::int32_t>(
                SUCCEEDED(mixResult) ? E_POINTER : mixResult);
            channelMeterReport->errorHresultKnown = true;
            CoTaskMemFree(mix);
        }
    }

    REFERENCE_TIME defaultPeriod = 0;
    REFERENCE_TIME minimumPeriod = 0;
    hr = client->GetDevicePeriod(&defaultPeriod, &minimumPeriod);
    if (duplex != nullptr) duplex->RecordDevicePeriod(duplexSide, hr);
    if (streamReport != nullptr) {
        streamReport->devicePeriodHresult = Hresult(hr);
    }
    if (streamReport != nullptr && FAILED(hr)) { streamReport->errorStage = "GET_DEVICE_PERIOD"; streamReport->errorHresult = Hresult(hr); }
    if (FAILED(hr) && renderChannelReport != nullptr) {
        renderChannelReport->lifecycleStatus = "DEVICE_PERIOD_FAILED";
        renderChannelReport->errorStage = "GetDevicePeriod";
        renderChannelReport->errorHresult = Hresult(hr);
    }
    if (captureEvidence != nullptr) captureEvidence->devicePeriod.Record(hr);
    std::printf("GetDevicePeriod HRESULT: %s\nDefault Device Period: %lld (100 ns)\nMinimum Device Period: %lld (100 ns)\n",
        Hresult(hr).c_str(), static_cast<long long>(defaultPeriod), static_cast<long long>(minimumPeriod));
    const REFERENCE_TIME selectedPeriod = customPeriod ? *customPeriod->requestedPeriod100ns : ProbeMath::SelectDevicePeriod(defaultPeriod, minimumPeriod, useMinimumPeriod);
    if (channelMeterReport != nullptr) channelMeterReport->minimumPeriod = useMinimumPeriod;
    if (FAILED(hr) || selectedPeriod <= 0) {
        if (channelMeterReport != nullptr) {
            channelMeterReport->status = ChannelMeter::RunStatus::Failed;
            channelMeterReport->errorStage = SUCCEEDED(hr) ? "Validate selected device period" : "GetDevicePeriod";
            channelMeterReport->errorHresult = static_cast<std::int32_t>(hr);
            channelMeterReport->errorHresultKnown = FAILED(hr);
        }
        std::printf("IsFormatSupported HRESULT: (not called)\nInitialize HRESULT: (not called)\n"
                    "Alignment retry: no\nSTATUS: INITIALIZE_FAILED\nResult: selected device period unavailable.\n");
        if (streamDuration != 0) PrintStreamNotStarted();
        releaseClient();
        return 4;
    }

    if (customPeriod && (*customPeriod->requestedPeriod100ns < minimumPeriod || *customPeriod->requestedPeriod100ns > defaultPeriod || ProbeMath::DurationForFrames(*customPeriod->requestedFrames, rate) != *customPeriod->requestedPeriod100ns)) {
        std::printf("Custom period is outside current endpoint bounds or frame conversion.\n");
        releaseClient();
        return 4;
    }
    REFERENCE_TIME requestedPeriod = selectedPeriod;
    if (streamReport != nullptr) streamReport->requestedPeriod100ns = requestedPeriod;
    UINT64 requestedFrames = ProbeMath::FramesForDuration(requestedPeriod, rate);
    if (captureEvidence != nullptr) {
        captureEvidence->finalPeriod100ns = static_cast<std::uint64_t>(requestedPeriod);
        captureEvidence->requestedBufferFrames = static_cast<std::uint32_t>(requestedFrames);
    }
    std::printf("Selected period mode: %s\nRequested period: %lld (100 ns), approximately %llu frames\n",
        useMinimumPeriod ? "minimum" : "default",
        static_cast<long long>(requestedPeriod), static_cast<unsigned long long>(requestedFrames));

    const HRESULT support = client->IsFormatSupported(AUDCLNT_SHAREMODE_EXCLUSIVE, &format.Format, nullptr);
    if (duplex != nullptr) duplex->RecordFormatSupport(duplexSide, support);
    if (streamReport != nullptr) {
        streamReport->formatSupported = support == S_OK;
        streamReport->queryHresult = Hresult(support);
        if (support != S_OK) { streamReport->errorStage = "IS_FORMAT_SUPPORTED"; streamReport->errorHresult = Hresult(support); }
    }
    if (renderChannelReport != nullptr) renderChannelReport->formatSupportHresult = Hresult(support);
    if (channelMeterReport != nullptr) {
        channelMeterReport->formatSupportHresult = static_cast<std::int32_t>(support);
        channelMeterReport->formatSupportHresultKnown = true;
    }
    if (captureEvidence != nullptr) captureEvidence->formatSupport.Record(support);
    std::printf("IsFormatSupported(EXCLUSIVE) HRESULT: %s\n", Hresult(support).c_str());
    if (support == AUDCLNT_E_UNSUPPORTED_FORMAT) {
        if (renderChannelReport != nullptr) {
            renderChannelReport->lifecycleStatus = "UNSUPPORTED";
            renderChannelReport->errorStage = "IsFormatSupported";
            renderChannelReport->errorHresult = Hresult(support);
        }
        if (channelMeterReport != nullptr) {
            channelMeterReport->status = ChannelMeter::RunStatus::Unsupported;
            channelMeterReport->errorStage = "IsFormatSupported";
            channelMeterReport->errorHresult = static_cast<std::int32_t>(support);
            channelMeterReport->errorHresultKnown = true;
        }
        std::printf("Initialize HRESULT: (not called)\nAlignment retry: no\nSTATUS: %s\n"
                    "Result: exact physical format is not supported.\n",
                    streamDuration == 0 ? "UNSUPPORTED" : "INITIALIZE_FAILED");
        if (streamDuration != 0) PrintStreamNotStarted();
        releaseClient();
        return streamDuration == 0 ? 3 : 4;
    }
    if (support != S_OK) {
        if (renderChannelReport != nullptr) {
            renderChannelReport->lifecycleStatus = "SUPPORT_QUERY_FAILED";
            renderChannelReport->errorStage = "IsFormatSupported";
            renderChannelReport->errorHresult = Hresult(support);
        }
        if (channelMeterReport != nullptr) {
            channelMeterReport->status = ChannelMeter::RunStatus::Failed;
            channelMeterReport->errorStage = "IsFormatSupported";
            channelMeterReport->errorHresult = static_cast<std::int32_t>(support);
            channelMeterReport->errorHresultKnown = true;
        }
        std::printf("Initialize HRESULT: (not called)\nAlignment retry: no\nSTATUS: INITIALIZE_FAILED\n"
                    "Result: exact format support query failed; initialization was not attempted.\n");
        if (streamDuration != 0) PrintStreamNotStarted();
        releaseClient();
        return 4;
    }

    hr = client->Initialize(AUDCLNT_SHAREMODE_EXCLUSIVE, AUDCLNT_STREAMFLAGS_EVENTCALLBACK,
                            requestedPeriod, requestedPeriod, &format.Format, nullptr);
    const HRESULT firstInitialize = hr;
    if (streamReport != nullptr) { streamReport->firstInitializeHresult = Hresult(hr); streamReport->initializeHresult = Hresult(hr); }
    if (renderChannelReport != nullptr) renderChannelReport->firstInitializeHresult = Hresult(hr);
    if (channelMeterReport != nullptr) {
        channelMeterReport->firstInitializeHresult = static_cast<std::int32_t>(hr);
        channelMeterReport->firstInitializeHresultKnown = true;
        if (hr != AUDCLNT_E_BUFFER_SIZE_NOT_ALIGNED) {
            channelMeterReport->finalInitializeHresult = static_cast<std::int32_t>(hr);
            channelMeterReport->finalInitializeHresultKnown = true;
        }
    }
    if (captureEvidence != nullptr) captureEvidence->firstInitialize.Record(hr);
    HRESULT initializeResult = hr;
    HRESULT alignmentBufferResult = S_OK;
    UINT32 alignedFrames = 0;
    bool retried = false;
    bool retryActivationAttempted = false;
    bool alignmentAdjusted = false;
    std::printf("Initialize HRESULT (attempt 1): %s\n", Hresult(hr).c_str());
    if (hr == AUDCLNT_E_BUFFER_SIZE_NOT_ALIGNED) {
        if (captureEvidence != nullptr) captureEvidence->alignmentRetryAttempted = true;
        if (captureEvidence != nullptr) captureEvidence->finalInitialize.NotCalled();
        alignmentBufferResult = client->GetBufferSize(&alignedFrames);
        if (captureEvidence != nullptr) {
            captureEvidence->alignmentGetBufferSize.Record(alignmentBufferResult);
            captureEvidence->alignmentBufferFrames = alignedFrames;
        }
        std::printf("Alignment GetBufferSize HRESULT: %s; required frames: %u\n",
                    Hresult(alignmentBufferResult).c_str(), alignedFrames);
        releaseClient();
        if (alignmentBufferResult == S_OK && alignedFrames != 0) {
            requestedFrames = alignedFrames;
            requestedPeriod = ProbeMath::DurationForFrames(alignedFrames, rate);
            alignmentAdjusted = true;
            retryActivationAttempted = true;
            hr = device->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr, reinterpret_cast<void**>(&client));
            if (captureEvidence != nullptr && SUCCEEDED(hr) && client != nullptr) {
                captureEvidence->retryActivateClient.Record(hr);
                captureEvidence->clientAcquired = true;
                captureEvidence->clientReleased = false;
            } else if (captureEvidence != nullptr) {
                captureEvidence->retryActivateClient.Record(hr);
            }
            std::printf("IAudioClient reactivation HRESULT: %s\n", Hresult(hr).c_str());
            if (SUCCEEDED(hr) && client != nullptr) {
                retried = true;
                hr = client->Initialize(AUDCLNT_SHAREMODE_EXCLUSIVE, AUDCLNT_STREAMFLAGS_EVENTCALLBACK,
                                        requestedPeriod, requestedPeriod, &format.Format, nullptr);
                initializeResult = hr;
                if (streamReport != nullptr) streamReport->initializeHresult = Hresult(hr);
                if (renderChannelReport != nullptr)
                    renderChannelReport->alignmentRetryInitializeHresult = Hresult(hr);
                if (channelMeterReport != nullptr) {
                    channelMeterReport->alignmentRetryInitializeHresult = static_cast<std::int32_t>(hr);
                    channelMeterReport->alignmentRetryInitializeHresultKnown = true;
                    channelMeterReport->finalInitializeHresult = static_cast<std::int32_t>(hr);
                    channelMeterReport->finalInitializeHresultKnown = true;
                }
                if (captureEvidence != nullptr) captureEvidence->finalInitialize.Record(hr);
                std::printf("Initialize HRESULT (alignment retry): %s\n", Hresult(hr).c_str());
            }
        } else {
            hr = alignmentBufferResult;
        }
    }
    if (captureEvidence != nullptr) {
        if (!captureEvidence->alignmentRetryAttempted) captureEvidence->finalInitialize.Record(initializeResult);
        captureEvidence->finalPeriod100ns = static_cast<std::uint64_t>(std::max<REFERENCE_TIME>(0, requestedPeriod));
        captureEvidence->requestedBufferFrames = static_cast<std::uint32_t>(requestedFrames);
    }
    if (renderChannelReport != nullptr) renderChannelReport->initializeHresult = Hresult(initializeResult);
    if (alignmentAdjusted) {
        std::printf("Alignment changed request: %lld -> %lld (100 ns), %llu -> %u frames\n",
            static_cast<long long>(selectedPeriod), static_cast<long long>(requestedPeriod),
            static_cast<unsigned long long>(ProbeMath::FramesForDuration(selectedPeriod, rate)), alignedFrames);
    } else {
        std::printf("Final requested period: %lld (100 ns), %llu frames\n",
            static_cast<long long>(requestedPeriod), static_cast<unsigned long long>(requestedFrames));
    }
    std::printf("Alignment retry applied: %s\n", retried ? "yes" : "no");
    if (duplex != nullptr) duplex->RecordInitialize(duplexSide, firstInitialize, initializeResult,
        initializeResult == S_OK && client != nullptr);
    if (streamReport != nullptr) {
        streamReport->initializeHresult = Hresult(initializeResult);
        streamReport->initialized = initializeResult == S_OK && client != nullptr;
        if (!*streamReport->initialized) { streamReport->errorStage = "INITIALIZE"; streamReport->errorHresult = Hresult(initializeResult); }
    }
    if (hr != S_OK || client == nullptr) {
        if (streamReport != nullptr) { streamReport->initialized = false; streamReport->errorStage = "INITIALIZE"; streamReport->errorHresult = Hresult(initializeResult); }
        if (renderChannelReport != nullptr) {
            renderChannelReport->lifecycleStatus = "INITIALIZE_FAILED";
            renderChannelReport->errorStage = "Initialize";
            renderChannelReport->errorHresult = Hresult(initializeResult);
        }
        if (channelMeterReport != nullptr) {
            channelMeterReport->status = hr == AUDCLNT_E_UNSUPPORTED_FORMAT
                ? ChannelMeter::RunStatus::Unsupported : ChannelMeter::RunStatus::Failed;
            channelMeterReport->errorStage = alignmentBufferResult != S_OK ? "Alignment GetBufferSize"
                : retryActivationAttempted && !retried ? "Alignment retry Activate"
                : client == nullptr && SUCCEEDED(hr) ? "Alignment geometry" : "Initialize";
            channelMeterReport->errorHresult = static_cast<std::int32_t>(hr);
            channelMeterReport->errorHresultKnown = FAILED(hr);
        }
        std::printf("Initialize HRESULT (last Initialize call): %s\nSTATUS: INITIALIZE_FAILED\n"
                    "Result: format was reported supported, but initialization failed (first attempt %s).\n",
                    Hresult(initializeResult).c_str(), Hresult(firstInitialize).c_str());
        if (streamDuration != 0) PrintStreamNotStarted();
        releaseClient();
        if (captureLifecycle != nullptr) captureLifecycle->Transition(CaptureRuntimeEvidence::LifecycleState::Failed);
        return 4;
    }
    if (renderChannelReport != nullptr) {
        renderChannelReport->actualSampleRateHz = rate;
        renderChannelReport->actualChannels = channels;
        renderChannelReport->actualEncoding = "PCM16";
    }
    if (captureLifecycle != nullptr &&
        !captureLifecycle->Transition(CaptureRuntimeEvidence::LifecycleState::Initialized))
        captureLifecycle->Transition(CaptureRuntimeEvidence::LifecycleState::Failed);

    UINT32 actualFrames = 0;
    const HRESULT bufferResult = client->GetBufferSize(&actualFrames);
    if (streamReport != nullptr) {
        streamReport->actualBufferFramesHresult = Hresult(bufferResult);
        if (bufferResult == S_OK) {
            streamReport->actualBufferFrames = actualFrames;
            streamReport->actualBufferPeriod100ns = ProbeMath::DurationForFrames(actualFrames, rate);
        }
        else { streamReport->errorStage = "GET_BUFFER_SIZE"; streamReport->errorHresult = Hresult(bufferResult); }
    }
    if (captureEvidence != nullptr) {
        captureEvidence->actualGetBufferSize.Record(bufferResult);
        captureEvidence->actualBufferFrames = actualFrames;
    }
    std::printf("Actual GetBufferSize HRESULT: %s; ", Hresult(bufferResult).c_str());
    if (bufferResult == S_OK) std::printf("buffer frames: %u\n", actualFrames);
    else std::puts("buffer frames: unavailable");
    if (bufferResult == S_OK && rate != 0) {
        std::printf("Actual buffer period: %.3f ms\n",
                    ProbeMath::ExpectedIntervalMilliseconds(actualFrames, rate));
    }
    if (renderChannelReport != nullptr && bufferResult == S_OK) {
        renderChannelReport->actualBufferFrames = actualFrames;
        renderChannelReport->actualPeriodMicroseconds = static_cast<std::uint64_t>(
            ProbeMath::DurationForFrames(actualFrames, rate) / 10);
    }
    if (channelMeterReport != nullptr && bufferResult == S_OK) {
        channelMeterReport->actualBufferFrames = actualFrames;
        channelMeterReport->actualBufferPeriod100ns = static_cast<std::uint64_t>(
            ProbeMath::DurationForFrames(actualFrames, rate));
    }
    REFERENCE_TIME latency = 0;
    const HRESULT latencyResult = client->GetStreamLatency(&latency);
    if (SUCCEEDED(latencyResult)) {
        std::printf("GetStreamLatency HRESULT: %s; system estimate: %lld (100 ns), %.3f ms\n",
                    Hresult(latencyResult).c_str(), static_cast<long long>(latency),
                    static_cast<double>(latency) / 10'000.0);
    } else {
        std::printf("GetStreamLatency HRESULT: %s; system estimate unavailable\n", Hresult(latencyResult).c_str());
    }
    HANDLE eventHandle = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    const DWORD eventError = eventHandle == nullptr ? GetLastError() : ERROR_SUCCESS;
    const HRESULT eventCreateResult = eventHandle == nullptr
        ? (eventError == ERROR_SUCCESS ? E_FAIL : HRESULT_FROM_WIN32(eventError))
        : S_OK;
    const HRESULT eventResult = eventHandle == nullptr ? E_FAIL : client->SetEventHandle(eventHandle);
    if (streamReport != nullptr) {
        streamReport->eventCreateHresult = Hresult(eventCreateResult);
        streamReport->setEventHandleHresult = Hresult(eventResult);
        streamReport->eventReady = eventCreateResult == S_OK && eventResult == S_OK && bufferResult == S_OK && actualFrames != 0;
        streamReport->initialized = true;
        if (!*streamReport->eventReady) { streamReport->errorStage = "EVENT_READINESS"; streamReport->errorHresult = Hresult(eventCreateResult != S_OK ? eventCreateResult : eventResult); }
    }
    if (captureEvidence != nullptr) {
        captureEvidence->eventCreation.Record(eventCreateResult);
        if (eventHandle != nullptr) captureEvidence->setEventHandle.Record(eventResult);
        else captureEvidence->setEventHandle.NotCalled();
        captureEvidence->eventAcquired = eventHandle != nullptr;
    }
    const auto closeEvent = [&]() {
        if (eventHandle == nullptr) return true;
        const BOOL closed = CloseHandle(eventHandle);
        if (streamReport != nullptr) streamReport->cleanup = closed
            ? "EVENT_RELEASE_ATTEMPTED;CLIENT_RELEASE_PENDING"
            : "EVENT_RELEASE_FAILED;CLIENT_RELEASE_PENDING";
        if (renderChannelReport != nullptr && !closed) renderChannelReport->cleanupStatus = "FAILED";
        if (channelMeterReport != nullptr && !closed) channelMeterReport->cleanupStatus = "FAILED";
        if (captureEvidence != nullptr) {
            const DWORD error = closed ? ERROR_SUCCESS : GetLastError();
            captureEvidence->eventClose.Record(closed ? S_OK
                : (error == ERROR_SUCCESS ? E_FAIL : HRESULT_FROM_WIN32(error)));
            if (closed) captureEvidence->eventReleased = true;
        }
        if (closed) eventHandle = nullptr;
        return closed != FALSE;
    };
    const bool bufferReady = bufferResult == S_OK && actualFrames != 0;
    const bool eventReady = eventCreateResult == S_OK && eventResult == S_OK;
    if (duplex != nullptr) duplex->RecordBufferAndEvent(duplexSide, bufferResult, actualFrames, eventResult,
        bufferReady && eventReady);
    if (renderChannelReport != nullptr)
        renderChannelReport->lifecycleStatus = bufferReady && eventReady ? "INITIALIZED_READY" : "INITIALIZED_NOT_READY";
    if ((!bufferReady || !eventReady) && renderChannelReport != nullptr) {
        renderChannelReport->errorStage = !bufferReady ? "GetBufferSize" : "SetEventHandle";
        renderChannelReport->errorHresult = Hresult(!bufferReady ? bufferResult : eventResult);
    }
    std::printf("Initialize HRESULT: %s\nInitialize status: INITIALIZED\n"
                "Event handle creation HRESULT: %s\nSetEventHandle HRESULT: %s\n"
                "Event-driven client readiness: %s\n",
                Hresult(hr).c_str(), Hresult(eventCreateResult).c_str(),
                eventHandle == nullptr ? "(not called)" : Hresult(eventResult).c_str(),
                bufferReady && eventReady ? "READY" : "NOT_READY");
    if (streamDuration != 0) std::printf("Stream duration requested: %u s (allowed range 1-30 s)\n", streamDuration);
    bool loopbackReady = true;
    if (loopbackCapture != nullptr) {
        loopbackReady = loopbackCapture->Configure(loopbackCapture->Channel(), rate,
            rate * (streamDuration + 1), 8'192);
        std::printf("Loopback capture storage: %s (%llu frame capacity)\n",
            loopbackReady ? "reserved before Start" : "unavailable",
            static_cast<unsigned long long>(rate) * (streamDuration + 1));
    }
    if (loopbackRender != nullptr) {
        loopbackReady = loopbackRender->Configure(rate, loopbackRender->Channel(), actualFrames,
            streamDuration) && loopbackReady;
        std::printf("Loopback render marker sequence: %s; peak ceiling %d PCM16\n",
            loopbackReady ? "prepared before Start" : "preparation failed",
            AnalogLoopback::kSignalPeakCeiling);
    }
    bool preflightReady = true;
    if (rtlPreflight != nullptr) {
        preflightReady = flow == eCapture && liveRing == nullptr && loopbackCapture == nullptr &&
            loopbackRender == nullptr && monitorLatencyData == nullptr && monitorLatencySignal == nullptr &&
            rate == MonitorRtlPreflight::kExpectedRate && channels == 2 && formatName == L"pcm16" &&
            streamDuration == 5 && rtlPreflight->Configure();
        std::printf("Capture-only preflight storage: %s (%u-frame stereo capacity)\n",
            preflightReady ? "reserved before Start" : "unavailable/invalid configuration",
            MonitorRtlPreflight::kExpectedFrames);
    }
    if (duplex != nullptr && bufferReady && eventReady && loopbackReady)
        duplex->RecordBufferInfo(duplexSide, requestedPeriod, actualFrames);
    if (streamDuration != 0 && bufferReady && eventReady) {
        if (!preflightReady) {
            std::puts("PREFLIGHT_STATUS: FAILED");
            PrintStreamNotStarted(); releaseClient(); closeEvent();
            if (captureLifecycle != nullptr) captureLifecycle->Transition(CaptureRuntimeEvidence::LifecycleState::Failed);
            return 5;
        }
        if (!loopbackReady) {
            duplex->MarkPrepared(duplexSide, false);
            duplex->RecordClassification(duplexSide, ProbeMath::StreamStatus::InitializeFailed,
                ProbeMath::StreamQuality::SevereAnomalies);
            PrintStreamNotStarted();
            releaseClient();
            closeEvent();
            return 5;
        }
        SignalLevels::Accumulator signalAccumulator(flow == eCapture);
        MonitorRtlPreflight::StateMachine preflightState;
        if (rtlPreflight != nullptr) preflightState.Transition(MonitorRtlPreflight::State::PreflightCapture);
        std::atomic<bool> stopMeterDisplay{false};
        std::thread meterDisplay;
        bool meterSnapshotReady = true;
        if (channelMeter != nullptr) {
            try {
                meterDisplay = std::thread([&] {
                    while (!stopMeterDisplay.load(std::memory_order_acquire)) {
                        ChannelMeter::Snapshot snapshot;
                        if (channelMeter->TrySnapshot(snapshot)) {
                            std::printf("METER");
                            for (std::uint32_t channel = 0; channel < snapshot.channelCount; ++channel) {
                                const auto* window = ChannelMeter::LatestCompletedWindow(snapshot, channel);
                                if (window != nullptr)
                                    std::printf("  Ch%u window peak %.1f dBFS RMS %.1f dBFS silent %s; peak hold %.1f dBFS",
                                        channel + 1, ChannelMeter::Dbfs(window->peakMagnitude),
                                        ChannelMeter::RmsDbfs(*window),
                                        ChannelMeter::IsSilent(*window) ? "yes" : "no",
                                        ChannelMeter::Dbfs(snapshot.total[channel].peakHoldMagnitude));
                                else
                                    if (snapshot.total[channel].analyzedSamples != 0)
                                        std::printf("  Ch%u window peak/RMS unavailable; peak hold %.1f dBFS",
                                            channel + 1,
                                            ChannelMeter::Dbfs(snapshot.total[channel].peakHoldMagnitude));
                                    else
                                        std::printf("  Ch%u window peak/RMS unavailable; peak hold unavailable", channel + 1);
                            }
                            std::puts("");
                        }
                        std::this_thread::sleep_for(std::chrono::milliseconds(250));
                    }
                });
            } catch (const std::system_error&) {
                channelMeterReport->status = ChannelMeter::RunStatus::Failed;
                channelMeterReport->errorStage = "Start meter display thread";
                channelMeterReport->errorHresultKnown = false;
                PrintStreamNotStarted(); releaseClient();
                channelMeterReport->cleanupStatus = closeEvent() ? "RELEASE_ATTEMPTED" : "FAILED";
                return 5;
            }
        }
        const auto meterStartedAt = std::chrono::steady_clock::now();
        auto execution = RunStream(client, eventHandle, flow, actualFrames, rate, streamDuration,
                                   useProAudioMmcss, duplex, duplexSide, clockSeries, liveRing,
                                   measureSignalLevels ? &signalAccumulator : nullptr,
                                   loopbackCapture, loopbackRender,
                                   loopbackCapture != nullptr || loopbackRender != nullptr || monitorLatencyData != nullptr,
                                   monitorLatencyData, monitorLatencySignal, rtlPreflight,
                                   rtlPreflight != nullptr ? captureLifecycle : nullptr, channelMeter,
                                   renderChannelSignal, externalCancelEvent, route);
        if (streamReport != nullptr) {
            streamReport->startSucceeded = execution.started;
            streamReport->startHresult = execution.startAttempted ? Hresult(execution.startResult) : "NOT_ATTEMPTED";
            streamReport->stopSucceeded = execution.started ? std::optional<bool>(execution.stopped) : std::nullopt;
            streamReport->stopHresult = execution.started ? Hresult(execution.stopResult) : "NOT_ATTEMPTED";
            streamReport->events = execution.events; streamReport->packets = execution.packets;
            streamReport->framesProcessed = execution.frames;
            streamReport->elapsedMilliseconds = execution.elapsedMilliseconds;
            streamReport->expectedFrames = ProbeMath::ExpectedFramesForMicroseconds(
                static_cast<std::uint64_t>(execution.elapsedMilliseconds * 1000.0), rate);
            streamReport->frameCoveragePercent = *streamReport->expectedFrames == 0 ? 0.0 :
                100.0 * static_cast<double>(execution.frames) / static_cast<double>(*streamReport->expectedFrames);
            streamReport->discontinuities = execution.discontinuities;
            streamReport->timestampErrors = execution.timestampErrors;
            streamReport->unavailableCaptureBuffers = execution.unavailableCaptureBuffers;
            streamReport->bufferServiceFaults = execution.criticalErrors + execution.packetDrainLimitHits;
            streamReport->eventWaitTimeouts = execution.intermediateWaitTimeouts;
            streamReport->durationReached = execution.durationReached;
            streamReport->cancelled = execution.cancelled;
            streamReport->watchdogTimedOut = execution.watchdogTimedOut;
            streamReport->deviceInvalidated = execution.deviceInvalidated;
            streamReport->cleanup = execution.stopped ? "STOP_SUCCEEDED;RELEASE_PENDING" : execution.started ? "STOP_FAILED;RELEASE_PENDING" : "STOP_NOT_ATTEMPTED;RELEASE_PENDING";
            streamReport->errorStage = execution.errorStage;
            if (execution.criticalError || execution.cancelled || execution.watchdogTimedOut)
                streamReport->errorHresult = Hresult(execution.errorResult);
            streamReport->streamQuality = execution.criticalError || execution.discontinuities || execution.timestampErrors || execution.packetDrainLimitHits || !execution.sufficientFrames
                ? "ANOMALIES_PRESENT" : execution.started && execution.durationReached && execution.sufficientFrames && execution.stopped ? "CLEAN" : "INCOMPLETE";
        }
        if (channelMeter != nullptr) {
            stopMeterDisplay.store(true, std::memory_order_release);
            meterDisplay.join();
            meterSnapshotReady = channelMeter->TrySnapshot(channelMeterReport->meter);
            if (!meterSnapshotReady) {
                channelMeterReport->errorStage = "Read final meter snapshot";
                channelMeterReport->errorHresultKnown = false;
            }
            channelMeterReport->requestedDurationSeconds = streamDuration;
            channelMeterReport->elapsedMilliseconds = static_cast<std::uint64_t>(
                std::chrono::duration_cast<std::chrono::milliseconds>(
                    std::chrono::steady_clock::now() - meterStartedAt).count());
            if (execution.startAttempted) {
                channelMeterReport->startHresult = static_cast<std::int32_t>(execution.startResult);
                channelMeterReport->startHresultKnown = true;
            }
            if (execution.started) {
                channelMeterReport->stopHresult = static_cast<std::int32_t>(execution.stopResult);
                channelMeterReport->stopHresultKnown = true;
            }
        }
        if (captureEvidence != nullptr) {
            execution.captureServiceAttempted
                ? captureEvidence->captureService.Record(execution.captureServiceResult)
                : captureEvidence->captureService.NotCalled();
            execution.startResult == E_PENDING ? captureEvidence->start.NotCalled()
                                               : captureEvidence->start.Record(execution.startResult);
            execution.started ? captureEvidence->stop.Record(execution.stopResult)
                              : captureEvidence->stop.NotCalled();
            captureEvidence->packetCount = execution.packets;
            captureEvidence->frameCount = execution.frames;
            captureEvidence->emptyBufferResults = execution.emptyCaptureBuffers;
            captureEvidence->unavailableBufferResults = execution.unavailableCaptureBuffers;
            captureEvidence->discontinuities = execution.discontinuities;
            captureEvidence->timestampErrors = execution.timestampErrors;
            captureEvidence->positionGaps = execution.positionJumps;
            captureEvidence->positionOverlaps = execution.positionOverlaps;
            captureEvidence->invalidPositions = execution.invalidDevicePositions;
            captureEvidence->firstDevicePosition = execution.initialPositionValid ? execution.firstDevicePosition : 0;
            captureEvidence->firstDevicePositionValid = execution.initialPositionValid;
            captureEvidence->firstQpc100ns = execution.firstQpcPosition;
            captureEvidence->lastQpc100ns = execution.lastQpcPosition;
            captureEvidence->qpcPositionValid = execution.qpcValidPackets != 0;
            captureEvidence->durationReached = execution.durationReached;
            captureEvidence->cancelled = execution.cancelled;
            captureEvidence->watchdogTimedOut = execution.watchdogTimedOut;
            captureEvidence->deviceInvalidated = execution.deviceInvalidated;
            captureEvidence->criticalApiFailure = execution.criticalError;
            captureEvidence->firstFailureHresult = static_cast<std::int32_t>(execution.errorResult);
            captureEvidence->firstFailureStage = execution.errorStage;
            captureEvidence->captureServiceReleased = true;
        }
        if (renderChannelReport != nullptr) {
            if (execution.startAttempted) renderChannelReport->startHresult = Hresult(execution.startResult);
            if (execution.started) renderChannelReport->stopHresult = Hresult(execution.stopResult);
            renderChannelReport->generatedFrames = renderChannelSignal->GeneratedFrames();
            const auto& signalStats = renderChannelSignal->Stats();
            renderChannelReport->leftNonzeroFrames = signalStats[0].nonzeroFrames;
            renderChannelReport->rightNonzeroFrames = signalStats[1].nonzeroFrames;
            const auto peakText = [](std::int16_t peak) {
                if (peak == 0) return std::string("-INF");
                char text[32]{};
                std::snprintf(text, sizeof(text), "%.4f", 20.0 * std::log10(peak / 32768.0));
                return std::string(text);
            };
            renderChannelReport->leftPeakDbfs = peakText(signalStats[0].peakMagnitude);
            renderChannelReport->rightPeakDbfs = peakText(signalStats[1].peakMagnitude);
            renderChannelReport->eventCount = execution.events;
            renderChannelReport->eventIntervalAverageMs = execution.eventIntervalsObserved == 0
                ? 0.0 : execution.eventIntervalSumMilliseconds / execution.eventIntervalsObserved;
            renderChannelReport->eventIntervalMaximumMs = execution.maximumEventIntervalMilliseconds;
            const auto expected = static_cast<std::uint64_t>(rate) * streamDuration;
            renderChannelReport->frameCoveragePercent = expected == 0 ? 0.0
                : 100.0 * static_cast<double>(execution.frames) / expected;
            renderChannelReport->anomalies = execution.longEventIntervals + execution.intermediateWaitTimeouts +
                execution.packetDrainLimitHits + execution.criticalErrors;
            renderChannelReport->longEventIntervals = execution.longEventIntervals;
            renderChannelReport->intermediateWaitTimeouts = execution.intermediateWaitTimeouts;
            renderChannelReport->streamApiFailures = execution.criticalErrors;
            renderChannelReport->watchdogTimedOut = execution.watchdogTimedOut;
            renderChannelReport->cancelled = execution.cancelled;
            renderChannelReport->deviceLost = execution.deviceInvalidated;
            renderChannelReport->finalSilenceSubmitted = execution.finalSilenceSubmitted;
            const bool streamEnded = execution.durationReached && !execution.cancelled && !execution.criticalError;
            const auto status = ProbeMath::ClassifyStream(true, execution.startAttempted, execution.started,
                execution.durationReached, execution.events, execution.frames, execution.sufficientFrames,
                execution.warning, execution.criticalError, execution.stopped);
            const auto quality = ProbeMath::ClassifyStreamQuality(streamEnded, execution.sufficientFrames,
                execution.warning, 0, 0, 0, 0, execution.longEventIntervals,
                execution.packetDrainLimitHits, 0, actualFrames);
            renderChannelReport->lifecycleStatus = StreamStatusText(status);
            renderChannelReport->streamQuality = StreamQualityText(quality);
            if (execution.criticalError) {
                renderChannelReport->errorStage = execution.errorStage;
                renderChannelReport->errorHresult = Hresult(execution.errorResult);
            }
            std::printf("RENDER SIGNAL: Windows channel %u; generated frames %llu; nonzero L/R %llu/%llu; peak dBFS L/R %s/%s\n",
                renderChannelReport->renderChannel,
                static_cast<unsigned long long>(*renderChannelReport->generatedFrames),
                static_cast<unsigned long long>(*renderChannelReport->leftNonzeroFrames),
                static_cast<unsigned long long>(*renderChannelReport->rightNonzeroFrames),
                renderChannelReport->leftPeakDbfs->c_str(), renderChannelReport->rightPeakDbfs->c_str());
            std::printf("RENDER EVENTS: %llu; interval avg/max %s/%s ms; coverage %s%%; buffer-service anomalies %llu; lifecycle %s; quality %s\n",
                static_cast<unsigned long long>(*renderChannelReport->eventCount),
                std::to_string(*renderChannelReport->eventIntervalAverageMs).c_str(),
                std::to_string(*renderChannelReport->eventIntervalMaximumMs).c_str(),
                std::to_string(*renderChannelReport->frameCoveragePercent).c_str(),
                static_cast<unsigned long long>(*renderChannelReport->anomalies),
                renderChannelReport->lifecycleStatus->c_str(), renderChannelReport->streamQuality->c_str());
        }
        const auto signalMetrics = measureSignalLevels ? signalAccumulator.Finish() : SignalLevels::Metrics{};
        releaseClient();
        const BOOL eventClosed = CloseHandle(eventHandle);
        const DWORD closeError = eventClosed ? ERROR_SUCCESS : GetLastError();
        const HRESULT closeResult = eventClosed ? S_OK
            : (closeError == ERROR_SUCCESS ? E_FAIL : HRESULT_FROM_WIN32(closeError));
        if (streamReport != nullptr) streamReport->cleanup = eventClosed
            ? (execution.stopped ? "STOP_SUCCEEDED;EVENT_RELEASE_ATTEMPTED;CLIENT_RELEASE_ATTEMPTED" : "EVENT_RELEASE_ATTEMPTED;CLIENT_RELEASE_ATTEMPTED")
            : "EVENT_RELEASE_FAILED;CLIENT_RELEASE_ATTEMPTED";
        if (captureEvidence != nullptr) {
            captureEvidence->eventClose.Record(closeResult);
            captureEvidence->eventReleased = eventClosed != FALSE;
        }
        if (renderChannelReport != nullptr)
            renderChannelReport->cleanupStatus = eventClosed ? "RELEASE_ATTEMPTED" : "FAILED";
        if (renderChannelReport != nullptr)
            std::printf("RENDER CLEANUP STATUS: %s\n", renderChannelReport->cleanupStatus->c_str());
        std::printf("Exclusive IAudioClient released: yes\nEvent handle close HRESULT: %s\n",
                    Hresult(closeResult).c_str());
        if (!eventClosed) {
            if (duplex != nullptr) duplex->RequestStop(false);
            ++execution.criticalErrors;
            if (!execution.criticalError) {
                execution.errorStage = "CloseHandle";
                execution.errorResult = closeResult;
            }
            execution.criticalError = true;
        }
        if (channelMeterReport != nullptr) {
            channelMeterReport->status = ChannelMeter::ClassifyRun(
                execution.durationReached && execution.sufficientFrames,
                execution.cancelled, execution.deviceInvalidated, false,
                execution.criticalError, !eventClosed);
            if (!meterSnapshotReady && channelMeterReport->status == ChannelMeter::RunStatus::Completed)
                channelMeterReport->status = ChannelMeter::RunStatus::Incomplete;
            channelMeterReport->cleanupStatus = eventClosed ? "RELEASE_ATTEMPTED" : "FAILED";
            if (execution.criticalError) {
                channelMeterReport->errorStage = execution.errorStage;
                channelMeterReport->errorHresult = static_cast<std::int32_t>(execution.errorResult);
                channelMeterReport->errorHresultKnown = std::string_view(execution.errorStage) != "none";
            }
            for (std::uint32_t channel = 0; channel < channelMeterReport->meter.channelCount; ++channel) {
                const auto& values = channelMeterReport->meter.total[channel];
                if (values.analyzedSamples == 0) {
                    std::printf("METER SUMMARY Ch%u peak/hold/RMS unavailable; clips 0; frames 0; silence unavailable\n",
                        channel + 1);
                } else {
                    std::printf("METER SUMMARY Ch%u peak %.2f dBFS hold %.2f dBFS RMS %.2f dBFS clips %llu frames %llu silent %s\n",
                        channel + 1, ChannelMeter::Dbfs(values.peakMagnitude),
                        ChannelMeter::Dbfs(values.peakHoldMagnitude), ChannelMeter::RmsDbfs(values),
                        static_cast<unsigned long long>(values.clippedSamples),
                        static_cast<unsigned long long>(values.analyzedSamples),
                        ChannelMeter::IsSilent(values) ? "yes" : "no");
                }
            }
            std::printf("CAPTURE LIFECYCLE STATUS: %s; analyzed frames %llu; requested duration %u s; cleanup %s\n",
                ChannelMeter::RunStatusText(channelMeterReport->status),
                static_cast<unsigned long long>(channelMeterReport->meter.analyzedFrames),
                streamDuration, channelMeterReport->cleanupStatus.c_str());
        }
        if (captureEvidence != nullptr) captureEvidence->criticalApiFailure = execution.criticalError;
        if (duplex != nullptr) {
            duplex->RecordExecution(duplexSide, execution, execution.elapsedMilliseconds);
            duplex->RecordCleanup(duplexSide, true, !eventClosed || (execution.started && !execution.stopped));
        }
        const auto status = ProbeMath::ClassifyStream(true, execution.startAttempted, execution.started,
            execution.durationReached, execution.events, execution.frames, execution.sufficientFrames,
            execution.warning, execution.criticalError, execution.stopped);
        std::printf("STATUS: %s\n", StreamStatusText(status));
        const UINT64 positionAnomalies = execution.positionJumps + execution.positionOverlaps + execution.invalidDevicePositions;
        const UINT64 maximumPositionDeviation = std::max(execution.jumpFramesMaximum, execution.overlapFramesMaximum);
        const bool streamReachedEnd = status == ProbeMath::StreamStatus::StreamCompleted ||
            status == ProbeMath::StreamStatus::StreamCompletedWithWarnings;
        const auto quality = ProbeMath::ClassifyStreamQuality(streamReachedEnd, execution.sufficientFrames,
            execution.warning || positionAnomalies != 0, execution.discontinuities, positionAnomalies,
            execution.timestampErrors, execution.unavailableCaptureBuffers, execution.longEventIntervals,
            execution.packetDrainLimitHits, maximumPositionDeviation, actualFrames);
        std::printf("STREAM_QUALITY: %s\n", StreamQualityText(quality));
        if (channelMeterReport != nullptr) {
            const auto expectedCaptureFrames = static_cast<std::uint64_t>(rate) * streamDuration;
            channelMeterReport->durationReached = execution.durationReached;
            channelMeterReport->captureFrames = execution.frames;
            if (expectedCaptureFrames != 0)
                channelMeterReport->captureFrameCoveragePercent = 100.0 * execution.frames / expectedCaptureFrames;
            channelMeterReport->captureDiscontinuities = execution.discontinuities;
            channelMeterReport->captureTimestampErrors = execution.timestampErrors;
            channelMeterReport->unavailableCaptureBuffers = execution.unavailableCaptureBuffers;
            channelMeterReport->watchdogTimedOut = execution.watchdogTimedOut;
            channelMeterReport->cancelled = execution.cancelled;
            channelMeterReport->meterAnalysisAttempted = true;
            channelMeterReport->captureStreamStatus = StreamStatusText(status);
            channelMeterReport->captureStreamQuality = StreamQualityText(quality);
            channelMeterReport->meterAnalysisStatus = !meterSnapshotReady ? "SNAPSHOT_UNAVAILABLE"
                : channelMeterReport->meter.invalidInput || channelMeterReport->meter.overflowed ? "FAILED"
                : channelMeterReport->meter.analyzedFrames == 0 ? "NO_DATA" : "ANALYZED";
            std::printf("CAPTURE INTEGRITY: duration %s; frames %llu / %llu (%.2f%%); discontinuities %llu; timestamp errors %llu; unavailable buffers %llu; watchdog %s; cancelled %s\n",
                execution.durationReached ? "reached" : "not reached",
                static_cast<unsigned long long>(execution.frames),
                static_cast<unsigned long long>(expectedCaptureFrames),
                expectedCaptureFrames == 0 ? 0.0 : 100.0 * execution.frames / expectedCaptureFrames,
                static_cast<unsigned long long>(execution.discontinuities),
                static_cast<unsigned long long>(execution.timestampErrors),
                static_cast<unsigned long long>(execution.unavailableCaptureBuffers),
                execution.watchdogTimedOut ? "yes" : "no", execution.cancelled ? "yes" : "no");
            std::printf("CAPTURE STREAM STATUS: %s; STREAM QUALITY: %s; METER ANALYSIS: %s\n",
                StreamStatusText(status), StreamQualityText(quality),
                channelMeterReport->meterAnalysisStatus->c_str());
        }
        if (rtlPreflight != nullptr) {
            preflightState.Transition(MonitorRtlPreflight::State::Validating);
            const auto preflight = rtlPreflight->Analyze();
            if (captureEvidence != nullptr) {
                captureEvidence->continuityValidationAvailable = preflight.configured && preflight.baselineAvailable;
                captureEvidence->continuityClean = preflight.continuityClean;
                captureEvidence->timestampValidationAvailable = preflight.configured && preflight.baselineAvailable;
                captureEvidence->timestampsClean = preflight.timestampsClean;
                captureEvidence->clippingValidationAvailable = preflight.configured && preflight.baselineAvailable;
                captureEvidence->noClipping = preflight.noClipping;
                captureEvidence->discontinuities = std::max(captureEvidence->discontinuities,
                    rtlPreflight->Discontinuities());
                captureEvidence->timestampErrors = rtlPreflight->TimestampErrors();
                captureEvidence->positionGaps = std::max(captureEvidence->positionGaps,
                    rtlPreflight->PositionGaps());
                captureEvidence->positionOverlaps = std::max(captureEvidence->positionOverlaps,
                    rtlPreflight->PositionOverlaps());
                captureEvidence->invalidPositions = std::max(captureEvidence->invalidPositions,
                    rtlPreflight->InvalidPositions());
                captureEvidence->silentPackets = rtlPreflight->SilentPackets();
                captureEvidence->clippedSamples = rtlPreflight->ClippedSamples();
            }
            const bool valid = status == ProbeMath::StreamStatus::StreamCompleted && execution.durationReached &&
                !execution.cancelled && !execution.criticalError && execution.discontinuities == 0 &&
                execution.timestampErrors == 0 && execution.positionJumps == 0 && execution.positionOverlaps == 0 &&
                MonitorRtlPreflight::HasSufficientCaptureFrames(preflight.frames, rate, streamDuration) &&
                preflight.continuityClean &&
                preflight.baselineAvailable && preflight.timestampsClean && preflight.noClipping &&
                preflight.markerFound && preflight.input2Quiet;
            std::printf("Input 1 reference PRBS: %s; matched markers %zu; confidence %.3f\n",
                preflight.markerFound ? "MATCHED" : "NOT_FOUND", preflight.matchedMarkers, preflight.markerConfidence);
            std::printf("Input 2 baseline: %s; peak %.2f dBFS (significant threshold %.1f dBFS)\n",
                !preflight.baselineAvailable ? "INSUFFICIENT_DATA" : preflight.input2Quiet ? "QUIET" : "SIGNAL_PRESENT", preflight.input2PeakDbfs,
                MonitorRtlPreflight::kBaselineSignificantPeakDbfs);
            std::printf("Capture integrity: continuity %s; timestamps %s; clipping %s; frames %llu\n",
                preflight.continuityClean ? "CLEAN" : "ERROR", preflight.timestampsClean ? "CLEAN" : "ERROR",
                preflight.noClipping ? "NONE" : "DETECTED", static_cast<unsigned long long>(preflight.frames));
            bool armed = false;
            if (execution.cancelled) preflightState.Transition(MonitorRtlPreflight::State::Cancelled);
            else if (valid) armed = preflightState.Transition(MonitorRtlPreflight::State::Armed) &&
                preflightState.Get() == MonitorRtlPreflight::State::Armed;
            else preflightState.Transition(MonitorRtlPreflight::State::Failed);
            if (valid && !armed) preflightState.Transition(MonitorRtlPreflight::State::Failed);
            std::printf("PREFLIGHT_STATUS: %s\nPREFLIGHT_STATE: %s\nROUTING_ISOLATION_STATUS: NOT_APPLIED (Render not active)\n",
                execution.cancelled ? "CANCELLED" : armed ? "ARMED" : "FAILED",
                MonitorRtlPreflight::StateText(preflightState.Get()));
            rtlPreflight->Erase();
            return armed ? 0 : 8;
        }
        if (duplex != nullptr) duplex->RecordClassification(duplexSide, status, quality, signalMetrics);
        if (status == ProbeMath::StreamStatus::StreamCompleted ||
            status == ProbeMath::StreamStatus::StreamCompletedWithWarnings) {
            std::puts("Result: bounded stream test completed; this does not establish long-term stability or measured end-to-end latency.");
            return status == ProbeMath::StreamStatus::StreamCompleted ? 0 : 8;
        }
        return status == ProbeMath::StreamStatus::StartFailed ? 6 : 7;
    }
    std::printf("Result: %s\n",
                bufferReady && eventReady
                    ? "Initialize succeeded and the event-driven client is ready; no audio was started, captured, or rendered."
                    : "Initialize succeeded, but event-driven client preparation failed; no audio was started, captured, or rendered.");
    if (streamDuration != 0) std::puts("STATUS: STREAM_FAILED");
    if (streamDuration != 0) PrintStreamNotStarted();
    releaseClient();
    BOOL eventClosed = TRUE;
    HRESULT closeResult = S_OK;
    if (eventHandle != nullptr) {
        eventClosed = CloseHandle(eventHandle);
        const DWORD error = eventClosed ? ERROR_SUCCESS : GetLastError();
        closeResult = eventClosed ? S_OK : (error == ERROR_SUCCESS ? E_FAIL : HRESULT_FROM_WIN32(error));
        std::printf("Event handle close HRESULT: %s\n", Hresult(closeResult).c_str());
        if (captureEvidence != nullptr) {
            captureEvidence->eventClose.Record(closeResult);
            captureEvidence->eventReleased = eventClosed != FALSE;
        }
    }
    if (streamDuration != 0) std::puts("Exclusive IAudioClient released: yes");
    if (renderChannelReport != nullptr)
        renderChannelReport->cleanupStatus = eventClosed ? "RELEASE_ATTEMPTED" : "FAILED";
    if (channelMeterReport != nullptr) {
        channelMeterReport->status = ChannelMeter::RunStatus::Incomplete;
        channelMeterReport->cleanupStatus = eventClosed ? "RELEASE_ATTEMPTED" : "FAILED";
        if (!bufferReady || !eventReady) {
            channelMeterReport->errorStage = "Event-driven readiness";
            channelMeterReport->errorHresult = static_cast<std::int32_t>(
                bufferReady ? eventResult : bufferResult);
            channelMeterReport->errorHresultKnown = FAILED(bufferReady ? eventResult : bufferResult);
        }
    }
    if (!eventClosed) return streamDuration == 0 ? 5 : 7;
    return bufferReady && eventReady ? 0 : 5;
}

void ProbeFormat(IAudioClient* client, const char* label, const WAVEFORMATEX* format) {
    const HRESULT result = client->IsFormatSupported(AUDCLNT_SHAREMODE_EXCLUSIVE, format, nullptr);
    std::printf("    %-37s %s%s\n", label, Hresult(result).c_str(), result == S_OK ? " EXACT_SUPPORTED" : "");
}

using WasapiFormatMatrixApi = ExclusiveFormatMatrix::WasapiApi;

std::optional<std::string> MatrixReportId() {
    std::array<unsigned char, 16> bytes{};
    if (BCryptGenRandom(nullptr, bytes.data(), static_cast<ULONG>(bytes.size()),
                        BCRYPT_USE_SYSTEM_PREFERRED_RNG) < 0) return std::nullopt;
    bytes[6] = static_cast<unsigned char>((bytes[6] & 0x0f) | 0x40);
    bytes[8] = static_cast<unsigned char>((bytes[8] & 0x3f) | 0x80);
    constexpr char hex[] = "0123456789abcdef";
    std::string text;
    text.reserve(36);
    for (std::size_t i = 0; i < bytes.size(); ++i) {
        if (i == 4 || i == 6 || i == 8 || i == 10) text.push_back('-');
        text.push_back(hex[bytes[i] >> 4]);
        text.push_back(hex[bytes[i] & 0x0f]);
    }
    return text;
}

bool WriteNewJson(const std::wstring& path, const std::string& json) {
    HANDLE file = CreateFileW(path.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW,
                              FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) return false;
    bool ok = true;
    std::size_t offset = 0;
    while (offset < json.size()) {
        const DWORD chunk = static_cast<DWORD>(std::min<std::size_t>(json.size() - offset, 1U << 20));
        DWORD written = 0;
        if (!WriteFile(file, json.data() + offset, chunk, &written, nullptr) || written == 0) { ok = false; break; }
        offset += written;
    }
    if (ok && !FlushFileBuffers(file)) ok = false;
    CloseHandle(file);
    if (!ok) DeleteFileW(path.c_str());
    return ok;
}

void PrintFormatMatrix(const ExclusiveFormatMatrix::Report& report) {
    using namespace ExclusiveFormatMatrix;
    std::printf("\nWASAPI Exclusive format matrix — %s / %s\n",
        DirectionText(report.direction), LevelText(report.level));
    std::printf("%-17s %-13s %-10s %-13s %-10s\n",
        "Format", "44.1k Default", "44.1k Min", "48k Default", "48k Min");
    constexpr std::array encodings{Encoding::Pcm16, Encoding::Pcm24In32, Encoding::Float32};
    constexpr std::array channels{std::uint16_t{1}, std::uint16_t{2}};
    for (const auto encoding : encodings) for (const auto count : channels) {
        const Format key{0, count, encoding};
        std::string label = encoding == Encoding::Pcm24In32 ? "PCM24-in-32" : EncodingText(encoding);
        label += count == 1 ? " Mono" : " Stereo";
        std::printf("%-20s", label.c_str());
        for (const auto rate : {44'100U, 48'000U}) for (const auto period : {Period::Default, Period::Minimum}) {
            const auto found = std::find_if(report.candidates.begin(), report.candidates.end(), [&](const Candidate& c) {
                return c.format.sampleRate == rate && c.format.channels == key.channels &&
                    c.format.encoding == key.encoding && c.period == period;
            });
            std::printf(" %-12s", found == report.candidates.end() ? "INCOMPLETE" : CellText(found->status));
        }
        std::puts("");
    }
}

int RunFormatMatrix(IMMDeviceEnumerator* enumerator, EDataFlow direction, const std::wstring& endpointId,
                    ExclusiveFormatMatrix::Level level, const std::wstring& outputJson) {
    using namespace ExclusiveFormatMatrix;
    IMMDevice* device = nullptr;
    HRESULT hr = enumerator->GetDevice(endpointId.c_str(), &device);
    if (FAILED(hr) || device == nullptr) {
        std::printf("Selected endpoint lookup failed: %s\n", Hresult(hr).c_str());
        Release(device);
        return 1;
    }
    IMMEndpoint* endpoint = nullptr;
    EDataFlow actualDirection = eAll;
    hr = device->QueryInterface(__uuidof(IMMEndpoint), reinterpret_cast<void**>(&endpoint));
    if (SUCCEEDED(hr) && endpoint != nullptr) hr = endpoint->GetDataFlow(&actualDirection);
    Release(endpoint);
    if (FAILED(hr) || actualDirection != direction) {
        std::printf("Selected endpoint direction mismatch/query failed: %s\n", Hresult(hr).c_str());
        Release(device);
        return 1;
    }
    std::puts("Selected endpoint: explicit selection (identifier omitted from matrix/report)");
    WasapiFormatMatrixApi api(device);
    const auto report = Run(direction == eCapture ? Direction::Capture : Direction::Render, level, api);
    PrintFormatMatrix(report);
    if (!outputJson.empty()) {
#ifndef PROBE_VERSION
#define PROBE_VERSION "unknown"
#endif
        const auto reportId = MatrixReportId();
        if (!reportId) {
            std::fwprintf(stderr, L"Could not create a unique report identifier.\n");
            Release(device);
            return 1;
        }
        const auto json = ToJson(report, MatrixTimestampUtc(), PROBE_VERSION, *reportId);
        if (!WriteNewJson(outputJson, json)) {
            std::fwprintf(stderr, L"Could not create JSON report without overwriting an existing file.\n");
            Release(device);
            return 1;
        }
        std::puts("JSON report created (sanitized; endpoint ID and output path omitted from the report).");
    }
    Release(device);
    return 0;
}

void ProbeEndpoint(IMMDevice* device, EDataFlow flow, const wchar_t* endpointId) {
    DWORD state = 0;
    const HRESULT stateResult = device->GetState(&state);
    std::printf("\n=== %s endpoint ===\n  ID: %s\n  State: %s (%s)\n",
        flow == eCapture ? "CAPTURE" : "RENDER", Utf8(endpointId).c_str(),
        SUCCEEDED(stateResult) ? StateText(state).c_str() : "unavailable", Hresult(stateResult).c_str());

    IPropertyStore* properties = nullptr;
    HRESULT hr = device->OpenPropertyStore(STGM_READ, &properties);
    if (SUCCEEDED(hr) && properties != nullptr) {
        std::printf("  Friendly name: %s\n", PropertyString(properties, PKEY_Device_FriendlyName).c_str());
        std::printf("  Manufacturer: %s\n", PropertyString(properties, PKEY_Device_Manufacturer).c_str());
        PrintDriverInfo(properties);
    } else {
        std::printf("  Property store failed: %s\n", Hresult(hr).c_str());
    }

    IAudioClient* client = nullptr;
    hr = device->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr, reinterpret_cast<void**>(&client));
    std::printf("  IAudioClient activation: %s\n", Hresult(hr).c_str());
    if (FAILED(hr) || client == nullptr) {
        Release(properties);
        return;
    }

    WAVEFORMATEX* mix = nullptr;
    hr = client->GetMixFormat(&mix);
    std::printf("  GetMixFormat: %s\n", Hresult(hr).c_str());
    if (SUCCEEDED(hr) && mix != nullptr) {
        const std::size_t bytes = sizeof(WAVEFORMATEX) + mix->cbSize;
        PrintFormat("Mix format", mix, bytes);
        if (flow == eCapture)
            PrintChannelTopology("Capture channel topology (Windows labels only)", MeterTopology(mix, bytes));
        ProbeFormat(client, "GetMixFormat exact Exclusive", mix);
        CoTaskMemFree(mix);
    }

    if (properties != nullptr) {
        PROPVARIANT deviceFormat;
        PropVariantInit(&deviceFormat);
        hr = properties->GetValue(PKEY_AudioEngine_DeviceFormat, &deviceFormat);
        std::printf("  PKEY_AudioEngine_DeviceFormat: %s", Hresult(hr).c_str());
        if (SUCCEEDED(hr) && deviceFormat.vt == VT_BLOB && deviceFormat.blob.pBlobData != nullptr &&
            deviceFormat.blob.cbSize >= sizeof(WAVEFORMATEX)) {
            const auto* format = reinterpret_cast<const WAVEFORMATEX*>(deviceFormat.blob.pBlobData);
            std::printf(" (VT_BLOB, %lu bytes)\n", static_cast<unsigned long>(deviceFormat.blob.cbSize));
            PrintFormat("Device format", format, deviceFormat.blob.cbSize);
            ProbeFormat(client, "DeviceFormat exact Exclusive", format);
        } else {
            std::printf(" (not an available WAVEFORMATEX blob)\n");
        }
        PropVariantClear(&deviceFormat);
    }

    std::puts("  Exact Exclusive matrix (S_OK means exact support; no Initialize is called):");
    constexpr std::array<unsigned, 2> rates{48'000, 44'100};
    constexpr std::array<unsigned, 2> channelCounts{1, 2};
    for (const unsigned rate : rates) {
        std::printf("    -- %u Hz --\n", rate);
        for (const unsigned channels : channelCounts) {
            for (const unsigned bits : {16U, 24U, 32U}) {
                const bool packed24 = bits == 24;
                const unsigned containerBits = packed24 ? 24U : bits;
                auto plain = MakeFormat(rate, channels, containerBits, false, false);
                char plainLabel[80]{};
                std::snprintf(plainLabel, sizeof(plainLabel), "PCM%u WAVEFORMATEX %s",
                    bits, channels == 1 ? "mono" : "stereo");
                ProbeFormat(client, plainLabel, &plain.Format);
                auto pcmExt = MakeFormat(rate, channels, containerBits, false, true, bits);
                char extensibleLabel[80]{};
                std::snprintf(extensibleLabel, sizeof(extensibleLabel), "PCM%u%s extensible %s", bits,
                    packed24 ? " packed" : "", channels == 1 ? "mono" : "stereo");
                ProbeFormat(client, extensibleLabel, &pcmExt.Format);
                if (bits == 24) {
                    auto pcm24In32 = MakeFormat(rate, channels, 32, false, true, 24);
                    ProbeFormat(client, channels == 1 ? "PCM24-in-32 extensible mono" : "PCM24-in-32 extensible stereo", &pcm24In32.Format);
                }
            }
            auto floatExt = MakeFormat(rate, channels, 32, true, true);
            auto floatPlain = MakeFormat(rate, channels, 32, true, false);
            ProbeFormat(client, channels == 1 ? "float32 extensible mono" : "float32 extensible stereo", &floatExt.Format);
            ProbeFormat(client, channels == 1 ? "float32 WAVEFORMATEX mono" : "float32 WAVEFORMATEX stereo", &floatPlain.Format);
        }
    }
    Release(client);
    Release(properties);
}

void ListEndpoints(IMMDeviceEnumerator* enumerator, EDataFlow flow) {
    IMMDeviceCollection* collection = nullptr;
    const HRESULT hr = enumerator->EnumAudioEndpoints(flow, DEVICE_STATEMASK_ALL, &collection);
    std::printf("%s enumeration: %s\n", flow == eCapture ? "CAPTURE" : "RENDER", Hresult(hr).c_str());
    if (FAILED(hr) || collection == nullptr) return;
    UINT count = 0;
    collection->GetCount(&count);
    for (UINT index = 0; index < count; ++index) {
        IMMDevice* device = nullptr;
        if (FAILED(collection->Item(index, &device)) || device == nullptr) continue;
        LPWSTR id = nullptr;
        DWORD state = 0;
        device->GetId(&id);
        device->GetState(&state);
        IPropertyStore* properties = nullptr;
        std::string name = "(unknown)";
        if (SUCCEEDED(device->OpenPropertyStore(STGM_READ, &properties)) && properties != nullptr) {
            name = PropertyString(properties, PKEY_Device_FriendlyName);
        }
        std::printf("  %s | %s | %s\n", StateText(state).c_str(), name.c_str(), Utf8(id).c_str());
        Release(properties);
        if (id != nullptr) CoTaskMemFree(id);
        Release(device);
    }
    Release(collection);
}

bool ProbeById(IMMDeviceEnumerator* enumerator, EDataFlow flow, const std::wstring& id) {
    IMMDevice* device = nullptr;
    const HRESULT hr = enumerator->GetDevice(id.c_str(), &device);
    if (FAILED(hr) || device == nullptr) {
        std::printf("\n%s GetDevice(%s): %s\n", flow == eCapture ? "CAPTURE" : "RENDER",
                    Utf8(id.c_str()).c_str(), Hresult(hr).c_str());
        return false;
    }
    ProbeEndpoint(device, flow, id.c_str());
    Release(device);
    return true;
}

int ProbeInitById(IMMDeviceEnumerator* enumerator, EDataFlow flow, const std::wstring& id,
                  unsigned rate, unsigned channels, const std::wstring& format, bool useMinimumPeriod,
                  unsigned streamDuration, bool useProAudioMmcss,
                  DuplexCoordinator* duplex = nullptr, int duplexSide = -1,
                  ClockAnalysis::Series* clockSeries = nullptr,
                  LiveTransport::SpscPcm16StereoRing* liveRing = nullptr,
                  bool measureSignalLevels = false,
                  AnalogLoopback::CaptureData* loopbackCapture = nullptr,
                  AnalogLoopback::RenderSignal* loopbackRender = nullptr,
                  MonitorLatency::RunData* monitorLatencyData = nullptr,
                  AnalogLoopback::RenderSignal* monitorLatencySignal = nullptr,
                  MonitorRtlPreflight::CaptureData* rtlPreflight = nullptr,
                  ChannelMeter::Meter* channelMeter = nullptr,
                  ChannelMeter::Report* channelMeterReport = nullptr,
                  RenderSignal::Generator* renderChannelSignal = nullptr,
                  RenderChannelReport::Report* renderChannelReport = nullptr,
                  StreamValidationReport::Report* streamReport = nullptr,
                  HANDLE externalCancelEvent = nullptr,
                  std::optional<ExclusiveFormatMatrix::PeriodRequest> customPeriod = std::nullopt,
                  const WAVEFORMATEXTENSIBLE* explicitFormat = nullptr,
                  const ChannelRouting::Configuration* route = nullptr) {
    if (streamReport != nullptr) {
        streamReport->direction = flow == eCapture ? "CAPTURE" : "RENDER";
        streamReport->period = customPeriod ? "CUSTOM:" + std::to_string(*customPeriod->requestedPeriod100ns) + ":" + std::to_string(*customPeriod->requestedFrames) : useMinimumPeriod ? "MINIMUM" : "DEFAULT";
        streamReport->durationSeconds = streamDuration;
        streamReport->probeVersion = PROBE_VERSION;
    }
    if (channelMeterReport != nullptr) {
        channelMeterReport->requestedDurationSeconds = streamDuration;
        channelMeterReport->minimumPeriod = useMinimumPeriod;
    }
    std::unique_ptr<CaptureRuntimeEvidence::Session> evidenceSession;
    CaptureRuntimeEvidence::Facts evidenceFacts;
    CaptureRuntimeEvidence::Lifecycle captureLifecycle;
    if (rtlPreflight != nullptr) {
        evidenceSession = std::make_unique<CaptureRuntimeEvidence::Session>(Utf8(id.c_str()));
        evidenceFacts.source = RuntimeSafetyGate::EvidenceSource::RuntimeObserved;
        evidenceFacts.session = evidenceSession->Reference();
        evidenceFacts.endpointIdentity = evidenceSession->EndpointIdentity();
        evidenceFacts.endpointExplicitlySelected = !id.empty();
        evidenceFacts.sampleRate = rate;
        evidenceFacts.requestedDurationSeconds = streamDuration;
        evidenceFacts.channels = static_cast<std::uint16_t>(channels);
        evidenceFacts.containerBits = format == L"pcm16" ? 16 : 32;
        evidenceFacts.validBits = evidenceFacts.containerBits;
        CaptureRuntimeEvidence::MarkUnattempted(evidenceFacts);
        captureLifecycle.Transition(CaptureRuntimeEvidence::LifecycleState::Configured);
    }
    if (rtlPreflight != nullptr && (flow != eCapture || liveRing != nullptr || loopbackCapture != nullptr ||
        loopbackRender != nullptr || monitorLatencyData != nullptr || monitorLatencySignal != nullptr)) {
        evidenceFacts.criticalApiFailure = true;
        evidenceFacts.firstFailureStage = "Capture-only route invariant";
        evidenceFacts.firstFailureHresult = E_INVALIDARG;
        captureLifecycle.Transition(CaptureRuntimeEvidence::LifecycleState::Failed);
        PrintCaptureEvidence(*evidenceSession, evidenceFacts, captureLifecycle);
        return 5;
    }
    IMMDevice* device = nullptr;
    const HRESULT hr = enumerator->GetDevice(id.c_str(), &device);
    if (rtlPreflight != nullptr) {
        evidenceFacts.getDevice.Record(hr);
        evidenceFacts.endpointAcquired = SUCCEEDED(hr) && device != nullptr;
        if (FAILED(hr) || device == nullptr) {
            evidenceFacts.criticalApiFailure = true;
            evidenceFacts.firstFailureHresult = static_cast<std::int32_t>(hr);
            evidenceFacts.firstFailureStage = "GetDevice";
        }
    }
    if (FAILED(hr) || device == nullptr) {
        if (renderChannelReport != nullptr) {
            renderChannelReport->lifecycleStatus = "ENDPOINT_ACQUISITION_FAILED";
            renderChannelReport->cleanupStatus = "NOT_ACQUIRED";
            renderChannelReport->errorStage = "GetDevice";
            renderChannelReport->errorHresult = Hresult(hr);
        }
        std::printf("GetDevice HRESULT: %s\nSTATUS: INITIALIZE_FAILED\nResult: endpoint could not be opened.\n",
                    Hresult(hr).c_str());
        if (channelMeterReport != nullptr) {
            channelMeterReport->status = ChannelMeter::RunStatus::Failed;
            channelMeterReport->errorStage = "GetDevice";
            channelMeterReport->errorHresult = static_cast<std::int32_t>(hr);
            channelMeterReport->errorHresultKnown = true;
            channelMeterReport->cleanupStatus = "NOT_ACQUIRED";
        }
        if (streamDuration != 0) PrintStreamNotStarted();
        Release(device);
        if (rtlPreflight != nullptr) {
            evidenceFacts.endpointReleased = device == nullptr;
            captureLifecycle.Transition(CaptureRuntimeEvidence::LifecycleState::Failed);
            PrintCaptureEvidence(*evidenceSession, evidenceFacts, captureLifecycle);
        }
        if (duplex != nullptr) {
            duplex->MarkPrepared(duplexSide, false);
            duplex->RecordClassification(duplexSide, ProbeMath::StreamStatus::InitializeFailed,
                                         ProbeMath::StreamQuality::SevereAnomalies);
        }
        return 1;
    }
    if (channelMeter != nullptr) {
        IMMEndpoint* endpoint = nullptr;
        EDataFlow actualFlow = eAll;
        HRESULT directionResult = device->QueryInterface(__uuidof(IMMEndpoint),
            reinterpret_cast<void**>(&endpoint));
        if (SUCCEEDED(directionResult) && endpoint != nullptr)
            directionResult = endpoint->GetDataFlow(&actualFlow);
        Release(endpoint);
        if (FAILED(directionResult) || actualFlow != eCapture) {
            channelMeterReport->status = ChannelMeter::RunStatus::Failed;
            channelMeterReport->errorStage = "Validate Capture endpoint direction";
            channelMeterReport->errorHresult = static_cast<std::int32_t>(directionResult);
            channelMeterReport->errorHresultKnown = FAILED(directionResult);
            channelMeterReport->cleanupStatus = "NOT_ACQUIRED";
            std::puts("CAPTURE METER ERROR: selected endpoint is not a verified Capture endpoint; no IAudioClient was activated.");
            Release(device);
            return 1;
        }
    }
    const int result = TestInitialize(device, flow, id, rate, channels, format, useMinimumPeriod,
                                      streamDuration, useProAudioMmcss, duplex, duplexSide, clockSeries,
                                      liveRing, measureSignalLevels, loopbackCapture, loopbackRender,
                                      monitorLatencyData, monitorLatencySignal, rtlPreflight,
                                      rtlPreflight != nullptr ? &evidenceFacts : nullptr,
                                      rtlPreflight != nullptr ? &captureLifecycle : nullptr,
                                      channelMeter, channelMeterReport,
                                      renderChannelSignal, renderChannelReport,
                                      streamReport, externalCancelEvent, customPeriod, explicitFormat, route);
    Release(device);
    if (streamReport != nullptr) streamReport->cleanup += ";ENDPOINT_RELEASE_ATTEMPTED";
    if (renderChannelReport != nullptr && renderChannelReport->cleanupStatus != "FAILED")
        renderChannelReport->cleanupStatus = "RELEASE_ATTEMPTED";
    if (rtlPreflight != nullptr) {
        const auto invalidated = [](const CaptureRuntimeEvidence::HresultFact& fact) {
            return (fact.state == CaptureRuntimeEvidence::Field::Failed ||
                    fact.state == CaptureRuntimeEvidence::Field::Succeeded) &&
                fact.value == static_cast<std::int32_t>(AUDCLNT_E_DEVICE_INVALIDATED);
        };
        evidenceFacts.deviceInvalidated = evidenceFacts.deviceInvalidated ||
            invalidated(evidenceFacts.getDevice) || invalidated(evidenceFacts.activateClient) ||
            invalidated(evidenceFacts.devicePeriod) || invalidated(evidenceFacts.formatSupport) ||
            invalidated(evidenceFacts.firstInitialize) || invalidated(evidenceFacts.alignmentGetBufferSize) ||
            invalidated(evidenceFacts.retryActivateClient) || invalidated(evidenceFacts.finalInitialize) ||
            invalidated(evidenceFacts.actualGetBufferSize) || invalidated(evidenceFacts.eventCreation) ||
            invalidated(evidenceFacts.setEventHandle) || invalidated(evidenceFacts.captureService) ||
            invalidated(evidenceFacts.start) || invalidated(evidenceFacts.stop);
        evidenceFacts.endpointReleased = device == nullptr;
        const auto current = captureLifecycle.State();
        if (current != CaptureRuntimeEvidence::LifecycleState::Completed &&
            current != CaptureRuntimeEvidence::LifecycleState::Failed &&
            current != CaptureRuntimeEvidence::LifecycleState::Cancelled) {
            captureLifecycle.Transition(evidenceFacts.cancelled
                ? CaptureRuntimeEvidence::LifecycleState::Cancelled
                : current == CaptureRuntimeEvidence::LifecycleState::Stopping &&
                    !evidenceFacts.criticalApiFailure && evidenceFacts.stop.Succeeded() && evidenceFacts.durationReached &&
                    (!evidenceFacts.clientAcquired || evidenceFacts.clientReleased) &&
                    (!evidenceFacts.eventAcquired || evidenceFacts.eventReleased) &&
                    (!evidenceFacts.captureService.Succeeded() || evidenceFacts.captureServiceReleased) &&
                    (!evidenceFacts.endpointAcquired || evidenceFacts.endpointReleased)
                    ? CaptureRuntimeEvidence::LifecycleState::Completed
                    : CaptureRuntimeEvidence::LifecycleState::Failed);
        }
        PrintCaptureEvidence(*evidenceSession, evidenceFacts, captureLifecycle);
    }
    if (streamDuration != 0) std::puts("Endpoint reference released: yes");
    if (duplex != nullptr) {
        if (!duplex->PreparationReported(duplexSide)) duplex->MarkPrepared(duplexSide, false);
        if (!duplex->Side(duplexSide).classified) {
            const auto status = result == 6 ? ProbeMath::StreamStatus::StartFailed
                : result == 7 || result == 5 ? ProbeMath::StreamStatus::StreamFailed
                : ProbeMath::StreamStatus::InitializeFailed;
            duplex->RecordClassification(duplexSide, status, ProbeMath::StreamQuality::SevereAnomalies);
        }
    }
    return result;
}

struct DuplexWorkerArgs {
    const char* label;
    EDataFlow flow;
    const std::wstring* endpointId;
    unsigned rate;
    unsigned channels;
    const std::wstring* format;
    bool useMinimumPeriod;
    unsigned duration;
    bool useProAudioMmcss;
    DuplexCoordinator* coordinator;
    int side;
    ClockAnalysis::Series* clockSeries;
    LiveTransport::SpscPcm16StereoRing* liveRing;
    bool measureSignalLevels;
    AnalogLoopback::CaptureData* loopbackCapture;
    AnalogLoopback::RenderSignal* loopbackRender;
    MonitorLatency::RunData* monitorLatencyData;
    AnalogLoopback::RenderSignal* monitorLatencySignal;
    std::optional<ExclusiveFormatMatrix::PeriodRequest> customPeriod;
    std::optional<ChannelRouting::Configuration> route;
};

void DuplexWorker(DuplexWorkerArgs args) {
    const HRESULT comResult = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    std::printf("\n[%s] CoInitializeEx(MTA) HRESULT: %s\n", args.label, Hresult(comResult).c_str());
    if (FAILED(comResult)) {
        args.coordinator->MarkPrepared(args.side, false);
        args.coordinator->RecordClassification(args.side, ProbeMath::StreamStatus::InitializeFailed,
                                                ProbeMath::StreamQuality::SevereAnomalies);
        return;
    }

    IMMDeviceEnumerator* enumerator = nullptr;
    const HRESULT createResult = CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL,
        __uuidof(IMMDeviceEnumerator), reinterpret_cast<void**>(&enumerator));
    std::printf("[%s] MMDeviceEnumerator creation HRESULT: %s\n", args.label, Hresult(createResult).c_str());
    if (FAILED(createResult) || enumerator == nullptr) {
        args.coordinator->MarkPrepared(args.side, false);
        args.coordinator->RecordClassification(args.side, ProbeMath::StreamStatus::InitializeFailed,
                                                ProbeMath::StreamQuality::SevereAnomalies);
    } else {
        std::optional<WAVEFORMATEXTENSIBLE> selectedFormat;
        auto rate=args.rate;
        auto channels=args.channels;
        auto formatName=*args.format;
        if(args.route){
            const auto& endpoint=args.flow==eCapture?args.route->capture:args.route->render;
            rate=endpoint.format.sampleRate;channels=endpoint.format.channels;
            formatName=endpoint.format.encoding==ExclusiveFormatMatrix::Encoding::Pcm16?L"pcm16":endpoint.format.encoding==ExclusiveFormatMatrix::Encoding::Pcm24In32?L"pcm24-in-32":L"float32";
            selectedFormat=ExclusiveFormatMatrix::BuildWasapiFormat(endpoint.format,endpoint.channelMask);
        }
        const int result = ProbeInitById(enumerator, args.flow, *args.endpointId, rate, channels,
            formatName, args.useMinimumPeriod, args.duration, args.useProAudioMmcss,
            args.coordinator, args.side, args.clockSeries, args.liveRing, args.measureSignalLevels,
            args.loopbackCapture, args.loopbackRender, args.monitorLatencyData,
            args.monitorLatencySignal, nullptr, nullptr, nullptr, nullptr, nullptr, nullptr, nullptr,
            args.customPeriod,selectedFormat?&*selectedFormat:nullptr,args.route?&*args.route:nullptr);
        if (!args.coordinator->PreparationReported(args.side)) args.coordinator->MarkPrepared(args.side, false);
        if (!args.coordinator->Side(args.side).classified) {
            const auto status = result == 6 ? ProbeMath::StreamStatus::StartFailed
                : result == 7 || result == 5 ? ProbeMath::StreamStatus::StreamFailed
                : ProbeMath::StreamStatus::InitializeFailed;
            args.coordinator->RecordClassification(args.side, status,
                                                    ProbeMath::StreamQuality::SevereAnomalies);
        }
    }
    Release(enumerator);
    CoUninitialize();
    std::printf("[%s] Worker COM and endpoint resources released.\n", args.label);
}

const char* ClockDataQualityText(ClockAnalysis::Status status) {
    switch (status) {
    case ClockAnalysis::Status::Available: return "CLEAN";
    case ClockAnalysis::Status::AvailableWithRejections: return "REJECTIONS_PRESENT";
    case ClockAnalysis::Status::InsufficientData: return "INSUFFICIENT";
    case ClockAnalysis::Status::UnreliableTimestamps: return "UNRELIABLE";
    case ClockAnalysis::Status::ClockApiFailed: return "API_ERROR";
    }
    return "INSUFFICIENT";
}

void PrintClockSide(const char* name, const ClockAnalysis::Series& series,
                    const ClockAnalysis::SideResult& result) {
    const auto serviceResult = series.serviceHresult == std::numeric_limits<std::int32_t>::min()
        ? "NOT_ATTEMPTED" : Hresult(static_cast<HRESULT>(series.serviceHresult));
    const auto frequencyResult = series.serviceHresult != ClockAnalysis::kSOk
        ? "NOT_ATTEMPTED" : Hresult(static_cast<HRESULT>(series.frequencyHresult));
    std::printf("%s clock: GetService HRESULT %s; GetFrequency HRESULT %s; frequency %llu\n",
        name, serviceResult.c_str(), frequencyResult.c_str(),
        static_cast<unsigned long long>(series.frequency));
    std::printf("%s snapshots: %zu total, %zu valid S_OK, %zu used in regression; "
                "S_FALSE %zu, API errors %zu, invalid QPC/sequence %zu/%zu, "
                "nonmonotonic positions %zu, unchanged positions %zu\n",
        name, result.snapshots, result.validSnapshots, result.analysisSnapshots,
        result.rejectedSFalse, result.rejectedApiErrors, result.invalidTimestamps,
        result.invalidSequences, result.nonmonotonicPositions, result.unchangedPositions);
    if (result.hasPositionApiError) {
        std::printf("%s first GetPosition error HRESULT: %s\n", name,
                    Hresult(static_cast<HRESULT>(result.firstPositionApiError)).c_str());
    }
    if (result.validSnapshots != 0) {
        std::printf("%s accepted QPC range: %llu..%llu (100 ns)\n", name,
            static_cast<unsigned long long>(result.qpcFirst100ns),
            static_cast<unsigned long long>(result.qpcLast100ns));
    } else {
        std::printf("%s accepted QPC range: unavailable\n", name);
    }
    std::printf("%s clock analysis status: %s; quality: %s\n", name,
        ClockAnalysis::StatusText(result.status), ClockDataQualityText(result.status));
    if (result.analysisSnapshots != 0) {
        std::printf("%s estimated rate relative to QPC: %.1f ppm; R-squared %.5f; RMS residual %.3f ms\n",
            name, result.ratePpm, result.rSquared, result.rmsResidualMilliseconds);
    } else {
        std::printf("%s estimated rate: unavailable\n", name);
    }
}

LiveTransport::MonitorStatus PrintLiveTransportDiagnostics(
    LiveTransport::SpscPcm16StereoRing& ring, const DuplexSideSummary& capture,
    const DuplexSideSummary& render, bool cancelled) {
    const auto stats = ring.GetStats();
    const bool captureCompleted = capture.status == ProbeMath::StreamStatus::StreamCompleted ||
        capture.status == ProbeMath::StreamStatus::StreamCompletedWithWarnings;
    const bool renderCompleted = render.status == ProbeMath::StreamStatus::StreamCompleted ||
        render.status == ProbeMath::StreamStatus::StreamCompletedWithWarnings;
    const bool streamWarning = capture.status != ProbeMath::StreamStatus::StreamCompleted ||
        render.status != ProbeMath::StreamStatus::StreamCompleted ||
        capture.quality != ProbeMath::StreamQuality::Clean ||
        render.quality != ProbeMath::StreamQuality::Clean;
    const auto status = LiveTransport::ClassifyMonitor(captureCompleted, renderCompleted,
        cancelled, streamWarning, stats);
    const double occupancyMilliseconds = stats.rate == 0 ? 0.0
        : static_cast<double>(stats.occupancyCurrent) * 1'000.0 / stats.rate;
    const double targetMilliseconds = stats.rate == 0 ? 0.0
        : static_cast<double>(stats.prefillFrames) * 1'000.0 / stats.rate;
    std::puts("\nLIVE TRANSPORT DIAGNOSTICS");
    std::printf("Initial output gain: -24 dB (fixed)\nCapture frames received: %llu\n"
                "Frames transferred to ring: %llu\nRender frames consumed: %llu\n"
                "Silence frames: %llu (prefill: %llu)\n"
                "Underrun events / frames: %llu / %llu\nOverflow events / frames dropped: %llu / %llu\n",
        static_cast<unsigned long long>(stats.captureFramesReceived),
        static_cast<unsigned long long>(stats.framesTransferred),
        static_cast<unsigned long long>(stats.renderFramesConsumed),
        static_cast<unsigned long long>(stats.silenceFrames),
        static_cast<unsigned long long>(stats.prefillSilenceFrames),
        static_cast<unsigned long long>(stats.underrunEvents),
        static_cast<unsigned long long>(stats.underrunFrames),
        static_cast<unsigned long long>(stats.overflowEvents),
        static_cast<unsigned long long>(stats.overflowFrames));
    std::printf("Ring occupancy min / average / max / current: %llu / %.1f / %llu / %llu frames\n"
                "Capture / Render WASAPI buffers: %u / %u frames\n"
                "Current buffer target (3 x larger actual buffer): %llu frames (%.3f ms); ring capacity: %llu frames\n"
                "Estimated current buffering: %.3f ms (not RTL or end-to-end latency)\n",
        static_cast<unsigned long long>(stats.occupancyMinimum), stats.occupancyAverage,
        static_cast<unsigned long long>(stats.occupancyMaximum),
        static_cast<unsigned long long>(stats.occupancyCurrent),
        stats.captureBufferFrames, stats.renderBufferFrames,
        static_cast<unsigned long long>(stats.prefillFrames), targetMilliseconds,
        static_cast<unsigned long long>(stats.capacityFrames), occupancyMilliseconds);
    if (stats.prefillCompleted) {
        std::printf("Initial prefill duration: %.3f ms\n",
            static_cast<double>(stats.prefillDurationMicroseconds) / 1'000.0);
    } else {
        std::puts("Initial prefill duration: not reached");
    }
    std::printf("Capture status: %s / %s\nRender status: %s / %s\nMONITOR_STATUS: %s\n",
        StreamStatusText(capture.status), StreamQualityText(capture.quality),
        StreamStatusText(render.status), StreamQualityText(render.quality),
        LiveTransport::MonitorStatusText(status));
    std::puts("Capture audio is transient in memory only; ring depth is buffering, not measured RTL.");
    return status;
}

void PrintSignalChannel(const char* label, const SignalLevels::Channel& channel) {
    std::printf("%s: Peak %.2f dBFS; RMS %.2f dBFS; nonzero / analyzed samples: %llu / %llu\n",
        label, channel.peakDbfs, channel.rmsDbfs,
        static_cast<unsigned long long>(channel.nonzeroSamples),
        static_cast<unsigned long long>(channel.analyzedSamples));
}

void PrintSignalDiagnostics(const DuplexSideSummary& capture, const DuplexSideSummary& render) {
    const auto& captureLevels = capture.signalLevels;
    const auto& renderLevels = render.signalLevels;
    std::puts("\nLIVE SIGNAL LEVEL DIAGNOSTICS");
    if (captureLevels.invalidData || captureLevels.overflowed) {
        std::puts("Capture levels: unavailable (invalid data or counter overflow)");
    } else {
        PrintSignalChannel("Capture L", captureLevels.channels[0]);
        PrintSignalChannel("Capture R", captureLevels.channels[1]);
    }
    std::printf("Capture packets analyzed: %llu; SILENT packets: %llu; zero-data packets: %llu; active frames: %llu\n",
        static_cast<unsigned long long>(captureLevels.packetsOrBuffers),
        static_cast<unsigned long long>(captureLevels.silentFlagCount),
        static_cast<unsigned long long>(captureLevels.zeroDataPacketsOrBuffers),
        static_cast<unsigned long long>(captureLevels.activeFrames));
    if (renderLevels.invalidData || renderLevels.overflowed) {
        std::puts("Render levels: unavailable (invalid data or counter overflow)");
    } else {
        PrintSignalChannel("Render L", renderLevels.channels[0]);
        PrintSignalChannel("Render R", renderLevels.channels[1]);
    }
    std::printf("Render SILENT buffers: %llu; zero-data buffers: %llu; prefill silence frames: %llu; underrun silence frames: %llu\n",
        static_cast<unsigned long long>(renderLevels.silentFlagCount),
        static_cast<unsigned long long>(renderLevels.zeroDataPacketsOrBuffers),
        static_cast<unsigned long long>(renderLevels.prefillSilenceFrames),
        static_cast<unsigned long long>(renderLevels.underrunSilenceFrames));
    const auto status = SignalLevels::Classify(captureLevels, renderLevels);
    std::printf("SIGNAL_STATUS: %s\n", SignalLevels::StatusText(status));
    if (SignalLevels::HasSingleChannelSignal(captureLevels))
        std::puts("SINGLE_CHANNEL_SIGNAL_DETECTED: Capture");
    if (SignalLevels::HasSingleChannelSignal(renderLevels))
        std::puts("SINGLE_CHANNEL_SIGNAL_DETECTED: Render");
    if (SignalLevels::HasLowSignalLevel(captureLevels))
        std::puts("LOW_SIGNAL_LEVEL: Capture peak below -60 dBFS (advisory only)");
    if (SignalLevels::HasLowSignalLevel(renderLevels))
        std::puts("LOW_SIGNAL_LEVEL: Render peak below -60 dBFS (advisory only)");
    std::puts("Digital signal presence does not establish audibility; signal levels do not change transport status or stream quality.");
}

void PrintClockTimestampAnomalies(const char* label,
                                  const std::array<AnalogLoopback::ClockSnapshotAnomaly,
                                                   AnalogLoopback::kTimestampAnomalyReportCount>& anomalies,
                                  std::size_t count,
                                  const AnalogLoopback::ClockStepAnomaly& strongestStep) {
    std::printf("%s IAudioClock: top %zu signed fit residuals\n", label, count);
    for (std::size_t i = 0; i < count; ++i) {
        const auto& anomaly = anomalies[i];
        const bool jump = std::abs(anomaly.signedAdjacentQpcStepMilliseconds) >=
            AnalogLoopback::kTimestampAnomalyDiagnosticFloorMilliseconds;
        const char* recovery = "no following valid snapshot";
        if (anomaly.hasNext) {
            const auto netFromBefore = anomaly.signedAdjacentQpcStepMilliseconds +
                anomaly.recoveryDeltaMilliseconds;
            if (std::abs(netFromBefore) < AnalogLoopback::kTimestampAnomalyDiagnosticFloorMilliseconds)
                recovery = "returned to previous fitted offset";
            else if (std::abs(anomaly.recoveryDeltaMilliseconds) <
                     AnalogLoopback::kTimestampAnomalyDiagnosticFloorMilliseconds)
                recovery = "offset remains at next snapshot";
            else
                recovery = "offset not settled at next snapshot";
        }
        std::printf("  Snapshot %zu (seq %u), stream %.3f ms, position %llu, QPC %llu: fit residual %+.4f ms; equivalent QPC offset %+.4f ms; adjacent step %+.4f ms [%s]; next delta %+.4f ms (%s)\n",
            anomaly.snapshotIndex + 1, anomaly.sequence, anomaly.streamTimeMilliseconds,
            static_cast<unsigned long long>(anomaly.position),
            static_cast<unsigned long long>(anomaly.qpc100ns),
            anomaly.signedFitResidualMilliseconds, anomaly.equivalentQpcOffsetMilliseconds,
            anomaly.signedAdjacentQpcStepMilliseconds, jump ? "JUMP" : "below diagnostic floor",
            anomaly.recoveryDeltaMilliseconds, recovery);
    }
    if (strongestStep.valid)
        std::printf("  Largest adjacent QPC step: snapshot %zu at stream %.3f ms, %+.4f ms (offset %+.4f → %+.4f ms)\n",
            strongestStep.snapshotIndex + 1, strongestStep.streamTimeMilliseconds,
            strongestStep.signedStepMilliseconds, strongestStep.beforeOffsetMilliseconds,
            strongestStep.afterOffsetMilliseconds);
}

void PrintTimestampAnomalyDiagnostics(const AnalogLoopback::TimestampAnomalyDiagnostics& diagnostics,
                                      const AnalogLoopback::Analysis& analysis) {
    std::puts("\nTIMESTAMP ANOMALY DETAILS (post-run)\nCapture packet QPC local deviations, largest absolute first:");
    for (std::size_t i = 0; i < diagnostics.capturePacketCount; ++i) {
        const auto& anomaly = diagnostics.capturePackets[i];
        std::printf("  Packet %zu, device position %llu, stream %.3f ms, QPC %llu: actual / expected delta %.4f / %.4f ms; signed residual %+.4f ms (%+.3f frames); fitted offset before / after %+.3f / %+.3f frames (%s)\n",
            anomaly.packetIndex + 1, static_cast<unsigned long long>(anomaly.devicePosition),
            anomaly.streamTimeMilliseconds, static_cast<unsigned long long>(anomaly.qpc100ns),
            anomaly.actualDeltaMilliseconds, anomaly.expectedDeltaMilliseconds,
            anomaly.signedResidualMilliseconds, anomaly.signedResidualFrames,
            anomaly.beforeOffsetFrames, anomaly.afterOffsetFrames,
            AnalogLoopback::CaptureOffsetStateText(anomaly.offsetState));
    }
    PrintClockTimestampAnomalies("Capture", diagnostics.captureClockResiduals,
        diagnostics.captureClockResidualCount, diagnostics.captureClockStrongestStep);
    PrintClockTimestampAnomalies("Render", diagnostics.renderClockResiduals,
        diagnostics.renderClockResidualCount, diagnostics.renderClockStrongestStep);
    const auto printMatch = [](const char* label, const AnalogLoopback::CommonModeMatch& match) {
        if (!match.matched) {
            std::printf("%s common-QPC-step match: none (comparison window %.3f ms)\n",
                label, match.comparisonWindowMilliseconds);
            return;
        }
        std::printf("%s common-QPC-step match: %s; Capture packet %zu ↔ clock QPC %llu; time separation %.4f ms (window %.3f ms); packet/clock steps %+.4f / %+.4f ms; magnitude difference %.4f ms\n",
            label, "same time/sign/magnitude",
            match.capturePacketIndex + 1, static_cast<unsigned long long>(match.clockEventQpc100ns),
            match.timeDifferenceMilliseconds, match.comparisonWindowMilliseconds,
            match.captureStepMilliseconds, match.clockStepMilliseconds,
            match.magnitudeDifferenceMilliseconds);
    };
    printMatch("Capture packet ↔ Capture clock", diagnostics.captureClockMatch);
    printMatch("Capture packet ↔ Render clock", diagnostics.renderClockMatch);
    std::printf("Common-mode comparison: steps ≥ %.3f ms, same sign, magnitude tolerance max(%.3f ms, 25%%); time window is measured from sample intervals and capped at %.0f ms. This is diagnostic matching, not a timing-quality criterion.\n",
        AnalogLoopback::kTimestampAnomalyDiagnosticFloorMilliseconds,
        AnalogLoopback::kTimestampAnomalyDiagnosticFloorMilliseconds,
        AnalogLoopback::kMaximumCommonModeWindowMilliseconds);
    std::printf("COMMON_MODE_TIMESTAMP_EVIDENCE: %s (diagnostic coincidence only; no component is attributed as the cause)\n",
        AnalogLoopback::CommonModeStateText(diagnostics.commonMode));
    const bool frameContinuityClean = analysis.discontinuities == 0 && analysis.framePositionGaps == 0;
    std::printf("TIMING_ALIGNMENT_STATE: %s\nAUDIO_FRAME_CONTINUITY: %s (discontinuities %llu; frame-position gaps %llu)\nMARKER_SEQUENCE_CONSISTENCY: %s (%zu matched / %zu missing / %zu contradictory)\n",
        AnalogLoopback::TimingQualityText(analysis.timingQuality),
        frameContinuityClean ? "CLEAN" : "ANOMALIES",
        static_cast<unsigned long long>(analysis.discontinuities),
        static_cast<unsigned long long>(analysis.framePositionGaps),
        AnalogLoopback::SequenceStatusText(analysis.sequence.status),
        analysis.sequence.matchedMarkers, analysis.sequence.missingMarkers,
        analysis.sequence.contradictoryMarkers);
}

AnalogLoopback::Analysis PrintAnalogLoopbackDiagnostics(
    const DuplexSideSummary& captureSide, const DuplexSideSummary& renderSide,
    const AnalogLoopback::CaptureData& capture, const AnalogLoopback::RenderSignal& render,
    const ClockAnalysis::Series& captureClock, const ClockAnalysis::Series& renderClock,
    const ClockAnalysis::Result& clockQuality) {
    const bool streamsCompleted = captureSide.status == ProbeMath::StreamStatus::StreamCompleted &&
        renderSide.status == ProbeMath::StreamStatus::StreamCompleted;
    const auto analysis = AnalogLoopback::Analyze(capture, render, captureClock, renderClock,
                                                   clockQuality, streamsCompleted);
    const auto timestampAnomalies = AnalogLoopback::AnalyzeTimestampAnomalies(
        capture.Packets(), capture.Rate(), analysis.capturePacketIntercept100ns,
        analysis.capturePacketSlope100nsPerFrame, captureClock, clockQuality.capture,
        renderClock, clockQuality.render);
    const auto captureBufferMs = captureSide.bufferFrames == 0 ? 0.0
        : static_cast<double>(captureSide.bufferFrames) * 1'000.0 / capture.Rate();
    const auto renderBufferMs = renderSide.bufferFrames == 0 ? 0.0
        : static_cast<double>(renderSide.bufferFrames) * 1'000.0 / render.Rate();
    const auto maxMarker = *std::max_element(render.Marker().begin(), render.Marker().end(),
        [](auto left, auto right) { return std::abs(static_cast<int>(left)) < std::abs(static_cast<int>(right)); });
    const auto absMarker = std::abs(static_cast<int>(maxMarker));
    const double markerPeakDbfs = 20.0 * std::log10(static_cast<double>(absMarker) / 32768.0);
    std::puts("\nPHYSICAL ANALOG LOOPBACK DIAGNOSTICS");
    std::printf("Capture channel: %u; Render channel: %u\n", capture.Channel(), render.Channel());
    std::printf("Capture Initialize period request / actual buffer: %lld (100 ns) / %u frames (%.3f ms)\n"
                "Render Initialize period request / actual buffer: %lld (100 ns) / %u frames (%.3f ms)\n",
        static_cast<long long>(captureSide.requestedPeriod), captureSide.bufferFrames, captureBufferMs,
        static_cast<long long>(renderSide.requestedPeriod), renderSide.bufferFrames, renderBufferMs);
    std::printf("Test signal peak: %.2f dBFS; marker length / count: %u / %zu frames\n",
        markerPeakDbfs, AnalogLoopback::kMarkerFrames, render.MarkerStarts().size());
    const double formedPeakDbfs = render.Peak() == 0 ? -std::numeric_limits<double>::infinity()
        : 20.0 * std::log10(static_cast<double>(render.Peak()) / 32768.0);
    std::printf("Render PCM formed: selected-channel nonzero frames %llu; peak %.2f dBFS; markers started / fully submitted %llu / %llu; submission timeline %s\n",
        static_cast<unsigned long long>(render.NonzeroFrames()), formedPeakDbfs,
        static_cast<unsigned long long>(render.MarkersStarted()),
        static_cast<unsigned long long>(render.MarkersCompleted()),
        render.SubmissionContinuous() ? "continuous" : "gap detected");
    std::printf("Measured Capture peak / RMS: %.2f / %.2f dBFS; clipped samples: %llu\n",
        analysis.capturePeakDbfs, analysis.captureRmsDbfs,
        static_cast<unsigned long long>(analysis.captureClippedSamples));
    const auto captureSilent = std::count_if(capture.Packets().begin(), capture.Packets().end(),
        [](const auto& packet) { return packet.silent; });
    std::printf("Capture frames stored: %llu; packets: %zu; SILENT packets: %llu; discontinuities: %llu; timestamp errors: %llu; device-position gaps: %llu%s\n",
        static_cast<unsigned long long>(capture.StoredFrames()), capture.Packets().size(),
        static_cast<unsigned long long>(captureSilent),
        static_cast<unsigned long long>(analysis.discontinuities),
        static_cast<unsigned long long>(analysis.timestampErrors),
        static_cast<unsigned long long>(analysis.positionGaps),
        capture.Truncated() ? "; storage truncated" : "");
    const double bestConfidence = analysis.peaks.empty() ? 0.0 : std::max_element(
        analysis.peaks.begin(), analysis.peaks.end(), [](const auto& left, const auto& right) {
            return left.confidence < right.confidence;
        })->confidence;
    double minimumConfidence = analysis.peaks.empty() ? 0.0 : 1.0;
    double minimumMargin = analysis.peaks.empty() ? 0.0 : 1.0;
    for (const auto& peak : analysis.peaks) {
        minimumConfidence = std::min(minimumConfidence, peak.confidence);
        minimumMargin = std::min(minimumMargin, peak.ambiguityMargin);
    }
    std::printf("Per-marker threshold detections: %zu; normalized correlation best / min / ambiguity margin min: %.3f / %.3f / %.3f (threshold >= %.2f; ambiguity margin >= %.2f)\n",
        analysis.peaks.size(), bestConfidence, minimumConfidence, minimumMargin,
        AnalogLoopback::kMinimumCorrelation, AnalogLoopback::kMinimumAmbiguityMargin);
    if (analysis.correlation.available) {
        std::printf("Global maximum absolute normalized correlation: %.6f at %.3f samples; polarity %s\n"
                    "Independent candidate peaks above / below %.2f threshold: %llu / %llu\n",
            analysis.correlation.maximum, analysis.correlation.maximumOffsetFrames,
            analysis.correlation.maximumPolarity < 0 ? "inverted" : "normal",
            AnalogLoopback::kMinimumCorrelation,
            static_cast<unsigned long long>(analysis.correlation.candidatesAboveThreshold),
            static_cast<unsigned long long>(analysis.correlation.candidatesBelowThreshold));
        for (std::size_t i = 0; i < analysis.correlation.bestIndependentCandidates.size(); ++i) {
            const auto& candidate = analysis.correlation.bestIndependentCandidates[i];
            std::printf("Best independent candidate %zu: score %.6f, offset %.3f samples, polarity %s\n",
                i + 1, candidate.confidence, candidate.offsetFrames,
                candidate.polarity < 0 ? "inverted" : "normal");
        }
    } else {
        std::puts("Global maximum absolute normalized correlation: unavailable (no complete marker window)");
    }
    std::printf("Per-marker threshold candidates above / below: %llu / %llu; threshold %.2f\n",
        static_cast<unsigned long long>(analysis.correlation.candidatesAboveThreshold),
        static_cast<unsigned long long>(analysis.correlation.candidatesBelowThreshold),
        AnalogLoopback::kMinimumCorrelation);
    std::printf("SEQUENCE_STATUS: %s\nMatched / missing / contradictory markers: %zu / %zu / %zu of %zu\n"
                "Mean matched correlation: %.4f (floor %.2f); sequence coherence score / ambiguity margin: %.4f / %.4f (floors %.2f / %.2f)\n"
                "Estimated marker spacing: %.3f frames (nominal %u); offset consistency RMS / max: %.3f / %.3f frames\n"
                "Sequence global polarity: %s; rejection reason: %s\n",
        AnalogLoopback::SequenceStatusText(analysis.sequence.status),
        analysis.sequence.matchedMarkers, analysis.sequence.missingMarkers,
        analysis.sequence.contradictoryMarkers, analysis.sequence.expectedMarkers,
        analysis.sequence.meanMatchedCorrelation, AnalogLoopback::kSequenceMinimumMeanCorrelation,
        analysis.sequence.coherenceScore, analysis.sequence.ambiguityMargin,
        AnalogLoopback::kSequenceMinimumScore, AnalogLoopback::kSequenceMinimumAmbiguityMargin,
        analysis.sequence.estimatedSpacingFrames, AnalogLoopback::kMarkerSpacingFrames,
        analysis.sequence.offsetConsistencyRmsFrames, analysis.sequence.offsetConsistencyMaximumFrames,
        analysis.sequence.globalPolarity < 0 ? "inverted" : "normal",
        analysis.sequence.rejectionReason);
    std::printf("Loopback analysis rejection reason: %s\n", analysis.rejectionReason);
    std::printf("Clock / timestamp alignment quality: %s; Capture/Render valid clock snapshots: %zu / %zu; render interpolation bracket max: %.3f ms\n"
                "Capture packet fit QPC intercept at position 0 / slope: %.0f / %.9f (100 ns/frame)\n"
                "Capture packet position/QPC fit: rate error %.1f ppm (limit ±%.0f); R-squared %.5f (minimum %.2f); residual RMS / max / local interval deviation %.3f / %.3f / %.3f frames\n",
        ClockAnalysis::StatusText(clockQuality.status), clockQuality.capture.validSnapshots,
        clockQuality.render.validSnapshots, analysis.maximumRenderBracketMilliseconds,
        static_cast<double>(analysis.capturePacketIntercept100ns),
        static_cast<double>(analysis.capturePacketSlope100nsPerFrame),
        analysis.capturePacketRateErrorPpm, AnalogLoopback::kMaximumCaptureRateErrorPpm,
        analysis.capturePacketRSquared, AnalogLoopback::kMinimumTimelineRSquared,
        analysis.capturePacketResidualFrames, analysis.capturePacketMaximumResidualFrames,
        analysis.capturePacketMaximumLocalIntervalDeviationFrames);
    if (clockQuality.hasRegression) {
        std::printf("Clock fit R-squared Capture/Render: %.5f / %.5f; residual RMS Capture/Render: %.4f / %.4f ms; max: %.4f / %.4f ms\n",
            clockQuality.capture.rSquared, clockQuality.render.rSquared,
            clockQuality.capture.rmsResidualMilliseconds, clockQuality.render.rmsResidualMilliseconds,
            clockQuality.capture.maximumResidualMilliseconds, clockQuality.render.maximumResidualMilliseconds);
    }
    PrintTimestampAnomalyDiagnostics(timestampAnomalies, analysis);
    for (std::size_t i = 0; i < analysis.sequence.matches.size(); ++i) {
        const auto& sequenceMatch = analysis.sequence.matches[i];
        const auto& peak = sequenceMatch.peak;
        if (i < analysis.delays.size()) {
            const auto& delay = analysis.delays[i];
            std::printf("Marker %zu (render sequence %zu): capture offset %.3f frames / device position %llu; render marker %.0f frames; delay %.3f samples (%.6f ms); confidence %.3f; margin %.3f\n",
                i + 1, sequenceMatch.markerIndex + 1, delay.captureFrame,
                static_cast<unsigned long long>(delay.captureDevicePosition), delay.renderFrame,
                delay.samples, delay.milliseconds, delay.confidence, delay.ambiguityMargin);
        } else {
            std::printf("Sequence marker %zu (Render index %zu): Capture offset %.3f frames; signed polarity %s; score %.3f; time mapping unavailable\n",
                i + 1, sequenceMatch.markerIndex + 1, peak.offsetFrames,
                peak.polarity < 0 ? "inverted" : "normal", peak.confidence);
        }
    }
    if (analysis.delays.size() >= AnalogLoopback::kMinimumMarkers &&
        std::isfinite(analysis.delayVariationSamples)) {
        const double medianMs = AnalogLoopback::SamplesToMilliseconds(analysis.medianSamples, render.Rate());
        const double minimumMs = AnalogLoopback::SamplesToMilliseconds(analysis.minimumSamples, render.Rate());
        const double maximumMs = AnalogLoopback::SamplesToMilliseconds(analysis.maximumSamples, render.Rate());
        std::printf("Delay median / min / max: %.3f / %.3f / %.3f samples; %.6f / %.6f / %.6f ms; variation %.3f samples (%.6f ms)\n",
            analysis.medianSamples, analysis.minimumSamples, analysis.maximumSamples,
            medianMs, minimumMs, maximumMs, analysis.maximumSamples - analysis.minimumSamples,
            maximumMs - minimumMs);
    }
    const auto toMs = [&](double frames) {
        return AnalogLoopback::SamplesToMilliseconds(frames, render.Rate());
    };
    std::printf("TIMING_QUALITY: %s; reason: %s\n"
                "Timing limits: GOOD ≤ %.3f ms; WARNING ≤ %.3f ms; render clock bracket max GOOD/WARNING %.0f/%.0f ms\n"
                "Capture packet residual RMS / max / local QPC interval deviation: %.3f / %.3f / %.3f frames (%.4f / %.4f / %.4f ms)\n"
                "Delay full-set range / max adjacent jump / fitted trend: %.3f / %.3f / %.3f frames (%.4f / %.4f / %.4f ms)\n",
        AnalogLoopback::TimingQualityText(analysis.timingQuality), analysis.timingQualityReason,
        toMs(AnalogLoopback::kGoodTimingErrorFrames), toMs(AnalogLoopback::kWarningTimingErrorFrames),
        AnalogLoopback::kGoodRenderClockBracketMilliseconds,
        AnalogLoopback::kWarningRenderClockBracketMilliseconds,
        analysis.capturePacketResidualFrames, analysis.capturePacketMaximumResidualFrames,
        analysis.capturePacketMaximumLocalIntervalDeviationFrames,
        toMs(analysis.capturePacketResidualFrames), toMs(analysis.capturePacketMaximumResidualFrames),
        toMs(analysis.capturePacketMaximumLocalIntervalDeviationFrames),
        analysis.delayVariationSamples, analysis.maximumLocalDelayJumpSamples, analysis.delayTrendSamples,
        toMs(analysis.delayVariationSamples), toMs(analysis.maximumLocalDelayJumpSamples),
        toMs(analysis.delayTrendSamples));
    if (analysis.sequence.status == AnalogLoopback::SequenceStatus::Matched &&
        analysis.timingQuality == AnalogLoopback::Analysis::TimingQuality::Unreliable)
        std::puts("TIMING_ALIGNMENT_UNRELIABLE: QPC-correlated stream positions or Capture packet timestamps did not meet the validity checks.");
    else if (analysis.sequence.status == AnalogLoopback::SequenceStatus::NotFound)
        std::puts("No marker was detected; no positive loopback delay is reported.");
    std::printf("LOOPBACK_STATUS: %s\n", AnalogLoopback::StatusText(analysis.status));
    std::puts("Method: match the selected-channel marker, map its Render stream-relative frame through IAudioClock position/QPC, and map the captured frame through Capture device-position/QPC packet anchors on the shared 100-ns QPC scale.");
    std::puts("Limitations: Windows/driver timestamp alignment and interpolation residuals remain sources of uncertainty; this is physical analog Render-to-Capture path delay, not B4-A Live Monitor delay, driver-only latency, speaker acoustics, or RTL over speakers.");
    return analysis;
}

void PrintMonitorLatencyDiagnostics(const MonitorLatency::RunData& data,
                                    const AnalogLoopback::RenderSignal& signal,
                                    const ClockAnalysis::Series& captureClock,
                                    const ClockAnalysis::Series& renderClock,
                                    const MonitorLatency::Analysis& analysis,
                                    const LiveTransport::Stats& ring,
                                    const DuplexSideSummary& capture,
                                    const DuplexSideSummary& render,
                                    bool cancelled) {
    std::puts("\nLIVE MONITOR TRANSPORT LATENCY DIAGNOSTICS");
    std::printf("Capture/Render status: %s / %s\n",
        StreamStatusText(capture.status), StreamStatusText(render.status));
    const double sourcePeakDbfs = signal.Peak() == 0 ? -std::numeric_limits<double>::infinity()
        : 20.0 * std::log10(static_cast<double>(signal.Peak()) / 32768.0);
    const double renderPeakDbfs = signal.Peak() == 0 ? sourcePeakDbfs
        : 20.0 * std::log10(static_cast<double>(signal.Peak()) * LiveTransport::kOutputGain / 32768.0);
    std::printf("Test PCM generated: %llu nonzero frames, source/output peak %.2f/%.2f dBFS; markers started/completed at Capture source: %llu/%llu; confirmed complete Render markers: %zu\n",
        static_cast<unsigned long long>(signal.NonzeroFrames()),
        sourcePeakDbfs, renderPeakDbfs,
        static_cast<unsigned long long>(signal.MarkersStarted()),
        static_cast<unsigned long long>(signal.MarkersCompleted()), data.renderMarkers.size());
    std::printf("Ring prefill: %llu frames (%.3f ms); duration: %.3f ms; occupancy at marker capture/Render is recorded per pair\n",
        static_cast<unsigned long long>(ring.prefillFrames),
        ring.rate == 0 ? 0.0 : static_cast<double>(ring.prefillFrames) * 1'000.0 / ring.rate,
        static_cast<double>(ring.prefillDurationMicroseconds) / 1'000.0);
    std::printf("Ring underrun events/frames: %llu/%llu; overflow events/frames: %llu/%llu; occupancy min/avg/max/current: %llu/%.1f/%llu/%llu frames\n",
        static_cast<unsigned long long>(ring.underrunEvents),
        static_cast<unsigned long long>(ring.underrunFrames),
        static_cast<unsigned long long>(ring.overflowEvents),
        static_cast<unsigned long long>(ring.overflowFrames),
        static_cast<unsigned long long>(ring.occupancyMinimum), ring.occupancyAverage,
        static_cast<unsigned long long>(ring.occupancyMaximum),
        static_cast<unsigned long long>(ring.occupancyCurrent));
    std::printf("Capture packet anchors: %zu; Capture/Render IAudioClock snapshots: %zu/%zu\n",
        data.capturePackets.size(), captureClock.snapshots.size(), renderClock.snapshots.size());
    std::printf("Capture timestamp errors/discontinuities/GetPosition-QPC bracket errors: %zu/%zu/%zu\n",
        data.captureTimestampErrors, data.captureDiscontinuities, data.captureClockQpcBracketErrors);
    const auto clockResult = ClockAnalysis::Analyze(captureClock, renderClock);
    PrintClockSide("Capture", captureClock, clockResult.capture);
    PrintClockSide("Render", renderClock, clockResult.render);
    std::printf("MONITOR_SEQUENCE_STATUS: %s (%zu provenance pairs, %zu missing, %zu rejected for timing/continuity)\n",
        MonitorLatency::SequenceStatusText(analysis.sequenceStatus), analysis.pairedMarkers,
        analysis.missingMarkers, analysis.rejectedMarkers);
    std::printf("Capture packet/Capture IAudioClock QPC boundaries: %zu/%zu; Capture clock continuity regions: %zu\n",
        analysis.capturePacketQpcBoundaries, analysis.captureClockQpcBoundaries,
        analysis.captureClockSegmentCount);
    const auto printSegments = [](const char* side, const auto& segments, std::size_t count) {
        for (std::size_t i = 0; i < count; ++i) {
            const auto& segment = segments[i];
            std::printf("  %s continuity region %zu: anchors %zu, frames %llu..%llu, whole-region residual RMS/max %.3f/%.3f frames (diagnostic only; per-marker local fits decide acceptance)\n",
                side, i + 1, segment.anchorCount,
                static_cast<unsigned long long>(segment.firstFrame),
                static_cast<unsigned long long>(segment.lastFrame), segment.rmsResidualFrames,
                segment.maximumResidualFrames);
        }
    };
    printSegments("Capture packet QPC", analysis.captureSegments, analysis.captureSegmentCount);
    printSegments("Capture IAudioClock", analysis.captureClockSegments,
        analysis.captureClockSegmentCount);
    const auto printLocalFit = [](const char* side, const MonitorLatency::LocalFit& fit) {
        std::printf("    %s local fit: %zu/%zu anchors used/candidate, RMS/max %.3f/%.3f frames, rejected QPC outliers %zu",
            side, fit.anchorCount, fit.candidateAnchors, fit.rmsResidualFrames, fit.maximumResidualFrames,
            fit.rejectedOutliers);
        if (fit.rejectedOutliers != 0) {
            std::printf(" at snapshot/packet indices");
            for (std::size_t i = 0; i < fit.rejectedOutliers; ++i)
                std::printf(" %zu", fit.outlierIndices[i] + 1);
        }
        if (!fit.fitted) std::printf("; rejected: %s", fit.rejectionReason);
        std::puts("");
    };
    std::puts("Per-marker provenance/timestamp audit (packet-relative markers are generated in software after GetBuffer):");
    for (std::size_t i = 0; i < analysis.markerDiagnosticCount; ++i) {
        const auto& marker = analysis.markerDiagnostics[i];
        std::printf("  Marker %u: %s — %s\n", marker.markerIndex + 1,
            MonitorLatency::MarkerReasonText(marker.reason), marker.detail);
        std::printf("    Source first frame=%llu; Render first/last frames=%llu/%llu; first-frame handoff=%.4f ms, full-marker handoff=%.4f ms; first ReleaseBuffer QPC=%llu..%llu; final ReleaseBuffer QPC=%llu..%llu (100 ns)\n",
            static_cast<unsigned long long>(marker.sourceFirstFrame),
            static_cast<unsigned long long>(marker.renderFirstFrame),
            static_cast<unsigned long long>(marker.renderLastFrame),
            marker.softwareFirstFrameMilliseconds, marker.softwareTransportMilliseconds,
            static_cast<unsigned long long>(marker.firstSubmissionBeginQpc100ns),
            static_cast<unsigned long long>(marker.firstSubmissionEndQpc100ns),
            static_cast<unsigned long long>(marker.fullSubmissionBeginQpc100ns),
            static_cast<unsigned long long>(marker.fullSubmissionEndQpc100ns));
        if (marker.packetQpc100ns != 0) {
            std::printf("    Capture packet first-frame QPC=%llu; GetBuffer service QPC=%llu; marker offset=%llu frames, packet-position-implied marker QPC=%s (all 100 ns)",
                static_cast<unsigned long long>(marker.packetQpc100ns),
                static_cast<unsigned long long>(marker.packetServiceQpc100ns),
                static_cast<unsigned long long>(marker.markerOffsetFrames),
                marker.markerQpc100ns == 0 ? "unavailable" : "available");
            if (marker.markerQpc100ns != 0)
                std::printf(" %llu (100 ns)", static_cast<unsigned long long>(marker.markerQpc100ns));
            std::printf("; marker generation QPC=%llu..%llu, Ring Push QPC=%llu..%llu, Render ReleaseBuffer QPC=%llu..%llu (all 100 ns)\n",
                static_cast<unsigned long long>(marker.markerGenerationBeginQpc100ns),
                static_cast<unsigned long long>(marker.markerGenerationEndQpc100ns),
                static_cast<unsigned long long>(marker.ringPushBeginQpc100ns),
                static_cast<unsigned long long>(marker.ringPushEndQpc100ns),
                static_cast<unsigned long long>(marker.renderSubmissionBeginQpc100ns),
                static_cast<unsigned long long>(marker.renderSubmissionEndQpc100ns));
            std::printf("    Capture packet/IAudioClock rate disagreement over local window: %.3f frames; signed position-origin offset %.3f frames; local rate delta %.2f ppm\n",
                marker.captureClockAgreementFrames,
                marker.captureClockOffsetFrames, marker.captureClockRateDifferencePpm);
            std::printf("    Push − packet timestamp=%.4f..%.4f ms; Push − packet-position-implied marker time=%.4f..%.4f ms; Push → ReleaseBuffer completion=%.4f ms\n",
                marker.pushMinusPacketBeginMilliseconds, marker.pushMinusPacketEndMilliseconds,
                marker.pushMinusMarkerBeginMilliseconds, marker.pushMinusMarkerEndMilliseconds,
                marker.softwareTransportMilliseconds);
        }
        if (marker.captureFit.candidateAnchors != 0)
            printLocalFit("Capture", marker.captureFit);
        if (marker.captureClockFit.candidateAnchors != 0)
            printLocalFit("Capture IAudioClock", marker.captureClockFit);
    }
    std::printf("Capture position-domain relation: signed IAudioClock-minus-packet offset median %.3f frames, local variation %.3f frames; rate difference %.2f ppm (offset is not an absolute-time validation)\n",
        analysis.captureClockOffsetMedianFrames, analysis.captureClockOffsetVariationFrames,
        analysis.captureClockRateDifferencePpm);
    std::printf("SOFTWARE_TRANSPORT_STATUS: %s (completed-marker software timing only)\n",
        analysis.softwareTransportMeasured ? "MEASURED" : "INCOMPLETE");
    std::printf("SOFTWARE_FIRST_FRAME_HANDOFF: count=%zu; median/min/max/spread=%.4f/%.4f/%.4f/%.4f ms\n",
        analysis.firstFrameHandoffSamples, analysis.firstFrameHandoffMedianMilliseconds,
        analysis.firstFrameHandoffMinimumMilliseconds, analysis.firstFrameHandoffMaximumMilliseconds,
        analysis.firstFrameHandoffSpreadMilliseconds);
    std::printf("SOFTWARE_FULL_MARKER_HANDOFF: count=%zu; median/min/max/spread=%.4f/%.4f/%.4f/%.4f ms\n",
        analysis.softwareTransportSamples, analysis.softwareTransportMedianMilliseconds,
        analysis.softwareTransportMinimumMilliseconds, analysis.softwareTransportMaximumMilliseconds,
        analysis.fullMarkerHandoffSpreadMilliseconds);
    std::puts("Render playback time: NOT MEASURED; ReleaseBuffer QPC marks successful buffer submission, not DAC playback. No per-marker playback estimate is available.");
    std::puts("CAPTURE_ABSOLUTE_ALIGNMENT: UNRESOLVED (the position-origin offset is observed, but its absolute correctness cannot be inferred from these independent origins).");
    std::printf("MONITOR_LATENCY_STATUS: %s\n", MonitorLatency::StatusText(analysis.status));
    if (analysis.rejectionReason[0] != '\0')
        std::printf("Latency measurement rejection reason: %s\n", analysis.rejectionReason);
    std::puts("Method: Capture packet positions/QPC and Capture IAudioClock position/frequency/QPC are fit independently in local windows and compared at common QPC. Frame-origin offset is reported separately from rate agreement and residual quality; the unknown absolute origin is not removed to claim accuracy. Render GetCurrentPadding is not used: Microsoft documents it as typically unnecessary for event-driven Exclusive streams.");
    std::puts("Capture QPC semantics: GetBuffer QPC timestamps the endpoint recording the packet's first-frame position, not GetBuffer service time. The synthetic marker is created only after packet receipt; its packet-position-implied timestamp is not its acquisition time. QPC values around marker generation, Push, and successful ReleaseBuffer report software scheduling only.");
    std::puts("Scope: the measured metric is Ring Push completion → Render ReleaseBuffer completion. It excludes actual Render playback/DAC time, Capture acquisition before packet service, physical RTL, and full microphone-monitor latency; ring prefill is not a latency measurement.");
    if (cancelled) std::puts("Operator cancellation: test did not reach its requested duration and cannot be MEASURED.");
}

std::optional<std::string> MatrixReportId();
bool WriteNewJson(const std::wstring& path, const std::string& json);

int RunDuplex(const std::wstring& captureId, const std::wstring& renderId,
              unsigned rate, unsigned channels, const std::wstring& format,
              bool useMinimumPeriod, unsigned duration, bool useProAudioMmcss, bool clockProbe,
              bool liveMonitor = false, bool measureSignalLevels = false,
              bool analogLoopback = false, unsigned captureChannel = 1,
              unsigned renderChannel = 1, bool monitorLatency = false,
              const std::wstring& outputJson = {}, HANDLE externalCancelEvent = nullptr,
              std::string operationId = {},
              std::optional<ExclusiveFormatMatrix::PeriodRequest> customPeriod = std::nullopt,
              bool sustainedValidation = false,
              std::optional<ChannelRouting::Configuration> route = std::nullopt,
              std::optional<ExclusiveFormatMatrix::PeriodRequest> captureCustomPeriod = std::nullopt,
              std::optional<ExclusiveFormatMatrix::PeriodRequest> renderCustomPeriod = std::nullopt) {
    HANDLE stopEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    const DWORD eventError = stopEvent == nullptr ? GetLastError() : ERROR_SUCCESS;
    if (stopEvent == nullptr) {
        const HRESULT error = eventError == ERROR_SUCCESS ? E_FAIL : HRESULT_FROM_WIN32(eventError);
        std::printf("Duplex stop event creation failed: %s\n", Hresult(error).c_str());
        return 1;
    }

    InterlockedExchange(&g_streamCancelRequested, 0);
    LiveTransport::SpscPcm16StereoRing liveRing;
    MonitorLatency::RunData monitorLatencyData;
    if (monitorLatency && !monitorLatencyData.Configure(8'192, MonitorLatency::kMaximumMarkers)) {
        std::puts("Monitor latency provenance storage allocation failed before device activation.");
        CloseHandle(stopEvent);
        return 1;
    }
    if (monitorLatency) {
        LARGE_INTEGER frequency{};
        if (!QueryPerformanceFrequency(&frequency) || frequency.QuadPart <= 0) {
            std::puts("Monitor latency QPC frequency query failed before device activation.");
            CloseHandle(stopEvent);
            return 1;
        }
        monitorLatencyData.qpcFrequency = static_cast<std::uint64_t>(frequency.QuadPart);
    }
    AnalogLoopback::CaptureData loopbackCapture(captureChannel);
    AnalogLoopback::RenderSignal loopbackRender(renderChannel);
    AnalogLoopback::RenderSignal monitorLatencySignal(1);
    const bool liveTransportEnabled = liveMonitor || monitorLatency;
    DuplexCoordinator coordinator(stopEvent, liveTransportEnabled ? &liveRing : nullptr,
        monitorLatency ? &monitorLatencySignal : nullptr, duration);
    g_duplexStopEvent.store(stopEvent);
    g_activeDuplexCoordinator.store(&coordinator);
    const BOOL handlerRegistered = SetConsoleCtrlHandler(HandleStreamConsoleControl, TRUE);
    const DWORD handlerError = handlerRegistered ? ERROR_SUCCESS : GetLastError();
    const HRESULT handlerResult = handlerRegistered ? S_OK
        : (handlerError == ERROR_SUCCESS ? E_FAIL : HRESULT_FROM_WIN32(handlerError));
    std::printf("Duplex console cancellation handler HRESULT: %s\n", Hresult(handlerResult).c_str());
    if (!handlerRegistered) {
        g_activeDuplexCoordinator.store(nullptr);
        g_duplexStopEvent.store(nullptr);
        CloseHandle(stopEvent);
        return 1;
    }

    std::atomic<bool> cancelWatcherDone{false};
    std::thread cancelWatcher;
    if (externalCancelEvent != nullptr) {
        try {
            cancelWatcher = std::thread([&] {
                while (!cancelWatcherDone.load(std::memory_order_acquire)) {
                    const DWORD wait = WaitForSingleObject(externalCancelEvent, 25);
                    if (wait == WAIT_OBJECT_0) { coordinator.RequestStop(true); return; }
                    if (wait == WAIT_FAILED) { coordinator.RequestStop(false); return; }
                }
            });
        } catch (const std::system_error&) {
            coordinator.RequestStop(false);
            SetConsoleCtrlHandler(HandleStreamConsoleControl, FALSE);
            g_activeDuplexCoordinator.store(nullptr);
            g_duplexStopEvent.store(nullptr);
            CloseHandle(stopEvent);
            return 1;
        }
    }

    if (liveMonitor) {
        std::puts("Mode: ACTIVE LIVE PCM16 MONITOR; Exclusive Capture is attenuated and sent to the selected Render endpoint.");
        std::puts("SAFETY: use headphones, minimum hardware output volume, and acoustically isolate output from the microphone.");
        std::puts("Output is fixed at -24 dB; no test tone or automatic gain increase is used.");
    } else if (analogLoopback) {
        std::puts("Mode: ACTIVE PHYSICAL ANALOG LOOPBACK; a fixed -36 dBFS deterministic marker is sent only to the selected Render channel.");
        std::puts("Safety: disconnect microphones, 48 V phantom power, speakers and direct hardware input monitoring before the analog cable test.");
    } else if (monitorLatency) {
        std::puts("Mode: ACTIVE SYNTHETIC Capture-to-Ring-to-Render software handoff timing test; no playback-time claim, physical loopback, or microphone routing.");
        std::puts("Safety: fixed -36 dBFS marker is attenuated by the existing -24 dB ring gain (about -60 dBFS peak); both confirmations are required.");
    } else {
        std::puts("Mode: ACTIVE WASAPI Exclusive Duplex; independent Capture and Render workers.");
        std::puts("Audio is not routed: Capture samples are discarded and Render submits WASAPI silence only.");
    }
    ClockAnalysis::Series captureClock;
    ClockAnalysis::Series renderClock;
    DuplexWorkerArgs captureArgs{ "Capture", eCapture, &captureId, rate, channels, &format,
        useMinimumPeriod, duration, useProAudioMmcss, &coordinator, 0,
        (clockProbe || analogLoopback || monitorLatency) ? &captureClock : nullptr,
        liveTransportEnabled ? &liveRing : nullptr, measureSignalLevels,
        analogLoopback ? &loopbackCapture : nullptr, nullptr,
        monitorLatency ? &monitorLatencyData : nullptr,
        monitorLatency ? &monitorLatencySignal : nullptr, captureCustomPeriod ? captureCustomPeriod : customPeriod };
    DuplexWorkerArgs renderArgs{ "Render", eRender, &renderId, rate, channels, &format,
        useMinimumPeriod, duration, useProAudioMmcss, &coordinator, 1,
        (clockProbe || analogLoopback || monitorLatency) ? &renderClock : nullptr,
        liveTransportEnabled ? &liveRing : nullptr, measureSignalLevels,
        nullptr, analogLoopback ? &loopbackRender : nullptr,
        monitorLatency ? &monitorLatencyData : nullptr,
        monitorLatency ? &monitorLatencySignal : nullptr, renderCustomPeriod ? renderCustomPeriod : customPeriod };
    captureArgs.route=route;
    renderArgs.route=route;
    std::thread captureThread;
    std::thread renderThread;
    bool threadCreationSucceeded = true;
    try {
        captureThread = std::thread(DuplexWorker, captureArgs);
        renderThread = std::thread(DuplexWorker, renderArgs);
    } catch (const std::exception& error) {
        std::printf("Duplex worker creation failed: %s\n", error.what());
        threadCreationSucceeded = false;
        coordinator.RequestStop(false);
    }

    bool bothPrepared = false;
    if (threadCreationSucceeded) {
        bothPrepared = coordinator.AwaitBothPrepared();
        if (monitorLatency && bothPrepared)
            monitorLatencyData.expectedMarkers = monitorLatencySignal.MarkerStarts().size();
        std::printf("Duplex preparation: %s; synchronized Start authorization: %s\n",
            bothPrepared ? "both endpoints READY" : "FAILED",
            bothPrepared ? "released" : "withheld");
    }
    if (captureThread.joinable()) captureThread.join();
    if (renderThread.joinable()) renderThread.join();
    cancelWatcherDone.store(true, std::memory_order_release);
    if (cancelWatcher.joinable()) cancelWatcher.join();

    const BOOL handlerRemoved = SetConsoleCtrlHandler(HandleStreamConsoleControl, FALSE);
    const DWORD removeError = handlerRemoved ? ERROR_SUCCESS : GetLastError();
    const HRESULT removeResult = handlerRemoved ? S_OK
        : (removeError == ERROR_SUCCESS ? E_FAIL : HRESULT_FROM_WIN32(removeError));
    std::printf("Duplex console handler removal HRESULT: %s\n", Hresult(removeResult).c_str());
    g_activeDuplexCoordinator.store(nullptr);
    g_duplexStopEvent.store(nullptr);
    const BOOL eventClosed = CloseHandle(stopEvent);
    const DWORD closeError = eventClosed ? ERROR_SUCCESS : GetLastError();
    const HRESULT closeResult = eventClosed ? S_OK
        : (closeError == ERROR_SUCCESS ? E_FAIL : HRESULT_FROM_WIN32(closeError));
    std::printf("Duplex stop event close HRESULT: %s\n", Hresult(closeResult).c_str());

    const auto capture = coordinator.Side(0);
    const auto render = coordinator.Side(1);
    std::printf("CAPTURE STATUS: %s\nCAPTURE STREAM_QUALITY: %s\n",
        StreamStatusText(capture.status), StreamQualityText(capture.quality));
    std::printf("RENDER STATUS: %s\nRENDER STREAM_QUALITY: %s\n",
        StreamStatusText(render.status), StreamQualityText(render.quality));
    if (capture.startSucceeded) {
        std::printf("Capture Start offset relative to coordinator: %.3f ms; end: ",
                    coordinator.OffsetMilliseconds(capture.startedAt));
        if (capture.hasEnd) std::printf("%.3f ms\n", coordinator.OffsetMilliseconds(capture.endedAt));
        else std::puts("unavailable");
    }
    if (render.startSucceeded) {
        std::printf("Render Start offset relative to coordinator: %.3f ms; end: ",
                    coordinator.OffsetMilliseconds(render.startedAt));
        if (render.hasEnd) std::printf("%.3f ms\n", coordinator.OffsetMilliseconds(render.endedAt));
        else std::puts("unavailable");
    }
    if (capture.startSucceeded && render.startSucceeded) {
        const double startDifference = std::abs(std::chrono::duration<double, std::milli>(
            capture.startedAt - render.startedAt).count());
        const auto captureStartUs = std::chrono::duration_cast<std::chrono::microseconds>(
            capture.startedAt.time_since_epoch()).count();
        const auto captureEndUs = std::chrono::duration_cast<std::chrono::microseconds>(
            capture.endedAt.time_since_epoch()).count();
        const auto renderStartUs = std::chrono::duration_cast<std::chrono::microseconds>(
            render.startedAt.time_since_epoch()).count();
        const auto renderEndUs = std::chrono::duration_cast<std::chrono::microseconds>(
            render.endedAt.time_since_epoch()).count();
        const auto overlapUs = ProbeMath::DuplexOverlapMicroseconds(
            captureStartUs, captureEndUs, renderStartUs, renderEndUs);
        const auto frameDifference = capture.frames > render.frames
            ? capture.frames - render.frames : render.frames - capture.frames;
        std::printf("Duplex Start time difference: %.3f ms\nActual simultaneous running time: %.3f ms\n"
                    "Capture/Render processed frame-count difference: %llu frames (not a clock-drift measurement)\n",
            startDifference, static_cast<double>(overlapUs) / 1'000.0,
            static_cast<unsigned long long>(frameDifference));
    } else {
        std::puts("Duplex Start difference / overlap: unavailable (both Start calls did not succeed).");
    }
    const auto classifiedOverall = ProbeMath::ClassifyDuplex(capture.status, render.status);
    const auto overall = !threadCreationSucceeded || FAILED(removeResult) || FAILED(closeResult)
        ? ProbeMath::StreamStatus::StreamFailed : classifiedOverall;
    const auto overallQuality = ProbeMath::ClassifyDuplexQuality(capture.quality, render.quality);
    if (!outputJson.empty()) {
        const auto reportId = MatrixReportId();
        if (!reportId) {
            std::fwprintf(stderr, L"Could not create a privacy-safe Duplex report identifier.\n");
            return 1;
        }
        DuplexValidationReport::Report report;
        report.reportId = *reportId;
        report.schemaVersion = route ? 3 : sustainedValidation ? 1 : 2;
        report.reportType = route ? (sustainedValidation ? "wasapi-sustained-duplex-validation-v2" : "wasapi-duplex-validation-v2") : sustainedValidation ? "wasapi-sustained-duplex-validation" : "wasapi-duplex-validation";
        report.operationId = operationId.empty() ? *reportId : operationId;
        report.timestampUtc = MatrixTimestampUtc();
        report.probeVersion = PROBE_VERSION;
        report.provenance = route ? (sustainedValidation ? "REAL_WASAPI_SUSTAINED_DUPLEX_V2" : "REAL_WASAPI_DUPLEX_V2") : sustainedValidation ? "REAL_WASAPI_SUSTAINED_DUPLEX" : "REAL_WASAPI_DUPLEX";
        report.captureEndpointId = Utf8(captureId.c_str());
        report.renderEndpointId = Utf8(renderId.c_str());
        report.route = route;
        if (route) report.routeFingerprint = ChannelRouting::Fingerprint(*route);
        report.period = captureCustomPeriod && renderCustomPeriod
            ? "CUSTOM_V2:" + std::to_string(*captureCustomPeriod->requestedPeriod100ns) + ":" + std::to_string(*captureCustomPeriod->requestedFrames) + ":" + std::to_string(*renderCustomPeriod->requestedPeriod100ns) + ":" + std::to_string(*renderCustomPeriod->requestedFrames)
            : customPeriod ? "CUSTOM:" + std::to_string(*customPeriod->requestedPeriod100ns) + ":" + std::to_string(*customPeriod->requestedFrames) : useMinimumPeriod ? "MINIMUM" : "DEFAULT";
        report.mmcss = useProAudioMmcss ? "PRO_AUDIO" : "OFF";
        report.durationSeconds = duration;
        report.bothReady = bothPrepared;
        report.startAuthorized = bothPrepared;
        report.cancelled = coordinator.OperatorCancelled();
        report.peerStopRequested = capture.peerStopped || render.peerStopped;
        const auto makeSide = [&](const DuplexSideSummary& source, std::string_view endpoint,
                                  std::string_view direction) {
            DuplexValidationReport::Side side;
            side.endpointId = endpoint;
            side.direction = direction;
            if(route)side.routeFormat=direction=="CAPTURE"?route->capture:route->render;
            const auto hrText = [](HRESULT value) -> std::string {
                return value == E_PENDING ? std::string{} : Hresult(value);
            };
            side.activationHresult = hrText(source.activationResult);
            side.devicePeriodHresult = hrText(source.devicePeriodResult);
            side.formatSupportHresult = hrText(source.formatSupportResult);
            side.initializeHresult = hrText(source.finalInitializeResult);
            side.startHresult = hrText(source.startResult);
            side.stopHresult = hrText(source.stopResult);
            side.bufferHresult = hrText(source.bufferResult);
            side.eventHresult = hrText(source.eventResult);
            side.bufferFrames = source.bufferFrames;
            side.framesProcessed = source.frames;
            side.selectedCaptureSamples = source.selectedCaptureSamples;
            side.invalidSelectedCaptureSamples = source.invalidSelectedCaptureSamples;
            const auto sideRate = route ? (direction == "CAPTURE" ? route->capture.format.sampleRate : route->render.format.sampleRate) : rate;
            side.expectedFrames = static_cast<std::uint64_t>(sideRate) * duration;
            side.events = source.events;
            side.packets = source.packets;
            side.discontinuities = source.discontinuities;
            side.timestampErrors = source.timestampErrors;
            side.serviceFaults = source.serviceFaults;
            side.packetDrainLimitHits = source.packetDrainLimitHits;
            side.intermediateWaitTimeouts = source.intermediateWaitTimeouts;
            side.longEventIntervals = source.longEventIntervals;
            side.devicePositionAnomalies = direction == "CAPTURE"
                ? std::optional<std::uint64_t>(source.devicePositionAnomalies) : std::nullopt;
            side.silentBuffersServiced = direction == "RENDER"
                ? std::optional<std::uint64_t>(source.silentBuffersServiced) : std::nullopt;
            side.unavailableCaptureBuffers = direction == "CAPTURE"
                ? std::optional<std::uint64_t>(source.unavailableCaptureBuffers) : std::nullopt;
            side.peerStopped = source.peerStopped;
            side.watchdogTimedOut = source.watchdogTimedOut;
            side.deviceInvalidated = source.deviceInvalidated;
            side.durationReached = source.durationReached;
            side.eventIntervalsObserved = source.eventIntervalsObserved;
            side.eventIntervalMedianMilliseconds = source.eventIntervalMedianMilliseconds;
            side.eventIntervalP95Milliseconds = source.eventIntervalP95Milliseconds;
            side.eventIntervalP99Milliseconds = source.eventIntervalP99Milliseconds;
            const auto expected = static_cast<std::uint64_t>(sideRate) * duration;
            if (expected != 0) side.frameCoveragePercent = 100.0 * static_cast<double>(source.frames) / static_cast<double>(expected);
            side.elapsedMilliseconds = source.elapsedMilliseconds;
            side.ready = source.eventReady && source.initialized;
            side.startSucceeded = source.startSucceeded;
            side.stopSucceeded = source.stopSucceeded;
            side.quality = StreamQualityText(source.quality);
            side.cleanup = source.cleanupUnknown ? "UNKNOWN" : source.cleanupAttempted ? "RELEASE_ATTEMPTED" : "NOT_ATTEMPTED";
            side.errorStage = source.errorStage;
            side.errorHresult = hrText(source.errorResult);
            if (source.startSucceeded) side.startOffsetMilliseconds = coordinator.OffsetMilliseconds(source.startedAt);
            return side;
        };
        report.capture = makeSide(capture, report.captureEndpointId, "CAPTURE");
        report.render = makeSide(render, report.renderEndpointId, "RENDER");
        if (capture.startSucceeded && render.startSucceeded) {
            report.startDifferenceMilliseconds = std::abs(std::chrono::duration<double, std::milli>(
                capture.startedAt - render.startedAt).count());
            if (capture.hasEnd && render.hasEnd) {
                const auto captureStartUs = std::chrono::duration_cast<std::chrono::microseconds>(capture.startedAt.time_since_epoch()).count();
                const auto captureEndUs = std::chrono::duration_cast<std::chrono::microseconds>(capture.endedAt.time_since_epoch()).count();
                const auto renderStartUs = std::chrono::duration_cast<std::chrono::microseconds>(render.startedAt.time_since_epoch()).count();
                const auto renderEndUs = std::chrono::duration_cast<std::chrono::microseconds>(render.endedAt.time_since_epoch()).count();
                report.simultaneousRuntimeMilliseconds = static_cast<double>(ProbeMath::DuplexOverlapMicroseconds(
                    captureStartUs, captureEndUs, renderStartUs, renderEndUs)) / 1000.0;
            }
        }
        const auto enoughFrames = [&](const DuplexSideSummary& side, unsigned sideRate) {
            const auto expected = static_cast<std::uint64_t>(sideRate) * duration;
            return expected != 0 && side.frames >= expected - std::max<std::uint64_t>(2ULL * side.bufferFrames, expected / 10);
        };
        const bool clean = bothPrepared && report.startAuthorized && capture.startSucceeded && render.startSucceeded &&
            capture.durationReached && render.durationReached && capture.stopSucceeded && render.stopSucceeded &&
            enoughFrames(capture, route ? route->capture.format.sampleRate : rate) &&
            enoughFrames(render, route ? route->render.format.sampleRate : rate) && report.simultaneousRuntimeMilliseconds &&
            *report.simultaneousRuntimeMilliseconds >= static_cast<double>(duration) * 900.0 &&
            capture.status == ProbeMath::StreamStatus::StreamCompleted && render.status == ProbeMath::StreamStatus::StreamCompleted &&
            overallQuality == ProbeMath::StreamQuality::Clean && capture.serviceFaults == 0 && render.serviceFaults == 0 &&
            capture.packetDrainLimitHits == 0 && render.packetDrainLimitHits == 0 &&
            capture.longEventIntervals == 0 && render.longEventIntervals == 0 &&
            !capture.watchdogTimedOut && !render.watchdogTimedOut &&
            capture.discontinuities == 0 && render.discontinuities == 0 && capture.timestampErrors == 0 && render.timestampErrors == 0 &&
            !capture.cleanupUnknown && !render.cleanupUnknown;
        report.status = report.cancelled ? "CANCELLED" : clean ? "DUPLEX_CLEAN"
            : capture.startSucceeded && render.startSucceeded && report.simultaneousRuntimeMilliseconds
                ? "DUPLEX_WARNINGS"
                : overall == ProbeMath::StreamStatus::InitializeFailed || overall == ProbeMath::StreamStatus::StartFailed ||
                    overall == ProbeMath::StreamStatus::StreamFailed ? "FAILED" : "INCOMPLETE";
        if (!WriteNewJson(outputJson, DuplexValidationReport::ToJson(report))) {
            std::fwprintf(stderr, L"Could not create Duplex JSON report without overwriting an existing file.\n");
            return 1;
        }
        std::puts("REAL_WASAPI_DUPLEX JSON report created; endpoint IDs are included for machine-local binding, physical RTL is NOT_MEASURED.");
    }
    std::printf("DUPLEX_STATUS: %s\nDUPLEX_STREAM_QUALITY: %s\n",
                StreamStatusText(overall), StreamQualityText(overallQuality));
    if (clockProbe) {
        const auto clockResult = ClockAnalysis::Analyze(captureClock, renderClock);
        std::puts("\nDUPLEX CLOCK DIAGNOSTICS");
        std::printf("Clock diagnostic status: %s\nClock data quality: %s\n",
            ClockAnalysis::StatusText(clockResult.status), ClockDataQualityText(clockResult.status));
        PrintClockSide("Capture", captureClock, clockResult.capture);
        PrintClockSide("Render", renderClock, clockResult.render);
        if (clockResult.hasRegression) {
            const double spanMilliseconds = static_cast<double>(
                clockResult.overlapEnd100ns - clockResult.overlapStart100ns) / 10'000.0;
            std::printf("Common QPC analysis interval: %llu..%llu (100 ns), %.1f ms\n"
                        "Estimated relative rate difference: %.1f ppm\n"
                        "Estimated relative phase change across interval: %.3f ms\n",
                static_cast<unsigned long long>(clockResult.overlapStart100ns),
                static_cast<unsigned long long>(clockResult.overlapEnd100ns), spanMilliseconds,
                clockResult.relativeRateDifferencePpm,
                clockResult.relativePhaseChangeMilliseconds);
        } else {
            std::puts("Estimated relative rate difference: unavailable\nEstimated relative phase change: unavailable");
        }
        std::puts("Interpretation: regression estimates stream-position rate against correlated Windows/driver QPC timestamps. "
                  "It is not proof of physical clock drift, absolute phase alignment, acoustic latency or RTL.");
    }
    const auto monitorStatus = liveTransportEnabled
        ? PrintLiveTransportDiagnostics(liveRing, capture, render, coordinator.OperatorCancelled())
        : LiveTransport::MonitorStatus::Failed;
    if (measureSignalLevels) PrintSignalDiagnostics(capture, render);
    AnalogLoopback::Analysis loopbackResult;
    if (analogLoopback) {
        const auto alignment = ClockAnalysis::Analyze(captureClock, renderClock);
        loopbackResult = PrintAnalogLoopbackDiagnostics(capture, render, loopbackCapture,
            loopbackRender, captureClock, renderClock, alignment);
        loopbackCapture.EraseSamples();
    }
    MonitorLatency::Analysis monitorLatencyAnalysis;
    if (monitorLatency) {
        const auto ringStats = liveRing.GetStats();
        monitorLatencyAnalysis = MonitorLatency::Analyze(monitorLatencyData, captureClock,
            48'000, overall == ProbeMath::StreamStatus::StreamCompleted,
            coordinator.OperatorCancelled(), ringStats);
        PrintMonitorLatencyDiagnostics(monitorLatencyData, monitorLatencySignal,
            captureClock, renderClock, monitorLatencyAnalysis, ringStats, capture, render,
            coordinator.OperatorCancelled());
    }
    if (!threadCreationSucceeded || FAILED(removeResult) || FAILED(closeResult)) return 7;
    if (overall == ProbeMath::StreamStatus::InitializeFailed) return 4;
    if (overall == ProbeMath::StreamStatus::StartFailed) return 6;
    if (overall == ProbeMath::StreamStatus::StreamFailed) return 7;
    if (monitorLatency)
        return monitorLatencyAnalysis.softwareTransportMeasured ? 0
            : monitorLatencyAnalysis.status == MonitorLatency::Status::Failed ? 7 : 8;
    if (liveTransportEnabled) {
        return monitorStatus == LiveTransport::MonitorStatus::Completed ? 0
            : monitorStatus == LiveTransport::MonitorStatus::CompletedWithWarnings ? 8 : 7;
    }
    if (analogLoopback)
        return loopbackResult.status == AnalogLoopback::Status::Measured ? 0
            : loopbackResult.status == AnalogLoopback::Status::Failed ? 7 : 8;
    return overall == ProbeMath::StreamStatus::StreamCompleted ? 0 : 8;
}

bool ParseStreamDuration(const std::wstring& text, unsigned& seconds, unsigned maximum = 30) {
    if (text.empty()) return false;
    unsigned value = 0;
    for (const wchar_t character : text) {
        if (character < L'0' || character > L'9') return false;
        value = value * 10 + static_cast<unsigned>(character - L'0');
        if (value > maximum) return false;
    }
    if (!ProbeMath::IsValidStreamDuration(value)) return false;
    seconds = value;
    return true;
}

bool ParseSustainedDuplexDuration(const std::wstring& text, unsigned& seconds) {
    if (text.empty()) return false;
    unsigned value = 0;
    for (const wchar_t character : text) {
        if (character < L'0' || character > L'9') return false;
        value = value * 10 + static_cast<unsigned>(character - L'0');
        if (value > 600) return false;
    }
    if (!ProbeMath::IsValidSustainedDuplexDuration(value)) return false;
    seconds = value;
    return true;
}

std::optional<ExclusiveFormatMatrix::PeriodRequest> ParseCustomPeriod(const std::wstring& text, std::uint32_t sampleRate = 48'000) {
    if (!text.starts_with(L"custom:")) return std::nullopt;
    const auto separator = text.find(L':', 7);
    if (separator == std::wstring::npos || text.find(L':', separator + 1) != std::wstring::npos || separator == 7 || separator + 1 >= text.size()) return std::nullopt;
    std::int64_t period = 0;
    for (std::size_t i = 7; i < separator; ++i) {
        if (text[i] < L'0' || text[i] > L'9' || period > (INT64_MAX - (text[i] - L'0')) / 10) return std::nullopt;
        period = period * 10 + (text[i] - L'0');
    }
    std::uint32_t frames = 0;
    for (std::size_t i = separator + 1; i < text.size(); ++i) {
        if (text[i] < L'0' || text[i] > L'9' || frames > (std::numeric_limits<std::uint32_t>::max() - static_cast<std::uint32_t>(text[i] - L'0')) / 10) return std::nullopt;
        frames = frames * 10 + static_cast<std::uint32_t>(text[i] - L'0');
    }
    if (period <= 0 || frames == 0 || sampleRate == 0 || ProbeMath::DurationForFrames(frames, sampleRate) != period) return std::nullopt;
    return ExclusiveFormatMatrix::PeriodRequest::Custom(period, frames);
}

bool IsGuidText(const std::wstring& text) {
    if (text.size() != 36) return false;
    for (std::size_t i = 0; i < text.size(); ++i) {
        if (i == 8 || i == 13 || i == 18 || i == 23) { if (text[i] != L'-') return false; }
        else if (!((text[i] >= L'0' && text[i] <= L'9') || (text[i] >= L'a' && text[i] <= L'f') || (text[i] >= L'A' && text[i] <= L'F'))) return false;
    }
    return true;
}

}  // namespace

int wmain(int argc, wchar_t** argv) {
    bool listOnly = false;
    bool scanFormats = false;
    bool validateFormats = false;
    bool confirmExclusiveInit = false;
    bool confirmExclusiveInitSpecified = false;
    bool outputJsonSpecified = false;
    bool cancelEventHandleSpecified = false;
    bool testInit = false;
    bool testStream = false;
    bool testDuplex = false;
    bool testDuplexV2 = false;
    bool testDuplexV2Custom = false;
    bool testMonitor = false;
    bool testMonitorLatency = false;
    bool testAnalogLoopback = false;
    bool testMonitorRtlPreflight = false;
    bool meterCapture = false;
    bool testOutputChannel = false;
    bool clockProbe = false;
    bool clockProbeSpecified = false;
    bool confirmLiveOutput = false;
    bool confirmLiveOutputSpecified = false;
    bool signalLevels = false;
    bool signalLevelsSpecified = false;
    bool confirmTestSignal = false;
    bool confirmTestSignalSpecified = false;
    bool confirmPhysicalLoopback = false;
    bool confirmPhysicalLoopbackSpecified = false;
    bool confirmLiveCapture = false, confirmLiveCaptureSpecified = false;
    bool confirmPhysicalRouting = false, confirmPhysicalRoutingSpecified = false;
    bool confirmSafetySetup = false, confirmSafetySetupSpecified = false;
    bool captureSpecified = false;
    bool renderSpecified = false;
    bool duplicateEndpoint = false;
    std::wstring captureId;
    std::wstring renderId;
    std::wstring rateText;
    std::wstring channelsText;
    std::wstring formatText;
    std::wstring periodText;
    std::wstring durationText;
    std::wstring captureChannelText;
    std::wstring renderChannelText;
    std::wstring testSignalText;
    std::wstring outputJson;
    std::wstring operationIdText;
    std::wstring cancelEventHandleText;
    std::wstring routeModeText, captureRateText, captureChannelsV2Text, captureFormatText, captureMaskText, captureIndexText;
    std::wstring renderRateText, renderChannelsV2Text, renderFormatText, renderMaskText, renderIndexText;
    std::wstring captureCustomPeriodText, renderCustomPeriodText;
    std::wstring mmcssText = L"off";
    bool rateSpecified = false;
    bool channelsSpecified = false;
    bool formatSpecified = false;
    bool periodSpecified = false;
    bool durationSpecified = false;
    bool mmcssSpecified = false;
    bool sustainedDuplex = false;
    bool captureChannelSpecified = false;
    bool renderChannelSpecified = false;
    unsigned streamDuration = 0;
    for (int index = 1; index < argc; ++index) {
        if (std::wcscmp(argv[index], L"--help") == 0) {
            std::puts("KLIDAN Audio Probe: Windows WASAPI endpoint inspection.");
            std::puts("Usage: KlidanAudioProbe.exe --list | [--capture-id ID] [--render-id ID]");
            std::puts("       KlidanAudioProbe.exe (--capture-id ID | --render-id ID) --scan-formats [--output-json FILE]");
            std::puts("       KlidanAudioProbe.exe (--capture-id ID | --render-id ID) --validate-formats --confirm-exclusive-init [--output-json FILE]");
            std::puts("       KlidanAudioProbe.exe (--capture-id ID | --render-id ID) --test-init --rate 44100|48000 --channels 1|2 --format pcm16|pcm24-in-32|float32 [--period default|minimum]");
            std::puts("       KlidanAudioProbe.exe (--capture-id ID | --render-id ID) --test-stream --duration 1..30 --rate 48000 --channels 2 --format pcm16 [--period default|minimum] [--mmcss off|pro-audio]");
            std::puts("       KlidanAudioProbe.exe --capture-id ID --render-id ID --test-duplex --duration 1..30 --rate 48000 --channels 2 --format pcm16 [--period default|minimum] [--mmcss off|pro-audio] [--clock-probe]");
            std::puts("       KlidanAudioProbe.exe --capture-id ID --render-id ID --test-sustained-duplex --duration 60|300|600 --rate 48000 --channels 2 --format pcm16 [--period default|minimum|custom:<100ns>:<frames>] [--mmcss off|pro-audio]");
            std::puts("       Add [--output-json FILE] [--operation-id UUID] for a no-overwrite structured Duplex report.");
            std::puts("       KlidanAudioProbe.exe --capture-id ID --render-id ID --test-monitor --confirm-live-output --duration 1..30 --rate 48000 --channels 2 --format pcm16 [--period default|minimum] [--mmcss off|pro-audio] [--signal-levels]");
            std::puts("       KlidanAudioProbe.exe --capture-id ID --render-id ID --test-monitor-latency --confirm-live-output --confirm-test-signal --duration 5 --rate 48000 --channels 2 --format pcm16 [--period default|minimum] [--mmcss off|pro-audio]");
            std::puts("       Add [--signal-levels] to --test-monitor for per-channel PCM16 peak/RMS diagnostics.");
            std::puts("       KlidanAudioProbe.exe --capture-id ID --render-id ID --test-analog-loopback --confirm-test-signal --confirm-physical-loopback --capture-channel 1|2 --render-channel 1|2 --duration 5 --rate 48000 --channels 2 --format pcm16 [--period default|minimum] [--mmcss off|pro-audio]");
            std::puts("       KlidanAudioProbe.exe --capture-id ID --test-monitor-rtl-preflight --confirm-live-capture --confirm-physical-routing --confirm-test-signal --confirm-safe-setup --test-signal klidan-prbs511-v1 --duration 5 --rate 48000 --channels 2 --format pcm16 [--period default|minimum] [--mmcss off|pro-audio]");
            std::puts("       KlidanAudioProbe.exe --capture-id ID --meter-capture --confirm-live-capture --channels 1|2 --duration 1..30 [--rate 48000] [--format pcm16] [--period default|minimum] [--mmcss off|pro-audio] [--output-json FILE]");
            std::puts("       KlidanAudioProbe.exe --render-id ID --test-output-channel --render-channel 1|2 --confirm-live-output --confirm-test-signal --duration 1..10 [--period default|minimum] [--mmcss off|pro-audio] [--output-json FILE]");
            std::puts("Analog loopback generates a fixed low-level marker only with both confirmations and measures Render-to-Capture through a physical cable.");
            std::puts("Live monitoring routes captured audio to Render at fixed -24 dB. Use headphones, minimum hardware volume, and acoustic isolation from the microphone.");
            std::puts("Without an explicit active-test flag, commands remain passive. --mmcss defaults to off.");
            return 0;
        }
        if (std::wcscmp(argv[index], L"--list") == 0) listOnly = true;
        else if (std::wcscmp(argv[index], L"--scan-formats") == 0) scanFormats = true;
        else if (std::wcscmp(argv[index], L"--validate-formats") == 0) validateFormats = true;
        else if (std::wcscmp(argv[index], L"--confirm-exclusive-init") == 0 && !confirmExclusiveInitSpecified) {
            confirmExclusiveInit = true;
            confirmExclusiveInitSpecified = true;
        }
        else if (std::wcscmp(argv[index], L"--output-json") == 0 && index + 1 < argc && !outputJsonSpecified) {
            outputJson = argv[++index];
            outputJsonSpecified = true;
        }
        else if (std::wcscmp(argv[index], L"--operation-id") == 0 && index + 1 < argc && operationIdText.empty()) operationIdText = argv[++index];
        else if (std::wcscmp(argv[index], L"--cancel-event-handle") == 0 && index + 1 < argc && !cancelEventHandleSpecified) {
            cancelEventHandleText = argv[++index]; cancelEventHandleSpecified = true;
        }
        else if (std::wcscmp(argv[index], L"--capture-id") == 0 && index + 1 < argc) { duplicateEndpoint = duplicateEndpoint || captureSpecified; captureId = argv[++index]; captureSpecified = true; }
        else if (std::wcscmp(argv[index], L"--render-id") == 0 && index + 1 < argc) { duplicateEndpoint = duplicateEndpoint || renderSpecified; renderId = argv[++index]; renderSpecified = true; }
        else if (std::wcscmp(argv[index], L"--test-init") == 0) testInit = true;
        else if (std::wcscmp(argv[index], L"--test-stream") == 0) testStream = true;
        else if (std::wcscmp(argv[index], L"--test-duplex") == 0) testDuplex = true;
        else if (std::wcscmp(argv[index], L"--test-duplex-v2") == 0) testDuplexV2 = true;
        else if (std::wcscmp(argv[index], L"--test-duplex-v2-custom") == 0) { testDuplexV2 = true; testDuplexV2Custom = true; }
        else if (std::wcscmp(argv[index], L"--test-sustained-duplex-v2") == 0) { testDuplexV2 = true; sustainedDuplex = true; }
        else if (std::wcscmp(argv[index], L"--test-sustained-duplex") == 0) { testDuplex = true; sustainedDuplex = true; }
        else if (std::wcscmp(argv[index], L"--test-monitor") == 0) testMonitor = true;
        else if (std::wcscmp(argv[index], L"--test-monitor-latency") == 0) testMonitorLatency = true;
        else if (std::wcscmp(argv[index], L"--test-analog-loopback") == 0) testAnalogLoopback = true;
        else if (std::wcscmp(argv[index], L"--test-monitor-rtl-preflight") == 0) testMonitorRtlPreflight = true;
        else if (std::wcscmp(argv[index], L"--meter-capture") == 0) meterCapture = true;
        else if (std::wcscmp(argv[index], L"--test-output-channel") == 0) testOutputChannel = true;
        else if (std::wcscmp(argv[index], L"--confirm-live-output") == 0 && !confirmLiveOutputSpecified) {
            confirmLiveOutput = true;
            confirmLiveOutputSpecified = true;
        }
        else if (std::wcscmp(argv[index], L"--signal-levels") == 0 && !signalLevelsSpecified) {
            signalLevels = true;
            signalLevelsSpecified = true;
        }
        else if (std::wcscmp(argv[index], L"--confirm-test-signal") == 0 && !confirmTestSignalSpecified) {
            confirmTestSignal = true;
            confirmTestSignalSpecified = true;
        }
        else if (std::wcscmp(argv[index], L"--confirm-physical-loopback") == 0 && !confirmPhysicalLoopbackSpecified) {
            confirmPhysicalLoopback = true;
            confirmPhysicalLoopbackSpecified = true;
        }
        else if (std::wcscmp(argv[index], L"--confirm-live-capture") == 0 && !confirmLiveCaptureSpecified) { confirmLiveCapture = true; confirmLiveCaptureSpecified = true; }
        else if (std::wcscmp(argv[index], L"--confirm-physical-routing") == 0 && !confirmPhysicalRoutingSpecified) { confirmPhysicalRouting = true; confirmPhysicalRoutingSpecified = true; }
        else if (std::wcscmp(argv[index], L"--confirm-safe-setup") == 0 && !confirmSafetySetupSpecified) { confirmSafetySetup = true; confirmSafetySetupSpecified = true; }
        else if (std::wcscmp(argv[index], L"--test-signal") == 0 && index + 1 < argc && testSignalText.empty()) testSignalText = argv[++index];
        else if (std::wcscmp(argv[index], L"--capture-channel") == 0 && index + 1 < argc && !captureChannelSpecified) { captureChannelText = argv[++index]; captureChannelSpecified = true; }
        else if (std::wcscmp(argv[index], L"--render-channel") == 0 && index + 1 < argc && !renderChannelSpecified) { renderChannelText = argv[++index]; renderChannelSpecified = true; }
        else if (std::wcscmp(argv[index], L"--clock-probe") == 0 && !clockProbeSpecified) {
            clockProbe = true;
            clockProbeSpecified = true;
        }
        else if (std::wcscmp(argv[index], L"--rate") == 0 && index + 1 < argc && !rateSpecified) { rateText = argv[++index]; rateSpecified = true; }
        else if (std::wcscmp(argv[index], L"--channels") == 0 && index + 1 < argc && !channelsSpecified) { channelsText = argv[++index]; channelsSpecified = true; }
        else if (std::wcscmp(argv[index], L"--format") == 0 && index + 1 < argc && !formatSpecified) { formatText = argv[++index]; formatSpecified = true; }
        else if (std::wcscmp(argv[index], L"--period") == 0 && index + 1 < argc && !periodSpecified) { periodText = argv[++index]; periodSpecified = true; }
        else if (std::wcscmp(argv[index], L"--duration") == 0 && index + 1 < argc && !durationSpecified) { durationText = argv[++index]; durationSpecified = true; }
        else if (std::wcscmp(argv[index], L"--mmcss") == 0 && index + 1 < argc && !mmcssSpecified) { mmcssText = argv[++index]; mmcssSpecified = true; }
        else if (std::wcscmp(argv[index], L"--route-mode") == 0 && index + 1 < argc && routeModeText.empty()) routeModeText=argv[++index];
        else if (std::wcscmp(argv[index], L"--capture-rate") == 0 && index + 1 < argc && captureRateText.empty()) captureRateText=argv[++index];
        else if (std::wcscmp(argv[index], L"--capture-channels") == 0 && index + 1 < argc && captureChannelsV2Text.empty()) captureChannelsV2Text=argv[++index];
        else if (std::wcscmp(argv[index], L"--capture-format") == 0 && index + 1 < argc && captureFormatText.empty()) captureFormatText=argv[++index];
        else if (std::wcscmp(argv[index], L"--capture-mask") == 0 && index + 1 < argc && captureMaskText.empty()) captureMaskText=argv[++index];
        else if (std::wcscmp(argv[index], L"--capture-indices") == 0 && index + 1 < argc && captureIndexText.empty()) captureIndexText=argv[++index];
        else if (std::wcscmp(argv[index], L"--render-rate") == 0 && index + 1 < argc && renderRateText.empty()) renderRateText=argv[++index];
        else if (std::wcscmp(argv[index], L"--render-channels") == 0 && index + 1 < argc && renderChannelsV2Text.empty()) renderChannelsV2Text=argv[++index];
        else if (std::wcscmp(argv[index], L"--render-format") == 0 && index + 1 < argc && renderFormatText.empty()) renderFormatText=argv[++index];
        else if (std::wcscmp(argv[index], L"--render-mask") == 0 && index + 1 < argc && renderMaskText.empty()) renderMaskText=argv[++index];
        else if (std::wcscmp(argv[index], L"--render-indices") == 0 && index + 1 < argc && renderIndexText.empty()) renderIndexText=argv[++index];
        else if (std::wcscmp(argv[index], L"--capture-custom-period") == 0 && index + 1 < argc && captureCustomPeriodText.empty()) captureCustomPeriodText=argv[++index];
        else if (std::wcscmp(argv[index], L"--render-custom-period") == 0 && index + 1 < argc && renderCustomPeriodText.empty()) renderCustomPeriodText=argv[++index];
        else {
            std::fwprintf(stderr, L"Invalid or incomplete argument. Run --help for usage.\n");
            return 2;
        }
    }
    const auto customPeriod = periodSpecified ? ParseCustomPeriod(periodText) : std::nullopt;
    std::optional<ChannelRouting::Configuration> routeV2;
    std::optional<ExclusiveFormatMatrix::PeriodRequest> captureCustomPeriodV2, renderCustomPeriodV2;
    const bool formatMatrixMode = scanFormats || validateFormats;
    if (formatMatrixMode) {
        const bool conflictingOptions = listOnly || testInit || testStream || testDuplex || testDuplexV2 || testMonitor ||
            testMonitorLatency || testAnalogLoopback || testMonitorRtlPreflight || meterCapture || clockProbe ||
            confirmLiveOutput || signalLevels || confirmTestSignal || confirmPhysicalLoopback || testOutputChannel ||
            confirmLiveCapture || confirmPhysicalRouting || confirmSafetySetup || captureChannelSpecified ||
            renderChannelSpecified || rateSpecified || channelsSpecified || formatSpecified || periodSpecified ||
            durationSpecified || mmcssSpecified || !testSignalText.empty();
        if (scanFormats == validateFormats || conflictingOptions || duplicateEndpoint || listOnly ||
            (captureSpecified == renderSpecified) || captureId.empty() && renderId.empty()) {
            std::fwprintf(stderr, L"Format matrix requires exactly one selected endpoint and exactly one scan level; matrix options cannot be combined with other test modes.\n");
            return 2;
        }
        if (validateFormats && !confirmExclusiveInit) {
            std::fwprintf(stderr, L"--validate-formats requires explicit --confirm-exclusive-init.\n");
            return 2;
        }
        if (scanFormats && confirmExclusiveInit) {
            std::fwprintf(stderr, L"--confirm-exclusive-init applies only to --validate-formats.\n");
            return 2;
        }
        if (outputJsonSpecified && outputJson.empty()) {
            std::fwprintf(stderr, L"--output-json requires a non-empty file path.\n");
            return 2;
        }
        if (outputJsonSpecified) {
            SetLastError(ERROR_SUCCESS);
            const DWORD attributes = GetFileAttributesW(outputJson.c_str());
            if (attributes != INVALID_FILE_ATTRIBUTES) {
                std::fwprintf(stderr, L"Refusing to overwrite the existing JSON report.\n");
                return 2;
            }
            const DWORD error = GetLastError();
            if (error != ERROR_FILE_NOT_FOUND && error != ERROR_PATH_NOT_FOUND) {
                std::fwprintf(stderr, L"Cannot verify that the JSON report path is available (Win32 error %lu).\n", error);
                return 2;
            }
        }
    } else if (confirmExclusiveInitSpecified || (outputJsonSpecified && !meterCapture && !testOutputChannel && !testStream && !testDuplex && !testDuplexV2)) {
        std::fwprintf(stderr, L"--confirm-exclusive-init requires a format matrix; --output-json requires a format matrix, --meter-capture, --test-output-channel, or a stream/duplex mode.\n");
        return 2;
    }
    const bool duplexMode = testDuplex || testDuplexV2;
    if (!operationIdText.empty() && !duplexMode) { std::fwprintf(stderr, L"--operation-id is an internal duplex correlation option.\n"); return 2; }
    if (!operationIdText.empty() && !IsGuidText(operationIdText)) { std::fwprintf(stderr, L"--operation-id must be a UUID.\n"); return 2; }
    if (cancelEventHandleSpecified && !testStream && !duplexMode) { std::fwprintf(stderr, L"--cancel-event-handle is an internal stream/duplex cancellation option.\n"); return 2; }
    HANDLE externalCancelEvent = nullptr;
    if (cancelEventHandleSpecified) {
        wchar_t* end = nullptr;
        const auto value = std::wcstoull(cancelEventHandleText.c_str(), &end, 10);
        if (end == cancelEventHandleText.c_str() || *end != L'\0' || value == 0) { std::fwprintf(stderr, L"Invalid inherited cancel event handle.\n"); return 2; }
        externalCancelEvent = reinterpret_cast<HANDLE>(static_cast<std::uintptr_t>(value));
        if (WaitForSingleObject(externalCancelEvent, 0) == WAIT_FAILED) { std::fwprintf(stderr, L"Inherited cancel event handle is invalid.\n"); return 2; }
    }
    if (clockProbe && !testDuplex) {
        std::fwprintf(stderr, L"--clock-probe requires --test-duplex.\n");
        return 2;
    }
    if (confirmLiveOutput && !testMonitor && !testMonitorLatency && !testOutputChannel) {
        std::fwprintf(stderr, L"--confirm-live-output requires --test-monitor or --test-monitor-latency.\n");
        return 2;
    }
    if (signalLevels && !testMonitor) {
        std::fwprintf(stderr, L"--signal-levels requires --test-monitor.\n");
        return 2;
    }
    if ((confirmTestSignal && !testAnalogLoopback && !testMonitorLatency && !testMonitorRtlPreflight && !testOutputChannel) ||
        (confirmPhysicalLoopback && !testAnalogLoopback)) {
        std::fwprintf(stderr, L"--confirm-test-signal requires --test-analog-loopback or --test-monitor-latency; --confirm-physical-loopback requires --test-analog-loopback.\n");
        return 2;
    }
    if ((captureChannelSpecified || renderChannelSpecified) && !testAnalogLoopback && !testOutputChannel) {
        std::fwprintf(stderr, L"Channel selection requires --test-analog-loopback or --test-output-channel.\n");
        return 2;
    }
    if (testOutputChannel && !confirmLiveOutput) {
        std::fwprintf(stderr, L"--test-output-channel requires explicit --confirm-live-output.\n");
        return 2;
    }
    if (testOutputChannel && !confirmTestSignal) {
        std::fwprintf(stderr, L"--test-output-channel requires explicit --confirm-test-signal.\n");
        return 2;
    }
    if (testOutputChannel && (!renderSpecified || renderId.empty() || captureSpecified || listOnly ||
        !renderChannelSpecified || (renderChannelText != L"1" && renderChannelText != L"2") ||
        !durationSpecified ||
        !ParseStreamDuration(durationText, streamDuration) || streamDuration > 10 ||
        rateSpecified || channelsSpecified || formatSpecified || captureChannelSpecified ||
        testInit || testStream || testDuplex || testDuplexV2 || testMonitor || testMonitorLatency || testAnalogLoopback ||
        testMonitorRtlPreflight || meterCapture || scanFormats || validateFormats || clockProbe || signalLevels ||
        confirmPhysicalLoopback || confirmLiveCapture || confirmPhysicalRouting || confirmSafetySetup ||
        !testSignalText.empty() || (periodSpecified && periodText != L"default" && periodText != L"minimum") ||
        (mmcssSpecified && mmcssText != L"off" && mmcssText != L"pro-audio"))) {
        std::fwprintf(stderr, L"--test-output-channel requires one explicit --render-id, --render-channel 1|2, both live-output and test-signal confirmations, --duration 1..10, and optional --period / --mmcss; format is fixed at 48000 Hz stereo PCM16.\n");
        return 2;
    }
    if (renderChannelSpecified && !testOutputChannel && !testAnalogLoopback) {
        std::fwprintf(stderr, L"--render-channel requires --test-analog-loopback or --test-output-channel.\n");
        return 2;
    }
    if (testAnalogLoopback && !confirmTestSignal) {
        std::fwprintf(stderr, L"--test-analog-loopback requires explicit --confirm-test-signal.\n");
        return 2;
    }
    if (testAnalogLoopback && !confirmPhysicalLoopback) {
        std::fwprintf(stderr, L"--test-analog-loopback requires explicit --confirm-physical-loopback.\n");
        return 2;
    }
    if (testMonitor && !confirmLiveOutput) {
        std::fwprintf(stderr, L"--test-monitor requires explicit --confirm-live-output.\n");
        return 2;
    }
    if (testMonitorLatency && !confirmLiveOutput) {
        std::fwprintf(stderr, L"--test-monitor-latency requires explicit --confirm-live-output.\n");
        return 2;
    }
    if (testMonitorLatency && !confirmTestSignal) {
        std::fwprintf(stderr, L"--test-monitor-latency requires explicit --confirm-test-signal.\n");
        return 2;
    }
    if (testMonitorRtlPreflight && (!confirmLiveCapture || !confirmPhysicalRouting || !confirmTestSignal || !confirmSafetySetup)) {
        std::fwprintf(stderr, L"--test-monitor-rtl-preflight requires --confirm-live-capture, --confirm-physical-routing, --confirm-test-signal and --confirm-safe-setup.\n"); return 2;
    }
    if (testMonitorRtlPreflight && testSignalText != L"klidan-prbs511-v1") {
        std::fwprintf(stderr, L"--test-monitor-rtl-preflight requires --test-signal klidan-prbs511-v1.\n"); return 2;
    }
    if ((meterCapture || testOutputChannel || testStream || duplexMode) && outputJsonSpecified) {
        if (outputJson.empty()) {
            std::fwprintf(stderr, L"--output-json requires a non-empty file path.\n");
            return 2;
        }
        SetLastError(ERROR_SUCCESS);
        const DWORD attributes = GetFileAttributesW(outputJson.c_str());
        if (attributes != INVALID_FILE_ATTRIBUTES) {
            std::fwprintf(stderr, L"Refusing to overwrite the existing JSON report.\n");
            return 2;
        }
        const DWORD error = GetLastError();
        if (error != ERROR_FILE_NOT_FOUND && error != ERROR_PATH_NOT_FOUND) {
            std::fwprintf(stderr, L"Cannot verify that the JSON report path is available (Win32 error %lu).\n", error);
            return 2;
        }
    }
    if ((confirmLiveCapture && !testMonitorRtlPreflight && !meterCapture) ||
        (confirmPhysicalRouting || confirmSafetySetup || !testSignalText.empty()) && !testMonitorRtlPreflight) {
        std::fwprintf(stderr, L"Preflight confirmations and --test-signal require --test-monitor-rtl-preflight.\n"); return 2;
    }
    if (meterCapture && !confirmLiveCapture) {
        std::fwprintf(stderr, L"--meter-capture requires explicit --confirm-live-capture.\n"); return 2;
    }
    if (mmcssSpecified && !testStream && !duplexMode && !testMonitor && !testMonitorLatency && !testAnalogLoopback && !testMonitorRtlPreflight && !meterCapture && !testOutputChannel) {
        std::fwprintf(stderr, L"--mmcss requires --test-stream or --test-duplex.\n");
        return 2;
    }
    if (durationSpecified && !testStream && !testDuplex && !testDuplexV2 && !testMonitor && !testMonitorLatency && !testAnalogLoopback && !testMonitorRtlPreflight && !meterCapture && !testOutputChannel) {
        std::fwprintf(stderr, L"--duration requires --test-stream or --test-duplex.\n");
        return 2;
    }
    if (testInit || testStream || testDuplex || testDuplexV2 || testMonitor || testMonitorLatency || testAnalogLoopback || testMonitorRtlPreflight || meterCapture || testOutputChannel) {
        const unsigned activeModeCount = static_cast<unsigned>(testInit) +
            static_cast<unsigned>(testStream) + static_cast<unsigned>(testDuplex) + static_cast<unsigned>(testDuplexV2) +
            static_cast<unsigned>(testMonitor) + static_cast<unsigned>(testMonitorLatency) +
            static_cast<unsigned>(testAnalogLoopback) + static_cast<unsigned>(testMonitorRtlPreflight) +
            static_cast<unsigned>(meterCapture) + static_cast<unsigned>(testOutputChannel);
        const bool oneEndpoint = captureId.empty() != renderId.empty();
        const bool endpointsValid = testOutputChannel ? renderSpecified && !renderId.empty() && !captureSpecified
            : meterCapture ? captureSpecified && !captureId.empty() && !renderSpecified
            : testDuplex || testDuplexV2 || testMonitor || testMonitorLatency || testAnalogLoopback
            ? captureSpecified && renderSpecified && !captureId.empty() && !renderId.empty()
            : testMonitorRtlPreflight ? captureSpecified && !captureId.empty() && !renderSpecified : oneEndpoint;
        if (testDuplexV2) {
            const auto number=[](const std::wstring& text,std::uint32_t maximum,std::uint32_t& out){if(text.empty())return false;wchar_t* end=nullptr;const auto value=std::wcstoul(text.c_str(),&end,0);if(end==text.c_str()||*end!=L'\0'||value>maximum)return false;out=static_cast<std::uint32_t>(value);return true;};
            const auto indices=[&](const std::wstring& text,std::array<std::uint16_t,2>& out,std::uint16_t count){std::size_t begin=0;for(std::uint16_t i=0;i<count;++i){const auto end=text.find(L',',begin);if((i+1<count&&end==std::wstring::npos)||(i+1==count&&end!=std::wstring::npos))return false;const auto token=text.substr(begin,end==std::wstring::npos?end:end-begin);std::uint32_t value{};if(!number(token,31,value))return false;out[i]=static_cast<std::uint16_t>(value);begin=end==std::wstring::npos?text.size():end+1;}return begin==text.size();};
            std::uint32_t cr{},cc{},cm{},rr{},rc{},rm{};ChannelRouting::Configuration route{};
            route.captureEndpointId=Utf8(captureId.c_str());route.renderEndpointId=Utf8(renderId.c_str());
            route.mode=routeModeText==L"MONO_TO_STEREO"?ChannelRouting::Mode::MonoToStereo:routeModeText==L"STEREO_TO_STEREO"?ChannelRouting::Mode::StereoToStereo:routeModeText==L"MONO_TO_MONO"?ChannelRouting::Mode::MonoToMono:static_cast<ChannelRouting::Mode>(255);
            const auto enc=[](const std::wstring& text){return text==L"PCM16"?ExclusiveFormatMatrix::Encoding::Pcm16:text==L"PCM24_IN_32"?ExclusiveFormatMatrix::Encoding::Pcm24In32:text==L"FLOAT32"?ExclusiveFormatMatrix::Encoding::Float32:static_cast<ExclusiveFormatMatrix::Encoding>(255);};
            const bool valid=number(captureRateText,48000,cr)&&(cr==44100||cr==48000)&&number(captureChannelsV2Text,32,cc)&&cc>0&&number(captureMaskText.empty()?L"0":captureMaskText,UINT32_MAX,cm)&&number(renderRateText,48000,rr)&&(rr==44100||rr==48000)&&number(renderChannelsV2Text,32,rc)&&rc>0&&number(renderMaskText.empty()?L"0":renderMaskText,UINT32_MAX,rm)&&enc(captureFormatText)!=static_cast<ExclusiveFormatMatrix::Encoding>(255)&&enc(renderFormatText)!=static_cast<ExclusiveFormatMatrix::Encoding>(255)&&indices(captureIndexText,route.captureChannels,ChannelRouting::CaptureCount(route.mode))&&indices(renderIndexText,route.renderChannels,ChannelRouting::RenderCount(route.mode));
            if(valid){route.capture.format={cr,static_cast<std::uint16_t>(cc),enc(captureFormatText)};route.capture.channelMask=cm;route.render.format={rr,static_cast<std::uint16_t>(rc),enc(renderFormatText)};route.render.channelMask=rm;if(ChannelRouting::Validate(route))routeV2=route;}
        }
        captureCustomPeriodV2 = testDuplexV2Custom && routeV2 ? ParseCustomPeriod(captureCustomPeriodText, routeV2->capture.format.sampleRate) : std::nullopt;
        renderCustomPeriodV2 = testDuplexV2Custom && routeV2 ? ParseCustomPeriod(renderCustomPeriodText, routeV2->render.format.sampleRate) : std::nullopt;
        if (activeModeCount != 1) {
            std::fwprintf(stderr, testMonitor || testMonitorLatency || testAnalogLoopback || testMonitorRtlPreflight || meterCapture || testOutputChannel
                ? L"Choose exactly one active test mode, including --test-monitor-latency.\n"
                : L"Choose exactly one of --test-init, --test-stream, or --test-duplex.\n");
            return 2;
        }
        if (testAnalogLoopback && (!captureChannelSpecified || !renderChannelSpecified ||
            (captureChannelText != L"1" && captureChannelText != L"2") ||
            (renderChannelText != L"1" && renderChannelText != L"2"))) {
            std::fwprintf(stderr, L"--test-analog-loopback requires --capture-channel 1|2 and --render-channel 1|2.\n");
            return 2;
        }
        const bool meterArgsValid = !meterCapture ||
            ((!rateSpecified || rateText == L"48000") && (!formatSpecified || formatText == L"pcm16"));
        if (listOnly || !endpointsValid || duplicateEndpoint || (testDuplexV2 && (!routeV2 || !durationSpecified || (periodSpecified && periodText!=L"default"&&periodText!=L"minimum") ||
                (testDuplexV2Custom ? (!captureCustomPeriodV2 || !renderCustomPeriodV2 || sustainedDuplex) : (!captureCustomPeriodText.empty() || !renderCustomPeriodText.empty())))) ||
            (!testDuplexV2 && !meterCapture && !testOutputChannel && (!rateSpecified || !channelsSpecified || !formatSpecified)) ||
            (meterCapture && !channelsSpecified) ||
            (!testDuplexV2 && !meterCapture && !testOutputChannel && rateText != L"44100" && rateText != L"48000") || !meterArgsValid ||
            (!testDuplexV2 && !testOutputChannel && channelsText != L"1" && channelsText != L"2") ||
            (!testDuplexV2 && !meterCapture && !testOutputChannel && formatText != L"pcm16" && formatText != L"pcm24-in-32" && formatText != L"float32") ||
            (periodSpecified && periodText != L"default" && periodText != L"minimum" && !(duplexMode && customPeriod))) {
            const wchar_t* message = meterCapture
                ? L"--meter-capture requires one explicit Capture endpoint, --channels 1|2, 48000 Hz PCM16, and optional --period default|minimum.\n"
            : testOutputChannel ? L"--test-output-channel requires both confirmations, --render-channel 1|2 and --duration 1..10; format is fixed at 48000 Hz stereo PCM16.\n"
                : testInit
                ? L"--test-init requires one endpoint, --rate 44100|48000, --channels 1|2, --format pcm16|pcm24-in-32|float32, and optional --period default|minimum.\n"
            : testDuplexV2 ? L"--test-duplex-v2 requires exact endpoints, route mode, per-side rate/channels/format, optional masks, and zero-based selected indices.\n"
            : testDuplex
                    ? L"--test-duplex requires both --capture-id and --render-id, --rate 48000, --channels 2, --format pcm16, and --period default|minimum|custom:<100ns>:<frames>.\n"
                    : testMonitorRtlPreflight
                        ? L"--test-monitor-rtl-preflight requires one Capture endpoint, four confirmations, --test-signal klidan-prbs511-v1, 5-second 48000 Hz stereo pcm16.\n"
                    : testAnalogLoopback
                        ? L"--test-analog-loopback requires both endpoints, both confirmations, 5-second 48000 Hz stereo pcm16, selected channels and optional --period/--mmcss.\n"
                    : testMonitorLatency
                        ? L"--test-monitor-latency requires both endpoints, both confirmations, 5-second 48000 Hz stereo pcm16, and optional --period/--mmcss.\n"
                    : testMonitor
                        ? L"--test-monitor requires both --capture-id and --render-id, --confirm-live-output, --rate 48000, --channels 2, --format pcm16, and optional --period default|minimum.\n"
                        : L"--test-stream requires one endpoint, --rate 48000, --channels 2, --format pcm16, and optional --period default|minimum.\n";
            std::fwprintf(stderr, L"%ls", message);
            return 2;
        }
        if (testInit && durationSpecified) {
            std::fwprintf(stderr, L"--duration requires --test-stream or --test-duplex.\n");
            return 2;
        }
        const bool durationValid = sustainedDuplex ? ParseSustainedDuplexDuration(durationText, streamDuration) : ParseStreamDuration(durationText, streamDuration);
        if ((testStream || testDuplex || testDuplexV2 || testMonitor || testMonitorLatency || testAnalogLoopback || testMonitorRtlPreflight || meterCapture || testOutputChannel) && (!durationSpecified || !durationValid)) {
            std::fwprintf(stderr, testAnalogLoopback
                ? L"--test-analog-loopback requires --duration 5.\n"
                : testMonitorRtlPreflight ? L"--test-monitor-rtl-preflight requires --duration 5.\n"
                : testMonitorLatency ? L"--test-monitor-latency requires --duration 5 for at least five sequence markers.\n"
                : testOutputChannel ? L"--test-output-channel requires --duration from 1 to 10 seconds.\n"
                : meterCapture ? L"--meter-capture requires --duration from 1 to 30 seconds.\n"
                : testMonitor ? L"--test-monitor requires --duration from 1 to 30 seconds.\n"
                : sustainedDuplex ? L"--test-sustained-duplex requires --duration 60, 300, or 600 seconds.\n" : L"--test-stream and --test-duplex require --duration from 1 to 30 seconds.\n");
            return 2;
        }
        if ((testDuplex || testDuplexV2) && sustainedDuplex && streamDuration != 60U && streamDuration != 300U && streamDuration != 600U) {
            std::fwprintf(stderr, L"--test-sustained-duplex supports only --duration 60, 300, or 600.\n");
            return 2;
        }
        if ((testAnalogLoopback || testMonitorRtlPreflight) && streamDuration != 5) {
            std::fwprintf(stderr, L"This preflight/loopback mode currently requires exactly --duration 5.\n");
            return 2;
        }
        if (testMonitorLatency && streamDuration != 5) {
            std::fwprintf(stderr, L"--test-monitor-latency currently requires exactly --duration 5.\n");
            return 2;
        }
        if ((testStream || testDuplex || testDuplexV2 || testMonitor || testMonitorLatency || testAnalogLoopback || testMonitorRtlPreflight || meterCapture || testOutputChannel) && mmcssText != L"off" && mmcssText != L"pro-audio") {
            std::fwprintf(stderr, L"--mmcss must be off or pro-audio.\n");
            return 2;
        }
        if ((testStream || testDuplex || testMonitor || testMonitorLatency || testAnalogLoopback || testMonitorRtlPreflight) && (rateText != L"48000" || channelsText != L"2" || formatText != L"pcm16")) {
            std::fwprintf(stderr, testDuplex
                ? L"--test-duplex currently supports only 48000 Hz stereo pcm16.\n"
                : testMonitorRtlPreflight ? L"--test-monitor-rtl-preflight currently supports only 48000 Hz stereo pcm16.\n"
                : testAnalogLoopback ? L"--test-analog-loopback currently supports only 48000 Hz stereo pcm16.\n"
                : testMonitorLatency ? L"--test-monitor-latency currently supports only 48000 Hz stereo pcm16.\n"
                : testMonitor ? L"--test-monitor currently supports only 48000 Hz stereo pcm16.\n"
                              : L"--test-stream currently supports only 48000 Hz stereo pcm16.\n");
            return 2;
        }
    } else if (rateSpecified || channelsSpecified || formatSpecified || periodSpecified || durationSpecified || mmcssSpecified || confirmLiveOutput || signalLevels || confirmTestSignal || confirmPhysicalLoopback || captureChannelSpecified || renderChannelSpecified || confirmLiveCapture || confirmPhysicalRouting || confirmSafetySetup || !testSignalText.empty() || !routeModeText.empty() || !captureRateText.empty() || !captureChannelsV2Text.empty() || !captureFormatText.empty() || !captureMaskText.empty() || !captureIndexText.empty() || !renderRateText.empty() || !renderChannelsV2Text.empty() || !renderFormatText.empty() || !renderMaskText.empty() || !renderIndexText.empty()) {
        std::fwprintf(stderr, L"--rate, --channels, --format, and --period require an active test; --duration and --mmcss require --test-stream or --test-duplex.\n");
        return 2;
    }
    if (testDuplex) {
        return RunDuplex(captureId, renderId, std::wcstoul(rateText.c_str(), nullptr, 10),
                         std::wcstoul(channelsText.c_str(), nullptr, 10), formatText,
                         periodText == L"minimum", streamDuration, mmcssText == L"pro-audio", clockProbe,
                         false, false, false, 1, 1, false, outputJson, externalCancelEvent,
                         operationIdText.empty() ? std::string{} : Utf8(operationIdText.c_str()), customPeriod, sustainedDuplex);
    }
    if (testDuplexV2 && routeV2) {
        return RunDuplex(captureId, renderId, routeV2->capture.format.sampleRate, routeV2->capture.format.channels, L"pcm16",
            periodText == L"minimum", streamDuration, mmcssText == L"pro-audio", false,
            false, false, false, 1, 1, false, outputJson, externalCancelEvent,
            operationIdText.empty()?std::string{}:Utf8(operationIdText.c_str()), std::nullopt, sustainedDuplex, routeV2,
            captureCustomPeriodV2, renderCustomPeriodV2);
    }
    if (testMonitor) {
        return RunDuplex(captureId, renderId, 48'000, 2, formatText,
                         periodText == L"minimum", streamDuration, mmcssText == L"pro-audio",
                         false, true, signalLevels);
    }
    if (testMonitorLatency) {
        return RunDuplex(captureId, renderId, 48'000, 2, formatText,
            periodText == L"minimum", streamDuration, mmcssText == L"pro-audio",
            false, false, false, false, 1, 1, true);
    }
    if (testAnalogLoopback) {
        return RunDuplex(captureId, renderId, 48'000, 2, formatText,
            periodText == L"minimum", streamDuration, mmcssText == L"pro-audio",
            false, false, false, true,
            static_cast<unsigned>(std::wcstoul(captureChannelText.c_str(), nullptr, 10)),
            static_cast<unsigned>(std::wcstoul(renderChannelText.c_str(), nullptr, 10)));
    }
    if (!listOnly && captureId.empty() && renderId.empty()) {
        std::fwprintf(stderr, L"Specify --list or at least one endpoint ID.\n");
        return 2;
    }

    const HRESULT comResult = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    if (FAILED(comResult)) {
        std::printf("CoInitializeEx failed: %s\n", Hresult(comResult).c_str());
        return 1;
    }
    IMMDeviceEnumerator* enumerator = nullptr;
    const HRESULT createResult = CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL,
        __uuidof(IMMDeviceEnumerator), reinterpret_cast<void**>(&enumerator));
    if (FAILED(createResult) || enumerator == nullptr) {
        std::printf("MMDeviceEnumerator creation failed: %s\n", Hresult(createResult).c_str());
        CoUninitialize();
        return 1;
    }
    if (testOutputChannel) {
        RenderSignal::Generator signal;
        RenderChannelReport::Report report;
        const auto selectedChannel = static_cast<std::uint32_t>(std::wcstoul(renderChannelText.c_str(), nullptr, 10) - 1);
        if (!signal.Configure(48'000, 2, selectedChannel)) {
            Release(enumerator);
            CoUninitialize();
            std::fwprintf(stderr, L"Could not configure the bounded 48 kHz stereo PCM16 signal.\n");
            return 2;
        }
        std::puts("RENDER CHANNEL TEST: explicit low-level tone, no Capture; startup buffer is silence; playback is not measured.");
        const int renderResult = ProbeInitById(enumerator, eRender, renderId, 48'000, 2, L"pcm16",
            periodText == L"minimum", streamDuration, mmcssText == L"pro-audio", nullptr, -1,
            nullptr, nullptr, false, nullptr, nullptr, nullptr, nullptr, nullptr,
            nullptr, nullptr, &signal, &report);
        std::puts("RENDER PLAYBACK / DAC OUTPUT: NOT_MEASURED; ReleaseBuffer only confirms software buffer submission.");
        std::puts("PHYSICAL_OUTPUT_MAPPING: UNKNOWN; digital output does not establish physical routing or audibility.");
        if (outputJsonSpecified) {
            const auto reportId = MatrixReportId();
            if (!reportId || !WriteNewJson(outputJson,
                    RenderChannelReport::ToJson(report, MatrixTimestampUtc(), *reportId, PROBE_VERSION))) {
                std::fwprintf(stderr, L"Could not create sanitized JSON report without overwriting an existing file.\n");
                Release(enumerator);
                CoUninitialize();
                return renderResult == 0 ? 1 : renderResult;
            }
            std::puts("Sanitized Render channel report created; endpoint ID and output path omitted.");
        }
        Release(enumerator);
        CoUninitialize();
        return renderResult;
    }
    if (formatMatrixMode) {
        const EDataFlow direction = captureSpecified ? eCapture : eRender;
        const auto& endpointId = captureSpecified ? captureId : renderId;
        const int result = RunFormatMatrix(enumerator, direction, endpointId,
            validateFormats ? ExclusiveFormatMatrix::Level::InitializeOnly
                            : ExclusiveFormatMatrix::Level::PassiveQuery,
            outputJson);
        Release(enumerator);
        CoUninitialize();
        return result;
    }
    if (listOnly) {
        ListEndpoints(enumerator, eCapture);
        ListEndpoints(enumerator, eRender);
    }
    bool success = true;
    int initResult = 0;
    if (meterCapture) {
        ChannelMeter::Meter meter;
        ChannelMeter::Report report;
        std::puts("CAPTURE METER: output disabled; no Render endpoint will be opened; PCM is analyzed in memory and discarded.");
        initResult = ProbeInitById(enumerator, eCapture, captureId, 48'000,
            static_cast<unsigned>(std::wcstoul(channelsText.c_str(), nullptr, 10)), L"pcm16",
            periodText == L"minimum", streamDuration, mmcssText == L"pro-audio",
            nullptr, -1, nullptr, nullptr, false, nullptr, nullptr, nullptr, nullptr, nullptr,
            &meter, &report);
        std::puts("RENDER_STATUS: NOT_STARTED");
        if (outputJsonSpecified) {
#ifndef PROBE_VERSION
#define PROBE_VERSION "unknown"
#endif
            const auto reportId = MatrixReportId();
            if (!reportId || !WriteNewJson(outputJson,
                    ChannelMeter::ToJson(report, MatrixTimestampUtc(), *reportId, PROBE_VERSION))) {
                std::fwprintf(stderr, L"Could not create sanitized JSON report without overwriting an existing file.\n");
                initResult = initResult == 0 ? 1 : initResult;
            } else {
                std::puts("Sanitized metering report created; endpoint identity and output path omitted.");
            }
        }
    } else if (testMonitorRtlPreflight) {
        MonitorRtlPreflight::CaptureData preflight;
        initResult = ProbeInitById(enumerator, eCapture, captureId, 48'000, 2, formatText,
            periodText == L"minimum", 5, mmcssText == L"pro-audio", nullptr, -1,
            nullptr, nullptr, false, nullptr, nullptr, nullptr, nullptr, &preflight);
        if (initResult == 1 || initResult == 4 || initResult == 5)
            std::puts("PREFLIGHT_STATUS: FAILED (not armed)\nPREFLIGHT_STATE: FAILED\nInput 1 marker: NOT_TESTED\nInput 2 baseline: NOT_TESTED");
        std::puts("ROUTING_ISOLATION_STATUS: NOT_APPLIED (Render not active)\nRENDER_START_STATUS: NOT_CALLED\n"
                  "Render endpoint activation: NOT_ATTEMPTED\nRing Push calls: 0\nPHYSICAL_RTL_STATUS: NOT_MEASURED");
    } else if (testInit || testStream) {
        StreamValidationReport::Report streamReport;
        initResult = !captureId.empty()
            ? ProbeInitById(enumerator, eCapture, captureId, std::wcstoul(rateText.c_str(), nullptr, 10), std::wcstoul(channelsText.c_str(), nullptr, 10), formatText, periodText == L"minimum", streamDuration, mmcssText == L"pro-audio", nullptr, -1, nullptr, nullptr, false, nullptr, nullptr, nullptr, nullptr, nullptr, nullptr, nullptr, nullptr, nullptr, &streamReport, externalCancelEvent)
            : ProbeInitById(enumerator, eRender, renderId, std::wcstoul(rateText.c_str(), nullptr, 10), std::wcstoul(channelsText.c_str(), nullptr, 10), formatText, periodText == L"minimum", streamDuration, mmcssText == L"pro-audio", nullptr, -1, nullptr, nullptr, false, nullptr, nullptr, nullptr, nullptr, nullptr, nullptr, nullptr, nullptr, nullptr, &streamReport, externalCancelEvent);
        if (testStream && outputJsonSpecified) {
            const auto reportId = MatrixReportId();
            const auto direction = captureId.empty() ? "RENDER" : "CAPTURE";
            streamReport.direction = direction;
            streamReport.durationSeconds = streamDuration;
            streamReport.period = periodText == L"minimum" ? "MINIMUM" : "DEFAULT";
            if (!reportId || !WriteNewJson(outputJson, StreamValidationReport::ToJson(streamReport, MatrixTimestampUtc(), *reportId))) {
                std::fwprintf(stderr, L"Could not create structured stream report without overwriting an existing file.\n");
                if (initResult == 0) initResult = 1;
            }
        }
    } else {
        if (!captureId.empty()) success = ProbeById(enumerator, eCapture, captureId) && success;
        if (!renderId.empty()) success = ProbeById(enumerator, eRender, renderId) && success;
    }
    Release(enumerator);
    CoUninitialize();
    return meterCapture || testMonitorRtlPreflight || testInit || testStream ? initResult : success ? 0 : 1;
}
