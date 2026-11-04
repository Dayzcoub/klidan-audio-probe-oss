#pragma once

#include <cstdint>
#include <memory>
#include <span>
#include <string>
#include <vector>

namespace probe::windows_endpoints {

struct PropertyEvidence {
    std::int32_t hresult{};
    std::string state{"UNKNOWN"};
};

struct SharedModeFormat {
    PropertyEvidence property;
    std::string parseState{"UNKNOWN"};
    std::string encoding{"UNKNOWN"};
    std::string topologyState{"UNKNOWN"};
    std::int32_t formatTag{};
    std::int32_t reportedChannelCount{};
    std::uint32_t sampleRate{};
    std::int32_t bitsPerSample{};
    std::int32_t validBitsPerSample{};
    bool hasFormat{};
    bool hasSpeakerMask{};
    std::uint32_t speakerMask{};
    std::string speakerMaskState{"UNKNOWN"};
};

struct Endpoint {
    std::string endpointId;
    PropertyEvidence endpointIdEvidence;
    std::string friendlyName;
    PropertyEvidence friendlyNameEvidence;
    std::string direction;
    std::string deviceState{"UNKNOWN"};
    std::uint32_t rawDeviceState{};
    PropertyEvidence deviceStateEvidence;
    std::string formFactor{"UNKNOWN"};
    PropertyEvidence formFactorEvidence;
    std::string containerId;
    PropertyEvidence containerIdEvidence;
    SharedModeFormat sharedModeFormat;
};

struct DiscoveryError {
    std::string direction;
    std::string stage;
    std::int32_t hresult{};
};

struct DiscoveryResult {
    std::int32_t captureHresult{};
    std::int32_t renderHresult{};
    std::vector<Endpoint> endpoints;
    std::vector<DiscoveryError> errors;
    bool complete{true};
};

struct RawEndpoint {
    std::wstring endpointId;
    std::wstring friendlyName;
    std::string direction;
    std::uint32_t deviceState{};
    std::uint32_t formFactor{};
    PropertyEvidence endpointIdEvidence;
    PropertyEvidence friendlyNameEvidence;
    PropertyEvidence deviceStateEvidence;
    PropertyEvidence formFactorEvidence;
    std::wstring containerId;
    PropertyEvidence containerIdEvidence;
    PropertyEvidence sharedFormatEvidence;
    std::vector<std::uint8_t> sharedFormatBlob;
};

struct RawDiscovery {
    std::int32_t captureHresult{};
    std::int32_t renderHresult{};
    std::vector<RawEndpoint> endpoints;
    std::vector<DiscoveryError> errors;
    bool complete{true};
};

class Source {
public:
    virtual ~Source() = default;
    virtual RawDiscovery Enumerate() = 0;
};

SharedModeFormat ParseSharedModeFormat(std::span<const std::uint8_t> blob, PropertyEvidence property);
std::string SerializeJson(const DiscoveryResult& result);
DiscoveryResult Discover(Source& source);
std::unique_ptr<Source> CreateMmDeviceSource();

} // namespace probe::windows_endpoints
