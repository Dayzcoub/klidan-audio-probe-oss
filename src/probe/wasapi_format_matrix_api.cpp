#define INITGUID
#include <windows.h>
#include <ks.h>
#include <ksmedia.h>

#include "wasapi_format_matrix_api.h"
#include "probe_math.h"

namespace ExclusiveFormatMatrix {
namespace {
template<class T> void Release(T*& value) noexcept { if (value) { value->Release(); value = nullptr; } }
}

std::optional<WAVEFORMATEXTENSIBLE> BuildWasapiFormat(const Format& format) noexcept {
    if ((format.sampleRate != 44'100 && format.sampleRate != 48'000) || format.channels < 1 || format.channels > 64)
        return std::nullopt;
    WORD bits{}, validBits{};
    GUID subFormat{};
    switch (format.encoding) {
    case Encoding::Pcm16: bits = validBits = 16; subFormat = KSDATAFORMAT_SUBTYPE_PCM; break;
    case Encoding::Pcm24In32: bits = 32; validBits = 24; subFormat = KSDATAFORMAT_SUBTYPE_PCM; break;
    case Encoding::Float32: bits = validBits = 32; subFormat = KSDATAFORMAT_SUBTYPE_IEEE_FLOAT; break;
    default: return std::nullopt;
    }
    const std::uint32_t block = static_cast<std::uint32_t>(format.channels) * (bits / 8);
    const std::uint64_t average = static_cast<std::uint64_t>(format.sampleRate) * block;
    if (block > MAXWORD || average > MAXDWORD) return std::nullopt;
    WAVEFORMATEXTENSIBLE out{};
    out.Format.wFormatTag = WAVE_FORMAT_EXTENSIBLE;
    out.Format.nChannels = format.channels;
    out.Format.nSamplesPerSec = format.sampleRate;
    out.Format.wBitsPerSample = bits;
    out.Format.nBlockAlign = static_cast<WORD>(block);
    out.Format.nAvgBytesPerSec = static_cast<DWORD>(average);
    out.Format.cbSize = sizeof(WAVEFORMATEXTENSIBLE) - sizeof(WAVEFORMATEX);
    out.Samples.wValidBitsPerSample = validBits;
    out.dwChannelMask = format.channels == 1 ? SPEAKER_FRONT_CENTER
        : format.channels == 2 ? SPEAKER_FRONT_LEFT | SPEAKER_FRONT_RIGHT : 0;
    out.SubFormat = subFormat;
    return out;
}

QueryResult WasapiApi::QuerySupport(const Format& candidate) {
    QueryResult result;
    const auto format = BuildWasapiFormat(candidate);
    if (!format || device_ == nullptr) { result.cleanup = Cleanup::NotAcquired; return result; }
    IAudioClient* client = nullptr;
    const HRESULT activateHr = device_->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr,
                                                 reinterpret_cast<void**>(&client));
    result.activationHr = static_cast<std::int32_t>(activateHr);
    if (FAILED(activateHr) || client == nullptr) {
        if (client != nullptr) { Release(client); result.clientReleaseAttempts = 1; result.cleanup = Cleanup::ReleaseAttempted; }
        else result.cleanup = Cleanup::NotAcquired;
        return result;
    }
    REFERENCE_TIME defaultPeriod = 0, minimumPeriod = 0;
    const HRESULT periodHr = client->GetDevicePeriod(&defaultPeriod, &minimumPeriod);
    result.devicePeriodHr = static_cast<std::int32_t>(periodHr);
    if (SUCCEEDED(periodHr)) {
        if (defaultPeriod > 0) result.defaultDevicePeriod100ns = defaultPeriod;
        if (minimumPeriod > 0) result.minimumDevicePeriod100ns = minimumPeriod;
    }
    result.supportHr = static_cast<std::int32_t>(client->IsFormatSupported(AUDCLNT_SHAREMODE_EXCLUSIVE,
                                                                           &format->Format, nullptr));
    Release(client);
    result.clientReleaseAttempts = 1;
    result.cleanup = Cleanup::ReleaseAttempted;
    return result;
}

Attempt WasapiApi::InitializeOnly(const Format& candidate, Period mode) {
    return InitializeOnly(candidate, PeriodRequest::Legacy(mode));
}

Attempt WasapiApi::InitializeOnly(const Format& candidate, const PeriodRequest& request) {
    Attempt result;
    const auto format = BuildWasapiFormat(candidate);
    if (!format || device_ == nullptr) { result.cleanup = Cleanup::NotAcquired; return result; }
    IAudioClient* client = nullptr;
    const auto releaseClient = [&] {
        if (client != nullptr) { Release(client); ++result.clientReleaseAttempts; }
    };
    const auto finishCleanup = [&] {
        result.cleanup = result.eventHandleCloseHr && *result.eventHandleCloseHr != S_OK
            ? Cleanup::Failed
            : result.clientReleaseAttempts != 0 ? Cleanup::ReleaseAttempted : Cleanup::NotAcquired;
    };
    HRESULT hr = device_->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr,
                                   reinterpret_cast<void**>(&client));
    result.activationHr = static_cast<std::int32_t>(hr);
    if (FAILED(hr) || client == nullptr) { releaseClient(); finishCleanup(); return result; }

    REFERENCE_TIME defaultPeriod = 0, minimumPeriod = 0;
    hr = client->GetDevicePeriod(&defaultPeriod, &minimumPeriod);
    result.periodQueryHr = static_cast<std::int32_t>(hr);
    if (FAILED(hr)) { releaseClient(); finishCleanup(); return result; }
    REFERENCE_TIME requested = request.IsCustom() ? *request.requestedPeriod100ns
        : request.mode == Period::Default ? defaultPeriod : minimumPeriod;
    result.periodAvailable = requested > 0;
    if (requested <= 0) { releaseClient(); finishCleanup(); return result; }
    result.requestedPeriod100ns = requested;
    result.requestedBufferFrames = request.requestedFrames;
    result.initializeAttempted = true;
    hr = client->Initialize(AUDCLNT_SHAREMODE_EXCLUSIVE, AUDCLNT_STREAMFLAGS_EVENTCALLBACK,
                            requested, requested, &format->Format, nullptr);
    result.firstInitializeHr = static_cast<std::int32_t>(hr);
    HRESULT finalInitialize = hr;
    if (hr == AUDCLNT_E_BUFFER_SIZE_NOT_ALIGNED) {
        UINT32 alignedFrames = 0;
        const HRESULT sizeHr = client->GetBufferSize(&alignedFrames);
        result.alignmentGetBufferSizeHr = static_cast<std::int32_t>(sizeHr);
        result.alignmentFrames = alignedFrames;
        releaseClient();
        if (sizeHr == S_OK && alignedFrames != 0) {
            requested = ProbeMath::DurationForFrames(alignedFrames, candidate.sampleRate);
            result.alignmentPeriod100ns = requested;
            hr = device_->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr,
                                   reinterpret_cast<void**>(&client));
            result.alignmentActivateHr = static_cast<std::int32_t>(hr);
            if (SUCCEEDED(hr) && client != nullptr) {
                hr = client->Initialize(AUDCLNT_SHAREMODE_EXCLUSIVE, AUDCLNT_STREAMFLAGS_EVENTCALLBACK,
                                        requested, requested, &format->Format, nullptr);
                result.alignmentInitializeHr = static_cast<std::int32_t>(hr);
                finalInitialize = hr;
            }
        }
    }
    if ((result.alignmentGetBufferSizeHr && *result.alignmentGetBufferSizeHr != S_OK) ||
        (result.alignmentActivateHr && *result.alignmentActivateHr != S_OK)) result.finalInitializeHr.reset();
    else if (result.firstInitializeHr != AUDCLNT_E_BUFFER_SIZE_NOT_ALIGNED || result.alignmentInitializeHr)
        result.finalInitializeHr = static_cast<std::int32_t>(finalInitialize);

    HANDLE eventHandle = nullptr;
    if (result.finalInitializeHr && *result.finalInitializeHr == S_OK && client != nullptr) {
        UINT32 actualFrames = 0;
        const HRESULT sizeHr = client->GetBufferSize(&actualFrames);
        result.actualBufferSizeHr = static_cast<std::int32_t>(sizeHr);
        if (sizeHr == S_OK) {
            result.actualBufferFrames = actualFrames;
            result.actualPeriod100ns = ProbeMath::DurationForFrames(actualFrames, candidate.sampleRate);
        }
        eventHandle = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        const HRESULT eventHr = eventHandle != nullptr ? S_OK
            : HRESULT_FROM_WIN32(GetLastError() == ERROR_SUCCESS ? ERROR_NOT_ENOUGH_MEMORY : GetLastError());
        result.eventCreateHr = static_cast<std::int32_t>(eventHr);
        if (eventHandle != nullptr) result.setEventHandleHr = static_cast<std::int32_t>(client->SetEventHandle(eventHandle));
    }
    if (eventHandle != nullptr) {
        if (!CloseHandle(eventHandle)) {
            const DWORD error = GetLastError();
            result.eventHandleCloseHr = static_cast<std::int32_t>(error == ERROR_SUCCESS ? E_FAIL : HRESULT_FROM_WIN32(error));
        } else result.eventHandleCloseHr = S_OK;
    }
    releaseClient();
    finishCleanup();
    return result;
}

} // namespace ExclusiveFormatMatrix
