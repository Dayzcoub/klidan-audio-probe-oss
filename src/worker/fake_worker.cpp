#include <windows.h>
#include <audioclient.h>
#include <mmdeviceapi.h>
#include <bcrypt.h>
#include <objbase.h>
#include <rapidjson/document.h>
#include <rapidjson/error/en.h>
#include <rapidjson/stringbuffer.h>
#include <rapidjson/writer.h>

#include "matrix_timestamp.h"

#include <algorithm>
#include <array>
#include <atomic>
#include <charconv>
#include <chrono>
#include <condition_variable>
#if defined(PROBE_TEST_WASAPI_BACKEND)
#include <cstdlib>
#endif
#include <cstdint>
#include <deque>
#include <iostream>
#include <filesystem>
#include <fstream>
#include <initializer_list>
#include <memory>
#include <mutex>
#include <optional>
#include <set>
#include <string>
#include <string_view>
#include <thread>
#include <unordered_set>
#include <unordered_map>
#include <vector>
#include "wasapi_format_matrix_api.h"
#include "exclusive_period_discovery.h"
#include "stream_report_contract.h"
#include "duplex_validation_report.h"
#include "windows_endpoint_discovery.h"

namespace {
constexpr std::uint32_t kMaximumFrame = 1'048'576;
constexpr std::size_t kMaximumConsumedRealConsentReceipts = 4096;
constexpr DWORD kDuplexChildDeadlineSlackSeconds = 25;
constexpr DWORD kDuplexGracefulShutdownMilliseconds = 3000;
constexpr DWORD kDuplexForcedShutdownWaitMilliseconds = 3000;

std::optional<ExclusiveFormatMatrix::PeriodRequest> ParseCustomDuplexPeriod(std::string_view value) {
    if (!value.starts_with("CUSTOM:")) return std::nullopt;
    const auto separator = value.find(':', 7);
    if (separator == std::string_view::npos || value.find(':', separator + 1) != std::string_view::npos) return std::nullopt;
    std::int64_t period = 0;
    const auto periodResult = std::from_chars(value.data() + 7, value.data() + separator, period);
    std::uint32_t frames = 0;
    const auto framesResult = std::from_chars(value.data() + separator + 1, value.data() + value.size(), frames);
    if (periodResult.ec != std::errc{} || periodResult.ptr != value.data() + separator || framesResult.ec != std::errc{} || framesResult.ptr != value.data() + value.size() || period <= 0 || frames == 0 || frames > 48'000 || ProbeMath::DurationForFrames(frames, 48'000) != period) return std::nullopt;
    return ExclusiveFormatMatrix::PeriodRequest::Custom(period, frames);
}
constexpr std::string_view kProtocol = "\"protocolVersion\":{\"major\":1,\"minor\":0},\"schemaVersion\":1";
struct ProtocolFailure : std::runtime_error { using std::runtime_error::runtime_error; };
struct Rejection : std::runtime_error { std::string code; Rejection(std::string c, const char* m) : std::runtime_error(m), code(std::move(c)) {} };

void RejectDuplicateProperties(const rapidjson::Value& root) {
    struct Entry { const rapidjson::Value* value; unsigned depth; };
    std::vector<Entry> todo{{&root, 0}};
    while (!todo.empty()) {
        auto [value, depth] = todo.back(); todo.pop_back();
        if (depth > 32) throw ProtocolFailure("JSON nesting exceeds 32");
        if (value->IsObject()) {
            std::unordered_set<std::string_view> names;
            for (auto m = value->MemberBegin(); m != value->MemberEnd(); ++m) {
                if (!names.emplace(m->name.GetString(), m->name.GetStringLength()).second) throw ProtocolFailure("duplicate JSON property");
                todo.push_back({&m->value, depth + 1});
            }
        } else if (value->IsArray()) for (auto i = value->Begin(); i != value->End(); ++i) todo.push_back({&*i, depth + 1});
    }
}

rapidjson::Document ParseObject(const std::vector<std::uint8_t>& bytes) {
    if (bytes.empty() || bytes.size() > kMaximumFrame || (bytes.size() >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)) throw ProtocolFailure("invalid frame size or BOM");
    rapidjson::Document doc;
    doc.Parse<rapidjson::kParseIterativeFlag | rapidjson::kParseValidateEncodingFlag>(reinterpret_cast<const char*>(bytes.data()), bytes.size());
    if (doc.HasParseError()) throw ProtocolFailure(std::string(rapidjson::GetParseError_En(doc.GetParseError())) + " at byte " + std::to_string(doc.GetErrorOffset()));
    if (!doc.IsObject()) throw ProtocolFailure("JSON root must be an object");
    RejectDuplicateProperties(doc);
    return doc;
}

bool Transfer(HANDLE pipe, void* buffer, std::size_t size, bool writing, DWORD timeout, DWORD& error, std::size_t& completedBytes) {
    auto* cursor=static_cast<std::uint8_t*>(buffer);
    completedBytes=0;
    while(size) {
        OVERLAPPED operation{}; operation.hEvent=CreateEventW(nullptr,TRUE,FALSE,nullptr);
        if(!operation.hEvent){error=GetLastError();return false;}
        DWORD transferred=0;
        const BOOL immediate=writing?WriteFile(pipe,cursor,static_cast<DWORD>(size),nullptr,&operation):ReadFile(pipe,cursor,static_cast<DWORD>(size),nullptr,&operation);
        if(!immediate) {
            error=GetLastError();
            if(error!=ERROR_IO_PENDING){CloseHandle(operation.hEvent);return false;}
            const DWORD waited=WaitForSingleObject(operation.hEvent,timeout);
            if(waited!=WAIT_OBJECT_0){error=waited==WAIT_TIMEOUT?WAIT_TIMEOUT:GetLastError();CancelIoEx(pipe,&operation);GetOverlappedResult(pipe,&operation,&transferred,TRUE);CloseHandle(operation.hEvent);return false;}
        }
        const BOOL completed=GetOverlappedResult(pipe,&operation,&transferred,FALSE);
        if(!completed){error=GetLastError();CloseHandle(operation.hEvent);return false;}
        CloseHandle(operation.hEvent);
        if(!transferred){error=ERROR_BROKEN_PIPE;return false;}
        cursor+=transferred;size-=transferred;completedBytes+=transferred;
    }
    error=ERROR_SUCCESS;return true;
}
bool ReadExactly(HANDLE pipe, void* destination, std::size_t size, bool cleanEof = false) {
    DWORD error{};
    std::size_t completed{};
    if(Transfer(pipe,destination,size,false,INFINITE,error,completed))return true;
    if(cleanEof&&completed==0&&error==ERROR_BROKEN_PIPE)return false;
    throw ProtocolFailure(error==WAIT_TIMEOUT?"pipe read timed out":"truncated or disconnected frame");
}
bool ReadFrame(HANDLE pipe, std::vector<std::uint8_t>& payload) {
    std::array<std::uint8_t,4> header{}; if (!ReadExactly(pipe, header.data(), header.size(), true)) return false;
    const std::uint32_t n = static_cast<std::uint32_t>(header[0]) | (static_cast<std::uint32_t>(header[1]) << 8) | (static_cast<std::uint32_t>(header[2]) << 16) | (static_cast<std::uint32_t>(header[3]) << 24);
    if (n == 0 || n > kMaximumFrame) throw ProtocolFailure("invalid frame length");
    payload.resize(n); ReadExactly(pipe, payload.data(), payload.size()); return true;
}

std::string GuidString(std::string_view input) {
    std::wstring wide(input.begin(), input.end());
    GUID id{};
    const auto wrapped = wide.size() == 38 ? wide : L"{" + wide + L"}";
    if (FAILED(CLSIDFromString(wrapped.c_str(), &id))) throw Rejection("INVALID_ARGUMENT", "request identifier must be a UUID");
    wchar_t out[40]{}; if (!StringFromGUID2(id, out, 40)) throw ProtocolFailure("UUID serialization failed");
    std::wstring result(out); result.erase(std::remove(result.begin(), result.end(), L'{'), result.end()); result.erase(std::remove(result.begin(), result.end(), L'}'), result.end());
    return std::string(result.begin(), result.end());
}
std::string NewGuid() { GUID id{}; if (FAILED(CoCreateGuid(&id))) throw ProtocolFailure("UUID generation failed"); wchar_t out[40]{}; StringFromGUID2(id, out, 40); std::wstring w(out); w.erase(std::remove(w.begin(),w.end(),L'{'),w.end()); w.erase(std::remove(w.begin(),w.end(),L'}'),w.end()); return std::string(w.begin(),w.end()); }

bool ReadString(const rapidjson::Value& object, const char* name, std::string& value) { auto it = object.FindMember(name); if (it == object.MemberEnd() || !it->value.IsString()) return false; value.assign(it->value.GetString(), it->value.GetStringLength()); return true; }
bool ReadInt(const rapidjson::Value& object, const char* name, int& value) { auto it = object.FindMember(name); if (it == object.MemberEnd() || !it->value.IsInt()) return false; value = it->value.GetInt(); return true; }
bool ValidCommandToken(std::string_view value) { return !value.empty() && value.size() <= 64 && std::all_of(value.begin(), value.end(), [](unsigned char c) { return (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_'; }); }
bool SupportsSchema(const rapidjson::Value& schemas,const char* name) {
    auto it=schemas.FindMember(name);
    if(it==schemas.MemberEnd()||!it->value.IsArray()||it->value.Empty()||it->value.Size()>16)return false;
    bool supported=false;
    for(auto version=it->value.Begin();version!=it->value.End();++version){if(!version->IsUint()||version->GetUint()==0)return false;supported|=version->GetUint()==1;}
    return supported;
}
std::string ValidateHandshake(const rapidjson::Value& hello,std::string& requestId) {
    std::string kind,command;
    std::string rawId;
    if(!ReadString(hello,"requestId",rawId))return "Handshake requestId must be a UUID.";
    try{requestId=GuidString(rawId);}catch(...){requestId.clear();return "Handshake requestId must be a UUID.";}
    if(!ReadString(hello,"kind",kind)||kind!="request"||!ReadString(hello,"command",command)||command!="Handshake")return "Handshake kind and command are required.";
    int bootstrap{};
    if(!ReadInt(hello,"bootstrapVersion",bootstrap)||bootstrap!=1)return "Unsupported bootstrap version.";
    auto payload=hello.FindMember("payload");
    if(payload==hello.MemberEnd()||!payload->value.IsObject())return "Handshake payload is required.";
    auto protocol=payload->value.FindMember("protocol");
    if(protocol==payload->value.MemberEnd()||!protocol->value.IsObject())return "Required protocol versions are missing.";
    int major{},minimum{},maximum{};
    if(!ReadInt(protocol->value,"major",major)||!ReadInt(protocol->value,"minMinor",minimum)||!ReadInt(protocol->value,"maxMinor",maximum))return "Required protocol versions are missing.";
    if(major!=1||minimum<0||maximum<minimum||minimum>0||maximum<0)return "No supported protocol version was offered.";
    auto schemas=payload->value.FindMember("schemas");
    if(schemas==payload->value.MemberEnd()||!schemas->value.IsObject())return "Required schema versions are missing.";
    if(!SupportsSchema(schemas->value,"command")||!SupportsSchema(schemas->value,"event")||!SupportsSchema(schemas->value,"report"))return "No supported command, event, and report schema set was offered.";
    return {};
}
std::string ValidateRequestEnvelope(const rapidjson::Value& request,std::string& correlation,std::string& command) {
    std::string requestId,kind;
    if(ReadString(request,"requestId",requestId)){try{correlation=GuidString(requestId);}catch(...){correlation.clear();}}
    if(ReadString(request,"commandOrEvent",command)&&!ValidCommandToken(command))command.clear();
    auto protocol=request.FindMember("protocolVersion");
    if(protocol==request.MemberEnd()||!protocol->value.IsObject())return "INCOMPATIBLE_PROTOCOL";
    int major{},minor{};
    if(!ReadInt(protocol->value,"major",major)||!ReadInt(protocol->value,"minor",minor)||major!=1||minor!=0)return "INCOMPATIBLE_PROTOCOL";
    int schema{};
    if(!ReadInt(request,"schemaVersion",schema)||schema!=1)return "INCOMPATIBLE_PROTOCOL";
    if(correlation.empty())return "INVALID_REQUEST";
    if(!ReadString(request,"kind",kind)||kind!="request")return "INVALID_REQUEST";
    if(command.empty())return "INVALID_REQUEST";
    auto correlationId=request.FindMember("correlationId");
    auto sessionId=request.FindMember("sessionId");
    auto sequence=request.FindMember("eventSequence");
    if(correlationId==request.MemberEnd()||!correlationId->value.IsNull()||sessionId==request.MemberEnd()||!sessionId->value.IsNull()||sequence==request.MemberEnd()||!sequence->value.IsUint64()||sequence->value.GetUint64()!=0)return "INVALID_REQUEST";
    auto payload=request.FindMember("payload");
    if(payload==request.MemberEnd()||!payload->value.IsObject())return "INVALID_REQUEST";
    return {};
}

class Writer {
public:
    explicit Writer(HANDLE pipe) : pipe_(pipe) {}
    bool Raw(std::string_view json) {
        if (json.empty() || json.size() > kMaximumFrame) return false;
        std::lock_guard lock(mutex_);
        const auto n = static_cast<std::uint32_t>(json.size()); std::array<std::uint8_t,4> h{static_cast<std::uint8_t>(n),static_cast<std::uint8_t>(n>>8),static_cast<std::uint8_t>(n>>16),static_cast<std::uint8_t>(n>>24)};
        return Write(h.data(),h.size()) && Write(json.data(),json.size());
    }
    bool Error(std::string_view stage, std::string_view code, std::string_view message,std::string_view correlation={}) {
        const auto id=correlation.empty()?"null":"\""+std::string(correlation)+"\"";
        return Raw("{\"kind\":\"error\",\"failureStage\":\""+std::string(stage)+"\",\"errorCode\":\""+std::string(code)+"\",\"hresult\":null,\"correlationId\":"+id+",\"message\":\""+std::string(message)+"\"}");
    }
    bool Response(std::string_view correlation, std::string_view command, std::string_view status, std::string_view code, std::string_view message, std::string_view payload = "{}", std::string_view session = "null") {
        const auto errorCode = code.empty() ? "null" : "\""+std::string(code)+"\"";
        const auto text = message.empty() ? "null" : "\""+std::string(message)+"\"";
        return Raw("{"+std::string(kProtocol)+",\"kind\":\"response\",\"requestId\":null,\"correlationId\":\""+std::string(correlation)+"\",\"sessionId\":"+std::string(session)+",\"eventSequence\":0,\"commandOrEvent\":\""+std::string(command)+"\",\"status\":\""+std::string(status)+"\",\"errorCode\":"+errorCode+",\"message\":"+text+",\"payload\":"+std::string(payload)+"}");
    }
    bool Event(std::string_view correlation, std::string_view session, std::string_view name, std::string_view payload) {
        std::lock_guard lock(mutex_);
        const auto sequence = ++sequence_;
        const auto json = "{"+std::string(kProtocol)+",\"kind\":\"event\",\"requestId\":null,\"correlationId\":\""+std::string(correlation)+"\",\"sessionId\":\""+std::string(session)+"\",\"eventSequence\":"+std::to_string(sequence)+",\"commandOrEvent\":\""+std::string(name)+"\",\"payload\":"+std::string(payload)+"}";
        return RawLocked(json);
    }
private:
    bool Write(const void* data, std::size_t size) { DWORD error{};std::size_t completed{};return Transfer(pipe_,const_cast<void*>(data),size,true,5000,error,completed); }
    bool RawLocked(std::string_view json) { if(json.empty()||json.size()>kMaximumFrame)return false;const auto n=static_cast<std::uint32_t>(json.size());std::array<std::uint8_t,4> h{static_cast<std::uint8_t>(n),static_cast<std::uint8_t>(n>>8),static_cast<std::uint8_t>(n>>16),static_cast<std::uint8_t>(n>>24)};return Write(h.data(),h.size())&&Write(json.data(),json.size()); }
    HANDLE pipe_; std::mutex mutex_; std::uint64_t sequence_{};
};

class EventPublisher {
public:
    explicit EventPublisher(Writer& writer):writer_(writer),thread_([this]{Run();}){}
    ~EventPublisher(){Stop();}
    bool Control(std::string correlation,std::string session,std::string name,std::string payload) {
        std::unique_lock lock(mutex_);
        if(name=="Stopping"||name=="Completed"||name=="Failed")latestSnapshot_.reset();
        cv_.wait(lock,[&]{return controls_.size()<ControlCapacity||failed_||stopping_;});
        if(failed_||stopping_)return false;
        controls_.push_back({std::move(correlation),std::move(session),std::move(name),std::move(payload),0});
        cv_.notify_all();return true;
    }
    bool Snapshot(std::string correlation,std::string session,std::string payload) {
        std::lock_guard lock(mutex_);if(failed_||stopping_)return false;
        const auto coalesced=latestSnapshot_?latestSnapshot_->coalesced+1:0;
        latestSnapshot_=Event{std::move(correlation),std::move(session),"MeterSnapshot",std::move(payload),coalesced};
        cv_.notify_all();return true;
    }
    void Stop() {
        {std::lock_guard lock(mutex_);stopping_=true;cv_.notify_all();}
        if(thread_.joinable())thread_.join();
    }
private:
    struct Event {std::string correlation,session,name,payload;std::uint64_t coalesced{};};
    static constexpr std::size_t ControlCapacity=32;
    void Run() {
        for(;;){Event event;
            {std::unique_lock lock(mutex_);cv_.wait(lock,[&]{return failed_||!controls_.empty()||latestSnapshot_.has_value()||stopping_;});
             if(failed_||(stopping_&&controls_.empty()&&!latestSnapshot_))return;
             if(!controls_.empty()){event=std::move(controls_.front());controls_.pop_front();cv_.notify_all();}
             else {event=std::move(*latestSnapshot_);latestSnapshot_.reset();}}
            if(event.name=="MeterSnapshot"){
                constexpr std::string_view marker="\"coalescedSnapshotCount\":0";
                const auto at=event.payload.find(marker);
                if(at==std::string::npos){std::lock_guard lock(mutex_);failed_=true;cv_.notify_all();return;}
                event.payload.replace(at,marker.size(),"\"coalescedSnapshotCount\":"+std::to_string(event.coalesced));
            }
            if(!writer_.Event(event.correlation,event.session,event.name,event.payload)){
                std::lock_guard lock(mutex_);failed_=true;controls_.clear();latestSnapshot_.reset();cv_.notify_all();return;
            }
        }
    }
    Writer& writer_;std::mutex mutex_;std::condition_variable cv_;std::deque<Event> controls_;std::optional<Event> latestSnapshot_;bool stopping_{},failed_{};std::thread thread_;
};

struct SessionSettings {
    std::string endpointId,direction,formatId,encoding,topologyId,period,mmcss,scenario;
    int sampleRate{},channelCount{},durationSeconds{}; std::optional<int> renderChannel;
    std::string Canonical() const { return endpointId+"|"+direction+"|"+formatId+"|"+std::to_string(sampleRate)+"|"+encoding+"|"+std::to_string(channelCount)+"|"+topologyId+"|"+std::to_string(durationSeconds)+"|"+period+"|"+mmcss+"|"+(renderChannel?std::to_string(*renderChannel):"null")+"|"+scenario; }
    std::string Json() const { return "{\"endpointId\":\""+endpointId+"\",\"direction\":\""+direction+"\",\"formatId\":\""+formatId+"\",\"sampleRate\":"+std::to_string(sampleRate)+",\"encoding\":\""+encoding+"\",\"channelCount\":"+std::to_string(channelCount)+",\"topologyId\":\""+topologyId+"\",\"durationSeconds\":"+std::to_string(durationSeconds)+",\"period\":\""+period+"\",\"mmcss\":\""+mmcss+"\",\"renderChannel\":"+(renderChannel?std::to_string(*renderChannel):"null")+",\"scenario\":\""+scenario+"\"}"; }
};
struct ActiveSession { std::string id,correlation; SessionSettings settings; std::mutex mutex; std::condition_variable cv; bool stop{},stopping{},terminal{}; std::uint64_t snapshotSequence{}; std::vector<double> peakHold; std::thread thread; };

std::string Sha256(std::string_view data) {
    BCRYPT_ALG_HANDLE alg{}; BCRYPT_HASH_HANDLE hash{}; DWORD objectBytes{}, hashBytes{}, got{};
    if(BCryptOpenAlgorithmProvider(&alg,BCRYPT_SHA256_ALGORITHM,nullptr,0)<0)throw ProtocolFailure("SHA256 provider unavailable");
    if(BCryptGetProperty(alg,BCRYPT_OBJECT_LENGTH,reinterpret_cast<PUCHAR>(&objectBytes),sizeof(objectBytes),&got,0)<0||BCryptGetProperty(alg,BCRYPT_HASH_LENGTH,reinterpret_cast<PUCHAR>(&hashBytes),sizeof(hashBytes),&got,0)<0){BCryptCloseAlgorithmProvider(alg,0);throw ProtocolFailure("SHA256 geometry unavailable");}
    std::vector<UCHAR> object(objectBytes), digest(hashBytes);
    NTSTATUS status=BCryptCreateHash(alg,&hash,object.data(),objectBytes,nullptr,0,0);
    if(status>=0)status=BCryptHashData(hash,reinterpret_cast<PUCHAR>(const_cast<char*>(data.data())),static_cast<ULONG>(data.size()),0);
    if(status>=0)status=BCryptFinishHash(hash,digest.data(),hashBytes,0);
    if(hash)BCryptDestroyHash(hash);BCryptCloseAlgorithmProvider(alg,0);if(status<0)throw ProtocolFailure("SHA256 operation failed");
    constexpr char hex[]="0123456789abcdef";std::string out;out.reserve(digest.size()*2);for(auto b:digest){out.push_back(hex[b>>4]);out.push_back(hex[b&15]);}return out;
}

class OperationService {
public:
    virtual ~OperationService()=default;
    virtual std::shared_ptr<ActiveSession> Accept(const rapidjson::Value& payload,std::string correlation)=0;
    virtual bool Start(const std::shared_ptr<ActiveSession>& session)=0;
    virtual std::string Stop(std::string_view id)=0;
    virtual bool Busy() const=0;
    virtual void Join()=0;
};

class FakeOperationService final : public OperationService {
public:
    explicit FakeOperationService(Writer& writer):writer_(writer),publisher_(writer){}
    std::shared_ptr<ActiveSession> Accept(const rapidjson::Value& payload,std::string correlation) override {
        ReapRetired();
        const auto& settingsValue=payload; SessionSettings s; std::string confirmation,fingerprint; bool accepted=false;
        if(!ReadString(settingsValue,"endpointId",s.endpointId)||!ReadString(settingsValue,"direction",s.direction)||!ReadString(settingsValue,"formatId",s.formatId)||!ReadString(settingsValue,"encoding",s.encoding)||!ReadString(settingsValue,"topologyId",s.topologyId)||!ReadString(settingsValue,"period",s.period)||!ReadString(settingsValue,"mmcss",s.mmcss)||!ReadString(settingsValue,"scenario",s.scenario)||!ReadInt(settingsValue,"sampleRate",s.sampleRate)||!ReadInt(settingsValue,"channelCount",s.channelCount)||!ReadInt(settingsValue,"durationSeconds",s.durationSeconds))throw Rejection("INVALID_ARGUMENT","session settings are incomplete");
        auto ch=settingsValue.FindMember("renderChannel");if(ch==settingsValue.MemberEnd())throw Rejection("INVALID_ARGUMENT","renderChannel field is required");if(ch->value.IsInt())s.renderChannel=ch->value.GetInt();else if(!ch->value.IsNull())throw Rejection("INVALID_ARGUMENT","renderChannel must be integer or null");
        auto receipt=settingsValue.FindMember("consentReceipt");if(receipt==settingsValue.MemberEnd()||!receipt->value.IsObject())throw Rejection("CONSENT_REQUIRED","fresh consent receipt is required");
        auto acceptedIt=receipt->value.FindMember("accepted");if(acceptedIt!=receipt->value.MemberEnd()&&acceptedIt->value.IsBool())accepted=acceptedIt->value.GetBool();
        if(!accepted||!ReadString(receipt->value,"confirmationId",confirmation)||!ReadString(receipt->value,"requestFingerprint",fingerprint))throw Rejection("CONSENT_REQUIRED","consent receipt is missing or not accepted");
        confirmation=GuidString(confirmation);
        if(!ValidSettings(s))throw Rejection("INVALID_SETTINGS","settings do not match a supported synthetic stream format");
        if(Sha256(s.Canonical())!=fingerprint)throw Rejection("FINGERPRINT_MISMATCH","consent fingerprint does not match immutable settings");
        std::lock_guard lock(mutex_);
        if(consumed_.contains(confirmation))throw Rejection("CONSENT_REPLAYED","consent receipt was already consumed");
        if(blocked_)throw Rejection("CLEANUP_UNKNOWN","previous fake operation has unknown cleanup; worker cannot be reused");
        if(active_)throw Rejection("BUSY","one operation is already active");
        consumed_.insert(confirmation);
        auto session=std::make_shared<ActiveSession>();session->id=NewGuid();session->correlation=std::move(correlation);session->settings=std::move(s);active_=session;return session;
    }
    bool Start(const std::shared_ptr<ActiveSession>& session) override {
        try { Emit(session,"Preparing","PREPARING","UNKNOWN","NOT_ATTEMPTED");session->thread=std::thread([this,session]{Run(session);});return true; }
        catch (...) { Emit(session,"Failed","FAILED","FAILED","UNKNOWN",",\"failureCode\":\"FAKE_SERVICE_START_FAILED\"");MarkTerminal(session,true);return false; }
    }
    std::string Stop(std::string_view id) override {
        std::lock_guard lock(mutex_);if(!active_||active_->id!=id)return "NO_SESSION";
        std::lock_guard sessionLock(active_->mutex);if(active_->stopping||active_->terminal)return "ALREADY_STOPPING";active_->stopping=true;active_->stop=true;active_->cv.notify_all();return "ACCEPTED";
    }
    bool Busy() const override { std::lock_guard lock(mutex_);return static_cast<bool>(active_); }
    void Join() override { std::vector<std::shared_ptr<ActiveSession>> sessions;{std::lock_guard lock(mutex_);if(active_)sessions.push_back(active_);sessions.insert(sessions.end(),retired_.begin(),retired_.end());retired_.clear();}for(auto&s:sessions)if(s->thread.joinable())s->thread.join();publisher_.Stop(); }
private:
    void ReapRetired() { std::vector<std::shared_ptr<ActiveSession>> sessions;{std::lock_guard lock(mutex_);sessions.swap(retired_);}for(auto&s:sessions)if(s->thread.joinable())s->thread.join(); }
    static bool ValidSettings(const SessionSettings& s) {
        if(s.sampleRate!=48000||s.topologyId!=s.formatId||s.period!="DEFAULT"&&s.period!="MINIMUM"||s.mmcss!="OFF"&&s.mmcss!="PRO_AUDIO"||s.channelCount<1||s.channelCount>64)return false;
        if((s.formatId.starts_with("PCM16-48000")&&s.encoding=="PCM16")||(s.formatId.starts_with("PCM24-48000")&&s.encoding=="PCM24_IN_32")||(s.formatId.starts_with("FLOAT32-48000")&&s.encoding=="FLOAT32")){}
        else return false;
        int expected=0;
        if(s.endpointId=="fake-capture-2"&&s.direction=="CAPTURE")expected=s.formatId=="PCM16-48000"?2:s.formatId=="FLOAT32-48000"?1:0;
        else if(s.endpointId=="fake-capture-64"&&s.direction=="CAPTURE")expected=s.formatId=="PCM16-48000"?64:s.formatId=="PCM24-48000"?48:s.formatId=="FLOAT32-48000"?32:s.formatId=="FLOAT32-48000-16"?16:s.formatId=="PCM24-48000-8"?8:s.formatId=="PCM16-48000-1"?1:0;
        else if(s.endpointId=="fake-render-8"&&s.direction=="RENDER")expected=s.formatId=="PCM16-48000"?8:s.formatId=="FLOAT32-48000"?2:s.formatId=="PCM16-48000-1"?1:0;
        else if(s.endpointId=="fake-render-64"&&s.direction=="RENDER")expected=s.formatId=="PCM16-48000"?64:s.formatId=="PCM24-48000"?48:s.formatId=="FLOAT32-48000"?32:s.formatId=="FLOAT32-48000-16"?16:s.formatId=="PCM24-48000-8"?8:s.formatId=="PCM16-48000-1"?1:0;
        if(expected==0||s.channelCount!=expected||s.durationSeconds<(s.direction=="CAPTURE"?1:1)||s.durationSeconds>(s.direction=="CAPTURE"?30:10))return false;
        if(s.direction=="CAPTURE"&&s.renderChannel)return false;
        if(s.direction=="RENDER"&&(!s.renderChannel||*s.renderChannel<1||*s.renderChannel>s.channelCount))return false;
        return s.scenario=="NORMAL"||s.scenario=="WARNING"||s.scenario=="FAILURE"||s.scenario=="DEVICE_LOSS"||s.scenario=="ALL_SILENT"||s.scenario=="NO_WINDOW";
    }
    bool WaitOrStop(const std::shared_ptr<ActiveSession>& s,std::chrono::milliseconds duration) { std::unique_lock lock(s->mutex);return s->cv.wait_for(lock,duration,[&]{return s->stop;}); }
    void Emit(const std::shared_ptr<ActiveSession>& s,std::string_view name,std::string_view lifecycle,std::string_view quality,std::string_view cleanup,std::string_view extra="") {
        const auto payload="{\"synthetic\":true,\"lifecycle\":\""+std::string(lifecycle)+"\",\"streamQuality\":\""+std::string(quality)+"\",\"cleanupEvidence\":\""+std::string(cleanup)+"\",\"physicalMapping\":\"UNKNOWN\",\"settings\":"+s->settings.Json()+std::string(extra)+"}";
        if(!publisher_.Control(s->correlation,s->id,std::string(name),payload))throw ProtocolFailure("control event publication failed");
    }
    void EmitSnapshot(const std::shared_ptr<ActiveSession>& s, std::optional<std::int64_t> window) {
        const auto sequence=++s->snapshotSequence;
        const auto windowIndex=window.value_or(1);
        const bool noWindow=!window.has_value();
        std::string channels="[";
        for(int i=1;i<=s->settings.channelCount;++i){
            if(i>1)channels+=",";
            if(s->settings.direction=="CAPTURE"){
                const bool silent=(windowIndex%3)==2 || s->settings.scenario=="ALL_SILENT";
                const double peak=-24.0-(i%7)*2.0;
                const double rms=peak-6.0;
                if(!silent)s->peakHold[static_cast<std::size_t>(i-1)]=std::max(s->peakHold[static_cast<std::size_t>(i-1)],peak);
                channels+="{\"index\":"+std::to_string(i)+",\"windowPeakDbfs\":"+(noWindow||silent?"null":std::to_string(peak))+",\"windowRmsDbfs\":"+(noWindow||silent?"null":std::to_string(rms))+",\"silent\":"+(noWindow?"null":silent?"true":"false")+",\"sessionPeakHoldDbfs\":"+(noWindow||s->peakHold[static_cast<std::size_t>(i-1)]<-200?"null":std::to_string(s->peakHold[static_cast<std::size_t>(i-1)]))+",\"clipCount\":"+(noWindow?"null":"0")+"}";
            } else {
                const bool selected=i==s->settings.renderChannel.value_or(0);
                const double peak=-36.0, rms=-42.0;
                if(selected&&!noWindow)s->peakHold[static_cast<std::size_t>(i-1)]=std::max(s->peakHold[static_cast<std::size_t>(i-1)],peak);
                channels+="{\"index\":"+std::to_string(i)+",\"intendedPeakDbfs\":"+(selected&&!noWindow?std::to_string(peak):"null")+",\"intendedRmsDbfs\":"+(selected&&!noWindow?std::to_string(rms):"null")+",\"digitalSilent\":"+(noWindow?"null":selected?"false":"true")+",\"sessionPeakHoldDbfs\":"+(selected&&!noWindow?std::to_string(s->peakHold[static_cast<std::size_t>(i-1)]):"null")+",\"clipCount\":"+(noWindow?"null":"0")+"}";
            }
        }
        channels+="]";
        const auto common=",\"snapshotSequence\":"+std::to_string(sequence)+",\"completedWindowIndex\":"+(window?std::to_string(*window):"null")+",\"windowDurationMs\":100,\"channelCount\":"+std::to_string(s->settings.channelCount)+",\"channels\":"+channels+",\"coalescedSnapshotCount\":0";
        const auto extra=s->settings.direction=="RENDER"?",\"selectedChannelIndex\":"+std::to_string(s->settings.renderChannel.value_or(0))+",\"intendedOutputModel\":\"SYNTHETIC_INTENDED_OUTPUT_NOT_MEASURED\"":"";
        if(!publisher_.Snapshot(s->correlation,s->id,"{\"synthetic\":true,\"lifecycle\":\"STARTED\",\"streamQuality\":\"UNKNOWN\",\"cleanupEvidence\":\"NOT_ATTEMPTED\""+common+extra+"}"))throw ProtocolFailure("snapshot publication failed");
    }
    void MarkTerminal(const std::shared_ptr<ActiveSession>& s,bool cleanupUnknown) {
        {std::lock_guard l(s->mutex);s->terminal=true;}
        {std::lock_guard l(mutex_);if(active_==s){if(cleanupUnknown)blocked_=true;active_.reset();retired_.push_back(s);}}
    }
    void Run(const std::shared_ptr<ActiveSession>& s) {
        try {
            if(WaitOrStop(s,std::chrono::milliseconds(100))){Emit(s,"Stopping","STOPPING","UNKNOWN","SYNTHETIC_CLEANUP_PENDING");std::this_thread::sleep_for(std::chrono::milliseconds(100));Emit(s,"Completed","COMPLETED","UNKNOWN","SYNTHETIC_CLEANUP_COMPLETED");MarkTerminal(s,false);return;}
            Emit(s,"Ready","READY","UNKNOWN","SYNTHETIC_READY");
            if(WaitOrStop(s,std::chrono::milliseconds(100))){Emit(s,"Stopping","STOPPING","UNKNOWN","SYNTHETIC_CLEANUP_PENDING");std::this_thread::sleep_for(std::chrono::milliseconds(100));Emit(s,"Completed","COMPLETED","UNKNOWN","SYNTHETIC_CLEANUP_COMPLETED");MarkTerminal(s,false);return;}
            Emit(s,"Started","STARTED","UNKNOWN","SYNTHETIC_STARTED");
            s->peakHold.assign(static_cast<std::size_t>(s->settings.channelCount),-201.0);
            EmitSnapshot(s,s->settings.scenario=="NO_WINDOW"?std::nullopt:std::optional<std::int64_t>(1));
            if(s->settings.scenario=="FAILURE"){if(WaitOrStop(s,std::chrono::milliseconds(50))){Emit(s,"Stopping","STOPPING","UNKNOWN","SYNTHETIC_CLEANUP_PENDING");std::this_thread::sleep_for(std::chrono::milliseconds(100));Emit(s,"Completed","COMPLETED","UNKNOWN","SYNTHETIC_CLEANUP_COMPLETED");MarkTerminal(s,false);return;}Emit(s,"Failed","FAILED","FAILED","SYNTHETIC_CLEANUP_COMPLETED",",\"failureCode\":\"SIMULATED_FAILURE\"");MarkTerminal(s,false);return;}
            if(s->settings.scenario=="DEVICE_LOSS"){if(WaitOrStop(s,std::chrono::milliseconds(50))){Emit(s,"Stopping","STOPPING","UNKNOWN","SYNTHETIC_CLEANUP_PENDING");std::this_thread::sleep_for(std::chrono::milliseconds(100));Emit(s,"Completed","COMPLETED","UNKNOWN","SYNTHETIC_CLEANUP_COMPLETED");MarkTerminal(s,false);return;}Emit(s,"Failed","FAILED","UNKNOWN","UNKNOWN",",\"failureCode\":\"SIMULATED_DEVICE_LOSS\"");MarkTerminal(s,true);return;}
            if(s->settings.scenario=="WARNING")Emit(s,"Warning","STARTED","WARNING","SYNTHETIC_ACTIVE",",\"warningCode\":\"SIMULATED_WARNING\" ");
            const auto windows=std::max(1,s->settings.durationSeconds*10);
            for(int w=1;w<windows;++w){if(WaitOrStop(s,std::chrono::milliseconds(100))){Emit(s,"Stopping","STOPPING",s->settings.scenario=="WARNING"?"WARNING":"UNKNOWN","SYNTHETIC_CLEANUP_PENDING");std::this_thread::sleep_for(std::chrono::milliseconds(100));Emit(s,"Completed","COMPLETED",s->settings.scenario=="WARNING"?"WARNING":"UNKNOWN","SYNTHETIC_CLEANUP_COMPLETED");MarkTerminal(s,false);return;}EmitSnapshot(s,s->settings.scenario=="NO_WINDOW"?std::nullopt:std::optional<std::int64_t>(w+1));}
            if(WaitOrStop(s,std::chrono::milliseconds(100))){Emit(s,"Stopping","STOPPING",s->settings.scenario=="WARNING"?"WARNING":"UNKNOWN","SYNTHETIC_CLEANUP_PENDING");std::this_thread::sleep_for(std::chrono::milliseconds(100));}
            Emit(s,"Completed","COMPLETED",s->settings.scenario=="WARNING"?"WARNING":"UNKNOWN","SYNTHETIC_CLEANUP_COMPLETED");MarkTerminal(s,false);
        } catch (...) { try{Emit(s,"Failed","FAILED","UNKNOWN","UNKNOWN",",\"failureCode\":\"FAKE_SERVICE_EXCEPTION\"");}catch(...){}MarkTerminal(s,true); }
    }
    Writer& writer_; EventPublisher publisher_; mutable std::mutex mutex_; std::shared_ptr<ActiveSession> active_; std::vector<std::shared_ptr<ActiveSession>> retired_; std::unordered_set<std::string> consumed_; bool blocked_{};
};

std::string Endpoint(std::string_view id,std::string_view name,std::string_view direction,int channels,std::string_view formats) {
    return "{\"endpointId\":\""+std::string(id)+"\",\"name\":\""+std::string(name)+"\",\"direction\":\""+std::string(direction)+"\",\"state\":\"SYNTHETIC\",\"channelCount\":"+std::to_string(channels)+",\"topology\":{\"channelCount\":"+std::to_string(channels)+",\"channels\":"+std::string(formats)+",\"groups\":null,\"speakerMask\":null},\"isSynthetic\":true,\"formatTopologies\":";
}
std::string Channels(int count) { std::string out="[";for(int i=1;i<=count;++i){if(i>1)out+=",";out+="{\"index\":"+std::to_string(i)+"}";}return out+"]"; }
std::string FormatSet(std::initializer_list<std::pair<std::string_view,int>> formats) { std::string out="[";bool first=true;for(auto [id,n]:formats){if(!first)out+=",";first=false;out+="{\"formatId\":\""+std::string(id)+"\",\"topology\":{\"channelCount\":"+std::to_string(n)+",\"channels\":"+Channels(n)+",\"groups\":null,\"speakerMask\":null}}";}return out+"]"; }

std::wstring Wide(std::string_view utf8) {
    const int count=MultiByteToWideChar(CP_UTF8,MB_ERR_INVALID_CHARS,utf8.data(),static_cast<int>(utf8.size()),nullptr,0);
    if(count<=0)throw Rejection("INVALID_ENDPOINT_ID","Endpoint ID is not valid UTF-8.");
    std::wstring result(static_cast<std::size_t>(count),L'\0');
    if(MultiByteToWideChar(CP_UTF8,MB_ERR_INVALID_CHARS,utf8.data(),static_cast<int>(utf8.size()),result.data(),count)!=count)
        throw Rejection("INVALID_ENDPOINT_ID","Endpoint ID is not valid UTF-8.");
    return result;
}

std::string MatrixTimestamp() {
    SYSTEMTIME t{};GetSystemTime(&t);char text[32]{};
    std::snprintf(text,sizeof(text),"%04u-%02u-%02uT%02u:%02u:%02u.%03uZ",t.wYear,t.wMonth,t.wDay,t.wHour,t.wMinute,t.wSecond,t.wMilliseconds);
    return text;
}

std::string SerializeRealMatrix(std::string_view endpointId,std::string_view direction,const ExclusiveFormatMatrix::Report& report) {
    const auto json=ExclusiveFormatMatrix::ToJson(report,MatrixTimestamp(),"worker","local-only");
    const auto start=json.find("\"candidates\": [");
    if(start==std::string::npos)return "{}";
    const auto provenance=
#if defined(PROBE_TEST_WASAPI_BACKEND)
        "TEST_FAKE_FORMAT_BACKEND";
#else
        report.level==ExclusiveFormatMatrix::Level::PassiveQuery?"REAL_WASAPI_QUERY":"REAL_WASAPI_INITIALIZE_ONLY";
#endif
    return "{\"provenance\":"+ExclusiveFormatMatrix::JsonString(provenance)+",\"timestampUtc\":"+ExclusiveFormatMatrix::JsonString(MatrixTimestamp())+
        +",\"endpointIdentityReference\":"+ExclusiveFormatMatrix::JsonString(std::string(endpointId))+
        +",\"direction\":"+ExclusiveFormatMatrix::JsonString(std::string(direction))+
        +",\"reportLevel\":"+ExclusiveFormatMatrix::JsonString(ExclusiveFormatMatrix::LevelText(report.level))+
        +",\"candidateReport\":"+json.substr(0,json.size()-2)+"}}";
}

#if defined(PROBE_TEST_WASAPI_BACKEND)
class InjectedWasapiApi final : public ExclusiveFormatMatrix::Api {
public:
    ExclusiveFormatMatrix::QueryResult QuerySupport(const ExclusiveFormatMatrix::Format& f) override {
        ExclusiveFormatMatrix::QueryResult q{0,0,ExclusiveFormatMatrix::Cleanup::ReleaseAttempted,1};
        q.devicePeriodHr=0;q.defaultDevicePeriod100ns=100'000;q.minimumDevicePeriod100ns=30'000;
        if(f.sampleRate==44'100&&f.channels==1&&f.encoding==ExclusiveFormatMatrix::Encoding::Pcm24In32)q.supportHr=ExclusiveFormatMatrix::kUnsupportedFormat;
        if(f.sampleRate==44'100&&f.channels==1&&f.encoding==ExclusiveFormatMatrix::Encoding::Float32){q.activationHr=static_cast<std::int32_t>(0x80004005U);q.supportHr.reset();q.cleanup=ExclusiveFormatMatrix::Cleanup::NotAcquired;q.clientReleaseAttempts=0;}
        if(f.sampleRate==48'000&&f.channels==2&&f.encoding==ExclusiveFormatMatrix::Encoding::Pcm24In32)q.supportHr=static_cast<std::int32_t>(0x8889000AU);
        return q;
    }
    ExclusiveFormatMatrix::Attempt InitializeOnly(const ExclusiveFormatMatrix::Format& f,ExclusiveFormatMatrix::Period p) override {
        using namespace ExclusiveFormatMatrix;Attempt a;
        a.activationHr=0;a.periodQueryHr=0;a.periodAvailable=true;a.requestedPeriod100ns=p==Period::Default?100'000:30'000;
        a.initializeAttempted=true;a.firstInitializeHr=0;a.finalInitializeHr=0;a.actualBufferSizeHr=0;
        a.actualBufferFrames=p==Period::Default?480U:144U;a.actualPeriod100ns=p==Period::Default?100'000:30'000;
        a.eventCreateHr=0;a.setEventHandleHr=0;a.eventHandleCloseHr=0;a.cleanup=Cleanup::ReleaseAttempted;a.clientReleaseAttempts=1;
        if(f.channels==48){a.firstInitializeHr=static_cast<std::int32_t>(0x8889000AU);a.finalInitializeHr=a.firstInitializeHr;a.actualBufferSizeHr.reset();a.actualBufferFrames.reset();a.actualPeriod100ns.reset();a.eventCreateHr.reset();a.setEventHandleHr.reset();a.eventHandleCloseHr.reset();}
        if(f.channels==32){a.firstInitializeHr=kBufferSizeNotAligned;a.alignmentGetBufferSizeHr=0;a.alignmentFrames=256;a.alignmentActivateHr=0;a.alignmentPeriod100ns=53'333;a.alignmentInitializeHr=0;a.finalInitializeHr=0;a.actualBufferFrames=256;a.actualPeriod100ns=53'333;a.clientReleaseAttempts=2;}
        if(f.channels==16)a.setEventHandleHr=static_cast<std::int32_t>(0x80004005U);
        if(f.channels==8){a.eventHandleCloseHr=static_cast<std::int32_t>(0x80004005U);a.cleanup=Cleanup::Failed;}
        return a;
    }
    ExclusiveFormatMatrix::Attempt InitializeOnly(const ExclusiveFormatMatrix::Format& f,const ExclusiveFormatMatrix::PeriodRequest& request) override {
        if(!request.IsCustom()) return InitializeOnly(f,request.mode);
        auto result=InitializeOnly(f,ExclusiveFormatMatrix::Period::Default);
        result.requestedPeriod100ns=request.requestedPeriod100ns;
        result.requestedBufferFrames=request.requestedFrames;
        result.actualPeriod100ns=request.requestedPeriod100ns;
        result.actualBufferFrames=request.requestedFrames;
        return result;
    }
};
#endif

std::string RevalidateEndpoint(std::string_view endpointId,std::string_view direction,IMMDevice** outDevice) {
    *outDevice=nullptr;
    const auto id=Wide(endpointId);
    IMMDeviceEnumerator* enumerator=nullptr;
    HRESULT hr=CoCreateInstance(__uuidof(MMDeviceEnumerator),nullptr,CLSCTX_ALL,__uuidof(IMMDeviceEnumerator),reinterpret_cast<void**>(&enumerator));
    if(FAILED(hr)||!enumerator){if(enumerator)enumerator->Release();return "ENUMERATOR_ACTIVATION_FAILED";}
    hr=enumerator->GetDevice(id.c_str(),outDevice);
    enumerator->Release();
    if(FAILED(hr)||!*outDevice)return "ENDPOINT_DISAPPEARED";
    DWORD state{};hr=(*outDevice)->GetState(&state);
    if(FAILED(hr)||state!=DEVICE_STATE_ACTIVE){(*outDevice)->Release();*outDevice=nullptr;return "ENDPOINT_NOT_ACTIVE";}
    IMMEndpoint* endpoint=nullptr;EDataFlow flow=eAll;
    hr=(*outDevice)->QueryInterface(__uuidof(IMMEndpoint),reinterpret_cast<void**>(&endpoint));
    if(SUCCEEDED(hr)&&endpoint)hr=endpoint->GetDataFlow(&flow);
    if(endpoint)endpoint->Release();
    if(FAILED(hr)||(direction=="CAPTURE"?flow!=eCapture:flow!=eRender)){(*outDevice)->Release();*outDevice=nullptr;return "ENDPOINT_DIRECTION_MISMATCH";}
    return {};
}

std::string JsonString(std::string_view value) {
    rapidjson::StringBuffer buffer;
    rapidjson::Writer<rapidjson::StringBuffer> writer(buffer);
    writer.String(value.data(), static_cast<rapidjson::SizeType>(value.size()));
    return {buffer.GetString(), buffer.GetSize()};
}

std::wstring QuoteArgument(std::wstring_view arg) {
    std::wstring out=L"\""; std::size_t slashes=0;
    for (const wchar_t c : arg) {
        if (c==L'\\') { ++slashes; continue; }
        if (c==L'\"') { out.append(slashes*2+1,L'\\'); out.push_back(c); slashes=0; continue; }
        out.append(slashes,L'\\'); slashes=0; out.push_back(c);
    }
    out.append(slashes*2,L'\\'); out.push_back(L'\"'); return out;
}

std::optional<std::string> RunStreamCli(std::string_view endpointId, std::string_view direction,
        std::string_view period, unsigned duration, std::string_view mmcss, HANDLE cancelEvent,
        std::string& failureCode, std::string& failureField) {
    std::wstring module(32768,L'\0');
    const DWORD moduleLength=GetModuleFileNameW(nullptr,module.data(),static_cast<DWORD>(module.size()));
    if(moduleLength==0||moduleLength>=module.size()){failureCode="WORKER_PATH_UNAVAILABLE";return std::nullopt;}
    module.resize(moduleLength);
    const auto slash=module.find_last_of(L"\\/");
    if(slash==std::wstring::npos){failureCode="WORKER_PATH_INVALID";return std::nullopt;}
    const std::wstring cliPath=module.substr(0,slash+1)+L"KlidanAudioProbe.exe";
    if(GetFileAttributesW(cliPath.c_str())==INVALID_FILE_ATTRIBUTES){failureCode="BUNDLED_PROBE_MISSING";return std::nullopt;}
    wchar_t tempDirectory[MAX_PATH+1]{};const DWORD tempLength=GetTempPathW(MAX_PATH,tempDirectory);
    if(tempLength==0||tempLength>MAX_PATH){failureCode="TEMP_PATH_UNAVAILABLE";return std::nullopt;}
    const auto id=Wide(NewGuid());
    const std::wstring reportPath=std::wstring(tempDirectory)+L"KlidanStream-"+id+L".json";
    struct TempFile { const std::wstring& path; ~TempFile(){DeleteFileW(path.c_str());} } cleanup{reportPath};
    if(!SetHandleInformation(cancelEvent,HANDLE_FLAG_INHERIT,HANDLE_FLAG_INHERIT)){failureCode="CANCEL_EVENT_INHERIT_FAILED";return std::nullopt;}
    const auto endpointWide=Wide(endpointId);
    std::wstring command=QuoteArgument(cliPath)+L" "+(direction=="CAPTURE"?L"--capture-id ":L"--render-id ")+QuoteArgument(endpointWide)+
        L" --test-stream --rate 48000 --channels 2 --format pcm16 --period "+std::wstring(period=="MINIMUM"?L"minimum":L"default")+
        L" --duration "+std::to_wstring(duration)+L" --mmcss "+std::wstring(mmcss=="PRO_AUDIO"?L"pro-audio":L"off")+
        L" --output-json "+QuoteArgument(reportPath)+L" --cancel-event-handle "+std::to_wstring(reinterpret_cast<std::uintptr_t>(cancelEvent));
    SIZE_T attributesSize=0;InitializeProcThreadAttributeList(nullptr,1,0,&attributesSize);
    std::vector<std::uint8_t> attributesBuffer(attributesSize);
    auto* attributes=reinterpret_cast<PPROC_THREAD_ATTRIBUTE_LIST>(attributesBuffer.data());
    if(!InitializeProcThreadAttributeList(attributes,1,0,&attributesSize)){failureCode="PROCESS_ATTRIBUTE_LIST_FAILED";return std::nullopt;}
    HANDLE inherited[]{cancelEvent};
    const BOOL attributeReady=UpdateProcThreadAttribute(attributes,0,PROC_THREAD_ATTRIBUTE_HANDLE_LIST,inherited,sizeof(inherited),nullptr,nullptr);
    if(!attributeReady){DeleteProcThreadAttributeList(attributes);failureCode="PROCESS_HANDLE_LIST_FAILED";return std::nullopt;}
    STARTUPINFOEXW startup{};startup.StartupInfo.cb=sizeof(startup);startup.lpAttributeList=attributes;
    PROCESS_INFORMATION process{};
    const BOOL created=CreateProcessW(cliPath.c_str(),command.data(),nullptr,nullptr,TRUE,
        EXTENDED_STARTUPINFO_PRESENT|CREATE_NO_WINDOW,nullptr,nullptr,&startup.StartupInfo,&process);
    DeleteProcThreadAttributeList(attributes);
    if(!created){failureCode="STREAM_PROCESS_START_FAILED";return std::nullopt;}
    const DWORD wait=WaitForSingleObject(process.hProcess,(duration+25)*1000);
    if(wait!=WAIT_OBJECT_0){
        SetEvent(cancelEvent);
        const DWORD graceful=WaitForSingleObject(process.hProcess,3000);
        if(graceful!=WAIT_OBJECT_0)TerminateProcess(process.hProcess,ERROR_TIMEOUT);
        WaitForSingleObject(process.hProcess,3000);
        CloseHandle(process.hThread);CloseHandle(process.hProcess);
        failureCode=graceful==WAIT_OBJECT_0?"STREAM_PROCESS_TIMEOUT":"STREAM_PROCESS_TERMINATED_OUTCOME_UNKNOWN";
        return std::nullopt;
    }
    DWORD exitCode{};GetExitCodeProcess(process.hProcess,&exitCode);
    CloseHandle(process.hThread);CloseHandle(process.hProcess);
    std::ifstream input(std::filesystem::path(reportPath),std::ios::binary|std::ios::ate);
    if(!input){failureCode="STREAM_REPORT_MISSING";return std::nullopt;}
    const auto length=input.tellg();
    if(length<=0||length>65536){failureCode="STREAM_REPORT_INVALID_SIZE";return std::nullopt;}
    std::string report(static_cast<std::size_t>(length),'\0');input.seekg(0);input.read(report.data(),length);
    std::vector<std::uint8_t> bytes(report.begin(),report.end());
    auto parsed=ParseObject(bytes);
    std::string_view invalidField;
    if(!StreamReportContract::Validate(parsed,direction,period,duration,&invalidField)){failureCode="STREAM_REPORT_CONTRACT_INVALID";failureField.assign(invalidField);return std::nullopt;}
    if(exitCode!=0&&exitCode!=4&&exitCode!=5&&exitCode!=6&&exitCode!=7&&exitCode!=8){failureCode="STREAM_PROCESS_FAILED";return std::nullopt;}
    return report;
}

std::optional<std::string> RunDuplexCli(std::string_view operationId, std::string_view captureId,
        std::string_view renderId, std::string_view period, unsigned duration,
        std::string_view mmcss, HANDLE cancelEvent, std::string& failureCode, std::string& failureDetail, bool sustained = false) {
    std::wstring module(32768, L'\0');
    const DWORD moduleLength = GetModuleFileNameW(nullptr, module.data(), static_cast<DWORD>(module.size()));
    if (moduleLength == 0 || moduleLength >= module.size()) { failureCode = "WORKER_PATH_UNAVAILABLE"; return std::nullopt; }
    module.resize(moduleLength);
    const auto slash = module.find_last_of(L"\\/");
    if (slash == std::wstring::npos) { failureCode = "WORKER_PATH_INVALID"; return std::nullopt; }
    const std::wstring cliPath = module.substr(0, slash + 1) + L"KlidanAudioProbe.exe";
    if (GetFileAttributesW(cliPath.c_str()) == INVALID_FILE_ATTRIBUTES) { failureCode = "BUNDLED_PROBE_MISSING"; return std::nullopt; }
    wchar_t tempDirectory[MAX_PATH + 1]{};
    const DWORD tempLength = GetTempPathW(MAX_PATH, tempDirectory);
    if (tempLength == 0 || tempLength > MAX_PATH) { failureCode = "TEMP_PATH_UNAVAILABLE"; return std::nullopt; }
    const std::wstring reportPath = std::wstring(tempDirectory) + L"KlidanDuplex-" + Wide(operationId) + L".json";
    struct TempFile { const std::wstring& path; ~TempFile() { DeleteFileW(path.c_str()); } } cleanup{reportPath};
    if (!SetHandleInformation(cancelEvent, HANDLE_FLAG_INHERIT, HANDLE_FLAG_INHERIT)) { failureCode = "CANCEL_EVENT_INHERIT_FAILED"; return std::nullopt; }
    const auto captureWide = Wide(captureId), renderWide = Wide(renderId), operationWide = Wide(operationId);
    const std::wstring cliPeriod = period == "MINIMUM" ? L"minimum" : period == "DEFAULT" ? L"default" : L"custom:" + Wide(period.substr(7));
    std::wstring command = QuoteArgument(cliPath) + L" --capture-id " + QuoteArgument(captureWide) +
        L" --render-id " + QuoteArgument(renderWide) + (sustained ? L" --test-sustained-duplex" : L" --test-duplex") + L" --rate 48000 --channels 2 --format pcm16" +
        L" --period " + cliPeriod +
        L" --duration " + std::to_wstring(duration) + L" --mmcss " +
        std::wstring(mmcss == "PRO_AUDIO" ? L"pro-audio" : L"off") + L" --output-json " + QuoteArgument(reportPath) +
        L" --operation-id " + QuoteArgument(operationWide) + L" --cancel-event-handle " +
        std::to_wstring(reinterpret_cast<std::uintptr_t>(cancelEvent));
    SIZE_T attributesSize = 0;
    InitializeProcThreadAttributeList(nullptr, 1, 0, &attributesSize);
    std::vector<std::uint8_t> attributesBuffer(attributesSize);
    auto* attributes = reinterpret_cast<PPROC_THREAD_ATTRIBUTE_LIST>(attributesBuffer.data());
    if (!InitializeProcThreadAttributeList(attributes, 1, 0, &attributesSize)) { failureCode = "PROCESS_ATTRIBUTE_LIST_FAILED"; return std::nullopt; }
    HANDLE inherited[]{cancelEvent};
    const BOOL attributeReady = UpdateProcThreadAttribute(attributes, 0, PROC_THREAD_ATTRIBUTE_HANDLE_LIST,
        inherited, sizeof(inherited), nullptr, nullptr);
    if (!attributeReady) { DeleteProcThreadAttributeList(attributes); failureCode = "PROCESS_HANDLE_LIST_FAILED"; return std::nullopt; }
    STARTUPINFOEXW startup{}; startup.StartupInfo.cb = sizeof(startup); startup.lpAttributeList = attributes;
    PROCESS_INFORMATION process{};
    const BOOL created = CreateProcessW(cliPath.c_str(), command.data(), nullptr, nullptr, TRUE,
        EXTENDED_STARTUPINFO_PRESENT | CREATE_NO_WINDOW | CREATE_SUSPENDED,
        nullptr, nullptr, &startup.StartupInfo, &process);
    DeleteProcThreadAttributeList(attributes);
    if (!created) { failureCode = "DUPLEX_PROCESS_START_FAILED"; return std::nullopt; }
    HANDLE job = CreateJobObjectW(nullptr, nullptr);
    JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits{};
    limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
    const bool owned = job != nullptr && SetInformationJobObject(job, JobObjectExtendedLimitInformation,
        &limits, sizeof(limits)) && AssignProcessToJobObject(job, process.hProcess);
    if (!owned || ResumeThread(process.hThread) == static_cast<DWORD>(-1)) {
        TerminateProcess(process.hProcess, ERROR_PROCESS_ABORTED);
        if (job != nullptr) TerminateJobObject(job, ERROR_PROCESS_ABORTED);
        WaitForSingleObject(process.hProcess, 3000);
        if (job != nullptr) CloseHandle(job);
        CloseHandle(process.hThread); CloseHandle(process.hProcess);
        failureCode = "DUPLEX_PROCESS_OWNERSHIP_FAILED";
        return std::nullopt;
    }
    const DWORD wait = WaitForSingleObject(process.hProcess, (duration + kDuplexChildDeadlineSlackSeconds) * 1000);
    if (wait != WAIT_OBJECT_0) {
        SetEvent(cancelEvent);
        const DWORD graceful = WaitForSingleObject(process.hProcess, kDuplexGracefulShutdownMilliseconds);
        if (graceful != WAIT_OBJECT_0) TerminateJobObject(job, ERROR_TIMEOUT);
        WaitForSingleObject(process.hProcess, kDuplexForcedShutdownWaitMilliseconds);
        CloseHandle(job); CloseHandle(process.hThread); CloseHandle(process.hProcess);
        failureCode = graceful == WAIT_OBJECT_0 ? "DUPLEX_PROCESS_TIMEOUT" : "DUPLEX_PROCESS_TERMINATED_OUTCOME_UNKNOWN";
        failureDetail = "nativeExitCode=UNKNOWN;reportCreated=false;termination=" + std::string(graceful == WAIT_OBJECT_0 ? "TIMEOUT" : "FORCED_TERMINATION") + ";argumentParsingFailure=false";
        return std::nullopt;
    }
    DWORD exitCode = 0;
    GetExitCodeProcess(process.hProcess, &exitCode);
    CloseHandle(job); CloseHandle(process.hThread); CloseHandle(process.hProcess);
    std::ifstream input(std::filesystem::path(reportPath), std::ios::binary | std::ios::ate);
    const bool reportCreated = input.good();
    failureDetail = "nativeExitCode=" + std::to_string(exitCode) + ";reportCreated=" + std::string(reportCreated ? "true" : "false") + ";termination=NORMAL;argumentParsingFailure=" + std::string(exitCode == 2 ? "true" : "false");
    if (!input) { failureCode = "DUPLEX_REPORT_MISSING"; return std::nullopt; }
    const auto length = input.tellg();
    if (length <= 0 || length > 65536) { failureCode = "DUPLEX_REPORT_INVALID_SIZE"; return std::nullopt; }
    std::string report(static_cast<std::size_t>(length), '\0'); input.seekg(0); input.read(report.data(), length);
    if (!input) { failureCode = "DUPLEX_REPORT_READ_FAILED"; return std::nullopt; }
    const std::vector<std::uint8_t> bytes(report.begin(), report.end());
    auto parsed = ParseObject(bytes);
    if (!DuplexValidationReport::Validate(parsed, captureId, renderId, period, duration, mmcss, operationId,
        sustained ? "REAL_WASAPI_SUSTAINED_DUPLEX" : "REAL_WASAPI_DUPLEX")) {
        failureCode = "DUPLEX_REPORT_CONTRACT_INVALID";
        return std::nullopt;
    }
    if (exitCode != 0 && exitCode != 4 && exitCode != 6 && exitCode != 7 && exitCode != 8) {
        failureCode = "DUPLEX_PROCESS_FAILED";
        return std::nullopt;
    }
    return report;
}

std::vector<ExclusiveFormatMatrix::Format> ReadCandidates(const rapidjson::Value& payload) {
    using namespace ExclusiveFormatMatrix;
    std::vector<std::uint16_t> channelCounts{1,2};
    if(payload.HasMember("channelCounts")){
        if(!payload["channelCounts"].IsArray()||payload["channelCounts"].Empty()||payload["channelCounts"].Size()>32)
            throw Rejection("INVALID_ARGUMENT","channelCounts must contain 1..32 explicit channel counts.");
        channelCounts.clear();std::set<unsigned> unique;
        for(const auto& value:payload["channelCounts"].GetArray()){
            if(!value.IsUint()||value.GetUint()<1||value.GetUint()>64||!unique.insert(value.GetUint()).second)
                throw Rejection("INVALID_ARGUMENT","Each explicit channel count must be unique and within 1..64.");
            channelCounts.push_back(static_cast<std::uint16_t>(value.GetUint()));
        }
    }
    std::vector<Format> formats;formats.reserve(channelCounts.size()*6);
    for(auto rate:{44'100U,48'000U})for(auto channels:channelCounts)
        for(auto encoding:{Encoding::Pcm16,Encoding::Pcm24In32,Encoding::Float32})formats.push_back({rate,channels,encoding});
    return formats;
}

ExclusiveFormatMatrix::Format ReadOneFormat(const rapidjson::Value& payload,ExclusiveFormatMatrix::Period& period) {
    using namespace ExclusiveFormatMatrix;
    int rate{},channels{};std::string encoding,periodText;
    if(!ReadInt(payload,"sampleRate",rate)||!ReadInt(payload,"channels",channels)||!ReadString(payload,"encoding",encoding)||!ReadString(payload,"period",periodText))
        throw Rejection("INVALID_ARGUMENT","sampleRate, channels, encoding, and period are required.");
    if((rate!=44100&&rate!=48000)||channels<1||channels>64)throw Rejection("NOT_TESTED","Format candidate cannot be represented safely.");
    const auto formatEncoding=encoding=="PCM16"?Encoding::Pcm16:encoding=="PCM24_IN_32"?Encoding::Pcm24In32:encoding=="FLOAT32"?Encoding::Float32:static_cast<Encoding>(255);
    if(static_cast<unsigned>(formatEncoding)>static_cast<unsigned>(Encoding::Float32))throw Rejection("NOT_TESTED","Encoding is not a supported matrix candidate.");
    if(periodText!="DEFAULT"&&periodText!="MINIMUM")throw Rejection("INVALID_ARGUMENT","period must be DEFAULT or MINIMUM.");
    period=periodText=="DEFAULT"?Period::Default:Period::Minimum;
    return {static_cast<std::uint32_t>(rate),static_cast<std::uint16_t>(channels),formatEncoding};
}
bool IsCustomPeriod(std::string_view value) {
    if (!value.starts_with("CUSTOM:")) return false;
    const auto first=value.find(':',7); if (first==std::string_view::npos) return false;
    try { const auto hns=std::stoll(std::string(value.substr(7,first-7))); const auto frames=std::stoul(std::string(value.substr(first+1))); return hns>0&&frames>0&&hns<=10'000'000&&frames<=48'000; }
    catch (...) { return false; }
}
bool IsAuthorizedPeriod(std::string_view value) { return value=="DEFAULT"||value=="MINIMUM"||IsCustomPeriod(value); }

struct ScanAllowedFormat { int rate{},channels{};std::string encoding;std::set<std::string> periods,mmcss; };
struct ScanAuthorization {
    std::string id;std::unordered_map<std::string,std::string> endpoints;std::set<std::string> operations;
    std::vector<ScanAllowedFormat> formats;std::unordered_set<std::string> usedOperations;
    std::uint64_t expires{},discoveryGeneration{};int maxDuration{},maxOperations{},used{};
    bool invalid{},active{};std::string activeOperationId;
};
bool HasOnlyMembers(const rapidjson::Value& value,std::initializer_list<std::string_view> names) {if(!value.IsObject())return false;for(auto it=value.MemberBegin();it!=value.MemberEnd();++it){const std::string_view name(it->name.GetString(),it->name.GetStringLength());if(std::find(names.begin(),names.end(),name)==names.end())return false;}return true;}
std::string ScanPlanFingerprint(const rapidjson::Value& payload) {
    const auto& endpoints=payload["endpoints"];const auto& operations=payload["allowedOperations"];const auto& formats=payload["formats"];
    std::vector<std::string> endpointLines,operationLines,formatLines;
    for(const auto& endpoint:endpoints.GetArray()){std::string id,direction;ReadString(endpoint,"endpointId",id);ReadString(endpoint,"direction",direction);endpointLines.push_back("E|"+direction+"|"+std::to_string(id.size())+"|"+id+"\n");}
    for(const auto& operation:operations.GetArray())operationLines.push_back("O|"+std::string(operation.GetString(),operation.GetStringLength())+"\n");
    for(const auto& format:formats.GetArray()){
        std::string encoding;ReadString(format,"encoding",encoding);int rate{},channels{};ReadInt(format,"sampleRateHz",rate);ReadInt(format,"channels",channels);
        for(const auto& period:format["periods"].GetArray())for(const auto& mmcss:format["mmcssModes"].GetArray())formatLines.push_back("F|"+std::to_string(rate)+"|"+encoding+"|"+std::to_string(channels)+"|"+std::string(period.GetString(),period.GetStringLength())+"|"+std::string(mmcss.GetString(),mmcss.GetStringLength())+"\n");
    }
    std::sort(endpointLines.begin(),endpointLines.end());std::sort(operationLines.begin(),operationLines.end());std::sort(formatLines.begin(),formatLines.end());
    int lifetime{},duration{},count{};ReadInt(payload,"lifetimeSeconds",lifetime);ReadInt(payload,"maximumOperationDurationSeconds",duration);ReadInt(payload,"maximumOperationCount",count);
    std::string canonical="C3.2|"+std::to_string(lifetime)+"|"+std::to_string(duration)+"|"+std::to_string(count)+"\n";
    for(const auto& line:endpointLines)canonical+=line;for(const auto& line:operationLines)canonical+=line;for(const auto& line:formatLines)canonical+=line;
    return Sha256(canonical);
}
std::shared_ptr<ScanAuthorization> ParseScanAuthorization(const rapidjson::Value& payload,const std::unordered_map<std::string,std::string>& discovered,std::uint64_t generation,std::unordered_set<std::string>& consumedConfirmations) {
    auto session=std::make_shared<ScanAuthorization>();int lifetime{},duration{},count{};std::string acceptedId,fingerprint;bool accepted=false;
    if(!HasOnlyMembers(payload,{"endpoints","allowedOperations","formats","lifetimeSeconds","maximumOperationDurationSeconds","maximumOperationCount","planFingerprint","authorization"})||!payload.HasMember("endpoints")||!payload["endpoints"].IsArray()||payload["endpoints"].Empty()||payload["endpoints"].Size()>2||!payload.HasMember("allowedOperations")||!payload["allowedOperations"].IsArray()||payload["allowedOperations"].Empty()||payload["allowedOperations"].Size()>5||!payload.HasMember("formats")||!payload["formats"].IsArray()||payload["formats"].Empty()||payload["formats"].Size()>32||!ReadInt(payload,"lifetimeSeconds",lifetime)||lifetime<1||lifetime>660||!ReadInt(payload,"maximumOperationDurationSeconds",duration)||duration<1||duration>600||!ReadInt(payload,"maximumOperationCount",count)||count<1||count>24||!ReadString(payload,"planFingerprint",fingerprint)||!payload.HasMember("authorization")||!HasOnlyMembers(payload["authorization"],{"accepted","confirmationId"}))throw Rejection("INVALID_SCAN_PLAN","Scan authorization plan is incomplete or outside its bounds.");
    const auto& receipt=payload["authorization"];if(receipt.HasMember("accepted")&&receipt["accepted"].IsBool())accepted=receipt["accepted"].GetBool();if(!accepted||!ReadString(receipt,"confirmationId",acceptedId))throw Rejection("SCAN_CONSENT_REQUIRED","Explicit operator authorization is required for this scan plan.");
    acceptedId=GuidString(acceptedId);if(consumedConfirmations.size()>=128)throw Rejection("SCAN_CONSENT_CAPACITY","Worker scan authorization history is bounded; restart the Worker before registering another session.");if(consumedConfirmations.contains(acceptedId))throw Rejection("SCAN_CONSENT_REPLAY","This scan authorization has already been used.");
    for(const auto& endpoint:payload["endpoints"].GetArray()){std::string id,direction;if(!HasOnlyMembers(endpoint,{"endpointId","direction"})||!ReadString(endpoint,"endpointId",id)||id.empty()||id.size()>32768||!ReadString(endpoint,"direction",direction)||(direction!="CAPTURE"&&direction!="RENDER")||id.starts_with("fake-")||id.starts_with("synthetic-")||discovered.find(id)==discovered.end()||discovered.at(id)!=direction||!session->endpoints.emplace(id,direction).second)throw Rejection("SCAN_ENDPOINT_INVALID","Every authorized endpoint must be a distinct exact endpoint from current active discovery.");}
    bool sustainedAllowed=false;
    for(const auto& operation:payload["allowedOperations"].GetArray()){if(!operation.IsString())throw Rejection("INVALID_SCAN_PLAN","Allowed operation names must be strings.");std::string name(operation.GetString(),operation.GetStringLength());if(name=="ValidateSustainedDuplex")sustainedAllowed=true;else if(name!="QueryFormatMatrix"&&name!="ValidateFormatMatrix"&&name!="InitializeExclusiveCustomPeriod"&&name!="ValidateStream"&&name!="ValidateDuplex")throw Rejection("SCAN_OPERATION_FORBIDDEN","Scan plans may authorize only Query, Initialize, custom-period Initialize, Stream, or Duplex.");if(!session->operations.insert(name).second)throw Rejection("INVALID_SCAN_PLAN","Duplicate operation scope.");}
    if(duration>10&&!sustainedAllowed)throw Rejection("INVALID_SCAN_PLAN","Durations over ten seconds require sustained Duplex authorization.");
    for(const auto& format:payload["formats"].GetArray()){
        ScanAllowedFormat item;std::string key;
        if(!HasOnlyMembers(format,{"sampleRateHz","encoding","channels","periods","mmcssModes"})||!ReadInt(format,"sampleRateHz",item.rate)||(item.rate!=44100&&item.rate!=48000)||!ReadInt(format,"channels",item.channels)||item.channels<1||item.channels>64||!ReadString(format,"encoding",item.encoding)||(item.encoding!="PCM16"&&item.encoding!="PCM24_IN_32"&&item.encoding!="FLOAT32")||!format.HasMember("periods")||!format["periods"].IsArray()||format["periods"].Empty()||!format.HasMember("mmcssModes")||!format["mmcssModes"].IsArray()||format["mmcssModes"].Empty())throw Rejection("INVALID_SCAN_PLAN","Format authorization contains an unsupported rate, encoding, channel count, period or MMCSS setting.");
        for(const auto& period:format["periods"].GetArray()){const std::string_view value(period.IsString()?period.GetString():"",period.IsString()?period.GetStringLength():0);if(!period.IsString()||!IsAuthorizedPeriod(value)||!item.periods.emplace(period.GetString(),period.GetStringLength()).second)throw Rejection("INVALID_SCAN_PLAN","Invalid or duplicate scan period.");}
        for(const auto& mode:format["mmcssModes"].GetArray()){if(!mode.IsString()||(std::string_view(mode.GetString(),mode.GetStringLength())!="OFF"&&std::string_view(mode.GetString(),mode.GetStringLength())!="PRO_AUDIO")||!item.mmcss.emplace(mode.GetString(),mode.GetStringLength()).second)throw Rejection("INVALID_SCAN_PLAN","Invalid or duplicate MMCSS setting.");}
        session->formats.push_back(std::move(item));
    }
    if(ScanPlanFingerprint(payload)!=fingerprint)throw Rejection("SCAN_FINGERPRINT_MISMATCH","Worker-computed immutable scan plan fingerprint does not match.");
    consumedConfirmations.insert(acceptedId);session->maxDuration=duration;session->maxOperations=count;session->expires=GetTickCount64()+static_cast<std::uint64_t>(lifetime)*1000;session->discoveryGeneration=generation;return session;
}
std::string AuthorizeScanOperation(const rapidjson::Value& payload,const std::shared_ptr<ScanAuthorization>& session,const std::unordered_map<std::string,std::string>& discovered,std::uint64_t generation,std::string_view command,const std::vector<ExclusiveFormatMatrix::Format>& formats,std::string_view period,int duration,std::string_view mmcss,std::string_view endpointId,std::string_view direction,std::string_view captureId={},std::string_view renderId={}) {
    const bool hasSession=payload.HasMember("scanSessionId"),hasOperation=payload.HasMember("scanOperationId");if(!hasSession&&!hasOperation)return {};if(!hasSession||!hasOperation||!session)throw Rejection("SCAN_SESSION_INVALID","Both scan session and operation identity are required.");
    std::string sessionId,scanOperationId;if(!ReadString(payload,"scanSessionId",sessionId)||!ReadString(payload,"scanOperationId",scanOperationId)||GuidString(sessionId)!=session->id)throw Rejection("SCAN_SESSION_INVALID","Scan session token is not valid for this Worker connection.");scanOperationId=GuidString(scanOperationId);
    if(payload.HasMember("operationId")&&(!payload["operationId"].IsString()||GuidString(std::string_view(payload["operationId"].GetString(),payload["operationId"].GetStringLength()))!=scanOperationId))throw Rejection("SCAN_OPERATION_ID_MISMATCH","Scan operation identity differs from the operation report identity.");
    if(session->invalid||GetTickCount64()>=session->expires||generation!=session->discoveryGeneration)throw Rejection("SCAN_SESSION_STALE","Scan authorization expired, was cancelled, or no longer matches current discovery.");
    if(!session->operations.contains(std::string(command))||session->active||session->used>=session->maxOperations||session->usedOperations.contains(scanOperationId))throw Rejection("SCAN_OPERATION_REJECTED","Operation is out of scope, replayed, concurrent, or beyond the approved count.");
    auto endpointAllowed=[&](std::string_view id,std::string_view dir){auto allowed=session->endpoints.find(std::string(id));auto current=discovered.find(std::string(id));return allowed!=session->endpoints.end()&&allowed->second==dir&&current!=discovered.end()&&current->second==dir;};
    if(command=="ValidateDuplex"||command=="ValidateSustainedDuplex"){if(!endpointAllowed(captureId,"CAPTURE")||!endpointAllowed(renderId,"RENDER"))throw Rejection("SCAN_ENDPOINT_OUT_OF_SCOPE","Duplex endpoint pair is outside the approved session.");}
    else if(!endpointAllowed(endpointId,direction))throw Rejection("SCAN_ENDPOINT_OUT_OF_SCOPE","Operation endpoint is outside the approved session.");
    if(duration>session->maxDuration)throw Rejection("SCAN_DURATION_EXCEEDED","Operation duration exceeds the approved scan-session limit.");
    for(const auto& candidate:formats){const auto encoding=ExclusiveFormatMatrix::EncodingText(candidate.encoding);const bool found=std::any_of(session->formats.begin(),session->formats.end(),[&](const auto& item){return item.rate==static_cast<int>(candidate.sampleRate)&&item.channels==candidate.channels&&item.encoding==encoding&&(command=="QueryFormatMatrix"||item.periods.contains(std::string(period)))&&(command=="QueryFormatMatrix"||command=="ValidateFormatMatrix"||item.mmcss.contains(std::string(mmcss)));});if(!found)throw Rejection("SCAN_FORMAT_OUT_OF_SCOPE","Format, period, or MMCSS setting is outside the approved scan plan.");}
    session->usedOperations.insert(scanOperationId);++session->used;session->active=true;session->activeOperationId=scanOperationId;return scanOperationId;
}
bool ScanReportSafe(std::string_view json) {
    try {
        std::vector<std::uint8_t> bytes(json.begin(),json.end());auto root=ParseObject(bytes);bool safe=true;
        const rapidjson::Value* report=&root;if(root.HasMember("report")&&root["report"].IsObject())report=&root["report"];if(report->HasMember("candidateReport")&&(*report)["candidateReport"].IsObject())report=&(*report)["candidateReport"];
        auto cleanupSafe=[](const rapidjson::Value& value,const char* name,bool noInitialize){const auto member=value.FindMember(name);if(member==value.MemberEnd()||!member->value.IsString())return false;const std::string_view state(member->value.GetString(),member->value.GetStringLength());return state=="NOT_ACQUIRED"||state=="NO_CLIENT_ACQUIRED"||(noInitialize&&(state=="INCOMPLETE"||state=="NOT_ATTEMPTED"))||(state.find("RELEASE_ATTEMPTED")!=std::string_view::npos&&state.find("FAILED")==std::string_view::npos&&state.find("UNKNOWN")==std::string_view::npos);};
        if(report->HasMember("candidates")) {
            const auto& candidates=(*report)["candidates"];if(!candidates.IsArray()||candidates.Empty())return false;
            const bool periodDiscovery=report->HasMember("reportType")&&(*report)["reportType"].IsString()&&std::string_view((*report)["reportType"].GetString(),(*report)["reportType"].GetStringLength())=="wasapi-exclusive-period-discovery";
            for(const auto& candidate:candidates.GetArray())if(!candidate.IsObject()||!cleanupSafe(candidate,periodDiscovery?"cleanupStatus":"queryCleanupStatus",false)||(candidate.HasMember("initializeAttempted")&&candidate["initializeAttempted"].IsBool()&&candidate["initializeAttempted"].GetBool()&&!cleanupSafe(candidate,"cleanupStatus",false)))return false;
        } else if(report->HasMember("capture")||report->HasMember("render")) {
            for(const auto* side:{"capture","render"})if(!report->HasMember(side)||!(*report)[side].IsObject()||!cleanupSafe((*report)[side],"cleanup",false))return false;
        } else if(!cleanupSafe(*report,"cleanup",false))return false;
        auto visit=[&](auto&& self,const rapidjson::Value& value)->void {
            if(value.IsObject()) {
                for(auto it=value.MemberBegin();it!=value.MemberEnd();++it) {
                    const std::string_view name(it->name.GetString(),it->name.GetStringLength());
                    if(name=="deviceInvalidated"&&it->value.IsBool()&&it->value.GetBool())safe=false;
                    self(self,it->value);
                }
            } else if(value.IsArray())for(const auto& item:value.GetArray())self(self,item);
        };
        visit(visit,root);return safe;
    } catch(...) { return false; }
}
struct ScanOperationFinish { std::shared_ptr<ScanAuthorization> session;bool accepted{},finished{};void Finish(bool reusable){if(!session||finished)return;session->active=false;session->activeOperationId.clear();if(!reusable)session->invalid=true;accepted=reusable;finished=true;}~ScanOperationFinish(){Finish(accepted);} };

bool HandleClient(HANDLE pipe) {
    Writer writer(pipe); FakeOperationService service(writer); std::vector<std::uint8_t> bytes;
    std::unordered_map<std::string,std::string> discoveredActive;
    std::unordered_set<std::string> consumedInitReceipts;
    std::unordered_set<std::string> consumedStreamReceipts;
    std::unordered_set<std::string> consumedDuplexReceipts;
    std::unordered_set<std::string> consumedScanConfirmations;
    std::shared_ptr<ScanAuthorization> scanAuthorization;
    std::uint64_t discoveryGeneration{};
    std::atomic<bool> realOperationBusy{};
    std::thread realOperation;
    std::mutex streamCancelMutex;
    HANDLE streamCancelEvent=nullptr;
    std::string streamOperationId,streamOperationCorrelation;
    HANDLE duplexCancelEvent=nullptr;
    std::string duplexOperationId,duplexOperationCorrelation,pendingDuplexCancelId;
    if(!ReadFrame(pipe,bytes))return true;
    try {
        auto hello=ParseObject(bytes);std::string helloId;
        const auto failure=ValidateHandshake(hello,helloId);
        if(!failure.empty())return writer.Error("HANDSHAKE","INCOMPATIBLE_PROTOCOL",failure,helloId);
        if(!writer.Raw("{\"bootstrapVersion\":1,\"kind\":\"response\",\"correlationId\":\""+helloId+"\",\"product\":\"KLIDAN_AUDIO_PROBE_WORKER\",\"protocol\":{\"major\":1,\"minor\":0},\"schemas\":{\"command\":1,\"event\":1,\"report\":1},\"workerVersion\":\"0.3.2\",\"capabilities\":[\"Handshake\",\"ListEndpoints\",\"ListWindowsEndpoints\",\"RealExclusiveFormatQuery\",\"RealExclusiveFormatInitialize\",\"RealExclusiveCustomPeriodInitialize\",\"RealExclusiveStreamValidation\",\"RealExclusiveDuplexValidation\",\"RealExclusiveSustainedDuplexValidation\",\"ScanSessionAuthorizationV1\",\"StartCaptureMeter\",\"StartRenderChannelTest\",\"StopSession\",\"CancelStreamValidation\",\"CancelDuplexValidation\",\"Shutdown\""
#if defined(PROBE_TEST_WASAPI_BACKEND)
            ",\"TestInjectedFormatBackend\",\"TestInjectedStreamBackend\",\"TestInjectedDuplexBackend\""
#endif
            "]}"))return false;
    } catch(const std::exception&){writer.Error("HANDSHAKE","INVALID_HANDSHAKE","Malformed handshake frame.");return false;}
    while(true) {
        try { if(!ReadFrame(pipe,bytes)) break; }
        catch(const ProtocolFailure& ex) { writer.Error("REQUEST_FRAME","PROTOCOL_ERROR",ex.what()); return false; }
        std::string correlation,command;
        try {
            auto request=ParseObject(bytes);
            const auto envelope=ValidateRequestEnvelope(request,correlation,command);
            if(!envelope.empty()) {
                if(correlation.empty()){writer.Error("REQUEST_ENVELOPE",envelope,"Malformed request envelope.");return false;}
                writer.Response(correlation,command.empty()?"UnknownCommand":command,"ERROR",envelope,envelope=="INCOMPATIBLE_PROTOCOL"?"Request protocol/schema is incompatible.":"Required request envelope fields are invalid.");
                continue;
            }
            const auto& payload=request["payload"];
            if(command=="ListEndpoints") {
                std::string direction;if(!ReadString(payload,"direction",direction)||(direction!="BOTH"&&direction!="CAPTURE"&&direction!="RENDER")){writer.Response(correlation,"ListEndpoints","ERROR","INVALID_ARGUMENT","direction must be BOTH, CAPTURE, or RENDER");continue;}
                const auto c2=Endpoint("fake-capture-2","ДЕМО · Capture · 2 канала","CAPTURE",2,Channels(2))+FormatSet({{"PCM16-48000",2},{"FLOAT32-48000",1}})+"}";
                const auto c64=Endpoint("fake-capture-64","ДЕМО · Capture · 64 канала","CAPTURE",64,Channels(64))+FormatSet({{"PCM16-48000",64},{"PCM24-48000",48},{"FLOAT32-48000",32},{"FLOAT32-48000-16",16},{"PCM24-48000-8",8},{"PCM16-48000-1",1}})+"}";
                const auto r8=Endpoint("fake-render-8","ДЕМО · Render · 8 каналов","RENDER",8,Channels(8))+FormatSet({{"PCM16-48000",8},{"FLOAT32-48000",2},{"PCM16-48000-1",1}})+"}";
                const auto r64=Endpoint("fake-render-64","ДЕМО · Render · 64 канала","RENDER",64,Channels(64))+FormatSet({{"PCM16-48000",64},{"PCM24-48000",48},{"FLOAT32-48000",32},{"FLOAT32-48000-16",16},{"PCM24-48000-8",8},{"PCM16-48000-1",1}})+"}";
                std::string list="[";if(direction!="RENDER")list+=c2+","+c64;if(direction=="BOTH")list+=",";if(direction!="CAPTURE")list+=r8+","+r64;if(list.ends_with(','))list.pop_back();list+="]";
                writer.Response(correlation,"ListEndpoints","OK","","","{\"provenance\":\"FAKE_SYNTHETIC\",\"endpoints\":"+list+"}");continue;
            }
            if(command=="ListWindowsEndpoints") {
#if defined(PROBE_TEST_DISCOVERY_DELAY)
                if(const auto* scenario=std::getenv("KLIDAN_TEST_FORMAT_SCENARIO");scenario&&std::string_view(scenario)=="HOLD_DISCOVERY")std::this_thread::sleep_for(std::chrono::milliseconds(350));
#endif
                if(realOperationBusy.load()){writer.Response(correlation,command,"ERROR","BUSY","Discovery cannot change while an Exclusive operation is active.");continue;}
                if(scanAuthorization)scanAuthorization->invalid=true;
                try {
#if defined(PROBE_TEST_WASAPI_BACKEND)
                    discoveredActive.clear();discoveredActive.emplace("injected-endpoint-capture","CAPTURE");discoveredActive.emplace("injected-endpoint-render","RENDER");
                    const auto payload=std::string("{\"provenance\":\"TEST_FAKE_FORMAT_BACKEND\",\"operationState\":\"COMPLETE\",\"captureHresult\":0,\"renderHresult\":0,\"errors\":[],\"endpoints\":[{\"endpointId\":\"injected-endpoint-capture\",\"endpointIdEvidence\":{\"hresult\":0,\"state\":\"AVAILABLE\"},\"friendlyName\":\"Test Capture\",\"friendlyNameEvidence\":{\"hresult\":0,\"state\":\"AVAILABLE\"},\"direction\":\"CAPTURE\",\"deviceState\":\"ACTIVE\",\"rawDeviceState\":1,\"deviceStateEvidence\":{\"hresult\":0,\"state\":\"AVAILABLE\"},\"formFactor\":\"UNKNOWN\",\"formFactorEvidence\":{\"hresult\":0,\"state\":\"UNKNOWN\"},\"sharedModeFormat\":{\"property\":{\"hresult\":0,\"state\":\"UNKNOWN\"},\"parseState\":\"UNKNOWN\",\"formatTag\":null,\"encoding\":null,\"reportedChannelCount\":null,\"sampleRate\":null,\"bitsPerSample\":null,\"validBitsPerSample\":null,\"topologyState\":\"UNKNOWN\",\"speakerMask\":null,\"speakerMaskState\":\"UNKNOWN\"}},{\"endpointId\":\"injected-endpoint-render\",\"endpointIdEvidence\":{\"hresult\":0,\"state\":\"AVAILABLE\"},\"friendlyName\":\"Test Render\",\"friendlyNameEvidence\":{\"hresult\":0,\"state\":\"AVAILABLE\"},\"direction\":\"RENDER\",\"deviceState\":\"ACTIVE\",\"rawDeviceState\":1,\"deviceStateEvidence\":{\"hresult\":0,\"state\":\"AVAILABLE\"},\"formFactor\":\"UNKNOWN\",\"formFactorEvidence\":{\"hresult\":0,\"state\":\"UNKNOWN\"},\"sharedModeFormat\":{\"property\":{\"hresult\":0,\"state\":\"UNKNOWN\"},\"parseState\":\"UNKNOWN\",\"formatTag\":null,\"encoding\":null,\"reportedChannelCount\":null,\"sampleRate\":null,\"bitsPerSample\":null,\"validBitsPerSample\":null,\"topologyState\":\"UNKNOWN\",\"speakerMask\":null,\"speakerMaskState\":\"UNKNOWN\"}}]}");
#else
                    auto source=probe::windows_endpoints::CreateMmDeviceSource();
                    const auto result=probe::windows_endpoints::Discover(*source);
                    discoveredActive.clear();
                    for(const auto& endpoint:result.endpoints)if(endpoint.deviceState=="ACTIVE"&&!endpoint.endpointId.empty())discoveredActive.emplace(endpoint.endpointId,endpoint.direction);
                    const auto payload=probe::windows_endpoints::SerializeJson(result);
#endif
                    ++discoveryGeneration;
                    if(payload.size()>kMaximumFrame){writer.Response(correlation,command,"ERROR","RESPONSE_TOO_LARGE","Windows endpoint metadata exceeds the protocol frame limit.");continue;}
                    writer.Response(correlation,command,"OK","","",payload);
                } catch(const std::exception&) {
                    writer.Response(correlation,command,"ERROR","DISCOVERY_FAILED","Windows endpoint discovery service failed.");
                }
                continue;
            }
            if(command=="RegisterScanSession") {
                if(realOperationBusy.load()||service.Busy()){writer.Response(correlation,command,"ERROR","BUSY","A scan session can only be registered while the Worker is idle.");continue;}
                if(scanAuthorization&&!scanAuthorization->invalid){writer.Response(correlation,command,"ERROR","SCAN_SESSION_EXISTS","Only one scan authorization may exist per Worker connection.");continue;}
                try {scanAuthorization=ParseScanAuthorization(payload,discoveredActive,discoveryGeneration,consumedScanConfirmations);scanAuthorization->id=NewGuid();writer.Response(correlation,command,"OK","","","{\"scanSessionId\":\""+scanAuthorization->id+"\",\"state\":\"AUTHORIZED\",\"expiresInSeconds\":"+std::to_string((scanAuthorization->expires-GetTickCount64())/1000)+"}");}
                catch(const Rejection& ex){writer.Response(correlation,command,"ERROR",ex.code,ex.what());}
                continue;
            }
            if(command=="CancelScanSession"||command=="CloseScanSession") {
                std::string id;if(!ReadString(payload,"scanSessionId",id)||!scanAuthorization||GuidString(id)!=scanAuthorization->id){writer.Response(correlation,command,"ERROR","SCAN_SESSION_INVALID","No matching scan session exists on this Worker connection.");continue;}
                if(command=="CloseScanSession"&&scanAuthorization->active){writer.Response(correlation,command,"ERROR","BUSY","An authorized operation is still active.");continue;}
                if(command=="CloseScanSession"&&realOperation.joinable())realOperation.join();
#if defined(PROBE_TEST_WASAPI_BACKEND)
                if(command=="CloseScanSession") {
                    const auto scenario=std::getenv("KLIDAN_TEST_CLOSE_SCENARIO");
                    if(scenario&&std::string_view(scenario)=="DELAY_RESPONSE") Sleep(30'000);
                    if(scenario&&std::string_view(scenario)=="NO_RESPONSE") Sleep(30'000);
                }
#endif
                scanAuthorization->invalid=true;
                if(command=="CancelScanSession"&&scanAuthorization->active){std::lock_guard lock(streamCancelMutex);if(streamCancelEvent)SetEvent(streamCancelEvent);if(duplexCancelEvent)SetEvent(duplexCancelEvent);}
                writer.Response(correlation,command,"OK","","","{\"state\":\"INVALIDATED\",\"operationOutcome\":\""+std::string(scanAuthorization->active?"UNKNOWN":"NOT_RUNNING")+"\"}");if(command=="CloseScanSession")scanAuthorization.reset();continue;
            }
            if(command=="InitializeExclusiveCustomPeriod") {
                std::string operationId,endpointId,direction,encoding,periodText; int sampleRate{},channels{},period100ns{},frames{};
                if(!ReadString(payload,"operationId",operationId)||!ReadString(payload,"endpointId",endpointId)||!ReadString(payload,"direction",direction)||
                   !ReadInt(payload,"sampleRate",sampleRate)||!ReadInt(payload,"channels",channels)||!ReadString(payload,"encoding",encoding)||
                   !ReadString(payload,"period",periodText)||direction!="CAPTURE"&&direction!="RENDER"||sampleRate!=48000||channels!=2||encoding!="PCM16"||
                   !ReadInt(payload,"period100ns",period100ns)||!ReadInt(payload,"frames",frames)||period100ns<=0||frames<=0||frames>48000||
                   periodText!="CUSTOM:"+std::to_string(period100ns)+":"+std::to_string(frames)||ProbeMath::DurationForFrames(static_cast<std::uint32_t>(frames),48000)!=period100ns) {
                    writer.Response(correlation,command,"ERROR","INVALID_ARGUMENT","Custom period requires exact 48 kHz stereo PCM16, a bounded duration, and matching frame conversion.");continue;
                }
                try { operationId=GuidString(operationId); } catch(const Rejection& ex){writer.Response(correlation,command,"ERROR",ex.code,ex.what());continue;}
                if(endpointId.starts_with("fake-")||endpointId.starts_with("synthetic-")){writer.Response(correlation,command,"ERROR","SYNTHETIC_ENDPOINT_REJECTED","Synthetic endpoint identities cannot be used for real period discovery.");continue;}
                const auto cached=discoveredActive.find(endpointId);
                if(cached==discoveredActive.end()||cached->second!=direction){writer.Response(correlation,command,"ERROR","ENDPOINT_NOT_DISCOVERED","The exact active endpoint must come from current Windows discovery.");continue;}
                if(!scanAuthorization||scanAuthorization->invalid||!payload.HasMember("scanSessionId")){writer.Response(correlation,command,"ERROR","SCAN_SESSION_REQUIRED","Custom-period discovery requires an active immutable scan authorization.");continue;}
                const ExclusiveFormatMatrix::Format format{48000,2,ExclusiveFormatMatrix::Encoding::Pcm16};
                std::shared_ptr<ScanAuthorization> operationScanSession;
                try { AuthorizeScanOperation(payload,scanAuthorization,discoveredActive,discoveryGeneration,command,{format},periodText,0,"OFF",endpointId,direction); operationScanSession=scanAuthorization; }
                catch(const Rejection& ex){writer.Response(correlation,command,"ERROR",ex.code,ex.what());continue;}
                if(service.Busy()){operationScanSession->active=false;writer.Response(correlation,command,"ERROR","BUSY","A fake session is active in this worker.");continue;}
                if(realOperationBusy.exchange(true)){operationScanSession->active=false;writer.Response(correlation,command,"ERROR","BUSY","Another real diagnostic operation is running in this worker.");continue;}
                if(realOperation.joinable())realOperation.join();
                std::string periodScenario;
#if defined(PROBE_TEST_WASAPI_BACKEND)
                periodScenario = std::getenv("KLIDAN_TEST_PERIOD_SCENARIO") ? std::getenv("KLIDAN_TEST_PERIOD_SCENARIO") : "";
#endif
                realOperation=std::thread([&,operationId=std::move(operationId),endpointId=std::move(endpointId),direction=std::move(direction),period100ns,frames,correlation,operationScanSession,format,periodScenario]() mutable {
                    ScanOperationFinish scanFinish{operationScanSession};
#if defined(PROBE_TEST_WASAPI_BACKEND)
                    if(periodScenario=="NO_RESPONSE") { Sleep(30000); scanFinish.Finish(false); realOperationBusy=false; return; }
#endif
                    const auto publish=[&](const std::string& report) { const auto body="{\"operationId\":"+JsonString(operationId)+",\"endpointId\":"+JsonString(endpointId)+",\"direction\":"+JsonString(direction)+",\"report\":"+report+"}"; const bool safe=ScanReportSafe(body); scanFinish.Finish(safe); if(!writer.Response(correlation,"InitializeExclusiveCustomPeriod","OK","","",body))scanFinish.Finish(false); };
#if defined(PROBE_TEST_WASAPI_BACKEND)
                    try { InjectedWasapiApi api; auto attempt=api.InitializeOnly(format,ExclusiveFormatMatrix::PeriodRequest::Custom(period100ns,static_cast<std::uint32_t>(frames))); static std::atomic<int> rejectedFirstFour{}; if(periodScenario=="REJECT_FIRST_FOUR" && rejectedFirstFour.fetch_add(1)<4) attempt.finalInitializeHr=ExclusiveFormatMatrix::kUnsupportedFormat; const auto classification=ExclusivePeriodDiscovery::Classify(attempt); ExclusiveFormatMatrix::Attempt reportAttempt=attempt; const auto report=ExclusivePeriodDiscovery::Report{endpointId,direction=="CAPTURE"?ExclusiveFormatMatrix::Direction::Capture:ExclusiveFormatMatrix::Direction::Render,format,{30'000,100'000},operationId,NewGuid(),{{period100ns,static_cast<std::uint32_t>(frames),classification,ExclusivePeriodDiscovery::ClassificationText(classification),reportAttempt}}}; publish(ExclusivePeriodDiscovery::ToJson(report,"2026-11-01T00:00:00Z","TEST_FAKE_PERIOD_BACKEND")); }
                    catch(...) { writer.Response(correlation,"InitializeExclusiveCustomPeriod","ERROR","INJECTED_BACKEND_FAILED","The injected custom-period backend did not produce a complete response."); }
#else
                    HRESULT com=CoInitializeEx(nullptr,COINIT_MULTITHREADED);
                    if(FAILED(com)){writer.Response(correlation,"InitializeExclusiveCustomPeriod","ERROR","COM_INITIALIZE_FAILED","Custom-period operation thread could not initialize COM.");realOperationBusy=false;return;}
                    IMMDevice* device=nullptr;
                    try { const auto validation=RevalidateEndpoint(endpointId,direction,&device); if(!validation.empty())throw Rejection("ENDPOINT_INVALID","Endpoint was not active immediately before custom-period Initialize."); ExclusiveFormatMatrix::WasapiApi api(device); const auto query=api.QuerySupport(format); if(!query.minimumDevicePeriod100ns||!query.defaultDevicePeriod100ns||period100ns<*query.minimumDevicePeriod100ns||period100ns>*query.defaultDevicePeriod100ns)throw Rejection("CUSTOM_PERIOD_OUT_OF_BOUNDS","Requested period is outside this endpoint's current minimum/default bounds."); const auto attempt=api.InitializeOnly(format,ExclusiveFormatMatrix::PeriodRequest::Custom(period100ns,static_cast<std::uint32_t>(frames))); const auto classification=ExclusivePeriodDiscovery::Classify(attempt); ExclusivePeriodDiscovery::Report report{endpointId,direction=="CAPTURE"?ExclusiveFormatMatrix::Direction::Capture:ExclusiveFormatMatrix::Direction::Render,format,{*query.minimumDevicePeriod100ns,*query.defaultDevicePeriod100ns},operationId,NewGuid(),{{period100ns,static_cast<std::uint32_t>(frames),classification,ExclusivePeriodDiscovery::ClassificationText(classification),attempt}}}; const auto text=ExclusivePeriodDiscovery::ToJson(report,MatrixTimestampUtc(),"REAL_WASAPI_INITIALIZE_ONLY"); device->Release();CoUninitialize();publish(text); }
                    catch(const Rejection& ex){if(device)device->Release();CoUninitialize();scanFinish.Finish(false);writer.Response(correlation,"InitializeExclusiveCustomPeriod","ERROR",ex.code,ex.what());}
                    catch(...){if(device)device->Release();CoUninitialize();scanFinish.Finish(false);writer.Response(correlation,"InitializeExclusiveCustomPeriod","ERROR","NATIVE_OPERATION_FAILED","Custom-period WASAPI operation did not produce a complete response.");}
#endif
                    realOperationBusy=false;
                });
                continue;
            }
            if(command=="QueryFormatMatrix"||command=="ValidateFormatMatrix") {
                std::string endpointId,direction;
                if(!ReadString(payload,"endpointId",endpointId)||endpointId.empty()||endpointId.size()>32768||
                   !ReadString(payload,"direction",direction)||(direction!="CAPTURE"&&direction!="RENDER")) {
                    writer.Response(correlation,command,"ERROR","INVALID_ARGUMENT","An exact endpointId and CAPTURE/RENDER direction are required.");continue;
                }
                if(endpointId.starts_with("fake-")||endpointId.starts_with("synthetic-")){writer.Response(correlation,command,"ERROR","SYNTHETIC_ENDPOINT_REJECTED","Fake endpoint identities cannot be used for real WASAPI operations.");continue;}
                const auto cached=discoveredActive.find(endpointId);
                if(cached==discoveredActive.end()) { writer.Response(correlation,command,"ERROR","ENDPOINT_NOT_DISCOVERED","Select an active endpoint from the latest explicit Windows discovery result.");continue; }
                if(cached->second!=direction) { writer.Response(correlation,command,"ERROR","ENDPOINT_DIRECTION_MISMATCH","The selected endpoint direction differs from the discovery result.");continue; }
                if(scanAuthorization&&!scanAuthorization->invalid&&!payload.HasMember("scanSessionId")){writer.Response(correlation,command,"ERROR","SCAN_SESSION_REQUIRED","Operations must use the active approved scan token until that session is closed.");continue;}
                if(service.Busy()){writer.Response(correlation,command,"ERROR","BUSY","A fake session is active in this worker.");continue;}
                if(realOperationBusy.exchange(true)) { writer.Response(correlation,command,"ERROR","BUSY","Another real diagnostic operation is running in this worker.");continue; }
                std::vector<ExclusiveFormatMatrix::Format> formats;
                ExclusiveFormatMatrix::Period period{};
                std::string consentId;
                std::shared_ptr<ScanAuthorization> operationScanSession;
                if(command=="QueryFormatMatrix") {
                    try { formats=ReadCandidates(payload); }
                    catch(const Rejection& ex){realOperationBusy=false;writer.Response(correlation,command,"ERROR",ex.code,ex.what());continue;}
                    if(payload.HasMember("scanSessionId")||payload.HasMember("scanOperationId"))try{AuthorizeScanOperation(payload,scanAuthorization,discoveredActive,discoveryGeneration,command,formats,"",0,"",endpointId,direction);operationScanSession=scanAuthorization;}catch(const Rejection& ex){realOperationBusy=false;writer.Response(correlation,command,"ERROR",ex.code,ex.what());continue;}
                } else {
                    try {
                        period=ExclusiveFormatMatrix::Period::Default;
                        const auto format=ReadOneFormat(payload,period);formats.push_back(format);
                        if(payload.HasMember("scanSessionId")||payload.HasMember("scanOperationId")){AuthorizeScanOperation(payload,scanAuthorization,discoveredActive,discoveryGeneration,command,formats,period==ExclusiveFormatMatrix::Period::Default?"DEFAULT":"MINIMUM",0,"",endpointId,direction);operationScanSession=scanAuthorization;}
                        else {
                        const auto receipt=payload.FindMember("consentReceipt");
                        std::string fingerprint;bool accepted=false;
                        if(receipt!=payload.MemberEnd()&&receipt->value.IsObject()){
                            if(const auto acceptedValue=receipt->value.FindMember("accepted");acceptedValue!=receipt->value.MemberEnd()&&acceptedValue->value.IsBool())accepted=acceptedValue->value.GetBool();
                            ReadString(receipt->value,"confirmationId",consentId);
                            ReadString(receipt->value,"requestFingerprint",fingerprint);
                        }
                        const auto canonical=endpointId+"|"+direction+"|"+std::to_string(format.sampleRate)+"|"+
                            std::to_string(format.channels)+"|"+EncodingText(format.encoding)+"|"+PeriodText(period);
                        if(!accepted)throw Rejection("CONSENT_NOT_ACCEPTED","A fresh accepted UUID consent receipt is required.");
                        if(consentId.empty())throw Rejection("CONSENT_ID_MISSING","A consent confirmation UUID is required.");
                        const auto canonicalConsentId=GuidString(consentId);
                        if(fingerprint!=Sha256(canonical))throw Rejection("SETTINGS_FINGERPRINT_MISMATCH","Consent fingerprint does not match the exact endpoint, direction, format, and period.");
                        if(consumedInitReceipts.size()>=kMaximumConsumedRealConsentReceipts)throw Rejection("CONSENT_CAPACITY","Worker has reached its bounded Initialize consent history; restart the worker before another attempt.");
                        if(!consumedInitReceipts.emplace(canonicalConsentId).second)throw Rejection("CONSENT_REPLAY","Consent UUID has already been consumed.");
                        }
                    } catch(const Rejection& ex){realOperationBusy=false;writer.Response(correlation,command,"ERROR",ex.code,ex.what());continue;}
                }
                if(realOperation.joinable())realOperation.join();
                const auto operationCommand=command;
                realOperation=std::thread([&,endpointId=std::move(endpointId),direction=std::move(direction),formats=std::move(formats),period,correlation,operationCommand,operationScanSession]() mutable {
                    ScanOperationFinish scanFinish{operationScanSession};
#if defined(PROBE_TEST_WASAPI_BACKEND)
                    try {
                        InjectedWasapiApi api;
                        const auto dir=direction=="CAPTURE"?ExclusiveFormatMatrix::Direction::Capture:ExclusiveFormatMatrix::Direction::Render;
                        ExclusiveFormatMatrix::Report report;
                        if(operationCommand=="QueryFormatMatrix")report=ExclusiveFormatMatrix::RunCandidates(dir,ExclusiveFormatMatrix::Level::PassiveQuery,api,formats);
                        else report={dir,ExclusiveFormatMatrix::Level::InitializeOnly,{ExclusiveFormatMatrix::RunSingle(dir,formats.front(),period,api,true)}};
                        const auto result=SerializeRealMatrix(endpointId,direction,report);
                        if(result.size()>kMaximumFrame-1024)writer.Response(correlation,operationCommand,"ERROR","RESPONSE_TOO_LARGE","Injected test result exceeds the IPC frame limit.");
                        else {const bool safe=!operationScanSession||ScanReportSafe(result);scanFinish.Finish(safe);if(!writer.Response(correlation,operationCommand,"OK","","",result))scanFinish.Finish(false);}
                    } catch(const std::exception&) { writer.Response(correlation,operationCommand,"ERROR","INJECTED_BACKEND_FAILED","The injected test backend did not produce a complete response."); }
                    realOperationBusy=false;return;
#else
                    HRESULT com=CoInitializeEx(nullptr,COINIT_MULTITHREADED);
                    if(FAILED(com)) { writer.Response(correlation,operationCommand,"ERROR","COM_INITIALIZE_FAILED","Dedicated operation thread could not initialize COM.","{\"stage\":\"COM_INITIALIZE\",\"hresult\":"+ExclusiveFormatMatrix::HresultJson(static_cast<std::int32_t>(com))+"}");realOperationBusy=false;return; }
                    IMMDevice* device=nullptr;
                    try {
                        const auto validation=RevalidateEndpoint(endpointId,direction,&device);
                        if(!validation.empty()) { CoUninitialize();writer.Response(correlation,operationCommand,"ERROR",validation,"Endpoint was not active with the requested direction immediately before the operation.");realOperationBusy=false;return; }
                        ExclusiveFormatMatrix::WasapiApi api(device);
                        const auto dir=direction=="CAPTURE"?ExclusiveFormatMatrix::Direction::Capture:ExclusiveFormatMatrix::Direction::Render;
                        ExclusiveFormatMatrix::Report report;
                        if(operationCommand=="QueryFormatMatrix")report=ExclusiveFormatMatrix::RunCandidates(dir,ExclusiveFormatMatrix::Level::PassiveQuery,api,formats);
                        else report={dir,ExclusiveFormatMatrix::Level::InitializeOnly,{ExclusiveFormatMatrix::RunSingle(dir,formats.front(),period,api,true)}};
                        const auto result=SerializeRealMatrix(endpointId,direction,report);
                        device->Release();CoUninitialize();
                        if(result.size()>kMaximumFrame-1024)writer.Response(correlation,operationCommand,"ERROR","RESPONSE_TOO_LARGE","Format result exceeds the IPC frame limit.");
                        else {const bool safe=!operationScanSession||ScanReportSafe(result);scanFinish.Finish(safe);if(!writer.Response(correlation,operationCommand,"OK","","",result))scanFinish.Finish(false);}
                    } catch(const std::exception&) { if(device)device->Release();CoUninitialize();writer.Response(correlation,operationCommand,"ERROR","NATIVE_OPERATION_FAILED","The real WASAPI format operation failed before a complete result was produced."); }
                    realOperationBusy=false;
#endif
                });
                continue;
            }
            if(command=="ValidateStream") {
                std::string operationId,endpointId,direction,period,encoding,mmcss,consentId,fingerprint;
                int sampleRate{},channels{},duration{};
                if(!ReadString(payload,"operationId",operationId)||!ReadString(payload,"endpointId",endpointId)||endpointId.empty()||endpointId.size()>32768||
                   !ReadString(payload,"direction",direction)||(direction!="CAPTURE"&&direction!="RENDER")||
                   !ReadInt(payload,"sampleRate",sampleRate)||sampleRate!=48000||
                   !ReadInt(payload,"channels",channels)||channels!=2||
                   !ReadString(payload,"encoding",encoding)||encoding!="PCM16"||
                   !ReadString(payload,"period",period)||(period!="DEFAULT"&&period!="MINIMUM")||
                   !ReadInt(payload,"durationSeconds",duration)||duration<1||duration>10||
                   !ReadString(payload,"mmcss",mmcss)||(mmcss!="OFF"&&mmcss!="PRO_AUDIO")) {
                    writer.Response(correlation,command,"ERROR","INVALID_ARGUMENT","Real stream validation requires exact endpoint, direction, 48 kHz stereo PCM16, period, MMCSS and duration 1..10.");continue;
                }
                try { operationId=GuidString(operationId); } catch(const Rejection& ex){writer.Response(correlation,command,"ERROR",ex.code,ex.what());continue;}
#if defined(PROBE_TEST_WASAPI_BACKEND)
                std::string scenario="CLEAN";
                if(payload.HasMember("testScenario")) {
                    if(!payload["testScenario"].IsString()){writer.Response(correlation,command,"ERROR","INVALID_ARGUMENT","Test scenario must be a string.");continue;}
                    scenario.assign(payload["testScenario"].GetString(),payload["testScenario"].GetStringLength());
                }
#else
                if(payload.HasMember("testScenario")){writer.Response(correlation,command,"ERROR","INVALID_ARGUMENT","Test-only stream scenarios are unavailable in the real worker.");continue;}
#endif
                if(endpointId.starts_with("fake-")||endpointId.starts_with("synthetic-")){writer.Response(correlation,command,"ERROR","SYNTHETIC_ENDPOINT_REJECTED","Synthetic endpoint identities cannot be used for real WASAPI stream validation.");continue;}
                const auto cached=discoveredActive.find(endpointId);
                if(cached==discoveredActive.end()){writer.Response(correlation,command,"ERROR","ENDPOINT_NOT_DISCOVERED","The exact endpoint must be selected by the latest explicit Windows discovery.");continue;}
                if(cached->second!=direction){writer.Response(correlation,command,"ERROR","ENDPOINT_DIRECTION_MISMATCH","The exact endpoint direction differs from the discovery result.");continue;}
                if(scanAuthorization&&!scanAuthorization->invalid&&!payload.HasMember("scanSessionId")){writer.Response(correlation,command,"ERROR","SCAN_SESSION_REQUIRED","Operations must use the active approved scan token until that session is closed.");continue;}
                const bool scanRequested=payload.HasMember("scanSessionId")||payload.HasMember("scanOperationId");
                if(!scanRequested){
                    const auto receipt=payload.FindMember("consentReceipt");bool accepted=false;
                    if(receipt!=payload.MemberEnd()&&receipt->value.IsObject()){if(receipt->value.HasMember("accepted")&&receipt->value["accepted"].IsBool())accepted=receipt->value["accepted"].GetBool();ReadString(receipt->value,"confirmationId",consentId);ReadString(receipt->value,"requestFingerprint",fingerprint);}
                    const auto canonical=endpointId+"|"+direction+"|48000|2|PCM16|"+period+"|"+std::to_string(duration)+"|"+mmcss+"|REAL_WASAPI_STREAM_V1";
                    if(!accepted||consentId.empty()||fingerprint!=Sha256(canonical)){writer.Response(correlation,command,"ERROR","STREAM_CONSENT_MISMATCH","Fresh consent must match the exact endpoint, format, period, duration and MMCSS setting.");continue;}
                    try { consentId=GuidString(consentId); } catch(const Rejection& ex){writer.Response(correlation,command,"ERROR",ex.code,ex.what());continue;}
                    if(consumedStreamReceipts.size()>=kMaximumConsumedRealConsentReceipts){writer.Response(correlation,command,"ERROR","CONSENT_CAPACITY","Stream consent history is bounded; restart the worker before another validation.");continue;}
                    if(!consumedStreamReceipts.emplace(consentId).second){writer.Response(correlation,command,"ERROR","CONSENT_REPLAY","This stream validation confirmation has already been used.");continue;}
                }
                if(service.Busy()||realOperationBusy.exchange(true)){writer.Response(correlation,command,"ERROR","BUSY","Another real operation is active in this worker.");continue;}
                std::shared_ptr<ScanAuthorization> operationScanSession;
                if(scanRequested)try{std::vector<ExclusiveFormatMatrix::Format> one{{48000,2,ExclusiveFormatMatrix::Encoding::Pcm16}};AuthorizeScanOperation(payload,scanAuthorization,discoveredActive,discoveryGeneration,command,one,period,duration,mmcss,endpointId,direction);operationScanSession=scanAuthorization;}catch(const Rejection& ex){realOperationBusy=false;writer.Response(correlation,command,"ERROR",ex.code,ex.what());continue;}
                if(realOperation.joinable())realOperation.join();
                streamOperationId=operationId;streamOperationCorrelation=correlation;
                realOperation=std::thread([&,command,endpointId,direction,period,duration,mmcss,correlation,operationId,operationScanSession
#if defined(PROBE_TEST_WASAPI_BACKEND)
                    ,scenario
#endif
                    ]() mutable {
                    ScanOperationFinish scanFinish{operationScanSession};
                    HANDLE cancellation=INVALID_HANDLE_VALUE;
#if defined(PROBE_TEST_WASAPI_BACKEND)
                    SECURITY_ATTRIBUTES testSecurity{sizeof(SECURITY_ATTRIBUTES),nullptr,TRUE};
                    cancellation=CreateEventW(&testSecurity,TRUE,FALSE,nullptr);
                    if(!cancellation){writer.Response(correlation,"ValidateStream","ERROR","CANCEL_EVENT_CREATE_FAILED","Could not create injected cancellation event.");realOperationBusy=false;return;}
                    {std::lock_guard lock(streamCancelMutex);streamCancelEvent=cancellation;}
                    if(scenario=="WAIT_FOR_CANCEL"&&WaitForSingleObject(cancellation,(duration+5)*1000)==WAIT_OBJECT_0)scenario="CANCELLED";
                    const auto expected=static_cast<std::uint64_t>(48000)*static_cast<std::uint64_t>(duration);
                    auto frames=expected;auto disc=0;auto timestamps=0;bool started=true;bool stopOk=true;bool reached=true;bool watchdog=false;bool invalidated=false;std::string quality="CLEAN",cleanup="STOP_SUCCEEDED;RELEASE_ATTEMPTED",startHr="0x00000000",stopHr="0x00000000";
                    if(scenario=="START_FAIL"){started=false;frames=0;reached=false;quality="INCOMPLETE";startHr="0x8889000A";stopOk=false;cleanup="STOP_NOT_ATTEMPTED;RELEASE_ATTEMPTED";}
                    else if(scenario=="ZERO_FRAMES"){frames=0;quality="INCOMPLETE";}
                    else if(scenario=="LOW_COVERAGE"){frames=expected/5;quality="ANOMALIES_PRESENT";}
                    else if(scenario=="DISCONTINUITY"){disc=2;quality="ANOMALIES_PRESENT";}
                    else if(scenario=="TIMEOUT"){frames=expected/2;reached=false;watchdog=true;quality="ANOMALIES_PRESENT";}
                    else if(scenario=="CANCELLED"){frames=expected/4;reached=false;quality="INCOMPLETE";}
                    else if(scenario=="DEVICE_LOSS"){frames=expected/3;reached=false;invalidated=true;quality="ANOMALIES_PRESENT";}
                    else if(scenario=="CLEANUP_UNKNOWN"){stopOk=false;stopHr="UNKNOWN";cleanup="UNKNOWN";quality="ANOMALIES_PRESENT";}
                    const auto coverage=expected?100.0*static_cast<double>(frames)/static_cast<double>(expected):0.0;
                    std::string reportId=NewGuid();
                    if(scenario=="INVALID_REPORT_ID")reportId="invalid-report-id";
                    if(scenario=="UPPERCASE_REPORT_ID")reportId="A0000000-0000-4000-8000-000000000001";
                    const std::string reportIdField=scenario=="MISSING_REPORT_ID"?"":"\"reportId\":"+JsonString(reportId)+",";
                    const std::string reportDirection=scenario=="MISMATCH_REPORT_DIRECTION"?(direction=="CAPTURE"?"RENDER":"CAPTURE"):direction;
                    const std::string reportPeriod=scenario=="MISMATCH_PERIOD"?(period=="MINIMUM"?"DEFAULT":"MINIMUM"):period;
                    const int reportDuration=scenario=="MISMATCH_DURATION"?duration+1:duration;
                    const int reportRate=scenario=="MISMATCH_SAMPLE_RATE"?44100:48000;
                    const std::string reportEncoding=scenario=="MISMATCH_ENCODING"?"PCM24_IN_32":"PCM16";
                    const int reportChannels=scenario=="MISMATCH_CHANNELS"?1:2;
                    const std::string reportProvenance=scenario=="MISMATCH_PROVENANCE"?"REAL_WASAPI_STREAM_VALIDATION":"TEST_FAKE_STREAM_BACKEND";
                    std::string report="{\"schemaVersion\":1,\"reportType\":\"wasapi-stream-validation\","+reportIdField+"\"timestampUtc\":\"2026-10-31T00:00:00.000Z\",\"provenance\":\""+reportProvenance+"\",\"probeVersion\":\"test\",\"direction\":\""+reportDirection+"\",\"format\":{\"sampleRateHz\":"+std::to_string(reportRate)+",\"encoding\":\""+reportEncoding+"\",\"channels\":"+std::to_string(reportChannels)+"},\"period\":\""+reportPeriod+"\",\"durationSeconds\":"+std::to_string(reportDuration)+",\"activationHresult\":\"0x00000000\",\"devicePeriodHresult\":\"0x00000000\",\"formatSupported\":true,\"initializeSucceeded\":true,\"firstInitializeHresult\":\"0x00000000\",\"finalInitializeHresult\":\"0x00000000\",\"requestedPeriod100ns\":30000,\"actualBufferFrames\":144,\"actualBufferPeriod100ns\":30000,\"actualBufferFramesHresult\":\"0x00000000\",\"eventCreateHresult\":\"0x00000000\",\"setEventHandleHresult\":\"0x00000000\",\"eventReady\":true,\"startSucceeded\":"+(started?"true":"false")+",\"startHresult\":\""+startHr+"\",\"events\":"+std::to_string((frames+143)/144)+",\"packets\":"+std::to_string((frames+143)/144)+",\"framesProcessed\":"+std::to_string(frames)+",\"expectedFrames\":"+std::to_string(expected)+",\"frameCoveragePercent\":"+std::to_string(coverage)+",\"elapsedMilliseconds\":"+std::to_string(reached?duration*1000:duration*250)+",\"durationReached\":"+(reached?"true":"false")+",\"discontinuities\":"+std::to_string(disc)+",\"timestampErrors\":"+std::to_string(timestamps)+",\"unavailableCaptureBuffers\":0,\"bufferServiceFaults\":0,\"eventWaitTimeouts\":0,\"watchdogTimedOut\":"+(watchdog?"true":"false")+",\"cancelled\":"+(scenario=="CANCELLED"?"true":"false")+",\"deviceInvalidated\":"+(invalidated?"true":"false")+",\"streamQuality\":\""+quality+"\",\"stopSucceeded\":"+(scenario=="CLEANUP_UNKNOWN"?"null":stopOk?"true":"false")+",\"stopHresult\":\""+stopHr+"\",\"cleanup\":\""+cleanup+"\",\"errorStage\":\"NONE\",\"errorHresult\":\"UNKNOWN\",\"physicalPlayback\":\"NOT_MEASURED\",\"physicalRouting\":\"UNKNOWN\"}";
                    const std::string responseOperationId=scenario=="MISMATCH_OPERATION_ID"?NewGuid():operationId;
                    const std::string responseEndpointId=scenario=="MISMATCH_ENDPOINT"?"injected-other-endpoint":endpointId;
                    const std::string responseDirection=scenario=="MISMATCH_RESPONSE_DIRECTION"?(direction=="CAPTURE"?"RENDER":"CAPTURE"):direction;
                    const auto response="{\"operationId\":"+JsonString(responseOperationId)+",\"endpointId\":"+JsonString(responseEndpointId)+",\"direction\":"+JsonString(responseDirection)+",\"report\":"+report+"}";scanFinish.Finish(!operationScanSession||ScanReportSafe(response));if(!writer.Response(correlation,command,"OK","","",response))scanFinish.Finish(false);
                    {std::lock_guard lock(streamCancelMutex);streamCancelEvent=nullptr;}
                    CloseHandle(cancellation);
#else
                    const HRESULT com=CoInitializeEx(nullptr,COINIT_MULTITHREADED);
                    if(FAILED(com)){writer.Response(correlation,"ValidateStream","ERROR","COM_INITIALIZE_FAILED","Stream operation thread could not initialize COM.","{\"hresult\":"+ExclusiveFormatMatrix::HresultJson(static_cast<std::int32_t>(com))+"}");realOperationBusy=false;return;}
                    IMMDevice* device=nullptr;const auto validation=RevalidateEndpoint(endpointId,direction,&device);
                    if(device)device->Release();CoUninitialize();
                    if(!validation.empty()){writer.Response(correlation,"ValidateStream","ERROR",validation,"Endpoint failed exact active-state/direction revalidation immediately before stream operation.");realOperationBusy=false;return;}
                    SECURITY_ATTRIBUTES security{sizeof(SECURITY_ATTRIBUTES),nullptr,TRUE};
                    cancellation=CreateEventW(&security,TRUE,FALSE,nullptr);
                    if(!cancellation){writer.Response(correlation,"ValidateStream","ERROR","CANCEL_EVENT_CREATE_FAILED","Could not create a cancellation event.");realOperationBusy=false;return;}
                    {std::lock_guard lock(streamCancelMutex);streamCancelEvent=cancellation;}
                    std::string failure,failureField;auto report=RunStreamCli(endpointId,direction,period,static_cast<unsigned>(duration),mmcss,cancellation,failure,failureField);
                    {std::lock_guard lock(streamCancelMutex);streamCancelEvent=nullptr;}
                    CloseHandle(cancellation);
                    if(!report){const auto message=failureField.empty()?"The native stream subprocess did not return complete structured evidence; operation outcome may be unknown.":"The native stream report contract failed at field "+failureField+"; operation outcome may be unknown.";writer.Response(correlation,"ValidateStream","ERROR",failure,message);realOperationBusy=false;return;}
                    const auto response="{\"operationId\":\""+operationId+"\",\"endpointId\":"+JsonString(endpointId)+",\"direction\":\""+direction+"\",\"report\":"+*report+"}";scanFinish.Finish(!operationScanSession||ScanReportSafe(response));if(!writer.Response(correlation,"ValidateStream","OK","","",response))scanFinish.Finish(false);
#endif
                    realOperationBusy=false;
                });
                continue;
            }
            if(command=="ValidateDuplex"||command=="ValidateSustainedDuplex") {
                const bool sustained=command=="ValidateSustainedDuplex";
                std::string operationId,captureId,renderId,period,mmcss;
                int sampleRate=0,channels=0,duration=0;
                const auto isCustomPeriodText = [&period] { return period.size() > 7 && period.compare(0, 7, "CUSTOM:") == 0; };
                if(!ReadString(payload,"operationId",operationId)||!ReadString(payload,"captureEndpointId",captureId)||
                   !ReadString(payload,"renderEndpointId",renderId)||!ReadString(payload,"period",period)||
                   !ReadString(payload,"mmcss",mmcss)||!ReadInt(payload,"sampleRate",sampleRate)||
                   !ReadInt(payload,"channels",channels)||!ReadInt(payload,"durationSeconds",duration)||
                   sampleRate!=48000||channels!=2||duration<1||(sustained?(duration!=60&&duration!=300&&duration!=600):duration>10)||
                   (period!="MINIMUM"&&period!="DEFAULT"&&!isCustomPeriodText())||(mmcss!="OFF"&&mmcss!="PRO_AUDIO")||
                   captureId.empty()||renderId.empty()||captureId==renderId||captureId.size()>32768||renderId.size()>32768) {
                    writer.Response(correlation,command,"ERROR","INVALID_ARGUMENT","Duplex validation requires an explicit distinct endpoint pair and bounded 48 kHz stereo PCM16 settings.");continue;
                }
                const auto customPeriod = ParseCustomDuplexPeriod(period);
                if (period.rfind("CUSTOM:", 0) == 0 && !customPeriod) { writer.Response(correlation,command,"ERROR","INVALID_ARGUMENT","Custom Duplex periods must use CUSTOM:<100ns>:<frames> at 48 kHz."); continue; }
                try { operationId=GuidString(operationId); }
                catch(const Rejection& ex){writer.Response(correlation,command,"ERROR",ex.code,ex.what());continue;}
                if(captureId.starts_with("fake-")||captureId.starts_with("synthetic-")||renderId.starts_with("fake-")||renderId.starts_with("synthetic-")) {
                    writer.Response(correlation,command,"ERROR","SYNTHETIC_ENDPOINT_REJECTED","Fake endpoints cannot be used for real Duplex validation.");continue;
                }
                const auto captureEntry=discoveredActive.find(captureId),renderEntry=discoveredActive.find(renderId);
                if(captureEntry==discoveredActive.end()||renderEntry==discoveredActive.end()) { writer.Response(correlation,command,"ERROR","ENDPOINT_NOT_DISCOVERED","Both exact endpoints must come from the latest explicit Windows discovery.");continue; }
                if(captureEntry->second!="CAPTURE"||renderEntry->second!="RENDER") { writer.Response(correlation,command,"ERROR","ENDPOINT_DIRECTION_MISMATCH","The selected endpoint pair must contain one Capture and one Render endpoint.");continue; }
                if(scanAuthorization&&!scanAuthorization->invalid&&!payload.HasMember("scanSessionId")){writer.Response(correlation,command,"ERROR","SCAN_SESSION_REQUIRED","Operations must use the active approved scan token until that session is closed.");continue;}
                const bool scanRequested=payload.HasMember("scanSessionId")||payload.HasMember("scanOperationId");
                if(!scanRequested){
                    const auto receipt=payload.FindMember("consentReceipt");bool accepted=false;std::string consentId,fingerprint;
                    if(receipt!=payload.MemberEnd()&&receipt->value.IsObject()){if(receipt->value.HasMember("accepted")&&receipt->value["accepted"].IsBool())accepted=receipt->value["accepted"].GetBool();ReadString(receipt->value,"confirmationId",consentId);ReadString(receipt->value,"requestFingerprint",fingerprint);}
                    const auto canonical=captureId+"|"+renderId+"|48000|2|PCM16|"+period+"|"+std::to_string(duration)+"|"+mmcss+"|REAL_WASAPI_DUPLEX_V1";
                    if(!accepted||consentId.empty()||fingerprint!=Sha256(canonical)){writer.Response(correlation,command,"ERROR","DUPLEX_CONSENT_MISMATCH","Fresh consent must match the exact endpoint pair and immutable Duplex settings.");continue;}
                    try { consentId=GuidString(consentId); } catch(const Rejection& ex){writer.Response(correlation,command,"ERROR",ex.code,ex.what());continue;}
                    if(consumedDuplexReceipts.size()>=kMaximumConsumedRealConsentReceipts){writer.Response(correlation,command,"ERROR","CONSENT_CAPACITY","Duplex consent history is bounded; restart the worker before another validation.");continue;}
                    if(!consumedDuplexReceipts.emplace(consentId).second){writer.Response(correlation,command,"ERROR","CONSENT_REPLAY","This Duplex confirmation has already been used.");continue;}
                }
                if(service.Busy()||realOperationBusy.exchange(true)){writer.Response(correlation,command,"ERROR","BUSY","Another real diagnostic operation is active in this worker.");continue;}
                std::shared_ptr<ScanAuthorization> operationScanSession;
                if(scanRequested)try{std::vector<ExclusiveFormatMatrix::Format> one{{48000,2,ExclusiveFormatMatrix::Encoding::Pcm16}};AuthorizeScanOperation(payload,scanAuthorization,discoveredActive,discoveryGeneration,command,one,period,duration,mmcss,"","",captureId,renderId);operationScanSession=scanAuthorization;}catch(const Rejection& ex){realOperationBusy=false;writer.Response(correlation,command,"ERROR",ex.code,ex.what());continue;}
                if(realOperation.joinable())realOperation.join();
                duplexOperationId=operationId;duplexOperationCorrelation=correlation;
                if(pendingDuplexCancelId!=operationId)pendingDuplexCancelId.clear();
#if defined(PROBE_TEST_WASAPI_BACKEND)
                std::string scenario="CLEAN";
                if(payload.HasMember("testScenario")) {
                    if(!payload["testScenario"].IsString()){writer.Response(correlation,command,"ERROR","INVALID_ARGUMENT","Test-only Duplex scenario must be a string.");realOperationBusy=false;continue;}
                    scenario.assign(payload["testScenario"].GetString(),payload["testScenario"].GetStringLength());
                    if(scenario!="CLEAN"&&scenario!="BENIGN_INTERMEDIATE_WAIT"&&scenario!="SERVICE_FAILURE"&&scenario!="WATCHDOG"&&scenario!="LONG_EVENT_INTERVAL"&&scenario!="PACKET_DRAIN_LIMIT"&&scenario!="CAPTURE_FAIL"&&scenario!="RENDER_FAIL"&&scenario!="INSUFFICIENT_FRAMES"&&scenario!="NO_OVERLAP"&&scenario!="CANCELLED"&&scenario!="CLEANUP_UNKNOWN"&&scenario!="WAIT_FOR_CANCEL"&&scenario!="DELAY_RESPONSE"&&scenario!="NO_RESPONSE") { writer.Response(correlation,command,"ERROR","INVALID_ARGUMENT","Unknown test-only Duplex scenario.");realOperationBusy=false;continue; }
                }
#else
                if(payload.HasMember("testScenario")){writer.Response(correlation,command,"ERROR","INVALID_ARGUMENT","Test-only Duplex scenarios are unavailable in the production worker.");realOperationBusy=false;continue;}
#endif
                SECURITY_ATTRIBUTES cancellationSecurity{sizeof(SECURITY_ATTRIBUTES),nullptr,TRUE};
                HANDLE cancellation=CreateEventW(&cancellationSecurity,TRUE,FALSE,nullptr);
                if(!cancellation){writer.Response(correlation,command,"ERROR","CANCEL_EVENT_CREATE_FAILED","Could not create Duplex cancellation event.");realOperationBusy=false;continue;}
                {std::lock_guard lock(streamCancelMutex);duplexCancelEvent=cancellation;}
                if(pendingDuplexCancelId==operationId){SetEvent(cancellation);pendingDuplexCancelId.clear();}
                realOperation=std::thread([&,command,operationId,captureId,renderId,period,mmcss,duration,correlation,cancellation,operationScanSession,sustained
#if defined(PROBE_TEST_WASAPI_BACKEND)
                    ,scenario
#endif
                    ]() mutable {
                    ScanOperationFinish scanFinish{operationScanSession};
                    const auto finishCancellation=[&](){std::lock_guard lock(streamCancelMutex);if(duplexCancelEvent==cancellation)duplexCancelEvent=nullptr;CloseHandle(cancellation);};
#if defined(PROBE_TEST_WASAPI_BACKEND)
                    DuplexValidationReport::Report fake;fake.schemaVersion=sustained?1:2;fake.reportType=sustained?"wasapi-sustained-duplex-validation":"wasapi-duplex-validation";fake.provenance=sustained?"TEST_FAKE_WASAPI_SUSTAINED_DUPLEX":"TEST_FAKE_WASAPI_DUPLEX";fake.operationId=operationId;fake.reportId=NewGuid();fake.timestampUtc="2026-10-31T00:00:00.000Z";fake.probeVersion="test";fake.captureEndpointId=captureId;fake.renderEndpointId=renderId;fake.period=period;fake.mmcss=mmcss;fake.durationSeconds=duration;fake.bothReady=true;fake.startAuthorized=true;fake.status="DUPLEX_CLEAN";fake.startDifferenceMilliseconds=0.025;fake.simultaneousRuntimeMilliseconds=duration*1000.0;
                    const auto expected=static_cast<std::uint64_t>(48000)*duration;
                    auto fill=[&](DuplexValidationReport::Side& side,const std::string& id,const char* direction){side.endpointId=id;side.direction=direction;side.bufferFrames=144;side.framesProcessed=expected;side.expectedFrames=expected;side.events=1666;side.packets=1666;side.eventIntervalsObserved=1665;side.frameCoveragePercent=100.0;side.elapsedMilliseconds=duration*1000.0;side.ready=true;side.startSucceeded=true;side.stopSucceeded=true;side.durationReached=true;side.quality="CLEAN";side.cleanup="RELEASE_ATTEMPTED";side.startOffsetMilliseconds=0.0;};
                    fill(fake.capture,captureId,"CAPTURE");fill(fake.render,renderId,"RENDER");
                    fake.capture.devicePositionAnomalies=0;fake.capture.unavailableCaptureBuffers=0;fake.render.silentBuffersServiced=1666;
                    if(scenario=="BENIGN_INTERMEDIATE_WAIT"){fake.capture.intermediateWaitTimeouts=1;fake.render.intermediateWaitTimeouts=1;}
                    else if(scenario=="SERVICE_FAILURE"){fake.capture.serviceFaults=1;fake.status="DUPLEX_WARNINGS";}
                    else if(scenario=="WATCHDOG"){fake.capture.watchdogTimedOut=true;fake.capture.durationReached=false;fake.capture.quality="ANOMALIES_PRESENT";fake.status="DUPLEX_WARNINGS";}
                    else if(scenario=="LONG_EVENT_INTERVAL"){fake.render.longEventIntervals=1;fake.render.quality="ANOMALIES_PRESENT";fake.status="DUPLEX_WARNINGS";}
                    else if(scenario=="PACKET_DRAIN_LIMIT"){fake.capture.packetDrainLimitHits=1;fake.capture.quality="ANOMALIES_PRESENT";fake.status="DUPLEX_WARNINGS";}
                    if(scenario=="WAIT_FOR_CANCEL") {
                        const auto wait=WaitForSingleObject(cancellation,8000);
                        if(wait!=WAIT_OBJECT_0) {
                            fake.status="INCOMPLETE";fake.capture.startSucceeded=false;fake.render.startSucceeded=false;
                            fake.capture.stopSucceeded=false;fake.render.stopSucceeded=false;
                            fake.capture.cleanup="UNKNOWN";fake.render.cleanup="UNKNOWN";
                        } else {
                            fake.cancelled=true;fake.status="CANCELLED";fake.capture.startSucceeded=false;fake.render.startSucceeded=false;
                            fake.capture.stopSucceeded=true;fake.render.stopSucceeded=true;
                            fake.capture.cleanup="RELEASE_ATTEMPTED";fake.render.cleanup="RELEASE_ATTEMPTED";
                            fake.capture.framesProcessed=0;fake.render.framesProcessed=0;
                            fake.capture.quality="UNKNOWN";fake.render.quality="UNKNOWN";
                        }
                    }
                    if(scenario=="CAPTURE_FAIL"){fake.capture.startSucceeded=false;fake.capture.stopSucceeded=false;fake.capture.quality="SEVERE_ANOMALIES";fake.capture.framesProcessed=0;fake.capture.cleanup="UNKNOWN";fake.status="FAILED";}
                    else if(scenario=="RENDER_FAIL"){fake.render.startSucceeded=false;fake.render.stopSucceeded=false;fake.render.quality="SEVERE_ANOMALIES";fake.render.framesProcessed=0;fake.render.cleanup="UNKNOWN";fake.status="FAILED";}
                    else if(scenario=="INSUFFICIENT_FRAMES"){fake.capture.framesProcessed=expected/4;fake.capture.quality="ANOMALIES_PRESENT";fake.status="DUPLEX_WARNINGS";}
                    else if(scenario=="NO_OVERLAP"){fake.simultaneousRuntimeMilliseconds=0.0;fake.status="INCOMPLETE";}
                    else if(scenario=="CANCELLED"){fake.cancelled=true;fake.status="CANCELLED";fake.capture.cleanup="UNKNOWN";fake.render.cleanup="UNKNOWN";}
                    else if(scenario=="CLEANUP_UNKNOWN"){fake.capture.cleanup="UNKNOWN";fake.status="DUPLEX_WARNINGS";}
                    if(scenario=="NO_RESPONSE") { Sleep(8000);finishCancellation();realOperationBusy=false;return; }
                    if(scenario=="DELAY_RESPONSE") Sleep(5500);
                    const auto report=DuplexValidationReport::ToJson(fake);
                    const auto response="{\"operationId\":"+JsonString(operationId)+",\"captureEndpointId\":"+JsonString(captureId)+",\"renderEndpointId\":"+JsonString(renderId)+",\"report\":"+report+"}";scanFinish.Finish(!operationScanSession||ScanReportSafe(response));if(!writer.Response(correlation,command,"OK","","",response))scanFinish.Finish(false);
                    finishCancellation();
#else
                    const HRESULT com=CoInitializeEx(nullptr,COINIT_MULTITHREADED);
                    if(FAILED(com)){writer.Response(correlation,command,"ERROR","COM_INITIALIZE_FAILED","Duplex operation thread could not initialize COM.","{\"hresult\":"+ExclusiveFormatMatrix::HresultJson(static_cast<std::int32_t>(com))+"}");finishCancellation();realOperationBusy=false;return;}
                    IMMDevice* device=nullptr;auto validation=RevalidateEndpoint(captureId,"CAPTURE",&device);if(device)device->Release();
                    if(validation.empty()){validation=RevalidateEndpoint(renderId,"RENDER",&device);if(device)device->Release();}
                    CoUninitialize();
                    if(!validation.empty()){writer.Response(correlation,command,"ERROR",validation,"Exact endpoint pair failed active-state/direction revalidation before operation.");finishCancellation();realOperationBusy=false;return;}
                    std::string failure, failureDetail;auto report=RunDuplexCli(operationId,captureId,renderId,period,static_cast<unsigned>(duration),mmcss,cancellation,failure,failureDetail,sustained);
                    finishCancellation();
                    if(!report){writer.Response(correlation,command,"ERROR",failure,"Structured Duplex evidence unavailable; operation or cleanup outcome may be unknown. "+failureDetail);realOperationBusy=false;return;}
                    const auto response="{\"operationId\":"+JsonString(operationId)+",\"captureEndpointId\":"+JsonString(captureId)+",\"renderEndpointId\":"+JsonString(renderId)+",\"report\":"+*report+"}";scanFinish.Finish(!operationScanSession||ScanReportSafe(response));if(!writer.Response(correlation,command,"OK","","",response))scanFinish.Finish(false);
#endif
                    realOperationBusy=false;
                });
                continue;
            }
            if(command=="CancelDuplexValidation") {
                std::string operationId;if(!ReadString(payload,"operationId",operationId)){writer.Response(correlation,command,"ERROR","INVALID_ARGUMENT","A valid Duplex operationId is required.");continue;}
                try{operationId=GuidString(operationId);}catch(const Rejection& ex){writer.Response(correlation,command,"ERROR",ex.code,ex.what());continue;}
                if(operationId!=duplexOperationId){
                    if(realOperationBusy.load()){writer.Response(correlation,command,"ERROR","NO_ACTIVE_DUPLEX_VALIDATION","Another real operation is active; this Duplex cancellation does not match it.");continue;}
                    pendingDuplexCancelId=operationId;
                    writer.Response(correlation,command,"OK","","","{\"accepted\":true,\"operationId\":"+JsonString(operationId)+",\"completion\":\"PENDING\"}");continue;
                }
                std::lock_guard lock(streamCancelMutex);
                if(duplexCancelEvent==nullptr){writer.Response(correlation,command,"ERROR","DUPLEX_ALREADY_TERMINAL","The operation no longer accepts cancellation; await its result.");continue;}
                if(!SetEvent(duplexCancelEvent)){writer.Response(correlation,command,"ERROR","CANCELLATION_SIGNAL_FAILED","Could not signal Duplex cancellation.");continue;}
                writer.Response(correlation,command,"OK","","","{\"accepted\":true,\"operationId\":"+JsonString(operationId)+",\"completion\":\"PENDING\"}");continue;
            }
            if(command=="CancelStreamValidation") {
                std::string operationId;if(!ReadString(payload,"operationId",operationId)||GuidString(operationId)!=streamOperationId){writer.Response(correlation,command,"ERROR","NO_ACTIVE_STREAM_VALIDATION","No matching real stream operation is active.");continue;}
                std::lock_guard lock(streamCancelMutex);
                if(streamCancelEvent==nullptr){writer.Response(correlation,command,"ERROR","STREAM_ALREADY_TERMINAL","The operation no longer accepts cancellation; await its result.");continue;}
                if(!SetEvent(streamCancelEvent)){writer.Response(correlation,command,"ERROR","CANCELLATION_SIGNAL_FAILED","Could not signal the native stream cancellation event.");continue;}
                writer.Response(correlation,command,"OK","","","{\"accepted\":true,\"operationId\":\""+operationId+"\",\"completion\":\"PENDING\"}");continue;
            }
            if(command=="StartCaptureMeter"||command=="StartRenderChannelTest") {
                if(realOperationBusy.load()){writer.Response(correlation,command,"ERROR","BUSY","A real format query or Initialize-only operation is running in this worker.");continue;}
                try {
                    const auto session=service.Accept(payload,correlation);
                    const auto ok=writer.Response(correlation,command,"OK","","","{\"acceptance\":\"ACCEPTED\",\"operationState\":\"ACCEPTED\",\"settings\":"+session->settings.Json()+"}","\""+session->id+"\"");
                    if(!ok)return false;service.Start(session);
                } catch(const Rejection& ex){writer.Response(correlation,command,"ERROR",ex.code,ex.what());}
                continue;
            }
            if(command=="StopSession") {
                std::string sessionId;if(!ReadString(payload,"sessionId",sessionId)){writer.Response(correlation,"StopSession","ERROR","INVALID_ARGUMENT","sessionId is required");continue;}
                sessionId=GuidString(sessionId);const auto result=service.Stop(sessionId);
                if(result=="ACCEPTED")writer.Response(correlation,"StopSession","OK","","","{\"acceptance\":\"ACCEPTED\"}","\""+sessionId+"\"");
                else writer.Response(correlation,"StopSession","ERROR",result=="ALREADY_STOPPING"?"ALREADY_STOPPING":"NO_SESSION",result=="ALREADY_STOPPING"?"Stop already accepted.":"No matching active session.");
                continue;
            }
            if(command=="Shutdown") {
                if(service.Busy()||realOperationBusy.load()){writer.Response(correlation,"Shutdown","ERROR","BUSY","Cannot shut down while an operation is active.");continue;}
                writer.Response(correlation,"Shutdown","OK","","","{\"shutdown\":\"IDLE\"}");service.Join();if(realOperation.joinable())realOperation.join();return true;
            }
            static const std::array<std::string_view,4> known{"InspectEndpoint","QueryFormatMatrix","ValidateFormatMatrix","ExportReport"};
            const bool isKnown=std::find(known.begin(),known.end(),command)!=known.end();
            writer.Response(correlation,command,"ERROR",isKnown?"NOT_IMPLEMENTED":"UNKNOWN_COMMAND",isKnown?"Command is unavailable in this slice.":"Unknown command.");
        } catch(const Rejection& ex) { if(!correlation.empty())writer.Response(correlation,command.empty()?"UnknownCommand":command,"ERROR",ex.code,ex.what());else{writer.Error("REQUEST_ENVELOPE",ex.code,ex.what());return false;} }
          catch(const ProtocolFailure& ex) { if(!correlation.empty()){writer.Response(correlation,command.empty()?"UnknownCommand":command,"ERROR","INVALID_REQUEST",ex.what());continue;}writer.Error("REQUEST_FRAME","PROTOCOL_ERROR",ex.what());return false; }
          catch(const std::exception& ex) { if(!correlation.empty()){writer.Response(correlation,command.empty()?"UnknownCommand":command,"ERROR","INVALID_ARGUMENT",ex.what());continue;}writer.Error("REQUEST_DISPATCH","PROTOCOL_ERROR","request failed");return false; }
    }
    {std::lock_guard lock(streamCancelMutex);if(streamCancelEvent)SetEvent(streamCancelEvent);if(duplexCancelEvent)SetEvent(duplexCancelEvent);}
    service.Join();if(realOperation.joinable())realOperation.join();return true;
}

int SelfTest() {
    const std::vector<std::string> valid={"{}","{\"a\":[1,2]}","{\"optional\":true}"};for(auto& s:valid){std::vector<std::uint8_t>b(s.begin(),s.end());(void)ParseObject(b);}
    const std::vector<std::string> invalid={"[]","{","{\"a\":1,\"a\":2}","{\"a\":[1,]}","{\"a\":\"bad}","{\"a\": [1,] }"};for(auto&s:invalid){try{std::vector<std::uint8_t>b(s.begin(),s.end());(void)ParseObject(b);return 3;}catch(...) {}}
    std::vector<std::uint8_t> bad{0xC3,0x28};try{(void)ParseObject(bad);return 4;}catch(...){}
    std::vector<std::uint8_t> deep{'{'};for(int i=0;i<35;++i)deep.insert(deep.end(),{'\"','a','\"',':','{'});deep.insert(deep.end(),{'\"','z','\"',':','0'});for(int i=0;i<36;++i)deep.push_back('}');try{(void)ParseObject(deep);return 5;}catch(...){}
    return 0;
}
}

int wmain(int argc,wchar_t** argv) {
    if(argc==2&&std::wstring_view(argv[1])==L"--self-test")return SelfTest();
    if(argc!=3||std::wstring_view(argv[1])!=L"--pipe"||std::wstring_view(argv[2]).rfind(L"\\\\.\\pipe\\KlidanAudioProbe.",0)!=0)return 2;
    HANDLE pipe=INVALID_HANDLE_VALUE;try{const auto deadline=GetTickCount64()+5000;while(GetTickCount64()<deadline){pipe=CreateFileW(argv[2],GENERIC_READ|GENERIC_WRITE,0,nullptr,OPEN_EXISTING,FILE_FLAG_OVERLAPPED,nullptr);if(pipe!=INVALID_HANDLE_VALUE)break;if(GetLastError()!=ERROR_PIPE_BUSY&&GetLastError()!=ERROR_FILE_NOT_FOUND)return 3;WaitNamedPipeW(argv[2],100);}if(pipe==INVALID_HANDLE_VALUE)return 4;const bool ok=HandleClient(pipe);CloseHandle(pipe);return ok?0:5;}catch(const std::exception& ex){std::cerr<<"worker protocol failure: "<<ex.what()<<'\n';if(pipe!=INVALID_HANDLE_VALUE)CloseHandle(pipe);return 6;}catch(...){if(pipe!=INVALID_HANDLE_VALUE)CloseHandle(pipe);return 7;}
}
