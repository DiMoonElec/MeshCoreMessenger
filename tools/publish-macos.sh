#!/bin/bash
set -euo pipefail

if [[ "$(uname -s)" != Darwin ]]; then
  echo "Build this local app bundle on macOS." >&2
  exit 1
fi

repo_dir="$(cd "$(dirname "$0")/.." && pwd)"
case "${1:-$(uname -m)}" in
  arm64|osx-arm64) runtime_id=osx-arm64 ;;
  x86_64|osx-x64) runtime_id=osx-x64 ;;
  *) echo "Usage: $0 [osx-arm64|osx-x64]" >&2; exit 2 ;;
esac

output_dir="$repo_dir/artifacts/macos/$runtime_id"
mkdir -p "$output_dir"
build_dir="$(mktemp -d "$output_dir/build.XXXXXX")"
bundle_dir="$build_dir/MeshCoreMessenger.app"
mkdir -p "$bundle_dir/Contents/MacOS" "$bundle_dir/Contents/Resources"

dotnet publish "$repo_dir/src/MeshCoreMessenger.Desktop" -c Release \
  -r "$runtime_id" --self-contained true --disable-build-servers -m:1 \
  -p:UseSharedCompilation=false -p:PublishTrimmed=false -p:PublishSingleFile=false \
  -o "$bundle_dir/Contents/MacOS"

cat > "$bundle_dir/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>CFBundleDevelopmentRegion</key><string>en</string>
  <key>CFBundleExecutable</key><string>MeshCoreMessenger.Desktop</string>
  <key>CFBundleIdentifier</key><string>org.meshcore.messenger.local</string>
  <key>CFBundleName</key><string>MeshCoreMessenger</string>
  <key>CFBundleDisplayName</key><string>MeshCoreMessenger</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>0.1.0</string>
  <key>CFBundleVersion</key><string>1</string>
  <key>NSHighResolutionCapable</key><true/>
</dict></plist>
PLIST

# Ad-hoc signature for use on the build machine; no Developer ID/notarization.
codesign --force --deep --sign - "$bundle_dir"
codesign --verify --deep --strict "$bundle_dir"
archive_path="$build_dir/MeshCoreMessenger-$runtime_id.zip"
ditto -c -k --sequesterRsrc --keepParent "$bundle_dir" "$archive_path"
echo "Local app: $bundle_dir"
echo "Archive: $archive_path"
