// SPDX-License-Identifier: MPL-2.0
//
// Link para Mac: the headset microphone, captured with AAudio and sent to the macOS runtime on the camera
// channel (MsgKind::Mic), like Quest Link exposes the Quest microphone to the PC. The runtime hands it to
// the "Quest 3 Microphone" audio driver on the Mac, so any app (Unity's Microphone class included) records
// the voice from the headset.

#pragma once

#include <atomic>
#include <thread>

class MicCapture
{
public:
    static MicCapture& Get();

    // Starts the capture thread (idempotent). Opening fails until RECORD_AUDIO is granted: it retries.
    void Start();
    void Stop();
    bool IsActive() const { return active_.load(); }
    void SetPermission(bool granted) { permission_.store(granted); }

private:
    MicCapture() = default;
    void Loop();

    std::thread thread_;
    std::atomic<bool> running_{false};
    std::atomic<bool> active_{false};
    std::atomic<bool> permission_{false};
};
