cmake_minimum_required(VERSION 3.20)
project(PhotoLibrarianRawDecoder LANGUAGES CXX)

set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>" CACHE STRING "" FORCE)
set(BUILD_SHARED_LIBS OFF CACHE BOOL "" FORCE)
set(ENABLE_OPENMP OFF CACHE BOOL "" FORCE)
set(ENABLE_LCMS OFF CACHE BOOL "" FORCE)
set(ENABLE_JASPER OFF CACHE BOOL "" FORCE)
set(ENABLE_EXAMPLES OFF CACHE BOOL "" FORCE)
set(ENABLE_RAWSPEED OFF CACHE BOOL "" FORCE)
set(ENABLE_DCRAW_DEBUG OFF CACHE BOOL "" FORCE)
set(ENABLE_X3FTOOLS OFF CACHE BOOL "" FORCE)
set(ENABLE_6BY9RPI OFF CACHE BOOL "" FORCE)
set(LIBRAW_INSTALL OFF CACHE BOOL "" FORCE)
set(CMAKE_DISABLE_FIND_PACKAGE_JPEG TRUE)

set(DOWNLOAD_DIRECTORY "${CMAKE_BINARY_DIR}/downloads")
set(LIBRAW_SOURCE_ARCHIVE "${DOWNLOAD_DIRECTORY}/LibRaw-0.22.3.tar.gz")
set(LIBRAW_CMAKE_ARCHIVE "${DOWNLOAD_DIRECTORY}/LibRaw-cmake-eb98e4325aef2ce85d2eb031c2ff18640ca616d3.tar.gz")
file(MAKE_DIRECTORY "${DOWNLOAD_DIRECTORY}")

function(download_pinned_archive url destination sha512)
    if(NOT EXISTS "${destination}")
        file(DOWNLOAD "${url}" "${destination}"
            EXPECTED_HASH "SHA512=${sha512}"
            STATUS download_status
            TLS_VERIFY ON
            SHOW_PROGRESS)
        list(GET download_status 0 download_code)
        if(NOT download_code EQUAL 0)
            file(REMOVE "${destination}")
            message(FATAL_ERROR "Could not download pinned source archive ${url}: ${download_status}")
        endif()
    else()
        file(SHA512 "${destination}" actual_sha512)
        if(NOT actual_sha512 STREQUAL sha512)
            message(FATAL_ERROR "Pinned source archive checksum mismatch for ${destination}")
        endif()
    endif()
endfunction()

download_pinned_archive(
    "https://github.com/LibRaw/LibRaw/archive/refs/tags/0.22.3.tar.gz"
    "${LIBRAW_SOURCE_ARCHIVE}"
    "a9453f8cff55302926c11c47ba7c8d04d81023bbbcad145551d2c0cc601de7eea8218d00a073e6b9ece845d3420897a2272a34fabf55f5b821183eaa7f66b3ef")
download_pinned_archive(
    "https://github.com/LibRaw/LibRaw-cmake/archive/eb98e4325aef2ce85d2eb031c2ff18640ca616d3.tar.gz"
    "${LIBRAW_CMAKE_ARCHIVE}"
    "63e68a4d30286ec3aa97168d46b7a1199268099ae27b61abcc92e93ec30e48d364086227983a1d724415e5f4da44d905422f30192453b95f31040e5f8469c3f9")

set(LIBRAW_SOURCE_DIRECTORY "${CMAKE_BINARY_DIR}/_deps/libraw")
set(LIBRAW_CMAKE_DIRECTORY "${CMAKE_BINARY_DIR}/_deps/libraw-cmake")
file(MAKE_DIRECTORY "${LIBRAW_SOURCE_DIRECTORY}" "${LIBRAW_CMAKE_DIRECTORY}")
file(ARCHIVE_EXTRACT INPUT "${LIBRAW_SOURCE_ARCHIVE}" DESTINATION "${LIBRAW_SOURCE_DIRECTORY}")
file(ARCHIVE_EXTRACT INPUT "${LIBRAW_CMAKE_ARCHIVE}" DESTINATION "${LIBRAW_CMAKE_DIRECTORY}")

file(GLOB libraw_source_candidates LIST_DIRECTORIES TRUE "${LIBRAW_SOURCE_DIRECTORY}/*")
file(GLOB libraw_cmake_candidates LIST_DIRECTORIES TRUE "${LIBRAW_CMAKE_DIRECTORY}/*")
list(LENGTH libraw_source_candidates libraw_source_count)
list(LENGTH libraw_cmake_candidates libraw_cmake_count)
if(NOT libraw_source_count EQUAL 1 OR NOT libraw_cmake_count EQUAL 1)
    message(FATAL_ERROR "Expected one source directory in each pinned LibRaw archive.")
endif()

list(GET libraw_source_candidates 0 LIBRAW_SOURCE_PATH)
list(GET libraw_cmake_candidates 0 LIBRAW_CMAKE_PATH)
set(LIBRAW_PATH "${LIBRAW_SOURCE_PATH}" CACHE STRING "Pinned LibRaw 0.22.3 source directory" FORCE)
add_subdirectory("${LIBRAW_CMAKE_PATH}" "${CMAKE_BINARY_DIR}/libraw-cmake")

add_library(PhotoLibrarian.RawDecoder SHARED RawDecoder.cpp)
target_compile_features(PhotoLibrarian.RawDecoder PRIVATE cxx_std_17)
target_compile_definitions(PhotoLibrarian.RawDecoder PRIVATE
    PHOTO_LIBRARIAN_RAWDECODER_EXPORTS
    NOMINMAX
    WIN32_LEAN_AND_MEAN)
target_link_libraries(PhotoLibrarian.RawDecoder PRIVATE raw_r)
set_target_properties(PhotoLibrarian.RawDecoder PROPERTIES
    OUTPUT_NAME "PhotoLibrarian.RawDecoder"
    CXX_EXTENSIONS OFF)
