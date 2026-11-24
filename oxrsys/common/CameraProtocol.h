// SPDX-License-Identifier: MPL-2.0
//
// MacXR Bridge: wire format of the "camera" channel (TcpRecordType::Camera, port 9949).
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
    Depth = 4,  // client -> runtime: one eye of XR_META_environment_depth (uint16 normalized depth)
    Audio = 5,  // runtime -> client: the app's audio mix (PCM s16 interleaved), played on the headset
    Mic = 6,    // client -> runtime: the headset microphone (PCM s16, AudioHeader), like Quest Link's microphone
    Stats = 7,  // client -> runtime, about once a second: headset battery, temperature, frame rate (ClientStats)
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

struct Fov { float angleLeft, angleRight, angleUp, angleDown; }; // radians, as XrFovf

// Environment depth (XR_META_environment_depth) for one eye, read back on the headset. Each pixel
// is the depth buffer value d in [0,1] scaled to uint16 (d * 65535). Row 0 is the BOTTOM of the
// image (OpenGL convention, toward fov.angleDown). Linear distance along the view's -Z axis:
//   ndc = 2d - 1;  z = farZ finite ? 2*far*near / ((far+near) - ndc*(far-near)) : near / (1 - d)
struct DepthHeader
{
    uint32_t width;
    uint32_t height;
    uint32_t payloadSize;   // width * height * 2 bytes follow this header
    uint32_t eye;           // 0 = left
    float nearZ;
    float farZ;             // +inf for an infinite far plane
    int64_t displayTimeNs;  // XrTime the depth was located for
    Pose headPose;          // head pose at displayTime (client STAGE), same space as viewPose
    Pose viewPose;          // depth view pose (client STAGE)
    Fov fov;
};

// The Editor's final audio mix, sent to the headset so the app is heard in the Quest (like Quest Link).
struct AudioHeader
{
    uint32_t sampleRate;    // e.g. 48000
    uint16_t channels;      // interleaved
    uint16_t format;        // 1 = PCM s16
    uint32_t frames;        // per channel
    uint32_t payloadSize;   // frames * channels * 2 bytes follow this header
    uint64_t sequence;      // increases by one per message (gaps = drops)
};

// Headset state for the developer HUD (MacXR Bridge). Fields are appended only; size tells how many are present.
struct ClientStats
{
    uint32_t size;               // sizeof(ClientStats) on the sender
    float batteryPercent;        // 0..100, < 0 unknown
    float batteryTemperatureC;   // < -100 unknown
    uint32_t charging;           // 1 while plugged in
    float displayFps;            // frames the client submitted during the last second
    float videoFps;              // decoded video frames during the last second
    uint32_t micActive;          // 1 while the headset microphone streams to the runtime
    uint32_t transport;          // 1 Wi-Fi, 2 USB ADB, 3 USB network
    uint32_t reserved[6];
};
static_assert(sizeof(ClientStats) == 56, "ClientStats layout is shared with C#");

// Headset microphone forwarded by the runtime to the "Headset Microphone" audio driver (UDP on localhost).
constexpr uint16_t MIC_DRIVER_PORT = 9950;
constexpr uint32_t MIC_DRIVER_MAGIC = 0x4d51584f; // "OXQM"
struct MicDatagram
{
    uint32_t magic = MIC_DRIVER_MAGIC;
    uint32_t sampleRate;
    uint32_t frames;        // mono s16 samples following this header
    uint32_t sequence;
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

// Latest depth for Unity (C export oxrsys_depth_get_latest). Layout is ABI-stable: 104 bytes.
struct DepthInfo
{
    uint32_t width;
    uint32_t height;
    uint32_t valid;
    uint32_t frameId;
    float nearZ;
    float farZ;
    Pose headPose;          // client STAGE
    Pose viewPose;          // client STAGE
    Fov fov;
    int64_t displayTimeNs;
};
static_assert(sizeof(DepthInfo) == 104, "DepthInfo layout is shared with C#");

} // namespace camera
} // namespace protocol
} // namespace oxr
