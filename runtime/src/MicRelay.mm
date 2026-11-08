// SPDX-License-Identifier: MPL-2.0

#include "MicRelay.h"

#include "../../common/CameraProtocol.h"

#include <CoreAudio/CoreAudio.h>
#include <CoreFoundation/CoreFoundation.h>
#include <arpa/inet.h>
#include <netinet/in.h>
#include <sys/socket.h>
#include <unistd.h>

#include <spdlog/spdlog.h>

#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstdlib>
#include <cstring>

using namespace oxr::protocol::camera;

namespace
{
constexpr const char* kDeviceUid = "LinkParaMac_QuestMic_UID"; // must match mic-driver/QuestMic.c

int64_t NowNs()
{
    return std::chrono::duration_cast<std::chrono::nanoseconds>(std::chrono::steady_clock::now().time_since_epoch()).count();
}

AudioObjectID FindQuestMic()
{
    CFStringRef uid = CFStringCreateWithCString(kCFAllocatorDefault, kDeviceUid, kCFStringEncodingUTF8);
    AudioObjectID device = kAudioObjectUnknown;
    AudioValueTranslation translation = {&uid, sizeof(uid), &device, sizeof(device)};
    UInt32 size = sizeof(translation);
    AudioObjectPropertyAddress address = {kAudioHardwarePropertyDeviceForUID, kAudioObjectPropertyScopeGlobal,
                                          kAudioObjectPropertyElementMain};
    OSStatus status = AudioObjectGetPropertyData(kAudioObjectSystemObject, &address, 0, nullptr, &size, &translation);
    CFRelease(uid);
    return status == noErr ? device : kAudioObjectUnknown;
}

AudioObjectID DefaultInput()
{
    AudioObjectID device = kAudioObjectUnknown;
    UInt32 size = sizeof(device);
    AudioObjectPropertyAddress address = {kAudioHardwarePropertyDefaultInputDevice, kAudioObjectPropertyScopeGlobal,
                                          kAudioObjectPropertyElementMain};
    AudioObjectGetPropertyData(kAudioObjectSystemObject, &address, 0, nullptr, &size, &device);
    return device;
}

bool SetDefault(AudioObjectID device)
{
    AudioObjectPropertyAddress address = {kAudioHardwarePropertyDefaultInputDevice, kAudioObjectPropertyScopeGlobal,
                                          kAudioObjectPropertyElementMain};
    return AudioObjectSetPropertyData(kAudioObjectSystemObject, &address, 0, nullptr, sizeof(device), &device) == noErr;
}
} // namespace

MicRelay& MicRelay::Get()
{
    static MicRelay instance;
    return instance;
}

MicRelay::~MicRelay()
{
    SetDefaultInput(false); // never leave a silent device as the default input
    if (udp_ >= 0) close(udp_);
}

void MicRelay::OnMic(const uint8_t* payload, size_t size)
{
    if (size < sizeof(AudioHeader)) return;
    AudioHeader ah = {};
    std::memcpy(&ah, payload, sizeof(ah));
    const size_t samples = static_cast<size_t>(ah.frames) * ah.channels;
    if (ah.format != 1 || ah.channels == 0 || ah.payloadSize != samples * sizeof(int16_t) ||
        size < sizeof(ah) + ah.payloadSize || ah.frames > 48000)
        return;
    const auto* pcm = reinterpret_cast<const int16_t*>(payload + sizeof(ah));

    // Mono (first channel) for the driver and for Unity.
    std::vector<int16_t> mono(ah.frames);
    double energy = 0.0;
    for (uint32_t i = 0; i < ah.frames; ++i)
    {
        int16_t s;
        std::memcpy(&s, pcm + static_cast<size_t>(i) * ah.channels, sizeof(s));
        mono[i] = s;
        energy += (s / 32768.0) * (s / 32768.0);
    }
    level_.store(static_cast<float>(std::sqrt(energy / std::max<uint32_t>(1, ah.frames))));
    lastNs_.store(NowNs());
    const uint32_t count = packets_.fetch_add(1) + 1;
    if (count == 1 || count % 3000 == 0)
        spdlog::info("MicRelay: headset microphone {} Hz x{} ({} messages)", ah.sampleRate, ah.channels, count);

    std::lock_guard<std::mutex> lock(mutex_);
    // 1) the audio driver (UDP on localhost; nothing listens if it is not installed)
    if (udp_ < 0)
    {
        udp_ = socket(AF_INET, SOCK_DGRAM, 0);
        if (udp_ >= 0)
        {
            int buffer = 256 * 1024;
            setsockopt(udp_, SOL_SOCKET, SO_SNDBUF, &buffer, sizeof(buffer));
        }
    }
    if (udp_ >= 0)
    {
        std::vector<uint8_t> datagram(sizeof(MicDatagram) + mono.size() * sizeof(int16_t));
        MicDatagram header = {};
        header.sampleRate = ah.sampleRate;
        header.frames = ah.frames;
        header.sequence = ++sequence_;
        std::memcpy(datagram.data(), &header, sizeof(header));
        std::memcpy(datagram.data() + sizeof(header), mono.data(), mono.size() * sizeof(int16_t));
        sockaddr_in to = {};
        to.sin_family = AF_INET;
        to.sin_port = htons(MIC_DRIVER_PORT);
        to.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
        sendto(udp_, datagram.data(), datagram.size(), 0, reinterpret_cast<sockaddr*>(&to), sizeof(to));
    }
    // 2) the ring for Unity (2 s)
    if (sampleRate_ != ah.sampleRate || ring_.empty())
    {
        sampleRate_ = ah.sampleRate;
        ring_.assign(static_cast<size_t>(sampleRate_) * 2, 0.0f);
        readPos_ = filled_ = 0;
    }
    const size_t capacity = ring_.size();
    for (int16_t s : mono)
    {
        if (filled_ == capacity) { readPos_ = (readPos_ + 1) % capacity; --filled_; }
        ring_[(readPos_ + filled_) % capacity] = s / 32768.0f;
        ++filled_;
    }
}

size_t MicRelay::Pull(float* dst, size_t maxSamples, uint32_t& sampleRate)
{
    std::lock_guard<std::mutex> lock(mutex_);
    sampleRate = sampleRate_;
    if (dst == nullptr || ring_.empty()) return 0;
    const size_t take = std::min(maxSamples, filled_);
    for (size_t i = 0; i < take; ++i) dst[i] = ring_[(readPos_ + i) % ring_.size()];
    readPos_ = (readPos_ + take) % ring_.size();
    filled_ -= take;
    return take;
}

OxrsysMicStatus MicRelay::Status()
{
    OxrsysMicStatus status = {};
    status.packets = packets_.load();
    status.sampleRate = sampleRate_;
    const int64_t last = lastNs_.load();
    status.lastAgeMs = last == 0 ? -1.0f : static_cast<float>((NowNs() - last) / 1e6);
    status.level = level_.load();
    const AudioObjectID quest = FindQuestMic();
    status.driverInstalled = quest != kAudioObjectUnknown ? 1u : 0u;
    status.isDefaultInput = quest != kAudioObjectUnknown && DefaultInput() == quest ? 1u : 0u;
    return status;
}

bool MicRelay::SetDefaultInput(bool useQuestMic)
{
    std::lock_guard<std::mutex> lock(defaultMutex_);
    if (useQuestMic)
    {
        const AudioObjectID quest = FindQuestMic();
        if (quest == kAudioObjectUnknown) return false;
        const AudioObjectID current = DefaultInput();
        if (current == quest) return true;
        if (!switched_) previousDefault_ = current;
        switched_ = SetDefault(quest) || switched_;
        if (switched_) spdlog::info("MicRelay: default input -> Quest 3 Microphone");
        return switched_;
    }
    if (!switched_) return true;
    switched_ = false;
    const AudioObjectID quest = FindQuestMic();
    if (previousDefault_ != kAudioObjectUnknown && (quest == kAudioObjectUnknown || DefaultInput() == quest))
    {
        SetDefault(previousDefault_);
        spdlog::info("MicRelay: default input restored");
    }
    return true;
}

void MicRelay::OnInstanceCreated()
{
    const char* env = std::getenv("OXRSYS_MIC_DEFAULT");
    if (env != nullptr && env[0] == '0') return;
    SetDefaultInput(true);
}

void MicRelay::OnInstanceDestroyed()
{
    SetDefaultInput(false);
}

extern "C" {

// Up to maxSamples mono float samples of the headset microphone (oldest first). Returns the count.
__attribute__((visibility("default")))
int oxrsys_mic_pull(float* dst, int maxSamples, int* sampleRate)
{
    uint32_t rate = 0;
    const size_t n = maxSamples > 0 ? MicRelay::Get().Pull(dst, static_cast<size_t>(maxSamples), rate) : 0;
    if (sampleRate != nullptr) *sampleRate = static_cast<int>(rate);
    return static_cast<int>(n);
}

__attribute__((visibility("default")))
int oxrsys_mic_status(OxrsysMicStatus* out)
{
    if (out == nullptr) return 0;
    *out = MicRelay::Get().Status();
    return 1;
}

// 1: the Quest microphone becomes the default input; 0: the previous one comes back. Returns 1 on success.
__attribute__((visibility("default")))
int oxrsys_mic_set_default(int useQuestMic)
{
    return MicRelay::Get().SetDefaultInput(useQuestMic != 0) ? 1 : 0;
}

}
