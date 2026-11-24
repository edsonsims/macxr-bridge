# Connect your app

Most apps need **no code** to work with MacXR Bridge: the Meta XR SDK talks to the runtime through OpenXR, as it would
to a Quest over Link. This page covers the project settings, what works on its own, and the optional hooks for the
developer tools.

## Project settings

In **Project Settings ▸ XR Plug-in Management**, on the **macOS (Standalone)** tab:
- Plug-in provider: **OpenXR**.
- OpenXR features: **Meta XR Feature** (plus the Meta features your app uses, as on Android).
- Interaction profiles: **Oculus Touch Controller**, **Meta Quest Touch Plus** and **Hand Interaction**.
- **Run In Background** on (Player settings), so the Editor keeps working while you're in the headset.

**Window ▸ MacXR Bridge ▸ Setup & Diagnostics ▸ Configure XR** (or the `xr-standalone` command) sets all of this.

## What works without code

| You use | In Play Mode with MacXR Bridge |
|---|---|
| `OVRCameraRig` / `OVRManager`, the OpenXR plug-in | Head tracking, controllers, hands |
| Interaction SDK | Pinch, poke, grab and rays with your real hands |
| `OVRPassthroughLayer` | Passthrough in the headset, under your app |
| `OVRSpatialAnchor` | Real anchors, saved on the headset |
| MRUK (`LoadSceneFromDevice`, `EnvironmentRaycastManager`) | Your real room with labels; raycasts against live depth |
| MRUK `PassthroughCameraAccess` | The real Quest camera, fed automatically when it connects |
| AI Building Blocks `ObjectDetectionAgent` | Runs on the real camera frames, on the Mac's GPU |
| Unity `Microphone` | Records from the headset microphone (the default input while the app runs) |
| Your app's audio | Plays in the headset |

To know in Editor code whether you're running on MacXR Bridge (for example to skip an Editor-only mock), call
`MacXRBridge.BridgeRuntime.IsMacXRBridge()` (Editor only).

## Hooks for the developer tools

The package is generic. A few tools need to know something only your app knows: where it placed things, when its
detection runs, which colliders are its own. Your app tells them through `MacXRBridge.EditorTools.BridgeHooks`, from an
Editor script in your project. All hooks are optional.

| Hook | Type | Used by | What your app does |
|---|---|---|---|
| `WireDetection` | `Func<string>` | Camera feed | Turns on your real detection when the Quest camera arrives (for example, turns off a mock you use in the Editor). Returns a short report |
| `UnwireDetection` | `Action` | Camera feed | Undoes `WireDetection` |
| `ReportPlaced(label, position)` / `ReportPlacedBatch(list)` | methods you call | Tests, debug view | Report where your app placed something in the world |
| `IgnoreCollider` | `Func<Collider, bool>` | Fallback raycast | Returns true for colliders the raycast should go through (your own labels, mock objects) |
| `ApplyAppThreshold` | `Action<float>` | `camera-show thr=…` | Applies a lower confidence threshold to your own filtering, for this Play session |
| `DetectNow`, `HoldDetection`, `DetectionStarted` | `Action`, `Action`, `Func<bool>` | Lockstep tests | Start one detection now; don't start another until asked; report whether one started |
| `PauseAppDetection` | `Action<bool>` | Diagnostics | Pauses or resumes your detection |
| `AppStatus` | `Func<string>` | `detect-status` | An extra status line about your detection |

### Example

Put this in an `Editor` folder of your project (the `MacXRBridge.Editor` assembly is auto-referenced):

```csharp
using MacXRBridge.EditorTools;
using UnityEditor;
using UnityEngine;

[InitializeOnLoad]
static class MyAppBridgeHooks
{
    static MyAppBridgeHooks()
    {
        // The real camera is here: stop using the Editor mock.
        BridgeHooks.WireDetection = () =>
        {
            var mock = Object.FindAnyObjectByType<MyMockDetector>(FindObjectsInactive.Include);
            if (mock != null) mock.enabled = false;
            return "mock off, real camera on";
        };

        // The fallback raycast should not land on our own labels.
        BridgeHooks.IgnoreCollider = c => c.GetComponentInParent<MyLabel>() != null;

        // Lockstep tests: let the test decide when the next detection starts.
        BridgeHooks.DetectNow = () => MyDetector.Instance?.RunNextDetectionNow();
        BridgeHooks.HoldDetection = () => MyDetector.Instance?.PauseScheduling();
        BridgeHooks.DetectionStarted = () => MyDetector.Instance != null && MyDetector.Instance.StartedSinceLastRequest;

        // Tests and the debug view: forward where the app placed its labels.
        // Runtime code can't reference this Editor assembly, so the app raises its own event and we forward it.
        EditorApplication.playModeStateChanged += state =>
        {
            if (state != PlayModeStateChange.EnteredPlayMode) return;
            var labels = Object.FindAnyObjectByType<MyLabelManager>();
            if (labels != null)
                labels.Placed += batch => BridgeHooks.ReportPlacedBatch(batch); // IReadOnlyList<(string label, Vector3 position)>
        };
    }
}
```

A complete, real example (about 110 lines) is [examples/HelloWorldBridgeHooks.cs](examples/HelloWorldBridgeHooks.cs),
from the Lumi app the package was built with. It uses that app's types, so read it as a pattern rather than copying it.
