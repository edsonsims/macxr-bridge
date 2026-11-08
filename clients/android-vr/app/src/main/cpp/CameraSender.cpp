// SPDX-License-Identifier: MPL-2.0

#include "CameraSender.h"

#include "Protocol.h"
#include "AudioPlayer.h"

#include <android/log.h>
#include <jni.h>
#include <sys/socket.h>
#include <unistd.h>

#include <cstring>
#include <vector>

#define LOG_TAG "OXRSys-Camera"
#define LOGI(...) __android_log_print(ANDROID_LOG_INFO, LOG_TAG, __VA_ARGS__)
#define LOGW(...) __android_log_print(ANDROID_LOG_WARN, LOG_TAG, __VA_ARGS__)

namespace protocol = oxr::protocol;
namespace cam = oxr::protocol::camera;

CameraSender& CameraSender::Get()
{
    static CameraSender instance;
    return instance;
}

void CameraSender::Attach(int socket)
{
    JoinReader();
    {
        std::lock_guard<std::mutex> lock(sendMutex_);
        socket_.store(socket);
        helloSent_ = false;
    }
    reader_ = std::thread(&CameraSender::ReaderLoop, this, socket);
    LOGI("Camera channel attached (socket %d)", socket);
}

void CameraSender::JoinReader()
{
    if (reader_.joinable()) reader_.join();
}

namespace
{
bool ReadExactly(int fd, void* data, size_t size)
{
    auto* bytes = static_cast<uint8_t*>(data);
    size_t got = 0;
    while (got < size)
    {
        ssize_t n = recv(fd, bytes + got, size - got, 0);
        if (n < 0 && errno == EINTR) continue;
        if (n <= 0) return false;
        got += static_cast<size_t>(n);
    }
    return true;
}
} // namespace

// Runtime -> client on the camera channel: today only the Editor's audio mix.
void CameraSender::ReaderLoop(int socket)
{
    std::vector<uint8_t> payload;
    std::vector<int16_t> pcm;
    uint64_t lastSequence = 0;
    uint32_t gaps = 0, packets = 0;
    for (;;)
    {
        protocol::TcpRecordHeader header = {};
        if (!ReadExactly(socket, &header, sizeof(header))) break;
        if (header.magic != protocol::TCP_RECORD_MAGIC || header.payloadSize > protocol::TCP_MAX_RECORD_PAYLOAD) break;
        payload.resize(header.payloadSize);
        if (!payload.empty() && !ReadExactly(socket, payload.data(), payload.size())) break;
        if (header.type == protocol::TcpRecordType::Disconnect) break;
        if (header.type != protocol::TcpRecordType::Camera || payload.size() < sizeof(cam::MsgHeader)) continue;
        cam::MsgHeader mh = {};
        std::memcpy(&mh, payload.data(), sizeof(mh));
        if (mh.kind != cam::MsgKind::Audio || payload.size() < sizeof(mh) + sizeof(cam::AudioHeader)) continue;
        cam::AudioHeader ah = {};
        std::memcpy(&ah, payload.data() + sizeof(mh), sizeof(ah));
        const size_t samples = static_cast<size_t>(ah.frames) * ah.channels;
        if (ah.format != 1 || ah.payloadSize != samples * sizeof(int16_t) ||
            payload.size() < sizeof(mh) + sizeof(ah) + ah.payloadSize) continue;
        pcm.resize(samples);
        std::memcpy(pcm.data(), payload.data() + sizeof(mh) + sizeof(ah), ah.payloadSize);
        if (lastSequence != 0 && ah.sequence != lastSequence + 1) ++gaps;
        lastSequence = ah.sequence;
        if (++packets == 1 || packets % 3000 == 0)
        {
            LOGI("Audio from the Editor: %u Hz x%u, %u frames/packet (packet %u, gaps %u)", ah.sampleRate, ah.channels,
                 ah.frames, packets, gaps);
        }
        AudioPlayer::Get().Push(pcm.data(), ah.frames, ah.channels, ah.sampleRate);
    }
    AudioPlayer::Get().Stop();
    LOGI("Camera channel reader ended (%u audio packets)", packets);
}

void CameraSender::Detach()
{
    std::lock_guard<std::mutex> lock(sendMutex_);
    socket_.store(-1);
    helloSent_ = false;
}

void CameraSender::SetHeadPose(const XrPosef& pose)
{
    std::lock_guard<std::mutex> lock(poseMutex_);
    headPose_ = pose;
}

bool CameraSender::SendAll(const void* data, size_t size)
{
    const int fd = socket_.load();
    if (fd < 0) return false;
    const auto* bytes = static_cast<const uint8_t*>(data);
    size_t sent = 0;
    while (sent < size)
    {
        ssize_t n = send(fd, bytes + sent, size - sent, MSG_NOSIGNAL);
        if (n < 0)
        {
            if (errno == EINTR) continue;
            return false;
        }
        if (n == 0) return false;
        sent += static_cast<size_t>(n);
    }
    return true;
}

void CameraSender::PushFrame(const uint8_t* jpeg, int length, int width, int height,
                             float fx, float fy, float cx, float cy,
                             const XrPosef& headFromCamera, int64_t captureTimeNs)
{
    if (socket_.load() < 0 || length <= 0) return;
    std::lock_guard<std::mutex> lock(sendMutex_);
    if (socket_.load() < 0) return;

    cam::Intrinsics intr = {};
    intr.fx = fx; intr.fy = fy; intr.cx = cx; intr.cy = cy; intr.skew = 0.0f;
    intr.width = static_cast<uint32_t>(width); intr.height = static_cast<uint32_t>(height);

    cam::Pose headFromCam = {};
    headFromCam.orientation = {headFromCamera.orientation.x, headFromCamera.orientation.y,
                               headFromCamera.orientation.z, headFromCamera.orientation.w};
    headFromCam.position = {headFromCamera.position.x, headFromCamera.position.y, headFromCamera.position.z};

    if (!helloSent_)
    {
        cam::Hello hello = {};
        hello.eye = 0;
        hello.maxFramerate = 30;
        hello.intrinsics = intr;
        hello.headFromCamera = headFromCam;
        protocol::TcpRecordHeader rec = {};
        rec.type = protocol::TcpRecordType::Camera;
        rec.payloadSize = sizeof(cam::MsgHeader) + sizeof(cam::Hello);
        cam::MsgHeader mh = {}; mh.kind = cam::MsgKind::Hello; mh.payloadSize = sizeof(cam::Hello);
        if (SendAll(&rec, sizeof(rec)) && SendAll(&mh, sizeof(mh)) && SendAll(&hello, sizeof(hello)))
        {
            helloSent_ = true;
            LOGI("Camera Hello sent (%dx%d)", width, height);
        }
        else { socket_.store(-1); return; }
    }

    cam::FrameHeader fh = {};
    fh.format = cam::FORMAT_JPEG;
    fh.width = static_cast<uint32_t>(width);
    fh.height = static_cast<uint32_t>(height);
    fh.payloadSize = static_cast<uint32_t>(length);
    fh.captureTimeNs = captureTimeNs;
    {
        std::lock_guard<std::mutex> plock(poseMutex_);
        fh.headPose.orientation = {headPose_.orientation.x, headPose_.orientation.y, headPose_.orientation.z, headPose_.orientation.w};
        fh.headPose.position = {headPose_.position.x, headPose_.position.y, headPose_.position.z};
    }
    fh.headFromCamera = headFromCam;
    fh.intrinsics = intr;

    protocol::TcpRecordHeader rec = {};
    rec.type = protocol::TcpRecordType::Camera;
    rec.payloadSize = static_cast<uint32_t>(sizeof(cam::MsgHeader) + sizeof(cam::FrameHeader) + length);
    cam::MsgHeader mh = {}; mh.kind = cam::MsgKind::Frame;
    mh.payloadSize = static_cast<uint32_t>(sizeof(cam::FrameHeader) + length);

    if (!(SendAll(&rec, sizeof(rec)) && SendAll(&mh, sizeof(mh)) && SendAll(&fh, sizeof(fh)) && SendAll(jpeg, length)))
    {
        LOGW("Camera frame send failed; detaching");
        socket_.store(-1);
        helloSent_ = false;
    }
}

void CameraSender::PushDepth(const cam::DepthHeader& header, const uint16_t* pixels)
{
    if (socket_.load() < 0 || pixels == nullptr || header.payloadSize == 0) return;
    std::lock_guard<std::mutex> lock(sendMutex_);
    if (socket_.load() < 0) return;

    protocol::TcpRecordHeader rec = {};
    rec.type = protocol::TcpRecordType::Camera;
    rec.payloadSize = static_cast<uint32_t>(sizeof(cam::MsgHeader) + sizeof(cam::DepthHeader) + header.payloadSize);
    cam::MsgHeader mh = {}; mh.kind = cam::MsgKind::Depth;
    mh.payloadSize = static_cast<uint32_t>(sizeof(cam::DepthHeader) + header.payloadSize);
    if (!(SendAll(&rec, sizeof(rec)) && SendAll(&mh, sizeof(mh)) && SendAll(&header, sizeof(header)) &&
          SendAll(pixels, header.payloadSize)))
    {
        LOGW("Depth send failed; detaching");
        socket_.store(-1);
        helloSent_ = false;
    }
}

extern "C" JNIEXPORT void JNICALL
Java_com_oculus_HeadsetCamera_nativePushCameraFrame(JNIEnv* env, jclass, jbyteArray jpeg, jint length,
                                                    jint width, jint height,
                                                    jfloat fx, jfloat fy, jfloat cx, jfloat cy,
                                                    jfloat px, jfloat py, jfloat pz,
                                                    jfloat qx, jfloat qy, jfloat qz, jfloat qw,
                                                    jlong timestampNs)
{
    if (!CameraSender::Get().IsAttached()) return;
    jbyte* data = env->GetByteArrayElements(jpeg, nullptr);
    if (data == nullptr) return;
    XrPosef headFromCamera = {};
    headFromCamera.orientation = {qx, qy, qz, qw};
    headFromCamera.position = {px, py, pz};
    CameraSender::Get().PushFrame(reinterpret_cast<const uint8_t*>(data), length, width, height,
                                  fx, fy, cx, cy, headFromCamera, timestampNs);
    env->ReleaseByteArrayElements(jpeg, data, JNI_ABORT);
}
