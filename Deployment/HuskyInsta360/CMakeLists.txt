cmake_minimum_required(VERSION 3.16)
project(husky_insta360 LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

find_package(PkgConfig REQUIRED)
pkg_check_modules(GSTREAMER REQUIRED
    gstreamer-1.0
    gstreamer-app-1.0)

add_executable(insta360_stitch_test src/camera_stitch_test.cpp)
target_include_directories(insta360_stitch_test PRIVATE ${GSTREAMER_INCLUDE_DIRS})
target_compile_options(insta360_stitch_test PRIVATE ${GSTREAMER_CFLAGS_OTHER})
target_link_directories(insta360_stitch_test PRIVATE ${GSTREAMER_LIBRARY_DIRS})
target_link_libraries(insta360_stitch_test PRIVATE
    CameraSDK
    MediaSDK
    ${GSTREAMER_LIBRARIES}
    pthread)
