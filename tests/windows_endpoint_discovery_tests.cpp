#include "../src/worker/windows_endpoint_discovery.h"

#include <windows.h>
#include <mmdeviceapi.h>
#include <mmreg.h>
#include <ks.h>
#include <ksmedia.h>
#include <rapidjson/document.h>

#include <cstring>
#include <iostream>

using namespace probe::windows_endpoints;

namespace {
int failures{};
void Check(bool condition,const char* name) { if(condition)std::cout<<"PASS: "<<name<<"\n";else{std::cerr<<"FAIL: "<<name<<"\n";++failures;} }

std::vector<std::uint8_t> Pcm(std::uint16_t channels,std::uint16_t bits=16,std::uint32_t rate=48000) {
    WAVEFORMATEX format{};format.wFormatTag=WAVE_FORMAT_PCM;format.nChannels=channels;format.nSamplesPerSec=rate;format.wBitsPerSample=bits;format.nBlockAlign=static_cast<WORD>(channels*bits/8);format.nAvgBytesPerSec=rate*format.nBlockAlign;
    std::vector<std::uint8_t> bytes(sizeof(format));std::memcpy(bytes.data(),&format,sizeof(format));return bytes;
}
std::vector<std::uint8_t> Extensible(std::uint16_t channels,std::uint16_t validBits,std::uint32_t mask,const GUID& subtype=KSDATAFORMAT_SUBTYPE_PCM) {
    WAVEFORMATEXTENSIBLE format{};format.Format.wFormatTag=WAVE_FORMAT_EXTENSIBLE;format.Format.nChannels=channels;format.Format.nSamplesPerSec=48000;format.Format.wBitsPerSample=32;format.Format.nBlockAlign=static_cast<WORD>(channels*4);format.Format.nAvgBytesPerSec=48000*format.Format.nBlockAlign;format.Format.cbSize=static_cast<WORD>(sizeof(format)-sizeof(WAVEFORMATEX));format.Samples.wValidBitsPerSample=validBits;format.dwChannelMask=mask;format.SubFormat=subtype;
    std::vector<std::uint8_t> bytes(sizeof(format));std::memcpy(bytes.data(),&format,sizeof(format));return bytes;
}
RawEndpoint Item(std::wstring id,std::wstring name,std::string direction,std::uint32_t state,std::vector<std::uint8_t> blob) {
    RawEndpoint e;e.endpointId=std::move(id);e.friendlyName=std::move(name);e.direction=std::move(direction);e.deviceState=state;e.formFactor=4;
    e.endpointIdEvidence={S_OK,"AVAILABLE"};e.friendlyNameEvidence={S_OK,"AVAILABLE"};e.deviceStateEvidence={S_OK,"AVAILABLE"};e.formFactorEvidence={S_OK,"AVAILABLE"};e.sharedFormatEvidence={S_OK,"AVAILABLE"};e.sharedFormatBlob=std::move(blob);return e;
}
class FakeSource final:public Source { public: RawDiscovery raw;RawDiscovery Enumerate() override{return raw;} };
}

int main() {
    const PropertyEvidence available{S_OK,"AVAILABLE"};
    const auto empty=ParseSharedModeFormat({},available);
    Check(empty.parseState=="MALFORMED"&&!empty.hasFormat,"empty format blob rejected");
    auto malformed=Pcm(2);malformed.resize(sizeof(WAVEFORMATEX)-1);
    Check(ParseSharedModeFormat(malformed,available).parseState=="MALFORMED","truncated WAVEFORMATEX rejected");
    auto badExtension=Extensible(2,24,3);badExtension.resize(sizeof(WAVEFORMATEX)+2);
    Check(ParseSharedModeFormat(badExtension,available).parseState=="MALFORMED","truncated WAVEFORMATEXTENSIBLE rejected");
    Check(ParseSharedModeFormat({},PropertyEvidence{E_ACCESSDENIED,"UNAVAILABLE"}).parseState=="UNAVAILABLE","property HRESULT retained for unavailable format");

    const auto pcm=ParseSharedModeFormat(Pcm(2),available);
    Check(pcm.hasFormat&&pcm.encoding=="PCM"&&pcm.reportedChannelCount==2&&pcm.sampleRate==48000,"PCM WAVEFORMATEX parsed exactly");
    const auto floating=ParseSharedModeFormat([]{auto f=Pcm(2,32);WAVEFORMATEX w{};std::memcpy(&w,f.data(),sizeof(w));w.wFormatTag=WAVE_FORMAT_IEEE_FLOAT;std::memcpy(f.data(),&w,sizeof(w));return f;}(),available);
    Check(floating.encoding=="FLOAT"&&floating.bitsPerSample==32,"IEEE float WAVEFORMATEX parsed");
    const auto extensible=ParseSharedModeFormat(Extensible(8,24,0x63F),available);
    Check(extensible.encoding=="PCM"&&extensible.validBitsPerSample==24&&extensible.reportedChannelCount==8&&extensible.speakerMaskState=="VALID","extensible PCM and valid bits parsed");
    const auto invalidMask=ParseSharedModeFormat(Extensible(2,16,SPEAKER_FRONT_LEFT),available);
    Check(invalidMask.speakerMaskState=="INVALID"&&invalidMask.reportedChannelCount==2,"speaker mask mismatch is retained as invalid metadata");

    for(const auto channels:{1,2,8,16,32,48,64}) {
        const auto parsed=ParseSharedModeFormat(Extensible(static_cast<WORD>(channels),24,0),available);
        Check(parsed.hasFormat&&parsed.reportedChannelCount==channels&&parsed.topologyState=="REPRESENTABLE_1_TO_64","supported channel count retained without inferred groups");
    }
    for(const auto channels:{65,128,256}) {
        const auto parsed=ParseSharedModeFormat(Pcm(static_cast<WORD>(channels)),available);
        Check(parsed.hasFormat&&parsed.reportedChannelCount==channels&&parsed.topologyState=="UNSUPPORTED_CHANNEL_COUNT","out-of-range channel count retained and marked unsupported");
    }

    FakeSource source;
    source.raw.captureHresult=S_OK;source.raw.renderHresult=S_OK;
    source.raw.endpoints={Item(L"capture-a",L"Same Name","CAPTURE",DEVICE_STATE_ACTIVE,Pcm(2)),Item(L"render-a",L"Same Name","RENDER",DEVICE_STATE_DISABLED,Extensible(8,24,0)),Item(L"render-b",L"Unplugged","RENDER",DEVICE_STATE_UNPLUGGED,Pcm(1)),Item(L"capture-b",L"Not present","CAPTURE",DEVICE_STATE_NOTPRESENT,Pcm(1)),Item(L"capture-c",L"Disabled","CAPTURE",DEVICE_STATE_DISABLED,Pcm(1))};
    const auto result=Discover(source);
    Check(result.complete&&result.endpoints.size()==5,"mixed Capture/Render list preserves duplicate friendly names");
    Check(result.endpoints[0].deviceState=="ACTIVE"&&result.endpoints[1].deviceState=="DISABLED"&&result.endpoints[2].deviceState=="UNPLUGGED"&&result.endpoints[3].deviceState=="NOT_PRESENT","all Windows endpoint states map distinctly");
    const auto json=SerializeJson(result);rapidjson::Document document;document.Parse(json.c_str());
    Check(!document.HasParseError()&&document["provenance"].GetString()==std::string("REAL_WINDOWS_METADATA"),"serialized listing is valid and explicitly real metadata");
    Check(document["endpoints"][0]["sharedModeFormat"]["reportedChannelCount"].GetInt()==2,"serialized format keeps reported channel count");

    FakeSource emptySource;emptySource.raw.captureHresult=S_OK;emptySource.raw.renderHresult=S_OK;
    Check(Discover(emptySource).endpoints.empty(),"successful empty enumeration remains an empty list");
    FakeSource captureOnly;captureOnly.raw.endpoints.push_back(Item(L"capture-only",L"Input","CAPTURE",DEVICE_STATE_ACTIVE,Pcm(1)));
    FakeSource renderOnly;renderOnly.raw.endpoints.push_back(Item(L"render-only",L"Output","RENDER",DEVICE_STATE_ACTIVE,Pcm(2)));
    Check(Discover(captureOnly).endpoints.size()==1&&Discover(captureOnly).endpoints[0].direction=="CAPTURE","Capture-only source remains Capture");
    Check(Discover(renderOnly).endpoints.size()==1&&Discover(renderOnly).endpoints[0].direction=="RENDER","Render-only source remains Render");
    FakeSource failedSource;failedSource.raw.captureHresult=E_ACCESSDENIED;failedSource.raw.renderHresult=S_OK;failedSource.raw.errors.push_back({"CAPTURE","ENUM_AUDIO_ENDPOINTS",E_ACCESSDENIED});
    const auto failed=Discover(failedSource);
    Check(!failed.complete&&failed.captureHresult==E_ACCESSDENIED&&failed.errors.front().hresult==E_ACCESSDENIED,"enumeration HRESULT and failure stage are retained");
    FakeSource disappearance;disappearance.raw.captureHresult=S_OK;disappearance.raw.renderHresult=S_OK;disappearance.raw.errors.push_back({"CAPTURE","COLLECTION_ITEM",HRESULT_FROM_WIN32(ERROR_NOT_FOUND)});disappearance.raw.endpoints.push_back(Item(L"still-here",L"Still present","CAPTURE",DEVICE_STATE_ACTIVE,Pcm(1)));
    const auto partial=Discover(disappearance);
    Check(!partial.complete&&partial.endpoints.size()==1&&partial.errors.size()==1,"device disappearance produces partial results with explicit error");
    auto missing=Item(L"id",L"","CAPTURE",DEVICE_STATE_ACTIVE,{});missing.friendlyNameEvidence={E_ACCESSDENIED,"UNAVAILABLE"};missing.formFactorEvidence={E_FAIL,"UNAVAILABLE"};missing.sharedFormatEvidence={E_FAIL,"UNAVAILABLE"};
    FakeSource missingProps;missingProps.raw.endpoints.push_back(std::move(missing));
    const auto missingResult=Discover(missingProps);
    Check(missingResult.endpoints[0].friendlyNameEvidence.hresult==E_ACCESSDENIED&&missingResult.endpoints[0].sharedModeFormat.property.hresult==E_FAIL,"missing property failures retain independent HRESULT evidence");
    auto failedState=Item(L"id",L"Endpoint","CAPTURE",DEVICE_STATE_ACTIVE,Pcm(1));failedState.deviceStateEvidence={E_FAIL,"UNAVAILABLE"};
    FakeSource stateFailure;stateFailure.raw.endpoints.push_back(std::move(failedState));rapidjson::Document stateJson;const auto stateText=SerializeJson(Discover(stateFailure));stateJson.Parse(stateText.c_str());
    Check(stateJson["endpoints"][0]["deviceState"].GetString()==std::string("UNKNOWN")&&stateJson["endpoints"][0]["rawDeviceState"].IsNull(),"failed device-state query does not serialize an unobserved raw state value");
    return failures?1:0;
}
