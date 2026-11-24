// SPDX-License-Identifier: MPL-2.0

#include "CameraRelay.h"
#include "../../common/Protocol.h"
#include "Config.h"
#include "MicRelay.h"

#import <CoreGraphics/CoreGraphics.h>
#import <ImageIO/ImageIO.h>

#include <spdlog/spdlog.h>

#include <algorithm>
#include <chrono>
#include <sys/socket.h>
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
        if (!depthReplay_.load()) depthInfo_.valid = 0; // a replayed session keeps its depth
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
    if (header.kind == MsgKind::Mic)
    {
        MicRelay::Get().OnMic(payload, payloadSize);
        return;
    }
    if (header.kind == MsgKind::Stats)
    {
        ClientStats stats = {};
        std::memcpy(&stats, payload, std::min(payloadSize, sizeof(stats)));
        std::lock_guard<std::mutex> lock(mutex_);
        clientStats_ = stats;
        clientStatsNs_ = std::chrono::duration_cast<std::chrono::nanoseconds>(
            std::chrono::steady_clock::now().time_since_epoch()).count();
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

void CameraRelay::SetDepthReplay(bool on)
{
    depthReplay_.store(on);
    spdlog::info("CameraRelay: depth from {}", on ? "a recorded session" : "the headset");
}

void CameraRelay::InjectDepth(const DepthInfo& info, const uint16_t* pixels, uint32_t count)
{
    const size_t n = static_cast<size_t>(info.width) * info.height;
    if (pixels == nullptr || n == 0 || count < n) return;
    auto depth = std::make_shared<std::vector<uint16_t>>(pixels, pixels + n);
    const int64_t now = std::chrono::duration_cast<std::chrono::nanoseconds>(
        std::chrono::steady_clock::now().time_since_epoch()).count();
    std::lock_guard<std::mutex> lock(mutex_);
    depth_ = std::move(depth);
    depthReceivedNs_ = now;
    depthInfo_ = info;
    depthInfo_.valid = 1;
    depthInfo_.frameId = nextDepthId_++;
}

void CameraRelay::OnDepth(const uint8_t* payload, size_t size)
{
    if (size < sizeof(DepthHeader)) return;
    if (depthReplay_.load()) return; // a recorded session is playing
    DepthHeader dh = {};
    std::memcpy(&dh, payload, sizeof(dh));
    const size_t pixels = static_cast<size_t>(dh.width) * dh.height;
    if (pixels == 0 || dh.payloadSize != pixels * sizeof(uint16_t) || size != sizeof(DepthHeader) + dh.payloadSize) return;
    auto depth = std::make_shared<std::vector<uint16_t>>(pixels);
    std::memcpy(depth->data(), payload + sizeof(DepthHeader), dh.payloadSize);
    const int64_t now = std::chrono::duration_cast<std::chrono::nanoseconds>(
        std::chrono::steady_clock::now().time_since_epoch()).count();

    std::lock_guard<std::mutex> lock(mutex_);
    depth_ = std::move(depth);
    depthReceivedNs_ = now;
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

void CameraRelay::SetSocket(int socket)
{
    std::lock_guard<std::mutex> lock(sendMutex_);
    socket_ = socket;
    if (socket < 0)
    {
        std::lock_guard<std::mutex> audioLock(audioMutex_);
        audioQueue_.clear();
    }
}

bool CameraRelay::PushAudio(const float* samples, uint32_t frames, uint32_t channels, uint32_t sampleRate)
{
    if (samples == nullptr || frames == 0 || channels == 0 || channels > 8) return false;
    {
        std::lock_guard<std::mutex> lock(sendMutex_);
        if (socket_ < 0) return false;
    }
    if (!audioSenderStarted_.exchange(true))
    {
        audioSender_ = std::thread(&CameraRelay::AudioSenderLoop, this);
        audioSender_.detach(); // lives as long as the runtime image
    }
    {
        std::lock_guard<std::mutex> lock(audioMutex_);
        if (channels != audioChannels_ || sampleRate != audioRate_)
        {
            audioQueue_.clear();
            audioChannels_ = channels;
            audioRate_ = sampleRate;
            spdlog::info("CameraRelay: audio from the Editor {} Hz x{}", sampleRate, channels);
        }
        // Never let a stalled socket grow the queue: keep at most ~250 ms.
        const size_t maxSamples = static_cast<size_t>(sampleRate) * channels / 4;
        const size_t count = static_cast<size_t>(frames) * channels;
        for (size_t i = 0; i < count; ++i)
        {
            const float v = std::clamp(samples[i], -1.0f, 1.0f);
            audioQueue_.push_back(static_cast<int16_t>(v * 32767.0f));
        }
        if (audioQueue_.size() > maxSamples)
        {
            const size_t drop = (audioQueue_.size() - maxSamples) / channels * channels;
            audioQueue_.erase(audioQueue_.begin(), audioQueue_.begin() + static_cast<std::ptrdiff_t>(drop));
        }
    }
    audioCv_.notify_one();
    return true;
}

void CameraRelay::AudioSenderLoop()
{
    std::vector<int16_t> chunk;
    std::vector<uint8_t> message;
    for (;;)
    {
        uint32_t channels = 0, rate = 0;
        uint64_t sequence = 0;
        {
            std::unique_lock<std::mutex> lock(audioMutex_);
            audioCv_.wait_for(lock, std::chrono::milliseconds(20), [this] { return !audioQueue_.empty(); });
            if (audioQueue_.empty() || audioChannels_ == 0) continue;
            channels = audioChannels_;
            rate = audioRate_;
            // Send whole frames, at most ~20 ms per message.
            const size_t maxSamples = std::max<size_t>(channels, static_cast<size_t>(rate) * channels / 50);
            const size_t take = std::min(audioQueue_.size(), maxSamples) / channels * channels;
            if (take == 0) continue;
            chunk.assign(audioQueue_.begin(), audioQueue_.begin() + static_cast<std::ptrdiff_t>(take));
            audioQueue_.erase(audioQueue_.begin(), audioQueue_.begin() + static_cast<std::ptrdiff_t>(take));
            sequence = ++audioSequence_;
        }
        AudioHeader ah = {};
        ah.sampleRate = rate;
        ah.channels = static_cast<uint16_t>(channels);
        ah.format = 1;
        ah.frames = static_cast<uint32_t>(chunk.size() / channels);
        ah.payloadSize = static_cast<uint32_t>(chunk.size() * sizeof(int16_t));
        ah.sequence = sequence;
        MsgHeader mh = {};
        mh.kind = MsgKind::Audio;
        mh.payloadSize = static_cast<uint32_t>(sizeof(ah) + ah.payloadSize);
        oxr::protocol::TcpRecordHeader rec = {};
        rec.type = oxr::protocol::TcpRecordType::Camera;
        rec.payloadSize = static_cast<uint32_t>(sizeof(mh) + mh.payloadSize);
        message.resize(sizeof(rec) + rec.payloadSize);
        uint8_t* p = message.data();
        std::memcpy(p, &rec, sizeof(rec)); p += sizeof(rec);
        std::memcpy(p, &mh, sizeof(mh)); p += sizeof(mh);
        std::memcpy(p, &ah, sizeof(ah)); p += sizeof(ah);
        std::memcpy(p, chunk.data(), ah.payloadSize);

        std::lock_guard<std::mutex> lock(sendMutex_);
        if (socket_ < 0) continue;
        size_t sent = 0;
        while (sent < message.size())
        {
            const ssize_t n = send(socket_, message.data() + sent, message.size() - sent, 0);
            if (n < 0 && errno == EINTR) continue;
            if (n <= 0) break; // the camera thread notices the dead socket and detaches
            sent += static_cast<size_t>(n);
        }
        if (sequence == 1 || sequence % 3000 == 0)
        {
            spdlog::info("CameraRelay: audio packet #{} ({} frames) sent to the headset", sequence, ah.frames);
        }
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
        if (dstCount < need || !depth_ || depth_->size() < need)
        {
            out.valid = 0;
            return out;
        }
        std::memcpy(dst, depth_->data(), need * sizeof(uint16_t));
    }
    return out;
}

bool CameraRelay::GetDepthSnapshot(DepthInfo& info, std::shared_ptr<const std::vector<uint16_t>>& pixels,
                                   int64_t& ageNs) const
{
    std::lock_guard<std::mutex> lock(mutex_);
    // A replayed session's depth is current by definition: it must not expire while the replay waits (paused,
    // lockstep tests waiting on inference) or when the live headset's camera channel reconnects (Wi-Fi). Otherwise
    // ray casts fall back to the app's own raycast and session tests stop being deterministic.
    const bool replay = depthReplay_.load();
    if (!depthInfo_.valid || !depth_ || (!connected_ && !replay)) return false;
    info = depthInfo_;
    pixels = depth_;
    const int64_t now = std::chrono::duration_cast<std::chrono::nanoseconds>(
        std::chrono::steady_clock::now().time_since_epoch()).count();
    ageNs = replay ? 0 : now - depthReceivedNs_;
    return true;
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

bool CameraRelay::GetClientStats(ClientStats& stats, float& ageSeconds) const
{
    std::lock_guard<std::mutex> lock(mutex_);
    if (clientStatsNs_ == 0) return false;
    stats = clientStats_;
    const int64_t now = std::chrono::duration_cast<std::chrono::nanoseconds>(
        std::chrono::steady_clock::now().time_since_epoch()).count();
    ageSeconds = static_cast<float>((now - clientStatsNs_) / 1e9);
    return true;
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

// Headset battery/temperature/frame rate for the developer HUD. Returns 1 when the client sent any.
__attribute__((visibility("default")))
int oxrsys_client_stats_get(oxr::protocol::camera::ClientStats* out, float* ageSeconds)
{
    if (out == nullptr) return 0;
    float age = 0.0f;
    if (!CameraRelay::Get().GetClientStats(*out, age)) return 0;
    if (ageSeconds != nullptr) *ageSeconds = age;
    return 1;
}

// Session replay: 1 = depth comes only from oxrsys_depth_inject (live depth ignored), 0 = back to the headset.
__attribute__((visibility("default")))
void oxrsys_depth_replay_mode(int on)
{
    CameraRelay::Get().SetDepthReplay(on != 0);
}

// One recorded depth frame (info in client STAGE, as oxrsys_depth_get_latest returns it).
__attribute__((visibility("default")))
void oxrsys_depth_inject(const unsigned short* pixels, unsigned int count, const oxr::protocol::camera::DepthInfo* info)
{
    if (info != nullptr) CameraRelay::Get().InjectDepth(*info, pixels, count);
}

// Path of runtime_status.json (stream stats: bitrate, latencies, encoder) written about once a second.
__attribute__((visibility("default")))
const char* oxrsys_runtime_status_path()
{
    return ::Config::Get().runtimeStatusPath.c_str();
}

// Unity audio thread: the Editor's final mix (interleaved float). Returns 1 while a headset is attached.
__attribute__((visibility("default")))
int oxrsys_audio_push(const float* samples, int frames, int channels, int sampleRate)
{
    if (frames <= 0 || channels <= 0 || sampleRate <= 0) return 0;
    return CameraRelay::Get().PushAudio(samples, static_cast<uint32_t>(frames), static_cast<uint32_t>(channels),
                                        static_cast<uint32_t>(sampleRate)) ? 1 : 0;
}

}
