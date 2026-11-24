# MacXR Bridge (Unity package)

The Unity side of [MacXR Bridge](https://github.com/edsonsims/macxr-bridge): Unity Play Mode on a Meta Quest 3 from an
Apple Silicon Mac, as Quest Link does on Windows, plus developer tools Quest Link doesn't have.

This package is the Editor half. It needs the MacXR Bridge runtime and Quest app, installed by `scripts/install.sh`
in the repository. Start with the repository's README.

## Install

1. Install the runtime: `zsh scripts/install.sh` in the repository.
2. Add this package: `zsh scripts/60-unity-add-package.sh /path/to/YourUnityProject`, or add it to
   `Packages/manifest.json` by hand:
   `"com.macxrbridge.xr": "file:<path>/macxr-bridge/unity-package/com.macxrbridge.xr"`.
3. **Window ▸ MacXR Bridge ▸ Setup & Diagnostics** checks every piece and fixes what's missing.
4. Open the OXRSys app on the Quest and press **Play**.

Requires Unity 6000.0, the OpenXR plug-in, Meta XR Core SDK 207 and MRUK 207.

## What's in it

- **Window ▸ MacXR Bridge ▸ Panel**: status, headset microphone, camera, depth and room, the headset debug view, mixed
  reality recording, and sessions with a replay timeline and tests.
- **Window ▸ MacXR Bridge ▸ Setup & Diagnostics**: checks the runtime, this Editor's `XR_RUNTIME_JSON`, the
  project's XR settings, the Quest app, the microphone driver and ffmpeg.
- **Window ▸ MacXR Bridge ▸ Spectator (Mixed Reality)**: the real camera with your app composited on top, live.
- Automatic in Play: the Quest camera feeds MRUK's `PassthroughCameraAccess`, and your app's audio plays in the
  headset.
- A command bridge (`<project>/.macxr/cmd/*.cmd`) that exposes every tool to scripts and AI coding agents.
- `BridgeHooks`: optional extension points for your app (detection wiring, placements for tests, lockstep).
- `BridgeSessionTests.RunAllFromCommandLine`: runs the perception tests from the command line.

## Documentation

- [Features](https://github.com/edsonsims/macxr-bridge/blob/main/docs/features.md)
- [Beyond Quest Link](https://github.com/edsonsims/macxr-bridge/blob/main/docs/beyond-quest-link.md)
- [Sessions and tests](https://github.com/edsonsims/macxr-bridge/blob/main/docs/sessions-and-tests.md)
- [Connect your app](https://github.com/edsonsims/macxr-bridge/blob/main/docs/connect-your-app.md)
- [Commands and automation](https://github.com/edsonsims/macxr-bridge/blob/main/docs/commands.md)
- [Troubleshooting](https://github.com/edsonsims/macxr-bridge/blob/main/docs/troubleshooting.md)

## License

MIT. The runtime and Quest client (an OXRSys fork) are MPL-2.0.

> MacXR Bridge is an independent project. It is not affiliated with, sponsored or endorsed by Meta Platforms, Inc.
> Meta, Meta Quest, Quest Link and Air Link are trademarks of Meta Platforms, Inc., used here only to describe
> compatibility. Built on [OXRSys](https://github.com/demonixis/oxrsys) by Yannick Comte and contributors (MPL-2.0).
