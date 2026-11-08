// SPDX-License-Identifier: MPL-2.0

#include "CameraRelay.h"

#import <CoreGraphics/CoreGraphics.h>
#import <ImageIO/ImageIO.h>

#include <spdlog/spdlog.h>

#include <cstring>

using namespace oxr::protocol::camera;

namespace
{
// q (orientation) * v (vector) in the frame of q; both glm-free to avoid pulling glm into a .mm.
Vec3 Rotate(const Quat& q, const Vec3& v)
{
    // t = 2 * cross(q.xyz, v); result = v + q.w*t + cross(q.xyz, t)
    const float tx = 2.0f * (q.y * v.z - q.z * v.y);
    const float ty = 2.0f * (q.z * v.x - q.x * v.z);
    const float tz = 2.0f * (q.x * v.y - q.y * v.x);
    Vec3 r;
    r.x = v.x + q.w * tx + (q.y * tz - q.z * ty);
    r.y = v.y + q.w * ty + (q.z * tx - q.x * tz);
    r.z = v.z + q.w * tz + (q.x * ty - q.y * tx);
    return r;
}

Quat Multiply(const Quat& a, const Quat& b)
{
    Quat r;
    r.w = a.w * b.w - a.x * b.x - a.y * b.y - a.z * b.z;
    r.x = a.w * b.x + a.x * b.w + a.y * b.z - a.z * b.y;
    r.y = a.w * b.y - a.x * b.z + a.y * b.w + a.z * b.x;
    r.z = a.w * b.z + a.x * b.y - a.y * b.x + a.z * b.w;
    return r;
}

Pose Compose(const Pose& parent, const Pose& child)
{
    Pose out;
    out.orientation = Multiply(parent.orientation, child.orientation);
    Vec3 rotated = Rotate(parent.orientation, child.position);
    out.position = {parent.position.x + rotated.x, parent.position.y + rotated.y, parent.position.z + rotated.z};
    return out;
}
} // namespace

CameraRelay& CameraRelay::Get()
{
    static CameraRelay instance;
    return instance;
}

void CameraRelay::SetConnected(bool connected)
{
    std::lock_guard<std::mutex> lock(mutex_);
    connected_ = connected;
    if (!connected)
    {
        info_.valid = 0;
        depthInfo_.valid = 0;
    }
    spdlog::info("CameraRelay: camera channel {}", connected ? "connected" : "disconnected");
}

bool CameraRelay::IsConnected() const
{
    std::lock_guard<std::mutex> lock(mutex_);
    return connected_;
}

bool CameraRelay::DecodeJpeg(const uint8_t* jpeg, size_t size, uint32_t width, uint32_t height, std::vector<uint8_t>& rgba)
{
    CFDataRef data = CFDataCreateWithBytesNoCopy(kCFAllocatorDefault, jpeg, size, kCFAllocatorNull);
    if (data == nullptr) return false;
    CGImageSourceRef src = CGImageSourceCreateWithData(data, nullptr);
    bool ok = false;
    if (src != nullptr)
    {
        CGImageRef img = CGImageSourceCreateImageAtIndex(src, 0, nullptr);
        if (img != nullptr)
        {
            const size_t w = width ? width : CGImageGetWidth(img);
            const size_t h = height ? height : CGImageGetHeight(img);
            rgba.assign(w * h * 4, 0);
            CGColorSpaceRef cs = CGColorSpaceCreateDeviceRGB();
            CGContextRef ctx = CGBitmapContextCreate(rgba.data(), w, h, 8, w * 4, cs,
                                                     kCGImageAlphaPremultipliedLast | kCGBitmapByteOrder32Big);
            if (ctx != nullptr)
            {
                CGContextDrawImage(ctx, CGRectMake(0, 0, w, h), img);
                CGContextRelease(ctx);
                ok = true;
            }
            CGColorSpaceRelease(cs);
            CGImageRelease(img);
        }
        CFRelease(src);
    }
    CFRelease(data);
    return ok;
}

void CameraRelay::OnMessage(const uint8_t* data, size_t size)
{
    if (size < sizeof(MsgHeader)) return;
    MsgHeader header = {};
    std::memcpy(&header, data, sizeof(header));
    if (header.version != CAMERA_VERSION || header.payloadSize != size - sizeof(MsgHeader)) return;
    const uint8_t* payload = data + sizeof(MsgHeader);
    const size_t payloadSize = header.payloadSize;

    if (header.kind == MsgKind::Hello)
    {
        if (payloadSize >= sizeof(Hello))
        {
            Hello hello = {};
            std::memcpy(&hello, payload, sizeof(hello));
            std::lock_guard<std::mutex> lock(mutex_);
            headFromCamera_ = hello.headFromCamera;
            spdlog::info("CameraRelay: Hello eye={} {}x{} fx={:.1f} fy={:.1f}", hello.eye,
                         hello.intrinsics.width, hello.intrinsics.height, hello.intrinsics.fx, hello.intrinsics.fy);
        }
        return;
    }
    if (header.kind == MsgKind::Depth)
    {
        OnDepth(payload, payloadSize);
        return;
    }
    if (header.kind != MsgKind::Frame || payloadSize < sizeof(FrameHeader)) return;
    FrameHeader fh = {};
    std::memcpy(&fh, payload, sizeof(fh));
    if (payloadSize != sizeof(FrameHeader) + fh.payloadSize) return;
    const uint8_t* image = payload + sizeof(FrameHeader);

    std::vector<uint8_t> rgba;
    if (fh.format == FORMAT_JPEG)
    {
        if (!DecodeJpeg(image, fh.payloadSize, fh.width, fh.height, rgba)) return;
    }
    else if (fh.format == FORMAT_RGBA8)
    {
        if (fh.payloadSize < static_cast<size_t>(fh.width) * fh.height * 4) return;
        rgba.assign(image, image + static_cast<size_t>(fh.width) * fh.height * 4);
    }
    else
    {
        return;
    }

    const Pose cameraPose = Compose(fh.headPose, fh.headFromCamera);
    std::lock_guard<std::mutex> lock(mutex_);
    rgba_.swap(rgba);
    info_.width = fh.width;
    info_.height = fh.height;
    info_.valid = 1;
    info_.frameId = nextFrameId_++;
    info_.fx = fh.intrinsics.fx; info_.fy = fh.intrinsics.fy;
    info_.cx = fh.intrinsics.cx; info_.cy = fh.intrinsics.cy;
    info_.cameraPose = cameraPose;
    info_.captureTimeNs = fh.captureTimeNs;
}

void CameraRelay::OnDepth(const uint8_t* payload, size_t size)
{
    if (size < sizeof(DepthHeader)) return;
    DepthHeader dh = {};
    std::memcpy(&dh, payload, sizeof(dh));
    const size_t pixels = static_cast<size_t>(dh.width) * dh.height;
    if (pixels == 0 || dh.payloadSize != pixels * sizeof(uint16_t) || size != sizeof(DepthHeader) + dh.payloadSize) return;
    std::vector<uint16_t> depth(pixels);
    std::memcpy(depth.data(), payload + sizeof(DepthHeader), dh.payloadSize);

    std::lock_guard<std::mutex> lock(mutex_);
    depth_.swap(depth);
    depthInfo_.width = dh.width;
    depthInfo_.height = dh.height;
    depthInfo_.valid = 1;
    depthInfo_.frameId = nextDepthId_++;
    depthInfo_.nearZ = dh.nearZ;
    depthInfo_.farZ = dh.farZ;
    depthInfo_.headPose = dh.headPose;
    depthInfo_.viewPose = dh.viewPose;
    depthInfo_.fov = dh.fov;
    depthInfo_.displayTimeNs = dh.displayTimeNs;
    if (depthInfo_.frameId == 1 || depthInfo_.frameId % 300 == 0)
    {
        spdlog::info("CameraRelay: depth #{} {}x{} near={:.3f} far={}", depthInfo_.frameId, dh.width, dh.height,
                     dh.nearZ, dh.farZ);
    }
}

DepthInfo CameraRelay::GetLatestDepth(uint16_t* dst, uint32_t dstCount, uint32_t lastFrameId)
{
    std::lock_guard<std::mutex> lock(mutex_);
    DepthInfo out = depthInfo_;
    if (!depthInfo_.valid || !connected_ || depthInfo_.frameId == lastFrameId)
    {
        out.valid = 0;
        return out;
    }
    if (dst != nullptr)
    {
        const uint32_t need = depthInfo_.width * depthInfo_.height;
        if (dstCount < need || depth_.size() < need)
        {
            out.valid = 0;
            return out;
        }
        std::memcpy(dst, depth_.data(), need * sizeof(uint16_t));
    }
    return out;
}

FrameInfo CameraRelay::GetLatest(uint8_t* dst, uint32_t dstCapacity, uint32_t lastFrameId)
{
    std::lock_guard<std::mutex> lock(mutex_);
    FrameInfo out = info_;
    if (!info_.valid || info_.frameId == lastFrameId)
    {
        out.valid = 0;
        return out;
    }
    // dst == nullptr is a probe: report the frame (dimensions, pose) without copying.
    if (dst != nullptr)
    {
        const uint32_t need = info_.width * info_.height * 4;
        if (dstCapacity < need || rgba_.size() < need)
        {
            out.valid = 0; // a frame exists but cannot be delivered into this buffer
            return out;
        }
        std::memcpy(dst, rgba_.data(), need);
    }
    return out;
}

// ─── C export for Unity (P/Invoke) ───────────────────────────────────────────

extern "C" {

__attribute__((visibility("default")))
int oxrsys_camera_connected()
{
    return CameraRelay::Get().IsConnected() ? 1 : 0;
}

// Copies the latest frame's RGBA into dst if newer than lastFrameId. Writes the FrameInfo into
// outInfo. Returns outInfo.valid.
__attribute__((visibility("default")))
int oxrsys_camera_get_latest(unsigned char* dst, unsigned int dstCapacity, unsigned int lastFrameId,
                             oxr::protocol::camera::FrameInfo* outInfo)
{
    oxr::protocol::camera::FrameInfo info = CameraRelay::Get().GetLatest(dst, dstCapacity, lastFrameId);
    if (outInfo != nullptr) *outInfo = info;
    return info.valid;
}

// Copies the latest environment depth (uint16 per pixel, row 0 = bottom) into dst if newer than
// lastFrameId. Writes the DepthInfo into outInfo. Returns outInfo.valid.
__attribute__((visibility("default")))
int oxrsys_depth_get_latest(unsigned short* dst, unsigned int dstCount, unsigned int lastFrameId,
                            oxr::protocol::camera::DepthInfo* outInfo)
{
    oxr::protocol::camera::DepthInfo info = CameraRelay::Get().GetLatestDepth(dst, dstCount, lastFrameId);
    if (outInfo != nullptr) *outInfo = info;
    return info.valid;
}

}
