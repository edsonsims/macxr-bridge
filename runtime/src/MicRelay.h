// SPDX-License-Identifier: MPL-2.0
//
// MacXR Bridge: the headset microphone on the Mac, like Quest Link's microphone on Windows.
// The client streams the Quest mic on the camera channel (MsgKind::Mic). The runtime:
//  - forwards it over UDP on localhost to the "Headset Microphone" audio driver (macxr-bridge/mic-driver),
//    so every app, Unity's Microphone class included, records from the headset;
//  - keeps the last two seconds for Unity (oxrsys_mic_pull) when the driver is not installed;
//  - while an XrInstance lives, makes that device the default input (restored afterwards;
//    OXRSYS_MIC_DEFAULT=0 disables).

#pragma once

#include <atomic>
#include <cstddef>
#include <cstdint>
#include <mutex>
#include <vector>

struct OxrsysMicStatus
{
    uint32_t packets;          // mic messages received since load
    uint32_t sampleRate;
    float lastAgeMs;           // time since the last one (< 0: never)
    float level;               // RMS of the last message, 0..1
    uint32_t driverInstalled;  // the "Headset Microphone" device exists
    uint32_t isDefaultInput;   // it is the system's default input now
    uint32_t reserved[2];
};
static_assert(sizeof(OxrsysMicStatus) == 32, "shared with C#");

class MicRelay
{
public:
    static MicRelay& Get();

    void OnMic(const uint8_t* payload, size_t size); // AudioHeader + PCM s16
    size_t Pull(float* dst, size_t maxSamples, uint32_t& sampleRate);
    OxrsysMicStatus Status();

    void OnInstanceCreated();
    void OnInstanceDestroyed();
    bool SetDefaultInput(bool useHeadsetMic);

private:
    MicRelay() = default;
    ~MicRelay();

    int udp_ = -1;
    std::mutex mutex_;
    std::vector<float> ring_;
    size_t readPos_ = 0;
    size_t filled_ = 0;
    uint32_t sampleRate_ = 48000;
    uint32_t sequence_ = 0;
    std::atomic<uint32_t> packets_{0};
    std::atomic<int64_t> lastNs_{0};
    std::atomic<float> level_{0.0f};

    std::mutex defaultMutex_;
    uint32_t previousDefault_ = 0; // AudioObjectID
    bool switched_ = false;
};
