cmake_minimum_required(VERSION 3.21)

project(MyPlasmFtd2xxProtocolRecorder VERSION 0.1.0 LANGUAGES CXX)

if(NOT WIN32 OR NOT MSVC)
    message(FATAL_ERROR "The protocol recorder requires Windows and an MSVC-compatible Win32 toolchain.")
endif()

if(NOT CMAKE_SIZEOF_VOID_P EQUAL 4)
    message(FATAL_ERROR "The protocol recorder must be configured for Win32/x86 (-A Win32).")
endif()

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

include(CTest)

add_library(ftd2xx_proxy SHARED
    proxy/proxy.cpp
    proxy/ftd2xx_proxy.def)
target_include_directories(ftd2xx_proxy PRIVATE include)
target_compile_definitions(ftd2xx_proxy PRIVATE
    UNICODE
    _UNICODE
    WIN32_LEAN_AND_MEAN
    NOMINMAX
    MYPLASM_PROXY_VERSION="${PROJECT_VERSION}")
target_compile_options(ftd2xx_proxy PRIVATE /W4 /WX /permissive- /EHsc)
target_link_libraries(ftd2xx_proxy PRIVATE ole32)
set_target_properties(ftd2xx_proxy PROPERTIES
    OUTPUT_NAME "ftd2xx"
    PREFIX "")

add_library(ftd2xx_mock SHARED
    mock/mock_ftd2xx.cpp
    mock/mock_ftd2xx.def)
target_include_directories(ftd2xx_mock PRIVATE include)
target_compile_definitions(ftd2xx_mock PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX)
target_compile_options(ftd2xx_mock PRIVATE /W4 /WX /permissive- /EHsc)
set_target_properties(ftd2xx_mock PROPERTIES
    OUTPUT_NAME "ftd2xx_real"
    PREFIX "")

add_library(ftd2xx_mock_missing_export SHARED
    mock/mock_ftd2xx.cpp
    mock/mock_ftd2xx_missing_export.def)
target_include_directories(ftd2xx_mock_missing_export PRIVATE include)
target_compile_definitions(ftd2xx_mock_missing_export PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX)
target_compile_options(ftd2xx_mock_missing_export PRIVATE /W4 /WX /permissive- /EHsc)
set_target_properties(ftd2xx_mock_missing_export PROPERTIES
    OUTPUT_NAME "ftd2xx_real_missing_export"
    PREFIX "")

add_executable(ftd2xx_proxy_test_host tests/test_host.cpp)
target_include_directories(ftd2xx_proxy_test_host PRIVATE include)
target_compile_definitions(ftd2xx_proxy_test_host PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX)
target_compile_options(ftd2xx_proxy_test_host PRIVATE /W4 /WX /permissive- /EHsc)

if(BUILD_TESTING)
    find_program(POWERSHELL_EXECUTABLE NAMES pwsh powershell REQUIRED)

    foreach(case_name IN ITEMS
        normal
        default_location
        missing_real
        missing_export
        failed_initialization
        reentrant
        concurrent
        stress
        flush_time
        logging_failure
        mock_error
        empty_read
        zero_write
        large_payload)
        add_test(
            NAME "proxy_${case_name}"
            COMMAND "${POWERSHELL_EXECUTABLE}"
                -NoProfile
                -ExecutionPolicy Bypass
                -File "${CMAKE_CURRENT_SOURCE_DIR}/tests/run-case.ps1"
                -CaseName "${case_name}"
                -ProxyDll "$<TARGET_FILE:ftd2xx_proxy>"
                -MockDll "$<TARGET_FILE:ftd2xx_mock>"
                -MissingExportMockDll "$<TARGET_FILE:ftd2xx_mock_missing_export>"
                -TestHost "$<TARGET_FILE:ftd2xx_proxy_test_host>"
                -WorkingRoot "${CMAKE_CURRENT_BINARY_DIR}/test-runs")
    endforeach()

    add_test(
        NAME proxy_exact_exports
        COMMAND "${POWERSHELL_EXECUTABLE}"
            -NoProfile
            -ExecutionPolicy Bypass
            -File "${CMAKE_CURRENT_SOURCE_DIR}/tests/verify-exports.ps1"
            -DllPath "$<TARGET_FILE:ftd2xx_proxy>")

    add_test(
        NAME proxy_install_restore_scripts
        COMMAND "${POWERSHELL_EXECUTABLE}"
            -NoProfile
            -ExecutionPolicy Bypass
            -File "${CMAKE_CURRENT_SOURCE_DIR}/tests/test-install-restore.ps1"
            -ProxyDll "$<TARGET_FILE:ftd2xx_proxy>"
            -MockDll "$<TARGET_FILE:ftd2xx_mock>"
            -InstallScript "${CMAKE_CURRENT_SOURCE_DIR}/../install-proxy.ps1"
            -RestoreScript "${CMAKE_CURRENT_SOURCE_DIR}/../restore-original.ps1"
            -WorkingRoot "${CMAKE_CURRENT_BINARY_DIR}/install-restore-tests")
endif()
