// SPDX-License-Identifier: MPL-2.0

#include "TrackingTape.h"

#include "TrackingReceiver.h"

#include <spdlog/spdlog.h>

#include <algorithm>
#include <atomic>
#include <chrono>
#include <cstdio>
#include <cstring>
#include <mutex>
#include <thread>
#include <vector>

namespace
{
constexpr uint32_t kMagic = 0x5452584f; // "OXRT"
constexpr uint32_t kVersion = 1;

struct Entry
{
    double t;
    std::vector<uint8_t> bytes;
};

int64_t NowNs()
{
    return std::chrono::duration_cast<std::chrono::nanoseconds>(std::chrono::steady_clock::now().time_since_epoch()).count();
}

std::mutex gMutex;
TrackingReceiver* gReceiver = nullptr;

// Recording
std::atomic<bool> gRecording{false};
FILE* gFile = nullptr;
int64_t gRecordStartNs = 0;
uint32_t gRecorded = 0;

// Replay
std::atomic<bool> gReplaying{false};
std::vector<Entry> gTape;
std::thread gReplayThread;
std::atomic<bool> gReplayRun{false};
double gClockT = 0.0;      // tape time set by Unity
double gClockSpeed = 1.0;  // 0 = paused
int64_t gClockSetNs = 0;
uint32_t gReplayed = 0;

void ReplayLoop()
{
    size_t lastIndex = static_cast<size_t>(-1);
    while (gReplayRun.load())
    {
        {
            std::lock_guard<std::mutex> lock(gMutex);
            if (gReceiver != nullptr && !gTape.empty())
            {
                const double t = gClockT + (NowNs() - gClockSetNs) / 1e9 * gClockSpeed;
                auto it = std::upper_bound(gTape.begin(), gTape.end(), t, [](double v, const Entry& e) { return v < e.t; });
                const size_t index = it == gTape.begin() ? 0 : static_cast<size_t>(it - gTape.begin()) - 1;
                // Re-sent every tick with a fresh timestamp: the pose stays "live" while paused.
                gReceiver->InjectReplay(gTape[index].bytes.data(), gTape[index].bytes.size());
                if (index != lastIndex) { ++gReplayed; lastIndex = index; }
            }
        }
        std::this_thread::sleep_for(std::chrono::milliseconds(8)); // ~120 Hz
    }
}
} // namespace

namespace oxrsys::tracking_tape
{

void AttachReceiver(TrackingReceiver* receiver)
{
    std::lock_guard<std::mutex> lock(gMutex);
    gReceiver = receiver;
}

void DetachReceiver(TrackingReceiver* receiver)
{
    std::lock_guard<std::mutex> lock(gMutex);
    if (gReceiver == receiver) gReceiver = nullptr;
}

bool Recording() { return gRecording.load(); }
bool Replaying() { return gReplaying.load(); }

void Record(const uint8_t* data, size_t size)
{
    std::lock_guard<std::mutex> lock(gMutex);
    if (gFile == nullptr || size == 0 || size > 64 * 1024) return;
    const double t = (NowNs() - gRecordStartNs) / 1e9;
    const uint32_t n = static_cast<uint32_t>(size);
    std::fwrite(&t, sizeof(t), 1, gFile);
    std::fwrite(&n, sizeof(n), 1, gFile);
    std::fwrite(data, 1, size, gFile);
    ++gRecorded;
}

} // namespace oxrsys::tracking_tape

extern "C" {

// Starts writing the live tracking stream to path (t = 0 now). Returns 1 on success.
__attribute__((visibility("default")))
int oxrsys_tracking_record_start(const char* path)
{
    std::lock_guard<std::mutex> lock(gMutex);
    if (gFile != nullptr) { std::fclose(gFile); gFile = nullptr; }
    gFile = path != nullptr ? std::fopen(path, "wb") : nullptr;
    if (gFile == nullptr) return 0;
    const uint32_t header[3] = {kMagic, kVersion, static_cast<uint32_t>(sizeof(oxr::protocol::TrackingPacket))};
    std::fwrite(header, sizeof(header), 1, gFile);
    gRecordStartNs = NowNs();
    gRecorded = 0;
    gRecording.store(true);
    spdlog::info("TrackingTape: recording to {}", path);
    return 1;
}

// Returns the number of packets written.
__attribute__((visibility("default")))
int oxrsys_tracking_record_stop()
{
    std::lock_guard<std::mutex> lock(gMutex);
    gRecording.store(false);
    if (gFile != nullptr) { std::fclose(gFile); gFile = nullptr; }
    spdlog::info("TrackingTape: recorded {} packets", gRecorded);
    return static_cast<int>(gRecorded);
}

// Loads a tape and replaces the live tracking with it (clock at 0, speed 1). Returns the packet count.
__attribute__((visibility("default")))
int oxrsys_tracking_replay_start(const char* path)
{
    FILE* file = path != nullptr ? std::fopen(path, "rb") : nullptr;
    if (file == nullptr) return 0;
    uint32_t header[3] = {};
    std::vector<Entry> tape;
    if (std::fread(header, sizeof(header), 1, file) == 1 && header[0] == kMagic && header[1] == kVersion)
    {
        for (;;)
        {
            Entry e;
            uint32_t n = 0;
            if (std::fread(&e.t, sizeof(e.t), 1, file) != 1 || std::fread(&n, sizeof(n), 1, file) != 1 || n > 64 * 1024) break;
            e.bytes.resize(n);
            if (std::fread(e.bytes.data(), 1, n, file) != n) break;
            tape.push_back(std::move(e));
        }
    }
    std::fclose(file);
    if (tape.empty()) return 0;

    // Stop a previous replay first (its thread takes gMutex).
    if (gReplayRun.exchange(false) && gReplayThread.joinable()) gReplayThread.join();
    {
        std::lock_guard<std::mutex> lock(gMutex);
        gTape = std::move(tape);
        gClockT = 0.0;
        gClockSpeed = 1.0;
        gClockSetNs = NowNs();
        gReplayed = 0;
        if (gReceiver != nullptr) gReceiver->ResetHistory(); // the tape's clock is not the headset's
        gReplaying.store(true);
    }
    gReplayRun.store(true);
    gReplayThread = std::thread(ReplayLoop);
    spdlog::info("TrackingTape: replaying {} packets from {}", gTape.size(), path);
    return static_cast<int>(gTape.size());
}

// Unity's replay clock: tape time t now, advancing at speed (0 = paused). Call every frame or on change.
__attribute__((visibility("default")))
void oxrsys_tracking_replay_clock(double t, double speed)
{
    std::lock_guard<std::mutex> lock(gMutex);
    gClockT = t;
    gClockSpeed = speed;
    gClockSetNs = NowNs();
}

__attribute__((visibility("default")))
void oxrsys_tracking_replay_stop()
{
    if (gReplayRun.exchange(false) && gReplayThread.joinable()) gReplayThread.join();
    std::lock_guard<std::mutex> lock(gMutex);
    gReplaying.store(false);
    gTape.clear();
    if (gReceiver != nullptr) gReceiver->ResetHistory(); // back to the headset's clock
    spdlog::info("TrackingTape: replay stopped ({} distinct packets shown)", gReplayed);
}

// 1 recording, 2 replaying, 0 idle.
__attribute__((visibility("default")))
int oxrsys_tracking_tape_state()
{
    return gReplaying.load() ? 2 : gRecording.load() ? 1 : 0;
}

}
