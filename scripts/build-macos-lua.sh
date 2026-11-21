#!/bin/bash
set -euo pipefail
repo_dir="$(cd "$(dirname "$0")/.." && pwd)"
output_dir="$1"
mkdir -p "$output_dir"
output_dir="$(cd "$output_dir" && pwd)"
case "${2:-$(uname -m)}" in
    osx-arm64|arm64) lua_arch=aarch64; toolchain_sha=515e3a93acc7e1938daba83eda4272e5495fd302d7cdd99ec7ebf408ed505ab7 ;;
    osx-x64|x64|x86_64) lua_arch=x64; toolchain_sha=7a8aa93fa45d1721908477abf4732a32637d420ffcb66ada9fb6456440b0d9e1 ;;
    *) echo "Unsupported Lua target architecture" >&2; exit 1 ;;
esac
cache_dir="$repo_dir/.native-deps/unluac"
mkdir -p "$cache_dir"
fetch() {
    local url="$1" target="$2" expected="$3"
    if [ ! -f "$target" ]; then
        curl -fL --retry 3 "$url" -o "$target.download"
        mv "$target.download" "$target"
    fi
    local actual
    actual="$(shasum -a 256 "$target" | cut -d ' ' -f 1)"
    if [ "$actual" != "$expected" ]; then echo "Lua build checksum mismatch for $target" >&2; exit 1; fi
}
fetch "https://codeload.github.com/FModel/unluac/tar.gz/refs/tags/v1.0.0" "$cache_dir/source.tar.gz" 9a102c4b9697bfddf273685ca0b469608f02d33f2088ff4d3b61a2c5341cb9b3
if [ ! -d "$cache_dir/unluac-1.0.0" ]; then tar -xzf "$cache_dir/source.tar.gz" -C "$cache_dir"; fi
fetch "https://github.com/graalvm/graalvm-ce-builds/releases/download/jdk-21.0.2/graalvm-community-jdk-21.0.2_macos-${lua_arch}_bin.tar.gz" "$cache_dir/graalvm-${lua_arch}.tar.gz" "$toolchain_sha"
lua_jdk="$cache_dir/jdk-${lua_arch}/Contents/Home"
if [ ! -x "$lua_jdk/bin/native-image" ]; then
    mkdir -p "$cache_dir/jdk-${lua_arch}"
    tar -xzf "$cache_dir/graalvm-${lua_arch}.tar.gz" -C "$cache_dir/jdk-${lua_arch}" --strip-components=1
fi
classes="$cache_dir/classes-${lua_arch}"
mkdir -p "$classes"
python3 - "$cache_dir/unluac-1.0.0/src" "$classes/sources.txt" <<'PY'
import pathlib, sys
sources = pathlib.Path(sys.argv[1]).rglob('*.java')
pathlib.Path(sys.argv[2]).write_text('\n'.join('"' + str(path).replace('\\', '\\\\').replace('"', '\\"') + '"' for path in sources))
PY
"$lua_jdk/bin/javac" --add-modules=org.graalvm.nativeimage,org.graalvm.word -d "$classes" "@$classes/sources.txt"
compiler="$cache_dir/clang-${lua_arch}"
if [ "$lua_arch" = "x64" ]; then compiler_arch=x86_64; else compiler_arch=arm64; fi
cat > "$compiler" <<COMPILER
#!/bin/bash
exec /usr/bin/xcrun clang -arch "$compiler_arch" "\$@"
COMPILER
chmod +x "$compiler"
MACOSX_DEPLOYMENT_TARGET=15.0 "$lua_jdk/bin/native-image" --shared --no-fallback --parallelism=4 -J-Xmx4g -H:CCompilerPath="$compiler" -march=compatibility -O2 \
    --add-modules=org.graalvm.nativeimage,org.graalvm.word -cp "$classes" -H:Name=libunluac -H:Path="$output_dir"
python3 - "$cache_dir/unluac-1.0.0/license.txt" "$lua_jdk" "$output_dir/Unluac-LICENSES.txt" <<'PY'
import pathlib, sys
jdk = pathlib.Path(sys.argv[2])
with pathlib.Path(sys.argv[3]).open('w') as output:
    output.write('FModel/unluac 1.0.0\n' + pathlib.Path(sys.argv[1]).read_text() + '\n\nGraalVM Community JDK 21.0.2 native-image\n')
    output.write((jdk / 'LICENSE_NATIVEIMAGE.txt').read_text() + '\n')
    for path in sorted((jdk / 'legal').rglob('*')):
        if path.is_file(): output.write('\n\n' + str(path.relative_to(jdk)) + '\n' + path.read_text(errors='replace'))
PY
