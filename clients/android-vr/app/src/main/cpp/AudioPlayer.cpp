// SPDX-License-Identifier: MPL-2.0

#include "AudioPlayer.h"

#include <android/log.h>

#include <algorithm>
#include <cstring>

#define LOG_TAG "OXRSys-Audio"
#define LOGI(...) __android_log_print(ANDROID_LOG_INFO, LOG_TAG, __VA_ARGS__)
#define LOGW(...) __android_log_print(ANDROID_LOG_WARN, LOG_TAG, __VA_ARGS__)

namespace
{
constexpr uint32_t kTargetMs = 60;    // jitter buffer we aim to keep
constexpr uint32_t kMaxMs = 160;      // above this we drop the oldest audio back to the target
constexpr uint32_t kCapacityMs = 500; // ring size
}

AudioPlayer& AudioPlayer::Get()
{
    static AudioPlayer instance;
    return instance;
}

bool AudioPlayer::OpenLocked(uint32_t channels, uint32_t sampleRate)
{
    CloseLocked();
    AAudioStreamBuilder* builder = nullptr;
    if (AAudio_createStreamBuilder(&builder) != AAUDIO_OK) return false;
    AAudioStreamBuilder_setDirection(builder, AAUDIO_DIRECTION_OUTPUT);
    AAudioStreamBuilder_setSampleRate(builder, static_cast<int32_t>(sampleRate));
    AAudioStreamBuilder_setChannelCount(builder, static_cast<int32_t>(channels));
    AAudioStreamBuilder_setFormat(builder, AAUDIO_FORMAT_PCM_I16);
    AAudioStreamBuilder_setPerformanceMode(builder, AAUDIO_PERFORMANCE_MODE_LOW_LATENCY);
    AAudioStreamBuilder_setSharingMode(builder, AAUDIO_SHARING_MODE_SHARED);
    AAudioStreamBuilder_setUsage(builder, AAUDIO_USAGE_GAME);
    AAudioStreamBuilder_setDataCallback(builder, &AudioPlayer::DataCallback, this);
    AAudioStreamBuilder_setErrorCallback(builder, &AudioPlayer::ErrorCallback, this);
    aaudio_result_t result = AAudioStreamBuilder_openStream(builder, &stream_);
    AAudioStreamBuilder_delete(builder);
    if (result != AAUDIO_OK || stream_ == nullptr)
    {
        LOGW("AAudio open failed: %s", AAudio_convertResultToText(result));
        stream_ = nullptr;
        return false;
    }
    channels_ = channels;
    sampleRate_ = sampleRate;
    outFormat_ = AAudioStream_getFormat(stream_);
    outChannels_ = static_cast<uint32_t>(std::max(1, AAudioStream_getChannelCount(stream_)));
    if (outFormat_ != AAUDIO_FORMAT_PCM_I16 && outFormat_ != AAUDIO_FORMAT_PCM_FLOAT)
    {
        LOGW("AAudio picked format %d; not supported", static_cast<int>(outFormat_));
        CloseLocked();
        return false;
    }
    ring_.assign(static_cast<size_t>(sampleRate) * channels * kCapacityMs / 1000, 0);
    readPos_ = 0;
    filled_ = 0;
    primed_ = false;
    result = AAudioStream_requestStart(stream_);
    LOGI("Audio stream %u Hz x%u -> device %d Hz x%u %s (burst %d) start=%s", sampleRate, channels,
         AAudioStream_getSampleRate(stream_), outChannels_, outFormat_ == AAUDIO_FORMAT_PCM_FLOAT ? "float" : "s16",
         AAudioStream_getFramesPerBurst(stream_), AAudio_convertResultToText(result));
    return result == AAUDIO_OK;
}

void AudioPlayer::CloseLocked()
{
    if (stream_ != nullptr)
    {
        AAudioStream_requestStop(stream_);
        AAudioStream_close(stream_);
        stream_ = nullptr;
    }
    channels_ = 0;
    sampleRate_ = 0;
    outChannels_ = 0;
    filled_ = 0;
    readPos_ = 0;
}

void AudioPlayer::Push(const int16_t* samples, uint32_t frames, uint32_t channels, uint32_t sampleRate)
{
    if (samples == nullptr || frames == 0 || channels == 0 || channels > 8 || sampleRate < 8000) return;
    std::lock_guard<std::mutex> lock(mutex_);
    if (restartRequested_.exchange(false) || stream_ == nullptr || channels != channels_ || sampleRate != sampleRate_)
    {
        if (!OpenLocked(channels, sampleRate)) return;
    }
    const size_t capacity = ring_.size();
    const size_t count = static_cast<size_t>(frames) * channels;
    for (size_t i = 0; i < count; ++i)
    {
        if (filled_ == capacity)
        {
            readPos_ = (readPos_ + channels) % capacity; // full: drop the oldest frame
            filled_ -= channels;
        }
        ring_[(readPos_ + filled_) % capacity] = samples[i];
        ++filled_;
    }
    // Clock drift / bursts: keep the latency bounded.
    const size_t maxSamples = static_cast<size_t>(sampleRate_) * channels_ * kMaxMs / 1000;
    const size_t targetSamples = static_cast<size_t>(sampleRate_) * channels_ * kTargetMs / 1000;
    if (filled_ > maxSamples)
    {
        size_t drop = (filled_ - targetSamples) / channels_ * channels_;
        readPos_ = (readPos_ + drop) % capacity;
        filled_ -= drop;
        if (++trims_ <= 3 || trims_ % 100 == 0) LOGI("Audio buffer trimmed to %u ms (x%u)", kTargetMs, trims_);
    }
}

aaudio_data_callback_result_t AudioPlayer::DataCallback(AAudioStream* stream, void* user, void* audioData, int32_t numFrames)
{
    auto* self = static_cast<AudioPlayer*>(user);
    // Always fill the WHOLE buffer in the stream's real format, starting from silence.
    const aaudio_format_t format = AAudioStream_getFormat(stream);
    const uint32_t outChannels = static_cast<uint32_t>(std::max(1, AAudioStream_getChannelCount(stream)));
    const size_t bytesPerSample = format == AAUDIO_FORMAT_PCM_FLOAT ? sizeof(float) : sizeof(int16_t);
    std::memset(audioData, 0, static_cast<size_t>(numFrames) * outChannels * bytesPerSample);

    std::unique_lock<std::mutex> lock(self->mutex_, std::try_to_lock);
    if (!lock.owns_lock() || self->channels_ == 0 || self->ring_.empty()) return AAUDIO_CALLBACK_RESULT_CONTINUE;
    const uint32_t inChannels = self->channels_;
    const size_t targetSamples = static_cast<size_t>(self->sampleRate_) * inChannels * kTargetMs / 1000;
    if (!self->primed_ && self->filled_ < targetSamples) return AAUDIO_CALLBACK_RESULT_CONTINUE;
    self->primed_ = true;

    const size_t capacity = self->ring_.size();
    const size_t availableFrames = self->filled_ / inChannels;
    const size_t frames = std::min(static_cast<size_t>(numFrames), availableFrames);
    auto* outF = static_cast<float*>(audioData);
    auto* outS = static_cast<int16_t*>(audioData);
    for (size_t f = 0; f < frames; ++f)
    {
        const size_t base = self->readPos_ + f * inChannels;
        for (uint32_t c = 0; c < outChannels; ++c)
        {
            const int16_t v = self->ring_[(base + std::min(c, inChannels - 1)) % capacity];
            if (format == AAUDIO_FORMAT_PCM_FLOAT) outF[f * outChannels + c] = v / 32768.0f;
            else outS[f * outChannels + c] = v;
        }
    }
    const size_t taken = frames * inChannels;
    self->readPos_ = (self->readPos_ + taken) % capacity;
    self->filled_ -= taken;
    if (frames < static_cast<size_t>(numFrames))
    {
        self->primed_ = false; // refill before resuming, so we do not crackle on every packet
        if (++self->underruns_ <= 3 || self->underruns_ % 200 == 0) LOGI("Audio underrun (x%u)", self->underruns_);
    }
    return AAUDIO_CALLBACK_RESULT_CONTINUE;
}

void AudioPlayer::ErrorCallback(AAudioStream*, void* user, aaudio_result_t error)
{
    // E.g. the output device changed or the headset slept: reopen on the next packet (never from this callback).
    LOGW("AAudio error %s; reopening on next packet", AAudio_convertResultToText(error));
    static_cast<AudioPlayer*>(user)->restartRequested_.store(true);
}

void AudioPlayer::Stop()
{
    std::lock_guard<std::mutex> lock(mutex_);
    CloseLocked();
}
