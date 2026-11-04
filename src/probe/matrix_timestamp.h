#pragma once

#include <windows.h>

#include <cstdio>
#include <string>

inline std::string MatrixTimestampUtc(const SYSTEMTIME& utc) {
    char text[32]{};
    std::snprintf(text, sizeof(text), "%04u-%02u-%02uT%02u:%02u:%02u.%03uZ",
        utc.wYear, utc.wMonth, utc.wDay, utc.wHour, utc.wMinute, utc.wSecond, utc.wMilliseconds);
    return text;
}

inline std::string MatrixTimestampUtc() {
    SYSTEMTIME utc{};
    GetSystemTime(&utc);
    return MatrixTimestampUtc(utc);
}
