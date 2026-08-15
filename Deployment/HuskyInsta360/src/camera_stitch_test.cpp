#include <camera/camera.h>
#include <camera/device_discovery.h>
#include <ins_realtime_stitcher.h>

#include <gst/app/gstappsrc.h>
#include <gst/gst.h>

#include <atomic>
#include <chrono>
#include <csignal>
#include <cstring>
#include <iostream>
#include <memory>
#include <thread>
#include <vector>

namespace {
std::atomic<bool> keep_running{true};
std::atomic<uint64_t> stitched_frames{0};
std::atomic<int> stitched_width{0};
std::atomic<int> stitched_height{0};

class GStreamerPublisher {
public:
    ~GStreamerPublisher() {
        Stop();
    }

    bool Start(int width, int height, int fps) {
        gst_init(nullptr, nullptr);

        const bool has_nvenc =
            gst_element_factory_find("nvh264enc") != nullptr;
        const std::string encoder = has_nvenc
            ? "nvh264enc bitrate=8000 gop-size=30 bframes=0 "
              "zerolatency=true"
            : "x264enc bitrate=8000 key-int-max=30 bframes=0 "
              "speed-preset=ultrafast tune=zerolatency";

        const std::string pipeline_description =
            "appsrc name=source is-live=true block=false format=time "
            "do-timestamp=true caps=video/x-raw,format=RGBA,width=" +
            std::to_string(width) + ",height=" + std::to_string(height) +
            ",framerate=" + std::to_string(fps) +
            "/1 ! queue max-size-buffers=2 leaky=downstream "
            "! videoconvert ! video/x-raw,format=NV12 "
            "! " + encoder +
            " ! h264parse config-interval=-1 "
            "! video/x-h264,profile=baseline,stream-format=byte-stream,"
            "alignment=au "
            "! rtspclientsink location=rtsp://127.0.0.1:8554/insta360 "
            "protocols=tcp latency=0";

        GError* error = nullptr;
        pipeline_ = gst_parse_launch(pipeline_description.c_str(), &error);
        if (error != nullptr) {
            std::cerr << "GStreamer pipeline error: " << error->message
                      << std::endl;
            g_error_free(error);
            if (pipeline_ != nullptr) {
                gst_object_unref(pipeline_);
                pipeline_ = nullptr;
            }
            return false;
        }
        if (pipeline_ == nullptr) return false;

        appsrc_ = gst_bin_get_by_name(GST_BIN(pipeline_), "source");
        if (appsrc_ == nullptr) {
            std::cerr << "GStreamer appsrc was not created." << std::endl;
            Stop();
            return false;
        }

        width_ = width;
        height_ = height;
        frame_duration_ = GST_SECOND / fps;
        next_pts_ = 0;

        const GstStateChangeReturn state_result =
            gst_element_set_state(pipeline_, GST_STATE_PLAYING);
        if (state_result == GST_STATE_CHANGE_FAILURE) {
            std::cerr << "GStreamer pipeline could not start." << std::endl;
            Stop();
            return false;
        }

        std::cout << "Video publisher started with "
                  << (has_nvenc ? "NVIDIA NVENC" : "software x264 fallback")
                  << ": rtsp://127.0.0.1:8554/insta360" << std::endl;
        return true;
    }

    void PushRgba(
        uint8_t* rgba,
        int stride,
        int width,
        int height) {
        if (appsrc_ == nullptr || rgba == nullptr ||
            width != width_ || height != height_) {
            return;
        }

        const gsize row_bytes = static_cast<gsize>(width_) * 4;
        const gsize buffer_size = row_bytes * height_;
        GstBuffer* buffer = gst_buffer_new_allocate(nullptr, buffer_size, nullptr);
        if (buffer == nullptr) return;

        GstMapInfo map{};
        if (!gst_buffer_map(buffer, &map, GST_MAP_WRITE)) {
            gst_buffer_unref(buffer);
            return;
        }
        for (int row = 0; row < height_; ++row) {
            std::memcpy(
                map.data + static_cast<gsize>(row) * row_bytes,
                rgba + static_cast<gsize>(row) * stride,
                row_bytes);
        }
        gst_buffer_unmap(buffer, &map);

        GST_BUFFER_PTS(buffer) = next_pts_;
        GST_BUFFER_DTS(buffer) = GST_CLOCK_TIME_NONE;
        GST_BUFFER_DURATION(buffer) = frame_duration_;
        next_pts_ += frame_duration_;

        const GstFlowReturn result =
            gst_app_src_push_buffer(GST_APP_SRC(appsrc_), buffer);
        if (result != GST_FLOW_OK && !push_error_reported_.exchange(true)) {
            std::cerr << "GStreamer stopped accepting video frames: "
                      << gst_flow_get_name(result) << std::endl;
        }
    }

    void Stop() {
        if (appsrc_ != nullptr) {
            gst_app_src_end_of_stream(GST_APP_SRC(appsrc_));
            gst_object_unref(appsrc_);
            appsrc_ = nullptr;
        }
        if (pipeline_ != nullptr) {
            gst_element_set_state(pipeline_, GST_STATE_NULL);
            gst_object_unref(pipeline_);
            pipeline_ = nullptr;
        }
    }

private:
    GstElement* pipeline_ = nullptr;
    GstElement* appsrc_ = nullptr;
    int width_ = 0;
    int height_ = 0;
    GstClockTime frame_duration_ = 0;
    GstClockTime next_pts_ = 0;
    std::atomic<bool> push_error_reported_{false};
};

void HandleSignal(int) {
    keep_running = false;
}

class StitchDelegate final : public ins_camera::StreamDelegate {
public:
    explicit StitchDelegate(std::shared_ptr<ins::RealTimeStitcher> stitcher)
        : stitcher_(std::move(stitcher)) {}

    void OnAudioData(const uint8_t*, size_t, int64_t) override {}

    void OnVideoData(
        const uint8_t* data,
        size_t size,
        int64_t timestamp,
        uint8_t stream_type,
        int stream_index) override {
        stitcher_->HandleVideoData(
            data, size, timestamp, stream_type, stream_index);
    }

    void OnGyroData(const std::vector<ins_camera::GyroData>& data) override {
        std::vector<ins::GyroData> converted(data.size());
        if (!data.empty()) {
            std::memcpy(
                converted.data(),
                data.data(),
                data.size() * sizeof(ins_camera::GyroData));
        }
        stitcher_->HandleGyroData(converted);
    }

    void OnExposureData(const ins_camera::ExposureData& data) override {
        ins::ExposureData converted{};
        converted.timestamp = data.timestamp;
        converted.exposure_time = data.exposure_time;
        stitcher_->HandleExposureData(converted);
    }

private:
    std::shared_ptr<ins::RealTimeStitcher> stitcher_;
};
}

int main() {
    std::signal(SIGINT, HandleSignal);
    std::signal(SIGTERM, HandleSignal);

    ins::InitEnv();
    ins_camera::SetLogLevel(ins_camera::LogLevel::WARNING);
    ins::SetLogLevel(ins::InsLogLevel::WARNING);

    std::cout << "Searching for Insta360 camera..." << std::endl;
    ins_camera::DeviceDiscovery discovery;
    auto devices = discovery.GetAvailableDevices();
    if (devices.empty()) {
        std::cerr
            << "No camera found. X5 must be in Android control mode and visible "
               "inside the container."
            << std::endl;
        return 2;
    }

    std::cout << "Found: " << devices[0].camera_name
              << " serial=" << devices[0].serial_number
              << " firmware=" << devices[0].fw_version << std::endl;

    const auto camera_type = devices[0].camera_type;
    auto camera = std::make_shared<ins_camera::Camera>(devices[0].info);
    if (!camera->Open()) {
        discovery.FreeDeviceDescriptors(devices);
        std::cerr << "Camera was found but could not be opened." << std::endl;
        return 3;
    }
    discovery.FreeDeviceDescriptors(devices);

    auto stitcher = std::make_shared<ins::RealTimeStitcher>();
    auto publisher = std::make_shared<GStreamerPublisher>();
    if (!publisher->Start(1920, 960, 30)) {
        std::cerr << "Could not start the RTSP/WebRTC publishing pipeline."
                  << std::endl;
        camera->Close();
        return 7;
    }
    const auto preview = camera->GetPreviewParam();
    ins::CameraInfo camera_info;
    camera_info.cameraName = preview.camera_name;
    camera_info.decode_type =
        static_cast<ins::VideoDecodeType>(preview.encode_type);
    camera_info.offset = preview.offset;
    camera_info.window_crop_info_.crop_offset_x =
        preview.crop_info.crop_offset_x;
    camera_info.window_crop_info_.crop_offset_y =
        preview.crop_info.crop_offset_y;
    camera_info.window_crop_info_.dst_width = preview.crop_info.dst_width;
    camera_info.window_crop_info_.dst_height = preview.crop_info.dst_height;
    camera_info.window_crop_info_.src_width = preview.crop_info.src_width;
    camera_info.window_crop_info_.src_height = preview.crop_info.src_height;

    stitcher->SetCameraInfo(camera_info);
    stitcher->SetStitchType(ins::STITCH_TYPE::DYNAMICSTITCH);
    stitcher->EnableFlowState(true);
    stitcher->SetOutputSize(1920, 960);
    stitcher->SetStitchRealTimeDataCallback(
        [publisher](uint8_t* data[4],
           int strides[4],
           int width,
           int height,
           int,
           int64_t) {
            if (data != nullptr && data[0] != nullptr) {
                stitched_width = width;
                stitched_height = height;
                ++stitched_frames;
                publisher->PushRgba(data[0], strides[0], width, height);
            }
        });

    std::shared_ptr<ins_camera::StreamDelegate> delegate =
        std::make_shared<StitchDelegate>(stitcher);
    camera->SetStreamDelegate(delegate);

    const auto resolution = ins_camera::VideoResolution::RES_1920_960P30;
    if (camera_type >= ins_camera::CameraType::Insta360X4) {
        if (!camera->SetVideoSubMode(ins_camera::SubVideoMode::VIDEO_LIVEVIEW)) {
            std::cerr << "Failed to switch camera to live-view mode." << std::endl;
            camera->Close();
            return 4;
        }

        ins_camera::RecordParams capture_params;
        capture_params.resolution = resolution;
        capture_params.bitrate = 0;
        if (!camera->SetVideoCaptureParams(
                capture_params,
                ins_camera::CameraFunctionMode::FUNCTION_MODE_LIVE_STREAM)) {
            std::cerr << "Failed to configure live-view resolution." << std::endl;
            camera->Close();
            return 5;
        }
    }

    ins_camera::LiveStreamParam live_params;
    live_params.video_resolution = resolution;
    live_params.lrv_video_resulution =
        ins_camera::VideoResolution::RES_1440_720P30;
    live_params.video_bitrate = 8 * 1024 * 1024;
    live_params.enable_audio = false;
    live_params.using_lrv = false;

    if (!camera->StartLiveStreaming(live_params)) {
        std::cerr << "Failed to start X5 live preview." << std::endl;
        camera->Close();
        return 6;
    }

    stitcher->StartStitch();
    std::cout
        << "Live preview started. Waiting for stitched frames; Ctrl+C stops."
        << std::endl;

    uint64_t previous = 0;
    while (keep_running) {
        std::this_thread::sleep_for(std::chrono::seconds(1));
        const uint64_t current = stitched_frames.load();
        std::cout << "stitched=" << stitched_width.load() << "x"
                  << stitched_height.load() << " fps=" << (current - previous)
                  << " total=" << current << std::endl;
        previous = current;
    }

    camera->StopLiveStreaming();
    stitcher->CancelStitch();
    publisher->Stop();
    camera->Close();
    return 0;
}
