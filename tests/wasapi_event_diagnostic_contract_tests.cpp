#include "wasapi_event_diagnostic_contract.h"
#include "wasapi_event_diagnostic_io.h"
#include "wasapi_event_diagnostic_json.h"

#include <rapidjson/document.h>
#include <rapidjson/schema.h>

#include <filesystem>
#include <fstream>
#include <iostream>
#include <iterator>
#include <string>
#include <vector>

using namespace WasapiEventDiagnostic;

namespace {
Options Parse(std::vector<std::wstring> values) {
    std::vector<wchar_t*> argv;
    argv.reserve(values.size());
    for (auto& value : values) argv.push_back(value.data());
    return ParseArguments(static_cast<int>(argv.size()), argv.data());
}
}

int main() {
    std::vector<std::wstring> args = {
        L"WasapiRenderEventDiagnostic.exe", L"--confirm-operator-authorization",
        L"--render-id", std::wstring(kRenderEndpointId), L"--duration", L"5",
        L"--rate", L"48000", L"--channels", L"2", L"--period-ms", L"3",
        L"--format", L"pcm16", L"--mmcss", L"off", L"--silence-only",
        L"--output-json", L"C:\\qa\\one.json"};
    const auto valid = Parse(args);
    if (valid.command != Command::Run || valid.outputPath != L"C:\\qa\\one.json") return 1;
    auto noConsent = args;
    noConsent.erase(noConsent.begin() + 1);
    if (Parse(std::move(noConsent)).command != Command::Invalid) return 2;
    auto otherEndpoint = args;
    otherEndpoint[3] = L"{0.0.0.00000000}.{00000000-0000-0000-0000-000000000000}";
    if (Parse(std::move(otherEndpoint)).command != Command::Invalid) return 3;
    auto wrongDuration = args;
    wrongDuration[5] = L"60";
    if (Parse(std::move(wrongDuration)).command != Command::Invalid) return 4;
    auto wrongMode = args;
    wrongMode[15] = L"pro-audio";
    if (Parse(std::move(wrongMode)).command != Command::Invalid) return 5;
    auto toneFlag = args;
    toneFlag.insert(toneFlag.end() - 2, L"--test-tone");
    if (Parse(std::move(toneFlag)).command != Command::Invalid) return 6;

    Report fixture;
    fixture.buildCommit = "test-fixture";
    fixture.operationId = "{00000000-0000-0000-0000-000000000001}";
    fixture.timestampUtc = "2026-11-11T00:00:00.000Z";
    fixture.diagnosticDisposition = "COMPLETED";
    fixture.cleanupState = "CONFIRMED";
    fixture.actualBufferFrames = 144;
    fixture.actualBufferPeriod100ns = 30'000;
    fixture.eventCount = 500;
    fixture.bufferGetCount = 500;
    fixture.bufferReleaseCount = 500;
    fixture.framesProcessed = 72'000;
    fixture.frameCoveragePercent = 30.0;
    fixture.elapsedMilliseconds = 5'000.0;
    fixture.eventRateHz = 100.0;
    fixture.meanFramesPerEvent = 144.0;
    fixture.meanEventIntervalMilliseconds = 10.0;
    fixture.firstEventDelayMilliseconds = 0.01;
    fixture.eventIntervalMedianMilliseconds = 10.0;
    fixture.eventIntervalP95Milliseconds = 10.1;
    fixture.eventIntervalP99Milliseconds = 10.2;
    fixture.eventIntervalMaximumMilliseconds = 13.0;
    fixture.eventIntervalMaximumEndOffsetMilliseconds = 13.01;
    fixture.waitTimeoutCount = 0;
    fixture.durationReached = true;
    fixture.clientReleased = true;
    fixture.eventHandleClosed = true;
    fixture.cancelEventClosed = true;
    const auto json = ToJson(fixture);
    rapidjson::Document document;
    document.Parse(json.data(), json.size());
    if (document.HasParseError()) return 7;
    std::ifstream schemaFile(PROBE_WASAPI_EVENT_SCHEMA, std::ios::binary);
    if (!schemaFile) return 8;
    const std::string schemaText((std::istreambuf_iterator<char>(schemaFile)), {});
    rapidjson::Document schemaJson;
    schemaJson.Parse(schemaText.data(), schemaText.size());
    if (schemaJson.HasParseError()) return 9;
    rapidjson::SchemaDocument schema(schemaJson);
    rapidjson::SchemaValidator validator(schema);
    if (!document.Accept(validator)) {
        std::cerr << "Independent WASAPI diagnostic JSON does not satisfy schema v1.\n";
        return 10;
    }
    if (std::string(document["audioQualityDisposition"].GetString()) != "NOT_CLASSIFIED" ||
        std::string(document["renderPolicy"].GetString()) != "SILENCE_ONLY" || std::string(document["shareMode"].GetString()) != "EXCLUSIVE" ||
        document["requestedFrames"].GetUint() != 144 || document["framesProcessed"].GetUint64() != 72'000) return 11;

    const auto unique = std::to_wstring(GetCurrentProcessId()) + L"-" + std::to_wstring(GetTickCount64());
    const auto output = std::filesystem::temp_directory_path() / (L"klidan-event-diagnostic-" + unique + L".json");
    DWORD writeError = ERROR_SUCCESS;
    if (!WriteNewJson(output.wstring(), json, writeError)) return 12;
    if (WriteNewJson(output.wstring(), "{}", writeError)) { DeleteFileW(output.c_str()); return 13; }
    std::ifstream saved(output, std::ios::binary);
    const std::string readback((std::istreambuf_iterator<char>(saved)), {});
    DeleteFileW(output.c_str());
    if (readback != json) return 14;
    std::cout << "Independent diagnostic: fixed one-operation authorization, strict schema v1, synthetic TEST fixture, and atomic no-overwrite read-back passed; no WASAPI was opened.\n";
    return 0;
}
