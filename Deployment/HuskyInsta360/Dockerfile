FROM nvidia/cuda:11.7.1-devel-ubuntu22.04

ENV DEBIAN_FRONTEND=noninteractive
ENV __EGL_VENDOR_LIBRARY_FILENAMES=/usr/share/glvnd/egl_vendor.d/10_nvidia.json
ENV __GLX_VENDOR_LIBRARY_NAME=nvidia

RUN apt-get update && apt-get install -y --no-install-recommends \
    build-essential \
    ca-certificates \
    cmake \
    gstreamer1.0-libav \
    gstreamer1.0-plugins-bad \
    gstreamer1.0-plugins-base \
    gstreamer1.0-plugins-good \
    gstreamer1.0-plugins-ugly \
    gstreamer1.0-rtsp \
    gstreamer1.0-tools \
    libbsd0 \
    libdc1394-dev \
    libgl1 \
    libglfw3-dev \
    libglvnd0 \
    libglx0 \
    libgomp1 \
    libgstreamer-plugins-base1.0-dev \
    libgstreamer1.0-dev \
    libjpeg-turbo8 \
    libmd0 \
    libpng16-16 \
    libudev-dev \
    libusb-1.0-0-dev \
    libvulkan-dev \
    libvulkan1 \
    libx11-6 \
    libxau6 \
    libxcb1 \
    libxdmcp6 \
    libxext6 \
    mesa-utils \
    pkg-config \
    udev \
    vulkan-tools \
    && rm -rf /var/lib/apt/lists/*

COPY sdk/CameraSDK-2.1.1-Linux.tar.gz /tmp/
COPY sdk/libMediaSDK-dev-3.1.1.0-amd64.tar.xz /tmp/

RUN mkdir -p /opt/insta360/camera /opt/insta360/media \
    && tar -xzf /tmp/CameraSDK-2.1.1-Linux.tar.gz \
       -C /opt/insta360/camera --strip-components=1 \
    && tar -xJf /tmp/libMediaSDK-dev-3.1.1.0-amd64.tar.xz \
       -C /opt/insta360/media --strip-components=1 \
    && apt-get update \
    && apt-get install -y \
       /opt/insta360/media/libMediaSDK-dev-3.1.1.0-20250922_191110-amd64.deb \
    && cp /opt/insta360/camera/lib/libCameraSDK.so /usr/local/lib/ \
    && cp -r /opt/insta360/camera/include/camera /usr/local/include/ \
    && cp -r /opt/insta360/camera/include/stream /usr/local/include/ \
    && ldconfig \
    && rm -rf /var/lib/apt/lists/* /tmp/CameraSDK* /tmp/libMediaSDK*

COPY nvidia-egl.json /usr/share/glvnd/egl_vendor.d/10_nvidia.json

WORKDIR /opt/husky-insta360
COPY CMakeLists.txt ./
COPY src ./src

RUN cmake -S . -B build -DCMAKE_BUILD_TYPE=Release \
    && cmake --build build --parallel

ENTRYPOINT ["/opt/husky-insta360/build/insta360_stitch_test"]
