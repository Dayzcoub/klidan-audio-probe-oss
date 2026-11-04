#include "windows_endpoint_discovery.h"

#define INITGUID
#include <windows.h>
#include <audioclient.h>
#include <devpkey.h>
#include <propkeydef.h>
#include <functiondiscoverykeys_devpkey.h>
#include <mmdeviceapi.h>
#include <mmreg.h>
#include <ks.h>
#include <ksmedia.h>
#include <propsys.h>
#include <propvarutil.h>
#include <objbase.h>
#include <rapidjson/stringbuffer.h>
#include <rapidjson/writer.h>

#include <algorithm>
#include <bit>
#include <cstring>
#include <cwchar>
#include <limits>
#include <memory>

namespace probe::windows_endpoints {
namespace {

constexpr std::size_t kMaximumPropertyStringChars=32768;
constexpr std::size_t kMaximumFormatBlobBytes=65536;

template<class T> class ComOwner {
public:
    ComOwner() = default;
    explicit ComOwner(T* value) : value_(value) {}
    ~ComOwner() { Reset(); }
    ComOwner(const ComOwner&) = delete;
    ComOwner& operator=(const ComOwner&) = delete;
    T** Put() { Reset(); return &value_; }
    T* Get() const { return value_; }
    T* operator->() const { return value_; }
    void Reset() { if(value_){value_->Release();value_=nullptr;} }
private:
    T* value_{};
};

class PropVariantOwner {
public:
    PropVariantOwner() { PropVariantInit(&value_); }
    ~PropVariantOwner() { PropVariantClear(&value_); }
    PROPVARIANT* Put() { PropVariantClear(&value_); PropVariantInit(&value_); return &value_; }
    const PROPVARIANT& Get() const { return value_; }
private:
    PROPVARIANT value_{};
};

struct CoTaskStringDeleter { void operator()(wchar_t* value) const { CoTaskMemFree(value); } };
using CoTaskString = std::unique_ptr<wchar_t, CoTaskStringDeleter>;

std::string Utf8(const std::wstring& value) {
    if(value.empty()) return {};
    const int needed=WideCharToMultiByte(CP_UTF8,WC_ERR_INVALID_CHARS,value.data(),static_cast<int>(value.size()),nullptr,0,nullptr,nullptr);
    if(needed<=0) return {};
    std::string out(static_cast<std::size_t>(needed),'\0');
    if(WideCharToMultiByte(CP_UTF8,WC_ERR_INVALID_CHARS,value.data(),static_cast<int>(value.size()),out.data(),needed,nullptr,nullptr)!=needed)return {};
    return out;
}

std::string DeviceState(std::uint32_t state) {
    switch(state) {
    case DEVICE_STATE_ACTIVE: return "ACTIVE";
    case DEVICE_STATE_DISABLED: return "DISABLED";
    case DEVICE_STATE_NOTPRESENT: return "NOT_PRESENT";
    case DEVICE_STATE_UNPLUGGED: return "UNPLUGGED";
    default: return "UNKNOWN";
    }
}

std::string FormFactor(std::uint32_t value) {
    switch(value) {
    case 0: return "REMOTE_NETWORK_DEVICE";
    case 1: return "SPEAKERS";
    case 2: return "LINE_LEVEL";
    case 3: return "HEADPHONES";
    case 4: return "MICROPHONE";
    case 5: return "HEADSET";
    case 6: return "HANDSET";
    case 7: return "DIGITAL_PASSTHROUGH";
    case 8: return "SPDIF";
    case 9: return "DIGITAL_AUDIO_DISPLAY";
    case 10: return "UNKNOWN";
    default: return "UNRECOGNIZED";
    }
}

PropertyEvidence FailureEvidence(HRESULT hr) { return {static_cast<std::int32_t>(hr),"UNAVAILABLE"}; }

void ReadEndpointProperties(IMMDevice* device, RawEndpoint& endpoint) {
    ComOwner<IPropertyStore> store;
    const HRESULT openHr=device->OpenPropertyStore(STGM_READ,store.Put());
    if(FAILED(openHr)) {
        endpoint.friendlyNameEvidence=FailureEvidence(openHr);
        endpoint.formFactorEvidence=FailureEvidence(openHr);
        endpoint.sharedFormatEvidence=FailureEvidence(openHr);
        return;
    }

    const auto readString=[&](REFPROPERTYKEY key,std::wstring& target,PropertyEvidence& evidence) {
        PropVariantOwner value;const HRESULT hr=store->GetValue(key,value.Put());
        evidence.hresult=static_cast<std::int32_t>(hr);
        if(FAILED(hr)){evidence.state="UNAVAILABLE";return;}
        if(value.Get().vt!=VT_LPWSTR||value.Get().pwszVal==nullptr){evidence.state="MALFORMED";return;}
        const auto length=wcsnlen_s(value.Get().pwszVal,kMaximumPropertyStringChars+1);
        if(length>kMaximumPropertyStringChars){evidence.state="MALFORMED";return;}
        target.assign(value.Get().pwszVal,length);evidence.state="AVAILABLE";
    };
    readString(PKEY_Device_FriendlyName,endpoint.friendlyName,endpoint.friendlyNameEvidence);

    {
        PropVariantOwner value;const HRESULT hr=store->GetValue(PKEY_Device_ContainerId,value.Put());
        endpoint.containerIdEvidence.hresult=static_cast<std::int32_t>(hr);
        if(FAILED(hr)) endpoint.containerIdEvidence.state="UNAVAILABLE";
        else if(value.Get().vt!=VT_CLSID||value.Get().puuid==nullptr) endpoint.containerIdEvidence.state="MALFORMED";
        else {
            wchar_t text[64]{};
            const int length=StringFromGUID2(*value.Get().puuid,text,static_cast<int>(std::size(text)));
            if(length<=1) endpoint.containerIdEvidence.state="MALFORMED";
            else { endpoint.containerId.assign(text,static_cast<std::size_t>(length-1)); endpoint.containerIdEvidence.state="AVAILABLE"; }
        }
    }

    {
        PropVariantOwner value;const HRESULT hr=store->GetValue(PKEY_AudioEndpoint_FormFactor,value.Put());
        endpoint.formFactorEvidence.hresult=static_cast<std::int32_t>(hr);
        if(FAILED(hr))endpoint.formFactorEvidence.state="UNAVAILABLE";
        else if(value.Get().vt!=VT_UI4)endpoint.formFactorEvidence.state="MALFORMED";
        else {endpoint.formFactor=value.Get().ulVal;endpoint.formFactorEvidence.state="AVAILABLE";}
    }
    {
        PropVariantOwner value;const HRESULT hr=store->GetValue(PKEY_AudioEngine_DeviceFormat,value.Put());
        endpoint.sharedFormatEvidence.hresult=static_cast<std::int32_t>(hr);
        if(FAILED(hr)){endpoint.sharedFormatEvidence.state="UNAVAILABLE";return;}
        if(value.Get().vt!=VT_BLOB||(value.Get().blob.cbSize>0&&value.Get().blob.pBlobData==nullptr)||value.Get().blob.cbSize>kMaximumFormatBlobBytes){
            endpoint.sharedFormatEvidence.state="MALFORMED";return;
        }
        endpoint.sharedFormatEvidence.state="AVAILABLE";
        if(value.Get().blob.cbSize>0)endpoint.sharedFormatBlob.assign(value.Get().blob.pBlobData,value.Get().blob.pBlobData+value.Get().blob.cbSize);
    }
}

class MmDeviceSource final : public Source {
public:
    RawDiscovery Enumerate() override {
        RawDiscovery result;
        const HRESULT init=CoInitializeEx(nullptr,COINIT_MULTITHREADED);
        if(FAILED(init)) {
            result.complete=false;
            result.captureHresult=result.renderHresult=static_cast<std::int32_t>(init);
            result.errors.push_back({"BOTH","COM_INITIALIZE",static_cast<std::int32_t>(init)});
            return result;
        }
        struct CoUninitializer { ~CoUninitializer(){CoUninitialize();} } uninit;

        ComOwner<IMMDeviceEnumerator> enumerator;
        const HRESULT create=CoCreateInstance(__uuidof(MMDeviceEnumerator),nullptr,CLSCTX_ALL,
            __uuidof(IMMDeviceEnumerator),reinterpret_cast<void**>(enumerator.Put()));
        if(FAILED(create)) {
            result.complete=false;
            result.captureHresult=result.renderHresult=static_cast<std::int32_t>(create);
            result.errors.push_back({"BOTH","CREATE_ENUMERATOR",static_cast<std::int32_t>(create)});
            return result;
        }

        const auto enumerateDirection=[&](EDataFlow flow,const char* direction,std::int32_t& savedHr) {
            ComOwner<IMMDeviceCollection> collection;
            HRESULT hr=enumerator->EnumAudioEndpoints(flow,DEVICE_STATEMASK_ALL,collection.Put());
            savedHr=static_cast<std::int32_t>(hr);
            if(FAILED(hr)){result.complete=false;result.errors.push_back({direction,"ENUM_AUDIO_ENDPOINTS",static_cast<std::int32_t>(hr)});return;}
            UINT count{};hr=collection->GetCount(&count);
            if(FAILED(hr)){result.complete=false;result.errors.push_back({direction,"GET_COUNT",static_cast<std::int32_t>(hr)});return;}
            for(UINT index=0;index<count;++index) {
                ComOwner<IMMDevice> device;hr=collection->Item(index,device.Put());
                if(FAILED(hr)){result.complete=false;result.errors.push_back({direction,"COLLECTION_ITEM",static_cast<std::int32_t>(hr)});continue;}
                RawEndpoint raw;raw.direction=direction;
                LPWSTR id{};hr=device->GetId(&id);CoTaskString owned(id);raw.endpointIdEvidence.hresult=static_cast<std::int32_t>(hr);
                if(SUCCEEDED(hr)&&owned){const auto length=wcsnlen_s(owned.get(),kMaximumPropertyStringChars+1);if(length<=kMaximumPropertyStringChars){raw.endpointId.assign(owned.get(),length);raw.endpointIdEvidence.state="AVAILABLE";}else raw.endpointIdEvidence.state="MALFORMED";}
                else raw.endpointIdEvidence.state="UNAVAILABLE";
                DWORD state{};hr=device->GetState(&state);raw.deviceState=state;raw.deviceStateEvidence.hresult=static_cast<std::int32_t>(hr);
                raw.deviceStateEvidence.state=SUCCEEDED(hr)?"AVAILABLE":"UNAVAILABLE";
                ReadEndpointProperties(device.Get(),raw);
                result.endpoints.push_back(std::move(raw));
            }
        };
        enumerateDirection(eCapture,"CAPTURE",result.captureHresult);
        enumerateDirection(eRender,"RENDER",result.renderHresult);
        return result;
    }
};

template<class Writer> void WriteEvidence(Writer& writer,const PropertyEvidence& evidence) {
    writer.StartObject();writer.Key("hresult");writer.Int(evidence.hresult);writer.Key("state");writer.String(evidence.state.c_str());writer.EndObject();
}

} // namespace

SharedModeFormat ParseSharedModeFormat(std::span<const std::uint8_t> blob,PropertyEvidence property) {
    SharedModeFormat result;result.property=std::move(property);
    if(result.property.state!="AVAILABLE") {result.parseState=result.property.state;return result;}
    if(blob.size()<sizeof(WAVEFORMATEX)){result.parseState="MALFORMED";return result;}
    WAVEFORMATEX format{};std::memcpy(&format,blob.data(),sizeof(format));
    const std::size_t required=sizeof(WAVEFORMATEX)+static_cast<std::size_t>(format.cbSize);
    if(required>blob.size()||format.nChannels==0||format.nSamplesPerSec==0||format.wBitsPerSample==0){result.parseState="MALFORMED";return result;}
    result.formatTag=format.wFormatTag;
    result.reportedChannelCount=format.nChannels;
    result.sampleRate=format.nSamplesPerSec;
    result.bitsPerSample=format.wBitsPerSample;
    result.validBitsPerSample=format.wBitsPerSample;
    if(format.wFormatTag==WAVE_FORMAT_PCM)result.encoding="PCM";
    else if(format.wFormatTag==WAVE_FORMAT_IEEE_FLOAT)result.encoding="FLOAT";
    else if(format.wFormatTag==WAVE_FORMAT_EXTENSIBLE) {
        constexpr std::size_t extensibleTail=sizeof(WAVEFORMATEXTENSIBLE)-sizeof(WAVEFORMATEX);
        if(format.cbSize<extensibleTail||blob.size()<sizeof(WAVEFORMATEXTENSIBLE)){result.parseState="MALFORMED";return result;}
        WAVEFORMATEXTENSIBLE extended{};std::memcpy(&extended,blob.data(),sizeof(extended));
        result.validBitsPerSample=extended.Samples.wValidBitsPerSample;
        result.hasSpeakerMask=true;result.speakerMask=extended.dwChannelMask;
        if(IsEqualGUID(extended.SubFormat,KSDATAFORMAT_SUBTYPE_PCM))result.encoding="PCM";
        else if(IsEqualGUID(extended.SubFormat,KSDATAFORMAT_SUBTYPE_IEEE_FLOAT))result.encoding="FLOAT";
        else result.encoding="UNKNOWN";
        if(result.speakerMask==0)result.speakerMaskState="UNKNOWN";
        else result.speakerMaskState=std::popcount(result.speakerMask)==result.reportedChannelCount?"VALID":"INVALID";
    } else result.encoding="UNKNOWN";
    result.hasFormat=true;
    result.topologyState=result.reportedChannelCount<=64?"REPRESENTABLE_1_TO_64":"UNSUPPORTED_CHANNEL_COUNT";
    result.parseState="VALID";
    return result;
}

DiscoveryResult Discover(Source& source) {
    const auto raw=source.Enumerate();DiscoveryResult result;
    result.captureHresult=raw.captureHresult;result.renderHresult=raw.renderHresult;
    result.errors=raw.errors;result.complete=raw.complete&&raw.errors.empty();
    result.endpoints.reserve(raw.endpoints.size());
    for(const auto& item:raw.endpoints) {
        Endpoint endpoint;endpoint.endpointId=Utf8(item.endpointId);endpoint.endpointIdEvidence=item.endpointIdEvidence;
        endpoint.friendlyName=Utf8(item.friendlyName);endpoint.friendlyNameEvidence=item.friendlyNameEvidence;
        endpoint.direction=item.direction;endpoint.rawDeviceState=item.deviceState;
        endpoint.deviceStateEvidence=item.deviceStateEvidence;
        endpoint.deviceState=item.deviceStateEvidence.state=="AVAILABLE"?DeviceState(item.deviceState):"UNKNOWN";
        endpoint.formFactor=item.formFactorEvidence.state=="AVAILABLE"?FormFactor(item.formFactor):"UNKNOWN";
        endpoint.formFactorEvidence=item.formFactorEvidence;
        endpoint.containerId=Utf8(item.containerId);endpoint.containerIdEvidence=item.containerIdEvidence;
        endpoint.sharedModeFormat=ParseSharedModeFormat(item.sharedFormatBlob,item.sharedFormatEvidence);
        result.endpoints.push_back(std::move(endpoint));
    }
    return result;
}

std::string SerializeJson(const DiscoveryResult& result) {
    rapidjson::StringBuffer buffer;rapidjson::Writer<rapidjson::StringBuffer> writer(buffer);
    writer.StartObject();writer.Key("provenance");writer.String("REAL_WINDOWS_METADATA");
    writer.Key("operationState");writer.String(result.complete?"COMPLETE":"PARTIAL");
    writer.Key("enumerationHresults");writer.StartObject();writer.Key("capture");writer.Int(result.captureHresult);writer.Key("render");writer.Int(result.renderHresult);writer.EndObject();
    writer.Key("errors");writer.StartArray();for(const auto& error:result.errors){writer.StartObject();writer.Key("direction");writer.String(error.direction.c_str());writer.Key("stage");writer.String(error.stage.c_str());writer.Key("hresult");writer.Int(error.hresult);writer.EndObject();}writer.EndArray();
    writer.Key("endpoints");writer.StartArray();
    for(const auto& endpoint:result.endpoints) {
        const auto& format=endpoint.sharedModeFormat;
        writer.StartObject();writer.Key("endpointId");if(endpoint.endpointId.empty())writer.Null();else writer.String(endpoint.endpointId.c_str());
        writer.Key("endpointIdEvidence");WriteEvidence(writer,endpoint.endpointIdEvidence);
        writer.Key("friendlyName");if(endpoint.friendlyName.empty())writer.Null();else writer.String(endpoint.friendlyName.c_str());writer.Key("friendlyNameEvidence");WriteEvidence(writer,endpoint.friendlyNameEvidence);
        writer.Key("direction");writer.String(endpoint.direction.c_str());writer.Key("deviceState");writer.String(endpoint.deviceState.c_str());writer.Key("rawDeviceState");if(endpoint.deviceStateEvidence.state=="AVAILABLE")writer.Uint(endpoint.rawDeviceState);else writer.Null();writer.Key("deviceStateEvidence");WriteEvidence(writer,endpoint.deviceStateEvidence);
        writer.Key("formFactor");writer.String(endpoint.formFactor.c_str());writer.Key("formFactorEvidence");WriteEvidence(writer,endpoint.formFactorEvidence);
        writer.Key("containerId");if(endpoint.containerId.empty())writer.Null();else writer.String(endpoint.containerId.c_str());writer.Key("containerIdEvidence");WriteEvidence(writer,endpoint.containerIdEvidence);
        writer.Key("sharedModeFormat");writer.StartObject();writer.Key("property");WriteEvidence(writer,format.property);writer.Key("parseState");writer.String(format.parseState.c_str());writer.Key("formatTag");if(format.hasFormat)writer.Int(format.formatTag);else writer.Null();
        writer.Key("encoding");if(format.hasFormat)writer.String(format.encoding.c_str());else writer.Null();writer.Key("reportedChannelCount");if(format.hasFormat)writer.Int(format.reportedChannelCount);else writer.Null();
        writer.Key("sampleRate");if(format.hasFormat)writer.Uint(format.sampleRate);else writer.Null();writer.Key("bitsPerSample");if(format.hasFormat)writer.Int(format.bitsPerSample);else writer.Null();writer.Key("validBitsPerSample");if(format.hasFormat)writer.Int(format.validBitsPerSample);else writer.Null();
        writer.Key("topologyState");writer.String(format.topologyState.c_str());writer.Key("speakerMask");if(format.hasSpeakerMask)writer.Uint(format.speakerMask);else writer.Null();writer.Key("speakerMaskState");writer.String(format.speakerMaskState.c_str());writer.EndObject();writer.EndObject();
    }
    writer.EndArray();writer.EndObject();return buffer.GetString();
}

std::unique_ptr<Source> CreateMmDeviceSource() { return std::make_unique<MmDeviceSource>(); }

} // namespace probe::windows_endpoints
