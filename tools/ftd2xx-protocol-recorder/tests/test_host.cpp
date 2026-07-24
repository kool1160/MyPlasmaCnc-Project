#include "ftd2xx_api.h"
#include "mock_contract.h"

#include <algorithm>
#include <array>
#include <filesystem>
#include <iostream>
#include <string>
#include <thread>
#include <vector>

namespace
{
struct ProxyApi
{
    HMODULE module = nullptr;
    FT_ListDevices_t listDevices = nullptr;
    FT_OpenEx_t openEx = nullptr;
    FT_Close_t close = nullptr;
    FT_Read_t read = nullptr;
    FT_Write_t write = nullptr;
    FT_SetBaudRate_t setBaudRate = nullptr;
    FT_SetDataCharacteristics_t setDataCharacteristics = nullptr;
    FT_SetFlowControl_t setFlowControl = nullptr;
    FT_GetQueueStatus_t getQueueStatus = nullptr;
    FT_SetLatencyTimer_t setLatencyTimer = nullptr;
    FT_SetBitMode_t setBitMode = nullptr;
};

struct MockApi
{
    MOCK_Reset_t reset = nullptr;
    MOCK_GetCallCount_t getCallCount = nullptr;
    MOCK_GetLastWriteSize_t getLastWriteSize = nullptr;
    MOCK_CopyLastWrite_t copyLastWrite = nullptr;
};

[[noreturn]] void Fail(const std::string& message)
{
    std::cerr << "FAIL: " << message << '\n';
    ExitProcess(1);
}

void Require(const bool condition, const std::string& message)
{
    if (!condition)
    {
        Fail(message);
    }
}

template <typename T>
T RequiredExport(const HMODULE module, const char* name)
{
    const T function = reinterpret_cast<T>(GetProcAddress(module, name));
    if (function == nullptr)
    {
        Fail(std::string("missing export: ") + name);
    }
    return function;
}

ProxyApi LoadProxy(const std::filesystem::path& path)
{
    ProxyApi api;
    api.module = LoadLibraryW(path.c_str());
    Require(api.module != nullptr, "proxy DLL could not be loaded");
    api.listDevices = RequiredExport<FT_ListDevices_t>(api.module, "FT_ListDevices");
    api.openEx = RequiredExport<FT_OpenEx_t>(api.module, "FT_OpenEx");
    api.close = RequiredExport<FT_Close_t>(api.module, "FT_Close");
    api.read = RequiredExport<FT_Read_t>(api.module, "FT_Read");
    api.write = RequiredExport<FT_Write_t>(api.module, "FT_Write");
    api.setBaudRate = RequiredExport<FT_SetBaudRate_t>(api.module, "FT_SetBaudRate");
    api.setDataCharacteristics =
        RequiredExport<FT_SetDataCharacteristics_t>(api.module, "FT_SetDataCharacteristics");
    api.setFlowControl =
        RequiredExport<FT_SetFlowControl_t>(api.module, "FT_SetFlowControl");
    api.getQueueStatus =
        RequiredExport<FT_GetQueueStatus_t>(api.module, "FT_GetQueueStatus");
    api.setLatencyTimer =
        RequiredExport<FT_SetLatencyTimer_t>(api.module, "FT_SetLatencyTimer");
    api.setBitMode = RequiredExport<FT_SetBitMode_t>(api.module, "FT_SetBitMode");
    return api;
}

MockApi LoadMockApi()
{
    const HMODULE mockModule = GetModuleHandleW(L"ftd2xx_real.dll");
    Require(mockModule != nullptr, "mock real DLL was not loaded by the proxy");
    return {
        RequiredExport<MOCK_Reset_t>(mockModule, "MOCK_Reset"),
        RequiredExport<MOCK_GetCallCount_t>(mockModule, "MOCK_GetCallCount"),
        RequiredExport<MOCK_GetLastWriteSize_t>(mockModule, "MOCK_GetLastWriteSize"),
        RequiredExport<MOCK_CopyLastWrite_t>(mockModule, "MOCK_CopyLastWrite")};
}

void VerifyLastWrite(const MockApi& mock, const std::vector<unsigned char>& expected)
{
    Require(mock.getLastWriteSize() == expected.size(), "mock write length changed");
    std::vector<unsigned char> actual(expected.size());
    const DWORD copied = mock.copyLastWrite(actual.data(), static_cast<DWORD>(actual.size()));
    Require(copied == actual.size(), "mock did not preserve complete write payload");
    Require(actual == expected, "mock received modified write payload");
}

FT_HANDLE Open(const ProxyApi& proxy)
{
    char serial[] = "MF2024";
    FT_HANDLE handle = nullptr;
    const FT_STATUS status = proxy.openEx(serial, FT_OPEN_BY_SERIAL_NUMBER, &handle);
    Require(status == FT_OK, "FT_OpenEx return value changed");
    Require(handle == reinterpret_cast<FT_HANDLE>(0x1234), "FT_OpenEx handle changed");
    return handle;
}

void RunNormal(const ProxyApi& proxy)
{
    DWORD deviceCount = 0;
    Require(
        proxy.listDevices(&deviceCount, nullptr, FT_LIST_NUMBER_ONLY) == FT_OK,
        "FT_ListDevices return value changed");
    Require(deviceCount == 1, "FT_ListDevices output changed");

    const FT_HANDLE handle = Open(proxy);
    const MockApi mock = LoadMockApi();
    Require(proxy.setBaudRate(handle, 115200) == FT_OK, "FT_SetBaudRate failed");
    Require(
        proxy.setDataCharacteristics(handle, 8, 1, 0) == FT_OK,
        "FT_SetDataCharacteristics failed");
    Require(
        proxy.setFlowControl(handle, 0x0100, 0x11, 0x13) == FT_OK,
        "FT_SetFlowControl failed");
    Require(proxy.setLatencyTimer(handle, 2) == FT_OK, "FT_SetLatencyTimer failed");
    Require(proxy.setBitMode(handle, 0xAA, 0x01) == FT_OK, "FT_SetBitMode failed");

    DWORD queueCount = 0;
    Require(proxy.getQueueStatus(handle, &queueCount) == FT_OK, "FT_GetQueueStatus failed");
    Require(queueCount == 4, "FT_GetQueueStatus output changed");

    const std::vector<unsigned char> writePayload{0x00, 0x10, 0x7F, 0x80, 0xFF};
    DWORD written = 0;
    Require(
        proxy.write(
            handle,
            const_cast<unsigned char*>(writePayload.data()),
            static_cast<DWORD>(writePayload.size()),
            &written) == FT_OK,
        "FT_Write return value changed");
    Require(written == writePayload.size(), "FT_Write byte count changed");
    VerifyLastWrite(mock, writePayload);

    std::array<unsigned char, 16> readBuffer{};
    readBuffer.fill(0xCC);
    DWORD read = 0;
    Require(
        proxy.read(handle, readBuffer.data(), static_cast<DWORD>(readBuffer.size()), &read) == FT_OK,
        "FT_Read return value changed");
    Require(read == 4, "FT_Read byte count changed");
    Require(
        std::equal(
            readBuffer.begin(),
            readBuffer.begin() + 4,
            std::array<unsigned char, 4>{0xDE, 0xAD, 0xBE, 0xEF}.begin()),
        "FT_Read payload changed");
    Require(readBuffer[4] == 0xCC, "FT_Read wrote beyond returned byte count");
    Require(proxy.close(handle) == FT_OK, "FT_Close return value changed");

    for (int index = 0; index < MockFunctionCount; ++index)
    {
        Require(
            mock.getCallCount(index) == 1,
            "a normal-path function was not forwarded exactly once: " + std::to_string(index));
    }
}

void RunInitializationFailure(const ProxyApi& proxy)
{
    DWORD count = 99;
    const FT_STATUS status = proxy.listDevices(&count, nullptr, FT_LIST_NUMBER_ONLY);
    Require(status == FT_OTHER_ERROR, "initialization failure did not fail safely");
    Require(count == 0, "initialization failure did not clear the count output");
}

void RunConcurrent(const ProxyApi& proxy)
{
    const FT_HANDLE handle = Open(proxy);
    const MockApi mock = LoadMockApi();
    mock.reset();
    constexpr int ThreadCount = 8;
    constexpr int CallsPerThread = 100;
    std::vector<std::thread> threads;
    for (int thread = 0; thread < ThreadCount; ++thread)
    {
        threads.emplace_back([&proxy, handle, CallsPerThread]() {
            for (int call = 0; call < CallsPerThread; ++call)
            {
                DWORD queue = 0;
                Require(
                    proxy.getQueueStatus(handle, &queue) == FT_OK && queue == 4,
                    "concurrent queue forwarding changed behavior");
            }
        });
    }
    for (auto& thread : threads)
    {
        thread.join();
    }
    Require(
        mock.getCallCount(MockGetQueueStatus) == ThreadCount * CallsPerThread,
        "concurrent calls were not forwarded exactly once");
    Require(proxy.close(handle) == FT_OK, "concurrent test close failed");
}

void RunReentrant(const ProxyApi& proxy)
{
    const FT_HANDLE handle = Open(proxy);
    std::array<unsigned char, 8> buffer{};
    DWORD read = 0;
    Require(
        proxy.read(handle, buffer.data(), static_cast<DWORD>(buffer.size()), &read) == FT_OK,
        "re-entrant mock read failed");
    Require(read == 4, "re-entrant read count changed");
    Require(proxy.close(handle) == FT_OK, "re-entrant test close failed");
}

void RunMockError(const ProxyApi& proxy)
{
    const FT_HANDLE handle = Open(proxy);
    const MockApi mock = LoadMockApi();
    const std::vector<unsigned char> payload{0x91, 0x92, 0x93};
    DWORD written = 0;
    Require(
        proxy.write(
            handle,
            const_cast<unsigned char*>(payload.data()),
            static_cast<DWORD>(payload.size()),
            &written) == FT_IO_ERROR,
        "mock error return was not preserved");
    Require(written == payload.size(), "mock error byte count was not preserved");
    VerifyLastWrite(mock, payload);
    Require(proxy.close(handle) == FT_OK, "mock error close failed");
}

void RunEmptyRead(const ProxyApi& proxy)
{
    const FT_HANDLE handle = Open(proxy);
    std::array<unsigned char, 8> buffer{};
    buffer.fill(0xA5);
    DWORD read = 99;
    Require(
        proxy.read(handle, buffer.data(), static_cast<DWORD>(buffer.size()), &read) == FT_OK,
        "empty read status changed");
    Require(read == 0, "empty read count changed");
    Require(
        std::all_of(buffer.begin(), buffer.end(), [](unsigned char value) { return value == 0xA5; }),
        "empty read changed the output buffer");
    Require(proxy.close(handle) == FT_OK, "empty read close failed");
}

void RunZeroWrite(const ProxyApi& proxy)
{
    const FT_HANDLE handle = Open(proxy);
    const MockApi mock = LoadMockApi();
    DWORD written = 99;
    Require(proxy.write(handle, nullptr, 0, &written) == FT_OK, "zero-byte write failed");
    Require(written == 0, "zero-byte write count changed");
    VerifyLastWrite(mock, {});
    Require(proxy.close(handle) == FT_OK, "zero write close failed");
}

void RunLargePayload(const ProxyApi& proxy)
{
    const FT_HANDLE handle = Open(proxy);
    const MockApi mock = LoadMockApi();
    std::vector<unsigned char> payload(1024 * 1024);
    for (size_t index = 0; index < payload.size(); ++index)
    {
        payload[index] = static_cast<unsigned char>(index & 0xFF);
    }
    DWORD written = 0;
    Require(
        proxy.write(handle, payload.data(), static_cast<DWORD>(payload.size()), &written) == FT_OK,
        "large write failed");
    Require(written == payload.size(), "large write count changed");
    VerifyLastWrite(mock, payload);
    Require(proxy.close(handle) == FT_OK, "large write close failed");
}
} // namespace

int wmain(const int argumentCount, wchar_t* arguments[])
{
    if (argumentCount != 3)
    {
        std::cerr << "usage: ftd2xx_proxy_test_host <mode> <proxy-path>\n";
        return 2;
    }

    const std::wstring mode = arguments[1];
    const ProxyApi proxy = LoadProxy(std::filesystem::absolute(arguments[2]));
    if (mode == L"missing_real" ||
        mode == L"missing_export" ||
        mode == L"failed_initialization")
    {
        RunInitializationFailure(proxy);
    }
    else if (mode == L"normal" ||
             mode == L"default_location" ||
             mode == L"logging_failure")
    {
        RunNormal(proxy);
    }
    else if (mode == L"concurrent")
    {
        RunConcurrent(proxy);
    }
    else if (mode == L"reentrant")
    {
        RunReentrant(proxy);
    }
    else if (mode == L"mock_error")
    {
        RunMockError(proxy);
    }
    else if (mode == L"empty_read")
    {
        RunEmptyRead(proxy);
    }
    else if (mode == L"zero_write")
    {
        RunZeroWrite(proxy);
    }
    else if (mode == L"large_payload")
    {
        RunLargePayload(proxy);
    }
    else
    {
        Fail("unknown test mode");
    }

    std::cout << "PASS: " << std::filesystem::path(mode).string() << '\n';
    return 0;
}
