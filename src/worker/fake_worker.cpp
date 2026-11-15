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
#include "channel_routing.h"
#include "scan_plan_v2_v4.h"
#include "scan_plan_v2_v4_transport.h"

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
#if defined(PROBE_TEST_WASAPI_BACKEND)
void SignalNamedTestEvent(const char* name) { if(!name||!*name)return;HANDLE event=OpenEventA(EVENT_MODIFY_STATE,FALSE,name);if(event){SetEvent(event);CloseHandle(event);} }
void WaitNamedTestEvent(const char* name) { if(!name||!*name)return;HANDLE event=OpenEventA(SYNCHRONIZE,FALSE,name);if(event){WaitForSingleObject(event,INFINITE);CloseHandle(event);} }
#endif

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
        if(f.channels==8){const auto* scenario=std::getenv("KLIDAN_TEST_SCAN_SCENARIO");if(!scenario||std::string_view(scenario)!="ALLOW_8CH_INITIALIZE_CLEAN"){a.eventHandleCloseHr=static_cast<std::int32_t>(0x80004005U);a.cleanup=Cleanup::Failed;}}
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
std::string WithDiscoveryGeneration(std::string_view json,std::uint64_t generation) {
    rapidjson::Document document;document.Parse(json.data(),json.size());
    if(document.HasParseError()||!document.IsObject())throw std::runtime_error("Invalid discovery JSON.");
    document.AddMember("discoveryGeneration",rapidjson::Value().SetUint64(generation),document.GetAllocator());
    rapidjson::StringBuffer buffer;rapidjson::Writer<rapidjson::StringBuffer> writer(buffer);document.Accept(writer);
    return {buffer.GetString(),buffer.GetSize()};
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
        std::string_view mmcss, HANDLE cancelEvent, std::string& failureCode, std::string& failureDetail, bool sustained = false,
        const ChannelRouting::Configuration* route = nullptr, const ScanPlanV2V4::Candidate* v4Candidate = nullptr) {
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
    const std::string expectedPeriod = v4Candidate ? "CUSTOM_V2:" + std::to_string(v4Candidate->capturePeriod100ns) + ":" + std::to_string(v4Candidate->captureFrames) + ":" + std::to_string(v4Candidate->renderPeriod100ns) + ":" + std::to_string(v4Candidate->renderFrames) : std::string(period);
    const std::wstring cliPeriod = v4Candidate ? L"default" : period == "MINIMUM" ? L"minimum" : period == "DEFAULT" ? L"default" : L"custom:" + Wide(period.substr(7));
    std::wstring command = QuoteArgument(cliPath) + L" --capture-id " + QuoteArgument(captureWide) +
        L" --render-id " + QuoteArgument(renderWide) + (route ? v4Candidate ? L" --test-duplex-v2-custom" : sustained ? L" --test-sustained-duplex-v2" : L" --test-duplex-v2" : sustained ? L" --test-sustained-duplex" : L" --test-duplex") + (route ? L"" : L" --rate 48000 --channels 2 --format pcm16");
    if(route){
        const auto rate=[](const auto& side){return std::to_wstring(side.format.sampleRate);};
        const auto channels=[](const auto& side){return std::to_wstring(side.format.channels);};
        const auto encoding=[](const auto& side){return Wide(ExclusiveFormatMatrix::EncodingText(side.format.encoding));};
        const auto mask=[](const auto& side){return std::to_wstring(side.channelMask);};
        const auto indexList=[](const auto& values,std::uint16_t count){std::wstring text;for(std::uint16_t i=0;i<count;++i){if(i)text+=L",";text+=std::to_wstring(values[i]);}return text;};
        command+=L" --route-mode "+Wide(ChannelRouting::ModeText(route->mode))+L" --capture-rate "+rate(route->capture)+L" --capture-channels "+channels(route->capture)+L" --capture-format "+encoding(route->capture)+L" --capture-mask "+mask(route->capture)+L" --capture-indices "+indexList(route->captureChannels,ChannelRouting::CaptureCount(route->mode))+L" --render-rate "+rate(route->render)+L" --render-channels "+channels(route->render)+L" --render-format "+encoding(route->render)+L" --render-mask "+mask(route->render)+L" --render-indices "+indexList(route->renderChannels,ChannelRouting::RenderCount(route->mode));
        if(v4Candidate){
            command+=L" --capture-custom-period "+QuoteArgument(L"custom:"+std::to_wstring(v4Candidate->capturePeriod100ns)+L":"+std::to_wstring(v4Candidate->captureFrames));
            command+=L" --render-custom-period "+QuoteArgument(L"custom:"+std::to_wstring(v4Candidate->renderPeriod100ns)+L":"+std::to_wstring(v4Candidate->renderFrames));
        }
    }
    command+=
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
    failureDetail = "nativeExitCode=" + std::to_string(exitCode) + ";reportCreated=" + std::string(reportCreated ? "true" : "false") + ";termination=NORMAL;argumentParsingFailure=UNKNOWN";
    if (!input) { failureCode = "DUPLEX_REPORT_MISSING"; return std::nullopt; }
    const auto length = input.tellg();
    if (length <= 0 || length > 65536) { failureCode = "DUPLEX_REPORT_INVALID_SIZE"; return std::nullopt; }
    std::string report(static_cast<std::size_t>(length), '\0'); input.seekg(0); input.read(report.data(), length);
    if (!input) { failureCode = "DUPLEX_REPORT_READ_FAILED"; return std::nullopt; }
    const std::vector<std::uint8_t> bytes(report.begin(), report.end());
    auto parsed = ParseObject(bytes);
    if(route){
        const auto equals=[](const rapidjson::Value& object,const char* key,std::string_view value){const auto it=object.FindMember(key);return it!=object.MemberEnd()&&it->value.IsString()&&std::string_view(it->value.GetString(),it->value.GetStringLength())==value;};
        const auto sideMatches=[&](const char* key,std::string_view id,std::string_view direction,const ChannelRouting::EndpointFormat& expected){
            if(!parsed.HasMember(key)||!parsed[key].IsObject())return false;const auto& side=parsed[key];
            if(!side.HasMember("format")||!side["format"].IsObject())return false;const auto& format=side["format"];
            return equals(side,"endpointId",id)&&equals(side,"direction",direction)&&format.HasMember("sampleRateHz")&&format["sampleRateHz"].IsUint()&&format["sampleRateHz"].GetUint()==expected.format.sampleRate&&format.HasMember("channels")&&format["channels"].IsUint()&&format["channels"].GetUint()==expected.format.channels&&equals(format,"encoding",ExclusiveFormatMatrix::EncodingText(expected.format.encoding))&&format.HasMember("channelMask")&&format["channelMask"].IsUint()&&format["channelMask"].GetUint()==expected.channelMask&&side.HasMember("ready")&&side["ready"].IsBool()&&side.HasMember("startSucceeded")&&side["startSucceeded"].IsBool()&&side.HasMember("stopSucceeded")&&side["stopSucceeded"].IsBool()&&side.HasMember("durationReached")&&side["durationReached"].IsBool()&&side.HasMember("cleanup")&&side["cleanup"].IsString()&&side.HasMember("actualBufferFrames")&&(side["actualBufferFrames"].IsUint64()||side["actualBufferFrames"].IsNull())&&side.HasMember("errorStage")&&side["errorStage"].IsString();
        };
        const auto routeSideMatches=[&](const char* key,const ChannelRouting::EndpointFormat& expected,const std::array<std::uint16_t,2>& indices,std::uint16_t count){const auto it=parsed["route"].FindMember(key);if(it==parsed["route"].MemberEnd()||!it->value.IsObject())return false;const auto& side=it->value;if(!side.HasMember("selectedIndicesZeroBased")||!side["selectedIndicesZeroBased"].IsArray()||side["selectedIndicesZeroBased"].Size()!=count||!side.HasMember("sampleRateHz")||!side["sampleRateHz"].IsUint()||side["sampleRateHz"].GetUint()!=expected.format.sampleRate||!side.HasMember("channels")||!side["channels"].IsUint()||side["channels"].GetUint()!=expected.format.channels||!equals(side,"encoding",ExclusiveFormatMatrix::EncodingText(expected.format.encoding))||!side.HasMember("channelMask")||!side["channelMask"].IsUint()||side["channelMask"].GetUint()!=expected.channelMask)return false;for(std::uint16_t i=0;i<count;++i)if(!side["selectedIndicesZeroBased"][i].IsUint()||side["selectedIndicesZeroBased"][i].GetUint()!=indices[i])return false;return true;};
        const auto requestedMatches=[&](){if(!parsed.HasMember("requested")||!parsed["requested"].IsObject())return false;const auto& q=parsed["requested"];return equals(q,"captureEndpointId",captureId)&&equals(q,"renderEndpointId",renderId)&&equals(q,"period",expectedPeriod)&&equals(q,"mmcss",mmcss)&&q.HasMember("durationSeconds")&&q["durationSeconds"].IsUint()&&q["durationSeconds"].GetUint()==duration;};
        const bool valid=parsed.IsObject()&&parsed.HasMember("schemaVersion")&&parsed["schemaVersion"].IsInt()&&parsed["schemaVersion"].GetInt()==3&&equals(parsed,"reportType",sustained?"wasapi-sustained-duplex-validation-v2":"wasapi-duplex-validation-v2")&&equals(parsed,"provenance",sustained?"REAL_WASAPI_SUSTAINED_DUPLEX_V2":"REAL_WASAPI_DUPLEX_V2")&&equals(parsed,"operationId",operationId)&&requestedMatches()&&parsed.HasMember("route")&&parsed["route"].IsObject()&&equals(parsed["route"],"fingerprint",ChannelRouting::Fingerprint(*route))&&equals(parsed["route"],"mode",ChannelRouting::ModeText(route->mode))&&equals(parsed["route"],"physicalMapping","UNKNOWN")&&equals(parsed["route"],"renderPolicy","SILENCE_ONLY")&&routeSideMatches("capture",route->capture,route->captureChannels,ChannelRouting::CaptureCount(route->mode))&&routeSideMatches("render",route->render,route->renderChannels,ChannelRouting::RenderCount(route->mode))&&sideMatches("capture",captureId,"CAPTURE",route->capture)&&sideMatches("render",renderId,"RENDER",route->render);
        if(!valid) { failureCode="DUPLEX_V2_REPORT_CONTRACT_INVALID";return std::nullopt; }
    } else if (!DuplexValidationReport::Validate(parsed, captureId, renderId, period, duration, mmcss, operationId,
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
enum class ScanInvalidationReason { None, Cancelled, Closed, DiscoveryRefresh, UnsafeCleanup };
struct ScanAuthorization {
    std::string id;std::unordered_map<std::string,std::string> endpoints;std::set<std::string> operations;
    std::vector<ScanAllowedFormat> formats;std::unordered_set<std::string> usedOperations;
    std::uint64_t expires{},discoveryGeneration{};int maxDuration{},maxOperations{},used{};
    std::atomic<bool> invalid{},active{};std::atomic<ScanInvalidationReason> invalidationReason{ScanInvalidationReason::None};
    bool version2{},version4{};std::string v2QaMode;std::optional<ChannelRouting::Configuration> route;std::string routeFingerprint;
    std::set<std::string> v2Periods,v2Mmcss;std::set<unsigned> sustainedDurations;
    std::uint64_t maxTotalDuration{},usedTotalDuration{};bool shortQualified{},qualificationFailed{};
    unsigned repeatabilityClean{},nextSustainedDuration{60},pendingDuration{};std::string pendingStage,pendingPeriod,pendingMmcss;
    std::vector<ScanPlanV2V4::Candidate> v4Candidates;
    std::unique_ptr<ScanPlanV2V4::TransportSequence> v4Sequence;
    unsigned v4RepeatabilityRuns{},v4InitializeTimeoutSeconds{},v4CleanupDeadlineSeconds{};
};
bool HasOnlyMembers(const rapidjson::Value& value,std::initializer_list<std::string_view> names) {if(!value.IsObject())return false;for(auto it=value.MemberBegin();it!=value.MemberEnd();++it){const std::string_view name(it->name.GetString(),it->name.GetStringLength());if(std::find(names.begin(),names.end(),name)==names.end())return false;}return true;}
ChannelRouting::Configuration ReadV2Route(const rapidjson::Value& value,std::string captureId,std::string renderId,bool strictMembers=true) {
    using namespace ChannelRouting;
    std::string mode;if((strictMembers&&!HasOnlyMembers(value,{"mode","capture","render"}))||!ReadString(value,"mode",mode))throw Rejection("ROUTE_INVALID","V2 route settings are incomplete.");
    const auto cap=value.FindMember("capture"),ren=value.FindMember("render");if(cap==value.MemberEnd()||ren==value.MemberEnd())throw Rejection("ROUTE_INVALID","Both V2 route sides are required.");
    Configuration route{};route.captureEndpointId=std::move(captureId);route.renderEndpointId=std::move(renderId);
    route.mode=mode=="MONO_TO_STEREO"?Mode::MonoToStereo:mode=="STEREO_TO_STEREO"?Mode::StereoToStereo:mode=="MONO_TO_MONO"?Mode::MonoToMono:static_cast<Mode>(255);
    const auto readSide=[](const rapidjson::Value& side,EndpointFormat& endpoint,std::array<std::uint16_t,2>& indices,std::uint16_t count){
        if(!HasOnlyMembers(side,{"sampleRateHz","channels","encoding","channelMask","selectedIndicesZeroBased"}))return false;
        const auto rate=side.FindMember("sampleRateHz"),channels=side.FindMember("channels");std::string encoding;
        if(rate==side.MemberEnd()||!rate->value.IsUint()||(rate->value.GetUint()!=44100&&rate->value.GetUint()!=48000)||channels==side.MemberEnd()||!channels->value.IsUint()||channels->value.GetUint()<1||channels->value.GetUint()>32||!ReadString(side,"encoding",encoding)||!side.HasMember("channelMask")||!side["channelMask"].IsUint()||!side.HasMember("selectedIndicesZeroBased")||!side["selectedIndicesZeroBased"].IsArray()||side["selectedIndicesZeroBased"].Size()!=count)return false;
        const auto encodingValue=encoding=="PCM16"?ExclusiveFormatMatrix::Encoding::Pcm16:encoding=="PCM24_IN_32"?ExclusiveFormatMatrix::Encoding::Pcm24In32:encoding=="FLOAT32"?ExclusiveFormatMatrix::Encoding::Float32:static_cast<ExclusiveFormatMatrix::Encoding>(255);
        endpoint.format={rate->value.GetUint(),static_cast<std::uint16_t>(channels->value.GetUint()),encodingValue};endpoint.channelMask=side["channelMask"].GetUint();
        for(std::uint16_t i=0;i<count;++i){const auto& item=side["selectedIndicesZeroBased"][i];if(!item.IsUint()||item.GetUint()>=channels->value.GetUint())return false;indices[i]=static_cast<std::uint16_t>(item.GetUint());}
        return true;
    };
    if(!readSide(cap->value,route.capture,route.captureChannels,CaptureCount(route.mode))||!readSide(ren->value,route.render,route.renderChannels,RenderCount(route.mode))||!Validate(route))throw Rejection("ROUTE_INVALID","V2 route formats, masks, or selected channel indices are invalid.");
    return route;
}
std::string ScanPlanV2Fingerprint(const ChannelRouting::Configuration& route,std::uint64_t generation,std::string_view qaMode,int lifetime,int maxDuration,int maxOperations,std::uint64_t maxTotal,const std::set<std::string>& periods,const std::set<std::string>& mmcss,const std::set<unsigned>& durations) {
    std::string canonical="SCAN_PLAN_V2|3|"+std::to_string(generation)+"|"+std::string(qaMode)+"|"+std::to_string(lifetime)+"|"+std::to_string(maxDuration)+"|"+std::to_string(maxOperations)+"|"+std::to_string(maxTotal)+"|"+ChannelRouting::Fingerprint(route)+"\n";
    for(const auto& period:periods)canonical+="P|"+period+"\n";for(const auto& mode:mmcss)canonical+="M|"+mode+"\n";for(auto duration:durations)canonical+="D|"+std::to_string(duration)+"\n";
    return Sha256(canonical);
}
std::shared_ptr<ScanAuthorization> ParseScanAuthorizationV2(const rapidjson::Value& payload,const std::unordered_map<std::string,std::string>& discovered,std::uint64_t generation,std::unordered_set<std::string>& consumedConfirmations) {
    auto session=std::make_shared<ScanAuthorization>();int version{},lifetime{},maxDuration{},maxOperations{};std::uint64_t maxTotal{};std::string qaMode,captureId,renderId,routeFingerprint,planFingerprint,acceptedId;bool accepted=false;
    if(!HasOnlyMembers(payload,{"contractVersion","qaMode","captureEndpointId","renderEndpointId","route","routeFingerprint","periods","mmcssModes","sustainedDurationsSeconds","lifetimeSeconds","maximumOperationDurationSeconds","maximumOperationCount","maximumTotalDurationSeconds","planFingerprint","authorization"})||!ReadInt(payload,"contractVersion",version)||version!=3||!ReadString(payload,"qaMode",qaMode)||(qaMode!="SHORT"&&qaMode!="AUTO_SHORT"&&qaMode!="FULL")||!ReadString(payload,"captureEndpointId",captureId)||!ReadString(payload,"renderEndpointId",renderId)||captureId.empty()||renderId.empty()||captureId==renderId||!ReadString(payload,"routeFingerprint",routeFingerprint)||!ReadString(payload,"planFingerprint",planFingerprint)||!ReadInt(payload,"lifetimeSeconds",lifetime)||!ReadInt(payload,"maximumOperationDurationSeconds",maxDuration)||!ReadInt(payload,"maximumOperationCount",maxOperations)||!payload.HasMember("maximumTotalDurationSeconds")||!payload["maximumTotalDurationSeconds"].IsUint64()||!payload.HasMember("authorization")||!HasOnlyMembers(payload["authorization"],{"accepted","confirmationId"}))throw Rejection("INVALID_SCAN_PLAN","V2 scan plan is incomplete or outside its authorized resource bounds.");
    maxTotal=payload["maximumTotalDurationSeconds"].GetUint64();if((qaMode=="SHORT"&&(lifetime!=120||maxDuration!=5||maxOperations!=3||maxTotal!=5))||(qaMode=="AUTO_SHORT"&&(lifetime!=180||maxDuration!=5||maxOperations!=6||maxTotal!=10))||(qaMode=="FULL"&&(lifetime!=1200||maxDuration!=600||maxOperations!=12||maxTotal!=995)))throw Rejection("INVALID_SCAN_PLAN","V2 resource budget does not match the selected QA depth.");
    session->route=ReadV2Route(payload["route"],captureId,renderId);if(ChannelRouting::Fingerprint(*session->route)!=routeFingerprint)throw Rejection("SCAN_FINGERPRINT_MISMATCH","V2 route fingerprint does not match the exact route configuration.");
    if(discovered.find(captureId)==discovered.end()||discovered.at(captureId)!="CAPTURE"||discovered.find(renderId)==discovered.end()||discovered.at(renderId)!="RENDER")throw Rejection("SCAN_ENDPOINT_INVALID","V2 scan endpoints must match the current exact Capture and Render discovery identities.");
    if(!payload.HasMember("periods")||!payload["periods"].IsArray()||payload["periods"].Empty()||payload["periods"].Size()>2||!payload.HasMember("mmcssModes")||!payload["mmcssModes"].IsArray()||payload["mmcssModes"].Empty()||payload["mmcssModes"].Size()>2||!payload.HasMember("sustainedDurationsSeconds")||!payload["sustainedDurationsSeconds"].IsArray()||payload["sustainedDurationsSeconds"].Size()!=(qaMode=="FULL"?3U:0U))throw Rejection("INVALID_SCAN_PLAN","V2 periods, MMCSS modes, and sustained qualification scope do not match the QA mode.");
    for(const auto& item:payload["periods"].GetArray()){if(!item.IsString()||(std::string_view(item.GetString(),item.GetStringLength())!="DEFAULT"&&std::string_view(item.GetString(),item.GetStringLength())!="MINIMUM")||!session->v2Periods.emplace(item.GetString(),item.GetStringLength()).second)throw Rejection("INVALID_SCAN_PLAN","V2 period scope is invalid or duplicated.");}
    for(const auto& item:payload["mmcssModes"].GetArray()){if(!item.IsString()||(std::string_view(item.GetString(),item.GetStringLength())!="OFF"&&std::string_view(item.GetString(),item.GetStringLength())!="PRO_AUDIO")||!session->v2Mmcss.emplace(item.GetString(),item.GetStringLength()).second)throw Rejection("INVALID_SCAN_PLAN","V2 MMCSS scope is invalid or duplicated.");}
    if(qaMode=="AUTO_SHORT"&&(session->v2Mmcss.size()!=1||!session->v2Mmcss.contains("OFF")))throw Rejection("INVALID_SCAN_PLAN","AUTO_SHORT authorizes only MMCSS OFF.");
    const unsigned expectedDurations[]{60,300,600};for(std::size_t i=0;i<payload["sustainedDurationsSeconds"].Size();++i){const auto& item=payload["sustainedDurationsSeconds"][static_cast<rapidjson::SizeType>(i)];if(!item.IsUint()||item.GetUint()!=expectedDurations[i])throw Rejection("INVALID_SCAN_PLAN","V2 sustained ladder must be exactly 60, 300, and 600 seconds.");session->sustainedDurations.insert(item.GetUint());}
    const auto& authorization=payload["authorization"];if(authorization.HasMember("accepted")&&authorization["accepted"].IsBool())accepted=authorization["accepted"].GetBool();if(!accepted||!ReadString(authorization,"confirmationId",acceptedId))throw Rejection("SCAN_CONSENT_REQUIRED","Explicit operator acceptance is required for V2 scan authorization.");
    acceptedId=GuidString(acceptedId);if(consumedConfirmations.size()>=128)throw Rejection("SCAN_CONSENT_CAPACITY","Worker scan authorization history is bounded; restart the Worker before another session.");if(consumedConfirmations.contains(acceptedId))throw Rejection("SCAN_CONSENT_REPLAY","This scan authorization has already been used.");
    const auto canonical=ScanPlanV2Fingerprint(*session->route,generation,qaMode,lifetime,maxDuration,maxOperations,maxTotal,session->v2Periods,session->v2Mmcss,session->sustainedDurations);if(canonical!=planFingerprint)throw Rejection("SCAN_FINGERPRINT_MISMATCH","Worker-computed immutable V2 plan fingerprint does not match.");
    consumedConfirmations.insert(acceptedId);session->version2=true;session->v2QaMode=qaMode;session->routeFingerprint=routeFingerprint;session->endpoints.emplace(captureId,"CAPTURE");session->endpoints.emplace(renderId,"RENDER");session->operations={"ValidateFormatMatrix","ValidateDuplexV2"};if(qaMode=="FULL")session->operations.insert("ValidateSustainedDuplexV2");
    for(const auto* side:{&session->route->capture,&session->route->render}){ScanAllowedFormat format;format.rate=static_cast<int>(side->format.sampleRate);format.channels=side->format.channels;format.encoding=ExclusiveFormatMatrix::EncodingText(side->format.encoding);format.periods=session->v2Periods;format.mmcss=session->v2Mmcss;session->formats.push_back(std::move(format));}
    session->maxDuration=maxDuration;session->maxOperations=maxOperations;session->maxTotalDuration=maxTotal;session->discoveryGeneration=generation;session->expires=GetTickCount64()+static_cast<std::uint64_t>(lifetime)*1000;
#if defined(PROBE_TEST_WASAPI_BACKEND)
    if(const auto* scenario=std::getenv("KLIDAN_TEST_SCAN_SCENARIO")){if(std::string_view(scenario)=="EXPIRE_ON_REGISTER")session->expires=GetTickCount64();else if(std::string_view(scenario)=="GENERATION_MISMATCH")++session->discoveryGeneration;}
#endif
    return session;
}
std::shared_ptr<ScanAuthorization> ParseScanAuthorizationV2V4(const rapidjson::Value& payload,const std::unordered_map<std::string,std::string>& discovered,std::uint64_t generation,std::unordered_set<std::string>& consumedConfirmations) {
    int version{}, repeatability{}, maxOperations{}, maxActive{}, initTimeout{}, lifetime{}, cleanup{};
    std::string captureId, renderId, routeFingerprint, planFingerprint, confirmation; bool accepted=false;
    if(!HasOnlyMembers(payload,{"contractVersion","captureEndpointId","renderEndpointId","route","routeFingerprint","candidates","captureBounds","renderBounds","mmcssModes","repeatabilityRuns","maximumOperationCount","maximumActiveDuplexSeconds","initializeTimeoutSeconds","sessionLifetimeSeconds","cleanupDeadlineSeconds","planFingerprint","authorization"})) throw Rejection("INVALID_SCAN_PLAN","V4 scan contract contains unexpected fields.");
    if(!ReadInt(payload,"contractVersion",version)||version!=4||!ReadString(payload,"captureEndpointId",captureId)||!ReadString(payload,"renderEndpointId",renderId)||captureId.empty()||renderId.empty()||captureId==renderId||!ReadString(payload,"routeFingerprint",routeFingerprint)||!ReadString(payload,"planFingerprint",planFingerprint)||!ReadInt(payload,"repeatabilityRuns",repeatability)||!ReadInt(payload,"maximumOperationCount",maxOperations)||!ReadInt(payload,"maximumActiveDuplexSeconds",maxActive)||!ReadInt(payload,"initializeTimeoutSeconds",initTimeout)||!ReadInt(payload,"sessionLifetimeSeconds",lifetime)||!ReadInt(payload,"cleanupDeadlineSeconds",cleanup)) throw Rejection("INVALID_SCAN_PLAN","V4 identity, fingerprint, or budget fields are malformed.");
    if(!payload.HasMember("candidates")||!payload["candidates"].IsArray()||payload["candidates"].Empty()||payload["candidates"].Size()>8) throw Rejection("INVALID_SCAN_PLAN","V4 candidate list is missing or outside its fixed bounds.");
    if(!payload.HasMember("mmcssModes")||!payload["mmcssModes"].IsArray()||payload["mmcssModes"].Size()!=1||!payload["mmcssModes"][0].IsString()||std::string_view(payload["mmcssModes"][0].GetString(),payload["mmcssModes"][0].GetStringLength())!="OFF") throw Rejection("INVALID_SCAN_PLAN","V4 MMCSS scope must be OFF.");
    if(!payload.HasMember("authorization")||!HasOnlyMembers(payload["authorization"],{"accepted","confirmationId"})||!payload["authorization"].HasMember("accepted")||!payload["authorization"].HasMember("confirmationId")) throw Rejection("INVALID_SCAN_PLAN","V4 authorization receipt is malformed.");
    auto route=ReadV2Route(payload["route"],captureId,renderId);
    if(ChannelRouting::Fingerprint(route)!=routeFingerprint||discovered.find(captureId)==discovered.end()||discovered.at(captureId)!="CAPTURE"||discovered.find(renderId)==discovered.end()||discovered.at(renderId)!="RENDER")throw Rejection("SCAN_FINGERPRINT_MISMATCH","V4 route or exact discovered endpoint identity mismatch.");
    auto readBounds=[](const rapidjson::Value& value)->ScanPlanV2V4::PeriodBounds {
        ScanPlanV2V4::PeriodBounds bounds;
        if(!HasOnlyMembers(value,{"minimumPeriod100ns","defaultPeriod100ns","alignmentFrames"})||!value.HasMember("minimumPeriod100ns")||!value["minimumPeriod100ns"].IsInt64()||!value.HasMember("defaultPeriod100ns")||!value["defaultPeriod100ns"].IsInt64()||!value.HasMember("alignmentFrames"))throw Rejection("INVALID_SCAN_PLAN","V4 device bounds are malformed.");
        bounds.minimum=value["minimumPeriod100ns"].GetInt64();bounds.defaultPeriod=value["defaultPeriod100ns"].GetInt64();
        if(value["alignmentFrames"].IsNull()){bounds.alignmentKnown=false;bounds.alignmentFrames=0;}
        else if(value["alignmentFrames"].IsUint()){bounds.alignmentKnown=true;bounds.alignmentFrames=value["alignmentFrames"].GetUint();}
        else throw Rejection("INVALID_SCAN_PLAN","V4 alignment must be reported or explicitly null.");
        ScanPlanV2V4::ValidateBounds(bounds);return bounds;
    };
    if(!payload.HasMember("captureBounds")||!payload.HasMember("renderBounds"))throw Rejection("INVALID_SCAN_PLAN","V4 Capture and Render bounds are required.");
    const auto captureBounds=readBounds(payload["captureBounds"]);const auto renderBounds=readBounds(payload["renderBounds"]);
    std::vector<ScanPlanV2V4::Candidate> candidates;
    for(const auto& item:payload["candidates"].GetArray()) {
        if(!HasOnlyMembers(item,{"index","requestedDuration100ns","captureFrames","renderFrames","capturePeriod100ns","renderPeriod100ns","captureAlignmentFrames","captureAlignmentKnown","renderAlignmentFrames","renderAlignmentKnown","captureMinimumFrames","captureMaximumFrames","renderMinimumFrames","renderMaximumFrames","rejectionReason"}))throw Rejection("INVALID_SCAN_PLAN","V4 candidate contains unexpected fields.");
        ScanPlanV2V4::Candidate c;std::uint64_t duration{};std::string reason;
        if(!item.HasMember("index")||!item["index"].IsUint()||!item["requestedDuration100ns"].IsUint64()||!item["captureFrames"].IsUint()||!item["renderFrames"].IsUint()||!item["capturePeriod100ns"].IsUint64()||!item["renderPeriod100ns"].IsUint64()||!item["captureAlignmentFrames"].IsUint()||!item["captureAlignmentKnown"].IsBool()||!item["renderAlignmentFrames"].IsUint()||!item["renderAlignmentKnown"].IsBool()||!item["captureMinimumFrames"].IsUint()||!item["captureMaximumFrames"].IsUint()||!item["renderMinimumFrames"].IsUint()||!item["renderMaximumFrames"].IsUint()||!ReadString(item,"rejectionReason",reason))throw Rejection("INVALID_SCAN_PLAN","V4 candidate geometry fields are invalid.");
        duration=item["requestedDuration100ns"].GetUint64();if(duration>static_cast<std::uint64_t>(std::numeric_limits<std::int64_t>::max()))throw Rejection("INVALID_SCAN_PLAN","V4 period exceeds signed 100 ns range.");
        c.index=item["index"].GetUint();c.duration100ns=static_cast<std::int64_t>(duration);c.captureFrames=item["captureFrames"].GetUint();c.renderFrames=item["renderFrames"].GetUint();c.capturePeriod100ns=static_cast<std::int64_t>(item["capturePeriod100ns"].GetUint64());c.renderPeriod100ns=static_cast<std::int64_t>(item["renderPeriod100ns"].GetUint64());c.captureAlignmentFrames=item["captureAlignmentFrames"].GetUint();c.captureAlignmentKnown=item["captureAlignmentKnown"].GetBool();c.renderAlignmentFrames=item["renderAlignmentFrames"].GetUint();c.renderAlignmentKnown=item["renderAlignmentKnown"].GetBool();c.captureMin=item["captureMinimumFrames"].GetUint();c.captureMax=item["captureMaximumFrames"].GetUint();c.renderMin=item["renderMinimumFrames"].GetUint();c.renderMax=item["renderMaximumFrames"].GetUint();c.rejectionReason=std::move(reason);candidates.push_back(std::move(c));
    }
    const auto budget=ScanPlanV2V4::ComputeBudget(static_cast<unsigned>(candidates.size()),repeatability);
    if(maxOperations!=static_cast<int>(budget.operations)||maxActive!=static_cast<int>(budget.activeSeconds)||lifetime!=static_cast<int>(budget.sessionSeconds)||initTimeout!=30||cleanup!=8)throw Rejection("INVALID_SCAN_PLAN","V4 operation or time budget does not match the derived contract.");
    auto canonical=ScanPlanV2V4::Canonical(captureId,renderId,route,captureBounds,renderBounds,candidates,repeatability,budget.operations,budget.activeSeconds,initTimeout,lifetime,cleanup,generation,routeFingerprint);
    if(Sha256(canonical)!=planFingerprint)throw Rejection("SCAN_FINGERPRINT_MISMATCH","Worker-computed immutable v4 plan fingerprint does not match.");
    const auto& auth=payload["authorization"];if(auth.HasMember("accepted")&&auth["accepted"].IsBool())accepted=auth["accepted"].GetBool();if(!accepted||!ReadString(auth,"confirmationId",confirmation))throw Rejection("SCAN_CONSENT_REQUIRED","Explicit fresh v4 operator acceptance is required.");
    confirmation=GuidString(confirmation);if(consumedConfirmations.size()>=128)throw Rejection("SCAN_CONSENT_CAPACITY","Worker authorization history is full; restart before another session.");if(consumedConfirmations.contains(confirmation))throw Rejection("SCAN_CONSENT_REPLAY","This authorization was already consumed.");consumedConfirmations.insert(confirmation);
    auto session=std::make_shared<ScanAuthorization>();session->version4=true;session->route=std::move(route);session->routeFingerprint=routeFingerprint;session->endpoints.emplace(captureId,"CAPTURE");session->endpoints.emplace(renderId,"RENDER");session->maxOperations=budget.operations;session->maxDuration=5;session->maxTotalDuration=budget.activeSeconds;session->discoveryGeneration=generation;session->expires=GetTickCount64()+static_cast<std::uint64_t>(lifetime)*1000;session->v4Candidates=candidates;session->v4RepeatabilityRuns=static_cast<unsigned>(repeatability);session->v4InitializeTimeoutSeconds=static_cast<unsigned>(initTimeout);session->v4CleanupDeadlineSeconds=static_cast<unsigned>(cleanup);session->v4Sequence=std::make_unique<ScanPlanV2V4::TransportSequence>(std::move(candidates),budget.operations,budget.activeSeconds);return session;
}
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
    consumedConfirmations.insert(acceptedId);session->maxDuration=duration;session->maxOperations=count;session->expires=GetTickCount64()+static_cast<std::uint64_t>(lifetime)*1000;session->discoveryGeneration=generation;
#if defined(PROBE_TEST_WASAPI_BACKEND)
    if(const auto* scenario=std::getenv("KLIDAN_TEST_SCAN_SCENARIO")){if(std::string_view(scenario)=="EXPIRE_ON_REGISTER")session->expires=GetTickCount64();else if(std::string_view(scenario)=="GENERATION_MISMATCH")++session->discoveryGeneration;}
#endif
    return session;
}
std::string AuthorizeScanOperation(const rapidjson::Value& payload,const std::shared_ptr<ScanAuthorization>& session,const std::unordered_map<std::string,std::string>& discovered,std::uint64_t generation,std::string_view command,const std::vector<ExclusiveFormatMatrix::Format>& formats,std::string_view period,int duration,std::string_view mmcss,std::string_view endpointId,std::string_view direction,std::string_view captureId={},std::string_view renderId={}) {
    const bool hasSession=payload.HasMember("scanSessionId"),hasOperation=payload.HasMember("scanOperationId");if(!hasSession&&!hasOperation)return {};if(!hasSession||!hasOperation||!session)throw Rejection("SCAN_SESSION_INVALID","Both scan session and operation identity are required.");
    std::string sessionId,scanOperationId;if(!ReadString(payload,"scanSessionId",sessionId)||!ReadString(payload,"scanOperationId",scanOperationId)||GuidString(sessionId)!=session->id)throw Rejection("SCAN_SESSION_INVALID","Scan session token is not valid for this Worker connection.");scanOperationId=GuidString(scanOperationId);
    if(payload.HasMember("operationId")&&(!payload["operationId"].IsString()||GuidString(std::string_view(payload["operationId"].GetString(),payload["operationId"].GetStringLength()))!=scanOperationId))throw Rejection("SCAN_OPERATION_ID_MISMATCH","Scan operation identity differs from the operation report identity.");
    if(session->invalid){const auto reason=session->invalidationReason.load();const char* message=reason==ScanInvalidationReason::Cancelled?"Scan authorization was cancelled.":reason==ScanInvalidationReason::Closed?"Scan authorization was closed.":reason==ScanInvalidationReason::DiscoveryRefresh?"Scan authorization was invalidated by endpoint discovery refresh.":"Scan authorization was invalidated after unsafe operation cleanup.";throw Rejection("SCAN_SESSION_STALE",message);}
    if(GetTickCount64()>=session->expires)throw Rejection("SCAN_SESSION_STALE","Scan authorization expired.");
    if(generation!=session->discoveryGeneration)throw Rejection("SCAN_SESSION_STALE","Scan authorization discovery generation changed.");
    if(!session->operations.contains(std::string(command))||session->active||session->used>=session->maxOperations||session->usedOperations.contains(scanOperationId))throw Rejection("SCAN_OPERATION_REJECTED","Operation is out of scope, replayed, concurrent, or beyond the approved count.");
    auto endpointAllowed=[&](std::string_view id,std::string_view dir){auto allowed=session->endpoints.find(std::string(id));auto current=discovered.find(std::string(id));return allowed!=session->endpoints.end()&&allowed->second==dir&&current!=discovered.end()&&current->second==dir;};
    if(command=="ValidateDuplex"||command=="ValidateSustainedDuplex"){if(!endpointAllowed(captureId,"CAPTURE")||!endpointAllowed(renderId,"RENDER"))throw Rejection("SCAN_ENDPOINT_OUT_OF_SCOPE","Duplex endpoint pair is outside the approved session.");}
    else if(!endpointAllowed(endpointId,direction))throw Rejection("SCAN_ENDPOINT_OUT_OF_SCOPE","Operation endpoint is outside the approved session.");
    if(duration>session->maxDuration)throw Rejection("SCAN_DURATION_EXCEEDED","Operation duration exceeds the approved scan-session limit.");
    for(const auto& candidate:formats){const auto encoding=ExclusiveFormatMatrix::EncodingText(candidate.encoding);const bool found=std::any_of(session->formats.begin(),session->formats.end(),[&](const auto& item){return item.rate==static_cast<int>(candidate.sampleRate)&&item.channels==candidate.channels&&item.encoding==encoding&&(command=="QueryFormatMatrix"||item.periods.contains(std::string(period)))&&(command=="QueryFormatMatrix"||command=="ValidateFormatMatrix"||item.mmcss.contains(std::string(mmcss)));});if(!found)throw Rejection("SCAN_FORMAT_OUT_OF_SCOPE","Format, period, or MMCSS setting is outside the approved scan plan.");}
    if(session->version2&&session->v2QaMode=="AUTO_SHORT"&&command=="ValidateFormatMatrix"){
        std::vector<std::string> periods(session->v2Periods.begin(),session->v2Periods.end());std::sort(periods.begin(),periods.end(),[](const auto& a,const auto& b){return a=="DEFAULT"&&b=="MINIMUM";});
        const unsigned candidate=session->used/3,step=session->used%3;const auto expectedDirection=step==0?"CAPTURE":"RENDER";
        if(candidate>=periods.size()||period!=periods[candidate]||step>1||direction!=expectedDirection||endpointId!=(step==0?session->route->captureEndpointId:session->route->renderEndpointId))throw Rejection("SCAN_OPERATION_REJECTED","AUTO_SHORT Initialize must follow the consented DEFAULT-then-MINIMUM Capture/Render sequence.");
    }
    session->usedOperations.insert(scanOperationId);++session->used;session->active=true;return scanOperationId;
}
std::string AuthorizeV2ScanOperation(const rapidjson::Value& payload,const std::shared_ptr<ScanAuthorization>& session,const std::unordered_map<std::string,std::string>& discovered,std::uint64_t generation,std::string_view command,const ChannelRouting::Configuration& route,std::string_view period,int duration,std::string_view mmcss) {
    std::string sessionId,scanOperationId;if(!session||!session->version2||!payload.HasMember("scanSessionId")||!payload.HasMember("scanOperationId")||!ReadString(payload,"scanSessionId",sessionId)||!ReadString(payload,"scanOperationId",scanOperationId)||GuidString(sessionId)!=session->id)throw Rejection("SCAN_SESSION_INVALID","A matching V2 scan-session token and operation identity are required.");
    scanOperationId=GuidString(scanOperationId);if(!ReadString(payload,"operationId",sessionId)||GuidString(sessionId)!=scanOperationId)throw Rejection("SCAN_OPERATION_ID_MISMATCH","V2 scan operation identity differs from its report identity.");
    if(session->invalid){const auto reason=session->invalidationReason.load();const char* message=reason==ScanInvalidationReason::Cancelled?"Scan authorization was cancelled.":reason==ScanInvalidationReason::DiscoveryRefresh?"Scan authorization was invalidated by discovery refresh.":"V2 scan authorization is invalid or cleanup was unsafe.";throw Rejection("SCAN_SESSION_STALE",message);}
    if(GetTickCount64()>=session->expires||generation!=session->discoveryGeneration)throw Rejection("SCAN_SESSION_STALE","V2 scan authorization expired or its discovery generation changed.");
    const auto& expected=*session->route;const auto sameSide=[](const auto& a,const auto& b,std::uint16_t count,const auto& ai,const auto& bi){if(a.format.sampleRate!=b.format.sampleRate||a.format.channels!=b.format.channels||a.format.encoding!=b.format.encoding||a.channelMask!=b.channelMask)return false;for(std::uint16_t i=0;i<count;++i)if(ai[i]!=bi[i])return false;return true;};
    if(route.captureEndpointId!=expected.captureEndpointId||route.renderEndpointId!=expected.renderEndpointId||route.mode!=expected.mode||!sameSide(route.capture,expected.capture,ChannelRouting::CaptureCount(route.mode),route.captureChannels,expected.captureChannels)||!sameSide(route.render,expected.render,ChannelRouting::RenderCount(route.mode),route.renderChannels,expected.renderChannels)||ChannelRouting::Fingerprint(route)!=session->routeFingerprint)throw Rejection("SCAN_ROUTE_MISMATCH","V2 route settings differ from the immutable authorized route.");
    if(session->qualificationFailed)throw Rejection("SCAN_QUALIFICATION_FAILED","A prior V2 stage did not produce clean, safe evidence.");
    if(!session->operations.contains(std::string(command))||session->active||session->used>=session->maxOperations||session->usedOperations.contains(scanOperationId))throw Rejection("SCAN_OPERATION_REJECTED","V2 operation is out of scope, replayed, concurrent, or beyond the authorized count.");
    if(!session->endpoints.contains(route.captureEndpointId)||!session->endpoints.contains(route.renderEndpointId)||discovered.find(route.captureEndpointId)==discovered.end()||discovered.at(route.captureEndpointId)!="CAPTURE"||discovered.find(route.renderEndpointId)==discovered.end()||discovered.at(route.renderEndpointId)!="RENDER")throw Rejection("SCAN_ENDPOINT_OUT_OF_SCOPE","V2 operation endpoints are not in the authorized current discovery.");
    if(duration<1||duration>session->maxDuration||!session->v2Periods.contains(std::string(period))||!session->v2Mmcss.contains(std::string(mmcss))||session->usedTotalDuration+static_cast<unsigned>(duration)>session->maxTotalDuration)throw Rejection("SCAN_DURATION_EXCEEDED","V2 duration, period, MMCSS, or total active-time budget is outside authorization.");
    if(command=="ValidateDuplexV2"){
        if(duration>10)throw Rejection("SCAN_DURATION_EXCEEDED","V2 short Duplex is limited to ten seconds.");
        if(session->v2QaMode=="SHORT"&&(duration!=5||session->used!=2||session->shortQualified))throw Rejection("SCAN_OPERATION_REJECTED","Short QA authorizes exactly one five-second Duplex after Capture and Render Initialize.");
        if(session->v2QaMode=="AUTO_SHORT") {std::vector<std::string> periods(session->v2Periods.begin(),session->v2Periods.end());std::sort(periods.begin(),periods.end(),[](const auto& a,const auto& b){return a=="DEFAULT"&&b=="MINIMUM";});const auto candidate=session->used/3;if(duration!=5||session->used%3!=2||candidate>=periods.size()||period!=periods[candidate]||mmcss!="OFF")throw Rejection("SCAN_OPERATION_REJECTED","AUTO_SHORT authorizes at most one five-second OFF Duplex after each candidate's two Initializes, in DEFAULT-then-MINIMUM order.");session->pendingStage="AUTO_SHORT";}
        else if(!session->shortQualified)session->pendingStage="SHORT";
        else if(session->repeatabilityClean<6)session->pendingStage="REPEATABILITY";
        else throw Rejection("SCAN_OPERATION_REJECTED","The authorized V2 short and six-run repeatability stages are already complete.");
    }else{
        if(session->v2QaMode!="FULL")throw Rejection("SCAN_OPERATION_REJECTED","Sustained validation is outside the authorized QA depth.");
        if(!session->shortQualified||session->repeatabilityClean!=6)throw Rejection("SCAN_QUALIFICATION_REQUIRED","V2 sustained validation requires a clean short Duplex and six clean repeatability operations.");
        if(!session->sustainedDurations.contains(static_cast<unsigned>(duration))||static_cast<unsigned>(duration)!=session->nextSustainedDuration)throw Rejection("SCAN_SUSTAINED_ORDER","V2 sustained durations must qualify in the authorized 60, 300, 600 second order.");
        session->pendingStage="SUSTAINED";
    }
    session->pendingDuration=static_cast<unsigned>(duration);session->pendingPeriod=period;session->pendingMmcss=mmcss;session->usedOperations.insert(scanOperationId);++session->used;session->usedTotalDuration+=static_cast<unsigned>(duration);session->active=true;return scanOperationId;
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
                    if((name=="deviceInvalidated"||name=="watchdogTimedOut"||name=="cancelled")&&it->value.IsBool()&&it->value.GetBool())safe=false;
                    self(self,it->value);
                }
            } else if(value.IsArray())for(const auto& item:value.GetArray())self(self,item);
        };
        visit(visit,root);return safe;
    } catch(...) { return false; }
}
bool CompleteV2Qualification(const std::shared_ptr<ScanAuthorization>& session,std::string_view command,std::string_view payload) {
    if(!session||!session->version2)return true;bool clean=false;
    try{
        const std::vector<std::uint8_t> bytes(payload.begin(),payload.end());const auto root=ParseObject(bytes);
        if(!root.HasMember("operationId")||!root["operationId"].IsString()||!root.HasMember("report")||!root["report"].IsObject())throw std::runtime_error("missing report");
        const auto& report=root["report"];const auto equals=[](const rapidjson::Value& object,const char* name,std::string_view expected){const auto it=object.FindMember(name);return it!=object.MemberEnd()&&it->value.IsString()&&std::string_view(it->value.GetString(),it->value.GetStringLength())==expected;};
        const auto requested=report.HasMember("requested")&&report["requested"].IsObject()?&report["requested"]:nullptr;const auto duration=requested&&requested->HasMember("durationSeconds")&&(*requested)["durationSeconds"].IsUint()?(*requested)["durationSeconds"].GetUint():0U;
        const auto expectedType=command=="ValidateSustainedDuplexV2"?"wasapi-sustained-duplex-validation-v2":"wasapi-duplex-validation-v2";
#if defined(PROBE_TEST_WASAPI_BACKEND)
        const auto expectedProvenance=command=="ValidateSustainedDuplexV2"?"TEST_FAKE_WASAPI_SUSTAINED_DUPLEX_V2":"TEST_FAKE_WASAPI_DUPLEX_V2";
#else
        const auto expectedProvenance=command=="ValidateSustainedDuplexV2"?"REAL_WASAPI_SUSTAINED_DUPLEX_V2":"REAL_WASAPI_DUPLEX_V2";
#endif
        const auto& route=*session->route;
        if(!report.HasMember("route")||!report["route"].IsObject())throw std::runtime_error("missing route evidence");
        const bool cleanStatus=report.HasMember("duplex")&&report["duplex"].IsObject()&&equals(report["duplex"],"status","DUPLEX_CLEAN");
        clean=requested&&root.HasMember("scanSessionId")&&equals(root,"scanSessionId",session->id)&&equals(root,"captureEndpointId",route.captureEndpointId)&&equals(root,"renderEndpointId",route.renderEndpointId)&&equals(report,"reportType",expectedType)&&equals(report,"provenance",expectedProvenance)&&report.HasMember("schemaVersion")&&report["schemaVersion"].IsInt()&&report["schemaVersion"].GetInt()==3&&equals(report,"operationId",std::string_view(root["operationId"].GetString(),root["operationId"].GetStringLength()))&&cleanStatus&&equals(*requested,"captureEndpointId",route.captureEndpointId)&&equals(*requested,"renderEndpointId",route.renderEndpointId)&&equals(*requested,"period",session->pendingPeriod)&&equals(*requested,"mmcss",session->pendingMmcss)&&duration==session->pendingDuration&&report.HasMember("route")&&report["route"].IsObject()&&equals(report["route"],"fingerprint",session->routeFingerprint)&&equals(report["route"],"mode",ChannelRouting::ModeText(route.mode))&&equals(report["route"],"renderPolicy","SILENCE_ONLY")&&equals(report["route"],"physicalMapping","UNKNOWN");
        for(const auto* sideName:{"capture","render"}){if(!report.HasMember(sideName)||!report[sideName].IsObject()){clean=false;break;}const bool capture=std::string_view(sideName)=="capture";const auto& expectedSide=capture?route.capture:route.render;const auto count=capture?ChannelRouting::CaptureCount(route.mode):ChannelRouting::RenderCount(route.mode);const auto& indices=capture?route.captureChannels:route.renderChannels;const auto& side=report[sideName];const auto routeSideIt=report["route"].FindMember(sideName);const rapidjson::Value* routeSide=routeSideIt==report["route"].MemberEnd()?nullptr:&routeSideIt->value;bool selected=routeSide&&routeSide->IsObject()&&routeSide->HasMember("selectedIndicesZeroBased")&&(*routeSide)["selectedIndicesZeroBased"].IsArray()&&(*routeSide)["selectedIndicesZeroBased"].Size()==count;for(std::uint16_t i=0;selected&&i<count;++i)selected=(*routeSide)["selectedIndicesZeroBased"][i].IsUint()&&(*routeSide)["selectedIndicesZeroBased"][i].GetUint()==indices[i];const auto direction=capture?"CAPTURE":"RENDER";const auto encoding=ExclusiveFormatMatrix::EncodingText(expectedSide.format.encoding);const bool format=routeSide&&routeSide->IsObject()&&routeSide->HasMember("sampleRateHz")&&(*routeSide)["sampleRateHz"].IsUint()&&(*routeSide)["sampleRateHz"].GetUint()==expectedSide.format.sampleRate&&routeSide->HasMember("channels")&&(*routeSide)["channels"].IsUint()&&(*routeSide)["channels"].GetUint()==expectedSide.format.channels&&routeSide->HasMember("encoding")&&(*routeSide)["encoding"].IsString()&&std::string_view((*routeSide)["encoding"].GetString())==encoding&&routeSide->HasMember("channelMask")&&(*routeSide)["channelMask"].IsUint()&&(*routeSide)["channelMask"].GetUint()==expectedSide.channelMask;if(!selected||!format||!equals(side,"endpointId",capture?route.captureEndpointId:route.renderEndpointId)||!equals(side,"direction",direction)||!equals(side,"quality","CLEAN")||!equals(side,"cleanup","RELEASE_ATTEMPTED")||!side.HasMember("startSucceeded")||!side["startSucceeded"].IsBool()||!side["startSucceeded"].GetBool()||!side.HasMember("stopSucceeded")||!side["stopSucceeded"].IsBool()||!side["stopSucceeded"].GetBool()||!side.HasMember("durationReached")||!side["durationReached"].IsBool()||!side["durationReached"].GetBool()||!side.HasMember("actualBufferFrames")||!side["actualBufferFrames"].IsUint64()||side["actualBufferFrames"].GetUint64()==0){clean=false;break;}}
    }catch(...){clean=false;}
    if(!clean){
        if(session->pendingStage=="AUTO_SHORT") {
            bool safelyCompleted=false;
            try {const std::vector<std::uint8_t> rejectedBytes(payload.begin(),payload.end());const auto rejectedRoot=ParseObject(rejectedBytes);const auto& report=rejectedRoot["report"];const auto& duplex=report["duplex"];const auto status=duplex.HasMember("status")&&duplex["status"].IsString()?std::string_view(duplex["status"].GetString(),duplex["status"].GetStringLength()):std::string_view{};const auto released=[](const rapidjson::Value& side){return side.HasMember("cleanup")&&side["cleanup"].IsString()&&std::string_view(side["cleanup"].GetString(),side["cleanup"].GetStringLength())=="RELEASE_ATTEMPTED";};const auto noRisk=[](const rapidjson::Value& side){for(const auto* field:{"deviceInvalidated","watchdogTimedOut","cancelled"})if(side.HasMember(field)&&side[field].IsBool()&&side[field].GetBool())return false;return true;};safelyCompleted=(status=="DUPLEX_WARNINGS"||status=="FAILED")&&report.HasMember("capture")&&report.HasMember("render")&&report["capture"].IsObject()&&report["render"].IsObject()&&released(report["capture"])&&released(report["render"])&&noRisk(report["capture"])&&noRisk(report["render"]);}catch(...){}
            if(!safelyCompleted)session->qualificationFailed=true;
        } else session->qualificationFailed=true;
        session->pendingStage.clear();session->pendingDuration=0;return false;
    }
    if(session->pendingStage=="SHORT")session->shortQualified=true;else if(session->pendingStage=="AUTO_SHORT"){}else if(session->pendingStage=="REPEATABILITY")++session->repeatabilityClean;else if(session->pendingStage=="SUSTAINED")session->nextSustainedDuration=session->pendingDuration==60?300:session->pendingDuration==300?600:0;else{session->qualificationFailed=true;return false;}
    session->pendingStage.clear();session->pendingDuration=0;return true;
}
struct ScanOperationFinish { std::shared_ptr<ScanAuthorization> session;bool accepted{},finished{};void Finish(bool reusable){if(finished){if(!reusable&&session){accepted=false;session->invalidationReason=ScanInvalidationReason::UnsafeCleanup;session->invalid=true;}return;}if(session){session->active=false;if(!reusable){session->invalidationReason=ScanInvalidationReason::UnsafeCleanup;session->invalid=true;}}accepted=reusable;finished=true;}~ScanOperationFinish(){Finish(accepted);} };

const ScanPlanV2V4::Candidate& AuthorizeV4Initialize(const rapidjson::Value& payload,
        const std::shared_ptr<ScanAuthorization>& session,
        const std::unordered_map<std::string,std::string>& discovered, std::uint64_t generation,
        ScanPlanV2V4::Step step, std::string& operationId, std::string& endpointId,
        std::string& direction, ExclusiveFormatMatrix::Format& format) {
    using namespace ScanPlanV2V4;
    if (!session || !session->version4 || !session->v4Sequence || session->invalid ||
        GetTickCount64() >= session->expires || generation != session->discoveryGeneration)
        throw Rejection("SCAN_SESSION_STALE", "V4 authorization is missing, expired, invalidated, or from an old discovery generation.");
    std::string sessionId, fingerprint;
    std::uint64_t candidateIndex{}, requestedPeriod{};
    std::uint32_t requestedFrames{};
    if (!ReadString(payload, "scanSessionId", sessionId) || GuidString(sessionId) != session->id ||
        !ReadString(payload, "operationId", operationId) || !ReadString(payload, "scanOperationId", sessionId) || GuidString(sessionId) != GuidString(operationId) ||
        !ReadString(payload, "endpointId", endpointId) || !ReadString(payload, "direction", direction) ||
        !ReadString(payload, "routeFingerprint", fingerprint) || fingerprint != session->routeFingerprint ||
        !payload.HasMember("candidateIndex") || !payload["candidateIndex"].IsUint64() ||
        !payload.HasMember("requestedPeriod100ns") || !payload["requestedPeriod100ns"].IsUint64() ||
        !payload.HasMember("requestedFrames") || !payload["requestedFrames"].IsUint())
        throw Rejection("INVALID_V4_REQUEST", "V4 Initialize request identity or exact geometry is malformed.");
    candidateIndex = payload["candidateIndex"].GetUint64(); requestedPeriod = payload["requestedPeriod100ns"].GetUint64(); requestedFrames = payload["requestedFrames"].GetUint();
    if (candidateIndex >= session->v4Candidates.size()) throw Rejection("V4_CANDIDATE_OUT_OF_SCOPE", "Candidate index is outside the registered v4 plan.");
    const auto& candidate = session->v4Candidates[static_cast<std::size_t>(candidateIndex)];
    if (!candidate.rejectionReason.empty()) throw Rejection("V4_CANDIDATE_REJECTED", "Rejected candidate geometry cannot be initialized.");
    const bool capture = step == Step::CaptureInitialize;
    const auto& side = capture ? session->route->capture : session->route->render;
    const auto expectedEndpoint = capture ? session->route->captureEndpointId : session->route->renderEndpointId;
    const auto expectedDirection = capture ? "CAPTURE" : "RENDER";
    const auto expectedFrames = capture ? candidate.captureFrames : candidate.renderFrames;
    const auto expectedPeriod = capture ? candidate.capturePeriod100ns : candidate.renderPeriod100ns;
    if (endpointId != expectedEndpoint || direction != expectedDirection || requestedFrames != expectedFrames || requestedPeriod != static_cast<std::uint64_t>(expectedPeriod) ||
        discovered.find(endpointId) == discovered.end() || discovered.at(endpointId) != direction)
        throw Rejection("V4_GEOMETRY_MISMATCH", "V4 Initialize endpoint, direction, frames, or period differs from the registered candidate.");
    int sampleRate{}, channels{}; std::string encoding;
    if (!ReadInt(payload,"sampleRate",sampleRate) || !ReadInt(payload,"channels",channels) || !ReadString(payload,"encoding",encoding) ||
        sampleRate != static_cast<int>(side.format.sampleRate) || channels != side.format.channels || encoding != ExclusiveFormatMatrix::EncodingText(side.format.encoding))
        throw Rejection("V4_ROUTE_MISMATCH", "V4 Initialize format differs from the authorized route side.");
    format = side.format;
    try { session->v4Sequence->Begin(static_cast<unsigned>(candidateIndex), step, GuidString(operationId)); }
    catch (const std::invalid_argument& error) { throw Rejection("V4_SEQUENCE_REJECTED", error.what()); }
    session->active = true;
    return candidate;
}

const ScanPlanV2V4::Candidate& AuthorizeV4Duplex(const rapidjson::Value& payload,
        const std::shared_ptr<ScanAuthorization>& session,
        const std::unordered_map<std::string,std::string>& discovered, std::uint64_t generation,
        const ChannelRouting::Configuration& route, std::string_view operationId,
        std::string_view captureId, std::string_view renderId, unsigned duration, std::string_view mmcss) {
    using namespace ScanPlanV2V4;
    if (!session || !session->version4 || !session->v4Sequence || session->invalid ||
        GetTickCount64() >= session->expires || generation != session->discoveryGeneration)
        throw Rejection("SCAN_SESSION_STALE", "V4 authorization is missing, expired, invalidated, or from an old discovery generation.");
    std::string sessionId, routeFingerprint;
    std::uint64_t candidateIndex{};
    std::uint32_t captureFrames{}, renderFrames{};
    std::uint64_t capturePeriod{}, renderPeriod{};
    if (!ReadString(payload,"scanSessionId",sessionId) || GuidString(sessionId)!=session->id ||
        !ReadString(payload,"routeFingerprint",routeFingerprint) || routeFingerprint!=session->routeFingerprint ||
        !payload.HasMember("candidateIndex") || !payload["candidateIndex"].IsUint64() ||
        !payload.HasMember("captureFrames") || !payload["captureFrames"].IsUint() ||
        !payload.HasMember("renderFrames") || !payload["renderFrames"].IsUint() ||
        !payload.HasMember("capturePeriod100ns") || !payload["capturePeriod100ns"].IsUint64() ||
        !payload.HasMember("renderPeriod100ns") || !payload["renderPeriod100ns"].IsUint64())
        throw Rejection("INVALID_V4_REQUEST", "V4 Duplex identity or per-side geometry is incomplete.");
    candidateIndex=payload["candidateIndex"].GetUint64();captureFrames=payload["captureFrames"].GetUint();renderFrames=payload["renderFrames"].GetUint();
    capturePeriod=payload["capturePeriod100ns"].GetUint64();renderPeriod=payload["renderPeriod100ns"].GetUint64();
    const auto& expected=*session->route;
    if (captureId!=expected.captureEndpointId || renderId!=expected.renderEndpointId ||
        routeFingerprint!=ChannelRouting::Fingerprint(route) || ChannelRouting::Fingerprint(route)!=session->routeFingerprint ||
        duration!=5 || mmcss!="OFF" || route.mode!=expected.mode ||
        route.capture.format.sampleRate!=expected.capture.format.sampleRate || route.capture.format.channels!=expected.capture.format.channels || route.capture.format.encoding!=expected.capture.format.encoding ||
        route.render.format.sampleRate!=expected.render.format.sampleRate || route.render.format.channels!=expected.render.format.channels || route.render.format.encoding!=expected.render.format.encoding)
        throw Rejection("V4_ROUTE_OR_BUDGET_MISMATCH", "V4 Duplex route or fixed five-second MMCSS-OFF scope differs from consent.");
    if (discovered.find(std::string(captureId))==discovered.end() || discovered.at(std::string(captureId))!="CAPTURE" ||
        discovered.find(std::string(renderId))==discovered.end() || discovered.at(std::string(renderId))!="RENDER")
        throw Rejection("SCAN_ENDPOINT_OUT_OF_SCOPE", "V4 Duplex endpoints are not current discovered Capture/Render endpoints.");
    if (candidateIndex>=session->v4Candidates.size()) throw Rejection("V4_CANDIDATE_OUT_OF_SCOPE", "Candidate index is outside the registered v4 plan.");
    const auto& candidate=session->v4Candidates[static_cast<std::size_t>(candidateIndex)];
    if (!candidate.rejectionReason.empty() || captureFrames!=candidate.captureFrames || renderFrames!=candidate.renderFrames ||
        capturePeriod!=static_cast<std::uint64_t>(candidate.capturePeriod100ns) || renderPeriod!=static_cast<std::uint64_t>(candidate.renderPeriod100ns))
        throw Rejection("V4_GEOMETRY_MISMATCH", "V4 Duplex geometry differs from the registered candidate.");
    try { session->v4Sequence->Begin(static_cast<unsigned>(candidateIndex),Step::Duplex,GuidString(operationId),duration); }
    catch (const std::invalid_argument& error) { throw Rejection("V4_SEQUENCE_REJECTED",error.what()); }
    session->active=true;
    return candidate;
}

struct V4OperationFinish {
    std::shared_ptr<ScanAuthorization> session; bool finished{};
    void Finish(ScanPlanV2V4::Disposition disposition, bool cleanup, bool exactGeometry = true) {
        if (finished) return; finished = true;
        if (session && session->v4Sequence) {
            try { session->v4Sequence->Complete(disposition, cleanup, exactGeometry); }
            catch (...) { disposition = ScanPlanV2V4::Disposition::Unknown; }
            session->active = false;
            if (disposition == ScanPlanV2V4::Disposition::Unknown || !cleanup) {
                session->invalidationReason = ScanInvalidationReason::UnsafeCleanup; session->invalid = true;
            }
        }
    }
    ~V4OperationFinish() { if (!finished) Finish(ScanPlanV2V4::Disposition::Unknown, false); }
};

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
        if(!writer.Raw("{\"bootstrapVersion\":1,\"kind\":\"response\",\"correlationId\":\""+helloId+"\",\"product\":\"KLIDAN_AUDIO_PROBE_WORKER\",\"protocol\":{\"major\":1,\"minor\":0},\"schemas\":{\"command\":1,\"event\":1,\"report\":1},\"workerVersion\":\"0.3.2\",\"capabilities\":[\"Handshake\",\"ListEndpoints\",\"ListWindowsEndpoints\",\"RealExclusiveFormatQuery\",\"RealExclusiveFormatInitialize\",\"RealExclusiveCustomPeriodInitialize\",\"RealExclusiveStreamValidation\",\"RealExclusiveDuplexValidation\",\"RealExclusiveSustainedDuplexValidation\",\"ScanSessionAuthorizationV1\",\"ScanSessionAuthorizationV2\",\"ScanSessionAuthorizationV2ContractV4\",\"V2CustomTransportContractV4\",\"RealExclusiveDuplexValidationV2\",\"RealExclusiveSustainedDuplexValidationV2\",\"StartCaptureMeter\",\"StartRenderChannelTest\",\"StopSession\",\"CancelStreamValidation\",\"CancelDuplexValidation\",\"Shutdown\""
#if defined(PROBE_TEST_WASAPI_BACKEND)
            ",\"TestInjectedFormatBackend\",\"TestInjectedStreamBackend\",\"TestInjectedDuplexBackend\""
#endif
            "]}"))return false;
    } catch(const std::exception&){writer.Error("HANDSHAKE","INVALID_HANDSHAKE","Malformed handshake frame.");return false;}
    while(true) {
        try { if(!ReadFrame(pipe,bytes)) break; }
        catch(const ProtocolFailure& ex) { writer.Error("REQUEST_FRAME","PROTOCOL_ERROR",ex.what()); return false; }
#if defined(PROBE_TEST_WASAPI_BACKEND)
        if(const auto* busyEvent=std::getenv("KLIDAN_TEST_OPERATION_BUSY_EVENT"))SignalNamedTestEvent(realOperationBusy.load()?busyEvent:std::getenv("KLIDAN_TEST_OPERATION_IDLE_EVENT"));
#endif
        if(!realOperationBusy.load()&&realOperation.joinable())realOperation.join();
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
                if(scanAuthorization){scanAuthorization->invalidationReason=ScanInvalidationReason::DiscoveryRefresh;scanAuthorization->invalid=true;}
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
                    const auto discoveryPayload=WithDiscoveryGeneration(payload,discoveryGeneration);
                    if(discoveryPayload.size()>kMaximumFrame){writer.Response(correlation,command,"ERROR","RESPONSE_TOO_LARGE","Windows endpoint metadata exceeds the protocol frame limit.");continue;}
                    writer.Response(correlation,command,"OK","","",discoveryPayload);
                } catch(const std::exception&) {
                    writer.Response(correlation,command,"ERROR","DISCOVERY_FAILED","Windows endpoint discovery service failed.");
                }
                continue;
            }
            if(command=="RegisterScanSession"||command=="RegisterScanSessionV2") {
                if(realOperationBusy.load()||service.Busy()){writer.Response(correlation,command,"ERROR","BUSY","A scan session can only be registered while the Worker is idle.");continue;}
                if(scanAuthorization&&!scanAuthorization->invalid){writer.Response(correlation,command,"ERROR","SCAN_SESSION_EXISTS","Only one scan authorization may exist per Worker connection.");continue;}
                try {if(command=="RegisterScanSessionV2"){int contract{};if(!ReadInt(payload,"contractVersion",contract))throw Rejection("INVALID_SCAN_PLAN","Contract version is required.");scanAuthorization=contract==4?ParseScanAuthorizationV2V4(payload,discoveredActive,discoveryGeneration,consumedScanConfirmations):contract==3?ParseScanAuthorizationV2(payload,discoveredActive,discoveryGeneration,consumedScanConfirmations):throw Rejection("INVALID_SCAN_PLAN","Unsupported V2 contract version.");}else scanAuthorization=ParseScanAuthorization(payload,discoveredActive,discoveryGeneration,consumedScanConfirmations);scanAuthorization->id=NewGuid();writer.Response(correlation,command,"OK","","","{\"scanSessionId\":\""+scanAuthorization->id+"\",\"contractVersion\":"+std::to_string(scanAuthorization->version4?4:scanAuthorization->version2?3:1)+",\"qaMode\":\""+scanAuthorization->v2QaMode+"\",\"state\":\"AUTHORIZED\",\"expiresInSeconds\":"+std::to_string((scanAuthorization->expires-GetTickCount64())/1000)+"}");}
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
                scanAuthorization->invalidationReason=command=="CancelScanSession"?ScanInvalidationReason::Cancelled:ScanInvalidationReason::Closed;scanAuthorization->invalid=true;
                if(command=="CancelScanSession"&&scanAuthorization->active){std::lock_guard lock(streamCancelMutex);if(streamCancelEvent)SetEvent(streamCancelEvent);if(duplexCancelEvent)SetEvent(duplexCancelEvent);}
                writer.Response(correlation,command,"OK","","","{\"state\":\"INVALIDATED\",\"operationOutcome\":\""+std::string(scanAuthorization->active?"UNKNOWN":"NOT_RUNNING")+"\"}");if(command=="CloseScanSession")scanAuthorization.reset();continue;
            }
            if(command=="InitializeCustomPeriodV2") {
                std::string operationId,endpointId,direction; ExclusiveFormatMatrix::Format format{};
                std::shared_ptr<ScanAuthorization> v4Session=scanAuthorization;
                const auto step=payload.HasMember("direction")&&payload["direction"].IsString()&&std::string_view(payload["direction"].GetString(),payload["direction"].GetStringLength())=="RENDER"?ScanPlanV2V4::Step::RenderInitialize:ScanPlanV2V4::Step::CaptureInitialize;
                const ScanPlanV2V4::Candidate* candidate=nullptr;
                try { candidate=&AuthorizeV4Initialize(payload,v4Session,discoveredActive,discoveryGeneration,step,operationId,endpointId,direction,format); }
                catch(const Rejection& ex){writer.Response(correlation,command,"ERROR",ex.code,ex.what());continue;}
                if(service.Busy()||realOperationBusy.exchange(true)) { if(v4Session->v4Sequence)v4Session->v4Sequence->Complete(ScanPlanV2V4::Disposition::Unknown,false);v4Session->active=false;v4Session->invalid=true;writer.Response(correlation,command,"ERROR","BUSY","A V4 audio operation is already active.");continue; }
                const auto frames=step==ScanPlanV2V4::Step::CaptureInitialize?candidate->captureFrames:candidate->renderFrames;
                const auto period=step==ScanPlanV2V4::Step::CaptureInitialize?candidate->capturePeriod100ns:candidate->renderPeriod100ns;
                std::string scenario;
#if defined(PROBE_TEST_WASAPI_BACKEND)
                scenario=std::getenv("KLIDAN_TEST_V4_SCENARIO")?std::getenv("KLIDAN_TEST_V4_SCENARIO"):"";
#endif
                realOperation=std::thread([&,operationId,endpointId,direction,format,frames,period,correlation,v4Session,step,scenario]() mutable {
                    V4OperationFinish finish{v4Session};
                    const auto terminal=[&](std::string_view status,std::string_view code,std::string_view message,std::string body="{}",ScanPlanV2V4::Disposition disposition=ScanPlanV2V4::Disposition::Unknown,bool cleanup=false,bool geometry=false){finish.Finish(disposition,cleanup,geometry);realOperationBusy=false;const bool sent=writer.Response(correlation,"InitializeCustomPeriodV2",status,code,message,body);if(!sent)finish.Finish(ScanPlanV2V4::Disposition::Unknown,false);return sent;};
                    HRESULT com=CoInitializeEx(nullptr,COINIT_MULTITHREADED);if(FAILED(com)){terminal("ERROR","COM_INITIALIZE_FAILED","V4 Initialize thread could not initialize COM.");return;}
                    IMMDevice* device=nullptr;
                    try {
#if !defined(PROBE_TEST_WASAPI_BACKEND)
                        const auto endpointError=RevalidateEndpoint(endpointId,direction,&device);if(!endpointError.empty())throw Rejection("ENDPOINT_INVALID","The exact active endpoint changed before V4 Initialize.");
#endif
#if defined(PROBE_TEST_WASAPI_BACKEND)
                        InjectedWasapiApi api;
#else
                        ExclusiveFormatMatrix::WasapiApi api(device);
#endif
                        const auto query=api.QuerySupport(format);
                        ExclusiveFormatMatrix::Attempt attempt;
                        const bool inBounds=query.minimumDevicePeriod100ns&&query.defaultDevicePeriod100ns&&period>=*query.minimumDevicePeriod100ns&&period<=*query.defaultDevicePeriod100ns;
                        if(inBounds)attempt=api.InitializeOnly(format,ExclusiveFormatMatrix::PeriodRequest::Custom(period,frames));
#if defined(PROBE_TEST_WASAPI_BACKEND)
                        if(scenario=="INIT_FAILED")attempt.finalInitializeHr=static_cast<std::int32_t>(0x80004005U);
                        if(scenario=="INIT_UNSUPPORTED")attempt.finalInitializeHr=ExclusiveFormatMatrix::kUnsupportedFormat;
                        if(scenario=="ALIGNMENT_MISMATCH"){attempt.firstInitializeHr=ExclusiveFormatMatrix::kBufferSizeNotAligned;attempt.alignmentFrames=frames+1;}
                        if(scenario=="ACTUAL_FRAMES_MISMATCH"&&attempt.actualBufferFrames)++*attempt.actualBufferFrames;
                        if(scenario=="ACTUAL_PERIOD_MISMATCH"&&attempt.actualPeriod100ns)++*attempt.actualPeriod100ns;
                        if(scenario=="CLEANUP_UNKNOWN")attempt.cleanup=ExclusiveFormatMatrix::Cleanup::Incomplete;
#endif
                        const bool success=inBounds&&attempt.finalInitializeHr&&*attempt.finalInitializeHr==ExclusiveFormatMatrix::kOk;
                        const bool cleanup=attempt.cleanup==ExclusiveFormatMatrix::Cleanup::ReleaseAttempted&&attempt.clientReleaseAttempts>0;
                        const bool exact=success&&attempt.actualBufferFrames&&*attempt.actualBufferFrames==frames&&attempt.actualPeriod100ns&&*attempt.actualPeriod100ns==period&&(!attempt.alignmentFrames||*attempt.alignmentFrames==frames);
                        const auto disposition=!cleanup?ScanPlanV2V4::Disposition::Unknown:!inBounds?ScanPlanV2V4::Disposition::Unsupported:!attempt.finalInitializeHr?ScanPlanV2V4::Disposition::Unknown:!success?(*attempt.finalInitializeHr==ExclusiveFormatMatrix::kUnsupportedFormat?ScanPlanV2V4::Disposition::Unsupported:ScanPlanV2V4::Disposition::Failed):!exact?ScanPlanV2V4::Disposition::Failed:ScanPlanV2V4::Disposition::Clean;
                        const auto hr=attempt.finalInitializeHr?std::to_string(*attempt.finalInitializeHr):"null";
                        const auto actualFrames=attempt.actualBufferFrames?std::to_string(*attempt.actualBufferFrames):"null";
                        const auto actualPeriod=attempt.actualPeriod100ns?std::to_string(*attempt.actualPeriod100ns):"null";
                        const auto cleanupText=cleanup?"RELEASE_ATTEMPTED":attempt.cleanup==ExclusiveFormatMatrix::Cleanup::Failed?"FAILED":"UNKNOWN";
                        const auto dispositionText=disposition==ScanPlanV2V4::Disposition::Clean?"INITIALIZED_EXACT":disposition==ScanPlanV2V4::Disposition::Unsupported?"UNSUPPORTED":disposition==ScanPlanV2V4::Disposition::Failed?"FAILED":"UNKNOWN";
                        const auto reason=!cleanup?"CLEANUP_UNKNOWN":!inBounds?"CUSTOM_PERIOD_OUT_OF_BOUNDS":success&&!exact?"ACTUAL_GEOMETRY_MISMATCH":disposition==ScanPlanV2V4::Disposition::Failed?"INITIALIZE_FAILED":disposition==ScanPlanV2V4::Disposition::Unsupported?"FORMAT_OR_PERIOD_UNSUPPORTED":"INITIALIZE_OUTCOME_UNKNOWN";
                        const auto alignmentFrames=attempt.alignmentFrames?std::to_string(*attempt.alignmentFrames):"null";
                        const auto alignmentHr=attempt.firstInitializeHr&&*attempt.firstInitializeHr==ExclusiveFormatMatrix::kBufferSizeNotAligned?std::string("BUFFER_SIZE_NOT_ALIGNED"):std::string("NONE");
                        const std::string body="{\"contractVersion\":4,\"operationId\":"+JsonString(operationId)+",\"scanSessionId\":"+JsonString(v4Session->id)+",\"candidateIndex\":"+std::to_string(candidate->index)+",\"routeFingerprint\":"+JsonString(v4Session->routeFingerprint)+",\"endpointId\":"+JsonString(endpointId)+",\"direction\":"+JsonString(direction)+",\"sampleRateHz\":"+std::to_string(format.sampleRate)+",\"channels\":"+std::to_string(format.channels)+",\"encoding\":"+JsonString(ExclusiveFormatMatrix::EncodingText(format.encoding))+",\"requestedPeriod100ns\":"+std::to_string(period)+",\"requestedFrames\":"+std::to_string(frames)+",\"actualPeriod100ns\":"+actualPeriod+",\"actualFrames\":"+actualFrames+",\"alignmentFrames\":"+alignmentFrames+",\"alignmentReason\":"+JsonString(alignmentHr)+",\"initializeDisposition\":"+JsonString(dispositionText)+",\"initializeHresult\":"+hr+",\"reasonCode\":"+JsonString(reason)+",\"cleanup\":"+JsonString(cleanupText)+",\"provenance\":\""+std::string(
#if defined(PROBE_TEST_WASAPI_BACKEND)
                            "TEST_FAKE_WASAPI_V4_INITIALIZE"
#else
                            "REAL_WASAPI_V4_INITIALIZE"
#endif
                        )+"\"}";
                        if(device)device->Release();device=nullptr;CoUninitialize();terminal("OK","","",body,disposition,cleanup,exact);
                    } catch(const Rejection& ex){if(device)device->Release();CoUninitialize();terminal("ERROR",ex.code,ex.what());}
                    catch(...){if(device)device->Release();CoUninitialize();terminal("ERROR","V4_INITIALIZE_FAILED","V4 Initialize did not produce complete evidence.");}
                });
                continue;
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
                    const auto terminal=[&](std::string_view status,std::string_view code,std::string_view message,std::string_view payload="{}",bool reusable=false){scanFinish.Finish(reusable);realOperationBusy=false;const bool sent=writer.Response(correlation,"InitializeExclusiveCustomPeriod",status,code,message,payload);
#if defined(PROBE_TEST_WASAPI_BACKEND)
                        if(sent)WaitNamedTestEvent(std::getenv("KLIDAN_TEST_POST_RESPONSE_GATE"));
#endif
                        if(!sent)scanFinish.Finish(false);return sent;};
#if defined(PROBE_TEST_WASAPI_BACKEND)
                    if(periodScenario=="NO_RESPONSE") { Sleep(30000); scanFinish.Finish(false); realOperationBusy=false; return; }
#endif
                    const auto publish=[&](const std::string& report) { const auto body="{\"operationId\":"+JsonString(operationId)+",\"endpointId\":"+JsonString(endpointId)+",\"direction\":"+JsonString(direction)+",\"report\":"+report+"}"; terminal("OK","","",body,ScanReportSafe(body)); };
#if defined(PROBE_TEST_WASAPI_BACKEND)
                    try { InjectedWasapiApi api; auto attempt=api.InitializeOnly(format,ExclusiveFormatMatrix::PeriodRequest::Custom(period100ns,static_cast<std::uint32_t>(frames))); static std::atomic<int> rejectedFirstFour{}; if(periodScenario=="REJECT_FIRST_FOUR" && rejectedFirstFour.fetch_add(1)<4) attempt.finalInitializeHr=ExclusiveFormatMatrix::kUnsupportedFormat; const auto classification=ExclusivePeriodDiscovery::Classify(attempt); ExclusiveFormatMatrix::Attempt reportAttempt=attempt; const auto report=ExclusivePeriodDiscovery::Report{endpointId,direction=="CAPTURE"?ExclusiveFormatMatrix::Direction::Capture:ExclusiveFormatMatrix::Direction::Render,format,{30'000,100'000},operationId,NewGuid(),{{period100ns,static_cast<std::uint32_t>(frames),classification,ExclusivePeriodDiscovery::ClassificationText(classification),reportAttempt}}}; publish(ExclusivePeriodDiscovery::ToJson(report,"2026-11-01T00:00:00Z","TEST_FAKE_PERIOD_BACKEND")); }
                    catch(...) { terminal("ERROR","INJECTED_BACKEND_FAILED","The injected custom-period backend did not produce a complete response."); }
#else
                    HRESULT com=CoInitializeEx(nullptr,COINIT_MULTITHREADED);
                    if(FAILED(com)){terminal("ERROR","COM_INITIALIZE_FAILED","Custom-period operation thread could not initialize COM.");return;}
                    IMMDevice* device=nullptr;
                    try { const auto validation=RevalidateEndpoint(endpointId,direction,&device); if(!validation.empty())throw Rejection("ENDPOINT_INVALID","Endpoint was not active immediately before custom-period Initialize."); ExclusiveFormatMatrix::WasapiApi api(device); const auto query=api.QuerySupport(format); if(!query.minimumDevicePeriod100ns||!query.defaultDevicePeriod100ns||period100ns<*query.minimumDevicePeriod100ns||period100ns>*query.defaultDevicePeriod100ns)throw Rejection("CUSTOM_PERIOD_OUT_OF_BOUNDS","Requested period is outside this endpoint's current minimum/default bounds."); const auto attempt=api.InitializeOnly(format,ExclusiveFormatMatrix::PeriodRequest::Custom(period100ns,static_cast<std::uint32_t>(frames))); const auto classification=ExclusivePeriodDiscovery::Classify(attempt); ExclusivePeriodDiscovery::Report report{endpointId,direction=="CAPTURE"?ExclusiveFormatMatrix::Direction::Capture:ExclusiveFormatMatrix::Direction::Render,format,{*query.minimumDevicePeriod100ns,*query.defaultDevicePeriod100ns},operationId,NewGuid(),{{period100ns,static_cast<std::uint32_t>(frames),classification,ExclusivePeriodDiscovery::ClassificationText(classification),attempt}}}; const auto text=ExclusivePeriodDiscovery::ToJson(report,MatrixTimestampUtc(),"REAL_WASAPI_INITIALIZE_ONLY"); device->Release();CoUninitialize();publish(text); }
                    catch(const Rejection& ex){if(device)device->Release();CoUninitialize();terminal("ERROR",ex.code,ex.what());}
                    catch(...){if(device)device->Release();CoUninitialize();terminal("ERROR","NATIVE_OPERATION_FAILED","Custom-period WASAPI operation did not produce a complete response.");}
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
                    const auto terminal=[&](std::string_view status,std::string_view code,std::string_view message,std::string_view payload="{}",bool reusable=false){scanFinish.Finish(reusable);realOperationBusy=false;const bool sent=writer.Response(correlation,operationCommand,status,code,message,payload);if(!sent)scanFinish.Finish(false);return sent;};
#if defined(PROBE_TEST_WASAPI_BACKEND)
                    try {
                        InjectedWasapiApi api;
                        const auto dir=direction=="CAPTURE"?ExclusiveFormatMatrix::Direction::Capture:ExclusiveFormatMatrix::Direction::Render;
                        ExclusiveFormatMatrix::Report report;
                        if(operationCommand=="QueryFormatMatrix")report=ExclusiveFormatMatrix::RunCandidates(dir,ExclusiveFormatMatrix::Level::PassiveQuery,api,formats);
                        else report={dir,ExclusiveFormatMatrix::Level::InitializeOnly,{ExclusiveFormatMatrix::RunSingle(dir,formats.front(),period,api,true)}};
                        const auto result=SerializeRealMatrix(endpointId,direction,report);
                        if(result.size()>kMaximumFrame-1024)terminal("ERROR","RESPONSE_TOO_LARGE","Injected test result exceeds the IPC frame limit.");
                        else terminal("OK","","",result,!operationScanSession||ScanReportSafe(result));
                    } catch(const std::exception&) { terminal("ERROR","INJECTED_BACKEND_FAILED","The injected test backend did not produce a complete response."); }
                    realOperationBusy=false;return;
#else
                    HRESULT com=CoInitializeEx(nullptr,COINIT_MULTITHREADED);
                    if(FAILED(com)) { terminal("ERROR","COM_INITIALIZE_FAILED","Dedicated operation thread could not initialize COM.","{\"stage\":\"COM_INITIALIZE\",\"hresult\":"+ExclusiveFormatMatrix::HresultJson(static_cast<std::int32_t>(com))+"}");return; }
                    IMMDevice* device=nullptr;
                    try {
                        const auto validation=RevalidateEndpoint(endpointId,direction,&device);
                        if(!validation.empty()) { CoUninitialize();terminal("ERROR",validation,"Endpoint was not active with the requested direction immediately before the operation.");return; }
                        ExclusiveFormatMatrix::WasapiApi api(device);
                        const auto dir=direction=="CAPTURE"?ExclusiveFormatMatrix::Direction::Capture:ExclusiveFormatMatrix::Direction::Render;
                        ExclusiveFormatMatrix::Report report;
                        if(operationCommand=="QueryFormatMatrix")report=ExclusiveFormatMatrix::RunCandidates(dir,ExclusiveFormatMatrix::Level::PassiveQuery,api,formats);
                        else report={dir,ExclusiveFormatMatrix::Level::InitializeOnly,{ExclusiveFormatMatrix::RunSingle(dir,formats.front(),period,api,true)}};
                        const auto result=SerializeRealMatrix(endpointId,direction,report);
                        device->Release();CoUninitialize();
                        if(result.size()>kMaximumFrame-1024)terminal("ERROR","RESPONSE_TOO_LARGE","Format result exceeds the IPC frame limit.");
                        else terminal("OK","","",result,!operationScanSession||ScanReportSafe(result));
                    } catch(const std::exception&) { if(device)device->Release();CoUninitialize();terminal("ERROR","NATIVE_OPERATION_FAILED","The real WASAPI format operation failed before a complete result was produced."); }
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
                    const auto terminal=[&](std::string_view status,std::string_view code,std::string_view message,std::string_view payload="{}",bool reusable=false){scanFinish.Finish(reusable);realOperationBusy=false;const bool sent=writer.Response(correlation,"ValidateStream",status,code,message,payload);if(!sent)scanFinish.Finish(false);return sent;};
                    HANDLE cancellation=INVALID_HANDLE_VALUE;
#if defined(PROBE_TEST_WASAPI_BACKEND)
                    SECURITY_ATTRIBUTES testSecurity{sizeof(SECURITY_ATTRIBUTES),nullptr,TRUE};
                    cancellation=CreateEventW(&testSecurity,TRUE,FALSE,nullptr);
                    if(!cancellation){terminal("ERROR","CANCEL_EVENT_CREATE_FAILED","Could not create injected cancellation event.");return;}
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
                    {std::lock_guard lock(streamCancelMutex);streamCancelEvent=nullptr;}
                    CloseHandle(cancellation);
                    const auto response="{\"operationId\":"+JsonString(responseOperationId)+",\"endpointId\":"+JsonString(responseEndpointId)+",\"direction\":"+JsonString(responseDirection)+",\"report\":"+report+"}";terminal("OK","","",response,!operationScanSession||ScanReportSafe(response));
#else
                    const HRESULT com=CoInitializeEx(nullptr,COINIT_MULTITHREADED);
                    if(FAILED(com)){terminal("ERROR","COM_INITIALIZE_FAILED","Stream operation thread could not initialize COM.","{\"hresult\":"+ExclusiveFormatMatrix::HresultJson(static_cast<std::int32_t>(com))+"}");return;}
                    IMMDevice* device=nullptr;const auto validation=RevalidateEndpoint(endpointId,direction,&device);
                    if(device)device->Release();CoUninitialize();
                    if(!validation.empty()){terminal("ERROR",validation,"Endpoint failed exact active-state/direction revalidation immediately before stream operation.");return;}
                    SECURITY_ATTRIBUTES security{sizeof(SECURITY_ATTRIBUTES),nullptr,TRUE};
                    cancellation=CreateEventW(&security,TRUE,FALSE,nullptr);
                    if(!cancellation){terminal("ERROR","CANCEL_EVENT_CREATE_FAILED","Could not create a cancellation event.");return;}
                    {std::lock_guard lock(streamCancelMutex);streamCancelEvent=cancellation;}
                    std::string failure,failureField;auto report=RunStreamCli(endpointId,direction,period,static_cast<unsigned>(duration),mmcss,cancellation,failure,failureField);
                    {std::lock_guard lock(streamCancelMutex);streamCancelEvent=nullptr;}
                    CloseHandle(cancellation);
                    if(!report){const auto message=failureField.empty()?"The native stream subprocess did not return complete structured evidence; operation outcome may be unknown.":"The native stream report contract failed at field "+failureField+"; operation outcome may be unknown.";terminal("ERROR",failure,message);return;}
                    const auto response="{\"operationId\":\""+operationId+"\",\"endpointId\":"+JsonString(endpointId)+",\"direction\":\""+direction+"\",\"report\":"+*report+"}";terminal("OK","","",response,!operationScanSession||ScanReportSafe(response));
#endif
                    realOperationBusy=false;
                });
                continue;
            }
            if(command=="ValidateDuplex"||command=="ValidateSustainedDuplex"||command=="ValidateDuplexV2"||command=="ValidateSustainedDuplexV2"||command=="ValidateCustomDuplexV2") {
                const bool sustained=command=="ValidateSustainedDuplex"||command=="ValidateSustainedDuplexV2";
                const bool customV4=command=="ValidateCustomDuplexV2";
                const bool routeV2=command=="ValidateDuplexV2"||command=="ValidateSustainedDuplexV2"||customV4;
                std::string operationId,captureId,renderId,period,mmcss;
                int sampleRate=0,channels=0,duration=0;
                const auto isCustomPeriodText = [&period] { return period.size() > 7 && period.compare(0, 7, "CUSTOM:") == 0; };
                if(!ReadString(payload,"operationId",operationId)||!ReadString(payload,"captureEndpointId",captureId)||
                   !ReadString(payload,"renderEndpointId",renderId)||!ReadString(payload,"period",period)||
                   !ReadString(payload,"mmcss",mmcss)||!ReadInt(payload,"durationSeconds",duration)||
                   (!routeV2&&(!ReadInt(payload,"sampleRate",sampleRate)||!ReadInt(payload,"channels",channels)||sampleRate!=48000||channels!=2))||
                   duration<1||(sustained?(duration!=60&&duration!=300&&duration!=600):duration>10)||
                   (period!="MINIMUM"&&period!="DEFAULT"&&!(customV4&&period=="CUSTOM_V2")&&(!isCustomPeriodText()||routeV2))||(mmcss!="OFF"&&mmcss!="PRO_AUDIO")||
                   captureId.empty()||renderId.empty()||captureId==renderId||captureId.size()>32768||renderId.size()>32768) {
                    writer.Response(correlation,command,"ERROR","INVALID_ARGUMENT","Duplex validation requires an explicit distinct endpoint pair and bounded 48 kHz stereo PCM16 settings.");continue;
                }
                std::optional<ChannelRouting::Configuration> route;
                if(routeV2){int version{};if(!ReadInt(payload,"contractVersion",version)||version!=ChannelRouting::kContractVersion){writer.Response(correlation,command,"ERROR","ROUTE_CONTRACT_UNSUPPORTED","A supported versioned routing contract is required.");continue;}try{route=ReadV2Route(payload,captureId,renderId,false);}catch(const Rejection& ex){writer.Response(correlation,command,"ERROR",ex.code,ex.what());continue;}}
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
                const bool scanRequested=payload.HasMember("scanSessionId")||payload.HasMember("scanOperationId");
                if(customV4&&!scanRequested){writer.Response(correlation,command,"ERROR","SCAN_SESSION_REQUIRED","V4 CUSTOM Duplex requires a registered v4 session.");continue;}
                if(routeV2&&scanRequested&&(!scanAuthorization||(!scanAuthorization->version2&&!(customV4&&scanAuthorization->version4)))){writer.Response(correlation,command,"ERROR","V1_V2_SESSION_CONFLICT","The session contract version cannot authorize this route operation.");continue;}
                if(routeV2&&!scanRequested&&scanAuthorization){writer.Response(correlation,command,"ERROR","SCAN_SESSION_CONFLICT","Close the existing scan session before starting a standalone V2 operation.");continue;}
                if(command=="ValidateSustainedDuplexV2"&&!scanRequested){writer.Response(correlation,command,"ERROR","SCAN_SESSION_REQUIRED","V2 sustained validation requires an authorized V2 scan session.");continue;}
                if(!routeV2&&scanAuthorization&&!scanAuthorization->invalid&&!payload.HasMember("scanSessionId")){writer.Response(correlation,command,"ERROR","SCAN_SESSION_REQUIRED","Operations must use the active approved scan token until that session is closed.");continue;}
                if(!scanRequested){
                    const auto receipt=payload.FindMember("consentReceipt");bool accepted=false;std::string consentId,fingerprint;
                    if(receipt!=payload.MemberEnd()&&receipt->value.IsObject()){if(receipt->value.HasMember("accepted")&&receipt->value["accepted"].IsBool())accepted=receipt->value["accepted"].GetBool();ReadString(receipt->value,"confirmationId",consentId);ReadString(receipt->value,"requestFingerprint",fingerprint);}
                    const auto canonical=routeV2?captureId+"|"+renderId+"|"+period+"|"+std::to_string(duration)+"|"+mmcss+"|REAL_WASAPI_DUPLEX_V2|"+ChannelRouting::Fingerprint(*route):captureId+"|"+renderId+"|48000|2|PCM16|"+period+"|"+std::to_string(duration)+"|"+mmcss+"|REAL_WASAPI_DUPLEX_V1";
                    if(!accepted||consentId.empty()||fingerprint!=Sha256(canonical)){writer.Response(correlation,command,"ERROR","DUPLEX_CONSENT_MISMATCH","Fresh consent must match the exact endpoint pair and immutable Duplex settings.");continue;}
                    try { consentId=GuidString(consentId); } catch(const Rejection& ex){writer.Response(correlation,command,"ERROR",ex.code,ex.what());continue;}
                    if(consumedDuplexReceipts.size()>=kMaximumConsumedRealConsentReceipts){writer.Response(correlation,command,"ERROR","CONSENT_CAPACITY","Duplex consent history is bounded; restart the worker before another validation.");continue;}
                    if(!consumedDuplexReceipts.emplace(consentId).second){writer.Response(correlation,command,"ERROR","CONSENT_REPLAY","This Duplex confirmation has already been used.");continue;}
                }
                if(service.Busy()||realOperationBusy.exchange(true)){writer.Response(correlation,command,"ERROR","BUSY","Another real diagnostic operation is active in this worker.");continue;}
                std::shared_ptr<ScanAuthorization> operationScanSession;
                const ScanPlanV2V4::Candidate* v4Candidate=nullptr;
                if(scanRequested)try{if(customV4)v4Candidate=&AuthorizeV4Duplex(payload,scanAuthorization,discoveredActive,discoveryGeneration,*route,operationId,captureId,renderId,static_cast<unsigned>(duration),mmcss);else if(routeV2)AuthorizeV2ScanOperation(payload,scanAuthorization,discoveredActive,discoveryGeneration,command,*route,period,duration,mmcss);else{std::vector<ExclusiveFormatMatrix::Format> one{{48000,2,ExclusiveFormatMatrix::Encoding::Pcm16}};AuthorizeScanOperation(payload,scanAuthorization,discoveredActive,discoveryGeneration,command,one,period,duration,mmcss,"","",captureId,renderId);}operationScanSession=scanAuthorization;}catch(const Rejection& ex){realOperationBusy=false;writer.Response(correlation,command,"ERROR",ex.code,ex.what());continue;}
                if(realOperation.joinable())realOperation.join();
                duplexOperationId=operationId;duplexOperationCorrelation=correlation;
                if(pendingDuplexCancelId!=operationId)pendingDuplexCancelId.clear();
#if defined(PROBE_TEST_WASAPI_BACKEND)
                std::string scenario="CLEAN";
                if(payload.HasMember("testScenario")) {
                    if(!payload["testScenario"].IsString()){if(operationScanSession){operationScanSession->active=false;operationScanSession->invalid=true;operationScanSession->invalidationReason=ScanInvalidationReason::UnsafeCleanup;}realOperationBusy=false;writer.Response(correlation,command,"ERROR","INVALID_ARGUMENT","Test-only Duplex scenario must be a string.");continue;}
                    scenario.assign(payload["testScenario"].GetString(),payload["testScenario"].GetStringLength());
                    if(scenario!="CLEAN"&&scenario!="BENIGN_INTERMEDIATE_WAIT"&&scenario!="SERVICE_FAILURE"&&scenario!="WATCHDOG"&&scenario!="LONG_EVENT_INTERVAL"&&scenario!="PACKET_DRAIN_LIMIT"&&scenario!="CAPTURE_FAIL"&&scenario!="RENDER_FAIL"&&scenario!="INSUFFICIENT_FRAMES"&&scenario!="NO_OVERLAP"&&scenario!="CANCELLED"&&scenario!="CLEANUP_UNKNOWN"&&scenario!="WAIT_FOR_CANCEL"&&scenario!="DELAY_RESPONSE"&&scenario!="NO_RESPONSE"&&scenario!="COMMAND_ERROR") { if(operationScanSession){operationScanSession->active=false;operationScanSession->invalid=true;operationScanSession->invalidationReason=ScanInvalidationReason::UnsafeCleanup;}realOperationBusy=false;writer.Response(correlation,command,"ERROR","INVALID_ARGUMENT","Unknown test-only Duplex scenario.");continue; }
                    if(scenario=="COMMAND_ERROR") { if(operationScanSession){operationScanSession->active=false;operationScanSession->invalid=true;operationScanSession->invalidationReason=ScanInvalidationReason::UnsafeCleanup;}realOperationBusy=false;writer.Response(correlation,command,"ERROR","TEST_INJECTED_COMMAND_ERROR","Injected correlated Worker rejection for regression testing.");continue; }
                }
#else
                if(payload.HasMember("testScenario")){if(operationScanSession){operationScanSession->active=false;operationScanSession->invalid=true;operationScanSession->invalidationReason=ScanInvalidationReason::UnsafeCleanup;}realOperationBusy=false;writer.Response(correlation,command,"ERROR","INVALID_ARGUMENT","Test-only Duplex scenarios are unavailable in the production worker.");continue;}
#endif
                SECURITY_ATTRIBUTES cancellationSecurity{sizeof(SECURITY_ATTRIBUTES),nullptr,TRUE};
                HANDLE cancellation=CreateEventW(&cancellationSecurity,TRUE,FALSE,nullptr);
                if(!cancellation){if(operationScanSession){operationScanSession->active=false;operationScanSession->invalidationReason=ScanInvalidationReason::UnsafeCleanup;operationScanSession->invalid=true;}realOperationBusy=false;writer.Response(correlation,command,"ERROR","CANCEL_EVENT_CREATE_FAILED","Could not create Duplex cancellation event.");continue;}
                {std::lock_guard lock(streamCancelMutex);duplexCancelEvent=cancellation;}
                if(pendingDuplexCancelId==operationId){SetEvent(cancellation);pendingDuplexCancelId.clear();}
                const auto v4CandidateCopy=v4Candidate?std::optional<ScanPlanV2V4::Candidate>(*v4Candidate):std::nullopt;
                realOperation=std::thread([&,command,operationId,captureId,renderId,period,mmcss,duration,correlation,cancellation,operationScanSession,sustained,route,customV4,v4CandidateCopy
#if defined(PROBE_TEST_WASAPI_BACKEND)
                    ,scenario
#endif
                    ]() mutable {
                    ScanOperationFinish scanFinish{customV4?nullptr:operationScanSession};
                    V4OperationFinish v4Finish{customV4?operationScanSession:nullptr};
                    const auto terminal=[&](std::string_view status,std::string_view code,std::string_view message,std::string_view payload="{}",bool reusable=false){if(customV4&&!v4Finish.finished)v4Finish.Finish(ScanPlanV2V4::Disposition::Unknown,false);scanFinish.Finish(reusable);realOperationBusy=false;const bool sent=writer.Response(correlation,command,status,code,message,payload);if(!sent){scanFinish.Finish(false);if(customV4)v4Finish.Finish(ScanPlanV2V4::Disposition::Unknown,false);}return sent;};
                    const auto finishCancellation=[&](){std::lock_guard lock(streamCancelMutex);if(duplexCancelEvent==cancellation)duplexCancelEvent=nullptr;CloseHandle(cancellation);};
#if defined(PROBE_TEST_WASAPI_BACKEND)
                    const auto v4PeriodText=v4CandidateCopy?"CUSTOM_V2:"+std::to_string(v4CandidateCopy->capturePeriod100ns)+":"+std::to_string(v4CandidateCopy->captureFrames)+":"+std::to_string(v4CandidateCopy->renderPeriod100ns)+":"+std::to_string(v4CandidateCopy->renderFrames):period;
                    DuplexValidationReport::Report fake;fake.schemaVersion=route?3:sustained?1:2;fake.reportType=route?(sustained?"wasapi-sustained-duplex-validation-v2":"wasapi-duplex-validation-v2"):sustained?"wasapi-sustained-duplex-validation":"wasapi-duplex-validation";fake.provenance=route?(sustained?"TEST_FAKE_WASAPI_SUSTAINED_DUPLEX_V2":"TEST_FAKE_WASAPI_DUPLEX_V2"):sustained?"TEST_FAKE_WASAPI_SUSTAINED_DUPLEX":"TEST_FAKE_WASAPI_DUPLEX";fake.operationId=operationId;fake.reportId=NewGuid();fake.timestampUtc="2026-10-31T00:00:00.000Z";fake.probeVersion="test";fake.captureEndpointId=captureId;fake.renderEndpointId=renderId;fake.period=v4PeriodText;fake.mmcss=mmcss;fake.durationSeconds=duration;fake.bothReady=true;fake.startAuthorized=true;fake.status="DUPLEX_CLEAN";fake.startDifferenceMilliseconds=0.025;fake.simultaneousRuntimeMilliseconds=duration*1000.0;fake.route=route;if(route)fake.routeFingerprint=ChannelRouting::Fingerprint(*route);
                    auto fill=[&](DuplexValidationReport::Side& side,const std::string& id,const char* direction){const auto isCapture=direction==std::string_view("CAPTURE");const auto rate=route?(isCapture?route->capture.format.sampleRate:route->render.format.sampleRate):48'000U;const auto expected=static_cast<std::uint64_t>(rate)*duration;side.endpointId=id;side.direction=direction;if(route)side.routeFormat=isCapture?route->capture:route->render;side.bufferFrames=v4CandidateCopy?(isCapture?v4CandidateCopy->captureFrames:v4CandidateCopy->renderFrames):144U;side.framesProcessed=expected;side.expectedFrames=expected;side.events=1666;side.packets=1666;side.eventIntervalsObserved=1665;side.frameCoveragePercent=100.0;side.elapsedMilliseconds=duration*1000.0;side.ready=true;side.startSucceeded=true;side.stopSucceeded=true;side.durationReached=true;side.quality="CLEAN";side.cleanup="RELEASE_ATTEMPTED";side.startOffsetMilliseconds=0.0;};
                    fill(fake.capture,captureId,"CAPTURE");fill(fake.render,renderId,"RENDER");
                    const auto expected=fake.capture.expectedFrames;
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
                    finishCancellation();
                    if(customV4&&v4CandidateCopy){
                        const auto actualCapture=static_cast<std::uint32_t>(fake.capture.bufferFrames),actualRender=static_cast<std::uint32_t>(fake.render.bufferFrames);
                        const auto actualCapturePeriod=ProbeMath::DurationForFrames(actualCapture,route->capture.format.sampleRate),actualRenderPeriod=ProbeMath::DurationForFrames(actualRender,route->render.format.sampleRate);
                        const bool geometry=actualCapture==v4CandidateCopy->captureFrames&&actualRender==v4CandidateCopy->renderFrames&&actualCapturePeriod==v4CandidateCopy->capturePeriod100ns&&actualRenderPeriod==v4CandidateCopy->renderPeriod100ns;
                        const bool cleanup=fake.capture.cleanup=="RELEASE_ATTEMPTED"&&fake.render.cleanup=="RELEASE_ATTEMPTED";
                        const auto disposition=!cleanup?ScanPlanV2V4::Disposition::Unknown:fake.status=="DUPLEX_CLEAN"&&geometry?ScanPlanV2V4::Disposition::Clean:ScanPlanV2V4::Disposition::Failed;
                        v4Finish.Finish(disposition,cleanup,geometry);
                        const auto label=disposition==ScanPlanV2V4::Disposition::Clean?"CLEAN":disposition==ScanPlanV2V4::Disposition::Failed?"FAILED":"UNKNOWN";
                        const auto evidence="{\"contractVersion\":4,\"operationId\":"+JsonString(operationId)+",\"scanSessionId\":"+JsonString(operationScanSession->id)+",\"candidateIndex\":"+std::to_string(v4CandidateCopy->index)+",\"captureEndpointId\":"+JsonString(captureId)+",\"renderEndpointId\":"+JsonString(renderId)+",\"routeFingerprint\":"+JsonString(operationScanSession->routeFingerprint)+",\"requestedCaptureFrames\":"+std::to_string(v4CandidateCopy->captureFrames)+",\"requestedCapturePeriod100ns\":"+std::to_string(v4CandidateCopy->capturePeriod100ns)+",\"requestedRenderFrames\":"+std::to_string(v4CandidateCopy->renderFrames)+",\"requestedRenderPeriod100ns\":"+std::to_string(v4CandidateCopy->renderPeriod100ns)+",\"actualCaptureFrames\":"+std::to_string(actualCapture)+",\"actualCapturePeriod100ns\":"+std::to_string(actualCapturePeriod)+",\"actualRenderFrames\":"+std::to_string(actualRender)+",\"actualRenderPeriod100ns\":"+std::to_string(actualRenderPeriod)+",\"duplexDisposition\":"+JsonString(label)+",\"reasonCode\":"+JsonString(geometry?fake.status:"ACTUAL_GEOMETRY_MISMATCH")+",\"cleanup\":"+JsonString(cleanup?"CAPTURE:RELEASE_ATTEMPTED;RENDER:RELEASE_ATTEMPTED":"UNKNOWN")+",\"provenance\":\"TEST_FAKE_WASAPI_V4_DUPLEX\",\"report\":"+report+"}";
                        terminal("OK","","",evidence,true);
                    } else {const auto response="{\"operationId\":"+JsonString(operationId)+",\"captureEndpointId\":"+JsonString(captureId)+",\"renderEndpointId\":"+JsonString(renderId)+",\"scanSessionId\":"+JsonString(operationScanSession?operationScanSession->id:std::string{})+",\"report\":"+report+"}";if(operationScanSession&&operationScanSession->version2)CompleteV2Qualification(operationScanSession,command,response);terminal("OK","","",response,!operationScanSession||ScanReportSafe(response));}
#else
                    const HRESULT com=CoInitializeEx(nullptr,COINIT_MULTITHREADED);
                    if(FAILED(com)){finishCancellation();terminal("ERROR","COM_INITIALIZE_FAILED","Duplex operation thread could not initialize COM.","{\"hresult\":"+ExclusiveFormatMatrix::HresultJson(static_cast<std::int32_t>(com))+"}");return;}
                    IMMDevice* device=nullptr;auto validation=RevalidateEndpoint(captureId,"CAPTURE",&device);if(device)device->Release();
                    if(validation.empty()){validation=RevalidateEndpoint(renderId,"RENDER",&device);if(device)device->Release();}
                    CoUninitialize();
                    if(!validation.empty()){finishCancellation();terminal("ERROR",validation,"Exact endpoint pair failed active-state/direction revalidation before operation.");return;}
                    std::string failure, failureDetail;auto report=RunDuplexCli(operationId,captureId,renderId,period,static_cast<unsigned>(duration),mmcss,cancellation,failure,failureDetail,sustained,route?&*route:nullptr,v4CandidateCopy?&*v4CandidateCopy:nullptr);
                    finishCancellation();
                    if(!report){terminal("ERROR",failure,"Structured Duplex evidence unavailable; operation or cleanup outcome may be unknown. "+failureDetail);return;}
                    if(customV4&&v4CandidateCopy){
                        std::vector<std::uint8_t> reportBytes(report->begin(),report->end());auto parsed=ParseObject(reportBytes);
                        const auto sideFrames=[&](const char* key)->std::optional<std::uint32_t>{if(!parsed.HasMember(key)||!parsed[key].IsObject()||!parsed[key].HasMember("actualBufferFrames")||!parsed[key]["actualBufferFrames"].IsUint64())return std::nullopt;const auto frames64=parsed[key]["actualBufferFrames"].GetUint64();if(frames64==0||frames64>UINT32_MAX)return std::nullopt;return static_cast<std::uint32_t>(frames64);};
                        const auto capFrames=sideFrames("capture"),renFrames=sideFrames("render");
                        const bool cleanup=parsed.HasMember("capture")&&parsed.HasMember("render")&&parsed["capture"].IsObject()&&parsed["render"].IsObject()&&
                            parsed["capture"].HasMember("cleanup")&&parsed["capture"]["cleanup"].IsString()&&std::string_view(parsed["capture"]["cleanup"].GetString())=="RELEASE_ATTEMPTED"&&
                            parsed["render"].HasMember("cleanup")&&parsed["render"]["cleanup"].IsString()&&std::string_view(parsed["render"]["cleanup"].GetString())=="RELEASE_ATTEMPTED"&&
                            parsed["capture"].HasMember("stopSucceeded")&&parsed["capture"]["stopSucceeded"].IsBool()&&parsed["capture"]["stopSucceeded"].GetBool()&&
                            parsed["render"].HasMember("stopSucceeded")&&parsed["render"]["stopSucceeded"].IsBool()&&parsed["render"]["stopSucceeded"].GetBool();
                        const auto capPeriod=capFrames?std::optional<std::uint64_t>(ProbeMath::DurationForFrames(*capFrames,route->capture.format.sampleRate)):std::nullopt;
                        const auto renPeriod=renFrames?std::optional<std::uint64_t>(ProbeMath::DurationForFrames(*renFrames,route->render.format.sampleRate)):std::nullopt;
                        const bool geometry=capFrames&&renFrames&&capPeriod&&renPeriod&&*capFrames==v4CandidateCopy->captureFrames&&*renFrames==v4CandidateCopy->renderFrames&&*capPeriod==static_cast<std::uint64_t>(v4CandidateCopy->capturePeriod100ns)&&*renPeriod==static_cast<std::uint64_t>(v4CandidateCopy->renderPeriod100ns);
                        const bool clean=parsed.HasMember("duplex")&&parsed["duplex"].IsObject()&&parsed["duplex"].HasMember("status")&&parsed["duplex"]["status"].IsString()&&std::string_view(parsed["duplex"]["status"].GetString())=="DUPLEX_CLEAN";
                        const auto disposition=!cleanup||!capFrames||!renFrames?ScanPlanV2V4::Disposition::Unknown:clean&&geometry?ScanPlanV2V4::Disposition::Clean:ScanPlanV2V4::Disposition::Failed;
                        v4Finish.Finish(disposition,cleanup,geometry);const auto label=disposition==ScanPlanV2V4::Disposition::Clean?"CLEAN":disposition==ScanPlanV2V4::Disposition::Failed?"FAILED":"UNKNOWN";
                        const auto nullable=[](const auto& value){return value?std::to_string(*value):std::string("null");};
                        const auto evidence="{\"contractVersion\":4,\"operationId\":"+JsonString(operationId)+",\"scanSessionId\":"+JsonString(operationScanSession->id)+",\"candidateIndex\":"+std::to_string(v4CandidateCopy->index)+",\"captureEndpointId\":"+JsonString(captureId)+",\"renderEndpointId\":"+JsonString(renderId)+",\"routeFingerprint\":"+JsonString(operationScanSession->routeFingerprint)+",\"requestedCaptureFrames\":"+std::to_string(v4CandidateCopy->captureFrames)+",\"requestedCapturePeriod100ns\":"+std::to_string(v4CandidateCopy->capturePeriod100ns)+",\"requestedRenderFrames\":"+std::to_string(v4CandidateCopy->renderFrames)+",\"requestedRenderPeriod100ns\":"+std::to_string(v4CandidateCopy->renderPeriod100ns)+",\"actualCaptureFrames\":"+nullable(capFrames)+",\"actualCapturePeriod100ns\":"+nullable(capPeriod)+",\"actualRenderFrames\":"+nullable(renFrames)+",\"actualRenderPeriod100ns\":"+nullable(renPeriod)+",\"duplexDisposition\":"+JsonString(label)+",\"reasonCode\":"+JsonString(!cleanup?"CLEANUP_UNKNOWN":!geometry?"ACTUAL_GEOMETRY_MISMATCH":clean?"CLEAN":"DUPLEX_NOT_CLEAN")+",\"cleanup\":"+JsonString(cleanup?"CAPTURE:RELEASE_ATTEMPTED;RENDER:RELEASE_ATTEMPTED":"UNKNOWN")+",\"provenance\":\"REAL_WASAPI_V4_DUPLEX\",\"report\":"+*report+"}";
                        terminal("OK","","",evidence,true);
                    } else {const auto response="{\"operationId\":"+JsonString(operationId)+",\"captureEndpointId\":"+JsonString(captureId)+",\"renderEndpointId\":"+JsonString(renderId)+",\"scanSessionId\":"+JsonString(operationScanSession?operationScanSession->id:std::string{})+",\"report\":"+*report+"}";if(operationScanSession&&operationScanSession->version2)CompleteV2Qualification(operationScanSession,command,response);terminal("OK","","",response,!operationScanSession||ScanReportSafe(response));}
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
