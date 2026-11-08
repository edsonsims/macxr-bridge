// SPDX-License-Identifier: MPL-2.0

#include "MicCapture.h"

#include "CameraSender.h"

#include <aaudio/AAudio.h>
#include <android/log.h>
#include <jni.h>

#include <chrono>
#include <vector>

#define LOG_TAG "OXRSys-Mic"
#define LOGI(...) __android_log_print(ANDROID_LOG_INFO, LOG_TAG, __VA_ARGS__)
#define LOGW(...) __android_log_print(ANDROID_LOG_WARN, LOG_TAG, __VA_ARGS__)

namespace
{
constexpr int32_t kRate = 48000;
constexpr int32_t kChunkFrames = 960; // 20 ms per message
}

MicCapture& MicCapture::Get()
{
    static MicCapture instance;
    return instance;
}

void MicCapture::Start()
{
    if (running_.exchange(true)) return;
    if (thread_.joinable()) thread_.join();
    thread_ = std::thread(&MicCapture::Loop, this);
}

void MicCapture::Stop()
{
    if (!running_.exchange(false)) return;
    if (thread_.joinable()) thread_.join();
}

void MicCapture::Loop()
{
    std::vector<int16_t> chunk(kChunkFrames);
    int failures = 0;
    while (running_.load())
    {
        if (!permission_.load())
        {
            std::this_thread::sleep_for(std::chrono::milliseconds(500));
            continue;
        }
        AAudioStreamBuilder* builder = nullptr;
        AAudioStream* stream = nullptr;
        if (AAudio_createStreamBuilder(&builder) != AAUDIO_OK)
        {
            std::this_thread::sleep_for(std::chrono::seconds(2));
            continue;
        }
        AAudioStreamBuilder_setDirection(builder, AAUDIO_DIRECTION_INPUT);
        AAudioStreamBuilder_setSampleRate(builder, kRate);
        AAudioStreamBuilder_setChannelCount(builder, 1);
        AAudioStreamBuilder_setFormat(builder, AAUDIO_FORMAT_PCM_I16);
        AAudioStreamBuilder_setPerformanceMode(builder, AAUDIO_PERFORMANCE_MODE_LOW_LATENCY);
        AAudioStreamBuilder_setSharingMode(builder, AAUDIO_SHARING_MODE_SHARED);
        // Little processing (no aggressive noise suppression): pitch analysis needs the voice as it is.
        AAudioStreamBuilder_setInputPreset(builder, AAUDIO_INPUT_PRESET_VOICE_RECOGNITION);
        aaudio_result_t result = AAudioStreamBuilder_openStream(builder, &stream);
        AAudioStreamBuilder_delete(builder);
        if (result != AAUDIO_OK || stream == nullptr)
        {
            if (++failures <= 3 || failures % 30 == 0) LOGW("Mic open failed: %s (retrying)", AAudio_convertResultToText(result));
            std::this_thread::sleep_for(std::chrono::seconds(2));
            continue;
        }
        const int32_t rate = AAudioStream_getSampleRate(stream);
        result = AAudioStream_requestStart(stream);
        LOGI("Mic capture %d Hz mono (burst %d) start=%s", rate, AAudioStream_getFramesPerBurst(stream),
             AAudio_convertResultToText(result));
        failures = 0;
        active_.store(result == AAUDIO_OK);
        int32_t filled = 0;
        uint64_t chunks = 0;
        while (running_.load() && result == AAUDIO_OK)
        {
            const int64_t timeoutNs = 100'000'000;
            aaudio_result_t n = AAudioStream_read(stream, chunk.data() + filled, kChunkFrames - filled, timeoutNs);
            if (n < 0)
            {
                LOGW("Mic read failed: %s; reopening", AAudio_convertResultToText(n));
                break;
            }
            filled += n;
            if (filled < kChunkFrames) continue;
            CameraSender::Get().PushMic(chunk.data(), static_cast<uint32_t>(kChunkFrames), 1, static_cast<uint32_t>(rate));
            filled = 0;
            if (++chunks == 1 || chunks % 3000 == 0) LOGI("Mic: %llu chunks sent", static_cast<unsigned long long>(chunks));
        }
        active_.store(false);
        AAudioStream_requestStop(stream);
        AAudioStream_close(stream);
        if (running_.load()) std::this_thread::sleep_for(std::chrono::milliseconds(500));
    }
    LOGI("Mic capture stopped");
}

extern "C" JNIEXPORT void JNICALL
Java_com_oculus_NativeActivity_nativeSetMicPermission(JNIEnv*, jobject, jboolean granted)
{
    MicCapture::Get().SetPermission(granted == JNI_TRUE);
    LOGI("Mic permission %s", granted == JNI_TRUE ? "granted" : "denied");
}
