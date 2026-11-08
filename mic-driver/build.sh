#!/bin/zsh
# Builds the "Quest 3 Microphone" driver (QuestMic.driver) in this folder. To install: install.sh (asks for the Mac password).
set -euo pipefail
cd "$(dirname "$0")"
rm -rf build/QuestMic.driver
mkdir -p build/QuestMic.driver/Contents/MacOS
cp Info.plist build/QuestMic.driver/Contents/Info.plist
clang -std=c11 -O2 -Wall -Wextra -arch arm64 -arch x86_64 -mmacosx-version-min=12.0 -bundle \
  -framework CoreAudio -framework CoreFoundation -o build/QuestMic.driver/Contents/MacOS/QuestMic QuestMic.c
codesign --force --sign - --timestamp=none build/QuestMic.driver
echo "ok: $(pwd)/build/QuestMic.driver"
