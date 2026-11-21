#!/bin/bash
set -euo pipefail
repo_dir="$(cd "$(dirname "$0")/.." && pwd)"
case "${1:-$(uname -m)}" in
    arm64|osx-arm64) runtime=osx-arm64 ;;
    x86_64|x64|osx-x64) runtime=osx-x64 ;;
    *) echo "Usage: bash scripts/package-macos.sh [osx-arm64|osx-x64] [output-directory]" >&2; exit 1 ;;
esac
destination="${2:-$repo_dir/dist/$runtime}"
mkdir -p "$destination"
destination="$(cd "$destination" && pwd)"
# Stage a new bundle so an unsuccessful publish cannot damage a previous build.
staging="$(mktemp -d "$destination/.FModel-build.XXXXXX")"
trap 'rm -rf "$staging"' EXIT
app="$staging/FModel.app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
dotnet publish "$repo_dir/FModel/FModel.csproj" -c Release -r "$runtime" \
    --self-contained true -p:PublishSingleFile=false -p:NuGetAudit=false \
    --disable-build-servers -m:1 -o "$app/Contents/MacOS"
cat > "$app/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
    <key>CFBundleExecutable</key><string>FModel</string>
    <key>CFBundleIdentifier</key><string>app.fmodel.desktop</string>
    <key>CFBundleName</key><string>FModel</string>
    <key>CFBundleDisplayName</key><string>FModel</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>CFBundleShortVersionString</key><string>4.4.4</string>
    <key>CFBundleVersion</key><string>4.4.4.0</string>
    <key>LSMinimumSystemVersion</key><string>15.0</string>
    <key>CFBundleIconFile</key><string>FModel</string>
    <key>NSHighResolutionCapable</key><true/>
    <key>NSPrincipalClass</key><string>NSApplication</string>
</dict></plist>
PLIST
iconset="$staging/FModel.iconset"
mkdir -p "$iconset"
sips -s format png "$repo_dir/FModel/FModel.ico" --out "$staging/icon.png" >/dev/null
for size in 16 32 128 256 512; do
    sips -z "$size" "$size" "$staging/icon.png" --out "$iconset/icon_${size}x${size}.png" >/dev/null
    retina=$((size * 2))
    sips -z "$retina" "$retina" "$staging/icon.png" --out "$iconset/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$iconset" -o "$app/Contents/Resources/FModel.icns"
cp "$repo_dir/LICENSE" "$repo_dir/NOTICE" "$app/Contents/Resources/"
cp "$repo_dir/CUE4Parse/LICENSE" "$app/Contents/Resources/CUE4Parse-LICENSE"
cp "$repo_dir/Snooper/LICENSE" "$app/Contents/Resources/Snooper-LICENSE"
cp "$repo_dir/FModel/Resources/Adonis-LICENSE.txt" "$app/Contents/Resources/Adonis-LICENSE.txt"
cp "$repo_dir/docs/macos.md" "$repo_dir/docs/fortnite24.20-validation.md" "$app/Contents/Resources/"
# Ad-hoc signing permits local use on Apple Silicon. Developer ID signing and
# notarization for distribution are a separate release step.
codesign --force --deep --sign - "$app"
final="$destination/FModel.app"
if [ -e "$final" ]; then
    previous="$destination/FModel.previous-$(date +%Y%m%d-%H%M%S).app"
    mv "$final" "$previous"
fi
mv "$app" "$final"
printf 'Built %s\n' "$final"
