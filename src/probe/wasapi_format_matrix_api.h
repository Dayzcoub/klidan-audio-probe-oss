#pragma once

#include <audioclient.h>
#include <mmdeviceapi.h>
#include <optional>

#include "exclusive_format_matrix.h"

namespace ExclusiveFormatMatrix {

std::optional<WAVEFORMATEXTENSIBLE> BuildWasapiFormat(const Format& format) noexcept;
std::optional<WAVEFORMATEXTENSIBLE> BuildWasapiFormat(const Format& format, std::uint32_t channelMask) noexcept;

class WasapiApi final : public Api {
public:
    explicit WasapiApi(IMMDevice* device) noexcept : device_(device) {}
    QueryResult QuerySupport(const Format& format) override;
    Attempt InitializeOnly(const Format& format, Period period) override;
    Attempt InitializeOnly(const Format& format, const PeriodRequest& request) override;
private:
    IMMDevice* device_;
};

} // namespace ExclusiveFormatMatrix
