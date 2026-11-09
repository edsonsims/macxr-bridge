// SPDX-License-Identifier: MPL-2.0
//
// MacXR Bridge: plays the Editor's audio mix on the headset (AAudio, low latency). PCM arrives over the
// camera channel (MsgKind::Audio); a small jitter buffer absorbs network bursts and the Mac/Quest clock
// drift (it trims back to the target when it grows, and plays silence on underrun).

#pragma once

#include <aaudio/AAudio.h>

#include <atomic>
#include <cstdint>
#include <mutex>
#include <vector>

class AudioPlayer
{
public:
    static AudioPlayer& Get();

    // Any thread: queue interleaved s16 frames. (Re)opens the stream when the format changes.
    void Push(const int16_t* samples, uint32_t frames, uint32_t channels, uint32_t sampleRate);
    void Stop();

private:
    AudioPlayer() = default;
    bool OpenLocked(uint32_t channels, uint32_t sampleRate);
    void CloseLocked();
    static aaudio_data_callback_result_t DataCallback(AAudioStream* stream, void* user, void* audioData, int32_t numFrames);
    static void ErrorCallback(AAudioStream* stream, void* user, aaudio_result_t error);

    std::mutex mutex_;
    AAudioStream* stream_ = nullptr;
    uint32_t channels_ = 0;        // of the PCM we receive
    uint32_t sampleRate_ = 0;
    // What the opened stream actually uses: AAudio may pick another format or channel count than requested, and
    // the callback must fill the whole buffer in that format (writing s16 into a float buffer leaves half of it
    // uninitialized — loud noise).
    aaudio_format_t outFormat_ = AAUDIO_FORMAT_PCM_I16;
    uint32_t outChannels_ = 0;
    // Ring buffer of interleaved samples.
    std::vector<int16_t> ring_;
    size_t readPos_ = 0;   // in samples
    size_t filled_ = 0;    // in samples
    bool primed_ = false;  // wait for the target fill before playing
    uint32_t underruns_ = 0;
    uint32_t trims_ = 0;
    std::atomic<bool> restartRequested_{false};
};
