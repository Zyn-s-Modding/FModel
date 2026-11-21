#!/bin/bash
set -euo pipefail
repo_dir="$(cd "$(dirname "$0")/.." && pwd)"
output_dir="$1"
case "${2:-$(uname -m)}" in
    arm64|osx-arm64) target_arch=arm64 ;;
    x86_64|osx-x64) target_arch=x86_64 ;;
    '') target_arch="$(uname -m)" ;;
    *) echo "Unsupported macOS audio runtime: $2" >&2; exit 1 ;;
esac
if command -v cmake >/dev/null 2>&1; then
    cmake_command=(cmake)
elif command -v uv >/dev/null 2>&1; then
    cmake_command=(uv --system-certs tool run --from cmake==3.31.6 cmake)
else
    echo "Building the macOS audio helper requires CMake (or uv to obtain an isolated CMake)." >&2
    exit 1
fi
revision=24ffd6ce3e1f34fdebf9f5316d18ad2e162838ce
checksum=4c126a0dfdb0d250fee6796f850dbed07412fcc0b947f4c3c4ae4d936cb01ca1
cache_dir="$repo_dir/.native-deps/vgmstream/$revision"
source_dir="$cache_dir/source"
build_dir="$cache_dir/build-$target_arch"
mkdir -p "$cache_dir" "$output_dir"
if [ ! -f "$source_dir/CMakeLists.txt" ]; then
    archive="$cache_dir/source.tar.gz"
    curl --fail --location --retry 2 --silent --show-error \
        "https://codeload.github.com/vgmstream/vgmstream/tar.gz/$revision" -o "$archive"
    printf '%s  %s\n' "$checksum" "$archive" | shasum -a 256 -c -
    mkdir -p "$source_dir"
    tar -xzf "$archive" --strip-components=1 -C "$source_dir"
fi
python3 "$repo_dir/Compatibility/prepare-vgmstream.py" "$source_dir"
speex_archive="$repo_dir/.native-deps/vgmstream/speex-1.2.1.tar.gz"
speex_source="$repo_dir/.native-deps/vgmstream/speex-release"
if [ ! -f "$speex_archive" ]; then curl -fL --retry 3 https://downloads.xiph.org/releases/speex/speex-1.2.1.tar.gz -o "$speex_archive"; fi
printf '%s  %s\n' 4b44d4f2b38a370a2d98a78329fefc56a0cf93d1c1be70029217baae6628feea "$speex_archive" | shasum -a 256 -c -
if [ ! -f "$speex_source/configure" ]; then mkdir -p "$speex_source"; tar -xzf "$speex_archive" --strip-components=1 -C "$speex_source"; fi
compiler="$cache_dir/clang-$target_arch"
cat > "$compiler" <<COMPILER
#!/bin/bash
exec /usr/bin/xcrun clang -arch "$target_arch" -mmacosx-version-min=15.0 "\$@"
COMPILER
chmod +x "$compiler"
python3 - "$build_dir" "$compiler" <<'PY'
import pathlib, re, shutil, sys
build = pathlib.Path(sys.argv[1])
cache = build / 'CMakeCache.txt'
if cache.exists():
    match = re.search(r'^CMAKE_C_COMPILER:[^=]+=(.*)$', cache.read_text(), re.M)
    if match and match[1] != sys.argv[2]:
        cache.unlink()
        shutil.rmtree(build / 'CMakeFiles', ignore_errors=True)
PY
ffmpeg_prefix="$repo_dir/.native-deps/vgmstream/ffmpeg-$target_arch/install"
bash "$repo_dir/scripts/build-macos-ffmpeg.sh" "$target_arch" "$ffmpeg_prefix"
"${cmake_command[@]}" -S "$source_dir" -B "$build_dir" \
    -UCMAKE_PROJECT_INCLUDE -UCMAKE_PROJECT_vgmstream_INCLUDE \
    -DCMAKE_C_COMPILER="$compiler" -DSPEEX_PATH="$speex_source" -DCMAKE_BUILD_TYPE=Release -DCMAKE_OSX_DEPLOYMENT_TARGET=15.0 -DCMAKE_OSX_ARCHITECTURES="$target_arch" \
    -DBUILD_STATIC=OFF -DBUILD_SHARED_LIBS=OFF -DBUILD_V123=OFF -DBUILD_AUDACIOUS=OFF \
    -DCMAKE_PREFIX_PATH="$ffmpeg_prefix" -DAVCODEC_VERSION=62 -DAVFORMAT_VERSION=62 -DAVUTIL_VERSION=60 -DSWRESAMPLE_VERSION=6 \
    -DUSE_FFMPEG=ON -DUSE_MPEG=ON -DUSE_VORBIS=ON -DUSE_CELT=ON \
    -DUSE_SPEEX=ON -DUSE_ATRAC9=ON -DUSE_G7221=ON -DUSE_G719=ON
"${cmake_command[@]}" --build "$build_dir" --target vgmstream_cli --parallel 8
cp "$build_dir/cli/vgmstream-cli" "$output_dir/vgmstream-cli"
chmod +x "$output_dir/vgmstream-cli"
cp "$source_dir/COPYING" "$output_dir/vgmstream-COPYING.txt"
python3 - "$ffmpeg_prefix" "$output_dir" "$source_dir" "$speex_source" <<'PY'
import pathlib, re, shutil, subprocess, sys
prefix, output, source, speex = map(pathlib.Path, sys.argv[1:])
for library in (prefix / 'lib').glob('*.dylib'):
    if re.fullmatch(r'lib(?:avcodec|avformat|avutil|swresample)\.\d+\.dylib', library.name):
        target = output / library.name
        shutil.copyfile(library, target)
        subprocess.run(['install_name_tool', '-id', '@loader_path/' + target.name, str(target)], check=True)
for target in [output / 'vgmstream-cli', *output.glob('libav*.dylib'), *output.glob('libswresample*.dylib')]:
    lines = subprocess.check_output(['otool', '-L', str(target)], text=True).splitlines()[1:]
    for line in lines:
        dependency = line.strip().split(' (', 1)[0]
        if dependency.startswith(str(prefix)):
            subprocess.run(['install_name_tool', '-change', dependency, '@loader_path/' + pathlib.Path(dependency).name, str(target)], check=True)
shutil.copyfile(prefix / 'FFmpeg-LICENSE.txt', output / 'FFmpeg-LICENSE.txt')
with (output / 'vgmstream-COPYING.txt').open('w') as notices:
    notices.write((source / 'COPYING').read_text())
    for root in [source / 'dependencies', speex]:
        for notice in sorted(root.rglob('*')):
            if notice.is_file() and notice.name.upper() in ['COPYING', 'COPYING.LIB', 'LICENSE', 'LICENSE.TXT', 'COPYRIGHT']:
                notices.write('\n\n' + str(notice.relative_to(root)) + '\n' + notice.read_text(errors='replace'))
PY
