// SPDX-License-Identifier: MPL-2.0
//
// Link para Mac: records the headset's tracking stream (head, controllers, hands, buttons, microgestures —
// the raw TrackingPackets) and plays it back as if it came from the headset. Unity's session recorder
// (LinkSession) drives both: it starts the tape with the camera/depth recording, and during a replay it sets
// the clock every frame, so pause, seek and slow motion move the tracking together with the camera frames.
// The app sees recorded input through the normal OpenXR path: hands, pinches and microgestures replay too.

#pragma once

#include <cstddef>
#include <cstdint>

class TrackingReceiver;

namespace oxrsys::tracking_tape
{

void AttachReceiver(TrackingReceiver* receiver);
void DetachReceiver(TrackingReceiver* receiver);

bool Recording();
// A live packet accepted from the connected client.
void Record(const uint8_t* data, size_t size);

// While true, live packets are ignored: the replay thread feeds the receiver.
bool Replaying();

} // namespace oxrsys::tracking_tape
