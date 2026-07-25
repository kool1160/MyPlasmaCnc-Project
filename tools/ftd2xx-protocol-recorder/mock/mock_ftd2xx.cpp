#include "ftd2xx_api.h"
#include "mock_contract.h"

#include <algorithm>
#include <array>
#include <atomic>
#include <cstring>
#include <mutex>
#include <vector>

namespace
{
std::array<std::atomic<ULONG>, MockFunctionCount> g_callCounts{};
std::mutex g_writeMutex;
std::vector<unsigned char> g_lastWrite;
const FT_HANDLE MockHandle = reinterpret_cast<FT_HANDLE>(0x1234);
constexpr std::array<unsigned char, 4> ReadPayload{0xDE, 0xAD, 0xBE, 0xEF};

bool EnvironmentFlag(const wchar_t* name)
{
    wchar_t value[8]{};
    return GetEnvironmentVariableW(name, value, static_cast<DWORD>(std::size(value))) > 0 &&
        value[0] == L'1';
}

void Count(const MockFunctionIndex function)
{
    g_callCounts[function].fetch_add(1);
}
} // namespace

extern "C" void __cdecl MOCK_Reset()
{
    for (auto& count : g_callCounts)
    {
        count.store(0);
    }

    std::lock_guard<std::mutex> guard(g_writeMutex);
    g_lastWrite.clear();
}

extern "C" ULONG __cdecl MOCK_GetCallCount(const int function)
{
    if (function < 0 || function >= MockFunctionCount)
    {
        return 0;
    }

    return g_callCounts[static_cast<size_t>(function)].load();
}

extern "C" DWORD __cdecl MOCK_GetLastWriteSize()
{
    std::lock_guard<std::mutex> guard(g_writeMutex);
    return static_cast<DWORD>(g_lastWrite.size());
}

extern "C" DWORD __cdecl MOCK_CopyLastWrite(LPVOID buffer, const DWORD bufferLength)
{
    std::lock_guard<std::mutex> guard(g_writeMutex);
    const DWORD copyLength =
        std::min(bufferLength, static_cast<DWORD>(g_lastWrite.size()));
    if (buffer != nullptr && copyLength > 0)
    {
        std::copy_n(g_lastWrite.data(), copyLength, static_cast<unsigned char*>(buffer));
    }

    return copyLength;
}

extern "C" FT_STATUS WINAPI FT_ListDevices(PVOID argument1, PVOID, DWORD flags)
{
    Count(MockListDevices);
    if (flags != FT_LIST_NUMBER_ONLY)
    {
        return FT_IO_ERROR;
    }
    if ((flags & FT_LIST_NUMBER_ONLY) != 0 && argument1 != nullptr)
    {
        *static_cast<LPDWORD>(argument1) = 1;
    }

    return FT_OK;
}

extern "C" FT_STATUS WINAPI FT_OpenEx(PVOID selector, DWORD flags, FT_HANDLE* handle)
{
    Count(MockOpenEx);
    if (selector == nullptr ||
        std::strcmp(static_cast<const char*>(selector), "MF2024") != 0 ||
        flags != FT_OPEN_BY_SERIAL_NUMBER)
    {
        return FT_IO_ERROR;
    }
    if (handle != nullptr)
    {
        *handle = MockHandle;
    }

    return FT_OK;
}

extern "C" FT_STATUS WINAPI FT_Close(FT_HANDLE handle)
{
    Count(MockClose);
    return handle == MockHandle ? FT_OK : FT_IO_ERROR;
}

extern "C" FT_STATUS WINAPI FT_Read(
    FT_HANDLE handle,
    LPVOID buffer,
    const DWORD requestedCount,
    LPDWORD actualCount)
{
    Count(MockRead);
    if (handle != MockHandle)
    {
        return FT_IO_ERROR;
    }
    const DWORD returnedCount = EnvironmentFlag(L"MOCK_EMPTY_READ")
        ? 0
        : std::min(requestedCount, static_cast<DWORD>(ReadPayload.size()));
    if (buffer != nullptr && returnedCount > 0)
    {
        std::copy_n(ReadPayload.data(), returnedCount, static_cast<unsigned char*>(buffer));
    }
    if (actualCount != nullptr)
    {
        *actualCount = returnedCount;
    }

    if (EnvironmentFlag(L"MOCK_REENTER"))
    {
        const HMODULE proxy = GetModuleHandleW(L"ftd2xx.dll");
        const auto queueFunction = proxy == nullptr
            ? nullptr
            : reinterpret_cast<FT_GetQueueStatus_t>(
                  GetProcAddress(proxy, "FT_GetQueueStatus"));
        DWORD queue = 0;
        if (queueFunction == nullptr ||
            queueFunction(handle, &queue) != FT_OK ||
            queue != ReadPayload.size())
        {
            return FT_IO_ERROR;
        }
    }
    return FT_OK;
}

extern "C" FT_STATUS WINAPI FT_Write(
    FT_HANDLE handle,
    LPVOID buffer,
    const DWORD requestedCount,
    LPDWORD actualCount)
{
    Count(MockWrite);
    if (handle != MockHandle)
    {
        return FT_IO_ERROR;
    }
    {
        std::lock_guard<std::mutex> guard(g_writeMutex);
        if (buffer == nullptr || requestedCount == 0)
        {
            g_lastWrite.clear();
        }
        else
        {
            const auto* bytes = static_cast<const unsigned char*>(buffer);
            g_lastWrite.assign(bytes, bytes + requestedCount);
        }
    }

    if (actualCount != nullptr)
    {
        *actualCount = requestedCount;
    }
    return EnvironmentFlag(L"MOCK_FTDI_ERROR") ? FT_IO_ERROR : FT_OK;
}

extern "C" FT_STATUS WINAPI FT_SetBaudRate(FT_HANDLE handle, ULONG baudRate)
{
    Count(MockSetBaudRate);
    return handle == MockHandle && baudRate == 115200 ? FT_OK : FT_IO_ERROR;
}

extern "C" FT_STATUS WINAPI FT_SetDataCharacteristics(
    FT_HANDLE handle,
    UCHAR wordLength,
    UCHAR stopBits,
    UCHAR parity)
{
    Count(MockSetDataCharacteristics);
    return handle == MockHandle && wordLength == 8 && stopBits == 1 && parity == 0
        ? FT_OK
        : FT_IO_ERROR;
}

extern "C" FT_STATUS WINAPI FT_SetFlowControl(
    FT_HANDLE handle,
    USHORT flowControl,
    UCHAR xon,
    UCHAR xoff)
{
    Count(MockSetFlowControl);
    return handle == MockHandle &&
            flowControl == 0x0100 &&
            xon == 0x11 &&
            xoff == 0x13
        ? FT_OK
        : FT_IO_ERROR;
}

extern "C" FT_STATUS WINAPI FT_GetQueueStatus(FT_HANDLE handle, LPDWORD queueCount)
{
    Count(MockGetQueueStatus);
    if (handle != MockHandle)
    {
        return FT_IO_ERROR;
    }
    if (queueCount != nullptr)
    {
        *queueCount = static_cast<DWORD>(ReadPayload.size());
    }
    return FT_OK;
}

extern "C" FT_STATUS WINAPI FT_SetLatencyTimer(FT_HANDLE handle, UCHAR latency)
{
    Count(MockSetLatencyTimer);
    return handle == MockHandle && latency == 2 ? FT_OK : FT_IO_ERROR;
}

extern "C" FT_STATUS WINAPI FT_SetBitMode(FT_HANDLE handle, UCHAR mask, UCHAR mode)
{
    Count(MockSetBitMode);
    return handle == MockHandle && mask == 0xAA && mode == 0x01
        ? FT_OK
        : FT_IO_ERROR;
}
