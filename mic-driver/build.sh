#!/bin/zsh
# Builds the "Headset Microphone" driver (MacXRBridgeMic.driver) in this folder. To install: install.sh (asks for the Mac password).
set -euo pipefail
cd "$(dirname "$0")"
rm -rf build/MacXRBridgeMic.driver
mkdir -p build/MacXRBridgeMic.driver/Contents/MacOS
cp Info.plist build/MacXRBridgeMic.driver/Contents/Info.plist
clang -std=c11 -O2 -Wall -Wextra -arch arm64 -arch x86_64 -mmacosx-version-min=12.0 -bundle \
  -framework CoreAudio -framework CoreFoundation -o build/MacXRBridgeMic.driver/Contents/MacOS/MacXRBridgeMic HeadsetMic.c
codesign --force --sign - --timestamp=none build/MacXRBridgeMic.driver
echo "ok: $(pwd)/build/MacXRBridgeMic.driver"
