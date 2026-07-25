#pragma once

#include <Windows.h>

enum MockFunctionIndex
{
    MockListDevices = 0,
    MockOpenEx = 1,
    MockClose = 2,
    MockRead = 3,
    MockWrite = 4,
    MockSetBaudRate = 5,
    MockSetDataCharacteristics = 6,
    MockSetFlowControl = 7,
    MockGetQueueStatus = 8,
    MockSetLatencyTimer = 9,
    MockSetBitMode = 10,
    MockFunctionCount = 11
};

using MOCK_Reset_t = void(__cdecl*)();
using MOCK_GetCallCount_t = ULONG(__cdecl*)(int);
using MOCK_GetLastWriteSize_t = DWORD(__cdecl*)();
using MOCK_CopyLastWrite_t = DWORD(__cdecl*)(LPVOID, DWORD);
