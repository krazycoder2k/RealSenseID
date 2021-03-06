cmake_minimum_required(VERSION 3.10.2)

include(cmake/Version.cmake)
rsid_extract_version()
project(RealSenseID VERSION ${RSID_VERSION})

# C++14 and above
if(NOT CMAKE_CXX_STANDARD)
    set(CMAKE_CXX_STANDARD 14)
    set(CMAKE_CXX_STANDARD_REQUIRED ON)
endif()

# Global properties

set(LIBRSID_CPP_TARGET rsid)
set(LIBRSID_C_TARGET rsid_c)

# Disable warnings generated when using deprecated unsafe functions.
if (MSVC)
    add_definitions(-D_CRT_SECURE_NO_WARNINGS)
endif ()

set(CMAKE_POSITION_INDEPENDENT_CODE ON)
set_property(GLOBAL PROPERTY USE_FOLDERS ON)
set_property(GLOBAL PROPERTY PREDEFINED_TARGETS_FOLDER "_build")
set(RSID_DEBUG_POSTFIX _debug)

# ---------------------------------------------------------------------------------------
# Set RSID_MASTER_PROJECT to ON if we are not used via add_subdirectory, but allow overriding
# ---------------------------------------------------------------------------------------
if(NOT DEFINED RSID_MASTER_PROJECT)
    if(CMAKE_CURRENT_SOURCE_DIR STREQUAL CMAKE_SOURCE_DIR)
        set(RSID_MASTER_PROJECT ON)
    else()
        set(RSID_MASTER_PROJECT OFF)
    endif()
endif()

# ---------------------------------------------------------------------------------------
# Set default build to release
# ---------------------------------------------------------------------------------------
if(RSID_MASTER_PROJECT AND NOT CMAKE_BUILD_TYPE)
    set(CMAKE_BUILD_TYPE "Release" CACHE STRING "Choose Release or Debug" FORCE)
endif()
message(STATUS "Build type: " ${CMAKE_BUILD_TYPE})

# Options
option(RSID_DEBUG_CONSOLE "Log everything to console" ON)
option(RSID_DEBUG_FILE "Log everything to rsid_debug.log file" OFF)
option(RSID_DEBUG_SERIAL "Log all serial communication" OFF)
option(RSID_C_CLIENT "Build c client" ON)
option(RSID_PREVIEW "Enable opencv preview" OFF)
option(RSID_EXAMPLES "Build examples" ON)
option(RSID_TIDY "Enable clang-tidy" OFF)
option(RSID_DOXYGEN "Build doxygen docs" OFF)

if(RSID_TIDY)
    set(CMAKE_CXX_CLANG_TIDY "clang-tidy")
    set(CMAKE_EXPORT_COMPILE_COMMANDS ON)
    message(STATUS "Enabled clang-tidy")
endif()

set(THIRD_PARTY_DIRECTORY "${CMAKE_CURRENT_SOURCE_DIR}/3rdparty")

# Paths for lib, bin, install, etc.
if(NOT ${CMAKE_SYSTEM_NAME} STREQUAL "Android")
    set(BIN_OUTPUT_PATH ${CMAKE_BINARY_DIR}/bin)
    set(LIBS_OUTPUT_PATH ${CMAKE_BINARY_DIR}/lib)
    set(CMAKE_RUNTIME_OUTPUT_DIRECTORY "${BIN_OUTPUT_PATH}")
    set(CMAKE_LIBRARY_OUTPUT_DIRECTORY "${LIBS_OUTPUT_PATH}")
    set(CMAKE_ARCHIVE_OUTPUT_DIRECTORY "${dS_OUTPUT_PATH}")
    set(CMAKE_INSTALL_PREFIX ${CMAKE_BINARY_DIR}/install)
endif()

include(cmake/OS.cmake)
include(cmake/Mbedtls.cmake)
include(cmake/SpdLog.cmake)

if(RSID_PREVIEW)
    if(${CMAKE_SYSTEM_NAME} STREQUAL "Android")
        include(cmake/UVC.cmake)
    else()
        include(cmake/OpenCV.cmake)
    endif()
endif()

add_subdirectory(src)
add_subdirectory(wrappers)

if(RSID_EXAMPLES)
    add_subdirectory(examples)
endif()

if(RSID_DOXYGEN)
    include(cmake/Doxygen.cmake)
endif()
