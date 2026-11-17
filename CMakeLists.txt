# Windows build of the C engine (macOS uses Package.swift / scripts/check_portable.sh).
# Toolchain: clang-cl from Visual Studio (the engine needs C11 <stdatomic.h> incl. _Atomic double).
#   scripts\build_win.ps1            (sets up the VS environment, configures and builds)
# Targets:
#   playercore_stub  static engine with the stub back-ends (-DPC_STUB_BACKENDS)
#   pcdump           plain-C dump tool on top of it (scripts/regress.sh drives it)
cmake_minimum_required(VERSION 3.21)
project(SurroundPlayerEngine C)

if(NOT CMAKE_C_COMPILER_ID STREQUAL "Clang")
    message(WARNING "Expected clang-cl; MSVC's C11 atomics are experimental")
endif()

set(FFMPEG_PREFIX "${CMAKE_SOURCE_DIR}/vendor/ffmpeg/windows-x64" CACHE PATH
    "FFmpeg install prefix (scripts/build_ffmpeg_win.ps1)")
if(NOT EXISTS "${FFMPEG_PREFIX}/include/libavformat/avformat.h")
    message(FATAL_ERROR "FFmpeg not found in ${FFMPEG_PREFIX}: run scripts\\build_ffmpeg_win.ps1")
endif()

# FFmpeg's MSVC build uses the static CRT (/MT): match it.
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
set(CMAKE_C_STANDARD 11)
set(CMAKE_C_STANDARD_REQUIRED ON)

# static FFmpeg libraries plus the system libraries they need (from their .pc Libs.private)
add_library(ffmpeg INTERFACE)
target_include_directories(ffmpeg INTERFACE "${FFMPEG_PREFIX}/include")
foreach(lib avformat avcodec swresample swscale avutil)
    find_library(FF_${lib} NAMES lib${lib}.a ${lib} PATHS "${FFMPEG_PREFIX}/lib" NO_DEFAULT_PATH REQUIRED)
    target_link_libraries(ffmpeg INTERFACE "${FF_${lib}}")
endforeach()
target_link_libraries(ffmpeg INTERFACE bcrypt ole32 user32 advapi32)

set(PC_DIR "${CMAKE_SOURCE_DIR}/Sources/PlayerCore")
set(PC_WARN -Wall -Wextra -Wno-unused-parameter -Wno-sign-compare)
if(CMAKE_C_COMPILER_FRONTEND_VARIANT STREQUAL "MSVC")
    # clang-cl: /W4 ~ -Wall -Wextra (its -Wall means -Weverything)
    set(PC_WARN /W4 -Wno-unused-parameter -Wno-sign-compare)
endif()

add_library(playercore_stub STATIC
    "${PC_DIR}/engine.c"
    "${PC_DIR}/pc_output.c"
    "${PC_DIR}/platform/stub/audio_stub.c"
    "${PC_DIR}/platform/stub/video_stub.c")
target_include_directories(playercore_stub PUBLIC "${PC_DIR}" "${PC_DIR}/include")
target_compile_definitions(playercore_stub PUBLIC PC_STUB_BACKENDS _CRT_SECURE_NO_WARNINGS)
target_compile_options(playercore_stub PRIVATE ${PC_WARN})
target_link_libraries(playercore_stub PUBLIC ffmpeg)

add_executable(pcdump tools/pcdump.c)
target_compile_options(pcdump PRIVATE ${PC_WARN})
target_link_libraries(pcdump PRIVATE playercore_stub)

# Real Windows back-ends: WASAPI audio, D3D11VA video.
add_library(playercore_win STATIC
    "${PC_DIR}/engine.c"
    "${PC_DIR}/pc_output.c"
    "${PC_DIR}/platform/windows/audio_wasapi.c"
    "${PC_DIR}/platform/windows/video_d3d11.c")
target_include_directories(playercore_win PUBLIC "${PC_DIR}" "${PC_DIR}/include")
target_compile_definitions(playercore_win PUBLIC _CRT_SECURE_NO_WARNINGS _WIN32_WINNT=0x0A00)
target_compile_options(playercore_win PRIVATE ${PC_WARN})
target_link_libraries(playercore_win PUBLIC ffmpeg ole32 avrt d3d11 dxgi)

# playercore.dll for the WinUI app (P/Invoke). Same sources; PC_API exports the public API.
add_library(playercore SHARED
    "${PC_DIR}/engine.c"
    "${PC_DIR}/pc_output.c"
    "${PC_DIR}/platform/windows/audio_wasapi.c"
    "${PC_DIR}/platform/windows/video_d3d11.c")
target_include_directories(playercore PRIVATE "${PC_DIR}" "${PC_DIR}/include")
target_compile_definitions(playercore PRIVATE PC_BUILD_DLL _CRT_SECURE_NO_WARNINGS _WIN32_WINNT=0x0A00)
target_compile_options(playercore PRIVATE ${PC_WARN})
target_link_libraries(playercore PRIVATE ffmpeg ole32 avrt d3d11 dxgi)

# pctest: same driver as pcdump, on the real back-ends (devices / watch / play / dump)
add_executable(pctest tools/pcdump.c)
target_compile_options(pctest PRIVATE ${PC_WARN})
target_compile_definitions(pctest PRIVATE PCTEST_FRAME_INFO)
target_link_libraries(pctest PRIVATE playercore_win)
