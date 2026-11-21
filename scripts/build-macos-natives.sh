#!/bin/bash
set -euo pipefail
repo_dir="$(cd "$(dirname "$0")/.." && pwd)"
output_dir="$1"
mkdir -p "$output_dir"
case "${2:-}" in
    osx-arm64) target_arch=arm64 ;;
    osx-x64) target_arch=x86_64 ;;
    '') target_arch="$(uname -m)" ;;
    *) echo "Unsupported macOS runtime: $2" >&2; exit 1 ;;
esac
native_dir="$repo_dir/CUE4Parse/CUE4Parse-Natives"
# ACL uses a redundant 'template' qualifier accepted by older Clang versions.
# The targeted diagnostic flag preserves the upstream submodule verbatim.
xcrun clang++ -arch "$target_arch" -mmacosx-version-min=14.0 -std=c++17 -O2 -dynamiclib \
    -Wno-missing-template-arg-list-after-template-kw -DWITH_ACL=1 \
    -I "$native_dir/common" -I "$native_dir/ACL/external/acl/includes" \
    -I "$native_dir/ACL/external/acl/external/rtm/includes" \
    "$native_dir/Features.cpp" "$native_dir/ACL/ACL.cpp" \
    -o "$output_dir/CUE4Parse-Natives.dylib"
xcrun clang -arch "$target_arch" -mmacosx-version-min=14.0 -O2 -dynamiclib -fobjc-arc \
    -framework AVFoundation -framework Foundation -framework AppKit -framework AudioToolbox -framework CoreAudio -framework Accelerate \
    "$repo_dir/FModel/Desktop/Native/MacAudio.m" -o "$output_dir/libFModelAudio.dylib"
