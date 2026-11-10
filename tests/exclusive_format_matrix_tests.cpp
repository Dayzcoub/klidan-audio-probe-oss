#include "../src/probe/exclusive_format_matrix.h"
#include "../src/probe/wasapi_format_matrix_api.h"
#include "../src/probe/channel_routing.h"

#include <cstdio>
#include <fstream>
#include <string>
#include <string_view>

namespace {
using namespace ExclusiveFormatMatrix;
int failures = 0;
#define CHECK(x) do { if (!(x)) { std::fprintf(stderr, "FAIL %s:%d: %s\n", __FILE__, __LINE__, #x); ++failures; } } while (false)
constexpr std::int32_t kDeviceBusy = static_cast<std::int32_t>(0x8889000AU);
constexpr std::int32_t kOtherFailure = static_cast<std::int32_t>(0x80004005U);

struct FakeApi final : Api {
    int queries = 0;
    int initializations = 0;
    bool failQueryCleanup = false;
    QueryResult QuerySupport(const Format& format) override {
        ++queries;
        QueryResult result{kOk, kOk, Cleanup::ReleaseAttempted, 1};
        if (format.encoding == Encoding::Pcm24In32 && format.channels == 1)
            result.supportHr = kUnsupportedFormat;
        if (format.encoding == Encoding::Float32 && format.sampleRate == 48'000 && format.channels == 2)
            result.supportHr = kDeviceBusy;
        if (failQueryCleanup) result.cleanup = Cleanup::Failed;
        return result;
    }
    Attempt InitializeOnly(const Format& format, Period period) override {
        ++initializations;
        Attempt a;
        a.periodQueryHr = kOk;
        a.periodAvailable = !(format.sampleRate == 44'100 && period == Period::Minimum);
        a.cleanup = Cleanup::ReleaseAttempted;
        a.clientReleaseAttempts = 1;
        if (!*a.periodAvailable) return a;
        a.requestedPeriod100ns = period == Period::Default ? 100'000 : 30'000;
        a.initializeAttempted = true;
        a.firstInitializeHr = kOk;
        a.finalInitializeHr = kOk;
        a.actualBufferSizeHr = kOk;
        a.actualBufferFrames = 144;
        a.actualPeriod100ns = 30'000;
        a.eventCreateHr = kOk;
        a.setEventHandleHr = kOk;
        a.eventHandleCloseHr = kOk;
        if (format.encoding == Encoding::Pcm16 && format.channels == 1 && format.sampleRate == 44'100 &&
            period == Period::Default) {
            a.firstInitializeHr = kBufferSizeNotAligned;
            a.alignmentGetBufferSizeHr = kOk;
            a.alignmentFrames = 256;
            a.alignmentActivateHr = kOk;
            a.alignmentPeriod100ns = 58'050;
            a.alignmentInitializeHr = kOk;
            a.finalInitializeHr = kOk;
            a.actualBufferFrames = 256;
            a.clientReleaseAttempts = 2;
        } else if (format.encoding == Encoding::Pcm24In32 && format.channels == 1) {
            a.firstInitializeHr = kUnsupportedFormat;
            a.finalInitializeHr = kUnsupportedFormat;
        } else if (format.encoding == Encoding::Float32 && format.sampleRate == 48'000 && format.channels == 2) {
            a.firstInitializeHr = kDeviceBusy;
            a.finalInitializeHr = kDeviceBusy;
        } else if (format.encoding == Encoding::Pcm16 && format.channels == 2 && period == Period::Default) {
            a.setEventHandleHr = kOtherFailure;
        } else if (format.encoding == Encoding::Pcm16 && format.channels == 2 &&
                   format.sampleRate == 48'000 && period == Period::Minimum) {
            a.cleanup = Cleanup::Incomplete;
            a.clientReleaseAttempts = 0;
        } else if (format.encoding == Encoding::Pcm16 && format.channels == 1 &&
                   format.sampleRate == 48'000 && period == Period::Default) {
            a.firstInitializeHr = kBufferSizeNotAligned;
            a.finalInitializeHr.reset();
            a.alignmentGetBufferSizeHr = kOk;
            a.alignmentFrames = 256;
            a.alignmentActivateHr.reset();
            a.alignmentInitializeHr.reset();
            a.clientReleaseAttempts = 1;
        } else if (format.encoding == Encoding::Pcm16 && format.channels == 1 &&
                   format.sampleRate == 48'000 && period == Period::Minimum) {
            a.firstInitializeHr = kBufferSizeNotAligned;
            a.finalInitializeHr.reset();
            a.alignmentGetBufferSizeHr = kOk;
            a.alignmentFrames = 256;
            a.alignmentActivateHr = kOtherFailure;
            a.alignmentPeriod100ns = 58'050;
            a.alignmentInitializeHr.reset();
            a.clientReleaseAttempts = 1;
        }
        if (format.encoding == Encoding::Pcm24In32 && format.channels == 2 &&
            format.sampleRate == 48'000 && period == Period::Default) {
            a.cleanup = Cleanup::Failed;
            a.eventHandleCloseHr = kOtherFailure;
        }
        return a;
    }
};

bool TestPassiveAndQueryCaching() {
    FakeApi api;
    const auto report = Run(Direction::Capture, Level::PassiveQuery, api);
    CHECK(report.candidates.size() == 24);
    CHECK(api.queries == 12 && api.initializations == 0);
    CHECK(report.candidates[0].status == Status::SupportedReported);
    CHECK(report.candidates[0].attempt.finalInitializeHr == std::nullopt);
    CHECK(InitializeStatusText(report.candidates[0], Level::PassiveQuery) == std::string_view("NOT_TESTED"));
    CHECK(report.candidates[2].status == Status::Unsupported); // PCM24-in-32 mono at 44.1 kHz.
    CHECK(report.candidates[23].status == Status::Failed); // Busy support query stays distinct.
    return failures == 0;
}

bool TestInitializeOnlyOutcomes() {
    FakeApi api;
    const auto report = Run(Direction::Render, Level::InitializeOnly, api);
    CHECK(report.candidates.size() == 24 && api.queries == 12 && api.initializations == 24);
    const auto& aligned = report.candidates[0];
    CHECK(aligned.status == Status::Ready);
    CHECK(aligned.query.supportHr == kOk && aligned.attempt.firstInitializeHr == kBufferSizeNotAligned);
    CHECK(aligned.attempt.alignmentGetBufferSizeHr == kOk && aligned.attempt.alignmentFrames == 256);
    CHECK(aligned.attempt.alignmentInitializeHr == kOk && aligned.attempt.actualBufferFrames == 256);
    CHECK(report.candidates[2].status == Status::Unsupported);
    CHECK(report.candidates[23].query.supportHr == kDeviceBusy);
    CHECK(report.candidates[23].status == Status::Failed);
    CHECK(report.candidates[23].attempt.finalInitializeHr == kDeviceBusy);
    CHECK(report.candidates[1].status == Status::Unsupported); // Minimum period is unavailable for this format.
    CHECK(report.candidates[6].status == Status::Failed); // Failed event readiness.
    CHECK(std::string_view(InitializeStatusText(report.candidates[6], Level::InitializeOnly)) == "INITIALIZED");
    CHECK(report.candidates[19].status == Status::Incomplete); // Missing cleanup evidence.
    CHECK(report.candidates[12].status == Status::Incomplete); // Alignment retry stopped before activation.
    CHECK(InitializeStatusText(report.candidates[12], Level::InitializeOnly) == std::string_view("INCOMPLETE"));
    CHECK(report.candidates[13].status == Status::Failed);
    CHECK(report.candidates[13].attempt.alignmentActivateHr == kOtherFailure);
    CHECK(InitializeStatusText(report.candidates[13], Level::InitializeOnly) == std::string_view("FAILED"));
    CHECK(InitializeStatusText(report.candidates[23], Level::InitializeOnly) == std::string_view("FAILED"));
    CHECK(InitializeStatusText(report.candidates[2], Level::InitializeOnly) == std::string_view("UNSUPPORTED"));
    CHECK(report.candidates[20].status == Status::Failed); // Verifiable event-handle close failure.
    CHECK(report.candidates[20].attempt.cleanup == Cleanup::Failed);

    FakeApi queryCleanupFailure;
    queryCleanupFailure.failQueryCleanup = true;
    const auto failedQuery = Run(Direction::Capture, Level::PassiveQuery, queryCleanupFailure);
    CHECK(failedQuery.candidates[0].status == Status::Failed);
    CHECK(std::string_view(SupportStatusText(failedQuery.candidates[0].query)) == "FAILED");
    const auto independentInitialize = Run(Direction::Capture, Level::InitializeOnly, queryCleanupFailure);
    CHECK(independentInitialize.candidates[0].status == Status::Ready);
    CHECK(std::string_view(SupportStatusText(independentInitialize.candidates[0].query)) == "FAILED");

    Candidate supportedButNotInitialized;
    supportedButNotInitialized.query.supportHr = kOk;
    supportedButNotInitialized.attempt.finalInitializeHr = kDeviceBusy;
    CHECK(supportedButNotInitialized.status == Status::NotTested);
    supportedButNotInitialized.status = Classify(supportedButNotInitialized, Level::InitializeOnly);
    CHECK(supportedButNotInitialized.status == Status::Failed);
    Candidate noClient;
    noClient.attempt.activationHr = kOk;
    noClient.attempt.cleanup = Cleanup::NotAcquired;
    CHECK(InitializeStatusText(noClient, Level::InitializeOnly) == std::string_view("NOT_TESTED"));
    CHECK(Classify(noClient, Level::InitializeOnly) == Status::Incomplete);
    Candidate activationFailed;
    activationFailed.attempt.activationHr = kOtherFailure;
    activationFailed.attempt.cleanup = Cleanup::NotAcquired;
    CHECK(InitializeStatusText(activationFailed, Level::InitializeOnly) == std::string_view("NOT_TESTED"));
    CHECK(Classify(activationFailed, Level::InitializeOnly) == Status::Failed);
    Candidate queryNoClient;
    queryNoClient.query.activationHr = kOk;
    queryNoClient.query.cleanup = Cleanup::NotAcquired;
    CHECK(Classify(queryNoClient, Level::PassiveQuery) == Status::Incomplete);
    Candidate queryCleanupMissing;
    queryCleanupMissing.query.activationHr = kOk;
    queryCleanupMissing.query.supportHr = kOk;
    CHECK(Classify(queryCleanupMissing, Level::PassiveQuery) == Status::Incomplete);
    CHECK(std::string_view(SupportStatusText(queryCleanupMissing.query)) == "INCOMPLETE");
    return failures == 0;
}

bool TestJsonSchemaAndPrivacy() {
    FakeApi api;
    const auto report = Run(Direction::Capture, Level::InitializeOnly, api);
    const auto json = ToJson(report, "2026-10-30T12:00:00Z", "0.1.0", "12345678-1234-4234-8234-123456789abc");
    const auto second = ToJson(report, "2026-10-30T12:00:00Z", "0.1.0", "12345678-1234-4234-8234-123456789abd");
    CHECK(json.find("\"schemaVersion\": 1") != std::string::npos);
    CHECK(json.find("wasapi-exclusive-format-matrix") != std::string::npos);
    CHECK(json.find("INITIALIZE_ONLY") != std::string::npos);
    CHECK(json.find("isFormatSupportedHr") != std::string::npos);
    CHECK(json.find("firstInitializeHr") != std::string::npos);
    CHECK(json.find("alignmentRetry") != std::string::npos);
    CHECK(json.find("finalInitializeHr") != std::string::npos);
    CHECK(json.find("actualBufferFrames") != std::string::npos);
    CHECK(json.find("eventHandleReady") != std::string::npos);
    CHECK(json.find("cleanupStatus") != std::string::npos);
    CHECK(json.find("\"certified\": false") != std::string::npos);
    CHECK(json.find("\"startCalled\": false") != std::string::npos);
    CHECK(json.find("\"audioTransferred\": false") != std::string::npos);
    CHECK(json.find("private-device-id") == std::string::npos);
    CHECK(json.find("H:/private") == std::string::npos);
    CHECK(json.find("\"isFormatSupportedHr\": \"0x00000000\"") != std::string::npos);
    CHECK(json.find("\"reportId\": \"12345678-1234-4234-8234-123456789abc\"") != std::string::npos);
    CHECK(second.find("\"reportId\": \"12345678-1234-4234-8234-123456789abd\"") != std::string::npos);
    CHECK(json.find("\"timestampUtc\": \"2026-10-30T12:00:00Z\"") != std::string::npos);
    CHECK(json.find("\"queryCleanupHr\": null") != std::string::npos);
    CHECK(json.find("\"cleanupHr\": null") != std::string::npos);
    CHECK(json.find("\"queryClientReleaseAttempts\": 1") != std::string::npos);
    CHECK(json.find("\"clientReleaseAttempts\": 1") != std::string::npos);
    CHECK(json.find("\"eventHandleCloseHr\": \"0x00000000\"") != std::string::npos);
    CHECK(JsonString("quote\" slash\\ newline\n") == "\"quote\\\" slash\\\\ newline\\n\"");
    CHECK(report.candidates.size() == 24);
    return failures == 0;
}

bool TestExplicitCandidatesAndSingleInitialization() {
    FakeApi api;
    const std::vector<Format> requested{{48'000,64,Encoding::Pcm16}};
    const auto report=RunCandidates(Direction::Capture,Level::PassiveQuery,api,requested);
    CHECK(report.candidates.size()==2&&api.queries==1&&api.initializations==0);
    CHECK(report.candidates[0].format.channels==64&&report.candidates[0].status==Status::SupportedReported);
    FakeApi initApi;
    const auto single=RunSingle(Direction::Render,{44'100,2,Encoding::Pcm24In32},Period::Default,initApi,true);
    CHECK(initApi.queries==1&&initApi.initializations==1&&single.period==Period::Default);
    CHECK(single.query.supportHr==kOk&&single.attempt.initializeAttempted&&single.attempt.firstInitializeHr==kOk);
    FakeApi unsupported;
    const auto rejected=RunSingle(Direction::Capture,{44'100,1,Encoding::Pcm24In32},Period::Default,unsupported,true);
    CHECK(unsupported.queries==1&&unsupported.initializations==0&&rejected.status==Status::Unsupported);
    const auto large=BuildWasapiFormat({48'000,64,Encoding::Float32});
    CHECK(large.has_value()&&large->Format.nChannels==64&&large->dwChannelMask==0&&large->Format.wBitsPerSample==32);
    const auto invalid=BuildWasapiFormat({48'000,65,Encoding::Pcm16});
    CHECK(!invalid.has_value());
    const auto eight=BuildWasapiFormat({48'000,8,Encoding::Pcm24In32},0);
    CHECK(eight && eight->Format.nChannels==8 && eight->dwChannelMask==0 && eight->Samples.wValidBitsPerSample==24);
    const auto invalidMask=BuildWasapiFormat({48'000,8,Encoding::Pcm16},0x3);
    CHECK(!invalidMask);
    const auto explicitMask=BuildWasapiFormat({48'000,2,Encoding::Float32},0x3);
    CHECK(explicitMask && explicitMask->dwChannelMask==0x3 && explicitMask->SubFormat==KSDATAFORMAT_SUBTYPE_IEEE_FLOAT);
    return failures==0;
}

bool TestChannelRouting() {
    using namespace ChannelRouting;
    Configuration route{"capture-id","render-id",{{48'000,8,Encoding::Pcm24In32},0},{{44'100,2,Encoding::Float32},3},Mode::MonoToStereo,{7,0},{0,1}};
    CHECK(Validate(route) && ModeText(route.mode)=="MONO_TO_STEREO");
    route.mode=Mode::StereoToStereo;route.captureChannels={6,7};
    CHECK(Validate(route) && CaptureCount(route.mode)==2 && RenderCount(route.mode)==2);
    route.capture={{48'000,32,Encoding::Pcm16},0};route.render={{48'000,32,Encoding::Pcm16},0};route.captureChannels={30,31};route.renderChannels={16,17};
    CHECK(Validate(route));
    route.mode=Mode::MonoToMono;route.captureChannels={0,0};route.renderChannels={31,0};
    CHECK(Validate(route) && CaptureCount(route.mode)==1 && RenderCount(route.mode)==1);
    route.mode=Mode::StereoToStereo;route.captureChannels={3,3};
    CHECK(!Validate(route));
    route.captureChannels={0,1};route.renderChannels={0,32};
    CHECK(!Validate(route));
    route.renderChannels={0,1};route.render.channelMask=1;
    CHECK(!Validate(route));
    route.render.channelMask=0;route.render.format.sampleRate=96'000;
    CHECK(!Validate(route));
    route.render.format.sampleRate=48'000;route.renderEndpointId=route.captureEndpointId;
    CHECK(!Validate(route));
    route.renderEndpointId="render-id";route.capture.format.encoding=static_cast<Encoding>(255);
    CHECK(!Validate(route));
    const std::int16_t pcm16[]{-16384,8192};
    route.capture.format={48'000,2,Encoding::Pcm16};
    EndpointFormat sampleFormat{route.capture.format,0};
    CHECK(ReadSample(pcm16,0,0,sampleFormat)==-0.5&&ReadSample(pcm16,0,1,sampleFormat)==0.25);
    const std::int32_t pcm24[]{-1073741824,536870912};
    sampleFormat.format.encoding=Encoding::Pcm24In32;
    CHECK(ReadSample(pcm24,0,0,sampleFormat)==-0.5&&ReadSample(pcm24,0,1,sampleFormat)==0.25);
    const float float32[]{0.125f,-0.25f};
    sampleFormat.format.encoding=Encoding::Float32;
    CHECK(ReadSample(float32,0,0,sampleFormat)==0.125&&ReadSample(float32,0,1,sampleFormat)==-0.25);
    return failures==0;
}
}

int main(int argc, char** argv) {
    if (argc == 3 && std::string_view(argv[1]) == "--write-json") {
        FakeApi api;
        const auto report = Run(Direction::Capture, Level::PassiveQuery, api);
        const auto json = ToJson(report, "2026-10-30T12:00:00Z", "0.1.0", "12345678-1234-4234-8234-123456789abc");
        std::ofstream output(argv[2], std::ios::binary | std::ios::trunc);
        if (!output) return 3;
        output.write(json.data(), static_cast<std::streamsize>(json.size()));
        return output ? 0 : 4;
    }
    if (argc != 1) return 2;
    TestPassiveAndQueryCaching();
    TestInitializeOnlyOutcomes();
    TestJsonSchemaAndPrivacy();
    TestExplicitCandidatesAndSingleInitialization();
    TestChannelRouting();
    if (failures != 0) return 1;
    std::puts("Exclusive format matrix fake API tests passed");
    return 0;
}
