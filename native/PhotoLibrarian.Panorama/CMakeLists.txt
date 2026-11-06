cmake_minimum_required(VERSION 3.20)
project(PhotoLibrarianPanorama LANGUAGES C CXX)

set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded" CACHE STRING "" FORCE)
set(BUILD_SHARED_LIBS OFF CACHE BOOL "" FORCE)
set(BUILD_WITH_STATIC_CRT ON CACHE BOOL "" FORCE)
set(BUILD_LIST "core,imgproc,features2d,calib3d,flann,stitching" CACHE STRING "" FORCE)
foreach(option
    BUILD_TESTS BUILD_PERF_TESTS BUILD_EXAMPLES BUILD_DOCS BUILD_JAVA
    BUILD_opencv_apps BUILD_opencv_python2 BUILD_opencv_python3
    BUILD_opencv_java BUILD_opencv_js BUILD_opencv_gapi
    WITH_IPP WITH_ITT WITH_OPENCL WITH_CUDA WITH_LAPACK WITH_EIGEN
    WITH_PROTOBUF WITH_FFMPEG WITH_GSTREAMER WITH_MSMF WITH_DSHOW
    WITH_VFW WITH_AVFOUNDATION WITH_V4L WITH_GTK WITH_QT WITH_WIN32UI
    WITH_JPEG WITH_PNG WITH_TIFF WITH_WEBP WITH_OPENJPEG WITH_OPENEXR
    WITH_JASPER WITH_ZLIB OPENCV_ENABLE_NONFREE OPENCV_GENERATE_PKGCONFIG)
    set(${option} OFF CACHE BOOL "" FORCE)
endforeach()
set(OPENCV_SKIP_PYTHON_LOADER ON CACHE BOOL "" FORCE)
set(OPENCV_PYTHON_SKIP_DETECTION ON CACHE BOOL "" FORCE)
set(OPENCV_ENABLE_ALLOCATOR_STATS OFF CACHE BOOL "" FORCE)

if(NOT DEFINED OPENCV_ARCHIVE OR NOT EXISTS "${OPENCV_ARCHIVE}")
    message(FATAL_ERROR "Supply the pinned OpenCV source archive through OPENCV_ARCHIVE.")
endif()
file(SHA512 "${OPENCV_ARCHIVE}" archive_hash)
if(NOT archive_hash STREQUAL "8ac63ddd61e22cc0eaeafee4f30ae6e1cab05fc4929e2cea29070203b9ca8dfead12cc0fd7c4a87b65c1e20ec6b9ab4865a1b83fad33d114fc0708fdf107c51b")
    message(FATAL_ERROR "The OpenCV 4.12.0 source archive checksum is incorrect.")
endif()
set(source_root "${CMAKE_BINARY_DIR}/_deps/opencv-4.12.0")
if(NOT EXISTS "${source_root}/CMakeLists.txt")
    file(ARCHIVE_EXTRACT INPUT "${OPENCV_ARCHIVE}" DESTINATION "${CMAKE_BINARY_DIR}/_deps")
endif()
add_subdirectory("${source_root}" "${CMAKE_BINARY_DIR}/opencv" EXCLUDE_FROM_ALL)

add_library(PhotoLibrarian.Panorama SHARED Panorama.cpp)
target_compile_features(PhotoLibrarian.Panorama PRIVATE cxx_std_17)
target_compile_definitions(PhotoLibrarian.Panorama PRIVATE NOMINMAX WIN32_LEAN_AND_MEAN)
target_include_directories(PhotoLibrarian.Panorama PRIVATE
    "${source_root}/modules/core/include" "${source_root}/modules/imgproc/include"
    "${source_root}/modules/features2d/include" "${source_root}/modules/flann/include"
    "${source_root}/modules/calib3d/include" "${source_root}/modules/stitching/include"
    "${CMAKE_BINARY_DIR}")
target_link_libraries(PhotoLibrarian.Panorama PRIVATE opencv_stitching)
