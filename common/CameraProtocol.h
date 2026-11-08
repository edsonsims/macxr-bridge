// SPDX-License-Identifier: MPL-2.0
//
// Link para Mac: wire format of the "camera" channel (TcpRecordType::Camera, port 9949).
//
// The headset client captures a passthrough camera with Android Camera2, JPEG-encodes each frame
// and sends it with the camera's intrinsics, its extrinsics relative to the head, and the head
// pose at capture time. The macOS runtime decodes the frame and exposes the latest one to Unity
// through a C export (oxrsys_camera_*), so the app's detection reads the real Quest camera in the
// Editor without the proprietary XR_METAX1_passthrough_camera extension.

#pragma once

#include <cstddef>
#include <cstdint>

namespace oxr
{
namespace protocol
{
namespace camera
{

constexpr uint16_t CAMERA_VERSION = 1;

enum class MsgKind : uint16_t
{
    Hello = 1,  // client -> runtime, once: which eye, resolution, framerate
    Frame = 2,  // client -> runtime: a JPEG frame + pose/intrinsics
    Config = 3, // runtime -> client: enable/disable, target fps
};

enum PixelFormat : uint32_t
{
    FORMAT_JPEG = 1,
    FORMAT_RGBA8 = 2, // raw, used only if JPEG is unavailable
};

struct Vec3 { float x, y, z; };
struct Quat { float x, y, z, w; };
struct Pose { Quat orientation; Vec3 position; };

struct Intrinsics
{
    float fx, fy;       // focal length in pixels
    float cx, cy;       // principal point in pixels
    float skew;         // usually 0
    uint32_t width;     // resolution the intrinsics are expressed in
    uint32_t height;
    uint32_t reserved = 0;
};

struct Hello
{
    uint32_t eye;            // 0 = left, 1 = right
    uint32_t maxFramerate;
    Intrinsics intrinsics;
    Pose headFromCamera;     // camera pose relative to the head (extrinsics)
};

struct MsgHeader
{
    uint16_t version = CAMERA_VERSION;
    MsgKind kind = MsgKind::Frame;
    uint32_t payloadSize = 0; // bytes after this header
};

struct FrameHeader
{
    uint32_t format;         // PixelFormat
    uint32_t width;
    uint32_t height;
    uint32_t payloadSize;    // image bytes following this header
    int64_t captureTimeNs;
    Pose headPose;           // head pose (STAGE space) at capture
    Pose headFromCamera;     // extrinsics
    Intrinsics intrinsics;   // at this frame's resolution
};

struct Config
{
    uint32_t enabled;
    uint32_t targetFps;
};

// The runtime's C export fills this for Unity (ABI-stable, self-contained).
struct FrameInfo
{
    uint32_t width;
    uint32_t height;
    uint32_t valid;
    uint32_t frameId;
    float fx, fy, cx, cy;
    Pose cameraPose;         // world (STAGE) pose: headPose * headFromCamera
    int64_t captureTimeNs;
};

} // namespace camera
} // namespace protocol
} // namespace oxr
