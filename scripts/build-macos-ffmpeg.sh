#!/bin/bash
set -euo pipefail
repo_dir="$(cd "$(dirname "$0")/.." && pwd)"
architecture="$1"
prefix="$2"
mkdir -p "$prefix"
prefix="$(cd "$prefix" && pwd)"
cache="$repo_dir/.native-deps/vgmstream"
archive="$cache/ffmpeg-n8.0.1.tar.gz"
source="$cache/ffmpeg-n8.0.1/source"
build="$cache/ffmpeg-n8.0.1/build-$architecture"
if [ ! -f "$archive" ]; then curl -fL --retry 3 https://codeload.github.com/FFmpeg/FFmpeg/tar.gz/refs/tags/n8.0.1 -o "$archive"; fi
printf '%s  %s\n' 679aa13a19415d5ddab91e580084e3ab20c963c8240001e5cbb955a97bdd81b1 "$archive" | shasum -a 256 -c -
if [ ! -f "$source/configure" ]; then mkdir -p "$source"; tar -xzf "$archive" --strip-components=1 -C "$source"; fi
if [ ! -f "$prefix/lib/libavformat.dylib" ]; then
    mkdir -p "$build"
    cd "$build"
    "$source/configure" --prefix="$prefix" --target-os=darwin --arch="$architecture" --cc="$(xcrun -f clang)" --sysroot="$(xcrun --sdk macosx --show-sdk-path)" \
        --extra-cflags="-arch $architecture -mmacosx-version-min=15.0" \
        --extra-ldflags="-arch $architecture -mmacosx-version-min=15.0" --enable-cross-compile \
        --enable-shared --disable-static --disable-autodetect --disable-x86asm --disable-programs --disable-doc \
        --disable-gpl --disable-version3 --disable-network --disable-avdevice --disable-swscale --disable-avfilter \
        --disable-everything --enable-avcodec --enable-avformat --enable-avutil --enable-swresample \
        --enable-parser=ac3,mpegaudio,xma,vorbis,opus \
        --enable-demuxer=ac3,eac3,spdif,asf,xwma,mov,oma,xmv,ogg,flac,wav,aac,mp3,smacker,bink,caf,mpc,mpc8,tak,ape,flv,voc \
        --enable-decoder=ac3,eac3,wmapro,wmav1,wmav2,xma1,xma2,aac,atrac3,atrac3p,mp2float,mp3float,smackaud,binkaudio_dct,binkaudio_rdft,pcm_s16be,pcm_s16be_planar,pcm_s16le,pcm_s16le_planar,pcm_s8,pcm_s8_planar,pcm_u8,flac,vorbis,opus,mpc7,mpc8,alac,adpcm_ima_qt,adpcm_ima_dk3,adpcm_ima_dk4,tak,ape,adpcm_swf
    make -j8
    make install
fi
cp "$source/COPYING.LGPLv2.1" "$prefix/FFmpeg-LICENSE.txt"
