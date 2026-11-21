#!/bin/bash
set -euo pipefail
repo_dir="$(cd "$(dirname "$0")/.." && pwd)"
case "${2:-$(uname -m)}" in
    arm64|osx-arm64) architecture=arm64 ;;
    x86_64|x64|osx-x64) architecture=x86_64 ;;
    *) echo "Unsupported Snooper architecture: $2" >&2; exit 1 ;;
esac
mkdir -p "$1"
output="$(cd "$1" && pwd)"
cache="$repo_dir/.native-deps"
mesa_cache="$cache/mesa"
mkdir -p "$mesa_cache"

fetch() {
    local url="$1" expected="$2" destination="$3"
    if [ -f "$destination" ] && [ "$(shasum -a 256 "$destination" | cut -d ' ' -f 1)" = "$expected" ]; then return; fi
    curl -fsSL --retry 3 "$url" -o "$destination.partial"
    if [ "$(shasum -a 256 "$destination.partial" | cut -d ' ' -f 1)" != "$expected" ]; then
        echo "Checksum mismatch: $destination" >&2; exit 1
    fi
    mv "$destination.partial" "$destination"
}

bottle() {
    local repository="$1" digest="$2" destination="$3"
    if [ -f "$destination" ] && [ "$(shasum -a 256 "$destination" | cut -d ' ' -f 1)" = "$digest" ]; then return; fi
    local token
    token="$(curl -fsSL "https://ghcr.io/token?service=ghcr.io&scope=repository:homebrew/core/$repository:pull" | python3 -c 'import json,sys; print(json.load(sys.stdin)["token"])')"
    curl -fsSL --retry 3 -H "Authorization: Bearer $token" "https://ghcr.io/v2/homebrew/core/$repository/blobs/sha256:$digest" -o "$destination.partial"
    if [ "$(shasum -a 256 "$destination.partial" | cut -d ' ' -f 1)" != "$digest" ]; then
        echo "Checksum mismatch: $destination" >&2; exit 1
    fi
    mv "$destination.partial" "$destination"
}

fetch https://archive.mesa3d.org/mesa-24.3.4.tar.xz \
    e641ae27191d387599219694560d221b7feaa91c900bcec46bf444218ed66025 "$mesa_cache/mesa-24.3.4.tar.xz"
if [ ! -f "$mesa_cache/mesa-24.3.4/meson.build" ]; then tar -xf "$mesa_cache/mesa-24.3.4.tar.xz" -C "$mesa_cache"; fi
python3 "$repo_dir/Compatibility/prepare-mesa.py" "$mesa_cache/mesa-24.3.4"

if [ "$architecture" = arm64 ]; then
    llvm_cache="$cache/llvm19"
    llvm_prefix="$llvm_cache/llvm@19/19.1.7"
    mkdir -p "$llvm_cache"
    bottle llvm/19 20101eea95e0f3d9aafdd715a8db02c4651f3a6ee1d7a4a3dc301c40199259ac "$llvm_cache/bottle.tar.gz"
    if [ ! -f "$llvm_prefix/LICENSE.TXT" ]; then
        tar -xzf "$llvm_cache/bottle.tar.gz" -C "$llvm_cache" \
            llvm@19/19.1.7/bin/llvm-config llvm@19/19.1.7/include \
            llvm@19/19.1.7/lib/libLLVM.dylib llvm@19/19.1.7/lib/libLLVM-19.dylib llvm@19/19.1.7/LICENSE.TXT
    fi
else
    llvm_cache="$cache/llvm19-intel"
    llvm_prefix="$llvm_cache/llvm/19.1.7_1"
    mkdir -p "$llvm_cache" "$cache/z3-intel"
    # Homebrew/homebrew-core 22dfee1662db712ea428f5d07207ca981d60e8ba, Sonoma bottle.
    bottle llvm eab56bb7092bb23a667808915dc4aad048140395194b955bbc743cb65ef65697 "$llvm_cache/bottle.tar.gz"
    if [ ! -f "$llvm_prefix/LICENSE.TXT" ]; then
        tar -xzf "$llvm_cache/bottle.tar.gz" -C "$llvm_cache" \
            llvm/19.1.7_1/bin/llvm-config llvm/19.1.7_1/include \
            llvm/19.1.7_1/lib/libLLVM.dylib llvm/19.1.7_1/lib/libLLVM-19.dylib llvm/19.1.7_1/LICENSE.TXT
    fi
    # The Intel LLVM bottle links Z3; the ARM bottle does not.
    bottle z3 0d091791e14b23d1e296861e13063b67fde37d42c6870567196e83ac69611b93 "$cache/z3-intel/bottle.tar.gz"
    tar -xzf "$cache/z3-intel/bottle.tar.gz" -C "$cache/z3-intel" z3/4.14.0/lib/libz3.4.14.0.0.dylib z3/4.14.0/LICENSE.txt
fi

uv_bin="$(command -v uv || true)"
if [ -z "$uv_bin" ] && [ -x "$HOME/.local/bin/uv" ]; then uv_bin="$HOME/.local/bin/uv"; fi
if [ -z "$uv_bin" ]; then echo "Install uv to build Snooper's isolated Mesa dependencies." >&2; exit 1; fi
if [ ! -x "$mesa_cache/venv/bin/meson" ]; then
    "$uv_bin" venv "$mesa_cache/venv"
    "$uv_bin" pip install --python "$mesa_cache/venv/bin/python" \
        meson==1.7.2 ninja==1.11.1.4 mako==1.3.10 MarkupSafe==3.0.4 PyYAML==6.0.2 packaging==25.0
fi

export PATH="$llvm_prefix/bin:$mesa_cache/venv/bin:$PATH"
export CFLAGS="-arch $architecture -mmacosx-version-min=15.0"
export CXXFLAGS="$CFLAGS"
export LDFLAGS="$CFLAGS"
mesa_build="$mesa_cache/build-$architecture"
cross_arguments=()
if [ "$architecture" != "$(uname -m)" ]; then
    cross_file="$mesa_cache/cross-$architecture.ini"
    python3 - "$cross_file" "$architecture" "$llvm_prefix/bin/llvm-config" <<'PY'
import sys
from pathlib import Path
path, architecture, llvm = sys.argv[1:]
def quote(value): return "'" + value.replace("\\", "\\\\").replace("'", "\\'") + "'"
Path(path).write_text("[binaries]\nc = ['clang', '-arch', " + quote(architecture) + "]\n"
    "cpp = ['clang++', '-arch', " + quote(architecture) + "]\nllvm-config = " + quote(llvm) + "\n"
    "objc = ['clang', '-arch', " + quote(architecture) + "]\nobjcpp = ['clang++', '-arch', " + quote(architecture) + "]\n"
    "[host_machine]\nsystem = 'darwin'\ncpu_family = " + quote(architecture) + "\ncpu = " + quote(architecture) + "\nendian = 'little'\n")
PY
    cross_arguments=(--cross-file "$cross_file")
fi
if [ ! -f "$mesa_build/build.ninja" ]; then
    meson setup "$mesa_build" "$mesa_cache/mesa-24.3.4" "${cross_arguments[@]}" \
        -Dgallium-drivers=llvmpipe -Dvulkan-drivers= -Dplatforms= -Dglx=disabled -Degl=disabled \
        -Dosmesa=true -Dllvm=enabled -Dshared-llvm=enabled -Dgles1=disabled -Dgles2=disabled \
        -Dvideo-codecs= -Dbuild-tests=false -Dtools= -Dbuildtype=release -Db_ndebug=true
fi
meson configure "$mesa_build" -Dc_args="$CFLAGS" -Dcpp_args="$CXXFLAGS" -Dc_link_args="$LDFLAGS" -Dcpp_link_args="$LDFLAGS"
ninja -C "$mesa_build" -j "${FMODEL_BUILD_JOBS:-8}"

mkdir -p "$cache/zstd"
fetch https://github.com/facebook/zstd/releases/download/v1.5.7/zstd-1.5.7.tar.gz \
    eb33e51f49a15e023950cd7825ca74a4a2b43db8354825ac24fc1b7ee09e6fa3 "$cache/zstd/zstd-1.5.7.tar.gz"
if [ ! -f "$cache/zstd/zstd-1.5.7/lib/zstd.h" ]; then tar -xf "$cache/zstd/zstd-1.5.7.tar.gz" -C "$cache/zstd"; fi
zstd_source="$cache/zstd/zstd-1.5.7"
xcrun clang -arch "$architecture" -mmacosx-version-min=15.0 -dynamiclib -O3 \
    -DZSTD_MULTITHREAD -DZSTD_DISABLE_ASM -DZSTD_LEGACY_SUPPORT=0 \
    -I "$zstd_source/lib" -I "$zstd_source/lib/common" \
    "$zstd_source"/lib/common/*.c "$zstd_source"/lib/compress/*.c \
    "$zstd_source"/lib/decompress/*.c "$zstd_source"/lib/dictBuilder/*.c \
    -Wl,-install_name,@rpath/libzstd.1.dylib -o "$output/libzstd.1.dylib"

cp -f "$mesa_build/src/gallium/targets/osmesa/libOSMesa.8.dylib" "$output/libOSMesa.dylib"
cp -f "$mesa_build/src/mapi/shared-glapi/libglapi.0.dylib" "$output/"
cp -f "$llvm_prefix/lib/libLLVM.dylib" "$output/"
chmod u+w "$output/libLLVM.dylib" "$output/libOSMesa.dylib" "$output/libglapi.0.dylib"
llvm_dependency="$(otool -L "$output/libOSMesa.dylib" | awk '/\/libLLVM.dylib/ {print $1}')"
install_name_tool -id @rpath/libOSMesa.dylib \
    -change @rpath/libglapi.0.dylib @loader_path/libglapi.0.dylib \
    -change "$llvm_dependency" @loader_path/libLLVM.dylib "$output/libOSMesa.dylib"
install_name_tool -id @rpath/libLLVM.dylib \
    -change @@HOMEBREW_PREFIX@@/opt/zstd/lib/libzstd.1.dylib @loader_path/libzstd.1.dylib "$output/libLLVM.dylib"
if [ "$architecture" = x86_64 ]; then
    cp -f "$cache/z3-intel/z3/4.14.0/lib/libz3.4.14.0.0.dylib" "$output/libz3.4.14.dylib"
    chmod u+w "$output/libz3.4.14.dylib"
    install_name_tool -id @rpath/libz3.4.14.dylib "$output/libz3.4.14.dylib"
    install_name_tool -change @@HOMEBREW_PREFIX@@/opt/z3/lib/libz3.4.14.dylib @loader_path/libz3.4.14.dylib "$output/libLLVM.dylib"
    cp "$cache/z3-intel/z3/4.14.0/LICENSE.txt" "$output/Z3-LICENSE.txt"
fi
for library in libzstd.1.dylib libLLVM.dylib libglapi.0.dylib libOSMesa.dylib; do
    codesign --force --sign - "$output/$library"
done
if [ "$architecture" = x86_64 ]; then codesign --force --sign - "$output/libz3.4.14.dylib"; fi
cp "$llvm_prefix/LICENSE.TXT" "$output/LLVM-LICENSE.txt"
cp "$zstd_source/LICENSE" "$output/zstd-LICENSE.txt"
python3 - "$mesa_cache/mesa-24.3.4" "$output/Mesa-LICENSES.txt" <<'PY'
from pathlib import Path
import sys
source, target = map(Path, sys.argv[1:])
files = [source / "docs/license.rst", *sorted(p for p in (source / "licenses").rglob("*") if p.is_file())]
target.write_text("\n\n".join(f"{p.relative_to(source)}\n{p.read_text()}" for p in files))
PY

# This checks actual SSBO texture sampling and expected pixel values. A context
# version string alone does not establish that the compatibility renderer works.
if [ "$architecture" = "$(uname -m)" ]; then
    xcrun clang -I "$mesa_cache/mesa-24.3.4/include" "$repo_dir/Compatibility/MesaSmoke.c" \
        -L "$output" -lOSMesa -Wl,-rpath,"$output" -o "$mesa_build/snooper-texture-check"
    MESA_GL_VERSION_OVERRIDE=4.6 MESA_GLSL_VERSION_OVERRIDE=460 "$mesa_build/snooper-texture-check"
fi
