#!/bin/bash
set -euo pipefail
repo_dir="$(cd "$(dirname "$0")/.." && pwd)"
out="${1:?native output directory}"
mkdir -p "$out"
out="$(cd "$out" && pwd)"
case "${2:-$(uname -m)}" in
  osx-arm64|arm64|'') arch=arm64; target=aarch64-apple-darwin ;;
  osx-x64|x86_64) arch=x86_64; target=x86_64-apple-darwin ;;
  *) exit 1 ;;
esac
case "$(uname -m)" in
  arm64) host=aarch64-apple-darwin; compiler_sha=89551c0ba1cc6d0312aebc4a6cafe4497223217ea8e87c81f6afbe127dfaeeb6 ;;
  x86_64) host=x86_64-apple-darwin; compiler_sha=594687a61b671445ea9fff0e6b6c6eef81cba30b005d22710217b4da8d2d2ecc ;;
esac
cache="$repo_dir/.native-deps/texture"
mkdir -p "$cache/toolchain-downloads"
fetch() {
  local url="$1" path="$2" sha="$3"
  if [ ! -f "$path" ]; then curl --fail --location --retry 3 "$url" --output "$path.tmp"; mv "$path.tmp" "$path"; fi
  echo "$sha  $path" | shasum -a 256 --check --status
}
fetch https://codeload.github.com/ScanMountGoat/tegra_swizzle/tar.gz/d7ed318ce233a4d557d4a2f207ed5dc04ed813c0 "$cache/tegra.tar.gz" c4e09038ad91ae9d261688f710fe004029afaf93364ce881cb2294e5745e492c
fetch https://codeload.github.com/BinomialLLC/crunch/tar.gz/36479bc697be19168daafbf15f47f3c60ccec004 "$cache/crunch.tar.gz" 673150d9f668958805084c219aacd70a4d2fefc9fd63c753b2988dbe8aa1dc99
tegra="$cache/tegra_swizzle-d7ed318ce233a4d557d4a2f207ed5dc04ed813c0"
crunch="$cache/crunch-36479bc697be19168daafbf15f47f3c60ccec004"
[ -d "$tegra" ] || tar -xzf "$cache/tegra.tar.gz" -C "$cache"
[ -d "$crunch" ] || tar -xzf "$cache/crunch.tar.gz" -C "$cache"
toolchain="$cache/rust-1.90.0-$host"
install_component() {
  local component="$1" sha="$2"
  local archive="$cache/toolchain-downloads/$component.tar.xz"
  fetch "https://static.rust-lang.org/dist/$component.tar.xz" "$archive" "$sha"
  if [ ! -f "$toolchain/.installed-$component" ]; then
    tar -xJf "$archive" -C "$cache/toolchain-downloads"
    bash "$cache/toolchain-downloads/$component/install.sh" --prefix="$toolchain" --disable-ldconfig --without=rust-docs
    touch "$toolchain/.installed-$component"
  fi
}
install_component "rustc-1.90.0-$host" "$compiler_sha"
case "$target" in
  aarch64-apple-darwin) std_sha=c36777aec17d617f85f94b7cb87bdd4270eeca30ef38c6f686809d163c881609 ;;
  x86_64-apple-darwin) std_sha=dd731e6f9f30cb9b2928b92b084d2f12a3abf06a481ecbd8c3553c3e6f742139 ;;
esac
install_component "rust-std-1.90.0-$target" "$std_sha"
build="$cache/build-$arch"
mkdir -p "$build"
python3 "$repo_dir/Compatibility/prepare-crunch.py" "$crunch/inc/crn_decomp.h" "$build/crn_decomp.h"
cp "$crunch/inc/crnlib.h" "$build/crnlib.h"
export MACOSX_DEPLOYMENT_TARGET=15.0
"$toolchain/bin/rustc" --edition=2018 --crate-type=rlib --crate-name tegra_swizzle --cfg 'feature="std"' \
  --target "$target" -C opt-level=3 "$tegra/src/lib.rs" -o "$build/libtegra_swizzle.rlib"
"$toolchain/bin/rustc" --edition=2021 --crate-type=cdylib --target "$target" -C opt-level=3 \
  --extern "tegra_swizzle=$build/libtegra_swizzle.rlib" "$repo_dir/Compatibility/TegraBridge.rs" -o "$out/libtegra_swizzle_x64.dylib"
xcrun clang++ -std=c++11 -dynamiclib -arch "$arch" -mmacosx-version-min=15.0 -O2 -fno-strict-aliasing \
  -Wno-duplicate-decl-specifier -I "$repo_dir/Compatibility/Include" -I "$build" "$repo_dir/Compatibility/CrunchBridge.cpp" -o "$out/libcrunch.dylib"
for name in libcrunch libtegra_swizzle_x64; do
  install_name_tool -id "@rpath/$name.dylib" "$out/$name.dylib"
  codesign --force --sign - "$out/$name.dylib"
done
cp "$tegra/LICENSE" "$out/tegra_swizzle-LICENSE.txt"
cp "$crunch/license.txt" "$out/Crunch-LICENSE.txt"
printf 'Built Crunch and Tegra texture decoders for %s\n' "$arch"
