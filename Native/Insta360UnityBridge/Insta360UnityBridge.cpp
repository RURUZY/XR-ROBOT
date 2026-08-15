#include <camera/camera.h>
#include <camera/device_discovery.h>
#include <ins_realtime_stitcher.h>

#include <algorithm>
#include <cstdint>
#include <cstring>
#include <memory>
#include <mutex>
#include <string>
#include <vector>

#define UNITY_BRIDGE_API extern "C" __declspec(dllexport)

namespace {
std::shared_ptr<ins_camera::Camera> camera;
std::shared_ptr<ins::RealTimeStitcher> stitcher;
std::shared_ptr<ins_camera::StreamDelegate> stream_delegate;
std::mutex frame_mutex;
std::vector<uint8_t> latest_frame;
std::string last_error;
int frame_width = 0;
int frame_height = 0;
uint64_t frame_number = 0;
bool running = false;

void SetError(const std::string& message) {
    last_error = message;
}

class UnityStreamDelegate final : public ins_camera::StreamDelegate {
public:
    explicit UnityStreamDelegate(std::shared_ptr<ins::RealTimeStitcher> target)
        : target_(std::move(target)) {}

    void OnAudioData(const uint8_t*, size_t, int64_t) override {}

    void OnVideoData(
        const uint8_t* data,
        size_t size,
        int64_t timestamp,
        uint8_t stream_type,
        int stream_index) override {
        target_->HandleVideoData(
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
        target_->HandleGyroData(converted);
    }

    void OnExposureData(const ins_camera::ExposureData& data) override {
        ins::ExposureData converted{};
        converted.timestamp = data.timestamp;
        converted.exposure_time = data.exposure_time;
        target_->HandleExposureData(converted);
    }

private:
    std::shared_ptr<ins::RealTimeStitcher> target_;
};

void StopInternal() {
    running = false;
    if (camera) {
        camera->StopLiveStreaming();
    }
    if (stitcher) {
        stitcher->CancelStitch();
    }
    if (camera) {
        camera->Close();
    }
    stream_delegate.reset();
    stitcher.reset();
    camera.reset();
    std::lock_guard<std::mutex> lock(frame_mutex);
    latest_frame.clear();
    frame_width = 0;
    frame_height = 0;
    frame_number = 0;
}
}

UNITY_BRIDGE_API bool Insta360_Start(int output_width, int output_height) {
    if (running) {
        return true;
    }

    if (output_width <= 0 || output_height <= 0) {
        SetError("Invalid output size.");
        return false;
    }

    try {
        ins::InitEnv();
        ins_camera::SetLogLevel(ins_camera::LogLevel::WARNING);
        ins::SetLogLevel(ins::InsLogLevel::WARNING);

        ins_camera::DeviceDiscovery discovery;
        auto devices = discovery.GetAvailableDevices();
        if (devices.empty()) {
            SetError("No Insta360 camera found. Connect X5 by USB and select USB camera mode.");
            return false;
        }

        const auto camera_type = devices[0].camera_type;
        camera = std::make_shared<ins_camera::Camera>(devices[0].info);
        if (!camera->Open()) {
            discovery.FreeDeviceDescriptors(devices);
            camera.reset();
            SetError("The Insta360 camera was found but could not be opened.");
            return false;
        }
        discovery.FreeDeviceDescriptors(devices);

        stitcher = std::make_shared<ins::RealTimeStitcher>();
        const auto preview = camera->GetPreviewParam();
        ins::CameraInfo info;
        info.cameraName = preview.camera_name;
        info.decode_type = static_cast<ins::VideoDecodeType>(preview.encode_type);
        info.offset = preview.offset;
        info.window_crop_info_.crop_offset_x = preview.crop_info.crop_offset_x;
        info.window_crop_info_.crop_offset_y = preview.crop_info.crop_offset_y;
        info.window_crop_info_.dst_width = preview.crop_info.dst_width;
        info.window_crop_info_.dst_height = preview.crop_info.dst_height;
        info.window_crop_info_.src_width = preview.crop_info.src_width;
        info.window_crop_info_.src_height = preview.crop_info.src_height;
        info.gyro_timestamp = preview.delay_timestamp;
        info.sweep_timestamp = preview.sweep_time;

        stitcher->SetCameraInfo(info);
        stitcher->SetStitchType(ins::STITCH_TYPE::DYNAMICSTITCH);
        stitcher->EnableFlowState(true);
        stitcher->SetOutputSize(output_width, output_height);
        stitcher->SetStitchRealTimeDataCallback(
            [](uint8_t* data[4],
               int[4],
               int width,
               int height,
               int,
               int64_t) {
                if (!running || data == nullptr || data[0] == nullptr) {
                    return;
                }
                const size_t byte_count =
                    static_cast<size_t>(width) * static_cast<size_t>(height) * 4;
                std::lock_guard<std::mutex> lock(frame_mutex);
                latest_frame.resize(byte_count);
                std::memcpy(latest_frame.data(), data[0], byte_count);
                frame_width = width;
                frame_height = height;
                ++frame_number;
            });

        stream_delegate = std::make_shared<UnityStreamDelegate>(stitcher);
        camera->SetStreamDelegate(stream_delegate);

        const auto resolution = ins_camera::VideoResolution::RES_1920_960P30;
        if (camera_type >= ins_camera::CameraType::Insta360X4) {
            if (!camera->SetVideoSubMode(ins_camera::SubVideoMode::VIDEO_LIVEVIEW)) {
                SetError("Failed to switch X5 to live-view mode.");
                StopInternal();
                return false;
            }
            ins_camera::RecordParams capture_params;
            capture_params.resolution = resolution;
            capture_params.bitrate = 0;
            if (!camera->SetVideoCaptureParams(
                    capture_params,
                    ins_camera::CameraFunctionMode::FUNCTION_MODE_LIVE_STREAM)) {
                SetError("Failed to configure X5 live-view resolution.");
                StopInternal();
                return false;
            }
        }

        ins_camera::LiveStreamParam live_params;
        live_params.video_resolution = resolution;
        live_params.lrv_video_resulution =
            ins_camera::VideoResolution::RES_1440_720P30;
        live_params.video_bitrate = 8 * 1024 * 1024;
        live_params.enable_audio = false;
        live_params.using_lrv = false;

        running = true;
        if (!camera->StartLiveStreaming(live_params)) {
            SetError("Failed to start X5 live preview.");
            StopInternal();
            return false;
        }
        stitcher->StartStitch();
        last_error.clear();
        return true;
    } catch (const std::exception& exception) {
        SetError(exception.what());
        StopInternal();
        return false;
    } catch (...) {
        SetError("Unknown error while starting Insta360 SDK.");
        StopInternal();
        return false;
    }
}

UNITY_BRIDGE_API void Insta360_Stop() {
    StopInternal();
}

UNITY_BRIDGE_API uint64_t Insta360_GetFrameNumber() {
    std::lock_guard<std::mutex> lock(frame_mutex);
    return frame_number;
}

UNITY_BRIDGE_API int Insta360_GetFrameWidth() {
    std::lock_guard<std::mutex> lock(frame_mutex);
    return frame_width;
}

UNITY_BRIDGE_API int Insta360_GetFrameHeight() {
    std::lock_guard<std::mutex> lock(frame_mutex);
    return frame_height;
}

UNITY_BRIDGE_API int Insta360_CopyLatestFrame(uint8_t* destination, int capacity) {
    if (destination == nullptr || capacity <= 0) {
        return 0;
    }
    std::lock_guard<std::mutex> lock(frame_mutex);
    if (latest_frame.empty() ||
        capacity < static_cast<int>(latest_frame.size())) {
        return 0;
    }
    std::memcpy(destination, latest_frame.data(), latest_frame.size());
    return static_cast<int>(latest_frame.size());
}

UNITY_BRIDGE_API const char* Insta360_GetLastError() {
    return last_error.c_str();
}
