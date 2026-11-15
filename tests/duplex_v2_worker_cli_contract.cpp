#include <windows.h>

#include <filesystem>
#include <cstddef>
#include <cstdint>
#include <fstream>
#include <iostream>
#include <iterator>
#include <string>
#include <vector>

namespace {
std::wstring Quote(const std::wstring& value) {
    return L"\"" + value + L"\"";
}

bool Run(const std::wstring& cli, bool flow8, bool custom = false) {
    SECURITY_ATTRIBUTES security{sizeof(security), nullptr, TRUE};
    HANDLE cancel = CreateEventW(&security, TRUE, FALSE, nullptr);
    HANDLE outputRead = nullptr, outputWrite = nullptr;
    if (!cancel || !CreatePipe(&outputRead, &outputWrite, &security, 0)) {
        if (cancel) CloseHandle(cancel);
        return false;
    }
    SetHandleInformation(outputRead, HANDLE_FLAG_INHERIT, 0);

    wchar_t temp[MAX_PATH + 1]{};
    if (!GetTempPathW(MAX_PATH, temp)) {
        CloseHandle(outputRead); CloseHandle(outputWrite); CloseHandle(cancel);
        return false;
    }
    const std::string operation = custom ? "00000000-0000-0000-0000-000000000083" : flow8 ? "00000000-0000-0000-0000-000000000082" : "00000000-0000-0000-0000-000000000081";
    const std::wstring wideOperation(operation.begin(), operation.end());
    const auto report = std::filesystem::path(temp) / (L"KlidanV2Contract-" + wideOperation + L".json");
    DeleteFileW(report.c_str());
    const auto captureChannels = flow8 ? L"1" : L"2";
    std::wstring command = Quote(cli) + L" --capture-id " + (flow8 ? L"flow8-capture-missing" : L"bloody-capture-missing") +
        L" --render-id " + (flow8 ? L"flow8-render-missing" : L"bloody-render-missing") +
        (custom ? L" --test-duplex-v2-custom" : L" --test-duplex-v2") + L" --output-json " + Quote(report.wstring()) + L" --operation-id " + wideOperation +
        L" --cancel-event-handle " + std::to_wstring(reinterpret_cast<std::uintptr_t>(cancel)) +
        L" --mmcss off --period default --duration 5 --route-mode MONO_TO_STEREO --capture-rate " + (custom ? L"44100" : L"48000") + L" --capture-channels " + captureChannels +
        L" --capture-format PCM16 --capture-mask 0 --capture-indices 0 --render-rate 48000 --render-channels 2" +
        L" --render-format PCM16 --render-mask 3 --render-indices 0,1" +
        (custom ? L" --capture-custom-period custom:29932:132 --render-custom-period custom:30000:144" : L"");

    SIZE_T attributeBytes = 0;
    InitializeProcThreadAttributeList(nullptr, 1, 0, &attributeBytes);
    std::vector<std::byte> attributeBuffer(attributeBytes);
    auto* attributes = reinterpret_cast<PPROC_THREAD_ATTRIBUTE_LIST>(attributeBuffer.data());
    bool attributeListInitialized = InitializeProcThreadAttributeList(attributes, 1, 0, &attributeBytes) != FALSE;
    bool ok = attributeListInitialized;
    HANDLE inherited[]{cancel, outputWrite};
    if (ok) ok = UpdateProcThreadAttribute(attributes, 0, PROC_THREAD_ATTRIBUTE_HANDLE_LIST,
        inherited, sizeof(inherited), nullptr, nullptr) != FALSE;

    STARTUPINFOEXW startup{};
    startup.StartupInfo.cb = sizeof(startup);
    startup.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
    startup.StartupInfo.hStdOutput = outputWrite;
    startup.StartupInfo.hStdError = outputWrite;
    startup.StartupInfo.hStdInput = GetStdHandle(STD_INPUT_HANDLE);
    startup.lpAttributeList = attributes;
    PROCESS_INFORMATION process{};
    if (ok) ok = CreateProcessW(cli.c_str(), command.data(), nullptr, nullptr, TRUE,
        EXTENDED_STARTUPINFO_PRESENT | CREATE_NO_WINDOW, nullptr, nullptr, &startup.StartupInfo, &process) != FALSE;
    if (attributeListInitialized) DeleteProcThreadAttributeList(attributes);
    CloseHandle(outputWrite);

    std::string output;
    if (ok) {
        const DWORD waited = WaitForSingleObject(process.hProcess, 20000);
        if (waited != WAIT_OBJECT_0) {
            TerminateProcess(process.hProcess, ERROR_TIMEOUT);
            WaitForSingleObject(process.hProcess, 3000);
            ok = false;
        } else {
            DWORD exitCode = 0;
            ok = GetExitCodeProcess(process.hProcess, &exitCode) && exitCode == 4;
            char buffer[1024]; DWORD read = 0;
            while (ReadFile(outputRead, buffer, sizeof(buffer), &read, nullptr) && read != 0) output.append(buffer, read);
            ok = ok && output.find("DUPLEX_STATUS: INITIALIZE_FAILED") != std::string::npos &&
                output.find("Invalid or incomplete argument") == std::string::npos;
        }
        CloseHandle(process.hThread); CloseHandle(process.hProcess);
    }
    CloseHandle(outputRead); CloseHandle(cancel);

    std::ifstream input(report, std::ios::binary);
    if (!input) ok = false;
    const std::string json((std::istreambuf_iterator<char>(input)), {});
    ok = ok && json.find("\"operationId\":\"" + operation + "\"") != std::string::npos &&
        json.find("\"status\":\"FAILED\"") != std::string::npos &&
        json.find("DUPLEX_CLEAN") == std::string::npos && json.find("DUPLEX_WARNINGS") == std::string::npos;
    if (custom) ok = ok && json.find("CUSTOM_V2:29932:132:30000:144") != std::string::npos;
    DeleteFileW(report.c_str());
    if (!ok) std::cerr << (flow8 ? "FLOW 8" : "Bloody") << " contract failed; exit/output/report did not match the endpoint-initialization failure contract.\n" << output;
    else std::cout << (flow8 ? "FLOW 8" : "Bloody") << " full Worker-to-CLI contract reached endpoint initialization failure.\n";
    return ok;
}
}

int wmain(int argc, wchar_t** argv) {
    if (argc != 2) return 2;
    const std::wstring cli = argv[1];
    return Run(cli, false) && Run(cli, true) && Run(cli, true, true) ? 0 : 1;
}
