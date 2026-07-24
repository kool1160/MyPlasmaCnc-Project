#include "ftd2xx_api.h"

#include <atomic>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <iomanip>
#include <mutex>
#include <sstream>
#include <string>
#include <unordered_map>
#include <vector>

#include <objbase.h>

namespace
{
struct RealApi
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

INIT_ONCE g_apiInitOnce = INIT_ONCE_STATIC_INIT;
RealApi g_api;
bool g_apiReady = false;
thread_local bool g_insideApiInitialization = false;

INIT_ONCE g_loggerInitOnce = INIT_ONCE_STATIC_INIT;
HANDLE g_logFile = INVALID_HANDLE_VALUE;
std::wstring g_logPath;
std::string g_sessionId = "uninitialized";
LARGE_INTEGER g_startCounter{};
LARGE_INTEGER g_counterFrequency{};
ULONGLONG g_lastFlushTick = 0;
unsigned long long g_bufferedBytes = 0;
SRWLOCK g_logLock = SRWLOCK_INIT;
std::atomic<unsigned long long> g_sequence{0};
thread_local bool g_insideLogger = false;

constexpr unsigned long long LogFlushByteThreshold = 64ULL * 1024ULL;
constexpr ULONGLONG LogFlushIntervalMilliseconds = 1000;

SRWLOCK g_handleLock = SRWLOCK_INIT;
std::unordered_map<FT_HANDLE, unsigned long long> g_handleIds;
std::atomic<unsigned long long> g_nextHandleId{1};

class SharedLockGuard
{
public:
    explicit SharedLockGuard(SRWLOCK& lock) noexcept : lock_(lock)
    {
        AcquireSRWLockShared(&lock_);
    }

    ~SharedLockGuard()
    {
        ReleaseSRWLockShared(&lock_);
    }

    SharedLockGuard(const SharedLockGuard&) = delete;
    SharedLockGuard& operator=(const SharedLockGuard&) = delete;

private:
    SRWLOCK& lock_;
};

class ExclusiveLockGuard
{
public:
    explicit ExclusiveLockGuard(SRWLOCK& lock) noexcept : lock_(lock)
    {
        AcquireSRWLockExclusive(&lock_);
    }

    ~ExclusiveLockGuard()
    {
        ReleaseSRWLockExclusive(&lock_);
    }

    ExclusiveLockGuard(const ExclusiveLockGuard&) = delete;
    ExclusiveLockGuard& operator=(const ExclusiveLockGuard&) = delete;

private:
    SRWLOCK& lock_;
};

std::wstring ParentDirectory(const std::wstring& path)
{
    return std::filesystem::path(path).parent_path().wstring();
}

std::wstring CurrentModulePath()
{
    HMODULE module = nullptr;
    const auto address = reinterpret_cast<LPCWSTR>(&CurrentModulePath);
    if (!GetModuleHandleExW(
            GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
            address,
            &module))
    {
        return {};
    }

    std::vector<wchar_t> buffer(32768);
    const DWORD length = GetModuleFileNameW(module, buffer.data(), static_cast<DWORD>(buffer.size()));
    if (length == 0 || length >= buffer.size())
    {
        return {};
    }

    return std::wstring(buffer.data(), length);
}

template <typename T>
bool Resolve(HMODULE module, const char* name, T& function)
{
    function = reinterpret_cast<T>(GetProcAddress(module, name));
    return function != nullptr;
}

BOOL CALLBACK InitializeApi(PINIT_ONCE, PVOID, PVOID*)
{
    try
    {
        const std::wstring proxyPath = CurrentModulePath();
        if (proxyPath.empty())
        {
            return TRUE;
        }

        const std::filesystem::path realPath =
            std::filesystem::path(ParentDirectory(proxyPath)) / L"ftd2xx_real.dll";
        g_api.module = LoadLibraryExW(
            realPath.c_str(),
            nullptr,
            LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
        if (g_api.module == nullptr)
        {
            return TRUE;
        }

        const bool complete =
            Resolve(g_api.module, "FT_ListDevices", g_api.listDevices) &&
            Resolve(g_api.module, "FT_OpenEx", g_api.openEx) &&
            Resolve(g_api.module, "FT_Close", g_api.close) &&
            Resolve(g_api.module, "FT_Read", g_api.read) &&
            Resolve(g_api.module, "FT_Write", g_api.write) &&
            Resolve(g_api.module, "FT_SetBaudRate", g_api.setBaudRate) &&
            Resolve(g_api.module, "FT_SetDataCharacteristics", g_api.setDataCharacteristics) &&
            Resolve(g_api.module, "FT_SetFlowControl", g_api.setFlowControl) &&
            Resolve(g_api.module, "FT_GetQueueStatus", g_api.getQueueStatus) &&
            Resolve(g_api.module, "FT_SetLatencyTimer", g_api.setLatencyTimer) &&
            Resolve(g_api.module, "FT_SetBitMode", g_api.setBitMode);
        if (!complete)
        {
            FreeLibrary(g_api.module);
            g_api = {};
            return TRUE;
        }

        g_apiReady = true;
    }
    catch (...)
    {
        g_apiReady = false;
    }

    return TRUE;
}

bool EnsureApi()
{
    if (g_insideApiInitialization)
    {
        return false;
    }

    g_insideApiInitialization = true;
    const BOOL initialized =
        InitOnceExecuteOnce(&g_apiInitOnce, InitializeApi, nullptr, nullptr);
    g_insideApiInitialization = false;
    return initialized != FALSE && g_apiReady;
}

std::string GuidString()
{
    GUID guid{};
    if (CoCreateGuid(&guid) != S_OK)
    {
        std::ostringstream fallback;
        fallback << GetCurrentProcessId() << '-' << GetTickCount64();
        return fallback.str();
    }

    char buffer[64]{};
    std::snprintf(
        buffer,
        sizeof(buffer),
        "%08lX-%04hX-%04hX-%02hhX%02hhX-%02hhX%02hhX%02hhX%02hhX%02hhX%02hhX",
        guid.Data1,
        guid.Data2,
        guid.Data3,
        guid.Data4[0],
        guid.Data4[1],
        guid.Data4[2],
        guid.Data4[3],
        guid.Data4[4],
        guid.Data4[5],
        guid.Data4[6],
        guid.Data4[7]);
    return buffer;
}

std::wstring UtcDirectoryStamp()
{
    SYSTEMTIME time{};
    GetSystemTime(&time);
    wchar_t buffer[32]{};
    swprintf_s(
        buffer,
        L"%04u%02u%02uT%02u%02u%02u.%03uZ-%lu",
        time.wYear,
        time.wMonth,
        time.wDay,
        time.wHour,
        time.wMinute,
        time.wSecond,
        time.wMilliseconds,
        GetCurrentProcessId());
    return buffer;
}

BOOL CALLBACK InitializeLogger(PINIT_ONCE, PVOID, PVOID*)
{
    try
    {
        g_sessionId = GuidString();
        QueryPerformanceCounter(&g_startCounter);
        QueryPerformanceFrequency(&g_counterFrequency);
        g_lastFlushTick = GetTickCount64();

        std::wstring captureDirectory;
        const DWORD overrideLength = GetEnvironmentVariableW(L"MYPLASM_PROXY_LOG_DIR", nullptr, 0);
        if (overrideLength > 1)
        {
            std::vector<wchar_t> overrideBuffer(overrideLength);
            if (GetEnvironmentVariableW(
                    L"MYPLASM_PROXY_LOG_DIR",
                    overrideBuffer.data(),
                    static_cast<DWORD>(overrideBuffer.size())) > 0)
            {
                captureDirectory = overrideBuffer.data();
            }
        }
        else
        {
            const DWORD localLength = GetEnvironmentVariableW(L"LOCALAPPDATA", nullptr, 0);
            if (localLength <= 1)
            {
                return TRUE;
            }

            std::vector<wchar_t> localBuffer(localLength);
            if (GetEnvironmentVariableW(
                    L"LOCALAPPDATA",
                    localBuffer.data(),
                    static_cast<DWORD>(localBuffer.size())) == 0)
            {
                return TRUE;
            }

            captureDirectory =
                (std::filesystem::path(localBuffer.data()) /
                 L"MyPlasmProtocolRecorder" /
                 L"captures" /
                 UtcDirectoryStamp())
                    .wstring();
        }

        std::filesystem::create_directories(captureDirectory);
        g_logPath = (std::filesystem::path(captureDirectory) / L"traffic.jsonl").wstring();
        g_logFile = CreateFileW(
            g_logPath.c_str(),
            FILE_APPEND_DATA,
            FILE_SHARE_READ,
            nullptr,
            OPEN_ALWAYS,
            FILE_ATTRIBUTE_NORMAL,
            nullptr);
    }
    catch (...)
    {
        g_logFile = INVALID_HANDLE_VALUE;
    }

    return TRUE;
}

std::string JsonEscape(const std::string& value)
{
    std::ostringstream output;
    for (const unsigned char character : value)
    {
        switch (character)
        {
        case '"':
            output << "\\\"";
            break;
        case '\\':
            output << "\\\\";
            break;
        case '\b':
            output << "\\b";
            break;
        case '\f':
            output << "\\f";
            break;
        case '\n':
            output << "\\n";
            break;
        case '\r':
            output << "\\r";
            break;
        case '\t':
            output << "\\t";
            break;
        default:
            if (character < 0x20 || character > 0x7E)
            {
                output << "\\u" << std::hex << std::setw(4) << std::setfill('0')
                       << static_cast<unsigned int>(character) << std::dec;
            }
            else
            {
                output << character;
            }
        }
    }

    return output.str();
}

std::string UtcTimestamp()
{
    SYSTEMTIME time{};
    GetSystemTime(&time);
    char buffer[40]{};
    std::snprintf(
        buffer,
        sizeof(buffer),
        "%04u-%02u-%02uT%02u:%02u:%02u.%03uZ",
        time.wYear,
        time.wMonth,
        time.wDay,
        time.wHour,
        time.wMinute,
        time.wSecond,
        time.wMilliseconds);
    return buffer;
}

unsigned long long ElapsedMicroseconds()
{
    LARGE_INTEGER now{};
    QueryPerformanceCounter(&now);
    if (g_counterFrequency.QuadPart <= 0)
    {
        return 0;
    }

    return static_cast<unsigned long long>(
        (now.QuadPart - g_startCounter.QuadPart) * 1000000LL / g_counterFrequency.QuadPart);
}

std::string Hex(const void* data, const DWORD length)
{
    if (data == nullptr || length == 0)
    {
        return {};
    }

    const auto* bytes = static_cast<const unsigned char*>(data);
    static constexpr char digits[] = "0123456789ABCDEF";
    std::string result;
    result.resize(static_cast<size_t>(length) * 2);
    for (DWORD index = 0; index < length; ++index)
    {
        result[static_cast<size_t>(index) * 2] = digits[bytes[index] >> 4];
        result[static_cast<size_t>(index) * 2 + 1] = digits[bytes[index] & 0x0F];
    }

    return result;
}

std::string PointerText(const void* pointer)
{
    std::ostringstream output;
    output << "0x" << std::uppercase << std::hex
           << reinterpret_cast<std::uintptr_t>(pointer);
    return output.str();
}

std::string ReadAnsiArgument(const void* pointer)
{
    if (pointer == nullptr)
    {
        return {};
    }

    char buffer[512]{};
    SIZE_T bytesRead = 0;
    if (!ReadProcessMemory(
            GetCurrentProcess(),
            pointer,
            buffer,
            sizeof(buffer) - 1,
            &bytesRead) &&
        bytesRead == 0)
    {
        return {};
    }

    buffer[sizeof(buffer) - 1] = '\0';
    return std::string(buffer, strnlen_s(buffer, sizeof(buffer)));
}

unsigned long long HandleId(const FT_HANDLE handle)
{
    if (handle == nullptr)
    {
        return 0;
    }

    const SharedLockGuard guard(g_handleLock);
    const auto found = g_handleIds.find(handle);
    return found == g_handleIds.end() ? 0 : found->second;
}

unsigned long long RegisterHandle(const FT_HANDLE handle) noexcept
{
    if (handle == nullptr)
    {
        return 0;
    }

    try
    {
        const ExclusiveLockGuard guard(g_handleLock);
        const auto existing = g_handleIds.find(handle);
        if (existing != g_handleIds.end())
        {
            return existing->second;
        }

        const unsigned long long id = g_nextHandleId.fetch_add(1);
        g_handleIds.emplace(handle, id);
        return id;
    }
    catch (...)
    {
        return 0;
    }
}

void UnregisterHandle(const FT_HANDLE handle) noexcept
{
    try
    {
        const ExclusiveLockGuard guard(g_handleLock);
        g_handleIds.erase(handle);
    }
    catch (...)
    {
    }
}

void LogRecord(
    const char* functionName,
    const unsigned long long handleId,
    const FT_STATUS status,
    const std::string& applicableFields)
{
    if (g_insideLogger)
    {
        return;
    }

    g_insideLogger = true;
    try
    {
        InitOnceExecuteOnce(&g_loggerInitOnce, InitializeLogger, nullptr, nullptr);
        if (g_logFile == INVALID_HANDLE_VALUE)
        {
            g_insideLogger = false;
            return;
        }

        const ExclusiveLockGuard guard(g_logLock);
        const unsigned long long sequence = g_sequence.fetch_add(1) + 1;
        std::ostringstream recordPrefix;
        recordPrefix
            << "{\"schema_version\":1"
            << ",\"session_id\":\"" << JsonEscape(g_sessionId) << "\""
            << ",\"utc_timestamp\":\"" << UtcTimestamp() << "\""
            << ",\"elapsed_us\":" << ElapsedMicroseconds()
            << ",\"process_id\":" << GetCurrentProcessId()
            << ",\"thread_id\":" << GetCurrentThreadId()
            << ",\"function\":\"" << functionName << "\""
            << ",\"sequence\":" << sequence
            << ",\"handle_id\":" << handleId
            << ",\"status\":" << status
            << applicableFields;

        const ULONGLONG now = GetTickCount64();
        const auto projectedBytes =
            g_bufferedBytes + static_cast<unsigned long long>(recordPrefix.tellp()) + 64ULL;
        const char* flushTrigger = "none";
        if (std::strcmp(functionName, "FT_Close") == 0)
        {
            flushTrigger = "close";
        }
        else if (projectedBytes >= LogFlushByteThreshold)
        {
            flushTrigger = "byte_threshold";
        }
        else if (now - g_lastFlushTick >= LogFlushIntervalMilliseconds)
        {
            flushTrigger = "time_threshold";
        }

        recordPrefix << ",\"flush_trigger\":\"" << flushTrigger << "\"}\r\n";
        const std::string line = recordPrefix.str();
        size_t offset = 0;
        while (offset < line.size())
        {
            const size_t remaining = line.size() - offset;
            const DWORD request = static_cast<DWORD>(
                std::min<size_t>(remaining, static_cast<size_t>(MAXDWORD)));
            DWORD written = 0;
            if (!WriteFile(
                    g_logFile,
                    line.data() + offset,
                    request,
                    &written,
                    nullptr) ||
                written == 0)
            {
                break;
            }
            offset += written;
        }
        g_bufferedBytes += static_cast<unsigned long long>(offset);

        if (std::strcmp(flushTrigger, "none") != 0 &&
            FlushFileBuffers(g_logFile) != FALSE)
        {
            g_bufferedBytes = 0;
            g_lastFlushTick = GetTickCount64();
        }
    }
    catch (...)
    {
        // Logging is deliberately best effort and cannot affect forwarding.
    }
    g_insideLogger = false;
}

template <typename Builder>
void LogSafely(
    const char* functionName,
    const unsigned long long handleId,
    const FT_STATUS status,
    Builder&& builder) noexcept
{
    try
    {
        std::ostringstream fields;
        builder(fields);
        LogRecord(functionName, handleId, status, fields.str());
    }
    catch (...)
    {
    }
}

void SetDwordOutput(LPDWORD output, const DWORD value)
{
    if (output != nullptr)
    {
        *output = value;
    }
}
} // namespace

extern "C" FT_STATUS WINAPI FT_ListDevices(PVOID argument1, PVOID argument2, DWORD flags)
{
    FT_STATUS status = FT_OTHER_ERROR;
    if (EnsureApi())
    {
        status = g_api.listDevices(argument1, argument2, flags);
    }
    else if ((flags & FT_LIST_NUMBER_ONLY) != 0)
    {
        SetDwordOutput(static_cast<LPDWORD>(argument1), 0);
    }

    DWORD reportedCount = 0;
    if ((flags & FT_LIST_NUMBER_ONLY) != 0 && argument1 != nullptr)
    {
        reportedCount = *static_cast<LPDWORD>(argument1);
    }
    LogSafely("FT_ListDevices", 0, status, [&](std::ostringstream& fields) {
        fields << ",\"arguments\":{\"argument1\":\"" << PointerText(argument1)
               << "\",\"argument2\":\"" << PointerText(argument2)
               << "\",\"flags\":" << flags << "}"
               << ",\"device_count\":" << reportedCount;
    });
    return status;
}

extern "C" FT_STATUS WINAPI FT_OpenEx(PVOID selector, DWORD flags, FT_HANDLE* handle)
{
    FT_STATUS status = FT_OTHER_ERROR;
    if (EnsureApi())
    {
        status = g_api.openEx(selector, flags, handle);
    }
    else if (handle != nullptr)
    {
        *handle = nullptr;
    }

    const FT_HANDLE returnedHandle =
        status == FT_OK && handle != nullptr ? *handle : nullptr;
    const unsigned long long id =
        status == FT_OK ? RegisterHandle(returnedHandle) : 0;
    LogSafely("FT_OpenEx", id, status, [&](std::ostringstream& fields) {
        fields << ",\"arguments\":{\"selector_pointer\":\"" << PointerText(selector)
               << "\",\"selector\":\"" << JsonEscape(ReadAnsiArgument(selector))
               << "\",\"flags\":" << flags << "}"
               << ",\"returned_handle\":\"" << PointerText(returnedHandle) << "\"";
    });
    return status;
}

extern "C" FT_STATUS WINAPI FT_Close(FT_HANDLE handle)
{
    const unsigned long long id = HandleId(handle);
    FT_STATUS status = FT_OTHER_ERROR;
    if (EnsureApi())
    {
        status = g_api.close(handle);
    }

    LogSafely("FT_Close", id, status, [&](std::ostringstream& fields) {
        fields << ",\"arguments\":{\"handle\":\"" << PointerText(handle) << "\"}";
    });
    if (status == FT_OK)
    {
        UnregisterHandle(handle);
    }
    return status;
}

extern "C" FT_STATUS WINAPI FT_Read(
    FT_HANDLE handle,
    LPVOID buffer,
    DWORD requestedCount,
    LPDWORD actualCount)
{
    FT_STATUS status = FT_OTHER_ERROR;
    if (EnsureApi())
    {
        status = g_api.read(handle, buffer, requestedCount, actualCount);
    }
    else
    {
        SetDwordOutput(actualCount, 0);
    }

    const DWORD actual = actualCount == nullptr ? 0 : *actualCount;
    const DWORD safePayloadLength = actual <= requestedCount ? actual : requestedCount;
    LogSafely("FT_Read", HandleId(handle), status, [&](std::ostringstream& fields) {
        fields << ",\"arguments\":{\"buffer\":\"" << PointerText(buffer) << "\"}"
               << ",\"requested_count\":" << requestedCount
               << ",\"actual_count\":" << actual
               << ",\"read_hex\":\"" << Hex(buffer, safePayloadLength) << "\"";
    });
    return status;
}

extern "C" FT_STATUS WINAPI FT_Write(
    FT_HANDLE handle,
    LPVOID buffer,
    DWORD requestedCount,
    LPDWORD actualCount)
{
    FT_STATUS status = FT_OTHER_ERROR;
    if (EnsureApi())
    {
        status = g_api.write(handle, buffer, requestedCount, actualCount);
    }
    else
    {
        SetDwordOutput(actualCount, 0);
    }

    const DWORD actual = actualCount == nullptr ? 0 : *actualCount;
    LogSafely("FT_Write", HandleId(handle), status, [&](std::ostringstream& fields) {
        fields << ",\"arguments\":{\"buffer\":\"" << PointerText(buffer) << "\"}"
               << ",\"requested_count\":" << requestedCount
               << ",\"actual_count\":" << actual
               << ",\"write_hex\":\"" << Hex(buffer, requestedCount) << "\"";
    });
    return status;
}

extern "C" FT_STATUS WINAPI FT_SetBaudRate(FT_HANDLE handle, ULONG baudRate)
{
    const FT_STATUS status =
        EnsureApi() ? g_api.setBaudRate(handle, baudRate) : FT_OTHER_ERROR;
    LogSafely("FT_SetBaudRate", HandleId(handle), status, [&](std::ostringstream& fields) {
        fields << ",\"arguments\":{\"baud_rate\":" << baudRate << "}"
               << ",\"baud_rate\":" << baudRate;
    });
    return status;
}

extern "C" FT_STATUS WINAPI FT_SetDataCharacteristics(
    FT_HANDLE handle,
    UCHAR wordLength,
    UCHAR stopBits,
    UCHAR parity)
{
    const FT_STATUS status = EnsureApi()
        ? g_api.setDataCharacteristics(handle, wordLength, stopBits, parity)
        : FT_OTHER_ERROR;
    LogSafely(
        "FT_SetDataCharacteristics",
        HandleId(handle),
        status,
        [&](std::ostringstream& fields) {
            fields << ",\"arguments\":{\"word_length\":"
                   << static_cast<unsigned int>(wordLength)
                   << ",\"stop_bits\":" << static_cast<unsigned int>(stopBits)
                   << ",\"parity\":" << static_cast<unsigned int>(parity) << "}"
                   << ",\"data_characteristics\":{\"word_length\":"
                   << static_cast<unsigned int>(wordLength)
                   << ",\"stop_bits\":" << static_cast<unsigned int>(stopBits)
                   << ",\"parity\":" << static_cast<unsigned int>(parity) << "}";
        });
    return status;
}

extern "C" FT_STATUS WINAPI FT_SetFlowControl(
    FT_HANDLE handle,
    USHORT flowControl,
    UCHAR xon,
    UCHAR xoff)
{
    const FT_STATUS status = EnsureApi()
        ? g_api.setFlowControl(handle, flowControl, xon, xoff)
        : FT_OTHER_ERROR;
    LogSafely("FT_SetFlowControl", HandleId(handle), status, [&](std::ostringstream& fields) {
        fields << ",\"arguments\":{\"flow_control\":" << flowControl
               << ",\"xon\":" << static_cast<unsigned int>(xon)
               << ",\"xoff\":" << static_cast<unsigned int>(xoff) << "}"
               << ",\"flow_control\":{\"mode\":" << flowControl
               << ",\"xon\":" << static_cast<unsigned int>(xon)
               << ",\"xoff\":" << static_cast<unsigned int>(xoff) << "}";
    });
    return status;
}

extern "C" FT_STATUS WINAPI FT_GetQueueStatus(FT_HANDLE handle, LPDWORD queueCount)
{
    FT_STATUS status = FT_OTHER_ERROR;
    if (EnsureApi())
    {
        status = g_api.getQueueStatus(handle, queueCount);
    }
    else
    {
        SetDwordOutput(queueCount, 0);
    }
    const DWORD count = queueCount == nullptr ? 0 : *queueCount;
    LogSafely("FT_GetQueueStatus", HandleId(handle), status, [&](std::ostringstream& fields) {
        fields << ",\"arguments\":{}"
               << ",\"queue_count\":" << count;
    });
    return status;
}

extern "C" FT_STATUS WINAPI FT_SetLatencyTimer(FT_HANDLE handle, UCHAR latency)
{
    const FT_STATUS status =
        EnsureApi() ? g_api.setLatencyTimer(handle, latency) : FT_OTHER_ERROR;
    LogSafely("FT_SetLatencyTimer", HandleId(handle), status, [&](std::ostringstream& fields) {
        fields << ",\"arguments\":{\"latency_timer\":"
               << static_cast<unsigned int>(latency) << "}"
               << ",\"latency_timer\":" << static_cast<unsigned int>(latency);
    });
    return status;
}

extern "C" FT_STATUS WINAPI FT_SetBitMode(FT_HANDLE handle, UCHAR mask, UCHAR mode)
{
    const FT_STATUS status =
        EnsureApi() ? g_api.setBitMode(handle, mask, mode) : FT_OTHER_ERROR;
    LogSafely("FT_SetBitMode", HandleId(handle), status, [&](std::ostringstream& fields) {
        fields << ",\"arguments\":{\"mask\":" << static_cast<unsigned int>(mask)
               << ",\"mode\":" << static_cast<unsigned int>(mode) << "}"
               << ",\"bit_mode\":{\"mask\":" << static_cast<unsigned int>(mask)
               << ",\"mode\":" << static_cast<unsigned int>(mode) << "}";
    });
    return status;
}
